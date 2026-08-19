using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The editor-facing halves of brief-wbond-capacitance: <b>C4's drag-loop rule</b> (§5.1), the panel's
/// own C6 invariant and C9 resonance state, the frequency readout, and the one moment the editor's
/// toolbar toggle reaches a placed component (§3.4).
/// </summary>
public class WBondCapacitancePanelTests
{
    private static WBondDesign Design(int wires = 4, int arrays = 1)
    {
        long loopNm = WBondUnits.ToNm(20.0, WBondUnit.Mil);
        var design = new WBondDesign();

        for (int a = 0; a < arrays; a++)
        {
            var array = new WireArray { Name = $"G{a + 1}" };
            for (int w = 0; w < wires; w++)
            {
                double y = a * 200 + w * 6;
                array.Wires.Add(LoopShape.CreateSeedWire(
                    Point3.Mils(0, y, 4), Point3.Mils(100, y, 1),
                    WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold", loopHeightNm: loopNm));
            }
            design.Arrays.Add(array);
        }
        return design;
    }

    // ---------------------------------------------------------------- C4: not in the drag loop

    /// <summary>
    /// <b>C4 — a drag frame the ladder has NOT cleared does not pay for the capacitance</b>, and the
    /// gesture's end pays for it once.
    ///
    /// <para>This is the rule a later refactor will quietly break: nothing but a counter can tell a
    /// per-frame rebuild from a per-gesture one, because both give the right answer at rest.</para>
    ///
    /// <para><b>Revised 2026-08-18.</b> The original rule was "never in the drag loop, full stop", on
    /// the premise that a stale C is second-order. It is not — see
    /// <c>ADrag_KeepsTheCapacitanceInStepWhenTheFrameBudgetAllowsIt</c> — so the gate is now the
    /// quality ladder's own rung rather than the gesture.</para>
    /// </summary>
    [Fact]
    public void C4_ADragFrameThatCannotAffordTheCapacitance_DoesNotPayForIt()
    {
        var vm = new WBondViewModel(Design(wires: 6));
        int before = vm.CapacitanceComputeCount;

        vm.BeginGesture();
        vm.RefreshCapacitanceDuringGesture = false;   // what the ladder sets below the Exact rung
        vm.Selection = new WireSelection { Wires = { 0, 1 } };
        for (int frame = 0; frame < 20; frame++)
            vm.NudgeSelection(0, 1, coarse: false, EditorView.Profile);

        Assert.Equal(before, vm.CapacitanceComputeCount);
        Assert.Equal(20, vm.IncrementalUpdateCount);

        // ...and the commit pays for it, exactly once.
        vm.EndGesture();
        Assert.Equal(before + 1, vm.CapacitanceComputeCount);
    }

    /// <summary>
    /// The inductance readout still moves DURING the drag — capacitance policy must never freeze the
    /// panel while the geometry is real.
    /// </summary>
    [Fact]
    public void TheReadoutMovesDuringADrag()
    {
        var vm = new WBondViewModel(Design(wires: 6));
        double before = vm.Readout.Rows[0].SelfPicoHenries;

        vm.BeginGesture();
        vm.SelectAllWires();
        vm.ScaleSelection(spanFactor: 1.0, heightFactor: 2.0, moveOutputFoot: true);

        Assert.NotEqual(before, vm.Readout.Rows[0].SelfPicoHenries);
    }

    /// <summary>
    /// A gesture that changed nothing must not pay for a rebuild on close — <c>EndGesture</c> is
    /// called on every click, not only after a drag.
    /// </summary>
    [Fact]
    public void C4_AGestureThatChangedNothing_RebuildsNothing()
    {
        var vm = new WBondViewModel(Design(wires: 4));
        int before = vm.CapacitanceComputeCount;

        vm.BeginGesture();
        vm.EndGesture();

        Assert.Equal(before, vm.CapacitanceComputeCount);
    }

    // ── The two drag defects the owner reported on 2026-08-18 ────────────────────

    /// <summary>
    /// <b>THE FLASH — a degraded drag frame publishes nothing at all.</b>
    ///
    /// <para>The panel used to alternate between two numbers ~70 % apart for the whole drag. The
    /// cause was the quality ladder's middle Chord rung: it replaced each moving wire's polyline with
    /// its chord, and a 20 mil loop flattened that way reports its array inductance ~70 % low
    /// (measured 597 pH exact against 180 pH collapsed). With the ladder stepping down on one slow
    /// frame and back up after three comfortable ones, the panel swung between the two. That rung is
    /// gone; a degraded frame now moves geometry and publishes nothing.</para>
    ///
    /// <para><b>It had nothing to do with capacitance</b> — the gap is 69.9 % with capacitance off
    /// and 72.3 % with it on — which is why this runs both ways.</para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ADegradedDragFrame_PublishesNothing(bool capacitance)
    {
        var vm = new WBondViewModel(Design(wires: 8)) { IncludeCapacitance = capacitance };
        var controller = new WBondPointerController(vm, frameBudgetMs: 1e-6);   // every frame overruns

        double exact = vm.Readout.Rows[0].SelfPicoHenries;

        vm.SelectAllWires();
        vm.BeginGesture();
        controller.BeginDrag();

        for (int frame = 0; frame < 10; frame++)
            controller.DragFrame(_ => vm.NudgeSelection(0, 1, coarse: false, EditorView.Profile));

        Assert.Equal(DragQuality.FreezeAndSnap, controller.Quality);
        Assert.Equal(exact, vm.Readout.Rows[0].SelfPicoHenries);

        controller.EndDrag();
        vm.EndGesture();

        // ...and the exact answer arrives on release, having actually moved.
        Assert.NotEqual(exact, vm.Readout.Rows[0].SelfPicoHenries);
        Assert.False(vm.DeferFills, "The deferral must not survive the drag.");
    }

    /// <summary>
    /// <b>THE FRAME RATE. A 500-wire drag never attempts a fill, on either canvas.</b>
    ///
    /// <para>Owner, 2026-08-18: <i>"the frame rate is slow when I drag 500 wires in a layout view …
    /// dragging 500 wires must always be fast, it should always take priority."</i> The ladder is
    /// handed the size of the job at <c>BeginDrag</c> and refuses to attempt a fill it can see is
    /// hopeless, so not one frame of such a drag pays for the matrix — and the exact answer arrives
    /// on release.</para>
    /// </summary>
    [Theory]
    [InlineData(EditorView.Layout)]
    [InlineData(EditorView.Profile)]
    public void ABigDrag_NeverPaysForTheFill(EditorView view)
    {
        var vm = new WBondViewModel(Design(wires: 500));
        var controller = new WBondPointerController(vm);   // the REAL 60 fps budget

        vm.SelectAllWires();
        vm.BeginGesture();
        controller.BeginDrag();

        Assert.Equal(DragQuality.FreezeAndSnap, controller.Quality);

        int fillsBefore = vm.IncrementalUpdateCount;
        int rebuildsBefore = vm.RebuildCount;
        int capacitanceBefore = vm.CapacitanceComputeCount;

        for (int frame = 0; frame < 30; frame++)
            controller.DragFrame(_ => vm.NudgeSelection(0, 1, coarse: false, view));

        Assert.Equal(fillsBefore, vm.IncrementalUpdateCount);
        Assert.Equal(rebuildsBefore, vm.RebuildCount);
        Assert.Equal(capacitanceBefore, vm.CapacitanceComputeCount);

        // The geometry still moved every frame — the drag is not frozen, only the arithmetic is.
        Assert.NotEqual(0, vm.Design.AllWires().First().Points[0].Z);

        controller.EndDrag();
        vm.EndGesture();

        Assert.Equal(fillsBefore + 1, vm.IncrementalUpdateCount);
        Assert.Equal(capacitanceBefore + 1, vm.CapacitanceComputeCount);
    }

    /// <summary>
    /// A big drag must not rebuild the MESH either. The old Chord rung called
    /// <c>CommitStructuralChange</c> — a full mesh rebuild and cold fill — on <b>every</b> frame it
    /// was engaged for, which was the dominant cost and the thing that made a 500-wire drag crawl.
    /// </summary>
    [Fact]
    public void ADegradedDrag_NeverRebuildsTheMesh()
    {
        var vm = new WBondViewModel(Design(wires: 200));
        var controller = new WBondPointerController(vm, frameBudgetMs: 1e-6);

        vm.SelectAllWires();
        vm.BeginGesture();
        controller.BeginDrag();

        int rebuilds = vm.RebuildCount;
        for (int frame = 0; frame < 20; frame++)
            controller.DragFrame(_ => vm.NudgeSelection(0, 1, coarse: false, EditorView.Profile));

        Assert.Equal(rebuilds, vm.RebuildCount);

        controller.EndDrag();
        vm.EndGesture();
        Assert.Equal(rebuilds, vm.RebuildCount);
    }

    /// <summary>
    /// A degraded drag must not leave the wires it moved visibly reshaped. The Chord rung mutated the
    /// geometry the canvas was drawing, so wires straightened on screen mid-drag and sprang back on
    /// release.
    /// </summary>
    [Fact]
    public void ADegradedDrag_NeverReshapesTheWires()
    {
        var vm = new WBondViewModel(Design(wires: 60));
        var controller = new WBondPointerController(vm, frameBudgetMs: 1e-6);

        int pointsBefore = vm.Design.AllWires().First().Points.Count;
        Assert.True(pointsBefore > 2, "The fixture must have interior points for this to mean anything.");

        vm.SelectAllWires();
        vm.BeginGesture();
        controller.BeginDrag();

        for (int frame = 0; frame < 10; frame++)
        {
            controller.DragFrame(_ => vm.NudgeSelection(0, 1, coarse: false, EditorView.Profile));
            Assert.All(vm.Design.AllWires(), w => Assert.Equal(pointsBefore, w.Points.Count));
        }

        controller.EndDrag();
        vm.EndGesture();
        Assert.All(vm.Design.AllWires(), w => Assert.Equal(pointsBefore, w.Points.Count));
    }

    /// <summary>
    /// <b>THE STEP ON RELEASE. A drag whose frames fit the budget must leave nothing to correct.</b>
    ///
    /// <para>Holding the capacitance frozen for a whole gesture rested on the premise that C is far
    /// less geometry-sensitive than L. Measured, |dC/dL| ≈ 0.4 and the two errors COMPOUND, because
    /// L_eff = L/(1 − ω²LC) rises with both — so the readout stepped <b>2 % to 15 %</b> at the moment
    /// the button was released, the size set by how far the drag went. With the ladder at its Exact
    /// rung the frame can afford the refresh, and the step must be exactly zero.</para>
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(20)]
    public void ADrag_KeepsTheCapacitanceInStepWhenTheFrameBudgetAllowsIt(int frames)
    {
        var vm = new WBondViewModel(Design(wires: 20));
        var controller = new WBondPointerController(vm, frameBudgetMs: 1e9);   // ladder stays Exact

        vm.SelectAllWires();
        vm.BeginGesture();
        controller.BeginDrag();

        for (int i = 0; i < frames; i++)
            controller.DragFrame(_ => vm.NudgeSelection(0, 1, coarse: true, EditorView.Profile));

        Assert.Equal(DragQuality.Exact, controller.Quality);

        controller.EndDrag();
        double lastDragFrame = vm.Readout.Rows[0].SelfPicoHenries;

        vm.EndGesture();
        double onRelease = vm.Readout.Rows[0].SelfPicoHenries;

        Assert.Equal(lastDragFrame, onRelease);
    }

    /// <summary>
    /// The premise that made the frozen capacitance look safe, measured — so nobody restores it on
    /// the strength of the same argument.
    /// </summary>
    [Fact]
    public void CapacitanceIsNotNegligiblyGeometrySensitive()
    {
        var baseline = Design(wires: 20);
        var baseMesh = WireMesh.Build(baseline);
        double l0 = ArrayReduction.Reduce(InductanceMatrix.Fill(baseMesh), baseMesh)[0, 0];
        double c0 = CapacitanceReduction.Create(baseMesh, parallel: false)!.GroundShunt(0);

        var raised = Design(wires: 20);
        foreach (var wire in raised.AllWires())
            for (int i = 1; i < wire.Points.Count - 1; i++)
                wire.Points[i] = wire.Points[i] with { Z = (long)(wire.Points[i].Z * 1.1) };

        var mesh = WireMesh.Build(raised);
        double l = ArrayReduction.Reduce(InductanceMatrix.Fill(mesh), mesh)[0, 0];
        double c = CapacitanceReduction.Create(mesh, parallel: false)!.GroundShunt(0);

        double dL = (l - l0) / l0;
        double dC = (c - c0) / c0;

        Assert.True(dL > 0.0 && dC < 0.0, "Raising a loop must raise L and lower C.");
        Assert.True(Math.Abs(dC / dL) > 0.2,
            $"C moved {dC * 100:F1} % against L's {dL * 100:F1} % — |dC/dL| = {Math.Abs(dC / dL):F2}. " +
            "A frozen capacitance is only defensible if this ratio is near zero, and it is not.");
    }

    /// <summary>A discrete edit outside a gesture recomputes immediately — there is nothing to defer to.</summary>
    [Fact]
    public void ADiscreteEdit_RecomputesTheCapacitanceStraightAway()
    {
        var vm = new WBondViewModel(Design(wires: 4));
        int before = vm.CapacitanceComputeCount;

        vm.SelectAllWires();
        vm.ScaleSelection(spanFactor: 1.0, heightFactor: 1.5, moveOutputFoot: true);

        Assert.Equal(before + 1, vm.CapacitanceComputeCount);
    }

    // ---------------------------------------------------------------- the toolbar toggle

    /// <summary>
    /// The toolbar toggle changes the reported inductance, and turning it off returns the panel to
    /// the partial inductance <b>exactly</b>.
    /// </summary>
    [Fact]
    public void TheToggle_ChangesTheReportedInductanceAndTurningItOffRestoresItExactly()
    {
        var vm = new WBondViewModel(Design(wires: 4));

        Assert.True(vm.IncludeCapacitance, "Capacitance ships ON.");
        double withCapacitance = vm.Readout.Rows[0].SelfPicoHenries;
        double partial = vm.Readout.Rows[0].PartialPicoHenries;

        Assert.True(withCapacitance > partial,
            $"At {vm.ReadoutFrequencyGHz} GHz the effective inductance must read above the partial " +
            $"one: {withCapacitance:F2} pH against {partial:F2} pH.");

        vm.IncludeCapacitance = false;

        Assert.Equal(partial, vm.Readout.Rows[0].SelfPicoHenries);
        Assert.False(vm.Readout.CapacitanceIncluded);
        Assert.False(vm.Design.IncludeCapacitance);
    }

    /// <summary>
    /// <b>C6, through the editor — with the toggle off the frequency box changes nothing.</b>
    /// </summary>
    [Fact]
    public void C6_WithTheToggleOff_TheFrequencyBoxIsInert()
    {
        var vm = new WBondViewModel(Design(wires: 4)) { IncludeCapacitance = false };
        double partial = vm.Readout.Rows[0].PartialPicoHenries;

        foreach (double ghz in new[] { 0.1, 1.0, 10.0, 60.0 })
        {
            vm.ReadoutFrequencyGHz = ghz;
            Assert.Equal(partial, vm.Readout.Rows[0].SelfPicoHenries);
        }
    }

    /// <summary>With it ON the frequency box is the whole point, and moving it up raises the number.</summary>
    [Fact]
    public void WithTheToggleOn_RaisingTheFrequencyRaisesTheEffectiveInductance()
    {
        var vm = new WBondViewModel(Design(wires: 4));
        double previous = 0.0;

        foreach (double ghz in new[] { 1.0, 5.0, 10.0, 20.0 })
        {
            vm.ReadoutFrequencyGHz = ghz;
            double effective = vm.Readout.Rows[0].SelfPicoHenries;

            Assert.True(effective > previous, $"{ghz} GHz gave {effective:F2} pH after {previous:F2} pH.");
            previous = effective;
        }
    }

    /// <summary>
    /// The frequency is a readout setting, and it costs no refill: changing it must not rebuild the
    /// capacitance.
    /// </summary>
    [Fact]
    public void ChangingTheFrequency_DoesNotRefillTheCapacitance()
    {
        var vm = new WBondViewModel(Design(wires: 4));
        int before = vm.CapacitanceComputeCount;

        vm.ReadoutFrequencyGHz = 25.0;

        Assert.Equal(before, vm.CapacitanceComputeCount);
        Assert.Equal(25.0, vm.Readout.ReadoutFrequencyGHz);
    }

    /// <summary>A non-positive frequency is refused rather than divided by.</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    [InlineData(double.NaN)]
    public void ANonPositiveFrequencyIsRefused(double ghz)
    {
        var vm = new WBondViewModel(Design(wires: 2));
        vm.ReadoutFrequencyGHz = ghz;
        Assert.Equal(10.0, vm.ReadoutFrequencyGHz);
    }

    // ---------------------------------------------------------------- the panel formatter

    /// <summary>The Frequency row shows GHz, never auto-ranged.</summary>
    [Fact]
    public void ThePanelShowsTheFrequencyInGigahertz()
    {
        var panel = new WBondPanelViewModel();
        var vm = new WBondViewModel(Design(wires: 3));

        panel.Update(vm.Readout);
        Assert.Equal("10 GHz", panel.Frequency);

        vm.ReadoutFrequencyGHz = 2.4;
        panel.Update(vm.Readout);
        Assert.Equal("2.4 GHz", panel.Frequency);
    }

    /// <summary>
    /// <b>C9 — above self-resonance the panel prints the warning and NO number.</b> A readout that
    /// swings through infinity and comes back negative is not a number a reader can discount.
    /// </summary>
    [Fact]
    public void C9_AboveResonance_ThePanelPrintsTheWarningAndBlanksTheNumbers()
    {
        var vm = new WBondViewModel(Design(wires: 4, arrays: 2));
        var panel = new WBondPanelViewModel();

        panel.Update(vm.Readout);
        Assert.False(panel.AboveResonance);
        Assert.Equal("", panel.ResonanceWarning);
        Assert.EndsWith(" pH", panel.Rows[0].Self, System.StringComparison.Ordinal);

        double srfGHz = vm.Readout.SelfResonanceGHz;
        Assert.True(srfGHz > 0.0, "This design must have a self-resonance to test against.");

        vm.ReadoutFrequencyGHz = srfGHz * 1.5;
        panel.Update(vm.Readout);

        Assert.True(panel.AboveResonance);
        Assert.Contains("Above self-resonance", panel.ResonanceWarning, System.StringComparison.Ordinal);
        foreach (var row in panel.Rows)
            Assert.Equal("", row.Self);
    }

    // ---------------------------------------------------------------- §3.4: toggle -> placed component

    /// <summary>
    /// <b>§3.4 — the toolbar toggle and the component parameter are two different things, joined at
    /// exactly one moment.</b>
    ///
    /// <para>A design open in the editor is not yet a component, and one document can be placed as
    /// several components with different settings. What the toggle writes is
    /// <c>WBondDesign.IncludeCapacitance</c>; <c>WBondPlacement.ApplyDesign</c> — the one place an
    /// import lands — is where a placed instance inherits it as its parameter default.</para>
    /// </summary>
    [Fact]
    public void ThePlacedComponentInheritsTheDesignsCapacitanceFlag()
    {
        var on = WBondPlacement.BuildCarrying(Design(wires: 2), "WB1");
        Assert.Equal("true", on.Parameters.First(p => p.Name == "IncludeCapacitance").Expression);

        var offDesign = Design(wires: 2);
        offDesign.IncludeCapacitance = false;

        var off = WBondPlacement.BuildCarrying(offDesign, "WB2");
        Assert.Equal("false", off.Parameters.First(p => p.Name == "IncludeCapacitance").Expression);

        // Re-importing a design over an existing component updates it in step, so the parameter can
        // never describe a payload that is no longer there.
        WBondPlacement.ApplyDesign(off, Design(wires: 3));
        Assert.Equal("true", off.Parameters.First(p => p.Name == "IncludeCapacitance").Expression);
    }

    // ── The panel's own toggle (owner, 2026-08-18) ───────────────────────────────

    /// <summary>
    /// <b>The capacitance switch is on the PANEL, because the panel has two hosts and only one of
    /// them has the editor toolbar.</b>
    /// </summary>
    [Fact]
    public void ThePanelCarriesTheCapacitanceToggleAndItWritesThrough()
    {
        var vm = new WBondViewModel(Design(wires: 4));
        var panel = new WBondPanelViewModel { Editor = vm };
        panel.Update(vm.Readout);

        Assert.True(panel.CanToggleCapacitance);
        Assert.True(panel.IncludeCapacitance);
        Assert.True(panel.ShowFrequency);

        panel.IncludeCapacitance = false;

        Assert.False(vm.IncludeCapacitance);
        Assert.False(vm.Design.IncludeCapacitance);

        panel.Update(vm.Readout);
        Assert.False(panel.IncludeCapacitance);
        Assert.False(panel.ShowFrequency, "With capacitance off the frequency row provably changes nothing.");
    }

    /// <summary>
    /// A panel with no editor behind it — the dock tool before any wirebond has been opened — offers
    /// no switch, rather than one that silently does nothing.
    /// </summary>
    [Fact]
    public void APanelWithNoEditorOffersNoToggle()
    {
        var panel = new WBondPanelViewModel();
        Assert.False(panel.CanToggleCapacitance);

        panel.Editor = new WBondViewModel(Design(wires: 2));
        Assert.True(panel.CanToggleCapacitance);
    }

    /// <summary>
    /// A design that ASKS for capacitance and cannot have it says so — the ground plane is what the
    /// charge returns to, and its absence moves the reported inductance optimistically.
    /// </summary>
    [Fact]
    public void AskingForCapacitanceWithNoGroundPlane_IsExplained()
    {
        var design = Design(wires: 4);
        design.GroundPlane.Enabled = false;

        var vm = new WBondViewModel(design);
        var panel = new WBondPanelViewModel { Editor = vm };
        panel.Update(vm.Readout);

        Assert.True(panel.IncludeCapacitance, "The toggle shows what was ASKED for.");
        Assert.False(vm.Readout.CapacitanceIncluded);
        Assert.Contains("ground plane is disabled", panel.CapacitanceUnavailable, StringComparison.Ordinal);
        Assert.False(panel.ShowFrequency);
    }

    /// <summary>The registry declares the parameter, and it ships ON.</summary>
    [Fact]
    public void TheRegistryDeclaresTheParameterAndItShipsOn()
    {
        var declared = ComponentTypeRegistry.DefaultParameters(SymbolKind.WBond, 0);
        Assert.Equal("true", declared.Single(p => p.Name == "IncludeCapacitance").Expression);
    }
}
