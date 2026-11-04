// CONDUCTOR THICKNESS IN A PLANAR RUN — THE SHEET-EQUIVALENT WIDTH.
//
// The planar kernel's metal is a zero-thickness SHEET (PlanarProblem's own header: "the surface mesher
// is a sheet mesher and never uses" a level's thickness for geometry). Thickness reaches it only
// through the surface impedance, i.e. through LOSS. A real strip of thickness t also has two
// SIDEWALLS, and the fringing field they carry adds capacitance a sheet does not have, so a sheet of
// the drawn width reads a HIGHER Z0 than the copper it stands for. Measured against independent
// solvers on a 25 µm copper line: 54.35 Ω where a 3D FEM solve, a 2.5D MoM with thick-conductor
// modelling, and the closed-form models all read 49.4-50.0 Ω.
//
// THE CORRECTION is the classical one — an effective-width offset, the same idea as the
// Hammerstad-Jensen thickness correction the MLIN model already applies — but the offset is not taken
// from a closed form. It is SOLVED, per level, with the 2-D cross-section kernel (kernel A) on this
// technology's own stackup:
//
//     find W′ such that  Z0_sheet(W′) = Z0_thick(W),   ΔW = W′ − W
//
// where Z0_thick is kernel A's answer for the real t-thick strip and Z0_sheet its answer for a thin
// strip in the sheet's own position. Kernel A cannot mesh a truly zero-thickness strip (its edge cell
// is a fraction of the smallest dimension — Mom/CLAUDE.md's "absurdly thin strip" trap), so the
// reference sheet is t/20 thick and the residual width THAT sheet itself carries is added back from
// the Hammerstad-Jensen thin-strip formula, where it is a few per cent of the answer.
//
// Z0 is the quantity matched, deliberately: it is what moves a match and what was measured wrong. One
// width cannot also match ε_eff (a thick strip's sidewalls are partly in air, so its ε_eff is a little
// LOWER than a sheet's); the residual ε_eff difference is reported, not hidden.
//
// THEN every outline on the level grows by ΔW/2 along its outward normal — except the edges a port
// label sits on, which stay exactly where they are drawn, so no reference plane moves and the port's
// cross-section is the corrected width. Holes shrink by the same amount, and a gap between two
// conductors on the level narrows by ΔW, which is the right direction for the extra sidewall coupling
// of thick metal.
//
// DECLINED, BY NAME, RATHER THAN APPLIED BADLY: a level where growing would close a gap or a slot (two
// conductors, or two parts of one, closer than ΔW) is left as drawn with a warning — merging them would
// short two nets, which is not an approximation of anything.

using Clipper2Lib;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Design.Layout.Em;

/// <summary>What one level's calibration found. Lengths in metres, impedances in ohms.</summary>
/// <param name="WidthM">The width the calibration was taken at.</param>
/// <param name="ThicknessM">The level's copper thickness.</param>
/// <param name="DeltaWM">The total width added (ΔW); each outline grows by half of it per side.</param>
/// <param name="Z0Thick">Kernel A's Z0 of the real thick strip at <paramref name="WidthM"/>.</param>
/// <param name="EeffThick">…and its static ε_eff.</param>
/// <param name="Z0SheetDrawn">Z0 of the sheet at the DRAWN width — what the planar run reads without
/// the correction.</param>
/// <param name="EeffSheetCorrected">ε_eff of the sheet at the corrected width.</param>
public sealed record PlanarThicknessCalibration(
    double WidthM, double ThicknessM, double DeltaWM,
    double Z0Thick, double EeffThick, double Z0SheetDrawn, double EeffSheetCorrected);

/// <summary>
/// The planar run's conductor-thickness correction — see the file header for the method, why Z0 is
/// the matched quantity, and what is declined.
/// </summary>
public static class PlanarThicknessCorrection
{
    /// <summary>The kernel-A reference sheet is this fraction of the real thickness.</summary>
    public const double ReferenceSheetFraction = 1.0 / 20.0;

    /// <summary>A dielectric thinner than this fraction of the strip's width is taken as the material below it when
    /// calibrating on a film-level stack. A judgement and not a measured boundary: the 2-D kernel reads a thick strip below a
    /// sheet at every layer measured under 73 µm, up to 5 µm, which is 6.8 % of it. A thicker layer declines as before.</summary>
    public const double ThinLayerFractionOfWidth = 0.05;

    /// <summary>Root-find tolerance on Z0, relative.</summary>
    public const double Z0Tolerance = 1e-5;

    /// <summary>
    /// Dielectric regions for a cross-section solve, in the frame whose y = 0 is the ground plane:
    /// <paramref name="dielectricsBottomUp"/> up to <paramref name="topCutM"/> (the topmost analysis
    /// level's sheet — the planar medium stops there and is air above, so a cover layer is out of the
    /// calibration exactly as it is out of the solve), each absorbing the conductor bands beneath it,
    /// air above, and the lowest region extended to −∞ (the ground plane shields everything below).
    /// </summary>
    public static IReadOnlyList<EmDielectricRegion> RegionsFor(
        IReadOnlyList<(double BottomM, double TopM, EmMaterial Material)> dielectricsBottomUp,
        double stackBottomM, double groundM, double topCutM)
    {
        var raw = new List<(double Bottom, double Top, EmMaterial Mat)>();
        double prev = stackBottomM;
        foreach (var (bottom, top, mat) in dielectricsBottomUp)
        {
            if (bottom >= topCutM - 1e-15) break;
            raw.Add((prev - groundM, top - groundM, mat));
            prev = top;
        }
        raw.Add((prev - groundM, double.PositiveInfinity, EmMaterial.Air));

        var merged = new List<(double Bottom, double Top, EmMaterial Mat)>();
        foreach (var r in raw)
        {
            if (merged.Count > 0 && merged[^1].Mat == r.Mat) merged[^1] = (merged[^1].Bottom, r.Top, r.Mat);
            else merged.Add(r);
        }

        int first = 0;
        for (int i = 0; i < merged.Count; i++)
        {
            first = i;
            if (merged[i].Top > 0) break;
        }
        var regions = new List<EmDielectricRegion>();
        for (int i = first; i < merged.Count; i++)
            regions.Add(new EmDielectricRegion(i == first ? double.NegativeInfinity : merged[i].Bottom,
                                               merged[i].Top, merged[i].Mat));
        return regions;
    }

    /// <summary>
    /// ΔW for a strip of width <paramref name="widthM"/> occupying [<paramref name="stripBottomM"/>,
    /// <paramref name="stripTopM"/>] above the ground plane, whose planar sheet sits at
    /// <paramref name="sheetM"/> (its bottom or top surface). Null with a reason when kernel A cannot
    /// answer.
    /// </summary>
    public static (PlanarThicknessCalibration? Calibration, string? Refusal) Calibrate(
        IReadOnlyList<EmDielectricRegion> regions, double stripBottomM, double stripTopM, double sheetM,
        double widthM, CancellationToken ct = default)
    {
        double t = stripTopM - stripBottomM;
        if (!(t > 0) || !(widthM > 0))
            return (null, "the level has no thickness or the reference width is not positive");
        if (!(stripBottomM > 0))
            return (null, "the level does not sit above the return plane");

        try
        {
            var (z0Thick, eThick) = Solve(regions, widthM, stripBottomM, stripTopM);
            if (!double.IsFinite(z0Thick)) return (null, "the 2-D cross-section solve found no capacitance");

            // The reference sheet sits ON the planar sheet's surface, inside the real strip's band.
            double ts = t * ReferenceSheetFraction;
            bool onTop = Math.Abs(sheetM - stripTopM) < Math.Abs(sheetM - stripBottomM);
            double y0 = onTop ? sheetM - ts : sheetM, y1 = onTop ? sheetM : sheetM + ts;
            double Z0Sheet(double w) => Solve(regions, w, y0, y1).Z0;

            double z0Drawn = Z0Sheet(widthM);
            if (!double.IsFinite(z0Drawn)) return (null, "the 2-D cross-section solve found no capacitance");
            // Z0 falls monotonically with width, so a bracket is [W, W + something] once found.
            double a = widthM, fa = z0Drawn - z0Thick;
            if (!(fa > 0))
                return (null, $"a sheet of the drawn width already reads {z0Drawn:F2} Ω against the thick strip's " +
                              $"{z0Thick:F2} Ω, so there is no width offset to find");
            double step = Math.Max(2 * t, 0.05 * widthM), b = widthM + step, fb = Z0Sheet(b) - z0Thick;
            for (int k = 0; fb > 0 && k < 8; k++)
            {
                ct.ThrowIfCancellationRequested();
                a = b; fa = fb; step *= 2; b = widthM + step; fb = Z0Sheet(b) - z0Thick;
            }
            if (!(fb < 0)) return (null, "no width up to several times the thickness matches the thick strip's Z0");

            // Illinois regula falsi on the bracket [a, b] with f(a) > 0 > f(b): superlinear, never
            // leaves the bracket, and halves the stale end's weight when one side keeps moving.
            double w = b;
            for (int it = 0, side = 0; it < 40; it++)
            {
                ct.ThrowIfCancellationRequested();
                w = (a * fb - b * fa) / (fb - fa);
                double fw = Z0Sheet(w) - z0Thick;
                if (!double.IsFinite(fw)) return (null, "the 2-D cross-section solve found no capacitance");
                if (Math.Abs(fw) <= Z0Tolerance * z0Thick || b - a <= 1e-6 * widthM) break;
                if (fw > 0) { a = w; fa = fw; if (side == -1) fb *= 0.5; side = -1; }
                else        { b = w; fb = fw; if (side ==  1) fa *= 0.5; side =  1; }
            }

            // The reference sheet is t/20 thick, not zero, and is itself effectively wider than a true
            // sheet by its own (small) thickness correction — added back from the closed form.
            double h = sheetM, epsR = regions.First(r => r.YTop > h).Material.EpsR;
            HammerstadJensen.ThicknessCorrectedWidths(w / h, ts / h, Math.Max(epsR, 1.0), out double u1, out _);
            double residual = (u1 - w / h) * h;

            double eSheet = Solve(regions, w, y0, y1).Eeff;
            return (new PlanarThicknessCalibration(widthM, t, w - widthM + residual,
                                                   z0Thick, eThick, z0Drawn, eSheet), null);
        }
        catch (InvalidOperationException ex)
        {
            return (null, $"the 2-D cross-section solve refused: {ex.Message}");
        }
    }

    private static (double Z0, double Eeff) Solve(IReadOnlyList<EmDielectricRegion> regions,
                                                  double widthM, double y0, double y1)
    {
        var strip = new EmConductor("strip",
        [
            new EmPoint(-0.5 * widthM, y0), new EmPoint(0.5 * widthM, y0),
            new EmPoint(0.5 * widthM, y1),  new EmPoint(-0.5 * widthM, y1),
        ], double.PositiveInfinity);
        var problem = new EmProblem([strip], regions, new EmGroundPlane(0, double.PositiveInfinity), [], widthM);
        var mesh = BoundaryMesher.Mesh(problem, EmMeshSettings.Default).Mesh;
        double c  = ChargeSolver.MaxwellCapacitance(mesh)[0, 0].Real;
        double c0 = ChargeSolver.MaxwellCapacitance(ChargeSolver.AirFilled(mesh))[0, 0].Real;
        // No capacitance is NaN, not an exception: Calibrate turns a non-finite Z0 into its refusal.
        if (!(c > 0) || !(c0 > 0)) return (double.NaN, double.NaN);
        return (1.0 / (EmConstants.C0 * Math.Sqrt(c * c0)), c / c0);
    }

    /// <summary>
    /// The width a level's calibration is taken at: the median length of the port edges on it (the
    /// line a port measures is the line that matters most), else the median 2·area/perimeter of its
    /// polygons — a long strip's width. ΔW depends only weakly on W, which is what makes one value per
    /// level adequate.
    /// </summary>
    public static double ReferenceWidth(IReadOnlyList<PlanarPolygon> polygons, IReadOnlyList<EmPoint> portPoints,
                                        out bool fromPorts)
    {
        var portWidths = new List<double>();
        foreach (var poly in polygons)
            foreach (var (a, b) in Edges(poly.Outer))
                if (portPoints.Any(p => OnEdge(p, a, b))) portWidths.Add(Dist(a, b));
        fromPorts = portWidths.Count > 0;
        if (fromPorts) return Median(portWidths);

        var widths = new List<double>();
        foreach (var poly in polygons)
        {
            double perimeter = Edges(poly.Outer).Sum(e => Dist(e.A, e.B))
                             + poly.HoleRings.Sum(h => Edges(h).Sum(e => Dist(e.A, e.B)));
            if (perimeter > 0) widths.Add(2 * poly.Area() / perimeter);
        }
        return widths.Count > 0 ? Median(widths) : 0;
    }

    /// <summary>
    /// Grows every polygon by <paramref name="deltaWM"/>/2 along its outward normal, holding each edge
    /// a port label sits on exactly where it is drawn. Declined (null and a reason) when growing would
    /// close a gap between polygons or a slot or hole within one.
    /// </summary>
    public static (IReadOnlyList<PlanarPolygon>? Grown, string? Decline, int HeldEdges) Grow(
        IReadOnlyList<PlanarPolygon> polygons, double deltaWM, IReadOnlyList<EmPoint> portPoints)
    {
        if (!(deltaWM > 0) || polygons.Count == 0) return (polygons, null, 0);
        double d = 0.5 * deltaWM;
        var result = new List<PlanarPolygon>(polygons.Count);
        var allGrown = new Paths64();
        var allDrawn = new Paths64();
        int held = 0;

        foreach (var poly in polygons)
        {
            var paths = new Paths64();
            var outer = ToPath(poly.Outer);
            if (!Clipper.IsPositive(outer)) outer.Reverse();
            paths.Add(outer);
            foreach (var hole in poly.HoleRings)
            {
                var hp = ToPath(hole);
                if (Clipper.IsPositive(hp)) hp.Reverse();
                paths.Add(hp);
            }
            allDrawn.AddRange(paths);

            var grown = Clipper.InflatePaths(paths, d * Scale, JoinType.Miter, EndType.Polygon);

            // A gap inside one conductor narrower than ΔW — a slot open at one end as much as an enclosed
            // hole — is FILLED by growing, which no ring count can see. The morphological closing (grow,
            // then shrink back by the same amount) returns the drawn outline exactly unless something was
            // filled, so any added area beyond flattening noise is a gap that would close.
            var closed = Clipper.InflatePaths(grown, -d * Scale, JoinType.Miter, EndType.Polygon);
            if (Clipper.Area(closed) - Clipper.Area(paths) > 0.25 * d * d * Scale * Scale)
                return (null,
                    "growing the outlines would close a slot or hole inside one conductor (two of its edges " +
                    $"are closer than ΔW = {deltaWM * 1e6:G4} µm)", 0);

            // Port edges: cut the grown copper back to the drawn edge's line, over the edge's own
            // extent plus the corners the growth added beside it.
            var cuts = new Paths64();
            foreach (var (a, b) in Edges(poly.Outer))
            {
                if (!portPoints.Any(p => OnEdge(p, a, b))) continue;
                held++;
                double len = Dist(a, b);
                double tx = (b.X - a.X) / len, ty = (b.Y - a.Y) / len;
                double nx = ty, ny = -tx;                         // one of the two normals…
                double mx = 0.5 * (a.X + b.X), my = 0.5 * (a.Y + b.Y), probe = Math.Min(1e-3 * len, 1e-7);
                if (poly.Contains(mx + nx * probe, my + ny * probe)) { nx = -nx; ny = -ny; }  // …the outward one
                double lat = 2 * d, depth = 3 * d;
                cuts.Add(ToPath(
                [
                    new EmPoint(a.X - tx * lat, a.Y - ty * lat),
                    new EmPoint(b.X + tx * lat, b.Y + ty * lat),
                    new EmPoint(b.X + tx * lat + nx * depth, b.Y + ty * lat + ny * depth),
                    new EmPoint(a.X - tx * lat + nx * depth, a.Y - ty * lat + ny * depth),
                ]));
            }
            if (cuts.Count > 0) grown = Clipper.Difference(grown, cuts, FillRule.NonZero);

            var outers = grown.Where(Clipper.IsPositive).ToList();
            var holes  = grown.Where(p => !Clipper.IsPositive(p)).ToList();
            if (outers.Count != 1 || holes.Count != poly.HoleRings.Count)
                return (null,
                    "growing the outlines would close a slot or hole inside one conductor (two of its edges " +
                    $"are closer than ΔW = {deltaWM * 1e6:G4} µm)", 0);

            allGrown.AddRange(grown);
            result.Add(new PlanarPolygon(FromPath(outers[0]),
                holes.Count == 0 ? null : [.. holes.Select(h => (IReadOnlyList<EmPoint>)FromPath(h))]));
        }

        // Two conductors that were apart must stay apart: merging them would short two nets.
        int Islands(Paths64 p) => Clipper.Union(p, FillRule.NonZero).Count(Clipper.IsPositive);
        if (Islands(allGrown) < Islands(allDrawn))
            return (null,
                $"two conductors on this level are closer than ΔW = {deltaWM * 1e6:G4} µm, and growing them " +
                "would join them", 0);

        return (result, null, held);
    }

    // ── geometry helpers ─────────────────────────────────────────────────────────────────────────

    /// <summary>Clipper works in integers; nanometres resolve every outline a layout can hold.</summary>
    private const double Scale = 1e9;

    private static Path64 ToPath(IReadOnlyList<EmPoint> ring)
    {
        var p = new Path64(ring.Count);
        foreach (var pt in ring) p.Add(new Point64(Math.Round(pt.X * Scale), Math.Round(pt.Y * Scale)));
        return p;
    }

    private static List<EmPoint> FromPath(Path64 p) => [.. p.Select(pt => new EmPoint(pt.X / Scale, pt.Y / Scale))];

    private static IEnumerable<(EmPoint A, EmPoint B)> Edges(IReadOnlyList<EmPoint> ring)
    {
        for (int i = 0; i < ring.Count; i++) yield return (ring[i], ring[(i + 1) % ring.Count]);
    }

    private static double Dist(EmPoint a, EmPoint b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));

    /// <summary>A port label sits on an edge when it is within half a micron of the segment (plus a
    /// part per thousand of its length) — the Port tool places labels ON the edge.</summary>
    private static bool OnEdge(EmPoint p, EmPoint a, EmPoint b)
    {
        double len = Dist(a, b);
        if (len <= 0) return false;
        double tx = (b.X - a.X) / len, ty = (b.Y - a.Y) / len;
        double s = (p.X - a.X) * tx + (p.Y - a.Y) * ty;
        double tol = 0.5e-6 + 1e-3 * len;
        if (s < -tol || s > len + tol) return false;
        return Math.Abs((p.X - a.X) * ty - (p.Y - a.Y) * tx) <= tol;
    }

    private static double Median(List<double> v)
    {
        v.Sort();
        int n = v.Count;
        return n % 2 == 1 ? v[n / 2] : 0.5 * (v[n / 2 - 1] + v[n / 2]);
    }
}
