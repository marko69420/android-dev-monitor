using CommunityToolkit.Mvvm.ComponentModel;
using AndroidDevMonitor.Core.Configuration;

namespace AndroidDevMonitor.App.ViewModels;

public partial class MetricCardViewModel(string title, string unit, double chartMinimum = double.NaN, double chartMaximum = double.NaN) : ObservableObject
{
    private readonly Queue<DateTimeOffset> _timestamps = [];
    public string Title { get; } = title;
    public string Unit { get; } = unit;
    public double ChartMinimum { get; } = chartMinimum;
    public double ChartMaximum { get; } = chartMaximum;
    [ObservableProperty] private string _displayValue = "Waiting for data";
    [ObservableProperty] private string _secondaryValue = "";
    [ObservableProperty] private string _source = "Waiting for data";
    [ObservableProperty] private bool _isAvailable;
    public System.Collections.ObjectModel.ObservableCollection<double> Values { get; } = [];

    public void Push(double? value, string display, string secondary, string source)
    {
        DisplayValue = display; SecondaryValue = secondary; Source = source; IsAvailable = value.HasValue;
        var now = DateTimeOffset.UtcNow;
        Values.Add(value ?? double.NaN);
        _timestamps.Enqueue(now);
        var cutoff = now - MonitoringConstants.SummaryWindow;
        while (_timestamps.TryPeek(out var timestamp) && timestamp < cutoff)
        {
            _timestamps.Dequeue();
            if (Values.Count > 0) Values.RemoveAt(0);
        }
    }

    public void Clear()
    {
        Values.Clear(); _timestamps.Clear(); DisplayValue = "Waiting for data";
        SecondaryValue = ""; Source = "Waiting for data"; IsAvailable = false;
    }
}
