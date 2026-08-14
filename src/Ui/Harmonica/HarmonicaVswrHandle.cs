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

/// <summary>R-h9r2-8's VSWR-circle drag: the numeric inverse — which VSWR's locus passes through a
/// given drag point — since the map from "drag distance" to VSWR has no closed form for a marker off
/// the real axis. brief-harmonicarf-r6b §1.1 removed the single θ = 0 grab point; a drag now grabs
/// the circle's own circumference anywhere (see <c>HarmonicaHitTest.Resolve</c>), so this class's own
/// job shrank to the pure geometry/inversion, with no one "handle" point of its own left to name.
/// </summary>
public static class HarmonicaVswrHandle
{
    /// <summary>
    /// The ONE remaining restriction on a VSWR-circle drag (brief-harmonicarf-r6b §1.2, owner ruling:
    /// "no clamping — the user may drag the circle outside the Smith chart if they want"). This floor
    /// is geometric, not a policy choice: ρ = (VSWR−1)/(VSWR+1) goes negative below VSWR = 1, and a
    /// negative-radius circle is not a circle. Set just above 1.0 rather than exactly at it, since
    /// VSWR = 1 is a zero-radius circle — nothing to grab or show a locus for.
    /// </summary>
    public const double MinVswr = 1.001;

    /// <summary>
    /// <see cref="VswrThrough"/>'s bisection SEARCH ceiling — not a display cap (brief-harmonicarf-r6b
    /// §1.2 removed the old display clamp entirely; nothing downstream of a drag clamps the resulting
    /// <c>VswrValue</c> anymore). An unbounded upper end still needs a finite bracket that provably
    /// contains the root, so this is picked large enough that no reachable drag point inside the
    /// panel's own canvas extent needs a VSWR beyond it: at VSWR = 1e6, ρ is 1 − 2e-6, i.e. the locus
    /// already sits within 2 parts per million of the Γ = 1 rim for every marker position, which is
    /// far tighter than screen-pixel resolution can distinguish. A drag that still lands outside a
    /// circle this close to the rim genuinely has no finite answer nearby, and <see cref="VswrThrough"/>
    /// clamping to this ceiling (rather than looping forever) is the honest fallback for that case.
    /// </summary>
    public const double MaxVswr = 1_000_000.0;

    /// <summary>Bisection iterations for <see cref="VswrThrough"/> — 60 halvings of a
    /// [1.001, 1e6] search bracket is well past double precision, at the cost of 60 cheap
    /// (2-point-locus) evaluations per pointer move. Nowhere near a hot path.</summary>
    private const int BisectionIterations = 60;

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
    ///
    /// <para><b>brief-harmonicarf-r6b §1.2 — <paramref name="dragGamma"/> is used exactly as given,
    /// no rim clamp.</b> An earlier version pulled a drag point back inside <c>|Γ| &lt; 0.999</c> —
    /// copied from <see cref="HarmonicaViewModel.SetMarkerGamma"/>'s own Γ = 1 guard, where it matters
    /// because that Γ becomes a new termination (an open circuit at exactly Γ = 1). Here
    /// <paramref name="dragGamma"/> is never converted to an impedance — it is only ever compared
    /// against a circle's centre and radius — so the guard bought nothing but silently capping every
    /// "drag past the rim" gesture this brief exists to unlock. Nothing in <see cref="CircleParams"/>
    /// requires it: ρ stays below 1 for any finite VSWR, so <c>ctr</c>/<c>rad</c> stay finite
    /// regardless of how far outside the unit circle the drag point itself sits.</para>
    ///
    /// <para><b>MEASURED, NOT ASSUMED — what "clamps to <see cref="MaxVswr"/>" actually means for an
    /// ordinary (passive, <c>|marker.Gamma| &lt; 1</c>) marker.</b> The underlying Möbius map is an
    /// automorphism of the passive half-plane, so a passive centre's whole power-wave disk images
    /// STRICTLY INSIDE the passive Γ disk for every finite VSWR — the locus approaches <c>|Γ| = 1</c>
    /// as VSWR → ∞ but can never reach or cross it. So a passive marker's circle can never legitimately
    /// need a VSWR the ceiling would reject; the drag simply asks for one closer and closer to the rim
    /// as the pointer approaches it, and <see cref="MaxVswr"/> only ever bites a drag point that has
    /// truly moved past what any finite VSWR reaches. The mirror case — an ACTIVE marker
    /// (<c>|marker.Gamma| &gt; 1</c>, R-h6-10's own flag) — has its ENTIRE family sitting outside
    /// <c>|Γ| = 1</c> instead, never inside; §2.3's "some added points land outside the unit circle"
    /// is this case, not a high-VSWR passive drag.</para>
    /// </summary>
    public static double VswrThrough(Complex center, Complex dragGamma, double z0)
    {
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
