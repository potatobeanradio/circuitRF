// Saving the `.crail` (owner, 2026-09-19).
//
// ── UNTIL THIS, THE WINDOW WROTE NOTHING ──────────────────────────────────────────────────────
//
// `RailDocumentIo` has been able to write a `.crail` since brief 1 and nothing in the application
// ever called it: the clipboard serialised one, the CLI round-tripped one in memory, and the
// window's Export button wrote RESULTS. So every edit this window makes — the rail set, the
// sources and loads, the targets, the Settings flyout's four values, the class overrides — lived
// for as long as the window did. The panel toggles the owner asked to persist are simply the edit
// that made the omission impossible to leave.
//
// ── THE DIRTY MARK IS A BYTE COMPARISON, NOT A FLAG ───────────────────────────────────────────
//
// `IsDirty` serialises the document and compares it with what was last read or written. That is
// the one definition that cannot drift: it compares the actual bytes a save would produce against
// the actual bytes on disk, so an edit nobody remembered to flag still shows, and a pair of edits
// that cancel out correctly does not. A boolean set by hand at every edit site is the version of
// this that is wrong six months later at the one site somebody forgot.
//
// It is refreshed from `QueueResolve` — the view model's own documented funnel for a committed
// edit — and from the panel setters, which deliberately do not queue a solve. **Save is enabled
// either way**: the mark tells the user something, it does not gate the command, so a refresh this
// file failed to reach can never be the reason a document would not write.

using System;
using CircuitRF.Design.RailRf;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.RailRf;

public sealed partial class RailRfViewModel
{
    /// <summary>The document exactly as it was last read from disk or written to it, or null for a
    /// scratch window that has never been either.</summary>
    private string? _savedSnapshot;

    /// <summary>
    /// Whether this document differs from what is on disk.
    /// </summary>
    /// <remarks>
    /// <b>Unvalidated on purpose</b> — this is a comparison, not a write, and a half-built document
    /// (a rail with no net picked yet) is exactly the state a user is in while they are working. A
    /// window that has never been saved is dirty the moment it holds anything at all.
    /// </remarks>
    public bool IsDirty => !string.Equals(_savedSnapshot, Snapshot(), StringComparison.Ordinal);

    private string Snapshot() => RailDocumentIo.SerializeUnvalidated(_document);

    /// <summary>
    /// Records that the document on screen is now what is on disk at
    /// <paramref name="path"/> — what a save calls, and what opening one calls.
    /// </summary>
    public void NoteSaved(string path)
    {
        _documentPath = path;
        _savedSnapshot = Snapshot();

        OnPropertyChanged(nameof(DocumentPath));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(DocumentLabel));
        OnPropertyChanged(nameof(DocumentPathTip));
    }

    /// <summary>Re-reads the dirty mark. Called from the edit funnel, not from each edit.</summary>
    private void RefreshDirty()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(DocumentLabel));
    }

    /// <summary>The document the window opened on, before anything was edited.</summary>
    private void CaptureSnapshot() => _savedSnapshot = Snapshot();

    /// <summary>
    /// Where Save as… opens: the folder the document is already in, else the CELL folder of the
    /// board it prices (the one holding <c>.ccell</c> above <c>layout/</c>), else the folder of the
    /// <c>.clay</c> itself. Null with neither — the picker's own default.
    /// </summary>
    /// <remarks>
    /// The picker used to open wherever the platform last left it, so a first save of real work
    /// landed as <c>board.crail</c> in a folder outside the workspace, where the tree does not show
    /// it and nothing offers to move it. Beside the board's cell is where the tree lists it and where
    /// its artwork reference is one short relative hop.
    /// </remarks>
    public string? SuggestedSaveFolder
    {
        get
        {
            if (_documentPath is { Length: > 0 } saved
                && System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(saved)) is { } own
                && System.IO.Directory.Exists(own))
                return own;

            if (Board?.ArtworkCellRef is not { Length: > 0 } clay
                || System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(clay)) is not { } dir
                || !System.IO.Directory.Exists(dir))
                return null;

            if (string.Equals(System.IO.Path.GetFileName(dir), CircuitRF.Design.Cells.CellFolder.LayoutSubFolder,
                              StringComparison.OrdinalIgnoreCase)
                && System.IO.Path.GetDirectoryName(dir) is { } cell
                && System.IO.File.Exists(System.IO.Path.Combine(cell, CircuitRF.Design.Cells.CellFolder.CcellFileName)))
                return cell;

            return dir;
        }
    }

    /// <summary>What Save as… suggests, without the extension: the document's own name, then the file
    /// it came from, then the board's layout it prices, and only then <c>board</c>.</summary>
    public string SuggestedSaveName =>
        _document.Name is { Length: > 0 } n ? n
        : _documentPath is { Length: > 0 } p ? System.IO.Path.GetFileNameWithoutExtension(p)
        : Board?.ArtworkCellRef is { Length: > 0 } clay ? System.IO.Path.GetFileNameWithoutExtension(clay)
        : "board";

    // ── The two commands, and why the writing is not here ────────────────────────────────────

    /// <summary>
    /// What a save actually does — installed by the window, because writing needs a file picker and
    /// a top level and this view model has neither.
    /// </summary>
    /// <remarks>
    /// <b>The same split <c>PostToUi</c> draws</b>, and wBond's and harmonicaRF's own
    /// <c>SaveDocumentHook</c>. It defaults to null so a test can drive every other part of this
    /// view model with no application host; a window always installs one.
    /// <para>The argument is <c>saveAs</c>.</para>
    /// </remarks>
    public Action<bool>? SaveRequested { get; set; }

    /// <summary>Write the document where it came from — or ask, the first time.</summary>
    [RelayCommand] private void Save()   => SaveRequested?.Invoke(false);

    /// <summary>Write it somewhere else.</summary>
    [RelayCommand] private void SaveAs() => SaveRequested?.Invoke(true);
}
