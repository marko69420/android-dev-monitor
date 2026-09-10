using System.IO.Compression;
using System.Text;

namespace AndroidDevMonitor.Core.Analysis;

public sealed record BugReportFinding(string Category, string Text);

public sealed record BugReportAnalysis(
    string Source,
    long LinesScanned,
    int AnrCount,
    int CrashCount,
    int TombstoneCount,
    int WatchdogCount,
    int LowMemoryCount,
    int StrictModeCount,
    int ThermalCount,
    IReadOnlyList<BugReportFinding> Findings,
    int FilesScanned = 1)
{
    public int TotalSignals => AnrCount + CrashCount + TombstoneCount + WatchdogCount + LowMemoryCount + StrictModeCount + ThermalCount;

    public string ToReport()
    {
        StringBuilder text = new();
        text.AppendLine("ANDROID BUG REPORT ANALYSIS");
        text.AppendLine($"Source: {Source}");
        text.AppendLine($"Files scanned: {FilesScanned:N0}");
        text.AppendLine($"Lines scanned: {LinesScanned:N0}");
        text.AppendLine($"Signals: {TotalSignals:N0}");
        text.AppendLine($"ANR {AnrCount} · crashes {CrashCount} · tombstones {TombstoneCount} · watchdog {WatchdogCount} · low memory {LowMemoryCount} · StrictMode {StrictModeCount} · thermal {ThermalCount}");
        text.AppendLine();
        text.AppendLine("HIGHLIGHTS");
        foreach (BugReportFinding finding in Findings) text.AppendLine($"[{finding.Category}] {finding.Text}");
        if (Findings.Count == 0) text.AppendLine("No matching high-signal events were found.");
        return text.ToString();
    }
}

public static class BugReportAnalyzer
{
    private const int MaximumFindings = 250;
    private const int MaximumFiles = 60;
    private const long MaximumEntryBytes = 64L * 1024 * 1024;
    private const long MaximumTotalBytes = 256L * 1024 * 1024;

    public static async Task<BugReportAnalysis> AnalyzeAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Bug report was not found.", path);
        if (string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            using ZipArchive archive = ZipFile.OpenRead(path);
            ZipArchiveEntry[] entries = archive.Entries
                .Where(IsRelevantArchiveEntry)
                .OrderByDescending(item => item.FullName.Contains("bugreport", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(item => item.FullName.Contains("tombstone", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(item => item.Length)
                .ToArray();
            if (entries.Length == 0) throw new InvalidDataException("The ZIP does not contain a readable bugreport text file.");

            long lines = 0;
            int anr = 0, crash = 0, tombstone = 0, watchdog = 0, lowMemory = 0, strictMode = 0, thermal = 0;
            List<BugReportFinding> findings = [];
            long totalBytes = 0;
            int filesScanned = 0;
            List<string> scannedNames = [];
            foreach (ZipArchiveEntry entry in entries.Take(MaximumFiles))
            {
                if (entry.Length == 0 || entry.Length > MaximumEntryBytes || totalBytes + entry.Length > MaximumTotalBytes) continue;
                totalBytes += entry.Length;
                filesScanned++;
                scannedNames.Add(entry.FullName);
                await using Stream stream = entry.Open();
                BugReportAnalysis result = await AnalyzeStreamAsync(stream, entry.FullName, cancellationToken);
                lines += result.LinesScanned;
                anr += result.AnrCount;
                crash += result.CrashCount;
                tombstone += result.TombstoneCount;
                watchdog += result.WatchdogCount;
                lowMemory += result.LowMemoryCount;
                strictMode += result.StrictModeCount;
                thermal += result.ThermalCount;
                findings.AddRange(result.Findings.Select(finding => new BugReportFinding(finding.Category, $"{entry.FullName}: {finding.Text}")));
                if (findings.Count >= MaximumFindings) findings.RemoveRange(MaximumFindings, findings.Count - MaximumFindings);
                if (totalBytes >= MaximumTotalBytes) break;
            }

            if (filesScanned == 0) throw new InvalidDataException("The ZIP entries are too large to analyze safely.");
            string fileCount = filesScanned == 1 ? "1 file" : $"{filesScanned} files";
            return new($"{path} :: {string.Join(", ", scannedNames)} ({fileCount})", lines, anr, crash, tombstone, watchdog, lowMemory, strictMode, thermal, findings, filesScanned);
        }

        await using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await AnalyzeStreamAsync(file, path, cancellationToken);
    }

    public static async Task<BugReportAnalysis> AnalyzeStreamAsync(Stream stream, string source, CancellationToken cancellationToken)
    {
        using StreamReader reader = new(stream, Encoding.UTF8, true, 64 * 1024, leaveOpen: true);
        long lines = 0;
        int anr = 0, crash = 0, tombstone = 0, watchdog = 0, lowMemory = 0, strictMode = 0, thermal = 0;
        List<BugReportFinding> findings = [];
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lines++;
            string? category = Classify(line);
            switch (category)
            {
                case "ANR": anr++; break;
                case "CRASH": crash++; break;
                case "TOMBSTONE": tombstone++; break;
                case "WATCHDOG": watchdog++; break;
                case "LOW MEMORY": lowMemory++; break;
                case "STRICTMODE": strictMode++; break;
                case "THERMAL": thermal++; break;
            }
            if (category is not null && findings.Count < MaximumFindings)
                findings.Add(new(category, Normalize(line)));
        }
        return new(source, lines, anr, crash, tombstone, watchdog, lowMemory, strictMode, thermal, findings);
    }

    private static string? Classify(string line)
    {
        if (line.Contains("FATAL EXCEPTION", StringComparison.OrdinalIgnoreCase) || line.Contains("Fatal signal", StringComparison.OrdinalIgnoreCase)) return "CRASH";
        if (line.Contains("ANR in ", StringComparison.OrdinalIgnoreCase) || line.Contains("am_anr", StringComparison.OrdinalIgnoreCase)) return "ANR";
        if (line.Contains("tombstone", StringComparison.OrdinalIgnoreCase)) return "TOMBSTONE";
        if (line.Contains("watchdog", StringComparison.OrdinalIgnoreCase)) return "WATCHDOG";
        if (line.Contains("lowmemorykiller", StringComparison.OrdinalIgnoreCase) || line.Contains("lmkd", StringComparison.OrdinalIgnoreCase) || line.Contains("low memory", StringComparison.OrdinalIgnoreCase)) return "LOW MEMORY";
        if (line.Contains("StrictMode", StringComparison.OrdinalIgnoreCase)) return "STRICTMODE";
        if (line.Contains("thermal thrott", StringComparison.OrdinalIgnoreCase) || line.Contains("THERMAL_STATUS", StringComparison.OrdinalIgnoreCase)) return "THERMAL";
        return null;
    }

    private static bool IsRelevantArchiveEntry(ZipArchiveEntry entry)
    {
        string name = entry.FullName;
        if (entry.Length == 0) return false;
        if (name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return true;
        return name.Contains("tombstone", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("anr", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("crash", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("logcat", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("dropbox", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string line)
    {
        string normalized = string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 320 ? normalized : normalized[..320] + "…";
    }
}
