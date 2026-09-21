// ================================================================
//  ReductionTests.cs — the gate for brief-lvs-6-reduction.md §6.
//
//  ── WHAT IS BEING PINNED ──────────────────────────────────────────────────────────────────────
//
//  Reduction is ON by default, which is the one decision in this series that changes what a
//  CORRECT design reports. So what these tests are mostly about is not what collapses but what
//  does NOT: the five conditions that make a node unreachable-after-all, the kinds that are
//  excluded by construction, and the antiparallel pair that a set-wise comparison would have
//  merged into a circuit nobody drew.
//
//  ── THE ONE THING THAT WOULD MAKE ALL OF IT WORTHLESS ─────────────────────────────────────────
//
//  A reducer that could tell the two sides apart. `LvsReduce.Apply` takes a netlist and an options
//  record with nothing side-specific in it — not even the measurement list, which is one list given
//  to both — and `BothSidesGetTheSameTreatment` feeds the IDENTICAL netlist in as both sides and
//  compares the two answers object for object.
//
//  ── WHY GATE 10 IS SHORT OF WHAT THE BRIEF ASKS FOR ───────────────────────────────────────────
//
//  Gate 10 wants "passes in both modes, with different device counts and the same zero findings".
//  There is no verdict to assert until brief 7 lands, and brief 5's two boards turn out to have
//  nothing reducible in them — every node on them is a boundary net or ground. So what is asserted
//  here is the half that exists and is the actual requirement: on a correct design, reduction never
//  changes whether the two sides read as the same circuit. The count half is exercised on a fixture
//  that does reduce, and the verdict half is added to this file when brief 7 lands.
// ================================================================

using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Lvs;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Workspace;
using CircuitRF.Diagnostics;

namespace CircuitRF.Ui.Tests.Lvs;

public sealed class ReductionTests
{
    // ══ 1 — four identical resistors on one net pair reduce to one ═══════════════════════════════
    //
    // R-lvs6-2, R-lvs6-2b, R-lvs6-2c. The value is the physics', the multiplicity is carried rather
    // than discarded, and the group names all four paths — which is what brief 8 un-reduces and
    // what makes reduction safe to have on by default.

    [Fact]
    public void FourIdenticalResistorsInParallelBecomeOneQuarterValueDeviceNamingAllFour()
    {
        var b = new Build().Boundary("in", "out");
        foreach (string name in new[] { "R1", "R2", "R3", "R4" })
            b.Device(name, DeviceKind.Resistor, ["in", "out"], ("R", 400.0));

        var (reduced, log) = LvsReduce.Apply(b.Netlist());

        var merged = Assert.Single(reduced.Devices);
        Assert.Equal("R1", merged.Path);
        Assert.Equal(4, merged.Multiplicity);
        Assert.Equal(["R1", "R2", "R3", "R4"], merged.Group);
        Assert.Equal(100.0, (double)merged.Parameters["R"]!, 9);

        var group = Assert.Single(log.Of(ReductionKind.Parallel));
        Assert.Equal(DeviceKind.Resistor, group.Type);
        Assert.Equal(4, log.DevicesBefore);
        Assert.Equal(1, log.DevicesAfter);
    }

    // ══ 2 — two in series through a bare node ════════════════════════════════════════════════════
    //
    // R-lvs6-2's third row, both directions of the arithmetic: R and L sum, C adds as 1/C.

    [Fact]
    public void TwoSeriesResistorsThroughABareNodeSumAndTwoSeriesCapacitorsAddReciprocally()
    {
        var (resistors, _) = LvsReduce.Apply(new Build()
            .Boundary("in", "out")
            .Device("R1", DeviceKind.Resistor, ["in", "mid"], ("R", 150.0))
            .Device("R2", DeviceKind.Resistor, ["mid", "out"], ("R", 50.0))
            .Netlist());

        var r = Assert.Single(resistors.Devices);
        Assert.Equal(200.0, (double)r.Parameters["R"]!, 9);
        Assert.Equal(["R1", "R2"], r.Group);
        Assert.Equal(1, r.Multiplicity);                 // a series merge is not a multiplicity

        // The node is gone, and so is the net that was only ever it.
        Assert.Equal(2, resistors.Nets.Count);
        Assert.Equal(new[] { 0, 1 }, resistors.BoundaryNets);

        var (capacitors, _) = LvsReduce.Apply(new Build()
            .Boundary("in", "out")
            .Device("C1", DeviceKind.Capacitor, ["in", "mid"], ("C", 2e-12))
            .Device("C2", DeviceKind.Capacitor, ["mid", "out"], ("C", 2e-12))
            .Netlist());

        Assert.Equal(1e-12, (double)Assert.Single(capacitors.Devices).Parameters["C"]!, 15);
    }

    // ══ 3 — the five conditions that make a node unreachable-after-all ═══════════════════════════
    //
    // R-lvs6-3a. If nothing else reaches the node, the two parts are electrically indistinguishable
    // from one; every one of these is a way something else does.
    //
    // The MEASURE case is first because it is the one that will be forgotten: reducing away a node
    // a measurement references produces a circuit that still parses, still runs, and silently
    // answers a different question.

    [Fact]
    public void ANodeAMeasureLineNamesIsNotReducedAcross()
    {
        var netlist = new Build()
            .Boundary("in", "out")
            .Label("mid", "mid")
            .Device("R1", DeviceKind.Resistor, ["in", "mid"], ("R", 150.0))
            .Device("R2", DeviceKind.Resistor, ["mid", "out"], ("R", 50.0))
            .Netlist();

        // The clause's own half: the node's name is picked out of the measurement text, whole
        // dotted path and segments alike, by a scan that errs towards naming one net too many.
        var measured = LvsReduceOptions.NamesIn(["db(V(mid)/V(in))"]);
        Assert.Contains("mid", measured);
        Assert.Contains("in", measured);

        var (reduced, log) = LvsReduce.Apply(netlist, new LvsReduceOptions { MeasuredNames = measured });

        Assert.Equal(2, reduced.Devices.Count);
        Assert.Empty(log.Groups);

        // On a FLAT netlist the name a measure uses is also the net's label, so this refusal and
        // the label clause below both fire on this node. They overlap on purpose — R-lvs6-3d makes
        // the same trade for net "0" — and they stay separate because they have different fixes and
        // because brief 9's hierarchy stitching carries names the label clause does not.
    }

    [Fact]
    public void AThirdTerminalOnTheNodeStopsTheSeriesMerge()
    {
        var (reduced, _) = LvsReduce.Apply(new Build()
            .Boundary("in", "out")
            .Device("R1", DeviceKind.Resistor, ["in", "mid"], ("R", 150.0))
            .Device("R2", DeviceKind.Resistor, ["mid", "out"], ("R", 50.0))
            .Device("R3", DeviceKind.Resistor, ["mid", "0"], ("R", 1000.0))
            .Netlist());

        Assert.Equal(3, reduced.Devices.Count);
    }

    [Fact]
    public void ACellBoundaryNetIsNotReducedAcross()
    {
        var (reduced, _) = LvsReduce.Apply(new Build()
            .Boundary("in", "mid", "out")
            .Device("R1", DeviceKind.Resistor, ["in", "mid"], ("R", 150.0))
            .Device("R2", DeviceKind.Resistor, ["mid", "out"], ("R", 50.0))
            .Netlist());

        Assert.Equal(2, reduced.Devices.Count);
    }

    [Fact]
    public void ALabelledNetIsNotReducedAcross()
    {
        var (reduced, _) = LvsReduce.Apply(new Build()
            .Boundary("in", "out")
            .Label("mid", "BIAS")
            .Device("R1", DeviceKind.Resistor, ["in", "mid"], ("R", 150.0))
            .Device("R2", DeviceKind.Resistor, ["mid", "out"], ("R", 50.0))
            .Netlist());

        Assert.Equal(2, reduced.Devices.Count);
    }

    [Fact]
    public void NetZeroIsNotReducedAcrossEvenWithOnlyTwoTerminalsOnIt()
    {
        // R-lvs6-3d: ground has more than two terminals in every real design, so this is exactly
        // the degenerate fixture the explicit exclusion exists to refuse.
        var (reduced, _) = LvsReduce.Apply(new Build()
            .Boundary("in", "out")
            .Device("R1", DeviceKind.Resistor, ["in", "0"], ("R", 150.0))
            .Device("R2", DeviceKind.Resistor, ["0", "out"], ("R", 50.0))
            .Netlist());

        Assert.Equal(2, reduced.Devices.Count);
    }

    // ══ 4 — antiparallel is a different circuit ══════════════════════════════════════════════════
    //
    // R-lvs6-2a. Two FETs whose drain and source are swapped relative to each other are on the same
    // three nets and would merge under any set-wise comparison. Port for port they are not parallel.

    [Fact]
    public void TwoFetsWithDrainAndSourceSwappedAreNotMerged()
    {
        var (reduced, log) = LvsReduce.Apply(new Build()
            .Boundary("g", "d", "s")
            .Device("M1", DeviceKind.Transistor, ["g", "d", "s"])
            .Device("M2", DeviceKind.Transistor, ["g", "s", "d"])
            .Netlist());

        Assert.Equal(2, reduced.Devices.Count);
        Assert.Empty(log.Groups);

        // And the same two, port for port, DO merge — so the test above is about the ordering and
        // not about some other reason the pair was refused.
        var (together, _) = LvsReduce.Apply(new Build()
            .Boundary("g", "d", "s")
            .Device("M1", DeviceKind.Transistor, ["g", "d", "s"])
            .Device("M2", DeviceKind.Transistor, ["g", "d", "s"])
            .Netlist());

        Assert.Equal(2, Assert.Single(together.Devices).Multiplicity);
    }

    // ══ 5 — what is never reduced ════════════════════════════════════════════════════════════════
    //
    // R-lvs6-4. Two series MLINs are one line only if their Z0s agree and a comparison tool must not
    // do that arithmetic on the user's behalf; collapsing an SnP is not even definable; an R in
    // series with an L would have a merged value with no dimension.

    [Theory]
    [InlineData(DeviceKind.TransmissionLine, DeviceKind.TransmissionLine)]   // MLIN + MLIN
    [InlineData(DeviceKind.Unknown, DeviceKind.Unknown)]                     // SnP + SnP
    [InlineData(DeviceKind.Resistor, DeviceKind.Inductor)]                   // across a type boundary
    public void AnExcludedSeriesPairStaysTwoDevices(DeviceKind first, DeviceKind second)
    {
        var (reduced, log) = LvsReduce.Apply(new Build()
            .Boundary("in", "out")
            .Device("X1", first,  ["in", "mid"])
            .Device("X2", second, ["mid", "out"])
            .Netlist());

        Assert.Equal(2, reduced.Devices.Count);
        Assert.Empty(log.Groups);

        // The node really was collapsible — the refusal is about the DEVICES, not about the node.
        var (control, _) = LvsReduce.Apply(new Build()
            .Boundary("in", "out")
            .Device("X1", DeviceKind.Resistor, ["in", "mid"])
            .Device("X2", DeviceKind.Resistor, ["mid", "out"])
            .Netlist());
        Assert.Single(control.Devices);
    }

    [Theory]
    [InlineData(DeviceKind.TransmissionLine)]
    [InlineData(DeviceKind.Unknown)]
    [InlineData(DeviceKind.Fixture)]
    public void AnExcludedKindIsNotMergedInParallelEither(DeviceKind kind)
    {
        var (reduced, _) = LvsReduce.Apply(new Build()
            .Boundary("in", "out")
            .Device("X1", kind, ["in", "out"])
            .Device("X2", kind, ["in", "out"])
            .Netlist());

        Assert.Equal(2, reduced.Devices.Count);
    }

    // ══ 6 — a kind nobody has thought about is excluded ══════════════════════════════════════════
    //
    // R-lvs6-4e. The lists in LvsReduce are ALLOW-lists, which is the whole requirement: a
    // DeviceKind added later is excluded because nobody wrote it down. A deny-list would make it
    // reducible by default, which is how a kind becomes reducible because nobody thought about it.

    [Fact]
    public void ADeviceKindThatDoesNotExistYetIsExcludedByDefault()
    {
        const DeviceKind future = (DeviceKind)9_999;

        var (parallel, _) = LvsReduce.Apply(new Build()
            .Boundary("in", "out")
            .Device("X1", future, ["in", "out"])
            .Device("X2", future, ["in", "out"])
            .Netlist());
        Assert.Equal(2, parallel.Devices.Count);

        var (series, _) = LvsReduce.Apply(new Build()
            .Boundary("in", "out")
            .Device("X1", future, ["in", "mid"])
            .Device("X2", future, ["mid", "out"])
            .Netlist());
        Assert.Equal(2, series.Devices.Count);
    }

    // ══ 7 — the fixed point ══════════════════════════════════════════════════════════════════════
    //
    // R-lvs6-1b. A series merge exposes a parallel one and the reverse. There is no iteration cap
    // and none is needed, because every collapse removes a device — and Apply ASSERTS that, so a
    // pass that reported a change without shrinking is a test failure rather than a hang.

    [Fact]
    public void ALadderThatNeedsThreePassesConvergesInThreeAndShrinksEveryPass()
    {
        // in ─R1─ b ─R2─ c ─R4─ out, with R3 across (in,c) and R5 across (in,out).
        //   pass 1: series over b            R1+R2      -> 5 devices to 4
        //   pass 2: parallel (in,c), then series over c -> 4 devices to 2
        //   pass 3: parallel (in,out)                   -> 2 devices to 1
        var (reduced, log) = LvsReduce.Apply(new Build()
            .Boundary("in", "out")
            .Device("R1", DeviceKind.Resistor, ["in", "b"],   ("R", 10.0))
            .Device("R2", DeviceKind.Resistor, ["b", "c"],    ("R", 10.0))
            .Device("R3", DeviceKind.Resistor, ["in", "c"],   ("R", 20.0))
            .Device("R4", DeviceKind.Resistor, ["c", "out"],  ("R", 5.0))
            .Device("R5", DeviceKind.Resistor, ["in", "out"], ("R", 15.0))
            .Netlist());

        Assert.Equal(3, log.Passes);
        Assert.Equal(5, log.DevicesBefore);
        Assert.Equal(1, log.DevicesAfter);

        var whole = Assert.Single(reduced.Devices);
        Assert.Equal(["R1", "R2", "R3", "R4", "R5"], whole.Group);

        // (10+10) || 20 = 10; + 5 = 15; || 15 = 7.5.
        Assert.Equal(7.5, (double)whole.Parameters["R"]!, 9);
    }

    // ══ 8 — determinism ══════════════════════════════════════════════════════════════════════════
    //
    // R-lvs6-1c. Groups are formed in (device path, ordinal) order, so the same input gives the same
    // merged identities every time and on every platform — which is what makes a report from one
    // machine comparable with a report from another.

    [Fact]
    public void TenRunsOverASymmetricFixtureGiveIdenticalMergedIdentitiesAndOrder()
    {
        var netlist = new Build()
            .Boundary("in", "out")
            .Device("R4", DeviceKind.Resistor, ["in", "out"], ("R", 400.0))
            .Device("R2", DeviceKind.Resistor, ["in", "out"], ("R", 400.0))
            .Device("R3", DeviceKind.Resistor, ["in", "out"], ("R", 400.0))
            .Device("R1", DeviceKind.Resistor, ["in", "out"], ("R", 400.0))
            .Netlist();

        string first = Dump(LvsReduce.Apply(netlist).Reduced);
        for (int i = 0; i < 9; i++) Assert.Equal(first, Dump(LvsReduce.Apply(netlist).Reduced));

        // The survivor is the canonically first path, not the first PLACED one.
        Assert.StartsWith("R1 ", first, StringComparison.Ordinal);
        Assert.Contains("R1,R2,R3,R4", first, StringComparison.Ordinal);
    }

    // ══ 9 — both sides get the same treatment ════════════════════════════════════════════════════
    //
    // R-lvs6-1a. The identical netlist in as both sides, and the two answers compared object for
    // object. Nothing in LvsReduceOptions is side-specific, so there is nothing a future caller can
    // half-supply to re-enter the trap.

    [Fact]
    public void BothSidesGetTheSameTreatment()
    {
        var netlist = new Build()
            .Boundary("in", "out")
            .Label("bias", "BIAS")
            .Device("C1", DeviceKind.Capacitor, ["in", "mid"],  ("C", 2e-12))
            .Device("C2", DeviceKind.Capacitor, ["mid", "out"], ("C", 2e-12))
            .Device("R1", DeviceKind.Resistor,  ["bias", "0"],  ("R", 1000.0))
            .Device("R2", DeviceKind.Resistor,  ["bias", "0"],  ("R", 1000.0))
            .Netlist();

        var options = LvsReduceOptions.Default;
        var (layoutSide, layoutLog)     = LvsReduce.Apply(netlist, options);
        var (schematicSide, schemaLog)  = LvsReduce.Apply(netlist, options);

        // Object for object. The dump carries every merged identity, its group, its multiplicity,
        // its nets and its values, so nothing a merge could have got wrong is outside it.
        Assert.Equal(Dump(layoutSide), Dump(schematicSide));
        Assert.Equal(Dump(layoutLog), Dump(schemaLog));
        Assert.Equal(2, layoutSide.Devices.Count);
    }

    // ══ 10 — reduction changes the count, never the verdict ══════════════════════════════════════
    //
    // R-lvs6-5c, and the brief calls it the most important test in it. The verdict half arrives with
    // brief 7; what stands in for it here is the property brief 5 already pins — that the correct
    // board's two sides read as the same circuit — asserted in BOTH modes.

    [Fact]
    public void TheCorrectBoardReadsTheSameOnBothSidesInBothModes()
    {
        var layout = ReadLayout("Attenuator");
        var schem  = ReadSchematic("Attenuator");

        foreach (var options in new[] { LvsReduceOptions.Default, LvsReduceOptions.NoReduce })
        {
            var (reducedLayout, layoutLog) = LvsReduce.Apply(layout, options);
            var (reducedSchem,  schemLog)  = LvsReduce.Apply(schem,  options);

            Assert.Equal(Shape(reducedSchem), Shape(reducedLayout));

            // R-lvs6-5c: the mode is on the face of the report either way.
            Assert.Equal(options.Enabled, layoutLog.Enabled);
            var mode = Assert.Single(layoutLog.Notes("Attenuator.clay"), n => n.Id == "lvs.reduce.mode");
            Assert.Equal(DiagnosticSeverity.Info, mode.Severity);
            Assert.Equal(options.Enabled, mode.Arguments["enabled"]);

            // Every node on this board is a boundary net or ground, so nothing on it is reducible
            // and the two modes count it identically. That is a fact about the fixture and it is
            // pinned here so a later change to the board is not mistaken for a change to this pass.
            Assert.Equal(4, reducedLayout.Devices.Count);
            Assert.Equal(4, reducedSchem.Devices.Count);
            Assert.Empty(layoutLog.Of(ReductionKind.Parallel));
            Assert.Empty(schemLog.Of(ReductionKind.Series));
        }
    }

    [Fact]
    public void NoReduceChangesTheCountAndNotWhetherTheTwoSidesAgree()
    {
        // The same circuit drawn two ways: one symbol on one side, two parts in parallel on the
        // other. Unreduced they are different counts and the same nets; reduced they are both one.
        var symbol = new Build()
            .Boundary("in", "out")
            .Device("C1", DeviceKind.Capacitor, ["in", "out"], ("C", 94e-12))
            .Netlist();

        var artwork = new Build()
            .Boundary("in", "out")
            .Device("C1", DeviceKind.Capacitor, ["in", "out"], ("C", 47e-12))
            .Device("C2", DeviceKind.Capacitor, ["in", "out"], ("C", 47e-12))
            .Netlist();

        var off = LvsReduce.Apply(symbol, LvsReduceOptions.NoReduce);
        var offArt = LvsReduce.Apply(artwork, LvsReduceOptions.NoReduce);
        Assert.Equal(1, off.Log.DevicesAfter);
        Assert.Equal(2, offArt.Log.DevicesAfter);
        Assert.NotEqual(Shape(off.Reduced), Shape(offArt.Reduced));

        var on = LvsReduce.Apply(symbol);
        var onArt = LvsReduce.Apply(artwork);
        Assert.Equal(1, on.Log.DevicesAfter);
        Assert.Equal(1, onArt.Log.DevicesAfter);
        Assert.Equal(Shape(on.Reduced), Shape(onArt.Reduced));
        Assert.Equal(94e-12, (double)onArt.Reduced.Devices[0].Parameters["C"]!, 15);
    }

    // ══ 11 — a finding names the individuals ═════════════════════════════════════════════════════
    //
    // R-lvs6-5b. Break one of four parallel resistors — move it onto a different net — and what is
    // left must still let a report say "R3", not "the merged group at net 14".

    [Fact]
    public void BreakingOneOfFourParallelResistorsLeavesTheIndividualNameable()
    {
        var (reduced, log) = LvsReduce.Apply(new Build()
            .Boundary("in", "out")
            .Device("R1", DeviceKind.Resistor, ["in", "out"], ("R", 400.0))
            .Device("R2", DeviceKind.Resistor, ["in", "out"], ("R", 400.0))
            .Device("R3", DeviceKind.Resistor, ["in", "stray"], ("R", 400.0))
            .Device("R4", DeviceKind.Resistor, ["in", "out"], ("R", 400.0))
            .Netlist());

        var merged = Assert.Single(reduced.Devices, d => d.Multiplicity == 3);
        Assert.Equal(["R1", "R2", "R4"], merged.Group);

        // The one that moved is still its own device under its own name, and its Group is itself —
        // so a finding about it names R3 with no branch on whether anything was merged.
        var stray = Assert.Single(reduced.Devices, d => d.Path == "R3");
        Assert.Equal(["R3"], stray.Group);
        Assert.Equal(1, stray.Multiplicity);

        Assert.Equal(["R1", "R2", "R4"], Assert.Single(log.Of(ReductionKind.Parallel)).Members);
    }

    // ══ 12 — a jumper on one side only ═══════════════════════════════════════════════════════════
    //
    // R-lvs6-5d. Collapsed on both sides or on neither: collapsing one side only compares a circuit
    // neither document draws. Reported at info either way, because a jumper absent from one document
    // is exactly the thing a designer wants told.

    [Fact]
    public void AJumperIsReportedAndIsNotCollapsedUnlessBothSidesHaveOne()
    {
        // Three parts on the jumper's far node, so the node is not series-collapsible on its own
        // account and what this test watches is the jumper and nothing else.
        var withJumper = new Build()
            .Boundary("in", "out")
            .Device("J1", DeviceKind.Resistor, ["in", "mid"],  ("R", 0.0))
            .Device("R1", DeviceKind.Resistor, ["mid", "out"], ("R", 50.0))
            .Device("R2", DeviceKind.Resistor, ["mid", "0"],   ("R", 1000.0))
            .Netlist();

        // One side only: found, said, and left alone. The node stays, and so does the device.
        var (kept, keptLog) = LvsReduce.Apply(withJumper);
        var found = Assert.Single(keptLog.Of(ReductionKind.Jumper));
        Assert.Equal("J1", found.Path);
        Assert.False(found.Applied);
        Assert.Equal(3, kept.Devices.Count);
        Assert.Equal(4, kept.Nets.Count);

        var note = Assert.Single(keptLog.Notes("board.clay"), n => n.Id == "lvs.reduce.jumper");
        Assert.Equal(DiagnosticSeverity.Info, note.Severity);
        Assert.Equal(false, note.Arguments["collapsed"]);
        Assert.Contains("J1", note.Render(), StringComparison.Ordinal);

        // Both sides have one: the two nets become one and the link stops being a device.
        var (collapsed, collapsedLog) = LvsReduce.Apply(
            withJumper, new LvsReduceOptions { CollapseJumpers = true });

        Assert.True(Assert.Single(collapsedLog.Of(ReductionKind.Jumper)).Applied);
        Assert.Equal(2, collapsed.Devices.Count);
        Assert.Equal(3, collapsed.Nets.Count);

        // The link is gone and its two nets are one: R1 now reaches the input directly.
        var r1 = Assert.Single(collapsed.Devices, d => d.Path == "R1");
        Assert.Equal(collapsed.BoundaryNets[0], r1.Terminals[0].NetIndex);
    }

    // ── building a netlist by hand ────────────────────────────────────────────────────────────
    //
    // Nets are named here and numbered in first-use order, which is what both readers do; "0" is
    // created first wherever it is used at all, so ground is index 0 the way the two sides put it.

    private sealed class Build
    {
        private readonly List<LvsDevice> _devices = [];
        private readonly Dictionary<string, int> _byName = new(StringComparer.Ordinal);
        private readonly List<string?> _labels = [];
        private readonly List<List<(int Device, int Terminal)>> _pins = [];
        private readonly List<string> _boundary = [];

        public Build Boundary(params string[] nets)
        {
            _boundary.AddRange(nets);
            foreach (string net in nets) Index(net);
            return this;
        }

        public Build Label(string net, string label)
        {
            _labels[Index(net)] = label;
            return this;
        }

        public Build Device(
            string path, DeviceKind kind, string[] nets, params (string Name, double Value)[] values)
        {
            int device = _devices.Count;
            var terminals = new List<LvsTerminal>(nets.Length);
            for (int i = 0; i < nets.Length; i++)
            {
                int net = Index(nets[i]);
                terminals.Add(new LvsTerminal(i + 1, "", net));
                _pins[net].Add((device, i));
            }

            var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (name, value) in values) parameters[name] = value;

            _devices.Add(new LvsDevice(
                path, path, new DeviceType(kind, null, kind.ToString()), terminals, parameters,
                new LvsProvenance("fixture", path, 0, 0)));
            return this;
        }

        public LvsNetlist Netlist() => new(
            _devices,
            [.. Enumerable.Range(0, _labels.Count).Select(i => new LvsNet(i, _labels[i], _pins[i]))],
            [.. _boundary.Select(Index)],
            []);

        private int Index(string net)
        {
            if (_byName.TryGetValue(net, out int existing)) return existing;
            _labels.Add(net == "0" ? "0" : null);
            _pins.Add([]);
            return _byName[net] = _labels.Count - 1;
        }
    }

    // ── reading a reduced netlist back ────────────────────────────────────────────────────────

    /// <summary>Everything a merge could have got wrong, as one string: the surviving identities in
    /// order, what each stands for, its multiplicity, its nets and its values.</summary>
    private static string Dump(LvsNetlist netlist) => string.Join("\n", netlist.Devices.Select(d =>
        $"{d.Path} [{string.Join(",", d.Group)}] x{d.Multiplicity} "
        + $"nets={string.Join(",", d.Terminals.Select(t => t.NetIndex))} "
        + $"params={string.Join(",", d.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal)
                                                 .Select(p => $"{p.Key}={p.Value}"))}"));

    /// <summary>Everything one log says, as one string.</summary>
    private static string Dump(ReductionLog log) => string.Join("\n",
        [$"enabled={log.Enabled} passes={log.Passes} {log.DevicesBefore}->{log.DevicesAfter}",
         .. log.Groups.Select(g =>
             $"{g.Kind} {g.Type} {g.Path} [{string.Join(",", g.Members)}] applied={g.Applied}")]);

    /// <summary>ProvingDesignTests' own order-independent reading of a circuit — each device's
    /// terminal count and the multiset of net degrees it lands on. Deliberately NOT the net numbers,
    /// which the two sides have no reason to agree about.</summary>
    private static string Shape(LvsNetlist netlist)
    {
        string Degrees(LvsDevice d) => string.Join(
            ",", d.Terminals.Select(t => netlist.Nets[t.NetIndex].Pins.Count).Order());

        return string.Join(" | ", netlist.Devices
            .Select(d => $"{d.Designator}:{d.Terminals.Count}:{Degrees(d)}")
            .Order(StringComparer.Ordinal));
    }

    // ── brief 5's fixture ─────────────────────────────────────────────────────────────────────

    private static LvsNetlist ReadLayout(string cell)
    {
        string cellDir = Path.Combine(RepoRoot(), "examples", "LVS", cell);
        string clayPath = Path.Combine(
            cellDir, "layout", CellFolder.ResolvePrimary(cellDir, ViewType.Layout).ResolvedName!);

        var view = LayoutPersistence.LoadFromFile(clayPath);
        var (resolution, _) = TechnologyResolver.ResolveForDocument(
            view.TechRef, clayPath, null, new TechnologyCache());

        Assert.NotNull(resolution.Tech);
        return LayoutRead.Read(view, clayPath, cellDir, resolution.Tech);
    }

    private static LvsNetlist ReadSchematic(string cell)
    {
        string cellDir = Path.Combine(RepoRoot(), "examples", "LVS", cell);
        string path = Path.Combine(
            cellDir, "schematic", CellFolder.ResolvePrimary(cellDir, ViewType.Schematic).ResolvedName!);

        var (model, _, _) = SchematicPersistence.LoadFromFile(path);
        return SchematicRead.Read(model, path);
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir;
    }
}
