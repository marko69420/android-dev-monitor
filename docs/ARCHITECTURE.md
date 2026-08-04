# Architecture

`AndroidDevMonitor.App` is the composition root and WPF/MVVM layer. `MainViewModel` holds the one global selected-device context; code-behind only assigns the injected view model. The Core project owns stable records/interfaces and has no WPF or ADB dependencies. ADB owns executable resolution, structured command results, per-serial concurrency gates, discovery, metadata and tolerant parsers. Collectors use those interfaces to produce the same immutable `MetricSample` and `AndroidProcess` models as demo mode. Infrastructure owns SQLite, media and exports.

On target change, the view model cancels and disposes the old context token, closes its session, clears transient package/device views, creates a new session, then launches metric/process/timer loops bound to the captured serial and session ID. Each callback rechecks both IDs, preventing late results from contaminating the next target.

Every local ADB process uses `ProcessStartInfo.ArgumentList`, redirected output, no shell window, timeout, cancellation, and a structured result. A keyed semaphore limits concurrent commands per device. Lightweight, process and heavy schedules come from `MonitoringConstants`; background results are marshalled to the WPF dispatcher in bounded snapshots.

Samples are retained in fixed-window buffers for UI and accumulated in the active session. SQLite runs WAL mode and sample inserts are batched in transactions. Sessions, events, markers, alerts, settings, media metadata, and automation sequences are local. Session exports run asynchronously to JSON/CSV and ZIP. Application shutdown cancels collectors and persists the active session before disposing services.

Demo mode substitutes discovery, monitoring, and media services at dependency-injection composition time. It does not fork the UI/domain model and cannot accidentally invoke ADB.
