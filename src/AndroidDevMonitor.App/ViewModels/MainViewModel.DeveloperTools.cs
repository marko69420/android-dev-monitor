using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using AndroidDevMonitor.Adb.Parsers;
using AndroidDevMonitor.Core.Analysis;
using AndroidDevMonitor.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AndroidDevMonitor.App.ViewModels;

public sealed record WirelessServiceRow(string Name, string Type, string Address, string Purpose);

public sealed record DeveloperToolRow(string Name, string Category, string Access, string Description);

public partial class MainViewModel
{
    [ObservableProperty] private string _wirelessStatus = "Refresh to discover secure ADB devices on the local network.";
    [ObservableProperty] private string _mdnsBackend = "Not checked";
    [ObservableProperty] private string _wirelessHost = "";
    [ObservableProperty] private string _pairingCode = "";
    [ObservableProperty] private WirelessServiceRow? _selectedWirelessService;
    [ObservableProperty] private string _connectionHealth = "No wireless connection measured";
    [ObservableProperty] private int _traceDurationSeconds = 10;
    [ObservableProperty] private string _developerLabStatus = "Choose a tool. Results are saved locally and never uploaded.";
    [ObservableProperty] private string _developerLabOutput = "";
    [ObservableProperty] private DeveloperToolRow? _selectedDeveloperTool;
    [ObservableProperty] private int _monkeyEventCount = 500;
    [ObservableProperty] private int _monkeySeed = 42;
    [ObservableProperty] private bool _developerLabIsRunning;
    [ObservableProperty] private SessionSummary? _comparisonSession;
    [ObservableProperty] private string _sessionComparison = "Choose a second session to compare against the selected session.";
    [ObservableProperty] private string? _comparisonApkPath;
    [ObservableProperty] private string? _bugReportPath;
    private CancellationTokenSource? _developerLabCts;

    public ObservableCollection<WirelessServiceRow> WirelessServices { get; } = [];
    public ObservableCollection<DeveloperToolRow> DeveloperTools { get; } = new(
    [
        new("Perfetto system trace", "Performance", "ADB · Android 10+", "CPU scheduling, frames, Binder, memory and frequency trace"),
        new("CPU sampling report", "Performance", "ADB · simpleperf", "Per-process CPU counters and hottest threads for the selected app"),
        new("Frame and jank report", "Performance", "ADB", "Frame timing, jank histogram, refresh rate and compositor state"),
        new("GPU and graphics report", "Graphics", "ADB · vendor dependent", "Renderer API, GPU properties, frequencies and SurfaceFlinger diagnostics"),
        new("Startup benchmark", "Performance", "ADB", "Cold-start package ten times and report median/P90"),
        new("Memory report", "Memory", "ADB", "Detailed meminfo, process importance, zRAM and LMK state"),
        new("Package inspector", "Application", "ADB", "Manifest, components, permissions, AppOps and package state"),
        new("Permission state report", "Application", "ADB", "Runtime grants, AppOps, notification and battery-optimization state"),
        new("Background work report", "Application", "ADB", "Services, jobs, alarms, wake locks, standby and restrictions"),
        new("Storage report", "Application", "ADB", "Package disk usage, volumes, quotas and device free space"),
        new("APK analyzer", "Build", "Local Android SDK", "Manifest, permissions, files, resources, native ABIs and signing data"),
        new("APK build comparison", "Build", "Local files", "Compare download size and DEX, native, resources, assets and metadata between two APKs"),
        new("Network and ports report", "Connectivity", "ADB", "Interfaces, DNS, routes, sockets and ADB forward/reverse mappings"),
        new("Logcat diagnostic bundle", "Diagnostics", "ADB", "Main, system, crash and events buffers"),
        new("Crash and ANR scan", "Diagnostics", "ADB", "Extract fatal exceptions, ANRs, watchdog and low-memory events"),
        new("Full bug report", "Diagnostics", "ADB", "Create Android bugreport ZIP for offline analysis"),
        new("Analyze bug report file", "Diagnostics", "Local ZIP/TXT", "Summarize ANRs, crashes, tombstones, watchdog, LMK, StrictMode and thermal signals"),
        new("Instrumentation inventory", "Testing", "ADB", "List installed test runners and package instrumentation targets"),
        new("Monkey stress test", "Testing", "ADB · changes device", "Repeatable package-scoped input stress test"),
        new("Emulator report", "Emulator", "ADB · emulator", "AVD identity, snapshots, display, battery, network and sensor state"),
        new("Connection benchmark", "Connectivity", "ADB", "Measure twenty shell round trips and report latency"),
        new("Security and build report", "Compatibility", "ADB", "Patch level, verified boot, SELinux, encryption and build identity"),
        new("Capability report", "Compatibility", "ADB", "Record API, features, profileability and available commands")
    ]);

    public string DeveloperLabDirectory => Path.Combine(DataDirectory, "DeveloperLab");

    partial void OnSelectedDeveloperToolChanged(DeveloperToolRow? value)
    {
        if (!DeveloperLabIsRunning && value is not null)
            DeveloperLabStatus = $"{value.Name} ready · results stay on this PC.";
    }

    [RelayCommand]
    private async Task RefreshWirelessAsync()
    {
        WirelessServices.Clear();
        if (IsDemo)
        {
            MdnsBackend = "libadbmdns · DEMO DATA";
            WirelessServices.Add(new("adb-Pixel-Demo", "_adb-tls-connect._tcp", "192.168.1.42:37123", "Secure paired device"));
            WirelessStatus = "1 secure device discovered · DEMO DATA";
            return;
        }

        if (_adb.ResolvedAdbPath is null)
        {
            WirelessStatus = "ADB was not found. Configure adb.exe in Settings.";
            return;
        }

        AdbCommandResult version = await _adb.ExecuteAsync(null, ["version"], TimeSpan.FromSeconds(5), CancellationToken.None);
        AdbCommandResult check = await _adb.ExecuteAsync(null, ["mdns", "check"], TimeSpan.FromSeconds(8), CancellationToken.None);
        AdbCommandResult services = await _adb.ExecuteAsync(null, ["mdns", "services"], TimeSpan.FromSeconds(10), CancellationToken.None);
        AdbVersionInfo versionInfo = WirelessAdbParser.ParseVersion(version.StandardOutput);
        string adbVersion = versionInfo.PlatformToolsVersion?.ToString() ?? "unknown";
        MdnsBackend = check.Success ? check.StandardOutput.Trim() : Describe(check);

        foreach (AdbMdnsService service in WirelessAdbParser.ParseServices(services.StandardOutput))
        {
            WirelessServices.Add(new(service.Name, service.Type, service.Address,
                service.CanPair ? "Ready to pair" : service.Type.Contains("tls-connect") ? "Secure paired device" : "Legacy TCP device"));
        }

        string capability = versionInfo.SupportsWifi2 ? "Wi-Fi 2.0 ready" : versionInfo.SupportsSecureWireless ? "secure Wi-Fi ready · update to Platform Tools 37+ for Wi-Fi 2.0" : "Platform Tools update required";
        WirelessStatus = $"Platform Tools {adbVersion} · {WirelessServices.Count} mDNS service(s) · {capability}";
    }

    partial void OnSelectedWirelessServiceChanged(WirelessServiceRow? value)
    {
        if (value is not null) WirelessHost = value.Address;
    }

    [RelayCommand]
    private async Task PairWirelessAsync()
    {
        if (!ValidateWirelessHost(out string host) || !Regex.IsMatch(PairingCode, @"^\d{6}$"))
        {
            WirelessStatus = "Enter the device pairing address and six-digit pairing code.";
            return;
        }
        AdbCommandResult result = await _adb.ExecuteAsync(null, ["pair", host, PairingCode], TimeSpan.FromSeconds(20), CancellationToken.None);
        PairingCode = "";
        await RefreshWirelessAsync();
        WirelessStatus = result.Success ? "Paired securely. Select the discovered secure device and connect." : "Pairing failed: " + CleanError(result);
    }

    [RelayCommand]
    private async Task ConnectWirelessAsync()
    {
        if (!ValidateWirelessHost(out string host)) { WirelessStatus = "Select a service or enter host:port."; return; }
        Stopwatch clock = Stopwatch.StartNew();
        AdbCommandResult result = await _adb.ExecuteAsync(null, ["connect", host], TimeSpan.FromSeconds(20), CancellationToken.None);
        clock.Stop();
        ConnectionHealth = result.Success ? $"Connected · {clock.ElapsedMilliseconds} ms handshake" : "Connection failed";
        WirelessStatus = result.Success ? result.StandardOutput.Trim() : CleanError(result);
        await RefreshDevicesAsync();
    }

    [RelayCommand]
    private async Task DisconnectWirelessAsync()
    {
        if (!ValidateWirelessHost(out string host)) { WirelessStatus = "Select a connected service or enter host:port."; return; }
        AdbCommandResult result = await _adb.ExecuteAsync(null, ["disconnect", host], TimeSpan.FromSeconds(10), CancellationToken.None);
        WirelessStatus = result.Success ? result.StandardOutput.Trim() : CleanError(result);
        ConnectionHealth = "Disconnected";
        await RefreshDevicesAsync();
    }

    [RelayCommand]
    private async Task MeasureWirelessLatencyAsync()
    {
        AndroidDevice? target = SelectedDevice;
        if (target is null) { ConnectionHealth = "Select a connected Android device."; return; }
        List<double> timings = [];
        for (int i = 0; i < 10; i++)
        {
            Stopwatch clock = Stopwatch.StartNew();
            AdbCommandResult result = await _adb.ExecuteAsync(target.Serial, ["shell", "echo", "ok"], TimeSpan.FromSeconds(5), CancellationToken.None);
            clock.Stop();
            if (result.Success) timings.Add(clock.Elapsed.TotalMilliseconds);
        }
        ConnectionHealth = timings.Count == 0 ? "Device did not answer" : $"{timings.Count}/10 replies · median {Percentile(timings, 0.5):0} ms · P90 {Percentile(timings, 0.9):0} ms";
    }

    [RelayCommand]
    private async Task RunDeveloperToolAsync()
    {
        if (DeveloperLabIsRunning) return;
        DeveloperToolRow? tool = SelectedDeveloperTool;
        AndroidDevice? target = SelectedDevice;
        if (tool is null) { DeveloperLabStatus = "Choose a tool first."; return; }
        bool requiresDevice = tool.Name is not ("APK analyzer" or "APK build comparison" or "Analyze bug report file");
        if (requiresDevice && target is null) { DeveloperLabStatus = "Select a connected Android device."; return; }
        Directory.CreateDirectory(DeveloperLabDirectory);
        _developerLabCts = new CancellationTokenSource();
        DeveloperLabIsRunning = true;
        DeveloperLabStatus = requiresDevice ? $"Running {tool.Name} on {target!.FriendlyName}…" : $"Running {tool.Name}…";
        try
        {
            string output = tool.Name switch
            {
                "APK analyzer" => await AnalyzeApkAsync(),
                "APK build comparison" => await CompareApksAsync(),
                "Analyze bug report file" => await AnalyzeBugReportFileAsync(),
                "Perfetto system trace" => await CapturePerfettoAsync(target!),
                "CPU sampling report" => await RunShellReportAsync(target!, "cpu", ["package=" + ShellPackage() + "; pid=$(pidof $package | cut -d' ' -f1); echo PACKAGE=$package PID=$pid; echo __SIMPLEPERF__; if [ -n \"$pid\" ]; then simpleperf stat -p $pid --duration 10 2>&1; fi; echo __THREADS__; top -b -n 1 -H -p $pid 2>/dev/null; echo __PROC_STAT__; cat /proc/$pid/stat 2>/dev/null"]),
                "Frame and jank report" => await RunShellReportAsync(target!, "frames", ["dumpsys gfxinfo " + ShellPackage() + " framestats; echo __DISPLAY__; dumpsys display; echo __SURFACEFLINGER__; dumpsys SurfaceFlinger --list 2>/dev/null"]),
                "GPU and graphics report" => await RunShellReportAsync(target!, "graphics", ["getprop ro.hardware.egl; getprop ro.opengles.version; getprop ro.hardware.vulkan; getprop ro.gfx.driver.0; echo __GPU_PROPERTIES__; getprop | grep -iE 'gpu|egl|vulkan|angle'; echo __GPU_SYSFS__; for f in /sys/class/kgsl/kgsl-3d0/gpuclk /sys/class/kgsl/kgsl-3d0/devfreq/cur_freq /sys/class/devfreq/*gpu*/cur_freq; do [ -r $f ] && echo $f=$(cat $f); done; echo __SURFACEFLINGER__; dumpsys SurfaceFlinger 2>/dev/null"]),
                "Startup benchmark" => await RunStartupBenchmarkAsync(target!),
                "Memory report" => await RunShellReportAsync(target!, "memory", ["dumpsys meminfo -a " + ShellPackage() + "; echo __PROCRANK__; procrank 2>/dev/null; echo __MEMINFO__; cat /proc/meminfo; echo __LMK__; dumpsys activity lmk 2>/dev/null"]),
                "Package inspector" => await RunShellReportAsync(target!, "package", ["dumpsys package " + ShellPackage() + "; echo __APPOPS__; appops get " + ShellPackage() + "; echo __ACTIVITY__; dumpsys activity package " + ShellPackage()]),
                "Permission state report" => await RunShellReportAsync(target!, "permissions", ["dumpsys package " + ShellPackage() + " | grep -A 120 -E 'requested permissions:|install permissions:|runtime permissions:'; echo __APPOPS__; appops get " + ShellPackage() + "; echo __NOTIFICATIONS__; dumpsys notification --noredact | grep -i -A 8 " + ShellPackage() + "; echo __IDLE__; dumpsys deviceidle whitelist | grep " + ShellPackage()]),
                "Background work report" => await RunShellReportAsync(target!, "background", ["dumpsys activity services " + ShellPackage() + "; echo __JOBS__; dumpsys jobscheduler " + ShellPackage() + "; echo __ALARMS__; dumpsys alarm; echo __POWER__; dumpsys power; echo __STANDBY__; am get-standby-bucket " + ShellPackage()]),
                "Storage report" => await RunShellReportAsync(target!, "storage", ["dumpsys diskstats; echo __PACKAGE__; dumpsys package " + ShellPackage() + " | grep -E 'codePath=|dataDir=|primaryCpuAbi=|secondaryCpuAbi='; echo __VOLUMES__; df -h; echo __QUOTA__; dumpsys storaged 2>/dev/null"]),
                "Network and ports report" => await RunNetworkReportAsync(target!),
                "Logcat diagnostic bundle" => await RunAdbReportAsync(target!, "logcat", ["logcat", "-d", "-b", "main,system,crash,events,radio", "-v", "threadtime"]),
                "Crash and ANR scan" => await RunShellReportAsync(target!, "crash-anr", ["logcat -d -b crash -b system -b main -v threadtime | grep -iE -B 5 -A 40 'FATAL EXCEPTION|ANR in|am_anr|Watchdog|lowmemorykiller|lmkd|has died|tombstone'"]),
                "Full bug report" => await CaptureBugReportAsync(target!),
                "Instrumentation inventory" => await RunShellReportAsync(target!, "instrumentation", ["pm list instrumentation -f; echo __SELECTED_PACKAGE__; dumpsys package " + ShellPackage() + " | grep -i -A 12 instrumentation"]),
                "Monkey stress test" => await RunMonkeyAsync(target!),
                "Emulator report" => await RunEmulatorReportAsync(target!),
                "Connection benchmark" => await RunConnectionBenchmarkAsync(target!),
                "Security and build report" => await RunShellReportAsync(target!, "security", ["getprop ro.build.fingerprint; getprop ro.build.version.security_patch; getprop ro.boot.verifiedbootstate; getprop ro.boot.vbmeta.device_state; getprop ro.crypto.state; getprop ro.crypto.type; echo __SELINUX__; getenforce; echo __LOCK_SETTINGS__; dumpsys lock_settings 2>/dev/null"]),
                "Capability report" => await RunShellReportAsync(target!, "capabilities", ["getprop; echo __FEATURES__; pm list features; echo __ADB_FEATURES__; echo $(getprop ro.build.version.sdk); echo __COMMANDS__; command -v perfetto simpleperf bugreport procrank showmap"]),
                _ => "Unsupported tool"
            };
            DeveloperLabOutput = output;
            DeveloperLabStatus = $"{tool.Name} completed.";
        }
        catch (OperationCanceledException)
        {
            DeveloperLabStatus = $"{tool.Name} cancelled.";
        }
        catch (Exception ex)
        {
            DeveloperLabOutput = ex.Message;
            DeveloperLabStatus = $"{tool.Name} failed.";
        }
        finally
        {
            DeveloperLabIsRunning = false;
            _developerLabCts.Dispose();
            _developerLabCts = null;
        }
    }

    [RelayCommand]
    private void CancelDeveloperTool()
    {
        _developerLabCts?.Cancel();
        DeveloperLabStatus = "Cancelling…";
    }

    [RelayCommand]
    private void CompareSessions()
    {
        SessionSummary? current = SelectedStoredSession;
        SessionSummary? baseline = ComparisonSession;
        if (current is null || baseline is null)
        {
            SessionComparison = "Select a session in the table and a baseline in the comparison list.";
            return;
        }
        if (current.Id == baseline.Id)
        {
            SessionComparison = "Choose two different sessions.";
            return;
        }

        SessionComparison =
            $"Selected: {current.StartedUtc.ToLocalTime():g}  ·  Baseline: {baseline.StartedUtc.ToLocalTime():g}\n" +
            $"Peak CPU: {Delta(current.PeakCpuPercent, baseline.PeakCpuPercent, "%")}  ·  " +
            $"Peak memory: {DeltaBytes(current.PeakMemoryBytes, baseline.PeakMemoryBytes)}  ·  " +
            $"Average FPS: {Delta(current.AverageFps, baseline.AverageFps, " fps")}  ·  " +
            $"P95 frame: {Delta(current.FrameTimeP95Ms, baseline.FrameTimeP95Ms, " ms")}  ·  " +
            $"Alerts: {current.AlertCount - baseline.AlertCount:+#;-#;0}";
    }

    [RelayCommand]
    private void OpenDeveloperLabDirectory()
    {
        Directory.CreateDirectory(DeveloperLabDirectory);
        Process.Start(new ProcessStartInfo(DeveloperLabDirectory) { UseShellExecute = true });
    }

    [RelayCommand]
    private void SelectComparisonApk()
    {
        OpenFileDialog picker = new() { Title = "Select baseline APK", Filter = "Android package (*.apk)|*.apk", CheckFileExists = true };
        if (picker.ShowDialog() == true) ComparisonApkPath = picker.FileName;
    }

    [RelayCommand]
    private void SelectBugReport()
    {
        OpenFileDialog picker = new() { Title = "Select Android bug report", Filter = "Android bug report (*.zip;*.txt)|*.zip;*.txt|All files (*.*)|*.*", CheckFileExists = true };
        if (picker.ShowDialog() == true) BugReportPath = picker.FileName;
    }

    private async Task<string> CapturePerfettoAsync(AndroidDevice target)
    {
        int seconds = Math.Clamp(TraceDurationSeconds, 5, 120);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string remote = $"/data/misc/perfetto-traces/adm-{stamp}.perfetto-trace";
        string local = Path.Combine(DeveloperLabDirectory, $"perfetto-{SafeName(target.Serial)}-{stamp}.perfetto-trace");
        AdbCommandResult capture = await _adb.ExecuteAsync(target.Serial, ["shell", "perfetto", "-o", remote, "-t", $"{seconds}s", "sched", "freq", "idle", "am", "wm", "gfx", "view", "binder_driver", "hal", "dalvik"], TimeSpan.FromSeconds(seconds + 20), LabToken);
        LabToken.ThrowIfCancellationRequested();
        if (!capture.Success) return "Perfetto unavailable: " + CleanError(capture);
        AdbCommandResult pull = await _adb.ExecuteAsync(target.Serial, ["pull", remote, local], TimeSpan.FromSeconds(60), LabToken);
        LabToken.ThrowIfCancellationRequested();
        _ = await _adb.ExecuteAsync(target.Serial, ["shell", "rm", remote], TimeSpan.FromSeconds(5), LabToken);
        return pull.Success ? local : "Trace captured but pull failed: " + CleanError(pull);
    }

    private async Task<string> RunStartupBenchmarkAsync(AndroidDevice target)
    {
        string package = ShellPackage();
        AdbCommandResult resolve = await _adb.ExecuteAsync(target.Serial, ["shell", "cmd", "package", "resolve-activity", "--brief", package], TimeSpan.FromSeconds(10), LabToken);
        string component = resolve.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        if (!resolve.Success || !component.Contains('/')) return "No launchable activity found for " + package;
        List<double> totals = [];
        StringBuilder details = new();
        for (int i = 1; i <= 10; i++)
        {
            LabToken.ThrowIfCancellationRequested();
            _ = await _adb.ExecuteAsync(target.Serial, ["shell", "am", "force-stop", package], TimeSpan.FromSeconds(5), LabToken);
            AdbCommandResult run = await _adb.ExecuteAsync(target.Serial, ["shell", "am", "start", "-W", "-n", component], TimeSpan.FromSeconds(20), LabToken);
            Match match = Regex.Match(run.StandardOutput, @"TotalTime:\s*(?<ms>\d+)");
            if (match.Success && double.TryParse(match.Groups["ms"].Value, out double ms)) totals.Add(ms);
            details.AppendLine($"Run {i}: {(match.Success ? match.Groups["ms"].Value + " ms" : CleanError(run))}");
        }
        if (totals.Count == 0) return details.ToString();
        details.Insert(0, $"Cold start · {package}\nMedian {Percentile(totals, .5):0} ms · P90 {Percentile(totals, .9):0} ms · {totals.Count}/10 valid\n\n");
        return await SaveTextAsync(target, "startup", details.ToString());
    }

    private async Task<string> RunShellReportAsync(AndroidDevice target, string name, IReadOnlyList<string> scripts) =>
        await RunAdbReportAsync(target, name, ["shell", "sh", "-c", string.Join("; ", scripts)]);

    private async Task<string> RunAdbReportAsync(AndroidDevice target, string name, IReadOnlyList<string> args)
    {
        AdbCommandResult result = await _adb.ExecuteAsync(target.Serial, args, TimeSpan.FromMinutes(2), LabToken);
        LabToken.ThrowIfCancellationRequested();
        string text = result.StandardOutput + (string.IsNullOrWhiteSpace(result.StandardError) ? "" : "\n\nSTDERR\n" + result.StandardError);
        return await SaveTextAsync(target, name, text);
    }

    private async Task<string> CaptureBugReportAsync(AndroidDevice target)
    {
        string path = Path.Combine(DeveloperLabDirectory, $"bugreport-{SafeName(target.Serial)}-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        AdbCommandResult result = await _adb.ExecuteAsync(target.Serial, ["bugreport", path], TimeSpan.FromMinutes(8), LabToken);
        LabToken.ThrowIfCancellationRequested();
        if (!result.Success) return CleanError(result);
        BugReportAnalysis analysis = await BugReportAnalyzer.AnalyzeAsync(path, LabToken);
        string reportPath = Path.ChangeExtension(path, ".analysis.txt");
        await File.WriteAllTextAsync(reportPath, analysis.ToReport(), Encoding.UTF8, LabToken);
        return $"Bug report: {path}\nAnalysis: {reportPath}\n\n{analysis.ToReport()}";
    }

    private async Task<string> RunMonkeyAsync(AndroidDevice target)
    {
        string package = ShellPackage();
        int events = Math.Clamp(MonkeyEventCount, 1, 100000);
        int seed = Math.Max(0, MonkeySeed);
        return await RunAdbReportAsync(target, "monkey", ["shell", "monkey", "-p", package, "-s", seed.ToString(CultureInfo.InvariantCulture), "--throttle", "100", "--monitor-native-crashes", "-v", events.ToString(CultureInfo.InvariantCulture)]);
    }

    private async Task<string> RunConnectionBenchmarkAsync(AndroidDevice target)
    {
        List<double> values = [];
        for (int i = 0; i < 20; i++)
        {
            LabToken.ThrowIfCancellationRequested();
            Stopwatch timer = Stopwatch.StartNew();
            AdbCommandResult result = await _adb.ExecuteAsync(target.Serial, ["shell", "echo", "ok"], TimeSpan.FromSeconds(5), LabToken);
            timer.Stop();
            if (result.Success) values.Add(timer.Elapsed.TotalMilliseconds);
        }
        string report = values.Count == 0 ? "No successful replies." : $"Replies {values.Count}/20\nMedian {Percentile(values, .5):0.0} ms\nP90 {Percentile(values, .9):0.0} ms\nP99 {Percentile(values, .99):0.0} ms\nMin {values.Min():0.0} ms\nMax {values.Max():0.0} ms";
        return await SaveTextAsync(target, "connection", report);
    }

    private async Task<string> RunNetworkReportAsync(AndroidDevice target)
    {
        string device = await RunShellReportAsync(target, "network", ["ip address; echo __ROUTES__; ip route; echo __DNS__; getprop | grep -i dns; echo __SOCKETS__; ss -tunap 2>/dev/null || netstat -tunap 2>/dev/null; echo __UID_STATS__; dumpsys netstats detail 2>/dev/null"]);
        AdbCommandResult forward = await _adb.ExecuteAsync(null, ["forward", "--list"], TimeSpan.FromSeconds(10), LabToken);
        AdbCommandResult reverse = await _adb.ExecuteAsync(target.Serial, ["reverse", "--list"], TimeSpan.FromSeconds(10), LabToken);
        LabToken.ThrowIfCancellationRequested();
        return device + $"\n\nADB FORWARD\n{forward.StandardOutput}\nADB REVERSE\n{reverse.StandardOutput}";
    }

    private async Task<string> RunEmulatorReportAsync(AndroidDevice target)
    {
        if (target.Kind != DeviceKind.Emulator) return "The selected target is a physical device. Select an emulator.";
        return await RunAdbReportAsync(target, "emulator", ["shell", "sh", "-c", "echo __IDENTITY__; getprop ro.kernel.qemu; getprop ro.boot.qemu.avd_name; getprop ro.product.model; echo __DISPLAY__; wm size; wm density; dumpsys display; echo __BATTERY__; dumpsys battery; echo __NETWORK__; dumpsys connectivity; echo __SENSORS__; dumpsys sensorservice"]);
    }

    private async Task<string> AnalyzeApkAsync()
    {
        string? apk = SelectedApkPath;
        if (string.IsNullOrWhiteSpace(apk) || !File.Exists(apk)) return "Choose an APK with Select APK first.";
        string? aapt = FindLatestBuildTool("aapt.exe");
        if (aapt is null) return "Android SDK Build Tools (aapt.exe) were not found.";
        ApkArchiveSnapshot snapshot = ApkArchiveAnalyzer.Analyze(apk);
        StringBuilder report = new(snapshot.ToReport() + $"\nAndroid SDK tool: {aapt}\n\n");
        foreach (string command in new[] { "badging", "permissions", "resources", "configurations" })
        {
            report.AppendLine($"__{command.ToUpperInvariant()}__");
            report.AppendLine(await RunHiddenProcessAsync(aapt, ["dump", command, apk]));
        }
        return await SaveStandaloneTextAsync("apk", report.ToString());
    }

    private async Task<string> CompareApksAsync()
    {
        string? current = SelectedApkPath;
        string? baseline = ComparisonApkPath;
        if (string.IsNullOrWhiteSpace(current) || !File.Exists(current) || string.IsNullOrWhiteSpace(baseline) || !File.Exists(baseline))
            return "Choose the current APK and baseline APK first.";
        string report = ApkArchiveAnalyzer.Compare(ApkArchiveAnalyzer.Analyze(current), ApkArchiveAnalyzer.Analyze(baseline));
        return await SaveStandaloneTextAsync("apk-comparison", report);
    }

    private async Task<string> AnalyzeBugReportFileAsync()
    {
        if (string.IsNullOrWhiteSpace(BugReportPath) || !File.Exists(BugReportPath)) return "Choose a bugreport ZIP or TXT first.";
        BugReportAnalysis analysis = await BugReportAnalyzer.AnalyzeAsync(BugReportPath, LabToken);
        return await SaveStandaloneTextAsync("bugreport-analysis", analysis.ToReport());
    }

    private static string? FindLatestBuildTool(string fileName)
    {
        string? sdk = Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT") ?? Environment.GetEnvironmentVariable("ANDROID_HOME");
        sdk ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk");
        string root = Path.Combine(sdk, "build-tools");
        return Directory.Exists(root) ? Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault() : null;
    }

    private async Task<string> RunHiddenProcessAsync(string executable, IReadOnlyList<string> arguments)
    {
        using Process process = new() { StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden } };
        foreach (string argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(LabToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(LabToken);
        try { await process.WaitForExitAsync(LabToken); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            throw;
        }
        return await stdout + (process.ExitCode == 0 ? "" : "\nSTDERR\n" + await stderr);
    }

    private async Task<string> SaveStandaloneTextAsync(string name, string text)
    {
        Directory.CreateDirectory(DeveloperLabDirectory);
        string path = Path.Combine(DeveloperLabDirectory, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        await File.WriteAllTextAsync(path, text, Encoding.UTF8, LabToken);
        return path + Environment.NewLine + Environment.NewLine + (text.Length > 12000 ? text[..12000] + "\n…output truncated in view; full report saved" : text);
    }

    private async Task<string> SaveTextAsync(AndroidDevice target, string name, string text)
    {
        Directory.CreateDirectory(DeveloperLabDirectory);
        string path = Path.Combine(DeveloperLabDirectory, $"{name}-{SafeName(target.Serial)}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        await File.WriteAllTextAsync(path, text, Encoding.UTF8);
        return path + Environment.NewLine + Environment.NewLine + (text.Length > 12000 ? text[..12000] + "\n…output truncated in view; full report saved" : text);
    }

    private string ShellPackage()
    {
        string package = SelectedPackage ?? "";
        if (!Regex.IsMatch(package, @"^[A-Za-z0-9_.]+$")) throw new InvalidOperationException("Select a valid application package first.");
        return package;
    }

    private bool ValidateWirelessHost(out string host)
    {
        host = WirelessHost.Trim();
        return WirelessAdbParser.IsValidEndpoint(host);
    }

    private static string CleanError(AdbCommandResult result) =>
        (string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError).Trim() is { Length: > 0 } text ? text : Describe(result);
    private static string SafeName(string value) => Regex.Replace(value, @"[^A-Za-z0-9_.-]", "_");
    private CancellationToken LabToken => _developerLabCts?.Token ?? CancellationToken.None;
    private static string Delta(double? value, double? baseline, string suffix) =>
        value.HasValue && baseline.HasValue ? $"{value.Value - baseline.Value:+0.0;-0.0;0.0}{suffix}" : "N/A";
    private static string DeltaBytes(long? value, long? baseline) =>
        value.HasValue && baseline.HasValue ? FormatSignedBytes(value.Value - baseline.Value) : "N/A";
    private static string FormatSignedBytes(long bytes)
    {
        string sign = bytes > 0 ? "+" : bytes < 0 ? "-" : "";
        double absolute = Math.Abs((double)bytes);
        return absolute >= 1024 * 1024 * 1024 ? $"{sign}{absolute / (1024 * 1024 * 1024):0.0} GB" :
            absolute >= 1024 * 1024 ? $"{sign}{absolute / (1024 * 1024):0.0} MB" :
            absolute >= 1024 ? $"{sign}{absolute / 1024:0.0} KB" : $"{sign}{absolute:0} B";
    }
    private static double Percentile(IEnumerable<double> source, double percentile)
    {
        double[] values = source.Order().ToArray();
        if (values.Length == 0) return 0;
        int index = (int)Math.Ceiling(percentile * values.Length) - 1;
        return values[Math.Clamp(index, 0, values.Length - 1)];
    }
}
