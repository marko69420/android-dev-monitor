namespace AndroidDevMonitor.Presentation.Platform;

/// <summary>Runs work on the UI thread. Background priority lets input and rendering go first.</summary>
public interface IUiDispatcher
{
    Task InvokeAsync(Action action, bool background = false);
}

public interface IDialogService
{
    Task<(string Name, string? Note)?> PromptMarkerAsync();

    Task<bool> ConfirmAsync(string title, string message);

    /// <summary>Shows an information or error message. The call does not wait for the user to dismiss it.</summary>
    void Notify(string message, bool error = false);
}

/// <summary>A file type choice in an open or save dialog, for example ("Android package", "*.apk").</summary>
public sealed record FileTypeFilter(string Name, params string[] Patterns)
{
    public static FileTypeFilter AllFiles { get; } = new("All files", "*.*");
}

public interface IFilePickerService
{
    Task<string?> OpenFileAsync(string title, IReadOnlyList<FileTypeFilter> filters, string? initialDirectory = null);

    Task<string?> SaveFileAsync(string title, string suggestedName, IReadOnlyList<FileTypeFilter> filters, string? initialDirectory = null);

    Task<string?> PickFolderAsync(string title, string? initialDirectory = null);
}

public interface IClipboardService
{
    Task SetTextAsync(string text);

    Task<string?> GetTextAsync();
}

/// <summary>Decodes images with the UI toolkit's codecs.</summary>
public interface IImageService
{
    /// <summary>An image object the view can bind to (a WPF ImageSource or an Avalonia Bitmap), or null when decoding fails.</summary>
    object? Decode(byte[] encoded);

    /// <summary>Decodes an image file into 32-bit BGRA pixels.</summary>
    (byte[] Pixels, int Width, int Height) LoadBgra(string path);
}

public interface IAppLifecycle
{
    /// <summary>Starts closing the main window; the normal close path saves the session first.</summary>
    void Shutdown();

    void PlayAlertSound();
}

/// <summary>Operating-system integration: opening files, the file manager, autostart and restarting the app.</summary>
public interface IDesktopShell
{
    void Open(string path);

    /// <summary>Opens the file manager with the file selected when the platform supports it, otherwise its folder.</summary>
    void Reveal(string path);

    void SetStartWithSystem(bool enabled);

    /// <summary>Starts a new instance of this application with the given arguments.</summary>
    bool StartNewInstance(IEnumerable<string> arguments);
}

public sealed record PlatformServices(
    IUiDispatcher Dispatcher,
    IDialogService Dialogs,
    IFilePickerService Files,
    IClipboardService Clipboard,
    IImageService Images,
    IAppLifecycle Lifecycle,
    IDesktopShell Shell);
