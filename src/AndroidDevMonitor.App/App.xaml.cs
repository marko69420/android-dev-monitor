using System.Windows;
using System.IO;
using AndroidDevMonitor.Adb.Discovery;
using AndroidDevMonitor.Adb.Execution;
using AndroidDevMonitor.App.Services;
using AndroidDevMonitor.App.ViewModels;
using AndroidDevMonitor.Collectors.Sources;
using AndroidDevMonitor.Core.Services;
using AndroidDevMonitor.Infrastructure.Database;
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
        var services = new ServiceCollection(); services.AddLogging(b => b.AddSerilog(dispose: true)); services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IAdbExecutor>(sp => new AdbExecutor(sp.GetRequiredService<ILogger<AdbExecutor>>()));
        services.AddSingleton<ISessionStore>(_ => new SqliteSessionStore(data)); services.AddSingleton<ISessionExporter, SessionExporter>();
        services.AddSingleton<IMediaService>(sp => demo ? new DemoMediaService(Path.Combine(data, "DemoMedia")) : new AdbMediaService(sp.GetRequiredService<IAdbExecutor>(), Path.Combine(data, "Media")));
        if (demo) { services.AddSingleton<IDeviceDiscoveryService, DemoDeviceDiscoveryService>(); services.AddSingleton<IMonitoringSource, DemoMonitoringSource>(); }
        else { services.AddSingleton<IDeviceDiscoveryService, DeviceDiscoveryService>(); services.AddSingleton<IGpuMetricProvider, UnsupportedGpuMetricProvider>(); services.AddSingleton<IMonitoringSource, LiveMonitoringSource>(); }
        services.AddSingleton(sp => new MainViewModel(sp.GetRequiredService<IDeviceDiscoveryService>(), sp.GetRequiredService<IMonitoringSource>(), sp.GetRequiredService<ISessionStore>(), sp.GetRequiredService<IMediaService>(), sp.GetRequiredService<ISessionExporter>(), sp.GetRequiredService<IAdbExecutor>(), sp.GetRequiredService<IDialogService>(), demo));
        services.AddSingleton<MainWindow>(); _provider = services.BuildServiceProvider(); var window = _provider.GetRequiredService<MainWindow>(); MainWindow = window; window.Show();
        try { await _provider.GetRequiredService<MainViewModel>().InitializeAsync(); } catch (Exception ex) { Log.Error(ex, "Startup failed"); MessageBox.Show(ex.Message, "Android Dev Monitor", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    protected override async void OnExit(ExitEventArgs e)
    {
        if (_provider?.GetService<MainViewModel>() is { } vm) await vm.DisposeAsync(); if (_provider is not null) await _provider.DisposeAsync(); Log.CloseAndFlush(); base.OnExit(e);
    }
}
