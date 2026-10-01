using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RazerBookRgb;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--self-test")) { SelfTest.Run(); return 0; }
            if (args.Contains("--idle-hardware-test"))
            {
                using var keyboard = BookKeyboard.Open();
                byte original = keyboard.Exchange(Protocol.Packet(0x0E, 0x84, 1, 0))[10];
                try
                {
                    for (int i = 0; i < 8; i++) { keyboard.KeepAlive(); Thread.Sleep(1500); }
                    keyboard.SetBrightness(0);
                    if (keyboard.Exchange(Protocol.Packet(0x0E, 0x84, 1, 0))[10] != 0) throw new IOException("Idle off brightness did not read back as zero.");
                }
                finally { keyboard.SetBrightness(original); }
                if (keyboard.Exchange(Protocol.Packet(0x0E, 0x84, 1, 0))[10] != original) throw new IOException("Wake brightness readback differs.");
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "idle-hardware-test-result.txt"), "PASS: eight keep-alives over 12 seconds acknowledged; idle brightness zero and original wake brightness verified by device readback. Original colors and brightness preserved. Physical illumination requires visual confirmation.");
                return 0;
            }
            if (args.Contains("--hardware-test"))
            {
                using var keyboard = BookKeyboard.Open();
                string device = keyboard.Probe();
                byte brightness = keyboard.Exchange(Protocol.Packet(0x0E, 0x84, 1, 0))[10];
                var test = new Profile { Mode = "Per-key", Brightness = (int)Math.Round(brightness * 100.0 / 255) };
                test.Colors[2 * 16 + 3] = "#0088FF";
                try
                {
                    keyboard.Apply(new Profile { Brightness = test.Brightness });
                    keyboard.Apply(test);
                    Thread.Sleep(1000);
                }
                finally
                {
                    keyboard.Exchange(Protocol.Static("#44D62C"));
                    keyboard.Exchange(Protocol.Packet(0x0E, 4, 1, brightness));
                }
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "hardware-test-result.txt"), device + Environment.NewLine + "PASS: static color, brightness, six per-key rows, custom-mode commit; all device acknowledgements checked. Original brightness restored; static green applied. Physical key positions still require visual confirmation.");
                return 0;
            }
            if (args.Contains("--probe"))
            {
                using var keyboard = BookKeyboard.Open();
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "probe-result.txt"), keyboard.Probe() + Environment.NewLine + keyboard.Path);
                return 0;
            }
            if (args.Contains("--apply-saved"))
            {
                using var keyboard = BookKeyboard.Open(); keyboard.Apply(SettingsStore.Read(SettingsStore.FilePath)); return 0;
            }
            bool preview = args.Contains("--preview");
            using var mutex = new Mutex(true, @"Local\RazerBookRGB.SingleInstance", out bool first);
            if (!first && !preview) { MessageBox.Show("Razer Book RGB is already running. Open it from the system tray.", "Razer Book RGB"); return 0; }
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var window = new MainWindow(args.Contains("--startup"), preview);
            if (preview)
            {
                window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
                {
                    await window.VerifyEditor();
                    window.UpdateLayout();
                    var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(window);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(AppContext.BaseDirectory, "preview.png")); encoder.Save(file);
                    window.Close();
                }));
            }
            return app.Run(window);
        }
        catch (Exception ex)
        {
            string report = ex.ToString();
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "error.txt"), report); } catch { }
            if (!args.Any(a => a.StartsWith("--"))) MessageBox.Show(ex.Message, "Razer Book RGB", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }
}
