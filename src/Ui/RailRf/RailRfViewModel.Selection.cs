// What is selected in this window, and the mark it puts on the board
// (owner, 2026-09-19).
//
// ── ONE SELECTION, ACROSS FIVE TABLES ─────────────────────────────────────────────────────────
//
// The specification column is four lists — Sources, Loads, Aggressors — and the parts table below
// them, and each was an ordinary ListBox owning its own SelectedItem. So a row could be highlighted
// in the parts table AND in the sources list at once, which reads as two selections and is not one:
// nothing in this window acts on a pair, the remove buttons act on their own list's row, and the
// board can only mark one thing. This file is the single place that answers "what is selected", and
// setting any one of them clears the rest.
//
// THE RANKED BREAKDOWN IS THE FIFTH, since R-rail19-3, and it joined this rather than keeping its
// own for exactly the reason above: it marks the board, and so does the parts table.
//
// ── ESCAPE CLEARS WHICHEVER IT IS ─────────────────────────────────────────────────────────────
//
// Escape already cleared the parts table. It clears all four now, through ONE command, because a
// window where the keystroke works on one list and silently does nothing on the next is worse than
// one where it does not exist — the user has no way to tell which case they are in.
//
// AND IT CLEARS A SELECTED MARKER, which is the fifth thing this window can have selected (owner,
// 2026-09-19). A marker is selected by clicking its glyph or its info box, and until then the only
// way back was to click empty plot area — the gesture that already deselects it, by selecting the
// container instead. Escape did nothing there, for exactly the reason above: HasRowSelection asked
// the four LISTS and a marker is not on one, so the handler returned before it ever ran. The
// selection lives on MarkerInfoBoxViewModel.IsSelected — the glyph and its box are one selectable
// thing — so DataDisplayViewModel.DeselectAll is what clears it, the same method the Data Display's
// own Escape KeyBinding is wired to. Not a second selection model; the Data Display's own.
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
    /// True while one selection is being moved to another list, so the clearing of the others does
    /// not recurse and does not publish an intermediate highlight per list.
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

    /// <summary>
    /// The row picked in the ranked breakdown, or null — <b>R-rail19-3's locator.</b>
    /// </summary>
    /// <remarks>
    /// <b>It joins the one selection rather than owning a second.</b> A breakdown row and a part
    /// row both mark the board and the board can only mark one thing, so a list that kept its own
    /// selection would leave two marks up and no way to tell which answered which question.
    ///
    /// <para><b>And it moves the camera</b> (R-rail19-3a), which the part table's mark deliberately
    /// does not: a part has a minimum on-screen size in the renderer because "where is C7" is asked
    /// from a view of the whole board, but a 0.2 mm run of copper on a 30 x 20 mm board at fit zoom
    /// is three pixels and a highlight the user cannot find has answered nothing.</para>
    /// </remarks>
    [ObservableProperty]
    private RailBreakdownRowViewModel? _selectedBreakdownRow;

    partial void OnSelectedPartChanged(RailPartRowViewModel? value)             => TakeSelection(value);
    partial void OnSelectedSourceChanged(RailSourceRowViewModel? value)         => TakeSelection(value);
    partial void OnSelectedLoadChanged(RailLoadRowViewModel? value)             => TakeSelection(value);
    partial void OnSelectedAggressorChanged(RailAggressorRowViewModel? value)   => TakeSelection(value);
    partial void OnSelectedBreakdownRowChanged(RailBreakdownRowViewModel? value)
    {
        TakeSelection(value);
        if (value is not null && !_movingSelection) ShowBreakdownRowOnBoard(value);
    }

    /// <summary>True while any of the five lists has a row selected.</summary>
    public bool HasRowSelection =>
        SelectedPart is not null || SelectedSource is not null
     || SelectedLoad is not null || SelectedAggressor is not null
     || SelectedBreakdownRow is not null;

    /// <summary>True while a marker on the results plot is selected — its glyph, or its info box,
    /// which are one selectable thing.</summary>
    public bool HasMarkerSelection => PlotHost.HasSelectedInfoBoxes;

    /// <summary>True while this window has ANYTHING selected — <b>what Escape reads</b>.</summary>
    /// <remarks>
    /// Rows and markers together, because the keystroke is one gesture. Gating it on the rows alone
    /// is what made Escape silently inert on a selected marker (owner, 2026-09-19).
    ///
    /// <para><b>And an ARMED pour pick, which is the third thing Escape has to reach</b> (owner,
    /// 2026-09-20). It is not a selection, but it is the same shape: a state the user entered
    /// deliberately, which changes what the next click does, and which they will try to leave with
    /// Escape because that is how they leave the layout canvas's zoom box next door. A key that
    /// works on two of three states is the one case a user cannot diagnose — this file's own header
    /// says so about the four lists.</para>
    /// </remarks>
    public bool HasSelection => HasRowSelection || HasMarkerSelection || IsPickingFromBoard;

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
                if (!ReferenceEquals(kept, SelectedBreakdownRow)) SelectedBreakdownRow = null;
            }
            finally { _movingSelection = false; }
        }

        PublishSelection();
    }

    /// <summary>
    /// Clears every list's selection AND any selected marker — what <b>Escape</b> is wired to.
    /// </summary>
    /// <remarks>
    /// A command rather than a setter call in the code-behind, so the keystroke and any menu row that
    /// ever wants it reach the same one thing, and so a test can drive it with no application host.
    /// </remarks>
    [RelayCommand]
    private void ClearSelection()
    {
        ClearRowSelection();

        // The armed pour pick goes with them — see HasSelection's own note.
        CancelPickFromBoard();

        // The Data Display's own — see this file's header. It also drops the plot CONTAINER's
        // selection, which is inert here: this window lays the one container out itself and draws no
        // selection chrome for it, and the container is not deletable.
        PlotHost.DeselectAll();
        OnPropertyChanged(nameof(HasMarkerSelection));
        OnPropertyChanged(nameof(HasSelection));
    }

    /// <summary>Clears every list's selection, leaving any selected marker alone.</summary>
    /// <remarks>
    /// Kept separate from <see cref="ClearSelectionCommand"/> because the document-replaced path
    /// calls it: a new document rebuilds the plot and its markers from scratch, and reaching into
    /// the plot host's selection there would be answering a question nobody asked.
    /// </remarks>
    [RelayCommand]
    private void ClearRowSelection()
    {
        _movingSelection = true;
        try
        {
            SelectedPart         = null;
            SelectedSource       = null;
            SelectedLoad         = null;
            SelectedAggressor    = null;
            SelectedBreakdownRow = null;
        }
        finally { _movingSelection = false; }

        BreakdownLocatorNote = "";
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
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(PartHighlight));
        OnPropertyChanged(nameof(BreakdownLocation));
        BoardOverlayLayer.PartHighlight = PartHighlight;
    }

    // ── R-rail19-3: the breakdown's locator ───────────────────────────────────────────────────

    /// <summary>
    /// What the selected breakdown row IS, where it is not copper — or empty.
    /// </summary>
    /// <remarks>
    /// <b>R-rail19-3b, and it is not a nicety.</b> Some rows are a source's own series resistance,
    /// a part's ESR or an observation port: none of them is copper and none has anywhere on the
    /// board to point at. Leaving the previous row's copper lit would be a locator pointing at the
    /// WRONG thing, which is worse than no locator — so the highlight is cleared and this says why.
    /// </remarks>
    [ObservableProperty]
    private string _breakdownLocatorNote = "";

    /// <summary>
    /// Where the board should look, in DBU — <b>set by the window to the canvas's own
    /// <c>ZoomToRegion</c></b>, which is the same camera move the layout editor makes when Update
    /// Layout places an instance off screen.
    /// </summary>
    /// <remarks>
    /// A hook rather than a canvas reference, for the reason every other seam on this view model is
    /// one: this file is framework-free and a test drives the locator with no application host.
    /// </remarks>
    public Action<Bbox>? ShowOnBoardHook { get; set; }

    /// <summary>Where the selected breakdown row's copper is, or null.</summary>
    public RailBreakdownLocation? BreakdownLocation =>
        SelectedBreakdownRow is { GroupKey.Length: > 0 } row &&
        SelectedRailResult?.BreakdownLocations.TryGetValue(row.GroupKey, out var found) == true
            ? found
            : null;

    private void ShowBreakdownRowOnBoard(RailBreakdownRowViewModel row)
    {
        if (BreakdownLocation is { Bounds.IsEmpty: false } place)
        {
            BreakdownLocatorNote = "";
            ShowOnBoardHook?.Invoke(place.Bounds);
            return;
        }

        BreakdownLocatorNote =
            $"{row.Row.Label} is not copper — it is a part, a source's own series resistance or an "
          + "observation port, so there is nothing on the board to point at.";
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
            // A breakdown row is COPPER, not an anchor: it has no pads, and its box is the extent
            // of every cell its group's elements touch (R-rail19-3). Handed the same mark type, so
            // the board draws one kind of selection however it was asked for.
            if (SelectedBreakdownRow is { } breakdown)
                return BreakdownLocation is { Bounds.IsEmpty: false } place
                    ? new RailPartHighlight(breakdown.Row.Label, [], place.Bounds)
                    : null;

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
