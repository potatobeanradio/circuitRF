// ================================================================
//  SymbolCanvasActivationTests.cs
//
//  Owner, 2026-08-17: "sometimes the Property Inspector does not update when I click on a Pin in the
//  Symbol editor." The "sometimes" is whether the Project Tree has been touched since. Clicking a file
//  node there calls PropertiesTool.SetActiveFileInfo, which — like every context setter in that class —
//  clears every OTHER context on its way past, including SymbolInspectorVm.SetContext(null). The symbol
//  document never left DocumentDock.ActiveDockable (the tree is a different dock region), so
//  WorkspaceViewModel.OnDocumentDockPropertyChanged never re-fires, and clicking back into the canvas
//  restores nothing: the inspector is detached from the VM and every subsequent pin click changes
//  nothing on screen.
//
//  Exactly the bug LayoutCanvasActivationTests already covers for the layout editor, and fixed the same
//  way — SymbolEditorDocument.CanvasInteracted, raised by the view on canvas GotFocus.
//
//  WorkspaceViewModel cannot be constructed headlessly (src/Ui/CLAUDE.md's own documented constraint),
//  so this composes the REAL types it wires together exactly as HookSymbolCellDirty and
//  OnSymbolCanvasInteracted do. The GotFocus wiring in the view's code-behind is the one link a
//  headless test cannot reach, for the same reason no view code-behind here can be.
// ================================================================

using Avalonia.Input;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.ViewModels.Dock;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class SymbolCanvasActivationTests
{
    private static (SymbolEditorDocument Doc, SymbolEditorViewModel Vm) MakeDoc()
    {
        var sym = new EditableSymbol { UserEditable = true };
        sym.Pins.Add(new SymbolPin(-100, 0, 0));
        sym.Pins.Add(new SymbolPin(100, 0, 1));
        var vm = new SymbolEditorViewModel(sym);
        return (new SymbolEditorDocument("Test", vm), vm);
    }

    [Fact]
    public void NotifyCanvasInteracted_RaisesEvent()
    {
        var (doc, _) = MakeDoc();
        bool raised = false;
        doc.CanvasInteracted += () => raised = true;

        doc.NotifyCanvasInteracted();

        Assert.True(raised);
    }

    [Fact]
    public void NotifyCanvasInteracted_NoSubscriber_DoesNotThrow()
    {
        var (doc, _) = MakeDoc();
        Assert.Null(Record.Exception(doc.NotifyCanvasInteracted));
    }

    /// <summary>Reproduces the owner's report end to end, in the inspector's own observable state:
    /// a pin click updates the panel, a project-tree click silently detaches it, and further pin
    /// clicks then do nothing — until the canvas interaction re-asserts the context.</summary>
    [Fact]
    public void SimulatedWorkspaceWiring_CanvasInteracted_ReassertsSymbolContext_AfterTreeClickDetachesIt()
    {
        var (doc, vm) = MakeDoc();
        var props = new PropertiesTool();

        // Mirrors WorkspaceViewModel.HookSymbolCellDirty's new subscription line.
        doc.CanvasInteracted += () => props.SetActiveSymbolEditor(doc.ViewModel);

        // The symbol tab is activated normally (mirrors OnDocumentDockPropertyChanged).
        props.SetActiveSymbolEditor(vm);
        Assert.True(props.IsSymbolEditorActive);

        // Clicking a pin drives the inspector — the behaviour that is supposed to work.
        vm.OnPointerPressed(100, 0, KeyModifiers.None);
        Assert.True(props.SymbolInspectorVm.IsPinSelected);
        Assert.Equal(2, props.SymbolInspectorVm.PinPortIndex);

        // The user clicks a file in the Project Tree while the symbol tab stays active —
        // OnTreeSelectionChanged's SetActiveFileInfo, which clears the symbol context as a side effect
        // even though DocumentDock.ActiveDockable never changed.
        string probe = Path.Combine(Path.GetTempPath(), "crf-sym-activation-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(probe, "x");
        try
        {
            props.SetActiveFileInfo(new FileInfoInspectorViewModel(probe));
            Assert.False(props.IsSymbolEditorActive);
            Assert.False(props.SymbolInspectorVm.IsPinSelected);   // detached from the VM

            // The reported symptom: clicking the OTHER pin now changes nothing at all.
            vm.OnPointerPressed(-100, 0, KeyModifiers.None);
            Assert.Equal(0, vm.Overlay.SelectedPinIndex);          // the VM knows perfectly well
            Assert.False(props.SymbolInspectorVm.IsPinSelected);   // the panel never hears about it

            // The user clicks back into the symbol canvas — GotFocus -> NotifyCanvasInteracted().
            doc.NotifyCanvasInteracted();

            Assert.True(props.IsSymbolEditorActive);
            Assert.True(props.SymbolInspectorVm.IsPinSelected);
            Assert.Equal(1, props.SymbolInspectorVm.PinPortIndex); // pin 0 -> port 1, the live selection

            // And it keeps working from here — the panel is attached again, not merely refreshed once.
            vm.OnPointerPressed(100, 0, KeyModifiers.None);
            Assert.Equal(2, props.SymbolInspectorVm.PinPortIndex);
        }
        finally
        {
            try { File.Delete(probe); } catch { }
        }
    }
}
