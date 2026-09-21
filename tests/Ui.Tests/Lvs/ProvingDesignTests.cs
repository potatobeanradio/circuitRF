// ================================================================
//  ProvingDesignTests.cs — the gate for brief-lvs-5-proving-designs.md §6.
//
//  ── WHAT IS BEING PINNED ──────────────────────────────────────────────────────────────────────
//
//  `examples/LVS/` is the ORACLE every comparison brief in the series is gated against, so what
//  this file asserts is that the fixture still IS what it claims to be: three designs that load,
//  three terminal maps nobody guessed, artwork a committed script reproduces byte for byte, and
//  six faults each of which is exactly one bounded mutation.
//
//  ── WHY SO MUCH OF IT IS ABOUT THE FIXTURE AND NOT ABOUT LVS ──────────────────────────────────
//
//  Most of the brief's gate cannot run until brief 7 exists, and the parts that can are here now.
//  A fixture is not self-verifying: an example that drifts quietly stops meaning what it meant,
//  and every later brief would then be measured against something nobody checked. The comparison
//  assertions (gates 5, 6, 7) are added to THIS FILE as each of those briefs lands.
//
//  ── THE ONE THING THAT WOULD MAKE ALL OF IT WORTHLESS ─────────────────────────────────────────
//
//  A terminal map derived BY ORDER. Every cell in all three designs declares its own, and the test
//  below fails on a single `ByOrder` anywhere: a fixture that relies on a positional guess is
//  testing the guess.
// ================================================================

using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Design.Layout;
using CircuitRF.Diagnostics;
using CircuitRF.Design.Layout.Lvs;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Tests.Layout.PCells;

namespace CircuitRF.Ui.Tests.Lvs;

public sealed class ProvingDesignTests
{
    private static string Lvs(params string[] parts)
        => Path.Combine([RepoRoot(), "examples", "LVS", .. parts]);

    private const string Correct = "Attenuator";
    private const string Broken  = "Attenuator broken";
    private const string Mmic    = "Bias tee";

    // ══ 1 — the fixture loads, and `check` has nothing to say about it ═══════════════════════════
    //
    // R-lvs5-2a. The broken board is broken for LVS, not malformed: it must pass `check` with ZERO
    // errors or the fixture is testing the wrong thing. Run as the PROCESS, because `check` is the
    // verb a user would reach for and an in-process call would not prove the verb resolves the
    // workspace, its two technologies and its sub-cells the way the verb does.

    [Fact]
    public void TheWholeWorkspaceChecksClean()
    {
        // Default severity, deliberately: `--severity warning` would make the exit code count the
        // warnings, and warnings are reported EITHER WAY — a check that hid them to keep the exit
        // code clean makes the exit code useless, which is that flag's own rule.
        var (exit, stdout, stderr) = RunCli("check", Lvs());
        string all = stdout + stderr;

        Assert.DoesNotContain("error:", all, StringComparison.Ordinal);
        Assert.Equal(0, exit);

        // The warnings that ARE expected are named, so a new one is a failure rather than noise:
        // three cells declaring no analysis (they are cells, not benches) and the two boards'
        // designator labels, which carry no manufacturable area.
        var warnings = all.Split('\n').Where(l => l.StartsWith("warning:", StringComparison.Ordinal)).ToList();
        Assert.Equal(5, warnings.Count);
        Assert.Equal(3, warnings.Count(w => w.Contains("no analysis will dispatch", StringComparison.Ordinal)));
        Assert.DoesNotContain(warnings, w => w.Contains("Attenuator bench", StringComparison.Ordinal));
        Assert.Equal(2, warnings.Count(w => w.Contains("no manufacturable area", StringComparison.Ordinal)));
    }

    // ══ 2 — every cell says which layout pin is which port, and none of them guessed ════════════

    [Fact]
    public void EveryCellDeclaresItsTerminalMap()
    {
        // Every cell that HAS a layout view. The test bench is deliberately not one of them: it is
        // a bench, it draws nothing, and a cell with no artwork has no terminals to map — which
        // `TerminalMap` states as `None` and which is the right answer rather than a finding.
        var cells = Directory.EnumerateFiles(Lvs(), CellFolder.CcellFileName, SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName!)
            .Where(d => CellFolder.ResolvePrimary(d!, ViewType.Layout).ResolvedName is { Length: > 0 })
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();

        // Three designs, four board parts and four die parts.
        Assert.Equal(11, cells.Count);

        var bench = TerminalMap.ResolveCell(Lvs("Attenuator bench"));
        Assert.Equal(TerminalMapOrigin.None, bench.Origin);

        foreach (string cell in cells)
        {
            var map = TerminalMap.ResolveCell(cell!);
            Assert.True(map.Origin is TerminalMapOrigin.Declared or TerminalMapOrigin.ImportTable,
                $"{Path.GetFileName(cell)} resolved its terminal map by {map.Origin}: " +
                string.Join(" ", map.Notes));
            Assert.NotEmpty(map.Terminals);
            Assert.Equal(map.Terminals.Select(t => t.Port).Order(),
                         Enumerable.Range(1, map.Terminals.Count));
        }
    }

    // ══ 3 — the generators reproduce the committed artwork, byte for byte ════════════════════════
    //
    // R-lvs5-1e. This is the whole reason the artwork is generated: run the script, compare the
    // bytes, and a fixture that has been hand-edited since fails here rather than silently.

    [PythonFact]
    public void TheBoardGeneratorReproducesTheCommittedArtwork()
    {
        string work = CopyWorkspace();
        RunPython(Path.Combine(work, Correct, "layout", "Attenuator.gen.py"), work);
        AssertSameBytes(Lvs(Correct, "layout", "Attenuator.clay"),
                        Path.Combine(work, Correct, "layout", "Attenuator.clay"));
    }

    [PythonFact]
    public void TheBreakScriptReproducesTheCommittedBrokenArtwork()
    {
        string work = CopyWorkspace();
        RunPython(Path.Combine(work, Broken, "layout", "Attenuator.break.py"), work);
        AssertSameBytes(Lvs(Broken, "layout", "Attenuator.clay"),
                        Path.Combine(work, Broken, "layout", "Attenuator.clay"));
    }

    [PythonFact]
    public void TheMmicGeneratorReproducesTheCommittedArtworkAndItsPartCells()
    {
        string work = CopyWorkspace();
        RunPython(Path.Combine(work, Mmic, "layout", "Bias tee.gen.py"), work);

        AssertSameBytes(Lvs(Mmic, "layout", "Bias tee.clay"),
                        Path.Combine(work, Mmic, "layout", "Bias tee.clay"));

        foreach (string part in Directory.EnumerateDirectories(Lvs("parts")).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(part);
            AssertSameBytes(Path.Combine(part, ".ccell"), Path.Combine(work, "parts", name, ".ccell"));
            AssertSameBytes(Path.Combine(part, "layout", name + ".clay"),
                            Path.Combine(work, "parts", name, "layout", name + ".clay"));
        }
    }

    // ══ 4 — `--only Fn` is exactly one fault, six times ══════════════════════════════════════════
    //
    // R-lvs5-2b. Each single-fault layout differs from the correct board in a BOUNDED, asserted
    // way — and the six together are the union of the six singles, which is what makes "all six at
    // once" the realistic case rather than a seventh, different design.

    [PythonTheory]
    [InlineData("F1", 0,  0)]   // a link re-pointed: one shape out, one in
    [InlineData("F2", 0, -1)]   // C1's placement deleted
    [InlineData("F3", 0, +1)]   // an unwired R4 placed
    [InlineData("F4", +1, 0)]   // a spur of copper added
    [InlineData("F5", -1, 0)]   // a stitching via deleted
    [InlineData("F6", 0,  0)]   // R3 re-pointed at a different part
    public void EachFaultAloneIsOneBoundedMutation(string fault, int shapeDelta, int instanceDelta)
    {
        string work = CopyWorkspace();
        string outPath = Path.Combine(work, fault + ".clay");
        RunPython(Path.Combine(work, Broken, "layout", "Attenuator.break.py"),
                  work, "--only", fault, "-o", outPath);

        var good = LayoutPersistence.LoadFromFile(Lvs(Correct, "layout", "Attenuator.clay"));
        var bad  = LayoutPersistence.LoadFromFile(outPath);

        Assert.Equal(good.Shapes.Count + shapeDelta, bad.Shapes.Count);
        Assert.Equal(good.Instances.Count + instanceDelta, bad.Instances.Count);

        // And the fault is the one it says it is, named by what it touched.
        switch (fault)
        {
            case "F1":
                Assert.Equal(good.Instances.Count, bad.Instances.Count);
                Assert.NotEqual(Fingerprint(good), Fingerprint(bad));
                break;
            case "F2":
                Assert.DoesNotContain(bad.Instances, i => i.SchematicId == "C1");
                break;
            case "F3":
                var extra = Assert.Single(bad.Instances, i => i.RefDes == "R4");
                Assert.Null(extra.SchematicId);
                break;
            case "F5":
                Assert.Equal(good.Shapes.OfType<ViaShape>().Count() - 1,
                             bad.Shapes.OfType<ViaShape>().Count());
                break;
            case "F6":
                var r3 = Assert.Single(bad.Instances, i => i.SchematicId == "R3");
                Assert.EndsWith("R0402-150R", r3.CellRef, StringComparison.Ordinal);
                break;
        }
    }

    [PythonFact]
    public void AllSixTogetherAreTheUnionOfTheSix()
    {
        string work = CopyWorkspace();
        var singles = new List<LayoutView>();
        foreach (string fault in new[] { "F1", "F2", "F3", "F4", "F5", "F6" })
        {
            string outPath = Path.Combine(work, fault + ".clay");
            RunPython(Path.Combine(work, Broken, "layout", "Attenuator.break.py"),
                      work, "--only", fault, "-o", outPath);
            singles.Add(LayoutPersistence.LoadFromFile(outPath));
        }

        var good = LayoutPersistence.LoadFromFile(Lvs(Correct, "layout", "Attenuator.clay"));
        var all  = LayoutPersistence.LoadFromFile(Lvs(Broken, "layout", "Attenuator.clay"));

        Assert.Equal(good.Shapes.Count + singles.Sum(s => s.Shapes.Count - good.Shapes.Count),
                     all.Shapes.Count);
        Assert.Equal(good.Instances.Count + singles.Sum(s => s.Instances.Count - good.Instances.Count),
                     all.Instances.Count);
    }

    // ══ The correct board extracts to the netlist it is drawn as ════════════════════════════════
    //
    // Not gate 5 — that needs brief 7's comparison. What this pins is that BOTH SIDES read as the
    // same circuit, which is the precondition for gate 5 meaning anything at all.

    [Fact]
    public void TheCorrectBoardsTwoSidesReadAsTheSameCircuit()
    {
        var layout = ReadLayout(Correct);
        var schem  = ReadSchematic(Correct);

        Assert.Empty(Above(layout, DiagnosticSeverity.Info));
        Assert.Empty(Above(schem,  DiagnosticSeverity.Info));

        Assert.Equal(4, layout.Devices.Count);
        Assert.Equal(4, schem.Devices.Count);
        Assert.Equal(["C1", "R1", "R2", "R3"], layout.Devices.Select(d => d.Designator).Order());
        Assert.Equal(["C1", "R1", "R2", "R3"], schem.Devices.Select(d => d.Designator).Order());

        // Three nets on each side, one of them called "0" — the layout's from the pour's stamp,
        // the schematic's from its Ground symbols.
        Assert.Equal(3, layout.Nets.Count);
        Assert.Equal(3, schem.Nets.Count);
        Assert.Single(layout.Nets, n => n.Label == "0");
        Assert.Single(schem.Nets,  n => n.Label == "0");

        // Two ports on each side, in port order.
        Assert.Equal(2, layout.BoundaryNets.Count);
        Assert.Equal(2, schem.BoundaryNets.Count);

        Assert.Equal(Shape(layout), Shape(schem));
    }

    // ══ 7 (in part) — the shunt pair really is interchangeable ══════════════════════════════════
    //
    // R-lvs5-1c. Not the comparison's answer, which is brief 7's — what is pinned here is that the
    // AUTOMORPHISM EXISTS: swap R1 and R3 in the layout netlist and you get the same circuit back.
    // A fixture where the two shunts were distinguishable would quietly stop exercising the branch
    // that is hardest to get right, and nothing would say so.

    [Fact]
    public void TheTwoShuntResistorsAreIndistinguishable()
    {
        var layout = ReadLayout(Correct);

        var r1 = Assert.Single(layout.Devices, d => d.Designator == "R1");
        var r3 = Assert.Single(layout.Devices, d => d.Designator == "R3");

        Assert.Equal(r1.Type.CellDir, r3.Type.CellDir);
        Assert.Equal(r1.Terminals.Count, r3.Terminals.Count);

        // Each has one terminal on ground and one on a port net, and the two port nets are the
        // two boundary nets — so exchanging them maps the circuit onto itself.
        int ground = layout.Nets.Single(n => n.Label == "0").Index;
        int[] Free(LvsDevice d) => [.. d.Terminals.Select(t => t.NetIndex).Where(n => n != ground)];

        Assert.Equal(layout.BoundaryNets.Order(), Free(r1).Concat(Free(r3)).Order());
        Assert.Single(Free(r1));
        Assert.Single(Free(r3));
    }

    // ══ F4 and F5 are visible in the ARTWORK'S OWN netlist, before any comparison ═══════════════
    //
    // R-lvs5-2d. These are the two faults that justify the merge edges and the island structure,
    // and they are the two a comparison cannot invent after the fact: a short is two nets that
    // became one, an open is one net that became several. If the artwork does not read that way
    // here, no amount of comparison will make the report right — so the fixture is pinned at the
    // extraction rather than only at the finding.

    [PythonFact]
    public void TheShortMakesTheInputAndGroundOneNet()
    {
        var faulted = ReadFaultedBoard("F4");
        var good    = ReadLayout(Correct);

        Assert.Equal(good.Nets.Count - 1, faulted.Nets.Count);

        // Every terminal that was on the input is now on the ground net, and the ground net is
        // still the one carrying the pour's name.
        var ground = Assert.Single(faulted.Nets, n => n.Label == "0");
        var r1 = Assert.Single(faulted.Devices, d => d.Designator == "R1");
        Assert.All(r1.Terminals, t => Assert.Equal(ground.Index, t.NetIndex));
    }

    [PythonFact]
    public void TheDeletedStitchingViaLeavesTheGroundInTwoIslands()
    {
        var faulted = ReadFaultedBoard("F5");
        var good    = ReadLayout(Correct);

        // One more net than the correct board: the ground split in two, and no other net moved.
        Assert.Equal(good.Nets.Count + 1, faulted.Nets.Count);

        // R1's return is on the island, which now reaches nothing else on the board; R3's is on
        // the one the pour still names.
        var r1 = Assert.Single(faulted.Devices, d => d.Designator == "R1");
        var r3 = Assert.Single(faulted.Devices, d => d.Designator == "R3");
        int island = r1.Terminals[1].NetIndex;
        int main   = r3.Terminals[1].NetIndex;

        Assert.NotEqual(island, main);
        Assert.Equal("0", faulted.Nets[main].Label);
        Assert.Null(faulted.Nets[island].Label);
        Assert.Single(faulted.Nets[island].Pins);
    }

    // ══ 5 — the correct board compares with ZERO findings above info ════════════════════════════
    //
    // R-lvs5-1d, switched on by brief 7. The easy path: every component carries a footprint and a
    // designator, and the layout came from Update Layout, so every instance carries a SchematicId.
    // Not "zero errors" — zero findings above info, which is the bar an example has to clear before
    // any of the broken board's findings mean anything.

    [Fact]
    public void TheCorrectBoardComparesWithNothingAboveInfo()
    {
        var result = LvsRun.Run(Lvs(Correct));

        Assert.True(result.IsClean,
            "the correct board reported: " + string.Join("; ",
                result.Diagnostics.Where(d => d.Severity > DiagnosticSeverity.Info).Select(d => d.Render())));

        // Every part paired, by the name the designer gave it, and every net with it.
        Assert.Equal(4, result.Comparison.Devices.Count);
        Assert.Equal(4, result.Comparison.Anchors);
        Assert.All(result.Comparison.Devices, p => Assert.Equal(p.SchematicName, p.LayoutName));
        Assert.Equal(3, result.Comparison.Nets.Count);

        // R-lvs6-5c: the reduction mode is on the face of the result either way, both sides.
        Assert.Equal(2, result.Diagnostics.Count(d => d.Id == "lvs.reduce.mode"));
    }

    // ══ 6 — each fault is ONE finding, and all of them together are not sixty ═══════════════════
    //
    // R-lvs5-2a/c. By DIAGNOSTIC ID and by the objects the finding names, never by its sentence.
    //
    // F6 is the one that is not a topology fault: re-pointing R3 at a 150 Ω land pattern changes a
    // VALUE, which is R-lvs7-3a's own reason for keeping parameter values out of the matching. It
    // went unreported until brief 10 and is `lvs.property.mismatch` now, which is why the committed
    // six-fault board reports six.

    [PythonTheory]
    [InlineData("F1", "lvs.terminal.wrong-net")]
    [InlineData("F2", "lvs.device.unmatched-schematic")]
    [InlineData("F3", "lvs.device.unmatched-layout")]
    [InlineData("F4", "lvs.net.short")]
    [InlineData("F5", "lvs.net.open")]
    [InlineData("F6", "lvs.property.mismatch")]
    public void EachFaultProducesExactlyItsOwnFinding(string fault, string? expected)
    {
        var findings = CompareFaultedBoard(fault).Comparison.Findings
            .Where(f => f.Severity > DiagnosticSeverity.Info).ToList();

        if (expected is null) { Assert.Empty(findings); return; }
        Assert.Equal(expected, Assert.Single(findings).Id);
    }

    [Fact]
    public void AllSixFaultsTogetherAreSixFindingsAndTheSameSixEveryRun()
    {
        string Report(LvsRunResult r) => string.Join("\n", r.Comparison.Findings
            .Where(f => f.Severity > DiagnosticSeverity.Info)
            .Select(f => $"{f.Id}: {f.Render()}"));

        var first = LvsRun.Run(Lvs(Broken));
        var ids = first.Comparison.Findings
            .Where(f => f.Severity > DiagnosticSeverity.Info).Select(f => f.Id).ToList();

        Assert.Equal(
            ["lvs.device.unmatched-layout", "lvs.device.unmatched-schematic",
             "lvs.net.open", "lvs.net.short", "lvs.property.mismatch", "lvs.terminal.wrong-net"],
            ids);

        // The objects, not the sentences: F1 is R2's second terminal, F2 is C1, F3 is the R4 that
        // is on the board and not on the drawing, F6 is R3 pointing at the wrong part.
        Assert.Equal("R2", Single(first, "lvs.terminal.wrong-net").Arguments["path"]);
        Assert.Equal("C1", Single(first, "lvs.device.unmatched-schematic").Arguments["path"]);
        Assert.Equal("R4", Single(first, "lvs.device.unmatched-layout").Arguments["path"]);
        Assert.Equal("R3", Single(first, "lvs.property.mismatch").Arguments["schematicPath"]);

        // R-lvs5-1c's other half, and R-lvs7-6a: ten runs, one answer, in one order.
        for (int run = 0; run < 10; run++)
            Assert.Equal(Report(first), Report(LvsRun.Run(Lvs(Broken))));
    }

    private static Diagnostic Single(LvsRunResult result, string id)
        => Assert.Single(result.Comparison.Findings, f => f.Id == id);

    // ══ The MMIC does NOT compare clean, and this is what it reports ════════════════════════════
    //
    // NOT a gate brief 5 or brief 7 asked for — it is here so a real, known limitation cannot
    // quietly change. A spiral inductor IS one continuous piece of metal, so a galvanic extraction
    // reads its two terminals as one net and the comparison correctly concludes that two schematic
    // nets are one piece of copper. The artwork is right, the extraction is right and the
    // comparison is right; what is missing is the rule that a DEVICE's own internal copper is not
    // interconnect, which is brief 3's `IsDevice` walk and brief 14's recognition — not this
    // brief's, which changes no extraction. `src/Design/RESOLVED.md` carries the detail.

    [Fact]
    public void TheMmicSpiralStillReadsAsAShortBecauseItsCopperIsInterconnect()
    {
        var result = LvsRun.Run(Lvs(Mmic));

        Assert.Equal(4, result.Comparison.Devices.Count);
        Assert.Equal(4, result.Comparison.Anchors);

        var only = Assert.Single(result.Diagnostics.Where(d => d.Severity > DiagnosticSeverity.Info));
        Assert.Equal("lvs.net.short", only.Id);
        Assert.Equal(2, only.Arguments["count"]);

        // The inductor is the reason, on its face: both its terminals are on one layout net.
        var spiral = Assert.Single(result.Layout.Devices, d => d.Designator == "L1");
        Assert.Equal(spiral.Terminals[0].NetIndex, spiral.Terminals[1].NetIndex);
    }

    // ══ 8 — the MMIC reaches ground through metal nobody drew ═══════════════════════════════════

    [Fact]
    public void TheMmicReportsItsUndrawnGroundReferenceWithTheViaCount()
    {
        var netlist = ReadLayout(Mmic);

        var note = Assert.Single(netlist.Notes, d => d.Id == "lvs.ground.reference-undrawn");
        Assert.Equal("Backside Metal", note.Arguments["stackupEntry"]);
        Assert.Equal(2, note.Arguments["vias"]);
    }

    // ══ 9 — the MIM capacitor's two terminals resolve on their own layers ═══════════════════════
    //
    // R-lvs5-3d, and the reason it is a gate: terminal 1 is Metal1 and terminal 2 is Metal2, and
    // there is NO Metal1 copper under terminal 2. A layer-blind lookup therefore finds nothing
    // there and reports an open — on a design that is entirely correct, and on the one device kind
    // a board fixture cannot contain.

    [Fact]
    public void TheMimCapacitorsTerminalsResolveOnTheirOwnLayers()
    {
        var cap = LayoutPersistence.LoadFromFile(Lvs("parts", "MIM-0P8P", "layout", "MIM-0P8P.clay"));
        Assert.Equal(1, cap.Pins.Single(p => p.Name == "1").Layer.Layer);
        Assert.Equal(2, cap.Pins.Single(p => p.Name == "2").Layer.Layer);

        var netlist = ReadLayout(Mmic);
        var c1 = Assert.Single(netlist.Devices, d => d.Designator == "C1");
        var l1 = Assert.Single(netlist.Devices, d => d.Designator == "L1");

        Assert.Equal(2, c1.Terminals.Count);
        Assert.NotEqual(c1.Terminals[0].NetIndex, c1.Terminals[1].NetIndex);

        // Terminal 2 reaches the inductor through the Metal2 lead and a post — the net it lands on
        // has other pins on it, which an open never does.
        int rf2 = c1.Terminals[1].NetIndex;
        Assert.Equal(rf2, l1.Terminals[0].NetIndex);
        Assert.True(netlist.Nets[rf2].Pins.Count >= 2);
    }

    // ══ 10 (in part) — the tolerance derivation is reproducible ═════════════════════════════════
    //
    // R-lvs5-4a/c. The generator MEASURES the spread between what the schematic asks for and what
    // the geometry on the grid produces, and this asserts each one still sits under the bound the
    // completion note recorded. A PCell change that widens the spread past the tolerance brief 10
    // ships fails here, loudly, instead of quietly making LVS pass a real error.
    //
    // The bounds are the note's: the observed spread of a CORRECT design (the floor a tolerance
    // must clear) and 0.5 %, the provisional default (the ceiling it must stay under).

    [PythonFact]
    public void TheMeasuredSpreadStaysUnderTheProvisionalTolerance()
    {
        string work = CopyWorkspace();
        string output = RunPython(Path.Combine(work, Mmic, "layout", "Bias tee.gen.py"), work);

        var rows = Regex.Matches(output, @"^(\S+)\s+(Capacitance|Inductance|Resistance)\s+\S+\s+\S+\s+([0-9.]+)%",
                                 RegexOptions.Multiline);
        Assert.Equal(4, rows.Count);

        foreach (System.Text.RegularExpressions.Match row in rows)
        {
            double spread = double.Parse(row.Groups[3].Value, CultureInfo.InvariantCulture);
            Assert.InRange(spread, 0.0, 0.5);
        }

        // And the worst of them is where the note says it is — a spread that collapsed to zero
        // would mean the geometry stopped being snapped, which is a different design.
        Assert.Contains("worst 0.2", output, StringComparison.Ordinal);
    }

    // ══ 11 — it is a shipped example, offered by Tools > Examples ═══════════════════════════════

    [Fact]
    public void TheExamplesRowResolvesAndTheFolderOpens()
    {
        var row = Assert.Single(ExampleWorkspaces.All(RepoRoot()), e => e.Folder == "LVS");

        Assert.Equal("Layout versus schematic", row.Title);
        Assert.Contains("purpose", row.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(row.CwsPath));
        Assert.True(File.Exists(Path.Combine(row.Directory, "README.md")));
    }

    // ══ The two schematics are one file, twice ══════════════════════════════════════════════════
    //
    // R-lvs5-2a. If they ever differ, the fixture stops being about artwork.

    [Fact]
    public void TheBrokenBoardsSchematicIsAByteForByteCopy()
        => AssertSameBytes(Lvs(Correct, "schematic", "Attenuator.csch"),
                           Lvs(Broken,  "schematic", "Attenuator.csch"));

    // ══ The MMIC technology is the shipped one, unmodified ══════════════════════════════════════
    //
    // R-lvs5-3a. Partly the point is to prove the starter technology works, and a copy that had
    // drifted would prove the opposite while looking identical.

    [Fact]
    public void TheMmicTechnologyIsTheShippedOneByteForByte()
        => AssertSameBytes(
            Path.Combine(RepoRoot(), "src", "Design", "resources", "technologies",
                         "mmic-GaAs_2LM_100um.ctech"),
            Lvs("tech", "mmic-GaAs_2LM_100um.ctech"));

    // ── reading the fixture ───────────────────────────────────────────────────────────────────

    private static LvsNetlist ReadLayout(string cell)
    {
        string cellDir = Lvs(cell);
        string clay = CellFolder.ResolvePrimary(cellDir, ViewType.Layout).ResolvedName!;
        string clayPath = Path.Combine(cellDir, "layout", clay);

        var view = LayoutPersistence.LoadFromFile(clayPath);
        var (resolution, _) = TechnologyResolver.ResolveForDocument(
            view.TechRef, clayPath, null, new TechnologyCache());

        Assert.NotNull(resolution.Tech);
        return LayoutRead.Read(view, clayPath, cellDir, resolution.Tech);
    }

    /// <summary>The broken board carrying exactly one fault, extracted. Written into a throwaway
    /// copy of the workspace, so the committed fixture is never touched.</summary>
    private static LvsNetlist ReadFaultedBoard(string fault)
    {
        string work = CopyWorkspace();
        string clayPath = Path.Combine(work, Broken, "layout", "Attenuator.clay");
        RunPython(Path.Combine(work, Broken, "layout", "Attenuator.break.py"),
                  work, "--only", fault, "-o", clayPath);

        var view = LayoutPersistence.LoadFromFile(clayPath);
        var (resolution, _) = TechnologyResolver.ResolveForDocument(
            view.TechRef, clayPath, null, new TechnologyCache());

        Assert.NotNull(resolution.Tech);
        return LayoutRead.Read(view, clayPath, Path.Combine(work, Broken), resolution.Tech);
    }

    /// <summary>The broken board carrying exactly one fault, COMPARED — the same throwaway copy
    /// <see cref="ReadFaultedBoard"/> makes, run through the one door.</summary>
    private static LvsRunResult CompareFaultedBoard(string fault)
    {
        string work = CopyWorkspace();
        string cell = Path.Combine(work, Broken);
        RunPython(Path.Combine(cell, "layout", "Attenuator.break.py"),
                  work, "--only", fault, "-o", Path.Combine(cell, "layout", "Attenuator.clay"));
        return LvsRun.Run(cell);
    }

    private static LvsNetlist ReadSchematic(string cell)
    {
        string cellDir = Lvs(cell);
        string name = CellFolder.ResolvePrimary(cellDir, ViewType.Schematic).ResolvedName!;
        string path = Path.Combine(cellDir, "schematic", name);

        var (model, _, _) = SchematicPersistence.LoadFromFile(path);
        return SchematicRead.Read(model, path);
    }

    private static IReadOnlyList<Diagnostic> Above(
        LvsNetlist netlist, DiagnosticSeverity severity)
        => [.. netlist.Notes.Where(n => n.Severity > severity)];

    /// <summary>Everything the comparison would read, order-independent: each device's type and the
    /// multiset of net degrees its terminals land on. Deliberately NOT the net numbers, which the
    /// two sides have no reason to agree about.</summary>
    private static string Shape(LvsNetlist netlist)
    {
        string Degrees(LvsDevice d) => string.Join(
            ",", d.Terminals.Select(t => netlist.Nets[t.NetIndex].Pins.Count).Order());

        return string.Join(" | ", netlist.Devices
            .Select(d => $"{d.Designator}:{d.Terminals.Count}:{Degrees(d)}")
            .Order(StringComparer.Ordinal));
    }

    private static string Fingerprint(LayoutView view)
        => string.Join(";", view.Shapes.Select(s => s.GetType().Name + LayoutPersistence.Serialize(
            new LayoutView { Shapes = { s } })).Order(StringComparer.Ordinal));

    // ── running things ────────────────────────────────────────────────────────────────────────

    /// <summary>A throwaway copy of the whole workspace, so a generator run never touches the
    /// repository — every one of these scripts WRITES, and a test that regenerated the committed
    /// fixture could not then tell whether the committed fixture was right.</summary>
    private static string CopyWorkspace()
    {
        string work = Path.Combine(Path.GetTempPath(), "crf-lvs5-" + Guid.NewGuid().ToString("N")[..12]);
        foreach (string dir in Directory.EnumerateDirectories(Lvs(), "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(work, Path.GetRelativePath(Lvs(), dir)));
        Directory.CreateDirectory(work);
        foreach (string file in Directory.EnumerateFiles(Lvs(), "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(Lvs(), file);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(work, rel))!);
            File.Copy(file, Path.Combine(work, rel), overwrite: true);
        }
        return work;
    }

    private static string RunPython(string script, string workspaceRoot, params string[] extra)
    {
        var psi = new ProcessStartInfo(PythonRunner.Interpreter!)
        {
            WorkingDirectory       = RepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(workspaceRoot);
        foreach (string a in extra) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        string output = proc.StandardOutput.ReadToEnd();
        string error  = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        Assert.True(proc.ExitCode == 0,
                    $"{Path.GetFileName(script)} exited {proc.ExitCode}\n{output}\n{error}");
        return output;
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory       = RepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.ArgumentList.Add(CliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(ProvingDesignTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        return Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
    }

    private static void AssertSameBytes(string expected, string actual)
    {
        Assert.True(File.Exists(actual), $"{actual} was not written");
        Assert.True(File.ReadAllBytes(expected).SequenceEqual(File.ReadAllBytes(actual)),
                    $"{Path.GetFileName(expected)} differs from the committed copy");
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir;
    }
}
