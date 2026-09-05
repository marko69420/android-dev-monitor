using System.Diagnostics;
using System.Runtime.InteropServices;
using AndroidDevMonitor.Core.Models;
using AndroidDevMonitor.Core.Services;

namespace AndroidDevMonitor.Infrastructure.Gpu;

public sealed class EmulatorGpuMetricProvider : IGpuMetricProvider, IDisposable
{
    private readonly object _gate = new();
    private IntPtr _query;
    private IntPtr _counter;
    private int? _pid;
    private DateTime _processStarted;
    private bool _disposed;
    public string Name => "Windows GPU Engine · emulator process";

    public async Task<bool> IsSupportedAsync(AndroidDevice device, CancellationToken cancellationToken) =>
        (await ReadAsync(device, null, cancellationToken)).Availability is Availability.Available or Availability.Waiting;

    public Task<MetricValue> ReadAsync(AndroidDevice device, string? packageName, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_disposed || !OperatingSystem.IsWindows() || !device.Serial.StartsWith("emulator-", StringComparison.Ordinal) ||
                !int.TryParse(device.Serial[9..], out var port) || port is < 1 or > 65535)
                return Missing(Availability.Unsupported, "No GPU provider for this target");
            try
            {
                var pid = FindOwner(port);
                if (pid is null) { Reset(); return Missing(Availability.Unsupported, "Local emulator process not found"); }
                using var process = Process.GetProcessById(pid.Value);
                if (!process.ProcessName.StartsWith("qemu-system-", StringComparison.OrdinalIgnoreCase))
                    return Missing(Availability.Unsupported, "Console port does not belong to an Android emulator");
                var started = process.StartTime;
                var source = $"Emulator GPU · PID {pid} · busiest engine · all Android apps";
                if (_query == IntPtr.Zero || _pid != pid || _processStarted != started)
                {
                    Reset();
                    var status = PdhOpenQueryW(null, UIntPtr.Zero, out _query);
                    if (status == 0) status = PdhAddEnglishCounterW(_query, $@"\GPU Engine(pid_{pid}_*)\Utilization Percentage", UIntPtr.Zero, out _counter);
                    if (status == 0) status = PdhCollectQueryData(_query);
                    if (status != 0) { Reset(); return Missing(Availability.Unsupported, $"Windows GPU counters unavailable (0x{status:X8})"); }
                    _pid = pid; _processStarted = started;
                    return Missing(Availability.Waiting, source + " · warming up");
                }
                var collectStatus = PdhCollectQueryData(_query);
                if (collectStatus != 0) { Reset(); return Missing(Availability.Error, $"GPU counter read failed (0x{collectStatus:X8})"); }
                uint bytes = 0;
                var result = PdhGetFormattedCounterArrayW(_counter, 0x200, ref bytes, out _, IntPtr.Zero);
                if (result != 0x800007D2 || bytes == 0)
                    return Missing(Availability.Waiting, source + " · no engine samples");
                var buffer = Marshal.AllocHGlobal(checked((int)bytes));
                try
                {
                    result = PdhGetFormattedCounterArrayW(_counter, 0x200, ref bytes, out var count, buffer);
                    if (result != 0) return Missing(Availability.Error, $"GPU counter formatting failed (0x{result:X8})");
                    double? busiest = null;
                    var stride = Marshal.SizeOf<CounterItem>();
                    for (var i = 0; i < count; i++)
                    {
                        var item = Marshal.PtrToStructure<CounterItem>(IntPtr.Add(buffer, i * stride));
                        var name = Marshal.PtrToStringUni(item.Name);
                        if (name?.StartsWith($"pid_{pid}_", StringComparison.OrdinalIgnoreCase) != true || item.Value.Status > 1 || !double.IsFinite(item.Value.Value)) continue;
                        busiest = Math.Max(busiest ?? 0, Math.Clamp(item.Value.Value, 0, 100));
                    }
                    return busiest is null ? Missing(Availability.Waiting, source + " · no valid engine samples") :
                        new MetricValue(busiest, "%", Availability.Available, source, DateTimeOffset.UtcNow);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
            {
                Reset();
                return Missing(Availability.Error, "Emulator GPU unavailable: " + ex.Message);
            }
        }
    }, cancellationToken);

    private static MetricValue Missing(Availability availability, string source) => MetricValue.Missing("%", availability, source);

    private static int? FindOwner(int port)
    {
        // IPv4 and IPv6 listener tables map the emulator console port to its exact host PID.
        foreach (var family in new[] { 2, 23 })
        {
            var size = 0;
            if (GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0) != 122 || size <= 0) continue;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buffer, ref size, false, family, 3, 0) != 0) continue;
                var count = Marshal.ReadInt32(buffer);
                var stride = family == 2 ? 24 : 56;
                var portOffset = family == 2 ? 8 : 20;
                var pidOffset = family == 2 ? 20 : 52;
                for (var i = 0; i < count; i++)
                {
                    var row = IntPtr.Add(buffer, 4 + i * stride);
                    var actualPort = (Marshal.ReadByte(row, portOffset) << 8) | Marshal.ReadByte(row, portOffset + 1);
                    if (actualPort == port) return Marshal.ReadInt32(row, pidOffset);
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return null;
    }

    private void Reset()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = _counter = IntPtr.Zero;
        _pid = null;
    }
    public void Dispose() { lock (_gate) { _disposed = true; Reset(); } }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct CounterValue { [FieldOffset(0)] public uint Status; [FieldOffset(8)] public double Value; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem { public IntPtr Name; public CounterValue Value; }
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQueryW(string? dataSource, UIntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, UIntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bytes, out uint count, IntPtr buffer);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);
}
