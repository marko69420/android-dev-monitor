using Avalonia;
using Avalonia.Headless;
using AndroidDevMonitor.Desktop.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace AndroidDevMonitor.Desktop.Tests;

public static class TestAppBuilder
{
    // Real Skia rendering (not the null renderer) so screenshots show exactly what users see.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
