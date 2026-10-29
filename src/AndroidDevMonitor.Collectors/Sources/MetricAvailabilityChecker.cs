using System.Globalization;
using System.Text.RegularExpressions;
using AndroidDevMonitor.Adb.Parsers;
using AndroidDevMonitor.Core.Configuration;
using AndroidDevMonitor.Core.Models;
using AndroidDevMonitor.Core.Services;

namespace AndroidDevMonitor.Collectors.Sources;

/// <summary>
/// Runs every metric provider of <see cref="LiveMonitoringSource"/> once, with the same commands and parsers, so
/// "Available" means the monitor can show that metric, and explains each metric it cannot show.
/// </summary>
public sealed partial class MetricAvailabilityChecker(IAdbExecutor adb, IGpuMetricProvider gpu)
{
    public const string DeviceCpu = "CPU · device";
    public const string AppCpu = "CPU · app";
    public const string DeviceMemory = "Memory · device";
    public const string AppRss = "Memory · app RSS";
    public const string AppPss = "Memory · app PSS";
    public const string Gpu = "GPU";
    public const string AppDiskIo = "Disk I/O · app";
    public const string DeviceDiskIo = "Disk I/O · device";
    public const string Storage = "Storage · device";
    public const string DeviceNetwork = "Network · device";
    public const string AppNetwork = "Network · app";
    public const string AppFrames = "Frame rate · app";
    public const string Thermal = "Thermal status";
    public const string Battery = "Battery";

    /// <summary>Every metric in report order.</summary>
    public static IReadOnlyList<string> Metrics { get; } =
        [DeviceCpu, AppCpu, DeviceMemory, AppRss, AppPss, Gpu, AppDiskIo, DeviceDiskIo, Storage, DeviceNetwork, AppNetwork, AppFrames, Thermal, Battery];

    [GeneratedRegex(@"^[A-Za-z][\w]*(?:\.[\w]+)+$")]
    private static partial Regex PackageNameRegex();
    [GeneratedRegex(@"Permission denied|Permission Denial|Operation not permitted|SecurityException", RegexOptions.IgnoreCase)]
    private static partial Regex DeniedRegex();
    [GeneratedRegex(@"No such file or directory|not found|Can't find service", RegexOptions.IgnoreCase)]
    private static partial Regex MissingRegex();
    [GeneratedRegex(@"No process found", RegexOptions.IgnoreCase)]
    private static partial Regex NoProcessRegex();

    public async Task<MetricAvailabilityReport> CheckAsync(AndroidDevice device, string? packageName, CancellationToken cancellationToken)
    {
        DateTimeOffset checkedUtc = DateTimeOffset.UtcNow;
        string? package = packageName is not null && PackageNameRegex().IsMatch(packageName) ? packageName : null;
        async Task<AdbCommandResult> Shell(params string[] command)
        {
            AdbCommandResult result = await adb.ExecuteAsync(device.Serial, ["shell", .. command], MonitoringConstants.DefaultAdbTimeout, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }

        int? pid = null;
        string? appWaiting = "Select an app in the top bar to check app metrics.";
        if (package is not null)
        {
            pid = ParsePid((await Shell("pidof", package)).StandardOutput);
            appWaiting = pid is null ? $"{package} is not running. Start it and run the report again." : null;
        }
        string pidText = pid?.ToString(CultureInfo.InvariantCulture) ?? "<pid>";
        MetricCapability Waiting(string metric, string provider) => new(metric, Availability.Waiting, provider, appWaiting!);
        // The app can exit between pidof and reading its /proc files.
        (Availability, string) appGone = (Availability.Waiting, $"{package} stopped while the report ran. Start it and run the report again.");

        AdbCommandResult? status = appWaiting is null ? await Shell("cat", $"/proc/{pidText}/status") : null;
        MetricCapability rss = status is null ? Waiting(AppRss, "/proc/<pid>/status")
            : Classify(AppRss, $"/proc/{pidText}/status", status, output => AndroidParsers.ParseProcessRss(output) is not null, whenMissing: appGone);
        int? uid = status is null ? null : AndroidParsers.ParseProcessUid(status.StandardOutput);

        List<MetricCapability> metrics =
        [
            Classify(DeviceCpu, "/proc/stat", await Shell("head", "-n", "1", "/proc/stat"), output => AndroidParsers.ParseProcStat(output) is not null),
            appWaiting is null
                ? Classify(AppCpu, $"/proc/{pidText}/stat", await Shell("cat", $"/proc/{pidText}/stat"), output => AndroidParsers.ParseProcessStat(output) is not null, whenMissing: appGone)
                : Waiting(AppCpu, "/proc/<pid>/stat"),
            Classify(DeviceMemory, "/proc/meminfo", await Shell("cat", "/proc/meminfo"), output => AndroidParsers.ParseMemInfo(output) is not null),
            rss,
            appWaiting is null
                ? Classify(AppPss, $"dumpsys meminfo {package}", await Shell("dumpsys", "meminfo", package!), output => AndroidParsers.ParseDumpsysPss(output) is not null)
                : Waiting(AppPss, "dumpsys meminfo"),
            await CheckGpuAsync(device, package, cancellationToken),
            appWaiting is null
                ? Classify(AppDiskIo, $"/proc/{pidText}/io", await Shell("cat", $"/proc/{pidText}/io"), output => AndroidParsers.ParseIo(output) is not null,
                    whenDenied: $"Only the app itself or root may read /proc/{pidText}/io, so the monitor shows device-wide disk I/O instead.", whenMissing: appGone)
                : Waiting(AppDiskIo, "/proc/<pid>/io"),
            Classify(DeviceDiskIo, "dumpsys storaged", await Shell("dumpsys", "storaged", "--force", "--hours", "0.001"), output => StoragedParser.ParseLatest(output) is not null,
                whenUnreadable: (Availability.Unsupported, "storaged returned no per-UID I/O records.")),
            Classify(Storage, "df /data", await Shell("df", "-k", "/data"), output => AndroidParsers.ParseDf(output) is not null),
            Classify(DeviceNetwork, "/proc/net/dev", await Shell("cat", "/proc/net/dev"), output => AndroidParsers.ParseNetDevTotals(output) is not null,
                whenUnreadable: (Availability.Waiting, "Only loopback (lo) has counters. Device traffic appears once Wi-Fi, mobile data or Ethernet is up.")),
            uid is not null ? await CheckAppNetworkAsync(uid.Value, Shell)
                // Without a UID, app traffic cannot be found; say why the status file gave none.
                : rss.Availability != Availability.Available ? rss with { Metric = AppNetwork, Provider = "/proc/uid_stat/<uid>" }
                : new(AppNetwork, Availability.Error, "/proc/uid_stat/<uid>", $"The app's UID was not found in /proc/{pidText}/status.", status),
            appWaiting is null
                ? Classify(AppFrames, $"dumpsys gfxinfo {package} framestats", await Shell("dumpsys", "gfxinfo", package!, "framestats"), output => AndroidParsers.ParseFrameStats(output) is not null,
                    whenUnreadable: (Availability.Waiting, "No frames yet: use the app so it draws. Apps that draw with OpenGL or Vulkan directly, as most games do, report few or none here."))
                : Waiting(AppFrames, "dumpsys gfxinfo framestats"),
            Classify(Thermal, "dumpsys thermalservice", await Shell("dumpsys", "thermalservice"), output => AndroidParsers.ParseThermalSeverity(output) is not null,
                whenMissing: (Availability.Unsupported, "This Android build has no thermal service.")),
            Classify(Battery, "dumpsys battery", await Shell("dumpsys", "battery"), output => AndroidParsers.ParseBattery(output).ContainsKey("level"))
        ];
        return new(device, package, pid, checkedUtc, metrics);
    }

    /// <summary>
    /// Turns one provider command into a status: available when the monitor's parser reads the output, otherwise the
    /// reason, from the output (permission denied, missing file or service, app gone) or from the exit code.
    /// </summary>
    public static MetricCapability Classify(string metric, string provider, AdbCommandResult result, Func<string, bool> parses,
        string? whenDenied = null, (Availability Availability, string Reason)? whenMissing = null, (Availability Availability, string Reason)? whenUnreadable = null)
    {
        if (result.Success && parses(result.StandardOutput)) return new(metric, Availability.Available, provider, Probe: result);
        string output = result.StandardOutput + "\n" + result.StandardError;
        (Availability availability, string reason) = result switch
        {
            { TimedOut: true } => (Availability.Error, $"ADB did not answer within {MonitoringConstants.DefaultAdbTimeout.TotalSeconds:0} seconds."),
            _ when DeniedRegex().IsMatch(output) => (Availability.PermissionDenied, whenDenied ?? $"The ADB shell user may not read {provider}."),
            _ when MissingRegex().IsMatch(output) => whenMissing ?? (Availability.Unsupported, $"{provider} does not exist on this device."),
            _ when NoProcessRegex().IsMatch(output) => (Availability.Waiting, "The app is not running. Start it and run the report again."),
            { ExitCode: not 0 } => (Availability.Error, FirstLine(result.StandardError) ?? FirstLine(result.StandardOutput) ?? (result.ExitCode is int code ? $"Exit code {code}." : "The command did not run.")),
            _ => whenUnreadable ?? (Availability.Unsupported, "The device answered in a format the monitor cannot read.")
        };
        return new(metric, availability, provider, reason, result);
    }

    private async Task<MetricCapability> CheckGpuAsync(AndroidDevice device, string? package, CancellationToken cancellationToken)
    {
        MetricValue reading = await gpu.ReadAsync(device, package, cancellationToken);
        string reason = reading.Availability switch
        {
            Availability.Available => "",
            Availability.Unsupported => reading.Source + ". GPU stays N/A; CPU load is never shown in its place.",
            _ => reading.Source + "."
        };
        return new(Gpu, reading.Availability, reading.Availability == Availability.Available ? reading.Source : gpu.Name, reason);
    }

    private static async Task<MetricCapability> CheckAppNetworkAsync(int uid, Func<string[], Task<AdbCommandResult>> shell)
    {
        string provider = $"/proc/uid_stat/{uid}";
        MetricCapability network = Classify(AppNetwork, provider, await shell(["cat", $"{provider}/tcp_rcv", $"{provider}/tcp_snd"]), output => AndroidParsers.ParseUidNetworkCounters(output) is not null,
            whenMissing: (Availability.Unsupported, "This kernel has no /proc/uid_stat, as most current Android kernels don't, so per-app traffic stays N/A. Device totals are not affected."));
        // A kernel with uid_stat creates a UID's counters on its first TCP traffic.
        if (network.Availability == Availability.Unsupported && (await shell(["ls", "-d", "/proc/uid_stat"])).StandardOutput.Trim() == "/proc/uid_stat")
            network = network with { Availability = Availability.Waiting, Reason = $"UID {uid} has no traffic counters yet. They appear after the app's first TCP traffic." };
        return network;
    }

    private static int? ParsePid(string output) =>
        int.TryParse(output.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid) ? pid : null;

    private static string? FirstLine(string text) =>
        text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
}
