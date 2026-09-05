using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AndroidDevMonitor.App.ViewModels;

public partial class TrackingMetricCardViewModel(string title, string unit, double maximum = double.NaN, bool useDeviceTotal = false) : ObservableObject
{
    public MetricCardViewModel Total { get; } = new(title, unit, 0, useDeviceTotal ? 100 : maximum);
    public MetricCardViewModel Active { get; } = new("Active", unit, 0, maximum);
    public MetricCardViewModel Background { get; } = new("Background", unit, 0, maximum);
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Chart), nameof(DisplayTitle))] private bool _isBackground;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PrimaryDescription))] private string _activePackage = "Waiting for foreground";
    [ObservableProperty] private string _backgroundState = "Choose a package";
    public MetricCardViewModel Chart => IsBackground ? Background : useDeviceTotal ? Total : Active;
    public string DisplayTitle => useDeviceTotal ? $"{title} · {(IsBackground ? "app" : "device")}" : title;
    public string PrimaryLabel => useDeviceTotal ? "DEVICE · " : "ACTIVE · ";
    public string PrimaryDescription => useDeviceTotal ? "Total RAM usage" : ActivePackage;
    public string PrimaryToolTip => useDeviceTotal ? "A: total device RAM (Android and all applications)" : "A: active application";
    public string SecondaryLabel => useDeviceTotal ? "APP · " : "BACKGROUND · ";
    [RelayCommand] private void ShowActive() => IsBackground = false;
    [RelayCommand] private void ShowBackground() => IsBackground = true;
    public void Clear()
    {
        Total.Clear(); Active.Clear(); Background.Clear(); ActivePackage = "Waiting for foreground";
        BackgroundState = "Choose a package";
    }
}
