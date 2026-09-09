# Android Dev Monitor

**A Windows Task Manager for Android developers.** Monitor Android apps and devices through ADB, correlate performance spikes with actions and logs, and export sessions for debugging beta builds.

**Version 2.0.0** · Windows desktop app · Physical Android devices and local emulators

![Android Dev Monitor v2.0.0 monitoring YouTube and opening live logs](docs/images/android-dev-monitor-demo.gif)

## [Download for Windows](https://github.com/marko69420/android-dev-monitor/releases/download/v2.0.0/AndroidDevMonitor-v2.0.0-win-x64.zip)

[![Download Android Dev Monitor v2.0.0 for Windows](https://img.shields.io/badge/Download_for_Windows-v2.0.0-19c4dc?style=for-the-badge&logo=windows11&logoColor=white)](https://github.com/marko69420/android-dev-monitor/releases/download/v2.0.0/AndroidDevMonitor-v2.0.0-win-x64.zip)

No installer and no .NET SDK required: download the ZIP, extract it, and run `AndroidDevMonitor.exe`.

1. Enable **Developer options → USB debugging** on the Android device, or start a local emulator.
2. Connect the device and accept its USB-debugging prompt.
3. Open Android Dev Monitor and select the detected device.

![Android Dev Monitor v2.0.0 dashboard and process table](docs/images/android-dev-monitor-overview.png)

> [!NOTE]
> Android Dev Monitor is an early preview. Core monitoring, sessions, exports, media capture, logs, file transfer, shell, and automation are implemented, but metric availability depends on the Android version, device vendor, and app permissions.

## Why it exists

Android Dev Monitor helps developers and testers spot expensive user flows without constantly switching between separate ADB tools. Track a package while reproducing a slow screen or action, mark the event, then compare CPU, memory, logical disk I/O, network, frame statistics, thermal state, and logs in the same session.

It is designed for:

- Developers testing beta builds across emulators and physical devices.
- QA teams capturing reproducible performance sessions alongside logs and screenshots.
- Indie developers who want a lightweight Windows dashboard around common ADB workflows.
- Technical users reporting evidence-backed performance bugs to app teams.

## What's new in v2.0.0

- **A/B app monitoring:** follow the application on screen, inspect a selected application's CPU and RAM, and keep total device RAM in view.
- **Readable application selectors:** choose names such as YouTube, Photos, Settings, or your own app from the installed launcher applications. The original package remains available in the tooltip.
- **Emulator GPU monitoring:** read Windows GPU Engine counters for the local Android emulator, with the source identified on the card.
- **Split disk charts:** separate write and read histories, a combined **W/R** value, and explicit B/s, KB/s, or MB/s units. Device-wide `storaged` readings provide a fallback when process I/O is inaccessible.
- **A clearer process table:** System processes first, friendly task names, original packages and commands, aligned metrics, search, and expandable groups. Selecting a group highlights its visible child processes.
- **Start/Stop for the process table:** freeze the current values and row order to inspect a busy list, then resume with the latest measurements.
- **A refreshed dark interface:** maximized startup, compact tabs, readable tooltips, and matching dark scrollbars.

## Track an application

Connect a device through ADB and select it in the top bar. The CPU card follows the foreground app automatically. Use the small application selector to choose an app to inspect alongside it; CPU and Memory share this selection.

| Card | A | B |
| --- | --- | --- |
| CPU usage | CPU usage of the application currently on screen | CPU usage of the selected application |
| Memory | Total device RAM used / capacity | RAM used by the selected application's processes (RSS) |

The selected app can be on screen or in the background. Choosing it for monitoring does not launch it. The selectors list apps with launcher activities, including system apps such as Settings.

To open an app on the Android device, use the top **Launch application** dropdown. Its **i** tooltip explains the action; selecting a package launches that app.

## Inspect a busy process list

Use **Stop** beside the search box to freeze the process table at that moment. Values and row order stay still while you search or expand groups. Dashboard monitoring continues. **Start** immediately returns the table to the latest process snapshot.

The **Name** column uses descriptions such as *Disk journal* and *App installer*. The **Package / command** column retains the technical identifiers. Hover over shortened text to read the full value.

## More tools

- Live ADB discovery, device details, normalized CPU measurements, memory, network totals, frame statistics, battery and thermal probes where available.
- 1-second lightweight, 2-second process, and 5-second heavy/discovery sampling targets; actual refresh time depends on device and ADB response times.
- Live/pause sessions, elapsed/active time, markers, editable sustained alert rules, SQLite storage, and JSON/CSV/ZIP export.
- Android screenshot and screen recording capture associated with device, package, and session.
- Instances, Media, Performance, Logs, File Explorer, Network, ADB Shell, Automation, Alerts, and Settings workspaces.
- Deterministic `--demo` mode with a permanent `DEMO DATA` badge and no ADB execution.
- Local operation without a backend or cloud telemetry. Standard monitoring does not require root; metric access depends on the device.

## Prerequisites

- Windows 10/11 x64.
- Optional for live mode: Android SDK Platform Tools. The app resolves a configured path, `ANDROID_SDK_ROOT`, `ANDROID_HOME`, then `PATH`.

The downloadable Windows build is self-contained and does not require .NET. Building from source requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) matching `global.json`.

The app still opens without ADB; use `--demo` to explore it with sample data. Unauthorized devices require accepting the Android USB-debugging prompt. Unavailable measurements appear as `—` in the process table or `N/A` in other views.

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
- `src/AndroidDevMonitor.Infrastructure`: SQLite, exports, media, and Windows emulator GPU counters.
- `tests`: parser, calculation, serialization and bounded-buffer tests.
- `docs`: architecture, metrics, UI behavior, limitations and implementation plan.

## Current limitations

- **GPU scope:** the Windows provider measures the busiest GPU engine of the matching local emulator process. This includes all apps inside that emulator; it is not a per-Android-app GPU percentage. Physical-device GPU monitoring still requires a supported provider.
- **Disk scope:** the card title identifies **App disk I/O** or **Device disk I/O**. The device fallback covers all Android UIDs and may use a longer sampling interval. While a sample is unavailable, the card can show `0/0` and a zero chart baseline; that placeholder does not prove there was no disk activity.
- **App memory:** B shows the sum of process RSS for the selected package. Shared memory can overlap, so adding app RSS values is not equivalent to total device RAM usage.
- **Device permissions:** UID network, process I/O, package storage, filesystem, socket ownership, and frame statistics depend on Android version, vendor, and permissions.
- **Recording:** `screenrecord` support varies by build. Recordings open in the Windows player; the Media page does not decode video thumbnails in-process.

See [metric definitions](docs/METRICS.md), [ADB limitations](docs/ADB_LIMITATIONS.md), and [product notes](docs/PRODUCT_NOTES.md) for more detail.

## License

Android Dev Monitor is available under the [MIT License](LICENSE).
