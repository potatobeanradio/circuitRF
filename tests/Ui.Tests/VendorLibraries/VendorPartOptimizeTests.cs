using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Netlist.Spice;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.VendorLibraries;
using CircuitRF.Ui.Tuning;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests.VendorLibraries;

/// <summary>
/// Brief vendorlib-4 §7: the optimizer choosing among real parts — Discrete over parts finds the grid optimum
/// reading one block per candidate, a mixed search reports the ideal-proxy and real-part costs, Apply is one undo
/// entry, the <c>tune … .Part</c> line round-trips, and <c>check</c>'s rules. Every library is generated
/// (<see cref="VendorLibraryFixture"/>); every gate is a counter or a structure, never a clock.
/// </summary>
public sealed class VendorPartOptimizeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-vl4-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly string _lib;
    private readonly string _ws;
    private readonly string _schDir;

    public VendorPartOptimizeTests()
    {
        _lib = Path.Combine(_dir, "lib");
        VendorLibraryFixture.Write(_lib);
        _ws = WorkspaceCreate.Create(_dir, "ws", "pcb-2layer_FR-4_70mil_1oz", vendorLibraries: [("Parts", _lib)]).WorkspaceDir;
        _schDir = Path.Combine(_ws, "cells", "top", "schematic");
        Directory.CreateDirectory(_schDir);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private const string FileA = "capacitors/series-a.lib";

    // ── The design: P1 — C1 — C2 (or R1) — P2, one S-parameter point ─────────

    private static EditableComponent VendorPart(string inst, string part, double x, string? footprint = null)
    {
        var comp = new EditableComponent { InstanceName = inst, Symbol = SymbolKind.SpiceModel, X = x, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.SpiceModel, 0))
            comp.Parameters.Add(new EditableParameter { Name = dp.Name, Expression = dp.Expression });
        comp.Parameters.First(p => p.Name == "File").Expression = VendorLibraryRefs.RefFor("Parts", FileA);
        comp.Parameters.First(p => p.Name == "Name").Expression = part;
        comp.Parameters.Add(new EditableParameter { Name = "Glyph", Expression = "C" });
        if (footprint is not null)
            comp.Parameters.Add(new EditableParameter { Name = ArtworkParameters.FootprintName, Expression = footprint });
        return comp;
    }

    private List<(double X, double Y)> Pins(EditableComponent c)
        => c.Symbol == SymbolKind.SpiceModel
            ? [.. CellSymbolResolver.Resolve(c.ExternalSymbolRef!, _schDir).Symbol!.Pins.OrderBy(p => p.PortIndex)
                                    .Select(p => ((double)p.LocalX + c.X, (double)p.LocalY + c.Y))]
            : [.. SymbolPortDefs.For(c.Symbol).Select(p => ((double)p.LocalX + c.X, (double)p.LocalY + c.Y))];

    /// <summary>Two elements in series between two 50 Ω ports, one frequency, and the tuning setup given.</summary>
    private SchematicEditModel Network(EditableComponent first, EditableComponent second, TuningSetup setup)
    {
        var model = new SchematicEditModel { SchematicDirectory = _schDir };
        model.Components.Add(first);
        model.Components.Add(second);
        var a = Pins(first);
        var b = Pins(second);
        // The series join runs between the parts, and each port hangs off a lead well clear of both, so no wire
        // or termination lands on a pin it is not meant for.
        model.Wires.Add(Wire(a[1], (a[1].X + 1000, a[1].Y), (a[1].X + 1000, b[0].Y), b[0]));
        model.Wires.Add(Wire(a[0], (a[0].X - 1000, a[0].Y)));
        model.Wires.Add(Wire(b[1], (b[1].X + 1000, b[1].Y)));
        Terminate(model, (a[0].X - 1000, a[0].Y), 1);
        Terminate(model, (b[1].X + 1000, b[1].Y), 2);
        model.Analyses.Add(new SParameterAnalysis("SP1", new FrequencySpec("1", "1", 1, startUnit: "GHz", stopUnit: "GHz")));
        model.Tuning = setup;
        return model;
    }

    private static EditableWire Wire(params (double X, double Y)[] pts)
    {
        var w = new EditableWire();
        w.Points.AddRange(pts);
        return w;
    }

    private static void Terminate(SchematicEditModel model, (double X, double Y) pin, int num)
    {
        var term = new EditableComponent { InstanceName = $"P{num}", Symbol = SymbolKind.Term, X = pin.X, Y = pin.Y + 200 };
        term.Parameters.Add(new EditableParameter { Name = "Num", Expression = num.ToString() });
        term.Parameters.Add(new EditableParameter { Name = "Z",   Expression = "50" });
        model.Components.Add(term);
        model.Components.Add(new EditableComponent { InstanceName = $"G{num}", Symbol = SymbolKind.Ground, X = pin.X, Y = pin.Y + 400 });
    }

    private static OptimizationGoal Goal(string expr, double limit)
        => new() { Name = "G" + Math.Abs(expr.GetHashCode() % 1000), Expression = expr, Analysis = "SP1", Type = GoalType.Eq,
                   Limit = limit.ToString(System.Globalization.CultureInfo.InvariantCulture) };

    /// <summary>Simulate's own preparation, in memory: the extraction, its netlist text, and its vendor parts.</summary>
    private PreparedCircuit Prepare(SchematicEditModel model, out NetExtractor.ExtractionResult extracted)
    {
        extracted = NetExtractor.Extract(model, "tb", DiskCellResolver.Instance);
        Assert.Empty(extracted.Conflicts);
        var text = CnlWriter.Write(extracted.TestBench, extracted.Library, "vl4");
        var circuit = PreparedCircuit.FromText(text, _schDir, _ws, "tb", extracted);
        Assert.Null(circuit.ReadError);
        return circuit;
    }

    // 1 — Discrete over parts finds the grid optimum, reading k blocks for k candidates.
    [Fact]
    public void DiscreteOverParts_FindsTheGridOptimum_AndReadsOneBlockPerCandidate()
    {
        long files = VendorLibraryIndex.FileReads(_lib);
        int reads  = SpiceNetlistReader.ReadsOnThisThread;

        // 0402 C0G parts of series A: 1.5, 3, 4.5 pF for C1 and 6, 7.5, 9 pF for C2 — a 3 × 3 grid.
        var model = Network(VendorPart("C1", "TSTC0402A0001", 0), VendorPart("C2", "TSTC0402A0016", 3000), new TuningSetup
        {
            Variables =
            [
                new TunableEntry { Key = "C1.Part", PartKind = "same", Min = "1 pF", Max = "5 pF", Opt = true },
                new TunableEntry { Key = "C2.Part", Min = "5 pF", Max = "10 pF", Opt = true },
            ],
            Goals     = [Goal("dB(SP1.S(2,1))", -2.5)],
            Optimizer = new OptimizerSettings { Seed = 1 },
        });
        var circuit = Prepare(model, out var extracted);

        var parts = extracted.PartTunables.ToDictionary(p => p.Key);
        Assert.Equal(["TSTC0402A0001", "TSTC0402A0004", "TSTC0402A0007"], parts["C1.Part"].Candidates.Select(c => c.Part));
        Assert.Equal(["TSTC0402A0010", "TSTC0402A0013", "TSTC0402A0016"], parts["C2.Part"].Candidates.Select(c => c.Part));   // 6, 7.5, 9 pF

        // One header scan of the one file, and one block per distinct candidate — six — read once through the
        // one reader, whatever the evaluations that follow.
        Assert.Equal(1 + 6, VendorLibraryIndex.FileReads(_lib) - files);
        Assert.Equal(6, SpiceNetlistReader.ReadsOnThisThread - reads);

        var result = OptimizationRun.Create(circuit).Run();
        Assert.Contains(OptimizationStages.PartSearch, result.Stages);
        Assert.Equal(1 + 6, VendorLibraryIndex.FileReads(_lib) - files);

        // Every pair, evaluated directly: the run's choice is the grid's optimum.
        var direct = OptimizationRun.ForEvaluation(circuit, null, GoalUse.Opt);
        var pairs = (from a in parts["C1.Part"].Candidates from b in parts["C2.Part"].Candidates
                     select (IReadOnlyDictionary<string, string>)new Dictionary<string, string> { ["C1.Part"] = a.Cell, ["C2.Part"] = b.Cell })
                    .ToList();
        var scored = direct.EvaluateValues(pairs, keepData: false);
        Assert.All(scored, s => Assert.Equal(PointStatus.Evaluated, s.Status));
        Assert.True(scored.Select(x => x.Cost).Distinct().Count() > 1, "every pair costs the same: the network is not connected");
        // Pairs can tie (several meet the goal at cost 0), so the claim is the chosen pair's own cost, not its name.
        double optimum = scored.Min(s => s.Cost);
        string c1 = result.Parts.Single(p => p.Key == "C1.Part").Part, c2 = result.Parts.Single(p => p.Key == "C2.Part").Part;
        var chosen = scored.Single(s => s.Values["C1.Part"] == c1 && s.Values["C2.Part"] == c2);
        Assert.Equal(optimum, chosen.Cost, 12);
        Assert.Equal(optimum, result.BestCost!.Value, 12);
        Assert.Equal(1 + 6, VendorLibraryIndex.FileReads(_lib) - files);
    }

    // 2 — a part with a continuous value: the ideal proxy's cost, the real parts' cost, a snapped neighbour.
    [Fact]
    public void MixedSearch_ReportsTheIdealAndTheRealCost_AndChoosesASnappedNeighbour()
    {
        var r1 = new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor, X = 3000, Y = 0 };
        r1.Parameters.Add(new EditableParameter { Name = "R", Expression = "20", Unit = "Ohm" });
        var model = Network(VendorPart("C1", "TSTC0402A0001", 0), r1, new TuningSetup
        {
            Variables =
            [
                new TunableEntry { Key = "C1.Part", Opt = true },
                new TunableEntry { Key = "R1.R", Min = "1 Ohm", Max = "200 Ohm", Opt = true },
            ],
            Goals     = [Goal("dB(SP1.S(2,1))", -1.5), Goal("phase(SP1.S(2,1))", 40)],
            Optimizer = new OptimizerSettings { Seed = 1 },
        });
        var circuit = Prepare(model, out var extracted);
        var part = extracted.PartTunables.Single();
        Assert.Equal(40, part.Candidates.Count);
        Assert.True(part.HasProxy);

        var result = OptimizationRun.Create(circuit).Run();
        Assert.NotNull(result.Snap);
        Assert.Equal(1, result.Snap!.Parts);
        Assert.Contains(result.Notes, n => n.Id == "opt.part.snap-report");
        Assert.True(double.IsFinite(result.Snap.CostBefore) && double.IsFinite(result.Snap.CostSnapped));

        // The continuous best had an IDEAL capacitor in C1's place; the chosen part is one of the two candidates
        // either side of it by value.
        var relaxed = result.Log.Where(r => !r.Failed && !r.Infeasible && r.Decoded.Values["C1.Part"].EndsWith(" F"))
                                .MinBy(r => r.Cost)!;
        Assert.Equal(result.Snap.CostBefore, relaxed.Cost, 12);
        double f = relaxed.Point[0] * (part.Candidates.Count - 1);
        var neighbours = new[] { part.Candidates[(int)Math.Floor(f)].Part, part.Candidates[(int)Math.Ceiling(f)].Part };
        Assert.Contains(result.Parts.Single().Part, neighbours);
        Assert.False(result.Parts.Single().Part.EndsWith(" F"));
    }

    // 3 — Apply: a part and a value in one undo entry; the footprint moves only when it came from the part.
    [Fact]
    public void Apply_IsOneUndoEntry_AndMovesTheFootprintOnlyWhenItCameFromThePart()
    {
        foreach (var (footprint, after) in new[] { ("smt:0402@N", "smt:0603@N"), ("smt:0805@L", "smt:0805@L") })
        {
            var r1 = new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor, X = 3000, Y = 0 };
            r1.Parameters.Add(new EditableParameter { Name = "R", Expression = "20", Unit = "Ohm" });
            var c1 = VendorPart("C1", "TSTC0402A0001", 0, footprint);
            // A search that crosses size: series A's C0G parts from 1 to 3.5 pF, in all three case sizes.
            var model = Network(c1, r1, new TuningSetup
            {
                Variables =
                [
                    new TunableEntry { Key = "C1.Part", Parts = "C0G 50V", Min = "1 pF", Max = "3.5 pF", Opt = true },
                    new TunableEntry { Key = "R1.R", Opt = true },
                ],
            });
            var vm = new SchematicViewModel(model);
            var catalog = TunableCatalog.Discover(model, DiskCellResolver.Instance, _ws);
            Assert.Equal(6, catalog.Find("C1.Part")!.VendorPart!.Candidates.Count);

            var report = TuningPush.Push(catalog,
                new Dictionary<string, string> { ["C1.Part"] = "TSTC0603A0002", ["R1.R"] = "75 Ohm" }, vm, _ => null);
            Assert.Equal(2, report.Written);
            Assert.Equal("TSTC0603A0002", Param(c1, "Name"));
            Assert.Equal(after, Param(c1, ArtworkParameters.FootprintName));
            Assert.Equal("75", Param(r1, "R"));

            vm.UndoRedo.Undo();
            Assert.Equal("TSTC0402A0001", Param(c1, "Name"));
            Assert.Equal(footprint, Param(c1, ArtworkParameters.FootprintName));
            Assert.Equal("20", Param(r1, "R"));
            Assert.False(vm.UndoRedo.CanUndo);
        }
    }

    private static string Param(EditableComponent c, string name)
        => c.Parameters.FirstOrDefault(p => p.Name == name)?.Expression ?? "";

    // 4 — the tune line's part keys survive a .cnl round trip byte for byte; a bad one is a reader error.
    [Fact]
    public void TheTuneLine_RoundTripsItsPartKeys_AndRefusesABadOne()
    {
        string[] lines =
        [
            "tune C1.Part kind=same min=1 pF max=47 pF opt=1",
            "tune C2.Part parts=\"0603 C0G 50V\" opt=1",
        ];
        string text = "R:R1 a 0 R=50 Ohm\n" + string.Join("\n", lines) + "\n";
        var (lib, tb) = new CnlReader().Read(text, "tb", null);
        Assert.Equal("same", tb.Tuning!.Variables[0].PartKind);
        Assert.Equal("0603 C0G 50V", tb.Tuning.Variables[1].Parts);
        string written = CnlWriter.Write(tb, lib, "rt");
        foreach (var line in lines) Assert.Contains(line + "\n", written.Replace("\r\n", "\n"));

        Assert.Throws<CnlReadException>(() => new CnlReader().Read("R:R1 a 0 R=50\ntune C1.Part kind=other opt=1\n", "tb", null));
        Assert.Throws<CnlReadException>(() => new CnlReader().Read("R:R1 a 0 R=50\ntune C1.Part kind=same parts=C0G\n", "tb", null));
    }

    // 5 — check: an empty candidate list is an error; a long one is not (issue #32 removed the 200-part cap).
    [Fact]
    public void Check_ReportsAnEmptyList_AndEmitsALongOne()
    {
        var model = new SchematicEditModel { SchematicDirectory = _schDir };
        model.Components.Add(VendorPart("C1", "TSTC0402A0001", 0));
        model.Components.Add(VendorPart("C2", "TSTC0402A0004", 2000));
        model.Tuning = new TuningSetup
        {
            Variables =
            [
                new TunableEntry { Key = "C1.Part", Min = "100 pF", Max = "200 pF", Opt = true },
                new TunableEntry { Key = "C2.Part", Parts = "TSTC", Opt = true },
            ],
        };
        var extracted = NetExtractor.Extract(model, "tb", DiskCellResolver.Instance);
        var catalog = TunableCatalog.Discover(model, DiskCellResolver.Instance, _ws);
        var findings = TuningValidator.Validate(extracted.TestBench, catalog);

        var empty = Assert.Single(findings, d => d.Id == "tuning.part.no-candidates");
        Assert.Equal(CircuitRF.Diagnostics.DiagnosticSeverity.Error, empty.Severity);
        Assert.Contains("C1.Part", empty.Render());

        // 220 candidates — over the old cap — are all emitted and nothing refuses them.
        Assert.DoesNotContain(findings, d => d.Id.StartsWith("tuning.part.", StringComparison.Ordinal) && d.Render().Contains("C2.Part"));
        var c2 = extracted.PartTunables.Single(p => p.Key == "C2.Part");
        Assert.True(c2.Emitted);
        Assert.Equal(220, c2.Candidates.Count);
    }
}
