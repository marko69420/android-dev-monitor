using System.Text.RegularExpressions;

namespace AndroidDevMonitor.Adb.Parsers;

public sealed record InstrumentationInfo(string Component, string TargetPackage, string SourcePath);

public static partial class InstrumentationParser
{
    [GeneratedRegex(@"^instrumentation:(?<path>[^=]+)=(?<component>[^\s]+)\s+\(target=(?<target>[^)]+)\)$", RegexOptions.Multiline)]
    private static partial Regex LineRegex();

    public static IReadOnlyList<InstrumentationInfo> Parse(string? output) =>
        LineRegex().Matches(output ?? string.Empty)
            .Select(match => new InstrumentationInfo(
                match.Groups["component"].Value,
                match.Groups["target"].Value,
                match.Groups["path"].Value))
            .ToArray();
}
