using System.Collections;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace AndroidDevMonitor.Desktop.Controls;

/// <summary>A small filled line chart for the overview cards.</summary>
public sealed class Sparkline : Control
{
    private static readonly IPen GridPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(38, 25, 195, 212)), 1);
    private static readonly IPen LinePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(26, 190, 225)), 1.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
    private static readonly IBrush AreaBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.FromArgb(105, 20, 168, 211), 0), new GradientStop(Color.FromArgb(8, 20, 168, 211), 1) }
    }.ToImmutable();

    public static readonly StyledProperty<IEnumerable?> ValuesProperty = AvaloniaProperty.Register<Sparkline, IEnumerable?>(nameof(Values));
    public static readonly StyledProperty<double> MinimumProperty = AvaloniaProperty.Register<Sparkline, double>(nameof(Minimum), double.NaN);
    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<Sparkline, double>(nameof(Maximum), double.NaN);
    public static readonly StyledProperty<bool> ShowMissingAsZeroProperty = AvaloniaProperty.Register<Sparkline, bool>(nameof(ShowMissingAsZero));

    static Sparkline() => AffectsRender<Sparkline>(ValuesProperty, MinimumProperty, MaximumProperty, ShowMissingAsZeroProperty);

    public IEnumerable? Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public bool ShowMissingAsZero { get => GetValue(ShowMissingAsZeroProperty); set => SetValue(ShowMissingAsZeroProperty, value); }

    public override void Render(DrawingContext context)
    {
        double width = Bounds.Width, height = Bounds.Height;
        if (width <= 0 || height <= 0) return;

        double[] numbers = Values?.Cast<object?>().Select(ToDouble).ToArray() ?? [];
        if (ShowMissingAsZero)
            numbers = numbers.Length == 0 ? [0, 0] : numbers.Select(value => double.IsFinite(value) ? value : 0).ToArray();
        double[] valid = numbers.Where(double.IsFinite).ToArray();
        double bottom = Math.Max(2, height - 2);
        context.DrawLine(GridPen, new Point(0, bottom), new Point(width, bottom));
        if (valid.Length == 0) return;
        if (numbers.Length == 1) numbers = [numbers[0], numbers[0]];

        double min = double.IsNaN(Minimum) ? Math.Min(0, valid.Min()) : Minimum;
        double max = double.IsNaN(Maximum) ? valid.Max() : Maximum;
        if (max <= min) max = min + 1;
        double range = max - min;
        Point[] points = new Point[numbers.Length];
        for (int i = 0; i < numbers.Length; i++)
        {
            double x = i * width / (numbers.Length - 1);
            double normalized = double.IsFinite(numbers[i]) ? Math.Clamp((numbers[i] - min) / range, 0, 1) : double.NaN;
            points[i] = new Point(x, bottom - normalized * Math.Max(1, height - 5));
        }

        StreamGeometry area = new();
        using (StreamGeometryContext geometry = area.Open())
        {
            for (int i = 0; i < points.Length; i++)
            {
                if (!double.IsFinite(points[i].Y)) continue;
                geometry.BeginFigure(new Point(points[i].X, bottom), true);
                geometry.LineTo(points[i]);
                while (i + 1 < points.Length && double.IsFinite(points[i + 1].Y)) geometry.LineTo(points[++i]);
                geometry.LineTo(new Point(points[i].X, bottom));
                geometry.EndFigure(true);
            }
        }

        StreamGeometry line = new();
        using (StreamGeometryContext geometry = line.Open())
        {
            bool open = false;
            foreach (Point point in points)
            {
                if (!double.IsFinite(point.Y))
                {
                    if (open) geometry.EndFigure(false);
                    open = false;
                    continue;
                }
                if (!open) geometry.BeginFigure(point, false);
                else geometry.LineTo(point);
                open = true;
            }
            if (open) geometry.EndFigure(false);
        }
        context.DrawGeometry(AreaBrush, null, area);
        context.DrawGeometry(null, LinePen, line);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ValuesProperty) return;
        if (change.OldValue is INotifyCollectionChanged oldCollection) oldCollection.CollectionChanged -= OnCollectionChanged;
        if (change.NewValue is INotifyCollectionChanged newCollection) newCollection.CollectionChanged += OnCollectionChanged;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    private static double ToDouble(object? value) => value switch
    {
        null => double.NaN,
        double d => d,
        _ => Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)
    };
}
