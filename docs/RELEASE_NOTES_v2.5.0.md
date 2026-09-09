# Android Dev Monitor v2.5.0

## New developer workflows

- Wireless Device Center with mDNS discovery, secure pairing, connect/disconnect, latency checks, and Platform Tools 37 / ADB Wi-Fi 2.0 capability detection.
- Cancellable Developer Lab with locally saved Perfetto, CPU, frame/jank, GPU, startup, memory, package, permission, background-work, storage, network, logcat, crash/ANR, bugreport, instrumentation, Monkey, emulator, security, and compatibility reports.
- Bug Report Analyzer for imported or newly captured ZIP/TXT reports.
- APK archive analysis and two-build size comparison by DEX, resources, native libraries, assets, manifest, and signing metadata.
- App & Device Lab with permission audit/rollback, instrumentation runner, deep links, port mappings, device keys, scrcpy launch, and timed foreground-to-background tests.
- Emulator Control Center with local AVD discovery, start, cold boot, stop, confirmed wipe-data, battery/power/network simulation, rotation, and snapshot listing.
- Multi-device startup, ADB latency, memory, and security-patch matrix.
- A/B comparison for saved performance sessions.
- Live log collection from main, system, crash, events, and radio buffers.

## Requirements and access

- The Windows package is self-contained and does not require the .NET SDK.
- Android Platform Tools are required for live-device features.
- scrcpy mirroring requires `scrcpy.exe` installed or available on `PATH`.
- Some counters and commands depend on Android version, vendor policy, debuggable/profileable state, or device permissions. The interface reports unavailable data instead of fabricating it.
