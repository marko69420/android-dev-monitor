using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using AndroidDevMonitor.App.ViewModels;

namespace AndroidDevMonitor.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel) { InitializeComponent(); DataContext = viewModel; }

    private async void OnLaunchApplicationMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ComboBox { IsDropDownOpen: true } selector || e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(selector, source) is not ComboBoxItem { Content: string package }) return;
        e.Handled = true;
        await LaunchApplicationAsync(selector, package);
    }

    /// <summary>
    /// Translates a click on the mirror image into real device coordinates and taps there.
    /// The image uses Uniform stretch, so the rendered frame can be letterboxed inside the control.
    /// </summary>
    private async void OnMirrorFrameClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Image image || DataContext is not MainViewModel viewModel) return;
        if (viewModel.MirrorFrame is not BitmapSource bitmap || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0) return;
        if (image.ActualWidth <= 0 || image.ActualHeight <= 0) return;

        double scale = Math.Min(image.ActualWidth / bitmap.PixelWidth, image.ActualHeight / bitmap.PixelHeight);
        if (scale <= 0) return;
        double offsetX = (image.ActualWidth - bitmap.PixelWidth * scale) / 2;
        double offsetY = (image.ActualHeight - bitmap.PixelHeight * scale) / 2;
        Point point = e.GetPosition(image);
        double deviceX = (point.X - offsetX) / scale;
        double deviceY = (point.Y - offsetY) / scale;
        if (deviceX < 0 || deviceY < 0 || deviceX > bitmap.PixelWidth || deviceY > bitmap.PixelHeight) return;

        ShowMirrorTapMarker(point);
        e.Handled = true;
        await viewModel.TapMirrorAsync((int)Math.Round(deviceX), (int)Math.Round(deviceY));
    }

    /// <summary>Draws a fading ring where the user tapped so the device-side touch is easy to follow.</summary>
    private void ShowMirrorTapMarker(Point position)
    {
        const double markerSize = 34;
        MirrorTapMarker.Visibility = Visibility.Visible;
        MirrorTapMarker.Opacity = 1;
        Canvas.SetLeft(MirrorTapMarker, position.X - markerSize / 2);
        Canvas.SetTop(MirrorTapMarker, position.Y - markerSize / 2);
        DoubleAnimation fade = new(1, 0, new Duration(TimeSpan.FromMilliseconds(650))) { BeginTime = TimeSpan.FromMilliseconds(150) };
        fade.Completed += (_, _) =>
        {
            MirrorTapMarker.Visibility = Visibility.Collapsed;
            MirrorTapMarker.BeginAnimation(OpacityProperty, null);
        };
        MirrorTapMarker.BeginAnimation(OpacityProperty, fade);
    }

    private async void OnLaunchApplicationKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not ComboBox { IsDropDownOpen: true } selector) return;
        var highlighted = selector.Items.Cast<object>().Select(item => selector.ItemContainerGenerator.ContainerFromItem(item))
            .OfType<ComboBoxItem>().FirstOrDefault(item => item.IsHighlighted);
        if (highlighted?.Content is not string package) return;
        e.Handled = true;
        await LaunchApplicationAsync(selector, package);
    }

    private async Task LaunchApplicationAsync(ComboBox selector, string package)
    {
        if (DataContext is not MainViewModel viewModel || !viewModel.LaunchSelectedCommand.CanExecute(null)) return;
        selector.SelectedItem = package;
        viewModel.SelectedPackage = package;
        selector.IsDropDownOpen = false;
        await viewModel.LaunchSelectedCommand.ExecuteAsync(null);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && !viewModel.CanClose()) e.Cancel = true;
        base.OnClosing(e);
    }
}
