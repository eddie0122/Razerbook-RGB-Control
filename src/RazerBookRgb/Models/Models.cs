using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace RazerBookRgb;

public sealed class Profile
{
    public string Mode { get; set; } = "Static";
    public string StaticColor { get; set; } = "#44D62C";
    public int Brightness { get; set; } = 65;
    public string[] Colors { get; set; } = Enumerable.Repeat("#44D62C", 96).ToArray();
    public bool RestoreOnLaunch { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public int IdleTimeoutSeconds { get; set; } = 0; // Zero means Never; old profiles inherit it.

    public void Validate()
    {
        if (Mode != "Static" && Mode != "Per-key") throw new InvalidDataException("Unknown lighting mode.");
        if (Brightness < 0 || Brightness > 100) throw new InvalidDataException("Brightness must be 0–100.");
        if (IdleTimeoutSeconds < 0 || IdleTimeoutSeconds > 86400) throw new InvalidDataException("Idle timeout must be 0–86400 seconds.");
        if (!IsColor(StaticColor) || Colors == null || Colors.Length != 96 || Colors.Any(c => !IsColor(c)))
            throw new InvalidDataException("Profile must contain 96 valid #RRGGBB colors.");
    }
    public static bool IsColor(string? s) => s != null && Regex.IsMatch(s, "^#[0-9a-fA-F]{6}$");
    public Profile Copy() => new() { Mode = Mode, StaticColor = StaticColor, Brightness = Brightness, Colors = (string[])Colors.Clone(), RestoreOnLaunch = RestoreOnLaunch, CloseToTray = CloseToTray, IdleTimeoutSeconds = IdleTimeoutSeconds };
}

public static class SettingsStore
{
    public static string Folder => AppContext.BaseDirectory;
    public static string FilePath => Path.Combine(Folder, "settings.json");
    public static Profile Read(string path)
    {
        var p = JsonSerializer.Deserialize<Profile>(File.ReadAllText(path)) ?? throw new InvalidDataException("Empty profile.");
        p.Validate();
        return p;
    }
    public static void Save(Profile p, string? path = null)
    {
        p.Validate();
        path ??= FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(p, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
    }
}

public sealed class ProfileLibrary
{
    public Dictionary<string, Profile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public static string FilePath => Path.Combine(SettingsStore.Folder, "profiles.json");

    public static string ValidateName(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Length > 80 || name.Any(char.IsControl))
            throw new InvalidDataException("Enter a profile name of 1 to 80 characters.");
        return name;
    }
    public ProfileLibrary Copy() => new() { Profiles = Profiles.ToDictionary(p => p.Key, p => p.Value.Copy(), StringComparer.OrdinalIgnoreCase) };
    public void Set(string name, Profile profile)
    {
        name = ValidateName(name);
        profile.Validate();
        Profiles[name] = profile.Copy();
    }
    public static ProfileLibrary Read(string? path = null)
    {
        path ??= FilePath;
        if (!File.Exists(path)) return new();
        var raw = JsonSerializer.Deserialize<ProfileLibrary>(File.ReadAllText(path)) ?? throw new InvalidDataException("Empty profile library.");
        if (raw.Profiles == null) throw new InvalidDataException("Missing profiles.");
        var result = new ProfileLibrary();
        foreach (var entry in raw.Profiles)
        {
            string name = ValidateName(entry.Key);
            if (entry.Value == null || result.Profiles.ContainsKey(name)) throw new InvalidDataException("Invalid or duplicate profile: " + name);
            result.Set(name, entry.Value);
        }
        return result;
    }
    public void Save(string? path = null)
    {
        path ??= FilePath;
        foreach (var entry in Profiles) { ValidateName(entry.Key); entry.Value.Validate(); }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
    }
}

public static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "RazerBookRGB";
    public static bool Enabled
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(RunKey); return key?.GetValue(Name) is string; }
    }
    public static string Command(string path) => "\"" + path + "\" --startup";
    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (enabled) key.SetValue(Name, Command(Environment.ProcessPath ?? throw new IOException("Cannot locate executable.")));
        else key.DeleteValue(Name, false);
    }
}

public record KeySpec(string Label, int Row, int Column, double Width = 1)
{
    public int Index => Row * 16 + Column;
}

public static class KeyboardLayout
{
    // Razer Book 13 standard matrix. Space illuminates multiple physical LEDs.
    public static readonly KeySpec[][] Rows = {
        new[] { new KeySpec("Esc",0,1), new("F1",0,2), new("F2",0,3), new("F3",0,4), new("F4",0,5), new("F5",0,6), new("F6",0,7), new("F7",0,8), new("F8",0,9), new("F9",0,10), new("F10",0,11), new("F11",0,12), new("F12",0,13), new("Del",0,14), new("Power",0,15) },
        new[] { new KeySpec("`",1,1), new("1",1,2), new("2",1,3), new("3",1,4), new("4",1,5), new("5",1,6), new("6",1,7), new("7",1,8), new("8",1,9), new("9",1,10), new("0",1,11), new("−",1,12), new("=",1,13), new("Backspace",1,15,2) },
        new[] { new KeySpec("Tab",2,1,1.5), new("Q",2,2), new("W",2,3), new("E",2,4), new("R",2,5), new("T",2,6), new("Y",2,7), new("U",2,8), new("I",2,9), new("O",2,10), new("P",2,11), new("[",2,12), new("]",2,13), new("\\",2,15,1.5) },
        new[] { new KeySpec("Caps",3,1,1.8), new("A",3,2), new("S",3,3), new("D",3,4), new("F",3,5), new("G",3,6), new("H",3,7), new("J",3,8), new("K",3,9), new("L",3,10), new(";",3,11), new("'",3,12), new("Enter",3,15,2.2) },
        new[] { new KeySpec("Shift",4,1,2.3), new("Z",4,3), new("X",4,4), new("C",4,5), new("V",4,6), new("B",4,7), new("N",4,8), new("M",4,9), new(",",4,10), new(".",4,11), new("/",4,12), new("Shift",4,15,2.7) },
        new[] { new KeySpec("Ctrl",5,1), new("Fn",5,2), new("Win",5,3), new("Alt",5,5), new("Space",5,7,5), new("Alt",5,9), new("Ctrl",5,11), new("←",5,12), new("↑",5,13), new("↓",5,15), new("→",5,14) }
    };
    public static IEnumerable<int> Addresses(KeySpec key) => key.Label == "Space" ? new[] { 86, 87, 88 } : new[] { key.Index };
}
