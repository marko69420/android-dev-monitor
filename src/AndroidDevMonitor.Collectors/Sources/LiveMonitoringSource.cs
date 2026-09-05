using System.Globalization;
using System.Text.RegularExpressions;
using AndroidDevMonitor.Adb.Parsers;
using AndroidDevMonitor.Collectors.Calculations;
using AndroidDevMonitor.Core.Configuration;
using AndroidDevMonitor.Core.Models;
using AndroidDevMonitor.Core.Services;
using Microsoft.Extensions.Logging;

namespace AndroidDevMonitor.Collectors.Sources;

public sealed class LiveMonitoringSource(IAdbExecutor adb, IGpuMetricProvider gpu, ILogger<LiveMonitoringSource> logger) : IMonitoringSource
{
    private readonly SemaphoreSlim _processCpuGate = new(1, 1);
    private readonly Dictionary<string, ProcessCpuBaseline> _processCpuBaselines = [];

    public async IAsyncEnumerable<MetricSample> StreamMetricsAsync(AndroidDevice device, string? packageName, Guid sessionId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ProcCpuStat? oldCpu = null; ProcessCpuStat? oldProcessCpu = null; IoCounters? oldIo = null;
        (long Rx, long Tx)? oldNetwork = null; (long Rx, long Tx)? oldAppNetwork = null;
        var last = DateTimeOffset.UtcNow; var iteration = 0; int? pid = null; int? processUid = null;
        long lastDiskWindowEnd = 0;
        using var timer = new PeriodicTimer(MonitoringConstants.LightweightInterval);
        while (!cancellationToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            if (pid is null || iteration % 5 == 0) pid = await ResolvePid(device.Serial, packageName, cancellationToken);
            var cpuTask = Shell(device.Serial, "head", "-n", "1", "/proc/stat", cancellationToken);
            List<string> paths = ["/proc/meminfo", "/proc/net/dev"];
            if (pid is not null)
            {
                paths.Add($"/proc/{pid}/stat");
                paths.Add($"/proc/{pid}/status");
                paths.Add($"/proc/{pid}/io");
            }
            if (processUid is not null)
            {
                paths.Add($"/proc/uid_stat/{processUid}/tcp_rcv");
                paths.Add($"/proc/uid_stat/{processUid}/tcp_snd");
            }
            var lightweightTask = ShellResult(device.Serial, ["shell", "cat", .. paths], cancellationToken);

            await Task.WhenAll(cpuTask, lightweightTask);
            var cpu = AndroidParsers.ParseProcStat(cpuTask.Result);
            var lightweight = lightweightTask.Result.StandardOutput;
            var procCpu = pid is null ? null : AndroidParsers.ParseProcessStat(ExtractProcessStat(lightweight, pid.Value));
            var memory = AndroidParsers.ParseMemInfo(lightweight);
            var io = AndroidParsers.ParseIo(lightweight);
            var network = ParseNetwork(lightweight);
            processUid = AndroidParsers.ParseProcessUid(lightweight) ?? processUid;
            var appNetwork = processUid is null ? null : AndroidParsers.ParseUidNetworkCounters(lightweight);
            var rss = AndroidParsers.ParseProcessRss(lightweight);
            if (pid is not null && procCpu is null)
            {
                pid = null;
                processUid = null;
                oldAppNetwork = null;
            }
            var elapsed = now - last;

            double? deviceDiskRead = null, deviceDiskWrite = null;
            string? deviceDiskSource = null;
            if (io is null)
            {
                var diskResult = await ShellResult(device.Serial, ["shell", "dumpsys", "storaged", "--force", "--hours", "0.001"], cancellationToken);
                var window = diskResult.Success ? StoragedParser.ParseLatest(diskResult.StandardOutput) : null;
                deviceDiskSource = "Device disk I/O · all Android UIDs · storaged";
                if (lastDiskWindowEnd > 0 && window is not null && window.EndSeconds > lastDiskWindowEnd && window.EndSeconds - window.StartSeconds <= 30)
                {
                    var seconds = window.EndSeconds - window.StartSeconds;
                    deviceDiskRead = window.ReadBytes / seconds;
                    deviceDiskWrite = window.WriteBytes / seconds;
                    deviceDiskSource += $" · {seconds}s interval";
                }
                if (window is not null) lastDiskWindowEnd = window.EndSeconds;
            }

            var gpuReading = await gpu.ReadAsync(device, packageName, cancellationToken);
            long? pss = null; double? fps = null; double? p95 = null; double? jank = null; double? temperature = null;
            long? storageTotal = null; long? storageAvailable = null; int? batteryPercent = null; bool? charging = null; string? thermalSeverity = null;
            string? activeInterface = null; string? ipAddress = null; string? networkType = null;
            long? packageCodeBytes = null; long? packageDataBytes = null; long? packageCacheBytes = null;
            if (iteration++ % (int)(MonitoringConstants.HeavyInterval.TotalSeconds / MonitoringConstants.LightweightInterval.TotalSeconds) == 0)
            {
                if (!string.IsNullOrWhiteSpace(packageName))
                {
                    var detailed = await Shell(device.Serial, "dumpsys", "meminfo", packageName, cancellationToken);
                    pss = AndroidParsers.ParseDumpsysPss(detailed);
                    var frames = AndroidParsers.ParseFrameStats(await Shell(device.Serial, "dumpsys", "gfxinfo", packageName, "framestats", cancellationToken));
                    fps = frames?.Fps; p95 = frames?.P95Ms; jank = frames?.JankPercent;
                    if (Regex.IsMatch(packageName, @"^[A-Za-z][\w]*(?:\.[\w]+)+$"))
                    {
                        var sizes = ParsePackageSizes(await Shell(
                            device.Serial,
                            "sh",
                            "-c",
                            $"apk=$(pm path {packageName} 2>/dev/null | sed -n '1s/^package://p'); " +
                            $"code=$(du -sk \"$apk\" 2>/dev/null | awk '{{print $1}}'); " +
                            $"data=$(du -sk /data/user/0/{packageName} 2>/dev/null | awk '{{print $1}}'); " +
                            $"cache=$(du -sk /data/user/0/{packageName}/cache 2>/dev/null | awk '{{print $1}}'); " +
                            "echo ADM_CODE=$code; echo ADM_DATA=$data; echo ADM_CACHE=$cache",
                            cancellationToken));
                        packageCodeBytes = sizes.CodeBytes;
                        packageDataBytes = sizes.DataBytes;
                        packageCacheBytes = sizes.CacheBytes;
                    }
                }
                var battery = AndroidParsers.ParseBattery(await Shell(device.Serial, "dumpsys", "battery", cancellationToken));
                if (double.TryParse(battery.GetValueOrDefault("temperature"), CultureInfo.InvariantCulture, out var tenths)) temperature = tenths / 10d;
                if (int.TryParse(battery.GetValueOrDefault("level"), CultureInfo.InvariantCulture, out var level)) batteryPercent = level;
                charging = battery.GetValueOrDefault("AC powered") == "true" || battery.GetValueOrDefault("USB powered") == "true" || battery.GetValueOrDefault("Wireless powered") == "true" ||
                           battery.GetValueOrDefault("status") is "2" or "5";
                var storage = AndroidParsers.ParseDf(await Shell(device.Serial, "df", "-k", "/data", cancellationToken));
                storageTotal = storage?.TotalBytes; storageAvailable = storage?.AvailableBytes;
                thermalSeverity = AndroidParsers.ParseThermalSeverity(await Shell(device.Serial, "dumpsys", "thermalservice", cancellationToken));
                var networkContext = ParseNetworkContext(await Shell(device.Serial, "ip", "-o", "-4", "addr", "show", "scope", "global", cancellationToken));
                activeInterface = networkContext.Interface; ipAddress = networkContext.IpAddress; networkType = networkContext.Type;
            }

            yield return new(sessionId, device.Serial, packageName, now,
                oldCpu is not null && cpu is not null ? MetricCalculators.DeviceCpuPercent(oldCpu, cpu) : null,
                oldCpu is not null && cpu is not null && oldProcessCpu is not null && procCpu is not null ? MetricCalculators.CpuPercent(oldCpu, cpu, oldProcessCpu, procCpu) : null,
                memory is null ? null : (memory.TotalKb - memory.AvailableKb) * 1024,
                memory?.AvailableKb * 1024,
                rss,
                pss,
                oldIo is not null && io is not null ? MetricCalculators.Rate(oldIo.ReadBytes, io.ReadBytes, elapsed) : null,
                oldIo is not null && io is not null ? MetricCalculators.Rate(oldIo.WriteBytes, io.WriteBytes, elapsed) : null,
                oldNetwork is not null && network is not null ? MetricCalculators.Rate(oldNetwork.Value.Rx, network.Value.Rx, elapsed) : null,
                oldNetwork is not null && network is not null ? MetricCalculators.Rate(oldNetwork.Value.Tx, network.Value.Tx, elapsed) : null,
                oldAppNetwork is not null && appNetwork is not null ? MetricCalculators.Rate(oldAppNetwork.Value.Rx, appNetwork.Value.Rx, elapsed) : null,
                oldAppNetwork is not null && appNetwork is not null ? MetricCalculators.Rate(oldAppNetwork.Value.Tx, appNetwork.Value.Tx, elapsed) : null,
                Fps: fps, FrameTimeP95Ms: p95, JankPercent: jank, TemperatureCelsius: temperature, GpuPercent: gpuReading.Value, Source: "ADB /proc + dumpsys",
                DeviceStorageTotalBytes: storageTotal, DeviceStorageAvailableBytes: storageAvailable, BatteryPercent: batteryPercent, IsCharging: charging,
                ThermalSeverity: thermalSeverity, ActiveNetworkInterface: activeInterface, IpAddress: ipAddress, NetworkType: networkType,
                PackageCodeBytes: packageCodeBytes, PackageDataBytes: packageDataBytes, PackageCacheBytes: packageCacheBytes,
                GpuSource: gpuReading.Source, GpuAvailability: gpuReading.Availability,
                DeviceDiskReadBytesPerSecond: deviceDiskRead, DeviceDiskWriteBytesPerSecond: deviceDiskWrite, DeviceDiskSource: deviceDiskSource);

            oldCpu = cpu ?? oldCpu; oldProcessCpu = procCpu ?? oldProcessCpu; oldIo = io;
            oldNetwork = network ?? oldNetwork; oldAppNetwork = appNetwork ?? oldAppNetwork; last = now;
            if (!await timer.WaitForNextTickAsync(cancellationToken)) break;
        }
    }

    public async Task<IReadOnlyList<AndroidProcess>> GetProcessesAsync(AndroidDevice device, CancellationToken cancellationToken)
    {
        await _processCpuGate.WaitAsync(cancellationToken);
        try
        {
            IReadOnlyList<AndroidProcess> processes = [];
            foreach (var args in new IReadOnlyList<string>[] { ["shell", "ps", "-A", "-o", "USER,PID,PPID,S,RSS,NAME,ARGS"], ["shell", "ps", "-A", "-o", "USER,PID,PPID,NAME,ARGS"], ["shell", "ps", "-A"], ["shell", "ps", "-ef"] })
            {
                var result = await adb.ExecuteAsync(device.Serial, args, MonitoringConstants.DefaultAdbTimeout, cancellationToken);
                if (!result.Success) continue;
                processes = AndroidParsers.ParseProcesses(result.StandardOutput);
                if (processes.Count > 0) break;
            }
            if (processes.Count == 0) return [];

            var processStatsResult = await adb.ExecuteAsync(device.Serial, ["shell", "cat", "/proc/[0-9]*/stat"], MonitoringConstants.DefaultAdbTimeout, cancellationToken);
            var cpuResult = await adb.ExecuteAsync(device.Serial, ["shell", "head", "-n", "1", "/proc/stat"], MonitoringConstants.DefaultAdbTimeout, cancellationToken);
            var deviceCpu = AndroidParsers.ParseProcStat(cpuResult.StandardOutput);
            var processStats = AndroidParsers.ParseProcessStats(processStatsResult.StandardOutput);
            _processCpuBaselines.TryGetValue(device.Serial, out var previous);
            if (deviceCpu is not null) _processCpuBaselines[device.Serial] = new(deviceCpu, processStats);

            var measured = new List<AndroidProcess>(processes.Count + 1);
            foreach (var process in processes)
            {
                processStats.TryGetValue(process.Pid, out var currentStat);
                ProcessCpuStat? previousStat = null;
                previous?.Processes.TryGetValue(process.Pid, out previousStat);
                var cpu = previous is not null && deviceCpu is not null && previousStat is not null && currentStat is not null
                    ? MetricCalculators.CpuPercent(previous.Device, deviceCpu, previousStat, currentStat)
                    : null;
                measured.Add(process with { StartTicks = currentStat?.StartTicks, CpuPercent = cpu });
            }

            if (previous is not null && deviceCpu is not null)
            {
                var totalCpu = MetricCalculators.DeviceCpuPercent(previous.Device, deviceCpu);
                var attributedCpu = measured.Sum(process => process.CpuPercent ?? 0);
                if (totalCpu is not null)
                {
                    if (attributedCpu > totalCpu.Value && attributedCpu > 0)
                    {
                        var scale = totalCpu.Value / attributedCpu;
                        for (var index = 0; index < measured.Count; index++)
                        {
                            if (measured[index].CpuPercent is double cpu) measured[index] = measured[index] with { CpuPercent = cpu * scale };
                        }
                        attributedCpu = totalCpu.Value;
                    }
                    measured.Add(new(0, null, "kernel", "Kernel / unattributed CPU", "Kernel, interrupts, and unassigned sampling time", null, "System", ProcessGroup.System, CpuPercent: Math.Max(0, totalCpu.Value - attributedCpu)));
                }
            }
            return measured;
        }
        finally { _processCpuGate.Release(); }
    }

    public async Task<CapabilitySet> DetectCapabilitiesAsync(AndroidDevice device, CancellationToken cancellationToken)
    {
        async Task<bool> Can(params string[] command) => (await adb.ExecuteAsync(device.Serial, ["shell", .. command], MonitoringConstants.DefaultAdbTimeout, cancellationToken)).Success;
        var proc = await Can("cat", "/proc/stat"); var mem = await Can("cat", "/proc/meminfo"); var network = await Can("cat", "/proc/net/dev");
        var processIo = await Can("test", "-r", "/proc/1/io");
        var appNetwork = await Can("test", "-d", "/proc/uid_stat");
        var battery = await Can("dumpsys", "battery"); var thermal = await Can("dumpsys", "thermalservice"); var gpuSupported = await gpu.IsSupportedAsync(device, cancellationToken);
        return new(proc, mem, processIo, network, appNetwork, true, gpuSupported, thermal, battery,
            new Dictionary<string, string>
            {
                ["CPU"] = "/proc/stat",
                ["Memory"] = "/proc/meminfo",
                ["Process I/O"] = "/proc/<pid>/io",
                ["Network"] = "/proc/net/dev",
                ["Selected-app network"] = "/proc/uid_stat/<uid>",
                ["FPS"] = "dumpsys gfxinfo",
                ["GPU"] = gpu.Name,
                ["Thermal"] = "dumpsys thermalservice"
            });
    }

    private async Task<int?> ResolvePid(string serial, string? package, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(package) || !Regex.IsMatch(package, @"^[A-Za-z][\w]*(\.[\w]+)+$")) return null;
        var output = await Shell(serial, "pidof", package, token);
        return int.TryParse(output.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), out var pid) ? pid : null;
    }
    private async Task<string> Shell(string serial, string first, params object[] tail)
    {
        var token = tail.OfType<CancellationToken>().FirstOrDefault();
        var args = tail.Where(x => x is not CancellationToken).Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)!).ToArray();
        var result = await adb.ExecuteAsync(serial, ["shell", first, .. args], MonitoringConstants.DefaultAdbTimeout, token);
        if (!result.Success) logger.LogDebug("Metric source unavailable: {Error}", result.StandardError);
        return result.StandardOutput;
    }
    private Task<AdbCommandResult> ShellResult(string serial, IReadOnlyList<string> args, CancellationToken token) => adb.ExecuteAsync(serial, args, MonitoringConstants.DefaultAdbTimeout, token);
    private async Task<AdbCommandResult?> ShellResultNullable(string serial, IReadOnlyList<string> args, CancellationToken token) => await ShellResult(serial, args, token);
    private static string ExtractProcessStat(string output, int pid) => output.Replace("\r", "").Split('\n').FirstOrDefault(line => line.StartsWith($"{pid} (", StringComparison.Ordinal)) ?? "";
    private static (long? CodeBytes, long? DataBytes, long? CacheBytes) ParsePackageSizes(string output)
    {
        long? Read(string name)
        {
            Match match = Regex.Match(output, $@"^ADM_{name}=(?<kb>\d+)$", RegexOptions.Multiline);
            return match.Success && long.TryParse(match.Groups["kb"].Value, out long kb)
                ? kb * 1024
                : null;
        }
        return (Read("CODE"), Read("DATA"), Read("CACHE"));
    }
    private sealed record ProcessCpuBaseline(ProcCpuStat Device, IReadOnlyDictionary<int, ProcessCpuStat> Processes);
    private static (long Rx, long Tx)? ParseNetwork(string output)
    {
        long rx = 0, tx = 0; var found = false;
        foreach (var line in output.Split('\n').Where(l => l.Contains(':')))
        {
            var parts = Regex.Split(line[(line.IndexOf(':') + 1)..].Trim(), @"\s+");
            if (parts.Length >= 9 && long.TryParse(parts[0], out var r) && long.TryParse(parts[8], out var t)) { rx += r; tx += t; found = true; }
        }
        return found ? (rx, tx) : null;
    }

    private static (string? Interface, string? IpAddress, string? Type) ParseNetworkContext(string output)
    {
        var address = Regex.Match(output, @"^\d+:\s+(?<interface>\S+)\s+inet\s+(?<ip>\d+(?:\.\d+){3})/", RegexOptions.Multiline);
        var route = Regex.Match(output, @"^default\s+via\s+\S+\s+dev\s+(?<interface>\S+)", RegexOptions.Multiline);
        var name = address.Success ? address.Groups["interface"].Value : route.Success ? route.Groups["interface"].Value : null;
        var type = name switch
        {
            null => null,
            var value when value.StartsWith("wlan", StringComparison.OrdinalIgnoreCase) => "Wi-Fi",
            var value when value.StartsWith("rmnet", StringComparison.OrdinalIgnoreCase) || value.StartsWith("ccmni", StringComparison.OrdinalIgnoreCase) => "Cellular",
            var value when value.StartsWith("eth", StringComparison.OrdinalIgnoreCase) => "Ethernet",
            _ => "Unknown"
        };
        return (name, address.Success ? address.Groups["ip"].Value : null, type);
    }
}

public sealed class UnsupportedGpuMetricProvider : IGpuMetricProvider
{
    public string Name => "Unsupported";
    public Task<bool> IsSupportedAsync(AndroidDevice device, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<MetricValue> ReadAsync(AndroidDevice device, string? packageName, CancellationToken cancellationToken) => Task.FromResult(MetricValue.Missing("%", Availability.Unsupported, "No supported GPU provider"));
}
