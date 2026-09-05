using System.Text.RegularExpressions;

namespace AndroidDevMonitor.Adb.Parsers;

public static class ForegroundParser
{
    public static string? Parse(string output)
    {
        foreach (var field in new[] { "topResumedActivity", "mResumedActivity" })
        {
            var match = Regex.Match(output, field + @"\s*[:=][^\r\n]*?\s(?<package>[A-Za-z][\w]*(?:\.[\w]+)+)/");
            if (match.Success) return match.Groups["package"].Value;
        }
        return null;
    }
}
