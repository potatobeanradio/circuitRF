using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Core.Tests.Netlist;

/// <summary>
/// Gate tests for the Z_Port 2N-net per-port reference reform (brief-zport-per-port-refs).
/// Tests 1–3 and 7 from the brief.
/// </summary>
public class ZPortArityTests
{
    // ── Test 1: Z_Port 2-port parses 4 nets ──────────────────────────────────

    [Fact]
    public void ZPort_2Port_Parses4Nets()
    {
        const string cnl = "Z_Port:Z1  a 0 b 0  Z[1,1]=50 Z[2,2]=50 Z[1,2]=0 Z[2,1]=0";
        var (_, tb) = new CnlReader().Read(cnl);

        var inst = Assert.Single(tb.Instances);
        Assert.Equal("Z1", inst.InstanceName);
        Assert.Equal("Z_Port", inst.Reference);
        Assert.Equal(["a", "0", "b", "0"], inst.NetBindings.ToList());
        Assert.Null(inst.RefNetBinding);
    }

    // ── Test 2: Odd net count → ±-pair arity error ───────────────────────────

    [Fact]
    public void ZPort_OddNets_Errors()
    {
        // 3 nets with Z[2,2] present → portCount=2, expected 4 nets, got 3 → error.
        var tb = new TestBench("test");
        tb.Instances.Add(new Instance("Z1", "Z_Port",
            ["a", "b", "c"],
            [new ParameterAssignment("Z[2,2]", "50")]));

        var lib = new Library("test");
        var ex = Assert.Throws<InvalidOperationException>(
            () => new Elaborator(lib).Elaborate(tb));

        Assert.Contains("even number of nets", ex.Message);
        Assert.Contains("Z1", ex.Message);
    }

    // ── Test 3: Net count mismatch → "expected N nets" error ─────────────────

    [Fact]
    public void ZPort_NetCountMismatch_Errors()
    {
        // Z[2,2] present (portCount=2) but only 2 nets → expected 4, got 2.
        var tb = new TestBench("test");
        tb.Instances.Add(new Instance("Z1", "Z_Port",
            ["a", "0"],
            [new ParameterAssignment("Z[2,2]", "50")]));

        var lib = new Library("test");
        var ex = Assert.Throws<InvalidOperationException>(
            () => new Elaborator(lib).Elaborate(tb));

        Assert.Contains("expected 4 nets", ex.Message);
        Assert.Contains("Z1", ex.Message);
    }

    // ── Test 7: SnP N-or-(N+1) convention unchanged by Z_Port change ─────────

    [Fact]
    public void SnP_NOrNPlus1_Unchanged()
    {
        // SnP with N+1 nets should still parse RefNetBinding correctly.
        const string cnl = "SnP:S1  n1 n_ref  NumPorts=1 File=\"test.s1p\"";
        var (_, tb) = new CnlReader().Read(cnl);

        var inst = Assert.Single(tb.Instances);
        Assert.Equal("SnP", inst.Reference);
        Assert.Equal(["n1"], inst.NetBindings.ToList());
        Assert.Equal("n_ref", inst.RefNetBinding);
    }
}
