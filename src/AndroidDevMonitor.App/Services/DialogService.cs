using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AndroidDevMonitor.App.Services;

public interface IDialogService
{
    (string Name, string? Note)? PromptMarker();
    bool Confirm(string title, string message);
    void Notify(string message, bool error = false);
}

public sealed class DialogService : IDialogService
{
    public (string Name, string? Note)? PromptMarker()
    {
        var name = new TextBox { Margin = new Thickness(0, 4, 0, 10), Text = "Loading screen" };
        var note = new TextBox { Margin = new Thickness(0, 4, 0, 12), AcceptsReturn = true, Height = 64 };
        var dialog = Create("Mark session event", new StackPanel { Children = { new TextBlock { Text = "Marker name" }, name, new TextBlock { Text = "Optional note" }, note } });
        return dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(name.Text) ? (name.Text.Trim(), string.IsNullOrWhiteSpace(note.Text) ? null : note.Text.Trim()) : null;
    }
    public bool Confirm(string title, string message) => MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
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
