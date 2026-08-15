// ================================================================
//  HarmonicaVswrHandle.cs  —  R-h9r2-8's drag handle
//
//  THE FINDING THAT SHAPES THIS FILE: the VSWR locus's own doc comment says "for real Z0 this reduces
//  to Γ = ρ·e^{jθ} about the matched point" — read at first as "the circle is centred at the marker's
//  own Γ with radius ρ = (VSWR-1)/(VSWR+1)" for ANY marker. Measured directly (a disposable Python
//  probe against LoadpullSurface's own closed-form ctr/rad, see the session that added this file): that
//  is false the moment the marker's own Γ is off the real axis — "the matched point" in that sentence
//  means Γ = 0 specifically (Zc real AND zero-reflection), not "wherever the marker happens to be". A
//  center at (0.3,-0.2), VSWR=3, Z0=50 draws a circle whose true centre is (0.23,-0.16), not (0.3,-0.2)
//  — a difference far larger than any grab tolerance.
//
//  ROUND 10 (owner, 2026-08-15) removed the last restriction and, with it, the bisection: "VSWR circles
//  are restricted in value. They should not be... VSWR can be any value, except NaN or infinity."
//  The inverse turns out to have a CLOSED FORM that already lives in RfCore — see VswrThroughEx.
// ================================================================

using System;
using System.Numerics;
using RfCore;
using RfCore.Loadpull;

namespace CircuitRF.Ui.Harmonica;

/// <summary>R-h9r2-8's VSWR-circle drag: which VSWR's locus passes through a given drag point.
/// brief-harmonicarf-r6b §1.1 removed the single θ = 0 grab point; a drag now grabs the circle's own
/// circumference anywhere (see <c>HarmonicaHitTest.Resolve</c>), so this class's own job is the pure
/// geometry/inversion, with no one "handle" point of its own left to name.
/// </summary>
public static class HarmonicaVswrHandle
{
    /// <summary>
    /// The value a drag lands on where the true answer is ±∞ — owner ruling, Round 10: "VSWR can be
    /// any value, except NaN or infinity. If infinity, make it 1e9."
    ///
    /// <para>That case is exactly the rim: <c>|s| = 1</c> in the marker's own power-wave plane, whose
    /// image for a passive marker is <c>|Γ| = 1</c>. Approaching it from inside the answer runs to
    /// <c>+∞</c>; from outside it comes back from <c>−∞</c> — the map is continuous through the point
    /// at infinity, not through any finite number, so a finite stand-in is a display choice and this
    /// is the owner's.</para>
    /// </summary>
    public const double InfiniteVswr = 1e9;

    /// <summary>The circle's own centre and radius in the Γ plane, for one (marker, VSWR, Z0) —
    /// derived from <see cref="LoadpullSurface.VswrLocus"/>'s own θ = 0 / θ = π samples rather than
    /// re-deriving the Möbius formula by hand: those two points are diametrically opposite BY
    /// CONSTRUCTION (the locus is sampled as <c>ctr + rad·e^{jθ}</c>), so their midpoint is exactly
    /// <c>ctr</c> and half their separation is exactly <c>rad</c>.</summary>
    private static (Complex Ctr, double Rad) CircleParams(Complex center, double vswr, double z0)
    {
        var pts = LoadpullSurface.VswrLocus(center, vswr, SurfacePlane.Gamma, new Complex(z0, 0.0), nPoints: 2);
        var ctr = (pts[0] + pts[1]) / 2.0;
        double rad = (pts[0] - pts[1]).Magnitude / 2.0;
        return (ctr, rad);
    }

    /// <summary>The VSWR whose locus passes through <paramref name="dragGamma"/>. See
    /// <see cref="VswrThroughEx"/> for the whole story; this is that answer without the flag.</summary>
    public static double VswrThrough(Complex center, Complex dragGamma, double z0)
        => VswrThroughEx(center, dragGamma, z0).Vswr;

    /// <summary>
    /// The VSWR whose locus passes through <paramref name="dragGamma"/>, and whether that answer had
    /// to stand in for an infinite one.
    ///
    /// <para><b>Closed form, not a search (Round 10).</b> The locus the renderer draws is the image of
    /// the power-wave circle <c>|s_c| = ρ</c> about the marker's own impedance, with
    /// <c>ρ = (V−1)/(V+1)</c> — so inverting it is just "map the drag point BACK to <c>s_c</c> and read
    /// its magnitude", which is exactly what <see cref="RfHelpers.VswrFromGamma"/> already computes
    /// (<c>s = (z_d − z_c)/(z_d + conj z_c)</c>, then <c>(1+|s|)/(1−|s|)</c>). Both sides normalise by
    /// the SAME real <c>z0</c>, which cancels out of that ratio exactly, so this and
    /// <see cref="CircleParams"/> can never disagree about which circle a point is on. The 60-iteration
    /// bisection this replaced was searching for a root the algebra hands over directly.</para>
    ///
    /// <para><b>And that is what unlocks the owner's ask.</b> <c>ρ &gt; 1</c> — a drag OUTSIDE the
    /// image of the passive disk — makes <c>(1+ρ)/(1−ρ)</c> NEGATIVE, which is a perfectly ordinary
    /// number for <c>LoadpullSurface.VswrCircleGamma</c> to draw (it squares ρ for the centre and takes
    /// <c>|ρ|</c> for the radius), and is the "negative VSWR" the owner asked to be able to drag to.
    /// The old <c>[1.001, 10⁶]</c> bracket could not express it at all, which is why every drag past
    /// the rim used to pin at the ceiling.</para>
    ///
    /// <para><paramref name="z0"/> is kept in the signature even though a real reference cancels: it
    /// is what <see cref="CircleParams"/> and the renderer are both handed, and dropping it here would
    /// leave a caller free to pass a different one to each.</para>
    /// </summary>
    public static (double Vswr, bool Saturated) VswrThroughEx(Complex center, Complex dragGamma, double z0)
    {
        _ = z0;   // see the doc comment: a real reference cancels out of the power-wave ratio exactly.

        double v = RfHelpers.VswrFromGamma(center, dragGamma);

        if (double.IsNaN(v)) return (1.0, true);                 // degenerate (Γ = 1 on either side)
        if (double.IsPositiveInfinity(v)) return (InfiniteVswr, true);
        if (double.IsNegativeInfinity(v)) return (-InfiniteVswr, true);
        return (v, false);
    }
}
