using System.Collections;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using AndroidDevMonitor.Core.Configuration;
using AndroidDevMonitor.Presentation.ViewModels;

namespace AndroidDevMonitor.Desktop.Controls;

/// <summary>A time-based line chart with an optional second series and marker/alert annotations.</summary>
public sealed class TimeSeriesChart : Control
{
    private static readonly IPen GridPen = CreatePen(Color.FromArgb(42, 123, 151, 167), 1);
    private static readonly IPen PrimaryPen = CreatePen(Color.FromRgb(25, 195, 212), 1.7);
    private static readonly IPen SecondaryPen = CreatePen(Color.FromRgb(89, 138, 255), 1.4);
    private static readonly IPen MarkerPen = CreatePen(Color.FromRgb(235, 184, 79), 1);
    private static readonly IPen AlertPen = CreatePen(Color.FromRgb(239, 93, 93), 1.2);
    private static readonly IBrush AreaBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.FromArgb(70, 20, 168, 211), 0), new GradientStop(Color.FromArgb(4, 20, 168, 211), 1) }
    }.ToImmutable();

    public static readonly StyledProperty<IEnumerable?> PrimaryPointsProperty = AvaloniaProperty.Register<TimeSeriesChart, IEnumerable?>(nameof(PrimaryPoints));
    public static readonly StyledProperty<IEnumerable?> SecondaryPointsProperty = AvaloniaProperty.Register<TimeSeriesChart, IEnumerable?>(nameof(SecondaryPoints));
    public static readonly StyledProperty<IEnumerable?> AnnotationsProperty = AvaloniaProperty.Register<TimeSeriesChart, IEnumerable?>(nameof(Annotations));
    public static readonly StyledProperty<double> MinimumProperty = AvaloniaProperty.Register<TimeSeriesChart, double>(nameof(Minimum), double.NaN);
    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<TimeSeriesChart, double>(nameof(Maximum), double.NaN);
    public static readonly StyledProperty<bool> FullRangeProperty = AvaloniaProperty.Register<TimeSeriesChart, bool>(nameof(FullRange));
    public static readonly StyledProperty<string> PrimaryLabelProperty = AvaloniaProperty.Register<TimeSeriesChart, string>(nameof(PrimaryLabel), "Primary");
    public static readonly StyledProperty<string> SecondaryLabelProperty = AvaloniaProperty.Register<TimeSeriesChart, string>(nameof(SecondaryLabel), "Secondary");

    static TimeSeriesChart() =>
        AffectsRender<TimeSeriesChart>(PrimaryPointsProperty, SecondaryPointsProperty, AnnotationsProperty, MinimumProperty, MaximumProperty, FullRangeProperty);

    public TimeSeriesChart()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    public IEnumerable? PrimaryPoints { get => GetValue(PrimaryPointsProperty); set => SetValue(PrimaryPointsProperty, value); }
    public IEnumerable? SecondaryPoints { get => GetValue(SecondaryPointsProperty); set => SetValue(SecondaryPointsProperty, value); }
    public IEnumerable? Annotations { get => GetValue(AnnotationsProperty); set => SetValue(AnnotationsProperty, value); }
    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public bool FullRange { get => GetValue(FullRangeProperty); set => SetValue(FullRangeProperty, value); }
    public string PrimaryLabel { get => GetValue(PrimaryLabelProperty); set => SetValue(PrimaryLabelProperty, value); }
    public string SecondaryLabel { get => GetValue(SecondaryLabelProperty); set => SetValue(SecondaryLabelProperty, value); }

    public override void Render(DrawingContext context)
    {
        double width = Bounds.Width, height = Bounds.Height;
        if (width < 2 || height < 2) return;
        DrawGrid(context, width, height);

        IReadOnlyList<ChartPoint> primary = ReadPoints(PrimaryPoints);
        IReadOnlyList<ChartPoint> secondary = ReadPoints(SecondaryPoints);
        ChartPoint[] all = primary.Concat(secondary).Where(point => point.Value.HasValue).ToArray();
        if (all.Length == 0) return;

        DateTimeOffset end = all.Max(point => point.TimestampUtc);
        DateTimeOffset start = FullRange ? all.Min(point => point.TimestampUtc) : end - MonitoringConstants.LiveChartWindow;
        double[] values = all.Select(point => point.Value!.Value).ToArray();
        double min = double.IsNaN(Minimum) ? Math.Min(0, values.Min()) : Minimum;
        double max = double.IsNaN(Maximum) ? values.Max() : Maximum;
        if (max <= min) max = min + 1;

        DrawSeries(context, primary, start, end, min, max, width, height, PrimaryPen, AreaBrush);
        DrawSeries(context, secondary, start, end, min, max, width, height, SecondaryPen, null);
        DrawAnnotations(context, start, end, width, height);
    }

    private static void DrawGrid(DrawingContext context, double width, double height)
    {
        for (int i = 1; i < 4; i++)
        {
            double y = i * height / 4;
            context.DrawLine(GridPen, new Point(0, y), new Point(width, y));
        }
        for (int i = 1; i < 6; i++)
        {
            double x = i * width / 6;
            context.DrawLine(GridPen, new Point(x, 0), new Point(x, height));
        }
    }

    private static void DrawSeries(DrawingContext context, IReadOnlyList<ChartPoint> source, DateTimeOffset start, DateTimeOffset end,
        double min, double max, double width, double height, IPen pen, IBrush? areaBrush)
    {
        ChartPoint[] points = source.Where(point => point.TimestampUtc >= start && point.TimestampUtc <= end).ToArray();
        if (points.Length == 0) return;
        List<List<Point>> groups = [];
        List<Point>? current = null;
        DateTimeOffset? previousTimestamp = null;

        foreach (ChartPoint item in points)
        {
            if (!item.Value.HasValue)
            {
                current = null;
                previousTimestamp = null;
                continue;
            }
            // A gap of more than a few samples means the collector was paused or disconnected: break the line.
            if (previousTimestamp.HasValue && item.TimestampUtc - previousTimestamp > TimeSpan.FromSeconds(8)) current = null;
            if (current is null)
            {
                current = [];
                groups.Add(current);
            }
            double x = (item.TimestampUtc - start).TotalMilliseconds / Math.Max(1, (end - start).TotalMilliseconds) * width;
            double normalized = Math.Clamp((item.Value.Value - min) / (max - min), 0, 1);
            current.Add(new Point(x, height - 2 - normalized * Math.Max(1, height - 4)));
            previousTimestamp = item.TimestampUtc;
        }

        foreach (List<Point> group in groups.Where(group => group.Count > 0))
        {
            if (group.Count == 1)
            {
                context.DrawEllipse(pen.Brush, null, group[0], 2.2, 2.2);
                continue;
            }
            if (areaBrush is not null)
            {
                StreamGeometry area = new();
                using (StreamGeometryContext geometry = area.Open())
                {
                    geometry.BeginFigure(new Point(group[0].X, height), true);
                    foreach (Point point in group) geometry.LineTo(point);
                    geometry.LineTo(new Point(group[^1].X, height));
                    geometry.EndFigure(true);
                }
                context.DrawGeometry(areaBrush, null, area);
            }
            StreamGeometry line = new();
            using (StreamGeometryContext geometry = line.Open())
            {
                geometry.BeginFigure(group[0], false);
                foreach (Point point in group.Skip(1)) geometry.LineTo(point);
                geometry.EndFigure(false);
            }
            context.DrawGeometry(null, pen, line);
        }
    }

    private void DrawAnnotations(DrawingContext context, DateTimeOffset start, DateTimeOffset end, double width, double height)
    {
        if (Annotations is null) return;
        foreach (ChartAnnotation annotation in Annotations.OfType<ChartAnnotation>().Where(item => item.TimestampUtc >= start && item.TimestampUtc <= end))
        {
            double x = (annotation.TimestampUtc - start).TotalMilliseconds / Math.Max(1, (end - start).TotalMilliseconds) * width;
            context.DrawLine(annotation.IsAlert ? AlertPen : MarkerPen, new Point(x, 0), new Point(x, height));
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        ToolTip.SetTip(this, DescribeAt(e.GetPosition(this).X));
        ToolTip.SetIsOpen(this, ToolTip.GetTip(this) is not null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        ToolTip.SetIsOpen(this, false);
        ToolTip.SetTip(this, null);
    }

    /// <summary>The tooltip text for the sample nearest to a horizontal position, or null when there is no data.</summary>
    internal string? DescribeAt(double x)
    {
        ChartPoint[] points = ReadPoints(PrimaryPoints).Where(point => point.Value.HasValue).ToArray();
        if (points.Length == 0 || Bounds.Width <= 0) return null;
        DateTimeOffset end = points.Max(point => point.TimestampUtc);
        DateTimeOffset start = FullRange ? points.Min(point => point.TimestampUtc) : end - MonitoringConstants.LiveChartWindow;
        DateTimeOffset target = start + TimeSpan.FromMilliseconds(x / Bounds.Width * (end - start).TotalMilliseconds);
        ChartPoint? primary = points.MinBy(point => Math.Abs((point.TimestampUtc - target).TotalMilliseconds));
        if (primary is null) return null;
        ChartPoint? secondary = ReadPoints(SecondaryPoints)
            .Where(point => point.Value.HasValue)
            .MinBy(point => Math.Abs((point.TimestampUtc - primary.TimestampUtc).TotalMilliseconds));
        return $"{primary.TimestampUtc.ToLocalTime():HH:mm:ss}\n{PrimaryLabel}: {primary.Value:N1}" +
               (secondary?.Value is double value && !string.IsNullOrWhiteSpace(SecondaryLabel) ? $"\n{SecondaryLabel}: {value:N1}" : "");
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != PrimaryPointsProperty && change.Property != SecondaryPointsProperty && change.Property != AnnotationsProperty) return;
        if (change.OldValue is INotifyCollectionChanged oldCollection) oldCollection.CollectionChanged -= OnCollectionChanged;
        if (change.NewValue is INotifyCollectionChanged newCollection) newCollection.CollectionChanged += OnCollectionChanged;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    private static IReadOnlyList<ChartPoint> ReadPoints(IEnumerable? values) =>
        values?.OfType<ChartPoint>().OrderBy(point => point.TimestampUtc).ToArray() ?? [];

    private static IPen CreatePen(Color color, double thickness) =>
        new ImmutablePen(new ImmutableSolidColorBrush(color), thickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
}
