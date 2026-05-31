using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Tests.Elaboration;

public class ElaboratorTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Library MakeLib(params Cell[] cells)
    {
        var lib = new Library("test");
        lib.Cells.AddRange(cells);
        return lib;
    }

    // ── Ground = 0 ────────────────────────────────────────────────────────────

    [Fact]
    public void GroundNodeIsZero()
    {
        var tb = new TestBench("tb");
        tb.Instances.Add(new Instance("R1", "R", ["N1", "0"],
            [new ParameterAssignment("R", "50")]));

        var nl = new Elaborator().Elaborate(tb);
        Assert.Equal(0, nl.Nodes.IndexOf("0"));
    }

    // ── Instance paths ────────────────────────────────────────────────────────

    [Fact]
    public void FlatInstancePathIsInstanceName()
    {
        var tb = new TestBench("tb");
        tb.Instances.Add(new Instance("R1", "R", ["N1", "0"],
            [new ParameterAssignment("R", "50")]));

        var nl = new Elaborator().Elaborate(tb);
        Assert.Equal("R1", nl.Components[0].InstancePath);
    }

    [Fact]
    public void HierarchicalInstancePathIsDotSeparated()
    {
        var inner = new Cell("Inner");
        inner.Ports.AddRange(["P1", "P2"]);
        inner.Instances.Add(new Instance("R1", "R", ["P1", "P2"],
            [new ParameterAssignment("R", "100")]));

        var tb = new TestBench("tb");
        tb.Instances.Add(new Instance("X1", "Inner", ["N3", "0"]));

        var nl = new Elaborator(MakeLib(inner)).Elaborate(tb);
        Assert.Equal("X1.R1", nl.Components[0].InstancePath);
    }

    // ── Port mapping / net connectivity ───────────────────────────────────────

    [Fact]
    public void SubCellPortsMappedToParentNets()
    {
        // Inner has ports P1,P2 and a resistor P1—P2.
        // Top instances Inner:X1 connecting N3,N7.
        // The resistor R1 should be on net indices for N3 and N7.
        var inner = new Cell("Inner");
        inner.Ports.AddRange(["P1", "P2"]);
        inner.Instances.Add(new Instance("R1", "R", ["P1", "P2"],
            [new ParameterAssignment("R", "50")]));

        var tb = new TestBench("tb");
        tb.Instances.Add(new Instance("X1", "Inner", ["N3", "N7"]));

        var nl = new Elaborator(MakeLib(inner)).Elaborate(tb);

        var r1 = nl.Components[0];
        int n3 = nl.Nodes.IndexOf("N3");
        int n7 = nl.Nodes.IndexOf("N7");
        Assert.Contains(n3, r1.Nodes);
        Assert.Contains(n7, r1.Nodes);
    }

    // ── Parameter resolution: override in parent scope ─────────────────────────

    [Fact]
    public void OverrideExpression_EvaluatedInParentScope()
    {
        // Global variable C2=10. Inner's parameter C defaults to 1.
        // X1 overrides C=C2 (evaluated in the TestBench's global scope → 10).
        // C1 inside Inner uses parameter C → should resolve to 10.
        var inner = new Cell("Inner");
        inner.Ports.AddRange(["P1", "P2"]);
        inner.Parameters.Add(new ParameterDeclaration("C", "1", "pF"));
        inner.Instances.Add(new Instance("C1", "C", ["P1", "P2"],
            [new ParameterAssignment("C", "C")]));

        var tb = new TestBench("tb");
        tb.GlobalVariables.Add(new Variable("C2", "10"));
        tb.Instances.Add(new Instance("X1", "Inner", ["N1", "0"],
            [new ParameterAssignment("C", "C2")]));

        var nl = new Elaborator(MakeLib(inner)).Elaborate(tb);

        var c1 = nl.Components.Single(c => c.InstancePath == "X1.C1");
        Assert.Equal(ValueKind.Real, c1.Parameters["C"].Kind);
        Assert.Equal(10.0, c1.Parameters["C"].AsReal(), 1e-10);
    }

    [Fact]
    public void DefaultExpression_EvaluatedInCellScope()
    {
        // Inner's parameter C defaults to "2*factor" where factor is a cell variable = 3.
        // No override given; C should resolve to 6.
        var inner = new Cell("Inner");
        inner.Ports.AddRange(["P1", "P2"]);
        inner.Variables.Add(new Variable("factor", "3"));
        inner.Parameters.Add(new ParameterDeclaration("C", "2*factor"));
        inner.Instances.Add(new Instance("C1", "C", ["P1", "P2"],
            [new ParameterAssignment("C", "C")]));

        var tb = new TestBench("tb");
        tb.Instances.Add(new Instance("X1", "Inner", ["N1", "0"]));

        var nl = new Elaborator(MakeLib(inner)).Elaborate(tb);

        var c1 = nl.Components.Single(c => c.InstancePath == "X1.C1");
        Assert.Equal(6.0, c1.Parameters["C"].AsReal(), 1e-10);
    }

    // ── Global variable propagation ───────────────────────────────────────────

    [Fact]
    public void GlobalVariableVisibleInFlatCircuit()
    {
        var tb = new TestBench("tb");
        tb.GlobalVariables.Add(new Variable("Rval", "75"));
        tb.Instances.Add(new Instance("R1", "R", ["N1", "0"],
            [new ParameterAssignment("R", "Rval")]));

        var nl = new Elaborator().Elaborate(tb);

        var r1 = nl.Components[0];
        Assert.Equal(75.0, r1.Parameters["R"].AsReal(), 1e-10);
    }

    // ── TestBench instances are the root frame ───────────────────────────────

    [Fact]
    public void TestBench_PrimitivesAtTopLevelDirectly()
    {
        // Port and R placed directly on the TestBench — no wrapper cell.
        var tb = new TestBench("tb");
        tb.Instances.Add(new Instance("Term1", "Port", ["N1", "0"],
            [new ParameterAssignment("Num", "1"), new ParameterAssignment("Z", "50", "Ohm")]));
        tb.Instances.Add(new Instance("R1", "R", ["N1", "0"],
            [new ParameterAssignment("R", "50", "Ohm")]));

        var nl = new Elaborator().Elaborate(tb);
        Assert.Equal(2, nl.Components.Count);
        Assert.Equal("Term1", nl.Components[0].InstancePath);
        Assert.Equal("R1",    nl.Components[1].InstancePath);
    }

    // ── Nonlinear partition (wired but empty in Phase 1) ─────────────────────

    [Fact]
    public void NonlinearSetsAreEmptyForLinearCircuit()
    {
        var tb = new TestBench("tb");
        tb.Instances.Add(new Instance("R1", "R", ["N1", "0"],
            [new ParameterAssignment("R", "50")]));

        var nl = new Elaborator().Elaborate(tb);
        Assert.Empty(nl.NonlinearComponents);
        Assert.Empty(nl.NonlinearNodes);
    }

    // ── Unknown cell ─────────────────────────────────────────────────────────

    [Fact]
    public void UnknownCellThrows()
    {
        var tb = new TestBench("tb");
        tb.Instances.Add(new Instance("X1", "NoSuchCell", ["N1", "0"]));

        Assert.Throws<InvalidOperationException>(() =>
            new Elaborator().Elaborate(tb));
    }
}
