using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Two owner reports from 2026-08-19, which turned out to be one mechanism seen from both ends:
/// <list type="number">
/// <item><i>"I was dragging a wire in the wBond host layout, but circuitRF crashed"</i> — an
///   <c>InvalidOperationException</c> out of <c>CapacitanceReduction.Compute</c>, by way of
///   <c>Republish</c> and <c>OnPointerMoved</c>.</item>
/// <item><i>"when I drag wires overtop of other wires, the dragged wires move back to their old
///   position during the drag and my mouse is no longer overtop of the wires that I was
///   dragging."</i></item>
/// </list>
///
/// <para>Both are the singular-matrix refusal. The second is what it does when it fires mid-gesture
/// (it undid the drag underneath the cursor); the first is what happened when the geometry it left
/// behind reached the one factorisation nothing guarded. The physics of the degenerate geometry is
/// pinned in <c>WBond.Tests/WireInThePlaneTests</c>; what belongs here is the state machine.</para>
/// </summary>
public class WBondDragSingularTests
{
    private static readonly long DiameterNm = WBondUnits.ToNm(1.0, WBondUnit.Mil);
    private static readonly long PitchNm = WBondUnits.ToNm(6.0, WBondUnit.Mil);

    private static WBondDesign Design(int wires)
    {
        var design = new WBondDesign();
        var array = new WireArray { Name = "G1" };
        for (int w = 0; w < wires; w++)
            array.Wires.Add(LoopShape.CreateSeedWire(
                Point3.Mils(0, w * 6, 0), Point3.Mils(100, w * 6, 0), DiameterNm, "Gold",
                loopHeightNm: WBondUnits.ToNm(20.0, WBondUnit.Mil)));
        design.Arrays.Add(array);
        return design;
    }

    private static List<Point3> PointsOf(WBondViewModel vm, int wire) =>
        [.. vm.Design.AllWires().ToList()[wire].Points];

    /// <summary>Flattens a wire onto the plane in the design only — what a profile drag through z = 0 leaves.</summary>
    private static void FlattenInTheDesign(WBondViewModel vm, int wire)
    {
        var w = vm.Design.AllWires().ToList()[wire];
        for (int i = 0; i < w.Points.Count; i++)
            w.Points[i] = new Point3(w.Points[i].X, w.Points[i].Y, 0);
    }

    // ---------------------------------------------------------------- report 2: the wires jumped back

    /// <summary>
    /// <b>Dragging one wire across another leaves it under the cursor.</b> For the instant the two
    /// coincide the matrices are singular — and that used to be treated as a failed edit, which rolled
    /// the whole gesture back and left the hand dragging nothing.
    ///
    /// <para>What is held now is the READOUT, not the geometry.</para>
    /// </summary>
    [Fact]
    public void DraggingOneWireAcrossAnother_LeavesItUnderTheCursor()
    {
        var vm = new WBondViewModel(Design(8));
        var start = PointsOf(vm, 2);

        vm.Selection = new WireSelection { Wires = { 2 } };
        vm.BeginGesture();

        // Frame one: straight on top of wire 3.
        WireEdits.Translate(vm.Design, vm.Selection, 0, PitchNm, EditorView.Layout);
        vm.CommitPointMove([2]);

        Assert.True(vm.ReadoutIsHeld);
        Assert.NotEqual(start, PointsOf(vm, 2));       // the geometry did NOT snap back
        Assert.Equal(start[0].Y + PitchNm, PointsOf(vm, 2)[0].Y);
    }

    /// <summary>
    /// <b>And the panel comes back as soon as the wires separate — in the same drag, not at the
    /// release.</b> Owner, 2026-08-19: <i>"during drag, if wires land on other wire, the Array
    /// Inductance panel stops updating (even when wires are moved off of the other wires during the
    /// same drag)."</i>
    ///
    /// <para>What makes recovery possible is that the held frames keep the MATRIX exact
    /// (<c>MoveWiresUnfactored</c>) — <b>L</b> is well defined at every position, it is only its
    /// Cholesky factor that does not exist while two wires coincide. Each frame then retries the
    /// factorisation and one of them succeeds. Holding the matrix as well would have left nothing to
    /// recover from short of a full rebuild.</para>
    /// </summary>
    [Fact]
    public void MovingBackOffTheOverlap_RestoresTheReadoutMidDrag()
    {
        var vm = new WBondViewModel(Design(8));
        vm.Selection = new WireSelection { Wires = { 2 } };
        vm.BeginGesture();

        WireEdits.Translate(vm.Design, vm.Selection, 0, PitchNm, EditorView.Layout);
        vm.CommitPointMove([2]);
        Assert.True(vm.ReadoutIsHeld);

        double heldInductance = vm.Readout.Rows[0].SelfPicoHenries;

        // Still mid-drag: come back off the wire it landed on.
        WireEdits.Translate(vm.Design, vm.Selection, 0, -PitchNm / 2, EditorView.Layout);
        vm.CommitPointMove([2]);

        Assert.False(vm.ReadoutIsHeld);
        Assert.NotEqual(heldInductance, vm.Readout.Rows[0].SelfPicoHenries);
    }

    /// <summary>
    /// And when the drag carries on past the overlap and the button comes up somewhere ordinary, the
    /// numbers are live and the wires are where they were dropped.
    /// </summary>
    [Fact]
    public void DraggingPastTheOverlap_RestoresTheReadoutOnRelease()
    {
        var vm = new WBondViewModel(Design(8));
        var start = PointsOf(vm, 2);
        string? refusal = null;
        vm.EditRefused += r => refusal = r;

        vm.Selection = new WireSelection { Wires = { 2 } };
        vm.BeginGesture();

        WireEdits.Translate(vm.Design, vm.Selection, 0, PitchNm, EditorView.Layout);
        vm.CommitPointMove([2]);
        Assert.True(vm.ReadoutIsHeld);

        // Keep going: half a pitch further, which is clear of both neighbours.
        WireEdits.Translate(vm.Design, vm.Selection, 0, PitchNm / 2, EditorView.Layout);
        vm.CommitPointMove([2]);

        vm.EndGesture();

        Assert.False(vm.ReadoutIsHeld);
        Assert.Null(vm.CapacitanceRefusal);
        Assert.Equal(start[0].Y + PitchNm + PitchNm / 2, PointsOf(vm, 2)[0].Y);   // it stayed where it was dropped
        Assert.NotNull(refusal);                                                  // the pause was still explained
    }

    /// <summary>
    /// Dropping a wire ON another is a different thing from dragging across one, and it is the case
    /// the rollback is actually for — a design with two wires on identical geometry has no inductance
    /// matrix. It is undone <b>at the release</b>, where nothing moves out from under the cursor.
    /// </summary>
    [Fact]
    public void DroppingAWireOnTopOfAnother_IsUndoneAtTheRelease()
    {
        var vm = new WBondViewModel(Design(8));
        var start = PointsOf(vm, 2);
        var refusals = new List<string>();
        vm.EditRefused += refusals.Add;

        vm.Selection = new WireSelection { Wires = { 2 } };
        vm.BeginGesture();
        WireEdits.Translate(vm.Design, vm.Selection, 0, PitchNm, EditorView.Layout);
        vm.CommitPointMove([2]);
        vm.EndGesture();

        Assert.False(vm.ReadoutIsHeld);
        Assert.Equal(start, PointsOf(vm, 2));
        Assert.NotEmpty(refusals);
    }

    // ---------------------------------------------------------------- report 1: the crash

    /// <summary>
    /// <b>The crash.</b> Outside a gesture there is no held state and no snapshot to restore, so the
    /// design and the mesh both keep the degenerate wire — and the next commit refills the capacitance
    /// over the whole mesh. That call is <c>RefreshCapacitance</c>, reached from <c>Republish</c>,
    /// which no <c>try</c> covered: the exception went out through <c>OnPointerMoved</c> and ended the
    /// process.
    ///
    /// <para>It now drops the capacitance and says which wire and why. The inductance is unaffected
    /// and keeps updating — a wire the plane shorts out still has one.</para>
    /// </summary>
    [Fact]
    public void ADegenerateWireLeftInTheMesh_DropsTheCapacitance_RatherThanThrowing()
    {
        var vm = new WBondViewModel(Design(8));   // no undo history: the refusal cannot restore
        var refusals = new List<string>();
        vm.EditRefused += refusals.Add;

        FlattenInTheDesign(vm, 6);
        vm.CommitPointMove([6]);
        Assert.NotEmpty(refusals);

        // Anything that republishes now refills P over that mesh. The toolbar's own capacitance
        // toggle is the shortest path to it and is a thing a user does; on the owner's machine it was
        // the next affordable drag frame. Either way this is the call that used to end the process.
        vm.IncludeCapacitance = false;
        vm.IncludeCapacitance = true;

        Assert.NotNull(vm.CapacitanceRefusal);
        Assert.Contains("ground plane", vm.CapacitanceRefusal);
        Assert.Contains("Wire 7 of array 'G1'", vm.CapacitanceRefusal);
        Assert.NotNull(vm.Readout);
    }

    /// <summary>
    /// A discrete edit — no gesture — still rolls back, and the rollback now puts the MESH back too.
    /// <c>IncrementalFill.MoveWires</c> re-flattens the moved wires into the mesh before its factor
    /// update discovers the matrix is singular, so "refused" never meant "nothing happened"; the mesh
    /// is what the capacitance is refilled from on every later frame.
    /// </summary>
    [Fact]
    public void ADiscreteEditRefused_LeavesTheMeshEvaluable()
    {
        var vm = new WBondViewModel(Design(8));
        vm.Selection = new WireSelection { Wires = { 0 } };
        vm.NudgeSelection(1, 0, coarse: false, EditorView.Layout);   // something to roll back to

        string? refusal = null;
        vm.EditRefused += r => refusal = r;

        FlattenInTheDesign(vm, 6);
        vm.CommitPointMove([6]);

        Assert.NotNull(refusal);
        CapacitanceReduction.Create(vm.Mesh, parallel: false);   // the next frame's fill is safe
        Assert.Null(vm.CapacitanceRefusal);
    }
}
