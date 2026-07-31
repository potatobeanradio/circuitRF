using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using Xunit;

namespace CircuitRF.Engine.Tests.Linear;

/// <summary>
/// The ABCD two-port. Every case here is checked against the analytic result for the network the
/// chain matrix describes, so the stamp is verified against network theory rather than against
/// itself.
/// </summary>
public class ChainModelTests
{
    private static NonlinearDcEngine.DcResult RunDc(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        return NonlinearDcEngine.Run(new Elaborator(lib).Elaborate(tb));
    }

    private static double NodeV(NonlinearDcEngine.DcResult r, string cnl, string net)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        int i = nl.Nodes.GetOrAssign(net);
        return i == 0 ? 0.0 : r.NodeVoltages[i - 1];
    }

    // ── The case that motivates the primitive ─────────────────────────────────

    /// <summary>
    /// A = D = 1, B = R, C = 0 is a pure series resistance. Its Z-matrix does not exist
    /// (Z11 = A/C = ∞), so this is exactly the two-port a Z_Port block cannot represent — and it is
    /// what a frequency-domain line model degenerates to at DC. The chain stamp must handle it
    /// without going singular.
    /// </summary>
    [Theory]
    [InlineData(50.0, 50.0)]     // equal divider → half
    [InlineData(10.0, 90.0)]
    [InlineData(1e-3, 1e3)]      // near-short series element
    public void SeriesImpedanceForm_CzeroAtDc_IsNonSingularAndCorrect(double rSeries, double rLoad)
    {
        string cnl = $@"
Vdc:VS   in 0   Vdc=10
Chain:CH in 0 out 0   A=1 B={rSeries.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} C=0 D=1
R:RL     out 0  R={rLoad.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} Ohm
";
        var r = RunDc(cnl);
        Assert.True(r.Converged, $"DC did not converge (residual {r.FinalResidual:G3}).");

        // Series R feeding RL is an ordinary divider.
        double expected = 10.0 * rLoad / (rSeries + rLoad);
        Assert.Equal(expected, NodeV(r, cnl, "out"), 6);
    }

    // ── Identity and general forms ────────────────────────────────────────────

    [Fact]
    public void IdentityChain_IsAThroughConnection()
    {
        string cnl = @"
Vdc:VS   in 0   Vdc=4
Chain:CH in 0 out 0   A=1 B=0 C=0 D=1
R:RL     out 0  R=25 Ohm
";
        var r = RunDc(cnl);
        Assert.True(r.Converged);
        Assert.Equal(4.0, NodeV(r, cnl, "out"), 8);
    }

    [Fact]
    public void OmittedEntries_DefaultToTheIdentityTwoPort()
    {
        // A partially-specified block must degrade to a wire, not to a silent zero matrix.
        string cnl = @"
Vdc:VS   in 0   Vdc=4
Chain:CH in 0 out 0   B=0
R:RL     out 0  R=25 Ohm
";
        var r = RunDc(cnl);
        Assert.True(r.Converged);
        Assert.Equal(4.0, NodeV(r, cnl, "out"), 8);
    }

    [Fact]
    public void ShuntAdmittanceForm_MatchesAnalytic()
    {
        // A = D = 1, B = 0, C = Y is a shunt admittance across the port.
        // Source 10 V through 100 Ω into a 0.01 S shunt (=100 Ω) → 5 V.
        string cnl = @"
Vdc:VS   in 0   Vdc=10
R:RS     in mid R=100 Ohm
Chain:CH mid 0 out 0   A=1 B=0 C=0.01 D=1
R:RL     out 0  R=1 GOhm
";
        var r = RunDc(cnl);
        Assert.True(r.Converged);
        Assert.Equal(5.0, NodeV(r, cnl, "mid"), 4);
    }

    [Fact]
    public void IdealTransformerForm_ScalesVoltageByTurnsRatio()
    {
        // A = 1/n, D = n is an ideal transformer of ratio n (V2 = V1·n, open-circuited).
        string cnl = @"
Vdc:VS   in 0   Vdc=2
Chain:CH in 0 out 0   A=0.25 B=0 C=0 D=4
R:RL     out 0  R=1 GOhm
";
        var r = RunDc(cnl);
        Assert.True(r.Converged);
        Assert.Equal(8.0, NodeV(r, cnl, "out"), 4);      // V2 = V1 / A = 2 / 0.25
    }

    // ── Frequency dependence ─────────────────────────────────────────────────

    [Fact]
    public void EntriesAreExpressionsInFreq_AndAreReEvaluatedPerFrequency()
    {
        // B = jωL as an explicit expression: a series inductor. |S21| must roll off with frequency.
        string cnl = @"
Port:P1  in 0   Num=1 Z=50 Ohm
Port:P2  out 0  Num=2 Z=50 Ohm
Chain:CH in 0 out 0   A=1 B=j*2*pi*freq*1e-9 C=0 D=1
";
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        double[] freqs = [1e8, 1e9, 1e10];
        var ds = SParameterEngine.Run(nl, freqs);

        double Mag21(int k) => ((System.Numerics.Complex)ds["S"][k, 1, 0]).Magnitude;

        Assert.True(Mag21(0) > Mag21(1) && Mag21(1) > Mag21(2),
            $"|S21| should fall with frequency for a series L: {Mag21(0):F4}, {Mag21(1):F4}, {Mag21(2):F4}");

        // Analytic: series Z between two 50 Ω ports → S21 = 2·Z0 / (2·Z0 + Z).
        for (int k = 0; k < freqs.Length; k++)
        {
            var z = new System.Numerics.Complex(0, 2 * Math.PI * freqs[k] * 1e-9);
            var expected = 100.0 / (100.0 + z);
            Assert.Equal(expected.Magnitude, Mag21(k), 6);
        }
    }
}
