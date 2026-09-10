#nullable disable
#pragma warning disable CS8632

using System;
using System.CodeDom.Compiler;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using AndroidDevMonitor.App.Services;
using AndroidDevMonitor.Core.Collections;
using AndroidDevMonitor.Core.Configuration;
using AndroidDevMonitor.Core.Models;
using AndroidDevMonitor.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel.__Internals;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AndroidDevMonitor.App.ViewModels;

public partial class MainViewModel : ObservableObject, IAsyncDisposable
{
	private static readonly Regex LogcatLineRegex = new Regex("^(?<month>\\d{2})-(?<day>\\d{2})\\s+(?<time>\\d{2}:\\d{2}:\\d{2}\\.\\d{3,6})\\s+(?<pid>\\d+)\\s+(?<tid>\\d+)\\s+(?<priority>[VDIWEAF])\\s+(?<tag>[^:]+):\\s*(?<message>.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private readonly IDeviceDiscoveryService _devicesService;

	private readonly IMonitoringSource _monitoring;

	private readonly ISessionStore _sessions;

	private readonly IMediaService _media;

	private readonly ISessionExporter _exporter;

	private readonly IAdbExecutor _adb;

	private readonly IDialogService _dialogs;

	private readonly TimeSeriesBuffer<MetricSample> _liveSamples = new TimeSeriesBuffer<MetricSample>(MonitoringConstants.LiveChartWindow, (MetricSample x) => x.TimestampUtc);

	private CancellationTokenSource? _contextCts;

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

	private AndroidDevice? _selectedDevice;

	private string? _selectedPackage;

	private string _currentPage = "Overview";

	private string _overviewTab = "Processes";

	private bool _isLive = true;

	private bool _isRecording;

	private string _searchText = "";

	private string _connectionText = "Connecting";

	private string _statusMessage = "Starting…";

	private string _sessionTime = "00:00:00";

	private string _dataAge = "Waiting for data";

	private string _networkContext = "Network context: waiting for data";

	private string _networkTotals = "Session RX N/A · TX N/A";

	private string _storageSummary = "Storage capacity: waiting for data";

	private string _appStorageSummary = "Selected-app size: N/A";

	private string _thermalSummary = "Thermal and battery: waiting for data";

	private string _logSearchText = "";

	private string _selectedLogPriority = "All";

	private string _selectedLogSource = "All";

	private bool _logRegexEnabled;

	private bool _isLogPaused;

	private bool _isLogAutoScroll = true;

	private LogEntry? _selectedLogEntry;

	private string _logFilterStatus = "Live logcat";

	private string _mediaSearchText = "";

	private string _selectedMediaKind = "All";

	private string _selectedMediaDevice = "All";

	private string _selectedMediaPackage = "All";

	private string _selectedMediaSort = "Newest";

	private string _mediaLayout = "Gallery";

	private string _mediaFilterStatus = "Local media library";

	private MediaItem? _selectedMediaItem;

	private string _mediaRenameText = "";

	private string _mediaNote = "";

	private SessionMarker? _selectedMediaMarker;

	private SessionSummary? _selectedStoredSession;

	private MonitoringSession? _loadedStoredSession;

	private string _storedSessionStatus = "Select a stored session";

	private string _storedSessionDetails = "Full-session metrics, markers, alerts, disconnects and package changes appear here.";

	private string _shellCommand = "getprop ro.product.model";

	private string _shellOutput = "Commands run on the globally selected Android instance.";

	private string _selectedShellPreset = "getprop";

	private bool _isShellRunning;

	private string _shellStatus = "Idle";

	private string _automationLog = "Automation actions are bound to the selected serial when started.";

	private string? _selectedApkPath;

	private string _automationCoordinates = "540,960";

	private string _automationText = "Hello Android";

	private int _swipeDurationMs = 350;

	private string _newStepArgument = "";

	private int _newStepDurationMs = 1000;

	private int _automationRepeatCount = 1;

	private bool _automationStopOnFailure = true;

	private bool _isAutomationRunning;

	private bool _isAutomationPaused;

	private string _automationState = "Idle";

	private string _localPath = Environment.GetFolderPath(Environment.SpecialFolder.Personal);

	private string _remotePath = "/sdcard";

	private string _fileSearchText = "";

	private string _newRemoteFolderName = "NewFolder";

	private string _remoteRenameText = "";

	private string _transferStatus = "No active transfer";

	private bool _isTransferRunning;

	private string _selectedRemoteQuickLocation = "/sdcard";

	private ProcessDisplayRow? _selectedProcessRow;

	private FileEntry? _selectedLocalFile;

	private FileEntry? _selectedRemoteFile;

	private AutomationStepKind _selectedStepKind = AutomationStepKind.Wait;

	private AutomationStep? _selectedSequenceStep;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand<string?>? navigateCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand<string?>? selectOverviewTabCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? toggleLiveCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? toggleLogPauseCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? clearLogViewCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? openFullLogsCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? copySelectedLogCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand<ProcessDisplayRow?>? toggleProcessGroupCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? refreshCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? markEventCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? exportSessionCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand<string?>? exportStoredSessionCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? openSessionExportFolderCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? deleteStoredSessionCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? importAppiumLogCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? saveLogsCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? openSelectedMediaCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? openMediaFolderCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? copyMediaPathCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? renameMediaCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? saveMediaMetadataCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? deleteMediaCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? screenshotCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? toggleRecordingCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? runShellCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? cancelShellCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? clearShellOutputCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? applyShellPresetCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? previousShellCommandCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? nextShellCommandCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? saveShellOutputCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? selectApkCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand<string?>? runAutomationCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? browseLocalCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? openSelectedLocalCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? localUpCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? localBackCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? localForwardCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? openLocalFolderCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? openSelectedRemoteCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? remoteUpCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? remoteBackCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? remoteForwardCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? goRemoteQuickLocationCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? createRemoteFolderCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? renameRemoteCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? deleteRemoteCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? cancelTransferCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? refreshFileExplorerCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? pushFileCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? pullFileCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? addSequenceStepCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand<AutomationStep?>? removeSequenceStepCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand<AutomationStep?>? moveSequenceStepUpCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand<AutomationStep?>? moveSequenceStepDownCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? pauseSequenceCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? resumeSequenceCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? stopSequenceCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? runSequenceCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? showMoreCommand;

	public bool IsDemo { get; }

	public string DemoBadge
	{
		get
		{
			if (!IsDemo)
			{
				return "";
			}
			return "DEMO DATA";
		}
	}

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

	public IReadOnlyList<string> LogPriorities { get; } = new global::_003C_003Ez__ReadOnlyArray<string>(new string[5] { "All", "Error", "Warning", "Info", "Debug" });

	public IReadOnlyList<string> LogSources { get; } = new global::_003C_003Ez__ReadOnlyArray<string>(new string[5] { "All", "logcat", "Appium (external)", "Android Dev Monitor", "Session" });

	public ObservableCollection<MediaItem> MediaItems { get; } = new ObservableCollection<MediaItem>();

	public ICollectionView MediaView { get; }

	public IReadOnlyList<string> MediaKinds { get; } = new global::_003C_003Ez__ReadOnlyArray<string>(new string[3] { "All", "Screenshots", "Recordings" });

	public IReadOnlyList<string> MediaSortOptions { get; } = new global::_003C_003Ez__ReadOnlyArray<string>(new string[4] { "Newest", "Oldest", "Largest", "Smallest" });

	public IReadOnlyList<string> MediaLayouts { get; } = new global::_003C_003Ez__ReadOnlyArray<string>(new string[2] { "Gallery", "List" });

	public ObservableCollection<string> MediaDeviceFilters { get; } = new ObservableCollection<string> { "All" };

	public ObservableCollection<string> MediaPackageFilters { get; } = new ObservableCollection<string> { "All" };

	public ObservableCollection<SessionSummary> StoredSessions { get; } = new ObservableCollection<SessionSummary>();

	public ObservableCollection<SessionMarker> StoredSessionMarkers { get; } = new ObservableCollection<SessionMarker>();

	public ObservableCollection<SessionAlert> StoredSessionAlerts { get; } = new ObservableCollection<SessionAlert>();

	public ObservableCollection<SessionEvent> StoredSessionEvents { get; } = new ObservableCollection<SessionEvent>();

	public ObservableCollection<string> ShellHistory { get; } = new ObservableCollection<string>();

	public IReadOnlyList<string> ShellPresets { get; } = new global::_003C_003Ez__ReadOnlyArray<string>(new string[8] { "getprop", "dumpsys battery", "dumpsys meminfo", "wm size", "wm density", "df -h", "pm list packages", "ps -A" });

	public ObservableCollection<FileEntry> LocalFiles { get; } = new ObservableCollection<FileEntry>();

	public ObservableCollection<FileEntry> RemoteFiles { get; } = new ObservableCollection<FileEntry>();

	public ICollectionView LocalFileView { get; }

	public ICollectionView RemoteFileView { get; }

	public IReadOnlyList<string> RemoteQuickLocations { get; } = new global::_003C_003Ez__ReadOnlyArray<string>(new string[8] { "/sdcard", "/sdcard/Download", "/sdcard/DCIM", "/sdcard/Pictures", "/sdcard/Movies", "/sdcard/Documents", "/sdcard/Android/data", "/data/local/tmp" });

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

	public string WindowTitle
	{
		get
		{
			if (!IsDemo)
			{
				return "Android Dev Monitor";
			}
			return "Android Dev Monitor — DEMO DATA";
		}
	}

	public string DeviceContext
	{
		get
		{
			if ((object)SelectedDevice != null)
			{
				return SelectedDevice.FriendlyName + " · " + SelectedDevice.Serial;
			}
			return "No Android device selected";
		}
	}

	public string SessionStatus => "Session " + SessionTime;

	public string LiveLabel
	{
		get
		{
			if (!IsLive)
			{
				return "Resume";
			}
			return "Pause";
		}
	}

	public string RecordingLabel
	{
		get
		{
			if (!IsRecording)
			{
				return "Record";
			}
			return "Stop Recording";
		}
	}

	public string AdbPathDisplay
	{
		get
		{
			object obj;
			if (!IsDemo)
			{
				obj = _adb.ResolvedAdbPath;
				if (obj == null)
				{
					return "ADB executable was not found";
				}
			}
			else
			{
				obj = "Demo provider (ADB disabled)";
			}
			return (string)obj;
		}
	}

	public string SelectedApkInfo
	{
		get
		{
			if (!string.IsNullOrWhiteSpace(SelectedApkPath) && File.Exists(SelectedApkPath))
			{
				return Path.GetFileName(SelectedApkPath) + " · " + FormatBytes(new FileInfo(SelectedApkPath).Length);
			}
			return "No APK selected";
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public AndroidDevice? SelectedDevice
	{
		get
		{
			return _selectedDevice;
		}
		set
		{
			if (!EqualityComparer<AndroidDevice>.Default.Equals(_selectedDevice, value))
			{
				AndroidDevice selectedDevice = _selectedDevice;
				OnPropertyChanging(nameof(SelectedDevice));
				_selectedDevice = value;
				OnSelectedDeviceChanged(selectedDevice, value);
				OnPropertyChanged(nameof(SelectedDevice));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string? SelectedPackage
	{
		get
		{
			return _selectedPackage;
		}
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_selectedPackage, value))
			{
				string selectedPackage = _selectedPackage;
				OnPropertyChanging(nameof(SelectedPackage));
				_selectedPackage = value;
				OnSelectedPackageChanged(selectedPackage, value);
				OnPropertyChanged(nameof(SelectedPackage));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string CurrentPage
	{
		get
		{
			return _currentPage;
		}
		[MemberNotNull("_currentPage")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_currentPage, value))
			{
				OnPropertyChanging(nameof(CurrentPage));
				_currentPage = value;
				OnPropertyChanged(nameof(CurrentPage));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string OverviewTab
	{
		get
		{
			return _overviewTab;
		}
		[MemberNotNull("_overviewTab")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_overviewTab, value))
			{
				OnPropertyChanging(nameof(OverviewTab));
				_overviewTab = value;
				OnPropertyChanged(nameof(OverviewTab));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsLive
	{
		get
		{
			return _isLive;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_isLive, value))
			{
				OnPropertyChanging(nameof(IsLive));
				_isLive = value;
				OnIsLiveChanged(value);
				OnPropertyChanged(nameof(IsLive));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsRecording
	{
		get
		{
			return _isRecording;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_isRecording, value))
			{
				OnPropertyChanging(nameof(IsRecording));
				_isRecording = value;
				OnIsRecordingChanged(value);
				OnPropertyChanged(nameof(IsRecording));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string SearchText
	{
		get
		{
			return _searchText;
		}
		[MemberNotNull("_searchText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_searchText, value))
			{
				OnPropertyChanging(nameof(SearchText));
				_searchText = value;
				OnSearchTextChanged(value);
				OnPropertyChanged(nameof(SearchText));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string ConnectionText
	{
		get
		{
			return _connectionText;
		}
		[MemberNotNull("_connectionText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_connectionText, value))
			{
				OnPropertyChanging(nameof(ConnectionText));
				_connectionText = value;
				OnPropertyChanged(nameof(ConnectionText));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string StatusMessage
	{
		get
		{
			return _statusMessage;
		}
		[MemberNotNull("_statusMessage")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_statusMessage, value))
			{
				OnPropertyChanging(nameof(StatusMessage));
				_statusMessage = value;
				OnPropertyChanged(nameof(StatusMessage));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string SessionTime
	{
		get
		{
			return _sessionTime;
		}
		[MemberNotNull("_sessionTime")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_sessionTime, value))
			{
				OnPropertyChanging(nameof(SessionTime));
				_sessionTime = value;
				OnSessionTimeChanged(value);
				OnPropertyChanged(nameof(SessionTime));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string DataAge
	{
		get
		{
			return _dataAge;
		}
		[MemberNotNull("_dataAge")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_dataAge, value))
			{
				OnPropertyChanging(nameof(DataAge));
				_dataAge = value;
				OnPropertyChanged(nameof(DataAge));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string NetworkContext
	{
		get
		{
			return _networkContext;
		}
		[MemberNotNull("_networkContext")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_networkContext, value))
			{
				OnPropertyChanging(nameof(NetworkContext));
				_networkContext = value;
				OnPropertyChanged(nameof(NetworkContext));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string NetworkTotals
	{
		get
		{
			return _networkTotals;
		}
		[MemberNotNull("_networkTotals")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_networkTotals, value))
			{
				OnPropertyChanging(nameof(NetworkTotals));
				_networkTotals = value;
				OnPropertyChanged(nameof(NetworkTotals));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string StorageSummary
	{
		get
		{
			return _storageSummary;
		}
		[MemberNotNull("_storageSummary")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_storageSummary, value))
			{
				OnPropertyChanging(nameof(StorageSummary));
				_storageSummary = value;
				OnPropertyChanged(nameof(StorageSummary));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string AppStorageSummary
	{
		get
		{
			return _appStorageSummary;
		}
		[MemberNotNull("_appStorageSummary")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_appStorageSummary, value))
			{
				OnPropertyChanging(nameof(AppStorageSummary));
				_appStorageSummary = value;
				OnPropertyChanged(nameof(AppStorageSummary));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string ThermalSummary
	{
		get
		{
			return _thermalSummary;
		}
		[MemberNotNull("_thermalSummary")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_thermalSummary, value))
			{
				OnPropertyChanging(nameof(ThermalSummary));
				_thermalSummary = value;
				OnPropertyChanged(nameof(ThermalSummary));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string LogSearchText
	{
		get
		{
			return _logSearchText;
		}
		[MemberNotNull("_logSearchText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_logSearchText, value))
			{
				OnPropertyChanging(nameof(LogSearchText));
				_logSearchText = value;
				OnLogSearchTextChanged(value);
				OnPropertyChanged(nameof(LogSearchText));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string SelectedLogPriority
	{
		get
		{
			return _selectedLogPriority;
		}
		[MemberNotNull("_selectedLogPriority")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_selectedLogPriority, value))
			{
				OnPropertyChanging(nameof(SelectedLogPriority));
				_selectedLogPriority = value;
				OnSelectedLogPriorityChanged(value);
				OnPropertyChanged(nameof(SelectedLogPriority));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string SelectedLogSource
	{
		get
		{
			return _selectedLogSource;
		}
		[MemberNotNull("_selectedLogSource")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_selectedLogSource, value))
			{
				OnPropertyChanging(nameof(SelectedLogSource));
				_selectedLogSource = value;
				OnSelectedLogSourceChanged(value);
				OnPropertyChanged(nameof(SelectedLogSource));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool LogRegexEnabled
	{
		get
		{
			return _logRegexEnabled;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_logRegexEnabled, value))
			{
				OnPropertyChanging(nameof(LogRegexEnabled));
				_logRegexEnabled = value;
				OnLogRegexEnabledChanged(value);
				OnPropertyChanged(nameof(LogRegexEnabled));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsLogPaused
	{
		get
		{
			return _isLogPaused;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_isLogPaused, value))
			{
				OnPropertyChanging(nameof(IsLogPaused));
				_isLogPaused = value;
				OnPropertyChanged(nameof(IsLogPaused));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsLogAutoScroll
	{
		get
		{
			return _isLogAutoScroll;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_isLogAutoScroll, value))
			{
				OnPropertyChanging(nameof(IsLogAutoScroll));
				_isLogAutoScroll = value;
				OnPropertyChanged(nameof(IsLogAutoScroll));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public LogEntry? SelectedLogEntry
	{
		get
		{
			return _selectedLogEntry;
		}
		set
		{
			if (!EqualityComparer<LogEntry>.Default.Equals(_selectedLogEntry, value))
			{
				OnPropertyChanging(nameof(SelectedLogEntry));
				_selectedLogEntry = value;
				OnPropertyChanged(nameof(SelectedLogEntry));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string LogFilterStatus
	{
		get
		{
			return _logFilterStatus;
		}
		[MemberNotNull("_logFilterStatus")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_logFilterStatus, value))
			{
				OnPropertyChanging(nameof(LogFilterStatus));
				_logFilterStatus = value;
				OnPropertyChanged(nameof(LogFilterStatus));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string MediaSearchText
	{
		get
		{
			return _mediaSearchText;
		}
		[MemberNotNull("_mediaSearchText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_mediaSearchText, value))
			{
				OnPropertyChanging(nameof(MediaSearchText));
				_mediaSearchText = value;
				OnMediaSearchTextChanged(value);
				OnPropertyChanged(nameof(MediaSearchText));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string SelectedMediaKind
	{
		get
		{
			return _selectedMediaKind;
		}
		[MemberNotNull("_selectedMediaKind")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_selectedMediaKind, value))
			{
				OnPropertyChanging(nameof(SelectedMediaKind));
				_selectedMediaKind = value;
				OnSelectedMediaKindChanged(value);
				OnPropertyChanged(nameof(SelectedMediaKind));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string SelectedMediaDevice
	{
		get
		{
			return _selectedMediaDevice;
		}
		[MemberNotNull("_selectedMediaDevice")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_selectedMediaDevice, value))
			{
				OnPropertyChanging(nameof(SelectedMediaDevice));
				_selectedMediaDevice = value;
				OnSelectedMediaDeviceChanged(value);
				OnPropertyChanged(nameof(SelectedMediaDevice));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string SelectedMediaPackage
	{
		get
		{
			return _selectedMediaPackage;
		}
		[MemberNotNull("_selectedMediaPackage")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_selectedMediaPackage, value))
			{
				OnPropertyChanging(nameof(SelectedMediaPackage));
				_selectedMediaPackage = value;
				OnSelectedMediaPackageChanged(value);
				OnPropertyChanged(nameof(SelectedMediaPackage));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string SelectedMediaSort
	{
		get
		{
			return _selectedMediaSort;
		}
		[MemberNotNull("_selectedMediaSort")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_selectedMediaSort, value))
			{
				OnPropertyChanging(nameof(SelectedMediaSort));
				_selectedMediaSort = value;
				OnSelectedMediaSortChanged(value);
				OnPropertyChanged(nameof(SelectedMediaSort));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string MediaLayout
	{
		get
		{
			return _mediaLayout;
		}
		[MemberNotNull("_mediaLayout")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_mediaLayout, value))
			{
				OnPropertyChanging(nameof(MediaLayout));
				_mediaLayout = value;
				OnPropertyChanged(nameof(MediaLayout));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string MediaFilterStatus
	{
		get
		{
			return _mediaFilterStatus;
		}
		[MemberNotNull("_mediaFilterStatus")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_mediaFilterStatus, value))
			{
				OnPropertyChanging(nameof(MediaFilterStatus));
				_mediaFilterStatus = value;
				OnPropertyChanged(nameof(MediaFilterStatus));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public MediaItem? SelectedMediaItem
	{
		get
		{
			return _selectedMediaItem;
		}
		set
		{
			if (!EqualityComparer<MediaItem>.Default.Equals(_selectedMediaItem, value))
			{
				OnPropertyChanging(nameof(SelectedMediaItem));
				_selectedMediaItem = value;
				OnSelectedMediaItemChanged(value);
				OnPropertyChanged(nameof(SelectedMediaItem));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string MediaRenameText
	{
		get
		{
			return _mediaRenameText;
		}
		[MemberNotNull("_mediaRenameText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_mediaRenameText, value))
			{
				OnPropertyChanging(nameof(MediaRenameText));
				_mediaRenameText = value;
				OnPropertyChanged(nameof(MediaRenameText));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string MediaNote
	{
		get
		{
			return _mediaNote;
		}
		[MemberNotNull("_mediaNote")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_mediaNote, value))
			{
				OnPropertyChanging(nameof(MediaNote));
				_mediaNote = value;
				OnPropertyChanged(nameof(MediaNote));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public SessionMarker? SelectedMediaMarker
	{
		get
		{
			return _selectedMediaMarker;
		}
		set
		{
			if (!EqualityComparer<SessionMarker>.Default.Equals(_selectedMediaMarker, value))
			{
				OnPropertyChanging(nameof(SelectedMediaMarker));
				_selectedMediaMarker = value;
				OnPropertyChanged(nameof(SelectedMediaMarker));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public SessionSummary? SelectedStoredSession
	{
		get
		{
			return _selectedStoredSession;
		}
		set
		{
			if (!EqualityComparer<SessionSummary>.Default.Equals(_selectedStoredSession, value))
			{
				OnPropertyChanging(nameof(SelectedStoredSession));
				_selectedStoredSession = value;
				OnSelectedStoredSessionChanged(value);
				OnPropertyChanged(nameof(SelectedStoredSession));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public MonitoringSession? LoadedStoredSession
	{
		get
		{
			return _loadedStoredSession;
		}
		set
		{
			if (!EqualityComparer<MonitoringSession>.Default.Equals(_loadedStoredSession, value))
			{
				OnPropertyChanging(nameof(LoadedStoredSession));
				_loadedStoredSession = value;
				OnPropertyChanged(nameof(LoadedStoredSession));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string StoredSessionStatus
	{
		get
		{
			return _storedSessionStatus;
		}
		[MemberNotNull("_storedSessionStatus")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_storedSessionStatus, value))
			{
				OnPropertyChanging(nameof(StoredSessionStatus));
				_storedSessionStatus = value;
				OnPropertyChanged(nameof(StoredSessionStatus));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string StoredSessionDetails
	{
		get
		{
			return _storedSessionDetails;
		}
		[MemberNotNull("_storedSessionDetails")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_storedSessionDetails, value))
			{
				OnPropertyChanging(nameof(StoredSessionDetails));
				_storedSessionDetails = value;
				OnPropertyChanged(nameof(StoredSessionDetails));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string ShellCommand
	{
		get
		{
			return _shellCommand;
		}
		[MemberNotNull("_shellCommand")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_shellCommand, value))
			{
				OnPropertyChanging(nameof(ShellCommand));
				_shellCommand = value;
				OnPropertyChanged(nameof(ShellCommand));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string ShellOutput
	{
		get
		{
			return _shellOutput;
		}
		[MemberNotNull("_shellOutput")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_shellOutput, value))
			{
				OnPropertyChanging(nameof(ShellOutput));
				_shellOutput = value;
				OnPropertyChanged(nameof(ShellOutput));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string SelectedShellPreset
	{
		get
		{
			return _selectedShellPreset;
		}
		[MemberNotNull("_selectedShellPreset")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_selectedShellPreset, value))
			{
				OnPropertyChanging(nameof(SelectedShellPreset));
				_selectedShellPreset = value;
				OnPropertyChanged(nameof(SelectedShellPreset));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsShellRunning
	{
		get
		{
			return _isShellRunning;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_isShellRunning, value))
			{
				OnPropertyChanging(nameof(IsShellRunning));
				_isShellRunning = value;
				OnPropertyChanged(nameof(IsShellRunning));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string ShellStatus
	{
		get
		{
			return _shellStatus;
		}
		[MemberNotNull("_shellStatus")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_shellStatus, value))
			{
				OnPropertyChanging(nameof(ShellStatus));
				_shellStatus = value;
				OnPropertyChanged(nameof(ShellStatus));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string AutomationLog
	{
		get
		{
			return _automationLog;
		}
		[MemberNotNull("_automationLog")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_automationLog, value))
			{
				OnPropertyChanging(nameof(AutomationLog));
				_automationLog = value;
				OnPropertyChanged(nameof(AutomationLog));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string? SelectedApkPath
	{
		get
		{
			return _selectedApkPath;
		}
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_selectedApkPath, value))
			{
				OnPropertyChanging(nameof(SelectedApkPath));
				_selectedApkPath = value;
				OnSelectedApkPathChanged(value);
				OnPropertyChanged(nameof(SelectedApkPath));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string AutomationCoordinates
	{
		get
		{
			return _automationCoordinates;
		}
		[MemberNotNull("_automationCoordinates")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_automationCoordinates, value))
			{
				OnPropertyChanging(nameof(AutomationCoordinates));
				_automationCoordinates = value;
				OnPropertyChanged(nameof(AutomationCoordinates));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string AutomationText
	{
		get
		{
			return _automationText;
		}
		[MemberNotNull("_automationText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_automationText, value))
			{
				OnPropertyChanging(nameof(AutomationText));
				_automationText = value;
				OnPropertyChanged(nameof(AutomationText));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public int SwipeDurationMs
	{
		get
		{
			return _swipeDurationMs;
		}
		set
		{
			if (!EqualityComparer<int>.Default.Equals(_swipeDurationMs, value))
			{
				OnPropertyChanging(nameof(SwipeDurationMs));
				_swipeDurationMs = value;
				OnPropertyChanged(nameof(SwipeDurationMs));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string NewStepArgument
	{
		get
		{
			return _newStepArgument;
		}
		[MemberNotNull("_newStepArgument")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_newStepArgument, value))
			{
				OnPropertyChanging(nameof(NewStepArgument));
				_newStepArgument = value;
				OnPropertyChanged(nameof(NewStepArgument));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public int NewStepDurationMs
	{
		get
		{
			return _newStepDurationMs;
		}
		set
		{
			if (!EqualityComparer<int>.Default.Equals(_newStepDurationMs, value))
			{
				OnPropertyChanging(nameof(NewStepDurationMs));
				_newStepDurationMs = value;
				OnPropertyChanged(nameof(NewStepDurationMs));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public int AutomationRepeatCount
	{
		get
		{
			return _automationRepeatCount;
		}
		set
		{
			if (!EqualityComparer<int>.Default.Equals(_automationRepeatCount, value))
			{
				OnPropertyChanging(nameof(AutomationRepeatCount));
				_automationRepeatCount = value;
				OnPropertyChanged(nameof(AutomationRepeatCount));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool AutomationStopOnFailure
	{
		get
		{
			return _automationStopOnFailure;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_automationStopOnFailure, value))
			{
				OnPropertyChanging(nameof(AutomationStopOnFailure));
				_automationStopOnFailure = value;
				OnPropertyChanged(nameof(AutomationStopOnFailure));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsAutomationRunning
	{
		get
		{
			return _isAutomationRunning;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_isAutomationRunning, value))
			{
				OnPropertyChanging(nameof(IsAutomationRunning));
				_isAutomationRunning = value;
				OnPropertyChanged(nameof(IsAutomationRunning));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsAutomationPaused
	{
		get
		{
			return _isAutomationPaused;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_isAutomationPaused, value))
			{
				OnPropertyChanging(nameof(IsAutomationPaused));
				_isAutomationPaused = value;
				OnPropertyChanged(nameof(IsAutomationPaused));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string AutomationState
	{
		get
		{
			return _automationState;
		}
		[MemberNotNull("_automationState")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_automationState, value))
			{
				OnPropertyChanging(nameof(AutomationState));
				_automationState = value;
				OnPropertyChanged(nameof(AutomationState));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string LocalPath
	{
		get
		{
			return _localPath;
		}
		[MemberNotNull("_localPath")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_localPath, value))
			{
				OnPropertyChanging(nameof(LocalPath));
				_localPath = value;
				OnPropertyChanged(nameof(LocalPath));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string RemotePath
	{
		get
		{
			return _remotePath;
		}
		[MemberNotNull("_remotePath")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_remotePath, value))
			{
				OnPropertyChanging(nameof(RemotePath));
				_remotePath = value;
				OnPropertyChanged(nameof(RemotePath));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string FileSearchText
	{
		get
		{
			return _fileSearchText;
		}
		[MemberNotNull("_fileSearchText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_fileSearchText, value))
			{
				OnPropertyChanging(nameof(FileSearchText));
				_fileSearchText = value;
				OnFileSearchTextChanged(value);
				OnPropertyChanged(nameof(FileSearchText));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string NewRemoteFolderName
	{
		get
		{
			return _newRemoteFolderName;
		}
		[MemberNotNull("_newRemoteFolderName")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_newRemoteFolderName, value))
			{
				OnPropertyChanging(nameof(NewRemoteFolderName));
				_newRemoteFolderName = value;
				OnPropertyChanged(nameof(NewRemoteFolderName));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string RemoteRenameText
	{
		get
		{
			return _remoteRenameText;
		}
		[MemberNotNull("_remoteRenameText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_remoteRenameText, value))
			{
				OnPropertyChanging(nameof(RemoteRenameText));
				_remoteRenameText = value;
				OnPropertyChanged(nameof(RemoteRenameText));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string TransferStatus
	{
		get
		{
			return _transferStatus;
		}
		[MemberNotNull("_transferStatus")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_transferStatus, value))
			{
				OnPropertyChanging(nameof(TransferStatus));
				_transferStatus = value;
				OnPropertyChanged(nameof(TransferStatus));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsTransferRunning
	{
		get
		{
			return _isTransferRunning;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_isTransferRunning, value))
			{
				OnPropertyChanging(nameof(IsTransferRunning));
				_isTransferRunning = value;
				OnPropertyChanged(nameof(IsTransferRunning));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string SelectedRemoteQuickLocation
	{
		get
		{
			return _selectedRemoteQuickLocation;
		}
		[MemberNotNull("_selectedRemoteQuickLocation")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_selectedRemoteQuickLocation, value))
			{
				OnPropertyChanging(nameof(SelectedRemoteQuickLocation));
				_selectedRemoteQuickLocation = value;
				OnPropertyChanged(nameof(SelectedRemoteQuickLocation));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public ProcessDisplayRow? SelectedProcessRow
	{
		get
		{
			return _selectedProcessRow;
		}
		set
		{
			if (!EqualityComparer<ProcessDisplayRow>.Default.Equals(_selectedProcessRow, value))
			{
				OnPropertyChanging(nameof(SelectedProcessRow));
				_selectedProcessRow = value;
				OnPropertyChanged(nameof(SelectedProcessRow));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public FileEntry? SelectedLocalFile
	{
		get
		{
			return _selectedLocalFile;
		}
		set
		{
			if (!EqualityComparer<FileEntry>.Default.Equals(_selectedLocalFile, value))
			{
				OnPropertyChanging(nameof(SelectedLocalFile));
				_selectedLocalFile = value;
				OnPropertyChanged(nameof(SelectedLocalFile));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public FileEntry? SelectedRemoteFile
	{
		get
		{
			return _selectedRemoteFile;
		}
		set
		{
			if (!EqualityComparer<FileEntry>.Default.Equals(_selectedRemoteFile, value))
			{
				OnPropertyChanging(nameof(SelectedRemoteFile));
				_selectedRemoteFile = value;
				OnSelectedRemoteFileChanged(value);
				OnPropertyChanged(nameof(SelectedRemoteFile));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public AutomationStepKind SelectedStepKind
	{
		get
		{
			return _selectedStepKind;
		}
		set
		{
			if (!EqualityComparer<AutomationStepKind>.Default.Equals(_selectedStepKind, value))
			{
				OnPropertyChanging(nameof(SelectedStepKind));
				_selectedStepKind = value;
				OnPropertyChanged(nameof(SelectedStepKind));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public AutomationStep? SelectedSequenceStep
	{
		get
		{
			return _selectedSequenceStep;
		}
		set
		{
			if (!EqualityComparer<AutomationStep>.Default.Equals(_selectedSequenceStep, value))
			{
				OnPropertyChanging(nameof(SelectedSequenceStep));
				_selectedSequenceStep = value;
				OnPropertyChanged(nameof(SelectedSequenceStep));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand<string?> NavigateCommand => navigateCommand ?? (navigateCommand = new RelayCommand<string>(Navigate));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand<string?> SelectOverviewTabCommand => selectOverviewTabCommand ?? (selectOverviewTabCommand = new RelayCommand<string>(SelectOverviewTab));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand ToggleLiveCommand => toggleLiveCommand ?? (toggleLiveCommand = new RelayCommand(ToggleLive));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand ToggleLogPauseCommand => toggleLogPauseCommand ?? (toggleLogPauseCommand = new RelayCommand(ToggleLogPause));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand ClearLogViewCommand => clearLogViewCommand ?? (clearLogViewCommand = new RelayCommand(ClearLogView));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand OpenFullLogsCommand => openFullLogsCommand ?? (openFullLogsCommand = new RelayCommand(OpenFullLogs));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand CopySelectedLogCommand => copySelectedLogCommand ?? (copySelectedLogCommand = new RelayCommand(CopySelectedLog));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand<ProcessDisplayRow?> ToggleProcessGroupCommand => toggleProcessGroupCommand ?? (toggleProcessGroupCommand = new RelayCommand<ProcessDisplayRow>(ToggleProcessGroup));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand RefreshCommand => refreshCommand ?? (refreshCommand = new AsyncRelayCommand(RefreshAsync));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand MarkEventCommand => markEventCommand ?? (markEventCommand = new RelayCommand(MarkEvent));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand ExportSessionCommand => exportSessionCommand ?? (exportSessionCommand = new AsyncRelayCommand(ExportSessionAsync));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand<string?> ExportStoredSessionCommand => exportStoredSessionCommand ?? (exportStoredSessionCommand = new AsyncRelayCommand<string>(ExportStoredSessionAsync));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand OpenSessionExportFolderCommand => openSessionExportFolderCommand ?? (openSessionExportFolderCommand = new RelayCommand(OpenSessionExportFolder));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand DeleteStoredSessionCommand => deleteStoredSessionCommand ?? (deleteStoredSessionCommand = new AsyncRelayCommand(DeleteStoredSessionAsync));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand ImportAppiumLogCommand => importAppiumLogCommand ?? (importAppiumLogCommand = new AsyncRelayCommand(ImportAppiumLogAsync));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand SaveLogsCommand => saveLogsCommand ?? (saveLogsCommand = new AsyncRelayCommand(SaveLogsAsync));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand OpenSelectedMediaCommand => openSelectedMediaCommand ?? (openSelectedMediaCommand = new RelayCommand(OpenSelectedMedia));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand OpenMediaFolderCommand => openMediaFolderCommand ?? (openMediaFolderCommand = new RelayCommand(OpenMediaFolder));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand CopyMediaPathCommand => copyMediaPathCommand ?? (copyMediaPathCommand = new RelayCommand(CopyMediaPath));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand RenameMediaCommand => renameMediaCommand ?? (renameMediaCommand = new AsyncRelayCommand(RenameMediaAsync));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand SaveMediaMetadataCommand => saveMediaMetadataCommand ?? (saveMediaMetadataCommand = new AsyncRelayCommand(SaveMediaMetadataAsync));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand DeleteMediaCommand => deleteMediaCommand ?? (deleteMediaCommand = new AsyncRelayCommand(DeleteMediaAsync));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand ScreenshotCommand => screenshotCommand ?? (screenshotCommand = new AsyncRelayCommand(ScreenshotAsync));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand ToggleRecordingCommand => toggleRecordingCommand ?? (toggleRecordingCommand = new AsyncRelayCommand(ToggleRecordingAsync));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand RunShellCommand => runShellCommand ?? (runShellCommand = new AsyncRelayCommand(RunShellAsync));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand CancelShellCommand => cancelShellCommand ?? (cancelShellCommand = new RelayCommand(CancelShell));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand ClearShellOutputCommand => clearShellOutputCommand ?? (clearShellOutputCommand = new RelayCommand(ClearShellOutput));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand ApplyShellPresetCommand => applyShellPresetCommand ?? (applyShellPresetCommand = new RelayCommand(ApplyShellPreset));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand PreviousShellCommandCommand => previousShellCommandCommand ?? (previousShellCommandCommand = new RelayCommand(PreviousShellCommand));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand NextShellCommandCommand => nextShellCommandCommand ?? (nextShellCommandCommand = new RelayCommand(NextShellCommand));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand SaveShellOutputCommand => saveShellOutputCommand ?? (saveShellOutputCommand = new AsyncRelayCommand(SaveShellOutputAsync));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand SelectApkCommand => selectApkCommand ?? (selectApkCommand = new RelayCommand(SelectApk));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand<string?> RunAutomationCommand => runAutomationCommand ?? (runAutomationCommand = new AsyncRelayCommand<string>(RunAutomationAsync));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand BrowseLocalCommand => browseLocalCommand ?? (browseLocalCommand = new RelayCommand(BrowseLocal));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand OpenSelectedLocalCommand => openSelectedLocalCommand ?? (openSelectedLocalCommand = new RelayCommand(OpenSelectedLocal));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand LocalUpCommand => localUpCommand ?? (localUpCommand = new RelayCommand(LocalUp));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand LocalBackCommand => localBackCommand ?? (localBackCommand = new RelayCommand(LocalBack));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand LocalForwardCommand => localForwardCommand ?? (localForwardCommand = new RelayCommand(LocalForward));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand OpenLocalFolderCommand => openLocalFolderCommand ?? (openLocalFolderCommand = new RelayCommand(OpenLocalFolder));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand OpenSelectedRemoteCommand => openSelectedRemoteCommand ?? (openSelectedRemoteCommand = new AsyncRelayCommand(OpenSelectedRemoteAsync));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand RemoteUpCommand => remoteUpCommand ?? (remoteUpCommand = new AsyncRelayCommand(RemoteUpAsync));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand RemoteBackCommand => remoteBackCommand ?? (remoteBackCommand = new AsyncRelayCommand(RemoteBackAsync));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand RemoteForwardCommand => remoteForwardCommand ?? (remoteForwardCommand = new AsyncRelayCommand(RemoteForwardAsync));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand GoRemoteQuickLocationCommand => goRemoteQuickLocationCommand ?? (goRemoteQuickLocationCommand = new AsyncRelayCommand(GoRemoteQuickLocationAsync));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand CreateRemoteFolderCommand => createRemoteFolderCommand ?? (createRemoteFolderCommand = new AsyncRelayCommand(CreateRemoteFolderAsync));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand RenameRemoteCommand => renameRemoteCommand ?? (renameRemoteCommand = new AsyncRelayCommand(RenameRemoteAsync));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand DeleteRemoteCommand => deleteRemoteCommand ?? (deleteRemoteCommand = new AsyncRelayCommand(DeleteRemoteAsync));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand CancelTransferCommand => cancelTransferCommand ?? (cancelTransferCommand = new RelayCommand(CancelTransfer));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand RefreshFileExplorerCommand => refreshFileExplorerCommand ?? (refreshFileExplorerCommand = new AsyncRelayCommand(RefreshFileExplorerAsync));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand PushFileCommand => pushFileCommand ?? (pushFileCommand = new AsyncRelayCommand(PushFileAsync));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand PullFileCommand => pullFileCommand ?? (pullFileCommand = new AsyncRelayCommand(PullFileAsync));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand AddSequenceStepCommand => addSequenceStepCommand ?? (addSequenceStepCommand = new RelayCommand(AddSequenceStep));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand<AutomationStep?> RemoveSequenceStepCommand => removeSequenceStepCommand ?? (removeSequenceStepCommand = new RelayCommand<AutomationStep>(RemoveSequenceStep));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand<AutomationStep?> MoveSequenceStepUpCommand => moveSequenceStepUpCommand ?? (moveSequenceStepUpCommand = new RelayCommand<AutomationStep>(MoveSequenceStepUp));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand<AutomationStep?> MoveSequenceStepDownCommand => moveSequenceStepDownCommand ?? (moveSequenceStepDownCommand = new RelayCommand<AutomationStep>(MoveSequenceStepDown));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand PauseSequenceCommand => pauseSequenceCommand ?? (pauseSequenceCommand = new RelayCommand(PauseSequence));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand ResumeSequenceCommand => resumeSequenceCommand ?? (resumeSequenceCommand = new RelayCommand(ResumeSequence));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand StopSequenceCommand => stopSequenceCommand ?? (stopSequenceCommand = new RelayCommand(StopSequence));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand RunSequenceCommand => runSequenceCommand ?? (runSequenceCommand = new AsyncRelayCommand(RunSequenceAsync));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand ShowMoreCommand => showMoreCommand ?? (showMoreCommand = new RelayCommand(ShowMore));

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
		NavigationItems = new global::_003C_003Ez__ReadOnlyArray<string>(new string[14]
		{
			"Overview", "Instances", "Wireless", "Developer Tools", "Media", "Performance", "Logs", "File Explorer", "Network", "ADB Shell", "Automation", "Alerts",
			"Settings", "Help"
		});
		if (isDemo) { TrackedPackages.Add("com.company.mygame"); SelectedPackage = TrackedPackages[0]; }
		PerformanceCharts = new global::_003C_003Ez__ReadOnlyArray<LiveChartViewModel>(new LiveChartViewModel[11]
		{
			CpuChart, DeviceMemoryChart, AppMemoryChart, FpsChart, FrameTimeChart, JankChart, DiskChart, NetworkChart, AppNetworkChart, ThermalChart,
			GpuChart
		});
		StoredSessionCharts = new global::_003C_003Ez__ReadOnlyArray<LiveChartViewModel>(new LiveChartViewModel[6] { StoredCpuChart, StoredMemoryChart, StoredFpsFrameChart, StoredDiskChart, StoredNetworkChart, StoredThermalChart });
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
		foreach (SessionSummary item2 in await _sessions.ListSessionSummariesAsync(CancellationToken.None))
		{
			StoredSessions.Add(item2);
		}
		SelectedStoredSession = StoredSessions.FirstOrDefault();
		await LoadAlertHistoryAsync();
		_ = DiscoveryLoopAsync(_appCts.Token);
	}

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

	private void SelectOverviewTab(string? tab)
	{
		if (!string.IsNullOrWhiteSpace(tab))
		{
			OverviewTab = tab;
		}
	}

	private void ToggleLive()
	{
		IsLive = !IsLive;
	}

	private void ToggleLogPause()
	{
		IsLogPaused = !IsLogPaused;
		LogFilterStatus = (IsLogPaused ? "Log view paused" : "Live logcat");
	}

	private void ClearLogView()
	{
		Logs.Clear();
		LogFilterStatus = "Local log view cleared";
	}

	private void OpenFullLogs()
	{
		CurrentPage = "Logs";
	}

	private void CopySelectedLog()
	{
		if ((object)SelectedLogEntry != null)
		{
			Clipboard.SetText($"{SelectedLogEntry.TimestampUtc:O} [{SelectedLogEntry.Priority}] {SelectedLogEntry.Source}: {SelectedLogEntry.Message}");
		}
	}

	private void ToggleProcessGroup(ProcessDisplayRow? row)
	{
		if ((object)row != null && row.CanExpand)
		{
			if (!_expandedProcessGroups.Add(row.Key))
			{
				_expandedProcessGroups.Remove(row.Key);
			}
			ApplyProcessFilter();
		}
	}

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
		else if ((object)SelectedDevice != null)
		{
			await RefreshProcessesAsync(SelectedDevice, CancellationToken.None);
			StatusMessage = "Current data refreshed safely";
		}
	}

	private void MarkEvent()
	{
		if (_session == null || (object)SelectedDevice == null)
		{
			return;
		}
		(string, string)? tuple = _dialogs.PromptMarker();
		if (!tuple.HasValue)
		{
			return;
		}
		DateTimeOffset utcNow = DateTimeOffset.UtcNow;
		SessionMarker sessionMarker = new SessionMarker(Guid.NewGuid(), _session.Id, utcNow, utcNow.ToLocalTime(), utcNow - _session.StartedUtc, tuple.Value.Item1, tuple.Value.Item2, SelectedDevice.Serial, SelectedDevice.FriendlyName, SelectedPackage);
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

	private async Task ExportStoredSessionAsync(string? format)
	{
		if ((object)SelectedStoredSession == null)
		{
			return;
		}
		MonitoringSession monitoringSession = ((!(LoadedStoredSession?.Id == SelectedStoredSession.Id)) ? (await _sessions.LoadSessionAsync(SelectedStoredSession.Id, CancellationToken.None)) : LoadedStoredSession);
		MonitoringSession session = monitoringSession;
		if (session == null)
		{
			_dialogs.Notify("The selected session could not be loaded.", error: true);
			return;
		}
		string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Android Dev Monitor", "Exports");
		try
		{
			StoredSessionStatus = "Exporting " + (format ?? "ZIP") + "…";
			string text = (format ?? "ZIP").ToUpperInvariant();
			string text2 = ((text == "JSON") ? (await _exporter.ExportJsonAsync(session, directory, CancellationToken.None)) : ((!(text == "CSV")) ? (await _exporter.ExportZipAsync(session, directory, CancellationToken.None)) : (await _exporter.ExportCsvAsync(session, directory, CancellationToken.None))));
			string path = text2;
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

	private void OpenSessionExportFolder()
	{
		string text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Android Dev Monitor", "Exports");
		Directory.CreateDirectory(text);
		Process.Start(new ProcessStartInfo(text)
		{
			UseShellExecute = true
		});
	}

	private async Task DeleteStoredSessionAsync()
	{
		if ((object)SelectedStoredSession != null)
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
			string[] array = await File.ReadAllLinesAsync(picker.FileName);
			foreach (string item in array.TakeLast(500))
			{
				ObservableCollection<LogEntry> logs = Logs;
				DateTimeOffset utcNow = DateTimeOffset.UtcNow;
				string priority = InferLogPriority(item);
				string deviceSerial = SelectedDevice?.Serial ?? "N/A";
				string selectedPackage = SelectedPackage;
				logs.Add(new LogEntry(utcNow, "Appium (external)", priority, item, deviceSerial, null, selectedPackage));
			}
			TrimLogs();
			SelectedLogSource = "Appium (external)";
			LogFilterStatus = $"Imported {Math.Min(array.Length, 500)} lines from {Path.GetFileName(picker.FileName)}";
		}
		catch (Exception ex)
		{
			_dialogs.Notify("Log import failed: " + ex.Message, error: true);
		}
	}

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
			string[] lines = (from LogEntry entry in (IEnumerable)LogView
				select $"{entry.TimestampUtc:O} [{entry.Priority}] [{entry.Source}] {entry.Message}").ToArray();
			await File.WriteAllLinesAsync(saveFileDialog.FileName, lines);
			LogFilterStatus = $"Saved {lines.Length} visible lines";
		}
	}

	private void OpenSelectedMedia()
	{
		if ((object)SelectedMediaItem == null || !File.Exists(SelectedMediaItem.LocalPath))
		{
			_dialogs.Notify("The selected media file no longer exists.", error: true);
			return;
		}
		Process.Start(new ProcessStartInfo(SelectedMediaItem.LocalPath)
		{
			UseShellExecute = true
		});
	}

	private void OpenMediaFolder()
	{
		string text = SelectedMediaItem?.LocalPath;
		if (!string.IsNullOrWhiteSpace(text) && File.Exists(text))
		{
			Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + text + "\"")
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

	private void CopyMediaPath()
	{
		if ((object)SelectedMediaItem != null)
		{
			Clipboard.SetText(SelectedMediaItem.LocalPath);
		}
	}

	private async Task RenameMediaAsync()
	{
		if ((object)SelectedMediaItem == null || string.IsNullOrWhiteSpace(MediaRenameText))
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

	private async Task SaveMediaMetadataAsync()
	{
		if ((object)SelectedMediaItem == null)
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

	private async Task DeleteMediaAsync()
	{
		if ((object)SelectedMediaItem == null)
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

	private async Task ScreenshotAsync()
	{
		if ((object)SelectedDevice == null)
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

	private async Task ToggleRecordingAsync()
	{
		if ((object)SelectedDevice == null)
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

	private async Task RunShellAsync()
	{
		if ((object)SelectedDevice == null || string.IsNullOrWhiteSpace(ShellCommand) || IsDemo || IsShellRunning)
		{
			ShellOutput = (IsDemo ? "ADB Shell is disabled in demo mode; no command was executed." : (((object)SelectedDevice == null) ? "Select a connected device." : ShellOutput));
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
			AdbCommandResult adbCommandResult = await _adb.ExecuteAsync(target.Serial, new global::_003C_003Ez__ReadOnlyArray<string>(new string[4] { "shell", "sh", "-c", command }), TimeSpan.FromSeconds(30L), _shellCts.Token);
			TrackAdbCommand(adbCommandResult);
			ShellOutput = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {target.FriendlyName} ({target.Serial})\n$ {command}\nExit: {adbCommandResult.ExitCode?.ToString() ?? "N/A"} · {adbCommandResult.Duration.TotalMilliseconds:N0} ms{(adbCommandResult.TimedOut ? " · TIMEOUT" : "")}{(adbCommandResult.Cancelled ? " · CANCELLED" : "")}\n\n{adbCommandResult.StandardOutput}{(string.IsNullOrWhiteSpace(adbCommandResult.StandardError) ? "" : ("\nSTDERR:\n" + adbCommandResult.StandardError))}";
			ShellStatus = (adbCommandResult.Success ? "Completed" : (adbCommandResult.Cancelled ? "Cancelled" : (adbCommandResult.TimedOut ? "Timed out" : ("Failed · exit " + (adbCommandResult.ExitCode?.ToString() ?? "N/A")))));
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

	private void CancelShell()
	{
		_shellCts?.Cancel();
	}

	private void ClearShellOutput()
	{
		ShellOutput = "";
		ShellStatus = "Local output cleared";
	}

	private void ApplyShellPreset()
	{
		if (!string.IsNullOrWhiteSpace(SelectedShellPreset))
		{
			ShellCommand = SelectedShellPreset;
		}
	}

	private void PreviousShellCommand()
	{
		if (ShellHistory.Count != 0)
		{
			_shellHistoryIndex = Math.Min(ShellHistory.Count - 1, _shellHistoryIndex + 1);
			ShellCommand = ShellHistory[_shellHistoryIndex];
		}
	}

	private void NextShellCommand()
	{
		if (ShellHistory.Count != 0)
		{
			_shellHistoryIndex = Math.Max(-1, _shellHistoryIndex - 1);
			ShellCommand = ((_shellHistoryIndex < 0) ? "" : ShellHistory[_shellHistoryIndex]);
		}
	}

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

	private async Task RunAutomationAsync(string? action)
	{
		if ((object)SelectedDevice != null && !string.IsNullOrWhiteSpace(action))
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
		string text = action;
		bool flag = ((text == "Clear Data" || text == "Uninstall") ? true : false);
		if (flag && !confirmationHandled && !_dialogs.Confirm(action, $"Device: {target.FriendlyName}\nSerial: {serial}\nPackage: {package}\n\nExact effect: {action} for this package on this device."))
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
			MediaItem mediaItem2 = await _media.CaptureScreenshotAsync(target, package, _session?.Id, token);
			AddMediaItem(mediaItem2);
			AppendAutomationLog("Screenshot saved: " + mediaItem2.FileName + " · " + serial);
			return true;
		}
		case "Start Recording":
			if (IsRecording)
			{
				return AutomationFailure(
					"Recording is already active on " +
					(_recordingTarget?.Serial ?? "another target") + ".");
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
				return AutomationFailure(
					"No recording started by this target is active. Current recording target: " +
					(_recordingTarget?.Serial ?? "none") + ".");
			}
			MediaItem mediaItem = await _media.StopRecordingAsync(
				_recordingTarget,
				_recordingPackage,
				_recordingSessionId,
				token);
			AddMediaItem(mediaItem);
			IsRecording = false;
			ClearRecordingContext();
			AppendAutomationLog("Recording saved: " + mediaItem.FileName + " · " + serial);
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
			AdbCommandResult stop = await _adb.ExecuteAsync(serial, new global::_003C_003Ez__ReadOnlyArray<string>(new string[4] { "shell", "am", "force-stop", package }), TimeSpan.FromSeconds(20L), token);
			AdbCommandResult adbCommandResult3 = await _adb.ExecuteAsync(serial, new global::_003C_003Ez__ReadOnlyArray<string>(new string[7] { "shell", "monkey", "-p", package, "-c", "android.intent.category.LAUNCHER", "1" }), TimeSpan.FromSeconds(30L), token);
			AppendAutomationResult(action, target, package, stop, $"Force stop exit {stop.ExitCode}\nLaunch exit {adbCommandResult3.ExitCode}\n{adbCommandResult3.StandardOutput}\n{adbCommandResult3.StandardError}");
			return stop.Success && adbCommandResult3.Success;
		}
		case "Double Tap":
		{
			(int, int) tuple3 = ParseCoordinates(argument ?? AutomationCoordinates, target);
			int x = tuple3.Item1;
			int y = tuple3.Item2;
			AdbCommandResult stop = await _adb.ExecuteAsync(serial, new global::_003C_003Ez__ReadOnlyArray<string>(new string[5]
			{
				"shell",
				"input",
				"tap",
				x.ToString(CultureInfo.InvariantCulture),
				y.ToString(CultureInfo.InvariantCulture)
			}), TimeSpan.FromSeconds(15L), token);
			await Task.Delay(90, token);
			AdbCommandResult adbCommandResult2 = await _adb.ExecuteAsync(serial, new global::_003C_003Ez__ReadOnlyArray<string>(new string[5]
			{
				"shell",
				"input",
				"tap",
				x.ToString(CultureInfo.InvariantCulture),
				y.ToString(CultureInfo.InvariantCulture)
			}), TimeSpan.FromSeconds(15L), token);
			AppendAutomationResult(action, target, package, adbCommandResult2, $"First tap exit {stop.ExitCode}\nSecond tap exit {adbCommandResult2.ExitCode}");
			return stop.Success && adbCommandResult2.Success;
		}
		default:
		{
			(int X, int Y) tuple = ParseCoordinates(argument ?? AutomationCoordinates, target);
			int item = tuple.X;
			int item2 = tuple.Y;
			(int Width, int Height) tuple2 = ParseResolution(target.Resolution);
			int item3 = tuple2.Width;
			int item4 = tuple2.Height;
			int num = Math.Max(120, item3 * 35 / 100);
			int num2 = Math.Max(180, item4 * 35 / 100);
			string durationMs = Math.Clamp(SwipeDurationMs, 50, 5000).ToString(CultureInfo.InvariantCulture);
			IReadOnlyList<string> readOnlyList;
			switch (action)
			{
			case "Install APK":
				if (!string.IsNullOrWhiteSpace(apkPath) && File.Exists(apkPath))
				{
					readOnlyList = new global::_003C_003Ez__ReadOnlyArray<string>(new string[3] { "install", "-r", apkPath });
					break;
				}
				goto default;
			case "Launch":
				if (!string.IsNullOrWhiteSpace(package))
				{
					readOnlyList = new global::_003C_003Ez__ReadOnlyArray<string>(new string[7] { "shell", "monkey", "-p", package, "-c", "android.intent.category.LAUNCHER", "1" });
					break;
				}
				goto default;
			case "Force Stop":
				if (!string.IsNullOrWhiteSpace(package))
				{
					readOnlyList = new global::_003C_003Ez__ReadOnlyArray<string>(new string[4] { "shell", "am", "force-stop", package });
					break;
				}
				goto default;
			case "Clear Data":
				if (!string.IsNullOrWhiteSpace(package))
				{
					readOnlyList = new global::_003C_003Ez__ReadOnlyArray<string>(new string[4] { "shell", "pm", "clear", package });
					break;
				}
				goto default;
			case "Uninstall":
				if (!string.IsNullOrWhiteSpace(package))
				{
					readOnlyList = new global::_003C_003Ez__ReadOnlyArray<string>(new string[2] { "uninstall", package });
					break;
				}
				goto default;
			case "App Info":
				if (!string.IsNullOrWhiteSpace(package))
				{
					readOnlyList = new global::_003C_003Ez__ReadOnlyArray<string>(new string[7]
					{
						"shell",
						"am",
						"start",
						"-a",
						"android.settings.APPLICATION_DETAILS_SETTINGS",
						"-d",
						"package:" + package
					});
					break;
				}
				goto default;
			case "Grant Permission":
				if (!string.IsNullOrWhiteSpace(package) && !string.IsNullOrWhiteSpace(argument ?? AutomationText))
				{
					readOnlyList = new global::_003C_003Ez__ReadOnlyArray<string>(new string[5]
					{
						"shell",
						"pm",
						"grant",
						package,
						(argument ?? AutomationText).Trim()
					});
					break;
				}
				goto default;
			case "Revoke Permission":
				if (!string.IsNullOrWhiteSpace(package) && !string.IsNullOrWhiteSpace(argument ?? AutomationText))
				{
					readOnlyList = new global::_003C_003Ez__ReadOnlyArray<string>(new string[5]
					{
						"shell",
						"pm",
						"revoke",
						package,
						(argument ?? AutomationText).Trim()
					});
					break;
				}
				goto default;
			case "Back":
				readOnlyList = KeyEvent("4");
				break;
			case "Home":
				readOnlyList = KeyEvent("3");
				break;
			case "Recents":
				readOnlyList = KeyEvent("187");
				break;
			case "Power":
				readOnlyList = KeyEvent("26");
				break;
			case "Volume Up":
				readOnlyList = KeyEvent("24");
				break;
			case "Volume Down":
				readOnlyList = KeyEvent("25");
				break;
			case "Enter":
				readOnlyList = KeyEvent("66");
				break;
			case "Escape":
				readOnlyList = KeyEvent("111");
				break;
			case "Tap Center":
				readOnlyList = Tap(item3 / 2, item4 / 2);
				break;
			case "Tap Coordinates":
				readOnlyList = Tap(item, item2);
				break;
			case "Long Press":
				readOnlyList = new global::_003C_003Ez__ReadOnlyArray<string>(new string[8]
				{
					"shell",
					"input",
					"swipe",
					item.ToString(CultureInfo.InvariantCulture),
					item2.ToString(CultureInfo.InvariantCulture),
					item.ToString(CultureInfo.InvariantCulture),
					item2.ToString(CultureInfo.InvariantCulture),
					"800"
				});
				break;
			case "Swipe Up":
				readOnlyList = Swipe(item, Math.Min(item4 - 1, item2 + num2), item, Math.Max(0, item2 - num2), durationMs);
				break;
			case "Swipe Down":
				readOnlyList = Swipe(item, Math.Max(0, item2 - num2), item, Math.Min(item4 - 1, item2 + num2), durationMs);
				break;
			case "Swipe Left":
				readOnlyList = Swipe(Math.Min(item3 - 1, item + num), item2, Math.Max(0, item - num), item2, durationMs);
				break;
			case "Swipe Right":
				readOnlyList = Swipe(Math.Max(0, item - num), item2, Math.Min(item3 - 1, item + num), item2, durationMs);
				break;
			case "Send Text":
				if (!string.IsNullOrWhiteSpace(argument ?? AutomationText))
				{
					readOnlyList = new global::_003C_003Ez__ReadOnlyArray<string>(new string[4]
					{
						"shell",
						"input",
						"text",
						EscapeAndroidInputText(argument ?? AutomationText)
					});
					break;
				}
				goto default;
			case "Portrait":
				readOnlyList = new global::_003C_003Ez__ReadOnlyArray<string>(new string[6] { "shell", "settings", "put", "system", "user_rotation", "0" });
				break;
			case "Landscape Left":
				readOnlyList = new global::_003C_003Ez__ReadOnlyArray<string>(new string[6] { "shell", "settings", "put", "system", "user_rotation", "1" });
				break;
			case "Landscape Right":
				readOnlyList = new global::_003C_003Ez__ReadOnlyArray<string>(new string[6] { "shell", "settings", "put", "system", "user_rotation", "3" });
				break;
			case "Auto Rotation":
				readOnlyList = new global::_003C_003Ez__ReadOnlyArray<string>(new string[6] { "shell", "settings", "put", "system", "accelerometer_rotation", "1" });
				break;
			default:
				readOnlyList = Array.Empty<string>();
				break;
			}
			IReadOnlyList<string> args = readOnlyList;
			if (args.Count == 0)
			{
				return AutomationFailure((action == "Install APK") ? "Select an APK first; selection never installs automatically." : (action + " requires a selected package or valid argument."));
			}
			switch (action)
			{
			case "Portrait":
			case "Landscape Left":
			case "Landscape Right":
				flag = true;
				break;
			default:
				flag = false;
				break;
			}
			if (flag)
			{
				await _adb.ExecuteAsync(serial, new global::_003C_003Ez__ReadOnlyArray<string>(new string[6] { "shell", "settings", "put", "system", "accelerometer_rotation", "0" }), TimeSpan.FromSeconds(15L), token);
			}
			TimeSpan timeout = ((action == "Install APK") ? TimeSpan.FromMinutes(5L) : TimeSpan.FromSeconds(30L));
			AdbCommandResult adbCommandResult = await _adb.ExecuteAsync(serial, args, timeout, token);
			AppendAutomationResult(action, target, package, adbCommandResult);
			return adbCommandResult.Success;
		}
		}
		static IReadOnlyList<string> KeyEvent(string code)
		{
			return new global::_003C_003Ez__ReadOnlyArray<string>(new string[4] { "shell", "input", "keyevent", code });
		}
		static IReadOnlyList<string> Swipe(int num3, int num4, int num5, int num6, string text2)
		{
			return new global::_003C_003Ez__ReadOnlyArray<string>(new string[8]
			{
				"shell",
				"input",
				"swipe",
				num3.ToString(CultureInfo.InvariantCulture),
				num4.ToString(CultureInfo.InvariantCulture),
				num5.ToString(CultureInfo.InvariantCulture),
				num6.ToString(CultureInfo.InvariantCulture),
				text2
			});
		}
		static IReadOnlyList<string> Tap(int num3, int num4)
		{
			return new global::_003C_003Ez__ReadOnlyArray<string>(new string[5]
			{
				"shell",
				"input",
				"tap",
				num3.ToString(CultureInfo.InvariantCulture),
				num4.ToString(CultureInfo.InvariantCulture)
			});
		}
	}

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

	private void OpenSelectedLocal()
	{
		FileEntry? selectedLocalFile = SelectedLocalFile;
		if ((object)selectedLocalFile != null && selectedLocalFile.IsDirectory)
		{
			NavigateLocalPath(SelectedLocalFile.FullPath, recordHistory: true);
		}
	}

	private void LocalUp()
	{
		string text = Directory.GetParent(LocalPath)?.FullName;
		if (text != null)
		{
			NavigateLocalPath(text, recordHistory: true);
		}
	}

	private void LocalBack()
	{
		if (_localBackHistory.Count != 0)
		{
			_localForwardHistory.Push(LocalPath);
			NavigateLocalPath(_localBackHistory.Pop(), recordHistory: false);
		}
	}

	private void LocalForward()
	{
		if (_localForwardHistory.Count != 0)
		{
			_localBackHistory.Push(LocalPath);
			NavigateLocalPath(_localForwardHistory.Pop(), recordHistory: false);
		}
	}

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

	private async Task OpenSelectedRemoteAsync()
	{
		if (SelectedRemoteFile?.IsDirectory ?? false)
		{
			await NavigateRemotePathAsync(SelectedRemoteFile.FullPath, recordHistory: true);
		}
	}

	private async Task RemoteUpAsync()
	{
		string text = RemotePath.TrimEnd('/');
		int num = text.LastIndexOf('/');
		await NavigateRemotePathAsync((num <= 0) ? "/" : text.Substring(0, num), recordHistory: true);
	}

	private async Task RemoteBackAsync()
	{
		if (_remoteBackHistory.Count != 0)
		{
			_remoteForwardHistory.Push(RemotePath);
			await NavigateRemotePathAsync(_remoteBackHistory.Pop(), recordHistory: false);
		}
	}

	private async Task RemoteForwardAsync()
	{
		if (_remoteForwardHistory.Count != 0)
		{
			_remoteBackHistory.Push(RemotePath);
			await NavigateRemotePathAsync(_remoteForwardHistory.Pop(), recordHistory: false);
		}
	}

	private async Task GoRemoteQuickLocationAsync()
	{
		await NavigateRemotePathAsync(SelectedRemoteQuickLocation, recordHistory: true);
	}

	private async Task CreateRemoteFolderAsync()
	{
		if ((object)SelectedDevice == null || IsDemo || !IsSafeRemoteName(NewRemoteFolderName))
		{
			StatusMessage = "Enter a valid folder name without slashes.";
			return;
		}
		string path = CombineRemote(RemotePath, NewRemoteFolderName.Trim());
		AdbCommandResult adbCommandResult = await _adb.ExecuteAsync(SelectedDevice.Serial, new global::_003C_003Ez__ReadOnlyArray<string>(new string[4] { "shell", "mkdir", "--", path }), TimeSpan.FromSeconds(20L), CancellationToken.None);
		StatusMessage = (adbCommandResult.Success ? ("Created " + path) : ("Create folder failed: " + adbCommandResult.StandardError.Trim()));
		if (adbCommandResult.Success)
		{
			await RefreshFileExplorerAsync();
		}
	}

	private async Task RenameRemoteAsync()
	{
		if ((object)SelectedDevice == null || (object)SelectedRemoteFile == null || IsDemo || !IsSafeRemoteName(RemoteRenameText))
		{
			StatusMessage = "Select an item and enter a valid new name.";
			return;
		}
		string text = CombineRemote(RemotePath, RemoteRenameText.Trim());
		AdbCommandResult adbCommandResult = await _adb.ExecuteAsync(SelectedDevice.Serial, new global::_003C_003Ez__ReadOnlyArray<string>(new string[5] { "shell", "mv", "--", SelectedRemoteFile.FullPath, text }), TimeSpan.FromSeconds(30L), CancellationToken.None);
		StatusMessage = (adbCommandResult.Success ? ("Renamed to " + RemoteRenameText.Trim()) : ("Rename failed: " + adbCommandResult.StandardError.Trim()));
		if (adbCommandResult.Success)
		{
			await RefreshFileExplorerAsync();
		}
	}

	private async Task DeleteRemoteAsync()
	{
		if ((object)SelectedDevice == null || (object)SelectedRemoteFile == null || IsDemo)
		{
			return;
		}
		FileEntry entry = SelectedRemoteFile;
		if (_dialogs.Confirm("Delete device item", $"Device: {SelectedDevice.FriendlyName}\nSerial: {SelectedDevice.Serial}\nPath: {entry.FullPath}\n\nThis permanently deletes the selected {(entry.IsDirectory ? "folder and its contents" : "file")}."))
		{
			IAdbExecutor adb = _adb;
			string serial = SelectedDevice.Serial;
			IReadOnlyList<string> arguments;
			if (!entry.IsDirectory)
			{
				IReadOnlyList<string> readOnlyList = new global::_003C_003Ez__ReadOnlyArray<string>(new string[5] { "shell", "rm", "-f", "--", entry.FullPath });
				arguments = readOnlyList;
			}
			else
			{
				IReadOnlyList<string> readOnlyList = new global::_003C_003Ez__ReadOnlyArray<string>(new string[5] { "shell", "rm", "-rf", "--", entry.FullPath });
				arguments = readOnlyList;
			}
			AdbCommandResult adbCommandResult = await adb.ExecuteAsync(serial, arguments, TimeSpan.FromMinutes(1L), CancellationToken.None);
			StatusMessage = (adbCommandResult.Success ? ("Deleted " + entry.Name) : ("Delete failed: " + adbCommandResult.StandardError.Trim()));
			if (adbCommandResult.Success)
			{
				await RefreshFileExplorerAsync();
			}
		}
	}

	private void CancelTransfer()
	{
		_transferCts?.Cancel();
	}

	private async Task RefreshFileExplorerAsync()
	{
		LoadLocalFiles();
		RemoteFiles.Clear();
		if ((object)SelectedDevice == null || IsDemo)
		{
			StatusMessage = (IsDemo ? "Remote file browsing is disabled in demo mode" : "Select a device");
			return;
		}
		AdbCommandResult adbCommandResult = await _adb.ExecuteAsync(SelectedDevice.Serial, new global::_003C_003Ez__ReadOnlyArray<string>(new string[5] { "shell", "ls", "-la", "--", RemotePath }), TimeSpan.FromSeconds(15L), CancellationToken.None);
		if (!adbCommandResult.Success)
		{
			StatusMessage = "Remote path inaccessible: " + adbCommandResult.StandardError.Trim();
			return;
		}
		string[] array = adbCommandResult.StandardOutput.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
		for (int i = 0; i < array.Length; i++)
		{
			Match match = Regex.Match(array[i], "^(?<kind>[d-])\\S*\\s+\\d+\\s+\\S+\\s+\\S+\\s+(?<size>\\d+)\\s+\\S+\\s+\\S+\\s+\\S+\\s+(?<name>.+)$");
			if (match.Success)
			{
				string value = match.Groups["name"].Value;
				if ((!(value == ".") && !(value == "..")) || 1 == 0)
				{
					RemoteFiles.Add(new FileEntry(value, RemotePath.TrimEnd('/') + "/" + value, match.Groups["kind"].Value == "d", long.TryParse(match.Groups["size"].Value, out var result) ? new long?(result) : ((long?)null), null));
				}
			}
		}
		StatusMessage = $"Loaded {RemoteFiles.Count} entries from {RemotePath}";
	}

	private async Task PushFileAsync()
	{
		if ((object)SelectedDevice == null || (object)SelectedLocalFile == null || SelectedLocalFile.IsDirectory || IsDemo)
		{
			return;
		}
		AndroidDevice target = SelectedDevice;
		FileEntry file = SelectedLocalFile;
		string text = CombineRemote(RemotePath, file.Name);
		_transferCts?.Dispose();
		_transferCts = new CancellationTokenSource();
		IsTransferRunning = true;
		TransferStatus = $"Pushing {file.Name} to {target.Serial}…";
		try
		{
			AdbCommandResult adbCommandResult = await _adb.ExecuteAsync(target.Serial, new global::_003C_003Ez__ReadOnlyArray<string>(new string[3] { "push", file.FullPath, text }), TimeSpan.FromMinutes(5L), _transferCts.Token);
			TransferStatus = (adbCommandResult.Success ? ("Push complete · " + file.Name) : (adbCommandResult.Cancelled ? "Push cancelled" : ("Push failed · " + adbCommandResult.StandardError.Trim())));
			StatusMessage = TransferStatus;
			if (adbCommandResult.Success && SelectedDevice?.Serial == target.Serial)
			{
				await RefreshFileExplorerAsync();
			}
		}
		catch (OperationCanceledException)
		{
			TransferStatus = "Push cancelled";
			StatusMessage = TransferStatus;
		}
		catch (Exception ex2)
		{
			TransferStatus = "Push failed · " + ex2.Message;
			StatusMessage = TransferStatus;
		}
		finally
		{
			IsTransferRunning = false;
		}
	}

	private async Task PullFileAsync()
	{
		if ((object)SelectedDevice == null || (object)SelectedRemoteFile == null || SelectedRemoteFile.IsDirectory || IsDemo)
		{
			return;
		}
		AndroidDevice selectedDevice = SelectedDevice;
		FileEntry file = SelectedRemoteFile;
		string localPath = LocalPath;
		_transferCts?.Dispose();
		_transferCts = new CancellationTokenSource();
		IsTransferRunning = true;
		TransferStatus = $"Pulling {file.Name} from {selectedDevice.Serial}…";
		try
		{
			AdbCommandResult adbCommandResult = await _adb.ExecuteAsync(selectedDevice.Serial, new global::_003C_003Ez__ReadOnlyArray<string>(new string[3] { "pull", file.FullPath, localPath }), TimeSpan.FromMinutes(5L), _transferCts.Token);
			TransferStatus = (adbCommandResult.Success ? ("Pull complete · " + file.Name) : (adbCommandResult.Cancelled ? "Pull cancelled" : ("Pull failed · " + adbCommandResult.StandardError.Trim())));
			StatusMessage = TransferStatus;
			if (adbCommandResult.Success)
			{
				LoadLocalFiles();
			}
		}
		catch (OperationCanceledException)
		{
			TransferStatus = "Pull cancelled";
			StatusMessage = TransferStatus;
		}
		catch (Exception ex2)
		{
			TransferStatus = "Pull failed · " + ex2.Message;
			StatusMessage = TransferStatus;
		}
		finally
		{
			IsTransferRunning = false;
		}
	}

	private void AddSequenceStep()
	{
		TimeSpan? duration = ((SelectedStepKind == AutomationStepKind.Wait) ? new TimeSpan?(TimeSpan.FromMilliseconds(Math.Clamp(NewStepDurationMs, 0, 300000))) : ((TimeSpan?)null));
		SequenceSteps.Add(new AutomationStep(Guid.NewGuid(), SelectedStepKind, StepDisplayName(SelectedStepKind), string.IsNullOrWhiteSpace(NewStepArgument) ? null : NewStepArgument.Trim(), duration, SelectedStepKind == AutomationStepKind.ClearData));
	}

	private void RemoveSequenceStep(AutomationStep? step)
	{
		if ((object)step != null)
		{
			SequenceSteps.Remove(step);
		}
	}

	private void MoveSequenceStepUp(AutomationStep? step)
	{
		if ((object)step != null)
		{
			int num = SequenceSteps.IndexOf(step);
			if (num > 0)
			{
				SequenceSteps.Move(num, num - 1);
			}
		}
	}

	private void MoveSequenceStepDown(AutomationStep? step)
	{
		if ((object)step != null)
		{
			int num = SequenceSteps.IndexOf(step);
			if (num >= 0 && num < SequenceSteps.Count - 1)
			{
				SequenceSteps.Move(num, num + 1);
			}
		}
	}

	private void PauseSequence()
	{
		if (IsAutomationRunning)
		{
			IsAutomationPaused = true;
			AutomationState = "Paused";
		}
	}

	private void ResumeSequence()
	{
		if (IsAutomationRunning)
		{
			IsAutomationPaused = false;
			AutomationState = "Running";
		}
	}

	private void StopSequence()
	{
		_automationCts?.Cancel();
	}

	private async Task RunSequenceAsync()
	{
		if ((object)SelectedDevice == null || SequenceSteps.Count == 0 || IsAutomationRunning)
		{
			return;
		}
		AndroidDevice target = SelectedDevice;
		string package = SelectedPackage;
		string apkPath = SelectedApkPath;
		AutomationStep[] steps = SequenceSteps.ToArray();
		string[] array = (from automationStep in steps
			where automationStep.IsDestructive
			select automationStep.Name).ToArray();
		TimeSpan value = TimeSpan.FromMilliseconds(steps.Sum((AutomationStep automationStep) => automationStep.Duration?.TotalMilliseconds ?? 500.0) * (double)Math.Clamp(AutomationRepeatCount, 1, 100));
		if (!_dialogs.Confirm("Run automation sequence", $"Device: {target.FriendlyName}\nSerial: {target.Serial}\nPackage: {package}\nSteps: {steps.Length}\nRepeat: {Math.Clamp(AutomationRepeatCount, 1, 100)}\nEstimated: {value:g}\nDestructive: {((array.Length == 0) ? "None" : string.Join(", ", array))}"))
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
				AutomationStep[] array2 = steps;
				foreach (AutomationStep step in array2)
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
		catch (Exception ex2)
		{
			AutomationState = "Failed: " + ex2.Message;
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
				RaiseAlert("RecordingFailed", "Critical", "Screen recording failed during Automation: " + ex2.Message);
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
			TimeSpan timeSpan = step.Duration ?? TimeSpan.FromSeconds(30L);
			DateTimeOffset deadline = DateTimeOffset.UtcNow + timeSpan;
			while (DateTimeOffset.UtcNow < deadline)
			{
				token.ThrowIfCancellationRequested();
				if (Logs.Any((LogEntry entry) => entry.Message.Contains(text, StringComparison.OrdinalIgnoreCase)))
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
			string expected = step.Argument ?? package;
			DateTimeOffset deadline = DateTimeOffset.UtcNow + (step.Duration ?? TimeSpan.FromSeconds(30L));
			while (DateTimeOffset.UtcNow < deadline)
			{
				AdbCommandResult adbCommandResult = await _adb.ExecuteAsync(target.Serial, new global::_003C_003Ez__ReadOnlyArray<string>(new string[4] { "shell", "dumpsys", "activity", "activities" }), TimeSpan.FromSeconds(10L), token);
				if (!string.IsNullOrWhiteSpace(expected) && adbCommandResult.StandardOutput.Contains(expected, StringComparison.Ordinal))
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
			string expected = step.Argument ?? package;
			if (string.IsNullOrWhiteSpace(expected))
			{
				return AutomationFailure("Check process requires a package.");
			}
			AdbCommandResult adbCommandResult2 = await _adb.ExecuteAsync(target.Serial, new global::_003C_003Ez__ReadOnlyArray<string>(new string[3] { "shell", "pidof", expected }), TimeSpan.FromSeconds(15L), token);
			AppendAutomationResult("Check Process", target, expected, adbCommandResult2);
			return adbCommandResult2.Success && !string.IsNullOrWhiteSpace(adbCommandResult2.StandardOutput);
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
		string text = string.Join('\n', new string[3]
		{
			extra,
			result.StandardOutput.Trim(),
			result.StandardError.Trim()
		}.Where((string value) => !string.IsNullOrWhiteSpace(value)));
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
			string automationLog = AutomationLog;
			AutomationLog = automationLog.Substring(automationLog.Length - 30000);
		}
	}

	private bool AutomationFailure(string message)
	{
		AppendAutomationLog("FAILED · " + message);
		return false;
	}

	private static (int X, int Y) ParseCoordinates(string value, AndroidDevice target)
	{
		(int Width, int Height) tuple = ParseResolution(target.Resolution);
		int item = tuple.Width;
		int item2 = tuple.Height;
		int[] array = (from match in Regex.Matches(value ?? "", "-?\\d+")
			select int.Parse(match.Value, CultureInfo.InvariantCulture)).Take(2).ToArray();
		if (array.Length != 2)
		{
			return (X: item / 2, Y: item2 / 2);
		}
		return (X: Math.Clamp(array[0], 0, item - 1), Y: Math.Clamp(array[1], 0, item2 - 1));
	}

	private static (int Width, int Height) ParseResolution(string resolution)
	{
		Match match = Regex.Match(resolution ?? "", "(?<width>\\d+)x(?<height>\\d+)");
		if (!match.Success || !int.TryParse(match.Groups["width"].Value, out var result) || !int.TryParse(match.Groups["height"].Value, out var result2))
		{
			return (Width: 1080, Height: 1920);
		}
		return (Width: Math.Max(1, result), Height: Math.Max(1, result2));
	}

	private static string EscapeAndroidInputText(string value)
	{
		return value.Replace("%", "\\%").Replace(" ", "%s");
	}

	private static string StepDisplayName(AutomationStepKind kind)
	{
		return Regex.Replace(kind.ToString(), "([a-z])([A-Z])", "$1 $2");
	}

	private void ShowMore()
	{
		_dialogs.Notify($"Device: {DeviceContext}\nADB: {AdbPathDisplay}\nMedia: {_media.MediaDirectory}\nSession: {_session?.Id.ToString() ?? "None"}");
	}

	private async Task RefreshDevicesAsync()
	{
		if (!(await _discoveryGate.WaitAsync(0)))
		{
			return;
		}
		try
		{
			ConnectionText = "Connecting";
			IReadOnlyList<AndroidDevice> readOnlyList = await _devicesService.DiscoverAsync(CancellationToken.None);
			List<AndroidDevice> enriched = (await Task.WhenAll(
				readOnlyList.Select(item => _devicesService.EnrichAsync(item, CancellationToken.None)))).ToList();
			AndroidDevice previousDevice = null;
			AndroidDevice selectedAfterRefresh = null;
			bool contextChanged = false;
			await ((DispatcherObject)Application.Current).Dispatcher.InvokeAsync((Action)delegate
			{
				AndroidDevice selectedDevice = SelectedDevice;
				previousDevice = selectedDevice;
				string previous = selectedDevice?.Serial;
				if (selectedDevice is not null &&
					!enriched.Any(item => item.Serial == selectedDevice.Serial))
				{
					RaiseAlert(
						"DeviceDisconnected",
						"Critical",
						$"{selectedDevice.FriendlyName} ({selectedDevice.Serial}) disconnected.");
				}
				AndroidDevice androidDevice =
					enriched.FirstOrDefault((AndroidDevice x) => x.Serial == previous) ??
					enriched.FirstOrDefault((AndroidDevice x) =>
						RestoreLastDevice && x.Serial == _preferredDeviceSerial) ??
					enriched.FirstOrDefault((AndroidDevice x) => x.IsConnected) ??
					enriched.FirstOrDefault();
				contextChanged =
					selectedDevice?.Serial != androidDevice?.Serial ||
					selectedDevice?.State != androidDevice?.State;
				_suppressDeviceSwitch = true;
				try
				{
					Devices.Clear();
					foreach (AndroidDevice item2 in enriched)
					{
						Devices.Add(item2);
					}
					SelectedDevice = androidDevice;
				}
				finally
				{
					_suppressDeviceSwitch = false;
				}
				selectedAfterRefresh = androidDevice;
				ConnectionText = ((Devices.Count != 0) ? $"{Devices.Count((AndroidDevice x) => x.IsConnected)} connected" : ((_adb.ResolvedAdbPath == null && !IsDemo) ? "ADB unavailable" : "No devices"));
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
			else if ((object)SelectedDevice == null)
			{
				StatusMessage = ((_adb.ResolvedAdbPath == null && !IsDemo) ? "ADB executable was not found. Configure it in Settings or use --demo." : "No Android devices found");
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
		_lastSampleUtc = default(DateTimeOffset);
		_lastNetworkTotalTimestamp = default(DateTimeOffset);
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
		ConnectionText = ((device.State == DeviceState.Connected) ? "ADB connected" : device.State.ToString());
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
		if (_session != null && (object)SelectedDevice != null)
		{
			_contextCts?.Cancel();
			_contextCts?.Dispose();
			_contextCts = new CancellationTokenSource();
			CancellationToken token = _contextCts.Token;
			_lastSampleUtc = default(DateTimeOffset);
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
				Guid id = session.Id;
				Guid? obj = _session?.Id;
				if (id != obj || sample.DeviceSerial != device.Serial)
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
				await ((DispatcherObject)Application.Current).Dispatcher.InvokeAsync((Action)delegate
				{
					ApplySample(sample);
				});
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
		catch (Exception ex2)
		{
			Exception ex3 = ex2;
			await ((DispatcherObject)Application.Current).Dispatcher.InvokeAsync<string>((Func<string>)(() => StatusMessage = "Collector error: " + ex3.Message));
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
		_ = 1;
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
		List<AndroidProcess> ordered = (from x in source
			orderby x.CpuPercent ?? (-1.0) descending, GroupRank(x.Group)
			select x).ToList();
		string foreground = IsDemo ? ordered.FirstOrDefault(p => p.PackageName != null)?.PackageName : null;
		if (!IsDemo)
		{
			AdbCommandResult adbCommandResult = await _adb.ExecuteAsync(device.Serial, new global::_003C_003Ez__ReadOnlyArray<string>(new string[4] { "shell", "dumpsys", "activity", "activities" }), TimeSpan.FromSeconds(10L), token);
			if (adbCommandResult.Success)
			{
				foreground = AndroidDevMonitor.Adb.Parsers.ForegroundParser.Parse(adbCommandResult.StandardOutput);
			}
		}
		string[] installed = null;
		if (_installedPackagesSerial != device.Serial)
		{
			if (IsDemo) installed = ordered
				.Where(p => p.Group == ProcessGroup.Apps || p.PackageName is "com.android.chrome" or "com.android.vending")
				.Select(p => p.PackageName).Where(p => p != null).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
			else
			{
				var packages = await _adb.ExecuteAsync(device.Serial, new[] { "shell", "cmd", "package", "query-activities", "--brief", "--components", "--user", "current", "-a", "android.intent.action.MAIN", "-c", "android.intent.category.LAUNCHER" }, TimeSpan.FromSeconds(10), token);
				if (packages.Success) installed = packages.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
					.Where(p => Regex.IsMatch(p, @"^[A-Za-z_][A-Za-z0-9_.]*/[A-Za-z0-9_.$]+$", RegexOptions.CultureInvariant))
					.Select(p => p[..p.IndexOf('/')]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
			}
		}
		await ((DispatcherObject)Application.Current).Dispatcher.InvokeAsync((Action)delegate
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
			if (ordered.Any((AndroidProcess process) => process.CpuPercent.HasValue))
			{
				_processSnapshotCpuTotal = Math.Clamp(ordered.Sum((AndroidProcess process) => process.CpuPercent.GetValueOrDefault()), 0.0, 100.0);
			}
			else
			{
				_processSnapshotCpuTotal = null;
			}
			if (!IsProcessTablePaused)
			{
				ApplyProcessSnapshot(ordered);
				ApplyProcessFilter();
			}
			EvaluateSelectedProcessAlert(ordered);
			foreach (string item in (from x in ordered
				select x.PackageName into x
				where x != null
				select x).Distinct())
			{
				if (!TrackedPackages.Contains(item))
				{
					TrackedPackages.Add(item);
				}
			}
			if (_packageAutoSelectedForSerial != device.Serial && !string.IsNullOrWhiteSpace(foreground) && TrackedPackages.Contains(foreground))
			{
				_packageAutoSelectedForSerial = device.Serial;
				SelectedPackage = foreground;
			}
		}, (DispatcherPriority)4);
	}

	private async Task TimerLoopAsync(MonitoringSession session, CancellationToken token)
	{
		using PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromSeconds(1L));
		_ = 1;
		try
		{
			while (await timer.WaitForNextTickAsync(token))
			{
				await ((DispatcherObject)Application.Current).Dispatcher.InvokeAsync((Action)delegate
				{
					SessionTime = (DateTimeOffset.UtcNow - session.StartedUtc).ToString("hh\\:mm\\:ss");
					bool waitingForFirstSample = _lastSampleUtc == default(DateTimeOffset);
					double num = waitingForFirstSample
						? (DateTimeOffset.UtcNow - session.StartedUtc).TotalSeconds
						: (DateTimeOffset.UtcNow - _lastSampleUtc).TotalSeconds;
					if (!IsLive)
					{
						DataAge = "Paused";
						EvaluateAlertCondition("StaleData", false, "");
						return;
					}
					DataAge = (waitingForFirstSample ? "Waiting for data" : $"Data age {num:N0}s");
					double staleThreshold = RuleThreshold("StaleData", 10);
					EvaluateAlertCondition(
						"StaleData",
						waitingForFirstSample && num <= staleThreshold ? null : num > staleThreshold,
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
			return source.Where(delegate(AndroidProcess process)
			{
				if (!process.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
					&& !FriendlyProcessName(process.Name).Contains(search, StringComparison.OrdinalIgnoreCase))
				{
					string? packageName = process.PackageName;
					if (packageName == null || !packageName.Contains(search, StringComparison.OrdinalIgnoreCase))
					{
						return process.Pid.ToString().Contains(search, StringComparison.OrdinalIgnoreCase);
					}
				}
				return true;
			}).ToList();
		}
		return source;
	}

	private void ApplyProcessSnapshot(IReadOnlyList<AndroidProcess> snapshot)
	{
		Dictionary<int, AndroidProcess> dictionary = snapshot.ToDictionary((AndroidProcess x) => x.Pid);
		for (int num = Processes.Count - 1; num >= 0; num--)
		{
			AndroidProcess androidProcess = Processes[num];
			if (!dictionary.TryGetValue(androidProcess.Pid, out var value) || !SameIdentity(androidProcess, value))
			{
				Processes.RemoveAt(num);
			}
		}
		Dictionary<int, AndroidProcess> dictionary2 = Processes.ToDictionary((AndroidProcess x) => x.Pid, (AndroidProcess x) => x);
		foreach (AndroidProcess item2 in snapshot)
		{
			if (!dictionary2.TryGetValue(item2.Pid, out var value2))
			{
				Processes.Add(item2);
				continue;
			}
			AndroidProcess androidProcess2 = item2 with
			{
				CpuPercent = (item2.CpuPercent ?? value2.CpuPercent),
				DiskReadBytesPerSecond = (item2.DiskReadBytesPerSecond ?? value2.DiskReadBytesPerSecond),
				DiskWriteBytesPerSecond = (item2.DiskWriteBytesPerSecond ?? value2.DiskWriteBytesPerSecond),
				NetworkRxBytesPerSecond = (item2.NetworkRxBytesPerSecond ?? value2.NetworkRxBytesPerSecond),
				NetworkTxBytesPerSecond = (item2.NetworkTxBytesPerSecond ?? value2.NetworkTxBytesPerSecond),
				Fps = (item2.Fps ?? value2.Fps)
			};
			if (!MateriallyEqual(value2, androidProcess2))
			{
				Processes[Processes.IndexOf(value2)] = androidProcess2;
			}
		}
		int targetIndex;
		for (targetIndex = 0; targetIndex < snapshot.Count; targetIndex++)
		{
			int item = Processes.Select((AndroidProcess process, int index) => (process: process, index: index)).FirstOrDefault(((AndroidProcess process, int index) tuple) => tuple.process.Pid == snapshot[targetIndex].Pid).index;
			if (item != targetIndex)
			{
				Processes.Move(item, targetIndex);
			}
		}
	}

	private IReadOnlyList<ProcessDisplayRow> BuildProcessRows(IReadOnlyList<AndroidProcess> source)
	{
		bool flag = !string.IsNullOrWhiteSpace(SearchText);
		AndroidProcess? unattributed = source.FirstOrDefault((AndroidProcess process) => process.Pid == 0);
		IEnumerable<AndroidProcess> groupedSource = source.Where((AndroidProcess process) => process.Pid != 0);
		var orderedEnumerable = (from @group in groupedSource.GroupBy<AndroidProcess, string>(ProcessGroupKey, StringComparer.Ordinal)
			select new
			{
				Key = @group.Key,
				Items = @group.ToArray()
			} into @group
			orderby @group.Items.Min((AndroidProcess process) => GroupRank(process.Group)),
				(@group.Items.Sum((AndroidProcess process) => process.CpuPercent.GetValueOrDefault()) > 0.05 ? 0 : 1),
				@group.Items.Sum((AndroidProcess process) => process.CpuPercent.GetValueOrDefault()) descending
			select @group).ThenBy(group => FriendlyGroupName(group.Key, group.Items), StringComparer.OrdinalIgnoreCase);
		List<ProcessDisplayRow> list = new List<ProcessDisplayRow>();
		if (unattributed != null)
		{
			list.Add(ToLeafRow(unattributed, "leaf:kernel-unattributed", "Kernel / unattributed CPU", isChild: false));
		}
		foreach (var item in orderedEnumerable)
		{
			AndroidProcess[] array = item.Items.OrderByDescending((AndroidProcess process) => process.CpuPercent ?? (-1.0)).ThenBy<AndroidProcess, string>((AndroidProcess process) => process.Name, StringComparer.OrdinalIgnoreCase).ToArray();
			string name = FriendlyGroupName(item.Key, array);
			string text = array.Select((AndroidProcess process) => NormalizePackage(process.PackageName)).FirstOrDefault((string value) => value != null);
			string text2 = "group:" + item.Key;
			bool num = array.Length > 1;
			bool flag2 = num && (flag || _expandedProcessGroups.Contains(text2));
			if (!num)
			{
				AndroidProcess androidProcess = array[0];
				list.Add(ToLeafRow(androidProcess, $"leaf:{androidProcess.Pid}:{androidProcess.StartTicks}", name, isChild: false));
				continue;
			}
			list.Add(new ProcessDisplayRow(text2, array.OrderBy((AndroidProcess process) => GroupRank(process.Group)).First().Group, name, text ?? $"{array.Length} Android tasks", null, flag2 ? $"{array.Length} tasks shown" : $"{array.Length} tasks", flag2 ? ((double?)null) : Sum(array.Select((AndroidProcess process) => process.CpuPercent)), flag2 ? ((long?)null) : Sum(array.Select((AndroidProcess process) => process.RssBytes)), flag2 ? ((double?)null) : Sum(array.Select((AndroidProcess process) => process.GpuPercent)), flag2 ? ((double?)null) : Sum(array.Select((AndroidProcess process) => process.DiskReadBytesPerSecond)), flag2 ? ((double?)null) : Sum(array.Select((AndroidProcess process) => process.DiskWriteBytesPerSecond)), flag2 ? ((double?)null) : Sum(array.Select((AndroidProcess process) => process.NetworkRxBytesPerSecond)), flag2 ? ((double?)null) : Sum(array.Select((AndroidProcess process) => process.NetworkTxBytesPerSecond)), flag2 ? ((double?)null) : (from process in array
				where process.Fps.HasValue
				select process.Fps).FirstOrDefault(), "N/A", "N/A", CanExpand: true, flag2, IsChild: false, text));
			if (flag2)
			{
				AndroidProcess[] array2 = array;
				foreach (AndroidProcess androidProcess2 in array2)
				{
					list.Add(ToLeafRow(androidProcess2, $"child:{item.Key}:{androidProcess2.Pid}:{androidProcess2.StartTicks}", DescribeTask(androidProcess2), isChild: true, parentKey: text2));
				}
			}
		}
		return list;
	}

	private static ProcessDisplayRow ToLeafRow(AndroidProcess process, string key, string name, bool isChild, string? parentKey = null)
	{
		return new ProcessDisplayRow(key, process.Group, name, process.PackageName ?? process.CommandLine, process.Pid, process.Status, process.CpuPercent, process.RssBytes, process.GpuPercent, process.DiskReadBytesPerSecond, process.DiskWriteBytesPerSecond, process.NetworkRxBytesPerSecond, process.NetworkTxBytesPerSecond, process.Fps, process.BatteryImpact ?? "N/A", process.ThermalRelation ?? "N/A", CanExpand: false, IsExpanded: false, isChild, process.PackageName, parentKey);
	}

	private void ApplyProcessRowSnapshot(IReadOnlyList<ProcessDisplayRow> snapshot)
	{
		Dictionary<string, ProcessDisplayRow> dictionary = snapshot.ToDictionary<ProcessDisplayRow, string>((ProcessDisplayRow row) => row.Key, StringComparer.Ordinal);
		for (int num = ProcessRows.Count - 1; num >= 0; num--)
		{
			if (!dictionary.ContainsKey(ProcessRows[num].Key))
			{
				ProcessRows.RemoveAt(num);
			}
		}
		Dictionary<string, ProcessDisplayRow> dictionary2 = ProcessRows.ToDictionary<ProcessDisplayRow, string>((ProcessDisplayRow row) => row.Key, StringComparer.Ordinal);
		foreach (ProcessDisplayRow item in snapshot)
		{
			if (!dictionary2.TryGetValue(item.Key, out var value))
			{
				ProcessRows.Add(item);
			}
			else
			{
				value.UpdateFrom(item);
			}
		}
		for (int num2 = 0; num2 < snapshot.Count; num2++)
		{
			int num3 = -1;
			for (int num4 = num2; num4 < ProcessRows.Count; num4++)
			{
				if (ProcessRows[num4].Key == snapshot[num2].Key)
				{
					num3 = num4;
					break;
				}
			}
			if (num3 >= 0 && num3 != num2)
			{
				ProcessRows.Move(num3, num2);
			}
		}
	}

	private static string ProcessGroupKey(AndroidProcess process)
	{
		string text = NormalizePackage(process.PackageName);
		if (text != null)
		{
			return "package:" + text;
		}
		string text2 = process.Name.Trim();
		if (text2.StartsWith("[kworker", StringComparison.OrdinalIgnoreCase) || text2.StartsWith("[rcu", StringComparison.OrdinalIgnoreCase) || text2.StartsWith("[migration", StringComparison.OrdinalIgnoreCase))
		{
			return "system:kernel-workers";
		}
		if (text2.StartsWith("android.hardware.", StringComparison.OrdinalIgnoreCase) || process.CommandLine.Contains("android.hardware.", StringComparison.OrdinalIgnoreCase))
		{
			return "system:hardware-services";
		}
		bool flag;
		switch (text2)
		{
		case "logd":
		case "logcat":
		case "statsd":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (flag)
		{
			return "system:logging";
		}
		switch (text2)
		{
		case "adbd":
			return "system:adb";
		case "system_server":
		case "servicemanager":
		case "hwservicemanager":
		case "vndservicemanager":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (flag)
		{
			return "system:android-core";
		}
		if (text2.StartsWith("zygote", StringComparison.OrdinalIgnoreCase) || text2.StartsWith("app_process", StringComparison.OrdinalIgnoreCase))
		{
			return "system:android-runtime";
		}
		return "process:" + text2;
	}

	private string FriendlyGroupName(string key, IReadOnlyList<AndroidProcess> items)
	{
		if (key.StartsWith("package:", StringComparison.Ordinal))
		{
			return FriendlyPackageName(key.Substring("package:".Length));
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
		bool flag = string.Equals(SelectedDevice?.Manufacturer, "Google", StringComparison.OrdinalIgnoreCase);
		switch (package)
		{
		case "com.android.systemui":
			return flag ? "Google System UI" : "Android System UI";
		case "com.android.phone":
			return "Phone services";
		case "com.android.settings":
			return "Android Settings";
		case "com.android.providers.media.module":
			return "Media Storage";
		case "com.android.providers.media":
			return "Media Storage";
		case "com.android.inputmethod.latin":
			return "Android Keyboard";
		case "com.android.vending":
			return "Google Play Store";
		case "com.google.android.gms":
			return "Google Play services";
		case "com.google.android.gsf":
		case "com.google.process.gservices":
			return "Google Services Framework";
		case "com.google.android.apps.nexuslauncher":
			return "Pixel Launcher";
		case "com.google.android.googlequicksearchbox":
			return "Google Search";
		case "com.google.android.apps.messaging":
			return "Google Messages";
		case "com.google.android.apps.photos":
			return "Google Photos";
		case "com.google.android.youtube":
			return "YouTube";
		case "com.google.android.apps.youtube.music":
			return "YouTube Music";
		case "com.google.android.inputmethod.latin":
			return "Gboard";
		case "com.google.android.configupdater":
			return "Google Config Updater";
		case "com.google.android.projection.gearhead":
			return "Android Auto";
		case "com.google.android.providers.media.module":
			return "Media Provider";
		case "com.google.android.apps.restore":
			return "Google Restore";
		case "com.google.process.gapps":
			return "Google Apps Services";
		case "com.google.pixel.exo":
			return "Pixel system service";
		case "com.google.android.ext.services":
			return "Google system services";
		case "com.google.android.ext.shared":
			return "Google shared services";
		case "com.google.android.permissioncontroller":
			return "Permission Controller";
		case "com.google.android.settings.intelligence":
			return "Settings Intelligence";
		case "android.process.acore":
			return "Android contacts services";
		case "android.process.media":
			return "Android media services";
		case "io.appium.settings":
			return "Appium Settings";
		default:
			return FriendlyPackageFallback(package);
		}
	}

	private static string FriendlyPackageFallback(string package)
	{
		string[] source = package.Split('.', StringSplitOptions.RemoveEmptyEntries);
		string str = Regex.Replace((source.Reverse().FirstOrDefault(delegate(string segment)
		{
			switch (segment)
			{
			default:
				return !(segment == "apps");
			case "service":
			case "services":
			case "provider":
			case "providers":
			case "app":
				return false;
			}
		}) ?? source.LastOrDefault() ?? package).Replace('_', ' ').Replace('-', ' '), "([a-z])([A-Z])", "$1 $2");
		str = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(str);
		if (package.StartsWith("com.google.", StringComparison.OrdinalIgnoreCase) && !str.StartsWith("Google", StringComparison.OrdinalIgnoreCase))
		{
			return "Google " + str;
		}
		if (package.StartsWith("com.android.", StringComparison.OrdinalIgnoreCase) && !str.StartsWith("Android", StringComparison.OrdinalIgnoreCase))
		{
			return "Android " + str;
		}
		return str;
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
			return FriendlyPackageFallback(process.Name.Substring(process.Name.LastIndexOf(':') + 1)) + " service";
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
		double[] array = (from value in values
			where value.HasValue
			select value.Value).ToArray();
		if (array.Length != 0)
		{
			return array.Sum();
		}
		return null;
	}

	private static long? Sum(IEnumerable<long?> values)
	{
		long[] array = (from value in values
			where value.HasValue
			select value.Value).ToArray();
		if (array.Length != 0)
		{
			return array.Sum();
		}
		return null;
	}

	private static int GroupRank(ProcessGroup group)
	{
		return group switch
		{
			ProcessGroup.System => 0,
			ProcessGroup.Apps => 1,
			ProcessGroup.Background => 2,
			_ => 3, 
		};
	}

	private static bool SameIdentity(AndroidProcess current, AndroidProcess next)
	{
		if (current.Pid == next.Pid && current.Name == next.Name)
		{
			if (current.StartTicks.HasValue && next.StartTicks.HasValue)
			{
				return current.StartTicks == next.StartTicks;
			}
			return true;
		}
		return false;
	}

	private static bool MateriallyEqual(AndroidProcess current, AndroidProcess next)
	{
		if (current.Name == next.Name && current.PackageName == next.PackageName && current.Status == next.Status && current.Group == next.Group && current.CpuPercent == next.CpuPercent && Math.Abs(current.RssBytes.GetValueOrDefault() - next.RssBytes.GetValueOrDefault()) < 131072 && current.DiskReadBytesPerSecond == next.DiskReadBytesPerSecond && current.DiskWriteBytesPerSecond == next.DiskWriteBytesPerSecond && current.NetworkRxBytesPerSecond == next.NetworkRxBytesPerSecond && current.NetworkTxBytesPerSecond == next.NetworkTxBytesPerSecond)
		{
			return current.Fps == next.Fps;
		}
		return false;
	}

	private void ApplySample(MetricSample s)
	{
		Guid sessionId = s.SessionId;
		Guid? obj = _session?.Id;
		if (sessionId != obj)
		{
			return;
		}
		LiveSamples.Add(s);
		while (LiveSamples.Count > 600)
		{
			LiveSamples.RemoveAt(0);
		}
		double? num = _processSnapshotCpuTotal ?? s.DeviceCpuPercent;
		double? obj2;
		if (num.HasValue)
		{
			double valueOrDefault = num.GetValueOrDefault();
			obj2 = Math.Clamp(valueOrDefault, 0.0, 100.0);
		}
		else
		{
			obj2 = null;
		}
		double? num2 = obj2;
		string secondary = ((!s.ProcessCpuPercent.HasValue) ? (SelectedPackage + ": N/A") : $"{SelectedPackage}: {s.ProcessCpuPercent:N1}%");
		CpuCard.Push(num2, (!num2.HasValue) ? "N/A" : $"{num2:N1}%", secondary, s.Source + "; same CPU window as the process table; 0% = idle, 100% = fully busy");
		long? num3 = SelectedDevice?.TotalMemoryBytes;
		long? deviceMemoryUsedBytes = s.DeviceMemoryUsedBytes;
		double? value = ((deviceMemoryUsedBytes.HasValue && num3 > 0) ? new double?((double)deviceMemoryUsedBytes.Value * 100.0 / (double)num3.Value) : ((double?)null));
		MemoryCard.Push(value, (!deviceMemoryUsedBytes.HasValue) ? "N/A" : ((!num3.HasValue) ? FormatBytes(deviceMemoryUsedBytes) : (FormatBytes(deviceMemoryUsedBytes) + " / " + FormatBytes(num3))), (!s.ProcessPssBytes.HasValue) ? (SelectedPackage + " RSS: " + FormatBytes(s.ProcessRssBytes)) : (SelectedPackage + " PSS: " + FormatBytes(s.ProcessPssBytes)), s.Source);
		GpuCard.Push(s.GpuPercent, s.GpuPercent.HasValue ? $"{s.GpuPercent:N1}%" : s.GpuAvailability == Availability.Waiting ? "Waiting" : "N/A",
			s.GpuSource ?? (IsDemo ? "Demo GPU" : "Unsupported on this target"), s.GpuSource ?? s.Source);
		var deviceDisk = s.DeviceDiskSource is not null;
		if (DiskCard.Title.StartsWith("Device", StringComparison.Ordinal) != deviceDisk) DiskChart.Clear();
		DiskCard.Push(deviceDisk ? s.DeviceDiskWriteBytesPerSecond : s.DiskWriteBytesPerSecond,
			deviceDisk ? s.DeviceDiskReadBytesPerSecond : s.DiskReadBytesPerSecond, s.DeviceDiskSource);
		CpuChart.Push(s.TimestampUtc, num2, s.ProcessCpuPercent);
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
		if (_lastNetworkTotalTimestamp != default(DateTimeOffset))
		{
			double num4 = Math.Clamp((s.TimestampUtc - _lastNetworkTotalTimestamp).TotalSeconds, 0.0, 10.0);
			_sessionNetworkRxBytes += Math.Max(0.0, s.NetworkRxBytesPerSecond.GetValueOrDefault()) * num4;
			_sessionNetworkTxBytes += Math.Max(0.0, s.NetworkTxBytesPerSecond.GetValueOrDefault()) * num4;
			NetworkTotals = "Session RX " + FormatBytes((long)_sessionNetworkRxBytes) + " · TX " + FormatBytes((long)_sessionNetworkTxBytes);
		}
		_lastNetworkTotalTimestamp = s.TimestampUtc;
		long? deviceStorageTotalBytes = s.DeviceStorageTotalBytes;
		if (deviceStorageTotalBytes.HasValue)
		{
			long valueOrDefault2 = deviceStorageTotalBytes.GetValueOrDefault();
			deviceStorageTotalBytes = s.DeviceStorageAvailableBytes;
			if (deviceStorageTotalBytes.HasValue)
			{
				long valueOrDefault3 = deviceStorageTotalBytes.GetValueOrDefault();
				long num5 = Math.Max(0L, valueOrDefault2 - valueOrDefault3);
				double value3 = ((valueOrDefault2 > 0) ? ((double)num5 * 100.0 / (double)valueOrDefault2) : 0.0);
				StorageSummary = $"Total {FormatBytes(valueOrDefault2)} · Used {FormatBytes(num5)} · Free {FormatBytes(valueOrDefault3)} · {value3:N1}%";
				double storageThresholdBytes = RuleThreshold("LowStorage", 1) * 1024 * 1024 * 1024;
				EvaluateAlertCondition(
					"LowStorage",
					valueOrDefault3 < storageThresholdBytes,
					"Only " + FormatBytes(valueOrDefault3) + " of storage remains.");
			}
		}
		if (s.PackageCodeBytes.HasValue || s.PackageDataBytes.HasValue || s.PackageCacheBytes.HasValue)
		{
			AppStorageSummary = $"Code {FormatBytes(s.PackageCodeBytes)} · Data {FormatBytes(s.PackageDataBytes)} · Cache {FormatBytes(s.PackageCacheBytes)}";
		}
		if (s.TemperatureCelsius.HasValue || s.BatteryPercent.HasValue || s.ThermalSeverity != null)
		{
			ThermalSummary = $"{(s.TemperatureCelsius.HasValue ? $"{s.TemperatureCelsius:N1} °C" : "Temperature N/A")} · {s.ThermalSeverity ?? "Severity N/A"} · Battery {(s.BatteryPercent.HasValue ? $"{s.BatteryPercent}%" : "N/A")} · {((s.IsCharging == true) ? "Charging" : ((s.IsCharging == false) ? "Not charging" : "Charging N/A"))}";
		}
		int num6 = _allProcesses.FindIndex((AndroidProcess p) => p.PackageName == s.PackageName);
		if (num6 >= 0)
		{
			AndroidProcess current = _allProcesses[num6];
			_allProcesses[num6] = current with
			{
				RssBytes = (s.ProcessRssBytes ?? current.RssBytes),
				DiskReadBytesPerSecond = s.DiskReadBytesPerSecond,
				DiskWriteBytesPerSecond = s.DiskWriteBytesPerSecond,
				NetworkRxBytesPerSecond = s.AppNetworkRxBytesPerSecond,
				NetworkTxBytesPerSecond = s.AppNetworkTxBytesPerSecond,
				Fps = s.Fps
			};
			if (!IsProcessTablePaused)
			{
				AndroidProcess androidProcess = Processes.FirstOrDefault((AndroidProcess p) => p.Pid == current.Pid);
				if ((object)androidProcess != null)
				{
					Processes[Processes.IndexOf(androidProcess)] = _allProcesses[num6];
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
			s.DeviceMemoryAvailableBytes.HasValue && num3 > 0
				? s.DeviceMemoryAvailableBytes.Value * 100.0 / num3.Value
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
		_ = 1;
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
		using PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromSeconds(5L));
		HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
		try
		{
			do
			{
				IEnumerable<string> source;
				AdbCommandResult? logcatResult = null;
				if (IsDemo)
				{
					source = new global::_003C_003Ez__ReadOnlyArray<string>(new string[2]
					{
						$"07-20 12:00:{DateTimeOffset.Now.Second:00}.000 13432 13432 I MyGame: Render loop stable on {device.Serial}",
						$"07-20 12:00:{DateTimeOffset.Now.Second:00}.100 13432 13455 D Unity: frame submitted"
					});
				}
				else
				{
					AdbCommandResult adbCommandResult = await _adb.ExecuteAsync(device.Serial, new global::_003C_003Ez__ReadOnlyArray<string>(new string[8] { "logcat", "-d", "-b", "main,system,crash,events,radio", "-v", "threadtime", "-t", "100" }), TimeSpan.FromSeconds(10L), token);
					logcatResult = adbCommandResult;
					source = (adbCommandResult.Success ? ((IEnumerable<string>)adbCommandResult.StandardOutput.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)) : ((IEnumerable<string>)Array.Empty<string>()));
				}
				string[] source2 = source.Where((string line) => seen.Add(line)).TakeLast(30).ToArray();
				if (seen.Count > 2000)
				{
					seen = seen.TakeLast(1000).ToHashSet<string>(StringComparer.Ordinal);
				}
				LogEntry[] entries = source2.Select((string line) => ParseLogcatEntry(line, device.Serial)).ToArray();
				IReadOnlyList<LogEntry> applicationEntries =
					await ReadApplicationDiagnosticsAsync(device.Serial, token);
				bool crashDetected = source2.Any((string line) => line.Contains("FATAL EXCEPTION", StringComparison.OrdinalIgnoreCase) || line.Contains("AndroidRuntime", StringComparison.OrdinalIgnoreCase));
				bool anrDetected = source2.Any((string line) => line.Contains("ANR in", StringComparison.OrdinalIgnoreCase));
				await ((DispatcherObject)Application.Current).Dispatcher.InvokeAsync((Action)delegate
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
						LogEntry[] array = entries;
						foreach (LogEntry item in array)
						{
							Logs.Add(item);
						}
						foreach (LogEntry item in applicationEntries)
						{
							Logs.Add(item);
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
				}, (DispatcherPriority)4);
			}
			while (await timer.WaitForNextTickAsync(token));
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			await ((DispatcherObject)Application.Current).Dispatcher.InvokeAsync((Action)(() =>
				EvaluateAlertCondition("LogcatStopped", true, "logcat collection stopped: " + ex.Message)));
		}
	}

	private bool FilterLog(object item)
	{
		if (!(item is LogEntry logEntry))
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
		string text = LogSearchText.Trim();
		if (text.Length == 0)
		{
			return true;
		}
		string text2 = $"{logEntry.Source} {logEntry.Priority} {logEntry.PackageName} {logEntry.Pid} {logEntry.Message}";
		if (!LogRegexEnabled)
		{
			return text2.Contains(text, StringComparison.OrdinalIgnoreCase);
		}
		return _logSearchRegex?.IsMatch(text2) ?? false;
	}

	private void RefreshLogFilter()
	{
		_logSearchRegex = null;
		if (LogRegexEnabled && !string.IsNullOrWhiteSpace(LogSearchText))
		{
			try
			{
				_logSearchRegex = new Regex(LogSearchText, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100L));
			}
			catch (ArgumentException ex)
			{
				LogFilterStatus = "Invalid regex: " + ex.Message;
				LogView.Refresh();
				return;
			}
		}
		LogView.Refresh();
		LogFilterStatus = $"{((IEnumerable)LogView).Cast<object>().Count()} visible · {Logs.Count} buffered";
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
		string s = $"{DateTime.Now.Year}-{match.Groups["month"].Value}-{match.Groups["day"].Value} {match.Groups["time"].Value}";
		string[] formats = new string[2] { "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss.ffffff" };
		DateTime result;
		DateTimeOffset dateTimeOffset = (DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out result) ? new DateTimeOffset(result) : DateTimeOffset.Now);
		if (dateTimeOffset > DateTimeOffset.Now.AddDays(2.0))
		{
			dateTimeOffset = dateTimeOffset.AddYears(-1);
		}
		int result2;
		int? pid = (int.TryParse(match.Groups["pid"].Value, CultureInfo.InvariantCulture, out result2) ? new int?(result2) : ((int?)null));
		string text;
		switch (match.Groups["priority"].Value)
		{
		case "E":
		case "F":
		case "A":
			text = "Error";
			break;
		case "W":
			text = "Warning";
			break;
		case "D":
		case "V":
			text = "Debug";
			break;
		default:
			text = "Info";
			break;
		}
		string priority = text;
		string packageName = ((!pid.HasValue) ? null : _allProcesses.FirstOrDefault((AndroidProcess process) => process.Pid == pid.Value)?.PackageName);
		string message = match.Groups["tag"].Value.Trim() + ": " + match.Groups["message"].Value;
		return new LogEntry(dateTimeOffset.ToUniversalTime(), IsDemo ? "Demo logcat" : "logcat", priority, message, serial, pid, packageName);
	}

	private static string InferLogPriority(string line)
	{
		if (!line.Contains(" E ", StringComparison.Ordinal) && !line.Contains("ERROR", StringComparison.OrdinalIgnoreCase) && !line.Contains("FATAL", StringComparison.OrdinalIgnoreCase))
		{
			if (!line.Contains(" W ", StringComparison.Ordinal) && !line.Contains("WARN", StringComparison.OrdinalIgnoreCase))
			{
				if (!line.Contains(" D ", StringComparison.Ordinal) && !line.Contains("DEBUG", StringComparison.OrdinalIgnoreCase))
				{
					return "Info";
				}
				return "Debug";
			}
			return "Warning";
		}
		return "Error";
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
		ObservableCollection<LogEntry> logs = Logs;
		DateTimeOffset timestampUtc = utcNow;
		string priority = ((severity == "Critical") ? "Error" : "Warning");
		string message2 = "Alert " + type + ": " + message;
		string serial = _session.Device.Serial;
		string currentPackage = _session.CurrentPackage;
		logs.Add(new LogEntry(timestampUtc, "Android Dev Monitor", priority, message2, serial, null, currentPackage));
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
		SessionAlert sessionAlert = Alerts.FirstOrDefault((SessionAlert x) => x.Type == type && !x.Resolved);
		if ((object)sessionAlert == null)
		{
			return;
		}
		SessionAlert sessionAlert2 = sessionAlert with
		{
			Resolved = true
		};
		Alerts[Alerts.IndexOf(sessionAlert)] = sessionAlert2;
		NotifyAlertResolved(sessionAlert, sessionAlert2);
		if (_session != null)
		{
			lock (_session.SyncRoot)
			{
				int num = _session.Alerts.IndexOf(sessionAlert);
				if (num >= 0)
				{
					_session.Alerts[num] = sessionAlert2;
				}
			}
		}
		_sessions.SaveAlertAsync(sessionAlert2, CancellationToken.None);
	}

	private async Task ReloadStoredSessionsAsync(Guid? preferredSessionId = null)
	{
		Guid? selectedId = preferredSessionId ?? SelectedStoredSession?.Id;
		IReadOnlyList<SessionSummary> obj = await _sessions.ListSessionSummariesAsync(CancellationToken.None);
		StoredSessions.Clear();
		foreach (SessionSummary item in obj)
		{
			StoredSessions.Add(item);
		}
		MainViewModel mainViewModel = this;
		SessionSummary? selectedStoredSession;
		if (selectedId.HasValue)
		{
			Guid id = selectedId.GetValueOrDefault();
			selectedStoredSession = StoredSessions.FirstOrDefault((SessionSummary item) => item.Id == id);
		}
		else
		{
			selectedStoredSession = StoredSessions.FirstOrDefault();
		}
		mainViewModel.SelectedStoredSession = selectedStoredSession;
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
		if ((object)summary == null)
		{
			StoredSessionStatus = "Select a stored session";
			return;
		}
		StoredSessionStatus = $"Loading session {summary.StartedUtc.ToLocalTime():g}…";
		try
		{
			MonitoringSession monitoringSession = await _sessions.LoadSessionAsync(summary.Id, token);
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
				foreach (LiveChartViewModel storedSessionChart in StoredSessionCharts)
				{
					storedSessionChart.AddAnnotation(marker.TimestampUtc, marker.Name, isAlert: false);
				}
			}
			foreach (SessionAlert alert in monitoringSession.Alerts)
			{
				StoredSessionAlerts.Add(alert);
				foreach (LiveChartViewModel storedSessionChart2 in StoredSessionCharts)
				{
					storedSessionChart2.AddAnnotation(alert.TimestampUtc, alert.Type, isAlert: true);
				}
			}
			foreach (SessionEvent @event in monitoringSession.Events)
			{
				StoredSessionEvents.Add(@event);
			}
			StoredSessionDetails = $"{summary.Device.FriendlyName} · {summary.Device.Serial}\n{summary.Device.VersionLine} · {summary.CurrentPackage ?? "No tracked package"}\nDuration {summary.Duration:g} · Active {summary.ActiveCollectionTime:g} · Samples {monitoringSession.Samples.Count:N0}\nPeak CPU {FormatNullable(summary.PeakCpuPercent, "%")} · Peak app memory {FormatBytes(summary.PeakMemoryBytes)} · Avg FPS {FormatNullable(summary.AverageFps, "")} · P95 frame {FormatNullable(summary.FrameTimeP95Ms, " ms")}";
			StoredSessionStatus = $"Full session loaded · {monitoringSession.Samples.Count:N0} samples · {summary.ExportStatus}";
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex2)
		{
			StoredSessionStatus = "Session load failed";
			_dialogs.Notify(ex2.Message, error: true);
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
		List<MetricSample> list = new List<MetricSample>(maximumPoints);
		double num = ((double)samples.Count - 1.0) / ((double)maximumPoints - 1.0);
		int num2 = -1;
		for (int i = 0; i < maximumPoints; i++)
		{
			int num3 = Math.Clamp((int)Math.Round((double)i * num), 0, samples.Count - 1);
			if (num3 != num2)
			{
				list.Add(samples[num3]);
				num2 = num3;
			}
		}
		return list;
	}

	private static string FormatNullable(double? value, string unit)
	{
		if (!value.HasValue)
		{
			return "N/A";
		}
		return $"{value.Value:N1}{unit}";
	}

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
		MainViewModel mainViewModel = this;
		MediaItem? selectedMediaItem;
		if (selectedId.HasValue)
		{
			Guid id = selectedId.GetValueOrDefault();
			selectedMediaItem = MediaItems.FirstOrDefault((MediaItem item) => item.Id == id);
		}
		else
		{
			selectedMediaItem = MediaItems.FirstOrDefault();
		}
		mainViewModel.SelectedMediaItem = selectedMediaItem;
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
		int num = MediaItems.IndexOf(oldItem);
		if (num >= 0)
		{
			MediaItems[num] = newItem;
		}
		SelectedMediaItem = newItem;
		RefreshMediaFilterOptions();
		RefreshMediaView();
	}

	private bool FilterMedia(object item)
	{
		if (!(item is MediaItem mediaItem))
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
		//IL_0046: Unknown result type (might be due to invalid IL or missing references)
		//IL_004b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0076: Unknown result type (might be due to invalid IL or missing references)
		//IL_0077: Unknown result type (might be due to invalid IL or missing references)
		//IL_0083: Unknown result type (might be due to invalid IL or missing references)
		//IL_0054: Unknown result type (might be due to invalid IL or missing references)
		//IL_0059: Unknown result type (might be due to invalid IL or missing references)
		//IL_0062: Unknown result type (might be due to invalid IL or missing references)
		//IL_0067: Unknown result type (might be due to invalid IL or missing references)
		//IL_0070: Unknown result type (might be due to invalid IL or missing references)
		//IL_0075: Unknown result type (might be due to invalid IL or missing references)
		((Collection<SortDescription>)(object)MediaView.SortDescriptions).Clear();
		SortDescription item = (SortDescription)(SelectedMediaSort switch
		{
			"Oldest" => new SortDescription("CapturedUtc", ListSortDirection.Ascending), 
			"Largest" => new SortDescription("FileSize", ListSortDirection.Descending), 
			"Smallest" => new SortDescription("FileSize", ListSortDirection.Ascending), 
			_ => new SortDescription("CapturedUtc", ListSortDirection.Descending), 
		});
		((Collection<SortDescription>)(object)MediaView.SortDescriptions).Add(item);
		MediaView.Refresh();
		MediaFilterStatus = $"{((IEnumerable)MediaView).Cast<object>().Count()} shown · {MediaItems.Count} total";
	}

	private void RefreshMediaFilterOptions()
	{
		string selectedMediaDevice = SelectedMediaDevice;
		string selectedMediaPackage = SelectedMediaPackage;
		MediaDeviceFilters.Clear();
		MediaDeviceFilters.Add("All");
		foreach (string item in (from item in MediaItems
			select item.DeviceSerial into value
			where !string.IsNullOrWhiteSpace(value)
			select value).Distinct<string>(StringComparer.Ordinal).Order())
		{
			MediaDeviceFilters.Add(item);
		}
		MediaPackageFilters.Clear();
		MediaPackageFilters.Add("All");
		foreach (string item2 in MediaItems.Select((MediaItem item) => item.PackageName ?? "N/A").Distinct<string>(StringComparer.Ordinal).Order())
		{
			MediaPackageFilters.Add(item2);
		}
		SelectedMediaDevice = (MediaDeviceFilters.Contains(selectedMediaDevice) ? selectedMediaDevice : "All");
		SelectedMediaPackage = (MediaPackageFilters.Contains(selectedMediaPackage) ? selectedMediaPackage : "All");
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
			string text = ((path == "/") ? "/" : ("/" + string.Join('/', path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))));
			if (recordHistory && !string.Equals(RemotePath, text, StringComparison.Ordinal))
			{
				_remoteBackHistory.Push(RemotePath);
				_remoteForwardHistory.Clear();
			}
			RemotePath = text;
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
			foreach (string item in Directory.EnumerateFileSystemEntries(LocalPath).Take(1000))
			{
				bool flag = Directory.Exists(item);
				FileInfo fileInfo = (flag ? null : new FileInfo(item));
				LocalFiles.Add(new FileEntry(Path.GetFileName(item), item, flag, fileInfo?.Length, flag ? new DateTime?(Directory.GetLastWriteTimeUtc(item)) : fileInfo?.LastWriteTimeUtc));
			}
		}
		catch (Exception ex)
		{
			StatusMessage = "Local path inaccessible: " + ex.Message;
		}
	}

	private bool FilterLocalFile(object item)
	{
		if (item is FileEntry fileEntry)
		{
			if (!string.IsNullOrWhiteSpace(FileSearchText))
			{
				return fileEntry.Name.Contains(FileSearchText, StringComparison.OrdinalIgnoreCase);
			}
			return true;
		}
		return false;
	}

	private bool FilterRemoteFile(object item)
	{
		if (item is FileEntry fileEntry)
		{
			if (!string.IsNullOrWhiteSpace(FileSearchText))
			{
				return fileEntry.Name.Contains(FileSearchText, StringComparison.OrdinalIgnoreCase);
			}
			return true;
		}
		return false;
	}

	private static bool IsSafeRemoteName(string value)
	{
		if (!string.IsNullOrWhiteSpace(value) && !(value == ".") && !(value == "..") && !value.Contains('/') && !value.Contains('\\'))
		{
			return !value.Contains('\0');
		}
		return false;
	}

	private static string CombineRemote(string directory, string name)
	{
		if (!(directory == "/"))
		{
			return directory.TrimEnd('/') + "/" + name;
		}
		return "/" + name;
	}

	private static string FormatBytes(long? value)
	{
		if (!value.HasValue)
		{
			return "N/A";
		}
		string[] array = new string[5] { "B", "KB", "MB", "GB", "TB" };
		double num = value.Value;
		int num2 = 0;
		while (num >= 1024.0 && num2 < array.Length - 1)
		{
			num /= 1024.0;
			num2++;
		}
		return $"{num:N1} {array[num2]}";
	}

	private static string FormatRate(double? value)
	{
		if (value.HasValue)
		{
			return FormatBytes((long)value.Value) + "/s";
		}
		return "N/A";
	}

	public async ValueTask DisposeAsync()
	{
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

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnSelectedDeviceChanged(AndroidDevice? oldValue, AndroidDevice? newValue)
	{
		OnPropertyChanged("DeviceContext");
		OnPropertyChanged(nameof(BottomDeviceContext));
		if (!_suppressDeviceSwitch && _initialized && newValue is null)
		{
			_ = HandleSelectedDeviceUnavailableAsync(oldValue);
			return;
		}
		if (!_suppressDeviceSwitch && _initialized && (object)newValue != null && (oldValue?.Serial != newValue.Serial || oldValue?.State != newValue.State))
		{
			_ = SwitchDeviceAsync(newValue);
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnSelectedPackageChanged(string? oldValue, string? newValue)
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

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnIsLiveChanged(bool value)
	{
		OnPropertyChanged("LiveLabel");
		OnPropertyChanged(nameof(CollectionState));
		StatusMessage = (value ? "Performance collection resumed" : "Performance collection paused");
		if (_session == null || (object)SelectedDevice == null)
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

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnIsRecordingChanged(bool value)
	{
		OnPropertyChanged("RecordingLabel");
		OnPropertyChanged(nameof(RecordingState));
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnSearchTextChanged(string value)
	{
		ApplyProcessFilter();
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnSessionTimeChanged(string value)
	{
		OnPropertyChanged("SessionStatus");
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnLogSearchTextChanged(string value)
	{
		RefreshLogFilter();
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnSelectedLogPriorityChanged(string value)
	{
		RefreshLogFilter();
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnSelectedLogSourceChanged(string value)
	{
		RefreshLogFilter();
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnLogRegexEnabledChanged(bool value)
	{
		RefreshLogFilter();
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnMediaSearchTextChanged(string value)
	{
		RefreshMediaView();
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnSelectedMediaKindChanged(string value)
	{
		RefreshMediaView();
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnSelectedMediaDeviceChanged(string value)
	{
		RefreshMediaView();
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnSelectedMediaPackageChanged(string value)
	{
		RefreshMediaView();
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnSelectedMediaSortChanged(string value)
	{
		RefreshMediaView();
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnSelectedMediaItemChanged(MediaItem? value)
	{
		MediaRenameText = (((object)value == null) ? "" : Path.GetFileNameWithoutExtension(value.FileName));
		MediaNote = value?.Note ?? "";
		Guid? guid = value?.MarkerId;
		object selectedMediaMarker;
		if (guid.HasValue)
		{
			Guid markerId = guid.GetValueOrDefault();
			selectedMediaMarker = Markers.FirstOrDefault((SessionMarker marker) => marker.Id == markerId);
		}
		else
		{
			selectedMediaMarker = null;
		}
		SelectedMediaMarker = (SessionMarker?)selectedMediaMarker;
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnSelectedStoredSessionChanged(SessionSummary? value)
	{
		_ = LoadStoredSessionAsync(value);
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnSelectedApkPathChanged(string? value)
	{
		OnPropertyChanged("SelectedApkInfo");
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnFileSearchTextChanged(string value)
	{
		LocalFileView.Refresh();
		RemoteFileView.Refresh();
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnSelectedRemoteFileChanged(FileEntry? value)
	{
		if ((object)value != null)
		{
			RemoteRenameText = value.Name;
		}
	}
}
