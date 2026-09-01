using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// The circulator, the coupler and the balun in harmonic balance (brief-sys-3): each passes a
/// two-tone signal at exactly the level its parameters state and <b>creates nothing</b>.
///
/// <para>The absence half is the half a "two tones came out" check cannot make. Every one of these
/// blocks is <c>ModelKind.Linear</c>, so an IM3 bin holding anything at all would mean the stamp had
/// become drive-dependent — and, for the coupler, that its complex S had been mishandled at some
/// mixing product. There is no nonlinear device anywhere in these netlists, deliberately: the whole
/// circuit IS the linear partition, so a product appearing could only have come from the
/// stamp.</para>
///
/// <para>This matters most for the coupler, which is the first block in the family whose S is
/// genuinely complex. The linear extractor stamps per harmonic ω = k·ω₀ including DC, and a −90°
/// that drifted with harmonic index would still show two tones at the right level.</para>
/// </summary>
public class SystemBlockHbTests(ITestOutputHelper output)
{
    private const string Source =
        "PnTone:Ps  a 0  Freq[1]=1.99e9 Pavl[1]=0 Phase[1]=0 Freq[2]=2.01e9 Pavl[2]=0 Phase[2]=0 Z=50";

    private const string Analysis =
        "analysis HB1 type=hb NumFreqs=2 Tone[1]=1.99e9 Tone[2]=2.01e9 MaxMixOrder=5 MaxHarm=3 Tol=1e-12";

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

    /// <summary>
    /// Power into the named 50 Ω load, in dBm, from the VOLTAGE there — the same reasoning
    /// <c>IdealSBlockHbTests</c> gives: these are linear-only nodes, so the HB cube carries no
    /// nonlinear terminal current at one, and <c>|V|²/2R</c> on the peak-phasor convention is what
    /// the load actually receives.
    /// </summary>
    private static double PDbm(DataSet ds, string net, int k1, int k2)
    {
        double v = TwoToneMeasurements.Tone(ds, 0, net, k1, k2).Magnitude;
        double w = v * v / (2.0 * 50.0);
        return w > 0 ? 10.0 * Math.Log10(w) + 30.0 : double.NegativeInfinity;
    }

    private void AssertCreatesNothing(DataSet ds, params string[] nets)
    {
        foreach (string net in nets)
        {
            double carrier = TwoToneMeasurements.Tone(ds, 0, net, 1, 0).Magnitude;
            foreach (var (k1, k2) in new[] { (2, -1), (-1, 2), (3, -2), (-2, 3),
                                             (1, 1), (2, 0), (0, 2), (1, -1), (0, 0) })
            {
                double v = TwoToneMeasurements.Tone(ds, 0, net, k1, k2).Magnitude;
                output.WriteLine($"{net} ({k1},{k2}) = {v:E3} V against a {carrier:E3} V carrier");
                Assert.True(v < 1e-9 * carrier,
                            $"{net} ({k1},{k2}) holds {v:E3} V — an ideal linear block creates nothing");
            }
        }
    }

    [Fact]
    public void ACirculatorCarriesBothTonesForward_AndNothingBackwards()
    {
        // Source into port 1, load on port 2 (the forward direction), load on port 3. Both tones
        // arrive at 0 dBm at port 2; port 3 sees nothing at all, which is the ideal isolation the
        // model refuses to stamp rather than stamping at 200 dB.
        var ds = Run($@"
{Source}
Circulator:C1  a 0  b 0  c 0  Direction=CW IL=0 Isolation=200 RL=200 Z0=50
R:R2  b 0  R=50
R:R3  c 0  R=50
{Analysis}
");
        output.WriteLine($"port 2: {PDbm(ds, "b", 1, 0):F6} / {PDbm(ds, "b", 0, 1):F6} dBm");
        Assert.Equal(0.0, PDbm(ds, "b", 1, 0), 6);
        Assert.Equal(0.0, PDbm(ds, "b", 0, 1), 6);

        // Relative to the forward carrier, not absolute: the ideal reverse entry is not stamped at
        // all, so what is left at port 3 is the HB solve's own convergence floor — 2.5e−11 of the
        // carrier, or 192 dB down. A 200 dB isolation stamped as 1e−10 would sit five orders above
        // it, which is what makes this a real assertion rather than a tolerance.
        double forward   = TwoToneMeasurements.Tone(ds, 0, "b", 1, 0).Magnitude;
        double backwards = TwoToneMeasurements.Tone(ds, 0, "c", 1, 0).Magnitude;
        output.WriteLine($"port 3 / port 2 = {backwards / forward:E3}");
        Assert.True(backwards < 1e-9 * forward,
                    $"port 3 holds {backwards:E3} V — a CW circulator sends nothing there");

        AssertCreatesNothing(ds, "b");
    }

    [Fact]
    public void ACouplerSplitsBothTonesAndCreatesNothing_EvenThoughItsSIsComplex()
    {
        // A 20 dB coupler: the through port keeps −0.0436 dB of it and the coupled port takes
        // −20 dB, and the split is set by Coupling ALONE, so both numbers come out of the same
        // parameter. The isolated port is exactly isolated.
        var ds = Run($@"
{Source}
Coupler:CPL1  a 0  b 0  c 0  d 0  Coupling=20 Phase=90 deg Directivity=200 IL=0 RL=200 Z0=50
R:R2  b 0  R=50
R:R3  c 0  R=50
R:R4  d 0  R=50
{Analysis}
");
        double cLin = Math.Pow(10.0, -20.0 / 20.0);
        double thruDb = 20.0 * Math.Log10(Math.Sqrt(1.0 - cLin * cLin));

        output.WriteLine($"thru {PDbm(ds, "b", 1, 0):F6} dBm (expected {thruDb:F6}), "
                       + $"cpl {PDbm(ds, "c", 1, 0):F6} dBm (expected −20)");
        Assert.Equal(thruDb, PDbm(ds, "b", 1, 0), 6);
        Assert.Equal(-20.0,  PDbm(ds, "c", 1, 0), 6);
        Assert.Equal(-20.0,  PDbm(ds, "c", 0, 1), 6);

        // Relative to the coupled port, for the same reason the circulator's reverse check is: with
        // the directivity off there is no entry, so what remains is the solve's own floor.
        double cpl = TwoToneMeasurements.Tone(ds, 0, "c", 1, 0).Magnitude;
        double iso = TwoToneMeasurements.Tone(ds, 0, "d", 1, 0).Magnitude;
        output.WriteLine($"iso / cpl = {iso / cpl:E3}");
        Assert.True(iso < 1e-9 * cpl, $"the isolated port holds {iso:E3} V");

        AssertCreatesNothing(ds, "b", "c");
    }

    [Fact]
    public void ABalunSplitsBothTonesInHalf_AndCreatesNothing()
    {
        // Each balanced port carries half the power — 3.0103 dB down — into its own 50 Ω load,
        // which also pins the common mode, so nothing here depends on the floating-node behaviour
        // the S-parameter file records.
        var ds = Run($@"
{Source}
Balun:B1  a 0  p 0  n 0  Zunb=50 Zbal=50 IL=0 AmpImb=0 PhaseImb=0 deg
R:Rp  p 0  R=50
R:Rn  n 0  R=50
{Analysis}
");
        double half = 20.0 * Math.Log10(1.0 / Math.Sqrt(2.0));
        output.WriteLine($"bal+ {PDbm(ds, "p", 1, 0):F6} dBm, bal− {PDbm(ds, "n", 1, 0):F6} dBm "
                       + $"(expected {half:F6} each)");

        Assert.Equal(half, PDbm(ds, "p", 1, 0), 6);
        Assert.Equal(half, PDbm(ds, "n", 1, 0), 6);

        // and they are still antiphase at BOTH tones, which is the property a level check cannot
        // see and the one the component exists to have.
        foreach (var (k1, k2) in new[] { (1, 0), (0, 1) })
        {
            var vp = TwoToneMeasurements.Tone(ds, 0, "p", k1, k2);
            var vn = TwoToneMeasurements.Tone(ds, 0, "n", k1, k2);
            Assert.True((vp + vn).Magnitude < 1e-9 * vp.Magnitude,
                        $"({k1},{k2}): bal+ {vp} and bal− {vn} should sum to zero");
        }

        AssertCreatesNothing(ds, "p", "n");
    }
}
