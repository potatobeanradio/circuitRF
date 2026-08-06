// L8c — the rooftop basis function, stated once.
//
// D2: ROOFTOP OVER AN ADJACENT CELL PAIR, AND NOTHING ELSE. L8b's mesher already emits a
// PlanarBasis only where two cells genuinely share an edge, so the basis set is exactly the set with
// no charge accumulating on the metal's outer rim. There is no second basis family, no charge-only
// basis and no "half rooftop" at the boundary — adding one would put charge on the rim, which is
// physically wrong and would silently change every answer rather than failing.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// THE DEFINITION, AND WHY IT IS WRITTEN IN THIS PARTICULAR NORMALISATION
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// A basis spans cells A and B sharing an edge; A is the lower-index (left, for X; below, for Y) cell
// — that is L8b's own construction order and it is a contract, not an observation. Let û be x̂ or ŷ
// and let ξ_a(r) be the distance from r to the cell's OUTER edge (the one the pair does not share),
// so ξ runs 0 → w on each cell and is continuous nowhere except where it needs to be. Then
//
//     f(r) = û · ξ_a(r) / Area(a)          for r in cell a ∈ {A, B}
//
// and three properties fall out rather than being imposed:
//
//   • CONTINUITY across the shared edge. There ξ_A = w_A and ξ_B = w_B, so both sides give
//     w/(w·L) = 1/L with L the shared edge's length. R-fil-1.
//   • ZERO on the pair's two outer edges, where ξ = 0. R-fil-1 — this is what keeps the charge off
//     the rim.
//   • ∇·f = ±1/Area(a) EXACTLY — a pulse on each cell, positive on A, negative on B — so
//     ∫∇·f dS = +1 − 1 = 0 to machine precision, not to a tolerance. R-fil-1's whole point: a basis
//     that does not conserve charge puts a monopole on every cell, and the wrongness looks like a bad
//     mesh rather than like a bad basis.
//
// The normalisation is "unit total current across the shared edge": ∫ f·û dℓ over that edge is
// L·(1/L) = 1 A. It is the choice that makes D4's per-CELL potential matrix exact — the divergence
// pulse integrates to ±1 on each cell, so the scalar block is a signed sum of cell-pair entries with
// no leftover factor.
//
// D5 — AN X-ROOFTOP AND A Y-ROOFTOP ARE POINTWISE ORTHOGONAL. f is purely x̂ or purely ŷ, so
// f_m·f_n ≡ 0 for a mixed pair and the whole VECTOR block vanishes there. In Michalski-Zheng
// formulation C — which is what L8a derived and what SpectralGreens implements — the vector kernel is
// a single scalar G_A with no xy component, so nothing reintroduces the coupling: a mixed pair
// couples through the SCALAR term alone. That halves the vector fill and gives Tier 4 a test that
// catches a formulation error immediately.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// L9c / D2 — THE VIA IS A ROOFTOP ONE DIMENSION OVER, AND THAT IS WHY IT NEEDS NO SECOND FAMILY
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// D2 offered two constructions and said the measurement decides: (a) an attachment basis spanning
// the via and the horizontal cells at each foot, so ∫∇·f dS = 0 holds BY CONSTRUCTION exactly as the
// rooftop's does; or (b) a separate vertical basis plus an explicit continuity constraint ROW in the
// system. **(a), and it turns out not to be a new construction at all.**
//
// L8b's D8 put every conductor level on ONE SHARED TENSOR GRID, explicitly so that "L9's multi-level
// stack needs vertical current to cross between them, and a per-layer grid would make that a re-mesh
// rather than an addition". Take that seriously and make a via's footprint EXACTLY one cell of that
// grid. Then:
//
//   • a horizontal rooftop spans two cells adjacent in x (or y), and the unit current crosses their
//     shared EDGE;
//   • a via basis spans two cells adjacent in z — the same (IX, IY) on two consecutive levels — and
//     the unit current crosses their shared FOOTPRINT.
//
// Same object, one dimension over. ∇·f is the same ±1/Area pulse, positive on the lower cell (current
// leaving it upward) and negative on the upper one, so ∫∇·f dS = +1 − 1 = 0 to machine precision and
// not to a tolerance. R-fil-1 is unchanged and R-via-3 is satisfied by construction rather than by a
// constraint row — which is what keeps L8d's D1 intact (one factorisation, Y = BᵀZ⁻¹B, reciprocity
// structural). **Construction (b) was never reached for**, and the reason it was not is worth
// stating: it changes the SHAPE of the linear system, and D2 asked for that to be reported before
// being built.
//
// THE ONE THING THAT IS GENUINELY DIFFERENT, and it is not the divergence: the horizontal rooftop's
// weight is a linear RAMP ξ/Area, because its two cells are 2-D sheets and the current has to grow
// from zero at the far edge. A via's two cells are still sheets but the current crosses between them
// through the footprint AREA, not across an edge, so the vertical current density is UNIFORM at
// 1/Area over the footprint — the ramp degenerates. That is why Weight() is a constant for Z, and
// it is the same normalisation ("unit total current across the shared connection"), not a different
// one.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// THE GROUND-ATTACHMENT (HALF) BASIS — ONE MESHED FOOT, AND ITS NET CHARGE IS NOT ZERO
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// A BACKSIDE via joins a signal level to the GROUND PLANE, which is the laterally infinite PEC the
// Green's function handles analytically and is never a meshed level. L9's own phase gate found that
// L9c's rooftop-in-z therefore cannot express the commonest via on a MMIC at all. What it needs is
// an ATTACHMENT basis: the same uniform 1/Area vertical weight over the footprint, one divergence
// pulse at the meshed foot, and NOTHING at the grounded foot.
//
// THE TWO STRUCTURAL INVARIANTS THAT DO NOT SURVIVE IT, both re-stated here rather than left to
// look like breakage:
//
//   (a) L9c's D5 asserts ∫∇·f dS = 0 as an EQUALITY on every basis. An attachment basis has ONE
//       pulse, so its net charge is ∓1 — balanced by its IMAGE below the plane, not by a second
//       pulse on the metal. Adding a compensating pulse "to restore D5" would double-count the
//       image the Green's function already carries. The equality that survives is |net| = 1 and it
//       is asserted exactly, not to a tolerance (ViaBasisTests).
//
//   (b) L8c's own header records that s_A + s_B = 0, so "any part of G_q that does not depend on ρ
//       contributes exactly ZERO to the scalar block". That cancellation is what makes the
//       EXTRACTED CONSTANT harmless everywhere else, and IT FAILS FOR THIS ROW: the four-term signed
//       sum degenerates to one term, so the constant survives into Z^φ. Nothing in the fill notices,
//       which is exactly why the ω → 0 capacitance is gated (R-gv-8, PlanarStaticLimitTests) rather
//       than the sign being reasoned about.
//
// SIGN CONVENTION, STATED ONCE AND DELIBERATELY NOT THE BRIEF'S. Its D4 calls the net charge "+1".
// This file instead keeps EVERY vertical basis's current flowing +z (A → B, upward), attachment
// included, so the ẑẑ block needs no per-basis direction factor and reciprocity stays structural
// with an attachment and an interior via in the SAME mesh — which is exactly the MMIC starter
// (a backside via and a Metal1↔Metal2 post). With that orientation the single pulse sits at the
// UPPER (meshed) foot and carries −1/Area, so the net charge on the metal is −1 and the plane's
// +1 image is the return. Matching the other vertical bases' direction is worth more than matching
// a sign in prose; the physics D4 is about — the charge is not zero, the ground plane is the
// return, do not add a compensating pulse — is unchanged.
//
// R-via-5 — WHERE VERTICAL BASES SIT IN THE UNKNOWN VECTOR IS A CONTRACT FROM THE MOMENT IT IS
// WRITTEN, because ports, the current-density map and de-embedding all index by it. **Every
// horizontal basis of every level comes before every vertical one.** Adding a via therefore renumbers
// no horizontal unknown, and adding a level renumbers no via — which is the property that matters,
// and which interleaving per level would destroy. See SurfaceMesher.

namespace CircuitRF.Engine.Mom;

/// <summary>
/// One cell of a rooftop's support, resolved into the numbers the fill actually uses: the cell, the
/// sign of its divergence pulse, and where its OUTER edge is.
/// </summary>
/// <param name="CellIndex">Index into <see cref="PlanarMesh.Cells"/>.</param>
/// <param name="Sign">+1 on the pair's lower-index cell, −1 on the other — the sign of ∇·f there.</param>
/// <param name="OuterEdge">The coordinate (x for an X basis, y for a Y basis) of the edge the pair
/// does NOT share. The linear weight is <c>|coord − OuterEdge| / Area</c>, and it is zero here.</param>
public readonly record struct RooftopHalf(int CellIndex, double Sign, double OuterEdge);

/// <summary>
/// The rooftop basis: evaluation, divergence, support, and the two half-descriptions the matrix fill
/// consumes. See the file header for the definition and for why it is normalised the way it is.
/// </summary>
public static class PlanarBasisFunctions
{
    /// <summary>
    /// The two halves of a rooftop, in the order (lower-index cell, higher-index cell) — i.e. the
    /// order L8b's <see cref="PlanarBasis"/> already fixes. <see cref="RooftopHalf.Sign"/> is +1 then
    /// −1, so the caller never has to rediscover which end of the pair the current flows out of.
    /// </summary>
    public static (RooftopHalf A, RooftopHalf B) Halves(PlanarMesh mesh, PlanarBasis basis)
    {
        // The GROUND-ATTACHMENT basis has ONE meshed foot. Both halves name that same cell so no
        // caller has to guard an index, and the GROUNDED half carries Sign = 0 — which makes the
        // fill's own four-term signed sum drop the ground terms with no special case anywhere. The
        // meshed half comes FIRST so Divergence's first-match walk returns the real pulse.
        if (basis.AttachesToGround)
            return (new RooftopHalf(basis.CellB, -1.0, double.NaN),
                    new RooftopHalf(basis.CellB,  0.0, double.NaN));

        var a = mesh.Cells[basis.CellA];
        var b = mesh.Cells[basis.CellB];

        return basis.Direction switch
        {
            PlanarBasisDirection.X =>
                (new RooftopHalf(basis.CellA, +1.0, a.XMin), new RooftopHalf(basis.CellB, -1.0, b.XMax)),
            PlanarBasisDirection.Y =>
                (new RooftopHalf(basis.CellA, +1.0, a.YMin), new RooftopHalf(basis.CellB, -1.0, b.YMax)),
            // Z: the two halves are the two FOOT CELLS, on consecutive levels. There is no outer edge
            // to ramp from — the weight is uniform over the footprint — so OuterEdge is NaN rather
            // than a plausible-looking coordinate that a caller could quietly use.
            _ => (new RooftopHalf(basis.CellA, +1.0, double.NaN),
                  new RooftopHalf(basis.CellB, -1.0, double.NaN)),
        };
    }

    /// <summary>True for the vertical (via) basis — the one whose weight is uniform rather than a
    /// ramp, and whose two cells sit on different levels at the same grid position.</summary>
    public static bool IsVertical(PlanarBasis basis) => basis.Direction == PlanarBasisDirection.Z;

    /// <summary>
    /// <b><c>∫∇·f dS</c> over the basis's whole support</b> — the sum of its divergence pulses, each
    /// integrating to exactly its own sign. <b>0 for every rooftop and every interior via</b> (L9c's
    /// D5, unchanged and still an equality); <b>−1 for a ground-attachment basis</b>, whose return
    /// charge is the plane's image rather than a second pulse on the metal. See the file header for
    /// why a compensating pulse would double-count the image, and for the sign convention.
    /// </summary>
    public static double NetCharge(PlanarMesh mesh, PlanarBasis basis)
    {
        var (a, b) = Halves(mesh, basis);
        return a.Sign + b.Sign;
    }

    /// <summary>
    /// The scalar weight <c>ξ/Area</c> at a point of one half's cell — the magnitude of <c>f</c>
    /// there, its direction being <see cref="PlanarBasis.Direction"/>. Always ≥ 0; the sign that
    /// distinguishes the two halves belongs to the DIVERGENCE, not to the current.
    /// </summary>
    public static double Weight(PlanarCell cell, RooftopHalf half, PlanarBasisDirection direction,
                                double x, double y)
    {
        // L9c: the vertical basis's current crosses the shared FOOTPRINT rather than a shared edge,
        // so its density is uniform at 1/Area. Same normalisation, degenerate ramp — see the header.
        if (direction == PlanarBasisDirection.Z) return 1.0 / cell.Area;
        double coord = direction == PlanarBasisDirection.X ? x : y;
        return Math.Abs(coord - half.OuterEdge) / cell.Area;
    }

    /// <summary>
    /// <c>f(r)</c> as a two-component vector, zero outside the rooftop's own pair. This is the
    /// definition Tier 0 tests directly; the fill never calls it (it integrates the weight against a
    /// closed form instead), which is exactly why it is worth having a second, independent statement
    /// of the same thing.
    /// </summary>
    public static (double Fx, double Fy) Evaluate(PlanarMesh mesh, PlanarBasis basis, double x, double y)
    {
        // A vertical basis has NO in-plane current at all, so this is genuinely (0, 0) rather than
        // unimplemented — the whole of its current is ẑ and is read through VerticalWeight.
        if (basis.Direction == PlanarBasisDirection.Z) return (0.0, 0.0);
        var (ha, hb) = Halves(mesh, basis);

        var ca = mesh.Cells[ha.CellIndex];
        if (Inside(ca, x, y))
        {
            double w = Weight(ca, ha, basis.Direction, x, y);
            return basis.Direction == PlanarBasisDirection.X ? (w, 0.0) : (0.0, w);
        }

        var cb = mesh.Cells[hb.CellIndex];
        if (Inside(cb, x, y))
        {
            double w = Weight(cb, hb, basis.Direction, x, y);
            return basis.Direction == PlanarBasisDirection.X ? (w, 0.0) : (0.0, w);
        }

        return (0.0, 0.0);
    }

    /// <summary>
    /// <c>∇·f</c> — the ±1/Area pulse. Zero outside the pair. The integral over the pair is exactly
    /// <c>+1 − 1</c> and is checked as an equality, not to a tolerance (R-fil-1).
    /// </summary>
    public static double Divergence(PlanarMesh mesh, PlanarBasis basis, double x, double y,
                                    int layerIndex = -1)
    {
        var (ha, hb) = Halves(mesh, basis);
        var ca = mesh.Cells[ha.CellIndex];
        var cb = mesh.Cells[hb.CellIndex];

        // A VERTICAL basis's two cells sit at the SAME (x, y) on two different levels, so the plain
        // in-plane containment test would answer "both" and the pulse would be counted twice. The
        // level has to be part of the question, and for a horizontal basis it is free (both cells are
        // on one level, so the default −1 means "this basis's own level" and nothing changes).
        if (layerIndex >= 0)
        {
            if (ca.LayerIndex == layerIndex && Inside(ca, x, y)) return ha.Sign / ca.Area;
            if (cb.LayerIndex == layerIndex && Inside(cb, x, y)) return hb.Sign / cb.Area;
            return 0.0;
        }

        if (basis.Direction == PlanarBasisDirection.Z)
            throw new ArgumentException(
                "A vertical (via) basis spans two cells at the same (x, y) on DIFFERENT levels, so " +
                "its divergence is not a function of (x, y) alone. Pass the layer index.",
                nameof(basis));

        if (Inside(ca, x, y)) return ha.Sign / ca.Area;
        if (Inside(cb, x, y)) return hb.Sign / cb.Area;
        return 0.0;
    }

    /// <summary>
    /// <b>The vertical current density of a via basis</b> — uniform at <c>1/Area</c> over the shared
    /// footprint and zero elsewhere, which is the whole of its current. Zero for a horizontal basis,
    /// as a statement rather than as an unimplemented case.
    /// </summary>
    public static double VerticalWeight(PlanarMesh mesh, PlanarBasis basis, double x, double y)
    {
        if (basis.Direction != PlanarBasisDirection.Z) return 0.0;
        var a = mesh.Cells[basis.CellA];
        return Inside(a, x, y) ? 1.0 / a.Area : 0.0;
    }

    /// <summary>
    /// The length of the edge the pair shares — the transverse extent the unit current is spread
    /// over, and therefore the value of <c>|f|</c> exactly on that edge.
    /// </summary>
    public static double SharedEdgeLength(PlanarMesh mesh, PlanarBasis basis)
    {
        var a = mesh.Cells[basis.CellA];
        return basis.Direction switch
        {
            PlanarBasisDirection.X => a.Height,
            PlanarBasisDirection.Y => a.Width,
            _ => throw new ArgumentException(
                "A vertical (via) basis has no shared EDGE — its unit current crosses the shared " +
                "FOOTPRINT of the two foot cells, an area rather than a length. Use " +
                "SharedFootprintArea. The two are the same quantity in the normalisation (\"unit " +
                "total current across the shared connection\") and different in dimension, which is " +
                "exactly why this refuses rather than returning a width.", nameof(basis)),
        };
    }

    /// <summary>The area the vertical basis's unit current crosses — the Z analogue of
    /// <see cref="SharedEdgeLength"/>, and therefore the value of <c>|f|</c> on the footprint.</summary>
    public static double SharedFootprintArea(PlanarMesh mesh, PlanarBasis basis)
    {
        if (basis.Direction != PlanarBasisDirection.Z)
            throw new ArgumentException("Only a vertical (via) basis has a shared footprint.", nameof(basis));
        return mesh.Cells[basis.CellA].Area;
    }

    /// <summary>
    /// Half-open containment, so a point on a shared gridline belongs to exactly one cell. The
    /// upper edges of the whole pair are closed, so the pair's own outer boundary is included — which
    /// matters only because <c>f</c> is zero there anyway.
    /// </summary>
    private static bool Inside(PlanarCell c, double x, double y) =>
        x >= c.XMin && x <= c.XMax && y >= c.YMin && y <= c.YMax;
}
