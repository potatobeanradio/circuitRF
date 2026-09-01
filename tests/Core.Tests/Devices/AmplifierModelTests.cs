using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using Xunit;

namespace CircuitRF.Core.Tests.Devices;

/// <summary>
/// The ideal amplifier's S-matrix, its terminal law and its intercept arithmetic (brief-sys-5).
///
/// <para>Every expected number here is computed from the dB the user typed — the voltage gain
/// <c>G = 2·√(10^(Gain/10)·Zout/Zin)</c> and the limiter scale <c>Vsat = ½·√(2·P_iip3·Zin)</c> are
/// both written out in this file. Nothing is read back out of the model, because a second copy of
/// the model's own algebra agreeing with itself would prove nothing. The trip from those numbers
/// through a real solve is gated in <c>Engine.Tests</c>.</para>
/// </summary>
public class AmplifierModelTests
{
    private static AmplifierModel Amp(
        double gainDb = 20, double ip3Dbm = 200, Ip3Reference ip3Ref = Ip3Reference.Output,
        double zIn = 50, double zOut = 50,
        double rlIn = 200, double rlOut = 200, double s12 = 200)
        => new(gainDb, ip3Dbm, ip3Ref, zIn, zOut, rlIn, rlOut, s12);

    private static NonlinearResult At(AmplifierModel m, double vIn, double vOut)
        => m.Evaluate(new PortVoltages([vIn, vOut]));

    /// <summary>brief-sys-5's own voltage gain, written here rather than read from the model.</summary>
    private static double VoltageGain(double gainDb, double zIn, double zOut)
        => 2.0 * Math.Sqrt(Math.Pow(10.0, gainDb / 10.0) * zOut / zIn);

    private static double PeakVoltsFor(double dBm, double z)
        => Math.Sqrt(2.0 * 1e-3 * Math.Pow(10.0, dBm / 10.0) * z);

    // ── Shape ─────────────────────────────────────────────────────────────────

    [Fact]
    public void TwoPorts_FourTerminals_NamedBecauseTheyAreNotInterchangeable()
    {
        var m = Amp();
        Assert.Equal(2, m.PortCount);
        Assert.Equal(["in+", "in-", "out+", "out-"], m.TerminalNames);
        Assert.Equal(50.0, m.PortZOf(0));
        Assert.Equal(50.0, m.PortZOf(1));
    }

    /// <summary>
    /// The "ideal is exactly linear" half of the contract, at the model level: with <c>IP3</c> at its
    /// default the amplifier is a <see cref="ModelKind.Linear"/> block that takes the family's
    /// wave-constraint stamp, so it does not enter the HB nonlinear partition at all and CANNOT
    /// produce a harmonic. Absence, not smallness.
    /// </summary>
    [Fact]
    public void AtTheDefaultIntercept_ItIsLinear_AndTheLimiterIsNotThere()
    {
        var m = Amp(ip3Dbm: 200);
        Assert.Equal(ModelKind.Linear, m.Kind);
        Assert.Equal(0.0, m.SaturationVolts);
    }

    [Theory]
    [InlineData(40.0)]
    [InlineData(189.9)]
    public void AStatedIntercept_MakesItNonlinear(double ip3Dbm)
        => Assert.Equal(ModelKind.Nonlinear, Amp(ip3Dbm: ip3Dbm).Kind);

    /// <summary>
    /// The "off" test is applied to the number the user TYPED, before <see cref="Ip3Reference"/> is
    /// applied. 200 dBm output-referred on a 20 dB amplifier converts to 180 dBm input-referred,
    /// which is BELOW the 190 dBm threshold — so a threshold applied after the conversion would
    /// leave a freshly placed amplifier nonlinear, with a limiter no one asked for.
    /// </summary>
    [Fact]
    public void TheOffTestReadsWhatWasTyped_NotWhatItConvertsTo()
    {
        Assert.Equal(ModelKind.Linear, Amp(gainDb: 20, ip3Dbm: 200, ip3Ref: Ip3Reference.Output).Kind);
        Assert.Equal(ModelKind.Linear, Amp(gainDb: 20, ip3Dbm: 200, ip3Ref: Ip3Reference.Input).Kind);
    }

    // ── The S-matrix the parameters name ──────────────────────────────────────

    [Theory]
    [InlineData( 0.0)]
    [InlineData(10.0)]
    [InlineData(20.0)]
    [InlineData(35.0)]
    public void SIsTheMatrixItsParametersDescribe_Unilateral(double gainDb)
    {
        var s = Amp(gainDb: gainDb).SAt(2 * Math.PI * 1e9);

        Assert.Equal(Math.Pow(10.0, gainDb / 20.0), s[1, 0].Real, 12);
        Assert.Equal(0.0, s[1, 0].Imaginary);
        // Ideal means the entry is ABSENT: an exact zero, so the stamp skips it entirely.
        Assert.Equal(Complex.Zero, s[0, 0]);
        Assert.Equal(Complex.Zero, s[1, 1]);
        Assert.Equal(Complex.Zero, s[0, 1]);
    }

    [Fact]
    public void AStatedReturnLossAndIsolationComeBackAsThemselves()
    {
        var s = Amp(gainDb: 20, rlIn: 15, rlOut: 12, s12: 30).SAt(0.0);

        Assert.Equal(Math.Pow(10.0, -15.0 / 20.0), s[0, 0].Real, 12);
        Assert.Equal(Math.Pow(10.0, -12.0 / 20.0), s[1, 1].Real, 12);
        Assert.Equal(Math.Pow(10.0, -30.0 / 20.0), s[0, 1].Real, 12);
        Assert.Equal(Math.Pow(10.0,  20.0 / 20.0), s[1, 0].Real, 12);
    }

    /// <summary>
    /// A GAIN is what the part is for and never snaps; the three SUPPRESSIONS do. −200 dB of gain is
    /// a 200 dB pad and is stamped as one, which is the same rule the attenuator's Loss follows.
    /// </summary>
    [Fact]
    public void AGainNeverSnaps_ASuppressionDoes()
    {
        var s = Amp(gainDb: -200, rlIn: 200, rlOut: 200, s12: 200).SAt(0.0);
        Assert.Equal(Math.Pow(10.0, -200.0 / 20.0), s[1, 0].Real);   // exact, not "to N places"
        Assert.NotEqual(0.0, s[1, 0].Real);
        Assert.Equal(Complex.Zero, s[0, 0]);
    }

    // ── The terminal law: brief-sys-5's own two equations ─────────────────────

    /// <summary>
    /// Matched and unilateral, the model IS the brief's pair of equations — the input a resistance,
    /// the output a source of <c>G·ψ(v_in)</c> behind Zout — with <c>G</c> computed here from the dB
    /// that were typed.
    /// </summary>
    [Theory]
    [InlineData(20.0, 50.0,  50.0)]
    [InlineData(10.0, 75.0,  50.0)]
    [InlineData(30.0, 50.0, 200.0)]
    public void TheTerminalLawIsTheBriefsOwnTwoEquations(double gainDb, double zIn, double zOut)
    {
        var m = Amp(gainDb: gainDb, ip3Dbm: 200, zIn: zIn, zOut: zOut);
        double g = VoltageGain(gainDb, zIn, zOut);

        double vIn = 0.013, vOut = 0.31;
        var r = At(m, vIn, vOut);

        Assert.Equal(vIn / zIn, r.I[0], 12);
        Assert.Equal((vOut - g * vIn) / zOut, r.I[1], 12);

        // Unilateral: the input port never sees the output, at any drive.
        Assert.Equal(0.0, r.Dg[0, 1]);
        Assert.Equal(1.0 / zIn,  r.Dg[0, 0], 12);
        Assert.Equal(1.0 / zOut, r.Dg[1, 1], 12);
        Assert.Equal(-g / zOut,  r.Dg[1, 0], 12);
    }

    [Fact]
    public void StoresNoCharge_SoEveryQAndDcEntryIsZero()
    {
        var r = At(Amp(ip3Dbm: 30), 0.05, 0.9);
        for (int p = 0; p < 2; p++)
        {
            Assert.Equal(0.0, r.Q[p]);
            for (int q = 0; q < 2; q++) Assert.Equal(0.0, r.Dc[p, q]);
        }
    }

    /// <summary>
    /// A reverse path makes the input see the output — and it is the ONLY thing that does. The
    /// off-diagonal admittance a stated S12 produces is checked against the closed form for
    /// <c>Y = G·(I+S)⁻¹(I−S)·G</c>, written out here for the 2×2 case.
    /// </summary>
    [Fact]
    public void AReversePathIsWhatMakesTheInputSeeTheOutput()
    {
        const double gainDb = 20, s12Db = 25, zIn = 50, zOut = 50;
        var m = Amp(gainDb: gainDb, ip3Dbm: 40, s12: s12Db, zIn: zIn, zOut: zOut);

        double s21 = Math.Pow(10.0,  gainDb / 20.0);
        double s12 = Math.Pow(10.0, -s12Db  / 20.0);
        double det = 1.0 * 1.0 - s12 * s21;
        double y12 = -2.0 * s12 / det / Math.Sqrt(zIn * zOut);

        var r = At(m, 0.0, 0.4);
        Assert.Equal(y12, r.Dg[0, 1], 12);
        Assert.Equal(y12 * 0.4, r.I[0], 12);
    }

    /// <summary>
    /// The refusal, by name, at construction. <c>(1+S11)(1+S22) = S12·S21</c> is a unity-gain reverse
    /// loop — an oscillator — and it has no admittance matrix, so the compression cannot be written
    /// as the memoryless <c>i = f(v)</c> every nonlinearity here is. Where it is NOT raised matters
    /// as much: the LINEAR amplifier stamps the definition of S and has no such degeneracy.
    /// </summary>
    [Fact]
    public void AUnityGainReverseLoopIsRefusedByName_ButOnlyWhenItHasToBeWrittenAsAnAdmittance()
    {
        // S21 = 10^(20/20) = 10 exactly, S12 = 10^(−20/20) = 0.1 exactly, so S12·S21 = 1 = det(I+S).
        var ex = Assert.Throws<InvalidOperationException>(
            () => Amp(gainDb: 20, ip3Dbm: 40, s12: 20));
        Assert.Contains("oscillator", ex.Message);

        // The same numbers with the intercept left at its default construct and stamp perfectly well.
        var linear = Amp(gainDb: 20, ip3Dbm: 200, s12: 20);
        Assert.Equal(ModelKind.Linear, linear.Kind);
        Assert.Equal(0.1, linear.SAt(0.0)[0, 1].Real, 12);

        // And evaluating THAT one anyway — which no engine does, since a linear block is stamped —
        // says the same thing rather than returning a silently zeroed admittance.
        Assert.Throws<InvalidOperationException>(() => At(linear, 0.1, 0.1));
    }

    // ── The intercept ─────────────────────────────────────────────────────────

    /// <summary>
    /// <c>IIP3 = 2·Vsat</c> in volts falls straight out of matching tanh's own expansion
    /// <c>x − x³/3</c> to <c>a₁x − a₃x³</c> in <c>IIP3 = √(4a₁/3a₃)</c>. Checked against the volts
    /// the stated dBm means at the input port, which is the same arithmetic
    /// <c>MixerModelTests</c> does for the mixer — the two now share one implementation.
    /// </summary>
    [Theory]
    [InlineData(  0.0, 50.0)]
    [InlineData( 20.0, 50.0)]
    [InlineData( 40.0, 75.0)]
    [InlineData(-10.0, 25.0)]
    public void TheLimiterScaleIsHalfThePeakVoltsAtTheStatedInputIntercept(double iip3Dbm, double zIn)
        => Assert.Equal(PeakVoltsFor(iip3Dbm, zIn) / 2.0,
                        Amp(ip3Dbm: iip3Dbm, ip3Ref: Ip3Reference.Input, zIn: zIn).SaturationVolts, 15);

    /// <summary>
    /// D5, and the gate that proves the selector is not decorative: <c>OIP3 = IIP3 + Gain</c> is an
    /// identity, so the SAME amplifier stated either way must limit at exactly the same scale.
    /// </summary>
    [Theory]
    [InlineData(10.0, 30.0)]
    [InlineData(20.0, 40.0)]
    [InlineData(35.0,  5.0)]
    public void TheTwoWaysOfStatingOneInterceptAgree(double gainDb, double iip3Dbm)
    {
        var byInput  = Amp(gainDb: gainDb, ip3Dbm: iip3Dbm,           ip3Ref: Ip3Reference.Input);
        var byOutput = Amp(gainDb: gainDb, ip3Dbm: iip3Dbm + gainDb,  ip3Ref: Ip3Reference.Output);
        Assert.Equal(byInput.SaturationVolts, byOutput.SaturationVolts, 15);
        Assert.Equal(PeakVoltsFor(iip3Dbm, 50.0) / 2.0, byOutput.SaturationVolts, 15);
    }

    /// <summary>
    /// An intercept is referred to AVAILABLE input power, which is what a datasheet means by it. The
    /// port voltage at a given available power is <c>(1 + S11)</c> times its matched value, so the
    /// limiter scale carries that factor — and it is exactly 1 at the default return loss, where it
    /// reduces to brief-sys-5's own formula bit for bit.
    /// </summary>
    [Fact]
    public void TheInterceptIsReferredToAvailablePower_NotToThePortVoltage()
    {
        double matched = Amp(ip3Dbm: 20, ip3Ref: Ip3Reference.Input, rlIn: 200).SaturationVolts;
        Assert.Equal(PeakVoltsFor(20.0, 50.0) / 2.0, matched, 15);

        double rho = Math.Pow(10.0, -15.0 / 20.0);
        double mismatched = Amp(ip3Dbm: 20, ip3Ref: Ip3Reference.Input, rlIn: 15).SaturationVolts;
        Assert.Equal(matched * (1.0 + rho), mismatched, 15);
    }

    /// <summary>
    /// The limiter itself: <c>ψ(x) = Vsat·tanh(x/Vsat)</c> on the forward path only, computed here
    /// from the scale the stated intercept names rather than from anything the model reports.
    /// </summary>
    [Fact]
    public void TheForwardPathIsSoftLimited_AndTheInputPathIsNot()
    {
        const double gainDb = 20, iip3Dbm = 10;
        var m = Amp(gainDb: gainDb, ip3Dbm: iip3Dbm, ip3Ref: Ip3Reference.Input);

        double vsat = PeakVoltsFor(iip3Dbm, 50.0) / 2.0;
        double g    = VoltageGain(gainDb, 50.0, 50.0);

        foreach (double vIn in new[] { 0.001, 0.05, 0.2, 0.5, 2.0 })
        {
            var r = At(m, vIn, 0.0);
            Assert.Equal(vIn / 50.0, r.I[0], 12);                              // still a resistance
            Assert.Equal(-g * vsat * Math.Tanh(vIn / vsat) / 50.0, r.I[1], 12);
            Assert.Equal(-g * (1.0 - Math.Pow(Math.Tanh(vIn / vsat), 2)) / 50.0, r.Dg[1, 0], 12);
        }

        // Bounded, so Newton cannot walk off it: the output current saturates at G·Vsat/Zout.
        Assert.True(Math.Abs(At(m, 1e6, 0.0).I[1]) < g * vsat / 50.0 + 1e-9);
    }

    /// <summary>
    /// The small-signal slope through the limiter is exactly the linear one — <c>ψ'(0) = 1</c> — so
    /// an S-parameter run reports the gain that was typed however the intercept is set. This is the
    /// model-level half; <c>AmplifierSParamTests</c> holds the same thing through a real solve.
    /// </summary>
    [Theory]
    [InlineData(200.0)]
    [InlineData( 40.0)]
    [InlineData(  0.0)]
    public void AtZeroDrive_TheJacobianIsTheLinearAdmittance_WhateverTheIntercept(double ip3Dbm)
    {
        const double gainDb = 20;
        var r = At(Amp(gainDb: gainDb, ip3Dbm: ip3Dbm), 0.0, 0.0);
        Assert.Equal(1.0 / 50.0, r.Dg[0, 0], 12);
        Assert.Equal(-VoltageGain(gainDb, 50.0, 50.0) / 50.0, r.Dg[1, 0], 12);
    }

    /// <summary>
    /// The mixer and the amplifier now share one limiter and one intercept derivation
    /// (<c>ThirdOrderLimiter</c>), which brief-sys-5 asks to be gated rather than assumed: the same
    /// stated dBm at the same port impedance produces the same scale, BIT for bit, on both.
    /// </summary>
    [Theory]
    [InlineData( 10.0, 50.0)]
    [InlineData( 30.0, 75.0)]
    [InlineData(-5.0,  25.0)]
    public void TheMixerAndTheAmplifierLimitAtExactlyTheSameScale(double iip3Dbm, double z)
    {
        var mixer = new MixerModel(-7, 7, z, 50, 50, 200, 200, 200, iip3Dbm);
        var amp   = Amp(ip3Dbm: iip3Dbm, ip3Ref: Ip3Reference.Input, zIn: z);
        Assert.Equal(mixer.SaturationVolts, amp.SaturationVolts);   // exact, not to N places
    }
}
