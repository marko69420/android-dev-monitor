using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using AndroidDevMonitor.Presentation.ViewModels;
using Serilog;

namespace AndroidDevMonitor.Desktop;

public partial class MainWindow : Window
{
    private static readonly IBrush EvenRowBrush = new SolidColorBrush(Color.Parse("#11191F"));
    private static readonly IBrush OddRowBrush = new SolidColorBrush(Color.Parse("#131C22"));
    private static readonly IBrush ProcessEvenRowBrush = new SolidColorBrush(Color.Parse("#111B22"));
    private static readonly IBrush ProcessOddRowBrush = new SolidColorBrush(Color.Parse("#141F27"));
    private static readonly IBrush RelatedRowBrush = new SolidColorBrush(Color.Parse("#123D49"));

    private bool _shutdownStarted;
    private bool _shutdownComplete;
    private bool _logScrollQueued;
    private MainViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        foreach (DataGrid grid in this.GetLogicalDescendants().OfType<DataGrid>())
            grid.LoadingRow += OnLoadingRow;
        ProcessTable.SelectionChanged += (_, _) => RefreshRelatedProcessRows();
        LaunchApplicationSelector.ContainerPrepared += OnLaunchContainerPrepared;
        LaunchApplicationSelector.AddHandler(KeyDownEvent, OnLaunchApplicationKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    public MainWindow(MainViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.LogView.CollectionChanged -= OnLogViewChanged;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
        _viewModel = DataContext as MainViewModel;
        if (_viewModel is not null)
        {
            _viewModel.LogView.CollectionChanged += OnLogViewChanged;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    /// <summary>Alternating row colors, and the process table's parent/child selection highlight.</summary>
    private void OnLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (ReferenceEquals(sender, ProcessTable)) ApplyProcessRowBackground(e.Row);
        else e.Row.Background = e.Row.Index % 2 == 0 ? EvenRowBrush : OddRowBrush;
    }

    private void RefreshRelatedProcessRows()
    {
        foreach (DataGridRow row in ProcessTable.GetVisualDescendantsOfType<DataGridRow>())
            ApplyProcessRowBackground(row);
    }

    /// <summary>Rows that belong to the selected process group are highlighted along with it.</summary>
    private void ApplyProcessRowBackground(DataGridRow row)
    {
        bool related = row.DataContext is ProcessDisplayRow item && ProcessTable.SelectedItem is ProcessDisplayRow selected &&
                       !ReferenceEquals(item, selected) && (item.Key == selected.Key || item.ParentKey == selected.Key);
        row.Classes.Set("related", related);
        row.Background = related ? RelatedRowBrush : row.Index % 2 == 0 ? ProcessEvenRowBrush : ProcessOddRowBrush;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsLogAutoScroll) && _viewModel?.IsLogAutoScroll == true) QueueLogScroll();
    }

    private void OnLogViewChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && _viewModel?.IsLogAutoScroll == true) QueueLogScroll();
    }

    /// <summary>Keeps the newest logcat row in view while Auto-scroll is on; batches bursts of new rows into one scroll.</summary>
    private void QueueLogScroll()
    {
        if (_logScrollQueued) return;
        _logScrollQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _logScrollQueued = false;
            if (_viewModel is not { IsLogAutoScroll: true, LogView.Count: > 0 } viewModel) return;
            object last = viewModel.LogView[^1];
            if (LogTable.IsEffectivelyVisible) LogTable.ScrollIntoView(last, null);
            if (OverviewLogTable.IsEffectivelyVisible) OverviewLogTable.ScrollIntoView(last, null);
        }, DispatcherPriority.Background);
    }

    /// <summary>Clicking a package in the launch list opens it on the device, even when it is already selected.</summary>
    private void OnLaunchContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container is ComboBoxItem item && item.Tag is not true)
        {
            item.Tag = true;
            item.AddHandler(PointerReleasedEvent, OnLaunchItemPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        }
    }

    private async void OnLaunchItemPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left || sender is not ComboBoxItem { Content: string package }) return;
        await LaunchApplicationAsync(package);
    }

    private void OnLaunchApplicationKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !LaunchApplicationSelector.IsDropDownOpen) return;
        // Let the ComboBox commit the highlighted item first, then launch whatever it selected.
        Dispatcher.UIThread.Post(async () =>
        {
            if (LaunchApplicationSelector.SelectedItem is string package) await LaunchApplicationAsync(package);
        });
    }

    private async Task LaunchApplicationAsync(string package)
    {
        try
        {
            if (DataContext is not MainViewModel viewModel || !viewModel.LaunchSelectedCommand.CanExecute(null)) return;
            viewModel.SelectedPackage = package;
            LaunchApplicationSelector.IsDropDownOpen = false;
            await viewModel.LaunchSelectedCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Launching {Package} failed", package);
        }
    }

    /// <summary>
    /// Translates a click on the mirror image into real device coordinates and taps there.
    /// The image uses Uniform stretch, so the rendered frame can be letterboxed inside the control.
    /// </summary>
    private async void OnMirrorFrameClick(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left || sender is not Image image || DataContext is not MainViewModel viewModel) return;
        Point point = e.GetPosition(image);
        if (MirrorGeometry.ToDevice(image.Bounds.Width, image.Bounds.Height, viewModel.MirrorFrameWidth, viewModel.MirrorFrameHeight, point.X, point.Y) is not { } device) return;

        ShowMirrorTapMarker(point);
        e.Handled = true;
        try
        {
            await viewModel.TapMirrorAsync(device.X, device.Y);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Mirror tap failed");
        }
    }

    /// <summary>Draws a fading ring where the user tapped so the device-side touch is easy to follow.</summary>
    private void ShowMirrorTapMarker(Point position)
    {
        const double markerSize = 34;
        Ellipse marker = MirrorTapMarker;
        marker.Transitions = null;
        marker.Opacity = 1;
        marker.IsVisible = true;
        Canvas.SetLeft(marker, position.X - markerSize / 2);
        Canvas.SetTop(marker, position.Y - markerSize / 2);
        marker.Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(650), Delay = TimeSpan.FromMilliseconds(150) }];
        marker.Opacity = 0;
        DispatcherTimer.RunOnce(() =>
        {
            if (marker.Opacity == 0) marker.IsVisible = false;
        }, TimeSpan.FromMilliseconds(850));
    }

    /// <summary>
    /// Holds the window open until the view model has stopped recording and saved the session,
    /// because the process exits as soon as the main window closes.
    /// </summary>
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (_shutdownComplete)
        {
            base.OnClosing(e);
            return;
        }
        e.Cancel = true;
        base.OnClosing(e);
        if (_shutdownStarted || DataContext is not MainViewModel viewModel) return;
        _shutdownStarted = true;
        bool canClose;
        try { canClose = await viewModel.CanCloseAsync(); }
        catch (Exception ex) { Log.Error(ex, "Close confirmation failed"); canClose = true; }
        if (!canClose)
        {
            _shutdownStarted = false;
            return;
        }
        IsEnabled = false;
        try { await viewModel.DisposeAsync(); }
        catch (Exception ex) { Log.Error(ex, "Shutdown cleanup failed"); }
        _shutdownComplete = true;
        Dispatcher.UIThread.Post(Close);
    }
}

internal static class VisualTreeHelpers
{
    public static IEnumerable<T> GetVisualDescendantsOfType<T>(this Visual root) where T : Visual =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root).OfType<T>();
}
