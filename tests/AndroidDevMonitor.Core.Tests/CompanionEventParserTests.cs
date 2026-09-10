using AndroidDevMonitor.Core.Analysis;

namespace AndroidDevMonitor.Core.Tests;

public sealed class CompanionEventParserTests
{
    [Fact]
    public void Parser_reads_marker_inside_normal_logcat_line()
    {
        const string line = "09-10 12:31:02.115  4211  4211 I AdmCompanion: ADM_COMPANION|duration|feed load|412|ms|first page";

        bool parsedOk = CompanionEventParser.TryParse(line, "com.example.app", "emulator-5554", out CompanionEvent? parsed);

        Assert.True(parsedOk);
        Assert.NotNull(parsed);
        Assert.Equal("duration", parsed!.Kind);
        Assert.Equal("feed load", parsed.Name);
        Assert.Equal(412d, parsed.Value.GetValueOrDefault());
        Assert.Equal("ms", parsed.Unit);
        Assert.Equal("first page", parsed.Payload);
        Assert.Equal("emulator-5554", parsed.DeviceSerial);
    }

    [Fact]
    public void Parser_ignores_lines_without_marker()
    {
        bool parsedOk = CompanionEventParser.TryParse("09-10 12:31:02.115  4211  4211 I Choreographer: Skipped 31 frames", "app", "serial", out CompanionEvent? parsed);

        Assert.False(parsedOk);
        Assert.Null(parsed);
    }

    [Fact]
    public void Parser_keeps_non_numeric_value_as_payload()
    {
        bool parsedOk = CompanionEventParser.TryParse("ADM_COMPANION|workmanager|sync|skipped||constraint unmet", "app", "serial", out CompanionEvent? parsed);

        Assert.True(parsedOk);
        Assert.NotNull(parsed);
        Assert.Null(parsed!.Value);
        Assert.Contains("skipped", parsed.Payload);
        Assert.Contains("constraint unmet", parsed.Payload);
    }

    [Fact]
    public void Report_groups_events_by_kind_and_reports_statistics()
    {
        List<CompanionEvent> events =
        [
            new(DateTimeOffset.UtcNow, "mark", "app start", null, null, "app", "serial", "", "raw"),
            new(DateTimeOffset.UtcNow, "duration", "feed load", 100, "ms", "app", "serial", "", "raw"),
            new(DateTimeOffset.UtcNow, "duration", "feed load", 300, "ms", "app", "serial", "", "raw")
        ];

        string report = CompanionEventParser.BuildReport("app", "serial", events, 42);

        Assert.Contains("Markers found: 3", report);
        Assert.Contains("duration", report);
        Assert.Contains("avg 200", report);
        Assert.Contains("feed load", report);
    }

    [Fact]
    public void Report_explains_when_no_markers_were_found()
    {
        string report = CompanionEventParser.BuildReport("app", "serial", [], 10);

        Assert.Contains("No ADM_COMPANION markers were found", report);
    }
}
