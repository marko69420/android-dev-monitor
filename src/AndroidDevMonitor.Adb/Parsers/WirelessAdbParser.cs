using System.Text.RegularExpressions;

namespace AndroidDevMonitor.Adb.Parsers;

public sealed record AdbVersionInfo(Version? ProtocolVersion, Version? PlatformToolsVersion)
{
    public bool SupportsSecureWireless => ProtocolVersion is not null && ProtocolVersion >= new Version(1, 0, 41);
    public bool SupportsWifi2 => PlatformToolsVersion is not null && PlatformToolsVersion >= new Version(37, 0, 0);
}

public sealed record AdbMdnsService(string Name, string Type, string Address)
{
    public bool CanPair => Type.Contains("pairing", StringComparison.OrdinalIgnoreCase);
    public bool CanConnect => Type.Contains("connect", StringComparison.OrdinalIgnoreCase) || Type == "_adb._tcp";
}

public static partial class WirelessAdbParser
{
    [GeneratedRegex(@"Android Debug Bridge version\s+(?<version>\d+\.\d+\.\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ProtocolRegex();

    [GeneratedRegex(@"^Version\s+(?<version>\d+\.\d+\.\d+)(?:[-\s]|$)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex PlatformToolsRegex();

    [GeneratedRegex(@"^(?<name>\S+)\s+(?<type>_adb[^\s]+)\s+(?<address>\S+)$", RegexOptions.Multiline)]
    private static partial Regex MdnsRegex();

    public static AdbVersionInfo ParseVersion(string? output)
    {
        output ??= string.Empty;
        return new(Parse(ProtocolRegex().Match(output)), Parse(PlatformToolsRegex().Match(output)));
    }

    public static IReadOnlyList<AdbMdnsService> ParseServices(string? output) =>
        MdnsRegex().Matches(output ?? string.Empty)
            .Select(match => new AdbMdnsService(
                match.Groups["name"].Value,
                match.Groups["type"].Value,
                match.Groups["address"].Value))
            .ToArray();

    public static bool IsValidEndpoint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        Match match = Regex.Match(value.Trim(), @"^(?:[A-Za-z0-9_.-]+|\[[0-9A-Za-z:.%_-]+\]):(?<port>\d{1,5})$");
        return match.Success && int.TryParse(match.Groups["port"].Value, out int port) && port is > 0 and <= 65535;
    }

    private static Version? Parse(Match match) =>
        match.Success && Version.TryParse(match.Groups["version"].Value, out Version? version) ? version : null;
}
