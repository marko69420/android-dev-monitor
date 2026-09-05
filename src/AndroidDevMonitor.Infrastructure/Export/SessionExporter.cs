using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AndroidDevMonitor.Core.Models;
using AndroidDevMonitor.Core.Services;

namespace AndroidDevMonitor.Infrastructure.Export;

public sealed class SessionExporter : ISessionExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private sealed record SessionExportSnapshot(Guid Id, DateTimeOffset StartedUtc, DateTimeOffset? EndedUtc, AndroidDevice Device, string? CurrentPackage, bool IsPaused, TimeSpan ActiveCollectionTime, MetricSample[] Samples, SessionMarker[] Markers, SessionAlert[] Alerts, SessionEvent[] Events);
    public async Task<string> ExportJsonAsync(MonitoringSession session, string directory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, $"session-{session.Id:N}.json");
        var json = await Task.Run(() => JsonSerializer.Serialize(Capture(session), JsonOptions), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false); return path;
    }

    public async Task<string> ExportCsvAsync(MonitoringSession session, string directory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, $"session-{session.Id:N}.csv");
        var csv = await Task.Run(() =>
        {
            var builder = new StringBuilder();
            builder.AppendLine("timestamp_utc,device_serial,package,device_cpu_percent,process_cpu_percent,device_memory_used_bytes,process_rss_bytes,process_pss_bytes,disk_read_bps,disk_write_bps,network_rx_bps,network_tx_bps,fps,p95_frame_ms,jank_percent,temperature_c,gpu_percent,source,gpu_source,gpu_availability,device_disk_read_bps,device_disk_write_bps,device_disk_source");
            MetricSample[] samples; lock (session.SyncRoot) samples = session.Samples.ToArray();
            foreach (var s in samples)
            {
                string V(double? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";
                builder.AppendLine(string.Join(',', Q(s.TimestampUtc.ToString("O")), Q(s.DeviceSerial), Q(s.PackageName), V(s.DeviceCpuPercent), V(s.ProcessCpuPercent), s.DeviceMemoryUsedBytes, s.ProcessRssBytes, s.ProcessPssBytes, V(s.DiskReadBytesPerSecond), V(s.DiskWriteBytesPerSecond), V(s.NetworkRxBytesPerSecond), V(s.NetworkTxBytesPerSecond), V(s.Fps), V(s.FrameTimeP95Ms), V(s.JankPercent), V(s.TemperatureCelsius), V(s.GpuPercent), Q(s.Source), Q(s.GpuSource), Q(s.GpuAvailability?.ToString()), V(s.DeviceDiskReadBytesPerSecond), V(s.DeviceDiskWriteBytesPerSecond), Q(s.DeviceDiskSource)));
            }
            return builder.ToString();
        }, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(path, csv, Encoding.UTF8, cancellationToken).ConfigureAwait(false); return path;
    }

    public async Task<string> ExportZipAsync(MonitoringSession session, string directory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory); var work = Path.Combine(Path.GetTempPath(), $"adm-{Guid.NewGuid():N}"); Directory.CreateDirectory(work);
        try
        {
            await ExportJsonAsync(session, work, cancellationToken).ConfigureAwait(false); await ExportCsvAsync(session, work, cancellationToken).ConfigureAwait(false);
            var zip = Path.Combine(directory, $"session-{session.Id:N}.zip");
            await Task.Run(() => { if (File.Exists(zip)) File.Delete(zip); ZipFile.CreateFromDirectory(work, zip, CompressionLevel.Optimal, false); }, cancellationToken).ConfigureAwait(false); return zip;
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }
    private static string Q(string? value) => $"\"{(value ?? "").Replace("\"", "\"\"")}\"";
    private static SessionExportSnapshot Capture(MonitoringSession session)
    {
        lock (session.SyncRoot) return new(session.Id, session.StartedUtc, session.EndedUtc, session.Device, session.CurrentPackage, session.IsPaused, session.ActiveCollectionTime, session.Samples.ToArray(), session.Markers.ToArray(), session.Alerts.ToArray(), session.Events.ToArray());
    }
}
