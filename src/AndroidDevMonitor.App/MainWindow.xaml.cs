using System.ComponentModel;
using System.Windows;
using AndroidDevMonitor.App.ViewModels;

namespace AndroidDevMonitor.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel) { InitializeComponent(); DataContext = viewModel; }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && !viewModel.CanClose()) e.Cancel = true;
        base.OnClosing(e);
    }
}
