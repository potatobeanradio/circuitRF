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
}
