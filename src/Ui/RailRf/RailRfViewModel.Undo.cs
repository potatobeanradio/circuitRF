// Undo and redo for the railRF window (owner, 2026-09-20).
//
// ── WHY THIS FILE EXISTS ──────────────────────────────────────────────────────────────────────
//
// It did not, and the report that asked for it is the same one that disarmed the pour pick: a
// click on the shipped board made a rail nobody asked for, the run gate correctly refused, and
// Ctrl+Z did nothing — because until now the ONLY undo stack anywhere in railRF was the Part
// Library editor's. Every edit this window makes was one way. A window whose left column is
// thirty editable values and whose keystroke for taking one back is silently inert is a window
// where the cost of trying something is unbounded, and that is what the owner was describing when
// they said they felt stuck in a state they could not get out of.
//
// ── ONE SNAPSHOT PER COMMITTED EDIT, THROUGH THE FUNNEL THAT ALREADY EXISTS ────────────────────
//
// `QueueResolve` is this view model's documented funnel for a committed edit — its own header says
// so, and the dirty mark already hangs off it for exactly that reason. So the undo entry hangs off
// it too, and off nothing else: there is no per-edit-site push to forget at the one site somebody
// adds next year, which is the failure mode `IsDirty` chose a byte comparison to avoid.
//
// The entry is COARSE — the whole `.crail`, before and after — and that is deliberate rather than
// lazy. `RailDocumentIo.SerializeUnvalidated` is the function the dirty mark already calls on every
// edit, so the cost is one serialisation this window was paying anyway; and a fine-grained command
// per field would be thirty command types that each have to agree with the document format about
// what an edit IS. The snapshot cannot disagree with it: it is the format.
//
// ── WHAT IT DOES NOT COVER, STATED RATHER THAN DISCOVERED ─────────────────────────────────────
//
// The four panel toggles. They deliberately do not call `QueueResolve` (they are not a solve), so
// they are not on the stack. They are persisted in the document and they do move the dirty mark —
// but a pane the user closed is not an edit they press Ctrl+Z to take back, and putting a layout
// change on the same stack as a value change is how an undo stack stops being predictable.
//
// ── THE BOARD IS NOT IN THE SNAPSHOT, AND MUST NOT BE RE-RESOLVED ─────────────────────────────
//
// A `.crail` NAMES its artwork, its part library and its companions; loading them is
// `LoadDocumentReferences`' job and it walks the disk. `ApplySnapshot` restores the document's own
// content and leaves `Board` exactly where it is — an undo is not a re-open, and re-resolving
// three files to take back a typed number would be both slow and a chance for the board under the
// user to change for reasons they did not cause.

using System;
using CircuitRF.Design.RailRf;
using CircuitRF.Ui.Commands;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.RailRf;

/// <summary>
/// One coarse-grained undo entry: the whole <see cref="RailDocument"/>, before and after one
/// committed edit.
/// </summary>
/// <remarks>
/// <b>The first <see cref="Execute"/> does nothing, because the edit has already happened.</b>
/// <see cref="UndoRedoStack.Execute"/> runs a command as it pushes it, and applying the "after"
/// snapshot there would re-read the whole document — and with it every row view model — on every
/// committed keystroke, replacing the text box the user is typing in. <c>PartLibrarySnapshotCommand</c>
/// carries the same flag for the same reason; read its note for the grid case that found it. A redo
/// (the second and later <see cref="Execute"/>) genuinely has to apply it, and does.
/// </remarks>
internal sealed class RailSnapshotCommand(
    RailRfViewModel owner, string beforeJson, string afterJson, string description) : IUiCommand
{
    private bool _alreadyApplied = true;

    public string Description { get; } = description;

    public void Execute()
    {
        if (_alreadyApplied) { _alreadyApplied = false; return; }
        owner.ApplySnapshot(afterJson);
    }

    public void Undo() => owner.ApplySnapshot(beforeJson);
}

public sealed partial class RailRfViewModel
{
    /// <summary>This window's undo history. <b>Per document</b> — a re-open replaces it.</summary>
    public UndoRedoStack UndoRedo { get; } = new();

    /// <summary>The document as of the last entry pushed, which is what the next one's "before" is.</summary>
    private string? _undoBaseline;

    /// <summary>True while an undo or a redo is writing, so the write does not record itself.</summary>
    private bool _applyingSnapshot;

    /// <summary>Take back the last committed edit.</summary>
    /// <remarks>
    /// <b>A command, so the keystroke and both menu surfaces reach one thing</b> — the window's own
    /// rule for every other verb here, and what lets a test drive undo with no application host.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => UndoRedo.Undo();

    /// <summary>Put it back.</summary>
    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => UndoRedo.Redo();

    /// <summary>True while there is something to take back.</summary>
    public bool CanUndo => UndoRedo.CanUndo;

    /// <summary>True while there is something to put back.</summary>
    public bool CanRedo => UndoRedo.CanRedo;

    /// <summary>What Undo would take back, as the menu row spells it.</summary>
    public string UndoDescription => UndoRedo.UndoDescription;

    /// <summary>What Redo would put back.</summary>
    public string RedoDescription => UndoRedo.RedoDescription;

    /// <summary>
    /// Starts the history at the document on screen — the constructor's last act, and a re-open's.
    /// </summary>
    private void BeginUndoHistory()
    {
        // ONCE. The stack raises CanUndo/CanRedo after its own Refresh, which is later than the
        // point ApplySnapshot returns from — so the two menu rows follow the stack rather than
        // guessing at it, and a second subscription on a re-open would notify twice forever.
        if (!_undoWired)
        {
            _undoWired = true;
            UndoRedo.PropertyChanged += (_, _) => NotifyUndoState();
        }

        UndoRedo.Reset();
        _undoBaseline = Snapshot();
        NotifyUndoState();
    }

    private bool _undoWired;

    /// <summary>
    /// Records one committed edit, if it changed anything. <b>Called from
    /// <see cref="QueueResolve"/> and from nowhere else</b> — see this file's header.
    /// </summary>
    private void NoteEdit()
    {
        if (_applyingSnapshot) return;

        string now = Snapshot();
        if (_undoBaseline is null) { _undoBaseline = now; return; }
        if (string.Equals(_undoBaseline, now, StringComparison.Ordinal)) return;

        var entry = new RailSnapshotCommand(this, _undoBaseline, now, "Edit the rail document");
        _undoBaseline = now;
        UndoRedo.Execute(entry);
        NotifyUndoState();
    }

    /// <summary>
    /// Puts <paramref name="json"/> on screen — what an undo and a redo both do.
    /// </summary>
    /// <remarks>
    /// <b>The selected rail is kept where it still exists.</b> <see cref="RebuildRails"/> selects the
    /// FIRST rail, which is its contract everywhere else; here it would mean that taking back a typed
    /// load current also moved the whole window to another rail, and a keystroke that does two things
    /// is a keystroke nobody presses twice.
    ///
    /// <para><b>Nothing here re-resolves the board</b> — see this file's header.</para>
    /// </remarks>
    internal void ApplySnapshot(string json)
    {
        string? keep = SelectedRailName;

        _applyingSnapshot = true;
        try
        {
            _document = RailDocumentIo.DeserializeUnvalidated(json);
            _undoBaseline = json;

            ClearResults();
            RebuildRails();
            if (keep is { Length: > 0 } name && Rails.Contains(name)) SelectedRailName = name;

            ApplyLayerVisibility();
            AnnouncePanels();
            RebuildRegulatorOffers();
            RefreshNetMarks();

            OnPropertyChanged(nameof(Document));
            OnPropertyChanged(nameof(PickRailButtonText));
        OnPropertyChanged(nameof(WillShowExistingRail));

            // The funnel, for the gate and the dirty mark — with the recording suppressed, because
            // applying a snapshot is not an edit of the document, it IS one being taken back.
            QueueResolve();
        }
        finally { _applyingSnapshot = false; }

        NotifyUndoState();
    }

    /// <summary>Re-states everything the two menu rows and the two keystrokes read.</summary>
    private void NotifyUndoState()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoDescription));
        OnPropertyChanged(nameof(RedoDescription));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }
}
