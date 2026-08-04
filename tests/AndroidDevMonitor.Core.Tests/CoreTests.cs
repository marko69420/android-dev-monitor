using System.Text.Json;
using AndroidDevMonitor.Core.Collections;
using AndroidDevMonitor.Core.Formatting;
using AndroidDevMonitor.Core.Models;

namespace AndroidDevMonitor.Core.Tests;

public sealed class CoreTests
{
    [Fact]
    public void Time_series_window_is_bounded()
    {
        var buffer = new TimeSeriesBuffer<MetricSample>(TimeSpan.FromSeconds(60), x => x.TimestampUtc); var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        buffer.Add(new(id, "a", null, now.AddSeconds(-61))); buffer.Add(new(id, "a", null, now)); Assert.Single(buffer.Snapshot());
    }
    [Fact]
    public void Session_serializes_device_samples_markers_and_alerts()
    {
        var session = new MonitoringSession { StartedUtc = DateTimeOffset.UtcNow, Device = new("emulator-5554", "Phone", DeviceState.Connected, DeviceKind.Emulator), CurrentPackage = "com.example.game" };
        session.Samples.Add(new(session.Id, session.Device.Serial, session.CurrentPackage, DateTimeOffset.UtcNow, DeviceCpuPercent: 50)); session.Markers.Add(new(Guid.NewGuid(), session.Id, DateTimeOffset.UtcNow, DateTimeOffset.Now, TimeSpan.FromSeconds(3), "Boss fight", null, session.Device.Serial, session.Device.FriendlyName, session.CurrentPackage)); session.Alerts.Add(new(Guid.NewGuid(), session.Id, DateTimeOffset.UtcNow, "FPS", "Warning", "FPS below 30", false));
        var json = JsonSerializer.Serialize(session); Assert.Contains("Boss fight", json); Assert.Contains("FPS below 30", json); Assert.Contains("emulator-5554", json);
    }
    [Fact]
    public void Automation_sequence_preserves_destructive_flags()
    {
        var sequence = new AutomationSequence(Guid.NewGuid(), "Smoke", [new(Guid.NewGuid(), AutomationStepKind.ClearData, "Clear data", "com.example", null, true)], 1, true); var clone = JsonSerializer.Deserialize<AutomationSequence>(JsonSerializer.Serialize(sequence)); Assert.True(clone!.Steps[0].IsDestructive);
    }
    [Theory]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1048576, "1.0 MB")]
    public void Unit_formatting(double value, string expected) => Assert.Equal(expected, UnitFormatter.Bytes(value));
    [Fact] public void Unsupported_values_are_explicit() { var value = MetricValue.Missing("%", Availability.Unsupported); Assert.Null(value.Value); Assert.Equal(Availability.Unsupported, value.Availability); }
}
