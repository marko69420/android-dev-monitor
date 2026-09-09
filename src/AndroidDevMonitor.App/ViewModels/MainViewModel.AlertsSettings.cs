using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Media;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using AndroidDevMonitor.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AndroidDevMonitor.App.ViewModels;

public partial class MainViewModel
{
    private readonly Dictionary<string, DateTimeOffset> _alertConditionSince = new(StringComparer.Ordinal);
    private readonly Queue<(DateTimeOffset Timestamp, bool TimedOut)> _adbCommandOutcomes = new();
    private readonly Queue<string> _recentAdbCommandTimings = new();
    private readonly Dictionary<string, long> _diagnosticLogOffsets = new(StringComparer.OrdinalIgnoreCase);
    private bool _selectedPackageObservedRunning;
    private bool _networkWasConnected;
    private bool _loadingSettings;
    private string? _preferredDeviceSerial;
    private AndroidDevice? _recordingTarget;
    private string? _recordingPackage;
    private Guid? _recordingSessionId;

    [ObservableProperty] private SessionAlert? _selectedActiveAlert;
    [ObservableProperty] private string _selectedAdbPath = "";
    [ObservableProperty] private string _adbVersion = "Not tested";
    [ObservableProperty] private string _adbServerState = "Not tested";
    [ObservableProperty] private string _adbDiagnostics = "Run Test connection to inspect the configured Android Debug Bridge.";
    [ObservableProperty] private bool _launchAtStartup;
    [ObservableProperty] private bool _restoreLastPage = true;
    [ObservableProperty] private bool _restoreLastDevice = true;
    [ObservableProperty] private bool _autoReconnectLastDevice = true;
    [ObservableProperty] private bool _confirmRecordingClose = true;
    [ObservableProperty] private bool _confirmAutomationClose = true;
    [ObservableProperty] private bool _compactDensity = true;
    [ObservableProperty] private bool _windowsNotificationsEnabled;
    [ObservableProperty] private bool _alertSoundsEnabled = true;
    [ObservableProperty] private bool _screenshotOnAlert;
    [ObservableProperty] private bool _recordingOnAlert;
    [ObservableProperty] private bool _quietMode;
    [ObservableProperty] private string _selectedNetworkFilter = "All traffic";
    [ObservableProperty] private string _networkDiagnosticsStatus = "Open Network or click Refresh to collect diagnostics.";
    [ObservableProperty] private string _networkGateway = "N/A";
    [ObservableProperty] private string _networkDnsServers = "N/A";
    [ObservableProperty] private string _networkConnectionState = "Unknown";
    [ObservableProperty] private string _networkFailureSummary = "No connection failures detected in buffered logs.";
    [ObservableProperty] private string _lastAdbCommandTimings = "No ADB command has completed yet.";

    public ObservableCollection<SessionAlert> ActiveAlerts { get; } = [];
    public ObservableCollection<AlertRule> AlertRules { get; } = new(CreateDefaultAlertRules());
    public ObservableCollection<NetworkInterfaceRow> NetworkInterfaces { get; } = [];
    public ObservableCollection<NetworkSocketRow> NetworkSockets { get; } = [];
    public ObservableCollection<NetworkSocketRow> FilteredNetworkSockets { get; } = [];
    public IReadOnlyList<string> NetworkFilters { get; } =
    [
        "All traffic",
        "Selected package",
        "TCP",
        "UDP",
        "Active connections",
        "Closed or failed connections"
    ];

    public string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AndroidDevMonitor");
    public string SessionDirectory => DataDirectory;
    public string DiagnosticsDirectory => Path.Combine(DataDirectory, "Logs");
    public string DatabasePath => Path.Combine(DataDirectory, "android-dev-monitor.db");
    public string ScreenshotDirectory => _media.MediaDirectory;
    public string RecordingDirectory => _media.MediaDirectory;
    public string SessionStorageSummary => FormatStorageUsageSummary(
        GetFilesSize([DatabasePath, DatabasePath + "-wal", DatabasePath + "-shm"]),
        10L * 1024 * 1024 * 1024);
    public string MediaStorageSummary => FormatStorageUsageSummary(
        GetDirectorySize(_media.MediaDirectory),
        5L * 1024 * 1024 * 1024);
    public string ApplicationVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
    public string DotNetVersion => Environment.Version.ToString();
    public string CollectorStatus =>
        IsDemo
            ? $"Demo metrics, processes, discovery and logs · samples {LiveSamples.Count}/600 · logs {Logs.Count}/500 · process rows {ProcessRows.Count}"
            : $"ADB metrics, processes, discovery and logcat · samples {LiveSamples.Count}/600 · logs {Logs.Count}/500 · process rows {ProcessRows.Count}";
    public string LastParserError
    {
        get
        {
            LogEntry? entry = Logs.Reverse().FirstOrDefault(item =>
                (item.Priority is "Warning" or "Error") &&
                (item.Message.Contains("parse", StringComparison.OrdinalIgnoreCase) ||
                 item.Message.Contains("parser", StringComparison.OrdinalIgnoreCase)));
            return entry is null
                ? "None in the current 500-entry log buffer"
                : $"{entry.LocalTimestamp:HH:mm:ss} · {entry.Source} · {entry.Message}";
        }
    }
    public string DemoModeStatus => IsDemo
        ? "Enabled · all simulated values are labeled DEMO DATA"
        : "Disabled · current values come from the selected ADB target";
    public string DemoModeActionText => IsDemo ? "Restart in live mode" : "Restart in Demo mode";
    public double UiFontSize => CompactDensity ? 12 : 13.5;
    public double UiRowHeight => CompactDensity ? 26 : 34;
    public string BottomDeviceContext => SelectedDevice is null
        ? "No Android target"
        : $"{SelectedDevice.FriendlyName} · Android {SelectedDevice.AndroidVersion} (API {SelectedDevice.ApiLevel?.ToString() ?? "N/A"}) · {SelectedDevice.State}";
    public string CollectionState => IsLive ? "Live" : "Paused";
    public string RecordingState => IsRecording ? "● Recording" : "";

    private void InitializeAlertsAndSettings()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(DiagnosticsDirectory);
        PropertyChanged += OnSettingsPropertyChanged;
        foreach (AlertRule rule in AlertRules)
            rule.PropertyChanged += OnAlertRulePropertyChanged;
    }

    partial void OnCompactDensityChanged(bool value)
    {
        OnPropertyChanged(nameof(UiFontSize));
        OnPropertyChanged(nameof(UiRowHeight));
    }

    private async Task LoadSettingsAsync()
    {
        _loadingSettings = true;
        try
        {
            LaunchAtStartup = await LoadSettingAsync("general.launchAtStartup", false);
            RestoreLastPage = await LoadSettingAsync("general.restoreLastPage", true);
            RestoreLastDevice = await LoadSettingAsync("general.restoreLastDevice", true);
            AutoReconnectLastDevice = await LoadSettingAsync("general.autoReconnectLastDevice", true);
            ConfirmRecordingClose = await LoadSettingAsync("general.confirmRecordingClose", true);
            ConfirmAutomationClose = await LoadSettingAsync("general.confirmAutomationClose", true);
            CompactDensity = await LoadSettingAsync("appearance.compactDensity", true);
            AlertSoundsEnabled = await LoadSettingAsync("alerts.sounds", true);
            ScreenshotOnAlert = await LoadSettingAsync("alerts.screenshotOnAlert", false);
            RecordingOnAlert = await LoadSettingAsync("alerts.recordingOnAlert", false);
            QuietMode = await LoadSettingAsync("alerts.quietMode", false);

            string? configuredAdb = await _sessions.LoadSettingAsync<string>("adb.path", CancellationToken.None);
            _adb.ConfigurePath(configuredAdb);
            SelectedAdbPath = _adb.ResolvedAdbPath ?? configuredAdb ?? "";
            OnPropertyChanged(nameof(AdbPathDisplay));

            _preferredDeviceSerial = RestoreLastDevice
                ? await _sessions.LoadSettingAsync<string>("general.lastDevice", CancellationToken.None)
                : null;
            if (RestoreLastPage)
            {
                string? lastPage = await _sessions.LoadSettingAsync<string>("general.lastPage", CancellationToken.None);
                if (!string.IsNullOrWhiteSpace(lastPage) && NavigationItems.Contains(lastPage))
                    CurrentPage = lastPage;
            }

            RuleSnapshot[]? savedRules =
                await _sessions.LoadSettingAsync<RuleSnapshot[]>("alerts.rules", CancellationToken.None);
            if (savedRules is not null)
            {
                foreach (RuleSnapshot saved in savedRules)
                {
                    AlertRule? rule = AlertRules.FirstOrDefault(item => item.Type == saved.Type);
                    if (rule is null) continue;
                    rule.Enabled = saved.Enabled;
                    rule.Threshold = saved.Threshold;
                    rule.DurationSeconds = saved.DurationSeconds;
                    rule.Severity = saved.Severity;
                }
            }

            if (AutoReconnectLastDevice &&
                !string.IsNullOrWhiteSpace(_preferredDeviceSerial) &&
                _preferredDeviceSerial.Contains(':') &&
                !IsDemo)
            {
                _ = await _adb.ExecuteAsync(
                    null,
                    ["connect", _preferredDeviceSerial],
                    TimeSpan.FromSeconds(15),
                    CancellationToken.None);
            }
        }
        finally
        {
            _loadingSettings = false;
        }

        ApplyStartupPreference();
        await RefreshAdbDiagnosticsAsync();
    }

    private async Task<T> LoadSettingAsync<T>(string key, T fallback)
    {
        T? value = await _sessions.LoadSettingAsync<T>(key, CancellationToken.None);
        return value is null ? fallback : value;
    }

    private async Task LoadAlertHistoryAsync()
    {
        HashSet<Guid> known = Alerts.Select(alert => alert.Id).ToHashSet();
        List<SessionAlert> history = [];
        foreach (SessionSummary summary in StoredSessions.Take(20))
        {
            MonitoringSession? stored =
                await _sessions.LoadSessionAsync(summary.Id, CancellationToken.None);
            if (stored is null) continue;
            history.AddRange(stored.Alerts.Where(alert => known.Add(alert.Id)));
        }
        foreach (SessionAlert alert in history.OrderByDescending(alert => alert.TimestampUtc))
            Alerts.Add(alert);
    }

    private async void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_loadingSettings || string.IsNullOrWhiteSpace(e.PropertyName)) return;
        try
        {
            switch (e.PropertyName)
            {
                case nameof(CurrentPage):
                    if (RestoreLastPage)
                        await _sessions.SaveSettingAsync("general.lastPage", CurrentPage, CancellationToken.None);
                    break;
                case nameof(SelectedDevice):
                    if (RestoreLastDevice && SelectedDevice is not null)
                    {
                        _preferredDeviceSerial = SelectedDevice.Serial;
                        await _sessions.SaveSettingAsync(
                            "general.lastDevice",
                            SelectedDevice.Serial,
                            CancellationToken.None);
                    }
                    break;
                case nameof(LaunchAtStartup):
                    ApplyStartupPreference();
                    await _sessions.SaveSettingAsync(
                        "general.launchAtStartup",
                        LaunchAtStartup,
                        CancellationToken.None);
                    break;
                case nameof(RestoreLastPage):
                    await _sessions.SaveSettingAsync(
                        "general.restoreLastPage",
                        RestoreLastPage,
                        CancellationToken.None);
                    break;
                case nameof(RestoreLastDevice):
                    await _sessions.SaveSettingAsync(
                        "general.restoreLastDevice",
                        RestoreLastDevice,
                        CancellationToken.None);
                    break;
                case nameof(AutoReconnectLastDevice):
                    await _sessions.SaveSettingAsync(
                        "general.autoReconnectLastDevice",
                        AutoReconnectLastDevice,
                        CancellationToken.None);
                    break;
                case nameof(ConfirmRecordingClose):
                    await _sessions.SaveSettingAsync(
                        "general.confirmRecordingClose",
                        ConfirmRecordingClose,
                        CancellationToken.None);
                    break;
                case nameof(ConfirmAutomationClose):
                    await _sessions.SaveSettingAsync(
                        "general.confirmAutomationClose",
                        ConfirmAutomationClose,
                        CancellationToken.None);
                    break;
                case nameof(CompactDensity):
                    await _sessions.SaveSettingAsync(
                        "appearance.compactDensity",
                        CompactDensity,
                        CancellationToken.None);
                    break;
                case nameof(AlertSoundsEnabled):
                    await _sessions.SaveSettingAsync(
                        "alerts.sounds",
                        AlertSoundsEnabled,
                        CancellationToken.None);
                    break;
                case nameof(ScreenshotOnAlert):
                    await _sessions.SaveSettingAsync(
                        "alerts.screenshotOnAlert",
                        ScreenshotOnAlert,
                        CancellationToken.None);
                    break;
                case nameof(RecordingOnAlert):
                    await _sessions.SaveSettingAsync(
                        "alerts.recordingOnAlert",
                        RecordingOnAlert,
                        CancellationToken.None);
                    break;
                case nameof(QuietMode):
                    await _sessions.SaveSettingAsync(
                        "alerts.quietMode",
                        QuietMode,
                        CancellationToken.None);
                    break;
            }
        }
        catch (Exception ex)
        {
            AdbDiagnostics = "Could not persist setting: " + ex.Message;
        }
    }

    private async void OnAlertRulePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_loadingSettings) return;
        try
        {
            RuleSnapshot[] rules = AlertRules
                .Select(rule => new RuleSnapshot(
                    rule.Type,
                    rule.Enabled,
                    rule.Threshold,
                    rule.DurationSeconds,
                    rule.Severity))
                .ToArray();
            await _sessions.SaveSettingAsync("alerts.rules", rules, CancellationToken.None);
        }
        catch (Exception ex)
        {
            StatusMessage = "Could not save alert rules: " + ex.Message;
        }
    }

    private void ApplyStartupPreference()
    {
        const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(runKey, writable: true) ??
                                 Registry.CurrentUser.CreateSubKey(runKey, writable: true);
        if (key is null) return;
        if (LaunchAtStartup && !string.IsNullOrWhiteSpace(Environment.ProcessPath))
            key.SetValue("AndroidDevMonitor", $"\"{Environment.ProcessPath}\"");
        else
            key.DeleteValue("AndroidDevMonitor", throwOnMissingValue: false);
    }

    [RelayCommand]
    private async Task RefreshNetworkDiagnosticsAsync()
    {
        AndroidDevice? target = SelectedDevice;
        if (target is null)
        {
            NetworkDiagnosticsStatus = "No Android target selected.";
            return;
        }
        if (IsDemo)
        {
            NetworkInterfaces.Clear();
            NetworkInterfaces.Add(new(
                "wlan0",
                "UP",
                "10.0.2.15/24",
                18_420_000,
                4_810_000));
            NetworkSockets.Clear();
            NetworkSockets.Add(new(
                "TCP",
                "ESTABLISHED",
                "10.0.2.15",
                "48722",
                "142.250.185.78",
                "443",
                "10123",
                SelectedPackage ?? "com.company.mygame"));
            NetworkGateway = "10.0.2.2";
            NetworkDnsServers = "10.0.2.3";
            NetworkConnectionState = "Connected · Wi-Fi · DEMO DATA";
            NetworkDiagnosticsStatus = "DEMO DATA · deterministic interface and socket diagnostics";
            ApplyNetworkSocketFilter();
            return;
        }

        NetworkDiagnosticsStatus = $"Collecting network diagnostics from {target.Serial}…";
        const string diagnosticsCommand =
            "echo __LINK__; ip -o link show 2>/dev/null; " +
            "echo __ADDR__; ip -o addr show 2>/dev/null; " +
            "echo __ROUTE__; ip route 2>/dev/null; " +
            "echo __DNS__; getprop 2>/dev/null | grep -i dns; " +
            "echo __CONNECTIVITY__; dumpsys connectivity 2>/dev/null; " +
            "echo __NETDEV__; cat /proc/net/dev 2>/dev/null";
        AdbCommandResult diagnostics = await _adb.ExecuteAsync(
            target.Serial,
            ["shell", "sh", "-c", diagnosticsCommand],
            TimeSpan.FromSeconds(15),
            CancellationToken.None);
        TrackAdbCommand(diagnostics);

        AdbCommandResult sockets = await _adb.ExecuteAsync(
            target.Serial,
            ["shell", "sh", "-c", "ss -tunap 2>/dev/null || netstat -tun 2>/dev/null"],
            TimeSpan.FromSeconds(15),
            CancellationToken.None);
        TrackAdbCommand(sockets);

        int? selectedUid = null;
        if (!string.IsNullOrWhiteSpace(SelectedPackage))
        {
            AdbCommandResult package = await _adb.ExecuteAsync(
                target.Serial,
                ["shell", "dumpsys", "package", SelectedPackage],
                TimeSpan.FromSeconds(15),
                CancellationToken.None);
            TrackAdbCommand(package);
            Match uidMatch = Regex.Match(package.StandardOutput, @"\buserId=(?<uid>\d+)");
            if (uidMatch.Success && int.TryParse(uidMatch.Groups["uid"].Value, out int uid))
                selectedUid = uid;
        }

        NetworkInterfaces.Clear();
        if (diagnostics.Success)
        {
            IReadOnlyDictionary<string, List<string>> sections =
                SplitDiagnosticSections(diagnostics.StandardOutput);
            foreach (NetworkInterfaceRow item in ParseNetworkInterfaces(sections))
                NetworkInterfaces.Add(item);
            NetworkGateway =
                ParseGateway(sections.GetValueOrDefault("ROUTE")) ??
                ParseConnectivityGateway(sections.GetValueOrDefault("CONNECTIVITY")) ??
                "N/A";
            string[] dns = ParseDnsServers(sections.GetValueOrDefault("DNS"));
            if (dns.Length == 0)
                dns = ParseConnectivityDnsServers(sections.GetValueOrDefault("CONNECTIVITY"));
            NetworkDnsServers = dns.Length == 0 ? "N/A" : string.Join(", ", dns);
        }
        else
        {
            NetworkGateway = "N/A";
            NetworkDnsServers = "N/A";
        }

        NetworkSockets.Clear();
        if (sockets.Success)
        {
            foreach (NetworkSocketRow socket in ParseNetworkSockets(
                         sockets.StandardOutput,
                         selectedUid,
                         SelectedPackage))
                NetworkSockets.Add(socket);
        }
        ApplyNetworkSocketFilter();

        int connectionFailures = Logs.Count(log =>
            log.Message.Contains("connection refused", StringComparison.OrdinalIgnoreCase) ||
            log.Message.Contains("network is unreachable", StringComparison.OrdinalIgnoreCase) ||
            log.Message.Contains("unknown host", StringComparison.OrdinalIgnoreCase) ||
            log.Message.Contains("connect timed out", StringComparison.OrdinalIgnoreCase));
        NetworkFailureSummary = connectionFailures == 0
            ? "No connection failures detected in buffered logs."
            : $"{connectionFailures} connection failure(s) detected in buffered logs.";
        NetworkConnectionState = target.IsConnected
            ? $"{(NetworkInterfaces.Any(item => item.State == "UP") ? "Connected" : "Interface state unavailable")} · {NetworkContext}"
            : target.State.ToString();
        NetworkDiagnosticsStatus =
            $"{NetworkInterfaces.Count} interface(s) · {NetworkSockets.Count} socket(s) · " +
            $"interfaces {Describe(diagnostics)} · sockets {Describe(sockets)}";
    }

    partial void OnSelectedNetworkFilterChanged(string value) => ApplyNetworkSocketFilter();

    private void ApplyNetworkSocketFilter()
    {
        IEnumerable<NetworkSocketRow> query = NetworkSockets;
        query = SelectedNetworkFilter switch
        {
            "Selected package" => query.Where(socket =>
                !string.IsNullOrWhiteSpace(SelectedPackage) &&
                string.Equals(socket.PackageName, SelectedPackage, StringComparison.Ordinal)),
            "TCP" => query.Where(socket => socket.Protocol.StartsWith("TCP", StringComparison.OrdinalIgnoreCase)),
            "UDP" => query.Where(socket => socket.Protocol.StartsWith("UDP", StringComparison.OrdinalIgnoreCase)),
            "Active connections" => query.Where(socket => socket.State is
                "ESTAB" or "ESTABLISHED" or "LISTEN" or "LISTENING" or "UNCONN"),
            "Closed or failed connections" => query.Where(socket => socket.State.Contains(
                "CLOSE",
                StringComparison.OrdinalIgnoreCase) || socket.State is "TIME-WAIT" or "SYN-SENT"),
            _ => query
        };
        FilteredNetworkSockets.Clear();
        foreach (NetworkSocketRow item in query)
            FilteredNetworkSockets.Add(item);
    }

    private static IReadOnlyDictionary<string, List<string>> SplitDiagnosticSections(string output)
    {
        Dictionary<string, List<string>> result = new(StringComparer.Ordinal);
        List<string>? current = null;
        foreach (string raw in output.Replace("\r", "").Split('\n'))
        {
            string line = raw.TrimEnd();
            Match marker = Regex.Match(line, @"^__(?<name>[A-Z]+)__$");
            if (marker.Success)
            {
                current = [];
                result[marker.Groups["name"].Value] = current;
            }
            else if (current is not null && !string.IsNullOrWhiteSpace(line))
            {
                current.Add(line);
            }
        }
        return result;
    }

    private static IReadOnlyList<NetworkInterfaceRow> ParseNetworkInterfaces(
        IReadOnlyDictionary<string, List<string>> sections)
    {
        Dictionary<string, (string State, List<string> Addresses, long? Rx, long? Tx)> interfaces =
            new(StringComparer.Ordinal);
        foreach (string line in sections.GetValueOrDefault("LINK") ?? [])
        {
            Match match = Regex.Match(
                line,
                @"^\d+:\s+(?<name>[^:@]+)(?:@[^:]+)?:\s+<(?<flags>[^>]*)>.*?(?:\bstate\s+(?<state>\S+))?");
            if (!match.Success) continue;
            string name = match.Groups["name"].Value;
            string state = match.Groups["state"].Success
                ? match.Groups["state"].Value
                : match.Groups["flags"].Value.Split(',').Contains("UP") ? "UP" : "DOWN";
            interfaces[name] = (state, [], null, null);
        }
        foreach (string line in sections.GetValueOrDefault("ADDR") ?? [])
        {
            Match match = Regex.Match(
                line,
                @"^\d+:\s+(?<name>[^@\s]+)(?:@\S+)?\s+(?<family>inet6?)\s+(?<address>\S+)");
            if (!match.Success) continue;
            string name = match.Groups["name"].Value;
            if (!interfaces.TryGetValue(name, out var item))
                item = ("Unknown", [], null, null);
            item.Addresses.Add(match.Groups["address"].Value);
            if (item.State == "Unknown")
                item.State = "UP";
            interfaces[name] = item;
        }
        foreach (string line in sections.GetValueOrDefault("NETDEV") ?? [])
        {
            int separator = line.IndexOf(':');
            if (separator <= 0) continue;
            string name = line[..separator].Trim();
            string[] values = Regex.Split(line[(separator + 1)..].Trim(), @"\s+");
            if (values.Length < 9 ||
                !long.TryParse(values[0], out long rx) ||
                !long.TryParse(values[8], out long tx))
                continue;
            if (!interfaces.TryGetValue(name, out var item))
                item = ("Unknown", [], null, null);
            item.Rx = rx;
            item.Tx = tx;
            interfaces[name] = item;
        }
        return interfaces
            .OrderBy(item => item.Key == "lo" ? 1 : 0)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => new NetworkInterfaceRow(
                item.Key,
                item.Value.State,
                item.Value.Addresses.Count == 0 ? "N/A" : string.Join(", ", item.Value.Addresses),
                item.Value.Rx,
                item.Value.Tx))
            .ToArray();
    }

    private static string? ParseGateway(IEnumerable<string>? lines)
    {
        foreach (string line in lines ?? [])
        {
            Match match = Regex.Match(line, @"^default(?:\s+via\s+(?<gateway>\S+))?\s+dev\s+(?<interface>\S+)");
            if (match.Success)
                return match.Groups["gateway"].Success
                    ? $"{match.Groups["gateway"].Value} via {match.Groups["interface"].Value}"
                    : $"via {match.Groups["interface"].Value}";
        }
        return null;
    }

    private static string? ParseConnectivityGateway(IEnumerable<string>? lines)
    {
        string text = string.Join('\n', lines ?? []);
        Match route = Regex.Match(
            text,
            @"(?:0\.0\.0\.0/0|default)\s+->\s+(?<gateway>(?:\d{1,3}\.){3}\d{1,3})\s+(?<interface>\S+)");
        if (route.Success)
            return $"{route.Groups["gateway"].Value} via {route.Groups["interface"].Value}";
        Match server = Regex.Match(text, @"ServerAddress:\s*/(?<gateway>(?:\d{1,3}\.){3}\d{1,3})");
        return server.Success ? server.Groups["gateway"].Value : null;
    }

    private static string[] ParseDnsServers(IEnumerable<string>? lines) =>
        (lines ?? [])
        .SelectMany(line => Regex.Matches(line, @"(?<address>(?:\d{1,3}\.){3}\d{1,3}|[0-9a-fA-F:]{3,})")
            .Select(match => match.Groups["address"].Value))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static string[] ParseConnectivityDnsServers(IEnumerable<string>? lines)
    {
        string text = string.Join('\n', lines ?? []);
        Match dns = Regex.Match(text, @"DnsAddresses:\s*\[(?<addresses>[^\]]+)\]");
        return dns.Success
            ? Regex.Matches(
                    dns.Groups["addresses"].Value,
                    @"(?<address>(?:\d{1,3}\.){3}\d{1,3}|[0-9a-fA-F:]{3,})")
                .Select(match => match.Groups["address"].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];
    }

    private static IReadOnlyList<NetworkSocketRow> ParseNetworkSockets(
        string output,
        int? selectedUid,
        string? selectedPackage)
    {
        List<NetworkSocketRow> result = [];
        foreach (string line in output.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] values = Regex.Split(line.Trim(), @"\s+");
            if (values.Length < 5 ||
                !(values[0].StartsWith("tcp", StringComparison.OrdinalIgnoreCase) ||
                  values[0].StartsWith("udp", StringComparison.OrdinalIgnoreCase)))
                continue;

            string protocol = values[0].ToUpperInvariant();
            string state;
            string local;
            string remote;
            if (values.Length >= 6 && !long.TryParse(values[1], out _))
            {
                state = values[1].ToUpperInvariant();
                local = values[4];
                remote = values[5];
            }
            else if (values.Length >= 6)
            {
                state = values[^1].ToUpperInvariant();
                local = values[3];
                remote = values[4];
            }
            else
            {
                continue;
            }

            Match uidMatch = Regex.Match(line, @"\buid[:=](?<uid>\d+)");
            string uidText = uidMatch.Success ? uidMatch.Groups["uid"].Value : "N/A";
            string package = selectedUid.HasValue &&
                             uidMatch.Success &&
                             int.TryParse(uidText, out int uid) &&
                             uid == selectedUid.Value
                ? selectedPackage ?? "N/A"
                : "N/A";
            (string localAddress, string localPort) = SplitEndpoint(local);
            (string remoteAddress, string remotePort) = SplitEndpoint(remote);
            result.Add(new(
                protocol,
                state,
                localAddress,
                localPort,
                remoteAddress,
                remotePort,
                uidText,
                package));
        }
        return result;
    }

    private static (string Address, string Port) SplitEndpoint(string endpoint)
    {
        endpoint = endpoint.Trim();
        if (endpoint.StartsWith('['))
        {
            int close = endpoint.LastIndexOf("]:", StringComparison.Ordinal);
            return close > 0
                ? (endpoint[1..close], endpoint[(close + 2)..])
                : (endpoint.Trim('[', ']'), "N/A");
        }
        int separator = endpoint.LastIndexOf(':');
        return separator > 0
            ? (endpoint[..separator], endpoint[(separator + 1)..])
            : (endpoint, "N/A");
    }

    [RelayCommand]
    private void AcknowledgeAlert()
    {
        if (SelectedActiveAlert is null) return;
        ResolveAlert(SelectedActiveAlert.Type);
    }

    [RelayCommand]
    private void ClearResolvedAlerts()
    {
        foreach (SessionAlert alert in Alerts.Where(item => item.Resolved).ToArray())
            Alerts.Remove(alert);
        StatusMessage = "Resolved alert history cleared from this view";
    }

    [RelayCommand]
    private async Task ExportAlertsAsync()
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Personal),
            "Android Dev Monitor",
            "Exports");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"alerts-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.csv");
        StringBuilder csv = new("timestamp_utc,severity,type,resolved,message\r\n");
        foreach (SessionAlert alert in Alerts.OrderBy(item => item.TimestampUtc))
        {
            csv.Append(Csv(alert.TimestampUtc.ToString("O"))).Append(',')
                .Append(Csv(alert.Severity)).Append(',')
                .Append(Csv(alert.Type)).Append(',')
                .Append(alert.Resolved ? "true" : "false").Append(',')
                .Append(Csv(alert.Message)).Append("\r\n");
        }
        await File.WriteAllTextAsync(path, csv.ToString(), Encoding.UTF8);
        StatusMessage = "Alert history exported to " + Path.GetFileName(path);
        _dialogs.Notify("Alert history exported:\n" + path);
    }

    [RelayCommand]
    private void SelectAdbExecutable()
    {
        OpenFileDialog dialog = new()
        {
            Title = "Select adb.exe",
            Filter = "Android Debug Bridge (adb.exe)|adb.exe|Executable files (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() == true)
            SelectedAdbPath = dialog.FileName;
    }

    [RelayCommand]
    private async Task ApplyAdbPathAsync()
    {
        if (!string.IsNullOrWhiteSpace(SelectedAdbPath) && !File.Exists(SelectedAdbPath))
        {
            _dialogs.Notify("The selected adb.exe path does not exist.", error: true);
            return;
        }

        _adb.ConfigurePath(SelectedAdbPath);
        SelectedAdbPath = _adb.ResolvedAdbPath ?? SelectedAdbPath;
        await _sessions.SaveSettingAsync("adb.path", SelectedAdbPath, CancellationToken.None);
        OnPropertyChanged(nameof(AdbPathDisplay));
        await RefreshAdbDiagnosticsAsync();
        await RefreshDevicesAsync();
    }

    [RelayCommand]
    private async Task AutoDetectAdbAsync()
    {
        _adb.ConfigurePath(null);
        SelectedAdbPath = _adb.ResolvedAdbPath ?? "";
        await _sessions.SaveSettingAsync("adb.path", SelectedAdbPath, CancellationToken.None);
        OnPropertyChanged(nameof(AdbPathDisplay));
        await RefreshAdbDiagnosticsAsync();
        await RefreshDevicesAsync();
    }

    [RelayCommand]
    private Task TestAdbConnectionAsync() => RefreshAdbDiagnosticsAsync();

    private async Task RefreshAdbDiagnosticsAsync()
    {
        if (IsDemo)
        {
            AdbVersion = "Demo provider";
            AdbServerState = "ADB disabled in demo mode";
            AdbDiagnostics = "DEMO DATA uses deterministic local providers and never invokes adb.exe.";
            return;
        }

        if (_adb.ResolvedAdbPath is null)
        {
            AdbVersion = "Not found";
            AdbServerState = "Unavailable";
            AdbDiagnostics =
                "adb.exe was not found. Select it here or install Android Platform Tools.";
            return;
        }

        AdbCommandResult version = await _adb.ExecuteAsync(
            null,
            ["version"],
            TimeSpan.FromSeconds(15),
            CancellationToken.None);
        TrackAdbCommand(version);
        AdbCommandResult devices = await _adb.ExecuteAsync(
            null,
            ["devices", "-l"],
            TimeSpan.FromSeconds(15),
            CancellationToken.None);
        TrackAdbCommand(devices);
        AdbVersion = version.Success
            ? version.StandardOutput.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? "Detected"
            : "Unavailable";
        AdbServerState = devices.Success ? "Running" : devices.TimedOut ? "Timed out" : "Error";
        AdbDiagnostics =
            $"Path: {_adb.ResolvedAdbPath}\n" +
            $"Version command: {Describe(version)}\n" +
            $"Devices command: {Describe(devices)}\n" +
            (devices.Success ? devices.StandardOutput.Trim() : devices.StandardError.Trim());
    }

    [RelayCommand]
    private async Task RestartAdbServerAsync()
    {
        string serial = SelectedDevice?.Serial ?? "no selected device";
        if (!_dialogs.Confirm(
                "Restart ADB server",
                $"Selected target: {serial}\n\n" +
                "This restarts the global ADB server and temporarily disconnects every Android target. Continue?"))
            return;

        AdbCommandResult stop = await _adb.ExecuteAsync(
            null,
            ["kill-server"],
            TimeSpan.FromSeconds(15),
            CancellationToken.None);
        TrackAdbCommand(stop);
        AdbCommandResult start = await _adb.ExecuteAsync(
            null,
            ["start-server"],
            TimeSpan.FromSeconds(20),
            CancellationToken.None);
        TrackAdbCommand(start);
        AdbServerState = start.Success ? "Running" : "Restart failed";
        AdbDiagnostics = $"kill-server: {Describe(stop)}\nstart-server: {Describe(start)}";
        StatusMessage = start.Success ? "ADB server restarted" : "ADB server restart failed";
        await RefreshDevicesAsync();
    }

    [RelayCommand]
    private void OpenSessionDirectory() => OpenDirectory(SessionDirectory);

    [RelayCommand]
    private async Task ClearOldSessionsAsync()
    {
        IReadOnlyList<SessionSummary> all = await _sessions.ListSessionSummariesAsync(CancellationToken.None);
        SessionSummary[] completed = all
            .Where(item => item.EndedUtc.HasValue && item.Id != _session?.Id)
            .ToArray();
        if (completed.Length == 0)
        {
            StatusMessage = "No completed sessions to clear";
            return;
        }
        if (!_dialogs.Confirm(
                "Clear completed sessions",
                $"Delete {completed.Length} completed local session(s)?\nDatabase: {DatabasePath}"))
            return;
        foreach (SessionSummary session in completed)
            await _sessions.DeleteSessionAsync(session.Id, CancellationToken.None);
        await ReloadStoredSessionsAsync();
        StatusMessage = $"{completed.Length} completed session(s) deleted";
    }

    [RelayCommand]
    private void OpenDiagnosticsDirectory() => OpenDirectory(DiagnosticsDirectory);

    [RelayCommand]
    private async Task ExportDiagnosticsAsync()
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Personal),
            "Android Dev Monitor",
            "Diagnostics");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"diagnostics-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.zip");

        await using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using ZipArchive archive = new(stream, ZipArchiveMode.Create);
        foreach (string file in Directory.EnumerateFiles(DataDirectory, "*", SearchOption.AllDirectories))
        {
            try
            {
                string relative = Path.GetRelativePath(DataDirectory, file);
                ZipArchiveEntry entry = archive.CreateEntry(relative, CompressionLevel.Fastest);
                await using Stream target = entry.Open();
                await using FileStream source = new(
                    file,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                await source.CopyToAsync(target);
            }
            catch
            {
                // A concurrently rotated log is allowed to disappear.
            }
        }

        ZipArchiveEntry summary = archive.CreateEntry("diagnostics.txt", CompressionLevel.Fastest);
        await using (Stream target = summary.Open())
        await using (StreamWriter writer = new(target, Encoding.UTF8))
        {
            await writer.WriteAsync(
                $"Generated: {DateTimeOffset.Now:O}\n" +
                $"Version: {ApplicationVersion}\n" +
                $".NET: {DotNetVersion}\n" +
                $"Mode: {(IsDemo ? "DEMO DATA" : "Live ADB")}\n" +
                $"Target: {DeviceContext}\n" +
                $"ADB: {AdbPathDisplay}\n" +
                $"Collectors: {CollectorStatus}\n" +
                $"Last data: {DataAge}\n");
        }
        StatusMessage = "Diagnostics exported to " + Path.GetFileName(path);
        _dialogs.Notify("Diagnostics package exported:\n" + path);
    }

    [RelayCommand]
    private async Task ResetDemoDataAsync()
    {
        if (!IsDemo)
        {
            _dialogs.Notify("Reset demo data is available only when the app starts with --demo.");
            return;
        }
        if (!_dialogs.Confirm(
                "Reset demo data",
                "Delete demo media and completed demo sessions? Live Android data is not affected."))
            return;

        foreach (MediaItem item in MediaItems.ToArray())
        {
            try { await _media.DeleteAsync(item, CancellationToken.None); }
            catch { }
        }
        IReadOnlyList<SessionSummary> sessions =
            await _sessions.ListSessionSummariesAsync(CancellationToken.None);
        foreach (SessionSummary session in sessions.Where(item =>
                     item.Device.Serial.StartsWith("demo-", StringComparison.OrdinalIgnoreCase) &&
                     item.Id != _session?.Id))
            await _sessions.DeleteSessionAsync(session.Id, CancellationToken.None);
        MediaItems.Clear();
        await ReloadMediaAsync();
        await ReloadStoredSessionsAsync();
        StatusMessage = "Demo data reset";
    }

    [RelayCommand]
    private void RestartWithOtherDataMode()
    {
        if (!CanClose()) return;

        string targetMode = IsDemo ? "live ADB mode" : "Demo mode";
        if (!_dialogs.Confirm(
                "Restart Android Dev Monitor",
                $"Restart the application in {targetMode}?"))
            return;

        string? executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            _dialogs.Notify("The current executable path could not be resolved.", true);
            return;
        }

        ProcessStartInfo start = new(executable)
        {
            UseShellExecute = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            string? managedEntryPoint = Environment.GetCommandLineArgs().FirstOrDefault();
            if (string.IsNullOrWhiteSpace(managedEntryPoint) || !File.Exists(managedEntryPoint))
            {
                _dialogs.Notify("The managed application path could not be resolved.", true);
                return;
            }
            start.ArgumentList.Add(managedEntryPoint);
        }
        if (!IsDemo)
            start.ArgumentList.Add("--demo");
        Process.Start(start);
        System.Windows.Application.Current.Shutdown();
    }

    public bool CanClose()
    {
        if (IsRecording && ConfirmRecordingClose &&
            !_dialogs.Confirm(
                "Recording is active",
                $"Screen recording is active on {_recordingTarget?.Serial ?? SelectedDevice?.Serial ?? "the selected target"}. Close anyway?"))
            return false;
        if (IsAutomationRunning && ConfirmAutomationClose &&
            !_dialogs.Confirm(
                "Automation is running",
                $"Automation is still bound to {SelectedDevice?.Serial ?? "its captured target"}. Close anyway?"))
            return false;
        return true;
    }

    private void SetRecordingContext(AndroidDevice target, string? packageName, Guid? sessionId)
    {
        _recordingTarget = target;
        _recordingPackage = packageName;
        _recordingSessionId = sessionId;
    }

    private void ClearRecordingContext()
    {
        _recordingTarget = null;
        _recordingPackage = null;
        _recordingSessionId = null;
    }

    private void EvaluateAlertCondition(
        string type,
        bool? condition,
        string message,
        string? fallbackSeverity = null)
    {
        AlertRule? rule = FindAlertRule(type);
        if (rule is not null && !rule.Enabled && !rule.SystemCritical)
        {
            _alertConditionSince.Remove(type);
            ResolveAlert(type);
            return;
        }
        if (!condition.HasValue) return;
        if (!condition.Value)
        {
            _alertConditionSince.Remove(type);
            ResolveAlert(type);
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!_alertConditionSince.TryGetValue(type, out DateTimeOffset since))
        {
            since = now;
            _alertConditionSince[type] = since;
        }
        if (now - since >= (rule?.Duration ?? TimeSpan.Zero))
            RaiseAlert(type, rule?.Severity ?? fallbackSeverity ?? "Warning", message);
    }

    private AlertRule? FindAlertRule(string type) =>
        AlertRules.FirstOrDefault(rule => rule.Type.Equals(type, StringComparison.Ordinal));

    private double RuleThreshold(string type, double fallback) =>
        FindAlertRule(type)?.Threshold ?? fallback;

    private void ResetAlertStateForTarget()
    {
        ActiveAlerts.Clear();
        _activeAlertTypes.Clear();
        _alertConditionSince.Clear();
        _selectedPackageObservedRunning = false;
        _networkWasConnected = false;
    }

    private async Task HandleSelectedDeviceUnavailableAsync(AndroidDevice? previous)
    {
        _contextCts?.Cancel();
        MonitoringSession? session = _session;
        _session = null;
        if (session is not null)
        {
            session.EndedUtc = DateTimeOffset.UtcNow;
            SessionEvent sessionEvent = new(
                Guid.NewGuid(),
                session.Id,
                DateTimeOffset.UtcNow,
                "SessionEnded",
                $"Monitoring stopped because {previous?.Serial ?? session.Device.Serial} is unavailable.",
                session.Device.Serial,
                session.CurrentPackage);
            lock (session.SyncRoot)
                session.Events.Add(sessionEvent);
            await _sessions.SaveEventAsync(sessionEvent, CancellationToken.None);
            await _sessions.SaveSessionAsync(session, CancellationToken.None);
        }
        ConnectionText = "No devices";
        StatusMessage = previous is null
            ? "No Android device selected"
            : $"{previous.FriendlyName} disconnected";
    }

    private void EvaluateSelectedProcessAlert(IReadOnlyList<AndroidProcess> processes)
    {
        if (string.IsNullOrWhiteSpace(SelectedPackage))
        {
            _selectedPackageObservedRunning = false;
            ResolveAlert("AppExited");
            return;
        }
        bool running = processes.Any(process =>
            string.Equals(process.PackageName, SelectedPackage, StringComparison.Ordinal));
        if (running)
        {
            _selectedPackageObservedRunning = true;
            EvaluateAlertCondition("AppExited", false, "");
        }
        else if (_selectedPackageObservedRunning)
        {
            EvaluateAlertCondition(
                "AppExited",
                true,
                $"Selected app process {SelectedPackage} exited.",
                "Critical");
        }
    }

    private void EvaluateExtendedSampleAlerts(MetricSample sample)
    {
        double diskWriteThreshold = RuleThreshold("DiskWriteSpike", 50);
        EvaluateAlertCondition(
            "DiskWriteSpike",
            sample.DiskWriteBytesPerSecond.HasValue
                ? sample.DiskWriteBytesPerSecond.Value > diskWriteThreshold * 1024 * 1024
                : null,
            $"Disk writes reached {FormatRate(sample.DiskWriteBytesPerSecond)}.");

        double networkSpikeThreshold = RuleThreshold("NetworkSpike", 25);
        double? totalNetwork =
            !sample.NetworkRxBytesPerSecond.HasValue && !sample.NetworkTxBytesPerSecond.HasValue
                ? null
                : sample.NetworkRxBytesPerSecond.GetValueOrDefault() +
                  sample.NetworkTxBytesPerSecond.GetValueOrDefault();
        EvaluateAlertCondition(
            "NetworkSpike",
            totalNetwork.HasValue
                ? totalNetwork.Value > networkSpikeThreshold * 1024 * 1024
                : null,
            $"Device traffic reached {FormatRate(totalNetwork)}.");

        bool hasNetworkContext =
            sample.ActiveNetworkInterface is not null ||
            sample.NetworkType is not null;
        bool? networkConnected = hasNetworkContext
            ? !string.IsNullOrWhiteSpace(sample.ActiveNetworkInterface) &&
              !string.Equals(sample.NetworkType, "None", StringComparison.OrdinalIgnoreCase) &&
              !string.Equals(sample.NetworkType, "Disconnected", StringComparison.OrdinalIgnoreCase)
            : null;
        if (networkConnected == true) _networkWasConnected = true;
        EvaluateAlertCondition(
            "NetworkLost",
            networkConnected.HasValue
                ? _networkWasConnected && !networkConnected.Value
                : null,
            "The active Android network connection was lost.",
            "Critical");
        EvaluateAlertCondition(
            "NetworkStopped",
            networkConnected == true && totalNetwork.HasValue
                ? totalNetwork.Value < RuleThreshold("NetworkStopped", 0.01) * 1024
                : null,
            "Network traffic unexpectedly stopped.");

        long? currentMemory = sample.ProcessPssBytes ?? sample.ProcessRssBytes;
        MetricSample? previous = _liveSamples.Snapshot()
            .Where(item =>
                item.TimestampUtc <= sample.TimestampUtc - TimeSpan.FromSeconds(25) &&
                string.Equals(item.PackageName, sample.PackageName, StringComparison.Ordinal))
            .LastOrDefault();
        long? previousMemory = previous?.ProcessPssBytes ?? previous?.ProcessRssBytes;
        double? growthMb = currentMemory.HasValue && previousMemory.HasValue
            ? (currentMemory.Value - previousMemory.Value) / (1024d * 1024d)
            : null;
        EvaluateAlertCondition(
            "RapidMemoryGrowth",
            growthMb.HasValue ? growthMb.Value > RuleThreshold("RapidMemoryGrowth", 100) : null,
            $"Selected-app memory grew by {growthMb:N1} MB in about 30 seconds.");

        long? totalMemory = SelectedDevice?.TotalMemoryBytes;
        double? usedPercent = totalMemory > 0 && sample.DeviceMemoryUsedBytes.HasValue
            ? sample.DeviceMemoryUsedBytes.Value * 100d / totalMemory.Value
            : null;
        EvaluateAlertCondition(
            "HighMemory",
            usedPercent.HasValue ? usedPercent.Value > RuleThreshold("HighMemory", 90) : null,
            $"Device memory use is {usedPercent:N1}%.");

        bool throttling = sample.ThermalSeverity is
            "Moderate" or "Severe" or "Critical" or "Emergency" or "Shutdown";
        EvaluateAlertCondition(
            "ThermalThrottling",
            sample.ThermalSeverity is null ? null : throttling,
            $"Android reports thermal throttling ({sample.ThermalSeverity}).",
            throttling && sample.ThermalSeverity is not "Moderate" ? "Critical" : "Warning");
    }

    private void TrackAdbCommand(AdbCommandResult result)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string command = string.Join(' ', result.Command);
        string outcome = result.TimedOut
            ? "timeout"
            : result.Cancelled
                ? "cancelled"
                : $"exit {result.ExitCode?.ToString() ?? "N/A"}";
        _recentAdbCommandTimings.Enqueue(
            $"{result.FinishedUtc.ToLocalTime():HH:mm:ss} · {result.Duration.TotalMilliseconds:N0} ms · {outcome} · adb {command}");
        while (_recentAdbCommandTimings.Count > 3)
            _recentAdbCommandTimings.Dequeue();
        LastAdbCommandTimings = string.Join("\n", _recentAdbCommandTimings.Reverse());

        _adbCommandOutcomes.Enqueue((now, result.TimedOut));
        while (_adbCommandOutcomes.TryPeek(out var item) &&
               now - item.Timestamp > TimeSpan.FromMinutes(1))
            _adbCommandOutcomes.Dequeue();
        int timedOut = _adbCommandOutcomes.Count(item => item.TimedOut);
        double rate = _adbCommandOutcomes.Count == 0
            ? 0
            : timedOut * 100d / _adbCommandOutcomes.Count;
        EvaluateAlertCondition(
            "AdbTimeoutRate",
            _adbCommandOutcomes.Count >= 4 && rate >= RuleThreshold("AdbTimeoutRate", 25),
            $"ADB timeout rate is {rate:N0}% over the last minute.");
    }

    private void NotifyAlertRaised(SessionAlert alert)
    {
        ActiveAlerts.Insert(0, alert);
        if (AlertSoundsEnabled && !QuietMode)
            SystemSounds.Exclamation.Play();
        _ = RunAlertMediaActionsAsync(alert);
    }

    private async Task RunAlertMediaActionsAsync(SessionAlert alert)
    {
        AndroidDevice? target = SelectedDevice;
        MonitoringSession? session = _session;
        if (target is null || session is null || !target.IsConnected || session.Device.Serial != target.Serial)
            return;
        if (ScreenshotOnAlert)
        {
            try
            {
                MediaItem screenshot = await _media.CaptureScreenshotAsync(
                    target,
                    session.CurrentPackage,
                    session.Id,
                    CancellationToken.None);
                screenshot = await _media.SaveMetadataAsync(
                    screenshot with { Note = $"Captured for alert {alert.Type}" },
                    CancellationToken.None);
                AddMediaItem(screenshot);
            }
            catch (Exception ex)
            {
                Logs.Add(new LogEntry(
                    DateTimeOffset.UtcNow,
                    "Android Dev Monitor",
                    "Warning",
                    "Screenshot-on-alert failed: " + ex.Message,
                    target.Serial,
                    null,
                    session.CurrentPackage));
                TrimLogs();
            }
        }
        if (RecordingOnAlert && !IsRecording && alert.Type != "RecordingFailed")
        {
            try
            {
                await _media.StartRecordingAsync(target, CancellationToken.None);
                SetRecordingContext(target, session.CurrentPackage, session.Id);
                IsRecording = true;
                StatusMessage = $"Recording started for alert {alert.Type} on {target.Serial}";
            }
            catch (Exception ex)
            {
                RaiseAlert("RecordingFailed", "Critical", "Recording-on-alert failed: " + ex.Message);
            }
        }
    }

    private void NotifyAlertResolved(SessionAlert previous, SessionAlert resolved)
    {
        int activeIndex = ActiveAlerts.IndexOf(previous);
        if (activeIndex >= 0) ActiveAlerts.RemoveAt(activeIndex);
        else
        {
            SessionAlert? active = ActiveAlerts.FirstOrDefault(item => item.Id == resolved.Id);
            if (active is not null) ActiveAlerts.Remove(active);
        }
    }

    private static string Describe(AdbCommandResult result)
    {
        if (result.Cancelled) return "cancelled";
        if (result.TimedOut) return $"timed out after {result.Duration.TotalSeconds:N1}s";
        return result.Success
            ? $"OK ({result.Duration.TotalMilliseconds:N0} ms)"
            : $"failed ({result.ExitCode?.ToString() ?? "no exit code"}): {result.StandardError.Trim()}";
    }

    private async Task<IReadOnlyList<LogEntry>> ReadApplicationDiagnosticsAsync(
        string deviceSerial,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(DiagnosticsDirectory)) return [];
        string? path = Directory.EnumerateFiles(
                DiagnosticsDirectory,
                "android-dev-monitor-*.log",
                SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (path is null) return [];

        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        long start;
        if (!_diagnosticLogOffsets.TryGetValue(path, out long savedOffset))
            start = Math.Max(0, stream.Length - 32 * 1024);
        else
            start = savedOffset > stream.Length ? 0 : savedOffset;
        int count = (int)Math.Min(64 * 1024, stream.Length - start);
        if (count <= 0) return [];

        byte[] buffer = new byte[count];
        stream.Position = start;
        int read = await stream.ReadAsync(buffer, cancellationToken);
        _diagnosticLogOffsets[path] = start + read;
        string text = Encoding.UTF8.GetString(buffer, 0, read);
        string[] lines = text.Replace("\r", "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (start > 0 && lines.Length > 0)
            lines = lines[1..];

        return lines.TakeLast(25).Select(line =>
        {
            Match match = Regex.Match(
                line,
                @"^(?<time>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}(?:\.\d+)?)\s+(?:[+-]\d{2}:\d{2}\s+)?\[(?<level>[A-Z]{3})\]\s*(?<message>.*)$");
            DateTimeOffset timestamp = match.Success &&
                                       DateTimeOffset.TryParse(
                                           match.Groups["time"].Value,
                                           out DateTimeOffset parsed)
                ? parsed.ToUniversalTime()
                : DateTimeOffset.UtcNow;
            string level = match.Groups["level"].Value switch
            {
                "ERR" or "FTL" => "Error",
                "WRN" => "Warning",
                "DBG" or "VRB" => "Debug",
                _ => "Info"
            };
            string message = match.Success ? match.Groups["message"].Value : line;
            return new LogEntry(
                timestamp,
                "Android Dev Monitor",
                level,
                message,
                deviceSerial);
        }).ToArray();
    }

    private static void OpenDirectory(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static long GetFilesSize(IEnumerable<string> paths)
    {
        long total = 0;
        foreach (string path in paths)
        {
            try
            {
                if (File.Exists(path))
                    total += new FileInfo(path).Length;
            }
            catch
            {
                // A SQLite sidecar may rotate while Settings is being rendered.
            }
        }
        return total;
    }

    private static long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Sum(file =>
                {
                    try { return new FileInfo(file).Length; }
                    catch { return 0L; }
                });
        }
        catch
        {
            return 0;
        }
    }

    private static string FormatStorageUsageSummary(long usedBytes, long warningBytes)
    {
        string state = usedBytes >= warningBytes ? "WARNING threshold reached" : "below warning threshold";
        return $"{FormatBytes(usedBytes)} used · warning at {FormatBytes(warningBytes)} · {state}";
    }

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private static AlertRule[] CreateDefaultAlertRules() =>
    [
        new("DeviceDisconnected", "Device state", true, null, TimeSpan.Zero, "Critical", "", "Device disconnected", true),
        new("DeviceOffline", "Device state", true, null, TimeSpan.Zero, "Critical", "", "Device became offline", true),
        new("DeviceUnauthorized", "Device state", true, null, TimeSpan.Zero, "Critical", "", "ADB authorization is missing", true),
        new("AdbTimeoutRate", "ADB command timeout rate", true, 25, TimeSpan.FromSeconds(15), "Warning", "%", "Too many ADB commands timed out"),
        new("AppExited", "Selected process", true, null, TimeSpan.Zero, "Critical", "", "Selected app process exited", true),
        new("Crash", "logcat crash", true, null, TimeSpan.Zero, "Critical", "", "Application crash detected", true),
        new("ANR", "logcat ANR", true, null, TimeSpan.Zero, "Critical", "", "Application not responding", true),
        new("LowFps", "FPS below", true, 30, TimeSpan.FromSeconds(5), "Warning", "FPS", "Frame rate stayed below threshold"),
        new("FrameTime", "P95 frame time above", true, 33.3, TimeSpan.FromSeconds(5), "Warning", "ms", "Frame time stayed above threshold"),
        new("Jank", "Jank above", true, 20, TimeSpan.FromSeconds(5), "Warning", "%", "Jank stayed above threshold"),
        new("HighCpu", "CPU above", true, 85, TimeSpan.FromSeconds(10), "Warning", "%", "Device CPU stayed above threshold"),
        new("HighMemory", "Memory used above", true, 90, TimeSpan.FromSeconds(10), "Warning", "%", "Device memory use stayed high"),
        new("RapidMemoryGrowth", "Selected-app growth above", true, 100, TimeSpan.FromSeconds(30), "Warning", "MB", "Selected-app memory grew rapidly"),
        new("LowMemory", "Available RAM below", true, 10, TimeSpan.FromSeconds(10), "Warning", "%", "Available device RAM stayed low"),
        new("LowStorage", "Storage free below", true, 1, TimeSpan.Zero, "Warning", "GB", "Available device storage is low"),
        new("DiskWriteSpike", "Disk writes above", true, 50, TimeSpan.FromSeconds(2), "Warning", "MB/s", "Selected-app write activity spiked"),
        new("NetworkLost", "Network connection", true, null, TimeSpan.Zero, "Critical", "", "Active network connection was lost"),
        new("NetworkStopped", "Network traffic below", false, 0.01, TimeSpan.FromSeconds(15), "Warning", "KB/s", "Traffic unexpectedly stopped"),
        new("NetworkSpike", "Network traffic above", true, 25, TimeSpan.FromSeconds(2), "Warning", "MB/s", "Device network traffic spiked"),
        new("HighTemperature", "Battery temperature above", true, 45, TimeSpan.Zero, "Critical", "°C", "Battery temperature is too high", true),
        new("ThermalSeverity", "Thermal severity", true, null, TimeSpan.Zero, "Critical", "", "Thermal severity became severe or critical", true),
        new("ThermalThrottling", "Thermal throttling", true, null, TimeSpan.Zero, "Warning", "", "Android reports thermal throttling"),
        new("RecordingFailed", "Screen recording", true, null, TimeSpan.Zero, "Critical", "", "Screen recording failed", true),
        new("LogcatStopped", "logcat health", true, null, TimeSpan.FromSeconds(10), "Warning", "", "logcat collection stopped"),
        new("StaleData", "Collector data age above", true, 10, TimeSpan.Zero, "Warning", "s", "Collector data became stale", true)
    ];

    private sealed record RuleSnapshot(
        string Type,
        bool Enabled,
        double? Threshold,
        double DurationSeconds,
        string Severity);
}

public sealed record NetworkInterfaceRow(
    string Name,
    string State,
    string Addresses,
    long? ReceivedBytes,
    long? SentBytes);

public sealed record NetworkSocketRow(
    string Protocol,
    string State,
    string LocalAddress,
    string LocalPort,
    string RemoteAddress,
    string RemotePort,
    string Uid,
    string PackageName);
