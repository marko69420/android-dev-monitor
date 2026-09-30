using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using AndroidDevMonitor.Core.Configuration;

namespace AndroidDevMonitor.Desktop.Tests;

public sealed class ProbeTests
{
    [AvaloniaFact]
    public async Task Settings_checkboxes_reflect_the_view_model()
    {
        AppPaths.DataDirectory = Path.Combine(Path.GetTempPath(), "adm-probe-" + Guid.NewGuid().ToString("N"));
        App app = (App)Avalonia.Application.Current!;
        MainWindow window = app.CreateMainWindow(demo: true);
        window.Show();
        await app.InitializeViewModelAsync();
        app.ViewModel!.CurrentPage = "Settings";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        CheckBox box = window.GetLogicalDescendants().OfType<CheckBox>().First(c => (c.Content as string) == "Restore last selected page");
        Assert.True(app.ViewModel.RestoreLastPage);
        Assert.Equal(true, box.IsChecked);
    }
}
