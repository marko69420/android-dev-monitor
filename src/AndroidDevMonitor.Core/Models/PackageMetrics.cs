namespace AndroidDevMonitor.Core.Models;

public sealed record PackageMetrics(bool IsRunning, double? CpuPercent, long? RssBytes)
{
    public static PackageMetrics Aggregate(IReadOnlyList<AndroidProcess> processes, string? package)
    {
        if (string.IsNullOrWhiteSpace(package)) return new(false, null, null);
        var matches = processes.Where(p => p.PackageName == package).ToArray();
        if (matches.Length == 0) return new(false, null, null);
        return new(true,
            matches.All(p => p.CpuPercent.HasValue) ? Math.Clamp(matches.Sum(p => p.CpuPercent!.Value), 0, 100) : null,
            matches.All(p => p.RssBytes.HasValue) ? matches.Sum(p => p.RssBytes!.Value) : null);
    }
}
