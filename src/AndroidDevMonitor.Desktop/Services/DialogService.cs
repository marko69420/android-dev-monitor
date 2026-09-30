using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AndroidDevMonitor.Presentation.Platform;

namespace AndroidDevMonitor.Desktop.Services;

/// <summary>Small modal dialogs styled like the main window.</summary>
public sealed class DialogService(Func<Window?> mainWindow) : IDialogService
{
    private static readonly IBrush DialogBackground = new SolidColorBrush(Color.FromRgb(21, 30, 37));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#FF8B87"));

    /// <summary>Raised when a dialog opens; tests use it to answer dialogs without a user.</summary>
    public event Action<Window>? DialogOpened;

    public Task<bool> ConfirmAsync(string title, string message)
    {
        Window dialog = Create(title, Message(message), "OK", out Button ok, out _);
        ok.Click += (_, _) => Accept(dialog);
        return ShowAsync(dialog, false);
    }

    public void Notify(string message, bool error = false)
    {
        TextBlock text = Message(message);
        if (error) text.Foreground = ErrorBrush;
        Window dialog = Create(error ? "Android Dev Monitor error" : "Android Dev Monitor", text, "OK", out Button ok, out Button cancel);
        cancel.IsVisible = false;
        ok.Click += (_, _) => Accept(dialog);
        _ = ShowAsync(dialog, false);
    }

    public async Task<(string Name, string? Note)?> PromptMarkerAsync()
    {
        TextBox name = new() { Text = "Loading screen", Margin = new Thickness(0, 4, 0, 10) };
        TextBox note = new() { AcceptsReturn = true, Height = 64, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
        StackPanel body = new() { Children = { new TextBlock { Text = "Marker name" }, name, new TextBlock { Text = "Optional note" }, note } };
        Window dialog = Create("Mark session event", body, "Save", out Button ok, out _);
        ok.Click += (_, _) => Accept(dialog);
        dialog.Opened += (_, _) => { name.Focus(); name.SelectAll(); };
        if (!await ShowAsync(dialog, false) || string.IsNullOrWhiteSpace(name.Text)) return null;
        return (name.Text.Trim(), string.IsNullOrWhiteSpace(note.Text) ? null : note.Text.Trim());
    }

    /// <summary>Asks for a path by typing it; used when the desktop has no file chooser (no portal and no GTK).</summary>
    public async Task<string?> PromptPathAsync(string title, string label, string? initialPath, bool mustExist, bool folder = false)
    {
        TextBox path = new() { Text = initialPath ?? "", Margin = new Thickness(0, 4, 0, 4) };
        TextBlock problem = new() { Foreground = ErrorBrush, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        StackPanel body = new() { Children = { new TextBlock { Text = label }, path, problem } };
        Window dialog = Create(title, body, "OK", out Button ok, out _);
        ok.Click += (_, _) =>
        {
            string value = Environment.ExpandEnvironmentVariables(path.Text?.Trim() ?? "");
            if (value.StartsWith('~')) value = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + value[1..];
            bool exists = folder ? Directory.Exists(value) : File.Exists(value);
            if (value.Length == 0 || (mustExist && !exists))
            {
                problem.Text = folder ? "That folder does not exist." : "That file does not exist.";
                problem.IsVisible = true;
                return;
            }
            path.Text = value;
            Accept(dialog);
        };
        dialog.Opened += (_, _) => path.Focus();
        return await ShowAsync(dialog, false) ? path.Text : null;
    }

    private async Task<bool> ShowAsync(Window dialog, bool fallback)
    {
        DialogOpened?.Invoke(dialog);
        Window? owner = mainWindow();
        if (owner is { IsVisible: true })
            return await dialog.ShowDialog<bool?>(owner) ?? fallback;

        TaskCompletionSource<bool> closed = new();
        dialog.Closed += (_, _) => closed.TrySetResult(true);
        dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.Show();
        await closed.Task;
        return dialog.Tag is true;
    }

    private static TextBlock Message(string message) => new() { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };

    private static Window Create(string title, Control body, string okText, out Button ok, out Button cancel)
    {
        ok = new Button { Content = okText, MinWidth = 86, IsDefault = true, Margin = new Thickness(8, 0, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Center };
        cancel = new Button { Content = "Cancel", MinWidth = 86, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Children = { cancel, ok } };
        Window window = new()
        {
            Title = title,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = DialogBackground,
            Content = new StackPanel { Margin = new Thickness(20), Children = { body, buttons } }
        };
        cancel.Click += (_, _) => window.Close(false);
        return window;
    }

    /// <summary>Closes with a positive answer. Tag carries it for dialogs shown without an owner.</summary>
    private static void Accept(Window dialog)
    {
        dialog.Tag = true;
        dialog.Close(true);
    }
}
