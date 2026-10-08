#nullable enable
using System.Diagnostics;
using System.Globalization;
using System.IO;
using AndroidDevMonitor.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AndroidDevMonitor.Presentation.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MirrorButtonLabel))]
    private bool _isMirrorRunning;

    [ObservableProperty] private string _mirrorStatus = "Mirror is idle. Select a device, then press Start mirror.";
    /// <summary>The latest frame as a toolkit image (WPF ImageSource or Avalonia Bitmap).</summary>
    [ObservableProperty] private object? _mirrorFrame;
    [ObservableProperty] private int _mirrorFrameWidth;
    [ObservableProperty] private int _mirrorFrameHeight;
    [ObservableProperty] private int _mirrorFps = 2;

    private CancellationTokenSource? _mirrorCts;
    private byte[]? _lastMirrorFrame;

    public IReadOnlyList<int> MirrorFpsOptions { get; } = [1, 2, 4, 8];

    public string MirrorButtonLabel => IsMirrorRunning ? "Stop mirror" : "Start mirror";

    /// <summary>
    /// Streams the real device screen into this window over ADB only, so it works without scrcpy
    /// installed and without a USB cable. Each frame is a fresh `exec-out screencap -p` PNG.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleMirrorAsync()
    {
        if (IsMirrorRunning)
        {
            _mirrorCts?.Cancel();
            MirrorStatus = "Stopping mirror…";
            return;
        }

        AndroidDevice? device = SelectedDevice;
        if (device is null)
        {
            MirrorStatus = "Select a connected Android device first.";
            return;
        }

        string? adb = _adb.ResolvedAdbPath;
        if (adb is null)
        {
            MirrorStatus = "ADB executable was not found. Set the adb path in Settings, then retry.";
            return;
        }

        using CancellationTokenSource cts = new();
        _mirrorCts = cts;
        IsMirrorRunning = true;
        string serial = device.Serial;
        try
        {
            while (!cts.IsCancellationRequested)
            {
                Stopwatch frame = Stopwatch.StartNew();
                byte[]? png = await CaptureFrameAsync(adb, serial, cts.Token);
                if (png is { Length: > 0 })
                {
                    _lastMirrorFrame = png;
                    (MirrorFrameWidth, MirrorFrameHeight) = ReadPngSize(png);
                    MirrorFrame = _images.Decode(png);
                    MirrorStatus = $"{device.FriendlyName} · {png.Length / 1024:N0} KB per frame · {MirrorFps} fps · stop with Stop mirror";
                }
                else
                {
                    MirrorStatus = "Frame capture failed. Check that the device is still connected.";
                }
                frame.Stop();
                int target = 1000 / Math.Max(1, MirrorFps);
                int wait = Math.Max(25, target - (int)frame.ElapsedMilliseconds);
                await Task.Delay(wait, cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            MirrorStatus = "Mirror stopped: " + ex.Message;
        }
        finally
        {
            IsMirrorRunning = false;
            if (ReferenceEquals(_mirrorCts, cts)) _mirrorCts = null;
            if (MirrorFrame is not null) MirrorStatus = "Mirror stopped. Press Start mirror to resume.";
        }
    }

    [RelayCommand]
    private void SaveMirrorFrame()
    {
        byte[]? frame = _lastMirrorFrame;
        if (frame is not { Length: > 0 })
        {
            MirrorStatus = "Start the mirror first, then save a frame.";
            return;
        }
        try
        {
            Directory.CreateDirectory(DeveloperLabDirectory);
            string serial = (SelectedDevice?.Serial ?? "device").Replace(':', '-');
            string path = Path.Combine(DeveloperLabDirectory, $"mirror-{serial}-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}.png");
            File.WriteAllBytes(path, frame);
            MirrorStatus = "Frame saved to " + path;
        }
        catch (Exception ex)
        {
            MirrorStatus = "Saving the frame failed: " + ex.Message;
        }
    }

    /// <summary>
    /// Sends a tap to the device at the real screen coordinates that match a click on the mirror image.
    /// The frame is a raw screencap, so its pixel size equals the current device display size.
    /// </summary>
    public async Task TapMirrorAsync(int deviceX, int deviceY)
    {
        AndroidDevice? device = SelectedDevice;
        if (device is null)
        {
            MirrorStatus = "Select a device first.";
            return;
        }

        var result = await _adb.ExecuteAsync(
            device.Serial,
            ["shell", "input", "tap", deviceX.ToString(CultureInfo.InvariantCulture), deviceY.ToString(CultureInfo.InvariantCulture)],
            TimeSpan.FromSeconds(10),
            CancellationToken.None);
        MirrorStatus = result.Success
            ? $"Tap sent at {deviceX}, {deviceY} on {device.FriendlyName}."
            : "Tap failed: " + CleanError(result);
    }

    /// <summary>
    /// Pushes the computer's clipboard into the device text field that currently has focus.
    /// This is the practical clipboard path for ADB-only setups; the scrcpy window adds full sync.
    /// </summary>
    [RelayCommand]
    private async Task PasteClipboardToDeviceAsync()
    {
        string text;
        try
        {
            text = await _clipboard.GetTextAsync() ?? string.Empty;
        }
        catch
        {
            text = string.Empty;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            MirrorStatus = "The clipboard has no text to send.";
            return;
        }

        InputTextToSend = text;
        await SendInputTextCommand.ExecuteAsync(null);
        MirrorStatus = DeviceLabStatus;
    }

    private async Task<byte[]?> CaptureFrameAsync(string adbPath, string serial, CancellationToken token)
    {
        ProcessStartInfo info = new()
        {
            FileName = adbPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (string argument in new[] { "-s", serial, "exec-out", "screencap", "-p" }) info.ArgumentList.Add(argument);

        using Process process = new() { StartInfo = info };
        try
        {
            if (!process.Start()) return null;
            using MemoryStream buffer = new();
            Task copy = process.StandardOutput.BaseStream.CopyToAsync(buffer, token);
            Task<string> errors = process.StandardError.ReadToEndAsync(token);
            try
            {
                await process.WaitForExitAsync(token);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
                throw;
            }
            await copy;
            string error = await errors;
            byte[] bytes = buffer.ToArray();
            if (process.ExitCode != 0 || !IsPng(bytes))
            {
                MirrorStatus = string.IsNullOrWhiteSpace(error)
                    ? "The device did not return a PNG frame. Screen capture may be blocked on this build."
                    : "Frame capture failed: " + error.Trim();
                return null;
            }
            return bytes;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            MirrorStatus = "Frame capture failed: " + ex.Message;
            return null;
        }
    }

    private static bool IsPng(byte[] bytes) =>
        bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;

    /// <summary>Reads the pixel size from the PNG header (IHDR), so tap mapping does not depend on the UI toolkit.</summary>
    internal static (int Width, int Height) ReadPngSize(byte[] png)
    {
        if (png.Length < 24 || !IsPng(png)) return (0, 0);
        int width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        int height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        return width > 0 && height > 0 ? (width, height) : (0, 0);
    }
}
