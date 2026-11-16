// ================================================================
//  ComparisonTests.cs — the gate for brief-lvs-7-comparison.md §7.
//
//  ── WHAT IS BEING PINNED ──────────────────────────────────────────────────────────────────────
//
//  The algorithm, on inputs small enough that a human can read the expected answer off the page.
//  The REAL designs are brief 5's, and their gates live in ProvingDesignTests.cs where the rest of
//  the fixture's assertions are — the two files together are brief 7's §7.
//
//  ── THE ONE THAT HAD TO BE WRITTEN FIRST ──────────────────────────────────────────────────────
//
//  `ATerminalPositionSwapIsAFinding`. R-lvs7-3c exists for exactly one failure — an implementation
//  that recolours from the multiset of neighbour colours and forgets WHICH TERMINAL each neighbour
//  attaches through. Such an implementation matches a drain to a source, and it passes every other
//  test in this file and in brief 5's.
//
//  ── WHY SO MANY OF THESE ARE HAND-BUILT NETLISTS ──────────────────────────────────────────────
//
//  `LvsNetlist` is a plain record and the comparison reads nothing else, so a three-terminal device
//  with its source and drain exchanged is four lines here and an entire fixture on disk otherwise.
//  Where the CLAIM is about a real design — does the correct board compare clean, does each fault
//  produce its own finding — the fixture is what the test uses, in the other file.
// ================================================================

using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Design.Layout.Lvs;
using CircuitRF.Design.Schematic;
using CircuitRF.Diagnostics;
using CircuitRF.Engine;

namespace CircuitRF.Ui.Tests.Lvs;

public sealed class ComparisonTests
{
    // ══ 4 — the terminal position is not optional ════════════════════════════════════════════════
    //
    // R-lvs7-3c. A FET whose drain and source are exchanged in the artwork is a different circuit,
    // and the only thing that says so is the terminal index. Three ports, all three nets held apart
    // by the cell's own port order, and two of the three terminals then land where they should not.

    [Fact]
    public void ATerminalPositionSwapIsAFinding()
    {
        var schematic = new Netlist()
            .Device("M1", DeviceKind.Transistor, "g", "d", "s")
            .Boundary("g", "d", "s").Build();

        var layout = new Netlist()
            .Device("M1", DeviceKind.Transistor, "g", "s", "d").Anchor("M1")
            .Boundary("g", "d", "s").Build();

        var findings = LvsCompare.Compare(schematic, layout).Findings;

        Assert.Equal(2, findings.Count(f => f.Id == "lvs.terminal.wrong-net"));
        Assert.Equal([2, 3], findings.Where(f => f.Id == "lvs.terminal.wrong-net")
                                     .Select(f => (int)f.Arguments["port"]!).Order());

        // And the control: the SAME three nets, the same device, terminals in the order the
        // schematic draws them. An implementation that ignored the index would pass this half and
        // fail nothing above.
        var correct = new Netlist()
            .Device("M1", DeviceKind.Transistor, "g", "d", "s").Anchor("M1")
            .Boundary("g", "d", "s").Build();
        Assert.True(LvsCompare.Compare(schematic, correct).IsClean);
    }

    // ══ 9 — an unequal symmetric class reports BOTH counts ══════════════════════════════════════
    //
    // R-lvs7-4d, and the shape of the fixture is the finding. Three interchangeable devices against
    // four can only share a colour while they share NO NET: colour refinement sees a net's degree,
    // so three caps on a rail and four caps on the same rail give that rail two different colours
    // and the devices on it are separated before they can be counted against each other.
    //
    // THAT case is not lost, it is reduction's (R-lvs6-1f, and the second half of this test): a
    // parallel group collapses to ONE device on each side carrying its multiplicity, the topologies
    // then match exactly, and three-against-four is a value difference — brief 10's
    // lvs.property.mismatch. Which is the whole reason reduction is on by default, and is load-
    // bearing for the comparison rather than merely tidy.

    [Fact]
    public void UnequalSymmetricClassesReportBothCounts()
    {
        var schematic = new Netlist()
            .Device("C1", DeviceKind.Capacitor, "a1", "b1")
            .Device("C2", DeviceKind.Capacitor, "a2", "b2")
            .Device("C3", DeviceKind.Capacitor, "a3", "b3").Build();

        var layout = new Netlist()
            .Device("@0", DeviceKind.Capacitor, "a1", "b1")
            .Device("@1", DeviceKind.Capacitor, "a2", "b2")
            .Device("@2", DeviceKind.Capacitor, "a3", "b3")
            .Device("@3", DeviceKind.Capacitor, "a4", "b4").Build();

        var result = LvsCompare.Compare(schematic, layout);

        Assert.Equal(3, result.Devices.Count);
        Assert.All(result.Devices, p => Assert.Equal(LvsPairedBy.Symmetry, p.By));

        var extra = Assert.Single(result.Findings, f => f.Id == "lvs.device.unmatched-layout");
        Assert.Equal(3, extra.Arguments["schematicCount"]);
        Assert.Equal(4, extra.Arguments["layoutCount"]);
    }

    [Fact]
    public void ThreeParallelCapsAgainstFourIsAMatchOnceTheyAreReduced()
    {
        static LvsNetlist Bank(int count)
        {
            var builder = new Netlist();
            for (int k = 0; k < count; k++)
                builder.Device($"C{k}", DeviceKind.Capacitor, "rail", "0");
            return builder.Boundary("rail").Label("0").Build();
        }

        var (schematic, _) = LvsReduce.Apply(Bank(3));
        var (layout, log)  = LvsReduce.Apply(Bank(4));

        Assert.Single(schematic.Devices);
        Assert.Equal(4, Assert.Single(layout.Devices).Multiplicity);
        Assert.Single(log.Of(ReductionKind.Parallel));

        // The topology matches; what differs is a value, and brief 10 is what says so.
        Assert.True(LvsCompare.Compare(schematic, layout).IsClean);
    }

    // ══ 3 — an arbitrary pairing is said out loud, and it is the SAME arbitrary pairing ══════════
    //
    // R-lvs7-4c and R-lvs7-6a in one test, because the two only matter together: a pairing that is
    // arbitrary AND unstable makes every finding downstream of it unreproducible.

    [Fact]
    public void AnAutomorphismIsReportedAndItsPairingIsStableAcrossRuns()
    {
        var schematic = new Netlist()
            .Device("C1", DeviceKind.Capacitor, "a", "b")
            .Device("C2", DeviceKind.Capacitor, "a", "b")
            .Boundary("a", "b").Build();
        var layout = new Netlist()
            .Device("@0", DeviceKind.Capacitor, "a", "b")
            .Device("@1", DeviceKind.Capacitor, "a", "b")
            .Boundary("a", "b").Build();

        var first = LvsCompare.Compare(schematic, layout);
        var note = Assert.Single(first.Findings, f => f.Id == "lvs.match.by-symmetry");
        Assert.Equal(2, note.Arguments["count"]);

        string Shape(LvsComparison c) => string.Join(
            " | ", c.Devices.Select(p => $"{p.SchematicName}={p.LayoutName}:{p.By}"));

        for (int run = 0; run < 10; run++)
            Assert.Equal(Shape(first), Shape(LvsCompare.Compare(schematic, layout)));
    }

    // ══ 5 — a contradicted anchor is reported, dropped, and does NOT cascade ════════════════════
    //
    // R-lvs7-2b/2c, on the real board, because the claim is about a design a person could draw.
    // The shunt at port 2 is told it is the series resistor and the series resistor is renamed to
    // something the schematic has never heard of: exactly one anchor is then wrong, none of its
    // terminals reaches the copper that pairing requires, and the four parts still match once the
    // name has been dropped.

    [Fact]
    public void AContradictedAnchorIsOneFindingAndThenACleanMatch()
    {
        var result = Board.Compare(view =>
        {
            var series = view.Instances.First(i => i.SchematicId == "R2");
            var far    = view.Instances.First(i => i.SchematicId == "R3");
            series.SchematicId = null;
            series.RefDes      = "R9";
            far.SchematicId    = "R2";
        });

        var finding = Assert.Single(result.Comparison.Findings, f => f.Severity > DiagnosticSeverity.Info);
        Assert.Equal("lvs.anchor.contradicted", finding.Id);
        Assert.Equal(DiagnosticSeverity.Warning, finding.Severity);

        // Every part still corresponds, and the two the name confused are paired on their structure.
        Assert.Equal(4, result.Comparison.Devices.Count);
        Assert.Empty(result.Comparison.UnmatchedSchematicDevices);
        Assert.Empty(result.Comparison.UnmatchedLayoutDevices);
        Assert.Equal("R9", result.Comparison.Devices.Single(p => p.SchematicName == "R2").LayoutName);
    }

    // ══ 6 — no anchors at all ═══════════════════════════════════════════════════════════════════
    //
    // R-lvs7-2e, and it is the case that proves the algorithm rather than the naming. Strip every
    // designator and every SchematicId from the correct board and it still matches.
    //
    // WHAT IS INTERCHANGEABLE HERE IS NOT WHAT BRIEF 5 EXPECTED, and the reason is worth reading.
    // R-lvs5-1c says the two shunt resistors are the automorphism. They are not, once the ports are
    // numbered: one sits at port 1 and the other at port 2, the boundary is anchored by POSITION on
    // both sides, and a correspondence may not map port 1 to port 2. What IS interchangeable is the
    // series resistor and the capacitor across it — both two-terminal, both between the same pair of
    // nets, and a land pattern says nothing about whether the part on it resists or stores. With no
    // designator to tell them apart nothing can, so the pairing is arbitrary and the report says so.

    [Fact]
    public void WithNoNameAtAllTheBoardStillMatchesOnItsStructure()
    {
        var result = Board.Compare(view =>
        {
            foreach (var inst in view.Instances) { inst.SchematicId = null; inst.RefDes = null; }
        });

        Assert.Equal(0, result.Comparison.Anchors);
        Assert.Equal(4, result.Comparison.Devices.Count);
        Assert.DoesNotContain(result.Comparison.Findings, f => f.Severity > DiagnosticSeverity.Info);

        Assert.Single(result.Comparison.Findings, f => f.Id == "lvs.match.structural-only");
        var symmetry = Assert.Single(result.Comparison.Findings, f => f.Id == "lvs.match.by-symmetry");
        Assert.Equal(2, symmetry.Arguments["count"]);

        // The two shunts are NOT the arbitrary pair: the port order tells them apart.
        Assert.All(new[] { "R1", "R3" }, d => Assert.Equal(
            LvsPairedBy.Structure, result.Comparison.Devices.Single(p => p.SchematicName == d).By));
    }

    // ══ 8 — a type mismatch is ONE line ═════════════════════════════════════════════════════════
    //
    // R-lvs7-5d. The placement declares itself a capacitor where the schematic draws a resistor.
    // Two "unmatched" lines would be a puzzle whose answer is this one sentence.

    [Fact]
    public void ATypeMismatchIsOneLineAndNotTwoUnmatchedDevices()
    {
        var result = Board.Compare(view => view.Instances
            .First(i => i.SchematicId == "R1").PartKind = LayoutPartKind.Name(SymbolKind.Capacitor));

        var finding = Assert.Single(result.Comparison.Findings, f => f.Severity > DiagnosticSeverity.Info);
        Assert.Equal("lvs.device.type-mismatch", finding.Id);
        Assert.Equal("R1", finding.Arguments["schematicPath"]);
        Assert.Equal("Resistor", finding.Arguments["schematicType"]);
        Assert.Equal("Capacitor", finding.Arguments["layoutType"]);

        Assert.DoesNotContain(result.Comparison.Findings,
                              f => f.Id.StartsWith("lvs.device.unmatched", StringComparison.Ordinal));
    }

    // ══ The property the whole series rests on: the comparator cannot tell the two sides apart ═══
    //
    // R-lvs3-1a, asked of the comparison itself. Swap the arguments and the same answer comes back
    // transposed — an asymmetry here is a bug nobody can see, because both inputs look right and
    // only the answer is wrong.

    [Fact]
    public void SwappingTheTwoSidesTransposesTheAnswer()
    {
        var schematic = new Netlist()
            .Device("R1", DeviceKind.Resistor, "a", "b")
            .Device("C1", DeviceKind.Capacitor, "b", "0")
            .Boundary("a").Label("0").Build();
        var layout = new Netlist()
            .Device("R1", DeviceKind.Resistor, "a", "b").Anchor("R1")
            .Device("C1", DeviceKind.Capacitor, "b", "0").Anchor("C1")
            .Boundary("a").Label("0").Build();

        var forward = LvsCompare.Compare(schematic, layout);
        var reverse = LvsCompare.Compare(layout, schematic);

        Assert.Equal(
            forward.Devices.Select(p => (p.SchematicName, p.LayoutName, p.By)).Order(),
            reverse.Devices.Select(p => (p.LayoutName, p.SchematicName, p.By)).Order());
        Assert.True(forward.IsClean && reverse.IsClean);
    }

    // ══ 10 — the refinement never caps ══════════════════════════════════════════════════════════
    //
    // R-lvs7-3d. The cap is a machine-check on the signatures, not a user-facing limit: the
    // partition strictly refines on every iteration that changes anything, so it cannot change
    // more times than there are objects. If this ever fires, the hashing is wrong.

    [Fact]
    public void RefinementNeverCapsOnAnyFixtureInTheRepo()
    {
        foreach (string cell in new[] { "Attenuator", "Attenuator broken", "Bias tee" })
        {
            var result = LvsRun.Run(Path.Combine(RepoRoot(), "examples", "LVS", cell));
            Assert.DoesNotContain(result.Diagnostics, d => d.Id == "lvs.compare.refinement-capped");
            Assert.InRange(result.Comparison.Iterations, 1, 8);
        }
    }

    // ══ 11 — the work grows near-linearly ═══════════════════════════════════════════════════════
    //
    // Overview §1i: gate on a counter, never on a wall clock. A ladder of N identical sections is
    // the worst case for a refinement — every section looks like every other until the ends
    // propagate inwards — so ten times the ladder is the honest ten-times fixture.

    [Fact]
    public void RefinementWorkGrowsNearLinearlyWithTheDesign()
    {
        long Work(int sections)
        {
            var schematic = Ladder(sections, "R");
            var layout    = Ladder(sections, "R");
            var result = LvsCompare.Compare(schematic, layout);
            Assert.True(result.IsClean, $"the {sections}-section ladder did not compare clean");
            return result.RefinementWork;
        }

        long small = Work(20);
        long large = Work(200);

        // Ten times the design, at most twenty times the work. The slack is the one n log n step in
        // the pass — grouping objects by their signature — which the counter deliberately includes
        // rather than hiding.
        Assert.InRange((double)large / small, 1.0, 20.0);
    }

    // ══ 12 — LvsRun is the only door ════════════════════════════════════════════════════════════
    //
    // R-lvs7-6b. The CLI calls it, the panel calls it, every non-unit test calls it. A second path
    // into the comparison is a second set of defaults and a second answer, and nothing would report
    // the drift.

    [Fact]
    public void LvsRunIsTheOnlyCallerOfTheComparison()
    {
        var callers = Directory
            .GetFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                                    StringComparison.Ordinal))
            .Where(f => StripComments(File.ReadAllText(f))
                        .Contains("LvsCompare.Compare", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["LvsRun.cs"], callers);
    }

    // ══ 13 — a cancelled run returns nothing and writes nothing ═════════════════════════════════
    //
    // R-lvs7-6c, on RunControl's own documented terms: cancelling abandons the run rather than
    // producing a partial result, so the caller catches and reports a cancelled run.

    [Fact]
    public void ACancelledRunReturnsNothing()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var control = new RunControl { Token = source.Token };

        Assert.Throws<OperationCanceledException>(
            () => LvsRun.Run(Path.Combine(RepoRoot(), "examples", "LVS", "Attenuator"), null, control));
    }

    // ── the correct board, mutated in memory ──────────────────────────────────────────────────

    /// <summary>
    /// Brief 5's correct board, with one bounded change applied to the loaded layout — R-lvs5-3e's
    /// own precedent, for faults that are about NAMES rather than about artwork. A `.clay` on disk
    /// per anchor mutation would be four more files nobody could regenerate.
    /// </summary>
    private static class Board
    {
        public static LvsRunResult Compare(Action<LayoutView> mutate)
        {
            string cell = Path.Combine(RepoRoot(), "examples", "LVS", "Attenuator");
            string clay = Path.Combine(cell, "layout", "Attenuator.clay");
            string csch = Path.Combine(cell, "schematic", "Attenuator.csch");

            var view = LayoutPersistence.LoadFromFile(clay);
            mutate(view);

            var (model, _, _) = SchematicPersistence.LoadFromFile(csch);
            var (resolution, _) = TechnologyResolver.ResolveForDocument(
                view.TechRef, clay, null, new TechnologyCache());

            return LvsRun.Run(view, clay, cell, resolution.Tech, model, csch);
        }
    }

    // ══ RF passives — a planned part compares on its specs ═══════════════════════════════════════
    //
    // MWPD's artwork is drawn from Z0, F and Gap, so those are what LVS reads off the generated cell and
    // compares with the schematic's. The divider Update Layout places compares clean; the same layout
    // against a schematic asking for another F is one property finding naming F.

    [Fact]
    public void AWilkinsonDividerComparesCleanAndAnFMismatchIsAFinding()
    {
        string scratch = Path.Combine(Path.GetTempPath(), "crf-lvs-wpd-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            var p = SchematicSync.SdlChecker.Prepare("F8-wilkinson-divider", scratch);
            var layout = p.Applied(p.Plan());
            Assert.Empty(SchematicSync.SdlChecker.Verify(p, layout));
            Assert.DoesNotContain(LvsRun.Run(p.CellDir).Findings, f => f.Severity == DiagnosticSeverity.Error);

            p.Model.Components.Single(c => c.InstanceName == "WPD1").Parameters.Single(q => q.Name == "F").Expression = "2.5";
            SchematicPersistence.SaveToFile(p.SchematicPath, p.Model, Path.GetFileNameWithoutExtension(p.SchematicPath));

            var errors = LvsRun.Run(p.CellDir).Findings.Where(f => f.Severity == DiagnosticSeverity.Error).ToList();
            var mismatch = Assert.Single(errors);
            Assert.Equal("lvs.property.mismatch", mismatch.Id);
            Assert.Equal("F", mismatch.Diagnostic.Arguments["name"]);
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>The two variants compare on their own specs too: an unequal split's K and a multi-section divider's Band
    /// are what its artwork was drawn from, so another value in the schematic is one property finding naming it. So is
    /// a ring's Corner, a name: mitred artwork against a schematic asking for curves.</summary>
    [Theory]
    [InlineData("F8b-wilkinson-unequal", "K", "2")]
    [InlineData("F8c-wilkinson-sections", "Band", "3")]
    [InlineData("F8d-wilkinson-mitred", "Corner", "Curve")]
    public void AWilkinsonVariantComparesCleanAndASpecMismatchIsAFinding(string design, string spec, string other)
    {
        string scratch = Path.Combine(Path.GetTempPath(), "crf-lvs-wpdv-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            var p = SchematicSync.SdlChecker.Prepare(design, scratch);
            var layout = p.Applied(p.Plan());
            Assert.Empty(SchematicSync.SdlChecker.Verify(p, layout));
            Assert.DoesNotContain(LvsRun.Run(p.CellDir).Findings, f => f.Severity == DiagnosticSeverity.Error);

            var divider = p.Model.Components.Single(c => c.InstanceName == "WPD1");
            if (divider.Parameters.SingleOrDefault(q => q.Name == spec) is { } row) row.Expression = other;
            else divider.Parameters.Add(new EditableParameter { Name = spec, Expression = other });
            SchematicPersistence.SaveToFile(p.SchematicPath, p.Model, Path.GetFileNameWithoutExtension(p.SchematicPath));

            var errors = LvsRun.Run(p.CellDir).Findings.Where(f => f.Severity == DiagnosticSeverity.Error).ToList();
            var mismatch = Assert.Single(errors);
            Assert.Equal("lvs.property.mismatch", mismatch.Id);
            Assert.Equal(spec, mismatch.Diagnostic.Arguments["name"]);
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>The hybrids (MBLC, MRRC) as Update Layout places them compare clean, and a spec changed on the
    /// schematic afterwards is exactly one property mismatch naming it. So does a radial stub (MRADIAL) on its angle,
    /// and N coupled lines (MCLINN) on one strip's own width; a delay line (MDLY) on its electrical length, and the
    /// meandered rat-race beside it on its turns; and the coupled array (MCLA) on its tap,
    /// its loading C and a custom end pattern's join moved to another strip's end.</summary>
    [Theory]
    [InlineData("F9-branch-line-hybrid", "BLC1", "N", "1")]
    [InlineData("F10-rat-race-hybrid", "RRC1", "F", "2.5")]
    [InlineData("F14-radial-stub", "RS1", "Angle", "90")]
    [InlineData("F15-coupled-lines", "CLN1", "W[2]", "1.5")]
    [InlineData("F29-delay-line", "DLY1", "Theta", "180")]
    [InlineData("F29-delay-line", "RRC1", "Meander", "3")]
    [InlineData("F31-coupled-array-interdigital", "CLA1", "Tap", "3.5")]
    [InlineData("F32-coupled-array-combline", "CLA1", "C", "1.6")]
    [InlineData("F33-coupled-array-pseudo", "CLA1", "L", "17")]
    [InlineData("F34-coupled-array-custom", "CLA1", "Ends", "SC JO JS")]
    public void AHybridComparesCleanAndASpecMismatchIsAFinding(string design, string instance, string spec, string changed)
    {
        string scratch = Path.Combine(Path.GetTempPath(), "crf-lvs-hyb-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            var p = SchematicSync.SdlChecker.Prepare(design, scratch);
            var layout = p.Applied(p.Plan());
            Assert.Empty(SchematicSync.SdlChecker.Verify(p, layout));
            Assert.DoesNotContain(LvsRun.Run(p.CellDir).Findings, f => f.Severity == DiagnosticSeverity.Error);

            p.Model.Components.Single(c => c.InstanceName == instance).Parameters.Single(q => q.Name == spec).Expression = changed;
            SchematicPersistence.SaveToFile(p.SchematicPath, p.Model, Path.GetFileNameWithoutExtension(p.SchematicPath));

            var errors = LvsRun.Run(p.CellDir).Findings.Where(f => f.Severity == DiagnosticSeverity.Error).ToList();
            var mismatch = Assert.Single(errors);
            Assert.Equal("lvs.property.mismatch", mismatch.Id);
            Assert.Equal(spec, mismatch.Diagnostic.Arguments["name"]);
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>The Gysel, Bagley and tee dividers (MGPD, MBPD, MTPD) as Update Layout places them compare clean, and a
    /// spec changed on the schematic afterwards is exactly one property mismatch naming it: the Gysel's F, the Bagley's
    /// Corner (a name: curved artwork against a schematic asking for mitred corners), and the tee's split K.</summary>
    [Theory]
    [InlineData("F16-gysel-divider", "GPD1", "F", "2.5")]
    [InlineData("F17-bagley-divider", "BPD1", "Corner", "Miter")]
    [InlineData("F18-tee-divider", "TPD1", "K", "1.4142")]
    public void ADividerComparesCleanAndASpecMismatchIsAFinding(string design, string instance, string spec, string changed)
    {
        string scratch = Path.Combine(Path.GetTempPath(), "crf-lvs-div-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            var p = SchematicSync.SdlChecker.Prepare(design, scratch);
            var layout = p.Applied(p.Plan());
            Assert.Empty(SchematicSync.SdlChecker.Verify(p, layout));
            Assert.DoesNotContain(LvsRun.Run(p.CellDir).Findings, f => f.Severity == DiagnosticSeverity.Error);

            var part = p.Model.Components.Single(c => c.InstanceName == instance);
            if (part.Parameters.SingleOrDefault(q => q.Name == spec) is { } row) row.Expression = changed;
            else part.Parameters.Add(new EditableParameter { Name = spec, Expression = changed });
            SchematicPersistence.SaveToFile(p.SchematicPath, p.Model, Path.GetFileNameWithoutExtension(p.SchematicPath));

            var errors = LvsRun.Run(p.CellDir).Findings.Where(f => f.Severity == DiagnosticSeverity.Error).ToList();
            var mismatch = Assert.Single(errors);
            Assert.Equal("lvs.property.mismatch", mismatch.Id);
            Assert.Equal(spec, mismatch.Diagnostic.Arguments["name"]);
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>The coupled-resonator filter (MCRC), one fixture per shape and one for Coupling = Full, compares clean as
    /// Update Layout places it, and one stored dimension or name changed on the schematic afterwards is exactly one
    /// property mismatch naming it: the artwork is drawn from the geometry the part stores.</summary>
    [Theory]
    [InlineData("F19-coupled-resonator-straight", "S2", "0.6")]
    [InlineData("F20-coupled-resonator-zigzag", "Tap", "7")]
    [InlineData("F21-coupled-resonator-hairpin", "Uc", "0.35")]
    [InlineData("F22-coupled-resonator-open-loop", "Arrangement", "Inline")]
    [InlineData("F23-coupled-resonator-ring", "L", "70")]
    [InlineData("F28-coupled-resonator-full", "Coupling", "Pairs")]
    public void ACoupledResonatorFilterComparesCleanAndAGeometryMismatchIsAFinding(string design, string spec, string changed)
    {
        string scratch = Path.Combine(Path.GetTempPath(), "crf-lvs-crc-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            var p = SchematicSync.SdlChecker.Prepare(design, scratch);
            var layout = p.Applied(p.Plan());
            Assert.Empty(SchematicSync.SdlChecker.Verify(p, layout));
            Assert.DoesNotContain(LvsRun.Run(p.CellDir).Findings, f => f.Severity == DiagnosticSeverity.Error);

            p.Model.Components.Single(c => c.InstanceName == "CRC1").Parameters.Single(q => q.Name == spec).Expression = changed;
            SchematicPersistence.SaveToFile(p.SchematicPath, p.Model, Path.GetFileNameWithoutExtension(p.SchematicPath));

            var errors = LvsRun.Run(p.CellDir).Findings.Where(f => f.Severity == DiagnosticSeverity.Error).ToList();
            var mismatch = Assert.Single(errors);
            Assert.Equal("lvs.property.mismatch", mismatch.Id);
            Assert.Equal(spec, mismatch.Diagnostic.Arguments["name"]);
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>The ladder (MLAD) as Update Layout places it compares clean, its via on G1, and its list changed on the
    /// schematic afterwards is exactly one property mismatch naming Sections, compared as text.</summary>
    [Fact]
    public void ALadderComparesCleanAndASectionsMismatchIsAFinding()
    {
        string scratch = Path.Combine(Path.GetTempPath(), "crf-lvs-lad-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            var p = SchematicSync.SdlChecker.Prepare("F24-microstrip-ladder", scratch);
            var layout = p.Applied(p.Plan());
            Assert.Empty(SchematicSync.SdlChecker.Verify(p, layout));
            Assert.DoesNotContain(LvsRun.Run(p.CellDir).Findings, f => f.Severity == DiagnosticSeverity.Error);

            p.Model.Components.Single(c => c.InstanceName == "LAD1").Parameters.Single(q => q.Name == "Sections").Expression =
                "line(W=1.1mm, L=5mm); stub(W=1.1mm, L=9mm, end=short); line(W=1.1mm, L=5mm)";
            SchematicPersistence.SaveToFile(p.SchematicPath, p.Model, Path.GetFileNameWithoutExtension(p.SchematicPath));

            var errors = LvsRun.Run(p.CellDir).Findings.Where(f => f.Severity == DiagnosticSeverity.Error).ToList();
            var mismatch = Assert.Single(errors);
            Assert.Equal("lvs.property.mismatch", mismatch.Id);
            Assert.Equal("Sections", mismatch.Diagnostic.Arguments["name"]);
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>The coupler options and the phase shifter as Update Layout places them compare clean, and a spec changed
    /// on the schematic afterwards is exactly one property mismatch naming it: MCLC's Band, Coupling (tandem and
    /// broadside) and Compensation (a name), MIDC's Coupling at six fingers, MSPS's Phase. The jumpers of a tandem or a
    /// Lange have no artwork here (inductors stand in for bond wires), so their unmatched-device findings are set aside.</summary>
    [Theory]
    [InlineData("F11b-coupler-sections", "CLC1", "Band", "3")]
    [InlineData("F11c-coupler-tandem", "CLC1", "Coupling", "4")]
    [InlineData("F11d-coupler-wiggly", "CLC1", "Compensation", "None")]
    [InlineData("F11e-coupler-broadside", "CLC1", "Coupling", "4")]
    [InlineData("F13b-interdigitated-six", "IDC1", "Coupling", "4")]
    [InlineData("F25-schiffman-phase-shifter", "SPS1", "Phase", "60")]
    public void ACouplerOptionComparesCleanAndASpecMismatchIsAFinding(string design, string instance, string spec, string changed)
    {
        string scratch = Path.Combine(Path.GetTempPath(), "crf-lvs-cpl-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            var p = SchematicSync.SdlChecker.Prepare(design, scratch);
            var layout = p.Applied(p.Plan());
            Assert.Empty(SchematicSync.SdlChecker.Verify(p, layout));
            List<CircuitRF.Design.Layout.Lvs.LvsFinding> Errors() => [.. LvsRun.Run(p.CellDir).Findings
                .Where(f => f.Severity == DiagnosticSeverity.Error && f.Id != "lvs.device.unmatched-schematic")];
            Assert.Empty(Errors());

            p.Model.Components.Single(c => c.InstanceName == instance).Parameters.Single(q => q.Name == spec).Expression = changed;
            SchematicPersistence.SaveToFile(p.SchematicPath, p.Model, Path.GetFileNameWithoutExtension(p.SchematicPath));

            var mismatch = Assert.Single(Errors());
            Assert.Equal("lvs.property.mismatch", mismatch.Id);
            Assert.Equal(spec, mismatch.Diagnostic.Arguments["name"]);
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch { /* best effort */ }
        }
    }

    // ── hand-built netlists ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// A netlist in as few lines as the type allows. <b>Nets are unlabelled unless
    /// <see cref="Label"/> says otherwise</b>, because a label is an anchor and a builder that
    /// named every net would make every structural test an anchored one.
    /// </summary>
    private sealed class Netlist
    {
        private readonly List<LvsDevice> _devices = [];
        private readonly List<string> _nets = [];
        private readonly List<List<(int Device, int Terminal)>> _pins = [];
        private readonly HashSet<string> _labelled = new(StringComparer.Ordinal);
        private readonly List<string> _boundary = [];

        public Netlist Device(string path, DeviceKind kind, params string[] nets)
        {
            int index = _devices.Count;
            var terminals = new List<LvsTerminal>();
            for (int t = 0; t < nets.Length; t++)
            {
                int net = Net(nets[t]);
                terminals.Add(new LvsTerminal(t + 1, $"{t + 1}", net));
                _pins[net].Add((index, t));
            }

            _devices.Add(new LvsDevice(
                path, path, new DeviceType(kind, null, kind.ToString()), terminals,
                new Dictionary<string, object?>(), new LvsProvenance("x", path, 0, 0)));
            return this;
        }

        /// <summary>The claim the last device makes about its counterpart — a <c>SchematicId</c>.</summary>
        public Netlist Anchor(string id)
        {
            _devices[^1] = _devices[^1] with { AnchorId = id };
            return this;
        }

        public Netlist Label(params string[] nets)
        {
            foreach (string n in nets) { Net(n); _labelled.Add(n); }
            return this;
        }

        public Netlist Boundary(params string[] nets)
        {
            foreach (string n in nets) { Net(n); _boundary.Add(n); }
            return this;
        }

        public LvsNetlist Build() => new(
            _devices,
            [.. _nets.Select((n, i) => new LvsNet(i, _labelled.Contains(n) ? n : null, _pins[i]))],
            [.. _boundary.Select(n => _nets.IndexOf(n))],
            []);

        private int Net(string name)
        {
            int index = _nets.IndexOf(name);
            if (index >= 0) return index;
            _nets.Add(name);
            _pins.Add([]);
            return _nets.Count - 1;
        }
    }

    /// <summary>N identical series sections between two ports — the refinement's worst case.</summary>
    private static LvsNetlist Ladder(int sections, string prefix)
    {
        var builder = new Netlist();
        for (int k = 0; k < sections; k++)
            builder.Device($"{prefix}{k}", DeviceKind.Resistor, $"n{k}", $"n{k + 1}")
                   .Device($"C{k}", DeviceKind.Capacitor, $"n{k + 1}", "0");
        return builder.Boundary("n0", $"n{sections}").Label("0").Build();
    }

    private static string StripComments(string code) =>
        Regex.Replace(Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline),
                      @"//[^\r\n]*", "");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
