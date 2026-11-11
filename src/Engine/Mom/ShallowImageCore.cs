// MIM-8 — the cell-pair integral of a SHALLOW image's static kernel, in the form the fill can use.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// WHAT THIS IS, AND WHY IT IS NOT A BETTER QUADRATURE
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// §MIM-3 measured the cross-level block of the multi-level fill losing four decades between
// cell/separation 1 and 20, and named the mechanism: at d ≪ cell the kernel carries a peak of width
// d inside a cell of width h, and a rule that treats the pair as smooth integrates straight over it.
// §MIM-8 located the peak exactly. It is not the kernel and it is not the two extracted asymptotes —
// a cross-REGION pair has none of those (LayeredSpectralGreens.AsymptoticAtHeights returns zero
// coefficients for one, by design) — it is an IMAGE whose depth is of the order of the film
// thickness. On the shipped MMIC stack, with a 0.2 µm capacitor dielectric, the scalar kernel's
// cross-level fit put an image of amplitude 0.144 at a depth of 0.128 µm, i.e. 0.05 of a 2.5 µm cell.
//
// MIM-12a — AND THAT IMAGE IS NO LONGER A FITTED ONE. A thin region is a Fabry-Perot cavity whose
// multiple reflections are a closed-form geometric series (LayeredSpectralGreens.
// ThinRegionImagesAtHeights), and the fit was extrapolating all of it: the series decays like
// e^{-k_rho d} with d = 0.2 um, i.e. it is structure out to 5e6 m^-1, against a sampling path that
// stops at 1.9e5. Dcim.FitAtHeights now peels that series out of its samples and the list this file
// integrates holds the EXACT images instead. Nothing here changed — the closed form of a static
// image over a cell pair does not care where the image came from — which is the point of MIM-8's
// machinery having been built as a list.
//
// FromDcimAtHeights treats every image as smooth and moves only its value at ρ = 0 into the constant.
// That is right while the depth is large against a cell — R-fil-8 says so in as many words, and
// PlanarKernelTerms.SmallestImageDepth exists to let the claim be checked — and it is wrong here.
//
// The treatment is the one the via z-integral already uses one dimension up: SUBTRACT the offending
// term's static part and put it back in CLOSED FORM. What that needs is this file's one function —
// the pulse-weighted mean of 1/√(ρ² + b²) over a pair of rectangular cells. Outer Gauss on the fill's
// own graded rule, inner closed form, exactly as PlanarFill.CellPairCores does it in the plane and as
// ViaZIntegral.MeanAtOffset does it at a real offset. The only thing new is that b is COMPLEX, which
// is what a fitted depth is.
//
// WHAT IT DOES NOT TOUCH. The VECTOR block carries the same peak and does not need this: measured on
// MIM-3's own plate ladder, the vector half of the cross-level error is 1.7e-6 at cell/separation 50
// where the total is 3.2e-1 — five decades down, because at these dimensions the scalar half is
// 1/(ω²ε₀µ₀A) larger. Adding the ramp-weighted moments at a complex offset would be arithmetic with
// nothing at the end of it, so it was measured and not built. §MIM-8 in HISTORY.md carries the table.

using System.Numerics;

namespace CircuitRF.Engine.Mom;

/// <summary>
/// The closed-form half of MIM-8's peak subtraction: the cell-pair mean of a shallow image's static
/// kernel. See the file header for what is subtracted and why.
/// </summary>
public static class ShallowImageCore
{
    /// <summary>
    /// <c>(1/(A_a A_b)) ∫∫dS ∫∫dS′ 1/√(ρ² + b²)</c> over two rectangular cells at a complex depth
    /// <paramref name="depth"/> — normalised exactly as <c>PlanarFillCores.ScalarCore</c> is, so it
    /// drops straight into the entry beside the planar cores.
    ///
    /// <para>The outer integral is the fill's own graded rule for that cell pair
    /// (<see cref="PlanarFill.RuleForCells"/> — the same panels and nodes the geometric cores are
    /// taken on, so refining the fill settings refines this too and a forced-high reference remains a
    /// reference). The inner one is <see cref="RectangleIntegrals.InverseAtComplexOffset"/>, which is
    /// where the peak actually lives: a rule cannot miss a feature it never samples across.</para>
    /// </summary>
    public static Complex CellPairMean(PlanarMesh mesh, int cellA, int cellB, Complex depth,
                                       PlanarFillSettings settings)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(settings);
        return CellPairMean(mesh.Cells[cellA], mesh.Cells[cellB], depth, settings);
    }

    /// <summary>
    /// The same, over two cells the caller already holds — what the ACCELERATED near field reads,
    /// where a cell pair is addressed by its translation class's synthetic representative rather
    /// than by a mesh index (<c>PairClassifier</c>).
    /// </summary>
    public static Complex CellPairMean(PlanarCell a, PlanarCell b, Complex depth,
                                       PlanarFillSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var (nodes, panels) = PlanarFill.RuleForCells(a, b, settings);
        var (gx, gw) = Legendre.Nodes(nodes);

        double invAa = 1.0 / a.Area, invAb = 1.0 / b.Area;
        Complex sum = Complex.Zero;

        // PMS-5 — the panels PlanarFill.CellPairCores takes, so an overlapping strip and block are cut the same way.
        foreach (var (xa, xb, ya, yb) in PlanarLocalGridFill.OuterPanels(a, b, panels, settings.SelfPanels))
        {
            double cx = 0.5 * (xa + xb), hx = 0.5 * (xb - xa);
            double cy = 0.5 * (ya + yb), hy = 0.5 * (yb - ya);

            for (int i = 0; i < nodes; i++)
            {
                double x = cx + hx * gx[i];
                for (int j = 0; j < nodes; j++)
                {
                    double y = cy + hy * gx[j];
                    sum += gw[i] * gw[j] * hx * hy * invAa * invAb
                         * RectangleIntegrals.InverseAtComplexOffset(
                               b.XMin - x, b.XMax - x, b.YMin - y, b.YMax - y, depth);
                }
            }
        }
        return sum;
    }
}
