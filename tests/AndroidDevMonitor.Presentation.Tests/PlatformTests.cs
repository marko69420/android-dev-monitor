using AndroidDevMonitor.Core.Models;
using AndroidDevMonitor.Presentation.Platform;
using AndroidDevMonitor.Presentation.ViewModels;

namespace AndroidDevMonitor.Presentation.Tests;

public sealed class PlatformTests
{
    [Fact]
    public void Autostart_entry_is_a_valid_desktop_entry()
    {
        string entry = DesktopShell.CreateAutostartEntry("/opt/android-dev-monitor/AndroidDevMonitor");
        Assert.StartsWith("[Desktop Entry]\n", entry);
        Assert.Contains("Type=Application\n", entry);
        Assert.Contains("Exec=/opt/android-dev-monitor/AndroidDevMonitor\n", entry);
    }

    [Theory]
    [InlineData("/usr/bin/app", "/usr/bin/app")]
    [InlineData("/home/dev/Android Dev Monitor/app", "\"/home/dev/Android Dev Monitor/app\"")]
    [InlineData("/tmp/a$b", "\"/tmp/a\\$b\"")]
    [InlineData("/opt/100%/app", "/opt/100%%/app")]
    public void Exec_arguments_are_quoted_when_needed(string value, string expected) =>
        Assert.Equal(expected, DesktopShell.QuoteExecArgument(value));

    [Fact]
    public void Linux_autostart_is_written_and_removed()
    {
        if (OperatingSystem.IsWindows()) return;
        string directory = Path.Combine(Path.GetTempPath(), "adm-autostart-" + Guid.NewGuid().ToString("N"));
        try
        {
            DesktopShell shell = new(directory);
            shell.SetStartWithSystem(true);
            Assert.True(File.Exists(shell.AutostartEntryPath));
            Assert.Contains("Exec=", File.ReadAllText(shell.AutostartEntryPath));
            shell.SetStartWithSystem(false);
            Assert.False(File.Exists(shell.AutostartEntryPath));
            shell.SetStartWithSystem(false);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Png_size_is_read_from_the_header()
    {
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAMAAAACCAYAAACddGYaAAAAEUlEQVR4nGP4z8DwH4YZkDkAm34L9XKwuTwAAAAASUVORK5CYII=");
        Assert.Equal((3, 2), MainViewModel.ReadPngSize(png));
        Assert.Equal((0, 0), MainViewModel.ReadPngSize([1, 2, 3]));
    }

    [Theory]
    [InlineData(DeviceState.NoPermissions, "udev")]
    [InlineData(DeviceState.Unauthorized, "Allow USB debugging")]
    [InlineData(DeviceState.Offline, "Reconnect")]
    public void Unusable_devices_explain_the_fix(DeviceState state, string hint)
    {
        AndroidDevice device = new("ABC123", "Pixel 8", state, DeviceKind.Physical);
        Assert.Contains(hint, MainViewModel.DescribeUnusableDevice(device));
    }

    [Fact]
    public void Mirror_click_maps_through_the_letterbox()
    {
        // A 1080x2400 portrait frame in a 1000x1000 control: scale 1000/2400, bars left and right.
        Assert.Equal((540, 1200), MirrorGeometry.ToDevice(1000, 1000, 1080, 2400, 500, 500));
        Assert.Equal((0, 0), MirrorGeometry.ToDevice(1000, 1000, 1080, 2400, 275, 0));
        Assert.Null(MirrorGeometry.ToDevice(1000, 1000, 1080, 2400, 10, 500));
        Assert.Null(MirrorGeometry.ToDevice(1000, 1000, 0, 0, 500, 500));
    }
}
