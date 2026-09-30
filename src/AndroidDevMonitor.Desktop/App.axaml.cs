using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using AndroidDevMonitor.Adb.Discovery;
using AndroidDevMonitor.Adb.Execution;
using AndroidDevMonitor.Collectors.Sources;
using AndroidDevMonitor.Core.Configuration;
using AndroidDevMonitor.Core.Services;
using AndroidDevMonitor.Desktop.Services;
using AndroidDevMonitor.Infrastructure.Database;
using AndroidDevMonitor.Infrastructure.Export;
using AndroidDevMonitor.Infrastructure.Gpu;
using AndroidDevMonitor.Infrastructure.Media;
using AndroidDevMonitor.Presentation.Platform;
using AndroidDevMonitor.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace AndroidDevMonitor.Desktop;

public partial class App : Application
{
    private ServiceProvider? _provider;
    private MainWindow? _mainWindow;

    /// <summary>Set by the headless tests to replace platform services (for example to answer dialogs).</summary>
    public static Func<Func<Window?>, PlatformServices>? PlatformFactory { get; set; }

    public MainViewModel? ViewModel { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            bool demo = desktop.Args?.Any(arg => arg.Equals("--demo", StringComparison.OrdinalIgnoreCase)) == true;
            _mainWindow = CreateMainWindow(demo);
            desktop.MainWindow = _mainWindow;
            desktop.Exit += (_, _) => Shutdown();
            _mainWindow.Opened += async (_, _) => await InitializeViewModelAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Builds the services and the main window; used by the desktop lifetime and the headless tests.</summary>
    public MainWindow CreateMainWindow(bool demo)
    {
        string data = AppPaths.DataDirectory;
        Directory.CreateDirectory(data);
        string logDirectory = Path.Combine(data, "Logs");
        Directory.CreateDirectory(logDirectory);
        Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.File(
            Path.Combine(logDirectory, "android-dev-monitor-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 14,
            fileSizeLimitBytes: 25L * 1024 * 1024,
            rollOnFileSizeLimit: true).CreateLogger();
        Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        ServiceCollection services = new();
        services.AddLogging(builder => builder.AddSerilog(dispose: true));
        services.AddSingleton(PlatformFactory?.Invoke(() => _mainWindow) ?? AvaloniaPlatformServices.Create(() => _mainWindow));
        services.AddSingleton<IAdbExecutor>(sp => new AdbExecutor(sp.GetRequiredService<ILogger<AdbExecutor>>()));
        services.AddSingleton<ISessionStore>(_ => new SqliteSessionStore(data));
        services.AddSingleton<ISessionExporter, SessionExporter>();
        services.AddSingleton<IMediaService>(sp => demo
            ? new DemoMediaService(Path.Combine(data, "DemoMedia"))
            : new AdbMediaService(sp.GetRequiredService<IAdbExecutor>(), Path.Combine(data, "Media")));
        if (demo)
        {
            services.AddSingleton<IDeviceDiscoveryService, DemoDeviceDiscoveryService>();
            services.AddSingleton<IMonitoringSource, DemoMonitoringSource>();
        }
        else
        {
            services.AddSingleton<IDeviceDiscoveryService, DeviceDiscoveryService>();
            services.AddSingleton<IGpuMetricProvider, EmulatorGpuMetricProvider>();
            services.AddSingleton<IMonitoringSource, LiveMonitoringSource>();
        }
        services.AddSingleton(sp => new MainViewModel(
            sp.GetRequiredService<IDeviceDiscoveryService>(),
            sp.GetRequiredService<IMonitoringSource>(),
            sp.GetRequiredService<ISessionStore>(),
            sp.GetRequiredService<IMediaService>(),
            sp.GetRequiredService<ISessionExporter>(),
            sp.GetRequiredService<IAdbExecutor>(),
            sp.GetRequiredService<PlatformServices>(),
            demo));
        _provider = services.BuildServiceProvider();
        ViewModel = _provider.GetRequiredService<MainViewModel>();
        _mainWindow = new MainWindow(ViewModel);
        return _mainWindow;
    }

    public async Task InitializeViewModelAsync()
    {
        if (ViewModel is null) return;
        try
        {
            await ViewModel.InitializeAsync();
            ViewModel.EnableExtendedLogFilters();
            await ViewModel.LoadSavedLogFiltersAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Startup failed");
            _provider?.GetService<PlatformServices>()?.Dialogs.Notify(ex.Message, error: true);
        }
    }

    /// <summary>MainWindow's closing handler has already saved the session; release the remaining services.</summary>
    public void Shutdown()
    {
        try { if (_provider is not null) Task.Run(() => _provider.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) { Log.Error(ex, "Service disposal failed"); }
        _provider = null;
        Dispatcher.UIThread.UnhandledException -= OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        Log.CloseAndFlush();
    }

    private void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled UI exception");
        e.Handled = true;
        _provider?.GetService<PlatformServices>()?.Dialogs.Notify(
            $"An unexpected error occurred and was written to the log:\n\n{e.Exception.Message}", error: true);
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
