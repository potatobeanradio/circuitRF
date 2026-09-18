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

        var view = new LayoutView
        {
            DbuPerMicron = board.DbuPerMicron,
            DisplayUnit  = LayoutUnit.Um,
        };
        foreach (var shape in board.Shapes) view.Shapes.Add(shape);

        BoardLayout = new LayoutEditorViewModel(view) { Technology = board.Technology };
        BoardOverlayLayer.DbuPerMicron = board.DbuPerMicron;
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
