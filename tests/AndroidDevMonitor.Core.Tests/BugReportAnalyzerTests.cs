using System.IO.Compression;
using System.Text;
using AndroidDevMonitor.Core.Analysis;

namespace AndroidDevMonitor.Core.Tests;

public sealed class BugReportAnalyzerTests
{
    [Fact]
    public async Task Analyzer_classifies_high_signal_events()
    {
        const string report = "01 FATAL EXCEPTION: main\n02 ANR in com.example\n03 lmkd killed process\n04 StrictMode policy violation\n05 thermal throttling\n";
        await using MemoryStream stream = new(Encoding.UTF8.GetBytes(report));
        BugReportAnalysis result = await BugReportAnalyzer.AnalyzeStreamAsync(stream, "memory", CancellationToken.None);
        Assert.Equal(5, result.LinesScanned);
        Assert.Equal(1, result.CrashCount);
        Assert.Equal(1, result.AnrCount);
        Assert.Equal(1, result.LowMemoryCount);
        Assert.Equal(1, result.StrictModeCount);
        Assert.Equal(1, result.ThermalCount);
    }

    [Fact]
    public async Task Analyzer_reads_primary_bugreport_from_zip()
    {
        string path = Path.Combine(Path.GetTempPath(), $"adm-test-{Guid.NewGuid():N}.zip");
        try
        {
            using (ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                ZipArchiveEntry entry = archive.CreateEntry("bugreport-device.txt");
                await using StreamWriter writer = new(entry.Open());
                await writer.WriteLineAsync("Watchdog detected system_server");
            }
            BugReportAnalysis result = await BugReportAnalyzer.AnalyzeAsync(path, CancellationToken.None);
            Assert.Equal(1, result.WatchdogCount);
            Assert.Contains("bugreport-device.txt", result.Source);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task Analyzer_reads_multiple_high_signal_zip_sources()
    {
        string path = Path.Combine(Path.GetTempPath(), $"adm-test-{Guid.NewGuid():N}.zip");
        try
        {
            using (ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                await WriteEntryAsync(archive, "bugreport-device.txt", "ANR in com.example");
                await WriteEntryAsync(archive, "FS/data/misc/tombstones/tombstone_00", "Fatal signal 11");
                await WriteEntryAsync(archive, "FS/data/system/dropbox/system_app_strictmode.txt", "StrictMode policy violation");
            }

            BugReportAnalysis result = await BugReportAnalyzer.AnalyzeAsync(path, CancellationToken.None);
            Assert.Equal(3, result.FilesScanned);
            Assert.Equal(1, result.AnrCount);
            Assert.Equal(1, result.CrashCount);
            Assert.Equal(1, result.StrictModeCount);
            Assert.Contains(result.Findings, finding => finding.Text.Contains("tombstone_00", StringComparison.Ordinal));
            Assert.Contains("3 files", result.Source, StringComparison.Ordinal);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static async Task WriteEntryAsync(ZipArchive archive, string name, string text)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name);
        await using StreamWriter writer = new(entry.Open());
        await writer.WriteLineAsync(text);
    }
}
