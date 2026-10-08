using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AndroidDevMonitor.Presentation.Platform;
using Microsoft.Win32;

namespace AndroidDevMonitor.App.Services;

/// <summary>WPF implementations of the platform services the shared view models use.</summary>
public static class WpfPlatformServices
{
    public static PlatformServices Create(Dispatcher dispatcher) => new(
        new WpfUiDispatcher(dispatcher),
        new DialogService(),
        new WpfFilePickerService(),
        new WpfClipboardService(),
        new WpfImageService(),
        new WpfAppLifecycle(),
        new DesktopShell());
}

public sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public Task InvokeAsync(Action action, bool background = false) =>
        dispatcher.InvokeAsync(action, background ? DispatcherPriority.Background : DispatcherPriority.Normal).Task;
}

public sealed class DialogService : IDialogService
{
    public Task<(string Name, string? Note)?> PromptMarkerAsync()
    {
        var name = new TextBox { Margin = new Thickness(0, 4, 0, 10), Text = "Loading screen" };
        var note = new TextBox { Margin = new Thickness(0, 4, 0, 12), AcceptsReturn = true, Height = 64 };
        var dialog = Create("Mark session event", new StackPanel { Children = { new TextBlock { Text = "Marker name" }, name, new TextBlock { Text = "Optional note" }, note } });
        (string Name, string? Note)? result = dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(name.Text) ? (name.Text.Trim(), string.IsNullOrWhiteSpace(note.Text) ? null : note.Text.Trim()) : null;
        return Task.FromResult(result);
    }

    public Task<bool> ConfirmAsync(string title, string message) =>
        Task.FromResult(MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK);

    public void Notify(string message, bool error = false) => MessageBox.Show(message, error ? "Android Dev Monitor error" : "Android Dev Monitor", MessageBoxButton.OK, error ? MessageBoxImage.Error : MessageBoxImage.Information);

    private static Window Create(string title, Panel body)
    {
        var ok = new Button { Content = "Save", Width = 86, IsDefault = true, Margin = new Thickness(8, 0, 0, 0) }; var cancel = new Button { Content = "Cancel", Width = 86, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; buttons.Children.Add(cancel); buttons.Children.Add(ok);
        var root = new StackPanel { Margin = new Thickness(20) }; root.Children.Add(body); root.Children.Add(buttons);
        var window = new Window { Title = title, Width = 420, Height = 270, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, Background = new SolidColorBrush(Color.FromRgb(21, 30, 37)), Foreground = Brushes.White, Content = root };
        ok.Click += (_, _) => window.DialogResult = true; return window;
    }
}

public sealed class WpfFilePickerService : IFilePickerService
{
    public Task<string?> OpenFileAsync(string title, IReadOnlyList<FileTypeFilter> filters, string? initialDirectory = null)
    {
        OpenFileDialog dialog = new() { Title = title, Filter = ToFilter(filters), CheckFileExists = true, Multiselect = false };
        if (initialDirectory is not null && Directory.Exists(initialDirectory)) dialog.InitialDirectory = initialDirectory;
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.FileName : null);
    }

    public Task<string?> SaveFileAsync(string title, string suggestedName, IReadOnlyList<FileTypeFilter> filters, string? initialDirectory = null)
    {
        SaveFileDialog dialog = new() { Title = title, Filter = ToFilter(filters), FileName = suggestedName };
        if (initialDirectory is not null && Directory.Exists(initialDirectory)) dialog.InitialDirectory = initialDirectory;
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.FileName : null);
    }

    public Task<string?> PickFolderAsync(string title, string? initialDirectory = null)
    {
        OpenFolderDialog dialog = new() { Title = title, InitialDirectory = initialDirectory };
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.FolderName : null);
    }

    /// <summary>Builds a Win32 filter string such as "Log files (*.log;*.txt)|*.log;*.txt".</summary>
    public static string ToFilter(IEnumerable<FileTypeFilter> filters) => string.Join("|", filters.Select(filter =>
    {
        string patterns = string.Join(";", filter.Patterns);
        string label = filter.Name.Contains('(') ? filter.Name : $"{filter.Name} ({patterns})";
        return label + "|" + patterns;
    }));
}

public sealed class WpfClipboardService : IClipboardService
{
    public Task SetTextAsync(string text)
    {
        Clipboard.SetText(text);
        return Task.CompletedTask;
    }

    public Task<string?> GetTextAsync() => Task.FromResult<string?>(Clipboard.ContainsText() ? Clipboard.GetText() : null);
}

public sealed class WpfImageService : IImageService
{
    public object? Decode(byte[] encoded)
    {
        try
        {
            using MemoryStream stream = new(encoded, writable: false);
            BitmapImage image = new();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    public (byte[] Pixels, int Width, int Height) LoadBgra(string path)
    {
        BitmapImage image = new();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();

        FormatConvertedBitmap converted = new(image, PixelFormats.Bgra32, null, 0);
        int width = converted.PixelWidth;
        int height = converted.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);
        return (pixels, width, height);
    }
}

public sealed class WpfAppLifecycle : IAppLifecycle
{
    // Closing the main window runs MainWindow.OnClosing, which saves the session before the process exits.
    public void Shutdown() => Application.Current.MainWindow?.Close();

    public void PlayAlertSound() => SystemSounds.Exclamation.Play();
}
