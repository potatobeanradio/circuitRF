using System;
using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using Xunit;

namespace CircuitRF.Engine.Tests.Devices;

/// <summary>
/// Gate tests for the ferrite bead.
///
/// <para><b>The claim the whole component exists to make is that its LOSS is frequency-dependent</b>
/// — nothing at DC, a maximum at the parallel resonance, falling again above it. A series RLC cannot
/// do that, which is why importing a bead card as one was refused before this model existed. So the
/// tests are about the shape of Z(ω), measured through the S-parameter engine rather than by reading
/// the model's own arithmetic back: the oracle is the closed-form impedance, computed here
/// independently of the stamp.</para>
///
///   T1 — Z(ω) matches the closed form, at frequencies either side of resonance and at it.
///   T2 — at DC the bead is Rdc and nothing else, so it does not open a supply rail.
///   T3 — the resistive part PEAKS at the parallel resonance and falls above it. This is the whole
///        difference from an inductor and from a series RLC.
///   T4 — each of the three parallel elements is OFF at zero rather than shorting the tank.
///   T5 — a bead with no parameters at all is a wire, not a singular matrix.
/// </summary>
public class BeadStampTests
{
    /// <summary>
    /// The impedance a two-port S-parameter run reports for a series element, converted back:
    /// for a series Z between two 50 Ω terminations, S21 = 2·Z0/(2·Z0 + Z), so Z = 2·Z0·(1/S21 − 1).
    /// Going through the engine rather than calling <c>Stamp</c> directly is deliberate — it is the
    /// path a user's circuit takes, and it would catch a stamp that is self-consistent and wired to
    /// the wrong nodes.
    /// </summary>
    private static Complex SeriesImpedance(string beadLine, double freqHz)
    {
        string cnl = $"""
            Term:T1  n1 0  Num=1 Z=50
            {beadLine}
            Term:T2  n2 0  Num=2 Z=50
            """;
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var ds = SParameterEngine.Run(nl, [freqHz]);
        var s21 = (Complex)ds["S"][0, 1, 0];
        return 2.0 * 50.0 * (Complex.One / s21 - Complex.One);
    }

    /// <summary>The closed form, written out here so the test is not the model quoting itself.</summary>
    private static Complex Expected(double rdc, double l, double rp, double cp, double freqHz)
    {
        double w = 2 * Math.PI * freqHz;
        if (w == 0 || l <= 0) return new Complex(rdc, 0);
        Complex y = Complex.One / new Complex(0, w * l);
        if (rp > 0) y += new Complex(1.0 / rp, 0);
        if (cp > 0) y += new Complex(0, w * cp);
        return new Complex(rdc, 0) + Complex.One / y;
    }

    // A plausible signal-line bead: a few hundred ohms at a few hundred megahertz, peaking near
    // 350 MHz. Not any particular part — a shape, chosen so resonance sits inside the sweep.
    private const double Rdc = 0.05, L = 250e-9, Rp = 600.0, Cp = 0.8e-12;
    private const string Line = "Bead:FB1 n1 n2 Rdc=0.05 L=250 nH Rp=600 Cp=0.8 pF";

    // ── T1 ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1e6)]
    [InlineData(50e6)]
    [InlineData(200e6)]
    [InlineData(355.9e6)]     // near the parallel resonance
    [InlineData(800e6)]
    [InlineData(3e9)]
    public void T1_TheImpedanceMatchesTheClosedForm(double freqHz)
    {
        var z = SeriesImpedance(Line, freqHz);
        var e = Expected(Rdc, L, Rp, Cp, freqHz);
        Assert.Equal(e.Real, z.Real, Math.Abs(e.Real) * 1e-9 + 1e-9);
        Assert.Equal(e.Imaginary, z.Imaginary, Math.Abs(e.Imaginary) * 1e-9 + 1e-9);
    }

    // ── T2 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T2_AtDcTheBeadIsItsWindingResistance_AndDoesNotOpenTheRail()
    {
        // A bead in a supply rail must pass DC. The inductive branch shorts the tank out at ω = 0,
        // which is both the physics and what a DC operating point needs from this part — an open
        // here would leave every node past it unsolvable.
        const string cnl = """
            Vdc:V1   n1 0  Vdc=5
            Bead:FB1 n1 n2 Rdc=0.05 L=250 nH Rp=600 Cp=0.8 pF
            R:RL     n2 0  R=9.95
            """;
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var dc = NonlinearDcEngine.Run(nl);
        double vLoad = dc.NodeVoltages[nl.Nodes.GetOrAssign("n2") - 1];

        // 5 V across 0.05 + 9.95 Ω: the bead drops exactly its winding resistance's share.
        Assert.Equal(4.975, vLoad, 6);
    }

    // ── T3 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T3_TheLossPeaksAtResonance_WhichIsWhatABeadIsFor()
    {
        // Sweep the real part of Z. A bead's resistance is near zero at DC, rises to a maximum at
        // the parallel resonance, and falls again above it. An inductor's is flat at zero and a
        // series RLC's is flat at R — neither has a maximum anywhere, which is exactly why a bead
        // card could not be imported as one.
        double best = 0, bestF = 0;
        foreach (double f in new[] { 1e6, 10e6, 50e6, 100e6, 200e6, 300e6, 356e6, 450e6, 700e6, 1.5e9, 5e9 })
        {
            double r = SeriesImpedance(Line, f).Real;
            if (r > best) { best = r; bestF = f; }
        }

        // The peak is Rdc + Rp, by construction — at resonance the reactive branches cancel exactly.
        Assert.True(best > 0.9 * (Rdc + Rp), $"the peak resistance was {best:F1} Ω, expected near {Rdc + Rp:F1}");
        double f0 = 1.0 / (2 * Math.PI * Math.Sqrt(L * Cp));
        Assert.True(Math.Abs(bestF - f0) < 0.25 * f0, $"the peak sat at {bestF:E2} Hz, resonance is {f0:E2} Hz");

        // …and it really does come back down. This is the half a fixed RLC cannot reproduce.
        Assert.True(SeriesImpedance(Line, 5e9).Real < 0.25 * best, "the loss must fall again above resonance");
        Assert.True(SeriesImpedance(Line, 1e6).Real < 0.05 * best, "…and be negligible well below it");
    }

    // ── T4 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T4_EachParallelElement_IsOffAtZeroRatherThanShortingTheTank()
    {
        const double F = 300e6;
        double w = 2 * Math.PI * F;

        // Rp = 0 removes the LOSS branch. Reading a zero parallel resistance literally would short
        // the tank and leave a bead of 0.05 Ω, which is the opposite of what an omitted parameter
        // means — and would still simulate.
        var noRp = SeriesImpedance("Bead:FB1 n1 n2 Rdc=0.05 L=250 nH Cp=0.8 pF", F);
        Assert.Equal(Expected(Rdc, L, 0, Cp, F).Imaginary, noRp.Imaginary, Math.Abs(noRp.Imaginary) * 1e-9 + 1e-9);
        Assert.True(Math.Abs(noRp.Real) < 1e-6 + Rdc * 1.001, "with no Rp the impedance is purely reactive");

        // Cp = 0 removes the capacitive branch, so the reactance goes on rising — ωL, undamped.
        var noCp = SeriesImpedance("Bead:FB1 n1 n2 Rdc=0.05 L=250 nH", F);
        Assert.Equal(w * L, noCp.Imaginary, w * L * 1e-9);

        // L = 0 leaves a plain Rdc: no ferrite was described, so there is no tank.
        var noL = SeriesImpedance("Bead:FB1 n1 n2 Rdc=0.05 Rp=600 Cp=0.8 pF", F);
        Assert.Equal(Rdc, noL.Real, 1e-9);
        Assert.Equal(0.0, noL.Imaginary, 1e-9);
    }

    // ── T5 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T5_ABeadWithNoParametersAtAll_IsAWire()
    {
        // Z = 0 is stamped as a short constraint rather than a division, so this is an ordinary
        // answer rather than a singular matrix.
        var z = SeriesImpedance("Bead:FB1 n1 n2", 1e9);
        Assert.Equal(0.0, z.Real, 1e-9);
        Assert.Equal(0.0, z.Imaginary, 1e-9);
    }
}
