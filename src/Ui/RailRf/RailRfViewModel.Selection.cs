// What is selected in this window, and the mark it puts on the board
// (owner, 2026-09-19).
//
// ── ONE SELECTION, ACROSS FOUR TABLES ─────────────────────────────────────────────────────────
//
// The specification column is four lists — Sources, Loads, Aggressors — and the parts table below
// them, and each was an ordinary ListBox owning its own SelectedItem. So a row could be highlighted
// in the parts table AND in the sources list at once, which reads as two selections and is not one:
// nothing in this window acts on a pair, the remove buttons act on their own list's row, and the
// board can only mark one thing. This file is the single place that answers "what is selected", and
// setting any one of the four clears the other three.
//
// ── ESCAPE CLEARS WHICHEVER IT IS ─────────────────────────────────────────────────────────────
//
// Escape already cleared the parts table. It clears all four now, through ONE command, because a
// window where the keystroke works on one list and silently does nothing on the next is worse than
// one where it does not exist — the user has no way to tell which case they are in.
//
// ── A SOURCE AND A LOAD ARE PLACES ON THE BOARD, SO THEY ARE MARKED LIKE ONE ──────────────────
//
// A part row marked its pads and its body box; a source or load row marked nothing, although a
// source and a load are ANCHORED — at a refdes and pin, or at a coordinate. The mark is the same
// mark (RailPartHighlight, a draw argument rather than part of the scene — see its own header), and
// the pads come from PdnAttachments.Resolve, which is the resolver every port already goes through:
// a pin FIELD such as U1.VDD reaches six pads and marks six. An AGGRESSOR is a frequency and not a
// place, so selecting one clears the mark rather than inventing somewhere to put it.

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CircuitRF.Render;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.RailRf;

public sealed partial class RailRfViewModel
{
    /// <summary>
    /// True while one selection is being moved to another list, so the clearing of the other three
    /// does not recurse and does not publish three intermediate highlights.
    /// </summary>
    private bool _movingSelection;

    /// <summary>
    /// The row picked in the parts table, or null for none — <b>the list box's own selection</b>,
    /// bound two ways.
    /// </summary>
    /// <remarks>
    /// <b>The table and the board are two views of one part.</b> Before this the parts table listed
    /// thirteen capacitors beside a picture of the board and there was no way to find any of them on
    /// it: the Position column says where C7 is in millimetres, and a coordinate is not something a
    /// person locates by eye on a 30 mm board (owner, 2026-09-19).
    ///
    /// <para><b>Null is an ordinary state and it is reachable</b> — Escape clears it
    /// (<see cref="ClearRowSelectionCommand"/>). A selection that could be made and not unmade
    /// leaves a mark on the board that outlives any reason for it, and the only way back would be to
    /// close the window.</para>
    /// </remarks>
    [ObservableProperty]
    private RailPartRowViewModel? _selectedPart;

    /// <summary>The row picked in the Sources list, or null. <inheritdoc cref="SelectedPart"/></summary>
    [ObservableProperty]
    private RailSourceRowViewModel? _selectedSource;

    /// <summary>The row picked in the Loads list, or null.</summary>
    [ObservableProperty]
    private RailLoadRowViewModel? _selectedLoad;

    /// <summary>The row picked in the Aggressors list, or null. It marks nothing on the board —
    /// an aggressor is a frequency.</summary>
    [ObservableProperty]
    private RailAggressorRowViewModel? _selectedAggressor;

    partial void OnSelectedPartChanged(RailPartRowViewModel? value)             => TakeSelection(value);
    partial void OnSelectedSourceChanged(RailSourceRowViewModel? value)         => TakeSelection(value);
    partial void OnSelectedLoadChanged(RailLoadRowViewModel? value)             => TakeSelection(value);
    partial void OnSelectedAggressorChanged(RailAggressorRowViewModel? value)   => TakeSelection(value);

    /// <summary>True while any of the four lists has a row selected — what Escape reads.</summary>
    public bool HasRowSelection =>
        SelectedPart is not null || SelectedSource is not null
     || SelectedLoad is not null || SelectedAggressor is not null;

    /// <summary>
    /// Makes <paramref name="kept"/> the window's one selection and clears the rest.
    /// </summary>
    private void TakeSelection(object? kept)
    {
        if (_movingSelection) return;

        if (kept is not null)
        {
            _movingSelection = true;
            try
            {
                if (!ReferenceEquals(kept, SelectedPart))       SelectedPart       = null;
                if (!ReferenceEquals(kept, SelectedSource))     SelectedSource     = null;
                if (!ReferenceEquals(kept, SelectedLoad))       SelectedLoad       = null;
                if (!ReferenceEquals(kept, SelectedAggressor))  SelectedAggressor  = null;
            }
            finally { _movingSelection = false; }
        }

        PublishSelection();
    }

    /// <summary>Clears every list's selection — what <b>Escape</b> is wired to.</summary>
    /// <remarks>
    /// A command rather than a setter call in the code-behind, so the keystroke and any menu row that
    /// ever wants it reach the same one thing, and so a test can drive it with no application host.
    /// </remarks>
    [RelayCommand]
    private void ClearRowSelection()
    {
        _movingSelection = true;
        try
        {
            SelectedPart      = null;
            SelectedSource    = null;
            SelectedLoad      = null;
            SelectedAggressor = null;
        }
        finally { _movingSelection = false; }

        PublishSelection();
    }

    /// <summary>
    /// Tells the window and the board what is selected now.
    /// </summary>
    /// <remarks>
    /// The highlight is PUSHED at the overlay, the way every other board input on this view model is
    /// (Result, Plane, Kind, HiddenLayers). It does NOT rebuild the scene — see
    /// <see cref="RailLayoutOverlay"/>.
    /// </remarks>
    private void PublishSelection()
    {
        OnPropertyChanged(nameof(HasRowSelection));
        OnPropertyChanged(nameof(PartHighlight));
        BoardOverlayLayer.PartHighlight = PartHighlight;
    }

    /// <summary>
    /// Where the selected row is on the board, or null when nothing is selected, the selection is an
    /// aggressor, or the board does not place it.
    /// </summary>
    /// <remarks>
    /// <b>Resolved HERE, from the board netlist, and not by the overlay or the renderer.</b> Which
    /// pads belong to <c>C7</c> — or to <c>U1.VDD</c> — is a question about the netlist, and
    /// answering it below the firewall would be a second copy of <c>PdnAttachments</c>' resolution
    /// that could come to disagree with the one every port already uses.
    ///
    /// <para><b>A row the board does not place produces null and the picture does not change.</b>
    /// The parts table already says <i>not placed</i> on that row; a mark drawn at the placement
    /// centroid of a part with no pads, or at the origin, would say something that is not true.</para>
    /// </remarks>
    public RailPartHighlight? PartHighlight
    {
        get
        {
            if (Board is not { } board) return null;

            // A part row is an anchor naming the whole component — every pad of it, which is what
            // PdnAttachments.Resolve returns for a refdes with no pin. One code path, so the mark on
            // a part and the mark on a port are the same mark.
            return SelectedPart is { } part
                     ? Mark(new RailPortAnchor { Refdes = part.Refdes }, part.Refdes, board)
                 : SelectedSource is { } source
                     ? Mark(source.Source.Anchor, source.Anchor, board)
                 : SelectedLoad is { } load
                     ? Mark(load.Load.Anchor, load.Anchor, board)
                 : null;
        }
    }

    /// <summary>The mark for one anchor, labelled as the row spells it.</summary>
    private RailPartHighlight? Mark(RailPortAnchor anchor, string label, RailBoardInputs board)
    {
        var pads = PdnAttachments.Resolve(anchor, board.Pads);

        // The body box where the placement states a centroid — it is what the part actually COVERS,
        // and on a bulk capacitor that is several millimetres wider than its two pads. A coordinate
        // anchor names no component, so it has none.
        Bbox? body = null;
        if (anchor.Refdes is { Length: > 0 } refdes &&
            Placement is { Refusal: null } table &&
            table.Rows.FirstOrDefault(r =>
                string.Equals(r.Refdes, refdes, StringComparison.OrdinalIgnoreCase)) is
                { Refdes: { Length: > 0 } } placed)
        {
            var box = new Bbox(placed.X, placed.Y, placed.X, placed.Y);
            foreach (var (x, y) in pads) box = box.Union(new Bbox(x, y, x, y));
            long reach = RailPartHighlight.PadReachDbu;
            body = new Bbox(box.MinX - reach, box.MinY - reach, box.MaxX + reach, box.MaxY + reach);
        }

        if (pads.Count == 0 && body is null) return null;

        return new RailPartHighlight(label, [.. pads], body);
    }
}
