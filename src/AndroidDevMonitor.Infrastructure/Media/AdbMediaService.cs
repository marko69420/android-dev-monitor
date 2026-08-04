using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using AndroidDevMonitor.Core.Configuration;
using AndroidDevMonitor.Core.Models;
using AndroidDevMonitor.Core.Services;

namespace AndroidDevMonitor.Infrastructure.Media;

public sealed class AdbMediaService(IAdbExecutor adb, string mediaDirectory) : IMediaService
{
    private sealed record RecordingState(string RemotePath, DateTimeOffset Started, Process Process);
    private readonly ConcurrentDictionary<string, RecordingState> _recordings = new();
    public string MediaDirectory { get; } = Ensure(mediaDirectory);

    public async Task<MediaItem> CaptureScreenshotAsync(AndroidDevice device, string? packageName, Guid? sessionId, CancellationToken cancellationToken)
    {
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff"); var safeSerial = Safe(device.Serial); var name = $"screenshot-{safeSerial}-{stamp}.png"; var local = Path.Combine(MediaDirectory, name); var remote = $"/sdcard/Download/{name}";
        var capture = await adb.ExecuteAsync(device.Serial, ["shell", "screencap", "-p", remote], MonitoringConstants.DefaultAdbTimeout, cancellationToken);
        if (!capture.Success) throw new InvalidOperationException(capture.StandardError);
        var pull = await adb.ExecuteAsync(device.Serial, ["pull", remote, local], TimeSpan.FromMinutes(1), cancellationToken);
        await adb.ExecuteAsync(device.Serial, ["shell", "rm", remote], MonitoringConstants.DefaultAdbTimeout, cancellationToken);
        if (!pull.Success) throw new InvalidOperationException(pull.StandardError);
        var item = new MediaItem(Guid.NewGuid(), MediaKind.Screenshot, name, local, device.Serial, device.FriendlyName, packageName, DateTimeOffset.UtcNow, null, new FileInfo(local).Length, sessionId, null, device.Resolution, local);
        return await SaveMetadataAsync(item, cancellationToken);
    }

    public async Task StartRecordingAsync(AndroidDevice device, CancellationToken cancellationToken)
    {
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff"); var remote = $"/sdcard/Download/recording-{Safe(device.Serial)}-{stamp}.mp4";
        if (_recordings.ContainsKey(device.Serial)) throw new InvalidOperationException("A recording is already active for this device.");
        if (adb.ResolvedAdbPath is null) throw new InvalidOperationException("ADB executable was not found.");
        var startInfo = new ProcessStartInfo { FileName = adb.ResolvedAdbPath, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var argument in new[] { "-s", device.Serial, "shell", "screenrecord", "--time-limit", "180", remote }) startInfo.ArgumentList.Add(argument);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Unable to start adb screenrecord.");
            if (!_recordings.TryAdd(device.Serial, new(remote, DateTimeOffset.UtcNow, process))) { process.Kill(true); process.Dispose(); throw new InvalidOperationException("A recording is already active for this device."); }
            await Task.Delay(400, cancellationToken);
            if (process.HasExited) { var error = await process.StandardError.ReadToEndAsync(cancellationToken); _recordings.TryRemove(device.Serial, out _); process.Dispose(); throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Android screenrecord exited immediately." : error); }
        }
        catch { if (!_recordings.ContainsKey(device.Serial)) process.Dispose(); throw; }
    }

    public async Task<MediaItem> StopRecordingAsync(AndroidDevice device, string? packageName, Guid? sessionId, CancellationToken cancellationToken)
    {
        if (!_recordings.TryRemove(device.Serial, out var state)) throw new InvalidOperationException("No recording is active for this device.");
        var signal = await adb.ExecuteAsync(device.Serial, ["shell", "pkill", "-2", "screenrecord"], MonitoringConstants.DefaultAdbTimeout, cancellationToken);
        if (!signal.Success) await adb.ExecuteAsync(device.Serial, ["shell", "killall", "-2", "screenrecord"], MonitoringConstants.DefaultAdbTimeout, cancellationToken);
        using (state.Process)
        {
            using var finishCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); finishCts.CancelAfter(TimeSpan.FromSeconds(8));
            try { await state.Process.WaitForExitAsync(finishCts.Token); } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { try { state.Process.Kill(true); } catch { } }
        }
        await Task.Delay(500, cancellationToken);
        var name = Path.GetFileName(state.RemotePath); var local = Path.Combine(MediaDirectory, name); var pull = await adb.ExecuteAsync(device.Serial, ["pull", state.RemotePath, local], TimeSpan.FromMinutes(2), cancellationToken);
        if (!pull.Success) throw new InvalidOperationException(pull.StandardError);
        await adb.ExecuteAsync(device.Serial, ["shell", "rm", state.RemotePath], MonitoringConstants.DefaultAdbTimeout, cancellationToken);
        var item = new MediaItem(Guid.NewGuid(), MediaKind.Recording, name, local, device.Serial, device.FriendlyName, packageName, state.Started, DateTimeOffset.UtcNow - state.Started, new FileInfo(local).Length, sessionId, null, device.Resolution);
        return await SaveMetadataAsync(item, cancellationToken);
    }

    public async Task<IReadOnlyList<MediaItem>> ScanAsync(CancellationToken cancellationToken)
    {
        var paths = await Task.Run(() => Directory.EnumerateFiles(MediaDirectory)
            .Where(path => path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            .ToArray(), cancellationToken).ConfigureAwait(false);
        var items = new List<MediaItem>(paths.Length);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            var stored = await MediaMetadataStore.ReadAsync(path, cancellationToken).ConfigureAwait(false);
            items.Add(stored is null
                ? new(Guid.NewGuid(), path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? MediaKind.Screenshot : MediaKind.Recording, info.Name, info.FullName, "Unknown", "Unknown", null, info.CreationTimeUtc, null, info.Length, null, null, null, path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? path : null)
                : stored with { FileName = info.Name, LocalPath = info.FullName, FileSize = info.Length, ThumbnailPath = stored.Kind == MediaKind.Screenshot ? info.FullName : stored.ThumbnailPath });
        }
        return items.OrderByDescending(item => item.CapturedUtc).ToArray();
    }

    public async Task<MediaItem> RenameAsync(MediaItem item, string newFileName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(item.LocalPath)) throw new FileNotFoundException("The media file no longer exists.", item.LocalPath);
        var clean = Path.GetFileName(newFileName.Trim());
        if (string.IsNullOrWhiteSpace(clean) || clean.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("Enter a valid file name.", nameof(newFileName));
        var extension = Path.GetExtension(item.LocalPath);
        if (!clean.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) clean += extension;
        var destination = Path.Combine(Path.GetDirectoryName(item.LocalPath)!, clean);
        if (!string.Equals(destination, item.LocalPath, StringComparison.OrdinalIgnoreCase) && File.Exists(destination)) throw new IOException($"A file named {clean} already exists.");
        if (!string.Equals(destination, item.LocalPath, StringComparison.OrdinalIgnoreCase)) File.Move(item.LocalPath, destination);
        var renamed = item with
        {
            FileName = clean,
            LocalPath = destination,
            ThumbnailPath = item.Kind == MediaKind.Screenshot ? destination : item.ThumbnailPath
        };
        await MediaMetadataStore.SaveAsync(renamed, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(destination, item.LocalPath, StringComparison.OrdinalIgnoreCase)) MediaMetadataStore.Delete(item.LocalPath);
        return renamed;
    }

    public async Task<MediaItem> SaveMetadataAsync(MediaItem item, CancellationToken cancellationToken)
    {
        await MediaMetadataStore.SaveAsync(item, cancellationToken).ConfigureAwait(false);
        return item;
    }

    public Task DeleteAsync(MediaItem item, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(item.LocalPath)) File.Delete(item.LocalPath);
        if (!string.IsNullOrWhiteSpace(item.ThumbnailPath) && !string.Equals(item.ThumbnailPath, item.LocalPath, StringComparison.OrdinalIgnoreCase) && File.Exists(item.ThumbnailPath))
            File.Delete(item.ThumbnailPath);
        MediaMetadataStore.Delete(item.LocalPath);
        return Task.CompletedTask;
    }
    private static string Ensure(string path) { Directory.CreateDirectory(path); return path; }
    private static string Safe(string value) => string.Concat(value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
}

internal static class MediaMetadataStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static string Sidecar(string mediaPath) => mediaPath + ".adm.json";

    public static async Task SaveAsync(MediaItem item, CancellationToken cancellationToken)
    {
        var sidecar = Sidecar(item.LocalPath);
        var temporary = sidecar + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(item, JsonOptions), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, sidecar, true);
    }

    public static async Task<MediaItem?> ReadAsync(string mediaPath, CancellationToken cancellationToken)
    {
        var sidecar = Sidecar(mediaPath);
        if (!File.Exists(sidecar)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(sidecar, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<MediaItem>(json);
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    public static void Delete(string mediaPath)
    {
        var sidecar = Sidecar(mediaPath);
        if (File.Exists(sidecar)) File.Delete(sidecar);
    }
}
