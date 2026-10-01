using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace RazerBookRgb;

public partial class MainWindow : Window
{
    Profile profile = new();
    Profile saved = new();
    ProfileLibrary library = new();
    bool libraryAvailable = true;
    readonly Forms.ToolStripMenuItem profilesMenu = new("Profiles");
    readonly Dictionary<Button, KeySpec> keys = new();
    readonly HashSet<int> selected = new();
    readonly SemaphoreSlim hardwareGate = new(1, 1);
    readonly Forms.NotifyIcon tray;
    readonly System.Drawing.Icon trayIcon;
    readonly DispatcherTimer retryTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    readonly DispatcherTimer idleTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly IdleLightingPolicy idlePolicy = new();
    KeyboardActivity? activity;
    bool lightingManaged, suspended, sessionLocked;
    long nextIdleAttempt;
    bool updating, ready, exit, busy, dirty, pendingRestore;
    string paint = "#44D62C";
    readonly bool startup;
    readonly bool preview;
    public MainWindow(bool atStartup = false, bool previewOnly = false)
    {
        startup = atStartup; preview = previewOnly;
        InitializeComponent();
        string? warning = null;
        if (!preview && File.Exists(SettingsStore.FilePath))
        {
            try { profile = SettingsStore.Read(SettingsStore.FilePath); }
            catch (Exception e) { warning = "Saved profile could not be loaded: " + e.Message; }
        }
        saved = profile.Copy();
        if (!preview)
        {
            try { library = ProfileLibrary.Read(); }
            catch (Exception e) { libraryAvailable = false; warning = "Profiles could not be loaded: " + e.Message; }
        }
        BuildKeyboard();
        foreach (string hex in new[] { "#44D62C", "#00C8FF", "#7866FF", "#FF4D91", "#FF9D3D", "#FFFFFF", "#FF3030", "#000000" })
        {
            var button = new Button { Background = Brush(hex), Width = 23, Height = 23, Margin = new Thickness(0, 0, 3, 3), Padding = new Thickness(0), ToolTip = hex };
            button.Click += (_, _) => SetPaint(hex, true); PalettePanel.Children.Add(button);
        }
        using (var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/App.ico")).Stream)
        using (var icon = new System.Drawing.Icon(iconStream))
            trayIcon = (System.Drawing.Icon)icon.Clone();
        tray = new Forms.NotifyIcon { Icon = trayIcon, Text = "Razer Book RGB", Visible = !preview };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Razer Book RGB", null, (_, _) => Dispatcher.Invoke(ShowEditor));
        menu.Items.Add("Restore saved lighting", null, (_, _) => Dispatcher.Invoke(async () => await RestoreAsync()));
        menu.Items.Add(profilesMenu);
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(() => { exit = true; Close(); }));
        tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowEditor);
        LoadControls(); ready = true; UpdateKeyboard();
        RefreshProfiles();
        if (!preview)
        {
            SystemEvents.PowerModeChanged += PowerChanged;
            SystemEvents.SessionSwitch += SessionChanged;
        }
        SourceInitialized += (_, _) =>
        {
            if (preview) return;
            try { activity = new KeyboardActivity(new WindowInteropHelper(this).Handle); }
            catch (Exception ex) { IdleStatusText.Text = "Keyboard idle detection unavailable: " + ex.Message; }
        };
        idleTimer.Tick += async (_, _) => await IdleTickAsync();
        retryTimer.Tick += async (_, _) => { if (pendingRestore) await RestoreAsync(); };
        if (!preview) { retryTimer.Start(); idleTimer.Start(); }
        Loaded += async (_, _) =>
        {
            if (preview) { StatusText.Text = "Preview mode · hardware unchanged"; return; }
            if (startup) Hide();
            if (warning != null) { StatusText.Text = warning; return; }
            await ProbeAsync();
            if (saved.RestoreOnLaunch) await RestoreAsync();
        };
        Closing += OnClosing;
        Closed += (_, _) => { retryTimer.Stop(); idleTimer.Stop(); activity?.Dispose(); SystemEvents.PowerModeChanged -= PowerChanged; SystemEvents.SessionSwitch -= SessionChanged; tray.Visible = false; tray.Dispose(); trayIcon.Dispose(); };
    }
    static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
    void RefreshProfiles(string? selectedName = null)
    {
        string name = selectedName ?? ProfileBox.Text;
        ProfileBox.ItemsSource = library.Profiles.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
        ProfileBox.Text = name;
        ProfileControls.IsEnabled = libraryAvailable;
        foreach (Forms.ToolStripItem item in profilesMenu.DropDownItems.Cast<Forms.ToolStripItem>().ToArray()) item.Dispose();
        profilesMenu.DropDownItems.Clear();
        foreach (string entry in library.Profiles.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            var item = new Forms.ToolStripMenuItem(entry) { Tag = entry };
            // Preserve literal ampersands in user-supplied names.
            item.Text = entry.Replace("&", "&&");
            item.Click += async (_, _) => await LoadProfileAsync(entry);
            profilesMenu.DropDownItems.Add(item);
        }
        if (profilesMenu.DropDownItems.Count == 0)
            profilesMenu.DropDownItems.Add(new Forms.ToolStripMenuItem(libraryAvailable ? "No saved profiles" : "Profiles unavailable") { Enabled = false });
    }
    void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !libraryAvailable) return;
        if (!Profile.IsColor(HexBox.Text)) { ColorError.Text = "Enter a valid #RRGGBB color."; return; }
        try
        {
            string name = ProfileLibrary.ValidateName(ProfileBox.Text);
            if (library.Profiles.ContainsKey(name) && !preview && MessageBox.Show(this, "Replace saved profile '" + name + "'?", "Save profile", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            var next = library.Copy(); next.Set(name, profile);
            if (!preview) next.Save();
            library = next; RefreshProfiles(name);
            StatusText.Text = "Saved profile '" + name + "'. Load or Apply to update lighting.";
        }
        catch (Exception ex) { StatusText.Text = "Could not save named profile: " + ex.Message; }
    }
    async void LoadProfile_Click(object sender, RoutedEventArgs e) => await LoadProfileAsync(ProfileBox.Text.Trim());
    async Task LoadProfileAsync(string name)
    {
        if (busy || !libraryAvailable) return;
        if (!library.Profiles.TryGetValue(name, out var stored)) { StatusText.Text = "Select a saved profile to load."; return; }
        if (dirty && !preview && MessageBox.Show(this, "Discard unapplied preview changes and load '" + name + "'?", "Load profile", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        busy = true; ApplyButton.IsEnabled = false; ProfileControls.IsEnabled = false;
        try
        {
            var next = stored.Copy();
            next.RestoreOnLaunch = saved.RestoreOnLaunch; next.CloseToTray = saved.CloseToTray;
            if (!preview) SettingsStore.Save(next);
            saved = next.Copy(); profile = next; selected.Clear(); dirty = false;
            LoadControls(); UpdateKeyboard(); ProfileBox.Text = name;
            DirtyText.Text = "Loaded profile '" + name + "'";
            activity?.Reset(); nextIdleAttempt = 0;
            if (suspended || sessionLocked) { pendingRestore = true; DirtyText.Text += " - waiting for resume or unlock"; }
            else
            {
                bool applied = await SendAsync(next.Copy());
                pendingRestore = !applied && next.RestoreOnLaunch;
                if (!applied) DirtyText.Text += pendingRestore ? " - waiting for keyboard; retrying every 10 seconds" : " - saved; keyboard update failed";
            }
        }
        catch (Exception ex) { StatusText.Text = "Could not load profile: " + ex.Message; }
        finally { busy = false; ApplyButton.IsEnabled = true; ProfileControls.IsEnabled = libraryAvailable; }
    }
    void BuildKeyboard()
    {
        foreach (var row in KeyboardLayout.Rows)
        {
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            for (int i = 0; i < row.Length; i++)
            {
                var key = row[i]; grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(key.Width, GridUnitType.Star) });
                var button = new Button { Content = key.Label, Height = 34, Margin = new Thickness(2, 0, 2, 0), FontSize = key.Label.Length > 4 ? 9 : 11, Padding = new Thickness(1), ToolTip = key.Label + " · row " + key.Row + ", column " + key.Column };
                Grid.SetColumn(button, i); keys[button] = key; button.Click += Key_Click; grid.Children.Add(button);
            }
            KeyboardPanel.Children.Add(grid);
        }
    }
    void LoadControls()
    {
        updating = true;
        try { LoginCheck.IsChecked = Startup.Enabled; } catch { LoginCheck.IsChecked = false; }
        RestoreCheck.IsChecked = profile.RestoreOnLaunch; TrayCheck.IsChecked = profile.CloseToTray;
        string timeout = profile.IdleTimeoutSeconds.ToString();
        if (!IdleTimeoutBox.Items.Cast<ComboBoxItem>().Any(i => (string)i.Tag == timeout))
            IdleTimeoutBox.Items.Add(new ComboBoxItem { Content = timeout + " seconds", Tag = timeout });
        IdleTimeoutBox.SelectedValue = timeout;
        BrightnessSlider.Value = profile.Brightness; BrightnessLabel.Text = "Brightness  " + profile.Brightness + "%";
        updating = false; SetPaint(profile.StaticColor, false);
    }
    void SetPaint(string hex, bool change)
    {
        paint = hex.ToUpperInvariant(); updating = true;
        var rgb = Protocol.Rgb(paint);
        HexBox.Text = paint; Swatch.Background = Brush(paint);
        RedSlider.Value = rgb[0]; GreenSlider.Value = rgb[1]; BlueSlider.Value = rgb[2];
        RedLabel.Text = "Red  " + rgb[0]; GreenLabel.Text = "Green  " + rgb[1]; BlueLabel.Text = "Blue  " + rgb[2]; ColorError.Text = "";
        updating = false;
        if (change && ready && profile.Mode == "Static") { profile.StaticColor = paint; Changed(); }
    }
    void UpdateKeyboard()
    {
        bool custom = profile.Mode == "Per-key";
        StaticButton.BorderBrush = Brush(custom ? "#414850" : "#82EB71"); CustomButton.BorderBrush = Brush(custom ? "#82EB71" : "#414850");
        ModeHint.Text = custom ? "Select keys, choose a color, then paint your selection." : "One color across the entire keyboard.";
        SelectionTools.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        SelectionText.Text = custom ? selected.Count + " keys selected · Click a key to select or deselect it." : "Preview · " + profile.StaticColor;
        foreach (var pair in keys)
        {
            var color = (Color)ColorConverter.ConvertFromString(custom ? profile.Colors[pair.Value.Index] : profile.StaticColor);
            pair.Key.Background = new SolidColorBrush(Color.FromRgb((byte)(color.R * .20 + 20), (byte)(color.G * .20 + 20), (byte)(color.B * .20 + 20)));
            pair.Key.Foreground = new SolidColorBrush(color.R + color.G + color.B < 100 ? Colors.LightGray : color);
            pair.Key.BorderBrush = custom && selected.Contains(pair.Value.Index) ? Brushes.White : new SolidColorBrush(color);
            pair.Key.BorderThickness = new Thickness(custom && selected.Contains(pair.Value.Index) ? 2.5 : 1);
        }
    }
    void Changed() { dirty = true; DirtyText.Text = "Preview changed · Apply to update your keyboard"; UpdateKeyboard(); }
    void Key_Click(object sender, RoutedEventArgs e)
    {
        if (profile.Mode != "Per-key") return;
        int index = keys[(Button)sender].Index;
        if (!selected.Add(index)) selected.Remove(index);
        UpdateKeyboard();
    }
    void Static_Click(object sender, RoutedEventArgs e) { profile.Mode = "Static"; SetPaint(profile.StaticColor, false); Changed(); }
    void Settings_Click(object sender, RoutedEventArgs e) => SettingsCard.BringIntoView();
    void Custom_Click(object sender, RoutedEventArgs e) { profile.Mode = "Per-key"; Changed(); }
    void SelectAll_Click(object sender, RoutedEventArgs e) { foreach (var k in keys.Values) selected.Add(k.Index); UpdateKeyboard(); }
    void ClearSelection_Click(object sender, RoutedEventArgs e) { selected.Clear(); UpdateKeyboard(); }
    void Paint_Click(object sender, RoutedEventArgs e)
    {
        if (!Profile.IsColor(HexBox.Text)) { ColorError.Text = "Enter a valid #RRGGBB color."; return; }
        if (selected.Count == 0) { StatusText.Text = "Select at least one key to paint."; return; }
        foreach (var key in keys.Values.Where(k => selected.Contains(k.Index))) foreach (int i in KeyboardLayout.Addresses(key)) profile.Colors[i] = paint;
        Changed();
    }
    void Hex_Changed(object sender, TextChangedEventArgs e)
    {
        if (updating || !ready) return;
        if (Profile.IsColor(HexBox.Text)) SetPaint(HexBox.Text, true);
        else ColorError.Text = "Use # followed by six hex digits.";
    }
    void Rgb_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (updating || !ready) return;
        SetPaint($"#{(int)RedSlider.Value:X2}{(int)GreenSlider.Value:X2}{(int)BlueSlider.Value:X2}", true);
    }
    void Pick_Click(object sender, RoutedEventArgs e)
    {
        var rgb = Protocol.Rgb(paint);
        using var dialog = new Forms.ColorDialog { FullOpen = true, Color = System.Drawing.Color.FromArgb(rgb[0], rgb[1], rgb[2]) };
        if (dialog.ShowDialog() == Forms.DialogResult.OK) SetPaint($"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}", true);
    }
    void Brightness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (updating || !ready) return;
        profile.Brightness = (int)BrightnessSlider.Value; BrightnessLabel.Text = "Brightness  " + profile.Brightness + "%"; Changed();
    }
    void Login_Click(object sender, RoutedEventArgs e)
    {
        if (preview) return;
        try { Startup.Set(LoginCheck.IsChecked == true); StatusText.Text = LoginCheck.IsChecked == true ? "Startup enabled. Keep the EXE in its current location." : "Startup disabled."; }
        catch (Exception ex) { StatusText.Text = "Could not change startup: " + ex.Message; LoadControls(); }
    }
    void Preferences_Click(object sender, RoutedEventArgs e)
    {
        bool restore = RestoreCheck.IsChecked == true, close = TrayCheck.IsChecked == true;
        try
        {
            var next = saved.Copy(); next.RestoreOnLaunch = restore; next.CloseToTray = close;
            if (!preview) SettingsStore.Save(next);
            saved = next; profile.RestoreOnLaunch = restore; profile.CloseToTray = close;
            if (!restore) pendingRestore = false;
            StatusText.Text = "Preferences saved.";
        }
        catch (Exception ex) { StatusText.Text = "Could not save preferences: " + ex.Message; LoadControls(); }
    }
    void IdleTimeout_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (updating || !ready || !int.TryParse(IdleTimeoutBox.SelectedValue as string, out int seconds)) return;
        try
        {
            var next = saved.Copy(); next.IdleTimeoutSeconds = seconds;
            if (!preview) SettingsStore.Save(next);
            saved = next; profile.IdleTimeoutSeconds = seconds;
            activity?.Reset(); nextIdleAttempt = 0;
            StatusText.Text = seconds == 0 ? "Idle timeout disabled · keep the app running to keep lights on." : "Lights off after " + seconds + " seconds without a key press.";
            if (!lightingManaged && saved.RestoreOnLaunch) pendingRestore = true;
        }
        catch (Exception ex) { StatusText.Text = "Could not save idle timeout: " + ex.Message; LoadControls(); }
    }
    async Task IdleTickAsync()
    {
        if (!lightingManaged || suspended || busy || pendingRestore || preview) return;
        long now = Environment.TickCount64;
        if (now < nextIdleAttempt) return;
        // If raw input registration failed, keep lights on rather than guessing inactivity.
        int seconds = activity == null ? 0 : saved.IdleTimeoutSeconds;
        var action = idlePolicy.Next(now, activity?.LastKeyAt ?? now, seconds, sessionLocked);
        if (activity != null)
            IdleStatusText.Text = sessionLocked ? "Lighting paused while Windows is locked." : idlePolicy.IsDark ? "Lights off · press any key to restore." : seconds == 0 ? "Never · lights stay on while the app runs." : "Lights off in " + Math.Max(0, seconds - (now - activity.LastKeyAt) / 1000) + "s · keyboard activity only";
        if (action == LightingAction.None || !await hardwareGate.WaitAsync(0)) return;
        try
        {
            var current = saved.Copy();
            await Task.Run(() =>
            {
                using var keyboard = BookKeyboard.Open();
                if (action == LightingAction.TurnOff) keyboard.SetBrightness(0);
                else if (action == LightingAction.Restore) keyboard.Apply(current);
                else keyboard.KeepAlive();
            });
            idlePolicy.Completed(action, Environment.TickCount64);
        }
        catch (Exception ex) { nextIdleAttempt = Environment.TickCount64 + 10000; StatusText.Text = "Lighting timer: " + ex.Message; }
        finally { hardwareGate.Release(); }
    }
    async Task<bool> SendAsync(Profile p)
    {
        if (preview) { StatusText.Text = "Preview mode · hardware unchanged"; return true; }
        await hardwareGate.WaitAsync();
        try
        {
            await Task.Run(() => { using var keyboard = BookKeyboard.Open(); keyboard.Apply(p); });
            lightingManaged = true; activity?.Reset(); idlePolicy.Applied(Environment.TickCount64); nextIdleAttempt = 0;
            StatusText.Text = "Lighting applied · Razer Book 13"; return true;
        }
        catch (Exception ex) { StatusText.Text = ex.Message; return false; }
        finally { hardwareGate.Release(); }
    }
    async Task ApplyAsync()
    {
        if (busy) return;
        if (!Profile.IsColor(HexBox.Text)) { ColorError.Text = "Enter a valid #RRGGBB color."; return; }
        busy = true; ApplyButton.IsEnabled = false;
        var next = profile.Copy();
        try
        {
            if (!preview) SettingsStore.Save(next);
            saved = next; dirty = false; DirtyText.Text = "Profile saved";
            pendingRestore = !await SendAsync(next) && next.RestoreOnLaunch;
            if (pendingRestore) DirtyText.Text = "Saved · waiting for keyboard; retrying every 10 seconds";
        }
        catch (Exception ex) { StatusText.Text = "Could not save profile: " + ex.Message; }
        finally { busy = false; ApplyButton.IsEnabled = true; }
    }
    async void Apply_Click(object sender, RoutedEventArgs e) => await ApplyAsync();
    async Task ProbeAsync()
    {
        if (preview) return;
        await hardwareGate.WaitAsync();
        try { StatusText.Text = await Task.Run(() => { using var keyboard = BookKeyboard.Open(); return keyboard.Probe(); }); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { hardwareGate.Release(); }
    }
    async Task RestoreAsync()
    {
        if (busy || preview || suspended || sessionLocked) return;
        busy = true;
        try { pendingRestore = !await SendAsync(saved.Copy()); }
        finally { busy = false; }
    }
    async void Reconnect_Click(object sender, RoutedEventArgs e) { await ProbeAsync(); if (pendingRestore) await RestoreAsync(); }
    void PowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (e.Mode == PowerModes.Suspend) suspended = true;
            if (e.Mode == PowerModes.Resume)
            {
                suspended = false; activity?.Reset();
                if (saved.RestoreOnLaunch) pendingRestore = true;
                else lightingManaged = false;
            }
        });
    }
    void SessionChanged(object sender, SessionSwitchEventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (e.Reason == SessionSwitchReason.SessionLock || e.Reason == SessionSwitchReason.ConsoleDisconnect || e.Reason == SessionSwitchReason.RemoteDisconnect) sessionLocked = true;
        if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.ConsoleConnect || e.Reason == SessionSwitchReason.RemoteConnect)
        { sessionLocked = false; activity?.Reset(); }
    });
    void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "RGB profile (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try { var next = SettingsStore.Read(dialog.FileName); next.RestoreOnLaunch = profile.RestoreOnLaunch; next.CloseToTray = profile.CloseToTray; profile = next; LoadControls(); Changed(); StatusText.Text = "Profile imported. Apply to use it."; }
        catch (Exception ex) { StatusText.Text = "Cannot import profile: " + ex.Message; }
    }
    void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "RGB profile (*.json)|*.json", FileName = "My-keyboard.json" };
        if (dialog.ShowDialog(this) != true) return;
        try { SettingsStore.Save(profile, dialog.FileName); StatusText.Text = "Profile exported."; }
        catch (Exception ex) { StatusText.Text = "Cannot export profile: " + ex.Message; }
    }
    public void ShowEditor() { Show(); WindowState = WindowState.Normal; Activate(); }
    public async Task VerifyEditor()
    {
        if (!preview) throw new InvalidOperationException("UI checks require preview mode.");
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException("UI test: " + message); }
        using (var input = new KeyboardActivity(new WindowInteropHelper(this).Handle))
        {
            input.Reset();
            Check(Environment.TickCount64 - input.LastKeyAt < 1000, "background keyboard input registration");
        }
        HexBox.Text = "#112233";
        Check(profile.StaticColor == "#112233" && RedSlider.Value == 17 && GreenSlider.Value == 34 && BlueSlider.Value == 51, "hex and RGB synchronization");
        RedSlider.Value = 255;
        Check(profile.StaticColor == "#FF2233", "RGB edit updates static preview");
        Custom_Click(this, new RoutedEventArgs());
        var w = keys.Single(k => k.Value.Label == "W").Key;
        w.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        HexBox.Text = "#0088FF";
        Paint_Click(this, new RoutedEventArgs());
        Check(profile.Colors[35] == "#0088FF" && profile.Colors[34] == "#44D62C", "paint affects only selected key");
        SelectAll_Click(this, new RoutedEventArgs()); HexBox.Text = "#AA00CC"; Paint_Click(this, new RoutedEventArgs());
        Check(keys.Values.All(k => profile.Colors[k.Index] == "#AA00CC") && profile.Colors[86] == "#AA00CC" && profile.Colors[88] == "#AA00CC", "select all and space LEDs");
        BrightnessSlider.Value = 23; Check(profile.Brightness == 23, "brightness edit");
        HexBox.Text = "#bad"; Check(ColorError.Text.Length > 0 && paint == "#AA00CC", "invalid hex preserves last valid color");
        IdleTimeoutBox.SelectedValue = "30"; Check(profile.IdleTimeoutSeconds == 30 && saved.IdleTimeoutSeconds == 30, "idle preference saves independently of preview colors");
        IdleTimeoutBox.SelectedValue = "0"; Check(saved.IdleTimeoutSeconds == 0, "Never option");
        HexBox.Text = "#AA00CC";
        IdleTimeoutBox.SelectedValue = "30";
        ProfileBox.Text = "Gaming & RGB"; SaveProfileButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(library.Profiles.Count == 1 && profilesMenu.DropDownItems.Count == 1, "save profile populates UI and tray");
        Static_Click(this, new RoutedEventArgs()); HexBox.Text = "#112233"; BrightnessSlider.Value = 80;
        ProfileBox.Text = "Reading"; SaveProfileButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(library.Profiles.Count == 2 && ProfileBox.Items.Count == 2, "multiple named profiles");
        ProfileBox.Text = "Gaming & RGB"; LoadProfileButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(profile.Mode == "Per-key" && profile.Brightness == 23 && profile.Colors[35] == "#AA00CC" && profile.IdleTimeoutSeconds == 30, "UI loads all profile lighting settings");
        HexBox.Text = "#FFFFFF"; Paint_Click(this, new RoutedEventArgs());
        var readingItem = profilesMenu.DropDownItems.Cast<Forms.ToolStripMenuItem>().Single(i => (string?)i.Tag == "Reading");
        readingItem.PerformClick();
        Check(profile.Mode == "Static" && profile.StaticColor == "#112233" && profile.Brightness == 80 && !dirty, "tray loads and applies profile");
        saved.CloseToTray = false;
        await LoadProfileAsync("Gaming & RGB");
        Check(!profile.CloseToTray && library.Profiles["Gaming & RGB"].CloseToTray, "loading preserves global preferences");
        ProfileBox.Text = "gaming & rgb"; BrightnessSlider.Value = 42; SaveProfile_Click(this, new RoutedEventArgs());
        Check(library.Profiles.Count == 2 && library.Profiles["Gaming & RGB"].Brightness == 42, "overwrite by name is case insensitive");
        library = new(); RefreshProfiles("");
        profile = new Profile(); saved = profile.Copy(); selected.Clear(); LoadControls(); UpdateKeyboard(); dirty = false; DirtyText.Text = "";
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ui-test-result.txt"), "PASS: hex/RGB synchronization, single-key paint, select all, space LEDs, brightness, invalid input, immediate idle preference, Never option, multiple named profiles, overwrite, UI and tray loading, global preferences. Preview only; no hardware or startup changes.");
    }
    void OnClosing(object? sender, CancelEventArgs e)
    {
        if (preview) return;
        if (!exit && profile.CloseToTray) { e.Cancel = true; Hide(); return; }
        if (dirty && MessageBox.Show(this, "You have unapplied preview changes. Exit without applying them?", "Razer Book RGB", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) { exit = false; e.Cancel = true; }
    }
}
