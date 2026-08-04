namespace AndroidDevMonitor.Core.Configuration;

public static class MonitoringConstants
{
    public static readonly TimeSpan LightweightInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan ProcessInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan HeavyInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DiscoveryInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan SummaryWindow = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan LiveChartWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan DefaultAdbTimeout = TimeSpan.FromSeconds(12);
    public const int RecentLogLimit = 500;
    public const int SampleQueueCapacity = 4096;
    public const int LogQueueCapacity = 5000;
    public const int InMemorySessionSampleLimit = 3600;
}
