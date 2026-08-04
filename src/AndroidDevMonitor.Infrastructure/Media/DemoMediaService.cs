using AndroidDevMonitor.Core.Models;
using AndroidDevMonitor.Core.Services;

namespace AndroidDevMonitor.Infrastructure.Media;

public sealed class DemoMediaService(string mediaDirectory) : IMediaService
{
    private readonly Dictionary<string, DateTimeOffset> _recordings = [];
    public string MediaDirectory { get; } = Ensure(mediaDirectory);

    public async Task<MediaItem> CaptureScreenshotAsync(AndroidDevice device, string? packageName, Guid? sessionId, CancellationToken cancellationToken)
    {
        var name = $"demo-screenshot-{device.Serial}-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}.png"; var path = Path.Combine(MediaDirectory, name);
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M/wHwAF/gL+TlyS9wAAAABJRU5ErkJggg==");
        await File.WriteAllBytesAsync(path, png, cancellationToken);
        var item = new MediaItem(Guid.NewGuid(), MediaKind.Screenshot, name, path, device.Serial, device.FriendlyName, packageName, DateTimeOffset.UtcNow, null, png.Length, sessionId, "Demo media", device.Resolution, path);
        return await SaveMetadataAsync(item, cancellationToken);
    }
    public Task StartRecordingAsync(AndroidDevice device, CancellationToken cancellationToken)
    {
        if (!_recordings.TryAdd(device.Serial, DateTimeOffset.UtcNow)) throw new InvalidOperationException("A recording is already active for this device.");
        return Task.CompletedTask;
    }
    public async Task<MediaItem> StopRecordingAsync(AndroidDevice device, string? packageName, Guid? sessionId, CancellationToken cancellationToken)
    {
        if (!_recordings.Remove(device.Serial, out var started)) throw new InvalidOperationException("No recording is active for this device.");
        var name = $"demo-recording-{device.Serial}-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}.mp4"; var path = Path.Combine(MediaDirectory, name); await File.WriteAllBytesAsync(path, [], cancellationToken);
        var item = new MediaItem(Guid.NewGuid(), MediaKind.Recording, name, path, device.Serial, device.FriendlyName, packageName, started, DateTimeOffset.UtcNow - started, 0, sessionId, "Demo recording metadata", device.Resolution);
        return await SaveMetadataAsync(item, cancellationToken);
    }
    public async Task<IReadOnlyList<MediaItem>> ScanAsync(CancellationToken cancellationToken)
    {
        var paths = await Task.Run(() => Directory.EnumerateFiles(MediaDirectory).Where(path => path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)).ToArray(), cancellationToken).ConfigureAwait(false);
        var result = new List<MediaItem>(paths.Length);
        foreach (var path in paths)
        {
            var file = new FileInfo(path);
            var stored = await MediaMetadataStore.ReadAsync(path, cancellationToken).ConfigureAwait(false);
            result.Add(stored is null
                ? new(Guid.NewGuid(), path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? MediaKind.Screenshot : MediaKind.Recording, file.Name, file.FullName, "demo", "Demo Device", "com.company.mygame", file.CreationTimeUtc, null, file.Length, null, "Demo media", null, path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? path : null)
                : stored with { FileName = file.Name, LocalPath = file.FullName, FileSize = file.Length, ThumbnailPath = stored.Kind == MediaKind.Screenshot ? file.FullName : stored.ThumbnailPath });
        }
        return result.OrderByDescending(item => item.CapturedUtc).ToArray();
    }

    public async Task<MediaItem> RenameAsync(MediaItem item, string newFileName, CancellationToken cancellationToken)
    {
        var clean = Path.GetFileName(newFileName.Trim());
        if (string.IsNullOrWhiteSpace(clean) || clean.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("Enter a valid file name.", nameof(newFileName));
        var extension = Path.GetExtension(item.LocalPath);
        if (!clean.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) clean += extension;
        var destination = Path.Combine(Path.GetDirectoryName(item.LocalPath)!, clean);
        if (!string.Equals(destination, item.LocalPath, StringComparison.OrdinalIgnoreCase)) File.Move(item.LocalPath, destination);
        var renamed = item with { FileName = clean, LocalPath = destination, ThumbnailPath = item.Kind == MediaKind.Screenshot ? destination : item.ThumbnailPath };
        await MediaMetadataStore.SaveAsync(renamed, cancellationToken);
        if (!string.Equals(destination, item.LocalPath, StringComparison.OrdinalIgnoreCase)) MediaMetadataStore.Delete(item.LocalPath);
        return renamed;
    }

    public async Task<MediaItem> SaveMetadataAsync(MediaItem item, CancellationToken cancellationToken)
    {
        await MediaMetadataStore.SaveAsync(item, cancellationToken);
        return item;
    }

    public Task DeleteAsync(MediaItem item, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(item.LocalPath)) File.Delete(item.LocalPath);
        MediaMetadataStore.Delete(item.LocalPath);
        return Task.CompletedTask;
    }
    private static string Ensure(string path) { Directory.CreateDirectory(path); return path; }
}
