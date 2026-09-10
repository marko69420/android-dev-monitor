# Android Dev Monitor v2.6.0

The largest update so far: the full roadmap feature set, a refreshed dark interface, and the first shipped HTML user guide.

## Highlights

- **Refreshed dark design:** dark check boxes with an accent check, gradient panel surfaces, taller sidebar rows with a gradient selection highlight, accent focus on text fields, and unified toolbar and status bars.
- **Logcat 2.0:** crash/ANR-only mode, bookmarks, "10 seconds before event", persisted filter presets, and a two-row toolbar that is easier to scan.
- **In-app device mirror:** live ADB screencap stream with click-to-tap input, a visible touch marker, frame saving, Windows-clipboard push into the device, and an optional full-speed scrcpy window (scrcpy also forwards device audio on Android 11+).
- **Emulator Control Center:** GPS fix, fold/unfold, dark-mode toggle, font scale, Play Store image detection, full device profile, sensor status, airplane-mode toggle, and snapshot save/load/list/delete.
- **Multi-device lab:** install, launch, screenshot, and logcat across every connected device, one ZIP report, and one scrcpy window per device.
- **Test Runner:** Monkey stress with seed/throttle and a crash verdict, crash/ANR scan, UI Automator hierarchy dump, automatic screenshot + logcat on failure, and ZIP export of all artifacts.
- **App bundle (AAB) analyzer:** modules, DEX, native ABIs, resources, and `BundleConfig.pb`, each with per-module sizes.
- **Companion SDK:** tiny `AdmCompanion.java` plus a `Companion event report` that reads `ADM_COMPANION` markers, value statistics, and a timeline.
- **Background Inspector:** Doze state, App Standby bucket, background restriction, and battery whitelist added to the background work report.
- **Developer productivity:** APK install/update, uninstall, clear data, force stop, cold restart, on-device text typing, device property diff, screenshot pixel diff, and app-data browsing or pulls on debuggable builds.
- **Dark guide and help center:** `docs/USER_GUIDE.html` ships inside the build and opens from the in-app Help page.

## Platform notes

- The Windows package is self-contained and does not require the .NET SDK. Building from source requires the .NET 10 SDK.
- Android Platform Tools are required for live-device features; ADB Wi-Fi 2.0 detection depends on the Platform Tools version.
- Some counters and commands depend on the Android version, vendor policy, debuggable/profileable state, or device permissions. The interface reports unavailable data instead of fabricating it.

## Verification

- Debug and Release builds complete with 0 warnings and 0 errors.
- The test suite covers 69 tests (23 Core, 40 ADB, 6 Collectors).
- The demo executable starts, renders every workspace, and shuts down cleanly.

Previous releases: [v2.5.0](https://github.com/marko69420/android-dev-monitor/releases/tag/v2.5.0) · [v2.0.0](https://github.com/marko69420/android-dev-monitor/releases/tag/v2.0.0)
