using AndroidDevMonitor.Core.Configuration;
using AndroidDevMonitor.Core.Models;
using AndroidDevMonitor.Core.Services;

namespace AndroidDevMonitor.Collectors.Sources;

public sealed class DemoDeviceDiscoveryService : IDeviceDiscoveryService
{
    private static readonly AndroidDevice[] Devices =
    [
        new("demo-emulator-5554", "[DEMO] Small Phone 5554", DeviceState.Connected, DeviceKind.Emulator, "11", 30, "Google", "Small Phone", "x86_64", "1080x1920", "420", 4, 4L * 1024 * 1024 * 1024, 32L * 1024 * 1024 * 1024, DateTimeOffset.UtcNow),
        new("demo-emulator-5558", "[DEMO] Small Phone 2 5558", DeviceState.Connected, DeviceKind.Emulator, "11", 30, "Google", "Small Phone 2", "x86_64", "1080x1920", "420", 2, 3L * 1024 * 1024 * 1024, 24L * 1024 * 1024 * 1024, DateTimeOffset.UtcNow),
        new("demo-physical-pixel6", "[DEMO] Pixel 6", DeviceState.Connected, DeviceKind.Physical, "14", 34, "Google", "Pixel 6", "arm64-v8a", "1080x2400", "420", 8, 8L * 1024 * 1024 * 1024, 128L * 1024 * 1024 * 1024, DateTimeOffset.UtcNow)
    ];
    public Task<IReadOnlyList<AndroidDevice>> DiscoverAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AndroidDevice>>(Devices);
    public Task<AndroidDevice> EnrichAsync(AndroidDevice device, CancellationToken cancellationToken) => Task.FromResult(device);
}

public sealed class DemoMonitoringSource : IMonitoringSource
{
    private long _tick;
    private long _processTick;
    public async IAsyncEnumerable<MetricSample> StreamMetricsAsync(AndroidDevice device, string? packageName, Guid sessionId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(MonitoringConstants.LightweightInterval);
        while (!cancellationToken.IsCancellationRequested)
        {
            var t = Interlocked.Increment(ref _tick); var phase = (device.Serial.GetHashCode(StringComparison.Ordinal) & 31) + t;
            double Wave(double baseline, double amplitude, double divisor) => baseline + Math.Sin(phase / divisor) * amplitude;
            yield return new(sessionId, device.Serial, packageName, DateTimeOffset.UtcNow,
                Wave(27, 12, 5), Wave(15, 8, 4), (long)(Wave(1.7, .18, 9) * 1024 * 1024 * 1024), (long)(Wave(2.3, .18, 9) * 1024 * 1024 * 1024),
                (long)(Wave(512, 42, 11) * 1024 * 1024), (long)(Wave(475, 30, 8) * 1024 * 1024), Wave(900_000, 500_000, 3), Wave(3_500_000, 2_000_000, 4),
                Wave(2_400_000, 1_200_000, 7), Wave(350_000, 180_000, 6), Wave(1_700_000, 900_000, 7), Wave(210_000, 90_000, 6),
                Wave(58, 2, 8), Wave(18, 4, 6), Wave(4, 3, 5), Wave(35, 2, 20), Wave(38, 12, 5), "Deterministic demo provider",
                DeviceStorageTotalBytes: device.TotalStorageBytes, DeviceStorageAvailableBytes: 18L * 1024 * 1024 * 1024,
                BatteryPercent: 82, IsCharging: false, ThermalSeverity: "None", ActiveNetworkInterface: "wlan0",
                IpAddress: "10.0.2.15", NetworkType: "Wi-Fi", PackageCodeBytes: 184L * 1024 * 1024,
                PackageDataBytes: 640L * 1024 * 1024, PackageCacheBytes: 96L * 1024 * 1024);
            if (!await timer.WaitForNextTickAsync(cancellationToken)) break;
        }
    }

    public Task<IReadOnlyList<AndroidProcess>> GetProcessesAsync(AndroidDevice device, CancellationToken cancellationToken)
    {
        var phase = Interlocked.Increment(ref _processTick);
        var total = Math.Clamp(31 + Math.Sin(phase / 2.4) * 12, 0, 100);
        double Cpu(double share) => total * share;
        return Task.FromResult<IReadOnlyList<AndroidProcess>>(
        [
            new(13432, 901, "u0_a124", "MyGame", "com.company.mygame", "com.company.mygame", "Foreground", ProcessGroup.Apps, 1001, Cpu(.42), 512L*1024*1024, 1_200_000, 2_100_000, 3_400_000, 400_000, 59),
            new(11234, 901, "u0_a126", "Unity Player Activity", "com.unity3d.player", "com.unity3d.player", "Foreground service", ProcessGroup.Apps, 1002, Cpu(.18), 256L*1024*1024, 300_000, 600_000, null, null, 60),
            new(18123, 1, "u0_a12", "Google Play services", "com.google.android.gms", "com.google.android.gms", "Background", ProcessGroup.Background, 888, Cpu(.08), 182L*1024*1024),
            new(19857, 1, "u0_a19", "Google Play Store", "com.android.vending", "com.android.vending", "Background", ProcessGroup.Background, 889, Cpu(.06), 96L*1024*1024),
            new(17302, 1, "u0_a42", "Chrome", "com.android.chrome", "com.android.chrome", "Sleeping", ProcessGroup.Background, 890, Cpu(.05), 71L*1024*1024),
            new(1, 0, "root", "Android System", "init", null, "System", ProcessGroup.System, 1, Cpu(.05), 123L*1024*1024),
            new(2628, 1, "system", "System UI", "com.android.systemui", "com.android.systemui", "System", ProcessGroup.System, 100, Cpu(.05), 82L*1024*1024),
            new(2630, 2628, "system", "System UI screenshot", "com.android.systemui:screenshot", "com.android.systemui", "Sleeping", ProcessGroup.System, 101, Cpu(.02), 18L*1024*1024),
            new(0, null, "kernel", "Kernel / unattributed CPU", "Kernel, interrupts, and unassigned sampling time", null, "System", ProcessGroup.System, CpuPercent: Cpu(.09))
        ]);
    }
    public Task<CapabilitySet> DetectCapabilitiesAsync(AndroidDevice device, CancellationToken cancellationToken) => Task.FromResult(new CapabilitySet(true, true, true, true, true, true, true, true, true, new Dictionary<string, string> { ["All"] = "Deterministic demo provider" }));
}
