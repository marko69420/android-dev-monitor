using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace AndroidDevMonitor.Core.Models;

public enum DeviceState { Connected, Offline, Unauthorized, Connecting, Disconnected, NoPermissions }
public enum DeviceKind { Emulator, Physical, Unknown }
public enum Availability { Available, Waiting, Unsupported, PermissionDenied, Stale, Error }
public enum ProcessGroup { Apps, Background, System }
public enum MediaKind { Screenshot, Recording }
public enum AutomationStepKind { InstallApk, Launch, Restart, ForceStop, ClearData, Wait, Tap, SwipeUp, SwipeDown, SwipeLeft, SwipeRight, EnterText, Back, Home, Screenshot, StartRecording, StopRecording, Marker, WaitForLog, WaitForForeground, CheckProcess }

public sealed record AndroidDevice(
    string Serial,
    string FriendlyName,
    DeviceState State,
    DeviceKind Kind,
    string AndroidVersion = "Unknown",
    int? ApiLevel = null,
    string Manufacturer = "Unknown",
    string Model = "Unknown",
    string Abi = "Unknown",
    string Resolution = "N/A",
    string Density = "N/A",
    int? CpuCores = null,
    long? TotalMemoryBytes = null,
    long? TotalStorageBytes = null,
    DateTimeOffset? LastSeenUtc = null)
{
    public string VersionLine => $"Android {AndroidVersion} · API {ApiLevel?.ToString() ?? "N/A"}";
    public bool IsConnected => State == DeviceState.Connected;
}

public sealed record MetricValue(double? Value, string Unit, Availability Availability, string Source, DateTimeOffset TimestampUtc)
{
    public static MetricValue Missing(string unit, Availability state, string source = "Unavailable") => new(null, unit, state, source, DateTimeOffset.UtcNow);
}

public sealed record MetricSample(
    Guid SessionId,
    string DeviceSerial,
    string? PackageName,
    DateTimeOffset TimestampUtc,
    double? DeviceCpuPercent = null,
    double? ProcessCpuPercent = null,
    long? DeviceMemoryUsedBytes = null,
    long? DeviceMemoryAvailableBytes = null,
    long? ProcessRssBytes = null,
    long? ProcessPssBytes = null,
    double? DiskReadBytesPerSecond = null,
    double? DiskWriteBytesPerSecond = null,
    double? NetworkRxBytesPerSecond = null,
    double? NetworkTxBytesPerSecond = null,
    double? AppNetworkRxBytesPerSecond = null,
    double? AppNetworkTxBytesPerSecond = null,
    double? Fps = null,
    double? FrameTimeP95Ms = null,
    double? JankPercent = null,
    double? TemperatureCelsius = null,
    double? GpuPercent = null,
    string Source = "Unknown",
    long? DeviceStorageTotalBytes = null,
    long? DeviceStorageAvailableBytes = null,
    int? BatteryPercent = null,
    bool? IsCharging = null,
    string? ThermalSeverity = null,
    string? ActiveNetworkInterface = null,
    string? IpAddress = null,
    string? NetworkType = null,
    long? PackageCodeBytes = null,
    long? PackageDataBytes = null,
    long? PackageCacheBytes = null,
    string? GpuSource = null,
    Availability? GpuAvailability = null,
    double? DeviceDiskReadBytesPerSecond = null,
    double? DeviceDiskWriteBytesPerSecond = null,
    string? DeviceDiskSource = null);

public sealed record AndroidProcess(
    int Pid,
    int? ParentPid,
    string User,
    string Name,
    string CommandLine,
    string? PackageName,
    string Status,
    ProcessGroup Group,
    long? StartTicks = null,
    double? CpuPercent = null,
    long? RssBytes = null,
    double? DiskReadBytesPerSecond = null,
    double? DiskWriteBytesPerSecond = null,
    double? NetworkRxBytesPerSecond = null,
    double? NetworkTxBytesPerSecond = null,
    double? Fps = null,
    double? GpuPercent = null,
    string? BatteryImpact = null,
    string? ThermalRelation = null);

public sealed record ProcessSnapshot(DateTimeOffset TimestampUtc, string DeviceSerial, IReadOnlyList<AndroidProcess> Processes);

public sealed record SessionMarker(Guid Id, Guid SessionId, DateTimeOffset TimestampUtc, DateTimeOffset LocalTimestamp, TimeSpan Elapsed, string Name, string? Note, string DeviceSerial, string FriendlyDeviceName, string? PackageName);
public sealed record SessionAlert(Guid Id, Guid SessionId, DateTimeOffset TimestampUtc, string Type, string Severity, string Message, bool Resolved);
public sealed record SessionEvent(Guid Id, Guid SessionId, DateTimeOffset TimestampUtc, string Type, string Message, string DeviceSerial, string? PackageName);

public sealed class MonitoringSession
{
    [JsonIgnore] public object SyncRoot { get; } = new();
    public Guid Id { get; init; } = Guid.NewGuid();
    public required DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset? EndedUtc { get; set; }
    public required AndroidDevice Device { get; init; }
    public string? CurrentPackage { get; set; }
    public bool IsPaused { get; set; }
    public TimeSpan ActiveCollectionTime { get; set; }
    public Collection<MetricSample> Samples { get; } = [];
    public Collection<SessionMarker> Markers { get; } = [];
    public Collection<SessionAlert> Alerts { get; } = [];
    public Collection<SessionEvent> Events { get; } = [];
}

public sealed record SessionSummary(
    Guid Id,
    DateTimeOffset StartedUtc,
    DateTimeOffset? EndedUtc,
    AndroidDevice Device,
    string? CurrentPackage,
    TimeSpan ActiveCollectionTime,
    int AlertCount,
    int MarkerCount,
    double? AverageFps,
    double? FrameTimeP95Ms,
    long? PeakMemoryBytes,
    double? PeakCpuPercent,
    string ExportStatus)
{
    [JsonIgnore] public TimeSpan Duration => (EndedUtc ?? DateTimeOffset.UtcNow) - StartedUtc;
}

public sealed record MediaItem(
    Guid Id,
    MediaKind Kind,
    string FileName,
    string LocalPath,
    string DeviceSerial,
    string FriendlyDeviceName,
    string? PackageName,
    DateTimeOffset CapturedUtc,
    TimeSpan? Duration,
    long FileSize,
    Guid? SessionId,
    string? Note,
    string? Resolution = null,
    string? ThumbnailPath = null,
    Guid? MarkerId = null);
public sealed record LogEntry(DateTimeOffset TimestampUtc, string Source, string Priority, string Message, string DeviceSerial, int? Pid = null, string? PackageName = null)
{
    [JsonIgnore] public DateTimeOffset LocalTimestamp => TimestampUtc.ToLocalTime();
}
public sealed record FileEntry(string Name, string FullPath, bool IsDirectory, long? Size, DateTimeOffset? ModifiedUtc);
public sealed record AutomationStep(Guid Id, AutomationStepKind Kind, string Name, string? Argument, TimeSpan? Duration, bool IsDestructive);
public sealed record AutomationSequence(Guid Id, string Name, IReadOnlyList<AutomationStep> Steps, int RepeatCount, bool StopOnFailure);
public sealed class AlertRule : INotifyPropertyChanged
{
    private bool _enabled;
    private double? _threshold;
    private TimeSpan _duration;
    private string _severity;

    public AlertRule(
        string type,
        string metric,
        bool enabled,
        double? threshold,
        TimeSpan duration,
        string severity,
        string unit,
        string description,
        bool systemCritical = false)
    {
        Type = type;
        Metric = metric;
        _enabled = enabled;
        _threshold = threshold;
        _duration = duration;
        _severity = severity;
        Unit = unit;
        Description = description;
        SystemCritical = systemCritical;
    }

    public string Type { get; }
    public string Metric { get; }
    public bool Enabled
    {
        get => _enabled;
        set => SetField(ref _enabled, value);
    }
    public double? Threshold
    {
        get => _threshold;
        set => SetField(ref _threshold, value);
    }
    public TimeSpan Duration
    {
        get => _duration;
        set
        {
            if (SetField(ref _duration, value))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DurationSeconds)));
        }
    }
    public double DurationSeconds
    {
        get => Duration.TotalSeconds;
        set => Duration = TimeSpan.FromSeconds(Math.Clamp(value, 0, 3600));
    }
    public string Severity
    {
        get => _severity;
        set => SetField(ref _severity, value);
    }
    public string Unit { get; }
    public string Description { get; }
    public bool SystemCritical { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public sealed record AppSettings
{
    public string? AdbPath { get; init; }
    public string? LastDeviceSerial { get; init; }
    public string? LastPage { get; init; }
    public bool RestoreLastDevice { get; init; } = true;
    public bool ConfirmRecordingClose { get; init; } = true;
    public bool ConfirmAutomationClose { get; init; } = true;
    public bool CompactDensity { get; init; } = true;
}

public sealed record CapabilitySet(
    bool ProcessCpu,
    bool ProcessMemory,
    bool ProcessIo,
    bool DeviceNetwork,
    bool AppNetwork,
    bool Fps,
    bool Gpu,
    bool Thermal,
    bool Battery,
    IReadOnlyDictionary<string, string> Sources);

public sealed record AdbCommandResult(
    IReadOnlyList<string> Command,
    string? DeviceSerial,
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    bool Cancelled)
{
    [JsonIgnore] public TimeSpan Duration => FinishedUtc - StartedUtc;
    [JsonIgnore] public bool Success => ExitCode == 0 && !TimedOut && !Cancelled;
}
