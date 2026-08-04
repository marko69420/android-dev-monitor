using AndroidDevMonitor.Core.Models;

namespace AndroidDevMonitor.Core.Services;

public interface IAdbExecutor
{
    string? ResolvedAdbPath { get; }
    void ConfigurePath(string? configuredPath);
    Task<AdbCommandResult> ExecuteAsync(string? serial, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
}

public interface IDeviceDiscoveryService
{
    Task<IReadOnlyList<AndroidDevice>> DiscoverAsync(CancellationToken cancellationToken);
    Task<AndroidDevice> EnrichAsync(AndroidDevice device, CancellationToken cancellationToken);
}

public interface IMonitoringSource
{
    IAsyncEnumerable<MetricSample> StreamMetricsAsync(AndroidDevice device, string? packageName, Guid sessionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<AndroidProcess>> GetProcessesAsync(AndroidDevice device, CancellationToken cancellationToken);
    Task<CapabilitySet> DetectCapabilitiesAsync(AndroidDevice device, CancellationToken cancellationToken);
}

public interface ISessionStore
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task SaveSessionAsync(MonitoringSession session, CancellationToken cancellationToken);
    Task AppendSamplesAsync(IReadOnlyList<MetricSample> samples, CancellationToken cancellationToken);
    Task SaveMarkerAsync(SessionMarker marker, CancellationToken cancellationToken);
    Task SaveAlertAsync(SessionAlert alert, CancellationToken cancellationToken);
    Task SaveEventAsync(SessionEvent sessionEvent, CancellationToken cancellationToken);
    Task<IReadOnlyList<MonitoringSession>> ListSessionsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<SessionSummary>> ListSessionSummariesAsync(CancellationToken cancellationToken);
    Task<MonitoringSession?> LoadSessionAsync(Guid sessionId, CancellationToken cancellationToken);
    Task MarkSessionExportedAsync(Guid sessionId, string path, CancellationToken cancellationToken);
    Task DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken);
    Task SaveSettingAsync<T>(string key, T value, CancellationToken cancellationToken);
    Task<T?> LoadSettingAsync<T>(string key, CancellationToken cancellationToken);
}

public interface IMediaService
{
    string MediaDirectory { get; }
    Task<MediaItem> CaptureScreenshotAsync(AndroidDevice device, string? packageName, Guid? sessionId, CancellationToken cancellationToken);
    Task StartRecordingAsync(AndroidDevice device, CancellationToken cancellationToken);
    Task<MediaItem> StopRecordingAsync(AndroidDevice device, string? packageName, Guid? sessionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<MediaItem>> ScanAsync(CancellationToken cancellationToken);
    Task<MediaItem> RenameAsync(MediaItem item, string newFileName, CancellationToken cancellationToken);
    Task<MediaItem> SaveMetadataAsync(MediaItem item, CancellationToken cancellationToken);
    Task DeleteAsync(MediaItem item, CancellationToken cancellationToken);
}

public interface ISessionExporter
{
    Task<string> ExportJsonAsync(MonitoringSession session, string directory, CancellationToken cancellationToken);
    Task<string> ExportCsvAsync(MonitoringSession session, string directory, CancellationToken cancellationToken);
    Task<string> ExportZipAsync(MonitoringSession session, string directory, CancellationToken cancellationToken);
}

public interface IGpuMetricProvider
{
    string Name { get; }
    Task<bool> IsSupportedAsync(AndroidDevice device, CancellationToken cancellationToken);
    Task<MetricValue> ReadAsync(AndroidDevice device, string? packageName, CancellationToken cancellationToken);
}
