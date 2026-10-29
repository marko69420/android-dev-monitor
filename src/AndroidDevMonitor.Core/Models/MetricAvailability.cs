using System.Text;

namespace AndroidDevMonitor.Core.Models;

/// <summary>Whether one metric can be read on a device, which provider reads it, and why not when it cannot.</summary>
/// <param name="Probe">The command that was run to check the provider; null when nothing was run, e.g. no app is selected.</param>
public sealed record MetricCapability(string Metric, Availability Availability, string Provider, string Reason = "", AdbCommandResult? Probe = null);

/// <summary>The result of running each metric provider once on a device and, when one is selected, an app.</summary>
public sealed record MetricAvailabilityReport(AndroidDevice Device, string? PackageName, int? ProcessId, DateTimeOffset CheckedUtc, IReadOnlyList<MetricCapability> Metrics)
{
    private const int RawLinesPerProvider = 40;
    private const int ReasonWidth = 90;

    public static string Describe(Availability availability) => availability == Availability.PermissionDenied ? "Permission denied" : availability.ToString();

    /// <summary>A summary table with a reason under every unavailable metric, then each provider's raw output.</summary>
    public string ToReport()
    {
        StringBuilder text = new();
        text.AppendLine("METRIC AVAILABILITY");
        text.AppendLine($"Device: {Device.FriendlyName} ({Device.Serial}) · {Device.VersionLine} · {Device.Kind}");
        text.AppendLine("App: " + (PackageName is null ? "none selected" : ProcessId is null ? $"{PackageName} · not running" : $"{PackageName} · PID {ProcessId}"));
        text.AppendLine($"Checked: {CheckedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        text.Append($"{Metrics.Count(metric => metric.Availability == Availability.Available)} of {Metrics.Count} available");
        foreach (IGrouping<Availability, MetricCapability> group in Metrics.Where(metric => metric.Availability != Availability.Available).GroupBy(metric => metric.Availability).OrderBy(group => group.Key))
            text.Append($" · {group.Count()} {Describe(group.Key).ToLowerInvariant()}");
        text.AppendLine();
        text.AppendLine();

        int metricWidth = Metrics.Select(metric => metric.Metric.Length).DefaultIfEmpty(0).Max() + 2;
        int statusWidth = Describe(Availability.PermissionDenied).Length + 2;
        string indent = new(' ', metricWidth + statusWidth);
        text.AppendLine("METRIC".PadRight(metricWidth) + "STATUS".PadRight(statusWidth) + "PROVIDER");
        foreach (MetricCapability metric in Metrics)
        {
            text.AppendLine(metric.Metric.PadRight(metricWidth) + Describe(metric.Availability).PadRight(statusWidth) + metric.Provider);
            foreach (string line in Wrap(metric.Reason, ReasonWidth)) text.AppendLine(indent + line);
        }

        text.AppendLine();
        text.AppendLine("RAW PROVIDER OUTPUT");
        foreach (MetricCapability metric in Metrics)
        {
            if (metric.Probe is not { } probe) continue;
            string exit = probe.TimedOut ? "timed out" : probe.Cancelled ? "cancelled" : probe.ExitCode is int code ? $"exit {code}" : "did not run";
            text.AppendLine($"--- {metric.Metric} · adb {string.Join(' ', probe.Command)} · {exit}");
            string output = probe.StandardOutput.TrimEnd() + (string.IsNullOrWhiteSpace(probe.StandardError) ? "" : "\nstderr: " + probe.StandardError.Trim());
            if (output.Trim().Length == 0) output = "(no output)";
            string[] lines = output.Replace("\r", "").Split('\n');
            foreach (string line in lines.Take(RawLinesPerProvider)) text.AppendLine(line);
            if (lines.Length > RawLinesPerProvider) text.AppendLine($"… {lines.Length - RawLinesPerProvider} more lines");
        }
        return text.ToString();
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        StringBuilder line = new();
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                yield return line.ToString();
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0) yield return line.ToString();
    }
}
