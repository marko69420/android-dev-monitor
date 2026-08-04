using AndroidDevMonitor.Adb.Parsers;

namespace AndroidDevMonitor.Collectors.Calculations;

public static class MetricCalculators
{
    public static double? CpuPercent(ProcCpuStat previousDevice, ProcCpuStat currentDevice, ProcessCpuStat previousProcess, ProcessCpuStat currentProcess)
    {
        if (previousProcess.Pid != currentProcess.Pid || previousProcess.StartTicks != currentProcess.StartTicks) return null;
        var total = currentDevice.Total - previousDevice.Total;
        var process = currentProcess.TotalTicks - previousProcess.TotalTicks;
        if (total <= 0 || process < 0) return null;
        return Math.Clamp(process * 100d / total, 0, 100);
    }

    public static double? DeviceCpuPercent(ProcCpuStat previous, ProcCpuStat current)
    {
        var total = current.Total - previous.Total;
        var active = current.Active - previous.Active;
        return total <= 0 || active < 0 ? null : Math.Clamp(active * 100d / total, 0, 100);
    }

    public static double? Rate(long previous, long current, TimeSpan elapsed) => elapsed.TotalSeconds <= 0 || current < previous ? null : (current - previous) / elapsed.TotalSeconds;
}
