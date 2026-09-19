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
    /// <b>Changing a layout's display unit raises no <c>Changed</c> event, on purpose</b> — it is a
    /// document PREFERENCE, not geometry, and the layout editor deliberately keeps it off the undo
    /// stack and out of the change notification the spatial index listens to. So there is nothing to
    /// subscribe to, and the window asks on ACTIVATION instead, which is the moment a user who just
    /// changed the unit in the other window comes back to look at this one.
    /// </remarks>
    public void RefreshIfUnitChanged()
    {
        var now = BoardLengthFormat();
        if (now == _lastLengthFormat) return;
        _lastLengthFormat = now;

        foreach (var row in Sources) row.NotifyAnchorChanged();
        foreach (var row in Loads)   row.NotifyAnchorChanged();

        RebuildParts();
        OnPropertyChanged(nameof(MeshCellEntry));
        OnPropertyChanged(nameof(PortLines));
        OnPropertyChanged(nameof(BreakdownLines));
    }

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
                DisplayUnit  = LayoutUnit.Um,
            };
            foreach (var shape in board.Shapes) view.Shapes.Add(shape);
        }

        BoardLayout = new LayoutEditorViewModel(view) { Technology = board.Technology };
        BoardOverlayLayer.DbuPerMicron = board.DbuPerMicron;
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
