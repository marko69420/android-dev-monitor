using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using AndroidDevMonitor.Adb.Parsers;
using AndroidDevMonitor.App.Services;
using AndroidDevMonitor.Core.Collections;
using AndroidDevMonitor.Core.Configuration;
using AndroidDevMonitor.Core.Models;
using AndroidDevMonitor.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AndroidDevMonitor.App.ViewModels;

public partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly Regex LogcatLineRegex = new Regex(@"^(?<month>\d{2})-(?<day>\d{2})\s+(?<time>\d{2}:\d{2}:\d{2}\.\d{3,6})\s+(?<pid>\d+)\s+(?<tid>\d+)\s+(?<priority>[VDIWEAF])\s+(?<tag>[^:]+):\s*(?<message>.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly IDeviceDiscoveryService _devicesService;
    private readonly IMonitoringSource _monitoring;
    private readonly ISessionStore _sessions;
    private readonly IMediaService _media;
    private readonly ISessionExporter _exporter;
    private readonly IAdbExecutor _adb;
    private readonly IDialogService _dialogs;

    private readonly TimeSeriesBuffer<MetricSample> _liveSamples = new TimeSeriesBuffer<MetricSample>(MonitoringConstants.LiveChartWindow, x => x.TimestampUtc);

    private CancellationTokenSource? _contextCts;
    private bool _disposed;
    private CancellationTokenSource? _automationCts;
    private CancellationTokenSource? _transferCts;
    private CancellationTokenSource? _shellCts;
    private CancellationTokenSource? _storedSessionCts;
    private readonly CancellationTokenSource _appCts = new CancellationTokenSource();
    private MonitoringSession? _session;
    private List<AndroidProcess> _allProcesses = new List<AndroidProcess>();
    private DateTimeOffset _lastSampleUtc;
    private DateTimeOffset _lastNetworkTotalTimestamp;
    private double? _processSnapshotCpuTotal;
    private double _sessionNetworkRxBytes;
    private double _sessionNetworkTxBytes;
    private bool _initialized;
    private bool _suppressDeviceSwitch;
    private readonly HashSet<string> _activeAlertTypes = new HashSet<string>();
    private readonly HashSet<string> _expandedProcessGroups = new HashSet<string>(StringComparer.Ordinal);
    private Regex? _logSearchRegex;
    private string? _packageAutoSelectedForSerial;
    private readonly SemaphoreSlim _discoveryGate = new SemaphoreSlim(1, 1);
    private readonly Stack<string> _remoteBackHistory = new Stack<string>();
    private readonly Stack<string> _remoteForwardHistory = new Stack<string>();
    private readonly Stack<string> _localBackHistory = new Stack<string>();
    private readonly Stack<string> _localForwardHistory = new Stack<string>();
    private int _shellHistoryIndex = -1;

    [ObservableProperty] private AndroidDevice? _selectedDevice;
    [ObservableProperty] private string? _selectedPackage;
    [ObservableProperty] private string _currentPage = "Overview";
    [ObservableProperty] private string _overviewTab = "Processes";
    [ObservableProperty] private bool _isLive = true;
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _connectionText = "Connecting";
    [ObservableProperty] private string _statusMessage = "Starting…";
    [ObservableProperty] private string _sessionTime = "00:00:00";
    [ObservableProperty] private string _dataAge = "Waiting for data";
    [ObservableProperty] private string _networkContext = "Network context: waiting for data";
    [ObservableProperty] private string _networkTotals = "Session RX N/A · TX N/A";
    [ObservableProperty] private string _storageSummary = "Storage capacity: waiting for data";
    [ObservableProperty] private string _appStorageSummary = "Selected-app size: N/A";
    [ObservableProperty] private string _thermalSummary = "Thermal and battery: waiting for data";
    [ObservableProperty] private string _logSearchText = "";
    [ObservableProperty] private string _selectedLogPriority = "All";
    [ObservableProperty] private string _selectedLogSource = "All";
    [ObservableProperty] private bool _logRegexEnabled;
    [ObservableProperty] private bool _isLogPaused;
    [ObservableProperty] private bool _isLogAutoScroll = true;
    [ObservableProperty] private LogEntry? _selectedLogEntry;
    [ObservableProperty] private string _logFilterStatus = "Live logcat";
    [ObservableProperty] private string _mediaSearchText = "";
    [ObservableProperty] private string _selectedMediaKind = "All";
    [ObservableProperty] private string _selectedMediaDevice = "All";
    [ObservableProperty] private string _selectedMediaPackage = "All";
    [ObservableProperty] private string _selectedMediaSort = "Newest";
    [ObservableProperty] private string _mediaLayout = "Gallery";
    [ObservableProperty] private string _mediaFilterStatus = "Local media library";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenSelectedMediaCommand), nameof(CopyMediaPathCommand), nameof(RenameMediaCommand), nameof(SaveMediaMetadataCommand), nameof(DeleteMediaCommand))]
    private MediaItem? _selectedMediaItem;
    [ObservableProperty] private string _mediaRenameText = "";
    [ObservableProperty] private string _mediaNote = "";
    [ObservableProperty] private SessionMarker? _selectedMediaMarker;
    [ObservableProperty] private SessionSummary? _selectedStoredSession;
    [ObservableProperty] private MonitoringSession? _loadedStoredSession;
    [ObservableProperty] private string _storedSessionStatus = "Select a stored session";
    [ObservableProperty] private string _storedSessionDetails = "Full-session metrics, markers, alerts, disconnects and package changes appear here.";
    [ObservableProperty] private string _shellCommand = "getprop ro.product.model";
    [ObservableProperty] private string _shellOutput = "Commands run on the globally selected Android instance.";
    [ObservableProperty] private string _selectedShellPreset = "getprop";
    [ObservableProperty] private bool _isShellRunning;
    [ObservableProperty] private string _shellStatus = "Idle";
    [ObservableProperty] private string _automationLog = "Automation actions are bound to the selected serial when started.";
    [ObservableProperty] private string? _selectedApkPath;
    [ObservableProperty] private string _automationCoordinates = "540,960";
    [ObservableProperty] private string _automationText = "Hello Android";
    [ObservableProperty] private int _swipeDurationMs = 350;
    [ObservableProperty] private string _newStepArgument = "";
    [ObservableProperty] private int _newStepDurationMs = 1000;
    [ObservableProperty] private int _automationRepeatCount = 1;
    [ObservableProperty] private bool _automationStopOnFailure = true;
    [ObservableProperty] private bool _isAutomationRunning;
    [ObservableProperty] private bool _isAutomationPaused;
    [ObservableProperty] private string _automationState = "Idle";
    [ObservableProperty] private string _localPath = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
    [ObservableProperty] private string _remotePath = "/sdcard";
    [ObservableProperty] private string _fileSearchText = "";
    [ObservableProperty] private string _newRemoteFolderName = "NewFolder";
    [ObservableProperty] private string _remoteRenameText = "";
    [ObservableProperty] private string _transferStatus = "No active transfer";
    [ObservableProperty] private bool _isTransferRunning;
    [ObservableProperty] private string _selectedRemoteQuickLocation = "/sdcard";
    [ObservableProperty] private ProcessDisplayRow? _selectedProcessRow;
    [ObservableProperty] private FileEntry? _selectedLocalFile;
    [ObservableProperty] private FileEntry? _selectedRemoteFile;
    [ObservableProperty] private AutomationStepKind _selectedStepKind = AutomationStepKind.Wait;
    [ObservableProperty] private AutomationStep? _selectedSequenceStep;

    public bool IsDemo { get; }

    public string DemoBadge => IsDemo ? "DEMO DATA" : "";

    public IReadOnlyList<string> NavigationItems { get; }

    public ObservableCollection<AndroidDevice> Devices { get; } = new ObservableCollection<AndroidDevice>();

    public ObservableCollection<string> TrackedPackages { get; } = new ObservableCollection<string>();

    public ObservableCollection<AndroidProcess> Processes { get; } = new ObservableCollection<AndroidProcess>();

    public ObservableCollection<ProcessDisplayRow> ProcessRows { get; } = new ObservableCollection<ProcessDisplayRow>();

    public ObservableCollection<MetricSample> LiveSamples { get; } = new ObservableCollection<MetricSample>();

    public ObservableCollection<SessionMarker> Markers { get; } = new ObservableCollection<SessionMarker>();

    public ObservableCollection<SessionAlert> Alerts { get; } = new ObservableCollection<SessionAlert>();

    public ObservableCollection<LogEntry> Logs { get; } = new ObservableCollection<LogEntry>();

    public ICollectionView LogView { get; }

    public IReadOnlyList<string> LogPriorities { get; } = ["All", "Error", "Warning", "Info", "Debug"];

    public IReadOnlyList<string> LogSources { get; } = ["All", "logcat", "Appium (external)", "Android Dev Monitor", "Session"];

    public ObservableCollection<MediaItem> MediaItems { get; } = new ObservableCollection<MediaItem>();

    public ICollectionView MediaView { get; }

    public IReadOnlyList<string> MediaKinds { get; } = ["All", "Screenshots", "Recordings"];

    public IReadOnlyList<string> MediaSortOptions { get; } = ["Newest", "Oldest", "Largest", "Smallest"];

    public IReadOnlyList<string> MediaLayouts { get; } = ["Gallery", "List"];

    public ObservableCollection<string> MediaDeviceFilters { get; } = new ObservableCollection<string> { "All" };

    public ObservableCollection<string> MediaPackageFilters { get; } = new ObservableCollection<string> { "All" };

    public ObservableCollection<SessionSummary> StoredSessions { get; } = new ObservableCollection<SessionSummary>();

    public ObservableCollection<SessionMarker> StoredSessionMarkers { get; } = new ObservableCollection<SessionMarker>();

    public ObservableCollection<SessionAlert> StoredSessionAlerts { get; } = new ObservableCollection<SessionAlert>();

    public ObservableCollection<SessionEvent> StoredSessionEvents { get; } = new ObservableCollection<SessionEvent>();

    public ObservableCollection<string> ShellHistory { get; } = new ObservableCollection<string>();

    public IReadOnlyList<string> ShellPresets { get; } = ["getprop", "dumpsys battery", "dumpsys meminfo", "wm size", "wm density", "df -h", "pm list packages", "ps -A"];

    public ObservableCollection<FileEntry> LocalFiles { get; } = new ObservableCollection<FileEntry>();

    public ObservableCollection<FileEntry> RemoteFiles { get; } = new ObservableCollection<FileEntry>();

    public ICollectionView LocalFileView { get; }

    public ICollectionView RemoteFileView { get; }

    public IReadOnlyList<string> RemoteQuickLocations { get; } = ["/sdcard", "/sdcard/Download", "/sdcard/DCIM", "/sdcard/Pictures", "/sdcard/Movies", "/sdcard/Documents", "/sdcard/Android/data", "/data/local/tmp"];

    public ObservableCollection<AutomationStep> SequenceSteps { get; } = new ObservableCollection<AutomationStep>();

    public IReadOnlyList<AutomationStepKind> AutomationStepKinds { get; } = Enum.GetValues<AutomationStepKind>();

    public MetricCardViewModel CpuCard => CpuTracking.Total;

    public MetricCardViewModel MemoryCard => MemoryTracking.Total;

    public MetricCardViewModel GpuCard { get; } = new MetricCardViewModel("GPU", "%", 0.0, 100.0);

    public DiskMetricCardViewModel DiskCard { get; } = new();

    public LiveChartViewModel CpuChart { get; } = new LiveChartViewModel("CPU", "Device", "Selected app", "%", 0.0, 100.0);

    public LiveChartViewModel DeviceMemoryChart { get; } = new LiveChartViewModel("Device memory", "Used", "Available", "B", 0.0);

    public LiveChartViewModel AppMemoryChart { get; } = new LiveChartViewModel("Selected-app memory", "RSS", "PSS", "B", 0.0);

    public LiveChartViewModel FpsChart { get; } = new LiveChartViewModel("Frame rate", "FPS", "", " FPS", 0.0, 120.0);

    public LiveChartViewModel FrameTimeChart { get; } = new LiveChartViewModel("Frame time", "P95", "", " ms", 0.0);

    public LiveChartViewModel JankChart { get; } = new LiveChartViewModel("Jank", "Jank", "", "%", 0.0, 100.0);

    public LiveChartViewModel DiskChart { get; } = new LiveChartViewModel("Disk I/O · app or device", "Read", "Write", "B/s", 0.0);

    public LiveChartViewModel NetworkChart { get; } = new LiveChartViewModel("Device-total network", "RX", "TX", "B/s", 0.0);

    public LiveChartViewModel AppNetworkChart { get; } = new LiveChartViewModel("Selected-app network", "RX", "TX", "B/s", 0.0);

    public LiveChartViewModel ThermalChart { get; } = new LiveChartViewModel("Battery temperature", "Temperature", "", " °C", 0.0);

    public LiveChartViewModel GpuChart { get; } = new LiveChartViewModel("GPU", "GPU", "", "%", 0.0, 100.0);

    public IReadOnlyList<LiveChartViewModel> PerformanceCharts { get; }

    public LiveChartViewModel StoredCpuChart { get; } = new LiveChartViewModel("Full CPU timeline", "Device", "Selected app", "%", 0.0, 100.0, TimeSpan.MaxValue);

    public LiveChartViewModel StoredMemoryChart { get; } = new LiveChartViewModel("Full memory timeline", "Device used", "Selected app", "B", 0.0, double.NaN, TimeSpan.MaxValue);

    public LiveChartViewModel StoredFpsFrameChart { get; } = new LiveChartViewModel("FPS and frame time", "FPS", "P95 frame ms", "", 0.0, double.NaN, TimeSpan.MaxValue);

    public LiveChartViewModel StoredDiskChart { get; } = new LiveChartViewModel("Full disk I/O timeline", "Read", "Write", "B/s", 0.0, double.NaN, TimeSpan.MaxValue);

    public LiveChartViewModel StoredNetworkChart { get; } = new LiveChartViewModel("Full network timeline", "RX", "TX", "B/s", 0.0, double.NaN, TimeSpan.MaxValue);

    public LiveChartViewModel StoredThermalChart { get; } = new LiveChartViewModel("Full thermal timeline", "Temperature", "", " °C", 0.0, double.NaN, TimeSpan.MaxValue);

    public IReadOnlyList<LiveChartViewModel> StoredSessionCharts { get; }

    public string WindowTitle => IsDemo ? "Android Dev Monitor — DEMO DATA" : "Android Dev Monitor";

    public string DeviceContext => SelectedDevice is { } device ? device.FriendlyName + " · " + device.Serial : "No Android device selected";

    public string SessionStatus => "Session " + SessionTime;

    public string LiveLabel => IsLive ? "Pause" : "Resume";

    public string RecordingLabel => IsRecording ? "Stop Recording" : "Record";

    public string AdbPathDisplay => IsDemo ? "Demo provider (ADB disabled)" : _adb.ResolvedAdbPath ?? "ADB executable was not found";

    public string SelectedApkInfo =>
        !string.IsNullOrWhiteSpace(SelectedApkPath) && File.Exists(SelectedApkPath)
            ? Path.GetFileName(SelectedApkPath) + " · " + FormatBytes(new FileInfo(SelectedApkPath).Length)
            : "No APK selected";

    public MainViewModel(IDeviceDiscoveryService devicesService, IMonitoringSource monitoring, ISessionStore sessions, IMediaService media, ISessionExporter exporter, IAdbExecutor adb, IDialogService dialogs, bool isDemo)
    {
        _devicesService = devicesService;
        _monitoring = monitoring;
        _sessions = sessions;
        _media = media;
        _exporter = exporter;
        _adb = adb;
        _dialogs = dialogs;
        IsDemo = isDemo;
        NavigationItems = ["Overview", "Instances", "Wireless", "Developer Tools", "Media", "Performance", "Logs", "File Explorer", "Network", "ADB Shell", "Automation", "Alerts", "Settings", "Help"];
        if (isDemo) { TrackedPackages.Add("com.company.mygame"); SelectedPackage = TrackedPackages[0]; }
        PerformanceCharts = [CpuChart, DeviceMemoryChart, AppMemoryChart, FpsChart, FrameTimeChart, JankChart, DiskChart, NetworkChart, AppNetworkChart, ThermalChart, GpuChart];
        StoredSessionCharts = [StoredCpuChart, StoredMemoryChart, StoredFpsFrameChart, StoredDiskChart, StoredNetworkChart, StoredThermalChart];
        LogView = CollectionViewSource.GetDefaultView(Logs);
        LogView.Filter = FilterLog;
        MediaView = CollectionViewSource.GetDefaultView(MediaItems);
        MediaView.Filter = FilterMedia;
        LocalFileView = CollectionViewSource.GetDefaultView(LocalFiles);
        LocalFileView.Filter = FilterLocalFile;
        RemoteFileView = CollectionViewSource.GetDefaultView(RemoteFiles);
        RemoteFileView.Filter = FilterRemoteFile;
        SelectedDeveloperTool = DeveloperTools.FirstOrDefault();
        InitializeAlertsAndSettings();
    }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }
        _initialized = true;
        await _sessions.InitializeAsync(CancellationToken.None);
        await LoadSettingsAsync();
        await RefreshDevicesAsync();
        foreach (MediaItem item in await _media.ScanAsync(CancellationToken.None))
        {
            MediaItems.Add(item);
        }
        RefreshMediaFilterOptions();
        RefreshMediaView();
        foreach (SessionSummary summary in await _sessions.ListSessionSummariesAsync(CancellationToken.None))
        {
            StoredSessions.Add(summary);
        }
        SelectedStoredSession = StoredSessions.FirstOrDefault();
        await LoadAlertHistoryAsync();
        _ = DiscoveryLoopAsync(_appCts.Token);
    }

    [RelayCommand]
    private void Navigate(string? page)
    {
        if (!string.IsNullOrWhiteSpace(page))
        {
            CurrentPage = page;
            if (page == "File Explorer")
            {
                _ = RefreshFileExplorerAsync();
            }
            else if (page == "Performance")
            {
                _ = ReloadStoredSessionsAsync();
            }
            else if (page == "Network")
            {
                _ = RefreshNetworkDiagnosticsAsync();
            }
            else if (page == "Wireless")
            {
                _ = RefreshWirelessAsync();
            }
        }
    }

    [RelayCommand]
    private void SelectOverviewTab(string? tab)
    {
        if (!string.IsNullOrWhiteSpace(tab))
        {
            OverviewTab = tab;
        }
    }

    [RelayCommand]
    private void ToggleLive()
    {
        IsLive = !IsLive;
    }

    [RelayCommand]
    private void ToggleLogPause()
    {
        IsLogPaused = !IsLogPaused;
        LogFilterStatus = IsLogPaused ? "Log view paused" : "Live logcat";
    }

    [RelayCommand]
    private void ClearLogView()
    {
        Logs.Clear();
        LogFilterStatus = "Local log view cleared";
    }

    [RelayCommand]
    private void OpenFullLogs()
    {
        CurrentPage = "Logs";
    }

    [RelayCommand]
    private void CopySelectedLog()
    {
        if (SelectedLogEntry is not null)
        {
            Clipboard.SetText($"{SelectedLogEntry.TimestampUtc:O} [{SelectedLogEntry.Priority}] {SelectedLogEntry.Source}: {SelectedLogEntry.Message}");
        }
    }

    [RelayCommand]
    private void ToggleProcessGroup(ProcessDisplayRow? row)
    {
        if (row is not null && row.CanExpand)
        {
            if (!_expandedProcessGroups.Add(row.Key))
            {
                _expandedProcessGroups.Remove(row.Key);
            }
            ApplyProcessFilter();
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (CurrentPage == "Instances")
        {
            await RefreshDevicesAsync();
        }
        else if (CurrentPage == "Media")
        {
            await ReloadMediaAsync();
        }
        else if (CurrentPage == "Performance")
        {
            await ReloadStoredSessionsAsync();
        }
        else if (CurrentPage == "File Explorer")
        {
            await RefreshFileExplorerAsync();
        }
        else if (CurrentPage == "Network")
        {
            await RefreshNetworkDiagnosticsAsync();
        }
        else if (CurrentPage == "ADB Shell")
        {
            StatusMessage = "Shell target refreshed: " + DeviceContext;
        }
        else if (SelectedDevice is not null)
        {
            await RefreshProcessesAsync(SelectedDevice, CancellationToken.None);
            StatusMessage = "Current data refreshed safely";
        }
    }

    [RelayCommand]
    private void MarkEvent()
    {
        if (_session == null || SelectedDevice is null)
        {
            return;
        }
        if (_dialogs.PromptMarker() is not { } prompt)
        {
            return;
        }
        DateTimeOffset utcNow = DateTimeOffset.UtcNow;
        SessionMarker sessionMarker = new SessionMarker(Guid.NewGuid(), _session.Id, utcNow, utcNow.ToLocalTime(), utcNow - _session.StartedUtc, prompt.Name, prompt.Note, SelectedDevice.Serial, SelectedDevice.FriendlyName, SelectedPackage);
        lock (_session.SyncRoot)
        {
            _session.Markers.Add(sessionMarker);
        }
        Markers.Add(sessionMarker);
        foreach (LiveChartViewModel performanceChart in PerformanceCharts)
        {
            performanceChart.AddAnnotation(utcNow, sessionMarker.Name, isAlert: false);
        }
        _sessions.SaveMarkerAsync(sessionMarker, CancellationToken.None);
        StatusMessage = "Marker added: " + sessionMarker.Name;
    }

    [RelayCommand]
    private async Task ExportSessionAsync()
    {
        if (_session == null)
        {
            return;
        }
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Android Dev Monitor", "Exports");
        try
        {
            string path = await _exporter.ExportZipAsync(_session, directory, CancellationToken.None);
            await _sessions.MarkSessionExportedAsync(_session.Id, path, CancellationToken.None);
            StatusMessage = "Exported " + Path.GetFileName(path);
        }
        catch (Exception ex)
        {
            _dialogs.Notify(ex.Message, error: true);
        }
    }

    [RelayCommand]
    private async Task ExportStoredSessionAsync(string? format)
    {
        if (SelectedStoredSession is null)
        {
            return;
        }
        MonitoringSession? session = LoadedStoredSession?.Id == SelectedStoredSession.Id
            ? LoadedStoredSession
            : await _sessions.LoadSessionAsync(SelectedStoredSession.Id, CancellationToken.None);
        if (session == null)
        {
            _dialogs.Notify("The selected session could not be loaded.", error: true);
            return;
        }
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Android Dev Monitor", "Exports");
        try
        {
            StoredSessionStatus = "Exporting " + (format ?? "ZIP") + "…";
            string path = (format ?? "ZIP").ToUpperInvariant() switch
            {
                "JSON" => await _exporter.ExportJsonAsync(session, directory, CancellationToken.None),
                "CSV" => await _exporter.ExportCsvAsync(session, directory, CancellationToken.None),
                _ => await _exporter.ExportZipAsync(session, directory, CancellationToken.None)
            };
            await _sessions.MarkSessionExportedAsync(session.Id, path, CancellationToken.None);
            StoredSessionStatus = "Exported " + Path.GetFileName(path);
            await ReloadStoredSessionsAsync(session.Id);
        }
        catch (Exception ex)
        {
            StoredSessionStatus = "Export failed";
            _dialogs.Notify(ex.Message, error: true);
        }
    }

    [RelayCommand]
    private void OpenSessionExportFolder()
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Android Dev Monitor", "Exports");
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo(directory)
        {
            UseShellExecute = true
        });
    }

    [RelayCommand]
    private async Task DeleteStoredSessionAsync()
    {
        if (SelectedStoredSession is not null)
        {
            SessionSummary selectedStoredSession = SelectedStoredSession;
            if (_session?.Id == selectedStoredSession.Id)
            {
                _dialogs.Notify("The active session cannot be deleted. Switch target or close it first.", error: true);
            }
            else if (_dialogs.Confirm("Delete stored session", $"Session: {selectedStoredSession.Id}\nDevice: {selectedStoredSession.Device.FriendlyName}\nSerial: {selectedStoredSession.Device.Serial}\nStarted: {selectedStoredSession.StartedUtc.ToLocalTime():g}\n\nThis permanently deletes its local metrics, markers, alerts and events."))
            {
                await _sessions.DeleteSessionAsync(selectedStoredSession.Id, CancellationToken.None);
                await ReloadStoredSessionsAsync();
                StoredSessionStatus = "Stored session deleted";
            }
        }
    }

    [RelayCommand]
    private async Task ImportAppiumLogAsync()
    {
        OpenFileDialog picker = new OpenFileDialog
        {
            Title = "Import external Appium log",
            Filter = "Log files (*.log;*.txt)|*.log;*.txt|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (picker.ShowDialog() != true)
        {
            return;
        }
        try
        {
            string[] lines = await File.ReadAllLinesAsync(picker.FileName);
            foreach (string line in lines.TakeLast(500))
            {
                Logs.Add(new LogEntry(DateTimeOffset.UtcNow, "Appium (external)", InferLogPriority(line), line, SelectedDevice?.Serial ?? "N/A", null, SelectedPackage));
            }
            TrimLogs();
            SelectedLogSource = "Appium (external)";
            LogFilterStatus = $"Imported {Math.Min(lines.Length, 500)} lines from {Path.GetFileName(picker.FileName)}";
        }
        catch (Exception ex)
        {
            _dialogs.Notify("Log import failed: " + ex.Message, error: true);
        }
    }

    [RelayCommand]
    private async Task SaveLogsAsync()
    {
        SaveFileDialog saveFileDialog = new SaveFileDialog
        {
            Title = "Save visible logs",
            Filter = "Log file (*.log)|*.log|Text file (*.txt)|*.txt",
            FileName = $"android-dev-monitor-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.log"
        };
        if (saveFileDialog.ShowDialog() == true)
        {
            string[] lines = LogView.Cast<LogEntry>().Select(entry => $"{entry.TimestampUtc:O} [{entry.Priority}] [{entry.Source}] {entry.Message}").ToArray();
            await File.WriteAllLinesAsync(saveFileDialog.FileName, lines);
            LogFilterStatus = $"Saved {lines.Length} visible lines";
        }
    }

    private bool HasSelectedMedia() => SelectedMediaItem is not null;

    [RelayCommand(CanExecute = nameof(HasSelectedMedia))]
    private void OpenSelectedMedia()
    {
        if (SelectedMediaItem is null)
        {
            return;
        }
        if (!File.Exists(SelectedMediaItem.LocalPath))
        {
            _dialogs.Notify("The selected media file no longer exists.", error: true);
            return;
        }
        Process.Start(new ProcessStartInfo(SelectedMediaItem.LocalPath)
        {
            UseShellExecute = true
        });
    }

    [RelayCommand]
    private void OpenMediaFolder()
    {
        string? path = SelectedMediaItem?.LocalPath;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"")
            {
                UseShellExecute = true
            });
        }
        else if (Directory.Exists(_media.MediaDirectory))
        {
            Process.Start(new ProcessStartInfo(_media.MediaDirectory)
            {
                UseShellExecute = true
            });
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedMedia))]
    private void CopyMediaPath()
    {
        if (SelectedMediaItem is not null)
        {
            Clipboard.SetText(SelectedMediaItem.LocalPath);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedMedia))]
    private async Task RenameMediaAsync()
    {
        if (SelectedMediaItem is null || string.IsNullOrWhiteSpace(MediaRenameText))
        {
            return;
        }
        try
        {
            MediaItem mediaItem = await _media.RenameAsync(SelectedMediaItem, MediaRenameText, CancellationToken.None);
            ReplaceMediaItem(SelectedMediaItem, mediaItem);
            StatusMessage = "Renamed media to " + mediaItem.FileName;
        }
        catch (Exception ex)
        {
            _dialogs.Notify("Rename failed: " + ex.Message, error: true);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedMedia))]
    private async Task SaveMediaMetadataAsync()
    {
        if (SelectedMediaItem is null)
        {
            return;
        }
        try
        {
            SessionMarker? selectedMediaMarker = SelectedMediaMarker;
            MediaItem item = SelectedMediaItem with
            {
                Note = (string.IsNullOrWhiteSpace(MediaNote) ? null : MediaNote.Trim()),
                MarkerId = selectedMediaMarker?.Id,
                SessionId = selectedMediaMarker?.SessionId ?? SelectedMediaItem.SessionId
            };
            item = await _media.SaveMetadataAsync(item, CancellationToken.None);
            ReplaceMediaItem(SelectedMediaItem, item);
            StatusMessage = "Saved metadata for " + item.FileName;
        }
        catch (Exception ex)
        {
            _dialogs.Notify("Could not save media metadata: " + ex.Message, error: true);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedMedia))]
    private async Task DeleteMediaAsync()
    {
        if (SelectedMediaItem is null)
        {
            return;
        }
        MediaItem item = SelectedMediaItem;
        if (!_dialogs.Confirm("Delete local media", $"File: {item.FileName}\nDevice: {item.FriendlyDeviceName}\nSerial: {item.DeviceSerial}\nPath: {item.LocalPath}\n\nThis permanently deletes the local media file."))
        {
            return;
        }
        try
        {
            await _media.DeleteAsync(item, CancellationToken.None);
            MediaItems.Remove(item);
            SelectedMediaItem = null;
            RefreshMediaFilterOptions();
            RefreshMediaView();
            StatusMessage = "Deleted " + item.FileName;
        }
        catch (Exception ex)
        {
            _dialogs.Notify("Delete failed: " + ex.Message, error: true);
        }
    }

    [RelayCommand]
    private async Task ScreenshotAsync()
    {
        if (SelectedDevice is null)
        {
            return;
        }
        try
        {
            MediaItem mediaItem = await _media.CaptureScreenshotAsync(SelectedDevice, SelectedPackage, _session?.Id, CancellationToken.None);
            AddMediaItem(mediaItem);
            StatusMessage = "Screenshot saved: " + mediaItem.FileName;
        }
        catch (Exception ex)
        {
            _dialogs.Notify("Screenshot failed: " + ex.Message, error: true);
        }
    }

    [RelayCommand]
    private async Task ToggleRecordingAsync()
    {
        if (SelectedDevice is null)
        {
            return;
        }
        try
        {
            if (!IsRecording)
            {
                AndroidDevice target = SelectedDevice;
                await _media.StartRecordingAsync(target, CancellationToken.None);
                SetRecordingContext(target, SelectedPackage, _session?.Id);
                IsRecording = true;
                ResolveAlert("RecordingFailed");
                StatusMessage = "Screen recording started on " + target.Serial;
            }
            else
            {
                AndroidDevice target = _recordingTarget ?? SelectedDevice;
                MediaItem mediaItem = await _media.StopRecordingAsync(
                    target,
                    _recordingPackage,
                    _recordingSessionId,
                    CancellationToken.None);
                AddMediaItem(mediaItem);
                IsRecording = false;
                ClearRecordingContext();
                ResolveAlert("RecordingFailed");
                StatusMessage = "Recording saved: " + mediaItem.FileName;
            }
        }
        catch (Exception ex)
        {
            IsRecording = false;
            ClearRecordingContext();
            RaiseAlert("RecordingFailed", "Critical", "Screen recording failed: " + ex.Message);
            _dialogs.Notify("Recording failed: " + ex.Message, error: true);
        }
    }

    [RelayCommand]
    private async Task RunShellAsync()
    {
        if (SelectedDevice is null || string.IsNullOrWhiteSpace(ShellCommand) || IsDemo || IsShellRunning)
        {
            ShellOutput = IsDemo ? "ADB Shell is disabled in demo mode; no command was executed." : SelectedDevice is null ? "Select a connected device." : ShellOutput;
            return;
        }
        AndroidDevice target = SelectedDevice;
        string command = ShellCommand.Trim();
        if (ShellHistory.Count == 0 || ShellHistory[0] != command)
        {
            ShellHistory.Insert(0, command);
        }
        while (ShellHistory.Count > 100)
        {
            ShellHistory.RemoveAt(ShellHistory.Count - 1);
        }
        _shellHistoryIndex = -1;
        _shellCts?.Dispose();
        _shellCts = new CancellationTokenSource();
        IsShellRunning = true;
        ShellStatus = "Running on " + target.Serial;
        try
        {
            AdbCommandResult result = await _adb.ExecuteAsync(target.Serial, ["shell", "sh", "-c", command], TimeSpan.FromSeconds(30), _shellCts.Token);
            TrackAdbCommand(result);
            string exitCode = result.ExitCode?.ToString() ?? "N/A";
            string stderr = string.IsNullOrWhiteSpace(result.StandardError) ? "" : "\nSTDERR:\n" + result.StandardError;
            ShellOutput = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {target.FriendlyName} ({target.Serial})\n$ {command}\nExit: {exitCode} · {result.Duration.TotalMilliseconds:N0} ms{(result.TimedOut ? " · TIMEOUT" : "")}{(result.Cancelled ? " · CANCELLED" : "")}\n\n{result.StandardOutput}{stderr}";
            ShellStatus = result.Success ? "Completed" : result.Cancelled ? "Cancelled" : result.TimedOut ? "Timed out" : "Failed · exit " + exitCode;
        }
        catch (OperationCanceledException)
        {
            ShellStatus = "Cancelled";
        }
        finally
        {
            IsShellRunning = false;
        }
    }

    [RelayCommand]
    private void CancelShell()
    {
        _shellCts?.Cancel();
    }

    [RelayCommand]
    private void ClearShellOutput()
    {
        ShellOutput = "";
        ShellStatus = "Local output cleared";
    }

    [RelayCommand]
    private void ApplyShellPreset()
    {
        if (!string.IsNullOrWhiteSpace(SelectedShellPreset))
        {
            ShellCommand = SelectedShellPreset;
        }
    }

    [RelayCommand]
    private void PreviousShellCommand()
    {
        if (ShellHistory.Count != 0)
        {
            _shellHistoryIndex = Math.Min(ShellHistory.Count - 1, _shellHistoryIndex + 1);
            ShellCommand = ShellHistory[_shellHistoryIndex];
        }
    }

    [RelayCommand]
    private void NextShellCommand()
    {
        if (ShellHistory.Count != 0)
        {
            _shellHistoryIndex = Math.Max(-1, _shellHistoryIndex - 1);
            ShellCommand = _shellHistoryIndex < 0 ? "" : ShellHistory[_shellHistoryIndex];
        }
    }

    [RelayCommand]
    private async Task SaveShellOutputAsync()
    {
        SaveFileDialog picker = new SaveFileDialog
        {
            Title = "Save ADB Shell output",
            Filter = "Text file (*.txt)|*.txt|Log file (*.log)|*.log",
            FileName = $"adb-shell-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.txt"
        };
        if (picker.ShowDialog() == true)
        {
            await File.WriteAllTextAsync(picker.FileName, ShellOutput);
            ShellStatus = "Saved " + Path.GetFileName(picker.FileName);
        }
    }

    [RelayCommand]
    private void SelectApk()
    {
        OpenFileDialog openFileDialog = new OpenFileDialog
        {
            Title = "Select APK for Automation",
            Filter = "Android packages (*.apk)|*.apk",
            CheckFileExists = true
        };
        if (openFileDialog.ShowDialog() == true)
        {
            SelectedApkPath = openFileDialog.FileName;
        }
    }

    [RelayCommand]
    private async Task RunAutomationAsync(string? action)
    {
        if (SelectedDevice is not null && !string.IsNullOrWhiteSpace(action))
        {
            try
            {
                await ExecuteAutomationActionAsync(action, SelectedDevice, SelectedPackage, SelectedApkPath, CancellationToken.None, confirmationHandled: false);
            }
            catch (Exception ex)
            {
                if (action.Contains("Recording", StringComparison.OrdinalIgnoreCase))
                {
                    IsRecording = false;
                    ClearRecordingContext();
                    RaiseAlert("RecordingFailed", "Critical", "Screen recording failed: " + ex.Message);
                }
                AppendAutomationLog("FAILED · " + action + " · " + ex.Message);
                StatusMessage = action + " failed";
            }
        }
    }

    private async Task<bool> ExecuteAutomationActionAsync(string action, AndroidDevice target, string? package, string? apkPath, CancellationToken token, bool confirmationHandled, string? argument = null)
    {
        string serial = target.Serial;
        bool destructive = action is "Clear Data" or "Uninstall";
        if (destructive && !confirmationHandled && !_dialogs.Confirm(action, $"Device: {target.FriendlyName}\nSerial: {serial}\nPackage: {package}\n\nExact effect: {action} for this package on this device."))
        {
            return false;
        }
        if (IsDemo)
        {
            AppendAutomationLog($"DEMO · {action} · {target.FriendlyName} ({serial}) · {package ?? "no package"} · no ADB command executed");
            return true;
        }
        switch (action)
        {
            case "Screenshot":
            {
                MediaItem screenshot = await _media.CaptureScreenshotAsync(target, package, _session?.Id, token);
                AddMediaItem(screenshot);
                AppendAutomationLog("Screenshot saved: " + screenshot.FileName + " · " + serial);
                return true;
            }
            case "Start Recording":
                if (IsRecording)
                {
                    return AutomationFailure("Recording is already active on " + (_recordingTarget?.Serial ?? "another target") + ".");
                }
                await _media.StartRecordingAsync(target, token);
                SetRecordingContext(target, package, _session?.Id);
                IsRecording = true;
                AppendAutomationLog("Recording started · " + serial);
                return true;
            case "Stop Recording":
            {
                if (!IsRecording || _recordingTarget?.Serial != target.Serial)
                {
                    return AutomationFailure("No recording started by this target is active. Current recording target: " + (_recordingTarget?.Serial ?? "none") + ".");
                }
                MediaItem recording = await _media.StopRecordingAsync(_recordingTarget, _recordingPackage, _recordingSessionId, token);
                AddMediaItem(recording);
                IsRecording = false;
                ClearRecordingContext();
                AppendAutomationLog("Recording saved: " + recording.FileName + " · " + serial);
                return true;
            }
            case "Marker":
                AddAutomationMarker(string.IsNullOrWhiteSpace(argument) ? "Automation marker" : argument, target, package);
                return true;
            case "Restart":
            {
                if (string.IsNullOrWhiteSpace(package))
                {
                    return AutomationFailure("Restart requires a selected package.");
                }
                AdbCommandResult stop = await _adb.ExecuteAsync(serial, ["shell", "am", "force-stop", package], TimeSpan.FromSeconds(20), token);
                AdbCommandResult launch = await _adb.ExecuteAsync(serial, ["shell", "monkey", "-p", package, "-c", "android.intent.category.LAUNCHER", "1"], TimeSpan.FromSeconds(30), token);
                AppendAutomationResult(action, target, package, stop, $"Force stop exit {stop.ExitCode}\nLaunch exit {launch.ExitCode}\n{launch.StandardOutput}\n{launch.StandardError}");
                return stop.Success && launch.Success;
            }
            case "Double Tap":
            {
                (int tapX, int tapY) = ParseCoordinates(argument ?? AutomationCoordinates, target);
                AdbCommandResult first = await _adb.ExecuteAsync(serial, Tap(tapX, tapY), TimeSpan.FromSeconds(15), token);
                await Task.Delay(90, token);
                AdbCommandResult second = await _adb.ExecuteAsync(serial, Tap(tapX, tapY), TimeSpan.FromSeconds(15), token);
                AppendAutomationResult(action, target, package, second, $"First tap exit {first.ExitCode}\nSecond tap exit {second.ExitCode}");
                return first.Success && second.Success;
            }
        }

        (int x, int y) = ParseCoordinates(argument ?? AutomationCoordinates, target);
        (int width, int height) = ParseResolution(target.Resolution);
        int swipeX = Math.Max(120, width * 35 / 100);
        int swipeY = Math.Max(180, height * 35 / 100);
        string durationMs = Math.Clamp(SwipeDurationMs, 50, 5000).ToString(CultureInfo.InvariantCulture);
        string text = argument ?? AutomationText;
        bool hasPackage = !string.IsNullOrWhiteSpace(package);
        IReadOnlyList<string> args = action switch
        {
            "Install APK" when !string.IsNullOrWhiteSpace(apkPath) && File.Exists(apkPath) => ["install", "-r", apkPath],
            "Launch" when hasPackage => ["shell", "monkey", "-p", package!, "-c", "android.intent.category.LAUNCHER", "1"],
            "Force Stop" when hasPackage => ["shell", "am", "force-stop", package!],
            "Clear Data" when hasPackage => ["shell", "pm", "clear", package!],
            "Uninstall" when hasPackage => ["uninstall", package!],
            "App Info" when hasPackage => ["shell", "am", "start", "-a", "android.settings.APPLICATION_DETAILS_SETTINGS", "-d", "package:" + package],
            "Grant Permission" when hasPackage && !string.IsNullOrWhiteSpace(text) => ["shell", "pm", "grant", package!, text.Trim()],
            "Revoke Permission" when hasPackage && !string.IsNullOrWhiteSpace(text) => ["shell", "pm", "revoke", package!, text.Trim()],
            "Back" => KeyEvent("4"),
            "Home" => KeyEvent("3"),
            "Recents" => KeyEvent("187"),
            "Power" => KeyEvent("26"),
            "Volume Up" => KeyEvent("24"),
            "Volume Down" => KeyEvent("25"),
            "Enter" => KeyEvent("66"),
            "Escape" => KeyEvent("111"),
            "Tap Center" => Tap(width / 2, height / 2),
            "Tap Coordinates" => Tap(x, y),
            "Long Press" => Swipe(x, y, x, y, "800"),
            "Swipe Up" => Swipe(x, Math.Min(height - 1, y + swipeY), x, Math.Max(0, y - swipeY), durationMs),
            "Swipe Down" => Swipe(x, Math.Max(0, y - swipeY), x, Math.Min(height - 1, y + swipeY), durationMs),
            "Swipe Left" => Swipe(Math.Min(width - 1, x + swipeX), y, Math.Max(0, x - swipeX), y, durationMs),
            "Swipe Right" => Swipe(Math.Max(0, x - swipeX), y, Math.Min(width - 1, x + swipeX), y, durationMs),
            "Send Text" when !string.IsNullOrWhiteSpace(text) => ["shell", "input", "text", EscapeAndroidInputText(text)],
            "Portrait" => ["shell", "settings", "put", "system", "user_rotation", "0"],
            "Landscape Left" => ["shell", "settings", "put", "system", "user_rotation", "1"],
            "Landscape Right" => ["shell", "settings", "put", "system", "user_rotation", "3"],
            "Auto Rotation" => ["shell", "settings", "put", "system", "accelerometer_rotation", "1"],
            _ => []
        };
        if (args.Count == 0)
        {
            return AutomationFailure(action == "Install APK" ? "Select an APK first; selection never installs automatically." : action + " requires a selected package or valid argument.");
        }
        if (action is "Portrait" or "Landscape Left" or "Landscape Right")
        {
            await _adb.ExecuteAsync(serial, ["shell", "settings", "put", "system", "accelerometer_rotation", "0"], TimeSpan.FromSeconds(15), token);
        }
        TimeSpan timeout = action == "Install APK" ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(30);
        AdbCommandResult result = await _adb.ExecuteAsync(serial, args, timeout, token);
        AppendAutomationResult(action, target, package, result);
        return result.Success;

        static IReadOnlyList<string> KeyEvent(string code) => ["shell", "input", "keyevent", code];
        static IReadOnlyList<string> Tap(int x, int y) => ["shell", "input", "tap", x.ToString(CultureInfo.InvariantCulture), y.ToString(CultureInfo.InvariantCulture)];
        static IReadOnlyList<string> Swipe(int x1, int y1, int x2, int y2, string duration) =>
            ["shell", "input", "swipe", x1.ToString(CultureInfo.InvariantCulture), y1.ToString(CultureInfo.InvariantCulture), x2.ToString(CultureInfo.InvariantCulture), y2.ToString(CultureInfo.InvariantCulture), duration];
    }

    [RelayCommand]
    private void BrowseLocal()
    {
        OpenFolderDialog openFolderDialog = new OpenFolderDialog
        {
            Title = "Select local transfer folder",
            InitialDirectory = (Directory.Exists(LocalPath) ? LocalPath : null)
        };
        if (openFolderDialog.ShowDialog() == true)
        {
            NavigateLocalPath(openFolderDialog.FolderName, recordHistory: true);
        }
    }

    [RelayCommand]
    private void OpenSelectedLocal()
    {
        if (SelectedLocalFile is { IsDirectory: true } folder)
        {
            NavigateLocalPath(folder.FullPath, recordHistory: true);
        }
    }

    [RelayCommand]
    private void LocalUp()
    {
        if (Directory.GetParent(LocalPath)?.FullName is { } parent)
        {
            NavigateLocalPath(parent, recordHistory: true);
        }
    }

    [RelayCommand]
    private void LocalBack()
    {
        if (_localBackHistory.Count != 0)
        {
            _localForwardHistory.Push(LocalPath);
            NavigateLocalPath(_localBackHistory.Pop(), recordHistory: false);
        }
    }

    [RelayCommand]
    private void LocalForward()
    {
        if (_localForwardHistory.Count != 0)
        {
            _localBackHistory.Push(LocalPath);
            NavigateLocalPath(_localForwardHistory.Pop(), recordHistory: false);
        }
    }

    [RelayCommand]
    private void OpenLocalFolder()
    {
        if (Directory.Exists(LocalPath))
        {
            Process.Start(new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = true,
                ArgumentList = { LocalPath }
            });
        }
    }

    [RelayCommand]
    private async Task OpenSelectedRemoteAsync()
    {
        if (SelectedRemoteFile is { IsDirectory: true } folder)
        {
            await NavigateRemotePathAsync(folder.FullPath, recordHistory: true);
        }
    }

    [RelayCommand]
    private async Task RemoteUpAsync()
    {
        string path = RemotePath.TrimEnd('/');
        int separator = path.LastIndexOf('/');
        await NavigateRemotePathAsync(separator <= 0 ? "/" : path[..separator], recordHistory: true);
    }

    [RelayCommand]
    private async Task RemoteBackAsync()
    {
        if (_remoteBackHistory.Count != 0)
        {
            _remoteForwardHistory.Push(RemotePath);
            await NavigateRemotePathAsync(_remoteBackHistory.Pop(), recordHistory: false);
        }
    }

    [RelayCommand]
    private async Task RemoteForwardAsync()
    {
        if (_remoteForwardHistory.Count != 0)
        {
            _remoteBackHistory.Push(RemotePath);
            await NavigateRemotePathAsync(_remoteForwardHistory.Pop(), recordHistory: false);
        }
    }

    [RelayCommand]
    private async Task GoRemoteQuickLocationAsync()
    {
        await NavigateRemotePathAsync(SelectedRemoteQuickLocation, recordHistory: true);
    }

    [RelayCommand]
    private async Task CreateRemoteFolderAsync()
    {
        if (SelectedDevice is null || IsDemo || !IsSafeRemoteName(NewRemoteFolderName))
        {
            StatusMessage = "Enter a valid folder name without slashes.";
            return;
        }
        string path = CombineRemote(RemotePath, NewRemoteFolderName.Trim());
        AdbCommandResult result = await _adb.ExecuteAsync(SelectedDevice.Serial, ["shell", "mkdir -- " + AndroidParsers.ShellQuote(path)], TimeSpan.FromSeconds(20), CancellationToken.None);
        StatusMessage = result.Success ? "Created " + path : "Create folder failed: " + result.StandardError.Trim();
        if (result.Success)
        {
            await RefreshFileExplorerAsync();
        }
    }

    [RelayCommand]
    private async Task RenameRemoteAsync()
    {
        if (SelectedDevice is null || SelectedRemoteFile is null || IsDemo || !IsSafeRemoteName(RemoteRenameText))
        {
            StatusMessage = "Select an item and enter a valid new name.";
            return;
        }
        string destination = CombineRemote(RemotePath, RemoteRenameText.Trim());
        AdbCommandResult result = await _adb.ExecuteAsync(SelectedDevice.Serial, ["shell", "mv -- " + AndroidParsers.ShellQuote(SelectedRemoteFile.FullPath) + " " + AndroidParsers.ShellQuote(destination)], TimeSpan.FromSeconds(30), CancellationToken.None);
        StatusMessage = result.Success ? "Renamed to " + RemoteRenameText.Trim() : "Rename failed: " + result.StandardError.Trim();
        if (result.Success)
        {
            await RefreshFileExplorerAsync();
        }
    }

    [RelayCommand]
    private async Task DeleteRemoteAsync()
    {
        if (SelectedDevice is null || SelectedRemoteFile is null || IsDemo)
        {
            return;
        }
        FileEntry entry = SelectedRemoteFile;
        if (_dialogs.Confirm("Delete device item", $"Device: {SelectedDevice.FriendlyName}\nSerial: {SelectedDevice.Serial}\nPath: {entry.FullPath}\n\nThis permanently deletes the selected {(entry.IsDirectory ? "folder and its contents" : "file")}."))
        {
            string removeFlags = entry.IsDirectory ? "-rf" : "-f";
            AdbCommandResult result = await _adb.ExecuteAsync(SelectedDevice.Serial, ["shell", $"rm {removeFlags} -- {AndroidParsers.ShellQuote(entry.FullPath)}"], TimeSpan.FromMinutes(1), CancellationToken.None);
            StatusMessage = result.Success ? "Deleted " + entry.Name : "Delete failed: " + result.StandardError.Trim();
            if (result.Success)
            {
                await RefreshFileExplorerAsync();
            }
        }
    }

    [RelayCommand]
    private void CancelTransfer()
    {
        _transferCts?.Cancel();
    }

    [RelayCommand]
    private async Task RefreshFileExplorerAsync()
    {
        LoadLocalFiles();
        RemoteFiles.Clear();
        if (SelectedDevice is null || IsDemo)
        {
            StatusMessage = IsDemo ? "Remote file browsing is disabled in demo mode" : "Select a device";
            return;
        }
        // The trailing slash makes ls follow symlinked folders such as /sdcard instead of listing the link itself.
        string directory = RemotePath == "/" ? "/" : RemotePath.TrimEnd('/') + "/";
        AdbCommandResult listing = await _adb.ExecuteAsync(SelectedDevice.Serial, ["shell", "date +%z; ls -la -- " + AndroidParsers.ShellQuote(directory)], TimeSpan.FromSeconds(15), CancellationToken.None);
        if (!listing.Success)
        {
            StatusMessage = "Remote path inaccessible: " + listing.StandardError.Trim();
            return;
        }
        TimeSpan deviceOffset = AndroidParsers.ParseUtcOffset(listing.StandardOutput) ?? TimeZoneInfo.Local.GetUtcOffset(DateTime.Now);
        foreach (FileEntry entry in AndroidParsers.ParseLsLong(listing.StandardOutput, directory, deviceOffset))
        {
            RemoteFiles.Add(entry);
        }
        StatusMessage = $"Loaded {RemoteFiles.Count} entries from {RemotePath}";
    }

    [RelayCommand]
    private async Task PushFileAsync()
    {
        if (SelectedDevice is null || SelectedLocalFile is null || SelectedLocalFile.IsDirectory || IsDemo)
        {
            return;
        }
        AndroidDevice target = SelectedDevice;
        FileEntry file = SelectedLocalFile;
        string destination = CombineRemote(RemotePath, file.Name);
        _transferCts?.Dispose();
        _transferCts = new CancellationTokenSource();
        IsTransferRunning = true;
        TransferStatus = $"Pushing {file.Name} to {target.Serial}…";
        try
        {
            AdbCommandResult result = await _adb.ExecuteAsync(target.Serial, ["push", file.FullPath, destination], TimeSpan.FromMinutes(5), _transferCts.Token);
            TransferStatus = result.Success ? "Push complete · " + file.Name : result.Cancelled ? "Push cancelled" : "Push failed · " + result.StandardError.Trim();
            StatusMessage = TransferStatus;
            if (result.Success && SelectedDevice?.Serial == target.Serial)
            {
                await RefreshFileExplorerAsync();
            }
        }
        catch (OperationCanceledException)
        {
            TransferStatus = "Push cancelled";
            StatusMessage = TransferStatus;
        }
        catch (Exception ex)
        {
            TransferStatus = "Push failed · " + ex.Message;
            StatusMessage = TransferStatus;
        }
        finally
        {
            IsTransferRunning = false;
        }
    }

    [RelayCommand]
    private async Task PullFileAsync()
    {
        if (SelectedDevice is null || SelectedRemoteFile is null || SelectedRemoteFile.IsDirectory || IsDemo)
        {
            return;
        }
        AndroidDevice target = SelectedDevice;
        FileEntry file = SelectedRemoteFile;
        string localPath = LocalPath;
        _transferCts?.Dispose();
        _transferCts = new CancellationTokenSource();
        IsTransferRunning = true;
        TransferStatus = $"Pulling {file.Name} from {target.Serial}…";
        try
        {
            AdbCommandResult result = await _adb.ExecuteAsync(target.Serial, ["pull", file.FullPath, localPath], TimeSpan.FromMinutes(5), _transferCts.Token);
            TransferStatus = result.Success ? "Pull complete · " + file.Name : result.Cancelled ? "Pull cancelled" : "Pull failed · " + result.StandardError.Trim();
            StatusMessage = TransferStatus;
            if (result.Success)
            {
                LoadLocalFiles();
            }
        }
        catch (OperationCanceledException)
        {
            TransferStatus = "Pull cancelled";
            StatusMessage = TransferStatus;
        }
        catch (Exception ex)
        {
            TransferStatus = "Pull failed · " + ex.Message;
            StatusMessage = TransferStatus;
        }
        finally
        {
            IsTransferRunning = false;
        }
    }

    [RelayCommand]
    private void AddSequenceStep()
    {
        TimeSpan? duration = SelectedStepKind == AutomationStepKind.Wait ? TimeSpan.FromMilliseconds(Math.Clamp(NewStepDurationMs, 0, 300000)) : null;
        SequenceSteps.Add(new AutomationStep(Guid.NewGuid(), SelectedStepKind, StepDisplayName(SelectedStepKind), string.IsNullOrWhiteSpace(NewStepArgument) ? null : NewStepArgument.Trim(), duration, SelectedStepKind == AutomationStepKind.ClearData));
    }

    [RelayCommand]
    private void RemoveSequenceStep(AutomationStep? step)
    {
        if (step is not null)
        {
            SequenceSteps.Remove(step);
        }
    }

    [RelayCommand]
    private void MoveSequenceStepUp(AutomationStep? step)
    {
        if (step is not null)
        {
            int index = SequenceSteps.IndexOf(step);
            if (index > 0)
            {
                SequenceSteps.Move(index, index - 1);
            }
        }
    }

    [RelayCommand]
    private void MoveSequenceStepDown(AutomationStep? step)
    {
        if (step is not null)
        {
            int index = SequenceSteps.IndexOf(step);
            if (index >= 0 && index < SequenceSteps.Count - 1)
            {
                SequenceSteps.Move(index, index + 1);
            }
        }
    }

    [RelayCommand]
    private void PauseSequence()
    {
        if (IsAutomationRunning)
        {
            IsAutomationPaused = true;
            AutomationState = "Paused";
        }
    }

    [RelayCommand]
    private void ResumeSequence()
    {
        if (IsAutomationRunning)
        {
            IsAutomationPaused = false;
            AutomationState = "Running";
        }
    }

    [RelayCommand]
    private void StopSequence()
    {
        _automationCts?.Cancel();
    }

    [RelayCommand]
    private async Task RunSequenceAsync()
    {
        if (SelectedDevice is null || SequenceSteps.Count == 0 || IsAutomationRunning)
        {
            return;
        }
        AndroidDevice target = SelectedDevice;
        string? package = SelectedPackage;
        string? apkPath = SelectedApkPath;
        AutomationStep[] steps = SequenceSteps.ToArray();
        int repeatCount = Math.Clamp(AutomationRepeatCount, 1, 100);
        string[] destructiveSteps = steps.Where(step => step.IsDestructive).Select(step => step.Name).ToArray();
        TimeSpan estimate = TimeSpan.FromMilliseconds(steps.Sum(step => step.Duration?.TotalMilliseconds ?? 500.0) * repeatCount);
        if (!_dialogs.Confirm("Run automation sequence", $"Device: {target.FriendlyName}\nSerial: {target.Serial}\nPackage: {package}\nSteps: {steps.Length}\nRepeat: {repeatCount}\nEstimated: {estimate:g}\nDestructive: {(destructiveSteps.Length == 0 ? "None" : string.Join(", ", destructiveSteps))}"))
        {
            return;
        }
        _automationCts?.Dispose();
        _automationCts = new CancellationTokenSource();
        CancellationToken token = _automationCts.Token;
        IsAutomationRunning = true;
        IsAutomationPaused = false;
        AutomationState = "Running on " + target.FriendlyName + " · " + target.Serial;
        AppendAutomationLog($"Sequence started · {steps.Length} steps · target locked to {target.Serial}");
        try
        {
            for (int repeat = 0; repeat < Math.Clamp(AutomationRepeatCount, 1, 100); repeat++)
            {
                foreach (AutomationStep step in steps)
                {
                    token.ThrowIfCancellationRequested();
                    while (IsAutomationPaused)
                    {
                        await Task.Delay(100, token);
                    }
                    if (SelectedDevice?.Serial != target.Serial)
                    {
                        AutomationState = "Running on original target " + target.Serial + "; current selection is different";
                    }
                    if (!(await ExecuteSequenceStepAsync(step, target, package, apkPath, token)) && AutomationStopOnFailure)
                    {
                        throw new InvalidOperationException("Step failed: " + step.Name);
                    }
                }
            }
            AutomationState = "Completed";
            AppendAutomationLog("Sequence completed · " + target.Serial);
        }
        catch (OperationCanceledException)
        {
            if (IsRecording && _recordingTarget?.Serial == target.Serial)
            {
                try
                {
                    MediaItem interruptedRecording = await _media.StopRecordingAsync(
                        _recordingTarget,
                        _recordingPackage,
                        _recordingSessionId,
                        CancellationToken.None);
                    AddMediaItem(interruptedRecording);
                }
                catch
                {
                }
                IsRecording = false;
                ClearRecordingContext();
            }
            AutomationState = "Stopped";
            AppendAutomationLog("Sequence stopped · " + target.Serial);
        }
        catch (Exception ex)
        {
            AutomationState = "Failed: " + ex.Message;
            AppendAutomationLog(AutomationState);
            if (steps.Any(step => step.Kind is AutomationStepKind.StartRecording or AutomationStepKind.StopRecording))
            {
                if (IsRecording && _recordingTarget is not null)
                {
                    try
                    {
                        MediaItem interruptedRecording = await _media.StopRecordingAsync(
                            _recordingTarget,
                            _recordingPackage,
                            _recordingSessionId,
                            CancellationToken.None);
                        AddMediaItem(interruptedRecording);
                    }
                    catch
                    {
                    }
                }
                IsRecording = false;
                ClearRecordingContext();
                RaiseAlert("RecordingFailed", "Critical", "Screen recording failed during Automation: " + ex.Message);
            }
        }
        finally
        {
            IsAutomationRunning = false;
            IsAutomationPaused = false;
        }
    }

    private async Task<bool> ExecuteSequenceStepAsync(AutomationStep step, AndroidDevice target, string? package, string? apkPath, CancellationToken token)
    {
        if (step.Kind == AutomationStepKind.Wait)
        {
            await Task.Delay(step.Duration ?? TimeSpan.FromMilliseconds(Math.Clamp(NewStepDurationMs, 0, 300000)), token);
            AppendAutomationLog($"Wait completed · {(step.Duration ?? TimeSpan.FromMilliseconds(NewStepDurationMs)).TotalMilliseconds:N0} ms");
            return true;
        }
        if (step.Kind == AutomationStepKind.WaitForLog)
        {
            string text = step.Argument ?? "";
            DateTimeOffset deadline = DateTimeOffset.UtcNow + (step.Duration ?? TimeSpan.FromSeconds(30));
            while (DateTimeOffset.UtcNow < deadline)
            {
                token.ThrowIfCancellationRequested();
                if (Logs.Any(entry => entry.Message.Contains(text, StringComparison.OrdinalIgnoreCase)))
                {
                    AppendAutomationLog("Wait for log matched: " + text);
                    return true;
                }
                await Task.Delay(250, token);
            }
            return AutomationFailure("Wait for log timed out: " + text);
        }
        if (step.Kind == AutomationStepKind.WaitForForeground)
        {
            string? expected = step.Argument ?? package;
            DateTimeOffset deadline = DateTimeOffset.UtcNow + (step.Duration ?? TimeSpan.FromSeconds(30));
            while (DateTimeOffset.UtcNow < deadline)
            {
                AdbCommandResult activities = await _adb.ExecuteAsync(target.Serial, ["shell", "dumpsys", "activity", "activities"], TimeSpan.FromSeconds(10), token);
                if (!string.IsNullOrWhiteSpace(expected) && activities.StandardOutput.Contains(expected, StringComparison.Ordinal))
                {
                    AppendAutomationLog("Foreground package detected: " + expected);
                    return true;
                }
                await Task.Delay(500, token);
            }
            return AutomationFailure("Foreground wait timed out: " + expected);
        }
        if (step.Kind == AutomationStepKind.CheckProcess)
        {
            string? expected = step.Argument ?? package;
            if (string.IsNullOrWhiteSpace(expected))
            {
                return AutomationFailure("Check process requires a package.");
            }
            AdbCommandResult pidof = await _adb.ExecuteAsync(target.Serial, ["shell", "pidof", expected], TimeSpan.FromSeconds(15), token);
            AppendAutomationResult("Check Process", target, expected, pidof);
            return pidof.Success && !string.IsNullOrWhiteSpace(pidof.StandardOutput);
        }
        return await ExecuteAutomationActionAsync(step.Kind switch
        {
            AutomationStepKind.InstallApk => "Install APK",
            AutomationStepKind.Launch => "Launch",
            AutomationStepKind.Restart => "Restart",
            AutomationStepKind.ForceStop => "Force Stop",
            AutomationStepKind.ClearData => "Clear Data",
            AutomationStepKind.Tap => "Tap Coordinates",
            AutomationStepKind.SwipeUp => "Swipe Up",
            AutomationStepKind.SwipeDown => "Swipe Down",
            AutomationStepKind.SwipeLeft => "Swipe Left",
            AutomationStepKind.SwipeRight => "Swipe Right",
            AutomationStepKind.EnterText => "Send Text",
            AutomationStepKind.Back => "Back",
            AutomationStepKind.Home => "Home",
            AutomationStepKind.Screenshot => "Screenshot",
            AutomationStepKind.StartRecording => "Start Recording",
            AutomationStepKind.StopRecording => "Stop Recording",
            AutomationStepKind.Marker => "Marker",
            _ => step.Name,
        }, target, package, apkPath, token, confirmationHandled: true, step.Argument);
    }

    private void AddAutomationMarker(string name, AndroidDevice target, string? package)
    {
        if (_session == null || _session.Device.Serial != target.Serial)
        {
            AppendAutomationLog("Marker '" + name + "' skipped because active session belongs to another device.");
            return;
        }
        DateTimeOffset utcNow = DateTimeOffset.UtcNow;
        SessionMarker sessionMarker = new SessionMarker(Guid.NewGuid(), _session.Id, utcNow, utcNow.ToLocalTime(), utcNow - _session.StartedUtc, name, "Automation", target.Serial, target.FriendlyName, package);
        lock (_session.SyncRoot)
        {
            _session.Markers.Add(sessionMarker);
        }
        Markers.Add(sessionMarker);
        foreach (LiveChartViewModel performanceChart in PerformanceCharts)
        {
            performanceChart.AddAnnotation(utcNow, name, isAlert: false);
        }
        _sessions.SaveMarkerAsync(sessionMarker, CancellationToken.None);
        AppendAutomationLog("Marker added: " + name);
    }

    private void AppendAutomationResult(string action, AndroidDevice target, string? package, AdbCommandResult result, string? extra = null)
    {
        TrackAdbCommand(result);
        string text = string.Join('\n', new[] { extra, result.StandardOutput.Trim(), result.StandardError.Trim() }.Where(value => !string.IsNullOrWhiteSpace(value)));
        AppendAutomationLog($"{action} · {target.FriendlyName} ({target.Serial}) · {package ?? "no package"} · exit {result.ExitCode?.ToString() ?? "N/A"} · {result.Duration.TotalMilliseconds:N0} ms{(result.TimedOut ? " · TIMEOUT" : "")}{(result.Cancelled ? " · CANCELLED" : "")}{((text.Length == 0) ? "" : ("\n" + text))}");
        Logs.Add(new LogEntry(DateTimeOffset.UtcNow, "Session", result.Success ? "Info" : "Error", $"Automation {action}: exit {result.ExitCode?.ToString() ?? "N/A"} in {result.Duration.TotalMilliseconds:N0} ms", target.Serial, null, package));
        TrimLogs();
        if (_session != null && _session.Device.Serial == target.Serial)
        {
            AddSessionEvent("Automation", action + ": " + (result.Success ? "completed" : "failed"), target.Serial, package);
        }
    }

    private void AppendAutomationLog(string message)
    {
        AutomationLog = $"{AutomationLog}\n\n[{DateTimeOffset.Now:HH:mm:ss}] {message}";
        if (AutomationLog.Length > 30000)
        {
            AutomationLog = AutomationLog[^30000..];
        }
    }

    private bool AutomationFailure(string message)
    {
        AppendAutomationLog("FAILED · " + message);
        return false;
    }

    private static (int X, int Y) ParseCoordinates(string value, AndroidDevice target)
    {
        (int width, int height) = ParseResolution(target.Resolution);
        int[] values = Regex.Matches(value ?? "", @"-?\d+").Select(match => int.Parse(match.Value, CultureInfo.InvariantCulture)).Take(2).ToArray();
        if (values.Length != 2)
        {
            return (width / 2, height / 2);
        }
        return (Math.Clamp(values[0], 0, width - 1), Math.Clamp(values[1], 0, height - 1));
    }

    private static (int Width, int Height) ParseResolution(string resolution)
    {
        Match match = Regex.Match(resolution ?? "", @"(?<width>\d+)x(?<height>\d+)");
        if (!match.Success || !int.TryParse(match.Groups["width"].Value, out int width) || !int.TryParse(match.Groups["height"].Value, out int height))
        {
            return (1080, 1920);
        }
        return (Math.Max(1, width), Math.Max(1, height));
    }

    private static string EscapeAndroidInputText(string value)
    {
        return value.Replace("%", "\\%").Replace(" ", "%s");
    }

    private static string StepDisplayName(AutomationStepKind kind)
    {
        return Regex.Replace(kind.ToString(), "([a-z])([A-Z])", "$1 $2");
    }

    [RelayCommand]
    private void ShowMore()
    {
        _dialogs.Notify($"Device: {DeviceContext}\nADB: {AdbPathDisplay}\nMedia: {_media.MediaDirectory}\nSession: {_session?.Id.ToString() ?? "None"}");
    }

    private async Task RefreshDevicesAsync()
    {
        if (!await _discoveryGate.WaitAsync(0))
        {
            return;
        }
        try
        {
            ConnectionText = "Connecting";
            IReadOnlyList<AndroidDevice> discovered = await _devicesService.DiscoverAsync(CancellationToken.None);
            List<AndroidDevice> enriched = (await Task.WhenAll(
                discovered.Select(item => _devicesService.EnrichAsync(item, CancellationToken.None)))).ToList();
            AndroidDevice? previousDevice = null;
            AndroidDevice? selectedAfterRefresh = null;
            bool contextChanged = false;
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                AndroidDevice? selectedDevice = SelectedDevice;
                previousDevice = selectedDevice;
                string? previous = selectedDevice?.Serial;
                if (selectedDevice is not null &&
                    !enriched.Any(item => item.Serial == selectedDevice.Serial))
                {
                    RaiseAlert(
                        "DeviceDisconnected",
                        "Critical",
                        $"{selectedDevice.FriendlyName} ({selectedDevice.Serial}) disconnected.");
                }
                AndroidDevice? androidDevice =
                    enriched.FirstOrDefault(x => x.Serial == previous) ??
                    enriched.FirstOrDefault(x => RestoreLastDevice && x.Serial == _preferredDeviceSerial) ??
                    enriched.FirstOrDefault(x => x.IsConnected) ??
                    enriched.FirstOrDefault();
                contextChanged =
                    selectedDevice?.Serial != androidDevice?.Serial ||
                    selectedDevice?.State != androidDevice?.State;
                _suppressDeviceSwitch = true;
                try
                {
                    Devices.Clear();
                    foreach (AndroidDevice device in enriched)
                    {
                        Devices.Add(device);
                    }
                    SelectedDevice = androidDevice;
                }
                finally
                {
                    _suppressDeviceSwitch = false;
                }
                selectedAfterRefresh = androidDevice;
                ConnectionText = Devices.Count != 0 ? $"{Devices.Count(x => x.IsConnected)} connected" : _adb.ResolvedAdbPath == null && !IsDemo ? "ADB unavailable" : "No devices";
            });
            if (_initialized && contextChanged)
            {
                if (selectedAfterRefresh is not null)
                {
                    await SwitchDeviceAsync(selectedAfterRefresh);
                }
                else
                {
                    await HandleSelectedDeviceUnavailableAsync(previousDevice);
                }
            }
            else if (SelectedDevice is null)
            {
                StatusMessage = _adb.ResolvedAdbPath == null && !IsDemo ? "ADB executable was not found. Configure it in Settings or use --demo." : "No Android devices found";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            ConnectionText = "ADB unavailable";
        }
        finally
        {
            _discoveryGate.Release();
        }
    }

    private async Task SwitchDeviceAsync(AndroidDevice device)
    {
        _contextCts?.Cancel();
        _contextCts?.Dispose();
        _contextCts = new CancellationTokenSource();
        CancellationToken token = _contextCts.Token;
        if (_session != null)
        {
            _session.EndedUtc = DateTimeOffset.UtcNow;
            AddSessionEvent("SessionEnded", "Monitoring ended for " + _session.Device.FriendlyName, _session.Device.Serial, _session.CurrentPackage);
            await _sessions.SaveSessionAsync(_session, CancellationToken.None);
        }
        ConnectionText = "Connecting";
        _liveSamples.Clear();
        ResetProcessTable();
        CpuCard.Clear(); MemoryCard.Clear(); GpuCard.Clear(); DiskCard.Clear();
        ResetTracking();
        LiveSamples.Clear();
        Processes.Clear();
        ProcessRows.Clear();
        Markers.Clear();
        ResetAlertStateForTarget();
        _expandedProcessGroups.Clear();
        foreach (LiveChartViewModel performanceChart in PerformanceCharts)
        {
            performanceChart.Clear();
        }
        _lastSampleUtc = default;
        _lastNetworkTotalTimestamp = default;
        _sessionNetworkRxBytes = 0.0;
        _sessionNetworkTxBytes = 0.0;
        _processSnapshotCpuTotal = null;
        _packageAutoSelectedForSerial = null;
        NetworkContext = "Network context: waiting for data";
        NetworkTotals = "Session RX N/A · TX N/A";
        StorageSummary = "Storage capacity: waiting for data";
        AppStorageSummary = "Selected-app size: N/A";
        ThermalSummary = "Thermal and battery: waiting for data";
        _session = new MonitoringSession
        {
            StartedUtc = DateTimeOffset.UtcNow,
            Device = device,
            CurrentPackage = SelectedPackage
        };
        AddSessionEvent("SessionStarted", "Monitoring started for " + device.FriendlyName, device.Serial, SelectedPackage);
        await _sessions.SaveSessionAsync(_session, token);
        ConnectionText = device.State == DeviceState.Connected ? "ADB connected" : device.State.ToString();
        StatusMessage = "Monitoring " + device.FriendlyName;
        if (!device.IsConnected)
        {
            RaiseAlert(device.State switch
            {
                DeviceState.Offline => "DeviceOffline",
                DeviceState.Unauthorized => "DeviceUnauthorized",
                _ => "DeviceDisconnected",
            }, "Critical", $"{device.FriendlyName} is {device.State}.");
            _ = TimerLoopAsync(_session, token);
        }
        else
        {
            _ = CollectAsync(device, _session, token);
            _ = ProcessLoopAsync(device, token);
            _ = TimerLoopAsync(_session, token);
            _ = LogLoopAsync(device, _session, token);
        }
    }

    private void RestartContextLoops()
    {
        if (_session != null && SelectedDevice is not null)
        {
            _contextCts?.Cancel();
            _contextCts?.Dispose();
            _contextCts = new CancellationTokenSource();
            CancellationToken token = _contextCts.Token;
            _lastSampleUtc = default;
            GpuCard.Clear(); DiskCard.Clear();
            _ = CollectAsync(SelectedDevice, _session, token);
            _ = ProcessLoopAsync(SelectedDevice, token);
            _ = TimerLoopAsync(_session, token);
            _ = LogLoopAsync(SelectedDevice, _session, token);
        }
    }

    private async Task CollectAsync(AndroidDevice device, MonitoringSession session, CancellationToken token)
    {
        List<MetricSample> batch = new List<MetricSample>(10);
        try
        {
            await foreach (MetricSample sample in _monitoring.StreamMetricsAsync(device, SelectedPackage, session.Id, token))
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }
                if (session.Id != _session?.Id || sample.DeviceSerial != device.Serial)
                {
                    break;
                }
                if (!IsLive)
                {
                    continue;
                }
                lock (session.SyncRoot)
                {
                    session.Samples.Add(sample);
                    while (session.Samples.Count > MonitoringConstants.InMemorySessionSampleLimit)
                    {
                        session.Samples.RemoveAt(0);
                    }
                    session.ActiveCollectionTime += MonitoringConstants.LightweightInterval;
                }
                _liveSamples.Add(sample);
                batch.Add(sample);
                _lastSampleUtc = sample.TimestampUtc;
                await Application.Current.Dispatcher.InvokeAsync(() => ApplySample(sample));
                if (batch.Count >= 10)
                {
                    await _sessions.AppendSamplesAsync(batch.ToArray(), token);
                    batch.Clear();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await Application.Current.Dispatcher.InvokeAsync(() => StatusMessage = "Collector error: " + ex.Message);
        }
        finally
        {
            if (batch.Count > 0)
            {
                try
                {
                    await _sessions.AppendSamplesAsync(batch, CancellationToken.None);
                }
                catch
                {
                }
            }
        }
    }

    private async Task ProcessLoopAsync(AndroidDevice device, CancellationToken token)
    {
        using PeriodicTimer timer = new PeriodicTimer(MonitoringConstants.ProcessInterval);
        try
        {
            do
            {
                await RefreshProcessesAsync(device, token);
            }
            while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RefreshProcessesAsync(AndroidDevice device, CancellationToken token)
    {
        IReadOnlyList<AndroidProcess> source = await _monitoring.GetProcessesAsync(device, token);
        if (token.IsCancellationRequested || device.Serial != SelectedDevice?.Serial)
        {
            return;
        }
        List<AndroidProcess> ordered = source.OrderByDescending(x => x.CpuPercent ?? -1.0).ThenBy(x => GroupRank(x.Group)).ToList();
        string? foreground = IsDemo ? ordered.FirstOrDefault(p => p.PackageName != null)?.PackageName : null;
        if (!IsDemo)
        {
            AdbCommandResult activities = await _adb.ExecuteAsync(device.Serial, ["shell", "dumpsys", "activity", "activities"], TimeSpan.FromSeconds(10), token);
            if (activities.Success)
            {
                foreground = ForegroundParser.Parse(activities.StandardOutput);
            }
        }
        string[]? installed = null;
        if (_installedPackagesSerial != device.Serial)
        {
            if (IsDemo) installed = ordered
                .Where(p => p.Group == ProcessGroup.Apps || p.PackageName is "com.android.chrome" or "com.android.vending")
                .Select(p => p.PackageName).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            else
            {
                var packages = await _adb.ExecuteAsync(device.Serial, new[] { "shell", "cmd", "package", "query-activities", "--brief", "--components", "--user", "current", "-a", "android.intent.action.MAIN", "-c", "android.intent.category.LAUNCHER" }, TimeSpan.FromSeconds(10), token);
                if (packages.Success) installed = packages.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(p => Regex.IsMatch(p, @"^[A-Za-z_][A-Za-z0-9_.]*/[A-Za-z0-9_.$]+$", RegexOptions.CultureInvariant))
                    .Select(p => p[..p.IndexOf('/')]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            }
        }
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (token.IsCancellationRequested || device.Serial != SelectedDevice?.Serial) return;
            if (installed is not null)
            {
                BackgroundPackages.Clear();
                BackgroundApplications.Clear();
                foreach (var package in installed)
                {
                    BackgroundPackages.Add(package);
                    BackgroundApplications.Add(CreateApplicationChoice(package));
                }
                _installedPackagesSerial = device.Serial;
            }
            if (IsLive) UpdateTracking(ordered, foreground);
            _allProcesses = ordered;
            _processSnapshotCpuTotal = ordered.Any(process => process.CpuPercent.HasValue)
                ? Math.Clamp(ordered.Sum(process => process.CpuPercent.GetValueOrDefault()), 0.0, 100.0)
                : null;
            if (!IsProcessTablePaused)
            {
                ApplyProcessSnapshot(ordered);
                ApplyProcessFilter();
            }
            EvaluateSelectedProcessAlert(ordered);
            foreach (string package in ordered.Select(x => x.PackageName).OfType<string>().Distinct())
            {
                if (!TrackedPackages.Contains(package))
                {
                    TrackedPackages.Add(package);
                }
            }
            if (_packageAutoSelectedForSerial != device.Serial && !string.IsNullOrWhiteSpace(foreground) && TrackedPackages.Contains(foreground))
            {
                _packageAutoSelectedForSerial = device.Serial;
                SelectedPackage = foreground;
            }
        }, DispatcherPriority.Background);
    }

    private async Task TimerLoopAsync(MonitoringSession session, CancellationToken token)
    {
        using PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    SessionTime = (DateTimeOffset.UtcNow - session.StartedUtc).ToString(@"hh\:mm\:ss");
                    bool waitingForFirstSample = _lastSampleUtc == default;
                    double ageSeconds = waitingForFirstSample
                        ? (DateTimeOffset.UtcNow - session.StartedUtc).TotalSeconds
                        : (DateTimeOffset.UtcNow - _lastSampleUtc).TotalSeconds;
                    if (!IsLive)
                    {
                        DataAge = "Paused";
                        EvaluateAlertCondition("StaleData", false, "");
                        return;
                    }
                    DataAge = waitingForFirstSample ? "Waiting for data" : $"Data age {ageSeconds:N0}s";
                    double staleThreshold = RuleThreshold("StaleData", 10);
                    EvaluateAlertCondition(
                        "StaleData",
                        waitingForFirstSample && ageSeconds <= staleThreshold ? null : ageSeconds > staleThreshold,
                        $"No new performance sample for more than {staleThreshold:N0} seconds.");
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ApplyProcessFilter()
    {
        ApplyProcessRowSnapshot(BuildProcessRows(FilterProcesses(_frozenProcesses ?? _allProcesses)));
    }

    private IReadOnlyList<AndroidProcess> FilterProcesses(IReadOnlyList<AndroidProcess> source)
    {
        string search = SearchText.Trim();
        if (search.Length != 0)
        {
            return source.Where(process =>
                process.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                FriendlyProcessName(process.Name).Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (process.PackageName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                process.Pid.ToString().Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        return source;
    }

    private void ApplyProcessSnapshot(IReadOnlyList<AndroidProcess> snapshot)
    {
        Dictionary<int, AndroidProcess> incoming = snapshot.ToDictionary(x => x.Pid);
        for (int index = Processes.Count - 1; index >= 0; index--)
        {
            AndroidProcess existing = Processes[index];
            if (!incoming.TryGetValue(existing.Pid, out AndroidProcess? replacement) || !SameIdentity(existing, replacement))
            {
                Processes.RemoveAt(index);
            }
        }
        Dictionary<int, AndroidProcess> current = Processes.ToDictionary(x => x.Pid);
        foreach (AndroidProcess process in snapshot)
        {
            if (!current.TryGetValue(process.Pid, out AndroidProcess? previous))
            {
                Processes.Add(process);
                continue;
            }
            AndroidProcess merged = process with
            {
                CpuPercent = process.CpuPercent ?? previous.CpuPercent,
                DiskReadBytesPerSecond = process.DiskReadBytesPerSecond ?? previous.DiskReadBytesPerSecond,
                DiskWriteBytesPerSecond = process.DiskWriteBytesPerSecond ?? previous.DiskWriteBytesPerSecond,
                NetworkRxBytesPerSecond = process.NetworkRxBytesPerSecond ?? previous.NetworkRxBytesPerSecond,
                NetworkTxBytesPerSecond = process.NetworkTxBytesPerSecond ?? previous.NetworkTxBytesPerSecond,
                Fps = process.Fps ?? previous.Fps
            };
            if (!MateriallyEqual(previous, merged))
            {
                Processes[Processes.IndexOf(previous)] = merged;
            }
        }
        for (int targetIndex = 0; targetIndex < snapshot.Count; targetIndex++)
        {
            int pid = snapshot[targetIndex].Pid;
            int currentIndex = Processes.Select((process, index) => (process, index)).FirstOrDefault(entry => entry.process.Pid == pid).index;
            if (currentIndex != targetIndex)
            {
                Processes.Move(currentIndex, targetIndex);
            }
        }
    }

    private IReadOnlyList<ProcessDisplayRow> BuildProcessRows(IReadOnlyList<AndroidProcess> source)
    {
        bool searching = !string.IsNullOrWhiteSpace(SearchText);
        AndroidProcess? unattributed = source.FirstOrDefault(process => process.Pid == 0);
        var groups = source.Where(process => process.Pid != 0)
            .GroupBy(ProcessGroupKey, StringComparer.Ordinal)
            .Select(group => new { group.Key, Items = group.ToArray() })
            .OrderBy(group => group.Items.Min(process => GroupRank(process.Group)))
            .ThenBy(group => group.Items.Sum(process => process.CpuPercent.GetValueOrDefault()) > 0.05 ? 0 : 1)
            .ThenByDescending(group => group.Items.Sum(process => process.CpuPercent.GetValueOrDefault()))
            .ThenBy(group => FriendlyGroupName(group.Key, group.Items), StringComparer.OrdinalIgnoreCase);
        List<ProcessDisplayRow> rows = new List<ProcessDisplayRow>();
        if (unattributed != null)
        {
            rows.Add(ToLeafRow(unattributed, "leaf:kernel-unattributed", "Kernel / unattributed CPU", isChild: false));
        }
        foreach (var group in groups)
        {
            AndroidProcess[] items = group.Items.OrderByDescending(process => process.CpuPercent ?? -1.0).ThenBy(process => process.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            string name = FriendlyGroupName(group.Key, items);
            if (items.Length == 1)
            {
                AndroidProcess single = items[0];
                rows.Add(ToLeafRow(single, $"leaf:{single.Pid}:{single.StartTicks}", name, isChild: false));
                continue;
            }
            string? package = items.Select(process => NormalizePackage(process.PackageName)).FirstOrDefault(value => value != null);
            string groupKey = "group:" + group.Key;
            bool expanded = searching || _expandedProcessGroups.Contains(groupKey);
            rows.Add(new ProcessDisplayRow(
                groupKey,
                items.OrderBy(process => GroupRank(process.Group)).First().Group,
                name,
                package ?? $"{items.Length} Android tasks",
                null,
                expanded ? $"{items.Length} tasks shown" : $"{items.Length} tasks",
                expanded ? null : Sum(items.Select(process => process.CpuPercent)),
                expanded ? null : Sum(items.Select(process => process.RssBytes)),
                expanded ? null : Sum(items.Select(process => process.GpuPercent)),
                expanded ? null : Sum(items.Select(process => process.DiskReadBytesPerSecond)),
                expanded ? null : Sum(items.Select(process => process.DiskWriteBytesPerSecond)),
                expanded ? null : Sum(items.Select(process => process.NetworkRxBytesPerSecond)),
                expanded ? null : Sum(items.Select(process => process.NetworkTxBytesPerSecond)),
                expanded ? null : items.Where(process => process.Fps.HasValue).Select(process => process.Fps).FirstOrDefault(),
                "N/A",
                "N/A",
                CanExpand: true,
                expanded,
                IsChild: false,
                package));
            if (expanded)
            {
                foreach (AndroidProcess child in items)
                {
                    rows.Add(ToLeafRow(child, $"child:{group.Key}:{child.Pid}:{child.StartTicks}", DescribeTask(child), isChild: true, parentKey: groupKey));
                }
            }
        }
        return rows;
    }

    private static ProcessDisplayRow ToLeafRow(AndroidProcess process, string key, string name, bool isChild, string? parentKey = null)
    {
        return new ProcessDisplayRow(key, process.Group, name, process.PackageName ?? process.CommandLine, process.Pid, process.Status, process.CpuPercent, process.RssBytes, process.GpuPercent, process.DiskReadBytesPerSecond, process.DiskWriteBytesPerSecond, process.NetworkRxBytesPerSecond, process.NetworkTxBytesPerSecond, process.Fps, process.BatteryImpact ?? "N/A", process.ThermalRelation ?? "N/A", CanExpand: false, IsExpanded: false, isChild, process.PackageName, parentKey);
    }

    private void ApplyProcessRowSnapshot(IReadOnlyList<ProcessDisplayRow> snapshot)
    {
        Dictionary<string, ProcessDisplayRow> incoming = snapshot.ToDictionary(row => row.Key, StringComparer.Ordinal);
        for (int index = ProcessRows.Count - 1; index >= 0; index--)
        {
            if (!incoming.ContainsKey(ProcessRows[index].Key))
            {
                ProcessRows.RemoveAt(index);
            }
        }
        Dictionary<string, ProcessDisplayRow> current = ProcessRows.ToDictionary(row => row.Key, StringComparer.Ordinal);
        foreach (ProcessDisplayRow row in snapshot)
        {
            if (current.TryGetValue(row.Key, out ProcessDisplayRow? existing))
            {
                existing.UpdateFrom(row);
            }
            else
            {
                ProcessRows.Add(row);
            }
        }
        for (int targetIndex = 0; targetIndex < snapshot.Count; targetIndex++)
        {
            int currentIndex = -1;
            for (int index = targetIndex; index < ProcessRows.Count; index++)
            {
                if (ProcessRows[index].Key == snapshot[targetIndex].Key)
                {
                    currentIndex = index;
                    break;
                }
            }
            if (currentIndex >= 0 && currentIndex != targetIndex)
            {
                ProcessRows.Move(currentIndex, targetIndex);
            }
        }
    }

    private static string ProcessGroupKey(AndroidProcess process)
    {
        if (NormalizePackage(process.PackageName) is { } package)
        {
            return "package:" + package;
        }
        string name = process.Name.Trim();
        if (name.StartsWith("[kworker", StringComparison.OrdinalIgnoreCase) || name.StartsWith("[rcu", StringComparison.OrdinalIgnoreCase) || name.StartsWith("[migration", StringComparison.OrdinalIgnoreCase))
        {
            return "system:kernel-workers";
        }
        if (name.StartsWith("android.hardware.", StringComparison.OrdinalIgnoreCase) || process.CommandLine.Contains("android.hardware.", StringComparison.OrdinalIgnoreCase))
        {
            return "system:hardware-services";
        }
        switch (name)
        {
            case "logd" or "logcat" or "statsd":
                return "system:logging";
            case "adbd":
                return "system:adb";
            case "system_server" or "servicemanager" or "hwservicemanager" or "vndservicemanager":
                return "system:android-core";
        }
        if (name.StartsWith("zygote", StringComparison.OrdinalIgnoreCase) || name.StartsWith("app_process", StringComparison.OrdinalIgnoreCase))
        {
            return "system:android-runtime";
        }
        return "process:" + name;
    }

    private string FriendlyGroupName(string key, IReadOnlyList<AndroidProcess> items)
    {
        if (key.StartsWith("package:", StringComparison.Ordinal))
        {
            return FriendlyPackageName(key["package:".Length..]);
        }
        return key switch
        {
            "system:kernel-workers" => "Kernel workers",
            "system:hardware-services" => "Android hardware services",
            "system:logging" => "Android logging",
            "system:adb" => "Android Debug Bridge",
            "system:android-core" => "Android system services",
            "system:android-runtime" => "Android runtime",
            _ => FriendlyProcessName(items[0].Name),
        };
    }

    private string FriendlyPackageName(string package)
    {
        bool isGoogle = string.Equals(SelectedDevice?.Manufacturer, "Google", StringComparison.OrdinalIgnoreCase);
        return package switch
        {
            "com.android.systemui" => isGoogle ? "Google System UI" : "Android System UI",
            "com.android.phone" => "Phone services",
            "com.android.settings" => "Android Settings",
            "com.android.providers.media.module" => "Media Storage",
            "com.android.providers.media" => "Media Storage",
            "com.android.inputmethod.latin" => "Android Keyboard",
            "com.android.vending" => "Google Play Store",
            "com.google.android.gms" => "Google Play services",
            "com.google.android.gsf" or "com.google.process.gservices" => "Google Services Framework",
            "com.google.android.apps.nexuslauncher" => "Pixel Launcher",
            "com.google.android.googlequicksearchbox" => "Google Search",
            "com.google.android.apps.messaging" => "Google Messages",
            "com.google.android.apps.photos" => "Google Photos",
            "com.google.android.youtube" => "YouTube",
            "com.google.android.apps.youtube.music" => "YouTube Music",
            "com.google.android.inputmethod.latin" => "Gboard",
            "com.google.android.configupdater" => "Google Config Updater",
            "com.google.android.projection.gearhead" => "Android Auto",
            "com.google.android.providers.media.module" => "Media Provider",
            "com.google.android.apps.restore" => "Google Restore",
            "com.google.process.gapps" => "Google Apps Services",
            "com.google.pixel.exo" => "Pixel system service",
            "com.google.android.ext.services" => "Google system services",
            "com.google.android.ext.shared" => "Google shared services",
            "com.google.android.permissioncontroller" => "Permission Controller",
            "com.google.android.settings.intelligence" => "Settings Intelligence",
            "android.process.acore" => "Android contacts services",
            "android.process.media" => "Android media services",
            "io.appium.settings" => "Appium Settings",
            _ => FriendlyPackageFallback(package)
        };
    }

    private static string FriendlyPackageFallback(string package)
    {
        string[] segments = package.Split('.', StringSplitOptions.RemoveEmptyEntries);
        string segment = segments.Reverse().FirstOrDefault(value => value is not ("service" or "services" or "provider" or "providers" or "app" or "apps"))
            ?? segments.LastOrDefault()
            ?? package;
        string name = Regex.Replace(segment.Replace('_', ' ').Replace('-', ' '), "([a-z])([A-Z])", "$1 $2");
        name = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name);
        if (package.StartsWith("com.google.", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("Google", StringComparison.OrdinalIgnoreCase))
        {
            return "Google " + name;
        }
        if (package.StartsWith("com.android.", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("Android", StringComparison.OrdinalIgnoreCase))
        {
            return "Android " + name;
        }
        return name;
    }

    private static string FriendlyProcessName(string name)
    {
        name = name.Trim();
        string task = name.Trim('[', ']').ToLowerInvariant();
        string? description = task switch
        {
            _ when task.StartsWith("jbd2/", StringComparison.Ordinal) => "Disk journal",
            _ when task.StartsWith("irq/", StringComparison.Ordinal) => "Hardware interrupt handler",
            _ when task.StartsWith("idle_inject/", StringComparison.Ordinal) => "CPU idle control",
            _ when task.StartsWith("kworker/", StringComparison.Ordinal) => "System background worker",
            _ when task.StartsWith("rcu", StringComparison.Ordinal) => "Kernel synchronization",
            _ when task.StartsWith("migration/", StringComparison.Ordinal) => "CPU task balancing",
            _ when task.StartsWith("ksoftirqd/", StringComparison.Ordinal) => "Deferred interrupt handler",
            _ when task.StartsWith("kswapd", StringComparison.Ordinal) => "Memory reclaim",
            _ when task.StartsWith("kcompactd", StringComparison.Ordinal) => "Memory compaction",
            _ when task.StartsWith("cpuhp/", StringComparison.Ordinal) => "CPU availability manager",
            "hwrng" => "Hardware random number generator",
            "kthreadd" => "Kernel task manager",
            "kauditd" => "Security event logging",
            "khungtaskd" => "Unresponsive task monitor",
            "khugepaged" => "Large memory page manager",
            "oom_reaper" => "Low memory cleanup",
            "watchdogd" => "System health watchdog",
            "acpi_thermal_pm" => "Hardware temperature management",
            "dmabuf-deferred-free-worker" => "Shared buffer cleanup",
            "pool_workqueue_release" => "System worker cleanup",
            "kernel" => "Kernel and interrupts",
            "init" => "Android startup manager",
            "surfaceflinger" => "Android display compositor",
            "audioserver" => "Android audio service",
            "cameraserver" => "Android camera service",
            "netd" => "Android network service",
            "iptables-restore" => "Network firewall rules (IPv4)",
            "ip6tables-restore" => "Network firewall rules (IPv6)",
            "installd" => "App installer",
            "incidentd" => "System diagnostic reports",
            "storaged" => "Storage monitoring",
            "vold" => "Storage manager",
            "ueventd" => "Device event manager",
            "gatekeeperd" => "Device unlock verification",
            "lmkd" => "Low memory manager",
            "artd" => "App code optimization",
            "keystore2" => "Encryption key storage",
            "credstore" => "Credential storage",
            "drmserver" => "Protected media service",
            "mediaserver" => "Media playback service",
            "media.extractor" => "Media file reader",
            "media.metrics" => "Media playback statistics",
            "media.swcodec" => "Software media processing",
            "gpuservice" => "Graphics service",
            "tombstoned" => "Crash report service",
            "traced" => "Performance tracing service",
            "traced_probes" => "Performance trace collector",
            "wificond" => "Wi-Fi control service",
            "wpa_supplicant" => "Wi-Fi authentication",
            "mdnsd" => "Local network discovery",
            "prng_seeder" => "Random number initialization",
            "bt_vhci_forwarder" => "Emulator Bluetooth service",
            "qemu-props" => "Emulator settings service",
            "adbd" => "Android debugging service",
            "system_server" => "Core Android services",
            "servicemanager" => "Android service manager",
            "hwservicemanager" => "Hardware service manager",
            "vndservicemanager" => "Vendor service manager",
            "logd" => "System log service",
            "logcat" => "System log reader",
            "statsd" => "System statistics service",
            "zygote" or "zygote64" => "Application process launcher",
            "webview_zygote" => "Web content process launcher",
            "ps" => "Process list reader",
            "sh" => "Command shell",
            _ => null
        };
        if (description != null)
        {
            return description;
        }
        if (name.StartsWith("android.system.suspend", StringComparison.OrdinalIgnoreCase))
        {
            return "System suspend service";
        }
        if (name.StartsWith("android.hidl.allocator@", StringComparison.OrdinalIgnoreCase))
        {
            return "Android memory allocator";
        }
        if (name.StartsWith("android.hardware.", StringComparison.OrdinalIgnoreCase))
        {
            return "Android hardware service";
        }
        return name.StartsWith('[') ? "Kernel background task" : FriendlyPackageFallback(name);
    }

    private static string DescribeTask(AndroidProcess process)
    {
        if (process.PackageName == null)
        {
            return FriendlyProcessName(process.Name);
        }
        if (process.Name.Contains("persistent", StringComparison.OrdinalIgnoreCase))
        {
            return "Persistent background service";
        }
        if (process.Name.Contains("unstable", StringComparison.OrdinalIgnoreCase))
        {
            return "Isolated service process";
        }
        if (process.Name.EndsWith(":interactor", StringComparison.OrdinalIgnoreCase))
        {
            return "Assistant interaction service";
        }
        if (process.Name.EndsWith(":search", StringComparison.OrdinalIgnoreCase))
        {
            return "Search process";
        }
        if (process.Name.Contains(':'))
        {
            return FriendlyPackageFallback(process.Name[(process.Name.LastIndexOf(':') + 1)..]) + " service";
        }
        if (process.PackageName != null && string.Equals(NormalizePackage(process.PackageName), process.PackageName, StringComparison.Ordinal) && string.Equals(process.Name, process.PackageName, StringComparison.Ordinal))
        {
            return "Main application process";
        }
        return FriendlyProcessName(process.Name);
    }

    private static string? NormalizePackage(string? package)
    {
        if (string.IsNullOrWhiteSpace(package))
        {
            return null;
        }
        if (package.StartsWith("com.google.android.gms.", StringComparison.Ordinal))
        {
            return "com.google.android.gms";
        }
        if (package.StartsWith("com.android.systemui.", StringComparison.Ordinal))
        {
            return "com.android.systemui";
        }
        return package;
    }

    private static double? Sum(IEnumerable<double?> values)
    {
        double[] present = values.OfType<double>().ToArray();
        return present.Length != 0 ? present.Sum() : null;
    }

    private static long? Sum(IEnumerable<long?> values)
    {
        long[] present = values.OfType<long>().ToArray();
        return present.Length != 0 ? present.Sum() : null;
    }

    private static int GroupRank(ProcessGroup group) => group switch
    {
        ProcessGroup.System => 0,
        ProcessGroup.Apps => 1,
        ProcessGroup.Background => 2,
        _ => 3
    };

    /// <summary>A PID is only the same process if its name and, when known, its start time also match; PIDs get reused.</summary>
    private static bool SameIdentity(AndroidProcess current, AndroidProcess next) =>
        current.Pid == next.Pid && current.Name == next.Name &&
        (!current.StartTicks.HasValue || !next.StartTicks.HasValue || current.StartTicks == next.StartTicks);

    /// <summary>Ignores RSS changes under 128 KB so the table does not redraw rows for measurement noise.</summary>
    private static bool MateriallyEqual(AndroidProcess current, AndroidProcess next) =>
        current.Name == next.Name &&
        current.PackageName == next.PackageName &&
        current.Status == next.Status &&
        current.Group == next.Group &&
        current.CpuPercent == next.CpuPercent &&
        Math.Abs(current.RssBytes.GetValueOrDefault() - next.RssBytes.GetValueOrDefault()) < 128 * 1024 &&
        current.DiskReadBytesPerSecond == next.DiskReadBytesPerSecond &&
        current.DiskWriteBytesPerSecond == next.DiskWriteBytesPerSecond &&
        current.NetworkRxBytesPerSecond == next.NetworkRxBytesPerSecond &&
        current.NetworkTxBytesPerSecond == next.NetworkTxBytesPerSecond &&
        current.Fps == next.Fps;

    private void ApplySample(MetricSample s)
    {
        if (s.SessionId != _session?.Id)
        {
            return;
        }
        LiveSamples.Add(s);
        while (LiveSamples.Count > 600)
        {
            LiveSamples.RemoveAt(0);
        }
        double? deviceCpu = _processSnapshotCpuTotal ?? s.DeviceCpuPercent;
        if (deviceCpu.HasValue)
        {
            deviceCpu = Math.Clamp(deviceCpu.Value, 0.0, 100.0);
        }
        string cpuSecondary = s.ProcessCpuPercent.HasValue ? $"{SelectedPackage}: {s.ProcessCpuPercent:N1}%" : SelectedPackage + ": N/A";
        CpuCard.Push(deviceCpu, deviceCpu.HasValue ? $"{deviceCpu:N1}%" : "N/A", cpuSecondary, s.Source + "; same CPU window as the process table; 0% = idle, 100% = fully busy");
        long? totalMemory = SelectedDevice?.TotalMemoryBytes;
        long? usedMemory = s.DeviceMemoryUsedBytes;
        double? usedMemoryPercent = usedMemory.HasValue && totalMemory > 0 ? usedMemory.Value * 100.0 / totalMemory.Value : null;
        string memoryText = !usedMemory.HasValue ? "N/A" : !totalMemory.HasValue ? FormatBytes(usedMemory) : FormatBytes(usedMemory) + " / " + FormatBytes(totalMemory);
        string memorySecondary = s.ProcessPssBytes.HasValue ? SelectedPackage + " PSS: " + FormatBytes(s.ProcessPssBytes) : SelectedPackage + " RSS: " + FormatBytes(s.ProcessRssBytes);
        MemoryCard.Push(usedMemoryPercent, memoryText, memorySecondary, s.Source);
        GpuCard.Push(s.GpuPercent, s.GpuPercent.HasValue ? $"{s.GpuPercent:N1}%" : s.GpuAvailability == Availability.Waiting ? "Waiting" : "N/A",
            s.GpuSource ?? (IsDemo ? "Demo GPU" : "Unsupported on this target"), s.GpuSource ?? s.Source);
        var deviceDisk = s.DeviceDiskSource is not null;
        if (DiskCard.Title.StartsWith("Device", StringComparison.Ordinal) != deviceDisk) DiskChart.Clear();
        DiskCard.Push(deviceDisk ? s.DeviceDiskWriteBytesPerSecond : s.DiskWriteBytesPerSecond,
            deviceDisk ? s.DeviceDiskReadBytesPerSecond : s.DiskReadBytesPerSecond, s.DeviceDiskSource);
        CpuChart.Push(s.TimestampUtc, deviceCpu, s.ProcessCpuPercent);
        DeviceMemoryChart.Push(s.TimestampUtc, s.DeviceMemoryUsedBytes, s.DeviceMemoryAvailableBytes);
        AppMemoryChart.Push(s.TimestampUtc, s.ProcessRssBytes, s.ProcessPssBytes, preserveMissing: false);
        FpsChart.Push(s.TimestampUtc, s.Fps, null, preserveMissing: false);
        FrameTimeChart.Push(s.TimestampUtc, s.FrameTimeP95Ms, null, preserveMissing: false);
        JankChart.Push(s.TimestampUtc, s.JankPercent, null, preserveMissing: false);
        DiskChart.Push(s.TimestampUtc, deviceDisk ? s.DeviceDiskReadBytesPerSecond : s.DiskReadBytesPerSecond,
            deviceDisk ? s.DeviceDiskWriteBytesPerSecond : s.DiskWriteBytesPerSecond);
        DiskChart.CurrentText = (deviceDisk ? "Device · " : "App · ") + DiskChart.CurrentText;
        NetworkChart.Push(s.TimestampUtc, s.NetworkRxBytesPerSecond, s.NetworkTxBytesPerSecond);
        AppNetworkChart.Push(s.TimestampUtc, s.AppNetworkRxBytesPerSecond, s.AppNetworkTxBytesPerSecond);
        ThermalChart.Push(s.TimestampUtc, s.TemperatureCelsius, null, preserveMissing: false);
        GpuChart.Push(s.TimestampUtc, s.GpuPercent, null, preserveMissing: false);
        if (s.ActiveNetworkInterface != null || s.IpAddress != null || s.NetworkType != null)
        {
            NetworkContext = $"{s.NetworkType ?? "Unknown"} · {s.ActiveNetworkInterface ?? "N/A"} · {s.IpAddress ?? "N/A"}";
        }
        if (_lastNetworkTotalTimestamp != default)
        {
            double seconds = Math.Clamp((s.TimestampUtc - _lastNetworkTotalTimestamp).TotalSeconds, 0.0, 10.0);
            _sessionNetworkRxBytes += Math.Max(0.0, s.NetworkRxBytesPerSecond.GetValueOrDefault()) * seconds;
            _sessionNetworkTxBytes += Math.Max(0.0, s.NetworkTxBytesPerSecond.GetValueOrDefault()) * seconds;
            NetworkTotals = "Session RX " + FormatBytes((long)_sessionNetworkRxBytes) + " · TX " + FormatBytes((long)_sessionNetworkTxBytes);
        }
        _lastNetworkTotalTimestamp = s.TimestampUtc;
        if (s.DeviceStorageTotalBytes is long storageTotal && s.DeviceStorageAvailableBytes is long storageFree)
        {
            long storageUsed = Math.Max(0L, storageTotal - storageFree);
            double usedPercent = storageTotal > 0 ? storageUsed * 100.0 / storageTotal : 0.0;
            StorageSummary = $"Total {FormatBytes(storageTotal)} · Used {FormatBytes(storageUsed)} · Free {FormatBytes(storageFree)} · {usedPercent:N1}%";
            double storageThresholdBytes = RuleThreshold("LowStorage", 1) * 1024 * 1024 * 1024;
            EvaluateAlertCondition(
                "LowStorage",
                storageFree < storageThresholdBytes,
                "Only " + FormatBytes(storageFree) + " of storage remains.");
        }
        if (s.PackageCodeBytes.HasValue || s.PackageDataBytes.HasValue || s.PackageCacheBytes.HasValue)
        {
            AppStorageSummary = $"Code {FormatBytes(s.PackageCodeBytes)} · Data {FormatBytes(s.PackageDataBytes)} · Cache {FormatBytes(s.PackageCacheBytes)}";
        }
        if (s.TemperatureCelsius.HasValue || s.BatteryPercent.HasValue || s.ThermalSeverity != null)
        {
            string charging = s.IsCharging switch { true => "Charging", false => "Not charging", null => "Charging N/A" };
            ThermalSummary = $"{(s.TemperatureCelsius.HasValue ? $"{s.TemperatureCelsius:N1} °C" : "Temperature N/A")} · {s.ThermalSeverity ?? "Severity N/A"} · Battery {(s.BatteryPercent.HasValue ? $"{s.BatteryPercent}%" : "N/A")} · {charging}";
        }
        int trackedIndex = _allProcesses.FindIndex(p => p.PackageName == s.PackageName);
        if (trackedIndex >= 0)
        {
            AndroidProcess current = _allProcesses[trackedIndex];
            _allProcesses[trackedIndex] = current with
            {
                RssBytes = s.ProcessRssBytes ?? current.RssBytes,
                DiskReadBytesPerSecond = s.DiskReadBytesPerSecond,
                DiskWriteBytesPerSecond = s.DiskWriteBytesPerSecond,
                NetworkRxBytesPerSecond = s.AppNetworkRxBytesPerSecond,
                NetworkTxBytesPerSecond = s.AppNetworkTxBytesPerSecond,
                Fps = s.Fps
            };
            if (!IsProcessTablePaused)
            {
                AndroidProcess? visible = Processes.FirstOrDefault(p => p.Pid == current.Pid);
                if (visible is not null)
                {
                    Processes[Processes.IndexOf(visible)] = _allProcesses[trackedIndex];
                }
                ApplyProcessFilter();
            }
        }
        EvaluateAlertCondition(
            "HighCpu",
            s.DeviceCpuPercent.HasValue
                ? s.DeviceCpuPercent.Value > RuleThreshold("HighCpu", 85)
                : null,
            $"Device CPU is {s.DeviceCpuPercent:N1}%.");
        EvaluateAlertCondition(
            "LowFps",
            s.Fps.HasValue ? s.Fps.Value < RuleThreshold("LowFps", 30) : null,
            $"Tracked package FPS is {s.Fps:N1}.");
        EvaluateAlertCondition(
            "FrameTime",
            s.FrameTimeP95Ms.HasValue
                ? s.FrameTimeP95Ms.Value > RuleThreshold("FrameTime", 33.3)
                : null,
            $"P95 frame time is {s.FrameTimeP95Ms:N1} ms.");
        EvaluateAlertCondition(
            "Jank",
            s.JankPercent.HasValue
                ? s.JankPercent.Value > RuleThreshold("Jank", 20)
                : null,
            $"Jank is {s.JankPercent:N1}%.");
        double? availableMemoryPercent =
            s.DeviceMemoryAvailableBytes.HasValue && totalMemory > 0
                ? s.DeviceMemoryAvailableBytes.Value * 100.0 / totalMemory.Value
                : null;
        EvaluateAlertCondition(
            "LowMemory",
            availableMemoryPercent.HasValue
                ? availableMemoryPercent.Value < RuleThreshold("LowMemory", 10)
                : null,
            $"Available RAM is {FormatBytes(s.DeviceMemoryAvailableBytes)} ({availableMemoryPercent:N1}%).");
        EvaluateAlertCondition(
            "HighTemperature",
            s.TemperatureCelsius.HasValue
                ? s.TemperatureCelsius.Value > RuleThreshold("HighTemperature", 45)
                : null,
            $"Battery temperature is {s.TemperatureCelsius:N1} °C.",
            "Critical");

        bool severeThermal = s.ThermalSeverity is
            "Severe" or "Critical" or "Emergency" or "Shutdown";
        EvaluateAlertCondition(
            "ThermalSeverity",
            s.ThermalSeverity is null ? null : severeThermal,
            "Android thermal severity is " + s.ThermalSeverity + ".",
            "Critical");
        EvaluateExtendedSampleAlerts(s);
    }

    private async Task DiscoveryLoopAsync(CancellationToken token)
    {
        using PeriodicTimer timer = new PeriodicTimer(MonitoringConstants.DiscoveryInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                await RefreshDevicesAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task LogLoopAsync(AndroidDevice device, MonitoringSession session, CancellationToken token)
    {
        using PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            do
            {
                IEnumerable<string> source;
                AdbCommandResult? logcatResult = null;
                if (IsDemo)
                {
                    source = [$"07-20 12:00:{DateTimeOffset.Now.Second:00}.000 13432 13432 I MyGame: Render loop stable on {device.Serial}", $"07-20 12:00:{DateTimeOffset.Now.Second:00}.100 13432 13455 D Unity: frame submitted"];
                }
                else
                {
                    logcatResult = await _adb.ExecuteAsync(device.Serial, ["logcat", "-d", "-b", "main,system,crash,events,radio", "-v", "threadtime", "-t", "100"], TimeSpan.FromSeconds(10), token);
                    source = logcatResult.Success ? logcatResult.StandardOutput.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries) : [];
                }
                string[] newLines = source.Where(line => seen.Add(line)).TakeLast(30).ToArray();
                if (seen.Count > 2000)
                {
                    seen = seen.TakeLast(1000).ToHashSet(StringComparer.Ordinal);
                }
                LogEntry[] entries = newLines.Select(line => ParseLogcatEntry(line, device.Serial)).ToArray();
                IReadOnlyList<LogEntry> applicationEntries =
                    await ReadApplicationDiagnosticsAsync(device.Serial, token);
                bool crashDetected = newLines.Any(line => line.Contains("FATAL EXCEPTION", StringComparison.OrdinalIgnoreCase) || line.Contains("AndroidRuntime", StringComparison.OrdinalIgnoreCase));
                bool anrDetected = newLines.Any(line => line.Contains("ANR in", StringComparison.OrdinalIgnoreCase));
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (logcatResult is not null)
                        TrackAdbCommand(logcatResult);
                    EvaluateAlertCondition(
                        "LogcatStopped",
                        logcatResult is not null && !logcatResult.Success,
                        logcatResult?.TimedOut == true
                            ? "logcat collection timed out."
                            : "logcat collection stopped.");
                    if (!IsLogPaused)
                    {
                        foreach (LogEntry entry in entries.Concat(applicationEntries))
                        {
                            Logs.Add(entry);
                        }
                    }
                    if (crashDetected)
                    {
                        RaiseAlert("Crash", "Critical", "Application crash detected in logcat.");
                    }
                    if (anrDetected)
                    {
                        RaiseAlert("ANR", "Critical", "Application-not-responding event detected in logcat.");
                    }
                    TrimLogs();
                }, DispatcherPriority.Background);
            }
            while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
                EvaluateAlertCondition("LogcatStopped", true, "logcat collection stopped: " + ex.Message));
        }
    }

    private bool FilterLog(object item)
    {
        if (item is not LogEntry logEntry)
        {
            return false;
        }
        if (SelectedLogPriority != "All" && !logEntry.Priority.Equals(SelectedLogPriority, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (SelectedLogSource != "All" && !logEntry.Source.Contains(SelectedLogSource, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        string search = LogSearchText.Trim();
        if (search.Length == 0)
        {
            return true;
        }
        string haystack = $"{logEntry.Source} {logEntry.Priority} {logEntry.PackageName} {logEntry.Pid} {logEntry.Message}";
        return LogRegexEnabled
            ? _logSearchRegex?.IsMatch(haystack) ?? false
            : haystack.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshLogFilter()
    {
        _logSearchRegex = null;
        if (LogRegexEnabled && !string.IsNullOrWhiteSpace(LogSearchText))
        {
            try
            {
                _logSearchRegex = new Regex(LogSearchText, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException ex)
            {
                LogFilterStatus = "Invalid regex: " + ex.Message;
                LogView.Refresh();
                return;
            }
        }
        LogView.Refresh();
        LogFilterStatus = $"{LogView.Cast<object>().Count()} visible · {Logs.Count} buffered";
    }

    private void TrimLogs()
    {
        while (Logs.Count > 500)
        {
            Logs.RemoveAt(0);
        }
        OnPropertyChanged(nameof(CollectorStatus));
        OnPropertyChanged(nameof(LastParserError));
    }

    private LogEntry ParseLogcatEntry(string line, string serial)
    {
        Match match = LogcatLineRegex.Match(line);
        if (!match.Success)
        {
            return new LogEntry(DateTimeOffset.UtcNow, IsDemo ? "Demo logcat" : "logcat", InferLogPriority(line), line, serial);
        }
        // logcat threadtime lines carry no year; assume the current one and step back when that lands in the future (e.g. December logs read in January).
        string stamp = $"{DateTime.Now.Year}-{match.Groups["month"].Value}-{match.Groups["day"].Value} {match.Groups["time"].Value}";
        string[] formats = ["yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss.ffffff"];
        DateTimeOffset timestamp = DateTime.TryParseExact(stamp, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime parsed) ? new DateTimeOffset(parsed) : DateTimeOffset.Now;
        if (timestamp > DateTimeOffset.Now.AddDays(2.0))
        {
            timestamp = timestamp.AddYears(-1);
        }
        int? pid = int.TryParse(match.Groups["pid"].Value, CultureInfo.InvariantCulture, out int parsedPid) ? parsedPid : null;
        string priority = match.Groups["priority"].Value switch
        {
            "E" or "F" or "A" => "Error",
            "W" => "Warning",
            "D" or "V" => "Debug",
            _ => "Info"
        };
        string? packageName = pid.HasValue ? _allProcesses.FirstOrDefault(process => process.Pid == pid.Value)?.PackageName : null;
        string message = match.Groups["tag"].Value.Trim() + ": " + match.Groups["message"].Value;
        return new LogEntry(timestamp.ToUniversalTime(), IsDemo ? "Demo logcat" : "logcat", priority, message, serial, pid, packageName);
    }

    private static string InferLogPriority(string line)
    {
        if (line.Contains(" E ", StringComparison.Ordinal) || line.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || line.Contains("FATAL", StringComparison.OrdinalIgnoreCase))
        {
            return "Error";
        }
        if (line.Contains(" W ", StringComparison.Ordinal) || line.Contains("WARN", StringComparison.OrdinalIgnoreCase))
        {
            return "Warning";
        }
        if (line.Contains(" D ", StringComparison.Ordinal) || line.Contains("DEBUG", StringComparison.OrdinalIgnoreCase))
        {
            return "Debug";
        }
        return "Info";
    }

    private void AddSessionEvent(string type, string message, string deviceSerial, string? packageName)
    {
        if (_session != null)
        {
            SessionEvent sessionEvent = new SessionEvent(Guid.NewGuid(), _session.Id, DateTimeOffset.UtcNow, type, message, deviceSerial, packageName);
            lock (_session.SyncRoot)
            {
                _session.Events.Add(sessionEvent);
            }
            _sessions.SaveEventAsync(sessionEvent, CancellationToken.None);
            Logs.Add(new LogEntry(sessionEvent.TimestampUtc, "Session", "Info", type + ": " + message, deviceSerial, null, packageName));
            TrimLogs();
        }
    }

    private void RaiseAlert(string type, string severity, string message)
    {
        AlertRule? rule = FindAlertRule(type);
        if (rule is not null)
        {
            if (!rule.Enabled && !rule.SystemCritical)
            {
                return;
            }
            severity = rule.Severity;
        }
        if (_session == null || !_activeAlertTypes.Add(type))
        {
            return;
        }
        DateTimeOffset utcNow = DateTimeOffset.UtcNow;
        SessionAlert sessionAlert = new SessionAlert(Guid.NewGuid(), _session.Id, utcNow, type, severity, message, Resolved: false);
        SessionMarker sessionMarker = new SessionMarker(Guid.NewGuid(), _session.Id, utcNow, utcNow.ToLocalTime(), utcNow - _session.StartedUtc, type, message, _session.Device.Serial, _session.Device.FriendlyName, _session.CurrentPackage);
        lock (_session.SyncRoot)
        {
            _session.Alerts.Add(sessionAlert);
            _session.Markers.Add(sessionMarker);
        }
        Alerts.Insert(0, sessionAlert);
        NotifyAlertRaised(sessionAlert);
        Markers.Add(sessionMarker);
        foreach (LiveChartViewModel performanceChart in PerformanceCharts)
        {
            performanceChart.AddAnnotation(utcNow, type, isAlert: true);
        }
        _sessions.SaveAlertAsync(sessionAlert, CancellationToken.None);
        _sessions.SaveMarkerAsync(sessionMarker, CancellationToken.None);
        string serial = _session.Device.Serial;
        string? currentPackage = _session.CurrentPackage;
        Logs.Add(new LogEntry(utcNow, "Android Dev Monitor", severity == "Critical" ? "Error" : "Warning", "Alert " + type + ": " + message, serial, null, currentPackage));
        TrimLogs();
        AddSessionEvent("AlertTriggered", type + ": " + message, serial, currentPackage);
        StatusMessage = severity + " alert · " + message;
    }

    private void ResolveAlert(string type)
    {
        if (!_activeAlertTypes.Remove(type))
        {
            return;
        }
        SessionAlert? active = Alerts.FirstOrDefault(x => x.Type == type && !x.Resolved);
        if (active is null)
        {
            return;
        }
        SessionAlert resolved = active with { Resolved = true };
        Alerts[Alerts.IndexOf(active)] = resolved;
        NotifyAlertResolved(active, resolved);
        if (_session != null)
        {
            lock (_session.SyncRoot)
            {
                int index = _session.Alerts.IndexOf(active);
                if (index >= 0)
                {
                    _session.Alerts[index] = resolved;
                }
            }
        }
        _sessions.SaveAlertAsync(resolved, CancellationToken.None);
    }

    private async Task ReloadStoredSessionsAsync(Guid? preferredSessionId = null)
    {
        Guid? selectedId = preferredSessionId ?? SelectedStoredSession?.Id;
        IReadOnlyList<SessionSummary> summaries = await _sessions.ListSessionSummariesAsync(CancellationToken.None);
        StoredSessions.Clear();
        foreach (SessionSummary summary in summaries)
        {
            StoredSessions.Add(summary);
        }
        SelectedStoredSession = selectedId.HasValue
            ? StoredSessions.FirstOrDefault(item => item.Id == selectedId.Value)
            : StoredSessions.FirstOrDefault();
        if (StoredSessions.Count == 0)
        {
            LoadedStoredSession = null;
            StoredSessionStatus = "No stored sessions";
            StoredSessionDetails = "A session is saved automatically when monitoring starts and whenever its state changes.";
            ClearStoredSessionView();
        }
        OnPropertyChanged(nameof(SessionStorageSummary));
    }

    private async Task LoadStoredSessionAsync(SessionSummary? summary)
    {
        _storedSessionCts?.Cancel();
        _storedSessionCts?.Dispose();
        _storedSessionCts = new CancellationTokenSource();
        CancellationToken token = _storedSessionCts.Token;
        ClearStoredSessionView();
        LoadedStoredSession = null;
        if (summary is null)
        {
            StoredSessionStatus = "Select a stored session";
            return;
        }
        StoredSessionStatus = $"Loading session {summary.StartedUtc.ToLocalTime():g}…";
        try
        {
            MonitoringSession? monitoringSession = await _sessions.LoadSessionAsync(summary.Id, token);
            token.ThrowIfCancellationRequested();
            if (monitoringSession == null)
            {
                StoredSessionStatus = "Session data is missing";
                return;
            }
            LoadedStoredSession = monitoringSession;
            foreach (MetricSample item in Downsample(monitoringSession.Samples, 1800))
            {
                token.ThrowIfCancellationRequested();
                StoredCpuChart.Push(item.TimestampUtc, item.DeviceCpuPercent, item.ProcessCpuPercent);
                StoredMemoryChart.Push(item.TimestampUtc, item.DeviceMemoryUsedBytes, item.ProcessPssBytes ?? item.ProcessRssBytes);
                StoredFpsFrameChart.Push(item.TimestampUtc, item.Fps, item.FrameTimeP95Ms);
                StoredDiskChart.Push(item.TimestampUtc,
                    item.DeviceDiskSource is null ? item.DiskReadBytesPerSecond : item.DeviceDiskReadBytesPerSecond,
                    item.DeviceDiskSource is null ? item.DiskWriteBytesPerSecond : item.DeviceDiskWriteBytesPerSecond);
                StoredNetworkChart.Push(item.TimestampUtc, item.NetworkRxBytesPerSecond, item.NetworkTxBytesPerSecond);
                StoredThermalChart.Push(item.TimestampUtc, item.TemperatureCelsius);
            }
            foreach (SessionMarker marker in monitoringSession.Markers)
            {
                StoredSessionMarkers.Add(marker);
                foreach (LiveChartViewModel chart in StoredSessionCharts)
                {
                    chart.AddAnnotation(marker.TimestampUtc, marker.Name, isAlert: false);
                }
            }
            foreach (SessionAlert alert in monitoringSession.Alerts)
            {
                StoredSessionAlerts.Add(alert);
                foreach (LiveChartViewModel chart in StoredSessionCharts)
                {
                    chart.AddAnnotation(alert.TimestampUtc, alert.Type, isAlert: true);
                }
            }
            foreach (SessionEvent sessionEvent in monitoringSession.Events)
            {
                StoredSessionEvents.Add(sessionEvent);
            }
            StoredSessionDetails = $"{summary.Device.FriendlyName} · {summary.Device.Serial}\n{summary.Device.VersionLine} · {summary.CurrentPackage ?? "No tracked package"}\nDuration {summary.Duration:g} · Active {summary.ActiveCollectionTime:g} · Samples {monitoringSession.Samples.Count:N0}\nPeak CPU {FormatNullable(summary.PeakCpuPercent, "%")} · Peak app memory {FormatBytes(summary.PeakMemoryBytes)} · Avg FPS {FormatNullable(summary.AverageFps, "")} · P95 frame {FormatNullable(summary.FrameTimeP95Ms, " ms")}";
            StoredSessionStatus = $"Full session loaded · {monitoringSession.Samples.Count:N0} samples · {summary.ExportStatus}";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StoredSessionStatus = "Session load failed";
            _dialogs.Notify(ex.Message, error: true);
        }
    }

    private void ClearStoredSessionView()
    {
        foreach (LiveChartViewModel storedSessionChart in StoredSessionCharts)
        {
            storedSessionChart.Clear();
        }
        StoredSessionMarkers.Clear();
        StoredSessionAlerts.Clear();
        StoredSessionEvents.Clear();
    }

    private static IReadOnlyList<MetricSample> Downsample(IList<MetricSample> samples, int maximumPoints)
    {
        if (samples.Count <= maximumPoints)
        {
            return samples.ToArray();
        }
        List<MetricSample> points = new List<MetricSample>(maximumPoints);
        double step = (samples.Count - 1.0) / (maximumPoints - 1.0);
        int previousIndex = -1;
        for (int i = 0; i < maximumPoints; i++)
        {
            int index = Math.Clamp((int)Math.Round(i * step), 0, samples.Count - 1);
            if (index != previousIndex)
            {
                points.Add(samples[index]);
                previousIndex = index;
            }
        }
        return points;
    }

    private static string FormatNullable(double? value, string unit) => value.HasValue ? $"{value.Value:N1}{unit}" : "N/A";

    private async Task ReloadMediaAsync()
    {
        Guid? selectedId = SelectedMediaItem?.Id;
        MediaItems.Clear();
        foreach (MediaItem item in await _media.ScanAsync(CancellationToken.None))
        {
            MediaItems.Add(item);
        }
        RefreshMediaFilterOptions();
        RefreshMediaView();
        SelectedMediaItem = selectedId.HasValue
            ? MediaItems.FirstOrDefault(item => item.Id == selectedId.Value)
            : MediaItems.FirstOrDefault();
        StatusMessage = $"Media library refreshed · {MediaItems.Count} items";
    }

    private void AddMediaItem(MediaItem item)
    {
        MediaItems.Insert(0, item);
        RefreshMediaFilterOptions();
        RefreshMediaView();
        SelectedMediaItem = item;
    }

    private void ReplaceMediaItem(MediaItem oldItem, MediaItem newItem)
    {
        int index = MediaItems.IndexOf(oldItem);
        if (index >= 0)
        {
            MediaItems[index] = newItem;
        }
        SelectedMediaItem = newItem;
        RefreshMediaFilterOptions();
        RefreshMediaView();
    }

    private bool FilterMedia(object item)
    {
        if (item is not MediaItem mediaItem)
        {
            return false;
        }
        if (SelectedMediaKind == "Screenshots" && mediaItem.Kind != MediaKind.Screenshot)
        {
            return false;
        }
        if (SelectedMediaKind == "Recordings" && mediaItem.Kind != MediaKind.Recording)
        {
            return false;
        }
        if (SelectedMediaDevice != "All" && !string.Equals(mediaItem.DeviceSerial, SelectedMediaDevice, StringComparison.Ordinal))
        {
            return false;
        }
        if (SelectedMediaPackage != "All" && !string.Equals(mediaItem.PackageName ?? "N/A", SelectedMediaPackage, StringComparison.Ordinal))
        {
            return false;
        }
        if (!string.IsNullOrWhiteSpace(MediaSearchText) && !mediaItem.FileName.Contains(MediaSearchText, StringComparison.OrdinalIgnoreCase))
        {
            return mediaItem.Note?.Contains(MediaSearchText, StringComparison.OrdinalIgnoreCase) ?? false;
        }
        return true;
    }

    private void RefreshMediaView()
    {
        MediaView.SortDescriptions.Clear();
        MediaView.SortDescriptions.Add(SelectedMediaSort switch
        {
            "Oldest" => new SortDescription("CapturedUtc", ListSortDirection.Ascending),
            "Largest" => new SortDescription("FileSize", ListSortDirection.Descending),
            "Smallest" => new SortDescription("FileSize", ListSortDirection.Ascending),
            _ => new SortDescription("CapturedUtc", ListSortDirection.Descending)
        });
        MediaView.Refresh();
        MediaFilterStatus = $"{MediaView.Cast<object>().Count()} shown · {MediaItems.Count} total";
    }

    private void RefreshMediaFilterOptions()
    {
        string selectedMediaDevice = SelectedMediaDevice;
        string selectedMediaPackage = SelectedMediaPackage;
        MediaDeviceFilters.Clear();
        MediaDeviceFilters.Add("All");
        foreach (string serial in MediaItems.Select(item => item.DeviceSerial).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).Order())
        {
            MediaDeviceFilters.Add(serial);
        }
        MediaPackageFilters.Clear();
        MediaPackageFilters.Add("All");
        foreach (string package in MediaItems.Select(item => item.PackageName ?? "N/A").Distinct(StringComparer.Ordinal).Order())
        {
            MediaPackageFilters.Add(package);
        }
        SelectedMediaDevice = MediaDeviceFilters.Contains(selectedMediaDevice) ? selectedMediaDevice : "All";
        SelectedMediaPackage = MediaPackageFilters.Contains(selectedMediaPackage) ? selectedMediaPackage : "All";
        OnPropertyChanged(nameof(MediaStorageSummary));
    }

    private void NavigateLocalPath(string path, bool recordHistory)
    {
        if (!Directory.Exists(path))
        {
            StatusMessage = "Local path does not exist: " + path;
            return;
        }
        if (recordHistory && !string.Equals(LocalPath, path, StringComparison.OrdinalIgnoreCase))
        {
            _localBackHistory.Push(LocalPath);
            _localForwardHistory.Clear();
        }
        LocalPath = path;
        LoadLocalFiles();
    }

    private async Task NavigateRemotePathAsync(string path, bool recordHistory)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            string normalized = path == "/" ? "/" : "/" + string.Join('/', path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries));
            if (recordHistory && !string.Equals(RemotePath, normalized, StringComparison.Ordinal))
            {
                _remoteBackHistory.Push(RemotePath);
                _remoteForwardHistory.Clear();
            }
            RemotePath = normalized;
            await RefreshFileExplorerAsync();
        }
    }

    private void LoadLocalFiles()
    {
        LocalFiles.Clear();
        if (!Directory.Exists(LocalPath))
        {
            return;
        }
        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(LocalPath).Take(1000))
            {
                bool isDirectory = Directory.Exists(entry);
                FileInfo? file = isDirectory ? null : new FileInfo(entry);
                LocalFiles.Add(new FileEntry(Path.GetFileName(entry), entry, isDirectory, file?.Length, isDirectory ? Directory.GetLastWriteTimeUtc(entry) : file?.LastWriteTimeUtc));
            }
        }
        catch (Exception ex)
        {
            StatusMessage = "Local path inaccessible: " + ex.Message;
        }
    }

    private bool FilterLocalFile(object item) => FilterFileEntry(item);

    private bool FilterRemoteFile(object item) => FilterFileEntry(item);

    private bool FilterFileEntry(object item) =>
        item is FileEntry entry &&
        (string.IsNullOrWhiteSpace(FileSearchText) || entry.Name.Contains(FileSearchText, StringComparison.OrdinalIgnoreCase));

    private static bool IsSafeRemoteName(string value) =>
        !string.IsNullOrWhiteSpace(value) && value is not ("." or "..") && !value.Contains('/') && !value.Contains('\\') && !value.Contains('\0');

    private static string CombineRemote(string directory, string name) => directory == "/" ? "/" + name : directory.TrimEnd('/') + "/" + name;

    private static string FormatBytes(long? value)
    {
        if (!value.HasValue)
        {
            return "N/A";
        }
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = value.Value;
        int unit = 0;
        while (size >= 1024.0 && unit < units.Length - 1)
        {
            size /= 1024.0;
            unit++;
        }
        return $"{size:N1} {units[unit]}";
    }

    private static string FormatRate(double? value) => value.HasValue ? FormatBytes((long)value.Value) + "/s" : "N/A";

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _appCts.Cancel();
        _contextCts?.Cancel();
        _automationCts?.Cancel();
        _transferCts?.Cancel();
        _shellCts?.Cancel();
        _storedSessionCts?.Cancel();
        if (IsRecording && _recordingTarget is not null)
        {
            try
            {
                MediaItem recording = await _media.StopRecordingAsync(
                    _recordingTarget,
                    _recordingPackage,
                    _recordingSessionId,
                    CancellationToken.None);
                AddMediaItem(recording);
            }
            catch
            {
            }
            IsRecording = false;
            ClearRecordingContext();
        }
        if (_session != null)
        {
            _session.EndedUtc = DateTimeOffset.UtcNow;
            AddSessionEvent("SessionEnded", "Application closed while monitoring " + _session.Device.FriendlyName, _session.Device.Serial, _session.CurrentPackage);
            await _sessions.SaveSessionAsync(_session, CancellationToken.None);
        }
        _contextCts?.Dispose();
        _automationCts?.Dispose();
        _transferCts?.Dispose();
        _shellCts?.Dispose();
        _storedSessionCts?.Dispose();
        _appCts.Dispose();
        _discoveryGate.Dispose();
    }

    partial void OnSelectedDeviceChanged(AndroidDevice? oldValue, AndroidDevice? newValue)
    {
        OnPropertyChanged(nameof(DeviceContext));
        OnPropertyChanged(nameof(BottomDeviceContext));
        if (!_suppressDeviceSwitch && _initialized && newValue is null)
        {
            _ = HandleSelectedDeviceUnavailableAsync(oldValue);
            return;
        }
        if (!_suppressDeviceSwitch && _initialized && newValue is not null && (oldValue?.Serial != newValue.Serial || oldValue?.State != newValue.State))
        {
            _ = SwitchDeviceAsync(newValue);
        }
    }

    partial void OnSelectedPackageChanged(string? oldValue, string? newValue)
    {
        _selectedPackageObservedRunning = false;
        ResolveAlert("AppExited");
        if (_session != null && oldValue != newValue)
        {
            lock (_session.SyncRoot)
            {
                _session.CurrentPackage = newValue;
            }
            AddSessionEvent("PackageChanged", "Tracked package changed from " + (oldValue ?? "none") + " to " + (newValue ?? "none"), _session.Device.Serial, newValue);
            RestartContextLoops();
        }
    }

    partial void OnIsLiveChanged(bool value)
    {
        OnPropertyChanged(nameof(LiveLabel));
        OnPropertyChanged(nameof(CollectionState));
        StatusMessage = value ? "Performance collection resumed" : "Performance collection paused";
        if (_session == null || SelectedDevice is null)
        {
            return;
        }
        DateTimeOffset utcNow = DateTimeOffset.UtcNow;
        lock (_session.SyncRoot)
        {
            _session.IsPaused = !value;
        }
        AddSessionEvent(value ? "SessionResumed" : "SessionPaused", StatusMessage, SelectedDevice.Serial, SelectedPackage);
        foreach (LiveChartViewModel performanceChart in PerformanceCharts)
        {
            performanceChart.AddAnnotation(utcNow, value ? "Resumed" : "Paused", isAlert: false);
        }
        _sessions.SaveSessionAsync(_session, CancellationToken.None);
    }

    partial void OnIsRecordingChanged(bool value)
    {
        OnPropertyChanged(nameof(RecordingLabel));
        OnPropertyChanged(nameof(RecordingState));
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyProcessFilter();
    }

    partial void OnSessionTimeChanged(string value)
    {
        OnPropertyChanged(nameof(SessionStatus));
    }

    partial void OnLogSearchTextChanged(string value)
    {
        RefreshLogFilter();
    }

    partial void OnSelectedLogPriorityChanged(string value)
    {
        RefreshLogFilter();
    }

    partial void OnSelectedLogSourceChanged(string value)
    {
        RefreshLogFilter();
    }

    partial void OnLogRegexEnabledChanged(bool value)
    {
        RefreshLogFilter();
    }

    partial void OnMediaSearchTextChanged(string value)
    {
        RefreshMediaView();
    }

    partial void OnSelectedMediaKindChanged(string value)
    {
        RefreshMediaView();
    }

    partial void OnSelectedMediaDeviceChanged(string value)
    {
        RefreshMediaView();
    }

    partial void OnSelectedMediaPackageChanged(string value)
    {
        RefreshMediaView();
    }

    partial void OnSelectedMediaSortChanged(string value)
    {
        RefreshMediaView();
    }

    partial void OnSelectedMediaItemChanged(MediaItem? value)
    {
        MediaRenameText = value is null ? "" : Path.GetFileNameWithoutExtension(value.FileName);
        MediaNote = value?.Note ?? "";
        SelectedMediaMarker = value?.MarkerId is Guid markerId ? Markers.FirstOrDefault(marker => marker.Id == markerId) : null;
    }

    partial void OnSelectedStoredSessionChanged(SessionSummary? value)
    {
        _ = LoadStoredSessionAsync(value);
    }

    partial void OnSelectedApkPathChanged(string? value)
    {
        OnPropertyChanged(nameof(SelectedApkInfo));
    }

    partial void OnFileSearchTextChanged(string value)
    {
        LocalFileView.Refresh();
        RemoteFileView.Refresh();
    }

    partial void OnSelectedRemoteFileChanged(FileEntry? value)
    {
        if (value is not null)
        {
            RemoteRenameText = value.Name;
        }
    }
}
