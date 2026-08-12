// Conformal boundary cells — M1: the CUT CELL as geometry, before any physics touches it.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// R-cut-1 — THE GATE IS TILING, AND IT IS THE ONLY GATE M1 HAS
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// R-msh-1's own words: a mesh that does not tile its input is a solver that solves a slightly
// different structure and reports a smooth, plausible, wrong s-parameter. L8b made that exact for
// MANHATTAN artwork and MEASURED the deviation for everything else (0.47–0.59% of area on the
// shipping tapers, 17–24% of local WIDTH). This file makes it exact for non-Manhattan artwork too:
// the kept region of a boundary cell is the grid rectangle intersected with the metal, so the union
// of the cells IS the drawn polygon, to round-off.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// THE REPRESENTATION IS A LIST OF CONVEX PIECES, AND THAT IS A DELIBERATE DEVIATION FROM THE BRIEF
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// §2 suggests a half-plane (nx, ny, d) — "one straight cut" — and §8 flags that as a guess rather
// than a measurement. Two things it cannot carry, both of which the brief itself asks for:
//
//   * **R-cut-3's MERGE.** Absorbing a sliver into its neighbour gives "one L-shaped or trapezoidal
//     cell", which is not a rectangle minus a half-plane. A half-plane representation would have
//     forced the sliver remedy to be a SNAP (drop the sliver, lose its area) instead, and that trades
//     R-cut-3 against R-cut-1 — the two gates the phase exists for.
//
//   * **A cell the artwork crosses TWICE.** A half-plane silently mis-describes it; a piece list
//     either describes it exactly or is refused, and this file refuses (see below).
//
// So a region is a list of CONVEX, counter-clockwise pieces. Every quantity the fill wants is
// additive over them — area, centroid, the six closed-form cores (PolygonIntegrals), and the
// quadrature nodes — so the piece list costs one loop and nothing else. A whole rectangle is
// represented by a NULL region, not by a four-vertex piece, so every pre-conformal number in this
// repository is produced by exactly the code that produced it before (R-cut-2).
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// WHAT IS REFUSED PER CELL, AND WHY IT IS A MESH-REFINEMENT INSTRUCTION RATHER THAN A WRONG CELL
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// §1 puts "more than one cut per cell" out of scope and §2 says the gate must make that a refusal or
// a refinement instruction, "never a silently-wrong cell". Three configurations are detected here and
// all three fall back to L8b's STAIRCASE decision for that cell alone, with the count reported in the
// mesh notes so the user can refine and watch it go to zero:
//
//   (a) more than one POLYGON of the layer touches the cell — the union would need a real polygon
//       boolean, and two shapes overlapping (rather than abutting) would double-count;
//   (b) a HOLE ring touches the cell — the kept region is then not simply connected;
//   (c) the clipped region is NOT CONVEX — a reflex vertex of the artwork sits inside the cell, which
//       is exactly "a cell straddling a sharp corner".
//
// On Manhattan artwork none of the three can fire: every axis-parallel edge is already a hard
// gridline (R-msh-1), so no cell is crossed by any boundary at all. That is why R-cut-2's bit-identity
// holds by construction and not by care.

namespace CircuitRF.Engine.Mom;

/// <summary>
/// The part of a grid cell that is actually metal, when that is not the whole rectangle.
///
/// <para>A list of convex counter-clockwise pieces. One piece is the ordinary cut cell; two are what
/// R-cut-3's sliver merge produces. <b>Null on <see cref="PlanarCell.Region"/> means the whole
/// rectangle</b> — the pre-conformal case, which is not represented here at all.</para>
/// </summary>
public sealed class PlanarCellRegion
{
    /// <summary>
    /// Simple, counter-clockwise, in metres. Additive: every quantity the fill asks of a region is
    /// the sum over these.
    ///
    /// <para><b>They are no longer required to be CONVEX, and that is brief-convex-decomposition.md's
    /// M1.</b> The property the strip construction actually needs is flow-simplicity, per direction
    /// (<see cref="RooftopSupport.IsFlowSimple"/>); convexity implies it and is much stronger.
    /// <see cref="PolygonIntegrals"/> was general from the start — its edge reduction "needs neither
    /// convexity nor the observation point being inside" — so nothing downstream had to change.</para>
    /// </summary>
    public IReadOnlyList<IReadOnlyList<EmPoint>> Pieces { get; }

    /// <summary>∫∫dS over the region — <b>this is what <see cref="PlanarCell.Area"/> reports</b>, and
    /// therefore what normalises the basis (1/Area) and what R-cut-3's sliver threshold is a fraction
    /// of.</summary>
    public double Area { get; }

    public double CentroidX { get; }
    public double CentroidY { get; }

    /// <summary>The region's own bounding box — <b>not</b> the grid rectangle, which stays on
    /// <see cref="PlanarCell.XMin"/>…<see cref="PlanarCell.YMax"/>.</summary>
    public double XMin { get; }
    public double YMin { get; }
    public double XMax { get; }
    public double YMax { get; }

    /// <summary>True when this region came from absorbing a sliver neighbour (R-cut-3), so the cell
    /// covers two grid positions. Reported in the mesh notes — a mesher that silently re-shapes cells
    /// is worse than one that says it did.</summary>
    public bool Merged { get; }

    /// <summary>Per piece: whether it is convex, decided ONCE here so <see cref="Contains"/> can keep
    /// taking the half-plane test — bit for bit — where that test is valid, and take a ray cast only
    /// where M1's non-convex cells made it necessary.</summary>
    private readonly bool[] _convex;

    private PlanarCellRegion(IReadOnlyList<IReadOnlyList<EmPoint>> pieces, bool merged)
    {
        Pieces = pieces;
        Merged = merged;

        // ── The shoelace, taken about the piece's OWN first vertex ────────────────────────────
        //
        // Not a refinement: a plain absolute-coordinate shoelace is what a first version did, and it
        // is wrong at exactly the level this phase's own gate reads. A cell 87 µm across at
        // x = 19.8 mm has products of order 5.6e-5 differencing to an area of 7.3e-9, so the sum
        // carries ~3e-12 RELATIVE error — measured, and it broke R-cut-2's bit-identity outright
        // (a Manhattan cell whose clip is its own rectangle read 1.2e-12 short of Width·Height and
        // was classified as CUT). Translating to a local origin removes the cancellation entirely and
        // costs one subtraction per vertex.
        double a = 0, mx = 0, my = 0;
        double x0 = double.PositiveInfinity, y0 = double.PositiveInfinity;
        double x1 = double.NegativeInfinity, y1 = double.NegativeInfinity;
        foreach (var p in pieces)
        {
            double ox = p[0].X, oy = p[0].Y;
            for (int i = 0, n = p.Count, j = n - 1; i < n; j = i++)
            {
                double ax = p[j].X - ox, ay = p[j].Y - oy;
                double bx = p[i].X - ox, by = p[i].Y - oy;
                double cross = ax * by - bx * ay;
                a  += cross;
                mx += cross * (ax + bx + 3.0 * ox);
                my += cross * (ay + by + 3.0 * oy);
            }
            foreach (var v in p)
            {
                if (v.X < x0) x0 = v.X;
                if (v.Y < y0) y0 = v.Y;
                if (v.X > x1) x1 = v.X;
                if (v.Y > y1) y1 = v.Y;
            }
        }
        Area = 0.5 * a;
        // A degenerate region has no centroid; the caller never builds one (a region below the sliver
        // threshold is merged or dropped), but the guard keeps a NaN out of the quadrature if it did.
        CentroidX = Area != 0 ? mx / (6.0 * Area) : 0.5 * (x0 + x1);
        CentroidY = Area != 0 ? my / (6.0 * Area) : 0.5 * (y0 + y1);
        XMin = x0; YMin = y0; XMax = x1; YMax = y1;

        // The same 1e-9-of-an-area tolerance the mesher's own convexity test used, so a piece that
        // was called convex there is called convex here and keeps the half-plane path it always had.
        _convex = new bool[pieces.Count];
        double areaTol = 1e-9 * Math.Abs(Area);
        for (int i = 0; i < pieces.Count; i++) _convex[i] = IsConvex(pieces[i], areaTol);
    }

    /// <summary>One convex piece.</summary>
    internal static PlanarCellRegion FromPiece(IReadOnlyList<EmPoint> piece) => new([piece], false);

    /// <summary>Whether the point is inside (or on) the metal — the question
    /// <c>PlanarBasisFunctions.Divergence</c> has to ask once a cell is not its own rectangle, since
    /// a rectangle test would report a divergence pulse where there is no conductor.
    ///
    /// <para><b>The convex fast path is kept, and that is the point rather than an optimisation.</b>
    /// §5 of brief-convex-decomposition.md asks whether this implementation assumes convexity: it did,
    /// and the half-plane test it used is exactly the one every conformal number in this repository
    /// was produced by. Keeping it for a convex piece makes M1 bit-identical there by construction;
    /// only a genuinely non-convex piece — which could not exist before M1 — takes the ray cast.</para>
    /// </summary>
    public bool Contains(double x, double y)
    {
        for (int p = 0; p < Pieces.Count; p++)
        {
            var piece = Pieces[p];
            if (!_convex[p]) { if (RayCast(piece, x, y)) return true; continue; }

            bool inside = true;
            for (int i = 0, n = piece.Count, j = n - 1; i < n && inside; j = i++)
            {
                // Convex and counter-clockwise, so "inside" is "left of every edge" and the boundary
                // counts as inside — the same half-open convention Inside() uses on a rectangle.
                double cross = (piece[i].X - piece[j].X) * (y - piece[j].Y)
                             - (piece[i].Y - piece[j].Y) * (x - piece[j].X);
                if (cross < -1e-12 * (Math.Abs(Area) + 1e-300)) inside = false;
            }
            if (inside) return true;
        }
        return false;
    }

    /// <summary>Crossing number, for a piece that is simple but not convex. A point exactly ON the
    /// boundary is answered either way and that is deliberate — a quadrature node never lands there,
    /// and pretending to a tie-break the half-plane test does not have would be inventing one.</summary>
    private static bool RayCast(IReadOnlyList<EmPoint> piece, double x, double y)
    {
        bool inside = false;
        for (int i = 0, n = piece.Count, j = n - 1; i < n; j = i++)
        {
            double yi = piece[i].Y, yj = piece[j].Y;
            if (yi > y == yj > y) continue;
            double xc = piece[j].X + (y - yj) / (yi - yj) * (piece[i].X - piece[j].X);
            if (x < xc) inside = !inside;
        }
        return inside;
    }

    /// <summary>R-cut-3's merged cell: this region's pieces plus the absorbed sliver's.</summary>
    internal PlanarCellRegion Absorb(PlanarCellRegion? sliver,
                                     double sx0, double sy0, double sx1, double sy1)
    {
        var pieces = new List<IReadOnlyList<EmPoint>>(Pieces);
        if (sliver is null)
            pieces.Add([new EmPoint(sx0, sy0), new EmPoint(sx1, sy0),
                        new EmPoint(sx1, sy1), new EmPoint(sx0, sy1)]);
        else
            pieces.AddRange(sliver.Pieces);
        return new PlanarCellRegion(pieces, merged: true);
    }

    /// <summary>The whole rectangle, as an explicit region — used only when a WHOLE cell has to
    /// absorb a sliver, since a null region cannot carry a second piece.</summary>
    internal static PlanarCellRegion WholeRectangle(double x0, double y0, double x1, double y1)
        => new([[new EmPoint(x0, y0), new EmPoint(x1, y0),
                 new EmPoint(x1, y1), new EmPoint(x0, y1)]], false);

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Clipping
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Sutherland–Hodgman of a ring against an axis-aligned RECTANGLE, returned counter-clockwise.
    ///
    /// <para>The clip WINDOW is the rectangle, which is convex, so the algorithm is exact and needs no
    /// general boolean — that is the whole reason the roles are this way round rather than clipping
    /// the rectangle by the artwork. A non-convex subject can come back with zero-area doubled-back
    /// chains; they are harmless to every integral here (the signed edge sum cancels them exactly) but
    /// they fail <see cref="IsConvex"/>, which is what routes that cell to the staircase fallback.</para>
    /// </summary>
    public static List<EmPoint> ClipToRect(IReadOnlyList<EmPoint> ring,
                                           double x0, double y0, double x1, double y1)
    {
        var cur = new List<EmPoint>(ring.Count + 8);
        foreach (var p in ring) cur.Add(p);

        cur = ClipHalfPlane(cur, +1, 0, x0);   //  x ≥ x0
        cur = ClipHalfPlane(cur, -1, 0, -x1);  //  x ≤ x1
        cur = ClipHalfPlane(cur, 0, +1, y0);   //  y ≥ y0
        cur = ClipHalfPlane(cur, 0, -1, -y1);  //  y ≤ y1

        if (cur.Count >= 3 && PlanarPolygon.RingArea(cur) < 0) cur.Reverse();
        return cur;
    }

    /// <summary>Keeps the side <c>ax·x + ay·y ≥ c</c>.</summary>
    private static List<EmPoint> ClipHalfPlane(List<EmPoint> poly, double ax, double ay, double c)
    {
        var outp = new List<EmPoint>(poly.Count + 2);
        int n = poly.Count;
        if (n == 0) return outp;

        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            var a = poly[j];
            var b = poly[i];
            double da = ax * a.X + ay * a.Y - c;
            double db = ax * b.X + ay * b.Y - c;
            bool ina = da >= 0, inb = db >= 0;

            if (ina != inb)
            {
                double t = da / (da - db);
                outp.Add(new EmPoint(a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y)));
            }
            if (inb) outp.Add(b);
        }
        return outp;
    }

    /// <summary>
    /// Drops repeated and collinear vertices. <b>Both matter</b>: a clip against a rectangle produces
    /// a duplicate wherever a vertex lands exactly on a clip line — which on this mesher's grid is not
    /// rare, since every axis-parallel edge IS a gridline — and a collinear triple would make the
    /// convexity test read a rounding sign.
    /// </summary>
    public static List<EmPoint> Simplify(IReadOnlyList<EmPoint> ring, double tol)
    {
        var a = new List<EmPoint>(ring.Count);
        foreach (var p in ring)
            if (a.Count == 0 || Math.Abs(p.X - a[^1].X) > tol || Math.Abs(p.Y - a[^1].Y) > tol)
                a.Add(p);
        while (a.Count > 1 && Math.Abs(a[0].X - a[^1].X) <= tol && Math.Abs(a[0].Y - a[^1].Y) <= tol)
            a.RemoveAt(a.Count - 1);
        if (a.Count < 3) return a;

        var b = new List<EmPoint>(a.Count);
        for (int i = 0, n = a.Count; i < n; i++)
        {
            var prev = a[(i + n - 1) % n];
            var cur  = a[i];
            var next = a[(i + 1) % n];
            double cross = (cur.X - prev.X) * (next.Y - cur.Y) - (cur.Y - prev.Y) * (next.X - cur.X);
            if (Math.Abs(cross) > tol * tol) b.Add(cur);
        }
        return b.Count >= 3 ? b : a;
    }

    /// <summary>
    /// Convex to a tolerance measured in AREA rather than in angle, so a long thin cell is not judged
    /// by a cross product that is small only because one edge is short.
    /// </summary>
    public static bool IsConvex(IReadOnlyList<EmPoint> ring, double areaTol)
    {
        int n = ring.Count;
        if (n < 3) return false;
        for (int i = 0; i < n; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % n];
            var c = ring[(i + 2) % n];
            double cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
            if (cross < -areaTol) return false;         // the ring is already counter-clockwise
        }
        return true;
    }
}
