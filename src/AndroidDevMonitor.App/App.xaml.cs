using System.Windows;
using System.Windows.Threading;
using System.IO;
using AndroidDevMonitor.Adb.Discovery;
using AndroidDevMonitor.Adb.Execution;
using AndroidDevMonitor.App.Services;
using AndroidDevMonitor.Presentation.Platform;
using AndroidDevMonitor.Presentation.ViewModels;
using AndroidDevMonitor.Collectors.Sources;
using AndroidDevMonitor.Core.Services;
using AndroidDevMonitor.Infrastructure.Database;
using AndroidDevMonitor.Infrastructure.Gpu;
using AndroidDevMonitor.Infrastructure.Export;
using AndroidDevMonitor.Infrastructure.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace AndroidDevMonitor.App;

public partial class App : Application
{
    private ServiceProvider? _provider;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e); var demo = e.Args.Any(x => x.Equals("--demo", StringComparison.OrdinalIgnoreCase));
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AndroidDevMonitor"); Directory.CreateDirectory(data);
        var logDirectory = Path.Combine(data, "Logs"); Directory.CreateDirectory(logDirectory);
        Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.File(
            Path.Combine(logDirectory, "android-dev-monitor-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 14,
            fileSizeLimitBytes: 25L * 1024 * 1024,
            rollOnFileSizeLimit: true).CreateLogger();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        var services = new ServiceCollection(); services.AddLogging(b => b.AddSerilog(dispose: true)); services.AddSingleton(WpfPlatformServices.Create(Dispatcher));
        services.AddSingleton<IAdbExecutor>(sp => new AdbExecutor(sp.GetRequiredService<ILogger<AdbExecutor>>()));
        services.AddSingleton<ISessionStore>(_ => new SqliteSessionStore(data)); services.AddSingleton<ISessionExporter, SessionExporter>();
        services.AddSingleton<IMediaService>(sp => demo ? new DemoMediaService(Path.Combine(data, "DemoMedia")) : new AdbMediaService(sp.GetRequiredService<IAdbExecutor>(), Path.Combine(data, "Media")));
        if (demo) { services.AddSingleton<IDeviceDiscoveryService, DemoDeviceDiscoveryService>(); services.AddSingleton<IMonitoringSource, DemoMonitoringSource>(); }
        else { services.AddSingleton<IDeviceDiscoveryService, DeviceDiscoveryService>(); services.AddSingleton<IGpuMetricProvider, EmulatorGpuMetricProvider>(); services.AddSingleton<IMonitoringSource, LiveMonitoringSource>(); }
        services.AddSingleton(sp => new MainViewModel(sp.GetRequiredService<IDeviceDiscoveryService>(), sp.GetRequiredService<IMonitoringSource>(), sp.GetRequiredService<ISessionStore>(), sp.GetRequiredService<IMediaService>(), sp.GetRequiredService<ISessionExporter>(), sp.GetRequiredService<IAdbExecutor>(), sp.GetRequiredService<PlatformServices>(), demo));
        services.AddSingleton<MainWindow>(); _provider = services.BuildServiceProvider(); var window = _provider.GetRequiredService<MainWindow>(); MainWindow = window; window.Show();
        MainViewModel viewModel = _provider.GetRequiredService<MainViewModel>();
        try { await viewModel.InitializeAsync(); viewModel.EnableExtendedLogFilters(); await viewModel.LoadSavedLogFiltersAsync(); } catch (Exception ex) { Log.Error(ex, "Startup failed"); MessageBox.Show(ex.Message, "Android Dev Monitor", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        // MainWindow.OnClosing has already awaited the view model shutdown; release the remaining services off the UI thread.
        try { if (_provider is not null) Task.Run(() => _provider.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) { Log.Error(ex, "Service disposal failed"); }
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled UI exception");
        e.Handled = true;
        MessageBox.Show($"An unexpected error occurred and was written to the log:\n\n{e.Exception.Message}", "Android Dev Monitor", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unobserved task exception");
        e.SetObserved();
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.ExceptionObject as Exception, "Fatal unhandled exception");
        Log.CloseAndFlush();
    }
}
