#nullable enable
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using AndroidDevMonitor.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AndroidDevMonitor.App.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty] private double _gpsLatitude = 45.8150;
    [ObservableProperty] private double _gpsLongitude = 15.9819;
    [ObservableProperty] private string _snapshotName = "adm-snapshot";
    [ObservableProperty] private string _deviceFontScale = "1.0";
    [ObservableProperty] private string _emulatorProfileSummary =
        "Read the device profile to see API level, ABI, resolution, density, locale, dark mode, font scale and Play Store availability in one place.";
    [ObservableProperty] private string _multiDeviceSummary =
        "Runs one action across every connected device and keeps the results in a single folder so a device family can be checked in one pass.";
    [ObservableProperty] private string? _selectedFleetArtifact;

    public ObservableCollection<string> FleetArtifacts { get; } = [];

    [RelayCommand]
    private async Task SendGeoFixAsync()
    {
        AndroidDevice? device = SelectedDevice;
        if (device is null || device.Kind != DeviceKind.Emulator) { DeviceLabStatus = "GPS simulation requires a running emulator."; return; }
        string latitude = GpsLatitude.ToString("0.######", CultureInfo.InvariantCulture);
        string longitude = GpsLongitude.ToString("0.######", CultureInfo.InvariantCulture);
        AdbCommandResult result = await _adb.ExecuteAsync(
            device.Serial,
            ["emu", "geo", "fix", longitude, latitude],
            TimeSpan.FromSeconds(15),
            CancellationToken.None);
        DeviceLabOutput = result.StandardOutput + result.StandardError;
        DeviceLabStatus = result.Success ? $"GPS fix sent: {latitude}, {longitude}." : "GPS fix failed: " + CleanError(result);
    }

    [RelayCommand]
    private async Task FoldDeviceAsync() => await RunEmulatorConsoleAsync("Fold", ["emu", "fold"]);

    [RelayCommand]
    private async Task UnfoldDeviceAsync() => await RunEmulatorConsoleAsync("Unfold", ["emu", "unfold"]);

    [RelayCommand]
    private async Task ToggleDarkModeAsync()
    {
        AndroidDevice? device = SelectedDevice;
        if (device is null) { DeviceLabStatus = "Select a connected Android device."; return; }
        AdbCommandResult current = await _adb.ExecuteAsync(device.Serial, ["shell", "cmd", "uimode", "night"], TimeSpan.FromSeconds(15), CancellationToken.None);
        bool isNight = current.StandardOutput.Contains("yes", StringComparison.OrdinalIgnoreCase);
        string target = isNight ? "no" : "yes";
        AdbCommandResult result = await _adb.ExecuteAsync(device.Serial, ["shell", "cmd", "uimode", "night", target], TimeSpan.FromSeconds(15), CancellationToken.None);
        DeviceLabOutput = result.StandardOutput + result.StandardError;
        DeviceLabStatus = result.Success
            ? $"Dark mode switched {(target == "yes" ? "on" : "off")}."
            : "Dark mode switch failed: " + CleanError(result);
        await ReadExtendedProfileAsync();
    }

    [RelayCommand]
    private async Task ApplyFontScaleAsync()
    {
        AndroidDevice? device = SelectedDevice;
        if (device is null) { DeviceLabStatus = "Select a connected Android device."; return; }
        if (!double.TryParse(DeviceFontScale, NumberStyles.Float, CultureInfo.InvariantCulture, out double scale) || scale < 0.5 || scale > 2.0)
        {
            DeviceLabStatus = "Font scale must be a number between 0.5 and 2.0, for example 1.15.";
            return;
        }
        string value = scale.ToString("0.##", CultureInfo.InvariantCulture);
        AdbCommandResult result = await _adb.ExecuteAsync(device.Serial, ["shell", "settings", "put", "system", "font_scale", value], TimeSpan.FromSeconds(15), CancellationToken.None);
        DeviceLabStatus = result.Success ? $"Font scale set to {value}." : "Font scale change failed: " + CleanError(result);
        await ReadExtendedProfileAsync();
    }

    [RelayCommand]
    private async Task CheckPlayStoreAsync()
    {
        AndroidDevice? device = SelectedDevice;
        if (device is null) { DeviceLabStatus = "Select a connected Android device."; return; }
        AdbCommandResult result = await _adb.ExecuteAsync(device.Serial, ["shell", "pm", "path", "com.android.vending"], TimeSpan.FromSeconds(15), CancellationToken.None);
        bool installed = result.StandardOutput.Contains("package:", StringComparison.Ordinal);
        EmulatorProfileSummary = installed
            ? "Play Store is installed on this image; billing, licensing and Play-dependent tests can run."
            : "Play Store is NOT installed on this image (com.android.vending is missing). Use a Google Play system image for Play-dependent tests; Google Play services alone are not enough.";
        DeviceLabStatus = installed ? "Play Store detected." : "No Play Store on this image.";
    }

    [RelayCommand]
    private async Task ReadExtendedProfileAsync()
    {
        AndroidDevice? device = SelectedDevice;
        if (device is null) { DeviceLabStatus = "Select a connected Android device."; return; }
        const string script =
            "echo __FONT__; settings get system font_scale; " +
            "echo __NIGHT__; cmd uimode night; " +
            "echo __LOCALE__; getprop persist.sys.locale; " +
            "echo __PLAY__; pm path com.android.vending; " +
            "echo __AVD__; getprop ro.boot.qemu.avd_name; " +
            "echo __ABI__; getprop ro.product.cpu.abi; " +
            "echo __SDK__; getprop ro.build.version.sdk; " +
            "echo __SIZE__; wm size; " +
            "echo __DENSITY__; wm density";
        AdbCommandResult result = await _adb.ExecuteAsync(device.Serial, ["shell", "sh", "-c", script], TimeSpan.FromSeconds(30), CancellationToken.None);
        if (!result.Success)
        {
            DeviceLabStatus = "Profile read failed: " + CleanError(result);
            return;
        }

        string[] blocks = result.StandardOutput.Split("__", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string Value(string key) =>
            blocks.FirstOrDefault(block => block.StartsWith(key + "__", StringComparison.Ordinal)) is string match
                ? match[(key.Length + 2)..].Trim().Replace("\r", string.Empty)
                : "N/A";

        StringBuilder report = new();
        report.AppendLine($"Device:       {device.FriendlyName} ({device.Serial})");
        report.AppendLine($"Kind:         {device.Kind}   Android {device.AndroidVersion} · API {Value("SDK")}");
        report.AppendLine($"ABI:          {Value("ABI")}");
        report.AppendLine($"Display:      {Value("SIZE")}   {Value("DENSITY")}");
        report.AppendLine($"Locale:       {Value("LOCALE")}");
        report.AppendLine($"Font scale:   {Value("FONT")}");
        report.AppendLine($"Dark mode:    {Value("NIGHT")}");
        report.AppendLine($"AVD name:     {Value("AVD")}");
        report.AppendLine($"Play Store:   {(Value("PLAY").Contains("package:", StringComparison.Ordinal) ? "installed" : "not installed")}");
        EmulatorProfileSummary = report.ToString();
        DeviceLabStatus = "Device profile refreshed.";
    }

    [RelayCommand]
    private async Task SaveEmulatorSnapshotAsync()
    {
        string name = SnapshotName.Trim();
        if (!ValidSnapshotName(name)) { DeviceLabStatus = "Use letters, digits, dash or dot for the snapshot name."; return; }
        await RunEmulatorConsoleAsync($"Save snapshot '{name}'", ["emu", "avd", "snapshot", "save", name]);
    }

    [RelayCommand]
    private async Task LoadEmulatorSnapshotAsync()
    {
        string name = SnapshotName.Trim();
        if (!ValidSnapshotName(name)) { DeviceLabStatus = "Use letters, digits, dash or dot for the snapshot name."; return; }
        await RunEmulatorConsoleAsync($"Load snapshot '{name}'", ["emu", "avd", "snapshot", "load", name]);
    }

    [RelayCommand]
    private async Task DeleteEmulatorSnapshotAsync()
    {
        string name = SnapshotName.Trim();
        if (!ValidSnapshotName(name)) { DeviceLabStatus = "Use letters, digits, dash or dot for the snapshot name."; return; }
        if (!_dialogs.Confirm("Delete emulator snapshot", $"Delete snapshot '{name}' from the running emulator? This cannot be undone.")) return;
        await RunEmulatorConsoleAsync($"Delete snapshot '{name}'", ["emu", "avd", "snapshot", "delete", name]);
    }

    [RelayCommand]
    private async Task ListEmulatorSnapshotsAsync() =>
        await RunEmulatorConsoleAsync("Snapshot list", ["emu", "avd", "snapshot", "list"]);

    private async Task RunEmulatorConsoleAsync(string label, IReadOnlyList<string> arguments)
    {
        AndroidDevice? device = SelectedDevice;
        if (device is null || device.Kind != DeviceKind.Emulator) { DeviceLabStatus = $"{label}: select a running emulator."; return; }
        AdbCommandResult result = await _adb.ExecuteAsync(device.Serial, arguments, TimeSpan.FromSeconds(30), CancellationToken.None);
        DeviceLabOutput = result.StandardOutput + result.StandardError;
        DeviceLabStatus = result.Success ? $"{label}: done." : $"{label} failed: " + CleanError(result);
    }

    private static bool ValidSnapshotName(string name) =>
        name.Length > 0 && name.Length <= 60 && name.All(character => char.IsLetterOrDigit(character) || character is '-' or '.' or '_');

    // ---- Multi-device fleet lab -------------------------------------------------

    [RelayCommand]
    private async Task InstallOnAllDevicesAsync()
    {
        string apk = ApkPath.Trim();
        if (apk.Length == 0 || !File.Exists(apk)) { DeviceLabStatus = "Choose a local APK first."; return; }
        AndroidDevice[] targets = ConnectedDevices();
        if (targets.Length == 0) { DeviceLabStatus = "No connected devices."; return; }
        StringBuilder report = new();
        report.AppendLine("FLEET INSTALL");
        report.AppendLine($"APK:     {apk}");
        report.AppendLine($"Devices: {targets.Length}");
        report.AppendLine();
        foreach (AndroidDevice target in targets)
        {
            AdbCommandResult result = await _adb.ExecuteAsync(target.Serial, ["install", "-r", "-t", apk], TimeSpan.FromMinutes(5), CancellationToken.None);
            report.AppendLine($"{target.Serial}\t{(result.Success ? "OK" : "FAILED")}\t{result.StandardOutput.Trim()}\t{result.StandardError.Trim()}");
        }
        MultiDeviceSummary = $"Install finished on {targets.Length} device(s).";
        DeviceLabOutput = await SaveStandaloneTextAsync("fleet-install", report.ToString());
    }

    [RelayCommand]
    private async Task LaunchOnAllDevicesAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedPackage)) { DeviceLabStatus = "Select an application package first."; return; }
        AndroidDevice[] targets = ConnectedDevices();
        if (targets.Length == 0) { DeviceLabStatus = "No connected devices."; return; }
        string package = ShellPackage();
        StringBuilder report = new();
        report.AppendLine("FLEET LAUNCH");
        report.AppendLine($"Package: {package}");
        report.AppendLine();
        foreach (AndroidDevice target in targets)
        {
            await _adb.ExecuteAsync(target.Serial, ["shell", "am", "force-stop", package], TimeSpan.FromSeconds(20), CancellationToken.None);
            AdbCommandResult result = await _adb.ExecuteAsync(
                target.Serial,
                ["shell", "monkey", "-p", package, "-c", "android.intent.category.LAUNCHER", "1"],
                TimeSpan.FromSeconds(40),
                CancellationToken.None);
            report.AppendLine($"{target.Serial}\t{(result.Success ? "OK" : "FAILED")}\t{result.StandardOutput.Trim()}");
        }
        MultiDeviceSummary = $"Launch finished on {targets.Length} device(s).";
        DeviceLabOutput = await SaveStandaloneTextAsync("fleet-launch", report.ToString());
    }

    [RelayCommand]
    private async Task CaptureAllScreenshotsAsync()
    {
        AndroidDevice[] targets = ConnectedDevices();
        if (targets.Length == 0) { DeviceLabStatus = "No connected devices."; return; }
        Directory.CreateDirectory(DeveloperLabDirectory);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        List<string> saved = [];
        List<string> failures = [];
        foreach (AndroidDevice target in targets)
        {
            try
            {
                MediaItem item = await _media.CaptureScreenshotAsync(target, SelectedPackage, null, CancellationToken.None);
                string destination = Path.Combine(DeveloperLabDirectory, $"fleet-shot-{SafeName(target.Serial)}-{stamp}.png");
                File.Copy(item.LocalPath, destination, overwrite: true);
                saved.Add(destination);
                FleetArtifacts.Add(destination);
            }
            catch (Exception ex)
            {
                failures.Add($"{target.Serial}: {ex.Message}");
            }
        }
        SelectedFleetArtifact = saved.FirstOrDefault();
        MultiDeviceSummary = $"Captured {saved.Count} screenshot(s) from {targets.Length} device(s)" +
            (failures.Count == 0 ? "." : $"; {failures.Count} failed ({string.Join("; ", failures)}).");
        DeviceLabStatus = MultiDeviceSummary;
    }

    [RelayCommand]
    private async Task CollectFleetLogsAsync()
    {
        AndroidDevice[] targets = ConnectedDevices();
        if (targets.Length == 0) { DeviceLabStatus = "No connected devices."; return; }
        Directory.CreateDirectory(DeveloperLabDirectory);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        StringBuilder report = new();
        report.AppendLine("FLEET LOGCAT SNAPSHOT");
        report.AppendLine($"Generated: {DateTimeOffset.Now:O}");
        report.AppendLine();
        foreach (AndroidDevice target in targets)
        {
            AdbCommandResult result = await _adb.ExecuteAsync(
                target.Serial,
                ["shell", "logcat", "-d", "-v", "time", "-t", "400"],
                TimeSpan.FromSeconds(45),
                CancellationToken.None);
            report.AppendLine($"===== {target.Serial} ({target.FriendlyName}) · {(result.Success ? "ok" : "failed")} =====");
            report.AppendLine(result.StandardOutput.Trim());
            report.AppendLine();
        }
        string path = Path.Combine(DeveloperLabDirectory, $"fleet-logcat-{stamp}.txt");
        await File.WriteAllTextAsync(path, report.ToString(), Encoding.UTF8);
        FleetArtifacts.Add(path);
        SelectedFleetArtifact = path;
        MultiDeviceSummary = $"Logcat snapshot saved for {targets.Length} device(s).";
        DeviceLabStatus = MultiDeviceSummary;
    }

    [RelayCommand]
    private async Task ExportFleetBundleAsync()
    {
        Directory.CreateDirectory(DeveloperLabDirectory);
        string[] files = Directory.GetFiles(DeveloperLabDirectory)
            .Where(path => Path.GetFileName(path).StartsWith("fleet-", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(300)
            .ToArray();
        if (files.Length == 0) { DeviceLabStatus = "No fleet artifacts yet — install, launch, screenshot or collect logs first."; return; }
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string zipPath = Path.Combine(DeveloperLabDirectory, $"fleet-bundle-{stamp}.zip");
        await using (FileStream stream = File.Create(zipPath))
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create))
        {
            foreach (string file in files)
            {
                ZipArchiveEntry entry = archive.CreateEntry(Path.GetFileName(file), CompressionLevel.Optimal);
                await using Stream entryStream = entry.Open();
                await using FileStream source = File.OpenRead(file);
                await source.CopyToAsync(entryStream);
            }
        }
        FleetArtifacts.Add(zipPath);
        SelectedFleetArtifact = zipPath;
        MultiDeviceSummary = $"Fleet bundle exported with {files.Length} artifact(s).";
        DeviceLabStatus = MultiDeviceSummary;
    }

    [RelayCommand]
    private void OpenFleetArtifact(string? path)
    {
        string? target = path ?? SelectedFleetArtifact;
        if (string.IsNullOrWhiteSpace(target) || !File.Exists(target))
        {
            _dialogs.Notify("Select an existing fleet artifact first.", error: true);
            return;
        }
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    [RelayCommand]
    private void ClearFleetArtifacts()
    {
        FleetArtifacts.Clear();
        SelectedFleetArtifact = null;
        MultiDeviceSummary = "Fleet list cleared. The files stay in the session folder.";
    }

    private AndroidDevice[] ConnectedDevices() =>
        Devices.Where(device => device.State == DeviceState.Connected).ToArray();

    /// <summary>Opens one scrcpy window per connected device so the whole fleet is watchable at once.</summary>
    [RelayCommand]
    private void OpenScrcpyForAllDevices()
    {
        AndroidDevice[] targets = ConnectedDevices();
        if (targets.Length == 0) { MultiDeviceSummary = "No connected devices. Refresh the device list first."; return; }
        string? scrcpy = ResolveExecutable("scrcpy.exe");
        if (scrcpy is null) { MultiDeviceSummary = "scrcpy.exe was not found. Install scrcpy or add it to PATH."; return; }

        int launched = 0;
        foreach (AndroidDevice device in targets)
        {
            try
            {
                ProcessStartInfo start = new(scrcpy) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                start.ArgumentList.Add("--serial"); start.ArgumentList.Add(device.Serial);
                start.ArgumentList.Add("--window-title"); start.ArgumentList.Add($"Android Dev Monitor · {device.FriendlyName} ({device.Serial})");
                Process.Start(start);
                launched++;
            }
            catch (Exception ex)
            {
                MultiDeviceSummary = $"scrcpy failed for {device.Serial}: {ex.Message}";
            }
        }

        MultiDeviceSummary = $"Started {launched} scrcpy window(s) for {targets.Length} connected device(s). Audio forwarding is included on Android 11+.";
        DeviceLabStatus = MultiDeviceSummary;
    }

    /// <summary>Reads which virtual sensors this AVD exposes and whether they are enabled.</summary>
    [RelayCommand]
    private async Task ReadSensorStatusAsync()
    {
        AndroidDevice? device = SelectedDevice;
        if (device is null || device.Kind != DeviceKind.Emulator) { DeviceLabStatus = "Select a running emulator to read its virtual sensors."; return; }
        AdbCommandResult result = await _adb.ExecuteAsync(device.Serial, ["emu", "sensor", "status"], TimeSpan.FromSeconds(15), CancellationToken.None);
        if (!result.Success) { DeviceLabStatus = "Sensor query failed: " + CleanError(result); return; }

        string status = result.StandardOutput.Trim();
        DeviceLabOutput = status;
        int enabled = status.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.Contains("enabled", StringComparison.OrdinalIgnoreCase) && !line.Contains("disabled", StringComparison.OrdinalIgnoreCase));
        DeviceLabStatus = enabled == 0
            ? "All sensors report 'disabled' on this AVD. Headless instances expose them as disabled; start the AVD with a visible emulator window to use sensor values."
            : $"{enabled} sensor(s) enabled on {device.FriendlyName}; full list is in the activity log.";
    }

    /// <summary>Flips airplane mode through the platform command so Wi-Fi/radio states can be tested without touching settings UI.</summary>
    [RelayCommand]
    private async Task ToggleAirplaneModeAsync()
    {
        AndroidDevice? device = SelectedDevice;
        if (device is null) { DeviceLabStatus = "Select a connected device first."; return; }
        AdbCommandResult read = await _adb.ExecuteAsync(device.Serial, ["shell", "cmd", "connectivity", "airplane-mode"], TimeSpan.FromSeconds(10), CancellationToken.None);
        if (!read.Success) { DeviceLabStatus = "This build does not expose cmd connectivity airplane-mode: " + CleanError(read); return; }

        bool enabled = read.StandardOutput.Contains("enabled", StringComparison.OrdinalIgnoreCase);
        string action = enabled ? "disable" : "enable";
        AdbCommandResult write = await _adb.ExecuteAsync(device.Serial, ["shell", "cmd", "connectivity", "airplane-mode", action], TimeSpan.FromSeconds(10), CancellationToken.None);
        DeviceLabStatus = write.Success
            ? $"Airplane mode {action}d on {device.FriendlyName}. Use it to test offline handling."
            : "Changing airplane mode failed: " + CleanError(write);
    }
}
