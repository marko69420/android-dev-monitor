using Avalonia.Headless.XUnit;
using AndroidDevMonitor.Core.Configuration;
using AndroidDevMonitor.Core.Models;

namespace AndroidDevMonitor.Desktop.Tests;

/// <summary>
/// The app in live mode, against tests/fake-android: real adb process execution, parsing and UI updates,
/// with /proc numbers from this machine's kernel.
/// </summary>
public sealed class LiveDeviceTests
{
    private const string Serial = "FAKE0001";
    private const string Package = "com.example.fakegame";

    /// <summary>The fake device is a Python script that reads /proc, so these tests run on Linux only.</summary>
    private static bool FakeDeviceAvailable => OperatingSystem.IsLinux();

    private static async Task<AppSession> StartLiveAsync([System.Runtime.CompilerServices.CallerMemberName] string name = "")
    {
        AppSession session = await AppSession.StartAsync(demo: false, name);
        await AppSession.WaitUntil(() => session.ViewModel.SelectedDevice?.Serial == Serial, "the fake device to be discovered");
        return session;
    }

    [AvaloniaFact]
    public async Task Discovers_the_device_and_shows_live_metrics()
    {
        if (!FakeDeviceAvailable) return;
        await using AppSession session = await StartLiveAsync();
        AndroidDevice device = session.ViewModel.SelectedDevice!;
        Assert.Equal("Pixel 8 Pro", device.Model);
        Assert.Contains("Android 14", device.VersionLine);
        Assert.Equal("1344x2992", device.Resolution);

        await AppSession.WaitUntil(() => session.ViewModel.SelectedPackage == Package, "foreground app to be selected");
        await AppSession.WaitUntil(() => session.ViewModel.ProcessRows.Any(row => row.PackageOrCommand.Contains(Package)), "the app in the process table");
        await AppSession.WaitUntil(() => session.ViewModel.CpuCard.Values.Any(double.IsFinite), "CPU samples");
        await AppSession.WaitUntil(() => session.ViewModel.MemoryCard.DisplayValue.Contains("GB"), "memory card");
        await AppSession.WaitUntil(() => session.ViewModel.Logs.Any(entry => entry.Source == "logcat" && entry.Message.Contains("FakeGame")), "logcat rows");
        await AppSession.WaitUntil(() => session.ViewModel.FpsChart.PrimaryPoints.Any(point => point.Value > 0), "frame rate from gfxinfo", 45);

        Assert.Contains(session.AdbCommands(), line => line.Contains("adb devices -l"));
        Assert.Contains(session.AdbCommands(), line => line.Contains("shell pidof com.example.fakegame"));
        session.Screenshot("live-overview");
        session.ShowPage("Logs");
        session.Screenshot("live-logs");
        session.ShowPage("Settings");
        Assert.Contains("fake-android", session.ViewModel.AdbPathDisplay);
        session.Screenshot("live-settings");
    }

    [AvaloniaFact]
    public async Task File_explorer_lists_pulls_pushes_renames_and_deletes()
    {
        if (!FakeDeviceAvailable) return;
        await using AppSession session = await StartLiveAsync();
        session.ShowPage("File Explorer");
        string local = Path.Combine(session.Scratch, "transfer");
        Directory.CreateDirectory(local);
        File.WriteAllText(Path.Combine(local, "from computer.txt"), "pushed from Linux\n");
        session.ViewModel.LocalPath = local;

        session.ViewModel.SelectedRemoteQuickLocation = "/sdcard/Download";
        await session.ClickAsync("Go");
        await AppSession.WaitUntil(() => session.ViewModel.RemoteFiles.Any(file => file.Name == "readme.txt"), "remote listing");
        FileEntry quoted = session.ViewModel.RemoteFiles.Single(file => file.Name == "it's a \"quoted\" name.txt");
        Assert.Equal("/sdcard/Download/it's a \"quoted\" name.txt", quoted.FullPath);
        Assert.NotNull(quoted.ModifiedUtc);

        // Pull a file whose name needs quoting.
        session.ViewModel.SelectedRemoteFile = quoted;
        await session.ClickAsync("← Pull selected");
        Assert.Equal("quote test\n", File.ReadAllText(Path.Combine(local, quoted.Name)));

        // Push a file with a space in its name.
        await AppSession.WaitUntil(() => session.ViewModel.LocalFiles.Any(file => file.Name == "from computer.txt"), "local listing");
        session.ViewModel.SelectedLocalFile = session.ViewModel.LocalFiles.Single(file => file.Name == "from computer.txt");
        await session.ClickAsync("Push selected →");
        Assert.Equal("pushed from Linux\n", File.ReadAllText(Path.Combine(session.DeviceRoot, "sdcard", "Download", "from computer.txt")));
        await AppSession.WaitUntil(() => session.ViewModel.RemoteFiles.Any(file => file.Name == "from computer.txt"), "pushed file in the listing");

        // Create, rename and delete a folder with an apostrophe in its name.
        session.ViewModel.NewRemoteFolderName = "Level's saves";
        await session.ClickAsync("Create folder");
        Assert.True(Directory.Exists(Path.Combine(session.DeviceRoot, "sdcard", "Download", "Level's saves")));
        session.ViewModel.SelectedRemoteFile = session.ViewModel.RemoteFiles.Single(file => file.Name == "Level's saves");
        session.ViewModel.RemoteRenameText = "Old saves";
        await session.ClickAsync("Rename");
        Assert.True(Directory.Exists(Path.Combine(session.DeviceRoot, "sdcard", "Download", "Old saves")));
        session.ViewModel.SelectedRemoteFile = session.ViewModel.RemoteFiles.Single(file => file.Name == "Old saves");
        await session.ClickAsync("Delete");
        Assert.Contains(session.Platform.Confirmations, text => text.Contains("Serial: FAKE0001") && text.Contains("/sdcard/Download/Old saves"));
        Assert.False(Directory.Exists(Path.Combine(session.DeviceRoot, "sdcard", "Download", "Old saves")));
        session.Screenshot("live-file-explorer");
    }

    [AvaloniaFact]
    public async Task Adb_shell_runs_commands_on_the_device()
    {
        if (!FakeDeviceAvailable) return;
        await using AppSession session = await StartLiveAsync();
        session.ShowPage("ADB Shell");
        session.ViewModel.ShellCommand = "getprop ro.product.model; echo linux-ok";
        await session.ClickAsync("Run");
        await AppSession.WaitUntil(() => session.ViewModel.ShellOutput.Contains("linux-ok"), "shell output");
        Assert.Contains("Pixel 8 Pro", session.ViewModel.ShellOutput);

        string saved = Path.Combine(session.Scratch, "shell.txt");
        session.Platform.NextSaveFile = saved;
        await session.ClickAsync("Save output");
        Assert.Contains("linux-ok", File.ReadAllText(saved));
        session.Screenshot("live-adb-shell");
    }

    [AvaloniaFact]
    public async Task Screenshot_is_captured_pulled_and_listed_in_media()
    {
        if (!FakeDeviceAvailable) return;
        await using AppSession session = await StartLiveAsync();
        await session.ClickAsync("Screenshot");
        await AppSession.WaitUntil(() => session.ViewModel.MediaItems.Any(item => item.Kind == MediaKind.Screenshot), "screenshot in the media library");
        MediaItem shot = session.ViewModel.MediaItems.First(item => item.Kind == MediaKind.Screenshot);
        Assert.StartsWith(Path.Combine(AppPaths.DataDirectory, "Media"), shot.LocalPath);
        byte[] bytes = File.ReadAllBytes(shot.LocalPath);
        Assert.Equal(0x89, bytes[0]);
        Assert.Equal((byte)'P', bytes[1]);

        session.ShowPage("Media");
        session.ViewModel.SelectedMediaItem = shot;
        session.Settle();
        await session.ClickAsync("Copy path");
        Assert.Equal(shot.LocalPath, session.Platform.Clipboard);
        await session.ClickAsync("Open / Play");
        Assert.Contains(shot.LocalPath, session.Platform.Opened);
        session.Screenshot("live-media");
    }

    [AvaloniaFact]
    public async Task Live_mirror_streams_frames_and_taps_map_to_the_device()
    {
        if (!FakeDeviceAvailable) return;
        await using AppSession session = await StartLiveAsync();
        session.ShowPage("App & Device Lab");
        await session.ClickAsync("Start mirror", waitForCompletion: false);
        await AppSession.WaitUntil(() => session.ViewModel.MirrorFrame is Avalonia.Media.Imaging.Bitmap, "a mirrored frame");
        Assert.Equal(270, session.ViewModel.MirrorFrameWidth);
        Assert.Equal(600, session.ViewModel.MirrorFrameHeight);
        session.Screenshot("live-mirror");

        await session.ViewModel.TapMirrorAsync(135, 300);
        await AppSession.WaitUntil(() => session.AdbCommands().Any(line => line.Contains("input tap 135 300")), "the tap to reach the device");

        session.ViewModel.ToggleMirrorCommand.Execute(null);
        await AppSession.WaitUntil(() => !session.ViewModel.IsMirrorRunning, "the mirror to stop");
    }

    [AvaloniaFact]
    public async Task Apk_install_and_app_controls_run_against_the_device()
    {
        if (!FakeDeviceAvailable) return;
        await using AppSession session = await StartLiveAsync();
        await AppSession.WaitUntil(() => session.ViewModel.SelectedPackage == Package, "selected app");
        session.ShowPage("App & Device Lab");
        string apk = Path.Combine(session.Scratch, "game build.apk");
        File.WriteAllBytes(apk, [0x50, 0x4B, 0x03, 0x04]);
        session.Platform.NextOpenFile = apk;
        await session.ClickAsync("Browse");
        Assert.Equal(apk, session.ViewModel.ApkPath);
        await session.ClickAsync("Install / update");
        Assert.Contains("Installed game build.apk", session.ViewModel.DeviceLabStatus);
        Assert.Contains(session.AdbCommands(), line => line.Contains("install -r -t") && line.Contains("game build.apk"));

        await session.ClickAsync("Force stop");
        Assert.Contains(session.AdbCommands(), line => line.Contains($"am force-stop {Package}"));
        await session.ClickAsync("Clear data…");
        Assert.Contains(session.Platform.Confirmations, text => text.StartsWith("Clear app data"));
        Assert.Contains(session.AdbCommands(), line => line.Contains($"pm clear {Package}"));
    }

    [AvaloniaFact]
    public async Task Crash_in_logcat_raises_an_alert_with_sound()
    {
        if (!FakeDeviceAvailable) return;
        await using AppSession session = await StartLiveAsync();
        Environment.SetEnvironmentVariable("FAKE_ANDROID_CRASH", "1");
        await AppSession.WaitUntil(() => session.ViewModel.ActiveAlerts.Any(alert => alert.Type == "Crash"), "crash alert", 45);
        Assert.True(session.Platform.AlertSounds > 0);
        session.ShowPage("Alerts");
        session.Screenshot("live-alerts");
    }

    [AvaloniaFact]
    public async Task Phone_without_usb_permission_tells_the_user_how_to_fix_it()
    {
        if (!FakeDeviceAvailable) return;
        Environment.SetEnvironmentVariable("FAKE_ANDROID_STATE", "no-permissions");
        try
        {
            await using AppSession session = await AppSession.StartAsync(demo: false);
            await AppSession.WaitUntil(() => session.ViewModel.SelectedDevice?.Serial == Serial, "the device to be listed");
            Assert.Equal(DeviceState.NoPermissions, session.ViewModel.SelectedDevice!.State);
            await AppSession.WaitUntil(() => session.ViewModel.ActiveAlerts.Any(alert => alert.Message.Contains("udev")), "an alert that explains the fix");
            Assert.Equal("No USB permission", session.ViewModel.ConnectionText);
            Assert.DoesNotContain(session.AdbCommands(), line => line.Contains("shell"));
            session.ShowPage("Alerts");
            session.Screenshot("live-no-permissions");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FAKE_ANDROID_STATE", null);
        }
    }

    [AvaloniaFact]
    public async Task Closing_the_window_saves_the_session()
    {
        if (!FakeDeviceAvailable) return;
        string data;
        await using (AppSession session = await StartLiveAsync())
        {
            await AppSession.WaitUntil(() => session.ViewModel.CpuCard.Values.Count(double.IsFinite) >= 3, "a few samples");
            await session.ClickAsync("Mark Event");
            data = AppPaths.DataDirectory;
        }
        // A fresh store on the same folder sees the completed session with its marker.
        var store = new AndroidDevMonitor.Infrastructure.Database.SqliteSessionStore(data);
        await store.InitializeAsync(CancellationToken.None);
        IReadOnlyList<SessionSummary> sessions = await store.ListSessionSummariesAsync(CancellationToken.None);
        SessionSummary saved = Assert.Single(sessions);
        Assert.NotNull(saved.EndedUtc);
        Assert.Equal(Serial, saved.Device.Serial);
        Assert.Equal(1, saved.MarkerCount);
    }
}
