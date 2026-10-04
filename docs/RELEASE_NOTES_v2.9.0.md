# Android Dev Monitor v2.9.0

Android Dev Monitor now runs on **Linux** as well as Windows.

## Downloads

- **Windows 10/11 x64:** `AndroidDevMonitor-v2.9.0-win-x64.zip` — extract and run `AndroidDevMonitor.exe`.
- **Linux x64:** `AndroidDevMonitor-v2.9.0-linux-x64.tar.gz` — extract and run `./AndroidDevMonitor/AndroidDevMonitor`, or `./AndroidDevMonitor/install.sh` to add a menu entry and the `android-dev-monitor` command.

Both are self-contained; no .NET install is needed.

## New

- **Linux app.** The same pages, charts and tools as on Windows: live CPU, memory, disk, network, FPS and thermal cards, the process table, logcat with filters and bookmarks, File Explorer, ADB Shell, Automation, the in-app mirror, Media, Performance sessions, Alerts, Wireless and the Developer Tools. adb, the emulator and scrcpy are found through `ANDROID_HOME`/`ANDROID_SDK_ROOT`, `~/Android/Sdk`, `/usr/lib/android-sdk`, `/opt/android-sdk` or `PATH`. Data lives in `~/.local/share/AndroidDevMonitor`, "Start when I log in" uses `~/.config/autostart`, and logging out or `kill` closes the app cleanly with the session saved.
- **Automated UI tests.** CI builds both apps and runs headless UI tests of the Linux app against a fake Android device (file transfer with quotes and spaces in names, ADB Shell, screenshots, the live mirror, APK install, crash alerts, saving the session on close).

## Fixed (Windows and Linux)

- **Default-on settings start on.** Confirm before closing during recording or automation, alert sounds, compact density, and restoring the last page and device were read as off the first time the app started. Settings you already changed are kept.
- **"Open media directory"** in Settings now opens the media folder (the button did nothing).
- **Clear device problems.** A phone that is unauthorized, offline or (on Linux) has no USB permission now raises an alert that says how to fix it, and its dot in the device selector turns amber instead of staying green.

## Known limits on Linux

- Emulator GPU counters are Windows-only, so the GPU card shows `N/A` on Linux.
- Only x86_64 is published for now.

Previous release: [v2.8.1](https://github.com/marko69420/android-dev-monitor/releases/tag/v2.8.1)
