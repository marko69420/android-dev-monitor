using AndroidDevMonitor.Adb.Parsers;
using AndroidDevMonitor.Core.Models;

namespace AndroidDevMonitor.Adb.Tests;

public sealed class AndroidParserTests
{
    [Fact]
    public void Devices_parses_connected_offline_and_unauthorized()
    {
        var result = AndroidParsers.ParseDevices("List of devices attached\nemulator-5554 device product:sdk model:Small_Phone transport_id:1\nABC offline model:Pixel_6\nXYZ unauthorized usb:1-2\n");
        Assert.Equal(3, result.Count); Assert.Equal(DeviceKind.Emulator, result[0].Kind); Assert.Equal("Small Phone", result[0].FriendlyName); Assert.Equal(DeviceState.Offline, result[1].State); Assert.Equal(DeviceState.Unauthorized, result[2].State);
    }

    [Fact]
    public void Getprop_is_tolerant_of_empty_values()
    {
        var values = AndroidParsers.ParseGetProp("[ro.product.model]: [Pixel 6]\n[ro.empty]: []\nnoise");
        Assert.Equal("Pixel 6", values["ro.product.model"]); Assert.Equal("", values["ro.empty"]);
    }

    [Theory]
    [InlineData("Physical size: 1080x2400", "1080x2400")]
    [InlineData("Physical size: 1080x2400\nOverride size: 720x1600", "720x1600")]
    public void Wm_size_parses(string text, string expected) => Assert.Equal(expected, AndroidParsers.ParseWmSize(text));

    [Fact] public void Wm_density_prefers_override() => Assert.Equal("320", AndroidParsers.ParseWmDensity("Physical density: 420\nOverride density: 320"));

    [Fact]
    public void Ps_detects_columns_and_packages()
    {
        const string ps = "USER PID PPID NAME ARGS\nu0_a124 13432 901 com.company.game com.company.game\nroot 1 0 init /system/bin/init\n";
        var rows = AndroidParsers.ParseProcesses(ps); Assert.Equal(2, rows.Count); Assert.Equal("com.company.game", rows[0].PackageName); Assert.Equal(ProcessGroup.Apps, rows[0].Group); Assert.Equal(ProcessGroup.System, rows[1].Group);
    }

    [Fact]
    public void Ps_parses_rss_without_assuming_fixed_spacing()
    {
        const string ps = "USER PID PPID S RSS NAME ARGS\nu0_a124 13432 901 R 524288 com.company.game com.company.game\n";
        var row = Assert.Single(AndroidParsers.ParseProcesses(ps)); Assert.Equal(524_288L * 1024, row.RssBytes); Assert.Equal("Foreground", row.Status);
    }
    [Fact]
    public void Ps_parses_cpu_percentage()
    {
        const string ps = "USER PID PPID S RSS %CPU NAME ARGS\nshell 421 1 R 9556 36.4 adbd adbd --root\n";
        Assert.Equal(36.4, Assert.Single(AndroidParsers.ParseProcesses(ps)).CpuPercent);
    }

    [Fact] public void Proc_stat_parses_aggregate() { var s = AndroidParsers.ParseProcStat("cpu  100 2 30 400 5 1 2 0\ncpu0 1 1 1 1"); Assert.NotNull(s); Assert.Equal(540, s.Total); Assert.Equal(135, s.Active); }

    [Fact]
    public void Process_status_parses_uid()
    {
        Assert.Equal(10123, AndroidParsers.ParseProcessUid("Name:\tapp\nUid:\t10123\t10123\t10123\t10123\nVmRSS:\t2048 kB"));
    }

    [Fact]
    public void Uid_network_counters_use_last_two_numeric_lines()
    {
        var counters = AndroidParsers.ParseUidNetworkCounters("MemTotal: 100 kB\n12345\n67890\n");
        Assert.NotNull(counters);
        Assert.Equal(12345, counters.Value.Rx);
        Assert.Equal(67890, counters.Value.Tx);
    }
    [Fact]
    public void Multiple_process_stats_are_parsed_by_pid()
    {
        const string stats = "42 (game) R 1 1 1 1 1 1 1 1 1 1 30 20 1 1 1 1 1 1 1000 1 1\n7 (worker thread) S 1 1 1 1 1 1 1 1 1 1 4 3 1 1 1 1 1 1 900 1 1\n";
        var parsed = AndroidParsers.ParseProcessStats(stats);
        Assert.Equal(2, parsed.Count); Assert.Equal(50, parsed[42].TotalTicks); Assert.Equal(900, parsed[7].StartTicks);
    }

    [Fact]
    public void Process_stat_handles_spaces_inside_name()
    {
        var fields = string.Join(' ', new[] { "S", "1", "1", "1", "0", "0", "0", "0", "0", "0", "0", "25", "10", "0", "0", "0", "0", "0", "0", "777" });
        var s = AndroidParsers.ParseProcessStat($"42 (game worker) {fields}"); Assert.NotNull(s); Assert.Equal("game worker", s.Name); Assert.Equal(35, s.TotalTicks); Assert.Equal(777, s.StartTicks);
    }

    [Fact] public void Meminfo_uses_memavailable() { var m = AndroidParsers.ParseMemInfo("MemTotal: 4000000 kB\nMemFree: 100000 kB\nMemAvailable: 1200000 kB\nBuffers: 1 kB\nCached: 2 kB"); Assert.NotNull(m); Assert.Equal(1_200_000, m.AvailableKb); }
    [Fact] public void Process_status_parses_rss() => Assert.Equal(12_345L * 1024, AndroidParsers.ParseProcessRss("Name: app\nVmRSS: 12345 kB"));
    [Fact] public void Dumpsys_meminfo_parses_total_pss() => Assert.Equal(456_789L * 1024, AndroidParsers.ParseDumpsysPss(" TOTAL 456789 10 20"));
    [Fact] public void Process_io_parses_logical_bytes() { var io = AndroidParsers.ParseIo("read_bytes: 100\nwrite_bytes: 250\n"); Assert.Equal(100, io!.ReadBytes); Assert.Equal(250, io.WriteBytes); }

    [Fact]
    public void Gfxinfo_calculates_frames_without_fabricating_sixty()
    {
        const string data = "---PROFILEDATA---\nFlags,IntendedVsync,Vsync,OldestInputEvent,NewestInputEvent,HandleInputStart,AnimationStart,PerformTraversalsStart,DrawStart,SyncQueued,SyncStart,IssueDrawCommandsStart,SwapBuffers,FrameCompleted\n0,1000000000,0,0,0,0,0,0,0,0,0,0,0,1010000000\n0,2000000000,0,0,0,0,0,0,0,0,0,0,0,2040000000\n---PROFILEDATA---";
        var stats = AndroidParsers.ParseFrameStats(data); Assert.NotNull(stats); Assert.Equal(2, stats.FrameTimesMs.Count); Assert.Equal(40, stats.Fps); Assert.Equal(50, stats.JankPercent);
    }

    [Fact] public void Battery_parser_keeps_vendor_fields() { var b = AndroidParsers.ParseBattery("level: 82\ntemperature: 351\nstatus: 2"); Assert.Equal("351", b["temperature"]); }

    [Fact]
    public void Df_parser_returns_capacity_bytes()
    {
        var storage = AndroidParsers.ParseDf("Filesystem 1K-blocks Used Available Use% Mounted on\n/dev/block/dm-8 60818424 16532336 44286088 28% /data");
        Assert.NotNull(storage);
        Assert.Equal(60_818_424L * 1024, storage.TotalBytes);
        Assert.Equal(44_286_088L * 1024, storage.AvailableBytes);
        Assert.Equal(28, storage.UsedPercent);
    }

    [Theory]
    [InlineData("Thermal Status: 0", "None")]
    [InlineData("Thermal Status: 3", "Severe")]
    [InlineData("Thermal Status: 6", "Shutdown")]
    public void Thermal_severity_is_mapped(string text, string expected) => Assert.Equal(expected, AndroidParsers.ParseThermalSeverity(text));
}
