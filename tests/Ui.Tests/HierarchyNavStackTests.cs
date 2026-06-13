using System;
using System.Collections.Generic;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate tests for the <see cref="SchematicDocument"/> navigation stack (hier2 — document nav stack).
/// All tests are framework-free (headless): no Avalonia, no disk I/O, no Dock factory.
/// </summary>
public class HierarchyNavStackTests
{
    private sealed class StubCommand : IUiCommand
    {
        public string Description => "stub";
        public void Execute() { }
        public void Undo()    { }
    }

    private static SchematicViewModel MakeVm()
        => new(new SchematicEditModel());

    private static SchematicDocument MakeDoc(string title, SchematicViewModel vm)
        => new(title, vm);

    // ── Push / pop basics ────────────────────────────────────────────────────

    [Fact]
    public void PushIn_IncrementsNavDepth_ActiveViewModelIsNew()
    {
        var vmA = MakeVm();
        var vmB = MakeVm();
        var doc = MakeDoc("Top", vmA);

        doc.PushIn(vmB, "X1");

        Assert.Equal(1, doc.NavDepth);
        Assert.True(doc.CanPopOut);
        Assert.Same(vmB, doc.ActiveViewModel);
    }

    [Fact]
    public void PopOut_ReturnsToBaseVm_CanPopOutFalse()
    {
        var vmA = MakeVm();
        var vmB = MakeVm();
        var doc = MakeDoc("Top", vmA);

        doc.PushIn(vmB, "X1");
        var returned = doc.PopOut();

        Assert.Same(vmB, returned);
        Assert.Same(vmA, doc.ActiveViewModel);
        Assert.Equal(0, doc.NavDepth);
        Assert.False(doc.CanPopOut);
    }

    [Fact]
    public void PopOut_AtBase_ReturnsNull_NoStateChange()
    {
        var vmA = MakeVm();
        var doc = MakeDoc("Top", vmA);

        var returned = doc.PopOut();

        Assert.Null(returned);
        Assert.Same(vmA, doc.ActiveViewModel);
        Assert.Equal(0, doc.NavDepth);
    }

    [Fact]
    public void PopTo_Zero_ReturnsToBase_PoppedInOrder()
    {
        var vmA = MakeVm();
        var vmB = MakeVm();
        var vmC = MakeVm();
        var doc = MakeDoc("Top", vmA);

        doc.PushIn(vmB, "X1");
        doc.PushIn(vmC, "X2");

        var popped = doc.PopTo(0);

        Assert.Same(vmA, doc.ActiveViewModel);
        Assert.Equal(0, doc.NavDepth);
        // Outermost (most recently pushed = C) comes first.
        Assert.Equal(2, popped.Count);
        Assert.Same(vmC, popped[0]);
        Assert.Same(vmB, popped[1]);
    }

    [Fact]
    public void PopTo_MiddleFrame_LeavesCorrectActiveVm()
    {
        var vmA = MakeVm();
        var vmB = MakeVm();
        var vmC = MakeVm();
        var doc = MakeDoc("Top", vmA);

        doc.PushIn(vmB, "X1");
        doc.PushIn(vmC, "X2");

        var popped = doc.PopTo(1);

        Assert.Same(vmB, doc.ActiveViewModel);
        Assert.Equal(1, doc.NavDepth);
        Assert.Single(popped);
        Assert.Same(vmC, popped[0]);
    }

    [Fact]
    public void PopTo_NoChange_ReturnsEmpty()
    {
        var vmA = MakeVm();
        var vmB = MakeVm();
        var doc = MakeDoc("Top", vmA);
        doc.PushIn(vmB, "X1");

        // PopTo the current top — no-op.
        var popped = doc.PopTo(doc.NavDepth);

        Assert.Empty(popped);
        Assert.Same(vmB, doc.ActiveViewModel);
    }

    // ── ActiveViewModelChanged event ─────────────────────────────────────────

    [Fact]
    public void PushIn_RaisesActiveViewModelChanged()
    {
        var vmA = MakeVm();
        var vmB = MakeVm();
        var doc = MakeDoc("Top", vmA);
        int fired = 0;
        doc.ActiveViewModelChanged += (_, _) => fired++;

        doc.PushIn(vmB, "X1");

        Assert.Equal(1, fired);
    }

    [Fact]
    public void PopOut_RaisesActiveViewModelChanged()
    {
        var vmA = MakeVm();
        var vmB = MakeVm();
        var doc = MakeDoc("Top", vmA);
        doc.PushIn(vmB, "X1");

        int fired = 0;
        doc.ActiveViewModelChanged += (_, _) => fired++;

        doc.PopOut();

        Assert.Equal(1, fired);
    }

    [Fact]
    public void PopTo_RaisesActiveViewModelChanged_OnlyWhenFramesActuallyPopped()
    {
        var vmA = MakeVm();
        var vmB = MakeVm();
        var vmC = MakeVm();
        var doc = MakeDoc("Top", vmA);
        doc.PushIn(vmB, "X1");
        doc.PushIn(vmC, "X2");

        int fired = 0;
        doc.ActiveViewModelChanged += (_, _) => fired++;

        doc.PopTo(0);

        Assert.Equal(1, fired);
    }

    [Fact]
    public void PopTo_NoActualPop_DoesNotRaiseEvent()
    {
        var vmA = MakeVm();
        var doc = MakeDoc("Top", vmA);

        int fired = 0;
        doc.ActiveViewModelChanged += (_, _) => fired++;

        doc.PopTo(0); // already at 0

        Assert.Equal(0, fired);
    }

    // ── Title + dirty follow the active frame ─────────────────────────────────

    [Fact]
    public void ActiveVmDirty_DocTitleShowsBullet()
    {
        var vmA = MakeVm();
        var vmB = MakeVm();
        var doc = MakeDoc("Top", vmA);
        doc.PushIn(vmB, "X1");

        // Make the active (B) session dirty.
        vmB.UndoRedo.Execute(new StubCommand());

        Assert.True(doc.IsDirty);
        Assert.Contains("•", doc.Title);
    }

    [Fact]
    public void PopToCleanVm_ClearsDirtyBullet()
    {
        var vmA = MakeVm();
        var vmB = MakeVm();
        var doc = MakeDoc("Top", vmA);
        doc.PushIn(vmB, "X1");

        // Make B dirty.
        vmB.UndoRedo.Execute(new StubCommand());
        Assert.True(doc.IsDirty);
        Assert.Contains("•", doc.Title);

        // Pop back to clean A.
        doc.PopOut();

        Assert.False(doc.IsDirty);
        Assert.DoesNotContain("•", doc.Title);
    }

    [Fact]
    public void AtDepthZero_TitleMatchesBaseName()
    {
        var vmA = MakeVm();
        var doc = MakeDoc("Amp", vmA);

        Assert.Equal("Amp", doc.Title);
    }

    [Fact]
    public void AtDepthOne_TitleMatchesPushedLabel()
    {
        var vmA = MakeVm();
        var vmB = MakeVm();
        var doc = MakeDoc("Amp", vmA);
        doc.PushIn(vmB, "X1");

        Assert.Equal("X1", doc.Title);
    }

    [Fact]
    public void AfterPopOut_TitleReturnsToBaseTitle()
    {
        var vmA = MakeVm();
        var vmB = MakeVm();
        var doc = MakeDoc("Amp", vmA);
        doc.PushIn(vmB, "X1");
        doc.PopOut();

        Assert.Equal("Amp", doc.Title);
    }

    // ── NavFrames ─────────────────────────────────────────────────────────────

    [Fact]
    public void NavFrames_ReflectsFullStack()
    {
        var vmA = MakeVm();
        var vmB = MakeVm();
        var vmC = MakeVm();
        var doc = MakeDoc("Top", vmA);
        doc.PushIn(vmB, "X1");
        doc.PushIn(vmC, "X2");

        var frames = doc.NavFrames;

        Assert.Equal(3, frames.Count);
        Assert.Same(vmA, frames[0].Session); Assert.Equal("Top", frames[0].Label);
        Assert.Same(vmB, frames[1].Session); Assert.Equal("X1",  frames[1].Label);
        Assert.Same(vmC, frames[2].Session); Assert.Equal("X2",  frames[2].Label);
    }

    // ── ViewModel (base session) is unchanged ─────────────────────────────────

    [Fact]
    public void ViewModel_AlwaysReturnsBaseSession()
    {
        var vmA = MakeVm();
        var vmB = MakeVm();
        var doc = MakeDoc("Top", vmA);
        doc.PushIn(vmB, "X1");

        Assert.Same(vmA, doc.ViewModel);
        Assert.Same(vmB, doc.ActiveViewModel);
    }
}
