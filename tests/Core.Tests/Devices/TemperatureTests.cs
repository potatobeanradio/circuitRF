using System;
using System.Collections.Generic;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using Xunit;

namespace CircuitRF.Core.Tests.Devices;

/// <summary>
/// Pins the temperature contract every component model shares. These are cheap and they exist
/// because a temperature mistake is the silent kind: it converges, the numbers are finite, and the
/// answer is wrong by an amount that looks like a modelling difference.
/// </summary>
public sealed class TemperatureTests
{
    /// <summary>
    /// The nominal is 26.85 °C precisely because it is 300 K EXACTLY, and "exactly" is a claim about
    /// IEEE-754 doubles rather than about arithmetic on paper. If this ever fails, the nominal has
    /// been "tidied" to 27 °C and every device that states no temperature has acquired a small
    /// residual ΔT it is not supposed to have.
    /// </summary>
    [Fact]
    public void NominalIsExactlyThreeHundredKelvin()
    {
        Assert.Equal(300.0, Temperature.NominalK);              // bit-exact, not approximate
        Assert.Equal(300.0, Temperature.ToKelvin(Temperature.NominalC));
    }

    [Fact]
    public void CelsiusKelvinRoundTrips()
    {
        foreach (double c in new[] { -273.15, -40.0, 0.0, 26.85, 27.0, 85.0, 125.0 })
            Assert.Equal(c, Temperature.ToCelsius(Temperature.ToKelvin(c)), 12);
    }

    /// <summary>
    /// A temperature DIFFERENCE is the same number on both scales — which is the whole reason every
    /// published relation is written in ΔT rather than in two absolute temperatures. Asserting it
    /// keeps anyone from "fixing" DeltaT to convert something.
    /// </summary>
    [Fact]
    public void DeltaTIsScaleFree()
    {
        const double tempC = 85.0, tnomC = 27.0;
        double inCelsius = Temperature.DeltaT(tempC, tnomC);
        double inKelvin  = Temperature.ToKelvin(tempC) - Temperature.ToKelvin(tnomC);

        Assert.Equal(58.0, inCelsius, 12);
        Assert.Equal(inCelsius, inKelvin, 12);
    }

    [Fact]
    public void ThermalVoltageAtNominalIsTheTextbookValue()
    {
        // kT/q at 300 K = 25.852 mV. Four significant figures is all this is worth asserting to.
        Assert.Equal(0.025852, Temperature.ThermalVoltage(Temperature.NominalK), 6);
    }

    /// <summary>
    /// The forwarding alias still resolves to the same constant. Both spellings are in use — the
    /// factory and the FET tests use the old name — and a divergence would put two different
    /// nominals in one build with nothing to signal it.
    /// </summary>
    [Fact]
    public void FetBaseAliasStillMatchesTheDefinition()
        => Assert.Equal(Temperature.NominalC, CircuitRF.Core.Devices.Fet.FetModelBase.NominalTemperatureC);

    // ── The threshold temperature coefficient's coordinate system ─────────────

    private const double TempC = 127.0, TnomC = 27.0;

    /// <summary>Enough shift to be unmistakable: −5 mV/°C over a 100 °C rise is half a volt.</summary>
    private const double Vtotc = -5e-3;

    /// <summary>
    /// <b>A threshold temperature coefficient is applied in the CARD's own coordinates and the
    /// channel sign is taken AFTERWARDS</b> — <c>sign·(Vto + Vtotc·ΔT)</c>, never
    /// <c>sign·Vto + Vtotc·ΔT</c>. A card states Vto and Vtotc together in one convention, so a
    /// p-channel part's positive threshold comes with the coefficient that moves that positive
    /// number; signing the threshold first leaves the coefficient pushing the other way.
    ///
    /// <para><b>The two orders are indistinguishable on an n-channel device</b>, where the sign is
    /// +1 and both spellings read alike — which is why this needs a p-channel case to say anything
    /// at all, and why the disagreement survived: three of these four families signed first while
    /// the fourth signed last, and every one of them carried a comment claiming it matched the
    /// others. On a p-channel part they drift in OPPOSITE directions, by twice the whole shift.</para>
    ///
    /// <para>Every threshold here is private, so it is recovered from BEHAVIOUR rather than read:
    /// each of these laws is off at or below its threshold and conducting past it, exactly, with no
    /// leakage floor — so bracketing the predicted value by a tenth of a millivolt pins it.</para>
    /// </summary>
    [Theory]
    [InlineData(+1.0)]      // n-channel: both orders agree, so this is the control
    [InlineData(-1.0)]      // p-channel: the two orders differ by 2·Vtotc·ΔT
    public void AThresholdTempCoefficient_IsAppliedInTheCardsOwnCoordinates_InEveryFamilyThatHasOne(
        double sign)
    {
        // The card's own threshold, in the card's own convention for this channel.
        double vto      = sign * 2.0;
        double expected = vto + Vtotc * Temperature.DeltaT(TempC, TnomC);

        foreach (var (name, channelCurrent) in Families())
        {
            // Just inside cutoff, and just past it. "Inside" and "past" are on opposite sides of the
            // threshold for the two channel types, which is the whole of what the sign means.
            const double Probe = 1e-4;
            double off = channelCurrent(sign, vto, expected - sign * Probe);
            double on  = channelCurrent(sign, vto, expected + sign * Probe);

            Assert.True(off == 0.0,
                $"{name} (sign {sign:+0;-0}): still conducting {off:E3} A a tenth of a millivolt "
                + $"inside cutoff, so its threshold is not {expected:F6} V");
            Assert.True(on != 0.0,
                $"{name} (sign {sign:+0;-0}): off a tenth of a millivolt past {expected:F6} V, so "
                + "its threshold has moved — the temperature shift was applied in the wrong "
                + "coordinates");
        }
    }

    /// <summary>
    /// The four families that carry a threshold temperature coefficient, each as a probe returning
    /// its CHANNEL current for a card-coordinate gate-source voltage. The MOS family is absent on
    /// purpose: it has no Vtotc at all, deriving its shift from Phi and the bandgap instead.
    /// </summary>
    private static IEnumerable<(string Name, Func<double, double, double, double> ChannelCurrent)> Families()
    {
        yield return ("MESFET (Curtice quadratic)", static (sign, vto, vgs) =>
        {
            var m = new CircuitRF.Core.Devices.Fet.CurticeQuadraticFetModel(
                vto: vto, beta: 0.02, tempC: TempC, tnomC: TnomC, vtotc: Vtotc,
                channel: sign > 0 ? CircuitRF.Core.Devices.Fet.FetModelBase.Channel.N : CircuitRF.Core.Devices.Fet.FetModelBase.Channel.P);
            return m.Evaluate(new PortVoltages([vgs, sign * 3.0])).I[1];
        });

        yield return ("JFET", static (sign, vto, vgs) =>
        {
            var m = new CircuitRF.Core.Devices.Jfet.JfetModel(
                sign > 0 ? CircuitRF.Core.Devices.Jfet.JfetModel.Polarity.NChannel : CircuitRF.Core.Devices.Jfet.JfetModel.Polarity.PChannel,
                vto: vto, beta: 1e-3, tempC: TempC, tnomC: TnomC, vtoTempCoefficient: Vtotc);
            double vd = sign * 3.0;
            return m.Evaluate(new PortVoltages([vd, vgs, vgs - vd])).I[0];
        });

        yield return ("vertical power MOSFET", static (sign, vto, vgs) =>
        {
            var m = new CircuitRF.Core.Devices.Mos.VdmosModel(
                sign > 0 ? CircuitRF.Core.Devices.Mos.VdmosModel.Channel.N : CircuitRF.Core.Devices.Mos.VdmosModel.Channel.P,
                vto: vto, kp: 5.0, tempC: TempC, tnomC: TnomC, vtoTempCoefficient: Vtotc);
            double vd = sign * 3.0;
            return m.Evaluate(new PortVoltages([vd, -vd, vgs, vgs - vd])).I[0];
        });

        yield return ("IGBT", static (sign, vto, vgs) =>
        {
            var m = new CircuitRF.Core.Devices.Igbt.IgbtModel(
                sign > 0 ? CircuitRF.Core.Devices.Igbt.IgbtModel.Polarity.NChannel : CircuitRF.Core.Devices.Igbt.IgbtModel.Polarity.PChannel,
                vto: vto, kp: 8.0, tempC: TempC, tnomC: TnomC, vtoTempCoefficient: Vtotc);
            // The channel's drain is the internal base node, so it is driven forward and the
            // collector sits above it — the orientation the law is published in.
            double vb = sign * 1.0, vc = sign * 3.0;
            return m.Evaluate(new PortVoltages([vb, vc - vb, vgs, vgs - vb, vc])).I[0];
        });
    }

    /// <summary>
    /// The load-bearing one: a DEFAULT-constructed diode is still evaluated at exactly the nominal
    /// after the constant moved. Vt is private, so it is recovered from behaviour instead of read —
    /// with Is = 1 and N = 1 the conduction current is exp(V/Vt) − 1, so Vt = V / ln(I + 1).
    ///
    /// This is what makes the extraction provably numeric-identity rather than probably harmless.
    /// </summary>
    [Fact]
    public void DefaultDiodeStillSitsAtTheNominalTemperature()
    {
        var d = new DiodeModel(saturationCurrent: 1.0, emissionCoefficient: 1.0);

        const double v = 0.01;                       // well inside the exponential region
        var r = d.Evaluate(new PortVoltages([v]));
        double recoveredVt = v / System.Math.Log(r.I[0] + 1.0);

        Assert.Equal(Temperature.ThermalVoltage(Temperature.NominalK), recoveredVt, 12);
    }
}
