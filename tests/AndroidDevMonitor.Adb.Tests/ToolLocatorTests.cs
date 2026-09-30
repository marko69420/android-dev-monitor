using AndroidDevMonitor.Adb.Execution;

namespace AndroidDevMonitor.Adb.Tests;

public sealed class ToolLocatorTests
{
    private static ToolLocator Locator(bool windows, bool mac, IEnumerable<string> files, IReadOnlyDictionary<string, string>? variables = null, IEnumerable<string>? directories = null)
    {
        HashSet<string> fileSet = new(files, StringComparer.Ordinal);
        List<string> directoryList = directories?.ToList() ?? [];
        return new ToolLocator(new ToolEnvironment(
            windows,
            mac,
            name => variables is not null && variables.TryGetValue(name, out string? value) ? value : null,
            HomeDirectory: windows ? "C:/Users/dev" : mac ? "/Users/dev" : "/home/dev",
            LocalAppData: "C:/Users/dev/AppData/Local",
            ProgramFiles: "C:/Program Files",
            fileSet.Contains,
            parent => directoryList.Where(directory => Path.GetDirectoryName(directory) == parent)));
    }

    [Fact]
    public void Linux_finds_adb_in_the_android_studio_default_sdk() =>
        Assert.Equal(Path.Combine("/home/dev", "Android", "Sdk", "platform-tools", "adb"),
            Locator(windows: false, mac: false, ["/home/dev/Android/Sdk/platform-tools/adb"]).FindAdb());

    [Fact]
    public void Linux_prefers_android_home_over_the_default_sdk()
    {
        ToolLocator locator = Locator(false, false,
            ["/home/dev/Android/Sdk/platform-tools/adb", "/opt/sdk/platform-tools/adb"],
            new Dictionary<string, string> { ["ANDROID_HOME"] = "/opt/sdk" });
        Assert.Equal(Path.Combine("/opt/sdk", "platform-tools", "adb"), locator.FindAdb());
    }

    [Fact]
    public void Linux_falls_back_to_adb_on_path() =>
        Assert.Equal(Path.Combine("/usr/bin", "adb"),
            Locator(false, false, ["/usr/bin/adb"], new Dictionary<string, string> { ["PATH"] = "/usr/local/bin:/usr/bin" }).FindAdb());

    [Fact]
    public void Linux_finds_the_debian_platform_tools_package() =>
        Assert.Equal(Path.Combine("/usr/lib/android-sdk", "platform-tools", "adb"),
            Locator(false, false, ["/usr/lib/android-sdk/platform-tools/adb"]).FindAdb());

    [Fact]
    public void Windows_uses_exe_names_and_the_local_app_data_sdk()
    {
        ToolLocator locator = Locator(true, false, ["C:/Users/dev/AppData/Local/Android/Sdk/platform-tools/adb.exe"]);
        Assert.Equal("adb.exe", locator.ExecutableName("adb"));
        Assert.Equal(Path.Combine("C:/Users/dev/AppData/Local", "Android", "Sdk", "platform-tools", "adb.exe"), locator.FindAdb());
    }

    [Fact]
    public void MacOS_uses_the_library_sdk_location() =>
        Assert.Equal(Path.Combine("/Users/dev", "Library", "Android", "sdk", "emulator", "emulator"),
            Locator(false, true, ["/Users/dev/Library/Android/sdk/emulator/emulator"]).FindSdkTool("emulator", "emulator"));

    [Fact]
    public void Configured_adb_path_wins_when_it_exists() =>
        Assert.EndsWith("custom-adb", Locator(false, false, ["/tools/custom-adb", "/home/dev/Android/Sdk/platform-tools/adb"]).FindAdb("/tools/custom-adb"));

    [Fact]
    public void Missing_configured_path_falls_back_to_the_sdk() =>
        Assert.Equal(Path.Combine("/home/dev", "Android", "Sdk", "platform-tools", "adb"),
            Locator(false, false, ["/home/dev/Android/Sdk/platform-tools/adb"]).FindAdb("/nope/adb"));

    [Fact]
    public void Build_tools_pick_the_highest_version_numerically()
    {
        string root = "/home/dev/Android/Sdk/build-tools";
        ToolLocator locator = Locator(false, false,
            [$"{root}/9.0.0/aapt", $"{root}/35.0.0/aapt", $"{root}/34.0.0-rc1/aapt"],
            directories: [$"{root}/9.0.0", $"{root}/35.0.0", $"{root}/34.0.0-rc1"]);
        Assert.Equal(Path.Combine($"{root}/35.0.0", "aapt"), locator.FindBuildTool("aapt"));
    }

    [Fact]
    public void Scrcpy_is_found_in_snap_on_linux() =>
        Assert.Equal("/snap/bin/scrcpy", Locator(false, false, ["/snap/bin/scrcpy"]).FindScrcpy());

    [Fact]
    public void Nothing_installed_returns_null()
    {
        ToolLocator locator = Locator(false, false, []);
        Assert.Null(locator.FindAdb());
        Assert.Null(locator.FindScrcpy());
        Assert.Null(locator.FindBuildTool("aapt"));
    }
}
