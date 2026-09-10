#nullable enable
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AndroidDevMonitor.Core.Analysis;
using AndroidDevMonitor.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AndroidDevMonitor.App.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty] private string _apkPath = "";
    [ObservableProperty] private string _inputTextToSend = "";
    [ObservableProperty] private string _devicePropertiesSummary = "Run \"Diff device properties\" to save a baseline, then again after a build or reboot to see exactly what changed.";
    [ObservableProperty] private string _screenshotDiffSummary = "Compare two PNG screenshots pixel by pixel to see exactly where the UI moved or changed.";
    [ObservableProperty] private string _appDataFile = "";
    [ObservableProperty] private string _appDataSummary = "List, then pull single files, from a debuggable app's private data directory over run-as.";

    [RelayCommand]
    private void PickApkFile()
    {
        OpenFileDialog dialog = new()
        {
            Title = "Select an APK to install",
            Filter = "Android package (*.apk)|*.apk|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() == true) ApkPath = dialog.FileName;
    }

    /// <summary>Installs or replaces a local APK with adb install -r, keeping existing app data.</summary>
    [RelayCommand]
    private async Task InstallApkAsync()
    {
        AndroidDevice? device = SelectedDevice;
        if (device is null) { DeviceLabStatus = "Select a connected device first."; return; }
        if (string.IsNullOrWhiteSpace(ApkPath) || !File.Exists(ApkPath)) { DeviceLabStatus = "Choose an APK file first with Browse."; return; }

        string name = Path.GetFileName(ApkPath);
        DeviceLabStatus = $"Installing {name} on {device.FriendlyName}…";
        AdbCommandResult result = await _adb.ExecuteAsync(device.Serial,
            ["install", "-r", "-t", ApkPath], TimeSpan.FromMinutes(5), CancellationToken.None);
        DeviceLabOutput = (result.StandardOutput + result.StandardError).Trim();
        DeviceLabStatus = result.Success
            ? $"Installed {name} on {device.FriendlyName}. Existing app data was kept."
            : "Install failed: " + CleanError(result);
    }

    [RelayCommand]
    private async Task UninstallAppAsync()
    {
        if (!TryGetDeviceAndPackage(out AndroidDevice device, out string package)) return;
        if (!_dialogs.Confirm("Uninstall application", $"Uninstall {package} from {device.FriendlyName}?\n\nThis removes the app and all of its data from that device.")) return;
        AdbCommandResult result = await _adb.ExecuteAsync(device.Serial, ["uninstall", package], TimeSpan.FromMinutes(1), CancellationToken.None);
        DeviceLabOutput = (result.StandardOutput + result.StandardError).Trim();
        DeviceLabStatus = result.Success ? $"Uninstalled {package}." : "Uninstall failed: " + CleanError(result);
    }

    [RelayCommand]
    private async Task ClearAppDataAsync()
    {
        if (!TryGetDeviceAndPackage(out AndroidDevice device, out string package)) return;
        if (!_dialogs.Confirm("Clear app data", $"Delete all private data, databases and caches of {package} on {device.FriendlyName}?")) return;
        AdbCommandResult result = await _adb.ExecuteAsync(device.Serial, ["shell", "pm", "clear", package], TimeSpan.FromSeconds(30), CancellationToken.None);
        DeviceLabOutput = (result.StandardOutput + result.StandardError).Trim();
        DeviceLabStatus = result.Success ? $"Cleared all data and caches for {package}." : "Clear data failed: " + CleanError(result);
    }

    [RelayCommand]
    private async Task ForceStopAppAsync()
    {
        if (!TryGetDeviceAndPackage(out AndroidDevice device, out string package)) return;
        AdbCommandResult result = await _adb.ExecuteAsync(device.Serial, ["shell", "am", "force-stop", package], TimeSpan.FromSeconds(15), CancellationToken.None);
        DeviceLabStatus = result.Success ? $"Force-stopped {package}. Background work is now cancelled." : "Force stop failed: " + CleanError(result);
    }

    /// <summary>Force-stops the package, then relaunches its launcher activity so the next run is a true cold start.</summary>
    [RelayCommand]
    private async Task RestartAppAsync()
    {
        if (!TryGetDeviceAndPackage(out AndroidDevice device, out string package)) return;
        _ = await _adb.ExecuteAsync(device.Serial, ["shell", "am", "force-stop", package], TimeSpan.FromSeconds(15), CancellationToken.None);
        AdbCommandResult launch = await _adb.ExecuteAsync(device.Serial,
            ["shell", "monkey", "-p", package, "-c", "android.intent.category.LAUNCHER", "1"], TimeSpan.FromSeconds(30), CancellationToken.None);
        DeviceLabStatus = launch.Success
            ? $"Restarted {package} cold. Watch start-up and jank in the Performance workspace."
            : "Restart failed: " + CleanError(launch);
    }

    /// <summary>Types text into whatever field currently has focus on the device. Spaces travel as %s.</summary>
    [RelayCommand]
    private async Task SendInputTextAsync()
    {
        AndroidDevice? device = SelectedDevice;
        if (device is null) { DeviceLabStatus = "Select a connected device first."; return; }
        string text = InputTextToSend;
        if (string.IsNullOrEmpty(text)) { DeviceLabStatus = "Type the text you want to send first."; return; }

        string escaped = text.Replace("%", "%%").Replace(" ", "%s");
        AdbCommandResult result = await _adb.ExecuteAsync(device.Serial, ["shell", "input", "text", escaped], TimeSpan.FromSeconds(15), CancellationToken.None);
        DeviceLabStatus = result.Success ? "Text sent to the focused field on the device." : "Sending text failed: " + CleanError(result);
    }

    /// <summary>
    /// Saves a sorted getprop snapshot per device. The first run writes the baseline; every later run
    /// diffs against it and reports added, removed and changed properties. Useful after flashing a build.
    /// </summary>
    [RelayCommand]
    private async Task CompareDevicePropertiesAsync()
    {
        AndroidDevice? device = SelectedDevice;
        if (device is null) { DevicePropertiesSummary = "Select a connected device first."; return; }

        AdbCommandResult result = await _adb.ExecuteAsync(device.Serial, ["shell", "getprop"], TimeSpan.FromSeconds(30), CancellationToken.None);
        if (!result.Success) { DevicePropertiesSummary = "Reading device properties failed: " + CleanError(result); return; }

        string[] current = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToArray();
        if (current.Length == 0) { DevicePropertiesSummary = "The device returned no properties."; return; }

        Directory.CreateDirectory(DeveloperLabDirectory);
        string serial = device.Serial.Replace(':', '-');
        string baselinePath = Path.Combine(DeveloperLabDirectory, $"device-properties-{serial}.txt");
        if (!File.Exists(baselinePath))
        {
            await File.WriteAllLinesAsync(baselinePath, current, CancellationToken.None);
            DevicePropertiesSummary = $"Baseline saved for {device.FriendlyName}: {current.Length} properties.\nRun this again after a reboot, image flash or settings change to see the difference.";
            return;
        }

        string[] previous = (await File.ReadAllLinesAsync(baselinePath, CancellationToken.None))
            .Where(line => line.Length > 0)
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToArray();
        Dictionary<string, string> before = ToPropertyMap(previous);
        Dictionary<string, string> after = ToPropertyMap(current);

        List<string> added = after.Keys.Except(before.Keys, StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal).ToList();
        List<string> removed = before.Keys.Except(after.Keys, StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal).ToList();
        List<string> changed = before.Keys.Intersect(after.Keys, StringComparer.Ordinal)
            .Where(key => !string.Equals(before[key], after[key], StringComparison.Ordinal))
            .OrderBy(key => key, StringComparer.Ordinal).ToList();

        StringBuilder report = new();
        report.AppendLine("DEVICE PROPERTIES DIFF");
        report.AppendLine($"Device: {device.FriendlyName} ({device.Serial})");
        report.AppendLine($"Compared: {DateTimeOffset.Now:O}");
        report.AppendLine($"Added {added.Count} · removed {removed.Count} · changed {changed.Count}");
        report.AppendLine();
        foreach (string line in added) report.AppendLine("+ " + line);
        foreach (string line in removed) report.AppendLine("- " + line);
        foreach (string key in changed) report.AppendLine($"~ {key}\n    before: {before[key]}\n    after:  {after[key]}");

        string reportPath = await SaveStandaloneTextAsync($"device-properties-diff-{serial}", report.ToString());
        if (added.Count + removed.Count + changed.Count == 0)
        {
            DevicePropertiesSummary = $"No property changed since the saved baseline ({current.Length} properties compared).\nBaseline: {baselinePath}";
            return;
        }

        DevicePropertiesSummary = $"{added.Count} added · {removed.Count} removed · {changed.Count} changed since the baseline.\nReport: {reportPath}";
        DeviceLabOutput = report.ToString();
    }

    private static Dictionary<string, string> ToPropertyMap(IEnumerable<string> lines)
    {
        Dictionary<string, string> map = new(StringComparer.Ordinal);
        foreach (string line in lines)
        {
            int split = line.IndexOf("]: [", StringComparison.Ordinal);
            if (split <= 0) continue;
            string key = line[1..split];
            string value = line[(split + 4)..].TrimEnd(']');
            map[key] = value;
        }
        return map;
    }

    /// <summary>
    /// Compares the two newest screenshots of the selected device pixel by pixel and reports where the UI changed.
    /// </summary>
    [RelayCommand]
    private async Task CompareLatestScreenshotsAsync()
    {
        MediaItem[] shots = MediaItems
            .Where(item => item.Kind == MediaKind.Screenshot && File.Exists(item.LocalPath))
            .OrderByDescending(item => item.CapturedUtc)
            .Take(2)
            .ToArray();
        if (shots.Length < 2)
        {
            ScreenshotDiffSummary = "Take at least two screenshots first (Screenshot button in the toolbar or fleet Screenshot all), then run this again.";
            return;
        }

        try
        {
            (byte[] beforePixels, int beforeWidth, int beforeHeight) = LoadBgra(shots[1].LocalPath);
            (byte[] afterPixels, int afterWidth, int afterHeight) = LoadBgra(shots[0].LocalPath);
            if (beforeWidth != afterWidth || beforeHeight != afterHeight)
            {
                ScreenshotDiffSummary = $"The two screenshots have different sizes ({beforeWidth}x{beforeHeight} vs {afterWidth}x{afterHeight}). Rotation or a resolution change makes a pixel diff meaningless; capture both frames in the same orientation.";
                return;
            }

            ScreenshotDiff diff = ScreenshotComparer.Compare(beforeWidth, beforeHeight, beforePixels, afterPixels);
            StringBuilder report = new();
            report.AppendLine("SCREENSHOT PIXEL DIFF");
            report.AppendLine($"Device:   {shots[0].FriendlyDeviceName} ({shots[0].DeviceSerial})");
            report.AppendLine($"Baseline: {shots[1].FileName} · {shots[1].CapturedUtc:g}");
            report.AppendLine($"Current:  {shots[0].FileName} · {shots[0].CapturedUtc:g}");
            report.AppendLine($"Compared: {DateTimeOffset.Now:O}");
            report.AppendLine();
            report.Append(diff.ToReport());

            string reportPath = await SaveStandaloneTextAsync("screenshot-diff", report.ToString());
            TestArtifacts.Add(reportPath);
            ScreenshotDiffSummary = diff.AnyChange
                ? $"{diff.ChangedPercent:0.00}% of pixels changed ({diff.ChangedPixels:N0}) · max channel delta {diff.MaxChannelDelta} · area {diff.BoundingBox}\nReport: {reportPath}"
                : $"No visual change above the noise threshold ({diff.ComparedPixels:N0} pixels compared).\nReport: {reportPath}";
            DeviceLabOutput = report.ToString();
        }
        catch (Exception ex)
        {
            ScreenshotDiffSummary = "Comparing screenshots failed: " + ex.Message;
        }
    }

    private static (byte[] Pixels, int Width, int Height) LoadBgra(string path)
    {
        BitmapImage image = new();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();

        FormatConvertedBitmap converted = new(image, PixelFormats.Bgra32, null, 0);
        int width = converted.PixelWidth;
        int height = converted.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);
        return (pixels, width, height);
    }

    /// <summary>Lists a directory inside the app's private data area over run-as (debuggable builds only).</summary>
    [RelayCommand]
    private async Task ListAppDataAsync()
    {
        if (!TryGetDeviceAndPackage(out AndroidDevice device, out string package)) return;
        if (!TryResolveAppDataPath(out string relative, requireFile: false)) return;

        string target = relative.Length == 0 ? "." : relative;
        AdbCommandResult result = await _adb.ExecuteAsync(device.Serial,
            ["shell", "run-as", package, "ls", "-la", target], TimeSpan.FromSeconds(30), CancellationToken.None);
        if (!result.Success)
        {
            AppDataSummary = "Listing failed: " + CleanError(result) + "\nrun-as works only for debuggable builds.";
            return;
        }

        string listing = result.StandardOutput.Trim();
        DeviceLabOutput = listing;
        AppDataSummary = listing.Length == 0
            ? $"{package}:{relative} is empty."
            : $"{package}:{(relative.Length == 0 ? "/" : relative)} · {listing.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length - 1} entries (folder listing is in the activity log).";
    }

    /// <summary>Copies one file from the app's private data area to the local session folder as raw bytes.</summary>
    [RelayCommand]
    private async Task PullAppDataFileAsync()
    {
        if (!TryGetDeviceAndPackage(out AndroidDevice device, out string package)) return;
        if (!TryResolveAppDataPath(out string relative, requireFile: true)) return;

        string? adbPath = _adb.ResolvedAdbPath;
        if (string.IsNullOrWhiteSpace(adbPath))
        {
            AppDataSummary = "adb.exe was not found, so the file cannot be pulled.";
            return;
        }

        Directory.CreateDirectory(DeveloperLabDirectory);
        string destination = Path.Combine(DeveloperLabDirectory, $"app-data-{SafeName(package)}-{SafeName(Path.GetFileName(relative))}");
        ProcessStartInfo info = new(adbPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in new[] { "-s", device.Serial, "exec-out", "run-as", package, "cat", relative })
            info.ArgumentList.Add(argument);

        using Process process = new() { StartInfo = info };
        if (!process.Start())
        {
            AppDataSummary = "Unable to start adb for the pull.";
            return;
        }

        await using (FileStream stream = File.Create(destination))
        {
            await process.StandardOutput.BaseStream.CopyToAsync(stream);
        }
        string error = (await process.StandardError.ReadToEndAsync()).Trim();
        await process.WaitForExitAsync();

        long size = new FileInfo(destination).Length;
        if (process.ExitCode != 0 || size == 0)
        {
            AppDataSummary = $"Pull failed (exit {process.ExitCode}){(error.Length == 0 ? "." : ": " + error)}";
            return;
        }

        TestArtifacts.Add(destination);
        AppDataSummary = $"Pulled {relative} ({size:N0} bytes) to {destination}.";
        DeviceLabStatus = AppDataSummary;
    }

    private bool TryResolveAppDataPath(out string relative, bool requireFile)
    {
        relative = (AppDataFile ?? "").Trim().Replace('\\', '/').TrimStart('/');
        if (relative.Contains("..", StringComparison.Ordinal) || !Regex.IsMatch(relative, @"^[A-Za-z0-9_.\-/ ]*$"))
        {
            AppDataSummary = "Use a path relative to the app data directory: letters, digits, spaces, dot, dash, underscore and slash only (no '..').";
            return false;
        }

        if (requireFile && (relative.Length == 0 || relative.EndsWith('/')))
        {
            AppDataSummary = "Enter a file path inside the app data directory, for example shared_prefs/settings.xml.";
            return false;
        }

        return true;
    }
}
