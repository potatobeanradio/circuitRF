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
        Assert.Equal(2, tb.RawDirectives.Count);
        Assert.Equal("analysis", tb.RawDirectives[0].Kind);
        Assert.Equal("measure",  tb.RawDirectives[1].Kind);
        // verbatim content preserved
        Assert.Contains("sparam", tb.RawDirectives[0].RawLine);
        Assert.Contains("InsertionLoss", tb.RawDirectives[1].RawLine);
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

    [Fact]
    public void InlineRead_RawDirectivesRoundTrip()
    {
        var src = "analysis SP type=sparam start=1 GHz\nmeasure Gain = dB(S(2,1))\n";
        var (_, tb) = new CnlReader().Read(src);
        Assert.Equal(2, tb.RawDirectives.Count);
        Assert.Equal("analysis", tb.RawDirectives[0].Kind);
        Assert.Equal("measure",  tb.RawDirectives[1].Kind);
        Assert.Equal("SP type=sparam start=1 GHz", tb.RawDirectives[0].RawLine);
        Assert.Equal("Gain = dB(S(2,1))", tb.RawDirectives[1].RawLine);
    }
}
