# ADB limitations

Physical devices and emulators expose different kernel files, services and synthetic sensors. An emulator may expose more host-integrated counters but synthetic battery/thermal values; a physical device may hide kernel and UID data. Android/vendor releases change `ps`, `dumpsys`, thermal and frame-stat formats, so parsers detect columns/formats and fail to explicit unsupported states.

The application never requires or attempts root. On non-rooted production builds, other apps' `/proc/<pid>` I/O/status, protected `/data`, socket-to-UID mapping, tombstones, ANR traces and package storage details may be denied. File Explorer distinguishes permission denial from an empty folder.

CPU and device memory are broadly available. PSS is heavier and package-scoped. Logical process I/O is not physical flash wear. Device network totals are broadly available, while selected-app traffic requires supported UID counters. Network state, gateway, and DNS use `ip` first and `dumpsys connectivity` fallbacks because some Android builds deny link/default-route queries. FPS needs valid `gfxinfo` data for the tracked rendered package. GPU percentages are highly vendor-dependent and remain `N/A` without a genuine provider. Battery and thermal services may be missing or synthetic.

ADB states are distinct: offline, unauthorized, no permissions, disconnected and unavailable. Commands can time out or targets can disappear at any point; structured results retain serial, arguments, times, exit state and error text. Missing ADB never prevents application or demo-mode startup.
