using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Core.Tests.Netlist;

/// <summary>
/// Round-trip tests for CnlWriter Library support: define … end cell blocks.
/// </summary>
public class CnlHierarchyTests
{
    private static (Library lib, TestBench tb) RoundTrip(TestBench tb, Library lib)
    {
        var text = CnlWriter.Write(tb, lib);
        return new CnlReader().Read(text);
    }

    // ── Test 1: cell with ports, one parameter, two primitives ───────────────

    [Fact]
    public void Cell_WithPortsAndParameter_RoundTrips()
    {
        var cell = new Cell("amp");
        cell.Ports.AddRange(["in", "out"]);
        cell.Parameters.Add(new ParameterDeclaration("gain", "10", null));
        cell.Instances.Add(new Instance("R1", "R", ["in", "out"], [new ParameterAssignment("R", "50", "Ohm")]));
        cell.Instances.Add(new Instance("C1", "C", ["out", "0"], [new ParameterAssignment("C", "1", "pF")]));

        var lib = new Library("netlist");
        lib.Cells.Add(cell);

        var tb = new TestBench("tb");
        tb.Instances.Add(new Instance("X1", "amp", ["n1", "n2"], [new ParameterAssignment("gain", "20", null)]));

        var (rLib, rTb) = RoundTrip(tb, lib);

        // Library round-trips
        var rAmp = rLib.Find("amp");
        Assert.NotNull(rAmp);
        Assert.Equal(["in", "out"], rAmp.Ports.ToArray());

        var param = Assert.Single(rAmp.Parameters);
        Assert.Equal("gain", param.Name);
        Assert.Equal("10",   param.DefaultExpression);
        Assert.Null(param.Unit);

        Assert.Equal(2, rAmp.Instances.Count);
        Assert.Equal("R",          rAmp.Instances[0].Reference);
        Assert.Equal(["in", "out"], rAmp.Instances[0].NetBindings.ToArray());
        Assert.Contains(rAmp.Instances[0].Overrides, ov => ov.Name == "R" && ov.Expression == "50");

        Assert.Equal("C",        rAmp.Instances[1].Reference);
        Assert.Equal(["out", "0"], rAmp.Instances[1].NetBindings.ToArray());
        Assert.Contains(rAmp.Instances[1].Overrides, ov => ov.Name == "C" && ov.Expression == "1");

        // Top-level instance round-trips as a cell instance
        var x1 = Assert.Single(rTb.Instances);
        Assert.Equal("amp",      x1.Reference);
        Assert.Equal(["n1", "n2"], x1.NetBindings.ToArray());
        Assert.Equal("20", x1.Overrides.Single(o => o.Name == "gain").Expression);
    }

    // ── Test 2: cell with no parameters (no "parameters" line) ──────────────

    [Fact]
    public void Cell_NoParameters_NoParametersLineEmitted()
    {
        var cell = new Cell("buf");
        cell.Ports.AddRange(["a", "b"]);
        cell.Instances.Add(new Instance("R1", "R", ["a", "b"], [new ParameterAssignment("R", "100", "Ohm")]));

        var lib = new Library("netlist");
        lib.Cells.Add(cell);

        var tb = new TestBench("tb");
        tb.Instances.Add(new Instance("X2", "buf", ["p", "q"], []));

        var text = CnlWriter.Write(tb, lib);
        Assert.DoesNotContain("parameters", text);

        var (rLib, rTb) = new CnlReader().Read(text);

        var rBuf = rLib.Find("buf");
        Assert.NotNull(rBuf);
        Assert.Empty(rBuf.Parameters);
        Assert.Equal(["a", "b"], rBuf.Ports.ToArray());
        Assert.Single(rBuf.Instances);

        Assert.Single(rTb.Instances);
    }

    // ── Test 3: cell with no ports — "define Name ()" ────────────────────────

    [Fact]
    public void Cell_NoPorts_EmptyParentheses()
    {
        var cell = new Cell("bias");
        // No ports added
        cell.Instances.Add(new Instance("V1", "V", ["vdd", "0"], [new ParameterAssignment("V", "3.3")]));

        var lib = new Library("netlist");
        lib.Cells.Add(cell);

        var tb = new TestBench("tb");

        var text = CnlWriter.Write(tb, lib);
        Assert.Contains("define bias ()", text);

        var (rLib, _) = new CnlReader().Read(text);

        var rBias = rLib.Find("bias");
        Assert.NotNull(rBias);
        Assert.Empty(rBias.Ports);
        Assert.Single(rBias.Instances);
    }

    // ── Test 4: nested cells — cell A instances cell B ───────────────────────

    [Fact]
    public void NestedCells_BothRoundTrip()
    {
        // Leaf cell B
        var cellB = new Cell("res");
        cellB.Ports.AddRange(["p", "n"]);
        cellB.Parameters.Add(new ParameterDeclaration("R", "50", "Ohm"));
        cellB.Instances.Add(new Instance("R1", "R", ["p", "n"], [new ParameterAssignment("R", "R", "Ohm")]));

        // Parent cell A instances B
        var cellA = new Cell("filter");
        cellA.Ports.AddRange(["in", "out"]);
        cellA.Instances.Add(new Instance("XR", "res", ["in", "out"], [new ParameterAssignment("R", "75", "Ohm")]));
        cellA.Instances.Add(new Instance("C1", "C", ["out", "0"], [new ParameterAssignment("C", "100", "pF")]));

        var lib = new Library("netlist");
        lib.Cells.Add(cellB);
        lib.Cells.Add(cellA);

        var tb = new TestBench("tb");
        tb.Instances.Add(new Instance("X1", "filter", ["sig", "load"], []));

        var (rLib, rTb) = RoundTrip(tb, lib);

        // Both cells present
        var rB = rLib.Find("res");
        Assert.NotNull(rB);
        Assert.Equal(["p", "n"], rB.Ports.ToArray());
        var rBParam = Assert.Single(rB.Parameters);
        Assert.Equal("R", rBParam.Name);
        Assert.Equal("50", rBParam.DefaultExpression);
        Assert.Equal("Ohm", rBParam.Unit);

        var rA = rLib.Find("filter");
        Assert.NotNull(rA);
        Assert.Equal(["in", "out"], rA.Ports.ToArray());
        Assert.Equal(2, rA.Instances.Count);

        // Cell instance inside filter: XR references res
        var xr = rA.Instances.FirstOrDefault(i => i.InstanceName == "XR");
        Assert.NotNull(xr);
        Assert.Equal("res", xr.Reference);
        Assert.Equal(["in", "out"], xr.NetBindings.ToArray());

        // Top-level instance
        var x1 = Assert.Single(rTb.Instances);
        Assert.Equal("filter", x1.Reference);
    }

    // ── Test 5: flat Write(tb, header) still works unchanged ─────────────────

    [Fact]
    public void FlatWrite_NoLibrary_Unchanged()
    {
        var tb = new TestBench("flat");
        tb.Instances.Add(new Instance("R1", "R", ["a", "b"], [new ParameterAssignment("R", "50", "Ohm")]));

        var text = CnlWriter.Write(tb, "flat test");

        Assert.DoesNotContain("define", text);
        Assert.DoesNotContain("end", text);
        Assert.Contains("R:R1", text);

        var (rLib, rTb) = new CnlReader().Read(text);
        Assert.Empty(rLib.Cells);
        Assert.Single(rTb.Instances);
    }
}
