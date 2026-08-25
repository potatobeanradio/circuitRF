// L8d follow-up (2026-08-12) — the uniform feed a calibrated port needs, built by the SOLVER
// instead of demanded of the user's artwork.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// WHY THIS EXISTS. IT IS NOT A CONVENIENCE — WITHOUT IT A TAPER READS AS AN OPEN CIRCUIT
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// D6's peel forms the de-embedded wave matrix as
//
//     y_ij = (S_meas,ij − δ_ij·a₁₁) / (a₂₁(i)·a₂₁(j))
//
// and a₁₁ — the delta gap's EXTERNAL reflection — is measured on a calibration standard, which is an
// ISOLATED UNIFORM LINE of the port's own cross-section (D4). The diagonal is therefore a difference
// of two numbers that both sit within a few 1e-4 of unity, divided by a₂₁², and a₂₁ ∝ ω (the port is
// necessarily a series delta gap). On the owner's 50 → 12 Ω Klopfenstein taper at 1 GHz that divisor
// is 9.8e-5: the peel multiplies any error in a₁₁ by ~10⁴.
//
// So a₁₁ has to be RIGHT to about 1e-5, and it is only right if the DUT's metal actually looks like
// the standard for the distance the standard replaces. When the artwork starts changing width at the
// reference plane — every taper, every Klopfenstein, every part whose port sits on a flare — it does
// not, and the failure is not a mild inaccuracy:
//
//     MKlopf, 2000 mil, 50 → 12 Ω, drawn with the port on the taper's own end face
//       de-embedded  |S₁₁| = 1.0000 … 1.0235,  |S₂₁| = 0.0008 … 0.11,  Σ|S|² up to 1.06
//       the same part with 2 mm of uniform lead
//       de-embedded  |S₁₁| = 0.44,   |S₂₁| = 0.89,  Σ|S|² = 0.992   (and it tracks the analytic model)
//
// A non-passive answer that reads as a perfect open is what a user got, with no refusal.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// WHAT IT DOES
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// Before meshing, each port's own polygon is EXTENDED outward from its drawn end face by however
// much uniform line the calibration is short of — never more, and nothing at all when the drawn feed
// is already uniform for that distance. The lead is part of the artwork the fill sees, so it is real
// metal with real coupling; afterwards it is removed EXACTLY, because it is a uniform section of the
// port's own cross-section and γ is a quantity the calibration already measured (<see cref="Peel"/>).
//
// **The user's reference plane therefore lands on the DRAWN metal edge**, which is where §10.6 says
// it lands and where a user reading a plot assumes it lands. Nothing about the artwork changes, no
// setting is introduced, and a `.clay` that produced a good answer before produces a BIT-IDENTICAL
// one now (no shortfall ⇒ no lead ⇒ the same problem object).
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// WHAT IT DELIBERATELY DOES NOT DO
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// Every case it is not certain about is DECLINED rather than guessed, and declining costs nothing:
// it is exactly the behaviour that shipped before this file existed, warning included
// (`PlanarPorts.CheckFeedClearance`). The declines are: a port whose level cannot be determined from
// geometry alone; an end face that is not a single straight segment (so there is no unambiguous
// cross-section to extrude); and a lead that would run into other metal on the same level. Guessing
// any of those would move metal the user drew, which is a worse failure than the one being fixed.

using System.Numerics;
using NumFlat;

namespace CircuitRF.Engine.Mom;

/// <summary>
/// One port's automatically-added uniform feed: how long it is, and where the user's own metal
/// edge sits, which is the plane the answer must be reported at.
/// </summary>
/// <param name="PortNumber">The port this lead was grown for.</param>
/// <param name="LengthM">How much line was added, outward from <paramref name="DrawnEdgeM"/>.</param>
/// <param name="DrawnEdgeM">The longitudinal coordinate of the user's own metal edge — the plane
/// <see cref="PlanarFeedExtension.Peel"/> brings the answer back to.</param>
/// <param name="ExistingUniformM">How much uniform feed the artwork already had. The lead makes up
/// the difference to the calibration's required run and no more.</param>
public sealed record PlanarFeedLead(
    int    PortNumber,
    double LengthM,
    double DrawnEdgeM,
    double ExistingUniformM);

public static class PlanarFeedExtension
{
    /// <summary>
    /// How closely the cross-section must hold for the feed to count as uniform, as a fraction of
    /// the port's own width. <b>Tight on purpose.</b> The quantity it is protecting is amplified by
    /// 1/a₂₁² (≈ 10⁴ at 1 GHz), and a drawn uniform line matches its own end face EXACTLY — its
    /// vertices are the same numbers — so there is no fragility in asking for 0.1%. On the owner's
    /// MKlopf the width drifts 2.8% over the calibration's own run, which a loose tolerance would
    /// have waved through.
    /// </summary>
    public const double UniformFractionOfWidth = 1e-3;

    /// <summary>
    /// How finely the run inward from the end face is sampled for uniformity. This sets the
    /// RESOLUTION of the answer, never whether a departure is found — the scan reports the last
    /// station that still matched, so it can credit at most one step of gently-flaring metal as
    /// uniform and the lead comes out at most one step short of the full run (1.6% of it). At the
    /// WIDE end of a taper that is exactly what happens, because a given absolute flare is a smaller
    /// fraction of a wider face; it is measured in <c>ATaperGrowsALeadAtBothPorts…</c> rather than
    /// assumed away.
    /// </summary>
    private const int UniformitySamples = 64;

    /// <summary>
    /// Grow each port's feed to the length the calibration replaces, and return the problem the
    /// mesher should actually see. <b>Returns the input problem unchanged, by reference, when no
    /// port needs anything</b> — which is what makes every previously-recorded number reproducible.
    /// </summary>
    /// <param name="problem">The artwork as drawn.</param>
    /// <param name="ports">The ports as placed. Only <see cref="PlanarPort.Side"/>,
    /// <see cref="PlanarPort.Location"/> and <see cref="PlanarPort.LayerIndex"/> are read.</param>
    /// <param name="calibration">Supplies <see cref="PlanarCalibrationSettings.EndRunHeights"/> —
    /// the same setting that decides how much feed the standard reproduces, so the two cannot drift.</param>
    /// <param name="lengthFormat">Owner request, 2026-08-15 — every distance this method's own notes
    /// quote goes through this. See <see cref="SurfaceMesher.Mesh"/>'s own parameter of the same
    /// name.</param>
    public static (PlanarProblem Problem, IReadOnlyList<PlanarFeedLead> Leads, IReadOnlyList<string> Notes)
        Extend(PlanarProblem problem,
               IReadOnlyList<PlanarPort> ports,
               PlanarCalibrationSettings? calibration = null,
               SurfaceMesher.PlanarLengthFormat? lengthFormat = null)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(ports);

        var fmt      = lengthFormat ?? SurfaceMesher.DefaultLengthFormat;
        var cal      = calibration ?? PlanarCalibrationSettings.Default;
        double wanted = cal.EndRunHeights * problem.Slab.HeightM;
        if (!(wanted > 0)) return (problem, [], []);

        var leads = new List<PlanarFeedLead>();
        var notes = new List<string>();

        // Layers are records holding a polygon list; a layer is only rebuilt if a port grew on it.
        List<PlanarPolygon>?[] edited = new List<PlanarPolygon>?[problem.Layers.Count];

        foreach (var port in ports)
        {
            IReadOnlyList<PlanarPolygon> PolysOn(int level) =>
                edited[level] is { } e ? e : problem.Layers[level].Polygons;

            // An internal delta gap is not fed from outside the metal, so there is nothing to extend
            // and nothing that would be calibrated if there were. Growing a lead at the conductor's
            // end because this port happens to name a DIRECTION along that axis would move metal the
            // user drew, for a port whose answer does not pass through a calibration at all.
            if (port.Kind == PlanarPortKind.InternalDeltaGap) continue;

            if (!TryLevelOf(problem, port, out int layer)) continue;

            var polys = PolysOn(layer);
            if (!TryEndFace(polys, port, out int polyIndex, out int vertexA, out double edgeS,
                            out double tLo, out double tHi))
                continue;

            bool alongX  = port.Direction == PlanarBasisDirection.X;
            bool fromLow = port.Side is PlanarPortSide.MinX or PlanarPortSide.MinY;
            if (!(tHi - tLo > 0)) continue;

            double have = UniformRun(polys[polyIndex], alongX, fromLow, edgeS, tLo, tHi, wanted);
            double add  = wanted - have;

            // Nothing shorter than the scan's own step, which is the resolution `have` was measured
            // at — below it the "shortfall" is quantisation, and growing a sliver of lead would put
            // the reference plane inside a single mesh cell for no gain.
            if (add <= wanted / UniformitySamples) continue;

            double outward = fromLow ? -add : add;
            if (Obstructed(polys, alongX, edgeS, edgeS + outward, tLo, tHi))
            {
                notes.Add(
                    $"Port {port.Number}'s feed is not uniform for the {fmt(wanted)} the " +
                    "calibration replaces, and the uniform lead that would fix it cannot be grown — " +
                    "there is other metal on this level directly behind the port. The de-embedding " +
                    "therefore removes an error box measured on a straight line from a feed that is " +
                    "not one; read the result knowing that, or move the neighbouring metal.");
                continue;
            }

            var grown = ExtrudeFace(polys[polyIndex], vertexA, alongX, outward);
            var list  = edited[layer] ??= [.. polys];
            list[polyIndex] = grown;

            leads.Add(new PlanarFeedLead(port.Number, add, edgeS, have));
        }

        if (leads.Count == 0) return (problem, [], notes);

        var layers = new PlanarConductorLayer[problem.Layers.Count];
        for (int i = 0; i < layers.Length; i++)
            layers[i] = edited[i] is { } e
                ? problem.Layers[i] with { Polygons = e }
                : problem.Layers[i];

        notes.Add(FeedNote(leads, wanted, fmt));
        return (problem with { Layers = layers }, leads, notes);
    }

    /// <summary>
    /// R-fed-2 — remove the leads <see cref="Extend"/> grew, bringing every reference plane back to
    /// the user's own drawn metal edge.
    ///
    /// <para>This is exact rather than approximate, and that is the whole reason the lead is allowed
    /// to exist: it is a UNIFORM section of the port's own cross-section, so it is the matched line
    /// D6's algebra already assumes, and γ is not estimated — it is the value the two-line
    /// calibration measured for this very cross-section. Cascading a matched line of length ℓ onto
    /// port i multiplies <c>S_ij</c> by <c>e^{−(γ_iℓ_i + γ_jℓ_j)}</c>, so removing it multiplies by
    /// the reciprocal.</para>
    ///
    /// <para><b>Called on S referenced to each port's own Z_c, BEFORE renormalisation.</b> "Matched"
    /// means matched in Z_c; doing this after <c>Renormalise</c> would be peeling a line in the wrong
    /// reference and would put a reflection back that was never there.</para>
    /// </summary>
    /// <param name="sAtZc">De-embedded S at the leads' outer reference planes, referenced to Z_c.</param>
    /// <param name="lengthsM">Per port, how much lead sits between its reference plane and the drawn
    /// edge. Zero for a port that grew none.</param>
    /// <param name="gamma">Per port, the propagation constant its calibration measured.</param>
    public static Mat<Complex> Peel(Mat<Complex> sAtZc,
                                    IReadOnlyList<double> lengthsM,
                                    IReadOnlyList<Complex> gamma)
    {
        ArgumentNullException.ThrowIfNull(lengthsM);
        ArgumentNullException.ThrowIfNull(gamma);

        int p = sAtZc.RowCount;
        if (lengthsM.Count != p || gamma.Count != p)
            throw new ArgumentException($"{p} ports need {p} lengths and {p} propagation constants.");

        var theta = new Complex[p];
        bool any = false;
        for (int i = 0; i < p; i++)
        {
            theta[i] = gamma[i] * lengthsM[i];
            any |= lengthsM[i] != 0;
        }
        if (!any) return sAtZc;

        var s = new Mat<Complex>(p, p);
        for (int i = 0; i < p; i++)
            for (int j = 0; j < p; j++)
                s[i, j] = sAtZc[i, j] * Complex.Exp(theta[i] + theta[j]);
        return s;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Geometry
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Which conductor level the port drives, WITHOUT a mesh. The explicit answer wins; otherwise
    /// exactly one level may carry metal at the port's own point. Two candidates is the same
    /// ambiguity <c>PlanarPorts</c> refuses by name — declined here rather than pre-empted, so the
    /// user still gets that refusal's wording rather than a silently extended structure.
    /// </summary>
    private static bool TryLevelOf(PlanarProblem problem, PlanarPort port, out int layer)
    {
        layer = -1;
        if (port.LayerIndex is { } given)
        {
            if (given < 0 || given >= problem.Layers.Count) return false;
            layer = given;
            return true;
        }

        // ── ContainsOrOn, not Contains, and it is not a nicety ──────────────────────────────────
        //
        // A port label sits ON the conductor's end face — that is what a port IS — so the test point
        // is exactly on the boundary, where an even-odd ray cast is decided by which way the ray
        // happens to leave. It answered "inside" at a MinX end and "outside" at a MaxX one, so a
        // symmetric taper grew a lead at one port only, which is the worst of both behaviours.
        var (bx0, by0, bx1, by1) = problem.Bounds();
        double tol = 1e-6 * Math.Max(Math.Max(bx1 - bx0, by1 - by0), 1e-12);

        for (int i = 0; i < problem.Layers.Count; i++)
        {
            bool on = false;
            foreach (var poly in problem.Layers[i].Polygons)
            {
                if (!Polygon2D.ContainsOrOn(poly.Outer, port.Location, tol)) continue;
                bool inHole = false;
                foreach (var h in poly.HoleRings)
                    if (Polygon2D.ContainsStrict(h, port.Location)) { inHole = true; break; }
                if (!inHole) { on = true; break; }
            }
            if (!on) continue;
            if (layer >= 0) return false;                 // ambiguous
            layer = i;
        }
        return layer >= 0;
    }

    /// <summary>
    /// The polygon the port drives and its END FACE: the first metal met marching in from the named
    /// side along the port's own transverse line, required to be a SINGLE straight segment of the
    /// outline lying on that face.
    ///
    /// <para>The single-segment requirement is what makes the extrusion unambiguous — a face made of
    /// several collinear edges, or one the ring visits twice, has no one cross-section to extrude,
    /// and inventing one would move metal the user drew. Every drawn feed end (MLIN, MTaper, MKlopf,
    /// a plain rectangle) is a single segment, so this declines only the genuinely odd cases.</para>
    /// </summary>
    private static bool TryEndFace(IReadOnlyList<PlanarPolygon> polys, PlanarPort port,
                                   out int polyIndex, out int vertexA, out double edgeS,
                                   out double tLo, out double tHi)
    {
        polyIndex = -1; vertexA = -1; edgeS = 0; tLo = 0; tHi = 0;

        bool alongX  = port.Direction == PlanarBasisDirection.X;
        bool fromLow = port.Side is PlanarPortSide.MinX or PlanarPortSide.MinY;
        double tCoord = alongX ? port.Location.Y : port.Location.X;

        // ── The outermost metal along the port's own transverse line ────────────────────────────
        double best = fromLow ? double.PositiveInfinity : double.NegativeInfinity;
        for (int p = 0; p < polys.Count; p++)
        {
            var (x0, y0, x1, y1) = polys[p].Bounds();
            double tol = 1e-9 * Math.Max(Math.Max(x1 - x0, y1 - y0), 1e-12);

            foreach (var (a, b) in RunsAlong(polys[p], alongX, tCoord, tol))
            {
                double outer = fromLow ? a : b;
                if (fromLow ? outer < best : outer > best) { best = outer; polyIndex = p; }
            }
        }
        if (polyIndex < 0 || double.IsInfinity(best)) return false;

        // ── The outline vertices sitting on that face ───────────────────────────────────────────
        var ring = polys[polyIndex].Outer;
        var (bx0, by0, bx1, by1) = polys[polyIndex].Bounds();
        double scale = Math.Max(bx1 - bx0, by1 - by0);
        double onFace = 1e-9 * Math.Max(scale, 1e-12);

        double S(int i) => alongX ? ring[i].X : ring[i].Y;
        double T(int i) => alongX ? ring[i].Y : ring[i].X;

        int n = ring.Count, first = -1, count = 0;
        for (int i = 0; i < n; i++)
            if (Math.Abs(S(i) - best) <= onFace) { count++; if (first < 0) first = i; }

        if (count != 2) return false;

        // The two must be ADJACENT in the ring, i.e. the face is one edge. The pair can straddle the
        // wrap (a taper's own end cap is exactly the last→first edge), so both orderings are tried.
        int a1 = -1;
        for (int i = 0; i < n; i++)
            if (Math.Abs(S(i) - best) <= onFace && Math.Abs(S((i + 1) % n) - best) <= onFace) { a1 = i; break; }
        if (a1 < 0) return false;

        int a2 = (a1 + 1) % n;
        tLo = Math.Min(T(a1), T(a2));
        tHi = Math.Max(T(a1), T(a2));
        if (tCoord < tLo - onFace || tCoord > tHi + onFace) return false;

        vertexA = a1;
        edgeS   = best;
        return true;
    }

    /// <summary>
    /// How far inward from the end face the cross-section holds, capped at <paramref name="wanted"/>.
    ///
    /// <para><b>Running out of metal counts as uniform, and that is deliberate.</b> A feed shorter
    /// than the calibration's own run is a SHORT structure, not a non-uniform one — the existing code
    /// already clamps the standard to the cells that exist (<c>EndRunCellsFor</c>). Treating it as a
    /// shortfall would grow a lead on every short line in the repository and move numbers that are
    /// not wrong.</para>
    /// </summary>
    private static double UniformRun(PlanarPolygon poly, bool alongX, bool fromLow,
                                     double edgeS, double tLo, double tHi, double wanted)
    {
        double width = tHi - tLo;
        double tol   = UniformFractionOfWidth * width;
        double tMid  = 0.5 * (tLo + tHi);
        double dir   = fromLow ? 1.0 : -1.0;

        double sliceTol = 1e-9 * Math.Max(width, 1e-12);

        double last = 0;
        for (int k = 1; k <= UniformitySamples; k++)
        {
            double d = wanted * k / UniformitySamples;
            double s = edgeS + dir * d;

            var spans = SpansAcross(poly, alongX, s, sliceTol);

            bool found = false;
            foreach (var (a, b) in spans)
            {
                if (tMid < a - sliceTol || tMid > b + sliceTol) continue;
                found = true;
                if (Math.Abs(a - tLo) > tol || Math.Abs(b - tHi) > tol) return last;
                break;
            }
            if (!found) return wanted;      // the metal ended: a SHORT feed, not a flared one
            last = d;
        }
        return wanted;
    }

    /// <summary>Whether the lead would run into other metal on the same level. Sampled rather than
    /// solved: the question is only ever asked about a rectangle that sits OUTSIDE the drawn metal
    /// along the port's own line, so a coarse interior sample separates "empty" from "occupied"
    /// without a polygon boolean and without a new dependency.</summary>
    private static bool Obstructed(IReadOnlyList<PlanarPolygon> polys, bool alongX,
                                   double s0, double s1, double tLo, double tHi)
    {
        const int NS = 5, NT = 5;
        double sa = Math.Min(s0, s1), sb = Math.Max(s0, s1);
        for (int i = 1; i <= NS; i++)
        {
            double s = sa + (sb - sa) * i / (NS + 1.0);
            for (int j = 1; j <= NT; j++)
            {
                double t = tLo + (tHi - tLo) * j / (NT + 1.0);
                double x = alongX ? s : t, y = alongX ? t : s;
                foreach (var poly in polys)
                    if (poly.Contains(x, y)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Push the end face outward by <paramref name="outward"/>, by splicing two vertices into the
    /// ring after <paramref name="vertexA"/>. Winding and vertex order are preserved, so the result
    /// is the same simple polygon with a rectangle on its end — not a second shape abutting it,
    /// which would put two rings through the boundary cells at the seam and cost them their
    /// conformal cut for nothing.
    /// </summary>
    private static PlanarPolygon ExtrudeFace(PlanarPolygon poly, int vertexA, bool alongX, double outward)
    {
        var ring = poly.Outer;
        int n = ring.Count, b = (vertexA + 1) % n;

        EmPoint Push(EmPoint p) => alongX ? new EmPoint(p.X + outward, p.Y) : new EmPoint(p.X, p.Y + outward);

        var next = new List<EmPoint>(n + 2);
        for (int i = 0; i < n; i++)
        {
            next.Add(ring[i]);
            if (i != vertexA) continue;
            next.Add(Push(ring[vertexA]));
            next.Add(Push(ring[b]));
        }
        return poly with { Outer = next };
    }

    // ── The two slices, named once, because getting them the wrong way round is silent ──────────
    //
    // Polygon2D.HorizontalFootprint(ring, y) answers "the X-intervals at this Y". Both questions
    // below are that question in one of the two frames, so each is one call plus, for one of them,
    // a transposed ring. Writing them inline is how the first draft of this file got one backwards.

    /// <summary>The metal's LONGITUDINAL intervals at transverse station <paramref name="t"/> —
    /// "marching in from the named side, where does metal start?".</summary>
    private static List<(double A, double B)> RunsAlong(
        PlanarPolygon poly, bool alongX, double t, double tol)
        => alongX
            ? Polygon2D.HorizontalFootprint(poly.Outer, t, tol)               // x-intervals at y = t
            : Polygon2D.HorizontalFootprint(Transpose(poly.Outer), t, tol);   // y-intervals at x = t

    /// <summary>The metal's TRANSVERSE intervals at longitudinal station <paramref name="s"/> —
    /// "how wide is the feed here?".</summary>
    private static List<(double A, double B)> SpansAcross(
        PlanarPolygon poly, bool alongX, double s, double tol)
        => alongX
            ? Polygon2D.HorizontalFootprint(Transpose(poly.Outer), s, tol)    // y-intervals at x = s
            : Polygon2D.HorizontalFootprint(poly.Outer, s, tol);              // x-intervals at y = s

    private static IReadOnlyList<EmPoint> Transpose(IReadOnlyList<EmPoint> ring)
    {
        var q = new EmPoint[ring.Count];
        for (int i = 0; i < ring.Count; i++) q[i] = new EmPoint(ring[i].Y, ring[i].X);
        return q;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Notes
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private static string FeedNote(IReadOnlyList<PlanarFeedLead> leads, double wanted,
                                   SurfaceMesher.PlanarLengthFormat fmt)
    {
        var parts = leads.Select(l =>
            $"port {l.PortNumber} {fmt(l.LengthM)}" +
            (l.ExistingUniformM > 0 ? $" (on top of {fmt(l.ExistingUniformM)} it already had)" : ""));

        return $"{leads.Count} port(s) sit on metal that changes cross-section inside the " +
               $"{fmt(wanted)} of feed the calibration standard replaces, so a UNIFORM " +
               $"LEAD of the port's own width was added for the solve and removed again afterwards: " +
               string.Join(", ", parts) + ". Your reference planes are still your own drawn metal " +
               "edges — the lead is meshed, solved and then peeled as a matched section of the line " +
               "the calibration itself measured, so it changes where the error box is taken, not " +
               "where the answer is reported. Without it the error box is measured on a straight " +
               "line and applied to a flare, and the peel divides that mismatch by a₂₁² — which on a " +
               "taper is a non-passive answer that reads as an open circuit.";
    }
}
