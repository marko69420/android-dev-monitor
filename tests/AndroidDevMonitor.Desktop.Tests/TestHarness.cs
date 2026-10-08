using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Logging;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AndroidDevMonitor.Core.Configuration;
using AndroidDevMonitor.Desktop.Services;
using AndroidDevMonitor.Presentation.Platform;
using AndroidDevMonitor.Presentation.ViewModels;

namespace AndroidDevMonitor.Desktop.Tests;

/// <summary>
/// Runs the real Linux app (window, view model, services and adb execution) inside the headless Avalonia platform.
/// Live tests talk to tests/fake-android, a fake adb and Android device, so no phone or emulator is needed.
/// </summary>
public sealed class AppSession : IAsyncDisposable
{
    public static string RepositoryRoot { get; } = FindRepositoryRoot();
    public static string ScreenshotDirectory { get; } = Path.Combine(RepositoryRoot, "artifacts", "linux-ui-screenshots");

    public required App App { get; init; }
    public required MainWindow Window { get; init; }
    public required MainViewModel ViewModel { get; init; }
    public required TestPlatform Platform { get; init; }
    public required string Scratch { get; init; }
    public required string DeviceRoot { get; init; }

    [ModuleInitializer]
    internal static void KeepTestsAwayFromTheRealHome()
    {
        // Exports and diagnostics go to ~/Documents; point HOME at a scratch folder so tests never touch the real one.
        string home = Path.Combine(Path.GetTempPath(), "adm-ui-tests-home-" + Environment.ProcessId);
        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable("HOME", home);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", null);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(home, ".config"));
        Logger.Sink = BindingErrors.Instance;
    }

    public static async Task<AppSession> StartAsync(bool demo, [CallerMemberName] string name = "")
    {
        string scratch = Path.Combine(Path.GetTempPath(), "adm-ui-" + name + "-" + Guid.NewGuid().ToString("N")[..8]);
        string deviceRoot = Path.Combine(scratch, "device");
        Directory.CreateDirectory(scratch);
        AppPaths.DataDirectory = Path.Combine(scratch, "data");
        Environment.SetEnvironmentVariable("FAKE_ANDROID_ROOT", deviceRoot);
        Environment.SetEnvironmentVariable("FAKE_ANDROID_CRASH", null);
        // ANDROID_HOME is the SDK the app searches for platform-tools/adb.
        Environment.SetEnvironmentVariable("ANDROID_HOME", Path.Combine(RepositoryRoot, "tests", "fake-android", "sdk"));
        Environment.SetEnvironmentVariable("ANDROID_SDK_ROOT", null);

        TestPlatform platform = new();
        App.PlatformFactory = window => platform.Build(window);
        App app = (App)Application.Current!;
        MainWindow window = app.CreateMainWindow(demo);
        window.Width = 1600;
        window.Height = 900;
        window.Show();
        await app.InitializeViewModelAsync();
        return new AppSession
        {
            App = app, Window = window, ViewModel = app.ViewModel!, Platform = platform, Scratch = scratch, DeviceRoot = deviceRoot
        };
    }

    /// <summary>Runs the UI thread until the condition holds; background adb work keeps going meanwhile.</summary>
    public static async Task WaitUntil(Func<bool> condition, string what, int timeoutSeconds = 30)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for: " + what);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
        }
        Dispatcher.UIThread.RunJobs();
    }

    public void ShowPage(string page)
    {
        ViewModel.CurrentPage = page;
        Settle();
    }

    public void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    public string Screenshot(string name)
    {
        Settle();
        Directory.CreateDirectory(ScreenshotDirectory);
        string path = Path.Combine(ScreenshotDirectory, name + ".png");
        using WriteableBitmap? frame = Window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(path);
        return path;
    }

    /// <summary>
    /// Finds a button by its visible text and invokes its bound command the way a click does. Waits until the
    /// command finishes unless it is a long-running toggle such as "Start mirror".
    /// </summary>
    public async Task ClickAsync(string text, bool waitForCompletion = true)
    {
        Button button = Window.GetLogicalDescendants().OfType<Button>()
            .First(item => IsShown(item) && string.Equals(item.Content as string, text, StringComparison.Ordinal));
        object? parameter = button.CommandParameter;
        Assert.NotNull(button.Command);
        Assert.True(button.Command!.CanExecute(parameter), $"Button '{text}' is disabled");
        button.Command.Execute(parameter);
        if (waitForCompletion && button.Command is CommunityToolkit.Mvvm.Input.IAsyncRelayCommand asyncCommand)
            await WaitUntil(() => !asyncCommand.IsRunning, $"'{text}' to finish", 60);
        Settle();
    }

    /// <summary>
    /// On screen: attached to the window and not inside a hidden page. Content of a hidden ScrollViewer is never
    /// attached, and a detached control reports IsEffectivelyVisible = true, so both checks are needed.
    /// </summary>
    public static bool IsShown(Control control) => control.GetVisualRoot() is not null && control.IsEffectivelyVisible;

    public IReadOnlyList<string> AdbCommands()
    {
        string log = Path.Combine(DeviceRoot, "commands.log");
        return File.Exists(log) ? File.ReadAllLines(log) : [];
    }

    public async ValueTask DisposeAsync()
    {
        TaskCompletionSource closed = new();
        Window.Closed += (_, _) => closed.TrySetResult();
        Window.Close();
        await WaitUntil(() => closed.Task.IsCompleted, "window to close after saving the session");
        App.Shutdown();
        App.PlatformFactory = null;
        StopFakeApp();
    }

    private void StopFakeApp()
    {
        if (!OperatingSystem.IsLinux()) return;
        // The fake device starts a long-running process named after the monitored package; end it with the test.
        foreach (string pid in Directory.EnumerateDirectories("/proc").Select(Path.GetFileName).Where(p => p!.All(char.IsDigit))!)
        {
            try
            {
                string cmdline = File.ReadAllText($"/proc/{pid}/cmdline");
                if (cmdline.StartsWith("com.example.fakegame\0", StringComparison.Ordinal))
                    System.Diagnostics.Process.GetProcessById(int.Parse(pid)).Kill();
            }
            catch
            {
                // The process ended on its own.
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "AndroidDevMonitor.slnx")))
            directory = Path.GetDirectoryName(directory);
        return directory ?? throw new InvalidOperationException("Repository root not found");
    }
}

/// <summary>Platform services that answer dialogs and pickers the way a test says, and record what was shown.</summary>
public sealed class TestPlatform : IDialogService, IFilePickerService, IClipboardService, IAppLifecycle, IDesktopShell
{
    public bool ConfirmAnswer { get; set; } = true;
    public string? NextOpenFile { get; set; }
    public string? NextSaveFile { get; set; }
    public string? NextFolder { get; set; }
    public string Clipboard { get; set; } = "";
    public ConcurrentQueue<string> Confirmations { get; } = new();
    public ConcurrentQueue<string> Notifications { get; } = new();
    public ConcurrentQueue<string> Opened { get; } = new();
    public int AlertSounds { get; private set; }

    public PlatformServices Build(Func<Window?> window) =>
        new(new AvaloniaUiDispatcher(), this, this, this, new AvaloniaImageService(), this, this);

    public Task<(string Name, string? Note)?> PromptMarkerAsync() => Task.FromResult<(string, string?)?>(("Boss fight", "Frame drops start here"));

    public Task<bool> ConfirmAsync(string title, string message)
    {
        Confirmations.Enqueue(title + ": " + message);
        return Task.FromResult(ConfirmAnswer);
    }

    public void Notify(string message, bool error = false) => Notifications.Enqueue((error ? "ERROR " : "") + message);

    public Task<string?> OpenFileAsync(string title, IReadOnlyList<FileTypeFilter> filters, string? initialDirectory = null) => Task.FromResult(NextOpenFile);

    public Task<string?> SaveFileAsync(string title, string suggestedName, IReadOnlyList<FileTypeFilter> filters, string? initialDirectory = null) => Task.FromResult(NextSaveFile);

    public Task<string?> PickFolderAsync(string title, string? initialDirectory = null) => Task.FromResult(NextFolder);

    public Task SetTextAsync(string text)
    {
        Clipboard = text;
        return Task.CompletedTask;
    }

    public Task<string?> GetTextAsync() => Task.FromResult<string?>(Clipboard);

    public void Shutdown() { }

    public void PlayAlertSound() => AlertSounds++;

    public void Open(string path) => Opened.Enqueue(path);

    public void Reveal(string path) => Opened.Enqueue("reveal:" + path);

    public void SetStartWithSystem(bool enabled) { }

    public bool StartNewInstance(IEnumerable<string> arguments) => true;
}

/// <summary>Collects binding and property errors that Avalonia would otherwise only write to the debug output.</summary>
public sealed class BindingErrors : ILogSink
{
    public static BindingErrors Instance { get; } = new();

    public ConcurrentQueue<string> Errors { get; } = new();

    public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning && area is LogArea.Binding or LogArea.Property;

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
        Errors.Enqueue($"[{area}] {messageTemplate} ({source})");

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) =>
        Errors.Enqueue($"[{area}] {Format(messageTemplate, propertyValues)} ({source})");

    private static string Format(string template, object?[] values)
    {
        int index = 0;
        return System.Text.RegularExpressions.Regex.Replace(template, @"\{[^}]+\}", _ => index < values.Length ? values[index++]?.ToString() ?? "null" : "?");
    }
}
