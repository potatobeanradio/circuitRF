using System;
using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using Xunit;

namespace CircuitRF.Engine.Tests.Linear;

/// <summary>
/// Issue 55: a delay <c>T</c> on the VCCS and the VCVS, and the ideal <c>Delay</c> system block. Every expected value is closed form — <c>e^(−jωT)</c> — and each claim
/// is asserted at two frequencies, because a delay is a phase PROPORTIONAL to frequency and a single
/// point cannot tell it apart from a constant phase.
/// </summary>
public class DelayTests
{
    private static readonly double[] Freqs = [1e9, 3e9];

    private static Complex[] S21(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var ds = SParameterEngine.Run(new Elaborator(lib).Elaborate(tb), Freqs);
        var s = new Complex[Freqs.Length];
        for (int f = 0; f < Freqs.Length; f++) s[f] = (Complex)ds["S"][f, 1, 0];
        return s;
    }

    private static Complex Lag(double f, double t) => Complex.Exp(new Complex(0, -2 * Math.PI * f * t));

    private static void AssertClose(Complex expected, Complex actual)
        => Assert.True((expected - actual).Magnitude < 1e-9, $"expected {expected}, got {actual}");

    private const string VccsStage = @"
Port:P1   nin  0   Num=1  Z=50 Ohm
R:Rin     nin  0   R=50 Ohm
VCCS:G1   nout 0 nin 0   {0}
R:RL      nout 0   R=50 Ohm
Port:P2   nout 0   Num=2  Z=50 Ohm
";

    [Fact]
    public void Vccs_T_MultipliesTheTextbookGainByTheDelayPhase()
    {
        // −G·(RL ∥ Z0) = −0.25 (CurrentSourceTests), now lagging by ωT. "100 ps" also proves the time
        // units parse on an instance line — before issue 55 "ps" was refused as an unknown unit.
        var s = S21(string.Format(VccsStage, "G=10 mS T=100 ps"));
        for (int f = 0; f < Freqs.Length; f++) AssertClose(-0.25 * Lag(Freqs[f], 100e-12), s[f]);
    }

    [Fact]
    public void Vccs_T_LeavesDcAlone()
    {
        var (lib, tb) = new CnlReader().Read(@"
Vdc:V1    nc 0     Vdc=2 V
VCCS:G1   n_out 0 nc 0   G=0.01 T=1 ns
R:RL      n_out 0  R=100 Ohm
");
        var nl = new Elaborator(lib).Elaborate(tb);
        var dc = NonlinearDcEngine.Run(nl);
        Assert.Equal(-2.0, dc.NodeVoltages[nl.Nodes.IndexOf("n_out") - 1], 6);
    }

    // The phase comes from T alone. A complex G was once read as zero — an open circuit, silently.
    [Theory]
    [InlineData("VCCS:G1   nout 0 nin 0   G=0.01*exp(j*0.3)", "VCCS")]
    [InlineData("VCVS:E1   nout 0 nin 0   E=2*j",             "VCVS")]
    public void ComplexGain_IsRefusedByName(string line, string type)
    {
        var (lib, tb) = new CnlReader().Read($@"
Port:P1   nin  0   Num=1  Z=50 Ohm
{line}
R:RL      nout 0   R=50 Ohm
Port:P2   nout 0   Num=2  Z=50 Ohm
");
        var ex = Assert.ThrowsAny<Exception>(
            () => SParameterEngine.Run(new Elaborator(lib).Elaborate(tb), Freqs));
        Assert.Contains(type, ex.Message, StringComparison.Ordinal);
        Assert.Contains("complex", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Vcvs_T_MultipliesTheGainByTheDelayPhase()
    {
        const string stage = @"
Port:P1   nin  0   Num=1  Z=50 Ohm
R:Rin     nin  0   R=50 Ohm
VCVS:E1   nsrc 0 nin 0   {0}
R:Rs      nsrc nout R=50 Ohm
Port:P2   nout 0   Num=2  Z=50 Ohm
";
        var flat    = S21(string.Format(stage, "E=2"));
        var delayed = S21(string.Format(stage, "E=2 T=0.25 ns"));
        for (int f = 0; f < Freqs.Length; f++)
        {
            Assert.True(flat[f].Magnitude > 0.5, $"the undelayed stage should pass signal, got {flat[f]}");
            AssertClose(flat[f] * Lag(Freqs[f], 0.25e-9), delayed[f]);
        }
    }

    [Fact]
    public void DelayBlock_IsAMatchedReciprocalLosslessDelay()
    {
        var (lib, tb) = new CnlReader().Read(@"
Port:P1     a 0   Num=1  Z=50 Ohm
Delay:DL1   a 0 b 0   T=1.1 ns
Port:P2     b 0   Num=2  Z=50 Ohm
");
        var ds = SParameterEngine.Run(new Elaborator(lib).Elaborate(tb), Freqs);
        for (int f = 0; f < Freqs.Length; f++)
        {
            var lag = Lag(Freqs[f], 1.1e-9);
            AssertClose(Complex.Zero, (Complex)ds["S"][f, 0, 0]);
            AssertClose(Complex.Zero, (Complex)ds["S"][f, 1, 1]);
            AssertClose(lag, (Complex)ds["S"][f, 1, 0]);
            AssertClose(lag, (Complex)ds["S"][f, 0, 1]);
        }
    }
}
