// ================================================================
//  DataSourceLibraryViewModel.Freeze.cs  —  earlier runs kept on screen
//
//  Freeze holds the results a re-run replaces, so a display shows the
//  latest run with the ones before it underneath: each earlier run a
//  fainter copy of every trace, the oldest faintest, up to five. For the
//  whole display (every tab reads this library), for this session only —
//  nothing here is saved, and turning Freeze off lets them go.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using RfCore.Data;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

public partial class DataSourceLibraryViewModel
{
    /// <summary>How many earlier runs of one source Freeze keeps.</summary>
    public const int MaxFrozenRuns = 5;

    private readonly Dictionary<string, List<DataSet>> _frozenRuns = new(StringComparer.OrdinalIgnoreCase);
    private bool _isFrozen;

    /// <summary>
    /// While true, a reload keeps the result it replaces (<see cref="FrozenRunsFor"/>). Turning it off
    /// forgets them.
    /// </summary>
    public bool IsFrozen
    {
        get => _isFrozen;
        set
        {
            if (_isFrozen == value) return;
            _isFrozen = value;
            if (!value) _frozenRuns.Clear();
            OnPropertyChanged();
            OnPropertyChanged(nameof(FrozenRunCount));
            SnapshotChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The most earlier runs any one source holds — what the toolbar says.</summary>
    public int FrozenRunCount
    {
        get
        {
            int n = 0;
            foreach (var runs in _frozenRuns.Values) n = Math.Max(n, runs.Count);
            return n;
        }
    }

    /// <summary>The earlier runs of <paramref name="absPath"/>, newest first; empty when there are none.</summary>
    public IReadOnlyList<DataSet> FrozenRunsFor(string? absPath)
        => absPath is not null && _frozenRuns.TryGetValue(Path.GetFullPath(absPath), out var runs)
            ? runs
            : Array.Empty<DataSet>();

    private void KeepFrozenRun(DataSourceEntryViewModel entry, DataSet? replaced)
    {
        if (!_isFrozen || replaced is null || entry.FilePath is not { } path) return;
        if (ReferenceEquals(replaced, entry.Data)) return;
        string key = Path.GetFullPath(path);
        if (!_frozenRuns.TryGetValue(key, out var runs)) _frozenRuns[key] = runs = new List<DataSet>();
        runs.Insert(0, replaced);
        if (runs.Count > MaxFrozenRuns) runs.RemoveRange(MaxFrozenRuns, runs.Count - MaxFrozenRuns);
        OnPropertyChanged(nameof(FrozenRunCount));
    }
}
