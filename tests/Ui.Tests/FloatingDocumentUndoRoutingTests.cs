// ================================================================
//  FloatingDocumentUndoRoutingTests.cs — Cmd+Z follows the focused window (2026-09-01)
//
//  Owner: a floating Data Display had focus, Cmd+Z was typed to undo a plot move,
//  and a schematic that was NOT in focus undid an edit instead.
//
//  Two things were wrong and both are needed:
//
//   1. The shell's Undo command was routed ONLY from the shell's own document dock
//      (SetActiveUndoTarget from OnDocumentDockPropertyChanged). A torn-off window
//      taking focus never moved it. On macOS that matters everywhere, because the
//      menu bar is app-global — the SAME NativeMenu instance is attached to every
//      torn-off window (AttachSharedNativeMenuIfMacOS) — so Edit ▸ Undo's Cmd+Z fires
//      the shell's command from whichever window is key. Fixed by routing Undo through
//      the per-window resolution (R-menu-4) every File-menu command already uses.
//
//   2. Even routed correctly, a Data Display could not BE the target: the shell's
//      command was typed to IUndoableDocument, which requires an UndoRedoStack, and a
//      Data Display keeps the ported UndoRedoManager instead. Hence IEditHistoryDocument
//      — the six questions Undo/Redo actually ask, with no claim about the mechanism.
//
//  Part 2 is what this file gates. Part 1 lives in WorkspaceViewModel, which needs the
//  Avalonia application + dock factory to construct and so has no headless test here.
// ================================================================

using CircuitRF.Ui.Commands;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class FloatingDocumentUndoRoutingTests
{
    private static DataDisplayDocument NewDisplay()
        => new("plots", new DataDisplayDocumentViewModel());

    /// <summary>The whole point: the shell's Undo can now name a Data Display at all.</summary>
    [Fact]
    public void ADataDisplay_IsAnUndoTargetTheShellCanName()
        => Assert.IsAssignableFrom<IEditHistoryDocument>(NewDisplay());

    /// <summary>…and every stack-backed document still is, through the narrower contract.</summary>
    [Fact]
    public void AStackBackedDocument_IsStillAnUndoTarget()
    {
        var sch = new SchematicDocument("s", new ViewModels.SchematicViewModel(new SchematicEditModel()));
        Assert.IsAssignableFrom<IEditHistoryDocument>(sch);
        Assert.IsAssignableFrom<IUndoableDocument>(sch);
    }

    /// <summary>An empty history answers "nothing to undo" — which is what disables the menu item,
    /// and on macOS is what lets the key equivalent fall through instead of firing a stale target.
    /// (A brand-new display is NOT empty: its default tab is created by AddPlot, which records an
    /// entry like any other edit. Cleared here so the assertion is about the answer, not the seed.)</summary>
    [Fact]
    public void AnEmptyHistory_HasNothingToUndo()
    {
        var display = NewDisplay();
        display.ViewModel.Window.DataDisplay!.UndoRedo.Clear();

        IEditHistoryDocument doc = display;
        Assert.False(doc.CanUndoLast);
        Assert.False(doc.CanRedoLast);
    }

    /// <summary>
    /// An edit on the canvas — the plot stack, which is the one the owner's plot move lands on —
    /// is what the shell's Undo takes, and takes back.
    /// </summary>
    [Fact]
    public void AnEditOnTheCanvas_IsUndoneAndRedoneThroughTheDocument()
    {
        var display = NewDisplay();
        IEditHistoryDocument doc = display;
        var canvas = display.ViewModel.Window.DataDisplay!;

        int before = canvas.Plots.Count;
        canvas.AddPlot(PlotType.Rect);
        Assert.Equal(before + 1, canvas.Plots.Count);

        Assert.True(doc.CanUndoLast);
        doc.UndoLast();
        Assert.Equal(before, canvas.Plots.Count);

        Assert.True(doc.CanRedoLast);
        doc.RedoLast();
        Assert.Equal(before + 1, canvas.Plots.Count);
    }

    /// <summary>
    /// The enablement channel the shell subscribes to instead of an UndoRedoStack's PropertyChanged.
    /// Without it the Edit menu item — and on macOS the app-global Cmd+Z with it — stays stuck at
    /// whatever it was when the document took focus.
    /// </summary>
    [Fact]
    public void AnEdit_RaisesTheCommandNotificationTheShellFollows()
    {
        var display = NewDisplay();
        int fired = 0;
        display.ViewModel.Window.UndoCommand.CanExecuteChanged += (_, _) => fired++;

        display.ViewModel.Window.DataDisplay!.AddPlot(PlotType.Rect);

        Assert.True(fired > 0);
    }
}
