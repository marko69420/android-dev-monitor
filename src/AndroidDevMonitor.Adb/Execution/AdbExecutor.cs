using System.Collections.Concurrent;
using System.Diagnostics;
using AndroidDevMonitor.Core.Models;
using AndroidDevMonitor.Core.Services;
using Microsoft.Extensions.Logging;

namespace AndroidDevMonitor.Adb.Execution;

public sealed class AdbExecutor : IAdbExecutor, IDisposable
{
    private readonly ILogger<AdbExecutor> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _deviceLocks = new();
    public AdbExecutor(ILogger<AdbExecutor> logger, string? configuredPath = null)
    {
        _logger = logger;
        ResolvedAdbPath = ResolvePath(configuredPath);
    }

    public string? ResolvedAdbPath { get; private set; }
    public void ConfigurePath(string? configuredPath) => ResolvedAdbPath = ResolvePath(configuredPath);

    public async Task<AdbCommandResult> ExecuteAsync(string? serial, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var adbPath = ResolvedAdbPath;
        if (adbPath is null)
            return new(arguments, serial, started, DateTimeOffset.UtcNow, null, "", "ADB executable was not found. Select adb.exe in Settings or install Android Platform Tools.", false, false);

        var command = serial is null ? arguments.ToArray() : new[] { "-s", serial }.Concat(arguments).ToArray();
        var gate = _deviceLocks.GetOrAdd(serial ?? "__server__", _ => new SemaphoreSlim(2, 2));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = adbPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                }
            };
            foreach (var argument in command) process.StartInfo.ArgumentList.Add(argument);
            _logger.LogDebug("ADB {Serial} {Arguments}", serial, string.Join(' ', command));
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(linked.Token);
            var stderr = process.StandardError.ReadToEndAsync(linked.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                return new(command, serial, started, DateTimeOffset.UtcNow, process.ExitCode, await stdout, await stderr, false, false);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
                var cancelled = cancellationToken.IsCancellationRequested;
                return new(command, serial, started, DateTimeOffset.UtcNow, null, await Safe(stdout), await Safe(stderr), !cancelled, cancelled);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ADB execution failed for {Serial}", serial);
            return new(command, serial, started, DateTimeOffset.UtcNow, null, "", ex.Message, false, false);
        }
        finally { gate.Release(); }
    }

    public static string? ResolvePath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath)) return Path.GetFullPath(configuredPath);
        foreach (var variable in new[] { "ANDROID_SDK_ROOT", "ANDROID_HOME" })
        {
            var root = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(root))
            {
                var candidate = Path.Combine(root, "platform-tools", "adb.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => Path.Combine(part.Trim(), "adb.exe")).FirstOrDefault(File.Exists);
    }

    private static async Task<string> Safe(Task<string> task) { try { return await task; } catch { return ""; } }
    public void Dispose() { foreach (var gate in _deviceLocks.Values) gate.Dispose(); }
}
