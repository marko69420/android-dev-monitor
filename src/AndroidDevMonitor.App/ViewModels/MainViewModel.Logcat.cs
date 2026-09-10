#nullable enable
using System.Collections.Generic;
using System.Collections.ObjectModel;
using AndroidDevMonitor.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AndroidDevMonitor.App.ViewModels;

public sealed record SavedLogFilter(string Name, string SearchText, bool Regex, string Priority, string Source, bool CrashOnly, bool BookmarksOnly);

public partial class MainViewModel
{
    [ObservableProperty] private bool _logCrashOnly;
    [ObservableProperty] private bool _logBookmarksOnly;
    [ObservableProperty] private string _newLogFilterName = "";
    [ObservableProperty] private SavedLogFilter? _selectedSavedLogFilter;
    [ObservableProperty] private DateTimeOffset? _logWindowFrom;
    [ObservableProperty] private DateTimeOffset? _logWindowTo;

    private readonly HashSet<LogEntry> _bookmarkedLogs = new(ReferenceEqualityComparer.Instance);

    public ObservableCollection<SavedLogFilter> SavedLogFilters { get; } = [];

    public int BookmarkedLogCount => _bookmarkedLogs.Count;

    public string LogWindowSummary => LogWindowFrom is DateTimeOffset from && LogWindowTo is DateTimeOffset to
        ? $"Showing {from.ToLocalTime():HH:mm:ss.fff} – {to.ToLocalTime():HH:mm:ss.fff}"
        : "Time window: all buffered logs";

    /// <summary>Installs the Logcat 2.0 filter: crash/ANR quick view, bookmarks and a time window on top of the existing filter.</summary>
    public void EnableExtendedLogFilters() => LogView.Filter = FilterLogIncludingExtensions;

    private bool FilterLogIncludingExtensions(object item)
    {
        if (!FilterLog(item)) return false;
        return item is not LogEntry entry || PassesExtendedLogFilters(entry);
    }

    private bool PassesExtendedLogFilters(LogEntry entry)
    {
        if (LogCrashOnly && !IsCrashOrAnrLog(entry)) return false;
        if (LogBookmarksOnly && !_bookmarkedLogs.Contains(entry)) return false;
        if (LogWindowFrom is DateTimeOffset from && entry.TimestampUtc < from) return false;
        if (LogWindowTo is DateTimeOffset to && entry.TimestampUtc > to) return false;
        return true;
    }

    private static bool IsCrashOrAnrLog(LogEntry entry)
    {
        string priority = entry.Priority ?? "";
        if (priority.Equals("F", StringComparison.OrdinalIgnoreCase) ||
            priority.Equals("FATAL", StringComparison.OrdinalIgnoreCase) ||
            priority.Equals("A", StringComparison.OrdinalIgnoreCase) ||
            priority.Equals("ASSERT", StringComparison.OrdinalIgnoreCase)) return true;
        string message = entry.Message ?? "";
        return message.Contains("FATAL EXCEPTION", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("ANR in ", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("am_anr", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Fatal signal", StringComparison.OrdinalIgnoreCase);
    }

    partial void OnLogCrashOnlyChanged(bool value) => RefreshLogFilter();

    partial void OnLogBookmarksOnlyChanged(bool value) => RefreshLogFilter();

    partial void OnLogWindowFromChanged(DateTimeOffset? value)
    {
        OnPropertyChanged(nameof(LogWindowSummary));
        RefreshLogFilter();
    }

    partial void OnLogWindowToChanged(DateTimeOffset? value)
    {
        OnPropertyChanged(nameof(LogWindowSummary));
        RefreshLogFilter();
    }

    [RelayCommand]
    private void ToggleLogBookmark()
    {
        if (SelectedLogEntry is not LogEntry entry)
        {
            LogFilterStatus = "Select a log row first, then press Bookmark.";
            return;
        }

        bool added = _bookmarkedLogs.Add(entry);
        if (!added) _bookmarkedLogs.Remove(entry);
        OnPropertyChanged(nameof(BookmarkedLogCount));
        RefreshLogFilter();
        LogFilterStatus = added ? $"Bookmarked · {BookmarkedLogCount} saved" : $"Bookmark removed · {BookmarkedLogCount} saved";
    }

    [RelayCommand]
    private void ClearLogBookmarks()
    {
        _bookmarkedLogs.Clear();
        OnPropertyChanged(nameof(BookmarkedLogCount));
        RefreshLogFilter();
        LogFilterStatus = "Log bookmarks cleared";
    }

    [RelayCommand]
    private void ShowWindowBeforeSelected()
    {
        if (SelectedLogEntry is not LogEntry entry)
        {
            LogFilterStatus = "Select the crash or spike row first.";
            return;
        }

        LogWindowFrom = entry.TimestampUtc - TimeSpan.FromSeconds(10);
        LogWindowTo = entry.TimestampUtc;
        LogFilterStatus = $"Showing the 10 seconds before {entry.LocalTimestamp:HH:mm:ss.fff}";
    }

    [RelayCommand]
    private void ClearLogWindow()
    {
        LogWindowFrom = null;
        LogWindowTo = null;
        LogFilterStatus = "Time window cleared";
    }

    [RelayCommand]
    private async Task SaveLogFilterAsync()
    {
        string name = NewLogFilterName.Trim();
        if (name.Length == 0)
        {
            LogFilterStatus = "Type a name for the filter first.";
            return;
        }

        SavedLogFilter? existing = SavedLogFilters.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) SavedLogFilters.Remove(existing);
        SavedLogFilter filter = new(name, LogSearchText, LogRegexEnabled, SelectedLogPriority, SelectedLogSource, LogCrashOnly, LogBookmarksOnly);
        SavedLogFilters.Add(filter);
        SelectedSavedLogFilter = filter;
        NewLogFilterName = "";
        await PersistSavedLogFiltersAsync();
        LogFilterStatus = $"Saved filter '{name}'";
    }

    [RelayCommand]
    private void ApplySavedLogFilter()
    {
        SavedLogFilter? filter = SelectedSavedLogFilter;
        if (filter is null)
        {
            LogFilterStatus = "Choose a saved filter first.";
            return;
        }

        LogSearchText = filter.SearchText;
        LogRegexEnabled = filter.Regex;
        SelectedLogPriority = filter.Priority;
        SelectedLogSource = filter.Source;
        LogCrashOnly = filter.CrashOnly;
        LogBookmarksOnly = filter.BookmarksOnly;
        RefreshLogFilter();
        LogFilterStatus = $"Saved filter '{filter.Name}' applied";
    }

    [RelayCommand]
    private async Task DeleteSavedLogFilterAsync()
    {
        SavedLogFilter? filter = SelectedSavedLogFilter;
        if (filter is null) return;
        SavedLogFilters.Remove(filter);
        SelectedSavedLogFilter = null;
        await PersistSavedLogFiltersAsync();
        LogFilterStatus = $"Saved filter '{filter.Name}' deleted";
    }

    public async Task LoadSavedLogFiltersAsync()
    {
        SavedLogFilter[]? stored = await _sessions.LoadSettingAsync<SavedLogFilter[]>("logs.savedFilters", CancellationToken.None);
        if (stored is null) return;
        foreach (SavedLogFilter filter in stored)
        {
            if (string.IsNullOrWhiteSpace(filter.Name)) continue;
            if (SavedLogFilters.Any(item => item.Name.Equals(filter.Name, StringComparison.OrdinalIgnoreCase))) continue;
            SavedLogFilters.Add(filter);
        }
    }

    private async Task PersistSavedLogFiltersAsync() =>
        await _sessions.SaveSettingAsync("logs.savedFilters", SavedLogFilters.ToArray(), CancellationToken.None);
}
