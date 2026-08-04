# UI behavior

The shell keeps the expanded navigation, one global Android device dropdown, connection state, tracked-package selector, Live/Pause, fixed `Live · 1s` status, session timer, marker/export, and safe utility actions visible across pages. No second selector or instance-tab row exists.

Selecting another device cancels old collectors/log readers, ends the previous session, displays Connecting, loads metadata/capabilities, and creates a new serial-bound context. Existing sessions remain under Performance. Changing package clears package-transient card histories, preserves device identity/session, and records a package-change event.

Refresh is page-sensitive and safe: Overview refreshes process/current data, Instances discovers devices, Media rescans files, and Shell refreshes displayed context only. It never restarts ADB, Android, an emulator, a package, recording, or automation.

Live/Pause controls sample insertion only. It does not pause Android, ADB, or independent logs and does not backfill gaps. Session wall time continues; active collection time is stored separately. Paused collection is not treated as stale. Summary sparklines retain the last 60 seconds and live detailed pages the last 10 minutes. Stored Performance sessions show their complete duration.

Record exclusively controls Android `screenrecord`; Screenshot exclusively runs Android `screencap`. Results carry serial/package/session metadata and enter Media. Destructive automation actions show the exact serial/package and confirmation. An automation run captures its target serial; a later UI selection never redirects it.

Overview provides Processes, Performance, Network, Storage, Thermal and Logs quick tabs. Process rows use friendly app/system names; multi-process packages and Android service families expand into their real tasks. `Kernel / unattributed CPU` is pinned first and other active CPU consumers precede idle rows, so the visible CPU rows reconcile with the 0–100% device card. Sidebar pages are deeper stored/diagnostic workspaces. Unsupported data visibly displays `N/A`. Demo mode uses the same pages/view models with a persistent `DEMO DATA` indicator.
