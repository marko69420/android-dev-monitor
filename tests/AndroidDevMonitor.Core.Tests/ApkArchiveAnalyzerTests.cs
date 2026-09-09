using System.IO.Compression;
using AndroidDevMonitor.Core.Analysis;

namespace AndroidDevMonitor.Core.Tests;

public sealed class ApkArchiveAnalyzerTests
{
    [Fact]
    public void Analyzer_groups_apk_payload_and_detects_abis()
    {
        string path = CreateArchive(("classes.dex", 100), ("lib/arm64-v8a/libgame.so", 200), ("res/drawable/icon.png", 50));
        try
        {
            ApkArchiveSnapshot snapshot = ApkArchiveAnalyzer.Analyze(path);
            Assert.Contains("arm64-v8a", snapshot.Abis);
            Assert.Equal(100, snapshot.Sections.Single(item => item.Name == "DEX").UncompressedBytes);
            Assert.Equal(200, snapshot.Sections.Single(item => item.Name == "Native libraries").UncompressedBytes);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Comparison_reports_signed_section_growth()
    {
        string baselinePath = CreateArchive(("classes.dex", 100));
        string currentPath = CreateArchive(("classes.dex", 140));
        try
        {
            string report = ApkArchiveAnalyzer.Compare(ApkArchiveAnalyzer.Analyze(currentPath), ApkArchiveAnalyzer.Analyze(baselinePath));
            Assert.Contains("DEX\t140\t100\t+40", report);
        }
        finally { File.Delete(baselinePath); File.Delete(currentPath); }
    }

    private static string CreateArchive(params (string Path, int Size)[] entries)
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"adm-apk-{Guid.NewGuid():N}.apk");
        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach ((string entryPath, int size) in entries)
        {
            ZipArchiveEntry entry = archive.CreateEntry(entryPath, CompressionLevel.NoCompression);
            using Stream stream = entry.Open();
            stream.Write(new byte[size]);
        }
        return path;
    }
}
