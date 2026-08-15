using System;
using System.Numerics;

namespace CircuitRF.Ui.Harmonica;

/// <summary>
/// R-h45-4 / §4.5 consequence 2 — the compressed radial scale that puts a glyph with
/// <c>|Γ_intr| &gt; 1</c> <b>outside</b> the chart boundary rather than clamping or hiding it.
///
/// <para><b>Why this must not clamp.</b> The intrinsic current is the CONDUCTION current, not the
/// terminal current, so an intrinsic reflection outside the unit circle is <i>ordinary</i>, not an
/// error — §4.5's own words. Clamping it to the rim would silently report a different number;
/// hiding it would silently report none. Both are worse than a glyph sitting a little outside the
/// circle, which is unambiguous and self-explaining.</para>
///
/// <para><b>Why compressed rather than linear.</b> <c>|Γ_intr|</c> is unbounded above. A linear
/// scale would push a large value arbitrarily far off-panel — which is "hidden" by another route.
/// The map below is monotone, continuous at <c>|Γ| = 1</c>, exact inside the disc, and asymptotic to
/// <c>1 + margin</c>, so <b>every</b> finite value lands in a bounded annulus just outside the rim
/// and ORDER is always readable even when magnitude is compressed.</para>
/// </summary>
public static class IntrinsicGlyphScale
{
    /// <summary>How far beyond the unit circle a glyph may ever be drawn, in Γ units.</summary>
    public const double DefaultMargin = 0.25;

    /// <summary>How quickly the region just outside the rim is used up. Larger = more of the margin
    /// spent on values close to 1.</summary>
    public const double DefaultRate = 1.0;

    /// <summary>
    /// R7A §1 — the largest <c>|Γ|</c> a pointer may ever WRITE to a marker. <see cref="TrueRadius"/>
    /// saturates here rather than at its own asymptote (<c>1 + margin</c>, a true pole with no finite
    /// pre-image) — the old <c>1 - 1e-9</c> clamp put every pointer position at or beyond drawn radius
    /// <c>1 + margin</c> at the SAME Γ ≈ −10⁹, which does not survive its own Γ↔Z round trip
    /// (catastrophic cancellation in <c>Z = z0(1+Γ)/(1−Γ)</c>) and reads as a different, disagreeing
    /// number everywhere it is re-derived from Z (the readout strip, a `.charm` reload). At |Γ| = 10,
    /// Z round-trips to full double precision and is already past every physically interesting active
    /// termination (−0.818·z0 — see §1.3's own worked numbers at Z0 = 50 and 80 Ω).
    /// </summary>
    public const double MaxTrueMagnitude = 10.0;

    /// <summary>
    /// Maps a true <c>|Γ|</c> to the radius the glyph is drawn at.
    /// <list type="bullet">
    /// <item><c>|Γ| ≤ 1</c> → returned unchanged. Inside the disc the chart is exact; the compression
    /// exists only for the region that has nowhere else to go.</item>
    /// <item><c>|Γ| &gt; 1</c> → <c>1 + margin·(1 − 1/(1 + rate·(|Γ|−1)))</c>, which is 1 at the rim
    /// and approaches <c>1 + margin</c> as <c>|Γ| → ∞</c>.</item>
    /// </list>
    /// </summary>
    public static double DisplayRadius(double magnitude,
                                       double margin = DefaultMargin, double rate = DefaultRate)
    {
        if (double.IsNaN(magnitude)) return 0.0;
        if (magnitude <= 1.0) return Math.Max(0.0, magnitude);

        double m = Math.Max(0.0, margin);
        double k = Math.Max(1e-9, rate);
        return 1.0 + m * (1.0 - 1.0 / (1.0 + k * (magnitude - 1.0)));
    }

    /// <summary>
    /// The glyph's drawing position: the true Γ's ANGLE with the compressed radius. The angle is
    /// never touched — which harmonic band's glyph points where is real information.
    /// </summary>
    public static Complex DisplayPosition(Complex gammaIntrinsic,
                                          double margin = DefaultMargin, double rate = DefaultRate)
    {
        double mag = gammaIntrinsic.Magnitude;
        if (mag <= 1e-15) return Complex.Zero;
        double r = DisplayRadius(mag, margin, rate);
        return gammaIntrinsic / mag * r;
    }

    /// <summary>True when this glyph is being drawn in the compressed annulus outside the rim — the
    /// panel uses it to draw the marker differently so the compression is never silent.</summary>
    public static bool IsCompressed(Complex gammaIntrinsic) => gammaIntrinsic.Magnitude > 1.0;

    /// <summary>
    /// The exact inverse of <see cref="DisplayRadius"/>. H6 needs it because a pointer lands on a
    /// DRAWN radius and has to be turned back into the Γ that was drawn there — and a hit-test or a
    /// drag that inverted the chart transform alone would be wrong for everything in the annulus.
    ///
    /// <para>A drawn radius at or beyond <c>1 + margin</c> is the map's asymptote and has no finite
    /// pre-image; it saturates at <see cref="MaxTrueMagnitude"/> rather than returning infinity or the
    /// asymptote's own <c>1 - 1e-9</c>-clamped near-pole (R7A §1 — that plateau does not survive its own
    /// Γ↔Z round trip). The clamp is derived from <see cref="MaxTrueMagnitude"/> itself, so the two can
    /// never drift apart.</para>
    /// </summary>
    public static double TrueRadius(double displayRadius,
                                    double margin = DefaultMargin, double rate = DefaultRate)
    {
        if (double.IsNaN(displayRadius)) return 0.0;
        if (displayRadius <= 1.0) return Math.Max(0.0, displayRadius);

        double m = Math.Max(1e-12, margin);
        double k = Math.Max(1e-9, rate);

        double xMax = MaxTrueMagnitude - 1.0;
        double uMax = k * xMax / (1.0 + k * xMax);
        double u = Math.Min((displayRadius - 1.0) / m, uMax);
        return 1.0 + u / (k * (1.0 - u));
    }

    /// <summary>The Γ whose glyph would be drawn at <paramref name="displayed"/>. Angle untouched,
    /// exactly as <see cref="DisplayPosition"/> leaves it.</summary>
    public static Complex TruePosition(Complex displayed,
                                       double margin = DefaultMargin, double rate = DefaultRate)
    {
        double mag = displayed.Magnitude;
        if (mag <= 1e-15) return Complex.Zero;
        return displayed / mag * TrueRadius(mag, margin, rate);
    }
}
