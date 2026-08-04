# Android Dev Monitor implementation plan

## Scope and acceptance strategy

Build a native .NET 10 WPF application that remains useful with no ADB installed, exposes one global selected-device context, uses real ADB data in live mode, and uses deterministic data only behind a visible `DEMO DATA` state. The first build favors reliable capability detection and explicit `N/A` states over inferred metrics.

## Phases

1. Foundation: solution and projects, dependency injection, structured logging, SQLite initialization, settings, domain records, centralized intervals, bounded time-series buffers, demo-mode switch, and application lifetime cancellation.
2. Reference shell: compact dark theme, sidebar, exactly one device selector, tracked-package selector, global toolbar, summary cards/sparklines, Overview tabs, process grid, persistent status bar, responsive sizing, empty/error/loading states.
3. ADB foundation: executable discovery, argument-safe process execution, timeouts, per-device concurrency limits, `devices -l` parsing, metadata/capability detection, device switching, and error mapping.
4. Monitoring: process discovery, timed CPU deltas, memory, process I/O, device and UID network counters, package storage, FPS/frame statistics, battery, thermal, GPU capability abstraction, stale-data detection, and logcat batching.
5. Sessions: active/pause state, package history, markers, alert events, bounded queues, batched persistence, fixed 60-second and 10-minute chart windows, full stored-session views, JSON/CSV/ZIP export.
6. Tools: Instances, Media, Performance, Logs, File Explorer, Network, ADB Shell, Automation, Alerts, and Settings. Destructive actions remain confirmed and bound to their captured serial/package.
7. Hardening: parser/domain tests using sanitized fixtures, cancellation and PID-reuse tests, docs, format/build/test/publish, and a self-contained `win-x64` release script.

## Technical decisions

- WPF MVVM with CommunityToolkit.Mvvm and Microsoft.Extensions.DependencyInjection.
- `Microsoft.Data.Sqlite` for settings, sessions, samples, markers, alerts, events, media metadata, and automation sequences. Sample inserts are written in bounded batches.
- Custom lightweight WPF polyline rendering for sparklines/charts to avoid a large chart dependency and keep rendering bounded.
- `ProcessStartInfo.ArgumentList` for every local ADB invocation. A command result records timestamps, duration, serial, exit state, output, timeout, and cancellation.
- `MainViewModel` owns the active-device cancellation token. Device-list reconciliation suppresses transient ComboBox null selections, and only a real serial/state change cancels collectors and closes the old session.
- Immutable UI snapshots are posted at a throttled rate. Collection intervals and chart windows live only in `MonitoringConstants`.
- Live process CPU is normalized against aggregate `/proc/stat` deltas, so one saturated core on a two-core target is approximately 50% of device capacity.
- GPU, per-UID network, process I/O, FPS, and some thermal values are capability-dependent. Unsupported, denied, stale, or missing values remain distinct states.

## Capability-dependent metrics

| Metric | Preferred source | Fallback/unsupported behavior |
|---|---|---|
| Device/process CPU | `/proc/stat`, `/proc/<pid>/stat` timed deltas | `top` is diagnostic only; inaccessible process is `N/A` |
| Device memory | `/proc/meminfo` | `N/A` on parse/access failure |
| Process RSS/PSS | `/proc/<pid>/status`; `dumpsys meminfo <package>` | RSS remains available when PSS is denied |
| Process disk I/O | `/proc/<pid>/io` | `N/A`; never labeled physical SSD writes |
| Device/app network | `/proc/net/dev`; supported UID counters after package UID lookup | App values `N/A`; never inherit device totals |
| FPS/frame time | `dumpsys gfxinfo <package> framestats` | `N/A`; no synthetic 60 FPS in live mode |
| GPU | runtime-selected `IGpuMetricProvider` | `N/A` with provider/source explanation |
| Battery/thermal | `dumpsys battery`, `dumpsys thermalservice` | `N/A`; emulator synthetic values labeled when detectable |
| Package storage | `dumpsys package`, `dumpsys diskstats`/`df`, supported package stats | Show only measurable categories |

## Primary risks and mitigations

- Android/vendor output drift: tolerant line-oriented parsers, capability probes, source labels, and realistic parser fixtures.
- ADB command storms: keyed per-device semaphores, fixed schedules, heavy-command exclusion, timeouts, and cancellation.
- UI stalls under process/log volume: bounded channels, batched dispatch, virtualized controls, bounded chart/log histories.
- Device mixing: every sample/session/media/command carries a serial; coordinator generation checks reject stale callbacks.
- PID reuse: pair serial/PID with process start ticks when exposed and reset baselines when identity changes.
- Partial permissions/root differences: never require root; surface denied versus empty distinctly.
- Recording cleanup after disconnect: retain remote path/recovery metadata, pull only when connected, and never start a duplicate recording.
- Local SDK availability: use current .NET 10 LTS; validation requires a .NET 10 SDK even though the app can run self-contained after publish.
