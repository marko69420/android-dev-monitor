using AndroidDevMonitor.Collectors.Sources;
using AndroidDevMonitor.Core.Models;
using AndroidDevMonitor.Core.Services;
using static AndroidDevMonitor.Collectors.Sources.MetricAvailabilityChecker;

namespace AndroidDevMonitor.Collectors.Tests;

public sealed class MetricAvailabilityTests
{
    private const string Package = "com.example.app";
    private static readonly AndroidDevice Phone = new("R5CT1234", "Galaxy S23", DeviceState.Connected, DeviceKind.Physical, "14", 34, "samsung", "SM-S911B");

    /// <summary>A user-build phone: the app's /proc/pid/io is private, the kernel has no uid_stat and the build has no thermal service.</summary>
    private static ScriptedAdb LockedDownPhone() => new()
    {
        ["pidof com.example.app"] = Ok("4242\n"),
        ["cat /proc/4242/status"] = Ok("Name:\tcom.example.app\nUid:\t10211\t10211\t10211\t10211\nVmRSS:\t  204800 kB\n"),
        ["head -n 1 /proc/stat"] = Ok("cpu  100 0 50 1000 5 0 0 0 0 0\n"),
        ["cat /proc/4242/stat"] = Ok("4242 (com.example.app) S 1 4242 0 0 -1 4194560 100 0 0 0 250 80 0 0 20 0 30 0 12345 1000 200\n"),
        ["cat /proc/meminfo"] = Ok("MemTotal:        8000000 kB\nMemFree:         1000000 kB\nMemAvailable:    3000000 kB\n"),
        ["dumpsys meminfo com.example.app"] = Ok("** MEMINFO in pid 4242 [com.example.app] **\n        TOTAL   187654   160000\n"),
        ["cat /proc/4242/io"] = Failed(1, "cat: /proc/4242/io: Permission denied\n"),
        ["dumpsys storaged --force --hours 0.001"] = Ok("1700000000,1700000005\ncom.example.app 1 2 3 4 5 6 7 8\n"),
        ["df -k /data"] = Ok("Filesystem      1K-blocks   Used Available Use% Mounted on\n/dev/block/dm-5   100000  40000     60000  40% /data\n"),
        ["cat /proc/net/dev"] = Ok("Inter-|   Receive\n face |bytes\n    lo: 50 1 0 0 0 0 0 0 50 1 0 0 0 0 0 0\n wlan0: 1000 10 0 0 0 0 0 0 2000 20 0 0 0 0 0 0\n"),
        ["cat /proc/uid_stat/10211/tcp_rcv /proc/uid_stat/10211/tcp_snd"] = Failed(1, "cat: /proc/uid_stat/10211/tcp_rcv: No such file or directory\ncat: /proc/uid_stat/10211/tcp_snd: No such file or directory\n"),
        ["ls -d /proc/uid_stat"] = Failed(1, "ls: /proc/uid_stat: No such file or directory\n"),
        ["dumpsys gfxinfo com.example.app framestats"] = Ok("Applications Graphics Acceleration Info:\n** Graphics info for pid 4242 [com.example.app] **\n---PROFILEDATA---\nFlags,IntendedVsync\n---PROFILEDATA---\n"),
        ["dumpsys thermalservice"] = Ok("Can't find service: thermalservice\n"),
        ["dumpsys battery"] = Ok("Current Battery Service state:\n  level: 80\n  temperature: 300\n")
    };

    [Fact]
    public async Task Locked_down_phone_explains_every_metric_it_cannot_show()
    {
        MetricAvailabilityReport report = await new MetricAvailabilityChecker(LockedDownPhone(), new UnsupportedGpuMetricProvider()).CheckAsync(Phone, Package, CancellationToken.None);

        Assert.Equal(Metrics, report.Metrics.Select(metric => metric.Metric));
        Assert.Equal(4242, report.ProcessId);
        foreach (string available in new[] { DeviceCpu, AppCpu, DeviceMemory, AppRss, AppPss, DeviceDiskIo, Storage, DeviceNetwork, Battery })
            Assert.Equal(Availability.Available, Find(report, available).Availability);

        MetricCapability io = Find(report, AppDiskIo);
        Assert.Equal(Availability.PermissionDenied, io.Availability);
        Assert.Equal("/proc/4242/io", io.Provider);
        Assert.Equal("Only the app itself or root may read /proc/4242/io, so the monitor shows device-wide disk I/O instead.", io.Reason);

        MetricCapability network = Find(report, AppNetwork);
        Assert.Equal(Availability.Unsupported, network.Availability);
        Assert.Contains("no /proc/uid_stat", network.Reason);

        Assert.Equal(Availability.Waiting, Find(report, AppFrames).Availability);
        Assert.Contains("OpenGL or Vulkan", Find(report, AppFrames).Reason);
        Assert.Equal("This Android build has no thermal service.", Find(report, Thermal).Reason);
        Assert.Equal(Availability.Unsupported, Find(report, Gpu).Availability);
        Assert.Contains("CPU load is never shown in its place", Find(report, Gpu).Reason);
    }

    [Fact]
    public async Task Report_puts_the_reason_under_each_unavailable_metric_and_keeps_raw_output()
    {
        MetricAvailabilityReport report = await new MetricAvailabilityChecker(LockedDownPhone(), new UnsupportedGpuMetricProvider()).CheckAsync(Phone, Package, CancellationToken.None);
        string[] lines = report.ToReport().Split(Environment.NewLine);

        Assert.Contains("Device: Galaxy S23 (R5CT1234) · Android 14 · API 34 · Physical", lines);
        Assert.Contains("App: com.example.app · PID 4242", lines);
        Assert.Contains("9 of 14 available · 1 waiting · 3 unsupported · 1 permission denied", lines);
        int row = Array.FindIndex(lines, line => line.StartsWith(AppDiskIo, StringComparison.Ordinal));
        Assert.Matches(@"^Disk I/O · app\s+Permission denied\s+/proc/4242/io$", lines[row]);
        // The reason is wrapped under the provider column.
        int provider = lines[row].IndexOf("/proc/4242/io", StringComparison.Ordinal);
        Assert.Equal(new string(' ', provider) + "Only the app itself or root may read /proc/4242/io, so the monitor shows device-wide disk", lines[row + 1]);
        Assert.Equal(new string(' ', provider) + "I/O instead.", lines[row + 2]);
        int battery = Array.FindIndex(lines, line => line.StartsWith(Battery, StringComparison.Ordinal));
        Assert.Equal(["", "RAW PROVIDER OUTPUT"], lines[(battery + 1)..(battery + 3)]);

        Assert.Contains("--- CPU · device · adb -s R5CT1234 shell head -n 1 /proc/stat · exit 0", lines);
        Assert.Contains("--- Disk I/O · app · adb -s R5CT1234 shell cat /proc/4242/io · exit 1", lines);
        Assert.Contains("stderr: cat: /proc/4242/io: Permission denied", lines);
    }

    [Fact]
    public async Task Without_a_selected_app_only_device_metrics_are_read()
    {
        ScriptedAdb adb = LockedDownPhone();
        MetricAvailabilityReport report = await new MetricAvailabilityChecker(adb, new UnsupportedGpuMetricProvider()).CheckAsync(Phone, null, CancellationToken.None);

        foreach (string app in new[] { AppCpu, AppRss, AppPss, AppDiskIo, AppNetwork, AppFrames })
        {
            MetricCapability metric = Find(report, app);
            Assert.Equal(Availability.Waiting, metric.Availability);
            Assert.Equal("Select an app in the top bar to check app metrics.", metric.Reason);
            Assert.Null(metric.Probe);
        }
        Assert.DoesNotContain(adb.Commands, command => command.Contains("pidof") || command.Contains("4242") || command.Contains(Package));
        Assert.Contains("App: none selected", report.ToReport());
    }

    [Fact]
    public async Task App_that_is_not_running_is_waiting_instead_of_unsupported()
    {
        ScriptedAdb adb = LockedDownPhone();
        adb["pidof com.example.app"] = Failed(1, "");
        MetricAvailabilityReport report = await new MetricAvailabilityChecker(adb, new UnsupportedGpuMetricProvider()).CheckAsync(Phone, Package, CancellationToken.None);

        Assert.Null(report.ProcessId);
        Assert.All(new[] { AppCpu, AppRss, AppPss, AppDiskIo, AppNetwork, AppFrames }, app =>
        {
            Assert.Equal(Availability.Waiting, Find(report, app).Availability);
            Assert.Equal("com.example.app is not running. Start it and run the report again.", Find(report, app).Reason);
        });
        Assert.Equal(Availability.Available, Find(report, DeviceCpu).Availability);
    }

    [Fact]
    public async Task App_that_exits_during_the_report_is_waiting()
    {
        ScriptedAdb adb = LockedDownPhone();
        adb["cat /proc/4242/stat"] = Failed(1, "cat: /proc/4242/stat: No such file or directory\n");
        MetricAvailabilityReport report = await new MetricAvailabilityChecker(adb, new UnsupportedGpuMetricProvider()).CheckAsync(Phone, Package, CancellationToken.None);

        Assert.Equal(Availability.Waiting, Find(report, AppCpu).Availability);
        Assert.Contains("stopped while the report ran", Find(report, AppCpu).Reason);
    }

    [Fact]
    public async Task App_network_gives_the_status_file_reason_when_the_uid_is_unknown()
    {
        ScriptedAdb adb = LockedDownPhone();
        adb["cat /proc/4242/status"] = Failed(1, "cat: /proc/4242/status: No such file or directory\n");
        MetricAvailabilityReport report = await new MetricAvailabilityChecker(adb, new UnsupportedGpuMetricProvider()).CheckAsync(Phone, Package, CancellationToken.None);

        MetricCapability network = Find(report, AppNetwork);
        Assert.Equal(Availability.Waiting, network.Availability);
        Assert.Equal("/proc/uid_stat/<uid>", network.Provider);
        Assert.Equal(Find(report, AppRss).Reason, network.Reason);
        Assert.DoesNotContain(adb.Commands, command => command.Contains("uid_stat"));
    }

    [Fact]
    public async Task Kernel_with_uid_stat_but_no_counters_for_the_app_is_waiting()
    {
        ScriptedAdb adb = LockedDownPhone();
        adb["ls -d /proc/uid_stat"] = Ok("/proc/uid_stat\n");
        MetricAvailabilityReport report = await new MetricAvailabilityChecker(adb, new UnsupportedGpuMetricProvider()).CheckAsync(Phone, Package, CancellationToken.None);

        Assert.Equal(Availability.Waiting, Find(report, AppNetwork).Availability);
        Assert.Contains("UID 10211 has no traffic counters yet", Find(report, AppNetwork).Reason);
    }

    [Fact]
    public void Classify_reads_errors_from_the_output_when_there_is_no_exit_code()
    {
        static bool Never(string _) => false;
        // adb on devices without the shell v2 protocol reports exit code 0 and puts stderr in stdout.
        Assert.Equal(Availability.PermissionDenied, Classify("I/O", "/proc/1/io", Ok("cat: /proc/1/io: Permission denied\n"), Never).Availability);
        Assert.Equal(Availability.Unsupported, Classify("Thermal", "dumpsys thermalservice", Ok("Can't find service: thermalservice\n"), Never).Availability);

        MetricCapability timedOut = Classify("CPU", "/proc/stat", new([], "R5CT1234", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, "", "", true, false), Never);
        Assert.Equal(Availability.Error, timedOut.Availability);
        Assert.StartsWith("ADB did not answer within", timedOut.Reason);

        MetricCapability failed = Classify("CPU", "/proc/stat", Failed(255, "error: device offline\n"), Never);
        Assert.Equal(Availability.Error, failed.Availability);
        Assert.Equal("error: device offline", failed.Reason);

        Assert.Equal("The device answered in a format the monitor cannot read.", Classify("CPU", "/proc/stat", Ok("garbage"), Never).Reason);
        Assert.Equal(Availability.Available, Classify("CPU", "/proc/stat", Ok("cpu 1 2 3 4"), _ => true).Availability);
    }

    [Fact]
    public async Task Demo_source_reports_every_metric_as_demo_data()
    {
        MetricAvailabilityReport report = await new DemoMonitoringSource().CheckMetricAvailabilityAsync(Phone, Package, CancellationToken.None);
        Assert.Equal(Metrics, report.Metrics.Select(metric => metric.Metric));
        Assert.All(report.Metrics, metric => Assert.Equal("Deterministic demo provider", metric.Provider));
    }

    private static MetricCapability Find(MetricAvailabilityReport report, string metric) => report.Metrics.Single(item => item.Metric == metric);
    private static AdbCommandResult Ok(string output) => new([], "R5CT1234", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, output, "", false, false);
    private static AdbCommandResult Failed(int exitCode, string error) => new([], "R5CT1234", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, exitCode, "", error, false, false);

    /// <summary>Answers <c>adb shell</c> commands from a table keyed by the command after "shell" and records what ran.</summary>
    private sealed class ScriptedAdb : Dictionary<string, AdbCommandResult>, IAdbExecutor
    {
        public List<string> Commands { get; } = [];
        public string? ResolvedAdbPath => "adb";
        public void ConfigurePath(string? configuredPath) { }

        public Task<AdbCommandResult> ExecuteAsync(string? serial, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            string command = string.Join(' ', arguments.Skip(1));
            Commands.Add(command);
            AdbCommandResult result = TryGetValue(command, out AdbCommandResult? answer) ? answer : Failed(1, $"/system/bin/sh: unexpected command: {command}");
            return Task.FromResult(result with { Command = ["-s", serial!, .. arguments] });
        }
    }
}
