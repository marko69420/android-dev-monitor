using System.IO.Compression;
using System.Text;

namespace AndroidDevMonitor.Core.Analysis;

public sealed record AppBundleModule(string Name, int Files, long UncompressedBytes, long CompressedBytes, bool HasManifest, IReadOnlyList<string> Abis);

public sealed record AppBundleSnapshot(
    string Path,
    long FileBytes,
    bool HasBundleConfig,
    IReadOnlyList<AppBundleModule> Modules,
    IReadOnlyList<ApkSection> Sections,
    IReadOnlyList<string> Abis)
{
    public string ToReport()
    {
        StringBuilder report = new();
        report.AppendLine($"App bundle (AAB): {Path}");
        report.AppendLine($"File size: {FileBytes:N0} bytes");
        report.AppendLine($"BundleConfig.pb present: {(HasBundleConfig ? "yes" : "no")}");
        report.AppendLine($"Native ABIs: {(Abis.Count == 0 ? "none" : string.Join(", ", Abis))}");
        report.AppendLine();
        report.AppendLine("MODULE\tFILES\tUNCOMPRESSED BYTES\tCOMPRESSED BYTES\tMANIFEST\tABIS");
        foreach (AppBundleModule module in Modules)
        {
            string abis = module.Abis.Count == 0 ? "none" : string.Join(",", module.Abis);
            report.AppendLine($"{module.Name}\t{module.Files}\t{module.UncompressedBytes}\t{module.CompressedBytes}\t{(module.HasManifest ? "yes" : "no")}\t{abis}");
        }
        report.AppendLine();
        report.AppendLine("SECTION\tFILES\tUNCOMPRESSED BYTES\tCOMPRESSED BYTES");
        foreach (ApkSection section in Sections) report.AppendLine($"{section.Name}\t{section.Files}\t{section.UncompressedBytes}\t{section.CompressedBytes}");
        return report.ToString();
    }
}

public static class AppBundleAnalyzer
{
    private static readonly string[] NonModulePrefixes = ["META-INF", "BUNDLE-METADATA"];

    public static bool IsAppBundle(string path) =>
        Path.GetExtension(path).Equals(".aab", StringComparison.OrdinalIgnoreCase);

    public static AppBundleSnapshot Analyze(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("App bundle was not found.", path);
        using ZipArchive archive = ZipFile.OpenRead(path);
        ZipArchiveEntry[] entries = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToArray();

        bool hasBundleConfig = entries.Any(entry =>
            entry.FullName.Equals("BundleConfig.pb", StringComparison.OrdinalIgnoreCase));

        string[] moduleNames = entries
            .Select(entry => entry.FullName.Replace('\\', '/'))
            .Select(normalized => normalized.Split('/'))
            .Where(parts => parts.Length >= 2 && !NonModulePrefixes.Contains(parts[0], StringComparer.OrdinalIgnoreCase))
            .Select(parts => parts[0])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Prefer real bundle module roots; fall back to any top-level folder for hand-made archives.
        string[] roots = moduleNames
            .Where(name => entries.Any(entry =>
            {
                string normalized = entry.FullName.Replace('\\', '/');
                return normalized.Equals($"{name}/manifest/AndroidManifest.xml", StringComparison.OrdinalIgnoreCase) ||
                       normalized.Equals($"{name}/AndroidManifest.xml", StringComparison.OrdinalIgnoreCase);
            }))
            .ToArray();
        if (roots.Length == 0) roots = moduleNames;

        List<AppBundleModule> modules = [];
        List<ApkSection> sections = [];
        foreach (string root in roots.OrderBy(name => name.Equals("base", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(name => name, StringComparer.Ordinal))
        {
            ZipArchiveEntry[] owned = entries
                .Where(entry => entry.FullName.Replace('\\', '/').StartsWith(root + "/", StringComparison.Ordinal))
                .ToArray();
            bool hasManifest = owned.Any(entry =>
            {
                string normalized = entry.FullName.Replace('\\', '/');
                return normalized.EndsWith("/manifest/AndroidManifest.xml", StringComparison.OrdinalIgnoreCase) ||
                       normalized.Equals($"{root}/AndroidManifest.xml", StringComparison.OrdinalIgnoreCase);
            });
            string[] abis = owned
                .Select(entry => entry.FullName.Replace('\\', '/').Split('/'))
                .Where(parts => parts.Length >= 4 && parts[1].Equals("lib", StringComparison.OrdinalIgnoreCase))
                .Select(parts => parts[2])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            modules.Add(new(root, owned.Length, owned.Sum(entry => entry.Length), owned.Sum(entry => entry.CompressedLength), hasManifest, abis));

            foreach (IGrouping<string, ZipArchiveEntry> group in owned.GroupBy(entry => Category(entry.FullName.Replace('\\', '/'), root), StringComparer.Ordinal))
            {
                sections.Add(new(
                    $"{root} · {group.Key}",
                    group.Count(),
                    group.Sum(entry => entry.Length),
                    group.Sum(entry => entry.CompressedLength)));
            }
        }

        string[] allAbis = entries
            .Select(entry => entry.FullName.Replace('\\', '/').Split('/'))
            .Where(parts => parts.Length >= 3 && parts.Contains("lib", StringComparer.OrdinalIgnoreCase))
            .Select(parts => parts[Array.FindIndex(parts, part => part.Equals("lib", StringComparison.OrdinalIgnoreCase)) + 1])
            .Where(abi => !string.IsNullOrWhiteSpace(abi))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        ApkSection[] ordered = sections
            .GroupBy(section => section.Name, StringComparer.Ordinal)
            .Select(group => new ApkSection(group.Key, group.Sum(item => item.Files), group.Sum(item => item.UncompressedBytes), group.Sum(item => item.CompressedBytes)))
            .OrderByDescending(section => section.UncompressedBytes)
            .ToArray();

        return new(path, new FileInfo(path).Length, hasBundleConfig, modules, ordered, allAbis);
    }

    private static string Category(string normalized, string module)
    {
        string remainder = normalized.StartsWith(module + "/", StringComparison.Ordinal) ? normalized[(module.Length + 1)..] : normalized;
        if (remainder.EndsWith(".dex", StringComparison.OrdinalIgnoreCase)) return "DEX";
        if (remainder.StartsWith("lib/", StringComparison.OrdinalIgnoreCase)) return "Native libraries";
        if (remainder.StartsWith("manifest/", StringComparison.OrdinalIgnoreCase) || remainder.Equals("AndroidManifest.xml", StringComparison.OrdinalIgnoreCase)) return "Manifest";
        if (remainder.StartsWith("res/", StringComparison.OrdinalIgnoreCase) || remainder.Equals("resources.pb", StringComparison.OrdinalIgnoreCase)) return "Resources";
        if (remainder.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)) return "Assets";
        if (remainder.StartsWith("root/", StringComparison.OrdinalIgnoreCase)) return "Root files";
        if (remainder.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)) return "Signing metadata";
        if (remainder.EndsWith(".pb", StringComparison.OrdinalIgnoreCase)) return "Bundle metadata";
        return "Other";
    }
}
