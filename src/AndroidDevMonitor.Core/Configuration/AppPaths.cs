namespace AndroidDevMonitor.Core.Configuration;

/// <summary>
/// Where the app keeps its data and exports. Environment.GetFolderPath returns an empty string on Linux when a folder
/// such as ~/.local/share or ~/Documents does not exist yet, which would silently put files in the working directory,
/// so every path here falls back to a folder under the home directory.
/// </summary>
public static class AppPaths
{
    /// <summary>Overrides the data directory, for portable installs and tests.</summary>
    public const string DataDirectoryVariable = "ANDROID_DEV_MONITOR_DATA";

    private static string? _dataDirectory;

    public static string HomeDirectory =>
        NonEmpty(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify))
        ?? NonEmpty(Environment.GetEnvironmentVariable("HOME"))
        ?? Path.GetTempPath();

    /// <summary>%LOCALAPPDATA% on Windows, $XDG_DATA_HOME or ~/.local/share on Linux, ~/Library/Application Support on macOS.</summary>
    public static string LocalDataRoot =>
        NonEmpty(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify))
        ?? Path.Combine(HomeDirectory, ".local", "share");

    /// <summary>Sessions database, media and logs.</summary>
    public static string DataDirectory
    {
        get => _dataDirectory ??= NonEmpty(Environment.GetEnvironmentVariable(DataDirectoryVariable)) is { } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(LocalDataRoot, "AndroidDevMonitor");
        set => _dataDirectory = Path.GetFullPath(value);
    }

    /// <summary>The user's Documents folder (XDG_DOCUMENTS_DIR on Linux), or the home directory when there is none.</summary>
    public static string DocumentsDirectory =>
        NonEmpty(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.DoNotVerify)) is { } documents &&
        (OperatingSystem.IsWindows() || Directory.Exists(documents))
            ? documents
            : HomeDirectory;

    /// <summary>Documents/Android Dev Monitor, the root for exports and reports.</summary>
    public static string UserFilesDirectory => Path.Combine(DocumentsDirectory, "Android Dev Monitor");

    public static string ExportsDirectory => Path.Combine(UserFilesDirectory, "Exports");

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
