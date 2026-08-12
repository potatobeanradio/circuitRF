// Conformal boundary cells — M2: THE ROOFTOP OVER A CUT PAIR, and the finding that decides M3.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// THE FINDING: ξ/Area IS NOT THE RIGHT WEIGHT ON A CUT CELL, AND THE REASON IS NOT AN APPROXIMATION
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// §3's M2 asks the question outright: "State whether the ramp ξ/Area is still the right weight on a
// cut cell, or whether it has to become the cut cell's own linear profile." **It has to become the
// cut cell's own profile, and the failure of the old one is exact rather than small.**
//
// L8c's rooftop is f = û·ξ(r)/Area with ξ the distance to the cell's OUTER GRIDLINE. Take a cut
// cell A whose metal boundary is oblique, paired rightward across the shared gridline. At that
// gridline ξ = W_A, so the current crossing it is
//
//     ∫ f·x̂ dℓ = (W_A / Area_A) · L_shared
//
// which is 1 only when Area_A = W_A·L_shared — i.e. only when the cell is its whole rectangle.
// **On a cut cell the basis carries the wrong total current**, and by exactly the factor the phase
// exists to correct. It also puts f ≠ 0 on the oblique rim, which is the property §3 says to check
// hardest, because that is a line of charge on the edge of the metal.
//
// THE WEIGHT THAT WORKS, AND WHY IT IS STILL A "ROOFTOP". Keep the current purely x̂ (D5 depends on
// it) and keep ∇·f constant (D4 depends on it), so the weight must be a ramp of unit gradient in the
// flow direction. The only freedom left is WHERE it vanishes, and the three properties then fix that
// uniquely: it must vanish on the support's own OUTER boundary, whatever that boundary is made of —
// the grid line where the cell is whole, the metal rim where it is cut. So
//
//     f = û · (x − x_out(y)) / Area                                                          (★)
//
// with x_out(y) the region's own low-x boundary at that y. Then, exactly and not to a tolerance:
//
//   • ∇·f = ∂/∂x [(x − x_out(y))/Area] = 1/Area — x_out depends on y alone. R-fil-1 unchanged.
//   • ∫∇·f dS = Area/Area = 1 on each half, so the pair sums to 0 to machine precision.
//   • f = 0 on the WHOLE outer boundary, rim included: no charge lands on the metal's edge.
//   • ∫ f·x̂ dℓ across the shared face = (1/Area)∫(x_s − x_out(y))dy = Area/Area = 1 A.
//
// THE LAST ONE HAS A CONDITION, AND IT IS R-cut-4'S, ARRIVED AT FROM THE OTHER SIDE. That integral
// is the region's area only if the region is SWEPT by the shared face — if sliding every point of
// the face inward covers the region exactly. A convex clip is swept from the side the cut normal
// points away from, so a cut cell is anchored on two of its four faces and NOT on the other two. A
// basis across a face its own half is not anchored to would leak current out through the rim; it is
// **refused in the mesher, where the basis set is built**, and counted. That is R-cut-4's own
// instruction ("Decide it in the MESHER … and assert the resulting count — not in the fill, where it
// would be a guard on a division"), and the zero-length shared edge falls out of the same test.
//
// WHAT (★) COSTS, AND IT IS THE ONE THING THAT REACHES M3. x_out(y) is PIECEWISE affine — the rim
// where the rim bounds the region, the grid line where it does not — so (★) is not one linear weight
// over one rectangle but one affine weight over each of a few STRIPS. That is exactly the shape
// L8c's closed forms already take: an affine weight α·x + β·y + γ integrates against 1/R, ln r and r
// through the plain integral and its TWO first moments, which is what PolygonIntegrals returns. So
// M3's route (a) is not merely possible, it is required by M2's answer — and route (c), scaling the
// rectangle's own closed form by the area fraction, is refuted here rather than in M3: it keeps the
// ramp measured from the grid line and therefore keeps the wrong current and the rim charge.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// WHAT IS *NOT* EXACT, STATED HERE RATHER THAN FOUND LATER
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// **Pointwise continuity across the shared face does not survive a cut, and it cannot.** On the face
// the two halves read ℓ_A(y)/Area_A and ℓ_B(y)/Area_B with ℓ the local x-extent; for two whole
// rectangles both are 1/L and the rooftop is continuous, and for a cut cell ℓ varies with y while
// the other side's does not. Their INTEGRALS agree (both are 1 A), so the mismatch is a
// zero-net-charge line dipole on an internal gridline — not a monopole, and not on the metal's rim.
// It is a discretisation artifact of the same kind and order as the mesh, and the honest thing is
// that it is MEASURED rather than argued: see the fill's own oracle comparison in the phase note.
// Making it exact needs a basis whose current is not purely x̂ — which is RWG, and is §1's own
// out-of-scope.

namespace CircuitRF.Engine.Mom;

/// <summary>
/// One strip of a basis half's support: a convex ring, and the AFFINE weight
/// <c>w = Alpha·x + Beta·y + Gamma</c> that is zero on the support's outer boundary there and has
/// unit gradient along the flow direction. The basis function is <c>w/Area</c>.
/// </summary>
public readonly record struct WeightStrip(EmPoint[] Ring, double Alpha, double Beta, double Gamma)
{
    public double At(double x, double y) => Alpha * x + Beta * y + Gamma;
}

/// <summary>
/// A basis half's integration domain and weight — see the file header for why it is a list of strips
/// and not a rectangle plus a scalar.
/// </summary>
public sealed class RooftopSupport
{
    /// <summary>The strips, whose rings tile the cell's metal exactly.</summary>
    public required IReadOnlyList<WeightStrip> Strips { get; init; }

    /// <summary>Σ of the strips' areas — equal to <see cref="PlanarCell.Area"/>, and asserted so.</summary>
    public required double Area { get; init; }

    /// <summary>
    /// <b>Whether sliding the shared face inward covers the region</b>, which is the condition for
    /// the unit-current property to hold exactly. False means the region's far boundary is the metal
    /// rim rather than the shared gridline, so a basis across that face would push current out
    /// through the rim — R-cut-4's refusal, decided here and applied by the mesher.
    /// </summary>
    public required bool Anchored { get; init; }

    /// <summary>The length of the shared face that actually carries metal. <b>Zero is R-cut-4's other
    /// half</b>: two cells adjacent on the grid whose shared edge is entirely outside the metal are
    /// not a basis at all.</summary>
    public required double SharedFaceLength { get; init; }

    /// <summary>True when the support is the cell's whole rectangle with a single affine ramp — the
    /// pre-conformal case, which the fill takes on its own unchanged code path.</summary>
    public required bool IsWholeRectangle { get; init; }

    /// <summary>
    /// <b>FLOW-SIMPLICITY: at every transverse coordinate the region meets the line in exactly ONE
    /// interval</b> — the property the strip construction actually needs, and the one the
    /// convex-decomposition brief's §1 identifies as being weaker than convexity and PER DIRECTION.
    ///
    /// <para><see cref="Extent"/> returns the outer hull of the crossing set and <see cref="Build"/>
    /// makes ONE trapezoid spanning it, so a region that meets the line twice has source integrated
    /// over a gap where there is no metal. That — not non-convexity — is the sin. A merged L-shaped
    /// cell is flow-simple in both directions (its pieces share a face, so the union at any line is
    /// connected), which is why R-cut-3's merge works and has always worked.</para>
    ///
    /// <para>It is computed HERE rather than by a separate geometric predicate over the ring, from the
    /// same crossing walk <see cref="Extent"/> uses, so there is no second implementation that has to
    /// agree with what the strips actually do (L7b-b's D1 trap).</para>
    /// </summary>
    public required bool FlowSimple { get; init; }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Construction
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The support of one half of a rooftop: the cell, the flow direction, and which side of the cell
    /// the SHARED face is on.
    /// </summary>
    /// <param name="sharedIsHigh">True for the pair's lower-index (left/below) cell, whose shared face
    /// is its high-coordinate side and whose ramp therefore rises from the low side.</param>
    /// <param name="sharedCoord">The gridline the two cells share.</param>
    public static RooftopSupport Build(PlanarCell cell, PlanarBasisDirection dir,
                                       bool sharedIsHigh, double sharedCoord)
    {
        bool alongX = dir == PlanarBasisDirection.X;
        double tol  = 1e-9 * Math.Min(cell.Width, cell.Height);

        // ── The whole rectangle: L8c's own case, kept as one strip and FLAGGED, so the fill can take
        //    the code that produced every pre-conformal number in this repository (R-cut-2).
        if (cell.Region is null)
        {
            double outer = alongX ? (sharedIsHigh ? cell.XMin : cell.XMax)
                                  : (sharedIsHigh ? cell.YMin : cell.YMax);
            double sign  = sharedIsHigh ? 1.0 : -1.0;
            var ring = new[] { new EmPoint(cell.XMin, cell.YMin), new EmPoint(cell.XMax, cell.YMin),
                               new EmPoint(cell.XMax, cell.YMax), new EmPoint(cell.XMin, cell.YMax) };
            return new RooftopSupport
            {
                Strips           = [new WeightStrip(ring, alongX ? sign : 0, alongX ? 0 : sign, -sign * outer)],
                Area             = cell.Area,
                Anchored         = true,
                SharedFaceLength = alongX ? cell.Height : cell.Width,
                IsWholeRectangle = true,
                FlowSimple       = true,
            };
        }

        // ── A cut (or merged) cell: strips across the flow direction ──────────────────────────
        //
        // The breakpoints are every vertex coordinate TRANSVERSE to the flow, so inside a strip no
        // piece has a vertex and each piece's two chains are single straight edges. The region's own
        // low and high boundaries there are then affine, which is exactly what (★) needs.
        var breaks = new List<double>();
        foreach (var piece in cell.Region.Pieces)
            foreach (var v in piece) breaks.Add(alongX ? v.Y : v.X);
        breaks.Sort();

        var strips = new List<WeightStrip>();
        double area = 0, faceLen = 0;
        bool anchored = true, flowSimple = true;

        for (int k = 0; k + 1 < breaks.Count; k++)
        {
            double ta = breaks[k], tb = breaks[k + 1];
            if (tb - ta <= tol) continue;

            // FLOW-SIMPLICITY, asked at the strip's MIDPOINT rather than at its ends. Inside a strip
            // no piece has a vertex, so the crossing structure is constant and unambiguous there; at
            // a breakpoint it is degenerate by construction (that coordinate IS a vertex), and a
            // count taken there would read a tangency as a second interval.
            if (Intervals(cell.Region, alongX, 0.5 * (ta + tb), tol) > 1) flowSimple = false;

            // The region's own extent along the flow direction, at each end of the strip.
            var (loA, hiA) = Extent(cell.Region, alongX, ta, tol);
            var (loB, hiB) = Extent(cell.Region, alongX, tb, tol);
            if (double.IsNaN(loA) || double.IsNaN(loB)) continue;
            if (hiA - loA <= tol && hiB - loB <= tol) continue;

            // xlo(t) = a + b·t and xhi(t) = c + e·t, both affine inside the strip.
            double b = (loB - loA) / (tb - ta), a = loA - b * ta;
            double e = (hiB - hiA) / (tb - ta), c = hiA - e * ta;

            double outerA = sharedIsHigh ? a : c;
            double outerB = sharedIsHigh ? b : e;
            double sign   = sharedIsHigh ? 1.0 : -1.0;

            // Anchored: the FAR boundary is the shared gridline all the way along the strip.
            double farA = sharedIsHigh ? hiA : loA;
            double farB = sharedIsHigh ? hiB : loB;
            bool stripAnchored = Math.Abs(farA - sharedCoord) <= tol && Math.Abs(farB - sharedCoord) <= tol;
            anchored &= stripAnchored;
            if (stripAnchored) faceLen += tb - ta;

            EmPoint P(double along, double across) =>
                alongX ? new EmPoint(along, across) : new EmPoint(across, along);

            var ring = new[] { P(loA, ta), P(hiA, ta), P(hiB, tb), P(loB, tb) };
            // For Y flow the four points above wind the other way, because the roles of the axes are
            // swapped; PolygonIntegrals is signed, so the winding has to be right rather than absorbed.
            if (!alongX) Array.Reverse(ring);

            // w = sign·(flow-coordinate − outer(transverse)), with outer(t) = outerA + outerB·t.
            strips.Add(alongX
                ? new WeightStrip(ring,  sign,             -sign * outerB, -sign * outerA)
                : new WeightStrip(ring, -sign * outerB,     sign,          -sign * outerA));

            area += 0.5 * ((hiA - loA) + (hiB - loB)) * (tb - ta);
        }

        return new RooftopSupport
        {
            Strips           = strips,
            Area             = area,
            Anchored         = anchored && strips.Count > 0,
            SharedFaceLength = faceLen,
            IsWholeRectangle = false,
            FlowSimple       = flowSimple,
        };
    }

    /// <summary>
    /// <b>Whether the region is flow-simple in one direction</b> — the predicate
    /// <see cref="SurfaceMesher"/> asks of a clipped cell, and the one <see cref="Build"/> reports on
    /// the support it actually produced. Both go through <see cref="Intervals"/>, so there is exactly
    /// one answer to "does the strip construction describe this region".
    /// </summary>
    public static bool IsFlowSimple(PlanarCellRegion region, bool alongX, double tol)
    {
        ArgumentNullException.ThrowIfNull(region);

        var breaks = new List<double>();
        foreach (var piece in region.Pieces)
            foreach (var v in piece) breaks.Add(alongX ? v.Y : v.X);
        breaks.Sort();

        for (int k = 0; k + 1 < breaks.Count; k++)
        {
            if (breaks[k + 1] - breaks[k] <= tol) continue;
            if (Intervals(region, alongX, 0.5 * (breaks[k] + breaks[k + 1]), tol) > 1) return false;
        }
        return true;
    }

    /// <summary>
    /// How many disjoint intervals the region's intersection with the line at transverse coordinate
    /// <paramref name="at"/> consists of.
    ///
    /// <para>The crossings are collected with a HALF-OPEN rule and a winding count rather than by
    /// sorting raw hits, because both other readings are wrong on geometry this mesher actually
    /// produces: a vertex sitting exactly on the line would be counted twice by a closed rule, and
    /// the doubled-back chains Sutherland–Hodgman leaves when it clips a non-convex subject carry
    /// two coincident crossings of OPPOSITE sign, which cancel in a winding count and do not in a
    /// hit count. Intervals separated by less than <paramref name="tol"/> are merged — that is the
    /// merged cell's own shared face, where two pieces abut and the union is connected.</para>
    /// </summary>
    private static int Intervals(PlanarCellRegion region, bool alongX, double at, double tol)
    {
        var hits = new List<(double V, int Dir)>();
        foreach (var piece in region.Pieces)
            for (int i = 0, n = piece.Count, j = n - 1; i < n; j = i++)
            {
                double ca = alongX ? piece[j].Y : piece[j].X;
                double cb = alongX ? piece[i].Y : piece[i].X;
                double va = alongX ? piece[j].X : piece[j].Y;
                double vb = alongX ? piece[i].X : piece[i].Y;

                int dir = ca <= at && cb > at ? +1 : cb <= at && ca > at ? -1 : 0;
                if (dir == 0) continue;
                double t = (at - ca) / (cb - ca);
                hits.Add((va + t * (vb - va), dir));
            }

        if (hits.Count == 0) return 0;
        hits.Sort((p, q) => p.V.CompareTo(q.V));

        // Sweep to the covered intervals first, then merge — a gap shorter than tol is not a gap, it
        // is two pieces meeting on their shared face, which is exactly R-cut-3's merged cell.
        int count = 0, winding = 0;
        double start = 0, hi = double.NegativeInfinity;
        foreach (var (v, dir) in hits)
        {
            int before = winding;
            winding += dir;
            if (before == 0 && winding != 0) start = v;
            else if (before != 0 && winding == 0 && v - start > tol)
            {
                if (count == 0 || start - hi > tol) count++;
                hi = Math.Max(hi, v);
            }
        }
        return count;
    }

    /// <summary>
    /// <b>The cell's metal, tiled by the same trapezoids, carrying a UNIT weight</b> — the domain the
    /// DIVERGENCE PULSE is integrated over.
    ///
    /// <para>Shared with <see cref="Build"/> deliberately: the scalar block integrates ∇·f, which is a
    /// constant over the metal, and it must integrate it over exactly the region the ramp is
    /// integrated over or D4's signed cell-pair sum stops being the same object on both sides. Null
    /// for a whole rectangle, which is the fill's own fast path.</para>
    /// </summary>
    public static IReadOnlyList<WeightStrip>? Tiles(PlanarCell cell)
    {
        if (cell.Region is null) return null;

        // The DIRECTION is a free choice here — the tiles carry a unit weight, so only the domain
        // matters — but it is not an arbitrary one once M1 admits a cell that is flow-simple in one
        // direction only. Strips taken across the axis the outline crosses twice would span the gap
        // between the two runs, which is exactly the sin the predicate exists to catch, and it would
        // land on the DIVERGENCE PULSE's domain where nothing downstream would notice.
        var support = Build(cell, PlanarBasisDirection.X, sharedIsHigh: true, double.NaN);
        if (!support.FlowSimple)
            support = Build(cell, PlanarBasisDirection.Y, sharedIsHigh: true, double.NaN);

        var tiles = new WeightStrip[support.Strips.Count];
        for (int i = 0; i < tiles.Length; i++) tiles[i] = support.Strips[i] with { Alpha = 0, Beta = 0, Gamma = 1 };
        return tiles;
    }

    /// <summary>
    /// The region's own extent along the flow direction at one transverse coordinate — the union over
    /// the pieces, which for a merged cell is the sliver's outer boundary and the host's inner one.
    /// NaN when no piece reaches that coordinate.
    /// </summary>
    private static (double Lo, double Hi) Extent(PlanarCellRegion region, bool alongX,
                                                 double at, double tol)
    {
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        foreach (var piece in region.Pieces)
        {
            for (int i = 0, n = piece.Count, j = n - 1; i < n; j = i++)
            {
                double ca = alongX ? piece[j].Y : piece[j].X;
                double cb = alongX ? piece[i].Y : piece[i].X;
                double va = alongX ? piece[j].X : piece[j].Y;
                double vb = alongX ? piece[i].X : piece[i].Y;

                if (Math.Abs(ca - at) <= tol) { lo = Math.Min(lo, va); hi = Math.Max(hi, va); }
                if (Math.Abs(cb - at) <= tol) { lo = Math.Min(lo, vb); hi = Math.Max(hi, vb); }
                if (ca > at == cb > at) continue;
                double t = (at - ca) / (cb - ca);
                double v = va + t * (vb - va);
                lo = Math.Min(lo, v); hi = Math.Max(hi, v);
            }
        }
        return double.IsInfinity(lo) ? (double.NaN, double.NaN) : (lo, hi);
    }
}
