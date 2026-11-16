using System.Numerics;
using CircuitRF.Diagnostics;
using RfCore.Numerics;

namespace CircuitRF.Core.Devices.Microstrip;

/// <summary>
/// A microstrip radial stub (MRADIAL): a sector of angle α whose narrow end is the feed line's width W, open at its arc,
/// <c>Ro</c> from the sector's apex. With <c>Sides</c> = 2 it is a butterfly, two sectors back to back on one feed point.
///
/// <para><b>The radial line.</b> Between the plates of a sector the dominant wave is cylindrical: E across the
/// substrate, H around the apex. With the voltage V across the substrate and the current I flowing outward over the
/// arc α·r, the line's series impedance and shunt admittance per unit radius are Z' = jωμh/(α·r) and
/// Y' = jωε·α·r/h, so V'' + V'/r + k²V = 0: V is a sum of J₀(kr) and Y₀(kr), and I of J₁(kr) and Y₁(kr). Open at the
/// arc (I = 0), the input impedance at the feed radius r_i is</para>
/// <code>
///   Z_in = j · (η·h / (α·r_i)) · [J₀(k·r_i)·Y₁(k·r_o) − Y₀(k·r_i)·J₁(k·r_o)] / [J₁(k·r_i)·Y₁(k·r_o) − Y₁(k·r_i)·J₁(k·r_o)]
/// </code>
/// <para>which for a thin sector far from its apex tends to an open stub's −j·Z·cot(k·(r_o − r_i)). This is the
/// radial-line stub in March's closed form (<see href="https://doi.org/10.1109/TMTT.1985.1132997"/>).</para>
///
/// <para><b>Fringing, as an effective permittivity and an effective radius.</b> The field is partly in air, so the
/// wave sees εeff rather than εr: March takes it from a microstrip line whose width is the sector's chord at its mean
/// radius, W_eq = (r_i + r_o)·sin(α/2). Here that line is MLIN itself (<see cref="MicrostripLineModel"/>, dispersion
/// included), so η = η₀/√εeff and k is its β. The field also fringes past the arc, as it does past a line's open end:
/// the arc is moved out by that line's open-end length, <see cref="MicrostripOpenEnd.Extension"/> (Kirschning, Jansen and Koster
/// <see href="https://doi.org/10.1049/el:19810088"/>), so r_o above is r_o + Δl. Giannini, Sorrentino and Vrba
/// analyse the stub as a planar circuit (<see href="https://doi.org/10.1109/TMTT.1984.1132907"/>), a mode expansion
/// over the sector that is the more accurate reference; it is not used here.</para>
///
/// <para><b>Loss</b> comes cheaply because the radial line's conductor loss per unit length, Rs over the plates'
/// spacing, and its dielectric loss, tan δ, do not depend on r: each is a constant fraction of the series or shunt
/// term, Z' = jωL'(1 − jδc) and Y' = jωC'(1 − jδd). The equation keeps its Bessel form with
/// k = β·√((1 − jδc)(1 − jδd)) and the impedance level scaled by √((1 − jδc)/(1 − jδd)). δc = 2αc/β and
/// δd = 2αd/β are the equivalent line's own conductor and dielectric attenuation (Wheeler's conductor loss with
/// roughness, and tan δ in the filled fraction).</para>
///
/// <para><b>Where it holds.</b> One cylindrical mode: a centred feed excites the sector's next symmetric mode once
/// the arc is about a guided wavelength long, where this stops describing the stub and the instance says so. The
/// equivalent line's own range (MLIN's) is reported for W_eq; a very wide or very short sector is outside it. The
/// junction between the feed and the narrow end, and the side edges' fringing, are not in the model: the impedance
/// level is the sector's own arc width, as March's is.</para>
/// </summary>
public static class MicrostripRadialStub
{
    /// <summary>The widest sector one side can be: past a half-turn the arc would wrap behind its own feed.</summary>
    public const double MaxAngleDeg = 180.0;

    /// <summary>The radius from the apex at which the sector's chord is <paramref name="w"/>: where the feed line
    /// meets it.</summary>
    public static double InnerRadius(double w, double angleDeg) => w / (2.0 * Math.Sin(angleDeg * Math.PI / 360.0));

    /// <summary>The equivalent line March takes εeff from: the chord at the mean radius, (r_i + r_o)·sin(α/2).</summary>
    public static double EquivalentWidth(double w, double ro, double angleDeg)
        => (InnerRadius(w, angleDeg) + ro) * Math.Sin(angleDeg * Math.PI / 360.0);

    /// <summary>Why a stub cannot be built, or null.</summary>
    public static Diagnostic? Refusal(double w, double ro, double angleDeg, int sides)
    {
        if (!(w > 0.0) || !(ro > 0.0))
            return Diagnostic.Create("device.radialstub.not-positive", DiagnosticSeverity.Error,
                "Radial stub: W = {w} m and Ro = {ro} m must both be positive.", ("w", w), ("ro", ro));
        if (!(angleDeg > 0.0 && angleDeg <= MaxAngleDeg))
            return Diagnostic.Create("device.radialstub.angle", DiagnosticSeverity.Error,
                "Radial stub: Angle = {a} deg; a sector is wider than nothing and at most 180 deg. A bare number is "
              + "read in radians: write the unit.", ("a", angleDeg));
        if (sides is not (1 or 2))
            return Diagnostic.Create("device.radialstub.sides", DiagnosticSeverity.Error,
                "Radial stub: Sides = {n}; 1 is one sector, 2 a butterfly.", ("n", sides));
        double ri = InnerRadius(w, angleDeg);
        if (!(ro > ri))
            return Diagnostic.Create("device.radialstub.too-short", DiagnosticSeverity.Error,
                "Radial stub: Ro = {ro} m does not reach past {ri} m, where a {a} deg sector is as wide as its feed "
              + "(W = {w} m). Lengthen Ro or widen Angle.", ("ro", ro), ("ri", ri), ("a", angleDeg), ("w", w));
        return null;
    }

    /// <summary>
    /// One sector's input impedance at the feed, Ω, at <paramref name="freqHz"/> &gt; 0. <paramref name="line"/> is the
    /// equivalent line (width <see cref="EquivalentWidth"/>) on the stub's substrate; its validity findings go to its own
    /// reporter. <paramref name="arcWavelengths"/> is the effective arc's length in guided wavelengths, for the
    /// single-mode check.
    /// </summary>
    public static Complex SectorImpedance(double freqHz, double w, double ro, double angleDeg, MicrostripSubstrate s,
        MicrostripLineModel line, out double arcWavelengths)
    {
        var p = line.LineParameters(freqHz);
        double alpha = angleDeg * Math.PI / 180.0;
        double ri = InnerRadius(w, angleDeg);
        double roe = ro + MicrostripOpenEnd.Extension(EquivalentWidth(w, ro, angleDeg), s.HMeters, s.EpsR, p.EeffStatic);

        double beta = p.BetaRadPerM;
        var series = new Complex(1.0, -2.0 * p.ConductorLossNpPerM / beta);    // 1 − jδc
        var shunt = new Complex(1.0, -2.0 * p.DielectricLossNpPerM / beta);    // 1 − jδd
        Complex k = beta * Complex.Sqrt(series * shunt);
        Complex level = 2.0 * Math.PI * freqHz * MicrostripLoss.Mu0 * s.HMeters / (beta * alpha * ri)   // η·h/(α·r_i)
                      * Complex.Sqrt(series / shunt);
        arcWavelengths = alpha * roe * beta / (2.0 * Math.PI);

        Complex xi = k * ri, xo = k * roe;
        Complex j1o = Bessel.J1(xo), y1o = Bessel.Y1(xo);
        Complex num = Bessel.J0(xi) * y1o - Bessel.Y0(xi) * j1o;
        Complex den = Bessel.J1(xi) * y1o - Bessel.Y1(xi) * j1o;
        return Complex.ImaginaryOne * level * num / den;
    }
}
