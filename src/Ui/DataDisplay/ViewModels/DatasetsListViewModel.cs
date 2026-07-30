// ================================================================
//  DatasetsListViewModel.cs — docked Properties-tool Datasets section (R-dd-4/5)
// ================================================================
//
//  Lives beside the plot inspector in the SAME docked Properties tool (never a new top-level
//  panel) — see WorkspaceViewModel.RouteDataDisplayProperties / PropertiesTool.SetActiveDataDisplay.
//  Visible whenever a Data Display document is active, regardless of whether a plot is currently
//  selected: it exists for three reasons, only one of which is adding traces — renaming an alias,
//  seeing/re-pointing a missing dataset, and re-pointing a live one to swap what a whole comparison
//  plots against.

using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

public sealed partial class DatasetsListViewModel : ObservableObject
{
    private DisplayWindowViewModel? _window;
    private DataSourceLibraryViewModel? _wiredLibrary;

    public ObservableCollection<DatasetRowViewModel> Rows { get; } = new();

    public bool HasWindow => _window is not null;
    public bool HasRows   => Rows.Count > 0;

    /// <summary>Workspace root, so rows can mark outside-the-workspace sources external (R-stb-12).</summary>
    public Func<string?>? WorkspaceRootProvider { get; set; }

    private Func<string, System.Threading.Tasks.Task<string?>>? _locateFileRequested;

    /// <summary>Set by code-behind so a row's "Locate…"/"Re-point…" button can show a file picker.
    /// Propagates to already-built rows, so wiring order (before or after the first SetWindow
    /// call) doesn't matter.</summary>
    public Func<string, System.Threading.Tasks.Task<string?>>? LocateFileRequested
    {
        get => _locateFileRequested;
        set
        {
            _locateFileRequested = value;
            foreach (var row in Rows) row.LocateFileRequested = value;
        }
    }

    /// <summary>Called by PropertiesTool.SetActiveDataDisplay whenever the active Data Display
    /// document changes (including to/from null). Re-entrant-safe: re-wires even when the window
    /// reference is unchanged, since the initial call after construction always needs to run.</summary>
    public void SetWindow(DisplayWindowViewModel? window)
    {
        Unwire();
        _window = window;
        Wire();
        Rebuild();
        OnPropertyChanged(nameof(HasWindow));
    }

    private void Wire()
    {
        _wiredLibrary = _window?.DataSourceLibrary;
        if (_wiredLibrary is null) return;
        _wiredLibrary.Entries.CollectionChanged += OnEntriesChanged;
        _wiredLibrary.LibraryChanged            += OnLibraryChanged;
    }

    private void Unwire()
    {
        if (_wiredLibrary is null) return;
        _wiredLibrary.Entries.CollectionChanged -= OnEntriesChanged;
        _wiredLibrary.LibraryChanged            -= OnLibraryChanged;
        _wiredLibrary = null;
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();
    private void OnLibraryChanged(object? sender, EventArgs e) => RefreshExisting();

    private void Rebuild()
    {
        Rows.Clear();
        var lib = _window?.DataSourceLibrary;
        if (lib is not null)
        {
            foreach (var entry in lib.Entries)
            {
                var row = new DatasetRowViewModel(entry, lib, _window!)
                {
                    LocateFileRequested = _locateFileRequested,
                    WorkspaceRootProvider = WorkspaceRootProvider
                };
                Rows.Add(row);
            }
        }
        OnPropertyChanged(nameof(HasRows));
    }

    // A LibraryChanged event (alias set, entry restored/re-pointed) never changes WHICH entries
    // exist, only their state — refresh existing rows in place rather than rebuilding, so the
    // list doesn't visually flicker/reset scroll position on every edit.
    private void RefreshExisting()
    {
        foreach (var row in Rows) row.RefreshFromEntry();
    }
}
