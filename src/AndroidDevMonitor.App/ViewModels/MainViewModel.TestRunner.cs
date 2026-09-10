#nullable enable
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using AndroidDevMonitor.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AndroidDevMonitor.App.ViewModels;

public partial class MainViewModel
{
    private static readonly string[] TestArtifactPrefixes =
    [
        "instrumentation-", "monkey-", "crash-scan-", "uiautomator-", "test-artifact-",
        "companion-events-", "startup-", "background-", "permission-audit"
    ];

    [ObservableProperty] private int _monkeyThrottleMs = 50;
    [ObservableProperty] private bool _stopOnFailure = true;
    [ObservableProperty] private string? _selectedTestArtifact;
    [ObservableProperty] private string _testRunnerSummary =
        "Run instrumentation tests, Monkey stress, a crash/ANR scan and a UI hierarchy dump. Screenshot and logcat are captured automatically when a run fails.";

    public ObservableCollection<string> TestArtifacts { get; } = [];

    [RelayCommand]
    private async Task RunMonkeyStressAsync()
    {
        if (!TryGetDeviceAndPackage(out AndroidDevice device, out string package)) return;
        if (!Regex.IsMatch(package, @"^[A-Za-z0-9_.]+$")) { DeviceLabStatus = "Select a valid application package first."; return; }
        int events = Math.Clamp(MonkeyEventCount, 10, 20000);
        int throttle = Math.Clamp(MonkeyThrottleMs, 0, 1000);
        int seed = MonkeySeed == 0 ? Random.Shared.Next(1, int.MaxValue) : Math.Abs(MonkeySeed);
        MonkeySeed = seed;

        DeviceLabStatus = $"Monkey stress running: {events} events, seed {seed}…";
        Stopwatch timer = Stopwatch.StartNew();
        AdbCommandResult result = await _adb.ExecuteAsync(
            device.Serial,
            ["shell", "monkey", "-p", package, "-s", seed.ToString(CultureInfo.InvariantCulture),
             "--throttle", throttle.ToString(CultureInfo.InvariantCulture), "-v", events.ToString(CultureInfo.InvariantCulture)],
            TimeSpan.FromMinutes(15),
            CancellationToken.None);
        timer.Stop();

        string combined = result.StandardOutput + "\n" + result.StandardError;
        bool fault = combined.Contains("CRASH", StringComparison.OrdinalIgnoreCase) ||
                     combined.Contains("NOT RESPONDING", StringComparison.OrdinalIgnoreCase) ||
                     combined.Contains("Monkey aborted", StringComparison.OrdinalIgnoreCase);

        StringBuilder report = new();
        report.AppendLine("MONKEY STRESS RUN");
        report.AppendLine($"Package:   {package}");
        report.AppendLine($"Device:    {device.FriendlyName} ({device.Serial})");
        report.AppendLine($"Seed:      {seed}   (reuse this seed to replay the same event sequence)");
        report.AppendLine($"Events:    {events} requested · throttle {throttle} ms");
        report.AppendLine($"Duration:  {timer.Elapsed:g}");
        report.AppendLine($"Exit code: {result.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "N/A"}{(result.TimedOut ? " (timeout)" : "")}");
        report.AppendLine($"Verdict:   {(fault ? "FAULT DETECTED" : "no crash or ANR detected")}");
        report.AppendLine();
        report.AppendLine("--- Monkey output ---");
        report.AppendLine(result.StandardOutput);
        if (!string.IsNullOrWhiteSpace(result.StandardError)) report.AppendLine(result.StandardError);

        if (fault && StopOnFailure)
        {
            string? artifacts = await CaptureFailureArtifactsAsync(device, package, $"monkey-seed-{seed}");
            if (artifacts is not null)
            {
                report.AppendLine();
                report.AppendLine("--- Captured on failure ---");
                report.AppendLine(artifacts);
            }
        }

        DeviceLabOutput = await SaveTextAsync(device, "monkey", report.ToString());
        TestRunnerSummary = fault
            ? $"Monkey found a crash or ANR with seed {seed}. Re-run the same seed after the fix to confirm it is gone."
            : $"Monkey finished {events} events with seed {seed} and detected no crash or ANR.";
        DeviceLabStatus = fault ? "Monkey run finished with a fault; artifacts were saved." : "Monkey run finished cleanly.";
    }

    [RelayCommand]
    private async Task ScanForCrashOrAnrAsync()
    {
        if (!TryGetDeviceAndPackage(out AndroidDevice device, out string package)) return;
        DeviceLabStatus = "Scanning crash and ANR evidence…";
        AdbCommandResult markers = await _adb.ExecuteAsync(
            device.Serial,
            ["shell", "sh", "-c", "logcat -d -v time -t 3000 | grep -E 'FATAL EXCEPTION|ANR in|Force finishing|has died|lowmemorykiller' | tail -n 120"],
            TimeSpan.FromSeconds(45),
            CancellationToken.None);
        AdbCommandResult crashBuffer = await _adb.ExecuteAsync(
            device.Serial,
            ["shell", "logcat", "-d", "-b", "crash", "-t", "200"],
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        int hits = markers.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        StringBuilder report = new();
        report.AppendLine("CRASH / ANR SCAN");
        report.AppendLine($"Package:  {package}");
        report.AppendLine($"Device:   {device.FriendlyName} ({device.Serial})");
        report.AppendLine($"Time:     {DateTimeOffset.Now:O}");
        report.AppendLine($"Signals:  {hits}");
        report.AppendLine();
        report.AppendLine("--- FATAL / ANR / LMK markers ---");
        report.AppendLine(markers.StandardOutput.Trim());
        report.AppendLine();
        report.AppendLine("--- crash buffer ---");
        report.AppendLine(crashBuffer.StandardOutput.Trim());

        DeviceLabOutput = await SaveTextAsync(device, "crash-scan", report.ToString());
        TestRunnerSummary = hits == 0
            ? "No crash, ANR or low-memory kill markers were found in the current buffers."
            : $"{hits} crash/ANR marker line(s) found. The full output is saved as a local report.";
        DeviceLabStatus = "Crash scan finished.";
    }

    [RelayCommand]
    private async Task DumpUiHierarchyAsync()
    {
        AndroidDevice? device = SelectedDevice;
        if (device is null) { DeviceLabStatus = "Select a connected Android device."; return; }
        DeviceLabStatus = "Dumping the UI hierarchy…";
        Directory.CreateDirectory(DeveloperLabDirectory);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string remote = "/sdcard/adm-window.xml";
        string local = Path.Combine(DeveloperLabDirectory, $"uiautomator-{SafeName(device.Serial)}-{stamp}.xml");

        AdbCommandResult dump = await _adb.ExecuteAsync(device.Serial, ["shell", "uiautomator", "dump", remote], TimeSpan.FromSeconds(60), CancellationToken.None);
        if (!dump.Success)
        {
            DeviceLabStatus = "UI dump failed: " + CleanError(dump);
            return;
        }
        AdbCommandResult pull = await _adb.ExecuteAsync(device.Serial, ["pull", remote, local], TimeSpan.FromSeconds(60), CancellationToken.None);
        _ = await _adb.ExecuteAsync(device.Serial, ["shell", "rm", "-f", remote], TimeSpan.FromSeconds(15), CancellationToken.None);
        if (!pull.Success || !File.Exists(local))
        {
            DeviceLabStatus = "UI dump pull failed: " + CleanError(pull);
            return;
        }

        string xml = await File.ReadAllTextAsync(local);
        string[] texts = Regex.Matches(xml, "text=\"(?<text>[^\"]+)\"")
            .Select(match => match.Groups["text"].Value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        string[] ids = Regex.Matches(xml, "resource-id=\"(?<id>[^\"]+)\"")
            .Select(match => match.Groups["id"].Value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        StringBuilder summary = new();
        summary.AppendLine("UI AUTOMATOR HIERARCHY SUMMARY");
        summary.AppendLine($"Device:    {device.FriendlyName} ({device.Serial})");
        summary.AppendLine($"XML file:  {local}");
        summary.AppendLine($"Visible texts ({texts.Length}):");
        foreach (string text in texts.Take(200)) summary.AppendLine("  " + text);
        summary.AppendLine($"Resource ids ({ids.Length}):");
        foreach (string id in ids.Take(200)) summary.AppendLine("  " + id);
        DeviceLabOutput = await SaveTextAsync(device, "uiautomator", summary.ToString());
        TestArtifacts.Add(local);
        SelectedTestArtifact = local;
        TestRunnerSummary = $"UI hierarchy saved: {texts.Length} texts and {ids.Length} resource ids are available for scenario assertions.";
        DeviceLabStatus = "UI hierarchy captured.";
    }

    [RelayCommand]
    private async Task ExportTestArtifactsAsync()
    {
        Directory.CreateDirectory(DeveloperLabDirectory);
        string[] files = Directory.GetFiles(DeveloperLabDirectory)
            .Where(path => TestArtifactPrefixes.Any(prefix => Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(400)
            .ToArray();
        if (files.Length == 0)
        {
            DeviceLabStatus = "No test artifacts were found in the session folder yet.";
            return;
        }

        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string zipPath = Path.Combine(DeveloperLabDirectory, $"test-run-{stamp}.zip");
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using (FileStream stream = File.Create(zipPath))
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

        TestArtifacts.Add(zipPath);
        SelectedTestArtifact = zipPath;
        DeviceLabStatus = $"Exported {files.Length} artifact(s) to {zipPath}";
        TestRunnerSummary = $"One ZIP now holds {files.Length} test artifact(s) — share this file with the whole run.";
    }

    [RelayCommand]
    private void OpenTestArtifact(string? path)
    {
        string? target = path ?? SelectedTestArtifact;
        if (string.IsNullOrWhiteSpace(target) || !File.Exists(target))
        {
            _dialogs.Notify("Select an existing artifact first.", error: true);
            return;
        }
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    [RelayCommand]
    private void ClearTestArtifacts()
    {
        TestArtifacts.Clear();
        SelectedTestArtifact = null;
    }

    /// <summary>
    /// Captures a screenshot plus the interesting logcat lines when a run fails, so the failure can be inspected later.
    /// </summary>
    internal async Task<string?> CaptureFailureArtifactsAsync(AndroidDevice device, string? package, string label)
    {
        Directory.CreateDirectory(DeveloperLabDirectory);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string safeLabel = SafeName(label);
        List<string> produced = [];

        try
        {
            AdbCommandResult logs = await _adb.ExecuteAsync(
                device.Serial,
                ["shell", "sh", "-c", "logcat -d -v time -t 2500 | grep -E 'FATAL EXCEPTION|ANR in|Force finishing|has died|StrictMode' | tail -n 200"],
                TimeSpan.FromSeconds(45),
                CancellationToken.None);
            string logPath = Path.Combine(DeveloperLabDirectory, $"test-artifact-{safeLabel}-{stamp}.log.txt");
            await File.WriteAllTextAsync(logPath, logs.StandardOutput + logs.StandardError, Encoding.UTF8);
            produced.Add(logPath);
            TestArtifacts.Add(logPath);
        }
        catch (Exception ex)
        {
            produced.Add("logcat capture failed: " + ex.Message);
        }

        try
        {
            MediaItem screenshot = await _media.CaptureScreenshotAsync(device, package, null, CancellationToken.None);
            string destination = Path.Combine(DeveloperLabDirectory, $"test-artifact-{safeLabel}-{stamp}.png");
            File.Copy(screenshot.LocalPath, destination, overwrite: true);
            produced.Add(destination);
            TestArtifacts.Add(destination);
            SelectedTestArtifact = destination;
        }
        catch (Exception ex)
        {
            produced.Add("screenshot capture failed: " + ex.Message);
        }

        return produced.Count == 0 ? null : string.Join(Environment.NewLine, produced);
    }
}
