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

    /// <summary>The button's words — it names what it will do.</summary>
    public string TurnPartsButtonText => PartsReadAsTurned.Count == 1
        ? $"Turn {PartsReadAsTurned[0].Refdes} in the layout"
        : $"Turn these {PartsReadAsTurned.Count} in the layout";

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
    [RelayCommand(CanExecute = nameof(HasPartsReadAsTurned))]
    private void TurnParts()
    {
        if (Board is not { View: { } view, ArtworkCellRef: { Length: > 0 } clay }) return;
        var parts = PartsReadAsTurned;
        if (parts.Count == 0) return;

        var edits = new List<(int Index, LayoutInstance Before, LayoutInstance After)>(parts.Count);
        foreach (var part in parts)
        {
            if (part.InstanceIndex < 0 || part.InstanceIndex >= view.Instances.Count)
            {
                TurnPartsProblem = $"{part.Refdes} is no longer where railRF read it — the layout has " +
                                   "changed since. Nothing was turned.";
                return;
            }
            var before = view.Instances[part.InstanceIndex];
            edits.Add((part.InstanceIndex, before, TurnedParts.HalfTurn(before, part)));
        }

        string description = parts.Count == 1 ? $"Turn {parts[0].Refdes} 180°" : $"Turn {parts.Count} parts 180°";

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
    /// Re-reads the board's pads off the artwork this window holds — once, for a gesture that moved
    /// parts.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately not part of <see cref="NotifyArtworkChanged"/></b>, which runs on every edit
    /// in the layout window next door: the pad funnel builds a galvanic partition, and one of those
    /// per keystroke is the shape R-rail19-2c forbids. A gesture that knows it moved parts asks for
    /// it here, and the board is written through its backing field for NotifyArtworkChanged's own
    /// reason — assigning the property would rebuild the canvas and take the viewport away.
    /// </remarks>
    internal void RefreshBoardPads()
    {
        if (Board is not { View: { } view } board) return;

        var shapes = RailArtwork.FlattenedShapes(view, board.ArtworkCellRef, board.Technology);
        var resolved = RailArtwork.PadsFor(view, board.ArtworkCellRef, board.Technology, BoardNetlist, null, shapes);

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

        ClearResults();
        InvalidateNetWalks();
        RebuildAvailableNets();
        RebuildBoardFootprints();
        RebuildParts();
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
