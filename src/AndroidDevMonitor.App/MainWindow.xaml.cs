using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
