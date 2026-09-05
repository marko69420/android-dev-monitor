using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;

namespace AndroidDevMonitor.App.Controls;

public sealed class Sparkline : FrameworkElement
{
    private static readonly Pen GridPen = CreatePen(Color.FromArgb(38, 25, 195, 212), 1);
    private static readonly Pen LinePen = CreatePen(Color.FromRgb(26, 190, 225), 1.5);
    private static readonly Brush AreaBrush = CreateAreaBrush();

    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values),
        typeof(IEnumerable),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnValuesChanged));
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum),
        typeof(double),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum),
        typeof(double),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Values { get => (IEnumerable?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public static readonly DependencyProperty ShowMissingAsZeroProperty = DependencyProperty.Register(
        nameof(ShowMissingAsZero), typeof(bool), typeof(Sparkline),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool ShowMissingAsZero { get => (bool)GetValue(ShowMissingAsZeroProperty); set => SetValue(ShowMissingAsZeroProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        var numbers = Values?.Cast<object>().Select(Convert.ToDouble).ToArray() ?? [];
        if (ShowMissingAsZero)
            numbers = numbers.Length == 0 ? [0, 0] : numbers.Select(value => double.IsFinite(value) ? value : 0).ToArray();
        var valid = numbers.Where(double.IsFinite).ToArray();
        var bottom = Math.Max(2, ActualHeight - 2);
        dc.DrawLine(GridPen, new Point(0, bottom), new Point(ActualWidth, bottom));
        if (valid.Length == 0) return;
        if (numbers.Length == 1) numbers = [numbers[0], numbers[0]];

        var min = double.IsNaN(Minimum) ? Math.Min(0, valid.Min()) : Minimum;
        var max = double.IsNaN(Maximum) ? valid.Max() : Maximum;
        if (max <= min) max = min + 1;
        var range = max - min;
        var points = new Point[numbers.Length];
        for (var i = 0; i < numbers.Length; i++)
        {
            var x = i * ActualWidth / (numbers.Length - 1);
            var normalized = Math.Clamp((numbers[i] - min) / range, 0, 1);
            points[i] = new Point(x, bottom - normalized * Math.Max(1, ActualHeight - 5));
        }

        var area = new StreamGeometry();
        using (var context = area.Open())
        {
            for (var i = 0; i < points.Length; i++)
            {
                if (!double.IsFinite(points[i].Y)) continue;
                context.BeginFigure(new Point(points[i].X, bottom), true, true);
                context.LineTo(points[i], true, false);
                while (i + 1 < points.Length && double.IsFinite(points[i + 1].Y)) context.LineTo(points[++i], true, false);
                context.LineTo(new Point(points[i].X, bottom), true, false);
            }
        }
        area.Freeze();

        var line = new StreamGeometry();
        using (var context = line.Open())
        {
            var start = true;
            foreach (var point in points)
            {
                if (!double.IsFinite(point.Y)) { start = true; continue; }
                if (start) context.BeginFigure(point, false, false);
                else context.LineTo(point, true, false);
                start = false;
            }
        }
        line.Freeze();
        dc.DrawGeometry(AreaBrush, null, area);
        dc.DrawGeometry(null, LinePen, line);
    }

    private static void OnValuesChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var sparkline = (Sparkline)dependencyObject;
        if (args.OldValue is INotifyCollectionChanged oldCollection) CollectionChangedEventManager.RemoveHandler(oldCollection, sparkline.OnCollectionChanged);
        if (args.NewValue is INotifyCollectionChanged newCollection) CollectionChangedEventManager.AddHandler(newCollection, sparkline.OnCollectionChanged);
        sparkline.InvalidateVisual();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => InvalidateVisual();

    private static Pen CreatePen(Color color, double thickness)
    {
        var brush = new SolidColorBrush(color); brush.Freeze();
        var pen = new Pen(brush, thickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze(); return pen;
    }

    private static Brush CreateAreaBrush()
    {
        var brush = new LinearGradientBrush(
            Color.FromArgb(105, 20, 168, 211),
            Color.FromArgb(8, 20, 168, 211),
            new Point(0, 0),
            new Point(0, 1));
        brush.Freeze(); return brush;
    }
}
