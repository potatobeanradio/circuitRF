using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using RfCore.Data;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Linear;

/// <summary>
/// Brief NZ-3: <c>NF=</c> on the system amplifier, and the other system blocks thermal at their temperature. References are
/// closed forms: the stated NF, the Friis cascade, the noise-parameter curve, and Bosma's <c>Cs = kT(I − S·Sᴴ)</c>.
/// </summary>
public sealed class SystemBlockNoiseTests(ITestOutputHelper output)
{
    private const double T0 = NoiseMetrics.T0;

    private static string N(double x) => x.ToString("R", CultureInfo.InvariantCulture);

    private static (DataSet Ds, ElaboratedNetlist Nl, double[] Freqs) Run(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl    = new Elaborator(lib).Elaborate(tb);
        var spa   = tb.Analyses.OfType<SParameterAnalysis>().First();
        var freqs = spa.Expand(nl.ResolvedGlobals);
        return (SParameterEngine.Run(nl, freqs, AnalysisSettings.Default.WithNoiseOf(spa)), nl, freqs);
    }

    private const string Sweep = "analysis SP1 type=sparam start=1 stop=3 npts=3 Unit=GHz Noise=yes";

    private static double F(double db) => Math.Pow(10, db / 10);

    /// <summary>Gate 1: a matched amplifier with NF=3 returns NF = 3 dB, NFmin = 3 dB and Sopt = 0.</summary>
    [Fact]
    public void Amplifier_DatasheetNfComesBack()
    {
        var r = Run($"""
            Port:P1  a 0  Num=1 Z=50 Ohm
            Port:P2  b 0  Num=2 Z=50 Ohm
            Amp:A1  a 0 b 0  Gain=20 IP3=200 Zin=50 Zout=50 NF=3
            {Sweep}
            """);
        for (int fi = 0; fi < r.Freqs.Length; fi++)
        {
            Assert.Equal(3.0, r.Ds["NF"].RealValues[fi], 1e-9);
            Assert.Equal(3.0, r.Ds["NFmin"].RealValues[fi], 1e-9);
            Assert.True(((Complex)r.Ds["Sopt"][fi]).Magnitude < 1e-9, $"Sopt {r.Ds["Sopt"][fi]}");
        }
    }

    /// <summary>Gate 2: amplifier, attenuator at T0, amplifier, all matched — the Friis closed form.</summary>
    [Fact]
    public void Cascade_MeetsFriis()
    {
        const double G1 = 15, Nf1 = 1.5, L = 3, G2 = 20, Nf2 = 4;
        var r = Run($"""
            temp = 16.85
            Port:P1  a 0  Num=1 Z=50 Ohm
            Port:P2  d 0  Num=2 Z=50 Ohm
            Amp:A1   a 0 b 0  Gain={N(G1)} IP3=200 NF={N(Nf1)}
            Atten:T1 b 0 c 0  Loss={N(L)} Z0=50
            Amp:A2   c 0 d 0  Gain={N(G2)} IP3=200 NF={N(Nf2)}
            {Sweep}
            """);
        double t = (16.85 + 273.15) / T0, lossLin = F(L);
        double expected = F(Nf1) + (lossLin - 1) * t / F(G1) + (F(Nf2) - 1) * lossLin / F(G1);
        output.WriteLine($"Friis F = {expected:R}, run {r.Ds["Fn"].RealValues[0]:R}");
        Assert.All(r.Ds["Fn"].RealValues.ToArray(), f => Assert.Equal(expected, f, 1e-9));
    }

    /// <summary>Gate 3: an amplifier presenting 25 Ω, driven from 50 Ω, follows the noise-parameter curve: with
    /// Fmin = F, Zopt = 25 Ω and Rn = (F − 1)·25/4, F(50 Ω) = F + (F − 1)/8.</summary>
    [Fact]
    public void Mismatch_FollowsTheNoiseParameterCurve()
    {
        const double Nf = 2;
        var r = Run($"""
            Port:P1  a 0  Num=1 Z=50 Ohm
            Port:P2  b 0  Num=2 Z=50 Ohm
            Amp:A1  a 0 b 0  Gain=20 IP3=200 Zin=25 Ohm Zout=50 Ohm NF={N(Nf)}
            {Sweep}
            """);
        double f = F(Nf);
        for (int fi = 0; fi < r.Freqs.Length; fi++)
        {
            Assert.Equal(f + (f - 1) / 8, r.Ds["Fn"].RealValues[fi], 1e-9);
            Assert.Equal(Nf, r.Ds["NFmin"].RealValues[fi], 1e-9);
            Assert.Equal((f - 1) * 25 / 4, r.Ds["Rn"].RealValues[fi], 1e-9);
            Assert.True(((Complex)r.Ds["Zopt"][fi] - 25).Magnitude < 1e-7, $"Zopt {r.Ds["Zopt"][fi]}");
        }
    }

    /// <summary>Gate 4: NFopt, Gopt and Rn come back as NFmin, Sopt and Rn. With NF stated instead of Rn, the derived Rn
    /// gives exactly NF at Zs = Zin.</summary>
    [Fact]
    public void FourParameterForm_RoundTrips()
    {
        var gopt = new Complex(0.2, 0.25);
        var r = Run($"""
            Port:P1  a 0  Num=1 Z=50 Ohm
            Port:P2  b 0  Num=2 Z=50 Ohm
            Amp:A1  a 0 b 0  Gain=18 IP3=200 NFopt=1.2 Rn=8 Ohm Gopt=0.2+j0.25
            {Sweep}
            """);
        for (int fi = 0; fi < r.Freqs.Length; fi++)
        {
            Assert.Equal(1.2, r.Ds["NFmin"].RealValues[fi], 1e-9);
            Assert.Equal(8.0, r.Ds["Rn"].RealValues[fi], 1e-9);
            Assert.True(((Complex)r.Ds["Sopt"][fi] - gopt).Magnitude < 1e-9, $"Sopt {r.Ds["Sopt"][fi]}");
        }

        var d = Run($"""
            Port:P1  a 0  Num=1 Z=50 Ohm
            Port:P2  b 0  Num=2 Z=50 Ohm
            Amp:A1  a 0 b 0  Gain=18 IP3=200 NF=1.6 NFopt=1.2 Gopt=0.2+j0.25
            {Sweep}
            """);
        Assert.All(d.Ds["NF"].RealValues.ToArray(), nf => Assert.Equal(1.6, nf, 1e-9));
        Assert.All(d.Ds["NFmin"].RealValues.ToArray(), nf => Assert.Equal(1.2, nf, 1e-9));
    }

    /// <summary>§3: every passive system block — a PIM-carrying one included — is thermal at its Temp=:
    /// Cs = kT(I − S·Sᴴ) at its ports, to 1e-9 of kT. The parameter sets are passive as a matrix; the in-phase sums of a
    /// return loss and an isolation can make a set that is not, and that one is named instead (the test below).</summary>
    [Theory]
    [InlineData(2, "Atten:X1 n1 0 n2 0  Loss=6 Z0=50 RL=20")]
    [InlineData(2, "Atten:X1 n1 0 n2 0  Loss=3 Z0=50 RL=200 PIM=-100 PIMPc=43")]
    [InlineData(2, "Filter:X1 n1 0 n2 0  Response=Chebyshev Form=Bandpass Order=3 F1=1.5 GHz F2=2.5 GHz Ripple=0.1 IL=1")]
    [InlineData(4, "Coupler:X1 n1 0 n2 0 n3 0 n4 0  Coupling=10 Phase=90 deg Directivity=25 IL=0.5 RL=200 Z0=50")]
    [InlineData(3, "Switch:X1 n1 0 n2 0 n3 0  Throws=2 State=1 IL=1 Isolation=25 RL=25 OffState=Absorptive Z0=50")]
    [InlineData(3, "Circulator:X1 n1 0 n2 0 n3 0  Direction=CW IL=0.4 Isolation=30 RL=200 Z0=50")]
    [InlineData(3, "Circulator:X1 n1 0 n2 0 n3 0  Direction=CW IL=0.4 Isolation=30 RL=200 Z0=50 PIM=-90 PIMPc=43")]
    [InlineData(3, "Duplexer:X1 n1 0 n2 0 n3 0  Zant=50 Ohm TxF1=1 GHz TxF2=1.8 GHz RxF1=2.2 GHz RxF2=3 GHz TxIL=0.5 RxIL=0.5")]
    [InlineData(3, "Balun:X1 n1 0 n2 0 n3 0  Zunb=50 Zbal=50 IL=1 AmpImb=0 PhaseImb=0")]
    public void PassiveBlock_IsThermalAtItsTemp(int ports, string block)
    {
        const double Kelvin = 400;
        var lines = Enumerable.Range(1, ports).Select(k => $"Port:P{k}  n{k} 0  Num={k} Z=50 Ohm")
            .Append($"{block}  Temp={N(Kelvin - 273.15)}").Append(Sweep);
        var r = Run(string.Join('\n', lines));
        Assert.Contains(SParameterNoise.Table(r.Nl, r.Freqs), row => row.Label == "X1" && row.Kind == NoiseModelKind.Thermal);

        double kt = NoiseMetrics.Boltzmann * Kelvin, worst = 0;
        var s = r.Ds["S"]; var cs = r.Ds["Cs"];
        for (int fi = 0; fi < r.Freqs.Length; fi++)
            for (int i = 0; i < ports; i++)
                for (int j = 0; j < ports; j++)
                {
                    Complex ssh = 0;
                    for (int k = 0; k < ports; k++) ssh += (Complex)s[fi, i, k] * Complex.Conjugate((Complex)s[fi, j, k]);
                    Complex expected = kt * ((i == j ? 1 : 0) - ssh);
                    worst = Math.Max(worst, ((Complex)cs[fi, i, j] - expected).Magnitude / kt);
                }
        output.WriteLine($"{block}: worst |Cs − kT(I − SSᴴ)|/kT = {worst:E2}");
        Assert.True(worst < 1e-9, $"{worst:E2}");
    }

    /// <summary>§3: the active ones are named noiseless, each with its reason — an amplifier with no NF, a mixer (no LO in
    /// an S-parameter run, its NF kept for the nonlinear analyses), and a block declared passive whose S is not.</summary>
    [Theory]
    [InlineData("Amp:X1 n1 0 n2 0  Gain=20 IP3=200", "no NF=")]
    [InlineData("Mixer:X1 n1 0 lo 0 n2 0  NF=8 NFType=dsb", "NF=8 dB (DSB)")]
    [InlineData("Switch:X1 n1 0 n2 0 n3 0  Throws=2 State=1 IL=0.5 Isolation=28 RL=18 OffState=Absorptive Z0=50", "σ_max")]
    public void ActiveBlock_IsNamedNoiseless(string block, string reason)
    {
        var (lib, tb) = new CnlReader().Read($"""
            Port:P1  n1 0  Num=1 Z=50 Ohm
            Port:P2  n2 0  Num=2 Z=50 Ohm
            {block}
            {Sweep}
            """);
        var nl  = new Elaborator(lib).Elaborate(tb);
        var row = SParameterNoise.Table(nl, [1e9]).Single(x => x.Label == "X1");
        output.WriteLine(row.Reason);
        Assert.Equal(NoiseModelKind.Noiseless, row.Kind);
        Assert.Contains(reason, row.Reason);
        Assert.False(row.ByDefinition);
    }
}
