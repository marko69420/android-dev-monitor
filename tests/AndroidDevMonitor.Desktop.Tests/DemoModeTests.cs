using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;

namespace AndroidDevMonitor.Desktop.Tests;

public sealed class DemoModeTests
{
    [AvaloniaFact]
    public async Task Every_page_renders_without_binding_errors()
    {
        BindingErrors.Instance.Errors.Clear();
        await using AppSession session = await AppSession.StartAsync(demo: true);
        await AppSession.WaitUntil(() => session.ViewModel.ProcessRows.Count > 0 && session.ViewModel.Logs.Count > 0, "demo data");

        string[] pages = [.. session.ViewModel.NavigationItems, "App & Device Lab"];
        foreach (string page in pages)
        {
            session.ShowPage(page);
            Assert.Equal(page, session.ViewModel.CurrentPage);
            session.Screenshot("demo-" + page.Replace(' ', '-').Replace("&", "and"));
        }
        foreach (string tab in new[] { "Processes", "Performance", "Network", "Storage", "Thermal", "Logs" })
        {
            session.ShowPage("Overview");
            await session.ClickAsync(tab);
            Assert.Equal(tab, session.ViewModel.OverviewTab);
            session.Screenshot("demo-overview-" + tab);
        }

        Assert.True(BindingErrors.Instance.Errors.IsEmpty, string.Join("\n", BindingErrors.Instance.Errors.Distinct()));
    }

    [AvaloniaFact]
    public async Task Default_on_settings_are_on_at_first_start()
    {
        await using AppSession session = await AppSession.StartAsync(demo: true);
        session.ShowPage("Settings");
        Assert.True(session.ViewModel.ConfirmRecordingClose);
        Assert.True(session.ViewModel.AlertSoundsEnabled);
        Assert.True(session.ViewModel.CompactDensity);
        Assert.False(session.ViewModel.LaunchAtStartup);
        CheckBox restorePage = session.Window.GetLogicalDescendants().OfType<CheckBox>().Single(box => (box.Content as string) == "Restore last selected page");
        Assert.True(restorePage.IsChecked);
    }

    [AvaloniaFact]
    public async Task Only_the_current_page_is_visible()
    {
        await using AppSession session = await AppSession.StartAsync(demo: true);
        session.ShowPage("Logs");
        TextBlock[] titles = session.Window.GetLogicalDescendants().OfType<TextBlock>()
            .Where(text => text.Classes.Contains("sectionTitle") && AppSession.IsShown(text)).ToArray();
        Assert.Equal(["Logs"], titles.Select(title => title.Text));
    }

    [AvaloniaFact]
    public async Task Marker_and_process_search_work_from_the_ui()
    {
        await using AppSession session = await AppSession.StartAsync(demo: true);
        await AppSession.WaitUntil(() => session.ViewModel.ProcessRows.Count > 3, "processes");
        await session.ClickAsync("Mark Event");
        Assert.Contains(session.ViewModel.Markers, marker => marker.Name == "Boss fight");

        session.ViewModel.SearchText = "chrome";
        await AppSession.WaitUntil(() => session.ViewModel.ProcessRows.All(row => row.DisplayName.Contains("Chrome", StringComparison.OrdinalIgnoreCase) || row.PackageOrCommand.Contains("chrome", StringComparison.OrdinalIgnoreCase)),
            "process search to filter the table");
        Assert.NotEmpty(session.ViewModel.ProcessRows);
    }

    [AvaloniaFact]
    public async Task Log_filters_narrow_the_visible_rows()
    {
        await using AppSession session = await AppSession.StartAsync(demo: true);
        await AppSession.WaitUntil(() => session.ViewModel.Logs.Count > 3, "demo logs");
        session.ShowPage("Logs");
        int all = session.ViewModel.LogView.Count;
        session.ViewModel.SelectedLogPriority = "Debug";
        session.Settle();
        Assert.All(session.ViewModel.LogView, entry => Assert.Equal("Debug", entry.Priority));
        Assert.True(session.ViewModel.LogView.Count < all);
        session.ViewModel.SelectedLogPriority = "All";
        session.ViewModel.LogSearchText = "Render loop";
        session.Settle();
        Assert.All(session.ViewModel.LogView, entry => Assert.Contains("Render loop", entry.Message));

        string saved = Path.Combine(session.Scratch, "visible.log");
        session.Platform.NextSaveFile = saved;
        await session.ClickAsync("Save");
        Assert.Equal(session.ViewModel.LogView.Count, File.ReadAllLines(saved).Length);
    }
}

public sealed class PackagingTests
{
    [AvaloniaFact]
    public async Task Window_has_the_app_icon_and_the_guide_ships_with_the_app()
    {
        await using AppSession session = await AppSession.StartAsync(demo: true);
        Assert.NotNull(session.Window.Icon);
        Assert.True(Avalonia.Platform.AssetLoader.Exists(new Uri("avares://AndroidDevMonitor/Assets/app-icon.png")));
        Assert.True(File.Exists(session.ViewModel.UserGuidePath), session.ViewModel.UserGuidePath);
    }
}

public sealed class ImageServiceTests
{
    [Fact]
    public void Screenshots_load_as_unpremultiplied_bgra_like_on_windows()
    {
        // A 2x1 PNG: opaque red, then blue at half opacity. The screenshot diff expects WPF's Bgra32 layout.
        string path = Path.Combine(Path.GetTempPath(), "adm-bgra-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(path, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAYAAAD0In+KAAAADklEQVR4nGP4z8AAQg0AD3oDfnfpf5cAAAAASUVORK5CYII="));
        try
        {
            (byte[] pixels, int width, int height) = new AndroidDevMonitor.Desktop.Services.AvaloniaImageService().LoadBgra(path);
            Assert.Equal((2, 1), (width, height));
            Assert.Equal(new byte[] { 0, 0, 255, 255, 255, 0, 0, 128 }, pixels);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
