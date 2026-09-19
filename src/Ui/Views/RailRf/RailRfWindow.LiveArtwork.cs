// The board panel as a LIVE VIEW of the `.clay`, not a snapshot of it (owner, 2026-09-18).
//
// ── WHAT WAS WRONG ──────────────────────────────────────────────────────────────────────────────
//
// Reported on the shipped Power Rail example, with both the `.clay` and the `.crail` open: an edit
// made to the layout in its own document window changed nothing in railRF, where the board shown is
// meant to be a live view of the `.clay` itself.
//
// It was a snapshot: RebuildBoardLayout copied the shapes into a LayoutView of its own and bound the
// canvas to that. Which was worse than merely stale — LayoutView.Shapes is a list of REFERENCES, so
// the copy shared every LayoutShape object with the real document, and railRF's own canvas was
// editable. A primitive dragged in this window mutated the layout session's model from outside its
// command stack: nothing marked it dirty, nothing could undo it, and the next save wrote it. That
// half is fixed by LayoutCanvas.ReadOnly; this file is the other half.
//
// ── THE MECHANISM IS THE ONE HIERARCHY ALREADY USES ─────────────────────────────────────────────
//
// One LayoutEditorViewModel per `.clay` path, in the workspace's session registry, is what makes a
// pushed-in sub-cell show its parent's edits. railRF binds a SECOND LayoutEditorViewModel over that
// same LayoutView: pan, zoom and selection belong to the view model, so the two windows keep their
// own; the geometry is one object, so an edit in the layout editor IS an edit to what this draws.
//
// ── WHEN IT ADOPTS ──────────────────────────────────────────────────────────────────────────────
//
// On open, and on every activation. The second is what covers the order the owner did not hit: railRF
// opened FIRST holds its own copy until something opens that `.clay`, and the moment the user comes
// back to this window it adopts the shared model and is live from then on. Asking on activation costs
// a dictionary lookup and needs no event from the workspace for "a session appeared" — which is a
// signal that does not exist and would have to be invented, maintained and torn down.

using System;
using CircuitRF.Design.Layout;
using CircuitRF.Ui.RailRf;

namespace CircuitRF.Ui.Views.RailRf;

public partial class RailRfWindow
{
    /// <summary>The model this window is currently subscribed to, or null.</summary>
    private LayoutView? _watchedArtwork;

    private void WireLiveArtwork()
    {
        Activated += (_, _) =>
        {
            AdoptLiveArtwork();

            // …and the board's DISPLAY UNIT, which can have changed in the layout editor while this
            // window was behind it. It raises no change event of its own — see
            // RailRfViewModel.RefreshIfUnitChanged for why, and why activation is the right moment.
            Vm?.RefreshIfUnitChanged();
        };
        Closed += (_, _) => WatchArtwork(null);
    }

    /// <summary>
    /// Binds the board panel to the shared session's model for this document's artwork, where one is
    /// open, and keeps watching whatever it ends up on.
    /// </summary>
    private void AdoptLiveArtwork()
    {
        if (Vm is not { Board: { } board } vm) { WatchArtwork(null); return; }

        if (board.ArtworkCellRef is { Length: > 0 } clay
            && WorkspaceLocator.Any()?.LiveLayoutModel(clay) is { } live
            && !ReferenceEquals(board.View, live))
        {
            // A DIFFERENT object, so the picture has to be rebuilt around it — viewport included,
            // since this is a one-off swap onto the real document rather than an edit to what is
            // already shown. Shapes comes along because that is what the extraction reads.
            vm.Board = board with { View = live, Shapes = live.Shapes };
        }

        WatchArtwork(vm.Board?.View);
    }

    /// <summary>
    /// Subscribes to one model's <c>Changed</c> and drops the previous subscription.
    /// </summary>
    /// <remarks>
    /// <b>Both halves, every time.</b> A <c>LayoutView</c> outlives this window — it belongs to the
    /// layout session — so a handler left on it keeps a closed railRF window alive and repainting a
    /// canvas nobody is looking at. Same shape as this window's <c>ThemeService.ThemeChanged</c>
    /// handling next door, and for the same reason.
    /// </remarks>
    private void WatchArtwork(LayoutView? view)
    {
        if (ReferenceEquals(_watchedArtwork, view)) return;

        if (_watchedArtwork is not null) _watchedArtwork.Changed -= OnArtworkChanged;
        _watchedArtwork = view;
        if (_watchedArtwork is not null) _watchedArtwork.Changed += OnArtworkChanged;
    }

    /// <summary>
    /// The artwork moved under us — so the numbers computed against the copper that has just changed
    /// have to go.
    /// </summary>
    /// <remarks>
    /// <b>Nothing here repaints, on purpose.</b> <c>LayoutCanvas</c> subscribes to its bound model's
    /// <c>Changed</c> itself and already patches its path and tile caches from the
    /// <see cref="LayoutChangeInfo"/> before invalidating — which is exactly the incremental update a
    /// second repaint from here would throw away. Being bound to the live model IS the repaint; this
    /// handler exists only for the half the canvas cannot know about, which is that a DC result is now
    /// about a board that no longer exists.
    /// </remarks>
    private void OnArtworkChanged(object? sender, LayoutChangeInfo e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Vm?.NotifyArtworkChanged());
}
