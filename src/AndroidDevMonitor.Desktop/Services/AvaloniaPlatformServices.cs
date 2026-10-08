using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AndroidDevMonitor.Presentation.Platform;
using Serilog;
using SkiaSharp;

namespace AndroidDevMonitor.Desktop.Services;

/// <summary>Avalonia implementations of the platform services the shared view models use.</summary>
public static class AvaloniaPlatformServices
{
    public static PlatformServices Create(Func<Window?> mainWindow)
    {
        DialogService dialogs = new(mainWindow);
        return new PlatformServices(
            new AvaloniaUiDispatcher(),
            dialogs,
            new AvaloniaFilePickerService(mainWindow, dialogs),
            new AvaloniaClipboardService(mainWindow),
            new AvaloniaImageService(),
            new AvaloniaAppLifecycle(mainWindow),
            new DesktopShell());
    }
}

public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public Task InvokeAsync(Action action, bool background = false) =>
        Dispatcher.UIThread.InvokeAsync(action, background ? DispatcherPriority.Background : DispatcherPriority.Normal).GetTask();
}

public sealed class AvaloniaFilePickerService(Func<Window?> mainWindow, DialogService dialogs) : IFilePickerService
{
    public async Task<string?> OpenFileAsync(string title, IReadOnlyList<FileTypeFilter> filters, string? initialDirectory = null)
    {
        if (mainWindow() is not { StorageProvider: { CanOpen: true } storage })
            return await dialogs.PromptPathAsync(title, "Full path of the file to open", initialDirectory, mustExist: true);
        try
        {
            IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = ToFileTypes(filters),
                SuggestedStartLocation = await StartLocationAsync(storage, initialDirectory)
            });
            return files.Count == 0 ? null : files[0].TryGetLocalPath();
        }
        catch (Exception ex)
        {
            // No portal or GTK file chooser on this desktop: fall back to typing the path.
            Log.Warning(ex, "System file picker unavailable");
            return await dialogs.PromptPathAsync(title, "Full path of the file to open", initialDirectory, mustExist: true);
        }
    }

    public async Task<string?> SaveFileAsync(string title, string suggestedName, IReadOnlyList<FileTypeFilter> filters, string? initialDirectory = null)
    {
        string defaultPath = Path.Combine(initialDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), suggestedName);
        if (mainWindow() is not { StorageProvider: { CanSave: true } storage })
            return await dialogs.PromptPathAsync(title, "Full path to save to", defaultPath, mustExist: false);
        try
        {
            IStorageFile? file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = suggestedName,
                FileTypeChoices = ToFileTypes(filters),
                DefaultExtension = filters.SelectMany(filter => filter.Patterns).Select(Path.GetExtension).FirstOrDefault(extension => !string.IsNullOrEmpty(extension) && extension != ".*")?.TrimStart('.'),
                ShowOverwritePrompt = true,
                SuggestedStartLocation = await StartLocationAsync(storage, initialDirectory)
            });
            return file?.TryGetLocalPath();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "System save dialog unavailable");
            return await dialogs.PromptPathAsync(title, "Full path to save to", defaultPath, mustExist: false);
        }
    }

    public async Task<string?> PickFolderAsync(string title, string? initialDirectory = null)
    {
        if (mainWindow() is not { StorageProvider: { CanPickFolder: true } storage })
            return await dialogs.PromptPathAsync(title, "Full path of the folder", initialDirectory, mustExist: true, folder: true);
        try
        {
            IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                SuggestedStartLocation = await StartLocationAsync(storage, initialDirectory)
            });
            return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "System folder picker unavailable");
            return await dialogs.PromptPathAsync(title, "Full path of the folder", initialDirectory, mustExist: true, folder: true);
        }
    }

    internal static List<FilePickerFileType> ToFileTypes(IEnumerable<FileTypeFilter> filters) =>
        filters.Select(filter => new FilePickerFileType(filter.Name)
        {
            // "*.*" is the Windows spelling of "any file"; GTK and the portal expect "*".
            Patterns = filter.Patterns.Select(pattern => pattern == "*.*" ? "*" : pattern).ToArray()
        }).ToList();

    private static async Task<IStorageFolder?> StartLocationAsync(IStorageProvider storage, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return null;
        try { return await storage.TryGetFolderFromPathAsync(directory); }
        catch { return null; }
    }
}

public sealed class AvaloniaClipboardService(Func<Window?> mainWindow) : IClipboardService
{
    public async Task SetTextAsync(string text)
    {
        if (mainWindow()?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }

    public async Task<string?> GetTextAsync() =>
        mainWindow()?.Clipboard is { } clipboard ? await clipboard.TryGetTextAsync() : null;
}

public sealed class AvaloniaImageService : IImageService
{
    public object? Decode(byte[] encoded)
    {
        try
        {
            using MemoryStream stream = new(encoded, writable: false);
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    public (byte[] Pixels, int Width, int Height) LoadBgra(string path)
    {
        using SKCodec codec = SKCodec.Create(path) ?? throw new InvalidDataException("Unsupported or damaged image: " + path);
        SKImageInfo info = new(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using SKBitmap bitmap = SKBitmap.Decode(codec, info) ?? throw new InvalidDataException("The image could not be decoded: " + path);
        int stride = info.Width * 4;
        byte[] pixels = new byte[stride * info.Height];
        byte[] source = bitmap.Bytes;
        for (int row = 0; row < info.Height; row++)
            Buffer.BlockCopy(source, row * bitmap.RowBytes, pixels, row * stride, stride);
        return (pixels, info.Width, info.Height);
    }
}

public sealed class AvaloniaAppLifecycle(Func<Window?> mainWindow) : IAppLifecycle
{
    // Closing the main window runs MainWindow's closing handler, which saves the session before the process exits.
    public void Shutdown() => mainWindow()?.Close();

    public void PlayAlertSound()
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                // libcanberra is present on GNOME, KDE and most other desktops; fall back to the terminal bell.
                ProcessStartInfo start = new("canberra-gtk-play") { UseShellExecute = false, ArgumentList = { "--id=dialog-warning" } };
                using Process? process = Process.Start(start);
            }
            else if (OperatingSystem.IsMacOS())
            {
                using Process? process = Process.Start(new ProcessStartInfo("afplay") { ArgumentList = { "/System/Library/Sounds/Funk.aiff" } });
            }
        }
        catch
        {
            Console.Write('\a');
        }
    }
}
