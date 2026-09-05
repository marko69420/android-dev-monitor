using AndroidDevMonitor.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AndroidDevMonitor.App.ViewModels;

public sealed partial class ProcessDisplayRow : ObservableObject
{
    public ProcessDisplayRow(
        string Key,
        ProcessGroup Group,
        string Name,
        string PackageOrCommand,
        int? Pid,
        string Status,
        double? CpuPercent,
        long? RssBytes,
        double? GpuPercent,
        double? DiskReadBytesPerSecond,
        double? DiskWriteBytesPerSecond,
        double? NetworkRxBytesPerSecond,
        double? NetworkTxBytesPerSecond,
        double? Fps,
        string BatteryImpact,
        string ThermalRelation,
        bool CanExpand,
        bool IsExpanded,
        bool IsChild,
        string? PackageName,
        string? ParentKey = null)
    {
        this.Key = Key;
        this.ParentKey = ParentKey;
        _group = Group;
        _name = Name;
        _packageOrCommand = PackageOrCommand;
        _pid = Pid;
        _status = Status;
        _cpuPercent = CpuPercent;
        _rssBytes = RssBytes;
        _gpuPercent = GpuPercent;
        _diskReadBytesPerSecond = DiskReadBytesPerSecond;
        _diskWriteBytesPerSecond = DiskWriteBytesPerSecond;
        _networkRxBytesPerSecond = NetworkRxBytesPerSecond;
        _networkTxBytesPerSecond = NetworkTxBytesPerSecond;
        _fps = Fps;
        _batteryImpact = BatteryImpact;
        _thermalRelation = ThermalRelation;
        _canExpand = CanExpand;
        _isExpanded = IsExpanded;
        _isChild = IsChild;
        _packageName = PackageName;
    }

    public string Key { get; }
    public string? ParentKey { get; }
    [ObservableProperty] private ProcessGroup _group;
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _packageOrCommand;
    [ObservableProperty] private int? _pid;
    [ObservableProperty] private string _status;
    [ObservableProperty] private double? _cpuPercent;
    [ObservableProperty] private long? _rssBytes;
    [ObservableProperty] private double? _gpuPercent;
    [ObservableProperty] private double? _diskReadBytesPerSecond;
    [ObservableProperty] private double? _diskWriteBytesPerSecond;
    [ObservableProperty] private double? _networkRxBytesPerSecond;
    [ObservableProperty] private double? _networkTxBytesPerSecond;
    [ObservableProperty] private double? _fps;
    [ObservableProperty] private string _batteryImpact;
    [ObservableProperty] private string _thermalRelation;
    [ObservableProperty] private bool _canExpand;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isChild;
    [ObservableProperty] private string? _packageName;

    public string ExpanderGlyph => CanExpand ? IsExpanded ? "▾" : "▸" : "";
    public string DisplayName => IsChild ? $"    {Name}" : Name;
    public string GroupText => IsChild ? "" : Group switch
    {
        ProcessGroup.Apps => "Apps",
        ProcessGroup.Background => "Background",
        _ => "System"
    };

    public void UpdateFrom(ProcessDisplayRow value)
    {
        if (!string.Equals(Key, value.Key, StringComparison.Ordinal))
            throw new InvalidOperationException("A process display row cannot change identity.");
        Group = value.Group;
        Name = value.Name;
        PackageOrCommand = value.PackageOrCommand;
        Pid = value.Pid;
        Status = value.Status;
        CpuPercent = value.CpuPercent;
        RssBytes = value.RssBytes;
        GpuPercent = value.GpuPercent;
        DiskReadBytesPerSecond = value.DiskReadBytesPerSecond;
        DiskWriteBytesPerSecond = value.DiskWriteBytesPerSecond;
        NetworkRxBytesPerSecond = value.NetworkRxBytesPerSecond;
        NetworkTxBytesPerSecond = value.NetworkTxBytesPerSecond;
        Fps = value.Fps;
        BatteryImpact = value.BatteryImpact;
        ThermalRelation = value.ThermalRelation;
        CanExpand = value.CanExpand;
        IsExpanded = value.IsExpanded;
        IsChild = value.IsChild;
        PackageName = value.PackageName;
    }

    partial void OnGroupChanged(ProcessGroup value) => OnPropertyChanged(nameof(GroupText));
    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(DisplayName));
    partial void OnCanExpandChanged(bool value) => OnPropertyChanged(nameof(ExpanderGlyph));
    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ExpanderGlyph));
    partial void OnIsChildChanged(bool value)
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(GroupText));
    }
}
