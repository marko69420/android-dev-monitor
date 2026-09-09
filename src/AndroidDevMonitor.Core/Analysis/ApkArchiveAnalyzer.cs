using System.IO.Compression;
using System.Text;

namespace AndroidDevMonitor.Core.Analysis;

public sealed record ApkSection(string Name, int Files, long UncompressedBytes, long CompressedBytes);

public sealed record ApkArchiveSnapshot(string Path, long FileBytes, IReadOnlyList<ApkSection> Sections, IReadOnlyList<string> Abis)
{
    public string ToReport()
    {
        StringBuilder report = new();
        report.AppendLine($"APK: {Path}");
        report.AppendLine($"Download/file size: {FileBytes:N0} bytes");
        report.AppendLine($"Native ABIs: {(Abis.Count == 0 ? "none" : string.Join(", ", Abis))}");
        report.AppendLine();
        report.AppendLine("SECTION\tFILES\tUNCOMPRESSED BYTES\tCOMPRESSED BYTES");
        foreach (ApkSection section in Sections) report.AppendLine($"{section.Name}\t{section.Files}\t{section.UncompressedBytes}\t{section.CompressedBytes}");
        return report.ToString();
    }
}

public static class ApkArchiveAnalyzer
{
    public static ApkArchiveSnapshot Analyze(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("APK was not found.", path);
        using ZipArchive archive = ZipFile.OpenRead(path);
        ApkSection[] sections = archive.Entries
            .Where(entry => !string.IsNullOrEmpty(entry.Name))
            .GroupBy(entry => Category(entry.FullName), StringComparer.Ordinal)
            .Select(group => new ApkSection(group.Key, group.Count(), group.Sum(entry => entry.Length), group.Sum(entry => entry.CompressedLength)))
            .OrderByDescending(section => section.UncompressedBytes)
            .ToArray();
        string[] abis = archive.Entries
            .Select(entry => entry.FullName.Replace('\\', '/').Split('/'))
            .Where(parts => parts.Length >= 3 && parts[0] == "lib")
            .Select(parts => parts[1])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new(path, new FileInfo(path).Length, sections, abis);
    }

    public static string Compare(ApkArchiveSnapshot current, ApkArchiveSnapshot baseline)
    {
        StringBuilder report = new();
        report.AppendLine("APK BUILD COMPARISON");
        report.AppendLine($"Current:  {current.Path}");
        report.AppendLine($"Baseline: {baseline.Path}");
        report.AppendLine($"File size delta: {Signed(current.FileBytes - baseline.FileBytes)} bytes");
        report.AppendLine();
        report.AppendLine("SECTION\tCURRENT\tBASELINE\tDELTA");
        string[] names = current.Sections.Select(item => item.Name).Union(baseline.Sections.Select(item => item.Name), StringComparer.Ordinal).Order().ToArray();
        foreach (string name in names)
        {
            long now = current.Sections.FirstOrDefault(item => item.Name == name)?.UncompressedBytes ?? 0;
            long before = baseline.Sections.FirstOrDefault(item => item.Name == name)?.UncompressedBytes ?? 0;
            report.AppendLine($"{name}\t{now}\t{before}\t{Signed(now - before)}");
        }
        return report.ToString();
    }

    private static string Category(string path)
    {
        string normalized = path.Replace('\\', '/');
        if (normalized.EndsWith(".dex", StringComparison.OrdinalIgnoreCase)) return "DEX";
        if (normalized.StartsWith("lib/", StringComparison.OrdinalIgnoreCase)) return "Native libraries";
        if (normalized.StartsWith("res/", StringComparison.OrdinalIgnoreCase) || normalized == "resources.arsc") return "Resources";
        if (normalized.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)) return "Assets";
        if (normalized.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)) return "Signing metadata";
        if (normalized.Equals("AndroidManifest.xml", StringComparison.OrdinalIgnoreCase)) return "Manifest";
        return "Other";
    }

    private static string Signed(long value) => value.ToString("+#;-#;0", System.Globalization.CultureInfo.InvariantCulture);
}
