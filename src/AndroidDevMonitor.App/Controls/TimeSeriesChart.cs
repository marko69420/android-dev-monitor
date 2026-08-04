using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AndroidDevMonitor.App.ViewModels;

namespace AndroidDevMonitor.App.Controls;

public sealed class TimeSeriesChart : FrameworkElement
{
    private static readonly Pen GridPen = CreatePen(Color.FromArgb(42, 123, 151, 167), 1);
    private static readonly Pen PrimaryPen = CreatePen(Color.FromRgb(25, 195, 212), 1.7);
    private static readonly Pen SecondaryPen = CreatePen(Color.FromRgb(89, 138, 255), 1.4);
    private static readonly Pen MarkerPen = CreatePen(Color.FromRgb(235, 184, 79), 1);
    private static readonly Pen AlertPen = CreatePen(Color.FromRgb(239, 93, 93), 1.2);
    private static readonly Brush AreaBrush = CreateAreaBrush();

    public static readonly DependencyProperty PrimaryPointsProperty = RegisterEnumerable(nameof(PrimaryPoints));
    public static readonly DependencyProperty SecondaryPointsProperty = RegisterEnumerable(nameof(SecondaryPoints));
    public static readonly DependencyProperty AnnotationsProperty = RegisterEnumerable(nameof(Annotations));
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(TimeSeriesChart),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(TimeSeriesChart),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FullRangeProperty = DependencyProperty.Register(
        nameof(FullRange), typeof(bool), typeof(TimeSeriesChart),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PrimaryLabelProperty = DependencyProperty.Register(
        nameof(PrimaryLabel), typeof(string), typeof(TimeSeriesChart), new PropertyMetadata("Primary"));
    public static readonly DependencyProperty SecondaryLabelProperty = DependencyProperty.Register(
        nameof(SecondaryLabel), typeof(string), typeof(TimeSeriesChart), new PropertyMetadata("Secondary"));

    public IEnumerable? PrimaryPoints { get => (IEnumerable?)GetValue(PrimaryPointsProperty); set => SetValue(PrimaryPointsProperty, value); }
    public IEnumerable? SecondaryPoints { get => (IEnumerable?)GetValue(SecondaryPointsProperty); set => SetValue(SecondaryPointsProperty, value); }
    public IEnumerable? Annotations { get => (IEnumerable?)GetValue(AnnotationsProperty); set => SetValue(AnnotationsProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public bool FullRange { get => (bool)GetValue(FullRangeProperty); set => SetValue(FullRangeProperty, value); }
    public string PrimaryLabel { get => (string)GetValue(PrimaryLabelProperty); set => SetValue(PrimaryLabelProperty, value); }
    public string SecondaryLabel { get => (string)GetValue(SecondaryLabelProperty); set => SetValue(SecondaryLabelProperty, value); }

    public TimeSeriesChart()
    {
        ClipToBounds = true;
        Cursor = Cursors.Cross;
        MouseMove += OnMouseMove;
        MouseLeave += (_, _) => ToolTip = null;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth < 2 || ActualHeight < 2) return;
        DrawGrid(dc);

        var primary = ReadPoints(PrimaryPoints);
        var secondary = ReadPoints(SecondaryPoints);
        var all = primary.Concat(secondary).Where(point => point.Value.HasValue).ToArray();
        if (all.Length == 0) return;

        var end = all.Max(point => point.TimestampUtc);
        var start = FullRange
            ? all.Min(point => point.TimestampUtc)
            : end - Core.Configuration.MonitoringConstants.LiveChartWindow;
        var values = all.Select(point => point.Value!.Value).ToArray();
        var min = double.IsNaN(Minimum) ? Math.Min(0, values.Min()) : Minimum;
        var max = double.IsNaN(Maximum) ? values.Max() : Maximum;
        if (max <= min) max = min + 1;

        DrawSeries(dc, primary, start, end, min, max, PrimaryPen, AreaBrush);
        DrawSeries(dc, secondary, start, end, min, max, SecondaryPen, null);
        DrawAnnotations(dc, start, end);
    }

    private void DrawGrid(DrawingContext dc)
    {
        for (var i = 1; i < 4; i++)
        {
            var y = i * ActualHeight / 4;
            dc.DrawLine(GridPen, new Point(0, y), new Point(ActualWidth, y));
        }
        for (var i = 1; i < 6; i++)
        {
            var x = i * ActualWidth / 6;
            dc.DrawLine(GridPen, new Point(x, 0), new Point(x, ActualHeight));
        }
    }

    private void DrawSeries(
        DrawingContext dc,
        IReadOnlyList<ChartPoint> source,
        DateTimeOffset start,
        DateTimeOffset end,
        double min,
        double max,
        Pen pen,
        Brush? areaBrush)
    {
        var points = source.Where(point => point.TimestampUtc >= start && point.TimestampUtc <= end).ToArray();
        if (points.Length == 0) return;
        var groups = new List<List<Point>>();
        List<Point>? current = null;
        DateTimeOffset? previousTimestamp = null;

        foreach (var item in points)
        {
            if (!item.Value.HasValue)
            {
                current = null;
                previousTimestamp = null;
                continue;
            }
            if (previousTimestamp.HasValue && item.TimestampUtc - previousTimestamp > TimeSpan.FromSeconds(8)) current = null;
            current ??= AddGroup(groups);
            var x = (item.TimestampUtc - start).TotalMilliseconds / Math.Max(1, (end - start).TotalMilliseconds) * ActualWidth;
            var normalized = Math.Clamp((item.Value.Value - min) / (max - min), 0, 1);
            current.Add(new(x, ActualHeight - 2 - normalized * Math.Max(1, ActualHeight - 4)));
            previousTimestamp = item.TimestampUtc;
        }

        foreach (var group in groups.Where(group => group.Count > 0))
        {
            if (group.Count == 1)
            {
                dc.DrawEllipse(pen.Brush, null, group[0], 2.2, 2.2);
                continue;
            }
            if (areaBrush is not null)
            {
                var area = new StreamGeometry();
                using (var context = area.Open())
                {
                    context.BeginFigure(new(group[0].X, ActualHeight), true, true);
                    context.LineTo(group[0], true, false);
                    foreach (var point in group.Skip(1)) context.LineTo(point, true, false);
                    context.LineTo(new(group[^1].X, ActualHeight), true, false);
                }
                area.Freeze();
                dc.DrawGeometry(areaBrush, null, area);
            }
            var line = new StreamGeometry();
            using (var context = line.Open())
            {
                context.BeginFigure(group[0], false, false);
                foreach (var point in group.Skip(1)) context.LineTo(point, true, false);
            }
            line.Freeze();
            dc.DrawGeometry(null, pen, line);
        }
    }

    private void DrawAnnotations(DrawingContext dc, DateTimeOffset start, DateTimeOffset end)
    {
        if (Annotations is null) return;
        foreach (var annotation in Annotations.Cast<object>().OfType<ChartAnnotation>().Where(item => item.TimestampUtc >= start && item.TimestampUtc <= end))
        {
            var x = (annotation.TimestampUtc - start).TotalMilliseconds / Math.Max(1, (end - start).TotalMilliseconds) * ActualWidth;
            dc.DrawLine(annotation.IsAlert ? AlertPen : MarkerPen, new(x, 0), new(x, ActualHeight));
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs args)
    {
        var points = ReadPoints(PrimaryPoints).Where(point => point.Value.HasValue).ToArray();
        if (points.Length == 0 || ActualWidth <= 0) return;
        var end = points.Max(point => point.TimestampUtc);
        var start = FullRange
            ? points.Min(point => point.TimestampUtc)
            : end - Core.Configuration.MonitoringConstants.LiveChartWindow;
        var target = start + TimeSpan.FromMilliseconds(args.GetPosition(this).X / ActualWidth * (end - start).TotalMilliseconds);
        var primary = points.MinBy(point => Math.Abs((point.TimestampUtc - target).TotalMilliseconds));
        if (primary is null) return;
        var secondary = ReadPoints(SecondaryPoints)
            .Where(point => point.Value.HasValue)
            .MinBy(point => Math.Abs((point.TimestampUtc - primary.TimestampUtc).TotalMilliseconds));
        ToolTip = $"{primary.TimestampUtc.ToLocalTime():HH:mm:ss}\n{PrimaryLabel}: {primary.Value:N1}" +
                  (secondary?.Value is double value && !string.IsNullOrWhiteSpace(SecondaryLabel) ? $"\n{SecondaryLabel}: {value:N1}" : "");
    }

    private static List<Point> AddGroup(ICollection<List<Point>> groups)
    {
        var group = new List<Point>();
        groups.Add(group);
        return group;
    }

    private static IReadOnlyList<ChartPoint> ReadPoints(IEnumerable? values) =>
        values?.Cast<object>().OfType<ChartPoint>().OrderBy(point => point.TimestampUtc).ToArray() ?? [];

    private static DependencyProperty RegisterEnumerable(string name) =>
        DependencyProperty.Register(name, typeof(IEnumerable), typeof(TimeSeriesChart),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnEnumerableChanged));

    private static void OnEnumerableChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var chart = (TimeSeriesChart)dependencyObject;
        if (args.OldValue is INotifyCollectionChanged oldCollection) CollectionChangedEventManager.RemoveHandler(oldCollection, chart.OnCollectionChanged);
        if (args.NewValue is INotifyCollectionChanged newCollection) CollectionChangedEventManager.AddHandler(newCollection, chart.OnCollectionChanged);
        chart.InvalidateVisual();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => InvalidateVisual();

    private static Pen CreatePen(Color color, double thickness)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var pen = new Pen(brush, thickness)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        pen.Freeze();
        return pen;
    }

    private static Brush CreateAreaBrush()
    {
        var brush = new LinearGradientBrush(
            Color.FromArgb(70, 20, 168, 211),
            Color.FromArgb(4, 20, 168, 211),
            new Point(0, 0),
            new Point(0, 1));
        brush.Freeze();
        return brush;
    }
}
