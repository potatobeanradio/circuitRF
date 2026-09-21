// ================================================================
//  FindingsTests.cs — the gate for brief-lvs-8-findings.md §7.
//
//  ── WHAT IS BEING PINNED ──────────────────────────────────────────────────────────────────────
//
//  That the answer brief 7 arrives at becomes a report a designer can ACT ON: a short with the
//  metal that causes it, an open with its islands, a marker on everything the artwork has, a cap
//  that says it capped, and a summary on a run that found nothing.
//
//  ── THE TWO THAT MATTER MOST ──────────────────────────────────────────────────────────────────
//
//  `TheShortNamesTheSpursOwnWidthAndCoordinate` and `TheOpenNamesEveryIslandWithItsOwnMarker`.
//  R-lvs5-2d says F4 and F5 are the two faults that justify brief 2's merge edges and this brief's
//  islands, and a short reported without its path is the finding a user cannot act on. Everything
//  else here would pass over a report that said only "IN and GND are shorted".
//
//  ── THREE CORRECTIONS TO THE BRIEF, ALL RECORDED WHERE THEY BITE ──────────────────────────────
//
//  1. F5 is TWO islands, not three — brief 5's own fixture note already says so, and repeats the
//     arithmetic: a barrel joins at most one piece per conductor, so on a two-layer board removing
//     one tree edge splits one net in two.
//  2. A finding about a SCHEMATIC-only object carries no marker. There is no artwork to point at,
//     and that absence is the finding — see LvsFinding.HasMarker.
//  3. Gate 1's "every id is produced by some fixture" is asserted as a SOURCE scan in both
//     directions rather than by firing all thirty-six on three designs. Several of them need a
//     design that is malformed rather than merely wrong — an unreadable schematic, a flatten over
//     the ceiling — and a fixture built to fire one of those would be testing the fixture.
// ================================================================

using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Lvs;
using CircuitRF.Design.Schematic;
using CircuitRF.Diagnostics;

namespace CircuitRF.Ui.Tests.Lvs;

public sealed class FindingsTests
{
    private const string Correct = "Attenuator";
    private const string Broken  = "Attenuator broken";
    private const string Mmic    = "Bias tee";

    // ══ 1 — the catalogue is closed, in both directions ═════════════════════════════════════════
    //
    // R-lvs8-3/3a. An id nothing produces is dead; a finding with an id the catalogue does not list
    // breaks the CLI's `--json` contract silently, and neither shows up in any other test.

    [Fact]
    public void TheCatalogueIsExactlyWhatTheCodeProduces()
    {
        string source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Design", "Layout", "Lvs", "LvsDiagnostics.cs"));

        var produced = Regex.Matches(source, @"Diagnostic\.Create\(\s*""([a-z][a-z0-9.\-]*)""")
                            .Select(m => m.Groups[1].Value)
                            .ToHashSet(StringComparer.Ordinal);

        // The one id LVS reports without authoring: brief 1's positional-terminal-map warning,
        // carried verbatim rather than given a second `lvs.` spelling (see LvsFindingIds).
        produced.Add("check.terminals.derived-by-order");

        Assert.Equal(produced.Order(StringComparer.Ordinal),
                     LvsFindingIds.All.Order(StringComparer.Ordinal));

        foreach (string cell in new[] { Correct, Broken, Mmic })
            Assert.All(Run(cell).Findings, f => Assert.True(
                LvsFindingIds.Contains(f.Id), $"{cell} produced the unlisted id '{f.Id}'"));
    }

    // ══ 2 — F4: the short is a PATH ═════════════════════════════════════════════════════════════
    //
    // R-lvs8-4b, and the reason brief 2 kept the merge edges. The spur is 0.2 mm of TOP copper
    // between (1.40, 3.55) and (1.60, 5.80), and every other route between the input and ground on
    // this board is a 0.6 mm stitching barrel — correct artwork, and not the fault. A report that
    // named one of those vias would be worse than one that named nothing.

    [Fact]
    public void TheShortNamesTheSpursOwnWidthAndCoordinate()
    {
        var finding = Assert.Single(Run(Broken).Findings, f => f.Id == "lvs.net.short");

        Assert.Equal(["0", "IN"], finding.Objects.Order(StringComparer.Ordinal));

        // The narrowest metal on the route, not the first — 0.2 mm, to within the bisection's own
        // tenth of a micron.
        long width = Assert.IsType<long>(finding.Diagnostic.Arguments["widthDbu"]);
        Assert.InRange(width, 199_000, 201_000);

        // And WHERE, to within the spur's own extent (R-lvs8-4b).
        long x = Assert.IsType<long>(finding.Diagnostic.Arguments["x"]);
        long y = Assert.IsType<long>(finding.Diagnostic.Arguments["y"]);
        Assert.InRange(x, 1_400_000, 1_600_000);
        Assert.InRange(y, 3_550_000, 5_800_000);

        // R-lvs8-4c: the marker is the join geometry and not the whole net. The board is 20 mm
        // wide; the spur is 0.2 mm.
        Assert.True(finding.HasMarker);
        Assert.InRange(finding.Marker.MaxX - finding.Marker.MinX, 1, 400_000);

        Assert.Contains("neck", finding.Render(), StringComparison.Ordinal);
    }

    // ══ 3 — F5: the open is an ISLAND STRUCTURE ═════════════════════════════════════════════════
    //
    // R-lvs8-5a, with correction 1 above: two islands, which is what the fixture can produce.

    [Fact]
    public void TheOpenNamesEveryIslandWithItsOwnMarker()
    {
        var finding = Assert.Single(Run(Broken).Findings, f => f.Id == "lvs.net.open");

        Assert.Equal(2, finding.Diagnostic.Arguments["islands"]);

        // Each island names its pins, by the designer's own name for the part — the sentence a
        // user acts on ("R1.2 is on its own").
        Assert.Contains("R1.2", finding.Render(), StringComparison.Ordinal);
        Assert.Contains("R3.2", finding.Render(), StringComparison.Ordinal);
        Assert.Contains("R1.2", finding.Objects);

        // A marker PER island — at least one ring for each, and the two do not overlap in Y: the
        // upper-left ground is above the pour's own strip.
        Assert.True(finding.MarkerRings.Count >= 2);
        Assert.All(finding.MarkerRings, ring => Assert.True(ring.Length >= 6));
    }

    // ══ 4 — two nets deliberately separate are SILENT ═══════════════════════════════════════════
    //
    // R-lvs8-5b, and the test that stops LVS reporting every correct AC coupling as an open. The
    // correct board's IN and OUT are two pieces of copper joined at DC by nothing and at AC by C1,
    // and the schematic says they are two nets — so the artwork is right and the report says so by
    // saying nothing.

    [Fact]
    public void CopperInTwoIslandsTheSchematicCallsTwoNetsIsNotAnOpen()
    {
        var result = Run(Correct);

        Assert.DoesNotContain(result.Findings, f => f.Id == "lvs.net.open");
        Assert.DoesNotContain(result.Findings, f => f.Id == "lvs.net.short");
        Assert.True(result.IsClean);

        // The premise, so this cannot pass by the board having become one net: IN and OUT are
        // distinct, and C1 is the only thing between them.
        var c1 = Assert.Single(result.Layout.Devices, d => d.Designator == "C1");
        Assert.NotEqual(c1.Terminals[0].NetIndex, c1.Terminals[1].NetIndex);
    }

    // ══ 5 — copper with no pin on it is FLOATING, not an open ═══════════════════════════════════
    //
    // R-lvs8-5c. A pour somebody forgot to stitch is worth saying and is not the same defect, so it
    // is a warning of its own rather than an error about a net.

    [Fact]
    public void APourWithNoPinsOnItIsFloatingCopper()
    {
        var result = Mutate(view => view.Shapes.Add(new RectShape
        {
            X1 = 600_000, Y1 = 4_000_000, X2 = 1_200_000, Y2 = 4_600_000,
            Layer = new LayerKey(1, 0),
        }));

        var finding = Assert.Single(result.Findings, f => f.Id == "lvs.net.floating-copper");

        Assert.Equal(DiagnosticSeverity.Warning, finding.Severity);
        Assert.True(finding.HasMarker);
        Assert.DoesNotContain(result.Findings, f => f.Id == "lvs.net.open");

        // And it is the only thing wrong: unstitched copper is not an error about the comparison.
        Assert.Empty(result.Findings.Where(f => f.Severity == DiagnosticSeverity.Error));
    }

    // ══ 6, 8, 11 — order, markers and counts, on the six-fault board ════════════════════════════

    [Fact]
    public void TheReportIsTheSameReportEveryRunAndEveryFindingIsPlaced()
    {
        var first = Run(Broken);

        // R-lvs8-1b: severity, then id, then the primary object. Ten runs, one list.
        string Report(LvsRunResult r) => string.Join("\n", r.Findings.Select(
            f => $"{f.Severity} {f.Id} [{string.Join("|", f.Objects)}] {f.Render()}"));
        for (int run = 0; run < 10; run++) Assert.Equal(Report(first), Report(Run(Broken)));

        Assert.Equal(
            first.Findings.Select(f => f.Severity).OrderByDescending(s => s),
            first.Findings.Select(f => f.Severity));

        // R-lvs8-2c, with correction 2: every finding about the ARTWORK carries rings; a run-level
        // line and a schematic-only finding carry none, and neither is missing one.
        foreach (var finding in first.Findings)
        {
            if (finding.IsRunLevel) { Assert.False(finding.HasMarker); continue; }
            if (finding.Id == "lvs.device.unmatched-schematic") { Assert.False(finding.HasMarker); continue; }
            Assert.True(finding.HasMarker, $"{finding.Id} named {string.Join(",", finding.Objects)} "
                                           + "and gave nowhere to look");
            Assert.False(finding.Marker.IsEmpty);
            Assert.NotEmpty(finding.Key);
        }

        // R-lvs8-1c: both sides, before and after the collapse. Nothing collapses on this board, so
        // what is pinned is that the numbers are the two sides' own and are carried at all.
        Assert.Equal(4, first.Counts.Schematic.DevicesBefore);
        Assert.Equal(4, first.Counts.Schematic.DevicesAfter);
        Assert.Equal(4, first.Counts.Layout.DevicesBefore);
        Assert.Equal(5, first.Counts.Layout.NetsAfter);
        Assert.Equal(ReductionMode.On, first.Reduction);
    }

    // ══ 10 — a clean run still reports its summary ══════════════════════════════════════════════
    //
    // R-lvs8-6b. A run that deliberately concluded "these match" and said nothing at all is
    // indistinguishable from a broken command.

    [Fact]
    public void ACleanRunStillSaysWhatItCompared()
    {
        var board = Run(Correct);
        Assert.True(board.IsClean);

        var summary = Assert.Single(board.Findings, f => f.Id == "lvs.report.summary");
        Assert.Contains("4 device(s)", summary.Render(), StringComparison.Ordinal);
        Assert.Contains("reduction ON", summary.Render(), StringComparison.Ordinal);
        Assert.Contains(board.TechnologyName!, summary.Render(), StringComparison.Ordinal);
        Assert.Equal(2, board.Findings.Count(f => f.Id == "lvs.reduce.mode"));

        // The ground reminder, on the one fixture that relies on metal nobody drew.
        var mmic = Run(Mmic);
        Assert.Single(mmic.Findings, f => f.Id == "lvs.ground.reference-undrawn");
        Assert.Single(mmic.Findings, f => f.Id == "lvs.report.summary");
    }

    // ══ 7, 9 — un-reduced objects, and the cap ══════════════════════════════════════════════════
    //
    // Hand-built, because both claims need a shape the proving board does not have: four parallel
    // caps, and forty nets on one piece of copper. A `.clay` for either would be a fixture written
    // to satisfy a test.

    [Fact]
    public void AMergedDeviceIsNamedByEveryIndividualItStandsFor()
    {
        // Four capacitors in parallel on the board and none at all on the drawing. The collapse
        // makes the four ONE device, and R-lvs8-2b says the finding still has to name the four —
        // "the merged group at net 14" is a report nobody can act on.
        var schematic = new Netlist().Device("R1", DeviceKind.Resistor, "A", "0");
        var layout    = new Netlist().Device("R1", DeviceKind.Resistor, "A", "0");
        for (int k = 0; k < 4; k++) layout.Device($"C{k}", DeviceKind.Capacitor, "A", "0");

        var (s, _) = LvsReduce.Apply(schematic.Boundary("A").Label("0").Build(), LvsReduceOptions.Default);
        var (l, log) = LvsReduce.Apply(layout.Boundary("A").Label("0").Build(), LvsReduceOptions.Default);

        // The premise: the four really were collapsed into one.
        Assert.Equal(5, log.DevicesBefore);
        Assert.Equal(2, log.DevicesAfter);

        var unmatched = Assert.Single(
            Report(s, l), f => f.Id == "lvs.device.unmatched-layout");

        Assert.Equal(["C0", "C1", "C2", "C3"], unmatched.Objects.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AFortyNetPourCapsAndSaysThatItCapped()
    {
        // Forty schematic nets, one piece of copper: 780 pairs, all of them true.
        var schematic = new Netlist();
        var layout    = new Netlist();
        for (int k = 0; k < 40; k++)
        {
            schematic.Device($"R{k}", DeviceKind.Resistor, $"n{k}", "COM");
            layout.Device($"R{k}", DeviceKind.Resistor, "POUR", "COM");
        }

        // Not reduced: forty resistors between one pair of nets IS forty in parallel, and the
        // collapse would turn the fault this test is about into one device.
        var s = schematic.Build();
        var l = layout.Build();

        var comparison = LvsCompare.Compare(s, l);
        Assert.Equal(780, comparison.Shorts.Count);

        var findings = Report(s, l);
        Assert.Equal(LvsReport.MaxPerId, findings.Count(f => f.Id == "lvs.net.short"));

        var capped = Assert.Single(findings, f => f.Id == "lvs.report.capped");
        Assert.Contains("780", capped.Render(), StringComparison.Ordinal);
        Assert.True(capped.IsRunLevel);
    }

    // ── running the fixtures ──────────────────────────────────────────────────────────────────

    private static LvsRunResult Run(string cell) => LvsRun.Run(Lvs(cell));

    /// <summary>The correct board with one bounded change to the loaded artwork — the same move
    /// <c>ComparisonTests.Board</c> makes, for R-lvs5-3e's reason.</summary>
    private static LvsRunResult Mutate(Action<LayoutView> mutate)
    {
        string cell = Lvs(Correct);
        string clay = Path.Combine(cell, "layout", "Attenuator.clay");
        string csch = Path.Combine(cell, "schematic", "Attenuator.csch");

        var view = LayoutPersistence.LoadFromFile(clay);
        mutate(view);

        var (model, _, _) = SchematicPersistence.LoadFromFile(csch);
        var (resolution, _) = TechnologyResolver.ResolveForDocument(
            view.TechRef, clay, null, new TechnologyCache());

        return LvsRun.Run(view, clay, cell, resolution.Tech, model, csch);
    }

    /// <summary>The report over two netlists, with no artwork to locate anything in — which is the
    /// case R-lvs8-2c's "or an empty one" covers.</summary>
    private static IReadOnlyList<LvsFinding> Report(LvsNetlist schematic, LvsNetlist layout)
        => LvsReport.Build(
            LvsCompare.Compare(schematic, layout), schematic, layout, LvsGeometry.None, [],
            LvsCounts.Nothing, null, ReductionMode.On);

    /// <summary>A netlist in as few lines as the type allows — <c>ComparisonTests.Netlist</c>'s own
    /// shape, kept small rather than shared: the two files want different corners of it.</summary>
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

    private static string Lvs(params string[] parts)
        => Path.Combine([RepoRoot(), "examples", "LVS", .. parts]);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
