# Android Dev Monitor v2.8.0

This release fixes the Android side of File Explorer, makes shutdown and error handling reliable, and cleans up several screens.

## Highlights

- **Android File Explorer works again.** Folders with one-word names such as `Download` and `DCIM` were missing, and names with spaces were cut short. `/sdcard` now lists its contents, and device files show their modified date in local time.
- **Paths with spaces are safe.** Delete, rename and new folder on the device act on exactly the selected path. Previously, deleting `/sdcard/My Games` could remove `/sdcard/My` and `/sdcard/Games` instead.
- **No lost recordings on exit.** Closing the window waits until an active screen recording is pulled and the session is saved.
- **Errors are logged.** Unexpected errors are written to `%LOCALAPPDATA%\AndroidDevMonitor\Logs` and shown in a message instead of closing the app silently.
- **Accurate network totals.** Loopback traffic such as ADB port forwarding no longer counts as device RX/TX.

## Interface

- File Explorer shows local-time dates (`yyyy-MM-dd HH:mm`), keeps the end of long paths visible, and right-aligns sizes.
- Performance sessions show durations as `hh:mm:ss` in wider, aligned columns.
- ADB Shell toolbar buttons are no longer clipped.
- Media actions stay disabled until a screenshot or recording is selected.

## For contributors

- `MainViewModel` is readable source again instead of decompiler output.
- Any .NET 10 SDK can build the project, and the repository no longer stores the executable.
- The `Release` GitHub Actions workflow builds, tests and publishes releases.

## Verification

- The release build completes with 0 warnings and 0 errors.
- All 78 automated tests pass.
- Tested on Windows against an Android 16 emulator.

Previous release: [v2.6.1](https://github.com/marko69420/android-dev-monitor/releases/tag/v2.6.1)
