using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// The ideal amplifier in harmonic balance (brief-sys-5): the intercept the user typed is the
/// intercept that comes back out, the products ride the third power of drive, the compression point
/// falls where the limiter puts it, and an amplifier left at its default intercept creates NOTHING.
///
/// <para>Every expectation is arithmetic done in this file from the netlist line — the two-tone
/// third-order relation <c>P_IM3 = 3·Pin + Gain − 2·IIP3</c>, the cascade formula
/// <c>1/IIP3_sys = Σ_k (Π_{j&lt;k} G_j)/IIP3_k</c>, and tanh's own describing function for the
/// compression point. Nothing is read back out of a model.</para>
/// </summary>
public class AmplifierHbTests(ITestOutputHelper output)
{
    private static string N(double x) => x.ToString("R", CultureInfo.InvariantCulture);

    private const string TwoTone =
        "analysis HB1 type=hb NumFreqs=2 Tone[1]=1.99e9 Tone[2]=2.01e9 MaxMixOrder=7 MaxHarm=3 Tol=1e-12";

    private static string Carriers(double pcDbm) =>
        $"PnTone:Ps  n1 0  Freq[1]=1.99 GHz Pavl[1]={N(pcDbm)} Phase[1]=0 "
      + $"Freq[2]=2.01 GHz Pavl[2]={N(pcDbm)} Phase[2]=0 Z=50";

    private static DataSet RunTwoTone(string cnl, int expectedNonlinear = 1)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl  = new Elaborator(lib).Elaborate(tb);
        Assert.Equal(expectedNonlinear, nl.NonlinearComponents.Count);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var p   = HbEngine.Resolve(hba, nl.ResolvedGlobals);
        Assert.True(p.IsMultiTone, "an intercept gate must resolve to a two-tone HB");
        var ds = (DataSet)new HbEngine(nl, tb).Run(p);
        Assert.True(ds["Converged"].RealValues[0] > 0.5, "HB did not converge");
        return ds;
    }

    private static DataSet RunOneTone(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl  = new Elaborator(lib).Elaborate(tb);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var ds  = (DataSet)new HbEngine(nl, tb).Run(HbEngine.Resolve(hba, nl.ResolvedGlobals));
        Assert.True(ds["Converged"].RealValues[0] > 0.5, "HB did not converge");
        return ds;
    }

    /// <summary>
    /// Power into the 50 Ω load, in dBm, from the voltage there. Every node in these netlists is
    /// linear-only apart from the amplifier's own ports, so the cube carries no nonlinear terminal
    /// current at the load and |V|²/2R on the peak-phasor convention is what it receives — the same
    /// reasoning <c>IdealSBlockHbTests</c> sets out.
    /// </summary>
    private static double PDbm(Complex v)
    {
        double w = v.Magnitude * v.Magnitude / (2.0 * 50.0);
        return w > 0 ? 10.0 * Math.Log10(w) + 30.0 : double.NegativeInfinity;
    }

    /// <summary>
    /// A dB comparison with a stated tolerance IN dB. xUnit's decimal-places overload ROUNDS, so
    /// "1 decimal place" refuses a 0.058 dB error that is well inside the brief's own 0.1 dB — and
    /// 0.058 dB is not noise, it is tanh's fifth-order term, whose size at each backoff is measured
    /// in <c>ThirdOrderLimiter</c>'s remarks.
    /// </summary>
    private void NearDb(double expected, double actual, double tolDb, string what)
    {
        output.WriteLine($"{what}: expected {expected:F4} dB(m), got {actual:F4} — {actual - expected:+0.0000;-0.0000}");
        Assert.True(Math.Abs(actual - expected) < tolDb,
                    $"{what}: expected {expected:F4}, got {actual:F4} ({actual - expected:+0.0000;-0.0000} dB out)");
    }

    private static Complex V1(DataCube cube, string node, int k)
    {
        int i = Array.FindIndex(cube.Axes[0].Labels!, n => n.Equals(node, StringComparison.Ordinal));
        Assert.True(i >= 0, $"node '{node}' missing from the V cube's node axis");
        return (Complex)cube[i, k];
    }

    private static string Stage(double gainDb, double ip3Dbm, string ip3Ref) =>
        $"Amp:A1  n1 0 n2 0  Gain={N(gainDb)} IP3={N(ip3Dbm)} IP3Ref={ip3Ref} "
      + "Zin=50 Zout=50 RLin=200 RLout=200 S12=200";

    // ── The intercept is what was typed, both ways of typing it ───────────────

    /// <summary>
    /// The gate that matters. Two carriers 30 dB below the intercept — far enough down that tanh's
    /// own fifth-order term contributes only −0.06 dB, the figure <c>ThirdOrderLimiter</c>'s remarks
    /// tabulate against backoff — put IM3 where the third-order relation says.
    /// </summary>
    [Theory]
    [InlineData(20.0, 10.0, "Input")]
    [InlineData(20.0, 30.0, "Output")]     // the SAME amplifier: OIP3 = IIP3 + Gain
    [InlineData(10.0,  0.0, "Input")]
    [InlineData(30.0, 45.0, "Output")]     // IIP3 = 15
    public void TheThirdOrderProductsLandWhereTheStatedInterceptPutsThem(
        double gainDb, double ip3Dbm, string ip3Ref)
    {
        double iip3 = ip3Ref == "Output" ? ip3Dbm - gainDb : ip3Dbm;
        double pin  = iip3 - 30.0;                       // well below compression, as the brief asks

        var ds = RunTwoTone($@"
{Carriers(pin)}
{Stage(gainDb, ip3Dbm, ip3Ref)}
R:Rl  n2 0  R=50
{TwoTone}
");
        double fund  = PDbm(TwoToneMeasurements.Tone(ds, 0, "n2", 1, 0));
        double lower = PDbm(TwoToneMeasurements.Tone(ds, 0, "n2", 2, -1));
        double upper = PDbm(TwoToneMeasurements.Tone(ds, 0, "n2", -1, 2));

        // P_IM3 = 3·Pin + Gain − 2·IIP3, which is OIP3 = Pout + (Pout − P_IM3)/2 rearranged.
        double expectedFund = pin + gainDb;
        double expectedIm3  = 3.0 * pin + gainDb - 2.0 * iip3;

        output.WriteLine($"Gain {gainDb} dB, IP3 {ip3Dbm} dBm ({ip3Ref}) → IIP3 {iip3} dBm; "
                       + $"Pin {pin} dBm/carrier: fundamental {fund:F4} (expected {expectedFund:F4}), "
                       + $"IM3 {lower:F4}/{upper:F4} (expected {expectedIm3:F4})");

        NearDb(expectedFund, fund,  0.1, "fundamental");
        NearDb(expectedIm3,  lower, 0.1, "2f1-f2");
        NearDb(expectedIm3,  upper, 0.1, "2f2-f1");
        Assert.Equal(lower, upper, 3);      // a symmetric pair of tones makes a symmetric pair
    }

    /// <summary>
    /// The half of D5 that proves the selector is not decorative: the same physical amplifier stated
    /// input-referred and output-referred must produce the SAME product, not merely a plausible one
    /// each. Read together with the row pair above, this is the identity <c>OIP3 = IIP3 + Gain</c>
    /// measured rather than assumed.
    /// </summary>
    [Fact]
    public void StatingOneInterceptEitherWayGivesTheSameAmplifier()
    {
        const double gain = 20.0, iip3 = 10.0, pin = -20.0;

        string Netlist(double ip3, string reference) => $@"
{Carriers(pin)}
{Stage(gain, ip3, reference)}
R:Rl  n2 0  R=50
{TwoTone}
";
        double byInput  = PDbm(TwoToneMeasurements.Tone(RunTwoTone(Netlist(iip3,        "Input")),  0, "n2", 2, -1));
        double byOutput = PDbm(TwoToneMeasurements.Tone(RunTwoTone(Netlist(iip3 + gain, "Output")), 0, "n2", 2, -1));

        output.WriteLine($"IIP3 {iip3} dBm → {byInput:F6} dBm;  OIP3 {iip3 + gain} dBm → {byOutput:F6} dBm");
        Assert.Equal(byInput, byOutput, 9);
    }

    // ── The 3:1 slope ─────────────────────────────────────────────────────────

    [Fact]
    public void TheProductRidesTheThirdPowerOfDrive_OverAFifteenDbRange()
    {
        const double gain = 20.0, iip3 = 10.0;

        foreach (double drop in new[] { 0.0, 5.0, 10.0, 15.0 })
        {
            double pin = iip3 - 30.0 - drop;
            var ds = RunTwoTone($@"
{Carriers(pin)}
{Stage(gain, iip3, "Input")}
R:Rl  n2 0  R=50
{TwoTone}
");
            double got      = PDbm(TwoToneMeasurements.Tone(ds, 0, "n2", 2, -1));
            double expected = 3.0 * pin + gain - 2.0 * iip3;
            NearDb(expected, got, 0.1, $"IM3 at Pin {pin:F0} dBm/carrier");
        }
    }

    // ── Compression, and the constant it actually lands on ────────────────────

    /// <summary>
    /// <c>tanh</c>'s own describing function, evaluated here rather than quoted: the fundamental
    /// output of <c>ψ(A·cos θ) = Vsat·tanh((A/Vsat)·cos θ)</c>, divided by A, as a function of
    /// <c>u = A/Vsat</c>. The amplifier's input port is a plain resistance driven by a single tone
    /// through a linear source, so the input really is the pure sinusoid this integral assumes.
    /// </summary>
    private static double FundamentalGain(double u)
    {
        const int n = 200_000;
        double acc = 0.0;
        for (int i = 0; i < n; i++)
        {
            double th = (i + 0.5) * Math.PI / n;
            acc += Math.Tanh(u * Math.Cos(th)) * Math.Cos(th);
        }
        return (2.0 / Math.PI) * acc * (Math.PI / n) / u;
    }

    /// <summary>The drive, relative to the intercept (2·Vsat), at which the gain has fallen 1 dB.</summary>
    private static double OneDbBackoffFromIntercept()
    {
        double target = Math.Pow(10.0, -1.0 / 20.0);
        double lo = 1e-6, hi = 10.0;
        for (int i = 0; i < 100; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (FundamentalGain(mid) > target) lo = mid; else hi = mid;
        }
        return 20.0 * Math.Log10(0.5 * (lo + hi) / 2.0);
    }

    /// <summary>
    /// <b>brief-sys-5's 9.6 dB is the textbook CUBIC's number, not the tanh limiter's.</b> Measured
    /// here both ways: the limiter this family actually uses compresses 1 dB at
    /// <c>IIP3 − 8.9625 dB</c>, and at the cubic's <c>IIP3 − 9.6357 dB</c> it has compressed only
    /// 0.87 dB. The two differ by 0.67 dB, more than three times the brief's own 0.2 dB tolerance,
    /// so the gate below is written against the value tanh has rather than the value the brief
    /// attributes to it.
    /// </summary>
    [Fact]
    public void TheOneDbBackoffIsTanhsOwn_NotTheCubicsThatTheBriefQuotes()
    {
        double tanhBackoff = OneDbBackoffFromIntercept();
        double cubicBackoff = 20.0 * Math.Log10(Math.Sqrt(4.0 * (1.0 - Math.Pow(10.0, -1.0 / 20.0))) / 2.0);
        double atCubic = 20.0 * Math.Log10(FundamentalGain(2.0 * Math.Pow(10.0, cubicBackoff / 20.0)));

        output.WriteLine($"tanh: 1 dB down at IIP3 {tanhBackoff:F4} dB;  "
                       + $"cubic: IIP3 {cubicBackoff:F4} dB, where tanh is only {atCubic:F4} dB down");

        Assert.Equal(-8.9625, tanhBackoff,  3);
        Assert.Equal(-9.6357, cubicBackoff, 3);
        Assert.True(Math.Abs(atCubic + 1.0) > 0.1,
            "if tanh really compressed 1 dB at the cubic's backoff, the brief's 9.6 would be right");
    }

    [Theory]
    [InlineData(20.0, 10.0)]
    [InlineData(10.0, 20.0)]
    [InlineData(30.0,  0.0)]
    public void TheOneDbCompressionPointLandsWhereTheLimiterPutsIt(double gainDb, double iip3Dbm)
    {
        double pin = iip3Dbm + OneDbBackoffFromIntercept();

        var ds = RunOneTone($@"
P1Tone:Ps  n1 0  Pavl={N(pin)} Z=50 Freq=2e9 Phase=0
{Stage(gainDb, iip3Dbm, "Input")}
R:Rl  n2 0  R=50
analysis HB1 type=hb Tone=2e9 MaxHarm=7 Tol=1e-12
");
        double pout       = PDbm(V1(ds["V"], "n2", 1));
        double compression = (pin + gainDb) - pout;

        output.WriteLine($"Gain {gainDb} dB, IIP3 {iip3Dbm} dBm: driving at {pin:F4} dBm gives "
                       + $"{pout:F4} dBm out — {compression:F4} dB of compression");

        NearDb(1.0, compression, 0.2, "compression");   // the brief's own tolerance
    }

    // ── Ideal is exactly linear ───────────────────────────────────────────────

    /// <summary>
    /// At <c>IP3 = 200</c> the amplifier is a <see cref="CircuitRF.Core.ModelKind.Linear"/> block and
    /// does not enter the nonlinear partition at all, so a single tone driven hard produces no
    /// harmonics — their ABSENCE, which is what the brief asks for and what only this path can give.
    /// The netlist has no other nonlinear component in it, so anything at 2f or 3f could only have
    /// come from the amplifier.
    /// </summary>
    [Fact]
    public void AtItsDefaultIntercept_ADrivenAmplifierCreatesNothingAtAll()
    {
        var (lib, tb) = new CnlReader().Read($@"
P1Tone:Ps  n1 0  Pavl=20 Z=50 Freq=2e9 Phase=0
{Stage(20.0, 200.0, "Output")}
R:Rl  n2 0  R=50
analysis HB1 type=hb Tone=2e9 MaxHarm=5 Tol=1e-12
");
        var nl = new Elaborator(lib).Elaborate(tb);
        Assert.Empty(nl.NonlinearComponents);      // Linear on the INSTANCE, so nothing to solve

        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var ds  = (DataSet)new HbEngine(nl, tb).Run(HbEngine.Resolve(hba, nl.ResolvedGlobals));
        Assert.True(ds["Converged"].RealValues[0] > 0.5, "HB did not converge");

        var v = ds["V"];
        double fundamental = PDbm(V1(v, "n2", 1));
        output.WriteLine($"+20 dBm into a 20 dB amplifier → {fundamental:F6} dBm at the fundamental");
        NearDb(40.0, fundamental, 1e-6, "fundamental");

        for (int k = 2; k <= 5; k++)
        {
            Complex h = V1(v, "n2", k);
            output.WriteLine($"k={k}: {h}");
            Assert.Equal(0.0, h.Magnitude);       // absent, not small
        }
    }

    // ── Cascade sanity ────────────────────────────────────────────────────────

    /// <summary>
    /// A pad, this amplifier, and another pad. The net gain is the algebraic sum, and the system
    /// intercept follows the standard cascade formula — computed here, over all three stages, with
    /// the two pads carrying an infinite intercept of their own because an ideal attenuator has one.
    /// The input pad raising the system IIP3 by exactly its own loss is the part that a wrongly
    /// referred intercept would get wrong.
    /// </summary>
    [Theory]
    [InlineData(6.0,  3.0)]
    [InlineData(10.0, 0.5)]
    [InlineData(0.5, 10.0)]
    public void APadAnAmplifierAndAPad_CascadeTheWayTheFormulaSays(double lossInDb, double lossOutDb)
    {
        const double gain = 20.0, iip3 = 10.0;

        // 1/IIP3_sys = Σ_k (Π_{j<k} G_j)/IIP3_k, in linear power. The pads' terms are zero.
        double gPad     = Math.Pow(10.0, -lossInDb / 10.0);
        double iip3AmpW = 1e-3 * Math.Pow(10.0, iip3 / 10.0);
        double iip3SysW = 1.0 / (gPad / iip3AmpW);
        double iip3Sys  = 10.0 * Math.Log10(iip3SysW) + 30.0;
        double gainSys  = gain - lossInDb - lossOutDb;

        double pin = iip3Sys - 30.0;

        var ds = RunTwoTone($@"
{Carriers(pin)}
Atten:Ain   n1 0 n2 0  Loss={N(lossInDb)} Z0=50 RL=200
Amp:A1      n2 0 n3 0  Gain={N(gain)} IP3={N(iip3)} IP3Ref=Input Zin=50 Zout=50 RLin=200 RLout=200 S12=200
Atten:Aout  n3 0 n4 0  Loss={N(lossOutDb)} Z0=50 RL=200
R:Rl        n4 0  R=50
{TwoTone}
");
        double fund = PDbm(TwoToneMeasurements.Tone(ds, 0, "n4", 1, 0));
        double im3  = PDbm(TwoToneMeasurements.Tone(ds, 0, "n4", 2, -1));

        double expectedFund = pin + gainSys;
        double expectedIm3  = 3.0 * pin + gainSys - 2.0 * iip3Sys;

        output.WriteLine($"{lossInDb} dB pad + {gain} dB amp (IIP3 {iip3}) + {lossOutDb} dB pad → "
                       + $"system gain {gainSys} dB, system IIP3 {iip3Sys:F4} dBm; "
                       + $"fundamental {fund:F4} (expected {expectedFund:F4}), "
                       + $"IM3 {im3:F4} (expected {expectedIm3:F4})");

        Assert.Equal(iip3 + lossInDb, iip3Sys, 9);   // the formula's own answer, stated plainly
        NearDb(expectedFund, fund, 0.1, "system fundamental");
        NearDb(expectedIm3,  im3,  0.1, "system IM3");
    }
}
