using System.Globalization;
using System.Text;

namespace AndroidDevMonitor.Core.Analysis;

/// <summary>
/// One event emitted by the optional Android Dev Monitor companion SDK and observed in logcat.
/// </summary>
public sealed record CompanionEvent(
    DateTimeOffset TimestampUtc,
    string Kind,
    string Name,
    double? Value,
    string? Unit,
    string Project,
    string DeviceSerial,
    string Payload,
    string Raw)
{
    public string ToReportLine()
    {
        string value = "-";
        if (Value.HasValue)
        {
            value = Value.Value.ToString("0.###", CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(Unit)) value = value + " " + Unit;
        }
        string extra = string.IsNullOrWhiteSpace(Payload) ? string.Empty : "  " + Payload;
        return $"{TimestampUtc.ToLocalTime():HH:mm:ss.fff}\t{Kind}\t{Name}\t{value}{extra}";
    }
}

/// <summary>
/// Parses marker lines such as <c>ADM_COMPANION|mark|feed loaded|412|ms|first page</c> from logcat.
/// The format is deliberately simple so any app can emit it with one log call.
/// </summary>
public static class CompanionEventParser
{
    public const string Marker = "ADM_COMPANION";
    public const int MaximumReportedEvents = 500;

    public static bool TryParse(string? logLine, string project, string deviceSerial, out CompanionEvent? parsed) =>
        TryParse(logLine, project, deviceSerial, DateTimeOffset.UtcNow, out parsed);

    public static bool TryParse(string? logLine, string project, string deviceSerial, DateTimeOffset timestampUtc, out CompanionEvent? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(logLine)) return false;
        int marker = logLine.IndexOf(Marker, StringComparison.Ordinal);
        if (marker < 0) return false;

        string body = logLine[(marker + Marker.Length)..].Trim();
        if (body.StartsWith('|') || body.StartsWith(':')) body = body[1..].Trim();
        if (body.Length == 0) return false;

        string[] parts = body.Split('|');
        string kind = parts[0].Trim();
        if (kind.Length == 0) return false;

        string name = kind;
        if (parts.Length > 1 && parts[1].Trim().Length > 0) name = parts[1].Trim();

        double? value = null;
        string payload = string.Empty;
        if (parts.Length > 4) payload = parts[4].Trim();
        if (parts.Length > 2 && parts[2].Trim().Length > 0)
        {
            string rawValue = parts[2].Trim();
            double numeric;
            if (double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out numeric))
            {
                value = numeric;
            }
            else
            {
                payload = payload.Length == 0 ? rawValue : rawValue + " " + payload;
            }
        }

        string unit = string.Empty;
        if (parts.Length > 3) unit = parts[3].Trim();
        string? normalizedUnit = string.IsNullOrWhiteSpace(unit) ? null : unit;

        parsed = new CompanionEvent(timestampUtc, kind, name, value, normalizedUnit, project, deviceSerial, payload, logLine.Trim());
        return true;
    }

    public static string BuildReport(string project, string deviceSerial, IReadOnlyList<CompanionEvent> events, int scannedLines)
    {
        StringBuilder report = new();
        report.AppendLine("ANDROID DEV MONITOR COMPANION EVENTS");
        report.AppendLine($"Project:       {(string.IsNullOrWhiteSpace(project) ? "(not selected)" : project)}");
        report.AppendLine($"Device:        {deviceSerial}");
        report.AppendLine($"Generated:     {DateTimeOffset.Now:O}");
        report.AppendLine($"Scanned lines: {scannedLines:N0}");
        report.AppendLine($"Markers found: {events.Count:N0}");
        report.AppendLine();

        if (events.Count == 0)
        {
            report.AppendLine("No ADM_COMPANION markers were found. Add the companion SDK calls to your app, rebuild, and run the app while this tool collects logcat.");
            return report.ToString();
        }

        report.AppendLine("SUMMARY BY KIND");
        foreach (IGrouping<string, CompanionEvent> group in events.GroupBy(item => item.Kind, StringComparer.OrdinalIgnoreCase).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            double[] values = group.Where(item => item.Value.HasValue).Select(item => item.Value!.Value).ToArray();
            string stats = values.Length == 0
                ? $"{group.Count()} events"
                : $"{group.Count()} events · avg {values.Average():0.###} · min {values.Min():0.###} · max {values.Max():0.###}";
            report.AppendLine($"{group.Key}\t{stats}");
        }
        report.AppendLine();
        report.AppendLine("TIMELINE");
        report.AppendLine("TIME\tKIND\tNAME\tVALUE");
        foreach (CompanionEvent item in events.Take(MaximumReportedEvents)) report.AppendLine(item.ToReportLine());
        if (events.Count > MaximumReportedEvents)
            report.AppendLine($"{events.Count - MaximumReportedEvents} additional event(s) were trimmed in this view.");
        return report.ToString();
    }
}
