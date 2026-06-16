using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Core.Tests.Devices;

/// <summary>
/// Gate tests for single-index SDD equation forms (I[p] and Q[p]) and net-arity validation.
/// Brief: brief-sdd-single-index-nets.md
/// </summary>
public class SddSingleIndexTests
{
    private static (SddModel model, string[] nets) ParseSddWithNets(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl   = new Elaborator(lib).Elaborate(tb);
        var ec   = nl.Components.First(c => c.Model is SddModel);
        // Top-level SDD (no define block) lives directly on tb.Instances.
        var inst = tb.Instances.First(i => i.Reference.Equals("SDD", StringComparison.OrdinalIgnoreCase));
        return ((SddModel)ec.Model, inst.NetBindings.ToArray());
    }

    private static SddModel ParseSdd(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl  = new Elaborator(lib).Elaborate(tb);
        return (SddModel)nl.Components.First(c => c.Model is SddModel).Model;
    }

    // ── Test 1: nets vs equations are correctly split ────────────────────────

    [Fact]
    public void Sdd_SingleIndex_I_p_Parses_NetsAndEquationsSplit()
    {
        var (_, nets) = ParseSddWithNets(@"
SDD:X1  Vin 0  Vout 0  I[1]=_v1/50  I[2]=_v2/100
");
        // Exactly 4 nets — no equation fragments leaked into net list
        Assert.Equal(["Vin", "0", "Vout", "0"], nets);
        Assert.DoesNotContain(nets, n => n.Contains("I[", StringComparison.Ordinal));
        Assert.DoesNotContain(nets, n => n.Contains("_v", StringComparison.Ordinal));
        Assert.DoesNotContain(nets, n => n.Contains("=",  StringComparison.Ordinal));
    }

    // ── Test 2: I[p] binds currentAst, chargeAst stays null ─────────────────

    [Fact]
    public void Sdd_SingleIndex_BindsCurrent()
    {
        var sdd = ParseSdd(@"
SDD:X1  Vin 0  Vout 0  I[1]=_v1/50  I[2]=_v2/100
");
        // Both port currents bound, charges null
        var r1 = sdd.Evaluate(new PortVoltages([10.0, 5.0]));
        Assert.Equal(10.0 / 50.0, r1.I[0], 10);
        Assert.Equal(5.0  / 100.0, r1.I[1], 10);
        // No charge contribution
        Assert.Equal(0.0, r1.Q[0]);
        Assert.Equal(0.0, r1.Q[1]);
    }

    // ── Test 3: Q[p] binds chargeAst, currentAst stays null ─────────────────

    [Fact]
    public void Sdd_Qp_BindsCharge()
    {
        const double Cv = 1e-12;
        var sdd = ParseSdd($@"
Cv = {Cv}
SDD:X1  n1 0  Q[1]=Cv*_v1
");
        var r = sdd.Evaluate(new PortVoltages([5.0]));
        Assert.Equal(0.0, r.I[0]);               // no current term
        Assert.True(Math.Abs(r.Q[0] - Cv * 5.0) < 1e-25, $"q={r.Q[0]:G}");
        Assert.True(Math.Abs(r.Dc[0, 0] - Cv)   < 1e-24, $"dc={r.Dc[0,0]:G}");
    }

    // ── Test 4: two-index forms still work (regression) ─────────────────────

    [Fact]
    public void Sdd_TwoIndex_StillWorks()
    {
        var sdd = ParseSdd(@"
Cv = 1e-12
SDD:X1  n1 0 n2 0  I[1,0]=_v1/50  I[2,1]=Cv*_v2
");
        var r = sdd.Evaluate(new PortVoltages([10.0, 3.0]));
        Assert.Equal(10.0 / 50.0,   r.I[0], 10);   // port 1 current
        Assert.Equal(0.0,            r.I[1], 10);   // port 2 current (no I[2,0])
        Assert.Equal(0.0,            r.Q[0]);        // port 1 charge (no Q[1])
        Assert.True(Math.Abs(r.Q[1] - 1e-12 * 3.0) < 1e-25, $"q[1]={r.Q[1]:G}");
    }

    // ── Test 5: odd net count → clear error ──────────────────────────────────

    [Fact]
    public void Sdd_OddNets_Errors()
    {
        var ex = Assert.ThrowsAny<Exception>(() =>
            ParseSdd("SDD:X1  Vin Vout 0  I[1]=_v1  I[2]=_v2\n"));

        Assert.Contains("even", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3",    ex.Message);
    }

    // ── Test 6: equation references port beyond net count → clear error ──────

    [Fact]
    public void Sdd_PortRefBeyondNets_Errors()
    {
        // 2 nets → 1 port; equation references port 2 → must error
        var ex = Assert.ThrowsAny<Exception>(() =>
            ParseSdd("SDD:X1  Vin 0  I[1]=_v1/50  I[2]=_v2/100\n"));

        Assert.Contains("port 2", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
