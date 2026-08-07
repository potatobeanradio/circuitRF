// ================================================================
//  Reachability.cs  —  M3 of brief-harmonicarf-h6
//
//  R-h6-12  §6.6: "the map is not onto. With series Rd/Rs or a lossy embedding, whole regions of the
//           intrinsic plane are unreachable from any extrinsic termination. Silent sticking is a bad
//           experience, so the reachable region is SHADED on the chart during an intrinsic drag
//           (sampled coarsely and cached; refreshed on structural change)."
// ================================================================

using System.Numerics;
using System.Threading;

namespace CircuitRF.Harmonica;

/// <summary>
/// Where one marked band's intrinsic Γ can actually be put, given that the only thing the user can
/// move is its extrinsic termination.
/// </summary>
/// <param name="Boundary">
/// The image of the extrinsic sampling circle, as a closed polygon in the INTRINSIC Γ plane. This is
/// the shape the chart shades.
/// </param>
/// <param name="Interior">
/// The image of a handful of interior extrinsic points. Not drawn — it exists so the region can be
/// checked against the forward path rather than asserted, which is what M3's gate asks for.
/// </param>
/// <param name="Solves">HB solves this cost.</param>
/// <param name="Dropped">
/// Boundary samples that did not produce a value — an extrinsic point near the rim whose HB solve did
/// not converge, or one the drive is undefined at. They are DROPPED rather than substituted: the
/// region the tool shades is the region it actually solved, and interpolating across a gap would
/// shade somewhere nothing was measured. Reported so a caller can see when the polygon is chorded.
/// </param>
public sealed record ReachableRegion(
    IReadOnlyList<Complex> Boundary,
    IReadOnlyList<Complex> Interior,
    int Solves,
    int Dropped = 0)
{
    /// <summary>Signed-area magnitude of the boundary polygon, in Γ² — the number that makes "a lossy
    /// embedding reaches less" a measurement rather than a claim.</summary>
    public double Area
    {
        get
        {
            if (Boundary.Count < 3) return 0;
            double a = 0;
            for (int i = 0, j = Boundary.Count - 1; i < Boundary.Count; j = i++)
                a += Boundary[j].Real * Boundary[i].Imaginary
                   - Boundary[i].Real * Boundary[j].Imaginary;
            return Math.Abs(a) * 0.5;
        }
    }

    /// <summary>Whether an intrinsic Γ is inside the shaded region. Ray casting on the boundary
    /// polygon, the same test <c>ContourGrid.InsideHull</c> uses on the support mask.</summary>
    public bool Contains(Complex gamma)
    {
        if (Boundary.Count < 3) return false;
        bool inside = false;
        for (int i = 0, j = Boundary.Count - 1; i < Boundary.Count; j = i++)
        {
            double xi = Boundary[i].Real, yi = Boundary[i].Imaginary;
            double xj = Boundary[j].Real, yj = Boundary[j].Imaginary;
            if (yi > gamma.Imaginary != yj > gamma.Imaginary &&
                gamma.Real < (xj - xi) * (gamma.Imaginary - yi) / (yj - yi) + xi)
                inside = !inside;
        }
        return inside;
    }

    public static readonly ReachableRegion Empty = new([], [], 0);
    public bool IsEmpty => Boundary.Count < 3;
}

/// <summary>
/// R-h6-12's sampler. Coarse by construction: every sample is a full HB solve plus an intrinsic
/// evaluation, so the density is the whole cost.
///
/// <para><b>Why the BOUNDARY rather than a filled lattice.</b> The reachable set is the image of the
/// extrinsic disc under a smooth, locally invertible map, so its boundary is the image of the disc's
/// boundary — one ring of samples instead of a lattice, for the same shape. <see cref="Interior"/>
/// samples are taken anyway, at a handful of points, purely so the claim can be CHECKED: if an
/// interior extrinsic point maps outside the polygon, the map has folded and the shading is
/// reporting something the forward path does not agree with.</para>
///
/// <para><b>Cached on the structure, not on the terminations</b> — §6.6's own words, "refreshed on
/// structural change". Strictly the region moves as the other marked bands move, and during an
/// inverse drag they do; recomputing it per frame is 24 solves a frame, which is the entire tier-A
/// budget spent on shading. The design note's answer is taken as written and stated here rather than
/// silently improved on.</para>
/// </summary>
public static class Reachability
{
    /// <summary>How many points of the extrinsic circle are mapped. 24 is the shipping density —
    /// see this phase's own measurement.</summary>
    public const int DefaultBoundarySamples = 24;

    /// <summary>Interior probes, used only to check the region against the forward path.</summary>
    public const int DefaultInteriorSamples = 6;

    /// <summary>
    /// How far out the extrinsic sampling circle sits. Not 1.0: Γ = 1 is an open, whose impedance is
    /// infinite, and a passive termination at |Γ| = 1 is a boundary the solve is entitled to struggle
    /// at. 0.95 is inside that and still spans essentially the whole passive plane.
    /// </summary>
    public const double DefaultExtrinsicRadius = 0.95;

    /// <summary>
    /// Samples the region <paramref name="band"/>'s intrinsic Γ can reach, holding every other
    /// termination at <paramref name="baseline"/>.
    /// </summary>
    public static ReachableRegion Sample(
        HarmonicaContext ctx, TerminationSet baseline, InverseBand band, double pavlDbm,
        int boundarySamples = DefaultBoundarySamples,
        int interiorSamples = DefaultInteriorSamples,
        double extrinsicRadius = DefaultExtrinsicRadius,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(baseline);
        if (boundarySamples < 3) return ReachableRegion.Empty;

        bool needsSource = band.Side == TerminationSide.Source;
        int solves = 0;
        Complex[,]? warm = null;

        Complex? Map(Complex extrinsic)
        {
            ct.ThrowIfCancellationRequested();

            var z = HarmonicaDataSet.ImpedanceOf(extrinsic);
            // The same ill-posed case the inverse solve refuses: available power is undefined against
            // a source with Re Z ≤ 0, so such a point is not "unreachable", it is unaskable.
            if (band.Side == TerminationSide.Source && band.Band == 1 && z.Real <= 0) return null;

            var terms = baseline.Clone();
            terms.Set(band.Side, band.Band, z);

            solves++;
            var pt = ctx.Solve(terms, pavlDbm, warm);
            if (!pt.Converged) return null;
            warm = pt.V;

            var g = HarmonicaDataSet.Intrinsic(ctx, pt, needsSource).Gamma[(int)band.Side, band.Band];
            return double.IsFinite(g.Real) && double.IsFinite(g.Imaginary) ? g : null;
        }

        var boundary = new List<Complex>(boundarySamples);
        int dropped = 0;
        for (int i = 0; i < boundarySamples; i++)
        {
            double a = 2.0 * Math.PI * i / boundarySamples;
            var mapped = Map(Complex.FromPolarCoordinates(extrinsicRadius, a));
            if (mapped is { } m) boundary.Add(m); else dropped++;
        }

        var interior = new List<Complex>(interiorSamples);
        for (int i = 0; i < interiorSamples; i++)
        {
            // A deterministic low-discrepancy spiral rather than a lattice — no random, and no
            // clustering at the centre, which a naive polar lattice produces.
            double t = (i + 0.5) / interiorSamples;
            double r = extrinsicRadius * 0.72 * Math.Sqrt(t);
            double a = 2.399963229728653 * i;                   // the golden angle
            var mapped = Map(Complex.FromPolarCoordinates(r, a));
            if (mapped is { } m) interior.Add(m);
        }

        return new ReachableRegion(boundary, interior, solves, dropped);
    }

    /// <summary>The cache key: everything a region depends on that the design note says invalidates
    /// it. A termination move is deliberately NOT in it — see the class remarks.</summary>
    public readonly record struct Key(string StructuralKey, TerminationSide Side, int Band, double PavlDbm);

    public static Key KeyFor(CircuitModel model, InverseBand band, double pavlDbm)
        => new(model.StructuralKey, band.Side, band.Band, Math.Round(pavlDbm, 3));
}
