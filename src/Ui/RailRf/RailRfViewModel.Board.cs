// The board panel's own state: the layout the canvas shows, the overlay drawn on it, and the
// readout under the cursor (docs/sonnet-briefs/brief-railrf-8-board-view.md R-rail8-8, R-rail8-11,
// R-rail8-12; railrf.md §11.6).
//
// ── THE VIEWPORT IS PER DOCUMENT AND IT PERSISTS, THROUGH THE MECHANISM ALREADY TESTED ─────────
//
// R-rail8-8, and it is why the LayoutEditorViewModel lives HERE rather than in the window. Pan and
// zoom are canvas-owned state, and a canvas does not survive being re-realised — so LayoutCanvas
// records every viewport change on its bound view model (LayoutEditorViewModel.LastViewport) and
// restores it when one is re-bound. That is the layout editor's own fix for the owner's 2026-09-04
// report and nothing new is needed here: this object outlives the window's controls and is
// deduplicated per .crail, so a railRF document reopens where it was left exactly as a layout
// document does.

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CircuitRF.Render;
using CircuitRF.Ui.Layout;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.RailRf;

public sealed partial class RailRfViewModel
{
    /// <summary>
    /// The layout the board canvas is bound to — the imported artwork, and nothing railRF added.
    /// </summary>
    /// <remarks>
    /// <b>Built once per board and never written to</b> (R-rail8-2). The overlay draws over this;
    /// nothing railRF computes enters it, so a map repaint is a repaint rather than a rebuild of the
    /// path cache. It is rebuilt only when the BOARD changes, which is what makes the tab strip
    /// switch the overlay and never the geometry (R-rail8-12).
    /// </remarks>
    [ObservableProperty]
    private LayoutEditorViewModel? _boardLayout;

    /// <summary>railRF's maps, drawn through the seam wBond already uses.</summary>
    public RailLayoutOverlay BoardOverlayLayer { get; } = new();

    /// <summary>
    /// How every coordinate and every length on this window is SPELLED — the board's own display
    /// unit, never DBU (owner, 2026-09-18).
    /// </summary>
    /// <remarks>
    /// <b>A function, and asked afresh at every use.</b> The unit belongs to the layout, the layout is
    /// the live one the layout editor is also showing, and its unit picker can change it while this
    /// window is open — so a value captured when a row was built would go on printing the unit the
    /// board used to be in. The same argument the canvas's own <c>NavigationKeysSuppressed</c>
    /// predicate makes: a cached answer is a latch.
    ///
    /// <para>A window with no board answers <see cref="RailLengthFormat.Dbu"/>, which prints the
    /// integer and says "DBU" rather than picking a unit nobody stated.</para>
    /// </remarks>
    public Func<RailLengthFormat> BoardLengthFormat =>
        () => Board?.LengthFormat ?? RailLengthFormat.Dbu;

    /// <summary>
    /// The board netlist's pads, for the anchor field's tooltip on every source and load row.
    /// </summary>
    /// <remarks>
    /// A FUNCTION, like <see cref="BoardLengthFormat"/> and for the same reason: a row outlives the
    /// board being adopted under it (the live-artwork swap, and the open that resolves the netlist
    /// after the rows are built), and a row holding the pad list it was constructed with would go on
    /// offering the pads of a board that is no longer loaded.
    /// </remarks>
    public Func<IReadOnlyList<PlacedPin>> BoardPads =>
        () => Board?.Pads ?? [];

    /// <summary>The unit the last refresh printed in — see <see cref="RefreshIfUnitChanged"/>.</summary>
    private RailLengthFormat _lastLengthFormat = RailLengthFormat.Dbu;

    /// <summary>
    /// Re-states every unit-formatted string when the board's display unit has changed under us.
    /// </summary>
    /// <remarks>
    /// <b>Changing a layout's display unit stays off <c>Changed</c>, on purpose</b> — it is a document
    /// PREFERENCE, not geometry, and it belongs on neither the undo stack nor the notification the
    /// spatial index listens to. It raises <c>LayoutView.DisplayUnitChanged</c> instead, which is what
    /// this window subscribes to, so the change lands while the user is looking at it rather than at
    /// the next activation (owner, 2026-09-19 — two windows side by side is the ordinary case, and
    /// "activate railRF to see the unit you just picked" is not a thing anyone would guess).
    /// Activation still asks, for the window that was not watching this model yet.
    ///
    /// <para><b>Nothing is recomputed.</b> A unit is how a number is spelled, not what it is — so every
    /// result stands, and what happens here is that each string is asked for again.</para>
    /// </remarks>
    public void RefreshIfUnitChanged()
    {
        var now = BoardLengthFormat();
        if (now == _lastLengthFormat) return;
        _lastLengthFormat = now;

        // The board canvas holds its OWN LayoutEditorViewModel over the shared model (see
        // RailRfWindow.LiveArtwork), and that view model captured the unit when it was built. Without
        // this, the panel's own rulers and cursor readout go on reading in the old unit while every
        // row beside them reads in the new one.
        if (BoardLayout is { } canvas && !now.IsRawDbu) canvas.DisplayUnit = now.Unit;

        foreach (var row in Sources) row.NotifyAnchorChanged();
        foreach (var row in Loads)   row.NotifyAnchorChanged();

        RebuildParts();
        OnPropertyChanged(nameof(MeshCellEntry));
        OnPropertyChanged(nameof(PortLines));
        OnPropertyChanged(nameof(BreakdownRows));

        // The frequency half names the same ports — "Against the target" is a port and a verdict, and
        // the port is a coordinate wherever the rail anchors one. Leaving this out is what would make
        // "the units did not update" still true on the Frequency tab after the DC side was fixed.
        //
        // The anti-resonance, coincidence and removal rows are NOT here and it is not an omission:
        // every field in them is a frequency, an impedance or a part refdes, so there is no length in
        // any of them to re-state. The plane MODE rows carry a port name that this cannot re-state —
        // it is the netlist's, spelled when the plane was extracted — and they follow the next Find.
        OnPropertyChanged(nameof(MaskLines));
    }

    /// <summary>The <c>.ctech</c> this board's stackup was read from, or null.</summary>
    public string? TechnologyPath => Board?.TechPath;

    /// <summary>True where there is a technology FILE to open — the button's own visibility.</summary>
    public bool HasTechnologyFile => TechnologyPath is { Length: > 0 };

    /// <summary>The button's tooltip, naming the file it opens.</summary>
    public string EditTechnologyTip =>
        TechnologyPath is { Length: > 0 } p
            ? $"Open {System.IO.Path.GetFileName(p)} — the stackup this board is priced against, and "
            + "where a layer's visibility is set."
            : "This board resolved no .ctech file.";

    /// <summary>What the cursor is over on the board, or null. Published by the overlay; shown on
    /// the strip.</summary>
    [ObservableProperty]
    private string? _boardReadout;

    /// <summary>
    /// Wires the overlay up. Called once, from the constructor.
    /// </summary>
    private void BuildBoardPanel()
    {
        BoardOverlayLayer.ReadoutChanged += r => PostToUi(() => BoardReadout = r);

        // The window prints the empty |Z| tab's sentence itself, beside the Find button that
        // answers it, so the renderer must not centre a second copy under them — the same
        // collision "No board yet" already had with the map note.
        BoardOverlayLayer.EmptyNoteShownByHost = true;

        // R-rail8-11: forcing a region is a DOCUMENT edit and a re-solve, which is why the overlay
        // asks rather than writing. The override is keyed by PdnRegionRef — a drawing layer and a
        // vertex the geometry itself determines — so it survives a re-import, which is exactly when a
        // classification would otherwise silently change.
        BoardOverlayLayer.ForceRegion = (region, forced) =>
        {
            if (forced is { } cls) Document.ClassOverrides[region] = cls;
            else Document.ClassOverrides.Remove(region);

            QueueResolve();
        };

        // §2.3 step 2's SECOND route: the rail is picked by clicking its pour. ARMED BY THE
        // BUTTON and never by a bare click — see SyncPourPick's own note for the report that
        // changed, and for why the gesture is the same gesture and no longer an ambient one.
        SyncPourPick();

        // And an UNARMED click on bare board is how the user says "nothing is selected" — the other
        // half of Escape, wired once because the overlay reports the press and this decides what it
        // meant. See ClearSelectionOnBareBoard.
        BoardOverlayLayer.BackgroundClick = ClearSelectionOnBareBoard;
    }

    /// <summary>
    /// Clears the window's one selection when a left click landed on bare board.
    /// </summary>
    /// <remarks>
    /// <b>Escape's gesture, made with the pointer</b> (owner, 2026-09-21). A net picked under
    /// <i>Pick the rail</i> outlines its copper on the board, and the two ways a user says they are
    /// done looking at it are the key and a click on empty board — the second because that is what
    /// deselects on the layout canvas next door, and §11.6 promises someone who learned that canvas
    /// has learned this one. It runs <see cref="ClearSelectionCommand"/> and not a second clearing of
    /// its own, so the key and the click cannot come to mean different things.
    ///
    /// <para><b>The hit test is the layout editor's own</b> — <see cref="LayoutHitTest.HitStack"/> at
    /// the canvas's own tolerance, which is also what <see cref="TryPickPourAt"/> asks: what counts
    /// as "on something" is one answer, in one place, for both gestures. Anything hit leaves the
    /// selection alone, because a click ON the board is the user pointing at it.</para>
    ///
    /// <para>The armed pour pick never reaches here — the overlay returns before offering the press
    /// — which is deliberate: an armed miss is documented as leaving the gesture armed, and this
    /// would disarm what the user is still aiming.</para>
    /// </remarks>
    private void ClearSelectionOnBareBoard(long xDbu, long yDbu, long tolDbu)
    {
        if (!HasSelection) return;
        if (BoardLayout is not { } canvas) return;
        if (LayoutHitTest.HitStack(canvas.Model, canvas.Technology, xDbu, yDbu, tolDbu).Count > 0) return;

        ClearSelection();
    }

    // ── Click-the-pour is ARMED, and a plain click no longer touches the document ──────────────
    //
    // Owner, 2026-09-20: open the shipped Sensor board, press Run, click anywhere inside the green
    // rectangle, and the window fills with a refusal nothing on screen explains. What the click did
    // was MAKE A RAIL — `rail at (0, 0) µm`, anchored where the pointer was, selected — and that
    // rail states no reference layer, so the gate correctly refused the whole rail set and turned
    // the reference combo orange. Every further click made another one. Escape is wired to the row
    // selections and this is not one; Ctrl+Z did nothing because this window had no undo at all.
    //
    // R-ab2-4c armed the gesture on every board and said outright what it cost: "a left click on a
    // board with a pick list can now make a rail". The cost is not payable. A bare left click is
    // how you look at a board, and a gesture that EDITS THE DOCUMENT cannot be the same gesture —
    // the layout canvas's own marquee, pan and hit test are a left click, and §11.6 promises
    // someone who learned that canvas has learned this one.
    //
    // So the gesture keeps working and stops being ambient: it is ARMED, explicitly, by the button
    // beside the rail selector, and while it is armed the pointer over the board is a CROSSHAIR —
    // the sign every tool uses to say the next click is about to do something. It disarms itself on
    // the pick, on Escape, and on the button. The assisted-Gerber path §2.3 step 2 is about, where
    // no netlist named a net and the click is the only route to a rail, is unchanged apart from the
    // arming press; the sentence advertising it now names the button rather than a bare click.

    /// <summary>
    /// True while the next click on the board's copper will make a rail out of it.
    /// </summary>
    /// <remarks>
    /// <b>Off by default and off again after every pick.</b> One-shot rather than a mode that stays
    /// on: the whole defect this replaces is a document edit arriving from a gesture nobody aimed,
    /// and a tool left armed behind the user is the same defect with one more step in front of it.
    /// </remarks>
    [ObservableProperty]
    private bool _isPickingFromBoard;

    partial void OnIsPickingFromBoardChanged(bool value)
    {
        SyncPourPick();
        OnPropertyChanged(nameof(PickFromBoardText));
        OnPropertyChanged(nameof(PickFromBoardTip));

        // Escape reads HasSelection, and the armed pick is part of it — see that property's note.
        OnPropertyChanged(nameof(HasSelection));
    }

    /// <summary>What the pick-from-board button says — <b>which of its two states it is in</b>, the
    /// same rule <see cref="PickRailButtonText"/> keeps. It is the button's TOOLTIP now that the
    /// button itself is a glyph (owner, 2026-09-20), so it is also the only words there are.</summary>
    public string PickFromBoardText => IsPickingFromBoard ? "Now click the board" : "Pick from board";

    /// <summary>
    /// The pick-from-board button's tooltip, in <b>plain words</b>.
    /// </summary>
    /// <remarks>
    /// <b>It used to say "Arm the board"</b>, which the owner read and asked what it could possibly
    /// mean (2026-09-20) — and they were right: nothing is being armed except an internal flag, and
    /// the user is picking a rail. A tooltip is where somebody goes when they do not already know
    /// what a control does, so it is the last place a word from the implementation belongs.
    ///
    /// <para>Two faces, because the two states ask for different things: before the press it says
    /// what pressing will do, and after it says what to do NEXT — which is the question a user who
    /// has already pressed it actually has.</para>
    /// </remarks>
    public string PickFromBoardTip => IsPickingFromBoard
        ? "Click the copper you want. Everything connected to it — through vias, across layers — "
        + "becomes the rail. Press Escape if you did not mean to."
        : "Pick a rail straight off the board. Press this, then click the copper you want; "
        + "everything connected to it becomes the rail. Use it when the board has no netlist to "
        + "pick a net name from.";

    /// <summary>Arms the pour pick, or disarms it.</summary>
    /// <remarks>
    /// <b>A toggle rather than two commands</b>, because it is one button and the user needs the way
    /// out to be the control they just pressed. Escape is the other way out — see
    /// <c>ClearSelection</c>, which this window's Escape is already wired to.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanPickFromBoard))]
    private void PickFromBoard() => IsPickingFromBoard = !IsPickingFromBoard;

    /// <summary>There has to be copper to click on.</summary>
    public bool CanPickFromBoard => Board is not null;

    /// <summary>Disarms the pour pick. <b>What Escape calls</b>, and what a pick calls on itself.</summary>
    internal void CancelPickFromBoard() => IsPickingFromBoard = false;

    /// <summary>
    /// Arms the pour pick on the overlay while <see cref="IsPickingFromBoard"/> — and only then.
    /// </summary>
    /// <remarks>
    /// <b>The overlay's <c>PourPick</c> is the whole gate.</b> It is null except while the gesture is
    /// armed, and <see cref="RailLayoutOverlay.OnPointerPressed"/> declines the press outright when it
    /// is null — so an unarmed left click is not consumed, and reaches the canvas's own marquee, pan
    /// and hit test exactly as §11.6 requires.
    /// </remarks>
    private void SyncPourPick()
    {
        BoardOverlayLayer.PourPick = Board is not null && IsPickingFromBoard ? TryPickPourAt : null;
        PickFromBoardCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanPickFromBoard));
    }

    /// <summary>
    /// Makes a rail out of the copper under an ARMED click, or declines.
    /// </summary>
    /// <remarks>
    /// <b>The point has to be ON something</b>, and the test is <see cref="LayoutHitTest.HitStack"/>
    /// — the layout editor's own, at the canvas's own tolerance, so what counts as a hit here is what
    /// counts as a hit in the window someone learned the gesture in. Clicking bare substrate makes
    /// no rail: <c>Regions</c> seeds its connectivity walk from the anchor point, and a seed
    /// on no copper walks nothing while looking exactly like a rail that has been created.
    ///
    /// <para><b>A miss leaves the gesture armed</b>, because a miss is a miss — the user aimed at
    /// copper and hit substrate, and disarming would answer that by making them press the button
    /// again. A HIT disarms, so the next click is an ordinary one.</para>
    ///
    /// <para>The rail is NAMED after the place in the board's own display unit, not in DBU. A rail
    /// called <c>rail at (26500000, 9875000)</c> is a name nobody can check against a board, and it
    /// is the one string the rail selector, every refusal about the solve order and the report all
    /// use (<see cref="RailLengthFormat"/>'s own rule).</para>
    /// </remarks>
    private bool TryPickPourAt(long xDbu, long yDbu, long tolDbu)
    {
        if (Board is null || BoardLayout is not { } canvas) return false;

        var hits = LayoutHitTest.HitStack(canvas.Model, canvas.Technology, xDbu, yDbu, tolDbu);
        if (hits.Count == 0) return false;

        CancelPickFromBoard();
        PickRailAt(xDbu, yDbu, $"rail at {BoardLengthFormat().Point(xDbu, yDbu)}");
        return true;
    }

    // ── Placing a port from the board (owner, 2026-09-19) ─────────────────────────────────────

    /// <summary>
    /// The refdes and pin of the pad nearest <paramref name="xDbu"/>/<paramref name="yDbu"/>, or
    /// null where no pad of this board is within <paramref name="tolDbu"/>.
    /// </summary>
    /// <remarks>
    /// <b>The rail's own net first.</b> A pad the anchor names has to be one the extractor can then
    /// resolve on THIS rail, so a pad on the rail's net wins over a nearer one that is not — the
    /// same rule <see cref="RailAnchorEntry.Tip"/> filters its candidate list by, for the same
    /// reason: an anchor the tooltip offers and the solve refuses is worse than no offer.
    ///
    /// <para>Pads with no refdes are skipped. What is being built from this is a REFDES anchor, and
    /// a nameless pad cannot spell one; the coordinate form is the answer there and is always
    /// offered beside it.</para>
    /// </remarks>
    public (string Refdes, string? Pin)? PadAt(long xDbu, long yDbu, long tolDbu)
    {
        string? net = SelectedRail?.NetName;
        double bestD2 = (double)tolDbu * tolDbu;
        (string, string?)? best = null;
        bool bestOnNet = false;

        foreach (var pad in BoardPads())
        {
            if (pad.Refdes is not { Length: > 0 } refdes) continue;

            double dx = pad.X - (double)xDbu, dy = pad.Y - (double)yDbu;
            double d2 = dx * dx + dy * dy;
            if (d2 > bestD2) continue;

            bool onNet = net is { Length: > 0 }
                      && string.Equals(pad.Net, net, StringComparison.OrdinalIgnoreCase);

            // On-net beats nearer; among equals, nearer wins.
            if (best is not null && bestOnNet && !onNet) continue;
            if (best is not null && bestOnNet == onNet && d2 >= bestD2) continue;

            best = (refdes, pad.Pin is { Length: > 0 } ? pad.Pin : null);
            bestOnNet = onNet;
            bestD2 = d2;
        }

        return best;
    }

    /// <summary>Adds a source row on this rail, anchored where the user right-clicked.</summary>
    /// <remarks>
    /// A coordinate anchor records the topmost copper the board view is showing under the click
    /// (R-rail34-2, <see cref="ShownCopperLayerAt"/>), so a point over two nets is the one the user
    /// could see.
    ///
    /// <para>
    /// The same three lines <c>AddSource</c> runs, with the anchor filled in instead of left empty —
    /// so the row is complete the moment it appears and the Fast loop has something to solve. It is
    /// not a second way of making a source: both go through <c>RailSpec.Sources</c> and
    /// <c>RebuildForSelectedRail</c>, which is what keeps the row, the document and the solve one
    /// thing.</para>
    /// </remarks>
    public void PlaceSource(RailPortAnchor anchor)
    {
        if (SelectedRail is not { } rail) return;
        rail.Sources.Add(NewSeededSource(rail, WithShownLayer(anchor)));
        RebuildForSelectedRail();
        QueueResolve();
    }

    /// <summary>Adds a load row on this rail, anchored where the user right-clicked.</summary>
    /// <remarks><b>Carrying a starting current</b>, exactly as <c>AddLoad</c> does — so a part
    /// dropped on the board makes the rail draw something and the picture is not a field of zeros.
    /// The observation port is one cleared cell away, and <c>RailRfViewModel.Seeds.cs</c> carries the
    /// argument. A coordinate records its copper as <see cref="PlaceSource"/>'s does.</remarks>
    public void PlaceLoad(RailPortAnchor anchor)
    {
        if (SelectedRail is not { } rail) return;
        rail.Loads.Add(NewSeededLoad(WithShownLayer(anchor)));
        RebuildForSelectedRail();
        QueueResolve();
    }

    /// <summary>
    /// Rebuilds the board layout from the imported artwork.
    /// </summary>
    /// <remarks>
    /// <b>A fresh <see cref="LayoutEditorViewModel"/> per board, deliberately.</b> Re-using one across
    /// two boards would carry the first board's remembered viewport onto the second, which frames the
    /// new artwork at the old one's pan and zoom — a wrong picture that looks like a right one.
    /// </remarks>
    private void RebuildBoardLayout()
    {
        if (Board is not { } board)
        {
            BoardLayout = null;
            BoardOverlayLayer.Result = null;
            return;
        }

        // THE DOCUMENT'S OWN LayoutView WHERE THERE IS ONE, so the panel is a live view of the
        // `.clay` rather than a snapshot of it (R-rail19-1). A second LayoutEditorViewModel over one
        // model is exactly right here: pan, zoom and selection are the VIEW MODEL's, so the two
        // windows keep their own, while the GEOMETRY is one object and an edit in the layout editor
        // is an edit to what this draws.
        //
        // The fallback builds the snapshot this always built — an import into a throwaway directory
        // has no document to be live against, and `Shapes` is the artwork either way.
        var view = board.View;
        if (view is null)
        {
            view = new LayoutView
            {
                DbuPerMicron = board.DbuPerMicron,

                // The TECHNOLOGY's unit, not a constant. This path has no document to take one from,
                // and µm on a board whose stackup is stated in mils is a picture whose rulers disagree
                // with every other reading of the same artwork.
                DisplayUnit  = board.Technology.DefaultDisplayUnit,
            };
            foreach (var shape in board.Shapes) view.Shapes.Add(shape);
        }

        BoardLayout = new LayoutEditorViewModel(view)
        {
            Technology = board.Technology,

            // ── AND IT HAS TO KNOW WHERE THE `.clay` IS ────────────────────────────────────────
            //
            // A relative `CellRef` resolves against the directory holding the `.clay`, and a view
            // model that has never been told that directory resolves NONE of them
            // (`InstanceBaseDir`, which is derived from this one property and is "" without it). So
            // every part on the board drew as the broken-reference placeholder: a warning-coloured
            // box at the 28-pixel screen floor, in place of a land pattern, on a board where the
            // layout editor next door drew the same instances perfectly.
            //
            // Reported from outside on a real imported board, 2026-09-21 — and it was invisible for
            // as long as boards' parts were bare copper, because a board with no instances on it has
            // nothing that needs resolving. The `.crail` example's own board only grew instances
            // when its parts became footprints.
            //
            // It is the ADDRESS and not an invitation to write: the canvas is ReadOnly (R-rail19-1's
            // other half), so no save path on this view model is reachable from this window.
            CurrentLayoutPath = board.ArtworkCellRef,
        };
        BoardOverlayLayer.DbuPerMicron = board.DbuPerMicron;

        // The rows FIRST, because they are built from the technology, and the narrowing second,
        // because it is built from the rows' own hidden set.
        RebuildBoardLayers();
        SyncCanvasTechnology();
        SyncHiddenLayers();
    }

    /// <summary>
    /// The artwork this window is watching CHANGED under it — somebody edited the <c>.clay</c>.
    /// </summary>
    /// <remarks>
    /// <b>The viewport is deliberately not rebuilt.</b> The user is looking at the board while they
    /// edit it in the other window, so re-fitting under them would be the tool taking the view away
    /// at the moment they are using it. The model is the same object; only its contents moved.
    ///
    /// <para><b>And the RESULT goes.</b> Every number railRF is showing was computed against the
    /// copper that has just changed, and a drop in millivolts sitting beside artwork it was not
    /// measured on is the one outcome this window's whole status strip exists to prevent. Cleared
    /// rather than recomputed: a re-solve per edit would run the extraction on a board mid-edit, and
    /// <c>Run</c> is one keystroke away.</para>
    ///
    /// <para><b>And the PADS follow, a moment later</b> (brief 28). An edit that can move a pin
    /// schedules one debounced re-read off the UI thread — see <c>RailRfViewModel.PadRead.cs</c> for
    /// which <paramref name="change"/> kinds count and why a shape edit does not.</para>
    /// </remarks>
    /// <param name="change">What the layout said changed; null is <see cref="LayoutChangeInfo.Full"/>,
    /// as it is on <see cref="LayoutView.NotifyChanged"/>.</param>
    public void NotifyArtworkChanged(LayoutChangeInfo? change = null)
    {
        if (Board is not { View: { } live } board) return;

        // Decided BEFORE anything is cleared, because the invalidation below drops the selection
        // and a pad refresh has to be able to hand it back (R-rail28-2). The first edit of a burst
        // is the one that saw the user's pick.
        bool pinsMayHaveMoved = PinsMayHaveMoved(live, change);
        if (pinsMayHaveMoved && !IsReadingParts) _netAcrossPadRead = SelectedNet?.Name;

        // NOT `Board = board with { … }`. That would raise OnBoardChanged, which rebuilds the
        // LayoutEditorViewModel and with it the viewport — the very thing the paragraph above says
        // must not happen.
        //
        // ON A FLAT BOARD NOTHING NEEDS RE-STATING: `Shapes` is the document's own list, the same
        // object the edit mutated in place, so what the extraction reads is already current.
        //
        // ON A BOARD WHOSE PARTS ARE INSTANCES IT IS NOT (brief-footprint-3). There `Shapes` is a
        // FLATTENED list — clones, in the root's frame — so an edit next door leaves it describing
        // the board as it was, and the re-run this method invites would answer for the old copper
        // with nothing to say so. Re-flattened here, through the backing field for AdoptTechnology's
        // reason, and only where there is an instance to flatten: with none, FlattenedShapes hands
        // back `live.Shapes` itself and the identity above is preserved exactly.
        //
        // AND WHERE THE LAST INSTANCE HAS JUST GONE: the held list is still the old flatten, with
        // that part's lands in it, and only a re-flatten hands back the live list again.
        if (live.Instances.Count > 0 || !ReferenceEquals(board.Shapes, live.Shapes))
        {
#pragma warning disable MVVMTK0034
            _board = board with
            {
                Shapes = RailArtwork.FlattenedShapes(live, board.ArtworkCellRef, board.Technology),
            };
#pragma warning restore MVVMTK0034
        }

        ClearResults();
        InvalidateNetWalks();       // the copper moved, so every walk taken off it is of a board that is gone

        // ── AND WHAT THE BOARD SAYS ABOUT ITS PARTS (owner report, 2026-09-22) ────────────────
        //
        // An edit next door can PLACE one. Only the shapes were re-read here, so the two maps this
        // window keeps by designator — which land pattern a part sits on, and WHERE it is — went on
        // describing the board as it was: a footprint dropped into the layout while railRF was open
        // never appeared in the parts table's footprint or where columns at all, and no amount of
        // looking at railRF would show it until the document was closed and reopened.
        //
        // Neither call touches the canvas, which is the constraint the paragraph above sets:
        // RebuildBoardFootprints walks the instances, and RebuildParts rebuilds the ROWS. The
        // LayoutEditorViewModel and its viewport are untouched, so the user keeps the pan and zoom
        // they were looking at.
        RebuildBoardFootprints();
        RebuildParts();

        // The pads, net points and turned-parts reading, once the burst settles.
        if (pinsMayHaveMoved) SchedulePadRead();

        SyncBoardOverlayResult();
        RefreshRunGate();
        OnPropertyChanged(nameof(StatusLine));
    }

    /// <summary>
    /// The artwork's technology has been re-resolved elsewhere — adopt it.
    /// </summary>
    /// <remarks>
    /// <b>Why this exists</b> (owner, 2026-09-19): a layer switched to <c>Vis</c> off in the
    /// <c>.ctech</c> went on being drawn on railRF's board. The layout editor follows a live
    /// technology edit because the workspace pushes the re-resolved instance into every open layout
    /// document; this window held the instance it resolved once, when it opened the board, and nothing
    /// ever replaced it. The drawing is only half of it — the stackup is what the copper is PRICED
    /// against, so a window holding the old one would also solve against it.
    ///
    /// <para><b>The results go, for <see cref="NotifyArtworkChanged"/>'s reason.</b> Every number here
    /// was computed against the stackup that has just been re-read, and railRF cannot tell a
    /// visibility toggle from a copper thickness from the outside — so it says the numbers are of the
    /// old one rather than leaving them sitting beside a board they may no longer describe. Re-running
    /// is one keystroke.</para>
    ///
    /// <para><b>The reference test is kept, and the live seam is what makes it safe</b>
    /// (R-rail20-2b). Getting this wrong produces "the first toggle works and the rest do not",
    /// which is far harder to diagnose than "none of them work" — so it is written down here rather
    /// than left to be inferred. An unsaved <c>.ctech</c> edit reaches this window as a FRESH
    /// instance every time: <c>TechEditorViewModel.ApplySnapshot</c> deserialises a new clone per
    /// committed edit, undo and redo, and <c>TechnologyCache.SetLive</c> stores that clone. Nothing
    /// hands out the editor's own <c>Working</c> object, which is the thing that would make the
    /// second toggle a no-op.</para>
    ///
    /// <para><b>The BOARD is written to its backing field on purpose</b>, exactly as
    /// <see cref="NotifyArtworkChanged"/> explains: assigning the property would rebuild the
    /// <c>LayoutEditorViewModel</c> and take the viewport away from a user who is looking at it.</para>
    /// </remarks>
    public void AdoptTechnology(Technology technology)
    {
        ArgumentNullException.ThrowIfNull(technology);
        if (Board is not { } board || ReferenceEquals(board.Technology, technology)) return;

        bool physicsMoved = StackupSignature(board.Technology) != StackupSignature(technology);

        // The generator forbids touching its field (MVVMTK0034) and that rule is right nearly
        // everywhere; here the whole point is to change the value WITHOUT the notification, because
        // the notification is what rebuilds the canvas. Suppressed at the one line rather than argued
        // around with a second board property nothing else would use.
#pragma warning disable MVVMTK0034
        _board = board with { Technology = technology };
#pragma warning restore MVVMTK0034

        // The canvas reads its technology from this view model every frame and repaints on any of its
        // property changes, so this one assignment is both halves: the new layer table and the frame
        // that draws with it.
        // The rows are seeded from the new layer table; the canvas is then given whatever that
        // leaves visible (SyncCanvasTechnology hands back the adopted instance itself where this
        // window hides nothing, so the ordinary case costs no clone).
        RebuildBoardLayers();
        SyncCanvasTechnology();
        SyncHiddenLayers();

        // The stackup is what says which layers a via joins, so every galvanic walk is of the old
        // technology's connectivity — including the measured reference return.
        InvalidateNetWalks();

        if (!physicsMoved) return;

        ClearResults();
        SyncBoardOverlayResult();
        RefreshRunGate();
        OnPropertyChanged(nameof(StatusLine));
    }

    /// <summary>
    /// Tells the overlay which drawing layers the technology is not drawing.
    /// </summary>
    /// <remarks>
    /// The maps are laid OVER the artwork, so a layer the renderer skips has to take its shading with
    /// it — otherwise turning a layer off in the <c>.ctech</c> removes the copper and leaves the drop
    /// map of it floating on the board (owner, 2026-09-19).
    /// </remarks>
    /// <remarks>
    /// <b>And the WINDOW's own hidden layers are unioned in here</b> (R-rail20-1c), which is the
    /// whole of "there is exactly one hidden-layer set reaching the overlay, not two". railRF's own
    /// layer list and the technology's <c>Vis</c> boxes answer the same question, so they arrive at
    /// the overlay as one answer — see <c>RailRfViewModel.Layers.cs</c>.
    /// </remarks>
    private void SyncHiddenLayers()
    {
        var hidden = new HashSet<LayerKey>(WindowHiddenLayers);
        if (Board?.Technology is { } tech)
            foreach (var layer in tech.Layers)
                if (!layer.Visible) hidden.Add(layer.Key);

        BoardOverlayLayer.HiddenLayers = hidden;
    }

    /// <summary>
    /// The part of a technology the SOLVE reads — its stackup, as text.
    /// </summary>
    /// <remarks>
    /// <b>Why there is a signature at all.</b> Adopting a re-resolved technology has to invalidate the
    /// numbers when the stackup moved and must NOT when it did not: a user turning a drawing layer's
    /// visibility off is asking a question about the PICTURE, and losing the answer they just ran for
    /// it would make the toggle cost a re-solve (owner, 2026-09-19 — that toggle is the workflow this
    /// whole adoption exists for). Thicknesses, conductivities, dielectrics and the drawing layers each
    /// stackup entry claims all live under <see cref="Technology.Stackup"/>; visibility, colour and
    /// fill pattern live on the drawing-layer table beside it and are not here.
    ///
    /// <para><b>Serialised rather than compared field by field</b>, deliberately: a stackup field added
    /// later is in the comparison the day it is added, where a hand-written list of properties would
    /// silently keep saying "unchanged" about it.</para>
    /// </remarks>
    private static string StackupSignature(Technology technology) =>
        System.Text.Json.JsonSerializer.Serialize(technology.Stackup);

    /// <summary>Which map the overlay draws, from which tab the strip is on.</summary>
    /// <remarks>
    /// A mapping and not a second enum: <see cref="RailBoardOverlay"/> is what the window's strip is
    /// declared in and <see cref="RailMapKind"/> is what the renderer below the firewall takes, and
    /// neither project may reference the other's.
    /// </remarks>
    internal static RailMapKind MapKindOf(RailBoardOverlay overlay) => overlay switch
    {
        RailBoardOverlay.Drop      => RailMapKind.Drop,
        RailBoardOverlay.Impedance => RailMapKind.Impedance,
        RailBoardOverlay.Class     => RailMapKind.Class,
        _                          => RailMapKind.Copper,
    };

    partial void OnSelectedBoardOverlayChanged(RailBoardOverlay value)
    {
        BoardOverlayLayer.Kind = MapKindOf(value);
        AnnounceImpedanceMap();
    }

    /// <summary>Hands the overlay the rail the window is showing. Called whenever the result or the
    /// selected rail changes.</summary>
    private void SyncBoardOverlayResult() => BoardOverlayLayer.Result = SelectedRailResult;
}
