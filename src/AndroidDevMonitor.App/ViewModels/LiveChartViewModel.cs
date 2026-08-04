using System.Collections.ObjectModel;
using AndroidDevMonitor.Core.Configuration;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AndroidDevMonitor.App.ViewModels;

public sealed record ChartPoint(DateTimeOffset TimestampUtc, double? Value);
public sealed record ChartAnnotation(DateTimeOffset TimestampUtc, string Label, bool IsAlert);

public partial class LiveChartViewModel(
    string title,
    string primaryLabel,
    string secondaryLabel,
    string unit,
    double minimum = double.NaN,
    double maximum = double.NaN,
    TimeSpan? window = null) : ObservableObject
{
    private readonly TimeSpan _window = window ?? MonitoringConstants.LiveChartWindow;
    public string Title { get; } = title;
    public string PrimaryLabel { get; } = primaryLabel;
    public string SecondaryLabel { get; } = secondaryLabel;
    public string Unit { get; } = unit;
    public double Minimum { get; } = minimum;
    public double Maximum { get; } = maximum;
    public bool IsFullSession => _window == TimeSpan.MaxValue;
    public ObservableCollection<ChartPoint> PrimaryPoints { get; } = [];
    public ObservableCollection<ChartPoint> SecondaryPoints { get; } = [];
    public ObservableCollection<ChartAnnotation> Annotations { get; } = [];

    [ObservableProperty] private string _currentText = "Waiting for data";
    [ObservableProperty] private string _statisticsText = "Min —   Avg —   Max —";
    [ObservableProperty] private string _availabilityText = "Waiting";

    public void Push(DateTimeOffset timestampUtc, double? primary, double? secondary = null, bool preserveMissing = true)
    {
        if (primary.HasValue || preserveMissing) PrimaryPoints.Add(new(timestampUtc, primary));
        if (secondary.HasValue || preserveMissing) SecondaryPoints.Add(new(timestampUtc, secondary));
        Trim(timestampUtc);
        UpdateStatistics(primary, secondary);
    }

    public void AddAnnotation(DateTimeOffset timestampUtc, string label, bool isAlert)
    {
        Annotations.Add(new(timestampUtc, label, isAlert));
        Trim(timestampUtc);
    }

    public void Clear()
    {
        PrimaryPoints.Clear();
        SecondaryPoints.Clear();
        Annotations.Clear();
        CurrentText = "Waiting for data";
        StatisticsText = "Min —   Avg —   Max —";
        AvailabilityText = "Waiting";
    }

    private void Trim(DateTimeOffset now)
    {
        if (_window == TimeSpan.MaxValue) return;
        var cutoff = now - _window;
        while (PrimaryPoints.Count > 0 && PrimaryPoints[0].TimestampUtc < cutoff) PrimaryPoints.RemoveAt(0);
        while (SecondaryPoints.Count > 0 && SecondaryPoints[0].TimestampUtc < cutoff) SecondaryPoints.RemoveAt(0);
        while (Annotations.Count > 0 && Annotations[0].TimestampUtc < cutoff) Annotations.RemoveAt(0);
    }

    private void UpdateStatistics(double? primary, double? secondary)
    {
        var values = PrimaryPoints.Select(point => point.Value).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        if (values.Length == 0)
        {
            CurrentText = "N/A";
            StatisticsText = "Min —   Avg —   Max —";
            AvailabilityText = "N/A";
            return;
        }

        CurrentText = $"{PrimaryLabel} {Format(primary)}" +
                      (secondary.HasValue && !string.IsNullOrWhiteSpace(SecondaryLabel) ? $"   {SecondaryLabel} {Format(secondary)}" : "");
        StatisticsText = $"Min {Format(values.Min())}   Avg {Format(values.Average())}   Max {Format(values.Max())}";
        AvailabilityText = _window == TimeSpan.MaxValue ? "Stored · Full session" : "Live · Last 10 minutes";
    }

    private string Format(double? value)
    {
        if (!value.HasValue) return "N/A";
        if (Unit == "B") return FormatBytes(value.Value);
        if (Unit == "B/s") return $"{FormatBytes(value.Value)}/s";
        return $"{value.Value:N1}{Unit}";
    }

    private static string FormatBytes(double value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var index = 0;
        while (Math.Abs(value) >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }
        return $"{value:N1} {units[index]}";
    }
}
