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
//  So the handle location and the drag-to-VSWR mapping BOTH go through the real, closed-form Möbius
//  circle — never the naive "radius from the marker" shortcut — recovered from LoadpullSurface's own
//  PUBLIC VswrLocus at nPoints=2 (θ=0 and θ=π land diametrically opposite by construction, since
//  VswrLocus itself parametrizes the circle as ctr + rad·e^{jθ} — so their midpoint IS ctr and half
//  their separation IS rad, exactly, with no new RfCore surface needed).
// ================================================================

using System;
using System.Numerics;
using RfCore.Loadpull;

namespace CircuitRF.Ui.Harmonica;

/// <summary>R-h9r2-8's VSWR-circle drag handle: the θ = 0 point on the true locus, and the numeric
/// inverse — which VSWR's locus passes through a given drag point — since the map from "drag distance"
/// to VSWR has no closed form for a marker off the real axis.</summary>
public static class HarmonicaVswrHandle
{
    /// <summary>The lowest VSWR a drag may reach — short of 1.0 (a zero-radius circle, nothing to
    /// grab) but otherwise as close to a perfect match as the drag can express.</summary>
    public const double MinVswr = 1.001;

    /// <summary>The highest VSWR a drag may reach — the circle's own radius saturates well before
    /// this for any drag point inside the unit disk, so the cap is a sanity ceiling, not a geometric
    /// limit.</summary>
    public const double MaxVswr = 199.0;

    /// <summary>Bisection iterations for <see cref="VswrThrough"/> — 40 halvings of a
    /// [1.001, 199] search bracket is ~1e-13 relative precision, at the cost of 40 cheap
    /// (2-point-locus) evaluations per pointer move. Nowhere near a hot path.</summary>
    private const int BisectionIterations = 40;

    /// <summary>The Γ-plane reflection-coefficient magnitude for a given VSWR — the standard
    /// ρ = (VSWR−1)/(VSWR+1) relation. Pure unit conversion; carries no claim about WHERE on the Γ
    /// plane a circle of that radius is centred (see this file's own header for why that claim was
    /// wrong).</summary>
    public static double RhoOf(double vswr) => (vswr - 1.0) / (vswr + 1.0);

    /// <summary>The inverse of <see cref="RhoOf"/>, clamped to <see cref="MinVswr"/>/<see cref="MaxVswr"/>.
    /// Used both to convert a raw ρ into a displayable VSWR and, via <see cref="HarmonicaViewModel.
    /// SetMarkerVswr"/>, to re-clamp an already-computed VSWR by round-tripping it through ρ.</summary>
    public static double VswrOf(double rho)
    {
        rho = Math.Clamp(rho, 0.0, 0.99);
        return Math.Clamp((1.0 + rho) / (1.0 - rho), MinVswr, MaxVswr);
    }

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

    /// <summary>The handle's raw Gamma — the locus's own θ = 0 sample, i.e. exactly the point
    /// <see cref="HarmonicaPanelRenderer.DrawVswrLocus"/> starts (and closes) its outline at. Never run
    /// through <c>IntrinsicGlyphScale</c>: the locus itself is drawn on the raw chart transform.</summary>
    public static Complex HandleGamma(Complex center, double vswr, double z0)
        => LoadpullSurface.VswrLocus(center, vswr, SurfacePlane.Gamma, new Complex(z0, 0.0), nPoints: 1)[0];

    /// <summary>
    /// The VSWR whose locus passes through <paramref name="dragGamma"/> — a drag SETS this, it does
    /// not compute a radius from it, because there is no closed form: the circle's centre moves with
    /// VSWR too (see this file's own header). Found by bisection on
    /// <c>f(v) = |dragGamma − ctr(v)| − rad(v)</c>, which is strictly decreasing across the whole
    /// <see cref="MinVswr"/>…<see cref="MaxVswr"/> range for every marker position tested — positive
    /// near <see cref="MinVswr"/> (the tightest circle is a near-point at the marker, and almost any
    /// drag point lies outside it), negative near <see cref="MaxVswr"/> for any point inside the Γ
    /// disk. A drag that manages to fall outside that bracket either way clamps to the corresponding
    /// end rather than extrapolating past it.
    /// </summary>
    public static double VswrThrough(Complex center, Complex dragGamma, double z0)
    {
        // Γ = 1 is an open, same guard SetMarkerGamma already applies to a marker drag.
        double mag = dragGamma.Magnitude;
        if (mag > 0.999) dragGamma = dragGamma / mag * 0.999;

        double F(double v)
        {
            var (ctr, rad) = CircleParams(center, v, z0);
            return (dragGamma - ctr).Magnitude - rad;
        }

        double lo = MinVswr, hi = MaxVswr;
        if (F(lo) <= 0) return lo;
        if (F(hi) >= 0) return hi;

        for (int i = 0; i < BisectionIterations; i++)
        {
            double mid = (lo + hi) / 2.0;
            if (F(mid) > 0) lo = mid; else hi = mid;
        }
        return (lo + hi) / 2.0;
    }
}
