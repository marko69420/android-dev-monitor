using AndroidDevMonitor.Adb.Parsers;
using AndroidDevMonitor.Core.Configuration;
using AndroidDevMonitor.Core.Models;
using AndroidDevMonitor.Core.Services;
using System.Collections.Concurrent;

namespace AndroidDevMonitor.Adb.Discovery;

public sealed class DeviceDiscoveryService(IAdbExecutor adb) : IDeviceDiscoveryService
{
    private readonly ConcurrentDictionary<string, (AndroidDevice Device, DateTimeOffset Loaded)> _metadata = new();
    public async Task<IReadOnlyList<AndroidDevice>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var result = await adb.ExecuteAsync(null, ["devices", "-l"], MonitoringConstants.DefaultAdbTimeout, cancellationToken);
        return result.Success ? AndroidParsers.ParseDevices(result.StandardOutput) : [];
    }

    public async Task<AndroidDevice> EnrichAsync(AndroidDevice device, CancellationToken cancellationToken)
    {
        if (!device.IsConnected) return device;
        if (_metadata.TryGetValue(device.Serial, out var cached) && DateTimeOffset.UtcNow - cached.Loaded < TimeSpan.FromMinutes(1)) return cached.Device with { State = device.State, LastSeenUtc = device.LastSeenUtc };
        var getpropTask = adb.ExecuteAsync(device.Serial, ["shell", "getprop"], MonitoringConstants.DefaultAdbTimeout, cancellationToken);
        var sizeTask = adb.ExecuteAsync(device.Serial, ["shell", "wm", "size"], MonitoringConstants.DefaultAdbTimeout, cancellationToken);
        var densityTask = adb.ExecuteAsync(device.Serial, ["shell", "wm", "density"], MonitoringConstants.DefaultAdbTimeout, cancellationToken);
        var meminfoTask = adb.ExecuteAsync(device.Serial, ["shell", "cat", "/proc/meminfo"], MonitoringConstants.DefaultAdbTimeout, cancellationToken);
        var storageTask = adb.ExecuteAsync(device.Serial, ["shell", "df", "-k", "/data"], MonitoringConstants.DefaultAdbTimeout, cancellationToken);
        var coresTask = adb.ExecuteAsync(device.Serial, ["shell", "nproc"], MonitoringConstants.DefaultAdbTimeout, cancellationToken);
        Task<AdbCommandResult?> avdTask = device.Kind == DeviceKind.Emulator
            ? ReadAvdNameAsync(device.Serial, cancellationToken)
            : Task.FromResult<AdbCommandResult?>(null);
        await Task.WhenAll(new Task[]
        {
            getpropTask,
            sizeTask,
            densityTask,
            meminfoTask,
            storageTask,
            coresTask,
            avdTask
        });
        var getprop = await getpropTask;
        var size = await sizeTask;
        var density = await densityTask;
        var meminfo = await meminfoTask;
        var storage = await storageTask;
        var cores = await coresTask;
        var avd = await avdTask;
        var props = AndroidParsers.ParseGetProp(getprop.StandardOutput);
        var api = int.TryParse(props.GetValueOrDefault("ro.build.version.sdk"), out var n) ? n : (int?)null;
        var model = props.GetValueOrDefault("ro.product.model") ?? device.Model;
        var memory = AndroidParsers.ParseMemInfo(meminfo.StandardOutput);
        var storageInfo = AndroidParsers.ParseDf(storage.StandardOutput);
        var friendly = avd?.Success == true ? avd.StandardOutput.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(x => !x.Equals("OK", StringComparison.OrdinalIgnoreCase))?.Replace('_', ' ') : null;
        var enriched = device with
        {
            FriendlyName = !string.IsNullOrWhiteSpace(friendly) ? friendly : device.FriendlyName == device.Serial || device.FriendlyName == device.Model ? model : device.FriendlyName,
            Manufacturer = props.GetValueOrDefault("ro.product.manufacturer") ?? "Unknown",
            Model = model,
            AndroidVersion = props.GetValueOrDefault("ro.build.version.release") ?? "Unknown",
            ApiLevel = api,
            Abi = props.GetValueOrDefault("ro.product.cpu.abi") ?? "Unknown",
            Resolution = AndroidParsers.ParseWmSize(size.StandardOutput) ?? "N/A",
            Density = AndroidParsers.ParseWmDensity(density.StandardOutput) ?? "N/A",
            Kind = props.GetValueOrDefault("ro.kernel.qemu") == "1" ? DeviceKind.Emulator : device.Kind,
            CpuCores = int.TryParse(cores.StandardOutput.Trim(), out var coreCount) ? coreCount : null,
            TotalMemoryBytes = memory?.TotalKb * 1024,
            TotalStorageBytes = storageInfo?.TotalBytes
        };
        _metadata[device.Serial] = (enriched, DateTimeOffset.UtcNow); return enriched;
    }

    private async Task<AdbCommandResult?> ReadAvdNameAsync(string serial, CancellationToken cancellationToken) =>
        await adb.ExecuteAsync(serial, ["emu", "avd", "name"], MonitoringConstants.DefaultAdbTimeout, cancellationToken);
}
