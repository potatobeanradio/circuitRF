// ================================================================
//  PowerRailExampleTests.cs — the shipped Power Rail example, run.
//
//  brief-footprint-5-power-rail-example.md §6. The example is a document a reader opens and a page
//  of specific numbers they check, so what is gated here is both halves:
//
//   1. THE BOARD IS MADE OF PARTS. Thirteen capacitors and a ferrite, each an INSTANCE of a
//      footprint cell with a reference designator on silkscreen — because before this the artwork
//      was eighty loose rectangles, and a reader could not tell which pads were one part, could not
//      select one, and could not take one off the board.
//
//   2. THE THREE FINDINGS SURVIVE, BY SHAPE AND NOT BY NUMBER. The numbers moved when the field was
//      re-spaced and they are expected to; what may not move is that half the drop is one thin BOT
//      run, that two anti-resonances are named with a converter harmonic inside 2 % of the second,
//      and that one purchased part number placed nine times produces three separated mounting-loop
//      tiers.
//
//   3. THE README'S NUMBERS ARE THE RUN'S. A published figure that no longer matches what the
//      example produces is worse than no figure, because a reader checks it and concludes the tool
//      is wrong. So they are parsed back out of the README and compared to a live run — which is
//      what makes "every number re-measured" durable rather than a one-off act of care.
//
//   4. AND THE GENERATOR STILL REFUSES. Its two audits — a netlist pad with no land under it, and a
//      power via with no anti-pad — are what stand between this example and a board that solves as
//      a shorted one with nothing said. A refusal nobody has ever seen fire is a refusal nobody
//      knows still works, so both are provoked here on a COPY of the workspace.
// ================================================================

using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CircuitRF.Ui.RailRf;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Examples;

public sealed class PowerRailExampleTests(ITestOutputHelper output)
{
    private static string Root() => PowerRailFootprintCells.ExampleRoot();
    private static string Cell() => Path.Combine(Root(), "Sensor board");
    private static string ClayPath() => Path.Combine(Cell(), "layout", "Board.clay");
    private static string CrailPath() => Path.Combine(Cell(), "Sensor board.crail");
    private static string ReadmePath() => Path.Combine(Root(), "README.md");

    /// <summary>The window's own load, run to completion synchronously — the shape
    /// <c>DocRailFixtures</c> uses, for the same reason: the Fast loop then completes before
    /// <c>RunCommand.Execute</c> returns.</summary>
    private static RailRfViewModel Solved(
        bool accurate = false, Action<RailDocument>? edit = null, Action<LayoutView>? editBoard = null)
    {
        string crail = CrailPath(), clay = ClayPath();
        var document = RailDocumentIo.LoadFromFile(crail);
        var view = LayoutPersistence.LoadFromFile(clay);
        var technology = PowerRailFootprintCells.ExampleTechnology();
        edit?.Invoke(document);
        editBoard?.Invoke(view);

        // The companions are what make a REFDES resolve to copper. The window's own open reads them;
        // ApplyImport does not, so they are read here through the same walk.
        var netlist = RailArtwork.ResolveBoardNetlist(document, crail, view.DbuPerMicron, out _, out _);
        var placement = RailArtwork.ResolvePlacement(document, crail, view.DbuPerMicron, out _, out _);

        var vm = new RailRfViewModel(document, crail)
        {
            PostToUi = a => a(),
            RunOffThread = (work, _) => System.Threading.Tasks.Task.FromResult(work()),
        };

        vm.ApplyImport(
            new RailImportOptions(),
            new RailBoardInputs
            {
                Shapes = RailArtwork.FlattenedShapes(view, clay, technology),
                View = view,
                Technology = technology,
                DbuPerMicron = view.DbuPerMicron,
                ArtworkCellRef = clay,
            },
            placement: placement,
            netlist: netlist,
            library: PartLibraryIo.LoadFromFile(Path.Combine(Root(), "parts", "decoupling.crlib")));

        vm.ConfirmReference();
        vm.RunCommand.Execute(null);
        if (accurate) vm.AccuracyCommand.Execute(null);

        Assert.Null(vm.Refusal?.Sentence);
        return vm;
    }

    // ══ 2. The stackup did not move ═════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>R-fp5-1b.</b> The three drawing layers the technology gained are NON-CONDUCTING, and they
    /// must not move a single millivolt. The invariance was checked directly when they were added —
    /// Q0's answer was bit-identical with no geometry changed — and what holds it open afterwards is
    /// this: the stackup's own numbers, pinned, so a later edit to the layer table that reached the
    /// conductors would fail here rather than quietly re-pricing every board.
    /// </summary>
    [Fact]
    public void TheStackupIsUnchangedByTheDrawingLayers()
    {
        var tech = PowerRailFootprintCells.ExampleTechnology();
        var stack = tech.Stackup.Layers;

        Assert.Equal(8, stack.Count);
        Assert.Equal(["TOP", "PP1", "GND", "CORE", "IN3", "PP2", "BOT", "PTH"],
                     stack.Select(l => l.Name));

        foreach (var (name, thickness) in new (string, long)[]
                 { ("TOP", 35_000), ("GND", 17_500), ("IN3", 17_500), ("BOT", 35_000) })
        {
            var c = stack.Single(l => l.Name == name);
            Assert.Equal(StackupKind.Conductor, c.Kind);
            Assert.Equal(thickness, c.ThicknessDbu);
            Assert.Equal(5.8e7, c.SigmaSm);
        }

        foreach (var (name, thickness) in new (string, long)[]
                 { ("PP1", 200_000), ("CORE", 1_065_000), ("PP2", 200_000) })
        {
            var d = stack.Single(l => l.Name == name);
            Assert.Equal(StackupKind.Dielectric, d.Kind);
            Assert.Equal(thickness, d.ThicknessDbu);
            Assert.Equal(4.3, d.Epsr);
            Assert.Equal(0.02, d.TanD);
        }

        // And none of the three new layers is IN the stackup — a drawing layer that got into it
        // would be copper with no thickness, which is a perfect plane and an optimistic answer.
        foreach (var key in new[] { new LayerKey(5, 0), new LayerKey(6, 0), new LayerKey(7, 0) })
        {
            Assert.Contains(tech.Layers, l => l.Key == key);
            Assert.DoesNotContain(stack, l => l.DrawingLayers.Contains(key));
        }
    }

    // ══ 3 & 4. The board is made of parts, and every part is named ══════════════════════════════

    /// <summary>
    /// <b>R-fp5-2a / gate 3 and 4.</b> Seventeen placements; every one carries a designator, every
    /// designator is drawn on the technology's silkscreen role, and every refdes the board netlist
    /// names has a placement.
    ///
    /// <para><b>Seventeen rather than fourteen since brief-authored-board-3.</b> The regulator and
    /// the two loads are now placements of a ONE-PIN, NO-COPPER cell rather than facts stated only
    /// in the companion `.ipc` — which is that series' governing rule applied to this board: a
    /// board circuitRF drew must state everything the netlist would state, or the netlist cannot be
    /// projected from it and `U1.VDD` resolves to nothing. They draw no copper, so the artwork the
    /// solver sees is unchanged; what they add is the placement and the designator.</para>
    /// </summary>
    [Fact]
    public void EveryPartIsAnInstanceWithADesignatorDrawnOnSilk()
    {
        string clay = ClayPath();
        var view = LayoutPersistence.LoadFromFile(clay);
        var tech = PowerRailFootprintCells.ExampleTechnology();

        Assert.Equal(17, view.Instances.Count);

        string layoutDir = Path.GetDirectoryName(Path.GetFullPath(clay))!;
        foreach (var inst in view.Instances)
        {
            var res = CellLayoutResolver.Resolve(inst.CellRef, layoutDir);
            Assert.Equal(CellLayoutState.Resolved, res.State);
            Assert.False(string.IsNullOrWhiteSpace(inst.DisplayRefDes));

            // A terminal states one pin and draws nothing; a part states two and draws a land under
            // each, which is what makes it a land pattern rather than a list of coordinates.
            bool terminal = inst.DisplayRefDes!.StartsWith('U');
            Assert.Equal(terminal ? 1 : 2, res.View!.Pins.Count);
            Assert.Equal(terminal ? 0 : 2,
                res.View.Shapes.Count(s => s.Layer == new LayerKey(1, 0) && s.Pin is "1" or "2"));
            if (terminal) Assert.Empty(res.View.Shapes);
        }

        // Thirteen capacitors, one ferrite, and the three terminals — named as the .crail names them.
        Assert.Equal(
            ["C1", "C10", "C11", "C12", "C13", "C2", "C3", "C4", "C5", "C6", "C7", "C8", "C9",
             "FB1", "U1", "U2", "U3"],
            view.Instances.Select(i => i.DisplayRefDes!).Order(StringComparer.Ordinal));

        // The designators are ARTWORK on the silkscreen role, which is what the flatten emits and
        // what Gerber, DRC and `circuitrf check` all see. Not editor chrome.
        var silk = new LayerKey(6, 0);
        var flat = RailArtwork.FlattenedShapes(view, clay, tech);
        int labels = flat.Count(s => s is LabelShape { IsPort: false } && s.Layer == silk);
        Assert.Equal(17, labels);

        // Every refdes the netlist names is placed, and every placement is in the netlist.
        var document = RailDocumentIo.LoadFromFile(CrailPath());
        var netlist = RailArtwork.ResolveBoardNetlist(
            document, CrailPath(), view.DbuPerMicron, out _, out _);
        var pads = PdnBoardPads.PadsOf(netlist!);
        var placed = view.Instances.Select(i => i.DisplayRefDes!).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string refdes in pads.Select(p => p.Refdes!).Distinct())
            Assert.Contains(refdes, placed);
        foreach (string refdes in placed)
            Assert.Contains(pads, p => string.Equals(p.Refdes, refdes, StringComparison.OrdinalIgnoreCase));

        output.WriteLine($"{view.Instances.Count} placements, {labels} designators on silk, {pads.Count} pads");
    }

    // ══ 5. The generator's own refusal still fires ══════════════════════════════════════════════

    /// <summary>
    /// <b>R-fp5-2d / gate 5.</b> Break an anti-pad and assert the generator writes nothing. Run on
    /// a COPY, so the shipped workspace is untouched whatever happens here.
    ///
    /// <para><b>There used to be a second row here, <c>CRF_BREAK_PAD</c></b>, which moved a netlist
    /// pad off its land. It is gone because the defect is (brief-authored-board-3 R-ab3-4c): the
    /// netlist is PROJECTED from the lands now, so a pad that stands on no land is not a thing the
    /// generator can write. The invariant became structural instead of checked. The anti-pad half
    /// is a real statement about geometry that no writer could make, and it stays.</para>
    /// </summary>
    [Theory]
    [InlineData("CRF_BREAK_ANTIPAD", "its anti-pad is missing")]
    public void TheBoardGeneratorRefusesAndWritesNothing(string fault, string expected)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "crf-rail-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            CopyTree(Root(), tmp);
            string board = Path.Combine(tmp, "Sensor board", "layout", "Board.clay");
            string before = File.ReadAllText(board);

            var (code, stdout) = RunGenerator(tmp, fault);

            Assert.NotEqual(0, code);
            Assert.Contains("AUDIT:", stdout, StringComparison.Ordinal);
            Assert.Contains(expected, stdout, StringComparison.Ordinal);
            Assert.Equal(before, File.ReadAllText(board));
            output.WriteLine(stdout.Trim());
        }
        finally { try { Directory.Delete(tmp, true); } catch { /* best effort */ } }
    }

    /// <summary>
    /// And with no fault it writes the board that is committed, byte for byte.
    ///
    /// <para><b>The `.clay` is now the ONLY file it writes</b> (R-ab3-4a/b): the netlist and the
    /// placement table beside it are projected from that `.clay` by <c>circuitrf netlist</c>, which
    /// <c>NetlistBoardVerbTests</c> holds.</para>
    /// </summary>
    [Fact]
    public void TheCommittedBoardIsWhatTheGeneratorWrites()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "crf-rail-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            CopyTree(Root(), tmp);
            var (code, stdout) = RunGenerator(tmp, null);
            Assert.Equal(0, code);
            output.WriteLine(stdout.Trim());

            foreach (string relative in new[]
                     { "Sensor board/layout/Board.clay",
                       "footprints/TERM-OUT/layout/TERM-OUT.clay",
                       "footprints/TERM-VDD/layout/TERM-VDD.clay" })
                Assert.Equal(File.ReadAllText(Path.Combine(Root(), relative)),
                             File.ReadAllText(Path.Combine(tmp, relative)));

            // R-ab3-4b. It writes the artwork and NOTHING else now; the two companion files are
            // the verb's, and a generator that still wrote them would be the second projection
            // this series exists to prevent.
            string generator = File.ReadAllText(
                Path.Combine(Root(), "Sensor board", "layout", "Board.gen.py"));
            Assert.DoesNotContain("Board.ipc\"), \"w\"", generator, StringComparison.Ordinal);
            Assert.DoesNotContain("Board.placement.csv\"), \"w\"", generator, StringComparison.Ordinal);
        }
        finally { try { Directory.Delete(tmp, true); } catch { /* best effort */ } }
    }

    // ══ 6. The three findings survive — by SHAPE, not by number ═════════════════════════════════

    [Fact]
    public void TheThreeFindingsThisExampleExistsForAllSurvive()
    {
        var vm = Solved();
        var dc = vm.Current!.Result.Rails[0];
        var sweep = vm.SweepByModel[PdnModelKind.Fast];

        // ── Q0: half the drop is one thin BOT run ──────────────────────────────────────────────
        var worst = dc.Breakdown[0];
        Assert.Contains("BOT copper", worst.Label, StringComparison.Ordinal);
        Assert.InRange(worst.ShareOfTotal, 0.40, 0.55);
        Assert.Contains("0.2", worst.Label, StringComparison.Ordinal);   // still ~0.20 mm wide
        Assert.True(dc.WithinDropBudget, "the example's own budget is no longer met.");

        // ── Q1: two anti-resonances, and a converter harmonic inside 2 % of the second ─────────
        var peaks = sweep.Ports[0].Peaks;
        Assert.Equal(2, peaks.Count);
        Assert.All(peaks, p => Assert.True(p.IsAttributed, p.Contributors));
        Assert.True(peaks[0].FrequencyHz < peaks[1].FrequencyHz);

        var hit = sweep.Coincidences.FirstOrDefault(c =>
            ReferenceEquals(c.Peak, peaks[1]) || c.Peak.FrequencyHz == peaks[1].FrequencyHz);
        Assert.NotNull(hit);
        Assert.Contains("converter", hit.AggressorName, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(hit.SeparationFraction, 0.0, 0.02);

        // ── Q2: three separated mounting-loop tiers from ONE purchased part number ─────────────
        var byRefdes = vm.Parts.ToDictionary(p => p.Refdes, p => p.MountingInductanceHenries);
        double? ViaInLand = byRefdes["C1"], ShortFanout = byRefdes["C4"], LongFanout = byRefdes["C11"];
        Assert.NotNull(ViaInLand); Assert.NotNull(ShortFanout); Assert.NotNull(LongFanout);

        Assert.Equal(byRefdes["C1"], byRefdes["C2"]);
        Assert.Equal(byRefdes["C4"], byRefdes["C5"]);
        Assert.Equal(byRefdes["C11"], byRefdes["C12"]);

        // One part number, three tiers, each clearly separated from the next — 20 % is the gap that
        // makes them different rows of a table rather than measurement noise.
        Assert.True(ShortFanout > ViaInLand * 1.2, $"{ShortFanout} vs {ViaInLand}");
        Assert.True(LongFanout > ShortFanout * 1.2, $"{LongFanout} vs {ShortFanout}");

        var ranked = sweep.Removal.ToDictionary(r => r.Name.Split(' ')[0], r => r.GrowthDb);
        Assert.True(ranked["C1"] > ranked["C4"] && ranked["C4"] > ranked["C11"],
            "the three tiers no longer rank in mounting-loop order.");

        output.WriteLine($"Q0 {worst.ShareOfTotal:P0} on {worst.Label}");
        output.WriteLine($"Q1 {peaks[0].FrequencyHz / 1e6:0.###} MHz, {peaks[1].FrequencyHz / 1e6:0.###} MHz, " +
                         $"{hit.SeparationFraction:P1} from {hit.AggressorName}");
        output.WriteLine($"Q2 {ViaInLand * 1e12:0.#} / {ShortFanout * 1e12:0.#} / {LongFanout * 1e12:0.#} pH");
    }

    // ══ 7. The README's numbers are the run's ═══════════════════════════════════════════════════

    /// <summary>
    /// <b>R-fp5-5a / gate 7.</b> Every headline figure the README publishes, parsed back out of it
    /// and compared to a live run. This is the one that makes "re-measured" durable: without it the
    /// page drifts the first time anybody moves a part, and a reader who checks a number and finds
    /// it wrong concludes the tool is.
    /// </summary>
    [Fact]
    public void EveryNumberTheReadmePublishesIsWhatTheExampleProduces()
    {
        string readme = File.ReadAllText(ReadmePath());
        var vm = Solved();
        var dc = vm.Current!.Result.Rails[0];
        var sweep = vm.SweepByModel[PdnModelKind.Fast];

        // ── Q0 ─────────────────────────────────────────────────────────────────────────────────
        double fastDrop = dc.Ports[0].DropV!.Value * 1e3;
        Published(readme, @"\| Fast \(the default\) \| ([\d.]+) mV \|", fastDrop, 1e-3, "Fast drop");
        Published(readme, @"^\s*([\d.]+) mV\s+\d+%\s+[\d.]+ mm of [\d.]+ mm BOT copper",
                  dc.Breakdown[0].DropV * 1e3, 1e-3, "the BOT copper row");
        Published(readme, @"the drop falls to \*\*([\d.]+) mV\*\*",
                  WhatIf(w => WidenTheBotRun(w)).Current!.Result.Rails[0].Ports[0].DropV!.Value * 1e3,
                  1e-3, "the widened-run drop");

        // ── the plane ──────────────────────────────────────────────────────────────────────────
        var plane = Regex.Match(dc.PlaneCapacitanceLine, @"([\d.]+) pF.*?([\d.]+) cm");
        Assert.True(plane.Success, dc.PlaneCapacitanceLine);
        Published(readme, @"\*\*([\d.]+) pF over [\d.]+ cm", Number(plane.Groups[1]), 1e-3, "plane C");
        Published(readme, @"\*\*[\d.]+ pF over ([\d.]+) cm", Number(plane.Groups[2]), 1e-3, "plane area");

        // ── Q1 ─────────────────────────────────────────────────────────────────────────────────
        Published(readme, @"passes by ([\d.]+) dB at its worst", sweep.WorstMarginDb!.Value, 1e-3, "worst margin");

        var peaks = sweep.Ports[0].Peaks;
        Published(readme, @"\| ([\d.]+) MHz, [\d.]+ mΩ \| L\(C9\)", peaks[0].FrequencyHz / 1e6, 1e-3, "peak 1 f");
        Published(readme, @"\| [\d.]+ MHz, ([\d.]+) mΩ \| L\(C9\)", peaks[0].PeakOhms * 1e3, 1e-3, "peak 1 Z");
        Published(readme, @"\| ([\d.]+) MHz, [\d.]+ mΩ \| L\(C7", peaks[1].FrequencyHz / 1e6, 1e-3, "peak 2 f");
        Published(readme, @"\| [\d.]+ MHz, ([\d.]+) mΩ \| L\(C7", peaks[1].PeakOhms * 1e3, 1e-3, "peak 2 Z");

        var hit = sweep.Coincidences.First(c => c.Peak.FrequencyHz == peaks[1].FrequencyHz);
        Published(readme, @"sits ([\d.]+) % away from the second", hit.SeparationFraction * 100, 0.05, "coincidence");

        // ── Q2 ─────────────────────────────────────────────────────────────────────────────────
        var ranked = sweep.Removal.ToDictionary(r => r.Name.Split(' ')[0], r => r);
        foreach (string refdes in new[] { "C10", "C7", "C1", "C9", "C4", "C11" })
        {
            Published(readme, $@"^{refdes}\s+\S+.*?\s+(-?[\d.]+) dB\s+->", ranked[refdes].GrowthDb, 0.05,
                      $"{refdes}'s growth");
            Published(readme, $@"^{refdes}\s+\S+.*?->\s+(-?[\d.]+) dB", ranked[refdes].WorstMarginDb, 0.05,
                      $"{refdes}'s margin without it");
        }

        var loops = vm.Parts.ToDictionary(p => p.Refdes, p => p.MountingInductanceHenries);
        Published(readme, @"\*\*([\d.]+) pH\*\* \| 0.7 dB", loops["C4"]!.Value * 1e12, 0.05, "C4's loop");
        Published(readme, @"\*\*([\d.]+) pH\*\* \| 0.5 dB", loops["C11"]!.Value * 1e12, 0.05, "C11's loop");
        Published(readme, @"C11: ([\d.]+) pH", loops["C11"]!.Value * 1e12, 0.05, "C11's decomposition");

        Published(readme, @"worst margin goes to \*\*−([\d.]+) dB\*\*",
                  -WhatIf(d => Unmount(d, "C10")).SweepByModel[PdnModelKind.Fast].WorstMarginDb!.Value,
                  0.05, "C10 unmounted");

        // ── Fast against Accurate ──────────────────────────────────────────────────────────────
        var accurate = Solved(accurate: true).Current!.Result.Rails[0];
        Published(readme, @"\| Accuracy \| ([\d.]+) mV \|",
                  accurate.Ports[0].DropV!.Value * 1e3, 1e-3, "Accuracy drop");
    }

    // ══ 8. Depopulate works on it ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 8.</b> Unmount <c>C10</c>, re-run, and the worst margin moves by the amount the
    /// removal ranking predicted — which is the whole claim the ranking makes, arrived at by a
    /// different route. And the artwork is not touched: depopulating is a statement about what is
    /// fitted to the geometry, not a change to it.
    /// </summary>
    [Fact]
    public void UnmountingC10MovesTheMarginByWhatTheRankingPredicted()
    {
        string before = File.ReadAllText(ClayPath());

        var baseline = Solved();
        var predicted = baseline.SweepByModel[PdnModelKind.Fast]
                                .Removal.First(r => r.Name.StartsWith("C10", StringComparison.Ordinal));

        var without = WhatIf(d => Unmount(d, "C10"));
        double actual = without.SweepByModel[PdnModelKind.Fast].WorstMarginDb!.Value;

        Assert.Equal(predicted.WorstMarginDb, actual, 2);
        Assert.Equal(before, File.ReadAllText(ClayPath()));

        // And the row is still there, still resolved, still carrying the loop the geometry gave it —
        // unmounted is not deleted and it is not unresolved.
        var row = without.Parts.Single(p => p.Refdes == "C10");
        Assert.False(row.IsMounted);
        Assert.Equal(baseline.Parts.Single(p => p.Refdes == "C10").MountingInductanceHenries,
                     row.MountingInductanceHenries);

        output.WriteLine($"predicted {predicted.WorstMarginDb:0.###} dB, measured {actual:0.###} dB");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static RailRfViewModel WhatIf(Action<RailDocument> edit) => Solved(edit: edit);
    private static RailRfViewModel WhatIf(Action<LayoutView> edit) => Solved(editBoard: edit);

    private static void Unmount(RailDocument document, string refdes)
    {
        var parts = document.Rails[0].Parts;
        for (int i = 0; i < parts.Count; i++)
            if (parts[i].Refdes == refdes) parts[i] = parts[i] with { Mounted = false };
    }

    /// <summary>The README's "widen the BOT run from 0.20 mm to 0.40 mm", performed.</summary>
    private static void WidenTheBotRun(LayoutView view)
    {
        foreach (var r in view.Shapes.OfType<RectShape>())
            if (r.Layer.Layer == 4 && r.Y2 - r.Y1 == 200_000 && r.X2 - r.X1 > 1_000_000)
            { r.Y1 -= 100_000; r.Y2 += 100_000; }
    }

    private static double Number(Group g) =>
        double.Parse(g.Value, CultureInfo.InvariantCulture);

    private static void Published(string readme, string pattern, double actual, double tolerance, string what)
    {
        var m = Regex.Match(readme, pattern, RegexOptions.Multiline);
        Assert.True(m.Success, $"the README publishes no figure for {what} (/{pattern}/).");
        double published = Number(m.Groups[1]);
        Assert.True(Math.Abs(published - actual) <= tolerance,
            $"the README says {what} is {published} and the example produces {actual}. " +
            "Re-measure and re-publish — a figure a reader checks and finds wrong is worse than none.");
    }

    private static void CopyTree(string from, string to)
    {
        foreach (string dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(from, to, StringComparison.Ordinal));
        Directory.CreateDirectory(to);
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(from, to, StringComparison.Ordinal), true);
    }

    private static (int Code, string Output) RunGenerator(string root, string? fault)
    {
        var psi = new ProcessStartInfo("python3")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = root,
        };
        psi.ArgumentList.Add(Path.Combine(root, "Sensor board", "layout", "Board.gen.py"));
        psi.ArgumentList.Add(root);
        if (fault is not null) psi.Environment[fault] = "1";

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("python3 did not start. The Power Rail example's "
                + "board is written by a generator and this gate runs it.");
        string stdout = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout);
    }
}
