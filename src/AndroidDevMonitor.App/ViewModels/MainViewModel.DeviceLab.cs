using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AndroidDevMonitor.Adb.Parsers;
using AndroidDevMonitor.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AndroidDevMonitor.App.ViewModels;

public sealed record InstrumentationRow(string Component, string TargetPackage, string SourcePath)
{
    public string DisplayName => $"{Component} → {TargetPackage}";
}

public sealed record PermissionChangeRow(DateTimeOffset Timestamp, string DeviceSerial, string Package, string Permission, string Action, string Status);

public partial class MainViewModel
{
    [ObservableProperty] private string _permissionName = "android.permission.POST_NOTIFICATIONS";
    [ObservableProperty] private string _deviceLabStatus = "Select a connected device and application.";
    [ObservableProperty] private string _deviceLabOutput = "";
    [ObservableProperty] private InstrumentationRow? _selectedInstrumentation;
    [ObservableProperty] private string _instrumentationClass = "";
    [ObservableProperty] private int _instrumentationRepeatCount = 1;
    [ObservableProperty] private string _deepLinkUri = "https://example.com";
    [ObservableProperty] private int _forwardLocalPort = 8080;
    [ObservableProperty] private int _forwardDevicePort = 8080;
    [ObservableProperty] private string _selectedPortDirection = "Forward PC → device";
    [ObservableProperty] private string _selectedEmulatorAction = "Battery 50%";
    private readonly Stack<PermissionChangeRow> _permissionRollbackStack = new();

    public ObservableCollection<InstrumentationRow> Instrumentations { get; } = [];
    public ObservableCollection<PermissionChangeRow> PermissionChanges { get; } = [];
    public IReadOnlyList<string> PortDirections { get; } = ["Forward PC → device", "Reverse device → PC"];
    public IReadOnlyList<string> EmulatorActions { get; } =
    [
        "Battery 15%", "Battery 50%", "Battery 100%", "Unplug charger", "Plug AC charger",
        "Network LTE", "Network EDGE", "Network latency none", "Network latency high",
        "Rotate device", "Snapshot list"
    ];

    [RelayCommand]
    private async Task RefreshPermissionStateAsync()
    {
        if (!TryGetDeviceAndPackage(out AndroidDevice device, out string package)) return;
        AdbCommandResult result = await _adb.ExecuteAsync(device.Serial,
            ["shell", "sh", "-c", $"dumpsys package {package} | grep -A 120 -E 'requested permissions:|install permissions:|runtime permissions:'; echo __APPOPS__; appops get {package}; echo __STANDBY__; am get-standby-bucket {package}"],
            TimeSpan.FromSeconds(30), CancellationToken.None);
        DeviceLabOutput = result.StandardOutput;
        DeviceLabStatus = result.Success ? "Permission and AppOps state refreshed." : "Refresh failed: " + CleanError(result);
    }

    [RelayCommand]
    private async Task GrantPermissionAsync() => await ChangePermissionAsync(grant: true);

    [RelayCommand]
    private async Task RevokePermissionAsync() => await ChangePermissionAsync(grant: false);

    private async Task ChangePermissionAsync(bool grant)
    {
        if (!TryGetDeviceAndPackage(out AndroidDevice device, out string package) || !TryPermission(out string permission)) return;
        if (!grant && !_dialogs.Confirm("Revoke runtime permission", $"Revoke {permission} from {package}? The application may stop.")) return;
        string action = grant ? "grant" : "revoke";
        AdbCommandResult result = await _adb.ExecuteAsync(device.Serial, ["shell", "pm", action, package, permission], TimeSpan.FromSeconds(15), CancellationToken.None);
        DeviceLabStatus = result.Success ? $"Permission {action} completed for {package}." : $"Permission {action} failed: {CleanError(result)}";
        PermissionChangeRow change = new(DateTimeOffset.Now, device.Serial, package, permission, action, result.Success ? "Applied" : "Failed");
        AddPermissionChange(change);
        await AppendPermissionAuditAsync(change);
        if (result.Success) _permissionRollbackStack.Push(change);
        await RefreshPermissionStateAsync();
    }

    [RelayCommand]
    private async Task RollbackPermissionChangeAsync()
    {
        if (!_permissionRollbackStack.TryPeek(out PermissionChangeRow? change)) { DeviceLabStatus = "No applied permission change is available to roll back."; return; }
        string inverse = change.Action == "grant" ? "revoke" : "grant";
        if (!_dialogs.Confirm("Roll back permission change", $"{inverse} {change.Permission} for {change.Package} on {change.DeviceSerial}?")) return;
        AdbCommandResult result = await _adb.ExecuteAsync(change.DeviceSerial, ["shell", "pm", inverse, change.Package, change.Permission], TimeSpan.FromSeconds(15), CancellationToken.None);
        PermissionChangeRow rollback = new(DateTimeOffset.Now, change.DeviceSerial, change.Package, change.Permission, inverse, result.Success ? "Rollback applied" : "Rollback failed");
        AddPermissionChange(rollback);
        await AppendPermissionAuditAsync(rollback);
        if (result.Success) _permissionRollbackStack.Pop();
        DeviceLabStatus = result.Success ? "Last permission change rolled back." : "Rollback failed: " + CleanError(result);
        if (SelectedDevice?.Serial == change.DeviceSerial && SelectedPackage == change.Package) await RefreshPermissionStateAsync();
    }

    [RelayCommand]
    private async Task ResetAppOpsAsync()
    {
        if (!TryGetDeviceAndPackage(out AndroidDevice device, out string package)) return;
        if (!_dialogs.Confirm("Reset AppOps", $"Reset every AppOps override for {package} to Android defaults?")) return;
        AdbCommandResult result = await _adb.ExecuteAsync(device.Serial, ["shell", "cmd", "appops", "reset", package], TimeSpan.FromSeconds(15), CancellationToken.None);
        DeviceLabStatus = result.Success ? $"AppOps reset for {package}." : "AppOps reset failed: " + CleanError(result);
        await RefreshPermissionStateAsync();
    }

    [RelayCommand]
    private async Task DiscoverInstrumentationAsync()
    {
        AndroidDevice? device = SelectedDevice;
        if (device is null) { DeviceLabStatus = "Select a connected Android device."; return; }
        AdbCommandResult result = await _adb.ExecuteAsync(device.Serial, ["shell", "pm", "list", "instrumentation", "-f"], TimeSpan.FromSeconds(20), CancellationToken.None);
        Instrumentations.Clear();
        foreach (InstrumentationInfo item in InstrumentationParser.Parse(result.StandardOutput))
            Instrumentations.Add(new(item.Component, item.TargetPackage, item.SourcePath));
        SelectedInstrumentation = Instrumentations.FirstOrDefault();
        DeviceLabStatus = result.Success ? $"Found {Instrumentations.Count} instrumentation runner(s)." : "Instrumentation discovery failed: " + CleanError(result);
    }

    [RelayCommand]
    private async Task RunInstrumentationTestsAsync()
    {
        AndroidDevice? device = SelectedDevice;
        InstrumentationRow? runner = SelectedInstrumentation;
        if (device is null || runner is null) { DeviceLabStatus = "Select a device and instrumentation runner."; return; }
        string className = InstrumentationClass.Trim();
        if (className.Length > 0 && !Regex.IsMatch(className, @"^[A-Za-z0-9_.$#]+$")) { DeviceLabStatus = "The class or method filter is invalid."; return; }
        int repeats = Math.Clamp(InstrumentationRepeatCount, 1, 20);
        StringBuilder report = new();
        for (int run = 1; run <= repeats; run++)
        {
            List<string> args = ["shell", "am", "instrument", "-w", "-r"];
            if (className.Length > 0) args.AddRange(["-e", "class", className]);
            args.Add(runner.Component);
            Stopwatch timer = Stopwatch.StartNew();
            AdbCommandResult result = await _adb.ExecuteAsync(device.Serial, args, TimeSpan.FromMinutes(15), CancellationToken.None);
            timer.Stop();
            report.AppendLine($"===== RUN {run}/{repeats} · {timer.Elapsed:g} · {(result.Success ? "COMPLETED" : "FAILED")} =====");
            report.AppendLine(result.StandardOutput);
            if (!string.IsNullOrWhiteSpace(result.StandardError)) report.AppendLine(result.StandardError);
            if (!result.Success) break;
        }
        DeviceLabOutput = await SaveTextAsync(device, "instrumentation", report.ToString());
        DeviceLabStatus = "Instrumentation run finished and was saved locally.";
    }

    [RelayCommand]
    private async Task LaunchDeepLinkAsync()
    {
        AndroidDevice? device = SelectedDevice;
        if (device is null || !Uri.TryCreate(DeepLinkUri.Trim(), UriKind.Absolute, out Uri? uri)) { DeviceLabStatus = "Select a device and enter a valid absolute URI."; return; }
        List<string> args = ["shell", "am", "start", "-W", "-a", "android.intent.action.VIEW", "-d", uri.AbsoluteUri];
        if (!string.IsNullOrWhiteSpace(SelectedPackage)) args.AddRange(["-p", ShellPackage()]);
        AdbCommandResult result = await _adb.ExecuteAsync(device.Serial, args, TimeSpan.FromSeconds(30), CancellationToken.None);
        DeviceLabOutput = result.StandardOutput;
        DeviceLabStatus = result.Success ? "Deep link launched." : "Deep link failed: " + CleanError(result);
    }

    [RelayCommand]
    private async Task ApplyPortMappingAsync()
    {
        AndroidDevice? device = SelectedDevice;
        if (device is null || !ValidPort(ForwardLocalPort) || !ValidPort(ForwardDevicePort)) { DeviceLabStatus = "Select a device and use ports from 1 to 65535."; return; }
        bool reverse = SelectedPortDirection.StartsWith("Reverse", StringComparison.Ordinal);
        IReadOnlyList<string> args = reverse
            ? ["reverse", $"tcp:{ForwardDevicePort}", $"tcp:{ForwardLocalPort}"]
            : ["forward", $"tcp:{ForwardLocalPort}", $"tcp:{ForwardDevicePort}"];
        AdbCommandResult result = await _adb.ExecuteAsync(device.Serial, args, TimeSpan.FromSeconds(15), CancellationToken.None);
        DeviceLabStatus = result.Success ? "Port mapping applied." : "Port mapping failed: " + CleanError(result);
        await ListPortMappingsAsync();
    }

    [RelayCommand]
    private async Task RemovePortMappingAsync()
    {
        AndroidDevice? device = SelectedDevice;
        if (device is null || !ValidPort(ForwardLocalPort) || !ValidPort(ForwardDevicePort)) { DeviceLabStatus = "Select a device and use ports from 1 to 65535."; return; }
        bool reverse = SelectedPortDirection.StartsWith("Reverse", StringComparison.Ordinal);
        IReadOnlyList<string> args = reverse ? ["reverse", "--remove", $"tcp:{ForwardDevicePort}"] : ["forward", "--remove", $"tcp:{ForwardLocalPort}"];
        AdbCommandResult result = await _adb.ExecuteAsync(device.Serial, args, TimeSpan.FromSeconds(15), CancellationToken.None);
        DeviceLabStatus = result.Success ? "Port mapping removed." : "Remove failed: " + CleanError(result);
        await ListPortMappingsAsync();
    }

    [RelayCommand]
    private async Task ListPortMappingsAsync()
    {
        AndroidDevice? device = SelectedDevice;
        if (device is null) { DeviceLabStatus = "Select a connected Android device."; return; }
        AdbCommandResult forward = await _adb.ExecuteAsync(null, ["forward", "--list"], TimeSpan.FromSeconds(10), CancellationToken.None);
        AdbCommandResult reverse = await _adb.ExecuteAsync(device.Serial, ["reverse", "--list"], TimeSpan.FromSeconds(10), CancellationToken.None);
        DeviceLabOutput = $"FORWARD (PC → DEVICE)\n{forward.StandardOutput}\nREVERSE (DEVICE → PC)\n{reverse.StandardOutput}";
        DeviceLabStatus = "Port mappings refreshed.";
    }

    [RelayCommand]
    private async Task SendDeviceKeyAsync(string? key)
    {
        AndroidDevice? device = SelectedDevice;
        if (device is null || key is not ("HOME" or "BACK" or "APP_SWITCH" or "POWER" or "VOLUME_UP" or "VOLUME_DOWN")) { DeviceLabStatus = "Select a device."; return; }
        AdbCommandResult result = await _adb.ExecuteAsync(device.Serial, ["shell", "input", "keyevent", key], TimeSpan.FromSeconds(10), CancellationToken.None);
        DeviceLabStatus = result.Success ? $"Sent {key}." : "Input failed: " + CleanError(result);
    }

    [RelayCommand]
    private async Task ApplyEmulatorActionAsync()
    {
        AndroidDevice? device = SelectedDevice;
        if (device is null || device.Kind != DeviceKind.Emulator) { DeviceLabStatus = "Select an Android emulator."; return; }
        IReadOnlyList<string> args = SelectedEmulatorAction switch
        {
            "Battery 15%" => ["emu", "power", "capacity", "15"],
            "Battery 50%" => ["emu", "power", "capacity", "50"],
            "Battery 100%" => ["emu", "power", "capacity", "100"],
            "Unplug charger" => ["emu", "power", "ac", "off"],
            "Plug AC charger" => ["emu", "power", "ac", "on"],
            "Network LTE" => ["emu", "network", "speed", "lte"],
            "Network EDGE" => ["emu", "network", "speed", "edge"],
            "Network latency none" => ["emu", "network", "delay", "none"],
            "Network latency high" => ["emu", "network", "delay", "gprs"],
            "Rotate device" => ["emu", "rotate"],
            "Snapshot list" => ["emu", "avd", "snapshot", "list"],
            _ => []
        };
        if (args.Count == 0) return;
        AdbCommandResult result = await _adb.ExecuteAsync(device.Serial, args, TimeSpan.FromSeconds(20), CancellationToken.None);
        DeviceLabOutput = result.StandardOutput;
        DeviceLabStatus = result.Success ? $"Applied: {SelectedEmulatorAction}." : "Emulator command failed: " + CleanError(result);
    }

    [RelayCommand]
    private void StartDeviceMirroring()
    {
        AndroidDevice? device = SelectedDevice;
        if (device is null) { DeviceLabStatus = "Select a connected Android device."; return; }
        string? scrcpy = ResolveExecutable("scrcpy.exe");
        if (scrcpy is null) { DeviceLabStatus = "scrcpy.exe was not found. Install scrcpy or add it to PATH."; return; }
        ProcessStartInfo start = new(scrcpy) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--serial"); start.ArgumentList.Add(device.Serial);
        start.ArgumentList.Add("--window-title"); start.ArgumentList.Add($"Android Dev Monitor · {device.FriendlyName}");
        Process.Start(start);
        DeviceLabStatus = "Device mirroring started in scrcpy.";
    }

    [RelayCommand]
    private async Task BuildMultiDeviceMatrixAsync()
    {
        AndroidDevice[] targets = Devices.Where(device => device.IsConnected).ToArray();
        if (targets.Length == 0) { DeviceLabStatus = "No connected devices."; return; }
        string package = SelectedPackage ?? "";
        if (!Regex.IsMatch(package, @"^[A-Za-z0-9_.]+$")) { DeviceLabStatus = "Select an application package for the device matrix."; return; }
        StringBuilder report = new($"Package: {package}\nGenerated: {DateTimeOffset.Now:O}\n\nDevice\tSerial\tType\tAPI\tABI\tResolution\tDensity\tADB ms\tCold start ms\tPSS bytes\tSecurity patch\n");
        foreach (AndroidDevice device in targets)
        {
            Stopwatch latency = Stopwatch.StartNew();
            AdbCommandResult ping = await _adb.ExecuteAsync(device.Serial, ["shell", "echo", "ok"], TimeSpan.FromSeconds(10), CancellationToken.None);
            latency.Stop();
            AdbCommandResult resolve = await _adb.ExecuteAsync(device.Serial, ["shell", "cmd", "package", "resolve-activity", "--brief", package], TimeSpan.FromSeconds(10), CancellationToken.None);
            string component = resolve.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
            string startupMs = "N/A";
            if (component.Contains('/'))
            {
                _ = await _adb.ExecuteAsync(device.Serial, ["shell", "am", "force-stop", package], TimeSpan.FromSeconds(10), CancellationToken.None);
                AdbCommandResult startup = await _adb.ExecuteAsync(device.Serial, ["shell", "am", "start", "-W", "-n", component], TimeSpan.FromSeconds(30), CancellationToken.None);
                Match total = Regex.Match(startup.StandardOutput, @"TotalTime:\s*(?<ms>\d+)");
                if (total.Success) startupMs = total.Groups["ms"].Value;
            }
            AdbCommandResult memory = await _adb.ExecuteAsync(device.Serial, ["shell", "dumpsys", "meminfo", package], TimeSpan.FromSeconds(20), CancellationToken.None);
            long? pss = AndroidParsers.ParseDumpsysPss(memory.StandardOutput);
            AdbCommandResult patch = await _adb.ExecuteAsync(device.Serial, ["shell", "getprop", "ro.build.version.security_patch"], TimeSpan.FromSeconds(10), CancellationToken.None);
            report.AppendLine($"{device.FriendlyName}\t{device.Serial}\t{device.Kind}\t{device.ApiLevel}\t{device.Abi}\t{device.Resolution}\t{device.Density}\t{(ping.Success ? latency.ElapsedMilliseconds : -1)}\t{startupMs}\t{pss?.ToString(CultureInfo.InvariantCulture) ?? "N/A"}\t{patch.StandardOutput.Trim()}");
        }
        DeviceLabOutput = await SaveStandaloneTextAsync("device-matrix", report.ToString());
        DeviceLabStatus = $"Saved matrix for {targets.Length} connected device(s).";
    }

    private bool TryGetDeviceAndPackage(out AndroidDevice device, out string package)
    {
        device = SelectedDevice!;
        package = SelectedPackage ?? "";
        if (device is null || !Regex.IsMatch(package, @"^[A-Za-z0-9_.]+$"))
        {
            DeviceLabStatus = "Select a connected device and valid application package.";
            return false;
        }
        return true;
    }

    private bool TryPermission(out string permission)
    {
        permission = PermissionName.Trim();
        if (Regex.IsMatch(permission, @"^[A-Za-z0-9_.]+$")) return true;
        DeviceLabStatus = "Enter a valid Android permission name.";
        return false;
    }

    private static bool ValidPort(int port) => port is > 0 and <= 65535;

    private async Task AppendPermissionAuditAsync(PermissionChangeRow change)
    {
        Directory.CreateDirectory(DeveloperLabDirectory);
        string line = System.Text.Json.JsonSerializer.Serialize(change) + Environment.NewLine;
        await File.AppendAllTextAsync(Path.Combine(DeveloperLabDirectory, "permission-audit.jsonl"), line, Encoding.UTF8);
    }

    private void AddPermissionChange(PermissionChangeRow change)
    {
        PermissionChanges.Insert(0, change);
        while (PermissionChanges.Count > 5) PermissionChanges.RemoveAt(PermissionChanges.Count - 1);
    }

    private static string? ResolveExecutable(string name)
    {
        string? fromPath = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim().Trim('"'), name)).FirstOrDefault(File.Exists);
        if (fromPath is not null) return fromPath;
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "scrcpy", name),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "scrcpy", name),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "apps", "scrcpy", "current", name)
        ];
        return candidates.FirstOrDefault(File.Exists);
    }
}
