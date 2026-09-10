using System.IO.Compression;
using System.Text;
using AndroidDevMonitor.Core.Analysis;

namespace AndroidDevMonitor.Core.Tests;

public sealed class AppBundleAnalyzerTests
{
    [Fact]
    public void Analyzer_reads_bundle_modules_abis_and_sections()
    {
        string path = Path.Combine(Path.GetTempPath(), $"adm-test-{Guid.NewGuid():N}.aab");
        try
        {
            using (FileStream stream = File.Create(path))
            using (ZipArchive archive = new(stream, ZipArchiveMode.Create))
            {
                Write(archive, "BundleConfig.pb", "bundle");
                Write(archive, "base/manifest/AndroidManifest.xml", "<manifest/>");
                Write(archive, "base/dex/classes.dex", "dex-data");
                Write(archive, "base/lib/arm64-v8a/libdemo.so", "native");
                Write(archive, "base/res/values/strings.xml", "resource");
                Write(archive, "feature/manifest/AndroidManifest.xml", "<manifest/>");
                Write(archive, "feature/dex/classes.dex", "dex-data");
                Write(archive, "META-INF/MANIFEST.MF", "signature");
            }

            AppBundleSnapshot snapshot = AppBundleAnalyzer.Analyze(path);

            Assert.True(snapshot.HasBundleConfig);
            Assert.Equal(2, snapshot.Modules.Count);
            AppBundleModule baseModule = snapshot.Modules.First(module => module.Name == "base");
            Assert.True(baseModule.HasManifest);
            Assert.Equal(new[] { "arm64-v8a" }, baseModule.Abis.ToArray());
            Assert.Contains(snapshot.Abis, abi => abi == "arm64-v8a");
            Assert.Contains(snapshot.Sections, section => section.Name == "base · DEX" && section.Files == 1);
            Assert.DoesNotContain(snapshot.Modules, module => module.Name == "META-INF");
            string report = snapshot.ToReport();
            Assert.Contains("base", report);
            Assert.Contains("feature", report);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Analyzer_distinguishes_apk_from_app_bundle()
    {
        Assert.False(AppBundleAnalyzer.IsAppBundle("C:/tmp/app-release.apk"));
        Assert.True(AppBundleAnalyzer.IsAppBundle("C:/tmp/app-release.aab"));
    }

    private static void Write(ZipArchive archive, string name, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name);
        using StreamWriter writer = new(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }
}
