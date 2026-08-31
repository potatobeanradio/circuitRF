using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// The ideal mixer in the analysis it exists for: a two-tone harmonic balance with the RF on one
/// tone and the LO on the other, reading the converted power at the IF port.
///
/// <para>Every expectation here is arithmetic done from the STATED conversion gain — "−20 dBm in
/// at −7 dB of gain is −27 dBm out" — rather than from anything the model reports about itself.
/// The user types a gain in dB and the device runs on a volt⁻¹ constant derived from it through
/// three port impedances and two factors of two; this file is what connects the two ends of that,
/// end to end, through the real solver.</para>
/// </summary>
public class MixerHbTests(ITestOutputHelper output)
{
    // RF 2 GHz at −20 dBm, LO 1.8 GHz at +7 dBm — the drive the mixer's gain is stated at. IF
    // products land at 200 MHz (1,−1) and 3.8 GHz (1,1).
    private const string Cnl = @"
P1Tone:Prf   rf 0   Pavl=-20  Z=50  Freq=2.0e9
P1Tone:Plo   lo 0   Pavl=7    Z=50  Freq=1.8e9
Mixer:X1     rf 0  lo 0  if 0   ConvGain=-7  Plo=7
R:Rl         if 0   R=50
analysis HB1 type=hb NumFreqs=2 Tone[1]=2.0e9 Tone[2]=1.8e9 MaxMixOrder=5 MaxHarm=3 Tol=1e-10
";

    private static DataSet Run(string cnl)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mixer_{Guid.NewGuid():N}.cnl");
        File.WriteAllText(path, cnl);
        try
        {
            var (lib, tb) = CnlReader.ReadFile(path);
            var nl  = new Elaborator(lib).Elaborate(tb);
            var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
            var p   = HbEngine.Resolve(hba, nl.ResolvedGlobals);
            Assert.True(p.IsMultiTone, "the mixer gate must resolve to a two-tone HB");
            var ds = (DataSet)new HbEngine(nl, tb).Run(p);
            Assert.True(ds["Converged"].RealValues[0] > 0.5, "HB did not converge");
            return ds;
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void DownconvertedIf_IsTheRfDriveMinusTheStatedConversionGain()
    {
        var ds = Run(Cnl);
        double ifDbm = TwoToneMeasurements.PoutDbm(ds, 0, "if", 1, -1);
        output.WriteLine($"IF at f_rf − f_lo = {ifDbm:F4} dBm (expected −27)");
        Assert.Equal(-27.0, ifDbm, 3);
    }

    [Fact]
    public void BothSidebandsComeOutAtTheSameLevel()
    {
        // A product of two cosines is half the sum plus half the difference. The mixer does not
        // choose one, and a user who expects only the downconverted product needs to see that
        // written down somewhere that fails when it stops being true.
        var ds = Run(Cnl);
        double lower = TwoToneMeasurements.PoutDbm(ds, 0, "if", 1, -1);
        double upper = TwoToneMeasurements.PoutDbm(ds, 0, "if", 1,  1);
        output.WriteLine($"lower {lower:F4} dBm, upper {upper:F4} dBm");
        Assert.Equal(lower, upper, 6);
        Assert.Equal(-27.0, upper, 3);
    }

    [Fact]
    public void ConversionGain_DoesNotDependOnRfDrive()
    {
        // What makes it a gain: the IF tracks the RF one-for-one, because the law is linear in the
        // RF voltage. This is also the guard on the IIP3 default — a limiter left accidentally on
        // would show up here as compression at the strong drive.
        double IfAt(double pRfDbm) => TwoToneMeasurements.PoutDbm(
            Run(Cnl.Replace("Pavl=-20", $"Pavl={pRfDbm}")), 0, "if", 1, -1);

        Assert.Equal(-37.0, IfAt(-30), 3);
        Assert.Equal(-27.0, IfAt(-20), 3);
        Assert.Equal(-17.0, IfAt(-10), 3);
    }

    [Fact]
    public void ConversionGain_TracksLoDrive_WhichIsWhatAMultiplierDoes()
    {
        // The honest consequence of the mixing law, and the reason ConvGain is quoted together with
        // the LO drive it holds at. +3 dB of LO is +3 dB of conversion — a real switching mixer
        // would barely move, and the documentation says so.
        var hot  = Run(Cnl.Replace("Pavl=7 ", "Pavl=10 "));
        var cold = Run(Cnl.Replace("Pavl=7 ", "Pavl=4 "));

        Assert.Equal(-24.0, TwoToneMeasurements.PoutDbm(hot,  0, "if", 1, -1), 3);
        Assert.Equal(-30.0, TwoToneMeasurements.PoutDbm(cold, 0, "if", 1, -1), 3);
    }

    [Fact]
    public void PortImpedance_IsRealAndMovesTheAnswer()
    {
        // The IF port is a Thevenin source behind Zif, so mismatching it is a real, calculable
        // change rather than a cosmetic parameter: 50 Ω behind 50 Ω into a 50 Ω load halves the
        // open-circuit voltage; 200 Ω behind it into the same load delivers a fifth of it.
        // Pif ∝ (Zload/(Zif+Zload))², and the derived K itself carries a √(Zif/Zrf).
        var ds = Run(Cnl.Replace("ConvGain=-7", "ConvGain=-7  Zif=200"));
        double ifDbm = TwoToneMeasurements.PoutDbm(ds, 0, "if", 1, -1);

        // K ∝ √Zif and v_load = v_oc·50/250, against √50 and v_oc/2 at the matched default:
        //   ΔdB = 20·log10( (√(200/50)) · (50/250) / (1/2) ) = 20·log10(0.8)
        Assert.Equal(-27.0 + 20.0 * Math.Log10(0.8), ifDbm, 3);
    }

    [Fact]
    public void LoToIfIsolation_ShowsUpAtTheLoFrequency_AndOnlyThere()
    {
        // 25 dB of LO-IF isolation against a +7 dBm LO puts −18 dBm of LO feedthrough at the IF
        // port. The conversion product is untouched by it: leakage is a separate path, not a
        // perturbation of the mixing.
        var ds = Run(Cnl.Replace("Plo=7", "Plo=7  IsoLO_IF=25"));

        Assert.Equal(-18.0, TwoToneMeasurements.PoutDbm(ds, 0, "if", 0, 1), 2);
        Assert.Equal(-27.0, TwoToneMeasurements.PoutDbm(ds, 0, "if", 1, -1), 3);
    }

    [Fact]
    public void Iip3_Compresses_AndTheDefaultDoesNot()
    {
        // Driven 10 dB past the intercept the conversion is visibly down; the same drive with the
        // default IIP3 is exactly linear. The two runs differ in one parameter.
        string hard = Cnl.Replace("Pavl=-20", "Pavl=10");
        double linear     = TwoToneMeasurements.PoutDbm(Run(hard), 0, "if", 1, -1);
        double compressed = TwoToneMeasurements.PoutDbm(
            Run(hard.Replace("ConvGain=-7", "ConvGain=-7  IIP3=0")), 0, "if", 1, -1);

        output.WriteLine($"linear {linear:F3} dBm, compressed {compressed:F3} dBm");
        Assert.Equal(3.0, linear, 3);                       // −7 dB of gain on a +10 dBm drive
        Assert.True(compressed < linear - 1.0,
            $"expected more than 1 dB of compression, got {linear - compressed:F3} dB");
    }

    [Fact]
    public void WrongNetCount_IsRefusedByName()
    {
        // Five nets is an index-out-of-range from inside a Newton iteration if nothing catches it
        // first, at a point where nothing on the stack can say which instance was wrong.
        var bad = Cnl.Replace("Mixer:X1     rf 0  lo 0  if 0", "Mixer:X1     rf 0  lo 0  if");
        var ex = Assert.ThrowsAny<Exception>(() => Run(bad));
        Assert.Contains("X1", ex.Message);
        Assert.Contains("6 nets", ex.Message);
    }
}
