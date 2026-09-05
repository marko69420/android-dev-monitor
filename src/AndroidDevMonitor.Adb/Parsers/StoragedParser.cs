using System.Globalization;

namespace AndroidDevMonitor.Adb.Parsers;

public sealed record DiskUsageWindow(long StartSeconds, long EndSeconds, double ReadBytes, double WriteBytes);

public static class StoragedParser
{
    public static DiskUsageWindow? ParseLatest(string output)
    {
        long start = 0, end = 0;
        double read = 0, write = 0;
        bool found = false, valid = true;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var header = line.Split(',');
            if (header.Length == 2 && long.TryParse(header[1], out var nextEnd) &&
                (header[0].Length == 0 || long.TryParse(header[0], out _)))
            {
                start = header[0].Length == 0 ? end : long.Parse(header[0], CultureInfo.InvariantCulture);
                end = nextEnd; read = write = 0; found = true; valid = start > 0 && end > start;
                continue;
            }
            if (!found) continue;
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 9) { valid = false; continue; }
            for (var i = 1; i < fields.Length; i++)
            {
                if (!ulong.TryParse(fields[i], NumberStyles.None, CultureInfo.InvariantCulture, out var value)) { valid = false; break; }
                if (i % 2 == 1) read += value; else write += value;
            }
        }
        return found && valid ? new(start, end, read, write) : null;
    }
}
