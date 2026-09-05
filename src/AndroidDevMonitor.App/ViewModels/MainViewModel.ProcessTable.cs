using AndroidDevMonitor.Core.Models;
using CommunityToolkit.Mvvm.Input;

namespace AndroidDevMonitor.App.ViewModels;

public partial class MainViewModel
{
    private IReadOnlyList<AndroidProcess>? _frozenProcesses;
    private DateTimeOffset _processTableFrozenAt;

    public bool IsProcessTablePaused => _frozenProcesses is not null;
    public bool IsProcessTableLive => !IsProcessTablePaused;
    public string ProcessTableStatus => IsProcessTablePaused ? $"Frozen · {_processTableFrozenAt:HH:mm:ss}" : "Live";

    [RelayCommand(CanExecute = nameof(IsProcessTableLive))]
    private void StopProcessTable()
    {
        if (IsProcessTablePaused) return;
        _frozenProcesses = _allProcesses.ToArray();
        _processTableFrozenAt = DateTimeOffset.Now;
        NotifyProcessTableState();
    }

    [RelayCommand(CanExecute = nameof(IsProcessTablePaused))]
    private void StartProcessTable()
    {
        if (!IsProcessTablePaused) return;
        _frozenProcesses = null;
        ApplyProcessSnapshot(_allProcesses);
        ApplyProcessFilter();
        NotifyProcessTableState();
    }

    private void ResetProcessTable()
    {
        _frozenProcesses = null;
        _allProcesses.Clear();
        NotifyProcessTableState();
    }

    private void NotifyProcessTableState()
    {
        OnPropertyChanged(nameof(IsProcessTablePaused));
        OnPropertyChanged(nameof(IsProcessTableLive));
        OnPropertyChanged(nameof(ProcessTableStatus));
        StartProcessTableCommand.NotifyCanExecuteChanged();
        StopProcessTableCommand.NotifyCanExecuteChanged();
    }
}
