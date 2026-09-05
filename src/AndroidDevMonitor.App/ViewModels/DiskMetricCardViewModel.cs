using AndroidDevMonitor.Core.Formatting;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AndroidDevMonitor.App.ViewModels;

public partial class DiskMetricCardViewModel : ObservableObject
{
    public MetricCardViewModel Write { get; } = new("Write", "B/s", 0);
    public MetricCardViewModel Read { get; } = new("Read", "B/s", 0);
    [ObservableProperty] private string _displayValue = "0/0";
    [ObservableProperty] private double _chartMaximum = 1;
    [ObservableProperty] private string _source = "Selected Android process disk I/O · bytes per second · shared chart scale";
    [ObservableProperty] private string _title = "App disk I/O · W/R";

    public void Push(double? write, double? read, string? deviceSource = null)
    {
        var title = deviceSource is null ? "App disk I/O · W/R" : "Device disk I/O · W/R";
        if (Title != title) { Write.Clear(); Read.Clear(); }
        Title = title;
        Source = deviceSource ?? "Selected Android process disk I/O · bytes per second · shared chart scale";
        Write.Push(write, UnitFormatter.Rate(write), "", Source);
        Read.Push(read, UnitFormatter.Rate(read), "", Source);
        DisplayValue = write is null && read is null ? "0/0" : $"{(write is null ? "0" : UnitFormatter.Rate(write))} / {(read is null ? "0" : UnitFormatter.Rate(read))}";
        ChartMaximum = Math.Max(1, Write.Values.Concat(Read.Values).Where(double.IsFinite).DefaultIfEmpty(0).Max());
    }

    public void Clear()
    {
        Write.Clear(); Read.Clear(); DisplayValue = "0/0"; ChartMaximum = 1;
        Title = "App disk I/O · W/R"; Source = "Waiting for disk data";
    }
}
