# Metrics

| Metric | Meaning/unit | Scope | Source and interval | Calculation/limitations |
|---|---|---|---|---|
| CPU | Percent of all target CPU capacity | Device/process | `/proc/stat`, `/proc/<pid>/stat`, 1s | Timed tick deltas. A saturated core on two cores is ~50%. PID/start-tick changes reset baseline. |
| Memory | Bytes and percent | Device/process | `/proc/meminfo`, `/proc/<pid>/status`, 1s; `dumpsys meminfo`, 5s | Device used = total - available. Process RSS is lightweight; PSS is detailed and package-only. |
| Disk Read/Write | B/s | Process; device when exposed | `/proc/<pid>/io`, 1s | Counter delta/time. Logical I/O only, never physical SSD measurement. Permission/reset yields `N/A`. |
| Network RX/TX | B/s and session bytes | Device/package UID | `/proc/net/dev`, 1s; UID provider when supported | Device totals never masquerade as app traffic. Counters remain separated by direction. |
| FPS/frame time | FPS, ms, percent jank | Tracked package | `dumpsys gfxinfo <package> framestats`, 5s | Frame durations provide average/median/P90/P95/P99 and >16.67/33.3/50 ms classes. Invalid data is `N/A`, never fake 60 FPS. |
| GPU | Percent | Device/package where real | `IGpuMetricProvider`, 5s | Runtime capability only. Default provider is unsupported; CPU is never reused as GPU. |
| Battery | Percent/charging/temp °C | Device | `dumpsys battery`, 5s | Vendor/emulator values may be synthetic and should be labeled when detectable. |
| Thermal | Severity/sensor °C | Device | `dumpsys thermalservice`, 5s | Vendor output differs. Unknown sensors/denied services are explicit. |
| Storage capacity/app size | Bytes/percent | Device/package | `df` and supported dumpsys providers, 5s | Capacity, package data/cache/code, and I/O remain distinct. Unmeasurable categories are omitted. |

All samples record UTC time, device serial, session ID, package, and provider source. `Waiting`, `Unsupported`, `PermissionDenied`, `Stale`, and `Error` are separate availability states.
