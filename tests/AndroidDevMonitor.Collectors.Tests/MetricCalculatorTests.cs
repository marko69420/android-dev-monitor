using AndroidDevMonitor.Adb.Parsers;
using AndroidDevMonitor.Collectors.Calculations;
using AndroidDevMonitor.Collectors.Sources;
using AndroidDevMonitor.Core.Models;

namespace AndroidDevMonitor.Collectors.Tests;

public sealed class MetricCalculatorTests
{
    [Fact]
    public void One_busy_core_on_two_core_device_is_fifty_percent()
    {
        var d0 = new ProcCpuStat(0, 0, 0, 0, 0, 0, 0, 0); var d1 = new ProcCpuStat(100, 0, 0, 100, 0, 0, 0, 0);
        var p0 = new ProcessCpuStat(42, "game", 'R', 0, 0, 1000); var p1 = new ProcessCpuStat(42, "game", 'R', 100, 0, 1000);
        Assert.Equal(50, MetricCalculators.CpuPercent(d0, d1, p0, p1));
    }
    [Fact]
    public void Pid_reuse_resets_cpu_baseline()
    {
        var d0 = new ProcCpuStat(0, 0, 0, 0, 0, 0, 0, 0); var d1 = new ProcCpuStat(100, 0, 0, 100, 0, 0, 0, 0);
        Assert.Null(MetricCalculators.CpuPercent(d0, d1, new(42, "old", 'R', 10, 0, 1000), new(42, "new", 'R', 80, 0, 2000)));
    }
    [Fact] public void Counter_rate_handles_reset_and_zero_elapsed() { Assert.Equal(50, MetricCalculators.Rate(100, 200, TimeSpan.FromSeconds(2))); Assert.Null(MetricCalculators.Rate(200, 100, TimeSpan.FromSeconds(1))); Assert.Null(MetricCalculators.Rate(0, 1, TimeSpan.Zero)); }
    [Fact] public void Device_cpu_excludes_idle_and_iowait() { var before = new ProcCpuStat(0, 0, 0, 0, 0, 0, 0, 0); var after = new ProcCpuStat(40, 0, 20, 30, 10, 0, 0, 0); Assert.Equal(60, MetricCalculators.DeviceCpuPercent(before, after)); }
    [Fact]
    public void Device_cpu_maps_idle_to_zero_and_fully_busy_to_one_hundred()
    {
        var start = new ProcCpuStat(0, 0, 0, 0, 0, 0, 0, 0);
        Assert.Equal(0, MetricCalculators.DeviceCpuPercent(start, new(0, 0, 0, 100, 0, 0, 0, 0)));
        Assert.Equal(100, MetricCalculators.DeviceCpuPercent(start, new(70, 0, 30, 0, 0, 0, 0, 0)));
    }

    [Fact]
    public async Task Demo_process_cpu_is_total_capacity_and_never_exceeds_one_hundred()
    {
        var source = new DemoMonitoringSource();
        var device = new AndroidDevice("demo", "Demo", DeviceState.Connected, DeviceKind.Emulator);
        var processes = await source.GetProcessesAsync(device, CancellationToken.None);
        var total = processes.Sum(process => process.CpuPercent ?? 0);
        Assert.InRange(total, 0, 100);
        Assert.Equal(2, processes.Count(process => process.PackageName == "com.android.systemui"));
    }
}
