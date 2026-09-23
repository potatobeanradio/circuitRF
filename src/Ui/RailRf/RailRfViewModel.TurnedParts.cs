// The two-terminal parts placed at 180° to their schematic, named — and turned in the layout on
// request (field report, 2026-09-22).
//
// ── WHAT WAS REPORTED ───────────────────────────────────────────────────────────────────────────
//
// Picking a supply net outlined the whole board, and a crystal-local net outlined it too. Thirty of
// the board's fifty-five two-pin parts had been dragged onto their lands turned end for end, which
// no picture shows, and each put a schematic net on the ground side of its part.
// `TurnedParts` (src/Design) now reads those parts from the copper inside the one pad funnel every
// surface uses; this file is the half that TELLS the user, and offers the only fix that makes the
// document itself agree — LVS, the placement table and the bill of materials all read the `.clay`.
//
// ── WHERE THE EDIT GOES ─────────────────────────────────────────────────────────────────────────
//
// Through the layout editor's own command stack wherever a layout window has that `.clay` open, so
// it is one undoable entry and marks the document dirty like any other edit. Where none has, into
// the FILE and into the copy this window holds, because there is no session to go through and a
// gesture that only worked with a second window open would be one nobody could find.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Design.Layout;
using CircuitRF.Design.RailRf;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.RailRf;

public sealed partial class RailRfViewModel
{
    /// <summary>The parts the copper reads as placed at 180° to their schematic. Never null.</summary>
    public IReadOnlyList<TurnedPart> PartsReadAsTurned => Board?.TurnedParts ?? [];

    /// <summary>True while there is at least one.</summary>
    public bool HasPartsReadAsTurned => PartsReadAsTurned.Count > 0;

    /// <summary>What the window says about them — the extraction's own sentence, so the Messages
    /// note and the pane cannot come to say different things.</summary>
    public string PartsReadAsTurnedNote => TurnedParts.Sentence(PartsReadAsTurned);

    /// <summary>Whether Turn may run: not while a re-read is pending, because the list may name a part
    /// the user has just turned by hand, and turning it again would put it back the wrong way.</summary>
    public bool CanTurnParts => HasPartsReadAsTurned && !IsReadingParts;

    /// <summary>The button's words — it names what it will do.</summary>
    public string TurnPartsButtonText => TurnedParts.ButtonText(PartsReadAsTurned);

    /// <summary>Why the last Turn did not happen, or empty.</summary>
    [ObservableProperty]
    private string _turnPartsProblem = "";

    /// <summary>True while <see cref="TurnPartsProblem"/> has something to say.</summary>
    public bool HasTurnPartsProblem => TurnPartsProblem.Length > 0;

    partial void OnTurnPartsProblemChanged(string value) => OnPropertyChanged(nameof(HasTurnPartsProblem));

    /// <summary>
    /// Applies instance edits through an OPEN layout session for the given <c>.clay</c>, returning
    /// false where no layout window has it open. Set by the window; null in a test that wants the
    /// file path.
    /// </summary>
    internal Func<string, IReadOnlyList<(int Index, LayoutInstance Before, LayoutInstance After)>, string, bool>?
        EditLiveLayout { get; set; }

    /// <summary>
    /// Turns every part in <see cref="PartsReadAsTurned"/> 180° about its own two lands, so each land
    /// takes the other's place and the document states the pin order the copper already shows.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTurnParts))]
    private void TurnParts()
    {
        if (Board is not { View: { } view, ArtworkCellRef: { Length: > 0 } clay }) return;
        var parts = PartsReadAsTurned;
        if (parts.Count == 0) return;

        // The one edit list the LVS panel's Turn applies too (brief LVS 16 R-lvs16-3b), so a Turn
        // pressed in either window is the same edit.
        var edits = TurnedParts.Edits(view, parts, out var stale);
        if (stale is not null)
        {
            TurnPartsProblem = $"{stale.Refdes} is no longer where railRF read it — the layout has " +
                               "changed since. Nothing was turned.";
            return;
        }

        string description = TurnedParts.EditDescription(parts);

        if (EditLiveLayout?.Invoke(clay, edits, description) != true && !WriteTurnsToFile(clay, view, edits))
            return;

        TurnPartsProblem = "";
        RefreshBoardPads();
    }

    /// <summary>
    /// The no-session half of <see cref="TurnParts"/>: the file on disk, then the copy this window
    /// holds. Refused, with nothing written, where the file no longer holds the parts railRF read.
    /// </summary>
    private bool WriteTurnsToFile(
        string clay, LayoutView view,
        IReadOnlyList<(int Index, LayoutInstance Before, LayoutInstance After)> edits)
    {
        try
        {
            var onDisk = LayoutPersistence.LoadFromFile(clay);
            foreach (var (index, before, _) in edits)
            {
                if (index < onDisk.Instances.Count && SamePlacement(onDisk.Instances[index], before)) continue;

                TurnPartsProblem = $"'{Path.GetFileName(clay)}' has changed on disk since railRF read it, " +
                                   "so nothing was turned. Close and reopen this document, then turn them.";
                return false;
            }

            foreach (var (index, _, after) in edits) onDisk.Instances[index] = LayoutGeometry.Clone(after);
            LayoutPersistence.SaveToFile(clay, onDisk);
        }
        catch (Exception ex)
        {
            TurnPartsProblem = $"'{Path.GetFileName(clay)}' could not be written: {ex.Message}. Nothing was turned.";
            return false;
        }

        foreach (var (index, _, after) in edits) view.Instances[index] = after;
        return true;
    }

    private static bool SamePlacement(LayoutInstance a, LayoutInstance b) =>
        a.X == b.X && a.Y == b.Y && a.RotationDegrees == b.RotationDegrees && a.MirrorX == b.MirrorX
        && string.Equals(a.CellRef, b.CellRef, StringComparison.Ordinal)
        && string.Equals(a.SchematicId, b.SchematicId, StringComparison.Ordinal);

    /// <summary>
    /// Re-reads the board's pads off the artwork this window holds — now, on the UI thread, for a
    /// gesture that moved parts and must show the answer before anything else can be pressed.
    /// </summary>
    /// <remarks>
    /// The Turn gesture's route. An edit made in the layout window next door takes the DEBOUNCED one
    /// (<c>RailRfViewModel.PadRead.cs</c>), because one galvanic partition per keystroke is the shape
    /// R-rail19-2c forbids; both end in the same <see cref="RefreshBoardPads(RailBoardInputs, IReadOnlyList{LayoutShape}, RailArtwork.RailPadResolution, PinSignature)"/>.
    /// A read already settling is abandoned: this one answers the same question about a newer model.
    /// </remarks>
    internal void RefreshBoardPads()
    {
        if (Board is not { View: { } view } board) return;
        CancelPadRead();

        var shapes = RailArtwork.FlattenedShapes(view, board.ArtworkCellRef, board.Technology);
        PadReadsPerformed++;
        var resolved = RailArtwork.PadsFor(view, board.ArtworkCellRef, board.Technology, BoardNetlist, null, shapes);
        RefreshBoardPads(board, shapes, resolved, PinSignature.Of(view));
    }

    /// <summary>
    /// <b>What a pad refresh re-states — the ONE copy</b>, which both the Turn gesture and the
    /// debounced read call. Two copies of this list is the <see cref="RebuildAvailableNets"/> scar
    /// again: the one that forgets a line leaves a derived surface describing the old pads.
    /// </summary>
    /// <remarks>
    /// The board is written through its backing field for <see cref="NotifyArtworkChanged"/>'s reason
    /// — assigning the property would rebuild the canvas and take the viewport away.
    /// </remarks>
    private void RefreshBoardPads(
        RailBoardInputs board, IReadOnlyList<LayoutShape> shapes, RailArtwork.RailPadResolution resolved,
        PinSignature readFrom)
    {
        // R-rail28-2: the pick survives where its net does. The selection is cleared by every
        // invalidation below (and, on the debounced path, by the edit that started it), so the name
        // is taken first and handed back after the list is rebuilt.
        string? keep = SelectedNet?.Name ?? _netAcrossPadRead;
        _netAcrossPadRead = null;

#pragma warning disable MVVMTK0034
        _board = board with
        {
            Shapes      = shapes,
            Pads        = resolved.Pads,
            NetPoints   = resolved.NetPoints,
            Nets        = resolved.Nets,
            NetOrigin   = resolved.NetOrigin,
            TurnedParts = resolved.Turned,
        };
#pragma warning restore MVVMTK0034
        _padsReadFrom = readFrom;

        // The results GO, and not out of caution: every one was solved from the net points just
        // replaced, and a moved pin is a moved seed. On the debounced path NotifyArtworkChanged has
        // already cleared them for the copper; this also covers a run started while the read settled,
        // which was seeded from the pads this refresh replaces.
        ClearResults();
        InvalidateNetWalks();
        RebuildAvailableNets();
        RebuildBoardFootprints();
        RebuildParts();
        if (keep is not null) SelectNet(keep);
        SyncBoardOverlayResult();
        RefreshRunGate();
        NotifyPartsReadAsTurned();
        OnPropertyChanged(nameof(StatusLine));
    }

    private void NotifyPartsReadAsTurned()
    {
        OnPropertyChanged(nameof(PartsReadAsTurned));
        OnPropertyChanged(nameof(HasPartsReadAsTurned));
        OnPropertyChanged(nameof(PartsReadAsTurnedNote));
        OnPropertyChanged(nameof(TurnPartsButtonText));
        TurnPartsCommand.NotifyCanExecuteChanged();
    }
}
