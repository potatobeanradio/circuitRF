using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// <b>Undo puts the wires back instantly</b> (owner, 2026-08-18: <i>"Undo/Redo is still slow. The
/// wires should move instantly when user performs an Undo after moving wires. The wires moving takes
/// priority over the inductance calculation."</i>) — the drag rule of §4.4, applied to an edit that
/// has no gesture to end.
/// </summary>
public sealed class WBondUndoIsInstantTests
{
    /// <summary>
    /// Comfortably past <see cref="QualityLadder.FitsInOneFrame"/>, and no larger. The bound is
    /// crossed from about 64 wires moving together (N(N+1)/2 blocks against a 16.7 ms budget at the
    /// pessimistic 8 µs/block), so 120 exercises every deferral path at a fraction of the cost of
    /// building a 500-wire fixture seven times over.
    /// </summary>
    private const int BigEnoughToDefer = 120;

    private static WBondDesign Design(int wires)
    {
        long loopNm = WBondUnits.ToNm(20.0, WBondUnit.Mil);
        var design = new WBondDesign();
        var array = new WireArray { Name = "G1" };
        for (int w = 0; w < wires; w++)
            array.Wires.Add(LoopShape.CreateSeedWire(
                Point3.Mils(0, w * 6, 4), Point3.Mils(100, w * 6, 1),
                WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold", loopHeightNm: loopNm));
        design.Arrays.Add(array);
        return design;
    }

    /// <summary>A view-model whose deferred work is captured rather than dispatched.</summary>
    private static WBondViewModel Manual(WBondDesign design, out Func<bool> runQueued)
    {
        var vm = new WBondViewModel(design);
        var queue = new List<Action>();
        vm.RecomputeScheduler = a => queue.Add(a);

        runQueued = () =>
        {
            if (queue.Count == 0) return false;
            var pending = queue.ToArray();
            queue.Clear();
            foreach (var a in pending) a();
            return true;
        };
        return vm;
    }

    /// <summary>
    /// <b>The geometry lands before the arithmetic.</b> Undo puts every point back and notifies the
    /// canvas without having touched the matrix; the fill happens on the next frame.
    /// </summary>
    [Fact]
    public void UndoOfABigMove_RestoresTheGeometryBeforeItFills()
    {
        var vm = Manual(Design(BigEnoughToDefer), out var runQueued);

        var before = vm.Design.AllWires().Select(w => w.Points.ToArray()).ToArray();

        vm.SelectAllWires();
        vm.NudgeSelection(0, 1, coarse: true, EditorView.Profile);
        Assert.NotEqual(before[0][0], vm.Design.AllWires().First().Points[0]);

        int fillsBefore = vm.IncrementalUpdateCount;
        int redraws = 0;
        vm.ReadoutChanged += () => redraws++;

        vm.Undo();

        // The wires are back, the canvas has been told, and NOTHING has been filled yet.
        for (int i = 0; i < before.Length; i++)
            Assert.Equal(before[i], vm.Design.AllWires().ElementAt(i).Points.ToArray());

        Assert.True(redraws > 0, "The canvas must be told to repaint on the undo's own frame.");
        Assert.Equal(fillsBefore, vm.IncrementalUpdateCount);
        Assert.True(vm.HasPendingRecompute, "The fill must be queued, not skipped.");

        // ...and the next frame pays for it, once.
        Assert.True(runQueued());
        Assert.False(vm.HasPendingRecompute);
        Assert.Equal(1, vm.DeferredRecomputeCount);
        Assert.True(vm.IncrementalUpdateCount > fillsBefore);
    }

    /// <summary>Redo takes the same path — it is the same size of job in the other direction.</summary>
    [Fact]
    public void RedoOfABigMove_AlsoDefersItsFill()
    {
        var vm = Manual(Design(BigEnoughToDefer), out var runQueued);

        vm.SelectAllWires();
        vm.NudgeSelection(0, 1, coarse: true, EditorView.Profile);
        vm.Undo();
        runQueued();

        var moved = vm.Design.AllWires().First().Points[0];
        int fillsBefore = vm.IncrementalUpdateCount;

        vm.Redo();

        Assert.NotEqual(moved, vm.Design.AllWires().First().Points[0]);
        Assert.Equal(fillsBefore, vm.IncrementalUpdateCount);
        Assert.True(vm.HasPendingRecompute);

        Assert.True(runQueued());
        Assert.True(vm.IncrementalUpdateCount > fillsBefore);
    }

    /// <summary>
    /// <b>A SMALL undo is not deferred</b> — a deferral costs a frame of stale numbers, worth paying
    /// only when the alternative is a stalled canvas.
    /// </summary>
    [Fact]
    public void UndoOfASmallMove_StaysSynchronous()
    {
        var vm = Manual(Design(6), out _);

        double before = vm.Readout.Rows[0].SelfPicoHenries;

        vm.Selection = new WireSelection { Wires = { 0 } };
        vm.NudgeSelection(0, 1, coarse: true, EditorView.Profile);
        Assert.NotEqual(before, vm.Readout.Rows[0].SelfPicoHenries);

        vm.Undo();

        Assert.False(vm.HasPendingRecompute, "A small undo must not defer anything.");
        Assert.Equal(before, vm.Readout.Rows[0].SelfPicoHenries, before * 1e-9);
    }

    /// <summary>
    /// <b>Undoing a ONE-wire move refills one wire, not the whole design.</b>
    ///
    /// <para>The restore handed every index to the fill whether it had moved or not — on a big design
    /// that is the entire matrix for one wire's worth of edit, and it is most of why an undo felt
    /// slower than the drag that produced it. Asserted through the deferral bound, which is the
    /// observable consequence: one wire fits in a frame, the whole design does not.</para>
    /// </summary>
    [Fact]
    public void UndoOfAOneWireMove_OnABigDesign_IsNotDeferredBecauseItIsSmall()
    {
        var vm = Manual(Design(BigEnoughToDefer), out _);

        // The LAYOUT view, deliberately: the profile view's nudge moves the whole group by design
        // (WBondViewModel.ProfileGroupSubject), which would move the whole design and prove nothing here.
        vm.Selection = new WireSelection { Wires = { 7 } };
        vm.NudgeSelection(1, 0, coarse: true, EditorView.Layout);

        vm.Undo();

        Assert.False(vm.HasPendingRecompute,
            "Undoing one wire's move must restore one wire, so it fits in a frame and needs no " +
            "deferral. A pending recompute here means the restore is refilling every wire again.");
    }

    /// <summary>
    /// The whole point, measured: an undo returns before the fill it deferred could possibly have run.
    /// Stated as a RATIO against that fill rather than as an absolute, so it means the same thing on
    /// any machine and at any fixture size; the counter assertions above are the sharper gate.
    /// </summary>
    [Fact]
    public void UndoOfABigMove_ReturnsWithoutWaitingForTheFill()
    {
        var vm = Manual(Design(BigEnoughToDefer), out var runQueued);

        vm.SelectAllWires();
        vm.NudgeSelection(0, 1, coarse: true, EditorView.Profile);

        var sw = Stopwatch.StartNew();
        vm.Undo();
        double undoMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        runQueued();
        double fillMs = sw.Elapsed.TotalMilliseconds;

        Assert.True(undoMs < fillMs,
            $"The undo itself took {undoMs:F1} ms against {fillMs:F1} ms for the fill it deferred. " +
            "The undo is supposed to move points and return, leaving the arithmetic for the next " +
            "frame — if it costs as much as the fill, it is still doing the fill.");
    }

    /// <summary>
    /// A queued fill that lands mid-drag comes back later rather than being dropped — otherwise the
    /// matrix would stay stale until the next unrelated edit.
    /// </summary>
    [Fact]
    public void AQueuedFillArrivingDuringADrag_IsRequeuedRatherThanDropped()
    {
        var vm = Manual(Design(BigEnoughToDefer), out var runQueued);

        vm.SelectAllWires();
        vm.NudgeSelection(0, 1, coarse: true, EditorView.Profile);
        vm.Undo();
        Assert.True(vm.HasPendingRecompute);

        // A drag opens before the queued fill runs.
        vm.DeferFills = true;
        Assert.True(runQueued());
        Assert.True(vm.HasPendingRecompute, "The pending fill must survive a drag, not vanish into it.");
        Assert.Equal(0, vm.DeferredRecomputeCount);

        // The drag ends; the re-queued flush now runs.
        vm.DeferFills = false;
        Assert.True(runQueued());
        Assert.False(vm.HasPendingRecompute);
        Assert.Equal(1, vm.DeferredRecomputeCount);
    }

    /// <summary>A drag must not queue deferred work — it has its own end to pay at.</summary>
    [Fact]
    public void ADrag_QueuesNoDeferredWork()
    {
        var vm = Manual(Design(BigEnoughToDefer), out _);
        var controller = new WBondPointerController(vm);

        vm.SelectAllWires();
        vm.BeginGesture();
        controller.BeginDrag();

        for (int frame = 0; frame < 20; frame++)
            controller.DragFrame(_ => vm.NudgeSelection(0, 1, coarse: false, EditorView.Profile));

        Assert.False(vm.HasPendingRecompute);
        Assert.Equal(0, vm.DeferredRecomputeCount);

        controller.EndDrag();
        vm.EndGesture();
    }
}
