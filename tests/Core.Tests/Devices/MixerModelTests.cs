using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Expressions;
using Xunit;

namespace CircuitRF.Core.Tests.Devices;

/// <summary>
/// The ideal mixer's terminal law, its derived multiplier constant, and the four non-idealities.
///
/// <para>The gate that matters most here is <see cref="ConvGain_IsWhatComesOutOfTheStatedGain"/>:
/// the user types a conversion gain in dB and never sees the volt⁻¹ constant the model runs on, so
/// nothing but a test connects the two. It is checked against arithmetic done here from the stated
/// gain rather than against a number read out of the model, which is the whole point — a second
/// copy of the model's own algebra agreeing with itself would prove nothing.</para>
/// </summary>
public class MixerModelTests
{
    private static MixerModel Ideal(double convGainDb = -7.0, double ploDbm = 7.0,
                                    double zRf = 50, double zLo = 50, double zIf = 50)
        => new(convGainDb, ploDbm, zRf, zLo, zIf,
               isoLoRfDb: 200, isoLoIfDb: 200, isoRfIfDb: 200, iip3Dbm: 100);

    private static NonlinearResult At(MixerModel m, double vRf, double vLo, double vIf)
        => m.Evaluate(new PortVoltages([vRf, vLo, vIf]));

    private static double PeakVoltsFor(double dBm, double z)
        => Math.Sqrt(2.0 * 1e-3 * Math.Pow(10.0, dBm / 10.0) * z);

    // ── Shape ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ThreePorts_SixTerminals_Nonlinear()
    {
        var m = Ideal();
        Assert.Equal(3, m.PortCount);
        Assert.Equal(ModelKind.Nonlinear, m.Kind);
        Assert.Equal(["rf+", "rf-", "lo+", "lo-", "if+", "if-"], m.TerminalNames);
    }

    [Fact]
    public void StoresNoCharge_SoEveryQAndDcEntryIsZero()
    {
        var r = At(Ideal(), 0.3, 0.7, 0.1);
        for (int p = 0; p < 3; p++)
        {
            Assert.Equal(0.0, r.Q[p]);
            for (int q = 0; q < 3; q++) Assert.Equal(0.0, r.Dc[p, q]);
        }
    }

    // ── The terminal law ──────────────────────────────────────────────────────

    [Fact]
    public void EachPortIsItsOwnResistance_AndTheIfSitsBehindZif()
    {
        var m = Ideal(zRf: 75, zLo: 40, zIf: 200);
        double vRf = 0.11, vLo = 0.37, vIf = 0.05;
        var r = At(m, vRf, vLo, vIf);

        Assert.Equal(vRf / 75.0, r.I[0], 12);
        Assert.Equal(vLo / 40.0, r.I[1], 12);
        Assert.Equal((vIf - m.MultiplierK * vRf * vLo) / 200.0, r.I[2], 12);
    }

    [Fact]
    public void Unilateral_TheRfAndLoPortsNeverSeeTheIf()
    {
        var r = At(Ideal(), 0.2, 0.5, 3.0);   // a big IF voltage
        Assert.Equal(0.0, r.Dg[0, 2]);
        Assert.Equal(0.0, r.Dg[1, 2]);
        // and the LO port does not see the RF either
        Assert.Equal(0.0, r.Dg[1, 0]);
    }

    [Fact]
    public void IdealDefaults_LeaveEveryLeakageEntryEXACTLYZero()
    {
        // "Ideal" has to mean the entry is absent, not that it rounds to absent: StampLinearized
        // skips a zero admittance, so a 1e-10 here would put three phantom stamps into every
        // S-parameter matrix a mixer appears in.
        var r = At(Ideal(), 0.0, 0.0, 0.0);
        Assert.Equal(0.0, r.Dg[0, 1]);   // no LO out of the RF port
        Assert.Equal(0.0, r.Dg[2, 0]);   // no RF feedthrough at IF
        Assert.Equal(0.0, r.Dg[2, 1]);   // no LO feedthrough at IF
    }

    [Fact]
    public void WithNoLo_TheRfToIfSmallSignalGainIsZero_WhichIsWhatAnSparamRunSees()
    {
        // The linear engines linearise at the DC operating point, where v_lo = 0. This is the whole
        // of why an S-parameter sweep of a mixer reports no conversion — and why that is an answer
        // rather than an omission.
        var r = At(Ideal(), 0.0, 0.0, 0.0);
        Assert.Equal(0.0, r.Dg[2, 0]);

        // Give it a DC "LO" and the same device is a linear amplifier, which is what a multiplier
        // driven by a constant actually is.
        var biased = At(Ideal(), 0.0, 0.4, 0.0);
        Assert.True(Math.Abs(biased.Dg[2, 0]) > 1e-3);
    }

    // ── The derived multiplier constant ───────────────────────────────────────

    [Theory]
    [InlineData(-7.0,  7.0, 50, 50, 50)]
    [InlineData( 3.0,  0.0, 50, 50, 50)]
    [InlineData(-6.0, 10.0, 75, 50, 25)]
    public void ConvGain_IsWhatComesOutOfTheStatedGain(
        double convGainDb, double ploDbm, double zRf, double zLo, double zIf)
    {
        var m = Ideal(convGainDb, ploDbm, zRf, zLo, zIf);

        // Drive both ports at their matched levels and work the sideband amplitude out by hand:
        // a product of two cosines is half the sum plus half the difference, and the Zif/Zload
        // divider halves it again.
        const double pRfDbm = -20.0;
        double a = PeakVoltsFor(pRfDbm, zRf);
        double b = PeakVoltsFor(ploDbm, zLo);

        double vIfSidebandPeak = m.MultiplierK * a * b / 4.0;
        double pIf = vIfSidebandPeak * vIfSidebandPeak / (2.0 * zIf);
        double pRf = a * a / (2.0 * zRf);

        Assert.Equal(convGainDb, 10.0 * Math.Log10(pIf / pRf), 9);
    }

    [Fact]
    public void ConversionGainTracksLoAmplitude_ThreeDbForThreeDb()
    {
        // The honest consequence of a multiplier, and the reason the gain is quoted with the LO
        // drive it holds at. Doubling the LO voltage doubles the IF: +6 dB of LO power, +6 dB out.
        var m = Ideal();
        double vIfNominal = m.MultiplierK * 0.03 * 0.7079;
        double vIfDoubleLo = m.MultiplierK * 0.03 * (2 * 0.7079);
        Assert.Equal(6.0206, 20.0 * Math.Log10(vIfDoubleLo / vIfNominal), 3);
    }

    // ── Non-idealities ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(20.0)]
    [InlineData(35.0)]
    [InlineData(60.0)]
    public void LoToIfIsolation_IsAPowerRatioBetweenMatchedPorts(double isoDb)
    {
        var m = new MixerModel(-7, 7, 50, 50, 50,
                               isoLoRfDb: 200, isoLoIfDb: isoDb, isoRfIfDb: 200, iip3Dbm: 100);

        // With no RF at all, everything at the IF port is leakage. Drive the LO at 7 dBm and check
        // the power that lands across a matched IF load is exactly isoDb below it.
        double b = PeakVoltsFor(7.0, 50);
        var r = m.Evaluate(new PortVoltages([0.0, b, 0.0]));

        double vIfOpen = -r.I[2] * 50.0;      // Thevenin: i = (v − src)/Z with v = 0
        double vIfLoaded = vIfOpen / 2.0;     // into a matched 50 Ω
        double pIf = vIfLoaded * vIfLoaded / (2.0 * 50.0);
        double pLo = b * b / (2.0 * 50.0);

        Assert.Equal(-isoDb, 10.0 * Math.Log10(pIf / pLo), 9);
    }

    [Fact]
    public void RfToIfIsolation_IsTheOnlyIfPathLeftWhenTheLoIsOff()
    {
        var m = new MixerModel(-7, 7, 50, 50, 50,
                               isoLoRfDb: 200, isoLoIfDb: 200, isoRfIfDb: 30, iip3Dbm: 100);
        double a = PeakVoltsFor(-20.0, 50);
        var r = m.Evaluate(new PortVoltages([a, 0.0, 0.0]));

        double pIf = Math.Pow(-r.I[2] * 50.0 / 2.0, 2) / (2.0 * 50.0);
        double pRf = a * a / (2.0 * 50.0);
        Assert.Equal(-30.0, 10.0 * Math.Log10(pIf / pRf), 9);
    }

    [Fact]
    public void LoToRfIsolation_PutsTheLoOnTheRfPort_AndNothingTheOtherWay()
    {
        var m = new MixerModel(-7, 7, 50, 50, 50,
                               isoLoRfDb: 25, isoLoIfDb: 200, isoRfIfDb: 200, iip3Dbm: 100);
        var r = m.Evaluate(new PortVoltages([0.0, 0.5, 0.0]));
        Assert.True(Math.Abs(r.I[0]) > 0.0, "the LO must appear at the RF port");
        // …but the RF port is still invisible to the LO port: leakage is one-directional here, so
        // it cannot form a loop the solver has to break.
        Assert.Equal(0.0, r.Dg[1, 0]);
    }

    [Fact]
    public void Iip3_MatchesTheThirdOrderTermOfTheStatedIntercept()
    {
        // IIP3 = 2·Vsat falls straight out of matching tanh's own expansion x − x³/3 to
        // a₁x − a₃x³ in IIP3 = sqrt(4a₁/3a₃). Checked against the volts the stated dBm means at
        // the RF port, not against anything the model reports about itself.
        const double iip3Dbm = 12.0;
        var m = new MixerModel(-7, 7, 50, 50, 50, 200, 200, 200, iip3Dbm);
        Assert.Equal(PeakVoltsFor(iip3Dbm, 50) / 2.0, m.SaturationVolts, 12);
    }

    [Fact]
    public void Iip3_Compresses_AndTheDefaultDoesNot()
    {
        var soft  = new MixerModel(-7, 7, 50, 50, 50, 200, 200, 200, iip3Dbm: 0.0);
        var ideal = Ideal();
        Assert.Equal(0.0, ideal.SaturationVolts);

        // Well past the intercept the compressed device delivers visibly less IF than the ideal
        // one, and the ideal one stays exactly proportional to its RF drive.
        double small = Math.Abs(At(soft, 0.01, 0.7, 0).I[2]);
        double big   = Math.Abs(At(soft, 1.00, 0.7, 0).I[2]);
        Assert.True(big < 100 * small * 0.95, $"expected compression: {big} vs {100 * small}");

        double idealSmall = Math.Abs(At(ideal, 0.01, 0.7, 0).I[2]);
        double idealBig   = Math.Abs(At(ideal, 1.00, 0.7, 0).I[2]);
        Assert.Equal(100 * idealSmall, idealBig, 10);
    }

    // ── The Jacobian ──────────────────────────────────────────────────────────

    [Fact]
    public void Jacobian_MatchesFiniteDifferences_WithEveryNonIdealityOn()
    {
        var m = new MixerModel(-7, 7, 60, 45, 80,
                               isoLoRfDb: 22, isoLoIfDb: 18, isoRfIfDb: 31, iip3Dbm: 5.0);
        double[] v = [0.23, 0.61, -0.17];
        var r = m.Evaluate(new PortVoltages(v));

        const double h = 1e-7;
        for (int q = 0; q < 3; q++)
        {
            double[] up = (double[])v.Clone(); up[q] += h;
            double[] dn = (double[])v.Clone(); dn[q] -= h;
            var ru = m.Evaluate(new PortVoltages(up));
            var rd = m.Evaluate(new PortVoltages(dn));
            for (int p = 0; p < 3; p++)
                Assert.Equal((ru.I[p] - rd.I[p]) / (2 * h), r.Dg[p, q], 6);
        }
    }

    [Fact]
    public void GridEvaluate_AgreesWithTheScalarPath()
    {
        // The model opts into EvaluateInto, so the grid path is a different loop over the same
        // arithmetic — and it reuses its buffers, which is where an unwritten entry would hide.
        var m = new MixerModel(-7, 7, 50, 50, 50, 25, 30, 35, 10.0);
        double[] rf = [0.0, 0.10, -0.25, 0.40];
        double[] lo = [0.7, -0.30, 0.55, 0.05];
        double[] iv = [0.0, 0.02, -0.08, 0.11];

        var flat = new double[3 * rf.Length];
        for (int t = 0; t < rf.Length; t++)
        {
            flat[0 * rf.Length + t] = rf[t];
            flat[1 * rf.Length + t] = lo[t];
            flat[2 * rf.Length + t] = iv[t];
        }

        var into = new GridResult();
        m.EvaluateGrid(flat, ReadOnlySpan<double>.Empty, rf.Length, into);

        for (int t = 0; t < rf.Length; t++)
        {
            var scalar = m.Evaluate(new PortVoltages([rf[t], lo[t], iv[t]]));
            for (int p = 0; p < 3; p++)
            {
                Assert.Equal(scalar.I[p], into.I[into.PortBase(p) + t], 12);
                Assert.Equal(scalar.Q[p], into.Q[into.PortBase(p) + t], 12);
                for (int q = 0; q < 3; q++)
                    Assert.Equal(scalar.Dg[p, q], into.Dg[into.JacBase(p, q) + t], 12);
            }
        }
    }

    // ── The factory ───────────────────────────────────────────────────────────

    [Fact]
    public void Factory_BuildsItFromTheSchematicParameterNames()
    {
        var p = new Dictionary<string, Value>(StringComparer.Ordinal)
        {
            ["ConvGain"] = new Value(-3.0),
            ["Plo"]      = new Value(10.0),
            ["Zrf"]      = new Value(75.0),
            ["Zlo"]      = new Value(50.0),
            ["Zif"]      = new Value(25.0),
            ["IIP3"]     = new Value(6.0),
        };
        var m = Assert.IsType<MixerModel>(ComponentModelFactory.TryCreate("Mixer", p));
        Assert.Equal(3, m.PortCount);
        Assert.Equal(PeakVoltsFor(6.0, 75.0) / 2.0, m.SaturationVolts, 12);

        // The parameter names are the schematic's own, so a typo would silently fall back to a
        // default rather than fail — which is why the derived constant is checked, not just the type.
        double a = PeakVoltsFor(-20, 75), b = PeakVoltsFor(10, 50);
        double pIf = Math.Pow(m.MultiplierK * a * b / 4.0, 2) / (2.0 * 25.0);
        double pRf = a * a / (2.0 * 75.0);
        Assert.Equal(-3.0, 10.0 * Math.Log10(pIf / pRf), 9);
    }

    [Fact]
    public void Factory_UnnamedParametersFallBackToTheIdealDevice()
    {
        var m = Assert.IsType<MixerModel>(
            ComponentModelFactory.TryCreate("Mixer", new Dictionary<string, Value>()));
        Assert.Equal(0.0, m.SaturationVolts);
        var r = m.Evaluate(new PortVoltages([0, 0, 0]));
        Assert.Equal(0.0, r.Dg[2, 1]);
    }
}
