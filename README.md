# Android Dev Monitor

**A Windows Task Manager for Android developers.** Monitor Android apps and devices through ADB, correlate performance spikes with actions and logs, and export sessions for debugging beta builds.

![Android Dev Monitor overview](docs/images/android-dev-monitor-overview.png)

> [!NOTE]
> Android Dev Monitor is an early preview. Core monitoring, sessions, exports, media capture, logs, file transfer, shell, and automation are implemented, but metric availability depends on the Android version, device vendor, and app permissions.

## Why it exists

Android Dev Monitor helps developers and testers spot expensive user flows without constantly switching between separate ADB tools. Track a package while reproducing a slow screen or action, mark the event, then compare CPU, memory, logical disk I/O, network, frame statistics, thermal state, and logs in the same session.

It is designed for:

- Developers testing beta builds across emulators and physical devices.
- QA teams capturing reproducible performance sessions alongside logs and screenshots.
- Indie developers who want a lightweight Windows dashboard around common ADB workflows.
- Technical users reporting evidence-backed performance bugs to app teams.

## Features

- Compact dark WPF shell matching the supplied design, with exactly one device selector.
- Live ADB discovery and device metadata, tolerant process parsing, normalized aggregate CPU, memory, logical process I/O, network totals, FPS/frame stats, battery and thermal probes.
- Fixed 1-second lightweight, 2-second process, 5-second heavy/discovery sampling; 60-second sparklines and 10-minute live views.
- Live/pause sessions, elapsed/active time, markers, editable sustained alert rules, SQLite storage, and JSON/CSV/ZIP export.
- Android screenshot and screen recording capture associated with device, package, and session.
- Instances, Media, Performance, Logs, File Explorer, Network, ADB Shell, Automation, Alerts, and Settings workspaces.
- Deterministic `--demo` mode with a permanent `DEMO DATA` badge and no ADB execution.
- No backend, cloud telemetry, root, emulator reset, or automatic device/app restart.

## Prerequisites

- Windows 10/11 x64.
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (current LTS).
- Optional for live mode: Android SDK Platform Tools. The app resolves a configured path, `ANDROID_SDK_ROOT`, `ANDROID_HOME`, then `PATH`.

The app still opens without ADB. Unauthorized devices require accepting the Android USB-debugging prompt. Some `/proc`, socket, UID network, thermal, FPS, and GPU values vary by Android version/vendor and may correctly show `N/A`.

## Build and run

```powershell
dotnet restore AndroidDevMonitor.slnx
dotnet build AndroidDevMonitor.slnx -c Debug
dotnet run --project src/AndroidDevMonitor.App/AndroidDevMonitor.App.csproj
dotnet run --project src/AndroidDevMonitor.App/AndroidDevMonitor.App.csproj -- --demo
dotnet test AndroidDevMonitor.slnx -c Debug
dotnet publish src/AndroidDevMonitor.App/AndroidDevMonitor.App.csproj -c Release -r win-x64 --self-contained true -o artifacts/win-x64
```

After publishing:

```powershell
# Normal mode
.\artifacts\win-x64\AndroidDevMonitor.exe

# Demo mode
.\artifacts\win-x64\AndroidDevMonitor.exe --demo
```

Or run `powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1`.

## Data and troubleshooting

Local data is under `%LOCALAPPDATA%\AndroidDevMonitor`: SQLite database, diagnostics, media, demo media, sessions, and exports. If ADB is missing, install Platform Tools or set `ANDROID_SDK_ROOT`/`ANDROID_HOME`. If a device is `unauthorized`, accept its debugging prompt. `offline` usually requires reconnecting the device; Refresh performs discovery/collection refresh only and never restarts ADB or a target.

## Project structure

- `src/AndroidDevMonitor.App`: WPF/MVVM shell and dialogs.
- `src/AndroidDevMonitor.Core`: models, contracts, constants, buffers and formatting.
- `src/AndroidDevMonitor.Adb`: argument-safe execution, discovery and tolerant parsers.
- `src/AndroidDevMonitor.Collectors`: live/demo sources and counter calculations.
- `src/AndroidDevMonitor.Infrastructure`: SQLite, exports and media.
- `tests`: parser, calculation, serialization and bounded-buffer tests.
- `docs`: architecture, metrics, UI behavior, limitations and implementation plan.

## Current limitations

GPU usage is intentionally `N/A` until a real target-specific provider is detected; the app never substitutes CPU data or guesses a value. UID network, process I/O, package storage, filesystem, and socket ownership depend on Android/vendor permissions. `screenrecord` behavior and available frame statistics vary by build. Recordings open in the Windows player; the Media page does not decode video thumbnails in-process.
