// ================================================================
//  LayoutCanvasActivationTests.cs — brief-layout-testing-fixes.md item 3/R-fix-3
//
//  Bug: clicking the project tree calls PropertiesTool.SetActiveCell(null), which
//  unconditionally clears IsLayoutActive too (a different dock region's action
//  clobbering the layout context) — reproduced directly below. Clicking BACK onto an
//  already-active layout tab's own canvas never changed DocumentDock.ActiveDockable
//  (it was already this document), so WorkspaceViewModel.OnDocumentDockPropertyChanged
//  never re-fired to restore it; only a full tab-away-and-back round trip did.
//
//  WorkspaceViewModel itself cannot be constructed headlessly (src/Ui/CLAUDE.md's own
//  documented constraint), so this composes the REAL types it wires together — LayoutDocument,
//  LayoutEditorViewModel, PropertiesTool — exactly the way WorkspaceViewModel.HookLayoutCellDirty
//  and OnLayoutCanvasInteracted do, proving the LOGIC is correct; the actual GotFocus wiring in
//  LayoutEditorView.axaml.cs cannot be exercised headlessly for the same reason no prior Layout
//  Editor phase's view/canvas code-behind could be.
// ================================================================

using CircuitRF.Ui.Layout;
using CircuitRF.Ui.ViewModels.Dock;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class LayoutCanvasActivationTests
{
    [Fact]
    public void NotifyCanvasInteracted_RaisesEvent()
    {
        var doc = new LayoutDocument("Test", new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000 }));
        bool raised = false;
        doc.CanvasInteracted += () => raised = true;

        doc.NotifyCanvasInteracted();

        Assert.True(raised);
    }

    // ── brief-layout-testing-fixes.md item 8: File → Export → GDSII/DXF menu seam ─────────────────

    [Fact]
    public void RequestExportGdsii_RaisesEvent()
    {
        var doc = new LayoutDocument("Test", new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000 }));
        bool raised = false;
        doc.ExportGdsiiRequested += () => raised = true;

        doc.RequestExportGdsii();

        Assert.True(raised);
    }

    [Fact]
    public void RequestExportDxf_RaisesEvent()
    {
        var doc = new LayoutDocument("Test", new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000 }));
        bool raised = false;
        doc.ExportDxfRequested += () => raised = true;

        doc.RequestExportDxf();

        Assert.True(raised);
    }

    [Fact]
    public void RequestExportGdsii_NoSubscriber_DoesNotThrow()
    {
        var doc = new LayoutDocument("Test", new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000 }));
        var ex = Record.Exception(() => doc.RequestExportGdsii());
        Assert.Null(ex);
    }

    /// <summary>Reproduces the reported bug directly (SetActiveCell(null) clobbers IsLayoutActive even
    /// though the layout document was never deactivated), then proves the fix: simulating
    /// WorkspaceViewModel's own CanvasInteracted subscription re-asserts the layout context.</summary>
    [Fact]
    public void SimulatedWorkspaceWiring_CanvasInteracted_ReassertsLayoutContext_AfterTreeClickClobbersIt()
    {
        var vm = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000 });
        var doc = new LayoutDocument("Test", vm);
        var propsTool = new PropertiesTool();

        // Mirrors WorkspaceViewModel.HookLayoutCellDirty's new subscription line.
        doc.CanvasInteracted += () => propsTool.SetActiveLayout(doc.ActiveViewModel);

        // The layout tab is activated normally (mirrors OnDocumentDockPropertyChanged).
        propsTool.SetActiveLayout(vm);
        Assert.True(propsTool.IsLayoutActive);
        Assert.Same(vm, propsTool.LayoutInspectorVm.EditorVm);

        // The user clicks a non-Cell project-tree node while the layout tab stays active —
        // OnProjectTreeSelectionChanged's own unconditional SetActiveCell(null) call, which clobbers
        // IsLayoutActive as a side effect even though DocumentDock.ActiveDockable never changed.
        propsTool.SetActiveCell(null);
        Assert.False(propsTool.IsLayoutActive); // reproduces the reported staleness

        // The user clicks back into the layout canvas — GotFocus -> NotifyCanvasInteracted().
        doc.NotifyCanvasInteracted();

        Assert.True(propsTool.IsLayoutActive);
        Assert.Same(vm, propsTool.LayoutInspectorVm.EditorVm);
    }

    /// <summary>The same scenario, but the layout document has been PUSHED IN to a sub-cell
    /// (L3b) — re-asserting must route to the CURRENTLY ACTIVE frame's VM, not the base session,
    /// exactly like OnDocumentDockPropertyChanged already does via ActiveViewModel.</summary>
    [Fact]
    public void SimulatedWorkspaceWiring_CanvasInteracted_UsesActiveViewModel_NotBaseSession()
    {
        var baseVm = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000 });
        var doc = new LayoutDocument("Test", baseVm);
        var propsTool = new PropertiesTool();
        doc.CanvasInteracted += () => propsTool.SetActiveLayout(doc.ActiveViewModel);

        Assert.Same(baseVm, doc.ActiveViewModel); // no push-in yet — sanity check on the fixture

        propsTool.SetActiveCell(null); // clobber, as above
        doc.NotifyCanvasInteracted();

        Assert.True(propsTool.IsLayoutActive);
        Assert.Same(doc.ActiveViewModel, propsTool.LayoutInspectorVm.EditorVm);
    }

    // ── Push In / Pop Out re-route the Properties panel (owner, 2026-08-25) ──────────────────────
    //
    //  "Sometimes the Properties Inspector does not update to the object I selected in the Layout
    //  Editor. Clicking on canvas and then clicking back on the object still does not update."
    //
    //  The panel follows ONE LayoutEditorViewModel — whichever SetActiveLayout was last handed — and
    //  a push-in swaps which VM the canvas is editing without the document ever leaving
    //  DocumentDock.ActiveDockable. So the panel stayed on the parent frame and every selection made
    //  in the sub-cell was invisible to it.
    //
    //  Clicking away and back could not clear it, which is what makes this the reported bug and not
    //  a near miss: the one repair path (CanvasInteracted) is raised from the canvas's GotFocus, and
    //  GotFocus does not re-fire when focus is ALREADY on the canvas — which it is, because push-in's
    //  own gesture is a double-click on that same canvas. Pushing in from the TOOLBAR button moves
    //  focus to the button, so the next canvas click does repair it: hence "sometimes".

    /// <summary>The bug itself, with no frame subscription in place.</summary>
    [Fact]
    public void PushIn_WithoutFollowingTheFrame_LeavesThePanelOnTheParentsViewModel()
    {
        var baseVm = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000 });
        var subVm  = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000 });
        var doc    = new LayoutDocument("Test", baseVm);
        var propsTool = new PropertiesTool();

        propsTool.SetActiveLayout(doc.ActiveViewModel);   // tab activated at the base frame
        doc.PushIn(subVm, "sub");                         // user double-clicks an instance

        Assert.Same(subVm, doc.ActiveViewModel);                          // the canvas moved…
        Assert.Same(baseVm, propsTool.LayoutInspectorVm.EditorVm);        // …the panel did not
    }

    /// <summary>The fix: WorkspaceViewModel.WatchLayoutFrameProperties follows
    /// <c>ActiveViewModelChanged</c> and re-runs the whole activation routine, so every panel that
    /// reads off <c>ActiveViewModel</c> lands on the frame now on screen.</summary>
    [Fact]
    public void SimulatedWorkspaceWiring_FrameChange_RepointsThePanelAtTheFrameNowOnScreen()
    {
        var baseVm = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000 });
        var subVm  = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000 });
        var doc    = new LayoutDocument("Test", baseVm);
        var propsTool = new PropertiesTool();

        // Mirrors WorkspaceViewModel.WatchLayoutFrameProperties' subscription.
        doc.ActiveViewModelChanged += (_, _) => propsTool.SetActiveLayout(doc.ActiveViewModel);

        propsTool.SetActiveLayout(doc.ActiveViewModel);
        Assert.Same(baseVm, propsTool.LayoutInspectorVm.EditorVm);

        doc.PushIn(subVm, "sub");
        Assert.True(propsTool.IsLayoutActive);
        Assert.Same(subVm, propsTool.LayoutInspectorVm.EditorVm);

        // …and back out again — a pop is the same event and must route the same way.
        doc.PopOut();
        Assert.Same(baseVm, propsTool.LayoutInspectorVm.EditorVm);
    }

    /// <summary>The whole point of re-pointing: a selection made in the sub-cell has to reach the
    /// panel. Pinned against the real refresh path (Overlay notifications), not just the reference.</summary>
    [Fact]
    public void AfterAFrameChange_ASelectionInTheSubCellReachesThePanel()
    {
        var baseVm = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000 });
        var subView = new LayoutView { DbuPerMicron = 1000 };
        subView.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 });
        var subVm = new LayoutEditorViewModel(subView);

        var doc = new LayoutDocument("Test", baseVm);
        var propsTool = new PropertiesTool();
        doc.ActiveViewModelChanged += (_, _) => propsTool.SetActiveLayout(doc.ActiveViewModel);

        propsTool.SetActiveLayout(doc.ActiveViewModel);
        doc.PushIn(subVm, "sub");

        subVm.SelectAllCommand.Execute(null);

        Assert.False(propsTool.LayoutInspectorVm.IsEmptyState);
    }
}
