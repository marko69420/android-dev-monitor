namespace AndroidDevMonitor.Adb.Execution;

/// <summary>What <see cref="ToolLocator"/> needs to know about the machine; replaceable in tests.</summary>
public sealed record ToolEnvironment(
    bool IsWindows,
    bool IsMacOS,
    Func<string, string?> GetVariable,
    string HomeDirectory,
    string LocalAppData,
    string ProgramFiles,
    Func<string, bool> FileExists,
    Func<string, IEnumerable<string>> ListDirectories)
{
    public static ToolEnvironment Current { get; } = new(
        OperatingSystem.IsWindows(),
        OperatingSystem.IsMacOS(),
        Environment.GetEnvironmentVariable,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        File.Exists,
        directory => Directory.Exists(directory) ? Directory.EnumerateDirectories(directory) : []);
}

/// <summary>
/// Finds Android SDK tools (adb, emulator, aapt) and helpers such as scrcpy on Windows, Linux and macOS,
/// using the same order everywhere: explicit path, ANDROID_SDK_ROOT / ANDROID_HOME, the platform's default
/// SDK location, well-known install folders, then PATH.
/// </summary>
public sealed class ToolLocator(ToolEnvironment environment)
{
    public static ToolLocator Default { get; } = new(ToolEnvironment.Current);

    /// <summary>The file name of a tool on this platform, for example adb.exe on Windows and adb elsewhere.</summary>
    public string ExecutableName(string tool) => environment.IsWindows ? tool + ".exe" : tool;

    /// <summary>Candidate Android SDK roots, most specific first.</summary>
    public IReadOnlyList<string> SdkRoots()
    {
        List<string> roots = [];
        foreach (string variable in new[] { "ANDROID_SDK_ROOT", "ANDROID_HOME" })
        {
            string? value = environment.GetVariable(variable);
            if (!string.IsNullOrWhiteSpace(value)) roots.Add(value.Trim());
        }
        if (environment.IsWindows)
            roots.Add(Path.Combine(environment.LocalAppData, "Android", "Sdk"));
        else if (environment.IsMacOS)
            roots.Add(Path.Combine(environment.HomeDirectory, "Library", "Android", "sdk"));
        else
        {
            roots.Add(Path.Combine(environment.HomeDirectory, "Android", "Sdk"));
            // Debian/Ubuntu "android-sdk" packages and manual installs.
            roots.Add("/usr/lib/android-sdk");
            roots.Add("/opt/android-sdk");
        }
        return roots.Distinct(environment.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
    }

    public string? FindAdb(string? configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && environment.FileExists(configuredPath))
            return Path.GetFullPath(configuredPath);
        return FindSdkTool("platform-tools", "adb");
    }

    /// <summary>A tool from an SDK sub-folder (platform-tools, emulator), falling back to PATH.</summary>
    public string? FindSdkTool(string folder, string tool)
    {
        string name = ExecutableName(tool);
        return SdkRoots().Select(root => Path.Combine(root, folder, name)).FirstOrDefault(environment.FileExists)
            ?? FindOnPath(tool);
    }

    /// <summary>A tool from the newest installed build-tools version (for example aapt).</summary>
    public string? FindBuildTool(string tool)
    {
        string name = ExecutableName(tool);
        return SdkRoots()
            .SelectMany(root => environment.ListDirectories(Path.Combine(root, "build-tools")))
            .Select(directory => (directory, version: ParseVersion(Path.GetFileName(directory))))
            .OrderByDescending(entry => entry.version)
            .Select(entry => Path.Combine(entry.directory, name))
            .FirstOrDefault(environment.FileExists);
    }

    public string? FindScrcpy()
    {
        string name = ExecutableName("scrcpy");
        string[] extra = environment.IsWindows
            ?
            [
                Path.Combine(environment.LocalAppData, "Programs", "scrcpy", name),
                Path.Combine(environment.ProgramFiles, "scrcpy", name),
                Path.Combine(environment.HomeDirectory, "scoop", "apps", "scrcpy", "current", name)
            ]
            : environment.IsMacOS
                ? ["/opt/homebrew/bin/scrcpy", "/usr/local/bin/scrcpy"]
                : ["/snap/bin/scrcpy", "/usr/local/bin/scrcpy", Path.Combine(environment.HomeDirectory, ".local", "bin", "scrcpy")];
        return FindOnPath("scrcpy") ?? extra.FirstOrDefault(environment.FileExists);
    }

    public string? FindOnPath(string tool)
    {
        string name = ExecutableName(tool);
        return (environment.GetVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim().Trim('"'), name))
            .FirstOrDefault(environment.FileExists);
    }

    private static Version ParseVersion(string text)
    {
        string numeric = new(text.TakeWhile(ch => char.IsDigit(ch) || ch == '.').ToArray());
        return Version.TryParse(numeric.Contains('.') ? numeric : numeric + ".0", out Version? version) ? version : new Version(0, 0);
    }
}
