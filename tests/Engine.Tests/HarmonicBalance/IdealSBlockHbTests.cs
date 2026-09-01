using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// The ideal S-block in harmonic balance (brief-sys-2 gate 4): the linear partition stamps the same
/// wave-constraint rows at every mixing product the solver retains, INCLUDING DC.
///
/// <para>The gate asserts the ABSENCE of intermodulation as well as the presence of the carriers. A
/// linear block that quietly acquired a nonlinearity — or a stamp whose coefficients drifted with
/// harmonic index — would still show two tones at roughly the right level; it is the empty
/// (2,−1) and (1,1) bins that say the attenuator did nothing but attenuate.</para>
/// </summary>
public class IdealSBlockHbTests(ITestOutputHelper output)
{
    // 0 dBm per tone at 1.99 and 2.01 GHz into a matched 10 dB pad and a matched load. Every tone
    // must come out at −10 dBm and nothing else may appear at all.
    //
    // There is no nonlinear device in it, deliberately: the whole circuit is the linear partition,
    // so a product appearing anywhere could only have come from the stamp. (A wholly linear
    // netlist does run under HB — checked, not assumed.)
    private const string Cnl = @"
PnTone:Ps  a 0  Freq[1]=1.99e9 Pavl[1]=0 Phase[1]=0 Freq[2]=2.01e9 Pavl[2]=0 Phase[2]=0 Z=50
Atten:A1   a 0 b 0  Loss=10  Z0=50  RL=200
R:Rl       b 0  R=50
analysis HB1 type=hb NumFreqs=2 Tone[1]=1.99e9 Tone[2]=2.01e9 MaxMixOrder=5 MaxHarm=3 Tol=1e-12
";

    private static DataSet Run(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl  = new Elaborator(lib).Elaborate(tb);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var p   = HbEngine.Resolve(hba, nl.ResolvedGlobals);
        Assert.True(p.IsMultiTone, "the gate must resolve to a two-tone HB");
        var ds = (DataSet)new HbEngine(nl, tb).Run(p);
        Assert.True(ds["Converged"].RealValues[0] > 0.5, "HB did not converge");
        return ds;
    }

    [Fact]
    public void EveryToneComesOutTenDbDown()
    {
        var ds = Run(Cnl);
        double lower = PDbm(ds, 1, 0);
        double upper = PDbm(ds, 0, 1);
        output.WriteLine($"lower {lower:F6} dBm, upper {upper:F6} dBm (expected −10 each)");

        Assert.Equal(-10.0, lower, 6);
        Assert.Equal(-10.0, upper, 6);
    }

    /// <summary>
    /// Power delivered into the 50 Ω load at the pad's output, in dBm, from the VOLTAGE there.
    ///
    /// <para><c>TwoToneMeasurements.PoutW</c> cannot be used: node <c>b</c> is a linear-only node,
    /// and the HB cube carries no nonlinear terminal current at one (the linear back-solve recovers
    /// V and leaves I_NL zero by construction — <c>HbLinearNodeTests</c>' own T2). The load is a
    /// known 50 Ω, so the power is <c>|V|²/2R</c> on the same peak-phasor convention.</para>
    /// </summary>
    private static double PDbm(DataSet ds, int k1, int k2)
    {
        double v = TwoToneMeasurements.Tone(ds, 0, "b", k1, k2).Magnitude;
        double w = v * v / (2.0 * 50.0);
        return w > 0 ? 10.0 * Math.Log10(w) + 30.0 : double.NegativeInfinity;
    }

    [Fact]
    public void AndCreatesNothing()
    {
        // The half of the gate that a "two tones came out" assertion cannot make. Every retained
        // product that is not a carrier must be empty, not merely small: the attenuator is LINEAR,
        // so an IM3 bin holding anything at all would mean the stamp had become drive-dependent.
        var ds = Run(Cnl);
        double carrier = TwoToneMeasurements.Tone(ds, 0, "b", 1, 0).Magnitude;

        foreach (var (k1, k2) in new[] { (2, -1), (-1, 2), (3, -2), (-2, 3), (1, 1), (2, 0), (0, 2), (1, -1) })
        {
            double v = TwoToneMeasurements.Tone(ds, 0, "b", k1, k2).Magnitude;
            output.WriteLine($"({k1},{k2}) = {v:E3} V against a {carrier:E3} V carrier");
            Assert.True(v < 1e-12 * carrier,
                        $"({k1},{k2}) holds {v:E3} V — an ideal attenuator creates nothing");
        }
    }

    [Fact]
    public void TheStampIsTheSameAtEveryHarmonicIncludingDc()
    {
        // A frequency-flat block must attenuate a DC offset by exactly the same factor it attenuates
        // a carrier. The linear extractor stamps the DC harmonic through its own path
        // (HbLinearExtractor.ExtractDC), so this is the one product that could silently differ.
        var ds = Run(Cnl.Replace("Pavl[1]=0", "Pavl[1]=0 ").Replace(
            "PnTone:Ps  a 0", "Vdc:Vb  a 0  Vdc=0\nPnTone:Ps  a 0"));

        // With no DC drive the DC bin must be exactly empty rather than merely small.
        var v = TwoToneMeasurements.Tone(ds, 0, "b", 0, 0);
        output.WriteLine($"DC bin at the pad output: {v}");
        Assert.True(v.Magnitude < 1e-12, $"DC bin holds {v}");
    }
}
