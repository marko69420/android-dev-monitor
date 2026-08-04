using System.Text.Json;
using AndroidDevMonitor.Core.Models;
using AndroidDevMonitor.Core.Services;
using Microsoft.Data.Sqlite;

namespace AndroidDevMonitor.Infrastructure.Database;

public sealed class SqliteSessionStore : ISessionStore
{
    private readonly string _connectionString;
    public string DatabasePath { get; }

    public SqliteSessionStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        DatabasePath = Path.Combine(dataDirectory, "android-dev-monitor.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY, started_utc TEXT NOT NULL, ended_utc TEXT, device_json TEXT NOT NULL, package TEXT, paused INTEGER NOT NULL, active_ms INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS samples(id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL, timestamp_utc TEXT NOT NULL, payload_json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_samples_session_time ON samples(session_id, timestamp_utc);
            CREATE TABLE IF NOT EXISTS markers(id TEXT PRIMARY KEY, session_id TEXT NOT NULL, timestamp_utc TEXT NOT NULL, payload_json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS alerts(id TEXT PRIMARY KEY, session_id TEXT NOT NULL, timestamp_utc TEXT NOT NULL, payload_json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS events(id TEXT PRIMARY KEY, session_id TEXT NOT NULL, timestamp_utc TEXT NOT NULL, payload_json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS session_summaries(session_id TEXT PRIMARY KEY, stats_json TEXT NOT NULL, export_path TEXT);
            CREATE TABLE IF NOT EXISTS media(id TEXT PRIMARY KEY, captured_utc TEXT NOT NULL, payload_json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS device_aliases(serial TEXT PRIMARY KEY, alias TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value_json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS automation_sequences(id TEXT PRIMARY KEY, name TEXT NOT NULL, payload_json TEXT NOT NULL);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveSessionAsync(MonitoringSession session, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO sessions(id,started_utc,ended_utc,device_json,package,paused,active_ms) VALUES($id,$start,$end,$device,$package,$paused,$active) ON CONFLICT(id) DO UPDATE SET ended_utc=$end,package=$package,paused=$paused,active_ms=$active";
        command.Parameters.AddWithValue("$id", session.Id.ToString()); command.Parameters.AddWithValue("$start", session.StartedUtc.ToString("O")); command.Parameters.AddWithValue("$end", (object?)session.EndedUtc?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$device", JsonSerializer.Serialize(session.Device)); command.Parameters.AddWithValue("$package", (object?)session.CurrentPackage ?? DBNull.Value); command.Parameters.AddWithValue("$paused", session.IsPaused); command.Parameters.AddWithValue("$active", (long)session.ActiveCollectionTime.TotalMilliseconds);
        await command.ExecuteNonQueryAsync(cancellationToken);

        MetricSample[] samples;
        SessionMarker[] markers;
        SessionAlert[] alerts;
        SessionEvent[] events;
        lock (session.SyncRoot)
        {
            samples = session.Samples.ToArray();
            markers = session.Markers.ToArray();
            alerts = session.Alerts.ToArray();
            events = session.Events.ToArray();
        }
        var fpsValues = samples.Where(sample => sample.Fps.HasValue).Select(sample => sample.Fps!.Value).ToArray();
        var frameTimes = samples.Where(sample => sample.FrameTimeP95Ms.HasValue).Select(sample => sample.FrameTimeP95Ms!.Value).Order().ToArray();
        var memoryValues = samples.Select(sample => sample.ProcessPssBytes ?? sample.ProcessRssBytes).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        var cpuValues = samples.Where(sample => sample.DeviceCpuPercent.HasValue).Select(sample => sample.DeviceCpuPercent!.Value).ToArray();
        var stats = new SessionStats(
            fpsValues.Length == 0 ? null : fpsValues.Average(),
            Percentile(frameTimes, 0.95),
            memoryValues.Length == 0 ? null : memoryValues.Max(),
            cpuValues.Length == 0 ? null : cpuValues.Max());
        var summary = connection.CreateCommand();
        summary.CommandText = "INSERT INTO session_summaries(session_id,stats_json,export_path) VALUES($id,$stats,NULL) ON CONFLICT(session_id) DO UPDATE SET stats_json=$stats";
        summary.Parameters.AddWithValue("$id", session.Id.ToString());
        summary.Parameters.AddWithValue("$stats", JsonSerializer.Serialize(stats));
        await summary.ExecuteNonQueryAsync(cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var marker in markers) await UpsertPayloadAsync(connection, transaction, "markers", marker.Id, marker.SessionId, marker.TimestampUtc, marker, cancellationToken);
        foreach (var alert in alerts) await UpsertPayloadAsync(connection, transaction, "alerts", alert.Id, alert.SessionId, alert.TimestampUtc, alert, cancellationToken);
        foreach (var sessionEvent in events) await UpsertPayloadAsync(connection, transaction, "events", sessionEvent.Id, sessionEvent.SessionId, sessionEvent.TimestampUtc, sessionEvent, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task AppendSamplesAsync(IReadOnlyList<MetricSample> samples, CancellationToken cancellationToken)
    {
        if (samples.Count == 0) return;
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken); await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var sample in samples)
        {
            var command = connection.CreateCommand(); command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "INSERT INTO samples(session_id,timestamp_utc,payload_json) VALUES($session,$time,$json)";
            command.Parameters.AddWithValue("$session", sample.SessionId.ToString()); command.Parameters.AddWithValue("$time", sample.TimestampUtc.ToString("O")); command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(sample));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SaveMarkerAsync(SessionMarker marker, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken); var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO markers(id,session_id,timestamp_utc,payload_json) VALUES($id,$session,$time,$json)";
        command.Parameters.AddWithValue("$id", marker.Id.ToString()); command.Parameters.AddWithValue("$session", marker.SessionId.ToString()); command.Parameters.AddWithValue("$time", marker.TimestampUtc.ToString("O")); command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(marker));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveAlertAsync(SessionAlert alert, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken); var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO alerts(id,session_id,timestamp_utc,payload_json) VALUES($id,$session,$time,$json)";
        command.Parameters.AddWithValue("$id", alert.Id.ToString()); command.Parameters.AddWithValue("$session", alert.SessionId.ToString()); command.Parameters.AddWithValue("$time", alert.TimestampUtc.ToString("O")); command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(alert));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveEventAsync(SessionEvent sessionEvent, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken); var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO events(id,session_id,timestamp_utc,payload_json) VALUES($id,$session,$time,$json)";
        command.Parameters.AddWithValue("$id", sessionEvent.Id.ToString()); command.Parameters.AddWithValue("$session", sessionEvent.SessionId.ToString()); command.Parameters.AddWithValue("$time", sessionEvent.TimestampUtc.ToString("O")); command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(sessionEvent));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MonitoringSession>> ListSessionsAsync(CancellationToken cancellationToken)
    {
        var result = new List<MonitoringSession>(); await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken); var command = connection.CreateCommand();
        command.CommandText = "SELECT id,started_utc,ended_utc,device_json,package,paused,active_ms FROM sessions ORDER BY started_utc DESC LIMIT 200";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var device = JsonSerializer.Deserialize<AndroidDevice>(reader.GetString(3)); if (device is null) continue;
            result.Add(new MonitoringSession { Id = Guid.Parse(reader.GetString(0)), StartedUtc = DateTimeOffset.Parse(reader.GetString(1)), EndedUtc = reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2)), Device = device, CurrentPackage = reader.IsDBNull(4) ? null : reader.GetString(4), IsPaused = reader.GetBoolean(5), ActiveCollectionTime = TimeSpan.FromMilliseconds(reader.GetInt64(6)) });
        }
        return result;
    }

    public async Task<IReadOnlyList<SessionSummary>> ListSessionSummariesAsync(CancellationToken cancellationToken)
    {
        var result = new List<SessionSummary>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id,s.started_utc,s.ended_utc,s.device_json,s.package,s.active_ms,
                   COALESCE((SELECT COUNT(*) FROM alerts a WHERE a.session_id=s.id),0),
                   COALESCE((SELECT COUNT(*) FROM markers m WHERE m.session_id=s.id),0),
                   ss.stats_json,ss.export_path
            FROM sessions s
            LEFT JOIN session_summaries ss ON ss.session_id=s.id
            ORDER BY s.started_utc DESC
            LIMIT 200
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var device = JsonSerializer.Deserialize<AndroidDevice>(reader.GetString(3));
            if (device is null) continue;
            var stats = reader.IsDBNull(8) ? new SessionStats(null, null, null, null) : JsonSerializer.Deserialize<SessionStats>(reader.GetString(8)) ?? new(null, null, null, null);
            var export = reader.IsDBNull(9) ? "Not exported" : $"Exported · {Path.GetFileName(reader.GetString(9))}";
            result.Add(new(
                Guid.Parse(reader.GetString(0)),
                DateTimeOffset.Parse(reader.GetString(1)),
                reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2)),
                device,
                reader.IsDBNull(4) ? null : reader.GetString(4),
                TimeSpan.FromMilliseconds(reader.GetInt64(5)),
                reader.GetInt32(6),
                reader.GetInt32(7),
                stats.AverageFps,
                stats.FrameTimeP95Ms,
                stats.PeakMemoryBytes,
                stats.PeakCpuPercent,
                export));
        }
        return result;
    }

    public async Task<MonitoringSession?> LoadSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id,started_utc,ended_utc,device_json,package,paused,active_ms FROM sessions WHERE id=$id";
        command.Parameters.AddWithValue("$id", sessionId.ToString());
        MonitoringSession? session;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return null;
            var device = JsonSerializer.Deserialize<AndroidDevice>(reader.GetString(3));
            if (device is null) return null;
            session = new()
            {
                Id = Guid.Parse(reader.GetString(0)),
                StartedUtc = DateTimeOffset.Parse(reader.GetString(1)),
                EndedUtc = reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2)),
                Device = device,
                CurrentPackage = reader.IsDBNull(4) ? null : reader.GetString(4),
                IsPaused = reader.GetBoolean(5),
                ActiveCollectionTime = TimeSpan.FromMilliseconds(reader.GetInt64(6))
            };
        }
        foreach (var sample in await LoadPayloadsAsync<MetricSample>(connection, "samples", sessionId, cancellationToken)) session.Samples.Add(sample);
        foreach (var marker in await LoadPayloadsAsync<SessionMarker>(connection, "markers", sessionId, cancellationToken)) session.Markers.Add(marker);
        foreach (var alert in await LoadPayloadsAsync<SessionAlert>(connection, "alerts", sessionId, cancellationToken)) session.Alerts.Add(alert);
        foreach (var sessionEvent in await LoadPayloadsAsync<SessionEvent>(connection, "events", sessionId, cancellationToken)) session.Events.Add(sessionEvent);
        return session;
    }

    public async Task MarkSessionExportedAsync(Guid sessionId, string path, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO session_summaries(session_id,stats_json,export_path) VALUES($id,$stats,$path) ON CONFLICT(session_id) DO UPDATE SET export_path=$path";
        command.Parameters.AddWithValue("$id", sessionId.ToString());
        command.Parameters.AddWithValue("$stats", JsonSerializer.Serialize(new SessionStats(null, null, null, null)));
        command.Parameters.AddWithValue("$path", path);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var table in new[] { "samples", "markers", "alerts", "events", "session_summaries", "sessions" })
        {
            var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = $"DELETE FROM {table} WHERE {(table == "sessions" ? "id" : "session_id")}=$id";
            command.Parameters.AddWithValue("$id", sessionId.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SaveSettingAsync<T>(string key, T value, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO settings(key,value_json) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value_json=$value";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(value));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<T?> LoadSettingAsync<T>(string key, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT value_json FROM settings WHERE key=$key";
        command.Parameters.AddWithValue("$key", key);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string json ? JsonSerializer.Deserialize<T>(json) : default;
    }

    private static async Task UpsertPayloadAsync<T>(SqliteConnection connection, System.Data.Common.DbTransaction transaction, string table, Guid id, Guid sessionId, DateTimeOffset timestampUtc, T payload, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = $"INSERT OR REPLACE INTO {table}(id,session_id,timestamp_utc,payload_json) VALUES($id,$session,$time,$json)";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$session", sessionId.ToString());
        command.Parameters.AddWithValue("$time", timestampUtc.ToString("O"));
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(payload));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<T>> LoadPayloadsAsync<T>(SqliteConnection connection, string table, Guid sessionId, CancellationToken cancellationToken)
    {
        var result = new List<T>();
        var command = connection.CreateCommand();
        command.CommandText = $"SELECT payload_json FROM {table} WHERE session_id=$id ORDER BY timestamp_utc";
        command.Parameters.AddWithValue("$id", sessionId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var value = JsonSerializer.Deserialize<T>(reader.GetString(0));
            if (value is not null) result.Add(value);
        }
        return result;
    }

    private static double? Percentile(double[] sortedValues, double percentile)
    {
        if (sortedValues.Length == 0) return null;
        var rank = Math.Clamp(percentile, 0, 1) * (sortedValues.Length - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper) return sortedValues[lower];
        return sortedValues[lower] + (sortedValues[upper] - sortedValues[lower]) * (rank - lower);
    }

    private sealed record SessionStats(double? AverageFps, double? FrameTimeP95Ms, long? PeakMemoryBytes, double? PeakCpuPercent);
}
