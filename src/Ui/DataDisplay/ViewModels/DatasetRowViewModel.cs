// ================================================================
//  DatasetRowViewModel.cs — one row in the docked Datasets list (R-dd-4/5)
// ================================================================

using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

/// <summary>
/// One row of the Datasets list: alias · filename · status. Supports renaming (R-dd-5, refused on
/// a duplicate alias) and re-pointing (R-dd-4) — a missing dataset's row is the one place a
/// broken reference becomes actionable, and pointing a LIVE dataset at a different file updates
/// every trace using its alias in one action via <see cref="DisplayWindowViewModel.RepointDatasetAsync"/>.
/// </summary>
public sealed partial class DatasetRowViewModel : ObservableObject
{
    private readonly DataSourceEntryViewModel   _entry;
    private readonly DataSourceLibraryViewModel _library;
    private readonly DisplayWindowViewModel     _window;

    public DatasetRowViewModel(DataSourceEntryViewModel entry, DataSourceLibraryViewModel library,
                                DisplayWindowViewModel window)
    {
        _entry   = entry;
        _library = library;
        _window  = window;
        _aliasText = entry.Alias;

        LocateCommand = new AsyncRelayCommand(LocateOrRepointAsync);
    }

    public DataSourceEntryViewModel Entry => _entry;

    public string FileName => _entry.FileName ?? "(unknown)";
    public bool   IsBroken => _entry.IsBroken;

    /// <summary>
    /// Supplies the workspace root so a source outside it can be marked external (R-stb-12).
    /// Null (no workspace open) means nothing is classified as external.
    /// </summary>
    public Func<string?>? WorkspaceRootProvider { get; set; }

    /// <summary>
    /// R-stb-12 — true when this source lives OUTSIDE the workspace and therefore will not travel
    /// with it. A user about to share a workspace can see which sources will break on someone
    /// else's machine; without it, the failure surfaces there as a missing file with no explanation.
    /// </summary>
    public bool IsExternal =>
        _entry.FilePath is { } fp &&
        CircuitRF.Ui.Schematic.WorkspaceRefs.IsExternal(fp, WorkspaceRootProvider?.Invoke());

    public string StatusText => IsBroken ? "Missing" : IsExternal ? "External" : "Live";

    /// <summary>Missing and external are both states worth visually flagging.</summary>
    public bool IsFlagged => IsBroken || IsExternal;

    public string? StatusTooltip => IsBroken
        ? "The file was not found on disk. Trace settings are preserved — use Locate… to point at it again."
        : IsExternal
            ? "This file lives outside the workspace, so it will NOT travel with it — on another "
            + "machine it will report as missing until it is re-pointed."
            : null;

    public string LocateButtonText => IsBroken ? "Locate…" : "Re-point…";

    [ObservableProperty]
    private string _aliasText = "";

    [ObservableProperty]
    private string? _renameError;

    public bool HasRenameError => RenameError is not null;

    partial void OnRenameErrorChanged(string? value) => OnPropertyChanged(nameof(HasRenameError));

    /// <summary>Called by the view on the alias TextBox's LostFocus/Enter.</summary>
    public void CommitAlias()
    {
        string trimmed = AliasText.Trim();
        if (string.Equals(trimmed, _entry.Alias, StringComparison.Ordinal))
        {
            RenameError = null;
            AliasText   = _entry.Alias;
            return;
        }

        if (_library.TrySetAlias(_entry, trimmed))
        {
            RenameError = null;
        }
        else
        {
            RenameError = $"\"{trimmed}\" is already in use by another dataset.";
        }

        // Always resync display text to the canonical value (blank → file stem; a refused
        // rename leaves the entry's own alias untouched) — never leave stale typed text shown.
        AliasText = _entry.Alias;
    }

    public IAsyncRelayCommand LocateCommand { get; }

    /// <summary>Wired from the view: shows a single-file picker and returns the chosen path,
    /// or null if the user cancelled.</summary>
    public Func<string, Task<string?>>? LocateFileRequested { get; set; }

    private async Task LocateOrRepointAsync()
    {
        var request = LocateFileRequested;
        if (request is null) return;

        string? newPath = await request(_entry.FilePath ?? FileName);
        if (string.IsNullOrEmpty(newPath)) return;

        await _window.RepointDatasetAsync(_entry, newPath);
    }

    internal void RefreshFromEntry()
    {
        AliasText = _entry.Alias;
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(IsBroken));
        OnPropertyChanged(nameof(IsExternal));
        OnPropertyChanged(nameof(IsFlagged));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusTooltip));
        OnPropertyChanged(nameof(LocateButtonText));
    }
}
