using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AndroidDevMonitor.App.Converters;

public sealed class EqualityToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
public sealed class NullToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch { null => "N/A", double d => d.ToString(parameter?.ToString() ?? "N1", culture), long l => Format(l), _ => value.ToString() ?? "N/A" };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    private static string Format(long bytes) { string[] units = ["B", "KB", "MB", "GB"]; var n = (double)bytes; var i = 0; while (n >= 1024 && i < units.Length - 1) { n /= 1024; i++; } return $"{n:N1} {units[i]}"; }
}

public sealed class PageSelectionBrushConverter : IMultiValueConverter
{
    private static readonly Brush SelectedBrush = new SolidColorBrush(Color.FromRgb(27, 42, 50));

    static PageSelectionBrushConverter() => SelectedBrush.Freeze();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length >= 2 &&
        string.Equals(values[0]?.ToString(), values[1]?.ToString(), StringComparison.Ordinal)
            ? SelectedBrush
            : Brushes.Transparent;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        targetTypes.Select(_ => Binding.DoNothing).ToArray();
}

public sealed class NavigationIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value?.ToString() switch
        {
            "Overview" => "\uE9D2",
            "Instances" => "\uE80A",
            "Media" => "\uE91B",
            "Performance" => "\uE9D9",
            "Logs" => "\uE8A5",
            "File Explorer" => "\uED25",
            "Network" => "\uE968",
            "ADB Shell" => "\uE756",
            "Automation" => "\uE945",
            "Alerts" => "\uEA39",
            "Settings" => "\uE713",
            _ => "\uE10C"
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class MediaThumbnailConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var path = value as string;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = parameter is string text && int.TryParse(text, out var width) ? width : 480;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
