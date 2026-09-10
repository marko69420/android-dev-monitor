using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.Input;

namespace AndroidDevMonitor.App.ViewModels;

public partial class MainViewModel
{
    public string UserGuidePath { get; } = Path.Combine(System.AppContext.BaseDirectory, "Assets", "UserGuide.html");

    [RelayCommand]
    private void OpenUserGuide()
    {
        if (!File.Exists(UserGuidePath))
        {
            _dialogs.Notify("The local HTML guide is missing. Rebuild or reinstall Android Dev Monitor.", error: true);
            return;
        }

        Process.Start(new ProcessStartInfo(UserGuidePath) { UseShellExecute = true });
    }
}
