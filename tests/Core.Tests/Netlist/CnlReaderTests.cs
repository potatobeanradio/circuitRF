using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;

namespace CircuitRF.Core.Tests.Netlist;

public class CnlReaderTests
{
    private static string FixturePath(string name)
    {
        // Walk up from the test output directory to find testdata/
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "testdata", name);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException($"Fixture not found: {name}");
    }

    // ── recursion.cnl — valid multi-hop chain ─────────────────────────────────

    [Fact]
    public void RecursionFixture_ValidChainResolvesToTwo()
    {
        var path = FixturePath("recursion.cnl");
        var (lib, tb) = CnlReader.ReadFile(path);

        // C2 = gizmo = funtimes = 2
        var globalScope = new Scope("global");
        foreach (var v in tb.GlobalVariables)
            globalScope.Bind(v.Name, v.Expression, v.Unit);

        var ev = new Evaluator();
        var val = ev.Resolve("C2", globalScope);
        Assert.Equal(ValueKind.Real, val.Kind);
        Assert.Equal(2.0, val.AsReal());
    }

    // ── cycle.cnl — cyclic fixture ────────────────────────────────────────────

    [Fact]
    public void CycleFixture_ReportsCycleAndDoesNotHang()
    {
        var path = FixturePath("cycle.cnl");
        var (lib, tb) = CnlReader.ReadFile(path);

        var globalScope = new Scope("global");
        foreach (var v in tb.GlobalVariables)
            globalScope.Bind(v.Name, v.Expression, v.Unit);

        var ex = Assert.Throws<CycleException>(() => new Evaluator().Resolve("a", globalScope));
        Assert.Contains("a", ex.Chain);
        Assert.Contains("b", ex.Chain);
    }

    // ── pi_network.cnl — primary round-trip fixture ───────────────────────────

    [Fact]
    public void PiNetwork_GlobalVariablesParsed()
    {
        var (lib, tb) = CnlReader.ReadFile(FixturePath("pi_network.cnl"));
        var names = tb.GlobalVariables.Select(v => v.Name).ToHashSet();
        Assert.Contains("L1",      names);
        Assert.Contains("C2",      names);
        Assert.Contains("gizmo",   names);
        Assert.Contains("funtimes",names);
    }

    [Fact]
    public void PiNetwork_MyPiCellDefined()
    {
        var (lib, tb) = CnlReader.ReadFile(FixturePath("pi_network.cnl"));
        var cell = lib.Find("MyPiCell");
        Assert.NotNull(cell);
        Assert.Equal(["P1", "P2", "P3"], cell.Ports);
    }

    [Fact]
    public void PiNetwork_MyPiCellHasParameters()
    {
        var (lib, tb) = CnlReader.ReadFile(FixturePath("pi_network.cnl"));
        var cell = lib.Find("MyPiCell")!;
        var pnames = cell.Parameters.Select(p => p.Name).ToHashSet();
        Assert.Contains("L1", pnames);
        Assert.Contains("C2", pnames);
    }

    [Fact]
    public void PiNetwork_MyPiCellHasFourComponents()
    {
        var (lib, tb) = CnlReader.ReadFile(FixturePath("pi_network.cnl"));
        var cell = lib.Find("MyPiCell")!;
        Assert.Equal(4, cell.Instances.Count); // R1, L1, C1, C2
    }

    [Fact]
    public void PiNetwork_TopLevelInstancesPresent()
    {
        // Top-level instances now live directly on the TestBench, not in a synthetic cell.
        var (lib, tb) = CnlReader.ReadFile(FixturePath("pi_network.cnl"));
        var names = tb.Instances.Select(i => i.InstanceName).ToHashSet();
        Assert.Contains("Term1", names);
        Assert.Contains("Term2", names);
        Assert.Contains("X1",    names);
        Assert.Contains("X2",    names);
    }

    [Fact]
    public void PiNetwork_RawDirectivesPreserved()
    {
        var (lib, tb) = CnlReader.ReadFile(FixturePath("pi_network.cnl"));
        // type=sparam is now promoted to a typed SParameterAnalysis, not a RawDirective
        Assert.Empty(tb.RawDirectives);
        var spa = Assert.Single(tb.Analyses) as SParameterAnalysis;
        Assert.NotNull(spa);
        Assert.Equal("SP", spa.Name);
        Assert.Single(spa.Sweeps);
        // measure promoted to Measurements collection
        var m = Assert.Single(tb.Measurements);
        Assert.Equal("InsertionLoss", m.Name);
        Assert.Contains("dB", m.Expression);
    }

    // ── Full elaboration round-trip ───────────────────────────────────────────

    [Fact]
    public void PiNetwork_ElaboratesWithoutError()
    {
        var (lib, tb) = CnlReader.ReadFile(FixturePath("pi_network.cnl"));
        var nl = new Elaborator(lib).Elaborate(tb);
        Assert.NotEmpty(nl.Components);
    }

    [Fact]
    public void PiNetwork_GroundIsNodeZero()
    {
        var (lib, tb) = CnlReader.ReadFile(FixturePath("pi_network.cnl"));
        var nl = new Elaborator(lib).Elaborate(tb);
        Assert.Equal(0, nl.Nodes.IndexOf("0"));
    }

    [Fact]
    public void PiNetwork_X1ComponentsHaveCorrectPaths()
    {
        var (lib, tb) = CnlReader.ReadFile(FixturePath("pi_network.cnl"));
        var nl = new Elaborator(lib).Elaborate(tb);
        var paths = nl.Components.Select(c => c.InstancePath).ToHashSet();
        Assert.Contains("X1.R1", paths);
        Assert.Contains("X1.L1", paths);
        Assert.Contains("X1.C1", paths);
        Assert.Contains("X1.C2", paths);
        Assert.Contains("X2.R1", paths);
    }

    [Fact]
    public void PiNetwork_X1_C2_ResolvesToGlobalChain()
    {
        // X1 override C2=C2 passes the global C2 (= gizmo = funtimes = 2) into MyPiCell.
        // Inside MyPiCell, C1 has C=C2 pF. So C1.C should be 2 pF = 2e-12 F.
        var (lib, tb) = CnlReader.ReadFile(FixturePath("pi_network.cnl"));
        var nl = new Elaborator(lib).Elaborate(tb);
        var c1 = nl.Components.Single(c => c.InstancePath == "X1.C1");
        Assert.Equal(ValueKind.Real, c1.Parameters["C"].Kind);
        Assert.Equal(2e-12, c1.Parameters["C"].AsReal(), 1e-25);
    }

    [Fact]
    public void PiNetwork_X2_L1_ResolvesToQuarterNano()
    {
        // X2 override L1=0.25. Inside MyPiCell, L:L1 uses L=L1 nH. So L=0.25e-9 H.
        var (lib, tb) = CnlReader.ReadFile(FixturePath("pi_network.cnl"));
        var nl = new Elaborator(lib).Elaborate(tb);
        var l1 = nl.Components.Single(c => c.InstancePath == "X2.L1");
        Assert.Equal(ValueKind.Real, l1.Parameters["L"].Kind);
        Assert.Equal(0.25e-9, l1.Parameters["L"].AsReal(), 1e-25);
    }

    [Fact]
    public void PiNetwork_X1_L1_ResolvesTo5nH()
    {
        // X1 override L1=5. L=L1 nH → 5e-9 H.
        var (lib, tb) = CnlReader.ReadFile(FixturePath("pi_network.cnl"));
        var nl = new Elaborator(lib).Elaborate(tb);
        var l1 = nl.Components.Single(c => c.InstancePath == "X1.L1");
        Assert.Equal(5e-9, l1.Parameters["L"].AsReal(), 1e-25);
    }

    [Fact]
    public void PiNetwork_X2_C2_ResolvesTo10pF()
    {
        // X2 override C2=10. Inside MyPiCell, C:C1 uses C=C2 pF → 10e-12 F.
        var (lib, tb) = CnlReader.ReadFile(FixturePath("pi_network.cnl"));
        var nl = new Elaborator(lib).Elaborate(tb);
        var c1 = nl.Components.Single(c => c.InstancePath == "X2.C1");
        Assert.Equal(10e-12, c1.Parameters["C"].AsReal(), 1e-25);
    }

    // ── Inline .cnl tests ────────────────────────────────────────────────────

    [Fact]
    public void InlineRead_CommentsSkipped()
    {
        var src = "; this is a comment\nL1 = 5\n";
        var (_, tb) = new CnlReader().Read(src);
        Assert.Single(tb.GlobalVariables);
        Assert.Equal("L1", tb.GlobalVariables[0].Name);
    }

    [Fact]
    public void InlineRead_UnitOnGlobalVariable()
    {
        // Global variable with a unit suffix
        var src = "f0 = 2.4 GHz\n";
        var (_, tb) = new CnlReader().Read(src);
        Assert.Equal("GHz", tb.GlobalVariables[0].Unit);
        Assert.Equal("2.4", tb.GlobalVariables[0].Expression);
    }

    [Fact]
    public void InlineRead_PrimitiveWithUnitParam()
    {
        // Top-level primitives now go directly onto the TestBench.
        var src = "R:R1  N1 0  R=50 Ohm\n";
        var (_, tb) = new CnlReader().Read(src);
        var r1 = tb.Instances[0];
        Assert.Equal("R1", r1.InstanceName);
        Assert.Equal("R", r1.Reference);
        Assert.Equal(["N1", "0"], r1.NetBindings);
        var ov = r1.Overrides[0];
        Assert.Equal("R", ov.Name);
        Assert.Equal("50", ov.Expression);
        Assert.Equal("Ohm", ov.Unit);
    }

    [Theory]
    [InlineData("R:R1  N1 0  R=50 Ohm\n")]      // canonical
    [InlineData("R:R1  N1 0  R = 50 Ohm\n")]    // spaces around '='
    [InlineData("R:R1  N1 0  R =50 Ohm\n")]     // space before '='
    [InlineData("R:R1  N1 0  R= 50 Ohm\n")]     // space after '='
    public void InlineRead_SpacedEquals_ParsesSameAsCanonical(string src)
    {
        var (_, tb) = new CnlReader().Read(src);
        var r1 = tb.Instances[0];
        Assert.Equal(["N1", "0"], r1.NetBindings);   // 'R' must NOT be misread as a net
        var ov = r1.Overrides.Single();
        Assert.Equal("R", ov.Name);
        Assert.Equal("50", ov.Expression);
        Assert.Equal("Ohm", ov.Unit);
    }

    [Fact]
    public void InlineRead_SpacedEquals_CapacitorWithUnit()
    {
        // The Hero-4 case that broke the parser: "C = 1 uF".
        var (_, tb) = new CnlReader().Read("C:Cb  a b  C = 1 uF\n");
        var c = tb.Instances[0];
        Assert.Equal(["a", "b"], c.NetBindings);
        Assert.Equal("C",  c.Overrides.Single().Name);
        Assert.Equal("1",  c.Overrides.Single().Expression);
        Assert.Equal("uF", c.Overrides.Single().Unit);
    }

    [Theory]
    [InlineData("C:Cb a b C=1uF\n")]        // glued unit, no space around '='
    [InlineData("C:Cb a b C = 1uF\n")]      // glued unit + spaces around '=' (the Hero-4 example)
    [InlineData("C:Cb a b C =1uF\n")]
    public void InlineRead_GluedUnit_SplitsValueAndUnit(string src)
    {
        var (_, tb) = new CnlReader().Read(src);
        var c = tb.Instances[0];
        Assert.Equal(["a", "b"], c.NetBindings);
        Assert.Equal("C",  c.Overrides.Single().Name);
        Assert.Equal("1",  c.Overrides.Single().Expression);
        Assert.Equal("uF", c.Overrides.Single().Unit);
    }

    [Theory]
    [InlineData("R:Rx a b R=2e9\n",     "2e9")]      // scientific literal — must NOT split
    [InlineData("R:Rx a b R=2e-12\n",   "2e-12")]    // negative exponent — must NOT split
    [InlineData("R:Rx a b R=50\n",      "50")]       // bare number — no unit
    [InlineData("V:Vx a b V=Vs_mag\n",  "Vs_mag")]   // identifier — must NOT split
    public void InlineRead_GluedUnit_DoesNotSplitNonUnits(string src, string expectExpr)
    {
        var (_, tb) = new CnlReader().Read(src);
        var ov = tb.Instances[0].Overrides.Single();
        Assert.Equal(expectExpr, ov.Expression);
        Assert.Null(ov.Unit);
    }

    [Fact]
    public void InlineRead_SpacedEquals_MultipleParamsAndNets()
    {
        // Two params, mixed spacing, several nets — order and values preserved.
        var (_, tb) = new CnlReader().Read("L:Lx  n1 n2  L=300 pH  C = 1 uF\n");
        var l = tb.Instances[0];
        Assert.Equal(["n1", "n2"], l.NetBindings);
        Assert.Equal("300", l.Overrides[0].Expression);
        Assert.Equal("pH",  l.Overrides[0].Unit);
        Assert.Equal("C",   l.Overrides[1].Name);
        Assert.Equal("1",   l.Overrides[1].Expression);
        Assert.Equal("uF",  l.Overrides[1].Unit);
    }

    [Fact]
    public void InlineRead_SParamPromotedToTyped()
    {
        var src = "analysis SP type=sparam start=1 GHz\nmeasure Gain = dB(S(2,1))\n";
        var (_, tb) = new CnlReader().Read(src);
        // type=sparam is now promoted to a typed SParameterAnalysis, not a RawDirective
        Assert.Empty(tb.RawDirectives);
        var spa = Assert.Single(tb.Analyses) as SParameterAnalysis;
        Assert.NotNull(spa);
        Assert.Equal("SP", spa.Name);
        Assert.Single(spa.Sweeps);
        // measure promoted
        var m = Assert.Single(tb.Measurements);
        Assert.Equal("Gain",       m.Name);
        Assert.Equal("dB(S(2,1))", m.Expression);
    }
}
