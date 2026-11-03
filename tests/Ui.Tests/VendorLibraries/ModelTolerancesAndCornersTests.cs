using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Statistics;
using CircuitRF.Design.Symbol;
using CircuitRF.Design.VendorLibraries;
using CircuitRF.Design.VendorLibraries.ModelLibrary;
using CircuitRF.Design.Workspace;
using RfCore.Data;
using Xunit;
using static CircuitRF.Ui.Tests.VendorLibraries.ModelLibraryExportTests;

namespace CircuitRF.Ui.Tests.VendorLibraries;

/// <summary>
/// Brief vendorlib-13 §3: a library model's tolerances (distribution functions in its values) and corners (named
/// parameter sets in its define). Every part here is synthetic.
/// </summary>
[Collection(CellStatGlobalsCollection.Name)]
public sealed class ModelTolerancesAndCornersTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-vl13-" + Guid.NewGuid().ToString("N")[..12]);

    public ModelTolerancesAndCornersTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // 1 — R-vl13-1a: two instances of one library part whose default is a gauss draw differently in one trial, and both
    // are exactly nominal in an ordinary run.
    [Fact]
    public void LibraryPartDistribution_DrawsPerInstance_AndIsExactlyNominalOutsideATrial()
    {
        var f = Fixture();
        var r = Extract(f.Bench, Placed("U1", f.Ref, 0), Placed("U2", f.Ref, 20000));

        var nominal = Elaborate(r);
        var reference = new Elaborator(new CnlReader().Read("C:C1 a b C=10 pF", "t").Library)
            .Elaborate(new CnlReader().Read("C:C1 a b C=10 pF", "t").TestBench);
        double c10 = reference.Components.Single(c => c.InstancePath == "C1").Parameters["C"].AsReal();
        Assert.Equal(c10, C(nominal, "U1.C1"));
        Assert.Equal(c10, C(nominal, "U2.C1"));

        var trial = new Elaborator(r.Library)
        {
            BaseDirectory = SchematicCircuit.ReferenceBaseOfDirectory(f.Bench),
            Statistics = new ExpressionDraws(seed: 1, trial: 1),
        }.Elaborate(r.TestBench);
        Assert.NotEqual(C(trial, "U1.C1"), C(trial, "U2.C1"));
        Assert.NotEqual(c10, C(trial, "U1.C1"));
        Assert.NotEqual(c10, C(trial, "U2.C1"));
    }

    // 2 — R-vl13-1b: each of the four spreads, typed in the Yield panel's grammar and read by its parser, maps to the
    // expected function, and reads back to the same spread.
    [Theory]
    [InlineData(StatDistribution.Gauss, "± 5 % at 2σ", "gauss(10,0.05,2)")]
    [InlineData(StatDistribution.Gauss, "σ 0.1 pF",    "agauss(10,0.1,1)")]
    [InlineData(StatDistribution.Unif,  "± 2 %",       "unif(10,0.02)")]
    [InlineData(StatDistribution.Unif,  "9 … 11 pF",   "aunif(10,1)")]
    public void EachSpread_MapsToItsFunction_AndReadsBack(StatDistribution d, string typed, string expected)
    {
        var spread = ToleranceText.Parse(typed, d)!;
        string? fn = ModelTolerance.ToFunction("10", "pF", d, spread, out string? why);
        Assert.True(fn is not null, why);
        Assert.Equal(expected, fn);
        var back = ModelTolerance.FromFunction(fn!, "pF")!.Value;
        Assert.Equal(d, back.Distribution);
        Assert.Equal(ToleranceText.Format(d, spread), ToleranceText.Format(back.Distribution, back.Spread));
    }

    // 3 — R-vl13-1c: a part held at nominal on the statistics line keeps its draws at nominal while another keeps drawing.
    [Fact]
    public void HeldPart_StaysAtNominal_WhileAnotherPartDraws()
    {
        var c = PreparedCircuit.FromText(Bench("statistics trials=4 nominalparts=TSTCAP"), null, null);
        Assert.Equal("statistics trials=4 nominalparts=TSTCAP", TuningDirectiveText.Write(c.Tb!.Tuning!).Single());

        var result = StatisticalRun.Create(c, new StatisticalOptions { Mode = StatisticalMode.MonteCarlo }).Run();
        Assert.True(result.Data is not null, result.Refusal?.Render());
        Assert.False(Drew(result.Data!, "U1."));
        Assert.False(Drew(result.Data!, "U2."));
        Assert.True(Drew(result.Data!, "V1."));
    }

    // 5 — R-vl13-2d: a placed part on Corner=min gives what min's values typed by hand give, and an explicit value on the
    // same instance beats the corner.
    [Fact]
    public void PlacedPartCorner_EqualsItsValuesTypedByHand_AndAnExplicitValueWins()
    {
        var f = Fixture();
        var byCorner = Placed("U1", f.Ref, 0, ("Corner", "min", ""));
        var byHand   = Placed("U2", f.Ref, 20000, ("C0", "9", "pF"), ("R0", "0.5", "Ohm"));
        var mixed    = Placed("U3", f.Ref, 40000, ("Corner", "min", ""), ("C0", "12", "pF"));
        var e = Elaborate(Extract(f.Bench, byCorner, byHand, mixed));

        Assert.Equal(C(e, "U2.C1"), C(e, "U1.C1"));
        Assert.Equal(R(e, "U2.R1"), R(e, "U1.R1"));
        Assert.Equal(12e-12, C(e, "U3.C1"), 1e-24);
        Assert.Equal(R(e, "U2.R1"), R(e, "U3.R1"));
    }

    // 6 — R-vl13-2e/2f: a bench corner part:TSTCAP=max moves both instances of that part and no other part; Generate
    // crosses part: with temp= and counts correctly.
    [Fact]
    public void BenchPartCorner_MovesEveryInstanceOfThatPartOnly_AndGenerateCrossesIt()
    {
        var c = PreparedCircuit.FromText(Bench("corner slow temp=85 part:TSTCAP=max"), null, null);
        var corner = c.Tb!.Tuning!.Corners.Single();
        Assert.Equal("corner slow temp=85 part:TSTCAP=max", TuningDirectiveText.CornerLine(corner));

        var tuned = TunableOverrides.Apply(c.Tb!, c.Lib!, CornerRun.BindingsOf(corner));
        Assert.Null(tuned.Refusal);
        var nominal = new Elaborator(c.Lib!).Elaborate(c.Tb!);
        var atCorner = new Elaborator(tuned.Library!).Elaborate(tuned.TestBench!);
        Assert.Equal(11e-12, C(atCorner, "U1.C1"), 1e-24);
        Assert.Equal(11e-12, C(atCorner, "U2.C1"), 1e-24);
        Assert.Equal(R(nominal, "V1.R1"), R(atCorner, "V1.R1"));

        var parts = LibraryParts.Placed(c.Tb!, c.Lib!)
            .Where(p => p.Corners.Count > 0)
            .Select(p => new GeneratorValues(p.Part, [.. p.Corners.Select(k => k.Name)])).ToList();
        var gen = CornerGenerator.Parse("part:TSTCAP=min,max;temp=-40,25,85", [], parts);
        Assert.Null(gen.Refusal);
        Assert.Equal(6, gen.Corners.Count);
        Assert.Equal(6, gen.Corners.Select(k => k.Name).Distinct().Count());
        Assert.All(gen.Corners, k => Assert.Contains(k.PartCorners!["TSTCAP"], new[] { "min", "max" }));
        Assert.NotNull(CornerGenerator.Parse("part:TSTCAP=typ", [], parts).Refusal);
    }

    // 7 — R-vl13-2g: in a Monte Carlo at a corner that sets a part's corner, that part's draws are off and another part's on.
    [Fact]
    public void MonteCarloAtAPartCorner_HoldsThatPartsDraws_AndNotAnothers()
    {
        var c = PreparedCircuit.FromText(Bench("statistics trials=4\ncorner slow part:TSTCAP=max"), null, null);
        var result = CornerRun.Create(c, new CornerOptions { MonteCarlo = true, Mode = StatisticalMode.MonteCarlo }).Run();
        Assert.True(result.Yields.Count == 2, result.Refusal?.Render());
        var nominal = result.Yields[0].Result.Data!;
        var slow    = result.Yields[1].Result.Data!;
        Assert.True(Drew(nominal, "U1."));
        Assert.False(Drew(slow, "U1."));
        Assert.False(Drew(slow, "U2."));
        Assert.True(Drew(slow, "V1."));
    }

    // ── fixture ───────────────────────────────────────────────────────────────

    private static string Bench(string tuning) => $"""
        define TSTCAP (a b)
          part TSTCAP
          parameters C0=gauss(10,0.05,3) pF
          corner min C0=9 pF
          corner max C0=11 pF
          C:C1 a b C=C0
        end TSTCAP
        define TSTRES (a b)
          part TSTRES
          parameters R0=gauss(50,0.02,3) Ohm
          R:R1 a b R=R0
        end TSTRES
        Port:P1 in 0 Num=1 Z=50 Ohm
        Port:P2 out 0 Num=2 Z=50 Ohm
        TSTCAP:U1 in mid
        TSTCAP:U2 mid x
        TSTRES:V1 x out
        analysis SP1 type=sparam start=1 stop=2 npts=2 Unit=GHz
        {tuning}
        """;

    /// <summary>Whether any trial drew a mismatch stream under <paramref name="prefix"/>.</summary>
    private static bool Drew(DataSet data, string prefix)
        => data.CubesIn(StatisticalDataSet.TrialsGroup)
               .Where(kv => kv.Key.StartsWith("z:mismatch:" + prefix, StringComparison.Ordinal))
               .Any(kv => kv.Value.RealValues.Any(v => !double.IsNaN(v)));

    private sealed record Fix(string Bench, string Ref);

    /// <summary>
    /// <c>TSTCAP</c>: in — C1 (C0) — R1 (R0) — out, with <c>C0 = gauss(10, 0.05, 3) pF</c>, <c>R0 = 1 Ohm</c>, and the
    /// corners min (C0 9 pF, R0 0.5) and max (C0 11 pF), exported to a library the workspace references as <c>Lib</c>.
    /// </summary>
    private Fix Fixture()
    {
        string ws = WorkspaceCreate.Create(_dir, "ws", null).WorkspaceDir;
        var sch = new SchematicEditModel();
        var p1 = Pin("P1", 1, "in", 0, 0);
        var c1 = Part("C1", SymbolKind.Capacitor, 2000, 2000, "C", "C0", "");
        var r1 = Part("R1", SymbolKind.Resistor, 8000, 2000, "R", "R0", "");
        var p2 = Pin("P2", 2, "out", 14000, 0);
        foreach (var c in new[] { p1, c1, r1, p2 }) sch.Components.Add(c);
        Wire(sch, p1, 0, c1, 0);
        Wire(sch, c1, 1, r1, 0);
        Wire(sch, r1, 1, p2, 0);
        string cell = WriteCell(ws, "TSTCAP", sch);
        SymbolPersistence.SaveToFile(Path.Combine(CellFolder.SubFolderPath(cell, ViewType.Symbol), "TSTCAP.csym"),
                                     AutoSymbolGenerator.Generate("TSTCAP", 2));

        string ccellPath = Path.Combine(cell, CellFolder.CcellFileName);
        var ccell = CellPersistence.LoadFromFile(ccellPath);
        ccell.Parameters.Add(new CcellParameter { Name = "C0", DefaultExpression = "gauss(10, 0.05, 3)", Unit = "pF" });
        ccell.Parameters.Add(new CcellParameter { Name = "R0", DefaultExpression = "1", Unit = "Ohm" });
        ccell.Corners =
        [
            new CcellCorner { Name = "min", Values = new() { ["C0"] = "9 pF", ["R0"] = "0.5" } },
            new CcellCorner { Name = "max", Values = new() { ["C0"] = "11 pF", ["R0"] = "" } },
        ];
        CellPersistence.SaveToFile(ccellPath, ccell);

        string lib = Path.Combine(_dir, "lib");
        var export = ModelLibraryExport.Export(cell, new ExportOptions { LibraryDir = lib, Category = "Parts" });
        Assert.True(export.Succeeded, export.Refusal);
        Assert.Contains("corner min C0=9 pF R0=0.5 Ohm", File.ReadAllText(ModelNetlist.PathOf(export.ModelDir!)));

        var refs = new List<CwsVendorLibraryRef>();
        Assert.NotNull(VendorLibraryRefs.AddOrRepair(ws, refs, lib, "Lib", out _));
        var cws = WorkspacePersistence.LoadFromFile(Path.Combine(ws, ".cws"));
        cws.VendorLibraries = refs;
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), cws);
        WorkspaceRootFinder.InvalidateCache();

        string bench = Path.Combine(ws, "bench", "schematic");
        Directory.CreateDirectory(bench);
        return new Fix(bench, VendorModelRefs.RefFor("Lib", "Parts/TSTCAP"));
    }

    private static EditableComponent Placed(string name, string cellRef, double x, params (string Name, string Value, string Unit)[] rows)
    {
        var c = new EditableComponent { InstanceName = name, Symbol = SymbolKind.Generic, CellRef = cellRef, X = x };
        c.Parameters.Add(new EditableParameter { Name = VendorModelRefs.GlyphParameter, Expression = "", ShowOnSchematic = false });
        foreach (var (n, v, u) in rows) c.Parameters.Add(new EditableParameter { Name = n, Expression = v, Unit = u });
        return c;
    }

    private static NetExtractor.ExtractionResult Extract(string bench, params EditableComponent[] parts)
    {
        var model = new SchematicEditModel { SchematicDirectory = bench };
        foreach (var p in parts) model.Components.Add(p);
        var r = NetExtractor.Extract(model, "tb", DiskCellResolver.Instance);
        Assert.Empty(r.Conflicts);
        return r;
    }

    private static ElaboratedNetlist Elaborate(NetExtractor.ExtractionResult r)
        => new Elaborator(r.Library).Elaborate(r.TestBench);

    private static double C(ElaboratedNetlist n, string path) => n.Components.Single(c => c.InstancePath == path).Parameters["C"].AsReal();
    private static double R(ElaboratedNetlist n, string path) => n.Components.Single(c => c.InstancePath == path).Parameters["R"].AsReal();
}
