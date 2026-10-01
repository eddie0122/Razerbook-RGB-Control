# Razer Book RGB

## 1. Overview

Razer Book RGB is a Windows desktop application for controlling the Razer Book 13 keyboard backlight without a separate Chroma SDK. It targets the keyboard with USB ID `1532:026A`, HID interface 2.

- Set a single color or paint individual keys with independent brightness control.
- Save named lighting profiles and import or export profiles as JSON.
- Turn lighting off after keyboard inactivity and restore it on the next key press.
- Manage lighting from the system tray, optionally starting at Windows login.

This is an independent application, not an official Razer product. Other Razer laptops are not supported. The keyboard preview uses ANSI-style physical positions; ISO/JIS layouts may differ, and per-key positions need visual confirmation on your device.

## 2. How to use it

Build the application using section 3, then open `dist/RazerBookRGB.exe` on Windows x64. Generated executables are excluded from the source repository. The standalone executable requires no separate .NET runtime or administrator account. Exit an older running copy from its tray menu first.

1. Choose **Static color**, then select a swatch, use the color picker, or enter RGB / `#RRGGBB`.
2. For a custom design, choose **Per-key color**, select keys, choose a color, and click **Paint selected**. Click a selected key again to deselect it. Static colors and custom designs are stored separately.
3. Adjust **Brightness**, then click **Apply & save**. Zero brightness turns the backlight off.
4. To save a snapshot, enter a profile name and click **Save profile**. Choose a saved profile and click **Load** to apply it, or use the tray's **Profiles** menu. Later edits do not overwrite named profiles until you save them again. Loading prompts before discarding unapplied edits.
5. Use **Import / Export** to share JSON profiles. After importing, name and save the profile to add it to the profile menu.
6. In **Settings**, choose **Lights off after no key press**. Any attached keyboard resets this timer; mouse activity does not. The next key press restores the saved lighting. **Never** is the default.

Closing the window keeps the app in the tray by default. Double-click its icon to reopen it, or choose **Exit** from its menu to quit. Keep it running for lighting keep-alive and idle control. Preview edits are not applied by the timer.

**Start at Windows login** launches into the tray and is off by default. Keep the executable in a permanent location before enabling it. Disable startup before moving the executable, then enable it from the new location. When upgrading to a different folder, toggle startup off and on in the new version.

**Restore saved lighting on launch and wake** controls automatic restoration. If disabled, click **Apply & save** after launch or resume to start managing lighting. Locking Windows pauses lighting; unlocking restores it. Suspend pauses USB traffic. Startup, restore, and close-to-tray preferences are global rather than profile-specific.

### Portable settings

Settings are stored in the executable's directory, regardless of the folder from which you launch it. Use a writable folder so the app can save changes.

```text
RazerBookRGB/
  RazerBookRGB.exe
  settings.json       Created when settings are saved
  profiles.json       Created when named profiles are saved
```

- Settings: `settings.json` beside `RazerBookRGB.exe`. Preferences save immediately; lighting changes save with **Apply & save**.
- Named profiles: `profiles.json` beside `RazerBookRGB.exe`. Names are case insensitive and limited to 80 characters. Keep both JSON files when replacing the executable or copy them with it when moving to a new folder.
- Startup registration: the `RazerBookRGB` value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. Windows Startup Apps can also disable it.

### Troubleshooting

- Use **Reconnect** to check the device again. Failed device updates still save settings and retry every ten seconds when automatic restoration is enabled.
- Exit competing lighting controllers, such as Synapse or OpenRGB, if they overwrite colors or cause response errors.
- Lighting may persist after exit until another controller, sleep, or reboot changes it. Idle timing returns to the laptop or other lighting software after exit.
- The executable is unsigned and may display a Windows reputation prompt.

## 3. How to build and deploy

Install the **.NET 8 SDK on Windows**. There are no third-party NuGet dependencies. The initial publish needs network access to download Microsoft runtime packs.

From the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

The script publishes a self-contained Windows x64 executable to `dist`, and runs both self-tests and UI preview checks. It copies this README into the release folder. The single executable bundles WPF native libraries, which .NET extracts on first launch.

To publish into a different folder, including when the existing release is running:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1 -OutputDirectory .\artifacts\release
```

Deploy by copying `RazerBookRGB.exe` to a permanent writable folder on the target Windows x64 machine. No installer is required. Include the README for usage instructions; test reports and `preview.png` are optional. Exit the running copy before replacing it. User settings and named profiles live beside the executable. Preserve both JSON files during upgrades; do not include personal settings in a public release.

For a regular development build:

```powershell
dotnet build .\src\RazerBookRgb\RazerBookRgb.csproj -c Debug
```

## 4. Directory structure

```text
README.md                        Product and contributor documentation
.gitignore                       Build, local settings, and diagnostic exclusions
src/
  RazerBookRgb/
    RazerBookRgb.csproj           WPF project and release version
    Program.cs                   Entry point and diagnostic commands
    app.manifest                 Windows application manifest
    Assets/App.ico               Executable, window, and tray icon
    Views/                       Main window XAML and editor behavior
    Models/                      Profiles, persistence, startup, key layout
    Hardware/                    HID protocol, transport, keyboard activity
    Diagnostics/                 Built-in self-tests
scripts/
  build.ps1                      Publish and verify a release
  New-AppIcon.ps1                Regenerate the multi-resolution icon
```

Builds create the following local directories, which are excluded from Git:

| Directory | Contents |
| --- | --- |
| `dist/` | Published executable, README, test reports, and UI preview |
| `src/RazerBookRgb/bin/` | Compiled application files |
| `src/RazerBookRgb/obj/` | Intermediate build files |
| `.dotnet/` | Local .NET CLI state and package cache used by the build script |
| `artifacts/` | Optional output when selected with `-OutputDirectory` |

Old build caches, earlier release folders, and downloaded research sources have been removed from the working tree.

## 5. Development information

The application uses WPF for the editor, Windows Forms for the tray menu, and Windows SetupAPI / HID feature reports for device access. Source folders separate responsibilities while retaining the `RazerBookRgb` namespace. The built-in diagnostics compile into the executable so published builds can be checked directly.

### Verification commands

PowerShell can wait for the GUI executable to complete each diagnostic:

```powershell
Start-Process .\dist\RazerBookRGB.exe -ArgumentList '--self-test' -Wait
Start-Process .\dist\RazerBookRGB.exe -ArgumentList '--preview' -Wait
```

| Argument | Behavior |
| --- | --- |
| `--self-test` | Checks protocol packets, profiles, persistence, and idle policy. Writes `self-test-result.txt`. |
| `--preview` | Runs UI interaction checks and writes `ui-test-result.txt` and `preview.png`, without accessing the keyboard or writing user settings. |
| `--probe` | Reads device firmware without changing lighting. Writes `probe-result.txt`. |
| `--hardware-test` | Changes lighting to test brightness, static color, six custom rows, and the custom-mode commit. Leaves static green at the previous hardware brightness. |
| `--idle-hardware-test` | Checks keep-alive acknowledgements over 12 seconds, briefly turns lighting off, then restores brightness. Leaves colors unchanged. |
| `--apply-saved` | Applies saved lighting and exits. |
| `--startup` | Starts the regular application in the tray. |

Diagnostic reports are written next to the executable; failures attempt to write `error.txt` and return a nonzero exit code. Run hardware diagnostics deliberately on a supported device. Software checks cannot confirm physical key placement or visible illumination.

### Application icon

`Assets/App.ico` contains 16, 24, 32, 48, 64, 128, and 256 pixel versions of the RGB keyboard design. The project embeds it in the executable and as a WPF resource; the window and tray share that asset. To regenerate it on Windows:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\New-AppIcon.ps1
```

### Protocol references and privacy

The protocol implementation was informed by OpenRGB's Razer device definitions and transport, and OpenRazer's laptop brightness commands. Those upstream projects are GPL-2.0-or-later; downloaded research copies are not included in this source tree or the executable.

Keyboard inactivity uses Windows Raw Input. Only the last key-down timestamp is retained; typed text and key codes are not recorded.
