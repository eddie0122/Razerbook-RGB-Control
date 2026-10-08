using System;
using System.IO;
using System.Linq;

namespace RazerBookRgb;

public static class SelfTest
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); checks++; }
        var p = Protocol.Static("#123456");
        Check(p.Length == 91 && p[0] == 0 && p[2] == 0x3F && p[6] == 4 && p[7] == 3 && p[8] == 10, "static header");
        Check(p[9] == 6 && p[10] == 0x12 && p[11] == 0x34 && p[12] == 0x56 && p[89] == 0x7B, "static golden packet and checksum");
        var profile = new Profile { Mode = "Per-key" };
        for (int i = 0; i < 96; i++) profile.Colors[i] = $"#{i:X2}1234";
        var frames = Protocol.Frames(profile).ToArray();
        Check(frames.Length == 7, "six rows and commit");
        for (int row = 0; row < 6; row++)
        {
            var frame = frames[row];
            Check(frame[6] == 52 && frame[8] == 11 && frame[9] == 255 && frame[10] == row && frame[11] == 0 && frame[12] == 15, "frame bounds");
            for (int col = 0; col < 16; col++) Check(frame[13 + col * 3] == row * 16 + col && frame[14 + col * 3] == 0x12 && frame[15 + col * 3] == 0x34, "RGB matrix address");
        }
        Check(frames[6][9] == 5 && frames[6][10] == 0, "custom commit");
        var all = KeyboardLayout.Rows.SelectMany(r => r).ToArray();
        Check(all.All(k => k.Index >= 0 && k.Index < 96) && all.Select(k => k.Index).Distinct().Count() == all.Length, "unique valid key addresses");
        Check(KeyboardLayout.Addresses(all.Single(k => k.Label == "Space")).SequenceEqual(new[] { 86, 87, 88 }), "spacebar LEDs");
        Check(Startup.Command(@"C:\A B\Book.exe") == "\"C:\\A B\\Book.exe\" --startup", "quoted startup path");
        string temp = Path.Combine(Path.GetTempPath(), "BookRgb-test-" + Guid.NewGuid() + ".json");
        try { SettingsStore.Save(profile, temp); var loaded = SettingsStore.Read(temp); Check(loaded.Colors.SequenceEqual(profile.Colors) && loaded.Mode == "Per-key", "profile round trip"); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        try
        {
            var library = new ProfileLibrary();
            library.Set(" Gaming ", profile);
            library.Set("Reading", new Profile { StaticColor = "#112233", Brightness = 20, IdleTimeoutSeconds = 60 });
            profile.Colors[0] = "#FFFFFF";
            Check(library.Profiles["Gaming"].Colors[0] != profile.Colors[0], "named profiles own independent color arrays");
            library.Save(temp);
            var loaded = ProfileLibrary.Read(temp);
            Check(loaded.Profiles.Count == 2 && loaded.Profiles["reading"].IdleTimeoutSeconds == 60 && loaded.Profiles["Gaming"].Mode == "Per-key", "multiple profiles round trip");
            loaded.Set("READING", new Profile { Brightness = 90 });
            Check(loaded.Profiles.Count == 2 && loaded.Profiles["Reading"].Brightness == 90, "case insensitive replacement");
            bool rejected = false;
            try { loaded.Set("  ", profile); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "reject blank profile name");
            File.WriteAllText(temp, "{\"Profiles\":{\"Broken\":null}}");
            rejected = false;
            try { ProfileLibrary.Read(temp); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "reject malformed library without overwriting");
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        profile.Colors = new string[1]; bool invalid = false;
        try { profile.Validate(); } catch (InvalidDataException) { invalid = true; }
        Check(invalid, "reject malformed profile");
        Check(!Profile.IsColor("#GG0000") && !Profile.IsColor(null), "reject malformed color");
        var timer = new IdleLightingPolicy(); timer.Applied(1000);
        Check(timer.Next(2000, 1000, 5, false) == LightingAction.None, "no premature heartbeat");
        Check(timer.Next(2500, 1000, 5, false) == LightingAction.KeepAlive, "watchdog heartbeat");
        timer.Completed(LightingAction.KeepAlive, 5999);
        Check(timer.Next(5999, 1000, 5, false) == LightingAction.None, "on until exact deadline");
        Check(timer.Next(6000, 1000, 5, false) == LightingAction.TurnOff, "off at deadline");
        // An unsuccessful USB write must not advance policy state.
        Check(timer.Next(6100, 1000, 5, false) == LightingAction.TurnOff, "retry failed off command");
        timer.Completed(LightingAction.TurnOff, 6100);
        Check(timer.Next(10000, 1000, 5, false) == LightingAction.None, "no heartbeat while dark");
        Check(timer.Next(10000, 9999, 5, false) == LightingAction.Restore, "key press wakes lighting");
        Check(timer.Next(10000, 1000, 0, false) == LightingAction.Restore, "Never wakes idle lighting");
        timer.Completed(LightingAction.Restore, 10000);
        Check(timer.Next(86401000, 1000, 0, false) == LightingAction.KeepAlive, "Never stays lit indefinitely");
        Check(timer.Next(10001, 10000, 0, true) == LightingAction.TurnOff, "lock overrides Never");
        timer.Completed(LightingAction.TurnOff, 10001);
        Check(timer.Next(10002, 10002, 0, true) == LightingAction.None, "lock cannot wake on input");
        Check(timer.Next(10002, 10002, 0, false) == LightingAction.Restore, "unlock restores");
        timer.Applied(15000);
        Check(!timer.IsDark && timer.Next(15001, 15000, 5, false) == LightingAction.None, "apply resets policy");
        timer.LightingLost();
        Check(timer.Next(15002, 15000, 5, false) == LightingAction.Restore, "external off restores while active");
        Check(timer.Next(15002, 15000, 0, true) == LightingAction.None, "external off while locked stays dark");
        var old = System.Text.Json.JsonSerializer.Deserialize<Profile>("{}")!;
        Check(old.IdleTimeoutSeconds == 0, "legacy profiles default to Never");
        old.IdleTimeoutSeconds = 30;
        Check(old.Copy().IdleTimeoutSeconds == 30, "profile copy retains timeout");
        old.IdleTimeoutSeconds = -1; invalid = false;
        try { old.Validate(); } catch (InvalidDataException) { invalid = true; }
        Check(invalid, "reject invalid timeout");
        old.IdleTimeoutSeconds = 86401; invalid = false;
        try { old.Validate(); } catch (InvalidDataException) { invalid = true; }
        Check(invalid, "reject excessive timeout");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "self-test-result.txt"), $"PASS: {checks} checks. No hardware or startup settings changed.");
    }
}
