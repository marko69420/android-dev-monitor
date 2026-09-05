using System.Collections.ObjectModel;
using AndroidDevMonitor.Core.Formatting;
using AndroidDevMonitor.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AndroidDevMonitor.App.ViewModels;

public partial class MainViewModel
{
    [RelayCommand]
    private async Task LaunchSelectedAsync()
    {
        var device = SelectedDevice;
        var package = SelectedPackage;
        if (device is null || string.IsNullOrWhiteSpace(package)) return;
        try
        {
            var launched = await ExecuteAutomationActionAsync("Launch", device, package, SelectedApkPath, CancellationToken.None, false);
            StatusMessage = launched ? "Opened: " + package : "Could not open: " + package;
        }
        catch (Exception ex)
        {
            StatusMessage = "Could not open: " + package;
            AppendAutomationLog("FAILED · Launch · " + ex.Message);
        }
    }
    public TrackingMetricCardViewModel CpuTracking { get; } = new("CPU usage", "%", 100);
    public TrackingMetricCardViewModel MemoryTracking { get; } = new("Memory", "B", useDeviceTotal: true);
    public ObservableCollection<string> BackgroundPackages { get; } = [];
    public ObservableCollection<ApplicationChoice> BackgroundApplications { get; } = [];
    public sealed record ApplicationChoice(string PackageName, string DisplayName)
    {
        public string Description => $"{DisplayName}\n{PackageName}";
        public override string ToString() => DisplayName;
    }

    private ApplicationChoice CreateApplicationChoice(string package)
    {
        var name = package switch
        {
            "com.android.camera2" => "Camera",
            "com.android.stk" => "SIM Toolkit",
            "com.google.android.apps.docs" => "Drive",
            "com.google.android.apps.safetyhub" => "Personal Safety",
            "com.google.android.deskclock" or "com.android.deskclock" => "Clock",
            "com.google.android.dialer" or "com.android.dialer" => "Phone",
            "com.google.android.documentsui" or "com.android.documentsui" => "Files",
            "com.google.android.gm" => "Gmail",
            "com.google.android.googlequicksearchbox" => "Google",
            "com.zhiliaoapp.musically" or "com.ss.android.ugc.trill" => "TikTok",
            _ => FriendlyPackageName(package)
        };
        if (name.StartsWith("Google ", StringComparison.Ordinal)) name = name[7..];
        else if (name.StartsWith("Android ", StringComparison.Ordinal)) name = name[8..];
        return new(package, name);
    }
    [ObservableProperty] private string? _backgroundPackage;
    private string? _installedPackagesSerial;
    private string? _activePackage;

    partial void OnBackgroundPackageChanged(string? value)
    {
        CpuTracking.Background.Clear(); MemoryTracking.Background.Clear();
        UpdateTracking(_allProcesses, _activePackage, false);
    }

    private void ResetTracking()
    {
        CpuTracking.Clear(); MemoryTracking.Clear(); BackgroundPackage = null;
        BackgroundPackages.Clear(); BackgroundApplications.Clear(); _installedPackagesSerial = null; _activePackage = null;
    }

    private void UpdateTracking(IReadOnlyList<AndroidProcess> processes, string? activePackage, bool append = true)
    {
        if (_activePackage != activePackage)
        {
            CpuTracking.Active.Clear(); MemoryTracking.Active.Clear();
        }
        _activePackage = activePackage;
        CpuTracking.ActivePackage = MemoryTracking.ActivePackage = activePackage ?? "Unknown foreground";
        var active = PackageMetrics.Aggregate(processes, activePackage);
        var background = PackageMetrics.Aggregate(processes, BackgroundPackage);
        var state = BackgroundPackage is null ? "Choose a package" : BackgroundPackage == activePackage ? "On screen" :
            !background.IsRunning ? "Not running" : activePackage is null ? "Foreground unknown" : "In background";
        CpuTracking.BackgroundState = MemoryTracking.BackgroundState = state;
        var cpuText = background.CpuPercent is double cpu ? $"{cpu:N1}%" : "N/A";
        var memoryText = UnitFormatter.Bytes(background.RssBytes);
        if (append)
        {
            CpuTracking.Active.Push(active.CpuPercent, active.CpuPercent is double a ? $"{a:N1}%" : "N/A", "", "Package processes · total CPU capacity");
            MemoryTracking.Active.Push(active.RssBytes, UnitFormatter.Bytes(active.RssBytes), "", "Sum of package process RSS · shared pages may overlap");
            CpuTracking.Background.Push(background.CpuPercent, cpuText, "", state);
            MemoryTracking.Background.Push(background.RssBytes, memoryText, "", "Sum of package process RSS · " + state);
        }
        else
        {
            CpuTracking.Background.DisplayValue = cpuText;
            MemoryTracking.Background.DisplayValue = memoryText;
        }
    }
}
