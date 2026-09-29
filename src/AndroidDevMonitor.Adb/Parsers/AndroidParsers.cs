using System.Globalization;
using System.Text.RegularExpressions;
using AndroidDevMonitor.Core.Models;

namespace AndroidDevMonitor.Adb.Parsers;

public sealed record ProcCpuStat(long User, long Nice, long System, long Idle, long IoWait, long Irq, long SoftIrq, long Steal)
{
    public long Total => User + Nice + System + Idle + IoWait + Irq + SoftIrq + Steal;
    public long Active => Total - Idle - IoWait;
}
public sealed record ProcessCpuStat(int Pid, string Name, char State, long UserTicks, long SystemTicks, long StartTicks)
{
    public long TotalTicks => UserTicks + SystemTicks;
}
public sealed record MemoryInfo(long TotalKb, long AvailableKb, long FreeKb, long BuffersKb, long CachedKb);
public sealed record IoCounters(long ReadBytes, long WriteBytes);
public sealed record FrameStats(IReadOnlyList<double> FrameTimesMs, double Fps, double P95Ms, double JankPercent);
public sealed record StorageInfo(long TotalBytes, long UsedBytes, long AvailableBytes, double UsedPercent, string MountPoint);

public static partial class AndroidParsers
{
    [GeneratedRegex(@"^(?<serial>\S+)\s+(?<state>device|offline|unauthorized|no permissions)(?:\s+(?<details>.*))?$", RegexOptions.Multiline)]
    private static partial Regex DeviceLineRegex();
    [GeneratedRegex(@"(?<key>[\w.-]+):(?<value>\S+)")]
    private static partial Regex DetailRegex();
    [GeneratedRegex(@"^\s*(?<name>[^\s:]+):(?<fields>(?:\s*\d+){16})\s*$", RegexOptions.Multiline)]
    private static partial Regex NetDevLineRegex();
    // toybox `ls -la`: mode, links, owner, group, size, date (yyyy-MM-dd), time (HH:mm), name.
    [GeneratedRegex(@"^(?<kind>[dl-])\S*\s+\d+\s+\S+\s+\S+\s+(?<size>\d+)\s+(?<date>\d{4}-\d{2}-\d{2})\s+(?<time>\d{2}:\d{2})\s(?<name>.+)$")]
    private static partial Regex LsLongLineRegex();

    public static IReadOnlyList<AndroidDevice> ParseDevices(string output)
    {
        var devices = new List<AndroidDevice>();
        foreach (Match match in DeviceLineRegex().Matches(output.Replace("\r", "")))
        {
            var state = match.Groups["state"].Value switch
            {
                "device" => DeviceState.Connected,
                "offline" => DeviceState.Offline,
                "unauthorized" => DeviceState.Unauthorized,
                _ => DeviceState.NoPermissions
            };
            var details = DetailRegex().Matches(match.Groups["details"].Value).ToDictionary(m => m.Groups["key"].Value, m => m.Groups["value"].Value);
            var serial = match.Groups["serial"].Value;
            var model = details.GetValueOrDefault("model")?.Replace('_', ' ') ?? serial;
            var emulator = serial.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase) || details.ContainsKey("transport_id") && details.GetValueOrDefault("product")?.Contains("sdk", StringComparison.OrdinalIgnoreCase) == true;
            devices.Add(new(serial, model, state, emulator ? DeviceKind.Emulator : DeviceKind.Physical, Model: model, LastSeenUtc: DateTimeOffset.UtcNow));
        }
        return devices;
    }

    public static IReadOnlyDictionary<string, string> ParseGetProp(string output)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Replace("\r", "").Split('\n'))
        {
            var match = Regex.Match(line, @"^\[(?<key>[^]]+)\]:\s*\[(?<value>.*)\]$");
            if (match.Success) result[match.Groups["key"].Value] = match.Groups["value"].Value;
        }
        return result;
    }

    public static string? ParseWmSize(string output) => Regex.Matches(output, @"(?:Override|Physical) size:\s*(\d+x\d+)").Select(m => m.Groups[1].Value).LastOrDefault();
    public static string? ParseWmDensity(string output) => Regex.Matches(output, @"(?:Override|Physical) density:\s*(\d+)").Select(m => m.Groups[1].Value).LastOrDefault();

    public static IReadOnlyList<AndroidProcess> ParseProcesses(string output)
    {
        var lines = output.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return [];
        var headers = Regex.Split(lines[0].Trim(), @"\s+");
        int Index(params string[] names) => Array.FindIndex(headers, h => names.Contains(h, StringComparer.OrdinalIgnoreCase));
        var pidIndex = Index("PID");
        var ppidIndex = Index("PPID");
        var userIndex = Index("USER", "UID");
        var stateIndex = Index("S", "STAT", "STATE");
        var rssIndex = Index("RSS");
        var cpuIndex = Index("%CPU", "CPU%");
        var nameIndex = Index("NAME", "CMD", "COMMAND", "ARGS");
        if (pidIndex < 0) return [];
        var result = new List<AndroidProcess>();
        foreach (var line in lines.Skip(1))
        {
            var values = Regex.Split(line.Trim(), @"\s+");
            if (values.Length <= pidIndex || !int.TryParse(values[pidIndex], out var pid)) continue;
            var nameAt = nameIndex >= 0 ? Math.Min(nameIndex, values.Length - 1) : values.Length - 1;
            var command = string.Join(' ', values.Skip(nameAt));
            var name = values[nameAt];
            var package = Regex.IsMatch(name, @"^[a-zA-Z][\w]*(\.[\w]+)+(:[\w.-]+)?$") ? name.Split(':')[0] : null;
            var user = userIndex >= 0 && userIndex < values.Length ? values[userIndex] : "?";
            var system = user is "root" or "system" or "shell" || pid < 1000;
            var group = system ? ProcessGroup.System : package is not null && user.StartsWith("u0_", StringComparison.Ordinal) ? ProcessGroup.Apps : ProcessGroup.Background;
            int? ppid = ppidIndex >= 0 && ppidIndex < values.Length && int.TryParse(values[ppidIndex], out var parsedPpid) ? parsedPpid : null;
            var state = stateIndex >= 0 && stateIndex < values.Length ? MapState(values[stateIndex]) : "Unknown";
            long? rss = rssIndex >= 0 && rssIndex < values.Length && long.TryParse(values[rssIndex], out var rssKb) ? rssKb * 1024 : null;
            double? cpu = cpuIndex >= 0 && cpuIndex < values.Length && double.TryParse(values[cpuIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var cpuValue) ? cpuValue : null;
            result.Add(new(pid, ppid, user, name, command, package, state, group, CpuPercent: cpu, RssBytes: rss));
        }
        return result;
    }

    public static ProcCpuStat? ParseProcStat(string output)
    {
        var parts = Regex.Split(output.Split('\n')[0].Trim(), @"\s+");
        if (parts.Length < 5 || parts[0] != "cpu") return null;
        var values = parts.Skip(1).Select(x => long.TryParse(x, out var n) ? n : 0).Concat(Enumerable.Repeat(0L, 8)).Take(8).ToArray();
        return new(values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7]);
    }

    public static ProcessCpuStat? ParseProcessStat(string output)
    {
        var open = output.IndexOf('('); var close = output.LastIndexOf(')');
        if (open <= 0 || close <= open) return null;
        if (!int.TryParse(output[..open].Trim(), out var pid)) return null;
        var rest = Regex.Split(output[(close + 1)..].Trim(), @"\s+");
        if (rest.Length < 20 || !long.TryParse(rest[11], out var user) || !long.TryParse(rest[12], out var system) || !long.TryParse(rest[19], out var start)) return null;
        return new(pid, output[(open + 1)..close], rest[0][0], user, system, start);
    }

    public static IReadOnlyDictionary<int, ProcessCpuStat> ParseProcessStats(string output)
    {
        var result = new Dictionary<int, ProcessCpuStat>();
        foreach (var line in output.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var process = ParseProcessStat(line);
            if (process is not null) result[process.Pid] = process;
        }
        return result;
    }

    public static MemoryInfo? ParseMemInfo(string output)
    {
        var values = ParseKeyValues(output);
        if (!values.TryGetValue("MemTotal", out var total)) return null;
        values.TryGetValue("MemFree", out var free); values.TryGetValue("Buffers", out var buffers); values.TryGetValue("Cached", out var cached);
        var available = values.GetValueOrDefault("MemAvailable", free + buffers + cached);
        return new(total, available, free, buffers, cached);
    }

    public static long? ParseProcessRss(string output) => ParseKeyValues(output).TryGetValue("VmRSS", out var rss) ? rss * 1024 : null;
    public static int? ParseProcessUid(string output)
    {
        var line = output.Replace("\r", "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(value => value.StartsWith("Uid:", StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }

        var firstValue = line["Uid:".Length..]
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return int.TryParse(firstValue, CultureInfo.InvariantCulture, out var uid) && uid >= 0
            ? uid
            : null;
    }
    public static (long Rx, long Tx)? ParseUidNetworkCounters(string output)
    {
        long[] counters = output.Replace("\r", "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => long.TryParse(line.Trim(), out long value) ? (long?)value : null)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .TakeLast(2)
            .ToArray();
        return counters.Length == 2 ? (counters[0], counters[1]) : null;
    }
    /// <summary>Sums /proc/net/dev byte counters for real interfaces; loopback is excluded because it never leaves the device.</summary>
    public static (long Rx, long Tx)? ParseNetDevTotals(string output)
    {
        long rx = 0, tx = 0; var found = false;
        foreach (Match line in NetDevLineRegex().Matches(output.Replace("\r", "")))
        {
            if (line.Groups["name"].Value == "lo") continue;
            var fields = line.Groups["fields"].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 16 || !long.TryParse(fields[0], CultureInfo.InvariantCulture, out var r) || !long.TryParse(fields[8], CultureInfo.InvariantCulture, out var t)) continue;
            rx += r; tx += t; found = true;
        }
        return found ? (rx, tx) : null;
    }
    public static long? ParseDumpsysPss(string output)
    {
        var match = Regex.Match(output, @"TOTAL\s+(?<pss>\d+)");
        return match.Success && long.TryParse(match.Groups["pss"].Value, out var kb) ? kb * 1024 : null;
    }
    public static IoCounters? ParseIo(string output)
    {
        var values = ParseKeyValues(output);
        return values.TryGetValue("read_bytes", out var read) && values.TryGetValue("write_bytes", out var write) ? new(read, write) : null;
    }

    public static FrameStats? ParseFrameStats(string output)
    {
        var frames = new List<double>();
        var inProfile = false;
        foreach (var line in output.Replace("\r", "").Split('\n'))
        {
            if (line.StartsWith("---PROFILEDATA---")) { inProfile = !inProfile; continue; }
            if (!inProfile || line.StartsWith("Flags,") || string.IsNullOrWhiteSpace(line)) continue;
            var values = line.Split(',');
            if (values.Length < 14 || !long.TryParse(values[1], out var intended) || !long.TryParse(values[13], out var completed) || completed <= intended) continue;
            var ms = (completed - intended) / 1_000_000d;
            if (ms is > 0 and < 5000) frames.Add(ms);
        }
        if (frames.Count == 0) return null;
        var sorted = frames.Order().ToArray();
        var p95 = sorted[(int)Math.Clamp(Math.Ceiling(sorted.Length * .95) - 1, 0, sorted.Length - 1)];
        var fps = Math.Min(60, 1000d / frames.Average());
        return new(frames, fps, p95, frames.Count(x => x > 16.67) * 100d / frames.Count);
    }

    public static IReadOnlyDictionary<string, string> ParseBattery(string output) => output.Replace("\r", "").Split('\n')
        .Select(line => line.Split(':', 2, StringSplitOptions.TrimEntries)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Parses toybox <c>ls -la</c> output for one directory. Timestamps are device-local, so the device's
    /// UTC offset (from <c>date +%z</c>) turns them into real instants. Symlinks are listed as folders
    /// because the common ones (sdcard, storage/self) point at directories.
    /// </summary>
    public static IReadOnlyList<FileEntry> ParseLsLong(string output, string directory, TimeSpan deviceUtcOffset)
    {
        string prefix = directory == "/" ? "/" : directory.TrimEnd('/') + "/";
        List<FileEntry> entries = [];
        foreach (string line in output.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Match match = LsLongLineRegex().Match(line);
            if (!match.Success) continue;
            string kind = match.Groups["kind"].Value;
            string name = match.Groups["name"].Value;
            if (kind == "l")
            {
                int arrow = name.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrow > 0) name = name[..arrow];
            }
            if (name is "." or "..") continue;
            DateTimeOffset? modified = DateTime.TryParseExact($"{match.Groups["date"].Value} {match.Groups["time"].Value}", "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime local)
                ? new DateTimeOffset(local, deviceUtcOffset)
                : null;
            long? size = kind == "-" && long.TryParse(match.Groups["size"].Value, CultureInfo.InvariantCulture, out long bytes) ? bytes : null;
            entries.Add(new FileEntry(name, prefix + name, kind != "-", size, modified));
        }
        return entries;
    }

    /// <summary>Parses <c>date +%z</c> output such as +0200 or -0530.</summary>
    public static TimeSpan? ParseUtcOffset(string output)
    {
        Match match = Regex.Match(output, @"^(?<sign>[+-])(?<hours>\d{2})(?<minutes>\d{2})\s*$", RegexOptions.Multiline);
        if (!match.Success) return null;
        TimeSpan offset = new(int.Parse(match.Groups["hours"].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups["minutes"].Value, CultureInfo.InvariantCulture), 0);
        return match.Groups["sign"].Value == "-" ? -offset : offset;
    }

    /// <summary>
    /// Quotes one argument for the device shell. <c>adb shell</c> joins its arguments with spaces and the device
    /// shell splits them again, so any path with spaces or shell characters must be quoted.
    /// </summary>
    public static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    public static StorageInfo? ParseDf(string output)
    {
        var lines = output.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines.Skip(1))
        {
            var parts = Regex.Split(line.Trim(), @"\s+");
            if (parts.Length < 5) continue;
            var percentIndex = Array.FindIndex(parts, value => value.EndsWith('%') && double.TryParse(value.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out _));
            if (percentIndex < 3 ||
                !long.TryParse(parts[percentIndex - 3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var totalKb) ||
                !long.TryParse(parts[percentIndex - 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var usedKb) ||
                !long.TryParse(parts[percentIndex - 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var availableKb) ||
                !double.TryParse(parts[percentIndex].TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var usedPercent)) continue;
            return new(totalKb * 1024, usedKb * 1024, availableKb * 1024, usedPercent, parts[^1]);
        }
        return null;
    }

    public static string? ParseThermalSeverity(string output)
    {
        var match = Regex.Match(output, @"Thermal Status:\s*(?<status>\d+)", RegexOptions.IgnoreCase);
        if (!match.Success || !int.TryParse(match.Groups["status"].Value, out var status)) return null;
        return status switch { 0 => "None", 1 => "Light", 2 => "Moderate", 3 => "Severe", 4 => "Critical", 5 => "Emergency", 6 => "Shutdown", _ => "Unknown" };
    }

    private static Dictionary<string, long> ParseKeyValues(string output)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Replace("\r", "").Split('\n'))
        {
            var parts = line.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && long.TryParse(parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) result[parts[0]] = value;
        }
        return result;
    }
    private static string MapState(string state) => state.Length == 0 ? "Unknown" : state[0] switch { 'R' => "Foreground", 'S' => "Sleeping", 'D' => "Blocked", 'Z' => "Zombie", 'T' => "Stopped", _ => "Unknown" };
}
