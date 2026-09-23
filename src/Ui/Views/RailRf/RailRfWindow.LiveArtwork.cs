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
using CircuitRF.Design.RailRf;
using CircuitRF.Ui.RailRf;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Views.RailRf;

public partial class RailRfWindow
{
    /// <summary>The model this window is currently subscribed to, or null.</summary>
    private LayoutView? _watchedArtwork;

    private void WireLiveArtwork()
    {
        Opened += (_, _) => WatchWorkspaceTechnology();

        Activated += (_, _) =>
        {
            WatchWorkspaceTechnology();
            AdoptLiveArtwork();

            // …and the board's DISPLAY UNIT. OnArtworkUnitChanged covers the unit moving while this
            // window is watching; this covers the window that was not watching yet — one opened
            // before the `.clay`, or one whose model has just been swapped by the adoption above.
            Vm?.RefreshIfUnitChanged();
        };
        Closed += (_, _) =>
        {
            WatchArtwork(null);
            WatchWorkspace(null);
        };
    }

    /// <summary>
    /// Binds the board panel to the shared session's model for this document's artwork, where one is
    /// open, and keeps watching whatever it ends up on.
    /// </summary>
    private void AdoptLiveArtwork()
    {
        if (Vm is not { Board: { } board } vm) { WatchArtwork(null); return; }

        if (board.ArtworkCellRef is { Length: > 0 } clay)
        {
            // Whichever open workspace holds a session for it — the Turn gesture looks the same way.
            var live = WorkspaceLocator.AllWindows()
                .Select(w => (w.DataContext as ViewModels.WorkspaceViewModel)?.LiveLayoutModel(clay))
                .FirstOrDefault(m => m is not null);

            if (live is not null && !ReferenceEquals(board.View, live))
            {
                // A DIFFERENT object, so the picture has to be rebuilt around it — viewport included,
                // since this is a one-off swap onto the real document rather than an edit to what is
                // already shown. Shapes comes along because that is what the extraction reads.
                //
                // FLATTENED, and this line took `live.Shapes` until a board's parts became INSTANCES
                // (brief-footprint-3). The two open paths already flattened; only this one did not,
                // so a board whose lands are all inside footprint cells lost every one of them the
                // moment its layout document was opened beside the railRF window — the run completed,
                // the answer was of the rail's bare copper, and nothing said so. That is
                // FlattenedShapes' own stated failure, arrived at from the one direction it was not
                // wired into. On a board with no instances it hands back `live.Shapes` itself, so the
                // live-list identity NotifyArtworkChanged relies on is unchanged there.
                // No notes are collected: this window already reported the flatten's own sentences
                // when it opened this very `.clay`, and this runs on every Activated — a note posted
                // here would be the same sentence again each time the user came back to the window.
                vm.AdoptLiveView(live, RailArtwork.FlattenedShapes(live, clay, board.Technology));
            }
        }

        // OUTSIDE the ArtworkCellRef gate: a board that names no workspace cell can still have
        // resolved a `.ctech`, and the by-path route below needs neither a cell nor a session.
        AdoptLiveTechnology();

        WatchArtwork(vm.Board?.View);
    }

    /// <summary>
    /// Takes the technology the WORKSPACE is currently resolving this board's artwork to.
    /// </summary>
    /// <remarks>
    /// <b>The workspace's own resolution, not a re-read of the file</b>: a live <c>.ctech</c> edit is
    /// not on disk yet — the technology editor pushes its working copy into the workspace's cache and
    /// that is what every open layout document is drawing with. A window that went to the file would
    /// draw a different technology from the one beside it, which is worse than not following at all.
    ///
    /// <para>The open session's instance is preferred where there is one, so this window and the
    /// layout editor hold the SAME object and repeated asking is a no-op rather than a churn of equal
    /// copies.</para>
    ///
    /// <para><b>AND THERE IS USUALLY NO SESSION</b> (R-rail20-2, a first-time designer's report,
    /// 2026-09-20: a <c>Vis</c> toggle "only works after saving the file and then closing railRF and
    /// reopening"). Both routes above go through the <c>.clay</c>: the first needs a layout SESSION,
    /// which exists only while somebody has that file open in its own window, and the second needs
    /// <c>board.View</c>, which the OPEN path does not set at all — only the live-artwork swap does,
    /// and that swap is itself conditional on a session. So the pair of windows the layer question
    /// actually sends a user to — railRF and the technology editor — reached neither, and nothing
    /// arrived until a save had been made AND this window had been reopened. The third route is the
    /// one that needs no session: this board already resolved a <c>.ctech</c> PATH, and the
    /// workspace's own cache is where the unsaved edit lives.</para>
    ///
    /// <para><b>Third and not first</b>, because the two above are more specific: they answer with
    /// the instance the layout editor is drawing with, so the two windows hold one object and asking
    /// again costs nothing. This one answers by path, which is right for the technology and says
    /// nothing about which layout resolved it.</para>
    /// </remarks>
    private void AdoptLiveTechnology()
    {
        if (Vm is not { Board: { } board } vm) return;
        if (WorkspaceLocator.Any() is not { } workspace) return;

        Technology? tech = null;

        if (board.ArtworkCellRef is { Length: > 0 } clay)
        {
            // The second is used only where this window holds the `.clay`'s own model, because the
            // resolution needs that file's TechRef: resolving with a null one would walk up to the
            // WORKSPACE DEFAULT, which is a different technology than the board names and would be
            // adopted without anything saying so.
            tech = workspace.LiveLayoutTechnology(clay)
                ?? (board.View is { } view ? workspace.ResolveTechnologyForLayout(clay, view.TechRef) : null);
        }

        // By PATH — the board's own resolved `.ctech`, which needs no layout session and no model.
        tech ??= board.TechPath is { Length: > 0 } techPath ? workspace.TechnologyAt(techPath) : null;

        // Where none of the three answers, what is already held stands.
        if (tech is not null) vm.AdoptTechnology(tech);
    }

    private WorkspaceViewModel? _watchedWorkspace;

    /// <summary>
    /// Subscribes to the workspace's technology seam, so a <c>.ctech</c> edit lands HERE while this
    /// window is on screen rather than at whatever later moment it happens to be activated.
    /// </summary>
    /// <remarks>
    /// Re-asked on activation as well as at open, for <c>AdoptLiveArtwork</c>'s own reason: a railRF
    /// window can outlive one workspace window and come back to another, and there is no "a workspace
    /// appeared" signal to invent, maintain and tear down.
    /// </remarks>
    private void WatchWorkspaceTechnology() => WatchWorkspace(WorkspaceLocator.Any());

    private void WatchWorkspace(WorkspaceViewModel? workspace)
    {
        if (ReferenceEquals(_watchedWorkspace, workspace)) return;

        if (_watchedWorkspace is not null)
            _watchedWorkspace.TechnologyReResolved -= OnWorkspaceTechnologyReResolved;

        _watchedWorkspace = workspace;

        if (_watchedWorkspace is not null)
            _watchedWorkspace.TechnologyReResolved += OnWorkspaceTechnologyReResolved;
    }

    /// <summary>A technology was re-read somewhere in the workspace — take it if it is ours.</summary>
    /// <remarks>
    /// The path is not matched here: this window knows which technology it is using only through the
    /// same resolution <see cref="AdoptLiveTechnology"/> performs, and that call is a dictionary lookup
    /// which returns the instance already held when nothing about this board changed —
    /// <c>AdoptTechnology</c> then returns immediately. Matching the path first would mean keeping a
    /// second copy of the resolution rule here, which is the copy that goes stale.
    /// </remarks>
    private void OnWorkspaceTechnologyReResolved(string changedPath) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(AdoptLiveTechnology);

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

        if (_watchedArtwork is not null)
        {
            _watchedArtwork.Changed -= OnArtworkChanged;
            _watchedArtwork.DisplayUnitChanged -= OnArtworkUnitChanged;
        }

        _watchedArtwork = view;

        if (_watchedArtwork is not null)
        {
            _watchedArtwork.Changed += OnArtworkChanged;
            _watchedArtwork.DisplayUnitChanged += OnArtworkUnitChanged;
        }
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
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Vm?.NotifyArtworkChanged(e));

    /// <summary>
    /// The board's display unit changed in the other window — <b>while this one is on screen</b>.
    /// </summary>
    /// <remarks>
    /// <b>The activation refresh below is not enough, and the owner's report is why</b> (2026-09-19):
    /// two windows side by side, the unit picker moved in the layout editor, and railRF goes on
    /// printing the unit the board used to be in until something happens to activate it. Nothing
    /// re-states it because a unit raised no notification of any kind — which was defensible while it
    /// was private to one document window and is not once a second window draws the same model.
    ///
    /// <para>Nothing is recomputed: a unit is how a number is SPELLED and not what it is, so every
    /// result stands and only the strings are re-stated. That is the difference between this and
    /// <see cref="OnArtworkChanged"/>, which throws the numbers away because the copper moved.</para>
    /// </remarks>
    private void OnArtworkUnitChanged(object? sender, EventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Vm?.RefreshIfUnitChanged());
}
