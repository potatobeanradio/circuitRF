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
using System.Linq;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CircuitRF.Render;
using CircuitRF.Ui.Layout;
using CommunityToolkit.Mvvm.ComponentModel;

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
        OnPropertyChanged(nameof(BreakdownLines));

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

        BoardLayout = new LayoutEditorViewModel(view) { Technology = board.Technology };
        BoardOverlayLayer.DbuPerMicron = board.DbuPerMicron;
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
    /// </remarks>
    public void NotifyArtworkChanged()
    {
        if (Board is not { View: not null }) return;

        // NOT `Board = board with { … }`. That would raise OnBoardChanged, which rebuilds the
        // LayoutEditorViewModel and with it the viewport — the very thing the paragraph above says
        // must not happen. Nothing needs re-stating in any case: `Shapes` on the live path IS the
        // document's own list, the same object the edit mutated in place, so what the extraction
        // reads is already current.
        ClearResults();
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
        if (BoardLayout is { } canvas) canvas.Technology = technology;
        SyncHiddenLayers();

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
    private void SyncHiddenLayers() =>
        BoardOverlayLayer.HiddenLayers = Board?.Technology is { } tech
            ? new HashSet<LayerKey>(tech.Layers.Where(l => !l.Visible).Select(l => l.Key))
            : new HashSet<LayerKey>();

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

    partial void OnSelectedBoardOverlayChanged(RailBoardOverlay value) =>
        BoardOverlayLayer.Kind = MapKindOf(value);

    /// <summary>Hands the overlay the rail the window is showing. Called whenever the result or the
    /// selected rail changes.</summary>
    private void SyncBoardOverlayResult() => BoardOverlayLayer.Result = SelectedRailResult;
}
