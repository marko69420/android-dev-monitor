using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace AndroidDevMonitor.Presentation.Platform;

/// <summary>
/// Opens files and folders, reveals files in the file manager, and manages start-with-system on Windows, Linux and macOS.
/// </summary>
public sealed class DesktopShell : IDesktopShell
{
    public const string AppId = "AndroidDevMonitor";
    private readonly string _autostartDirectory;

    public DesktopShell(string? autostartDirectory = null)
    {
        string configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        _autostartDirectory = autostartDirectory ?? Path.Combine(configHome, "autostart");
    }

    public string AutostartEntryPath => Path.Combine(_autostartDirectory, AppId + ".desktop");

    public void Open(string path)
    {
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else
            Launch(OperatingSystem.IsMacOS() ? "open" : "xdg-open", path);
    }

    public void Reveal(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { path } });
            return;
        }
        if (OperatingSystem.IsMacOS())
        {
            if (File.Exists(path)) Launch("open", "-R", path);
            else Launch("open", path);
            return;
        }
        // Most Linux file managers (Nautilus, Dolphin, Nemo, Thunar, Caja) implement the freedesktop FileManager1
        // interface, which opens the folder with the file selected. Fall back to opening the folder.
        if (File.Exists(path) &&
            TryRun("dbus-send", "--session", "--print-reply", "--dest=org.freedesktop.FileManager1", "--type=method_call",
                "/org/freedesktop/FileManager1", "org.freedesktop.FileManager1.ShowItems",
                "array:string:" + new Uri(Path.GetFullPath(path)).AbsoluteUri, "string:"))
            return;
        Launch("xdg-open", File.Exists(path) ? Path.GetDirectoryName(Path.GetFullPath(path)) ?? path : path);
    }

    public void SetStartWithSystem(bool enabled)
    {
        if (OperatingSystem.IsWindows())
        {
            SetWindowsRunKey(enabled);
            return;
        }
        if (OperatingSystem.IsMacOS()) return;

        if (!enabled)
        {
            if (File.Exists(AutostartEntryPath)) File.Delete(AutostartEntryPath);
            return;
        }
        if (string.IsNullOrWhiteSpace(Environment.ProcessPath)) return;
        Directory.CreateDirectory(_autostartDirectory);
        File.WriteAllText(AutostartEntryPath, CreateAutostartEntry(LaunchCommand()));
    }

    public bool StartNewInstance(IEnumerable<string> arguments)
    {
        string? executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) return false;
        ProcessStartInfo start = new(executable)
        {
            UseShellExecute = OperatingSystem.IsWindows(),
            WorkingDirectory = AppContext.BaseDirectory
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            // Started as "dotnet AndroidDevMonitor.dll": the managed entry point is the first command-line argument.
            string? managedEntryPoint = Environment.GetCommandLineArgs().FirstOrDefault();
            if (string.IsNullOrWhiteSpace(managedEntryPoint) || !File.Exists(managedEntryPoint)) return false;
            start.ArgumentList.Add(managedEntryPoint);
        }
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) is not null;
    }

    public static string CreateAutostartEntry(string command) =>
        "[Desktop Entry]\n" +
        "Type=Application\n" +
        "Name=Android Dev Monitor\n" +
        "Comment=Android device performance and ADB monitor\n" +
        $"Exec={command}\n" +
        "Terminal=false\n" +
        "X-GNOME-Autostart-enabled=true\n";

    /// <summary>The Exec line for this process, quoted per the desktop entry specification.</summary>
    private static string LaunchCommand()
    {
        string executable = Environment.ProcessPath!;
        List<string> parts = [executable];
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase) &&
            Environment.GetCommandLineArgs().FirstOrDefault() is { } entry && File.Exists(entry))
            parts.Add(Path.GetFullPath(entry));
        return string.Join(' ', parts.Select(QuoteExecArgument));
    }

    public static string QuoteExecArgument(string value) =>
        value.Any(ch => char.IsWhiteSpace(ch) || "\"'\\`$;&|<>()*?#~".Contains(ch))
            ? "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$") + "\""
            : value;

    [SupportedOSPlatform("windows")]
    private static void SetWindowsRunKey(bool enabled)
    {
        const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(runKey, writable: true) ??
                                 Registry.CurrentUser.CreateSubKey(runKey, writable: true);
        if (key is null) return;
        if (enabled && !string.IsNullOrWhiteSpace(Environment.ProcessPath))
            key.SetValue(AppId, $"\"{Environment.ProcessPath}\"");
        else
            key.DeleteValue(AppId, throwOnMissingValue: false);
    }

    private static void Launch(string fileName, params string[] arguments)
    {
        ProcessStartInfo start = new(fileName) { UseShellExecute = false };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process? process = Process.Start(start);
    }

    private static bool TryRun(string fileName, params string[] arguments)
    {
        try
        {
            ProcessStartInfo start = new(fileName)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            using Process? process = Process.Start(start);
            if (process is null) return false;
            if (!process.WaitForExit(3000))
            {
                try { process.Kill(); } catch { }
                return false;
            }
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
