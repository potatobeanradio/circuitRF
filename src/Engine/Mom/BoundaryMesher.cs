using System.Numerics;

namespace CircuitRF.Engine.Mom;

/// <summary>
/// The boundary mesher (§10.5) — <b>one dimension, not two</b>: segments along conductor perimeters
/// and along dielectric interfaces.
///
/// <para><b>R-mom-8. Edge grading is geometric, from both ends of every conductor face, and it
/// applies to dielectric-interface segments near a conductor edge too.</b> The 1/√d current
/// singularity at a conductor edge has a bound-charge counterpart in the interface directly beside
/// it; grading only the metal leaves the larger error un-addressed. The grading is therefore
/// written against <i>segment geometry</i> — a cell-size field over a set of attractor points —
/// and not against "the microstrip case", so kernels B and C reuse it verbatim.</para>
///
/// <para>The size field is <c>h(x) = min over attractors a of [c₀ + (r−1)·|x − a|]</c>, clamped to
/// <c>h_max</c>. That linear form is <i>exactly</i> the geometric progression c₀, c₀r, c₀r², …:
/// cell k of that progression starts at distance d_k = c₀(rᵏ−1)/(r−1) and has size
/// c₀rᵏ = c₀ + (r−1)d_k. Stating it as a field rather than as a per-end loop is what makes it
/// compose — any number of attractors, both-ended intervals, and conductor edges projected onto a
/// dielectric interface all fall out of the same three lines.</para>
///
/// <para><b>R-mom-9.</b> Dielectric-interface segments are excluded wherever the interface lies
/// inside <i>or on</i> a conductor. The microstrip strip sits <i>on</i> the substrate: its bottom
/// face carries free charge and the interface beneath it does not exist. Two unknowns on the same
/// physical surface make the matrix singular.</para>
///
/// <para><b>R-mom-10.</b> The interface truncation distance is <see cref="EmMeshSettings.TruncationHeights"/>
/// substrate heights beyond the outermost conductor on each side, with a geometrically graded tail
/// — an explicit setting reported back in <see cref="EmMeshReport.TruncationHalfExtent"/>, never a
/// hidden constant.</para>
/// </summary>
public static class BoundaryMesher
{
    /// <summary>
    /// Mesh a problem. Assumes <see cref="QuasiStaticKernel.CanSolve"/> has already accepted it;
    /// it is defensive about degenerate geometry only to the extent needed not to divide by zero.
    /// </summary>
    /// <summary>Kernel A's own default: the existing <c>{v:G4} m</c> spelling, byte for byte, so
    /// every caller that passes no formatter keeps the text it always had.</summary>
    private static string DefaultLengthFormat(double v) =>
        v.ToString("G4", System.Globalization.CultureInfo.InvariantCulture) + " m";

    /// <param name="lengthFormat">Owner request, 2026-08-15 — every distance this report's notes
    /// quote goes through this. <c>null</c> is kernel A's own pre-existing <c>{v:G4} m</c> text; a UI
    /// caller with a layout open supplies one that reads in the layout's own display unit instead.
    /// See <see cref="SurfaceMesher.PlanarLengthFormat"/> — the same delegate type, shared with kernel
    /// B rather than a second one invented for this file.</param>
    public static EmMeshReport Mesh(EmProblem problem, EmMeshSettings settings,
                                    SurfaceMesher.PlanarLengthFormat? lengthFormat = null)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(settings);
        var fmt = lengthFormat ?? DefaultLengthFormat;

        var outlines = new List<IReadOnlyList<EmPoint>>(problem.Conductors.Count);
        var names    = new List<string>(problem.Conductors.Count);
        foreach (var c in problem.Conductors)
        {
            outlines.Add(Polygon2D.AsCcw(c.Outline));
            names.Add(c.Name);
        }

        var (x0, y0, x1, y1) = UnionBounds(outlines);
        double scale = Math.Max(Math.Max(x1 - x0, y1 - y0), 1e-12);
        double tol   = 1e-9 * scale;

        // Substrate height: the reference length the truncation extent is quoted in. With a ground
        // plane that is the plane-to-lowest-conductor distance; without one there is no substrate,
        // so the conductor bounding box is the only available scale.
        double hRef = problem.Ground is not null
            ? Math.Max(y0 - problem.Ground.Y, 1e-12)
            : scale;

        var notes = new List<string>();

        // ── conductor perimeters ──────────────────────────────────────────────────────────────
        var interfaceYs = InterfaceYs(problem, tol, notes, fmt);
        var template    = BuildConductorTemplate(outlines, settings, interfaceYs, tol);
        var segments    = new List<EmSegment>();
        segments.AddRange(BuildConductorSegments(outlines, template,
            (mid, outward) => problem.EpsAt(RegionProbeY(mid, outward, tol))));

        var perConductor = new int[outlines.Count];
        foreach (var s in segments) perConductor[s.ConductorIndex]++;

        // ── dielectric interfaces ─────────────────────────────────────────────────────────────
        double truncation = settings.TruncationHeights * hRef;
        double featureMin = double.MaxValue;
        foreach (var o in outlines) featureMin = Math.Min(featureMin, EdgeReference(o));
        if (double.IsInfinity(featureMin)) featureMin = scale;

        var attractors = new List<double>();
        foreach (var o in outlines)
        {
            var (cx0, _, cx1, _) = Polygon2D.Bounds(o);
            attractors.Add(cx0);
            attractors.Add(cx1);
        }

        // The interface cell size has TWO scales, and conflating them is what makes a narrow strip
        // come out wrong. Near a conductor edge the bound charge is singular and the scale is the
        // metal's own feature size (ifaceC0, graded geometrically). Away from it, the microstrip
        // field decays on the scale of the SUBSTRATE HEIGHT, so the cap out to `nearExtent` is
        // hRef/MinCellsAcrossWidth — not a fraction of the truncation length, which for a narrow
        // strip is a completely unrelated number. Only beyond nearExtent does the cap itself grow,
        // geometrically, so the truncated tail costs exactly TruncationTailCells cells.
        double ifaceC0   = settings.EdgeFractionOfWidth * featureMin;
        double growth    = Math.Max(settings.EdgeGrowthRatio - 1.0, 1e-6);
        double hNear     = hRef / Math.Max(1, settings.MinCellsAcrossWidth);
        double nearExtent = 2.0 * hRef;
        double tailSlope = GeometricSlopeFor(hNear, Math.Max(truncation - nearExtent, hNear),
                                             settings.TruncationTailCells);

        var perInterface = new List<int>();
        foreach (double iy in interfaceYs)
        {
            int before = segments.Count;
            AddInterfaceSegments(problem, outlines, segments, iy,
                x0 - truncation, x1 + truncation, attractors,
                ifaceC0, growth, hNear, nearExtent, tailSlope, tol, perInterface.Count);
            perInterface.Add(segments.Count - before);
        }

        // ── report ────────────────────────────────────────────────────────────────────────────
        double minCell = double.MaxValue, maxCell = 0;
        foreach (var s in segments)
        {
            double len = s.Length;
            if (len < minCell) minCell = len;
            if (len > maxCell) maxCell = len;
        }
        if (segments.Count == 0) minCell = 0;

        var wheeler = new double[problem.Conductors.Count];
        for (int i = 0; i < problem.Conductors.Count; i++)
        {
            var (cx0, cy0, cx1, cy1) = Polygon2D.Bounds(outlines[i]);
            double t = Math.Max(Math.Min(cx1 - cx0, cy1 - cy0), 1e-15);
            wheeler[i] = WheelerCrossoverHz(t, problem.Conductors[i].SigmaSm);
            notes.Add($"Conductor '{problem.Conductors[i].Name}': Wheeler's incremental-inductance rule " +
                      $"assumes skin depth ≪ metal thickness; δ = t/2 at {wheeler[i]:G4} Hz. Below that, R is " +
                      $"carried by the DC floor, not by skin effect.");
        }

        notes.Add($"Dielectric interfaces truncated {settings.TruncationHeights:G3} substrate heights " +
                  $"({fmt(truncation)}) beyond the outermost conductor on each side.");

        var mesh = new EmMesh(segments, names, problem.Ground);
        return new EmMeshReport(
            mesh, segments.Count, perConductor, perInterface, interfaceYs,
            minCell, maxCell, truncation, wheeler, template, notes);
    }

    /// <summary>R-mom-13: f at which δ = t/2, i.e. f = 4/(π·t²·µ₀·σ).</summary>
    public static double WheelerCrossoverHz(double thicknessM, double sigmaSm)
        => double.IsPositiveInfinity(sigmaSm) || sigmaSm <= 0
            ? 0.0
            : 4.0 / (Math.PI * thicknessM * thicknessM * EmConstants.Mu0 * sigmaSm);

    // ── conductor-only meshing (also the path the oracles and the Wheeler perturbation use) ────

    /// <summary>
    /// A template that puts <paramref name="cellsPerEdge"/> equal cells on every polygon edge —
    /// the "trivial uniform mesher". The closed-form Tier 1/2 oracles (coax, wire over ground, two
    /// wires) are circles approximated by regular polygons, where uniform <i>is</i> the correct
    /// mesh, so they exercise <see cref="ChargeSolver"/> without depending on the grading logic.
    /// </summary>
    public static ConductorMeshTemplate UniformTemplate(
        IReadOnlyList<IReadOnlyList<EmPoint>> outlines, int cellsPerEdge = 1)
    {
        var all = new List<IReadOnlyList<double[]>>(outlines.Count);
        foreach (var poly in outlines)
        {
            var edges = new List<double[]>(poly.Count);
            for (int e = 0; e < poly.Count; e++)
            {
                var f = new double[cellsPerEdge];
                for (int k = 0; k < cellsPerEdge; k++) f[k] = (k + 1.0) / cellsPerEdge;
                edges.Add(f);
            }
            all.Add(edges);
        }
        return new ConductorMeshTemplate(all);
    }

    /// <summary>
    /// Conductor perimeters only, no dielectric interfaces — what an air-filled [C₀] solve and the
    /// Wheeler perturbation both need, and what the closed-form oracles are built from.
    /// </summary>
    public static EmMesh ConductorsOnly(
        IReadOnlyList<IReadOnlyList<EmPoint>> outlines,
        IReadOnlyList<string>                 names,
        ConductorMeshTemplate                 template,
        EmGroundPlane?                        ground,
        Func<EmPoint, EmPoint, Complex>?      epsOutside = null)
    {
        var segs = BuildConductorSegments(outlines, template, epsOutside ?? ((_, _) => Complex.One));
        return new EmMesh(segs, names, ground);
    }

    private static List<EmSegment> BuildConductorSegments(
        IReadOnlyList<IReadOnlyList<EmPoint>> outlines,
        ConductorMeshTemplate                 template,
        Func<EmPoint, EmPoint, Complex>       epsOutside)
    {
        var segs = new List<EmSegment>();
        for (int c = 0; c < outlines.Count; c++)
        {
            var poly = outlines[c];
            var frs  = template.EdgeFractions[c];
            for (int e = 0; e < poly.Count; e++)
            {
                var a = poly[e];
                var b = poly[(e + 1) % poly.Count];
                var d = b - a;
                double len = d.Norm;
                if (len <= 0) continue;
                // CCW winding ⇒ the RIGHT normal points out of the metal.
                var outward = (d * (1.0 / len)).RightNormal;

                double prev = 0;
                foreach (double f in frs[e])
                {
                    var pa = a + d * prev;
                    var pb = a + d * f;
                    prev = f;
                    if ((pb - pa).Norm <= 0) continue;
                    var mid = new EmPoint(0.5 * (pa.X + pb.X), 0.5 * (pa.Y + pb.Y));
                    segs.Add(new EmSegment(pa, pb, outward, EmSegmentKind.Conductor,
                                           c, -1, epsOutside(mid, outward), Complex.Zero));
                }
            }
        }
        return segs;
    }

    // ── grading ───────────────────────────────────────────────────────────────────────────────

    private static ConductorMeshTemplate BuildConductorTemplate(
        IReadOnlyList<IReadOnlyList<EmPoint>> outlines,
        EmMeshSettings                        settings,
        IReadOnlyList<double>                 interfaceYs,
        double                                tol)
    {
        double growth = Math.Max(settings.EdgeGrowthRatio - 1.0, 1e-6);
        var all = new List<IReadOnlyList<double[]>>(outlines.Count);

        foreach (var poly in outlines)
        {
            var (cx0, _, cx1, _) = Polygon2D.Bounds(poly);
            double wRef = Math.Max(cx1 - cx0, 1e-12);
            double hMax = wRef / Math.Max(1, settings.MinCellsAcrossWidth);
            double eRef = EdgeReference(poly);

            var edges = new List<double[]>(poly.Count);
            for (int e = 0; e < poly.Count; e++)
            {
                var a = poly[e];
                var b = poly[(e + 1) % poly.Count];
                double len = (b - a).Norm;
                if (len <= 0) { edges.Add([1.0]); continue; }

                double c0 = Math.Min(settings.EdgeFractionOfWidth * eRef,
                                     len / (2.0 * Math.Max(1, settings.EdgeCells)));
                c0 = Math.Max(c0, len * 1e-6);

                double[] att = [0.0, len];
                var fr = PartitionFractions(len, s => SizeAt(s, att, c0, growth, hMax), minCells: 2);

                // Force a break wherever the edge crosses a dielectric interface, so no single
                // segment straddles two regions and has an ambiguous outward permittivity.
                fr = MergeForcedFractions(fr, ForcedCrossings(a, b, interfaceYs, len), tol / Math.Max(len, tol));
                edges.Add(fr);
            }
            all.Add(edges);
        }
        return new ConductorMeshTemplate(all);
    }

    private static List<double> ForcedCrossings(EmPoint a, EmPoint b, IReadOnlyList<double> ys, double len)
    {
        var res = new List<double>();
        double dy = b.Y - a.Y;
        if (Math.Abs(dy) <= 0 || len <= 0) return res;
        foreach (double y in ys)
        {
            double t = (y - a.Y) / dy;
            if (t > 1e-9 && t < 1.0 - 1e-9) res.Add(t);
        }
        return res;
    }

    private static double[] MergeForcedFractions(double[] fr, List<double> forced, double relTol)
    {
        if (forced.Count == 0) return fr;
        var set = new List<double>(fr);
        set.AddRange(forced);
        set.Sort();
        var outp = new List<double>(set.Count);
        foreach (double v in set)
        {
            if (v <= 0) continue;
            if (outp.Count > 0 && v - outp[^1] <= Math.Max(relTol, 1e-12)) continue;
            outp.Add(Math.Min(v, 1.0));
        }
        if (outp.Count == 0 || outp[^1] < 1.0) outp.Add(1.0);
        outp[^1] = 1.0;
        return [.. outp];
    }

    /// <summary>
    /// h(x) = min over attractors of [c₀ + (r−1)|x − a|], clamped to h_max — see the class remarks
    /// for why this linear form <i>is</i> the geometric progression.
    /// </summary>
    private static double SizeAt(double x, IReadOnlyList<double> attractors,
                                 double c0, double growth, double hMax)
    {
        double best = hMax;
        for (int i = 0; i < attractors.Count; i++)
        {
            double h = c0 + growth * Math.Abs(x - attractors[i]);
            if (h < best) best = h;
        }
        return Math.Max(best, 1e-18);
    }

    /// <summary>
    /// Walk an interval placing cells of the local target size, then rescale so the last cell lands
    /// exactly on the end. Returns cumulative fractions in (0, 1], last element exactly 1.
    /// </summary>
    public static double[] PartitionFractions(double length, Func<double, double> sizeAt, int minCells)
    {
        var xs = new List<double>();
        double x = 0;
        int guard = 0;
        while (x < length && guard++ < 20_000)
        {
            double s = sizeAt(x);
            if (!(s > 0)) break;
            s = Math.Min(s, sizeAt(Math.Min(length, x + 0.5 * s)));
            if (!(s > 0)) break;
            x += s;
            xs.Add(x);
        }
        if (xs.Count == 0) xs.Add(length);

        double last = xs[^1];
        if (last > 0)
            for (int i = 0; i < xs.Count; i++) xs[i] = xs[i] * length / last;

        if (xs.Count < minCells)
        {
            xs.Clear();
            for (int i = 1; i <= minCells; i++) xs.Add(length * i / minCells);
        }

        var fr = new double[xs.Count];
        for (int i = 0; i < xs.Count; i++) fr[i] = xs[i] / length;
        fr[^1] = 1.0;
        return fr;
    }

    // ── interfaces ────────────────────────────────────────────────────────────────────────────

    private static List<double> InterfaceYs(EmProblem problem, double tol, List<string> notes,
                                            SurfaceMesher.PlanarLengthFormat fmt)
    {
        var ys = new List<double>();
        for (int i = 0; i + 1 < problem.Regions.Count; i++)
        {
            var below = problem.Regions[i];
            var above = problem.Regions[i + 1];
            if (below.Material.EpsComplex == above.Material.EpsComplex) continue;
            double y = below.YTop;
            if (double.IsInfinity(y)) continue;
            // R-mom-7: a dielectric interface coincident with the ground plane is not an interface.
            // The image already enforces φ = 0 there exactly, dielectrics included.
            if (problem.Ground is not null && Math.Abs(y - problem.Ground.Y) <= tol)
            {
                notes.Add($"Dielectric interface at y = {fmt(y)} coincides with the ground plane and was " +
                          "dropped — the exact image already enforces φ = 0 there.");
                continue;
            }
            ys.Add(y);
        }
        return ys;
    }

    private static void AddInterfaceSegments(
        EmProblem problem,
        IReadOnlyList<IReadOnlyList<EmPoint>> outlines,
        List<EmSegment> segments,
        double y, double xLo, double xHi,
        IReadOnlyList<double> attractors,
        double c0, double growth, double hNear, double nearExtent, double tailSlope,
        double tol, int interfaceIndex)
    {
        // R-mom-9: drop everything inside or on a conductor.
        var excluded = new List<(double X0, double X1)>();
        foreach (var o in outlines)
            excluded.AddRange(Polygon2D.HorizontalFootprint(o, y, tol));
        excluded.Sort((p, q) => p.X0.CompareTo(q.X0));

        var merged = new List<(double X0, double X1)>();
        foreach (var s in excluded)
        {
            if (merged.Count > 0 && s.X0 <= merged[^1].X1 + tol)
                merged[^1] = (merged[^1].X0, Math.Max(merged[^1].X1, s.X1));
            else merged.Add(s);
        }

        var allowed = new List<(double X0, double X1)>();
        double cursor = xLo;
        foreach (var s in merged)
        {
            double a = Math.Max(s.X0, xLo);
            double b = Math.Min(s.X1, xHi);
            if (b <= cursor) continue;
            if (a > cursor) allowed.Add((cursor, a));
            cursor = Math.Max(cursor, b);
        }
        if (cursor < xHi) allowed.Add((cursor, xHi));

        // Conductor footprint edges are attractors too — the bound charge crowds right beside the
        // metal edge exactly as the free charge crowds on it (R-mom-8).
        var att = new List<double>(attractors);
        foreach (var s in merged) { att.Add(s.X0); att.Add(s.X1); }

        var epsBelow = problem.EpsAt(y - tol);
        var epsAbove = problem.EpsAt(y + tol);
        var k = (epsBelow - epsAbove) / (epsBelow + epsAbove);
        var up = new EmPoint(0, 1);

        double IfaceSize(double x)
        {
            double d = double.MaxValue;
            for (int i = 0; i < att.Count; i++) d = Math.Min(d, Math.Abs(x - att[i]));
            double near = c0 + growth * d;
            double cap  = hNear + tailSlope * Math.Max(0, d - nearExtent);
            return Math.Max(Math.Min(near, cap), 1e-18);
        }

        foreach (var (p, q) in allowed)
        {
            double len = q - p;
            if (len <= tol) continue;
            var fr = PartitionFractions(len, s => IfaceSize(p + s), minCells: 3);
            double prev = 0;
            foreach (double f in fr)
            {
                var a = new EmPoint(p + prev * len, y);
                var b = new EmPoint(p + f * len, y);
                prev = f;
                if (b.X - a.X <= 0) continue;
                segments.Add(new EmSegment(a, b, up, EmSegmentKind.DielectricInterface,
                                           -1, interfaceIndex, Complex.One, k));
            }
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The growth slope g = R − 1 of a geometric progression that starts at
    /// <paramref name="first"/> and covers <paramref name="length"/> in exactly
    /// <paramref name="cells"/> cells: first·(Rⁿ−1)/(R−1) = length. Solved by bisection because the
    /// closed form does not exist; n is 12 by default, so this costs nothing.
    /// </summary>
    public static double GeometricSlopeFor(double first, double length, int cells)
    {
        int n = Math.Max(1, cells);
        if (first * n >= length) return 1e-6;                 // already enough cells at constant size
        double lo = 1.0 + 1e-9, hi = 100.0;
        for (int it = 0; it < 200; it++)
        {
            double r = 0.5 * (lo + hi);
            double sum = first * (Math.Pow(r, n) - 1.0) / (r - 1.0);
            if (sum < length) lo = r; else hi = r;
        }
        return 0.5 * (lo + hi) - 1.0;
    }

    /// <summary>
    /// The reference length the outermost (edge) cell is a fraction of: the conductor's
    /// <b>smallest</b> bounding-box dimension, not its width.
    ///
    /// <para>§10.5 phrases the edge cell as "a small fraction of the width (~2–5%)", which is the
    /// right rule for a conductor whose width and thickness are comparable. For a rolled foil it is
    /// not: the charge singularity lives at the 90° metal <i>corner</i>, and the corner's own scale
    /// is the thickness. On 1.6 mm FR-4 with W = 2.9 mm and t = 35 µm, 3% of the width is 87 µm —
    /// larger than the entire side face — and the mesh cannot see the corner at all. Measured: with
    /// the width reference, ε_eff converges as N^−½ and sits 4% low at any affordable N; with the
    /// thickness reference it is within 0.1% of its own converged limit at N ≈ 150. Same rule, same
    /// 3%, correct reference length — and for a square conductor the two coincide, which is the
    /// case the "% of width" phrasing was written for.</para>
    /// </summary>
    public static double EdgeReference(IReadOnlyList<EmPoint> poly)
    {
        var (x0, y0, x1, y1) = Polygon2D.Bounds(poly);
        return Math.Max(Math.Min(x1 - x0, y1 - y0), 1e-15);
    }

    private static double RegionProbeY(EmPoint mid, EmPoint outward, double tol)
        => Math.Abs(outward.Y) > 1e-12 ? mid.Y + Math.Sign(outward.Y) * tol : mid.Y;

    internal static (double X0, double Y0, double X1, double Y1) UnionBounds(
        IReadOnlyList<IReadOnlyList<EmPoint>> outlines)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var o in outlines)
        {
            var (a, b, c, d) = Polygon2D.Bounds(o);
            x0 = Math.Min(x0, a); y0 = Math.Min(y0, b);
            x1 = Math.Max(x1, c); y1 = Math.Max(y1, d);
        }
        return (x0, y0, x1, y1);
    }
}
