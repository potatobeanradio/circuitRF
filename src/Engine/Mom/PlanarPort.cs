// L8d — the port: what one IS, how it resolves onto L8b's mesh, and the refusals.
//
// D2 — AN EDGE PORT'S CUT IS THE OUTERMOST ROOFTOP ROW OF THE FEED, AND NOTHING IS USER-POSITIONABLE.
// An edge port names an end of a conductor; the reference plane is then the shared edge of the two
// outermost cells there, one cell in from the drawn metal, and the half-cell beyond it is part of
// the error box. There is no port-offset setting, no reference-plane coordinate and no de-embedding
// distance to choose, because ALL of that is what the calibration removes (PlanarCalibration) — and
// offering a knob for it would offer a way to get a different answer for the same structure.
//
// THE OTHER TWO PORT TYPES ARE THE SAME OBJECT CUT SOMEWHERE ELSE.
//
// PlanarPortKind.Internal cuts at the foot of a via instead, between the metal and the ground
// plane, and drives that via's ground-attachment bases. It is the port a grounded component or a
// ground-returning device terminal needs, and it is the same incidence matrix and the same
// Y = BᵀZ⁻¹B one dimension over — a delta gap across the shared FOOTPRINT rather than a shared
// edge. Its polarity is not asked for: + is the metal and − is the plane, because one of its two
// terminals IS the ground reference.
// THE SECOND PORT TYPE — AN INTERNAL DELTA GAP — IS THE SAME OBJECT CUT SOMEWHERE ELSE.
// PlanarPortKind.InternalDeltaGap names a POINT ON the metal rather than an end of it, and the gap
// is the mesh gridline NEAREST that point with metal on both sides. Everything downstream of the
// cut is unchanged: the same rooftop row, the same incidence matrix, the same Y = BᵀZ⁻¹B (D1).
//
// What DOES change is that there is nothing outside the gap. An edge port has a feed, so it has an
// error box and the two-line calibration removes it; an internal port has metal on both sides, so
// there is no feed to remove, no Z_c to reference to, and no calibration standard that could even
// be built for it. Its s-parameters are therefore referenced to its own declared Z0 at the gap
// itself. That is what an internal port IS — see PlanarSolve's IdentityBox for how the two kinds
// share one de-embedding path without either pretending to be the other.
//
// D3 — THE GROUND REFERENCE IS THE STACKUP'S GROUND PLANE, ALWAYS. The return path is the ground
// plane by construction and there is nothing for the user to declare. A port naming any other
// reference — a coplanar ground, a second conductor, a differential pair, or (L9d) a via between two
// levels — is refused by name (see PlanarPortReference and PlanarPorts.ViaPortRefusal).
//
// L9d NOTE: through L8 those refusals pointed at "L9". L9 has now arrived and none of them is built,
// which is exactly why a refusal must name WHERE THE CAPABILITY ARRIVES rather than a phase number:
// §10.6 lists coplanar, differential, multi-mode and co-simulation ports as later work, and L9's own
// out-of-scope list keeps them there.
//
// ── RP-2a — D3 IS NOW HALF TRUE, AND THE HALF THAT CHANGED IS *TWO CUTS*, NOT A SECOND PLANE ─────
//
// A port referenced to DRAWN metal is not a second ground plane and never could be: a LayerStack has
// exactly two terminations and no interior PEC, so the medium cannot represent one (RP-2's R-rp2-1).
// What it is instead is TWO CUTS AT ONE STATION — an ordinary delta gap in the signal conductor and a
// second ordinary delta gap in the RETURN conductor, driven against each other. The incidence column
// then carries a signed block on each row set rather than one signed block, and NOTHING else moves:
// same mesh, same basis set, same Z, same factorisation, same Y = BᵀZ⁻¹B.
//
// IT IS TWO CUTS BECAUSE IT CANNOT BE ONE. RP-2's own first measurement (CoplanarSlotMeshTests, and
// src/Engine/Mom/RESOLVED.md) asked whether the surface mesher produces a basis spanning the SLOT
// between two conductors, so that a single delta gap could be cut across it. The answer is no, and
// structurally so: every polygon edge is a hard gridline, a cell exists only where a grid row's
// centre is inside metal, and a basis is a pair of grid-adjacent cells — so a slot of nonzero width
// always owns at least one metal-free grid row and refining ADDS rows to it. A basis over the slot
// would be a new basis family (a slot/magnetic-frill unknown) with its own singular treatment, which
// is a far larger question than a per-port reference.
//
// ONLY AN INTERNAL DELTA GAP. An edge port has a feed outside its cut, therefore an error box,
// therefore a calibration standard — and a coplanar port's standard is a COPLANAR line, with its own
// Z_c, its own β and its own static capacitance. PlanarCalibration builds uniform lines over the
// plane and PlanarPortResolution's cross-section fields (WidthM, TransverseLines, …) describe one
// conductor, so calibrating a coplanar edge port against those standards would produce s-parameters
// that are plausible and referenced to nothing. That is strictly worse than a refusal, so a
// conductor-referenced EDGE port is still refused, and the refusal names the capability it is
// waiting for rather than a phase (R-mom-17).
//
// R-msh-2 is honoured throughout: resolution INDEXES by L8b's cell and basis order and never
// re-sorts it. The one dictionary here is a pure LOOKUP built by a single forward pass over
// mesh.Bases and never iterated, so nothing on this path depends on hash order.

using System.Numerics;

namespace CircuitRF.Engine.Mom;

/// <summary>Which end of a conductor the port sits on — and therefore which way current flows in.</summary>
public enum PlanarPortSide
{
    /// <summary>The low-x end; current enters flowing +x̂.</summary>
    MinX,
    /// <summary>The high-x end; current enters flowing −x̂.</summary>
    MaxX,
    /// <summary>The low-y end; current enters flowing +ŷ.</summary>
    MinY,
    /// <summary>The high-y end; current enters flowing −ŷ.</summary>
    MaxY,
}

/// <summary>
/// <b>WHERE the delta gap is cut, which is the one thing that distinguishes the two port types this
/// kernel builds.</b> Both are the same object — a delta gap across the shared edge of two adjacent
/// cells, driving the rooftop row that spans it (D1) — and they differ only in which shared edge,
/// and therefore in whether there is a feed outside the gap to calibrate against.
/// </summary>
public enum PlanarPortKind
{
    /// <summary>
    /// The gap is one cell in from a conductor's own END FACE, so everything outside it is the port
    /// discontinuity and the two-line calibration removes it (<see cref="PlanarDeembed"/>). This is
    /// the port every measured or probed structure has.
    /// </summary>
    Edge,

    /// <summary>
    /// <b>The gap is an INTERIOR cut of a conductor</b> — metal on both sides, nothing outside it.
    /// A lumped element embedded in metal, or a device terminal in the middle of a structure.
    ///
    /// <para>There is no feed beyond the cut, therefore no error box, therefore <b>no de-embedding
    /// and no Z_c to reference to</b>: the reference plane IS the gap, and the published
    /// s-parameters are referenced to the port's own declared Z₀ directly. That is a property of
    /// what an internal port IS, not a missing feature — a two-line calibration removes a feed, and
    /// there is no feed here to remove.</para>
    /// </summary>
    InternalDeltaGap,

    /// <summary>
    /// <b>The gap is at the foot of a via, between the metal and the GROUND PLANE.</b> It does not
    /// cut the trace: the current it drives leaves the conductor vertically, down the via and into
    /// the plane, which is the path a grounded R, L or C — or a device terminal that returns to
    /// ground — needs.
    ///
    /// <para>Its + terminal is the metal and its − terminal is the ground plane — the only reading
    /// of a port whose second terminal is the ground reference. That polarity is fixed by what the
    /// port IS and is never asked for, unlike an <see cref="InternalDeltaGap"/>, whose two lips are
    /// both metal and whose direction therefore has to be stated.</para>
    ///
    /// <para>It drives the ground-attachment bases of the via under it — every cell of that via's
    /// footprint, since they are one conductor at one potential — and, like an
    /// <see cref="InternalDeltaGap"/>, <b>it is not de-embedded</b>: there is nothing outside the cut
    /// to remove, so its s-parameters are reported at the foot of the via in its own declared Z₀.</para>
    /// </summary>
    Internal,
}

/// <summary>
/// D3 — the only ground reference v1 has. The enum exists so the refusal can be worded against a
/// named alternative rather than against "not implemented", which is R-mom-17's whole point.
/// </summary>
public enum PlanarPortReference
{
    /// <summary>The stackup's ground plane. The only reference the MEDIUM can represent — see D3.</summary>
    GroundPlane,
    /// <summary>
    /// <b>RP-2a — the negative terminal is a drawn coplanar ground conductor.</b> A second cut, in
    /// that conductor, at the same station as the signal's, driven against it.
    /// <para>Identical to <see cref="SecondConductor"/> in what the kernel does; the two members
    /// differ in what the USER means — a ground strip against a second signal line — and that
    /// difference is what the layout note and a future coplanar calibration read differently.
    /// Building two code paths for one object would be the way to make them disagree.</para>
    /// </summary>
    CoplanarGround,
    /// <summary>
    /// <b>RP-2a — the negative terminal is a second drawn signal conductor.</b> The same two-cut
    /// object <see cref="CoplanarGround"/> is; see its remark for why both members exist.
    /// <para><b>This is not a differential-mode decomposition.</b> It is one port driven between two
    /// conductors. Multi-mode ports stay §10.6's later work.</para>
    /// </summary>
    SecondConductor,
    /// <summary>
    /// <b>L9d — a port driven BETWEEN the two levels a via joins</b>, i.e. §0.2 item 2's option (b).
    /// Named so the refusal can be worded against it; see <see cref="PlanarPorts"/> for the
    /// argument and for where the capability actually arrives (§10.6's co-simulation ports, which
    /// are not L9's).
    /// </summary>
    ViaBetweenLevels,
}

/// <summary>
/// A port on the planar structure: an end of a conductor, a reference impedance, and nothing else.
/// Neutral in the R-mom-1 sense — metres and ohms, no DBU, no <c>.clay</c>, no layer table.
/// </summary>
/// <param name="Number">1-based, and the order the s-parameter matrix is indexed in.</param>
/// <param name="Location">A point on (or just inside) the conductor.
/// <para>For an <see cref="PlanarPortKind.Edge"/> port only its TRANSVERSE coordinate is used to pick
/// the conductor run; the longitudinal one is not, because D2 fixes the cut at the outermost row.
/// For an <see cref="PlanarPortKind.InternalDeltaGap"/> port <b>both</b> coordinates are used — the
/// transverse one picks the run and the longitudinal one picks the cut — and the resolution reports
/// how far the chosen gridline landed from the point that was asked for.</para></param>
/// <param name="Side">Which end of the conductor this is — and, for an
/// <see cref="PlanarPortKind.InternalDeltaGap"/> port where there is no "end", which way positive
/// port current flows across the cut. Either way it is the sign of
/// <see cref="IncidenceSign"/> and getting it wrong is a hard π in the transmission phase.</param>
/// <param name="Z0">Reference impedance. Complex is allowed because
/// <c>RFNetwork.SToS</c> already handles it and refusing it here would be a gratuitous narrowing.</param>
/// <param name="LayerIndex">
/// <b>L9d/D2 — a port's LEVEL is part of its identity, and null means "infer it".</b>
///
/// <para>Through L9c this was a plain <c>int</c> defaulting to 0 and had never been given a non-zero
/// value. With more than one level a cut at a given (x, y) can intersect metal on several of them,
/// and picking one silently is exactly the shape of failure R-mom-17 exists to prevent — so the
/// default is now <b>null = infer</b>: exactly one candidate level resolves, or the port is refused
/// by name listing every level it could have meant. An explicit index is honoured as given (the
/// caller has disambiguated) and refused by name if that level carries no conductor there.</para>
///
/// <para>Every pre-L9d construction site passes nothing and every one-level mesh has exactly one
/// candidate, so inference reproduces the old behaviour exactly.</para>
/// </param>
/// <param name="GroundPathWidthM">
/// <b><see cref="PlanarPortKind.Internal"/> ports only: the side of the square path down to the
/// ground plane this port may GROW for itself, when the artwork has no via under it.</b> Metres;
/// null means grow nothing and refuse instead.
///
/// <para>A port to ground needs a conductor to ground: the current it drives has to leave the metal
/// somehow, and in this kernel only a via basis carries vertical current. Requiring the user to draw
/// one is honest but is a chore for what is, geometrically, one cell — so the solver grows it
/// (<see cref="PlanarGroundPath"/>), by the same rule R-fed-1's calibration feed follows: created
/// before meshing, REPORTED by name, and never at the expense of metal the user drew. A drawn via
/// always wins; this only fills in where there is none.</para>
///
/// <para><b>It is a real conductor and its inductance is in the answer</b>, which is why the width is
/// stated rather than assumed: the caller passes the technology's own default via size, so what gets
/// built is the via that board would actually have. Null keeps the strict behaviour — a port with no
/// via under it is refused by name — which is what a headless caller that has no technology to ask
/// gets.</para>
/// </param>
/// <param name="Kind">
/// <b>Edge (the default) or an interior delta gap.</b> An edge port names an END of a conductor and
/// its cut is fixed at the outermost cell pair (D2); an internal one names a POINT ON the metal and
/// its cut is the mesh gridline nearest that point, with metal on both sides. Every construction
/// site that predates this parameter passes nothing and gets exactly the port it always got.
/// </param>
/// <param name="NegativeLocation">
/// <b>RP-2a — a point on the RETURN conductor, for a <see cref="PlanarPortReference.CoplanarGround"/>
/// or <see cref="PlanarPortReference.SecondConductor"/> port. Null for every ground-referenced
/// port, which is every port that predates RP-2a.</b>
///
/// <para>It is resolved exactly the way <see cref="Location"/> is — the same
/// <c>TryResolveOnLayer</c>, the same nearest-gridline snap, the same worded refusals — because a
/// second resolution rule is a second chance for the two terminals to land somewhere nobody
/// intended (RP-2's R-rp2-2). The two cuts must then be at the same station, on the same level and
/// in different conductors, and each of those is refused by name rather than adjusted.</para>
/// </param>
/// <param name="NegativeLayerIndex">
/// The return terminal's level, on <see cref="LayerIndex"/>'s terms: null = infer, an explicit index
/// is honoured as given. <b>Stating BOTH levels is what permits a port whose two cuts are on
/// different levels</b>; inferring them and landing on two levels is refused, because two terminals
/// is two chances to do that silently (RP-2a's R-rp2a-3, L9d/D2's reason one level up).
/// </param>
public sealed record PlanarPort(
    int                 Number,
    EmPoint             Location,
    PlanarPortSide      Side,
    Complex             Z0,
    int?                LayerIndex = null,
    PlanarPortReference Reference  = PlanarPortReference.GroundPlane,
    PlanarPortKind      Kind       = PlanarPortKind.Edge,
    double?             GroundPathWidthM = null,
    EmPoint?            NegativeLocation = null,
    int?                NegativeLayerIndex = null)
{
    /// <summary>RP-2a — whether this port's negative terminal is a drawn conductor rather than the
    /// stackup's ground plane. The two members that say so are one object here (R-rp2a-12).</summary>
    public bool IsConductorReferenced =>
        Reference is PlanarPortReference.CoplanarGround or PlanarPortReference.SecondConductor;

    /// <summary>The basis direction the port's row is drawn from. <b>Z for an
    /// <see cref="PlanarPortKind.Internal"/> port</b>, whose current leaves the plane down a
    /// via rather than running along the metal — which is why <see cref="Side"/> means nothing to
    /// one and is not read.</summary>
    public PlanarBasisDirection Direction =>
        Kind == PlanarPortKind.Internal ? PlanarBasisDirection.Z
        : Side is PlanarPortSide.MinX or PlanarPortSide.MaxX
            ? PlanarBasisDirection.X
            : PlanarBasisDirection.Y;

    /// <summary>
    /// D1's ±1. A rooftop's current is positive along +x̂ (or +ŷ); current flowing INTO the structure
    /// is +x̂ at a MinX port and −x̂ at a MaxX one. The SAME sign is used for the excitation and for
    /// reading the current back — that is what makes <c>Y = BᵀZ⁻¹B</c> the actual admittance matrix
    /// rather than a sign-scrambled relative of one.
    /// </summary>
    /// <para><b>An internal port's sign is +1 and is not the user's to set</b> — it is the same "current
    /// flows INTO the structure" convention every other port here uses, one dimension over. Every
    /// vertical basis's current flows +z, from the plane up to the metal (see
    /// <c>PlanarBasisFunctions</c>' header), so a positive port current enters the metal from the
    /// plane, which puts + on the METAL and − on the ground plane.</para>
    ///
    /// <para><b>Measured rather than reasoned into place.</b> A short line with a via to ground at
    /// its centre is three 50 Ω ports meeting at one node above the plane, whose s-matrix is
    /// S_ii = −1/3, S_ij = +2/3 — and it comes back that way at +1. The other sign turns every term
    /// through this port by π and changes nothing else, which is a complete and plausible s-matrix
    /// that no magnitude plot would question; the derivation of which lip is "+" is exactly the step
    /// that is easy to write down backwards, so it is gated
    /// (<c>InternalPortTests.ASmallStructureAtLowFrequencyIsATHREEWAYNODE…</c>).</para>
    public double IncidenceSign =>
        Kind == PlanarPortKind.Internal ? +1.0
        : Side is PlanarPortSide.MinX or PlanarPortSide.MinY ? +1.0 : -1.0;
}

/// <summary>
/// <b>RP-2a — the RETURN terminal of a conductor-referenced port: the second cut, field for field.</b>
///
/// <para><see cref="PlanarPortResolution"/>'s cross-section fields are all singular — one conductor,
/// one cut, one width — and R-rp2a-10 says that record GROWS rather than forks. So this carries the
/// negative terminal's counterparts of exactly those fields and hangs off the resolution as a
/// nullable companion: a ground-referenced resolution is the record it has always been, with a null
/// here, and every consumer that never asks reads the same bits it always read.</para>
///
/// <para>There is deliberately no <c>Side</c>, <c>Direction</c>, <c>Z0</c> or <c>IncidenceSign</c>
/// here. A port has ONE polarity and ONE reference impedance; the negative terminal's sign is the
/// positive one's negated, which is a property of the port rather than of the terminal, and putting
/// a second copy of it here is how the two would drift apart.</para>
/// </summary>
public sealed record PlanarPortTerminal(
    IReadOnlyList<int>    BasisIndices,
    double                WidthM,
    double                ReferencePlaneM,
    double                OuterEdgeM,
    IReadOnlyList<double> TransverseLines,
    IReadOnlyList<double> LongitudinalRunM,
    int                   LayerIndex,
    int                   CutCellCount   = 0,
    double                GridWidthM     = 0,
    double                UndrivenMetalM = 0,
    double                GapOffsetM     = 0)
{
    public int BasisCount => BasisIndices.Count;

    /// <summary>The negative terminal is resolved by the SAME routine the positive one is, so it
    /// arrives as a full resolution and is narrowed here. Reusing that routine is R-rp2-2.</summary>
    internal static PlanarPortTerminal From(PlanarPortResolution r) =>
        new(r.BasisIndices, r.WidthM, r.ReferencePlaneM, r.OuterEdgeM, r.TransverseLines,
            r.LongitudinalRunM, r.LayerIndex, r.CutCellCount, r.GridWidthM, r.UndrivenMetalM,
            r.GapOffsetM);
}

/// <summary>
/// <b>RP-2c — the transverse metal profile at a conductor-referenced EDGE port's reference plane,
/// which is the thing its calibration standard has to rebuild.</b>
///
/// <para>A ground-referenced port's neighbourhood is ONE conductor over a plane, and
/// <see cref="PlanarPortResolution.TransverseLines"/> describes it completely — which is why D4 can
/// copy those lines into a rectangle and call it the port's error box. A coplanar port's is not:
/// it is two pieces of metal with a slot between them, and calibrating it against a rectangle
/// produces s-parameters that are plausible and referenced to nothing (RP-2's R-rp2-4). So the
/// profile is captured HERE, from the DUT's own mesh at the cut, and the standard is built from
/// it — the same D4 rule, over a cross-section that is no longer connected.</para>
///
/// <para><b>It is the mesh's profile rather than the artwork's</b>, for the reason
/// <c>SameConductor</c> gives one screen up: what the solve drives is cells, so what the standard
/// must reproduce is cells. <see cref="Lines"/> are the DUT's own transverse gridlines, trimmed to
/// the outermost metal — a void interval outside every conductor carries no cell and no basis, so
/// including it would only make the standard's grid wider than its metal.</para>
/// </summary>
/// <param name="Lines">Transverse gridlines, ascending; <c>IsMetal.Count + 1</c> of them.</param>
/// <param name="IsMetal">Per interval: is there metal spanning the reference plane there.</param>
/// <param name="PositiveLo">First interval of the conductor the + terminal cut.</param>
/// <param name="PositiveHi">Last interval of it, inclusive.</param>
/// <param name="NegativeLo">First interval of the RETURN conductor.</param>
/// <param name="NegativeHi">Last interval of it, inclusive.</param>
/// <param name="ConductorCount">How many separate runs of metal cross the plane here. <b>Two is the
/// coplanar pair the port drives; more is a third conductor whose potential nobody stated</b>, and
/// the calibration refuses it by name rather than choosing one for it.</param>
public sealed record PlanarPortCrossSection(
    IReadOnlyList<double> Lines,
    IReadOnlyList<bool>   IsMetal,
    int                   PositiveLo,
    int                   PositiveHi,
    int                   NegativeLo,
    int                   NegativeHi,
    int                   ConductorCount)
{
    /// <summary>The whole profile's transverse span — what <c>CheckFeedClearance</c> must not
    /// mistake for a neighbour, because every bit of it IS reproduced in the standard.</summary>
    public double SpanLoM => Lines[0];
    /// <inheritdoc cref="SpanLoM"/>
    public double SpanHiM => Lines[^1];

    /// <summary>A transverse coordinate inside the + conductor — where the standard's own signal cut
    /// is placed.</summary>
    public double PositiveCentreM => 0.5 * (Lines[PositiveLo] + Lines[PositiveHi + 1]);
    /// <inheritdoc cref="PositiveCentreM"/>
    public double NegativeCentreM => 0.5 * (Lines[NegativeLo] + Lines[NegativeHi + 1]);

    /// <summary>The slot width between the two driven conductors — reported, because it is the one
    /// dimension of a coplanar port that has no counterpart in a microstrip one.</summary>
    public double SlotM => PositiveHi < NegativeLo
        ? Lines[NegativeLo] - Lines[PositiveHi + 1]
        : Lines[PositiveLo] - Lines[NegativeHi + 1];
}

/// <summary>
/// <b>PCAL3 — the transverse metal profile a calibrated feed's standard has to rebuild when the feed
/// is NOT alone: the port's own conductor plus every PASSIVE neighbour inside the clearance
/// distance.</b>
///
/// <para>It is <see cref="PlanarPortCrossSection"/>'s shape, one requirement lighter and one
/// requirement heavier. Lighter, because nothing here is driven but the port's own run — a passive
/// neighbour has no port, leaves one driven mode at the reference plane, and so the error box stays
/// the per-port scalar D6 already solves for. Heavier, because the profile is no longer read off one
/// gridline: a standard is a uniform extrusion, so every conductor in it has to be uniform over the
/// run the standard reproduces, and one that is not is DECLINED by name rather than guessed at
/// (R-pcal3-2).</para>
///
/// <para><b>The neighbour is reproduced from the DUT's own gridlines, exactly as the port's own
/// conductor already is</b> — same rule as D4, one conductor wider. <see cref="IsMetal"/> is false
/// over the gap between them, so no basis crosses it and the standard's mesh has the slot the DUT
/// has.</para>
///
/// <para><b>Why the port's own conductor is not simply "interval OwnLo..OwnHi of a cross-section":</b>
/// <see cref="PlanarPortResolution.CrossSection"/> being non-null is what makes a port
/// conductor-referenced, drives a two-cut standard and puts ±½ V on two conductors. None of that is
/// true here — this port drives ONE cut against the ground plane and the neighbour is driven by
/// nothing — so it is a separate field rather than a reuse that would have to be told apart
/// everywhere it is read.</para>
/// </summary>
/// <param name="Lines">Transverse gridlines, ascending; <c>IsMetal.Count + 1</c> of them. The DUT's
/// own, verbatim.</param>
/// <param name="IsMetal">Per interval: is there metal spanning the reference plane there.</param>
/// <param name="OwnLo">First interval of the port's OWN conductor.</param>
/// <param name="OwnHi">Last interval of it, inclusive.</param>
/// <param name="NeighbourCount">How many separate passive conductors were taken in. Reported,
/// because "the standard got wider" is a cost and the user is entitled to know what bought it.</param>
/// <param name="NearestM">The nearest included neighbour's lateral gap from the port's own
/// conductor — the distance that would have been the breach.</param>
public sealed record PlanarPortNeighbourhood(
    IReadOnlyList<double> Lines,
    IReadOnlyList<bool>   IsMetal,
    int                   OwnLo,
    int                   OwnHi,
    int                   NeighbourCount,
    double                NearestM)
{
    /// <summary>The whole profile's transverse span — what <c>MeasureFeedClearance</c> must not
    /// report as a neighbour, because every bit of it IS reproduced in the standard.</summary>
    public double SpanLoM => Lines[0];
    /// <inheritdoc cref="SpanLoM"/>
    public double SpanHiM => Lines[^1];

    /// <summary>A transverse coordinate inside the port's own conductor — where the standard's own
    /// two cuts are placed.</summary>
    public double OwnCentreM => 0.5 * (Lines[OwnLo] + Lines[OwnHi + 1]);

    /// <summary>Is this interval part of the port's own conductor? The electrostatic problem D7
    /// solves on the standard is driven on exactly these cells and on no others.</summary>
    public bool IsOwn(int interval) => interval >= OwnLo && interval <= OwnHi;

    /// <summary>What the widening did, for the run's notes.</summary>
    public string Describe(SurfaceMesher.PlanarLengthFormat? fmt = null)
    {
        var f = fmt ?? (v => SurfaceMesher.Eng(v) + "m");
        return
            $"Its calibration standard reproduces {NeighbourCount} neighbouring conductor(s) beside " +
            $"the feed, the nearest {f(NearestM)} away, over the whole of the standard's run: the " +
            $"profile spans {f(SpanLoM)} to {f(SpanHiM)} across, against {f(Lines[OwnLo])} to " +
            $"{f(Lines[OwnHi + 1])} for the port's own conductor. The neighbour carries no port, so " +
            "the error box is still the scalar one; it is present in the standard and driven by " +
            "nothing, exactly as it is in the structure.";
    }
}

/// <summary>
/// <b>PCAL4 — a CALIBRATION GROUP: the ports whose feeds are mutually within the clearance distance
/// at one reference plane, and the transverse profile their shared standard reproduces.</b>
///
/// <para>R-pcal4-1. Two ports on conductors that are coupled at the plane support TWO modes there,
/// and no assignment of scalars to those two ports can represent that — so the GROUP, not the port,
/// becomes the unit of calibration: one shared standard carrying every conductor, one modal error
/// box, and <see cref="PlanarModalCalibration"/>'s N×N blocks in place of D6's three scalars. <b>A
/// group of one is today's case and takes today's code path</b>, unchanged and bit for bit: this
/// record is null on every port that is not in a multi-conductor group.</para>
///
/// <para>It is deliberately NOT <see cref="PlanarPortNeighbourhood"/> with a flag. That record says
/// "there is metal in my standard that nothing drives", and every site that reads it — the floating
/// electrostatic constraint, the single-cut standard ports, the resonance guard — is wrong for a
/// conductor that carries a port of its own. The two shapes look alike and mean opposite things
/// about who is driven, which is exactly the distinction PCAL1 measured at a factor of 2-3 in
/// required clearance.</para>
/// </summary>
/// <param name="Lines">Transverse gridlines, ascending, spanning every conductor of the group —
/// the DUT's own, verbatim, exactly as D4 takes them for one conductor.</param>
/// <param name="ConductorOf">Per interval: which conductor of the group, or −1 for the gaps.</param>
/// <param name="PortNumbers">The DUT port standing on each conductor, in the same order — so
/// conductor k of the standard is port <c>PortNumbers[k]</c> of the run.</param>
/// <param name="NearestM">The smallest lateral gap inside the group: the distance that would have
/// been the breach.</param>
public sealed record PlanarPortGroupProfile(
    IReadOnlyList<double> Lines,
    IReadOnlyList<int>    ConductorOf,
    IReadOnlyList<int>    PortNumbers,
    double                NearestM)
{
    /// <summary>How many conductors, hence how many modes and how many ports.</summary>
    public int ConductorCount => PortNumbers.Count;

    /// <summary>The whole profile's transverse span — what <c>MeasureFeedClearance</c> must not
    /// report as a neighbour, because every bit of it IS reproduced in the standard.</summary>
    public double SpanLoM => Lines[0];
    /// <inheritdoc cref="SpanLoM"/>
    public double SpanHiM => Lines[^1];

    /// <summary>Which conductor of the group a given port number stands on, or −1.</summary>
    public int IndexOfPort(int portNumber)
    {
        for (int k = 0; k < PortNumbers.Count; k++) if (PortNumbers[k] == portNumber) return k;
        return -1;
    }

    /// <summary>A transverse coordinate inside conductor k — where the standard's own cut goes.</summary>
    public double CentreM(int conductor)
    {
        int lo = -1, hi = -1;
        for (int t = 0; t < ConductorOf.Count; t++)
            if (ConductorOf[t] == conductor) { if (lo < 0) lo = t; hi = t; }
        return lo < 0 ? Lines[0] : 0.5 * (Lines[lo] + Lines[hi + 1]);
    }

    /// <summary>What the grouping did, for the run's notes.</summary>
    public string Describe(SurfaceMesher.PlanarLengthFormat? fmt = null)
    {
        var f = fmt ?? (v => SurfaceMesher.Eng(v) + "m");
        return
            $"Ports {string.Join(", ", PortNumbers)} form one CALIBRATION GROUP: their feeds are " +
            $"mutually coupled at the reference plane, the nearest pair {f(NearestM)} apart. They " +
            $"share one {ConductorCount}-conductor calibration standard and one MODAL error box of " +
            $"{ConductorCount}×{ConductorCount} blocks, because {ConductorCount} coupled conductors " +
            $"support {ConductorCount} modes there and a per-port scalar box cannot represent them. " +
            $"The profile spans {f(SpanLoM)} to {f(SpanHiM)} across.";
    }
}

/// <summary>
/// <b>PCAL2 — what the nearest piece of metal beside a calibrated feed IS.</b> PCAL1 measured four
/// candidate cases and found three of them distinct; the fourth, a flare or pad on the port's OWN
/// net, is <see cref="PlanarFeedExtension"/>'s job and is not a neighbour at all, so it never
/// reaches this enum.
/// </summary>
public enum PlanarNeighbourClass
{
    /// <summary>No other conductor inside the run of line the calibration standard reproduces.</summary>
    None,

    /// <summary>A separate conductor carrying no port — a passive trace, or a ground pour, which
    /// PCAL1 measured to be the same case to within 2 %: the neighbour's own width does not enter.
    /// One driven mode at the reference plane, and ≈ 2 substrate heights of clearance needed.</summary>
    Passive,

    /// <summary>A separate conductor carrying a port of its own. Two modes at the reference plane
    /// against a per-port SCALAR error box, and 4-5.5 substrate heights of clearance needed — 2-3×
    /// the passive case.</summary>
    Driven,
}

/// <summary>
/// <b>PCAL2/R-pcal2-5 — how much clear space one calibrated feed has, reported on every run that
/// de-embeds.</b> A pass/fail with no distance is how a threshold change becomes invisible.
///
/// <para><b>The margin is in SUBSTRATE HEIGHTS and there is deliberately no error bound.</b> PCAL1
/// looked for one and the negative result is structural: <c>PlanarDeembed.SolveErrorBox</c>'s
/// arguments are the two calibration standards and not the DUT, so its consistency and rejected
/// residuals are bit-for-bit identical between a run that is 22 dB wrong and one at the floor. σ_max
/// detects the failure well and estimates it not at all — it is non-monotonic in the error, with the
/// worst case measured reporting a SMALLER passivity excess than a case with half the error. So the
/// only honest quantitative thing to report is the geometry, in the variable the error was measured
/// to follow.</para>
/// </summary>
/// <param name="PortNumber">The port this is about.</param>
/// <param name="Neighbour">What the nearest other conductor is, or <see cref="PlanarNeighbourClass.None"/>.</param>
/// <param name="NearestM">Its lateral distance from the port's own profile. Infinity when there is none.</param>
/// <param name="RequiredM">What <see cref="Neighbour"/>'s class needs, in metres.</param>
/// <param name="SubstrateHeightM">h, so the margin can be stated in the units the error follows. 0
/// when the caller did not supply it, which leaves <see cref="Heights"/> NaN and decides nothing.</param>
/// <param name="EndRunM">The run of line the calibration standard reproduces — the region scanned.</param>
public sealed record PlanarFeedClearance(
    int                  PortNumber,
    PlanarNeighbourClass Neighbour,
    double               NearestM,
    double               RequiredM,
    double               SubstrateHeightM,
    double               EndRunM)
{
    /// <summary>Is the calibration being applied outside the condition it is valid under?</summary>
    public bool Breached => Neighbour != PlanarNeighbourClass.None && NearestM < RequiredM;

    /// <summary>The margin, in substrate heights — the variable PCAL1 measured the error to follow.</summary>
    public double Heights => SubstrateHeightM > 0 ? NearestM / SubstrateHeightM : double.NaN;

    /// <inheritdoc cref="Heights"/>
    public double RequiredHeights => SubstrateHeightM > 0 ? RequiredM / SubstrateHeightM : double.NaN;

    private string Which => Neighbour == PlanarNeighbourClass.Driven
        ? "carries a port of its own"
        : "carries no port";

    private string Needs => double.IsNaN(RequiredHeights)
        ? $"needs {Fmt(RequiredM, null)}"
        : $"needs {RequiredHeights:0.#}";

    private static string Fmt(double metres, SurfaceMesher.PlanarLengthFormat? fmt)
        => (fmt ?? SurfaceMesher.DefaultLengthFormat)(metres);

    /// <summary>R-pcal2-5 — the one line every de-embedded port reports, breached or not.</summary>
    public string Margin(SurfaceMesher.PlanarLengthFormat? fmt = null)
        => Neighbour == PlanarNeighbourClass.None
            ? $"Port {PortNumber}'s feed is clear: no other conductor within the {Fmt(EndRunM, fmt)} " +
              "of line the calibration standard reproduces."
            : $"Port {PortNumber}'s feed clearance is " +
              (double.IsNaN(Heights) ? Fmt(NearestM, fmt) : $"{Heights:0.##} substrate heights") +
              $" — {Fmt(NearestM, fmt)} to the nearest other conductor, which {Which} and so {Needs}.";

    /// <summary>
    /// R-pcal2-1 — <b>one port's half of the refusal: the port, the distance, and what it needed.</b>
    /// The explanation and the remedies are said once for the whole run by
    /// <see cref="RefusalFor"/>, because four ports of one coupled pair breach together and four
    /// copies of the same three paragraphs is a message nobody reads to the end of.
    /// </summary>
    public string Breach(SurfaceMesher.PlanarLengthFormat? fmt = null)
        => $"port {PortNumber} has other metal {Fmt(NearestM, fmt)} away" +
           (double.IsNaN(Heights) ? "" : $" ({Heights:0.##} substrate heights, against the " +
                                         $"{RequiredHeights:0.#} a neighbour that {Which} needs)");

    /// <summary>
    /// R-pcal2-1 — <b>the refusal.</b> The diagnosis is the sentence this check has always carried;
    /// what PCAL2 adds is the consequence and the way past it, which has to be reachable or this is
    /// a refusal that names a remedy nobody can follow.
    ///
    /// <para><b>There used to be TWO ways past, and the second one was wrong.</b> "Turn port
    /// de-embedding off and read the raw solve" is not a degraded answer at an edge port, it is an
    /// open circuit — measured at S11 = +1 and S21 = -107 dB on a plain microstrip whose de-embedded
    /// loss is a tenth of a dB. The switch it named has been removed;
    /// <c>EmSetup.LegacyRawSolveRequested</c> carries the whole finding.</para>
    /// </summary>
    public static string RefusalFor(IReadOnlyList<PlanarFeedClearance> breaches,
                                    SurfaceMesher.PlanarLengthFormat? fmt = null)
    {
        ArgumentNullException.ThrowIfNull(breaches);
        var parts = new List<string>(breaches.Count);
        foreach (var b in breaches) parts.Add(b.Breach(fmt));
        double endRun = breaches.Count > 0 ? breaches[0].EndRunM : 0;

        return (breaches.Count == 1 ? "A calibrated port's feed is not isolated: " : "Calibrated port feeds are not isolated: ") +
               string.Join("; ", parts) +
               $". That is inside the {Fmt(endRun, fmt)} of line the calibration standard " +
               "reproduces. The de-embedding replaces the port's neighbourhood with an isolated line " +
               "of the same width, so whatever is closer than that is not removed correctly — " +
               "measured on a coupled pair, 22 dB of error in S21 at the bottom of the band and an " +
               "answer that is not passive at 48 of 51 points, which used to be published with a " +
               "note and nothing on the file to carry it. Move the feed away from its neighbour, or " +
               "put the port where the line is already isolated. To get an answer out of this " +
               "geometry as drawn, turn ON \"de-embed outside the calibration's validity\", which " +
               "publishes the de-embedded answer and records on the Touchstone that the calibration " +
               "was applied outside it. (Reading the RAW solve used to be offered here as the other " +
               "way out. It is not one: at an edge port the raw answer is an open circuit, not a " +
               "degraded answer — see EmSetup.LegacyRawSolveRequested.)";
    }

    /// <summary>The pre-PCAL2 sentence, for the one-threshold
    /// <see cref="PlanarPorts.CheckFeedClearance"/> overload.</summary>
    public string LegacyWarning(SurfaceMesher.PlanarLengthFormat? fmt = null)
        => $"Port {PortNumber}'s feed has other metal {Fmt(NearestM, fmt)} away, inside the " +
           $"{Fmt(RequiredM, fmt)} the calibration standard assumes is empty. The de-embedding " +
           "replaces the port's neighbourhood with an isolated line of the same width, so whatever " +
           "is closer than that is not removed correctly. Move the feed away, or read the result " +
           "knowing this.";
}

/// <summary>
/// <b>PCAL2/R-pcal2-1 — a calibrated port's feed clearance is breached, so the run is REFUSED.</b>
/// Caught by the run service and reported as a refusal, exactly as
/// <see cref="PlanarMeshRefusedException"/> is: the precedent is the mesh ceiling, which already
/// stops a run that would take twenty minutes to produce nothing usable.
/// </summary>
public sealed class PlanarFeedClearanceRefusedException : InvalidOperationException
{
    public IReadOnlyList<PlanarFeedClearance> Breaches { get; }

    public PlanarFeedClearanceRefusedException(string message, IReadOnlyList<PlanarFeedClearance> breaches)
        : base(message) => Breaches = breaches;
}

/// <summary>
/// <b>Two ports resolved to one cut, so the run is REFUSED.</b> Caught by the run service and
/// reported as a refusal for the same reason <see cref="PlanarFeedClearanceRefusedException"/> is:
/// it is the user's geometry, not a fault in circuitRF, and "EngineError" would read as the latter.
/// See <see cref="PlanarPorts.ResolveAll"/> for the measurement that put it here.
/// </summary>
public sealed class PlanarPortCollisionRefusedException(string message, int firstPort, int secondPort)
    : InvalidOperationException(message)
{
    public int FirstPort  { get; } = firstPort;
    public int SecondPort { get; } = secondPort;
}

/// <summary>
/// What a port resolved to on a particular mesh — R-prt-2's report. Everything a user (or L8e's
/// panel) needs in order to see where the reference plane actually landed, and everything
/// <see cref="PlanarCalibration"/> needs in order to rebuild the port's neighbourhood exactly (D4).
/// </summary>
/// <param name="BasisIndices">Indices into <see cref="PlanarMesh.Bases"/>, in transverse order.</param>
/// <param name="WidthM">The resolved conductor width at the cut — <b>the metal actually on the
/// reference plane</b>, not the drawn width, so a staircased edge reports what was meshed and a
/// CONFORMAL boundary cell reports the metal rather than its grid rectangle. With no cut cell in the
/// port's run the two are the same subtraction and the number is bit-identical to L8d's.</param>
/// <param name="ReferencePlaneM">The coordinate of the shared edge the current crosses. D2.</param>
/// <param name="OuterEdgeM">The metal's own outer edge — one cell further out.</param>
/// <param name="TransverseLines">The port's own cross-section, <c>BasisIndices.Count + 1</c> lines.
/// D4 copies these into the calibration standard verbatim, which is why they are the METAL's extents
/// rather than the grid's whenever a boundary cell is cut: the standard is a uniform rectangle and it
/// has to be a rectangle of the DUT's own cross-section or the error box is not the same object.
/// Where nothing is cut they are the gridlines, copied verbatim as before.</param>
/// <param name="LongitudinalRunM">Cell sizes marching INWARD from the outer edge along the port's
/// own axis, as far as the conductor runs. D4 copies the first K of these.</param>
/// <param name="LayerIndex">L9d — the conductor level the cut actually landed on, inferred or
/// explicit. The de-embedding needs it (a standard is a single-level line on THIS level, D3) and so
/// does anything that has to say which level a reported quantity belongs to.</param>
/// <param name="CutCellCount">How many of the port's own cells the conformal mesher cut. Zero under
/// the staircase and on any Manhattan feed; non-zero says the width below is a metal width rather
/// than a grid one, and that the residual named in <see cref="PlanarPortResolution.Describe"/>
/// applies.</param>
/// <param name="GridWidthM">What the width WOULD have been on the grid — carried only so the report
/// can state the deficit as a number instead of a caveat. Equal to <c>WidthM</c> when nothing is
/// cut.</param>
/// <param name="Kind">Which kind of cut this is. An <see cref="PlanarPortKind.Edge"/> resolution
/// carries a feed outside the plane and is de-embedded; an <see cref="PlanarPortKind.InternalDeltaGap"/>
/// one has metal on both sides, is not de-embedded, and reports its s-parameters at the gap in its
/// own declared Z₀. Everything else in this record means the same thing for both.</param>
/// <param name="GapOffsetM">Internal ports only: <b>how far the gap actually landed from the point
/// that was asked for</b>, along the port's own axis. The cut can only be a mesh gridline, so a
/// requested position between two gridlines snaps to the nearer — and the distance it moved is
/// reported rather than left to be discovered, because it is bounded by half a cell and half a cell
/// is a quantity the user sets. Zero for an edge port, whose cut is fixed by D2 and is not asked
/// for at all.</param>
/// <param name="FootprintAreaM2"><b>Internal ports only: the meshed area of the via this port drives.</b>
/// An internal port's transverse dimension is an AREA rather than a width — its current crosses the via's
/// footprint, not a line across the metal — so <see cref="WidthM"/>, <see cref="ReferencePlaneM"/>
/// and <see cref="OuterEdgeM"/>, which are all in-plane lengths along a port's own axis, mean nothing
/// for one and are zero. This is the number that says how much via the port actually got: it is the
/// sum of the cell areas that carry a ground-attachment basis, so a footprint the mesh resolved into
/// fewer cells than the artwork drew reports the smaller number rather than the drawn one.</param>
/// <param name="UndrivenMetalM">Metal on the reference plane, adjacent to the port's own run, that
/// carries NO rooftop and is therefore not driven — R-cut-4 declining the outermost cell pair of a
/// conformal feed. Zero under the staircase and on any Manhattan feed. It is reported rather than
/// silently absorbed because it is the one thing a conformal port does WORSE than a staircased one,
/// and refining the transverse mesh is what shrinks it.</param>
/// <param name="CrossSection">
/// <b>RP-2c — the transverse metal profile at this port's reference plane</b>, for a
/// conductor-referenced EDGE port and null for every other port. <see cref="PlanarCalibration"/>
/// builds the coplanar standard from it exactly as it builds a microstrip standard from
/// <see cref="PlanarPortResolution.TransverseLines"/>; see <see cref="PlanarPortCrossSection"/> for
/// why one conductor's lines are not enough.
/// </param>
/// <param name="Neighbourhood">
/// <b>PCAL3 — the WIDENED transverse profile, when this feed has a passive neighbour close enough
/// that the standard has to contain it</b>, and null on every port whose feed is clear. It is not
/// produced by <see cref="PlanarPorts.Resolve"/>: resolving a port is a question about one port and
/// one mesh, and this one needs the run's whole port list (to tell a passive neighbour from a driven
/// one) and the slab (for the threshold), so <see cref="PlanarSolve"/> asks
/// <see cref="PlanarPorts.TryWidenForNeighbours"/> at setup and carries the answer back on the
/// resolution. A run whose feeds are all clear never takes that path and its ports are the records
/// they have always been (R-pcal3-4).
/// </param>
public sealed record PlanarPortResolution(
    int                    Number,
    PlanarPortSide         Side,
    PlanarBasisDirection   Direction,
    Complex                Z0,
    IReadOnlyList<int>     BasisIndices,
    double                 IncidenceSign,
    double                 WidthM,
    double                 ReferencePlaneM,
    double                 OuterEdgeM,
    IReadOnlyList<double>  TransverseLines,
    IReadOnlyList<double>  LongitudinalRunM,
    int                    LayerIndex     = 0,
    int                    CutCellCount   = 0,
    double                 GridWidthM     = 0,
    double                 UndrivenMetalM = 0,
    PlanarPortKind         Kind           = PlanarPortKind.Edge,
    double                 GapOffsetM     = 0,
    double                 FootprintAreaM2 = 0,
    PlanarPortReference    Reference       = PlanarPortReference.GroundPlane,
    PlanarPortTerminal?    Negative        = null,
    PlanarPortCrossSection? CrossSection   = null,
    PlanarPortNeighbourhood? Neighbourhood = null,
    PlanarPortGroupProfile? Group          = null)
{
    public int BasisCount => BasisIndices.Count;

    // ── RP-2a — THE NORMALISATION, AND THE MEASUREMENT THAT CHOSE IT ─────────────────────────────
    //
    // A two-cut port impresses a gap voltage on EACH of its two cuts, and the two add around the
    // loop: with ±w on the blocks the loop EMF is 2w, and the port current read back through the
    // same B is w·(I⁺ − I⁻) = 2w·I_line when the return conductor carries the whole return. So
    //
    //     Z₁₁ = v / i = Z_true / (4 w²)
    //
    // and only w = ½ makes the port report the impedance it is actually looking into. w = 1 gives a
    // complete, plausible s-matrix a QUARTER of the right impedance — no symptom, no warning, and
    // reciprocity and passivity both still hold. (The brief costed that error at a factor of two; it
    // is four, because w scales the excitation and the read-back alike.)
    //
    // The single-cut ground-referenced port is the same formula with ONE gap: EMF = w, i = w·I_line,
    // Z₁₁ = Z_true/w², so its w = 1 — which is why nothing about it moves and why the two are not
    // "the same constant" by coincidence.
    //
    // **MEASURED, not reasoned into place** (R-rp2a-5). The reasoning above is exactly the shape of
    // step this file already records getting backwards once (the internal port's SIGN, derived in
    // prose and caught only by a structure with a known answer). The gate is a coplanar-strips line
    // against the conformal-mapping closed form Z = η₀·K(k)/K(k′), which is exact for the ideal
    // structure and independent of everything here:
    // TwoCutPortTests.Gate2_ACoplanarStripLine_MatchesTheConformalMappingClosedForm.
    /// <summary>RP-2a — each cut of a two-cut port carries HALF the port's voltage. See the note
    /// above for the derivation and for the measurement that selected it.</summary>
    public const double TwoCutTerminalWeight = 0.5;

    /// <summary>
    /// <b>The incidence weight on this port's POSITIVE block.</b> For every ground-referenced port
    /// this is <see cref="IncidenceSign"/> itself, bit for bit, which is what makes RP-2a's
    /// bit-identity gate a statement about arithmetic rather than about tolerance.
    /// </summary>
    public double PositiveWeight => Negative is null ? IncidenceSign : IncidenceSign * TwoCutTerminalWeight;

    /// <summary>The weight on the NEGATIVE block — the positive one negated, so the two gaps add
    /// around the loop. Meaningless (and unread) when <see cref="Negative"/> is null.</summary>
    public double NegativeWeight => -PositiveWeight;

    /// <summary>RP-2a — whether this port drives a second cut in a drawn return conductor.</summary>
    public bool IsConductorReferenced => Negative is not null;

    /// <summary>
    /// <b>Whether the two-line calibration means anything for this port.</b> An edge port has a feed
    /// outside its cut, so it has an error box and a Z_c; an internal delta gap has metal on both
    /// sides and neither. Asked in exactly one place (<c>PlanarSolve</c>) so the two kinds cannot
    /// drift apart, and named rather than written as an enum comparison at each site.
    /// </summary>
    public bool IsDeembeddable => Kind == PlanarPortKind.Edge;

    /// <summary>The bulk (largest) cell along the port's axis, which is what D4 fills a standard's
    /// middle with so the standard's line and the DUT's feed are discretised identically.</summary>
    public double BulkCellM
    {
        get
        {
            double m = 0;
            foreach (double d in LongitudinalRunM) m = Math.Max(m, d);
            return m;
        }
    }

    /// <summary>R-prt-2's one-line summary, for the notes.</summary>
    public string Describe() =>
        (Kind switch
        {
            PlanarPortKind.InternalDeltaGap => DescribeDeltaGap(),
            PlanarPortKind.Internal    => DescribeInternal(),
            _                               => DescribeEdge(),
        }) +
        (CutCellCount == 0 && UndrivenMetalM <= 0 ? "" : ConformalNote()) +
        (Negative is null ? "" : DescribeReturn());

    /// <summary>
    /// <b>RP-2a — what this port RETURNS through, said per port.</b>
    ///
    /// <para>The kernel's standing sentence is that the stackup's ground plane is the negative
    /// terminal of every port in a run and is not selectable per port. That is false of this one,
    /// and a run can now mix the two, so the fact has to travel with the port rather than with the
    /// run — a reader of a mixed s-matrix otherwise has no way to tell which reference each column
    /// is in.</para>
    /// </summary>
    private string DescribeReturn()
    {
        var n = Negative!;
        return
            $" Its negative terminal is NOT the ground plane: it is a second cut, of " +
            $"{n.BasisCount} basis function(s) spanning {SurfaceMesher.Eng(n.WidthM)}m, in the " +
            (Reference == PlanarPortReference.CoplanarGround
                ? "coplanar ground conductor "
                : "second signal conductor ") +
            $"you named, at the same station ({(Direction == PlanarBasisDirection.X ? "x" : "y")} = " +
            $"{SurfaceMesher.Eng(n.ReferencePlaneM)}m) on level {n.LayerIndex}. The two cuts are " +
            "driven against each other, each carrying half the port's voltage, so the current this " +
            "port measures is the loop current between those two conductors and the return path is " +
            "the metal you drew rather than the plane underneath it.";
    }

    /// <summary>
    /// The internal port's own report. Like the gap's it has to say that nothing is de-embedded here
    /// and why, but the two facts specific to THIS port are that its current leaves the metal
    /// vertically — so the answer is at the foot of a via and not at a plane across the trace — and
    /// how much of the via's footprint the mesh actually resolved, which is the quantity a coarse
    /// mesh silently shrinks.
    /// </summary>
    private string DescribeInternal() =>
        $"Port {Number} is an internal port: a gap at the foot of the via under it, between " +
        $"the metal and the ground plane. It drives {BasisCount} ground-attachment basis " +
        $"function(s) over {FootprintAreaM2 * 1e6:0.###} mm² of via footprint. Its + terminal is the " +
        "metal and its − terminal is the plane, so positive port current flows UP the via into the " +
        "conductor, the same way into the structure as every other port here. It is NOT de-embedded: the " +
        "gap has the ground plane on one side and the via on the other, so there is no feed line " +
        "outside it to remove and no line impedance to reference to. Its s-parameters are reported " +
        "at the foot of the via, in the reference impedance you declared for it.";

    private string DescribeEdge() =>
        $"Port {Number} resolved to {BasisCount} basis function(s) across " +
        $"{SurfaceMesher.Eng(WidthM)}m of conductor; reference plane at " +
        $"{(Direction == PlanarBasisDirection.X ? "x" : "y")} = {SurfaceMesher.Eng(ReferencePlaneM)}m, " +
        $"one cell in from the metal edge at {SurfaceMesher.Eng(OuterEdgeM)}m.";

    /// <summary>
    /// The internal port's own report. It says three things an edge port's does not, and every one
    /// of them is something a user would otherwise have to infer: that this is an interior cut with
    /// metal on both sides, WHERE the cut landed against where it was asked for, and that nothing is
    /// de-embedded here because there is no feed to remove.
    /// </summary>
    private string DescribeDeltaGap() =>
        $"Port {Number} is an internal delta gap across {BasisCount} basis function(s) spanning " +
        $"{SurfaceMesher.Eng(WidthM)}m of conductor; the gap is the interior cut at " +
        $"{(Direction == PlanarBasisDirection.X ? "x" : "y")} = {SurfaceMesher.Eng(ReferencePlaneM)}m, " +
        $"with metal on both sides" +
        (GapOffsetM > 0
            ? $" — {SurfaceMesher.Eng(GapOffsetM)}m from where it was placed, because a gap can only " +
              "be a mesh gridline and this was the nearest one. Refine the mesh there to move it closer."
            : ", exactly where it was placed.") +
        " It is NOT de-embedded: there is no feed outside an interior cut, so there is no port " +
        "discontinuity to remove and no line impedance to reference to. Its s-parameters are " +
        "reported at the gap itself, in the reference impedance you declared for it.";

    /// <summary>
    /// <b>What a CONFORMAL boundary cell at a port does and does not cost, as a number.</b>
    ///
    /// <para>This used to be a refusal. It is a note because the refusal's own premise — "a port
    /// belongs on a drawn feed, which is Manhattan, so this should never fire" — is false of the
    /// parts a user actually selects: a taper's flanks are oblique from its very first cell, so on
    /// MKlopf and MTaper the outermost cell of the port's transverse run is cut and the whole run
    /// was refused for it. What the cut actually changes is that the reference plane's own metal is
    /// shorter than the gridline, and that is now MEASURED and carried into the calibration standard
    /// (the standard is built from <see cref="TransverseLines"/>, which are the metal's extents) —
    /// so the error box is the same object again and the residual is the one every port already has:
    /// the feed is not perfectly uniform over the length the standard replaces.</para>
    /// </summary>
    private string ConformalNote()
    {
        string s = "";

        if (CutCellCount > 0)
            s += $" {CutCellCount} of its cell(s) follow the metal rather than the grid, so its " +
                 $"width is the metal on the reference plane ({SurfaceMesher.Eng(WidthM)}m) rather " +
                 $"than the grid extent ({SurfaceMesher.Eng(GridWidthM)}m) — and the calibration " +
                 "standard is built to that same cross-section, so the error box is the same object. " +
                 "What stays approximate is that a standard is a UNIFORM line while a cut feed is, " +
                 "by construction, tapering.";

        if (UndrivenMetalM > 0)
            s += $" A further {SurfaceMesher.Eng(UndrivenMetalM)}m of metal beside the port " +
                 $"({UndrivenMetalM / (UndrivenMetalM + WidthM):P1} of the feed at the plane) carries " +
                 "NO rooftop and is not driven: the outline crosses those cells obliquely and their " +
                 "shared edge does not sweep them, so a basis there would push current out through " +
                 "the metal's rim. This is the one thing a conformal port does WORSE than a " +
                 "staircased one, and raising Cells per wavelength is what shrinks it: the undriven " +
                 "cells are the outermost of the transverse run, so their share of the width falls as " +
                 "the run gets longer.";

        return s;
    }
}

/// <summary>
/// Port resolution: geometry in, a row of basis indices out — or a worded refusal (R-prt-2). Never a
/// silent snap to something nearby.
/// </summary>
public static class PlanarPorts
{
    /// <summary>Resolves, or throws with the refusal's own wording.</summary>
    public static PlanarPortResolution Resolve(PlanarMesh mesh, PlanarPort port)
    {
        if (!TryResolve(mesh, port, out var res, out string? refusal))
            throw new InvalidOperationException(refusal);
        return res!;
    }

    public static IReadOnlyList<PlanarPortResolution> ResolveAll(
        PlanarMesh mesh, IReadOnlyList<PlanarPort> ports)
    {
        ArgumentNullException.ThrowIfNull(ports);
        var list = new List<PlanarPortResolution>(ports.Count);
        foreach (var p in ports) list.Add(Resolve(mesh, p));
        RefuseCoincidentCuts(list);
        return list;
    }

    /// <summary>
    /// <b>TWO PORTS MAY NOT DRIVE THE SAME ROOFTOP ROW, AND UNTIL NOW NOTHING SAID SO</b> (user
    /// report, 2026-09-18).
    ///
    /// <para>This is the across-ports half of a rule the two-cut resolution already enforces WITHIN
    /// one port — see <c>TryResolveTwoCut</c>'s "landed on the SAME rooftop row" refusal. Two port
    /// labels on one cut of one conductor are one terminal wearing two numbers: the same basis
    /// functions are excited by both and read back by both, so the matrix is not a network of the
    /// ports the user thinks they drew.</para>
    ///
    /// <para><b>Measured, not reasoned into place.</b> A designer's imported board carried two port
    /// labels at bit-identical coordinates and the run wrote a complete <c>.s3p</c> with no
    /// complaint. Reproduced on the shipped Klopfenstein taper example by copying its port 2 onto
    /// itself as port 3: the clean, passive two-port became a three-port with 40 of 47 rows
    /// NON-PASSIVE (worst σ_max = 1.0428) whose own caveat says "what produced the gain is not
    /// identified here" — and the two coincident ports were even peeled by DIFFERENT feed-lead
    /// lengths (179.58 mil against 89.58 mil) for what is one piece of metal. A plausible wrong
    /// answer with an unattributable passivity violation is the exact failure shape this file
    /// refuses everywhere else.</para>
    ///
    /// <para><b>Only the POSITIVE cuts are compared.</b> A two-cut port's NEGATIVE terminal is
    /// deliberately left out: two ports sharing one return conductor's cut is a configuration
    /// someone may mean, and refusing it would be a narrowing nothing here has measured. What is
    /// refused is the case with no reading at all.</para>
    /// </summary>
    private static void RefuseCoincidentCuts(List<PlanarPortResolution> resolved)
    {
        for (int i = 0; i < resolved.Count; i++)
            for (int j = i + 1; j < resolved.Count; j++)
                if (Indistinguishable(resolved[i], resolved[j]))
                    throw new PlanarPortCollisionRefusedException(
                        $"Ports {resolved[i].Number} and {resolved[j].Number} resolved to the SAME " +
                        $"CUT \u2014 the same rooftop row, the same side, the same direction, on level " +
                        $"{resolved[i].LayerIndex}. That is one terminal wearing two numbers, not two " +
                        "ports: both excite those basis functions and both read them back, so the " +
                        "s-parameters would be a complete, plausible matrix of a structure nobody " +
                        "drew \u2014 the measured symptom is a network that is not passive, with nothing " +
                        "able to say why. Move one port to the terminal you meant it to be, or " +
                        "delete it; two labels at the same place on one conductor end is usually a " +
                        "duplicate.",
                        resolved[i].Number, resolved[j].Number);
    }

    /// <summary>
    /// Whether two resolutions are the same TERMINAL \u2014 every property that identifies where a port
    /// drives, equal.
    ///
    /// <para><b>Every clause below is load-bearing, and two of them were put there by fixtures that
    /// are legitimately close to this.</b> A shared basis index alone is NOT enough: on a
    /// deliberately coarse mesh the two ends of a short line share their one interior edge, and they
    /// are told apart by <see cref="PlanarPortResolution.Side"/> and
    /// <see cref="PlanarPortResolution.IncidenceSign"/> (<c>ModalErrorBoxTests</c>); two ports of a
    /// coupled pair at one reference plane sit on different conductors and are told apart by their
    /// transverse spans, and where a mesh is too coarse to separate them the MODAL calibration has
    /// its own, better-diagnosed refusal to make (<c>GroupSeparationRemedyTests</c>). So this asks
    /// for indistinguishability, not overlap.</para>
    /// </summary>
    private static bool Indistinguishable(PlanarPortResolution a, PlanarPortResolution b)
    {
        if (a.LayerIndex != b.LayerIndex || a.Kind != b.Kind || a.Side != b.Side) return false;
        if (a.IncidenceSign != b.IncidenceSign) return false;
        if (a.BasisIndices.Count != b.BasisIndices.Count || a.BasisIndices.Count == 0) return false;

        var set = new HashSet<int>(a.BasisIndices);
        foreach (int x in b.BasisIndices) if (!set.Contains(x)) return false;
        return true;
    }

    public static bool TryResolve(PlanarMesh mesh, PlanarPort port,
                                  out PlanarPortResolution? resolution, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(port);
        resolution = null;

        // ── RP-2a — a port referenced to DRAWN metal is two cuts, and it has its own routine ─────
        if (port.IsConductorReferenced)
            return TryResolveTwoCut(mesh, port, out resolution, out refusal);

        // ── D3: the plane is still the only reference the MEDIUM can provide ─────────────────────
        if (port.Reference != PlanarPortReference.GroundPlane)
        {
            // L9d/§0.2 item 2 — the one genuinely new port question that phase had to answer.
            refusal = ViaPortRefusal(port.Number);
            return false;
        }

        return TryResolveAgainstPlane(mesh, port, out resolution, out refusal);
    }

    /// <summary>
    /// The ground-referenced resolution — L9d's level inference and everything under it, unchanged.
    /// <b>Split out of <see cref="TryResolve"/> so RP-2a's two terminals can each go through it</b>,
    /// which is R-rp2-2: one resolution rule, used twice, rather than a second one that gets a
    /// chance to disagree with it.
    /// </summary>
    private static bool TryResolveAgainstPlane(PlanarMesh mesh, PlanarPort port,
                                               out PlanarPortResolution? resolution, out string? refusal)
    {
        resolution = null;

        // ── L9d/D2 — WHICH LEVEL, and never silently ─────────────────────────────────────────────
        if (port.LayerIndex is { } explicitLayer)
            return TryResolveOnLayer(mesh, port, explicitLayer, out resolution, out refusal);

        var candidates = new List<int>();
        PlanarPortResolution? first = null;
        string? firstRefusal = null;
        for (int li = 0; li < mesh.LayerNames.Count; li++)
        {
            if (!TryResolveOnLayer(mesh, port, li, out var r, out string? why))
            {
                firstRefusal ??= why;
                continue;
            }
            candidates.Add(li);
            first ??= r;
        }

        if (candidates.Count == 1) { resolution = first; refusal = null; return true; }

        if (candidates.Count == 0)
        {
            // Every level said no. A one-level mesh has exactly one reason, and it is the useful one;
            // a multi-level mesh gets the first level's reason plus the fact that no level worked.
            refusal = mesh.LayerNames.Count <= 1
                ? firstRefusal
                : $"Port {port.Number} at ({SurfaceMesher.Eng(port.Location.X)}m, " +
                  $"{SurfaceMesher.Eng(port.Location.Y)}m) does not resolve on ANY of this mesh's " +
                  $"{mesh.LayerNames.Count} conductor levels ({string.Join(", ", mesh.LayerNames)}). " +
                  $"The reason on level 0 was: {firstRefusal}";
            return false;
        }

        // D2 — ambiguous, refused BY NAME. The alternative is to take the lowest (or the topmost)
        // level, which is a coin flip that produces a complete, plausible s-parameter set for a
        // structure the user did not draw.
        refusal =
            $"Port {port.Number} at ({SurfaceMesher.Eng(port.Location.X)}m, " +
            $"{SurfaceMesher.Eng(port.Location.Y)}m) can be cut on " +
            $"{candidates.Count} conductor levels — " +
            string.Join(", ", candidates.Select(i => $"level {i} ('{mesh.LayerNames[i]}')")) +
            ". A port's LEVEL is part of its identity: driving the wrong one drives a different " +
            "conductor with the same footprint, which produces a complete and plausible answer for " +
            "a structure that was not drawn. Say which level this port is on.";
        return false;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // RP-2a — A PORT REFERENCED TO DRAWN METAL: TWO CUTS AT ONE STATION
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Resolves a conductor-referenced port: the signal cut, the return cut, and the four things
    /// that must be true of the pair.</b>
    ///
    /// <para>Both cuts go through <see cref="TryResolveAgainstPlane"/> — the SAME routine, including
    /// L9d's level inference and its ambiguity refusal (R-rp2-2/R-rp2a-3). What this adds is only
    /// the checks that are about the PAIR, and every one of them is a refusal rather than an
    /// adjustment: a skewed pair, a pair on two levels nobody stated, and a pair that is really one
    /// conductor all produce a complete and plausible s-matrix for a structure nobody drew.</para>
    /// </summary>
    private static bool TryResolveTwoCut(PlanarMesh mesh, PlanarPort port,
                                         out PlanarPortResolution? resolution, out string? refusal)
    {
        resolution = null;

        // ── AN INTERNAL (VIA-TO-PLANE) PORT TAKES NO REFERENCE — that is what the port IS ────────
        //
        // RP-2a refused an EDGE port here too, because a coplanar edge port's error box is a coplanar
        // line and PlanarCalibration built uniform single conductors over the plane. RP-2c builds the
        // coplanar standard (PlanarCalibration.BuildCoplanarLine), so the refusal is gone and what
        // replaces it is the CROSS-SECTION captured below — the profile that standard is built from.
        // The via port's refusal is untouched and is not the same kind of statement: it is about what
        // the port is, not about a missing capability.
        if (port.Kind == PlanarPortKind.Internal)
        {
            refusal =
                $"Port {port.Number} is an internal (via-to-plane) port asking to return through " +
                "drawn metal. Its negative terminal is the ground plane by construction — that is " +
                "what the port IS, a gap at the foot of the via that reaches the plane — so there " +
                "is nothing for a second cut to be. A port between two pieces of drawn metal is an " +
                "internal delta gap with a return conductor named; a port from metal to the plane " +
                "is this one, and it takes no reference.";
            return false;
        }

        if (port.NegativeLocation is not { } negPoint)
        {
            refusal =
                $"Port {port.Number} names a {(port.Reference == PlanarPortReference.CoplanarGround ? "coplanar ground" : "second-conductor")} " +
                "reference but does not say WHERE the return conductor is cut. A conductor-referenced " +
                "port is two cuts driven against each other, so it needs a point on the return metal " +
                "as well as one on the signal metal; there is no nearest-conductor search, because " +
                "picking the return silently is picking which loop the answer is about.";
            return false;
        }

        // ── The two terminals, each through the one resolution rule ──────────────────────────────
        if (!TryResolveAgainstPlane(mesh, port with { Reference = PlanarPortReference.GroundPlane },
                                    out var pos, out refusal))
            return false;

        var negProbe = port with
        {
            Reference  = PlanarPortReference.GroundPlane,
            Location   = negPoint,
            LayerIndex = port.NegativeLayerIndex,
        };
        if (!TryResolveAgainstPlane(mesh, negProbe, out var neg, out string? negWhy))
        {
            // The inner refusal is worded for a port, and here it is worded for a TERMINAL — without
            // saying which, a user reading "Port 3 is not on any conductor" has two places to look.
            refusal = $"Port {port.Number}'s RETURN terminal, at " +
                      $"({SurfaceMesher.Eng(negPoint.X)}m, {SurfaceMesher.Eng(negPoint.Y)}m), did not " +
                      $"resolve. {negWhy}";
            return false;
        }

        // ── R-rp2a-3 — ONE LEVEL, unless the caller stated both ──────────────────────────────────
        if (pos!.LayerIndex != neg!.LayerIndex &&
            !(port.LayerIndex.HasValue && port.NegativeLayerIndex.HasValue))
        {
            refusal =
                $"Port {port.Number}'s two cuts were INFERRED onto different levels: the signal cut " +
                $"on level {pos.LayerIndex}{LayerName(mesh, pos.LayerIndex)} and the return cut on " +
                $"level {neg.LayerIndex}{LayerName(mesh, neg.LayerIndex)}. A port's level is part of " +
                "its identity and two terminals is two chances to land on the wrong one silently, so " +
                "a port spanning levels has to be SAID rather than inferred: state the level of both " +
                "terminals if that is what you meant, or move one of the two points onto the level " +
                "the other resolved on.";
            return false;
        }

        // ── R-rp2a-4 — TWO DIFFERENT CONDUCTORS, and the two ways that fails are said apart ──────
        var posSet = new HashSet<int>(pos.BasisIndices);
        bool sharesRow = false;
        foreach (int b in neg.BasisIndices) if (posSet.Contains(b)) { sharesRow = true; break; }

        if (sharesRow)
        {
            refusal =
                $"Port {port.Number}'s two cuts landed on the SAME rooftop row — the same basis " +
                "functions on the same cut of the same metal. Driven against each other those two " +
                "blocks cancel exactly, which is a short across the port, not a port. The two points " +
                "are close enough that they resolved to one cut: move the return point onto the " +
                "return conductor, or refine the mesh if the two conductors are genuinely there and " +
                "the mesh merged them.";
            return false;
        }

        if (pos.LayerIndex == neg.LayerIndex &&
            SameConductor(mesh, pos.LayerIndex, pos.BasisIndices, neg.BasisIndices))
        {
            refusal =
                $"Port {port.Number}'s two cuts are in the SAME conductor — the metal under the " +
                $"return point at ({SurfaceMesher.Eng(negPoint.X)}m, {SurfaceMesher.Eng(negPoint.Y)}m) " +
                $"is continuous with the metal under the signal point at " +
                $"({SurfaceMesher.Eng(port.Location.X)}m, {SurfaceMesher.Eng(port.Location.Y)}m) on " +
                $"level {pos.LayerIndex}{LayerName(mesh, pos.LayerIndex)}. A port between two cuts of " +
                "one conductor is an ordinary internal delta gap wearing a costume: it is already " +
                "what this kernel builds with no reference at all. Name a genuinely separate " +
                "conductor as the return, or drop the reference and cut a single gap.";
            return false;
        }

        // ── R-rp2a-2 — ONE STATION. A skewed pair is a port plus a length of line ────────────────
        //
        // Both cuts snap to gridlines of the SAME grid along the same axis, so two cuts genuinely at
        // one station agree to the bit; the tolerance below is insurance against a coordinate that
        // arrived by arithmetic rather than off the grid, scaled to the mesh's own extent.
        bool alongX = pos.Direction == PlanarBasisDirection.X;
        var  gLong  = alongX ? mesh.GridX : mesh.GridY;
        double span = gLong[^1] - gLong[0];
        double skew = Math.Abs(pos.ReferencePlaneM - neg.ReferencePlaneM);

        if (skew > 1e-12 * Math.Max(span, 1e-12))
        {
            string ax = alongX ? "x" : "y";
            refusal =
                $"Port {port.Number}'s two cuts are at different stations: the signal cut at " +
                $"{ax} = {SurfaceMesher.Eng(pos.ReferencePlaneM)}m and the return cut at " +
                $"{ax} = {SurfaceMesher.Eng(neg.ReferencePlaneM)}m, {SurfaceMesher.Eng(skew)}m apart. " +
                "That is not one port: it is a port plus that length of line, and it solves to a " +
                "complete and plausible s-matrix for a structure nobody drew. Place both points at " +
                "the same station — each cut snaps to the nearest mesh gridline, so two points on " +
                $"one {ax} take the same gridline unless the mesh offered one conductor a cut there " +
                "and not the other, which refining the mesh fixes.";
            return false;
        }

        // ── RP-2c — AN EDGE PORT'S NEIGHBOURHOOD, WHICH IS WHAT ITS STANDARD HAS TO BE ──────────
        //
        // Only an edge port has a feed outside the cut, so only an edge port has an error box and a
        // standard. An internal delta gap resolves exactly as it did under RP-2a, bit for bit, and
        // carries no cross-section: there is nothing to calibrate and nothing to build.
        PlanarPortCrossSection? xsec = null;
        if (port.Kind == PlanarPortKind.Edge)
        {
            // A conformal boundary cell at the port makes the DRIVEN width shorter than the grid
            // extent, and the single-conductor path handles that by re-centring TransverseLines on
            // the metal (see TryResolveOnLayer's own note). That re-centring has no meaning across a
            // slot — two conductors re-centred independently would move the slot — so it is refused
            // rather than approximated, and the remedy is the setting that caused it.
            if (pos.CutCellCount > 0 || neg.CutCellCount > 0)
            {
                refusal =
                    $"Port {port.Number} returns through drawn metal AND sits on conformal boundary " +
                    $"cells ({pos.CutCellCount} cut at the signal cut, {neg.CutCellCount} at the " +
                    "return cut). A cut cell makes the metal on the reference plane shorter than its " +
                    "grid extent, and a single conductor's calibration standard absorbs that by " +
                    "rebuilding the cross-section on the METAL's own extents. Across a slot that is " +
                    "not available: re-centring the two conductors independently moves the slot " +
                    "between them, and the slot is most of what sets a coplanar line's impedance. " +
                    "Set Boundary cells back to \"Staircase\" for this run, or move the port onto a " +
                    "straight, axis-aligned length of the pair.";
                return false;
            }

            // ── A STANDARD IS A SINGLE-LEVEL UNIFORM LINE (D3), SO THE PAIR MUST BE ON ONE ─────
            //
            // R-rp2a-3 permits a port whose two cuts are on different levels when both were STATED,
            // and for an internal delta gap that is fine — it has no standard. An EDGE port does,
            // and PlanarCalibration builds it on one level: a standard carrying a via is not a
            // standard, because the algebra's whole model is "box + matched UNIFORM line + box".
            // Building it on the signal's level anyway would put the return conductor beside the
            // signal instead of under it, which is a plausible s-parameter set for a line nobody has.
            if (pos.LayerIndex != neg.LayerIndex)
            {
                refusal =
                    $"Port {port.Number} is an EDGE port whose two cuts are on different levels — " +
                    $"the signal cut on level {pos.LayerIndex}{LayerName(mesh, pos.LayerIndex)} and " +
                    $"the return cut on level {neg.LayerIndex}{LayerName(mesh, neg.LayerIndex)}. An " +
                    "edge port has a feed outside its cut, so it is de-embedded, and its calibration " +
                    "standard is a uniform line on ONE conductor level: the two-line algebra models " +
                    "the section between the reference planes as a matched uniform line, and a level " +
                    "change in the middle of it is a discontinuity in the very thing that is assumed " +
                    "uniform. Put both cuts on one level, or cut this port as an internal delta gap " +
                    "instead — an interior cut has no feed and needs no standard, and a two-cut " +
                    "internal gap spanning levels is supported.";
                return false;
            }

            if (!TryCrossSection(mesh, pos, neg, out xsec, out string? why))
            {
                refusal = $"Port {port.Number} returns through drawn metal, and the metal profile at " +
                          $"its reference plane could not be read: {why}";
                return false;
            }
        }

        resolution = pos with
        {
            Reference    = port.Reference,
            Negative     = PlanarPortTerminal.From(neg),
            CrossSection = xsec,
        };
        refusal = null;
        return true;
    }

    /// <summary>
    /// <b>RP-2c — the transverse metal profile at a two-cut edge port's reference plane.</b>
    ///
    /// <para>An interval carries metal when the cell pair the cut spans is metal on BOTH sides
    /// there, which is the same question the port's own run asks of itself one conductor at a time.
    /// The profile is trimmed to the outermost metal: a void interval outside every conductor
    /// carries no cell and no basis, so it would only make the standard's grid wider than its
    /// metal.</para>
    ///
    /// <para>The plane's own column is recovered from <c>ReferencePlaneM</c> rather than passed
    /// down, because it IS <c>gLong[highCol]</c> for both sides — an edge port's plane is the shared
    /// face of its outermost cell pair, whichever end it is. Asserting that here rather than
    /// threading two more arguments through is deliberate: one derivation, one place to be wrong.</para>
    /// </summary>
    private static bool TryCrossSection(PlanarMesh mesh, PlanarPortResolution pos,
                                        PlanarPortResolution neg,
                                        out PlanarPortCrossSection? xsec, out string? why)
    {
        xsec = null;

        bool alongX = pos.Direction == PlanarBasisDirection.X;
        var  gLong  = alongX ? mesh.GridX : mesh.GridY;
        var  gTran  = alongX ? mesh.GridY : mesh.GridX;
        int  nLong  = gLong.Count - 1, nTran = gTran.Count - 1;

        int highCol = -1;
        double best = double.PositiveInfinity;
        for (int k = 1; k < gLong.Count - 1; k++)
        {
            double d = Math.Abs(gLong[k] - pos.ReferencePlaneM);
            if (d < best) { best = d; highCol = k; }
        }
        if (highCol < 1 || highCol >= nLong)
        {
            why = "its reference plane is not an interior gridline of the mesh.";
            return false;
        }
        int lowCol = highCol - 1;

        int nx = mesh.GridX.Count - 1, ny = mesh.GridY.Count - 1;
        var at = new int[nx * ny];
        Array.Fill(at, -1);
        for (int c = 0; c < mesh.Cells.Count; c++)
        {
            var cell = mesh.Cells[c];
            if (cell.LayerIndex == pos.LayerIndex) at[cell.IY * nx + cell.IX] = c;
        }

        int CellAt(int iLong, int iTran) => alongX ? at[iTran * nx + iLong] : at[iLong * nx + iTran];

        var metal = new bool[nTran];
        int first = -1, last = -1;
        for (int t = 0; t < nTran; t++)
        {
            metal[t] = CellAt(lowCol, t) >= 0 && CellAt(highCol, t) >= 0;
            if (!metal[t]) continue;
            if (first < 0) first = t;
            last = t;
        }

        if (first < 0)
        {
            why = "no metal crosses that plane at all.";
            return false;
        }

        int n = last - first + 1;
        var lines   = new double[n + 1];
        var isMetal = new bool[n];
        for (int k = 0; k <= n; k++) lines[k]   = gTran[first + k];
        for (int k = 0; k < n; k++)  isMetal[k] = metal[first + k];

        int conductors = 0;
        for (int k = 0; k < n; k++)
            if (isMetal[k] && (k == 0 || !isMetal[k - 1])) conductors++;

        if (!TryRun(0.5 * (pos.TransverseLines[0] + pos.TransverseLines[^1]), out int pLo, out int pHi) ||
            !TryRun(0.5 * (neg.TransverseLines[0] + neg.TransverseLines[^1]), out int nLo, out int nHi))
        {
            why = "one of the two cuts did not land inside a run of metal on that plane.";
            return false;
        }

        xsec = new PlanarPortCrossSection(lines, isMetal, pLo, pHi, nLo, nHi, conductors);
        why  = null;
        return true;

        bool TryRun(double centre, out int lo, out int hi)
        {
            lo = hi = -1;
            for (int k = 0; k < n; k++)
            {
                if (!isMetal[k]) continue;
                if (centre < lines[k] - 1e-15 || centre > lines[k + 1] + 1e-15) continue;
                lo = hi = k;
                while (lo - 1 >= 0 && isMetal[lo - 1]) lo--;
                while (hi + 1 < n  && isMetal[hi + 1]) hi++;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// <b>Are these two cuts in one piece of metal?</b> A 4-connectivity walk over the cells of one
    /// level, from the positive cut's own cells, asking whether the negative cut's cells are reached.
    ///
    /// <para>It is the mesh's connectivity rather than the artwork's on purpose: what the solve
    /// drives is cells, and two polygons the mesher merged are one conductor as far as the answer is
    /// concerned. <b>It is also IN-PLANE only</b> — two conductors joined by a via somewhere else are
    /// not caught here, and are not claimed to be: that is a short in the structure, which the solve
    /// reports as one, rather than a port that resolved to the wrong thing.</para>
    /// </summary>
    private static bool SameConductor(PlanarMesh mesh, int layerIndex,
                                      IReadOnlyList<int> fromBases, IReadOnlyList<int> toBases)
    {
        int nx = mesh.GridX.Count - 1, ny = mesh.GridY.Count - 1;
        if (nx < 1 || ny < 1) return false;

        var at = new int[nx * ny];
        Array.Fill(at, -1);
        for (int c = 0; c < mesh.Cells.Count; c++)
        {
            var cell = mesh.Cells[c];
            if (cell.LayerIndex == layerIndex) at[cell.IY * nx + cell.IX] = c;
        }

        var target = new HashSet<int>();
        foreach (int b in toBases)
        {
            target.Add(mesh.Bases[b].CellA);
            target.Add(mesh.Bases[b].CellB);
        }

        var seen  = new bool[nx * ny];
        var stack = new Stack<int>();
        void Push(int cellIndex)
        {
            var c = mesh.Cells[cellIndex];
            if (c.LayerIndex != layerIndex) return;
            int k = c.IY * nx + c.IX;
            if (seen[k]) return;
            seen[k] = true;
            stack.Push(k);
        }

        foreach (int b in fromBases) { Push(mesh.Bases[b].CellA); Push(mesh.Bases[b].CellB); }

        while (stack.Count > 0)
        {
            int k  = stack.Pop();
            int cx = k % nx, cy = k / nx;
            if (at[k] >= 0 && target.Contains(at[k])) return true;

            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int qx = cx + dx, qy = cy + dy;
                if (qx < 0 || qx >= nx || qy < 0 || qy >= ny) continue;
                int q = qy * nx + qx;
                if (seen[q] || at[q] < 0) continue;
                seen[q] = true;
                stack.Push(q);
            }
        }
        return false;
    }

    /// <summary>
    /// <b>§0.2 item 2's answer, worded once.</b> A port ON A VIA is refused, and the argument is
    /// that it is a different OBJECT rather than an unimplemented case.
    ///
    /// <para>L8d's D1 makes a port an incidence matrix over a row of rooftops whose shared edge IS
    /// the reference plane, with the half-cell beyond it — the part the calibration removes — as the
    /// delta gap's other terminal. A vertical basis has no analogue of any of that: its unit current
    /// already crosses its shared footprint, its "cut" is the via itself, and there is no cell beyond
    /// the cut to reference against because a via has no end in the layout plane. Driving the
    /// horizontal rooftops that happen to sit at the same (x, y) instead is a perfectly good
    /// port — it is simply a different one, and taking it silently is the substitution this refuses.
    /// The vertical unknowns are the tail of the unknown vector (R-via-5) and are never in any
    /// port's row, which is asserted structurally rather than assumed.</para>
    ///
    /// <para>A port genuinely BETWEEN two levels is an INTERNAL port — §10.6 lists co-simulation
    /// ports as later work and they are explicitly not L9's.</para>
    /// </summary>
    internal static string ViaPortRefusal(int portNumber) =>
        $"Port {portNumber} asks to be driven BETWEEN two levels at a via. That is not the port this " +
        "kernel builds: a port is a delta gap across the shared edge of the two outermost cells of a " +
        "conductor END, and it drives the horizontal rooftops across that cut. A via basis has no " +
        "end in the layout plane, its unit current already crosses its shared footprint, and there " +
        "is no cell beyond the cut to reference against — so there is nothing for a delta gap to " +
        "act across. Driving the horizontal metal at the same (x, y) instead is a legitimate port " +
        "and is what a GroundPlane-referenced port there already does, but it is a DIFFERENT port " +
        "and is not substituted silently. A port truly between two levels is an internal " +
        "(co-simulation) port; §10.6 lists those as later work, and nothing in this repository provides one.";

    /// <summary>
    /// <b>AN INTERNAL VIA PORT: the gap is at the foot of the via under the label, not anywhere in the
    /// plane.</b> Everything the two in-plane kinds share — a transverse run of rooftops, a
    /// longitudinal cut, a reference plane coordinate — is about current that runs ALONG the metal,
    /// and none of it applies to current that leaves it. What this resolves to instead is the
    /// ground-attachment bases of one via footprint (<c>PlanarBasisFunctions</c>' header), which is
    /// the same incidence row and the same <c>Y = BᵀZ⁻¹B</c> one dimension over.
    ///
    /// <para><b>Every attachment cell of that via is driven, not just the one under the label.</b>
    /// A via's footprint is one conductor at one potential, exactly as a wide feed's transverse row
    /// is: driving a single cell of it would leave the rest of the footprint shorting the trace
    /// straight to the plane beside the port, which is a complete and plausible answer for a
    /// structure with a short across it. The footprint is walked by 4-connectivity from the cell the
    /// label is in, over cells that carry an attachment basis on this level.</para>
    /// </summary>
    private static bool TryResolveInternal(PlanarMesh mesh, PlanarPort port, int layerIndex,
                                        out PlanarPortResolution? resolution, out string? refusal)
    {
        resolution = null;

        int nx = mesh.GridX.Count - 1, ny = mesh.GridY.Count - 1;
        if (nx < 1 || ny < 1)
        {
            refusal = $"Port {port.Number} cannot be placed: the mesh has no cells to place it on.";
            return false;
        }

        // The attachment bases of THIS level, indexed by their one meshed foot cell. One forward
        // pass over Bases, queried and never iterated — R-msh-2 as everywhere else in this file.
        var attachAt = new int[nx * ny];
        Array.Fill(attachAt, -1);
        int attachments = 0;
        for (int b = 0; b < mesh.Bases.Count; b++)
        {
            var bs = mesh.Bases[b];
            if (!bs.AttachesToGround || bs.LayerIndex != layerIndex) continue;
            var cell = mesh.Cells[bs.CellB];
            attachAt[cell.IY * nx + cell.IX] = b;
            attachments++;
        }

        // ── IS THE LABEL ON THE ARTWORK AT ALL? ──────────────────────────────────────────────────
        //
        // Asked of both axes and of the grid's own extent, for the reason the internal gap asks it:
        // IndexOf CLAMPS, so a point metres away from the board would land on the outermost cell and
        // find whatever is there. An edge port may legitimately sit just off the end face it names;
        // an internal port names a via it is standing on.
        bool inside = port.Location.X >= mesh.GridX[0] - 1e-15 && port.Location.X <= mesh.GridX[^1] + 1e-15
                   && port.Location.Y >= mesh.GridY[0] - 1e-15 && port.Location.Y <= mesh.GridY[^1] + 1e-15;
        int ix = IndexOf(mesh.GridX, port.Location.X), iy = IndexOf(mesh.GridY, port.Location.Y);

        if (!inside || ix < 0 || iy < 0 || attachAt[iy * nx + ix] < 0)
        {
            // The two ways this fails are genuinely different and a user can act on only one of
            // them at a time, so they are worded apart: nothing on this level goes to ground at
            // all, versus a via that is there but is not under the label.
            refusal = attachments == 0
                ? $"Port {port.Number} at ({SurfaceMesher.Eng(port.Location.X)}m, " +
                  $"{SurfaceMesher.Eng(port.Location.Y)}m) is an internal port, and nothing on layer " +
                  $"{layerIndex}{LayerName(mesh, layerIndex)} connects to the ground plane. An internal " +
                  "internal port drives the current that leaves the metal DOWNWARD, so its path is a via to " +
                  "the plane and the port is the gap at that via's foot — with no via there is no " +
                  "path and nothing to drive. Draw a via to the ground plane where the grounded element " +
                  "attaches and put the port on it, or make this an edge or internal delta-gap port, " +
                  "which drive current along the metal instead."
                : $"Port {port.Number} at ({SurfaceMesher.Eng(port.Location.X)}m, " +
                  $"{SurfaceMesher.Eng(port.Location.Y)}m) is an internal port and there is no via to the " +
                  $"ground plane under it. This level does carry {attachments} meshed via cell(s), so " +
                  "the port is beside its via rather than on it — an internal port drives the via it " +
                  "stands on, and moving it to the nearest one silently would drive a path the user " +
                  "did not point at. Move the label onto the via's own footprint, or raise Cells per " +
                  "wavelength if the footprint is too small to have survived meshing where you placed it.";
            return false;
        }

        // ── The whole footprint, by 4-connectivity ───────────────────────────────────────────────
        var stack   = new Stack<(int X, int Y)>();
        var claimed = new bool[nx * ny];
        stack.Push((ix, iy));
        claimed[iy * nx + ix] = true;

        var indices = new List<int>();
        double area = 0;
        while (stack.Count > 0)
        {
            var (cx, cy) = stack.Pop();
            int bi = attachAt[cy * nx + cx];
            indices.Add(bi);
            area += mesh.Cells[mesh.Bases[bi].CellB].Area;

            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int qx = cx + dx, qy = cy + dy;
                if (qx < 0 || qx >= nx || qy < 0 || qy >= ny) continue;
                if (claimed[qy * nx + qx] || attachAt[qy * nx + qx] < 0) continue;
                claimed[qy * nx + qx] = true;
                stack.Push((qx, qy));
            }
        }

        // Ascending basis order, so the row is the mesh's own order regardless of which cell the
        // label happened to land in — the same determinism an edge port gets from marching its run.
        indices.Sort();

        resolution = new PlanarPortResolution(
            Number:            port.Number,
            Side:              port.Side,
            Direction:         PlanarBasisDirection.Z,
            Z0:                port.Z0,
            BasisIndices:      indices,
            IncidenceSign:     port.IncidenceSign,
            // An internal port has no in-plane width, reference plane or outer edge: its current crosses
            // the via's FOOTPRINT rather than a line across the metal, and the area that replaces
            // them is reported in its own field rather than smuggled into a length.
            WidthM:            0,
            ReferencePlaneM:   0,
            OuterEdgeM:        0,
            TransverseLines:   [],
            LongitudinalRunM:  [],
            LayerIndex:        layerIndex,
            Kind:              PlanarPortKind.Internal,
            FootprintAreaM2:   area);

        refusal = null;
        return true;
    }

    private static bool TryResolveOnLayer(PlanarMesh mesh, PlanarPort port, int layerIndex,
                                          out PlanarPortResolution? resolution, out string? refusal)
    {
        resolution = null;
        if (port.Kind == PlanarPortKind.Internal)
            return TryResolveInternal(mesh, port, layerIndex, out resolution, out refusal);


        bool alongX = port.Direction == PlanarBasisDirection.X;
        var  gLong  = alongX ? mesh.GridX : mesh.GridY;   // along the current
        var  gTran  = alongX ? mesh.GridY : mesh.GridX;   // across it
        int  nLong  = gLong.Count - 1, nTran = gTran.Count - 1;

        if (nLong < 1 || nTran < 1)
        {
            refusal = $"Port {port.Number} cannot be placed: the mesh has no cells to place it on.";
            return false;
        }

        // ── (ix, iy) → cell index, for this layer only. A plain int[], as R-msh-2 requires. ──────
        int nx = mesh.GridX.Count - 1, ny = mesh.GridY.Count - 1;
        var at = new int[nx * ny];
        Array.Fill(at, -1);
        for (int c = 0; c < mesh.Cells.Count; c++)
        {
            var cell = mesh.Cells[c];
            if (cell.LayerIndex == layerIndex) at[cell.IY * nx + cell.IX] = c;
        }

        int CellAt(int iLong, int iTran) =>
            alongX ? at[iTran * nx + iLong] : at[iLong * nx + iTran];

        // ── The transverse index the port's own point falls in ───────────────────────────────────
        double tCoord = alongX ? port.Location.Y : port.Location.X;
        int    seedT  = IndexOf(gTran, tCoord);
        if (seedT < 0)
        {
            refusal = $"Port {port.Number} at ({SurfaceMesher.Eng(port.Location.X)}m, " +
                      $"{SurfaceMesher.Eng(port.Location.Y)}m) is outside the meshed region entirely.";
            return false;
        }

        // ── Basis lookup. Built by ONE forward pass over Bases; queried, never iterated. ─────────
        //
        // Hoisted above the column search (it used to sit below it) because an INTERNAL port's cut is
        // chosen by asking which candidate gridlines actually carry a rooftop — the search needs the
        // lookup, not the other way round. An edge port's search is unchanged and reaches exactly the
        // same columns it always did.
        var byPair = new Dictionary<(int A, int B, PlanarBasisDirection D), int>(mesh.Bases.Count);
        for (int b = 0; b < mesh.Bases.Count; b++)
        {
            var bs = mesh.Bases[b];
            byPair[(bs.CellA, bs.CellB, bs.Direction)] = b;
        }

        // Is the cell pair (lowC, lowC+1) at transverse index t paired into a rooftop?
        bool PairAt(int lowC, int t)
        {
            if (lowC < 0 || lowC + 1 >= nLong) return false;
            int a = CellAt(lowC, t), b = CellAt(lowC + 1, t);
            return a >= 0 && b >= 0 && byPair.ContainsKey((a, b, port.Direction));
        }

        bool fromLow = port.Side is PlanarPortSide.MinX or PlanarPortSide.MinY;
        bool internalGap = port.Kind == PlanarPortKind.InternalDeltaGap;

        int lowCol, highCol, outer;
        double gapOffset = 0;

        if (!internalGap)
        {
            // ── March in from the named side to the RUN OF METAL THE PORT IS ON (D2) ────────────
            //
            // This used to march from the mesh's own edge and stop at the FIRST metal it met,
            // reading only the port's transverse coordinate — "the transverse coordinate is all that
            // is read", as the note above still says for the clamp. That is right while the port's
            // row crosses exactly ONE run of metal, which is every uniform feed and every test
            // fixture, and it is why it stood so long.
            //
            // **It is silently wrong the moment the row crosses two.** Owner report, 2026-09-09: a
            // port placed on the wall of a NOTCH in a connector cutout resolved to
            // side = MaxX, plane = 109.6192 mm — the far edge of a polygon 5.7 mm away, and the
            // SAME plane the other port had already claimed. Both ports drove one edge: a complete,
            // plausible two-port answer for a structure nobody drew, with no refusal and nothing on
            // screen to say so. A row through a slot, a gap, or two separate conductors on one layer
            // is the same shape of error.
            //
            // The port's LONGITUDINAL coordinate is what disambiguates, and it costs nothing to read.
            // The run CONTAINING it wins; failing that the nearest one, which is what preserves the
            // documented allowance that "its label may legitimately sit just off the end face it
            // names" — a label beyond the metal still lands on the run it is beyond, exactly as the
            // old march did. Single-run rows resolve cell for cell as before.
            double lCoordEdge = alongX ? port.Location.X : port.Location.Y;

            outer = -1;
            double bestRunD = double.PositiveInfinity;
            for (int c = 0; c < nLong; )
            {
                if (CellAt(c, seedT) < 0) { c++; continue; }

                int runLo = c;
                while (c + 1 < nLong && CellAt(c + 1, seedT) >= 0) c++;
                int runHi = c++;

                double a = gLong[runLo], b = gLong[runHi + 1];
                double d = lCoordEdge < a ? a - lCoordEdge : lCoordEdge > b ? lCoordEdge - b : 0;
                if (d >= bestRunD) continue;

                bestRunD = d;
                outer    = fromLow ? runLo : runHi;
            }

            if (outer < 0)
            {
                refusal = $"Port {port.Number} at ({SurfaceMesher.Eng(port.Location.X)}m, " +
                          $"{SurfaceMesher.Eng(port.Location.Y)}m) does not lie on any conductor on layer " +
                          $"{layerIndex}{LayerName(mesh, layerIndex)} — nothing was meshed along that " +
                          "line. Move the port onto the " +
                          "metal, or check that the conductor survived meshing at this cell size.";
                return false;
            }

            int inner = fromLow ? outer + 1 : outer - 1;
            if (inner < 0 || inner >= nLong || CellAt(inner, seedT) < 0)
            {
                refusal = $"Port {port.Number}'s conductor is only one cell long in the direction current " +
                          "would flow, so there is no rooftop basis function to drive — a port needs a pair " +
                          "of adjacent cells. Lengthen the feed line, or raise Cells per wavelength so the " +
                          "feed is meshed into more than one cell.";
                return false;
            }

            lowCol  = Math.Min(outer, inner);
            highCol = Math.Max(outer, inner);
        }
        else
        {
            // ── AN INTERNAL DELTA GAP: THE CUT IS THE GRIDLINE NEAREST THE PLACED POINT ──────────
            //
            // Both of the label's coordinates matter here, which is the one place this port differs
            // from an edge port before the solve. The transverse one picks the conductor run exactly
            // as it always did; the LONGITUDINAL one picks which of that run's interior gridlines the
            // gap is cut on — and the only cuts on offer are the mesh's own, so the nearest usable
            // one wins and how far it moved is reported (GapOffsetM). Snapping silently would hide a
            // displacement bounded by half a cell, and half a cell is a quantity the user sets.
            //
            // "Usable" means the pair either side is metal AND is paired into a rooftop, which is
            // what makes this an INTERIOR cut rather than an end: at a conductor's end face one side
            // is empty, so that gridline is never a candidate and the refusal below can say so
            // truthfully rather than guessing at the user's intent.
            double lCoord = alongX ? port.Location.X : port.Location.Y;

            // ── THE INDEX LOOKUP CLAMPS, SO "IS IT ON THE METAL?" NEEDS THE EXTENT TOO ──────────
            //
            // IndexOf returns the nearest cell for a coordinate outside the grid rather than a
            // miss, which is right for an EDGE port (its label may legitimately sit just off the end
            // face it names, and the transverse coordinate is all that is read). For an internal
            // port it is not: a point metres away from the artwork would clamp onto the outermost
            // row, find metal there, and cut a gap the user never asked for — a complete, plausible
            // answer for a structure nobody drew. Asked of both axes, because a gap is placed by
            // both of its coordinates.
            bool inside = tCoord >= gTran[0] - 1e-15 && tCoord <= gTran[^1] + 1e-15
                       && lCoord >= gLong[0] - 1e-15 && lCoord <= gLong[^1] + 1e-15;

            if (!inside || CellAt(IndexOf(gLong, lCoord), seedT) < 0)
            {
                refusal = $"Port {port.Number} at ({SurfaceMesher.Eng(port.Location.X)}m, " +
                          $"{SurfaceMesher.Eng(port.Location.Y)}m) is an internal delta-gap port, and " +
                          $"it is not ON any conductor on layer {layerIndex}{LayerName(mesh, layerIndex)}. " +
                          "An internal port cuts a gap in metal, so it has to be placed on the metal it " +
                          "cuts — unlike an edge port, whose label may sit just off the end face it names.";
                return false;
            }

            int best = -1;
            double bestD = double.PositiveInfinity;
            for (int c = 0; c < nLong - 1; c++)          // the cut between cells c and c+1
            {
                if (!PairAt(c, seedT)) continue;
                double d = Math.Abs(gLong[c + 1] - lCoord);
                if (d < bestD) { bestD = d; best = c; }
            }

            if (best < 0)
            {
                refusal = $"Port {port.Number} is an internal delta-gap port, and the conductor under it " +
                          "has no interior cut to gap: along the direction its current flows, no two " +
                          "adjacent cells there are both metal and paired into a rooftop. An internal " +
                          "gap needs metal on BOTH sides — at a conductor's END there is metal on one " +
                          "side only, and that is an edge port, not this. Move the port into the middle " +
                          "of the conductor, raise Cells per wavelength so the run is meshed into more " +
                          "than one cell, or change this port to an edge port if the end is what you meant.";
                return false;
            }

            lowCol    = best;
            highCol   = best + 1;
            outer     = best;                 // no metal is outside an interior cut; see below
            gapOffset = bestD;
        }

        double sharedCoord = gLong[highCol];

        // ── §4 — A PORT ON A CUT CELL: THE RUN IS THE ROOFTOPS THAT EXIST, NOT THE METAL ─────────
        //
        // This used to be a blanket refusal, on the argument that "a port belongs on a drawn feed,
        // which is Manhattan, so this should never fire". **That premise is false of the parts a user
        // actually selects.** A taper's flanks are oblique from its very first cell, so on MKlopf and
        // MTaper the outermost cell of the port's transverse run is cut and the whole port was
        // refused for it — which made Boundary cells = Conformal unusable on the one part whose value
        // is a controlled ripple.
        //
        // What a cut at the port genuinely costs is TWO different things, and only the first is a
        // matter of bookkeeping:
        //
        //   (1) the reference plane's metal is shorter than the gridline, so the port's WIDTH is not
        //       the grid extent. That is measured below and carried into the calibration standard
        //       (D4 builds the standard from TransverseLines, and those are now the metal's own
        //       extents), so the error box is the same object again — the property the refusal was
        //       protecting.
        //
        //   (2) R-cut-4 can decline the outermost rooftop outright. Its Anchored test is all-or-
        //       nothing over the strips, and a shallow oblique rim leaves a sliver strip at the top
        //       of the cell whose metal does not reach the shared face — so the whole basis goes,
        //       even though the strip is under a percent of the cell. **That is a real limitation and
        //       it is NOT worked around here**: the run simply stops at the last cell pair that
        //       carries a rooftop, and how much metal that leaves undriven is reported. Accepting
        //       those bases instead would retire an EXACT property (L8c's ∫f·û dℓ = 1 A) for an
        //       approximate one, which needs its own measurement and its own brief.
        //
        // Under the staircase every metal-bearing pair is paired, so this scan reproduces the old one
        // cell for cell and every pre-conformal port is bit-identical.
        // ── AN EDGE PORT'S RUN IS THE END FACE, AND AN END IS WHERE THE METAL STOPS ─────────────
        //
        // Owner report, 2026-09-09: a port on the wall of a notch was drawn 0.853 mm wide (the wall)
        // and driven 1.213 mm (the wall PLUS the metal that carries on past it toward the feed).
        // The transverse walk asked only "is there a rooftop straddling the plane here", and at a
        // notch the answer stays yes well past the end of the edge: the metal above the wall is
        // continuous through the plane because the conductor turns and keeps going.
        //
        // Driving those rooftops is not an edge feed. Current there is injected in the MIDDLE of
        // unbroken metal — a delta gap, not an end — so the structure solved is not the one drawn,
        // and the marker and the excitation disagreed about the port's own width.
        //
        // The test is one lookup: the cell just OUTSIDE the face must be empty. On a uniform feed it
        // is empty across the whole width and every pre-existing port resolves cell for cell as
        // before; it can never fail at the seed, because `outer` is by construction the outermost
        // metal column of the seed's own run.
        //
        // It narrows the METAL walk below by the same test, deliberately, rather than letting the
        // difference fall into UndrivenMetalM: that field means "metal at the plane a conformal cell
        // declined to pair", and it carries that explanation in words. Metal that never ended here is
        // not undriven — it is not this port's cross-section at all.
        int outside = fromLow ? outer - 1 : outer + 1;
        bool EndsHere(int t) =>
            internalGap || outside < 0 || outside >= nLong || CellAt(outside, t) < 0;

        bool HasBasis(int t)
        {
            int a = CellAt(lowCol, t), b = CellAt(highCol, t);
            return a >= 0 && b >= 0 && byPair.ContainsKey((a, b, port.Direction)) && EndsHere(t);
        }

        if (!HasBasis(seedT))
        {
            refusal =
                $"Port {port.Number} sits on a cell pair the mesher did not pair into a rooftop, so " +
                "there is nothing at the port's own location for it to drive. With conformal boundary " +
                "cells that means the metal there follows an oblique outline and is not swept by the " +
                "reference plane — a rooftop across it would push its current out through the " +
                "conductor's rim instead. Move the port onto a straight, axis-aligned feed, raise " +
                "Cells per wavelength, or set Boundary cells back to \"Staircase\" for this run.";
            return false;
        }

        // The contiguous transverse run of ROOFTOPS at that column…
        int lo = seedT, hi = seedT;
        while (lo - 1 >= 0    && HasBasis(lo - 1)) lo--;
        while (hi + 1 < nTran && HasBasis(hi + 1)) hi++;

        // …and, separately, how far the METAL runs, so the note can say what was left out.
        int mLo = lo, mHi = hi;
        while (mLo - 1 >= 0    && CellAt(lowCol, mLo - 1) >= 0 && CellAt(highCol, mLo - 1) >= 0 && EndsHere(mLo - 1)) mLo--;
        while (mHi + 1 < nTran && CellAt(lowCol, mHi + 1) >= 0 && CellAt(highCol, mHi + 1) >= 0 && EndsHere(mHi + 1)) mHi++;

        double PlaneMetal(int t)
        {
            var ca = mesh.Cells[CellAt(lowCol, t)];
            var cb = mesh.Cells[CellAt(highCol, t)];
            // Both cells share that face, so the two lengths agree for a sound pair; the smaller is
            // what a pair only one side reaches would report.
            return Math.Min(
                RooftopSupport.Build(ca, port.Direction, sharedIsHigh: true,  sharedCoord).SharedFaceLength,
                RooftopSupport.Build(cb, port.Direction, sharedIsHigh: false, sharedCoord).SharedFaceLength);
        }

        int cutCells = 0;
        var metal    = new double[hi - lo + 1];
        for (int t = lo; t <= hi; t++)
        {
            if (mesh.Cells[CellAt(lowCol, t)].IsCut)  cutCells++;
            if (mesh.Cells[CellAt(highCol, t)].IsCut) cutCells++;
            metal[t - lo] = PlaneMetal(t);
        }

        double undriven = 0;
        for (int t = mLo; t < lo; t++) undriven += PlaneMetal(t);
        for (int t = hi + 1; t <= mHi; t++) undriven += PlaneMetal(t);

        var indices = new List<int>(hi - lo + 1);
        for (int t = lo; t <= hi; t++)
            indices.Add(byPair[(CellAt(lowCol, t), CellAt(highCol, t), port.Direction)]);

        // ── The geometry the report and the calibration both need ────────────────────────────────
        //
        // For an INTERNAL gap the plane IS the cut and there is no metal outside it, so the "outer
        // edge" is the plane itself. That is not a placeholder: OuterEdgeM means "where the metal
        // this port drives stops", and for an interior cut it stops at the cut. Every consumer of it
        // — the feed-clearance scan, the automatic lead, the peel — is an edge-port path and asks
        // only about edge ports (PlanarSolve gates on IsDeembeddable), so reporting the honest number
        // here cannot be mistaken for a feed of length zero.
        double gridWidth = gTran[hi + 1] - gTran[lo];
        double plane     = internalGap ? sharedCoord : fromLow ? gLong[outer + 1] : gLong[outer];
        double edge      = internalGap ? sharedCoord : fromLow ? gLong[outer]     : gLong[outer + 1];

        // ── The width, and the MEASUREMENT that says the two branches below are not both live ────
        //
        // The port's width is the metal ON the reference plane, which is the honest reading of what
        // WidthM has always documented ("not the drawn width, so a staircased edge reports what was
        // actually meshed"). **On real geometry that equals the grid extent even when the port's
        // cells are cut, and the reason is structural rather than lucky:** the face is short only
        // where the cell's metal is absent over a transverse band, and for a monotone rim the same
        // band makes one of the two halves unanchored — so R-cut-4 has already refused that pair and
        // it is not in the run. Measured on the slanted-end fixture in ConformalPortTests: 7 cut
        // cells in the port's run, face metal equal to the grid extent to the last bit.
        //
        // So the branch is taken on the DIFFERENCE rather than on "is anything cut", and the verbatim
        // path — L8d's own arithmetic, one subtraction for the width and the gridlines copied as they
        // are — is what a staircased port AND an ordinary conformal port both take. R-prt-5 asserts
        // the standard's coordinates as an EQUALITY, and rebuilding them from a running sum would
        // move them in the last bit for no reason.
        double metalWidth = 0;
        foreach (double m in metal) metalWidth += m;

        double width;
        var tLines = new double[hi - lo + 2];
        if (Math.Abs(metalWidth - gridWidth) <= 1e-12 * gridWidth)
        {
            width = gridWidth;
            for (int t = lo; t <= hi + 1; t++) tLines[t - lo] = gTran[t];
        }
        else
        {
            // The port's own CROSS-SECTION: one line per basis, spaced by the metal that basis
            // actually has on the reference plane, so D4's standard is a uniform rectangle OF THAT
            // cross-section and the error box stays the same object. Centred on the grid run, because
            // these lines are also what the mesh overlay draws the reference plane from.
            width     = metalWidth;
            tLines[0] = 0.5 * (gTran[lo] + gTran[hi + 1]) - 0.5 * width;
            for (int k = 0; k < metal.Length; k++) tLines[k + 1] = tLines[k] + metal[k];
        }

        // The cells the port's own current marches through, outermost first. For an edge port that
        // is the feed, and D4 copies the first K of them into the calibration standard. An internal
        // gap has no feed and no standard: the two cells its rooftop spans are the whole of it, given
        // upstream-first so BulkCellM still means "the cell this port's gap is discretised at".
        var run = new List<double>();
        if (internalGap)
        {
            int up = fromLow ? lowCol : highCol, down = fromLow ? highCol : lowCol;
            run.Add(gLong[up   + 1] - gLong[up]);
            run.Add(gLong[down + 1] - gLong[down]);
        }
        else
        {
            for (int k = 0; ; k++)
            {
                int i = fromLow ? outer + k : outer - k;
                if (i < 0 || i >= nLong || CellAt(i, seedT) < 0) break;
                run.Add(gLong[i + 1] - gLong[i]);
            }
        }

        resolution = new PlanarPortResolution(
            Number:            port.Number,
            Side:              port.Side,
            Direction:         port.Direction,
            Z0:                port.Z0,
            BasisIndices:      indices,
            IncidenceSign:     port.IncidenceSign,
            WidthM:            width,
            ReferencePlaneM:   plane,
            OuterEdgeM:        edge,
            TransverseLines:   tLines,
            LongitudinalRunM:  run,
            LayerIndex:        layerIndex,
            CutCellCount:      cutCells,
            GridWidthM:        gridWidth,
            UndrivenMetalM:    undriven,
            Kind:              port.Kind,
            GapOffsetM:        gapOffset);

        refusal = null;
        return true;
    }

    /// <summary>
    /// R-prt-3 / PCAL2 — the feed must be uniform and isolated for the distance the calibration
    /// replaces. Returns a WARNING, not a refusal, and asks ONE threshold of every neighbour: the
    /// pre-PCAL2 shape, kept for callers that have no port list to classify a neighbour with.
    /// <b>Every decision it makes is <see cref="MeasureFeedClearance"/>'s</b> — there is no second
    /// predicate and no second threshold here.
    /// </summary>
    public static string? CheckFeedClearance(PlanarMesh mesh, PlanarPortResolution port,
                                             double requiredM)
    {
        var c = MeasureFeedClearance(mesh, port, [port], endRunM: requiredM,
                                     drivenRequiredM: requiredM, passiveRequiredM: requiredM,
                                     slabHeightM: 0);
        return c is { Breached: true } ? c.LegacyWarning() : null;
    }

    /// <summary>
    /// <b>PCAL2 — how much clear space a calibrated port's feed actually has, and what the nearest
    /// thing in it IS.</b> The whole of the clearance predicate lives here; <see cref="PlanarSolve"/>
    /// decides what to do about the answer and <c>CheckFeedClearance</c> above renders the old
    /// sentence from it.
    ///
    /// <para><b>The region scanned is the calibration's own end run, and the threshold is a distance
    /// ACROSS it.</b> Those are two settings now (<see cref="PlanarCalibrationSettings.EndRunHeights"/>
    /// and the two clearance heights beside it) because they are two quantities: the standard
    /// reproduces the DUT's cells for <paramref name="endRunM"/> INWARD from the port, and that is
    /// the length whose surroundings it assumes are empty. Letting the scan's length grow with the
    /// threshold instead would make the check fire on ordinary circuit structure several
    /// millimetres down a clean feed — metal the standard never sees — which is the unclearable
    /// warning the 2026-08-12 midpoint fix below already had to cure once.</para>
    ///
    /// <para>Returns null when the question does not arise: a port with no feed, or a zero-length
    /// end run.</para>
    /// </summary>
    /// <param name="allPorts">Every port of the run, so a neighbouring conductor that carries one
    /// can be told from one that does not — PCAL1 measured those two cases 2-3× apart in the
    /// clearance they need. Pass the port itself and nothing else to ask the one-threshold
    /// question.</param>
    /// <param name="endRunM">How far inward from the port the calibration standard reproduces the
    /// DUT's own cells — <see cref="PlanarCalibrationSettings.EndRunHeights"/> × h.</param>
    /// <param name="slabHeightM">The substrate height, so the margin can be reported in the units
    /// PCAL1 measured the error to follow. 0 leaves <see cref="PlanarFeedClearance.Heights"/> NaN
    /// and changes no decision.</param>
    public static PlanarFeedClearance? MeasureFeedClearance(
        PlanarMesh mesh, PlanarPortResolution port,
        IReadOnlyList<PlanarPortResolution> allPorts,
        double endRunM, double drivenRequiredM, double passiveRequiredM, double slabHeightM,
        PlanarConductors? conductors = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(allPorts);
        if (!(endRunM > 0)) return null;

        // An internal delta gap has no feed and no calibration standard, so there is no length of
        // line this warning could be about. Saying nothing is the answer; warning about a neighbour
        // that is not being replaced by anything would be noise the user cannot act on.
        if (!port.IsDeembeddable) return null;

        var conn = conductors ?? PlanarConductors.Of(mesh);

        // ── WHOSE METAL IS IT? (PCAL2, corrected by R-pcal7-1) ───────────────────────────────
        //
        // TWO answers, not three. A conductor CARRYING A PORT needs 4-5.5 h of clearance; a
        // conductor carrying none needs ≈ 2 h (PCAL1 measured both). The port's own net is the
        // FIRST of those, and it used to be a third answer — "not a neighbour at all" — on the
        // reasoning that a flare or pad on the port's own net is R-fed-1's job, which grows a
        // collinear lead and peels it exactly.
        //
        // THAT REASONING IS SOUND ABOUT METAL IN LINE WITH THE FEED and says nothing about a
        // PARALLEL RETURN RUN of the same net, which is what a spiral inductor is made of. On
        // the shipped MMIC coil the metal 8 µm from port 1's feed is the next turn — the port's own
        // net — and the skip reported both feeds CLEAR while the published `.s2p` was an open
        // circuit with a negative resistance at every AC point (R-pcal7-1; §PCAL7-OWNNET in
        // RESOLVED.md has the table).
        //
        // So own-net metal is measured like any other, and it lands in the DRIVEN class by
        // construction: the port's own conductor carries this very port, so its label is already in
        // the `allPorts` set already. Nothing here has to say "driven" for it — the deleted
        // skip IS the whole change, and the 5 h threshold it now takes is the one that was measured.
        var driven = conn.LabelsCarryingAPort(mesh, allPorts);

        bool alongX = port.Direction == PlanarBasisDirection.X;
        bool fromLow = port.Side is PlanarPortSide.MinX or PlanarPortSide.MinY;

        // RP-2c — a conductor-referenced port's neighbourhood is the whole cross-section, slot and
        // return included, and every bit of it IS reproduced in the standard. Asking this question
        // of the signal run alone would report the port's own return conductor as a neighbour that
        // is not removed correctly, on every coplanar port, always — the same unclearable warning
        // the 2026-08-12 fix below removed for a different reason.
        // PCAL3 — a WIDENED profile is reproduced in the standard just as literally as a coplanar
        // port's return is, so the same sentence applies to it: everything inside the profile is not
        // a neighbour. This is what makes the refusal clear itself once the widening has happened,
        // and — just as important — what leaves a SECOND neighbour outside the widened span still
        // measured, from the widened edge, and still able to refuse the run.
        double tLo = port.Group?.SpanLoM ?? port.Neighbourhood?.SpanLoM ?? port.CrossSection?.SpanLoM
                  ?? port.TransverseLines[0];
        double tHi = port.Group?.SpanHiM ?? port.Neighbourhood?.SpanHiM ?? port.CrossSection?.SpanHiM
                  ?? port.TransverseLines[^1];
        double nearestDriven  = double.PositiveInfinity;
        double nearestPassive = double.PositiveInfinity;

        // ── R-pcal7-1 — THE FEED'S OWN CROSS-SECTION, COLUMN BY COLUMN ──────────────────────────
        //
        // The skip that used to stand here excused the port's whole NET. That is too much: a coil's
        // next turn is the port's own net and is a neighbour in every sense the calibration standard
        // cares about. It is also, in the case it was written for, exactly right — a taper's flare
        // and a pad are the port's own metal IN LINE WITH the feed, R-fed-1 grows a collinear lead
        // for them and peels it exactly, and calling them neighbours would refuse every taper in the
        // repository.
        //
        // What separates the two is not the NET, it is whether the metal is CONTINUOUS with the
        // feed's own run across the section. So the exemption is the run itself: at each column,
        // the maximal unbroken band of metal containing the port's own profile, on the port's own
        // level. A flare is inside it; a conductor with a gap between it and the feed is not,
        // whoever owns it. See FeedBands.
        var bands = FeedBands(mesh, port, alongX, fromLow, tLo, tHi, endRunM);

        for (int ci = 0; ci < mesh.Cells.Count; ci++)
        {
            var c = mesh.Cells[ci];

            // ── A CUT CELL'S METAL IS ITS REGION, NOT ITS GRID RECTANGLE (2026-09-12) ───────────
            //
            // `PlanarCell.XMin…YMax` is the GRID rectangle, and its own doc says that for a cut cell
            // it BOUNDS the metal rather than equalling it. Asking the clearance question of the
            // rectangle therefore measures a neighbour that is not there: at a 45° bend the cut
            // cell's box overhangs the metal by most of a cell, and where the port's profile edge
            // happens to sit under that overhang the gap comes out as EXACTLY ZERO. Measured on a
            // real board — the same file, the same geometry, the same everything else, reported
            // "port 3's feed clearance is 0 substrate heights, 0 µm to the nearest other conductor"
            // with `Conformal` and "port 3's feed is clear" with `Staircase`, and the zero refused
            // the run. `Region` is null on every Manhattan cell, so a staircased mesh is
            // bit-identical to what this did before — the same rule R-cut-2 holds everywhere else.
            var region = c.Region;
            double t0 = alongX ? (region?.YMin ?? c.YMin) : (region?.XMin ?? c.XMin);
            double t1 = alongX ? (region?.YMax ?? c.YMax) : (region?.XMax ?? c.XMax);
            if (t1 > tLo + 1e-15 && t0 < tHi - 1e-15) continue;   // inside the feed's own profile

            // …and inside the feed's own CROSS-SECTION, which on a taper or a pad is wider than the
            // profile and is still not a neighbour. Only on the port's own level: a band is a run of
            // metal, and metal on another level is not continuous with this one whatever it looks
            // like from above.
            if (c.LayerIndex == port.LayerIndex && bands is not null &&
                bands.TryGetValue(alongX ? c.IX : c.IY, out var band))
            {
                // ── BY GRID INDEX, NOT BY COORDINATE, AND THAT IS NOT A TIDY-UP ──────────────────
                //
                // A cut cell's `Region` is the METAL it holds, and a MERGED SLIVER's region covers
                // more than its own grid rectangle — the sliver's metal was folded into its
                // neighbour's cell (R-cut-1). Comparing that region against the band's gridlines
                // therefore fails for a cell that IS in the band: on the owner's Klopfenstein taper
                // the two cells at the flare's rim reported metal out to 643.1 µm from a grid cell
                // ending at 611.8, and the port's own flare came back as a neighbour 203.9 µm away.
                // The index says which column of the cross-section this is, and a merge does not
                // move it.
                int iTran = alongX ? c.IY : c.IX;
                if (iTran >= band.Lo && iTran <= band.Hi) continue;
            }

            // A cell no rooftop pairs with carries no current and is not in the solve at all, so it
            // cannot be a neighbour — see PlanarConductors.CarriesCurrent, and the conformal taper
            // that measured the difference.
            if (!conn.CarriesCurrent(ci)) continue;

            int label = conn.LabelOf(ci);

            // ── IS THIS CELL IN THE FEED REGION AT ALL? (fixed 2026-08-12) ──────────────────────
            //
            // `along` used to be measured to the cell's NEAR edge and then used only to skip cells
            // BEHIND the port — there was no upper bound at all, so the scan ran to the far end of
            // the structure and `nearest` came back as the smallest lateral gap ANYWHERE on the
            // board. That is not the quantity this warning's own text describes ("inside the
            // {endRun}m the calibration standard assumes is empty"), and the difference is not
            // cosmetic: it fired on every part that is ever wider than its port — every taper, stub
            // and tee — including feeds that are demonstrably clean. A warning that cannot be
            // cleared is one users learn to skip, and this is the one that has to stay readable,
            // because it is what R-fed-1's automatic lead CANNOT fix: a lead lengthens a feed, it
            // cannot move a neighbour sideways.
            //
            // The station is the cell's MIDPOINT, not its near edge, and that is load-bearing rather
            // than tidy. R-fed-1 sizes the lead so the feed is uniform for EXACTLY the end run, so
            // the DUT's own flare always begins at the region's far boundary and the cell straddling
            // it always has a lateral gap of zero. On a near-edge test that cell re-fires the warning
            // on every extended taper — reintroducing the unclearable warning one line below the fix
            // for it. A cell is judged by where most of it sits.
            // The METAL's own longitudinal extent, for the same reason as the transverse pair above.
            double l0 = alongX ? (region?.XMin ?? c.XMin) : (region?.YMin ?? c.YMin);
            double l1 = alongX ? (region?.XMax ?? c.XMax) : (region?.YMax ?? c.YMax);
            double mid = 0.5 * (l0 + l1);
            double along = fromLow ? mid - port.OuterEdgeM : port.OuterEdgeM - mid;
            if (along < -endRunM || along > endRunM) continue;

            double across = Math.Max(t0 >= tHi ? t0 - tHi : tLo - t1, 0);
            if (driven.Contains(label)) nearestDriven  = Math.Min(nearestDriven,  across);
            else                        nearestPassive = Math.Min(nearestPassive, across);
        }

        // Each class is judged against its OWN threshold, and the one reported is the one that is
        // worst off relative to what it needs — not the one that is physically nearest. A passive
        // trace at 1.9 h beside a driven one at 3 h is a clean passive neighbour and a breached
        // driven one, and the message has to be about the second.
        double dRatio = nearestDriven  / Math.Max(drivenRequiredM,  double.Epsilon);
        double pRatio = nearestPassive / Math.Max(passiveRequiredM, double.Epsilon);

        return dRatio <= pRatio
            ? new PlanarFeedClearance(port.Number,
                  double.IsInfinity(nearestDriven) ? PlanarNeighbourClass.None : PlanarNeighbourClass.Driven,
                  nearestDriven, drivenRequiredM, slabHeightM, endRunM)
            : new PlanarFeedClearance(port.Number,
                  double.IsInfinity(nearestPassive) ? PlanarNeighbourClass.None : PlanarNeighbourClass.Passive,
                  nearestPassive, passiveRequiredM, slabHeightM, endRunM);
    }


    /// <summary>
    /// <b>R-pcal7-1 — the feed's own unbroken cross-section at every column of the end run</b>, keyed
    /// by that column's grid index, or null when the port's own level cannot be walked.
    ///
    /// <para>At each column the band is the maximal unbroken run of metal containing the port's own
    /// transverse profile. A taper's flare, a pad and a bend's corner are that run
    /// getting wider; a second conductor — the next turn of a coil, a neighbouring trace — is
    /// separated from it by a gap and is not in it, whether or not it is the same NET.</para>
    ///
    /// <para><b>Where the profile band is not metal, the feed has ENDED, and the band is empty from
    /// there inward.</b> That is not a technicality: the shipped MMIC spiral's second port sits on a
    /// 30 µm pad that stops, and the coil body starts 10 µm later, reached through a via on another
    /// level. A band that resumed there would call 250 µm of coil "the feed getting wider" and the
    /// port would measure clear while its calibration standard described nothing that exists.</para>
    /// </summary>
    internal static Dictionary<int, (int Lo, int Hi)>? FeedBands(
        PlanarMesh mesh, PlanarPortResolution port,
        bool alongX, bool fromLow, double tLo, double tHi, double endRunM)
    {
        var gLong = alongX ? mesh.GridX : mesh.GridY;
        var gTran = alongX ? mesh.GridY : mesh.GridX;
        int nLong = gLong.Count - 1, nTran = gTran.Count - 1;
        if (nLong < 1 || nTran < 1) return null;

        int nx = mesh.GridX.Count - 1;
        var at = new int[nx * (mesh.GridY.Count - 1)];
        Array.Fill(at, -1);
        for (int i = 0; i < mesh.Cells.Count; i++)
        {
            var cell = mesh.Cells[i];
            if (cell.LayerIndex == port.LayerIndex) at[cell.IY * nx + cell.IX] = i;
        }

        int CellAt(int iLong, int iTran)
        {
            if ((uint)iLong >= (uint)nLong || (uint)iTran >= (uint)nTran) return -1;
            return alongX ? at[iTran * nx + iLong] : at[iLong * nx + iTran];
        }
        // ── METAL, NOT CURRENT, AND THE DIFFERENCE IS MEASURABLE ────────────────────────────────
        //
        // Elsewhere in this file a cell no rooftop pairs with is not a neighbour, because it is not
        // in the solve at all. Here the question is the opposite one — is this cell's metal part of
        // the feed's own unbroken cross-section — and a cell that exists is, whatever bases it ended
        // up carrying. Asking `CarriesCurrent` instead punched a hole in the band at an obliquely
        // CUT rim, where a conformal cell can have its rooftops declined for not being flow-simple,
        // and everything beyond the hole read as a neighbour: the owner's own Klopfenstein taper
        // came back refused with "other metal 203.9 µm away", which is its own flare.
        bool Metal(int iLong, int iTran) => CellAt(iLong, iTran) >= 0;

        // The profile's own transverse cells: every cell whose span lies inside [tLo, tHi].
        int pLo = -1, pHi = -1;
        for (int t = 0; t < nTran; t++)
            if (gTran[t] >= tLo - 1e-15 && gTran[t + 1] <= tHi + 1e-15) { if (pLo < 0) pLo = t; pHi = t; }
        if (pLo < 0) return null;

        // ── THE OUTERMOST CELL, WHICH IS NOT THE ONE IndexOf NAMES ON THE HIGH SIDE ─────────────
        //
        // `IndexOf` names the cell that STARTS at a gridline. For a port fed from the LOW side that
        // is the feed's own outermost cell, because `OuterEdgeM` is that cell's low line. For one
        // fed from the HIGH side `OuterEdgeM` is its outermost cell's HIGH line, so the cell IndexOf
        // names is the one just OUTSIDE the metal — and the walk below breaks at k = 0 on a column
        // with no metal in it and returns no band at all.
        //
        // It only shows when the grid runs past the port's own edge, which is why no fixture here
        // caught it: on a layout whose outermost metal IS this port's feed, IndexOf clamps to the
        // last cell and lands on the right one by accident. Put any metal beyond the port — another
        // trace, a pad, a second component — and the grid extends, the band goes null, and the
        // port's own flare is back in the neighbour class R-pcal7-1 exists to take it out of.
        int outer = IndexOf(gLong, port.OuterEdgeM);
        if (outer < 0) return null;
        if (!fromLow && outer > 0 &&
            Math.Abs(gLong[outer] - port.OuterEdgeM) < Math.Abs(gLong[outer + 1] - port.OuterEdgeM))
            outer--;

        var bands = new Dictionary<int, (int Lo, int Hi)>();
        for (int k = 0; ; k++)
        {
            int col = fromLow ? outer + k : outer - k;
            if (col < 0 || col >= nLong) break;
            double mid = 0.5 * (gLong[col] + gLong[col + 1]);
            if (Math.Abs(mid - port.OuterEdgeM) > endRunM) break;

            bool whole = true;
            for (int t = pLo; t <= pHi; t++) if (!Metal(col, t)) { whole = false; break; }
            if (!whole) break;                      // the feed has ended: no band here or further in

            int lo = pLo, hi = pHi;
            while (lo - 1 >= 0    && Metal(col, lo - 1)) lo--;
            while (hi + 1 < nTran && Metal(col, hi + 1)) hi++;
            bands[col] = (lo, hi);
        }
        return bands.Count == 0 ? null : bands;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // PCAL3 — PUTTING THE NEIGHBOUR IN THE PROFILE
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>R-pcal3-1 — a conductor within the clearance distance of a calibrated feed, carrying no
    /// port, joins that port's PROFILE and therefore its calibration standard.</b> Returns the port
    /// with a <see cref="PlanarPortNeighbourhood"/> on it, or null with <paramref name="declined"/>
    /// saying which conductor could not be taken in and why.
    ///
    /// <para><b>The fix is on the standard's side and it has to be.</b> The overview's §2 rules out
    /// the tempting alternative — have the solver separate the feeds — because the lead
    /// <see cref="PlanarFeedExtension"/> grows is peelable only while it is collinear and uniform,
    /// and routing two feeds apart needs a bend, which the peel cannot remove. So the standard is
    /// made to reproduce the port's actual neighbourhood instead of the neighbourhood being made to
    /// match the standard.</para>
    ///
    /// <para><b>Only a PASSIVE neighbour, and only on the port's own level.</b> A neighbour carrying
    /// a port supports a second mode at the reference plane against D6's per-port SCALAR error box,
    /// which is a different piece of algebra and is brief 4; PCAL1 measured no case of metal on
    /// another level at all, so there is no evidence either way and nothing is read into its
    /// silence. Both are declined by name rather than attempted.</para>
    ///
    /// <para><b>R-pcal3-2 — the standard is a uniform extrusion, so every conductor in it must be
    /// uniform.</b> A neighbour that bends, ends or changes width inside the run the standard
    /// reproduces cannot be extruded, and guessing what it should become costs more than declining.
    /// The run checked is D4's own — <paramref name="endRunCells"/> of the DUT's own cells marching
    /// inward, which is exactly the region the standard copies VERBATIM and exactly the region the
    /// port's own conductor is already held to. Using a different length here would mean the
    /// standard's two conductors were held to two different uniformity rules.</para>
    /// </summary>
    /// <param name="allPorts">Every port of the run, so a neighbour carrying one can be told from
    /// one that does not.</param>
    /// <param name="endRunCells">D4's own end run, in the port's own cells.</param>
    /// <param name="passiveRequiredM">The lateral distance a passive neighbour has to be beyond
    /// before it stops mattering — <see cref="PlanarCalibrationSettings.PassiveNeighbourClearanceHeights"/>
    /// × h. Anything further away is left outside the profile, because a profile wider than it needs
    /// to be costs unknowns on every standard of every run, forever.</param>
    public static PlanarPortResolution? TryWidenForNeighbours(
        PlanarMesh mesh, PlanarPortResolution port,
        IReadOnlyList<PlanarPortResolution> allPorts,
        int endRunCells, double passiveRequiredM,
        PlanarConductors conductors, out string? declined)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(allPorts);
        ArgumentNullException.ThrowIfNull(conductors);
        declined = null;

        string Who() => $"Port {port.Number}'s feed";

        if (!port.IsDeembeddable || endRunCells < 1) return null;

        if (port.CrossSection is not null)
        {
            declined =
                $"{Who()} returns through drawn metal, and widening a coplanar profile to take in a " +
                "passive neighbour is not attempted: that standard already drives two conductors at " +
                "±½ V and a third piece of metal beside them has no stated potential — the same " +
                "ambiguity the third-conductor refusal names one step further out.";
            return null;
        }

        if (port.CutCellCount > 0)
        {
            declined =
                $"{Who()} is meshed with conformal boundary cells ({port.CutCellCount} of the port's " +
                "own cells are cut), so its profile is the METAL's extents rather than the grid's and " +
                "a neighbour's gridlines would not line up with it. Set Boundary cells to " +
                "\"Staircase\" for this run, or separate the feeds.";
            return null;
        }

        var probe = FeedProfileProbe.For(mesh, port, endRunCells, conductors);
        if (probe is null) return null;

        bool alongX  = probe.AlongX;
        var  gLong   = probe.GLong;
        var  gTran   = probe.GTran;
        int  nTran   = probe.NTran;
        int  outer   = probe.Outer;
        int  oLo     = probe.OwnLo, oHi = probe.OwnHi;

        int  CellAt(int iLong, int iTran) => probe.CellAt(iLong, iTran);
        bool Metal(int iLong, int iTran)  => probe.Metal(iLong, iTran);
        int  Column(int j)                => probe.Column(j);

        // ── Walk outward on each side, taking in every passive conductor still inside the
        //    threshold and measuring the next gap from the edge the last one moved to ────────────
        var  mine    = new HashSet<int>(conductors.LabelsOf(mesh, port));
        var  driven  = conductors.LabelsCarryingAPort(mesh, allPorts);
        int  spanLo = oLo, spanHi = oHi;
        int  taken  = 0;
        double nearest = double.PositiveInfinity;

        for (int dir = 0; dir < 2 && declined is null; dir++)
        {
            bool up = dir == 0;
            while (true)
            {
                int edge = up ? spanHi + 1 : spanLo;          // the gridline the span currently ends on
                int t = up ? spanHi + 1 : spanLo - 1;
                while (t >= 0 && t < nTran && !Metal(outer, t)) t += up ? 1 : -1;
                if (t < 0 || t >= nTran) break;               // nothing further out on this side

                double gap = up ? gTran[t] - gTran[edge] : gTran[edge] - gTran[t + 1];
                if (gap >= passiveRequiredM) break;           // clear, and a profile is never wider
                                                              // than it has to be

                int nLoT = t, nHiT = t;
                while (nLoT - 1 >= 0    && Metal(outer, nLoT - 1)) nLoT--;
                while (nHiT + 1 < nTran && Metal(outer, nHiT + 1)) nHiT++;

                int label = conductors.LabelOf(CellAt(outer, t));
                if (driven.Contains(label))
                {
                    declined =
                        $"{Who()} has a conductor {SurfaceMesher.Eng(gap)}m away that CARRIES A PORT " +
                        "of its own. Two driven conductors at one reference plane support two modes, " +
                        "and this kernel's error box is one scalar per port — reproducing the metal " +
                        "in the standard does not fix that, so it is not attempted here.";
                    break;
                }
                if (mine.Contains(label))
                {
                    declined =
                        $"{Who()} has metal {SurfaceMesher.Eng(gap)}m away that is part of the port's " +
                        "OWN net, reaching the reference plane as a separate run. A standard " +
                        "reproducing it would be two conductors the structure shorts together " +
                        "somewhere this profile cannot see.";
                    break;
                }

                if (gap < nearest) nearest = gap;
                taken++;
                if (up) spanHi = nHiT; else spanLo = nLoT;
            }
        }

        if (declined is not null) return null;

        // ── NOTHING WITHIN THE THRESHOLD CROSSES THIS PORT'S OWN PLANE, ON THIS PORT'S OWN LEVEL ──
        //
        // Which is the ordinary answer for a clear feed, and is therefore NOT a decline: a null with
        // no reason means "there was nothing to widen". Whether that leaves a breach standing is a
        // question about the CLEARANCE, which this function does not measure and the caller does —
        // <see cref="PlanarSolve"/> says so there, where both halves are in hand.
        if (taken == 0) return null;

        // ── R-pcal3-2 — EVERY conductor in the profile has to survive the extrusion ──────────────
        int n = spanHi - spanLo + 1;
        var lines   = new double[n + 1];
        var isMetal = new bool[n];
        var labels  = new int[n];
        for (int k = 0; k <= n; k++) lines[k] = gTran[spanLo + k];
        for (int k = 0; k < n; k++)
        {
            isMetal[k] = Metal(outer, spanLo + k);
            labels[k]  = isMetal[k] ? conductors.LabelOf(CellAt(outer, spanLo + k)) : -1;
        }

        for (int j = 1; j < endRunCells; j++)
        {
            int col = Column(j);
            for (int k = 0; k < n; k++)
            {
                bool m = Metal(col, spanLo + k);
                if (m == isMetal[k] && (!m || conductors.LabelOf(CellAt(col, spanLo + k)) == labels[k]))
                    continue;

                double station = 0.5 * (gLong[col] + gLong[col + 1]);
                declined =
                    $"{Who()} has a neighbouring conductor that is not uniform over the " +
                    $"{SurfaceMesher.Eng(Math.Abs(station - port.OuterEdgeM))}m of line the " +
                    "calibration standard reproduces: at " +
                    $"{(alongX ? "x" : "y")} = {SurfaceMesher.Eng(station)}m the metal " +
                    $"{SurfaceMesher.Eng(0.5 * (lines[k] + lines[k + 1]))}m across " +
                    (m ? "appears where the reference plane has none"
                       : isMetal[k] ? "has ended or moved" : "belongs to a different conductor") +
                    ". A calibration standard is a uniform extrusion, so a neighbour that bends, " +
                    "ends or changes width inside that run cannot be put in it — and guessing what " +
                    "it should become there would move metal the user did not draw. Separate the " +
                    "feeds, or lengthen the neighbour so it runs straight past the port.";
                return null;
            }
        }

        return port with
        {
            Neighbourhood = new PlanarPortNeighbourhood(
                lines, isMetal, oLo - spanLo, oHi - spanLo, taken, nearest),
        };
    }

    /// <summary>
    /// <b>The transverse profile at one port's reference plane, read off the DUT's own mesh.</b>
    /// Shared by PCAL3's <see cref="TryWidenForNeighbours"/> and PCAL4's
    /// <see cref="TryFormCalibrationGroup"/> because it is the same question — where the port's own
    /// conductor is, which columns D4's end run copies, and which cells carry current — and two
    /// copies of it are two chances for a passive neighbour and a driven one to be found in
    /// different places.
    /// </summary>
    internal sealed class FeedProfileProbe
    {
        public required bool AlongX { get; init; }
        public required IReadOnlyList<double> GLong { get; init; }
        public required IReadOnlyList<double> GTran { get; init; }
        public required int NLong { get; init; }
        public required int NTran { get; init; }
        public required bool FromLow { get; init; }
        public required int Outer { get; init; }
        public required int OwnLo { get; init; }
        public required int OwnHi { get; init; }
        internal required int Nx { get; init; }
        internal required int[] At { get; init; }
        internal required PlanarConductors Conductors { get; init; }

        public int CellAt(int iLong, int iTran)
        {
            if ((uint)iLong >= (uint)NLong || (uint)iTran >= (uint)NTran) return -1;
            return AlongX ? At[iTran * Nx + iLong] : At[iLong * Nx + iTran];
        }

        /// <summary>A cell in no basis is not in the solve, so it is not metal for this purpose
        /// either — <see cref="PlanarConductors.CarriesCurrent"/>, and the conformal taper that
        /// measured the difference.</summary>
        public bool Metal(int iLong, int iTran)
        {
            int ci = CellAt(iLong, iTran);
            return ci >= 0 && Conductors.CarriesCurrent(ci);
        }

        /// <summary>The j-th column of D4's end run, marching INWARD from the port.</summary>
        public int Column(int j) => FromLow ? Outer + j : Outer - j;

        public static FeedProfileProbe? For(PlanarMesh mesh, PlanarPortResolution port,
                                            int endRunCells, PlanarConductors conductors)
        {
            bool alongX  = port.Direction == PlanarBasisDirection.X;
            var  gLong   = alongX ? mesh.GridX : mesh.GridY;
            var  gTran   = alongX ? mesh.GridY : mesh.GridX;
            int  nLong   = gLong.Count - 1, nTran = gTran.Count - 1;
            bool fromLow = port.Side is PlanarPortSide.MinX or PlanarPortSide.MinY;

            int nx = mesh.GridX.Count - 1;
            var at = new int[nx * (mesh.GridY.Count - 1)];
            Array.Fill(at, -1);
            for (int c = 0; c < mesh.Cells.Count; c++)
            {
                var cell = mesh.Cells[c];
                if (cell.LayerIndex == port.LayerIndex) at[cell.IY * nx + cell.IX] = c;
            }

            // ── The end run's columns, marching INWARD from the port, exactly as D4 copies them ──
            int planeIdx = 0;
            double best = double.PositiveInfinity;
            for (int k = 0; k < gLong.Count; k++)
            {
                double d = Math.Abs(gLong[k] - port.ReferencePlaneM);
                if (d < best) { best = d; planeIdx = k; }
            }
            int outer = fromLow ? planeIdx - 1 : planeIdx;
            if (outer < 0 || outer >= nLong) return null;

            var probe = new FeedProfileProbe
            {
                AlongX = alongX, GLong = gLong, GTran = gTran, NLong = nLong, NTran = nTran,
                FromLow = fromLow, Outer = outer, OwnLo = 0, OwnHi = 0, Nx = nx, At = at,
                Conductors = conductors,
            };

            if (probe.Column(endRunCells - 1) < 0 || probe.Column(endRunCells - 1) >= nLong) return null;

            // ── The port's own run, read off the mesh and cross-checked against the resolution ───
            double ownCentre = 0.5 * (port.TransverseLines[0] + port.TransverseLines[^1]);
            int seedT = IndexOf(gTran, ownCentre);
            if (seedT < 0 || !probe.Metal(outer, seedT)) return null;

            int oLo = seedT, oHi = seedT;
            while (oLo - 1 >= 0    && probe.Metal(outer, oLo - 1)) oLo--;
            while (oHi + 1 < nTran && probe.Metal(outer, oHi + 1)) oHi++;

            if (oHi - oLo + 1 != port.BasisCount) return null;
            for (int i = 0; i <= oHi - oLo; i++)
                if (Math.Abs(gTran[oLo + i] - port.TransverseLines[i]) >
                    1e-12 * Math.Max(1.0, Math.Abs(gTran[oLo + i])))
                    return null;

            return probe.WithOwnRun(oLo, oHi);
        }

        private FeedProfileProbe WithOwnRun(int lo, int hi) => new()
        {
            AlongX = AlongX, GLong = GLong, GTran = GTran, NLong = NLong, NTran = NTran,
            FromLow = FromLow, Outer = Outer, OwnLo = lo, OwnHi = hi, Nx = Nx, At = At,
            Conductors = Conductors,
        };
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // PCAL4 — THE CALIBRATION GROUP
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>R-pcal4-1 — the ports whose feeds are mutually within the DRIVEN clearance distance at one
    /// reference plane form a group, and the group is the unit of calibration.</b> Returns the
    /// profile their shared standard reproduces, or null with <paramref name="declined"/> saying
    /// which conductor could not be taken in and why.
    ///
    /// <para><b>A group of one is not a group</b> — this returns null with no reason there, exactly
    /// as <see cref="TryWidenForNeighbours"/> does, and the port keeps D6's scalar error box and
    /// every byte of today's answer (R-pcal4-1's "must remain bit-identical").</para>
    ///
    /// <para><b>What is declined, by name, rather than attempted</b> (R-pcal4-6): a neighbour that
    /// carries no port (that is PCAL3's, and a standard cannot hold one driven and one floating
    /// conductor under two different electrostatic rules); a neighbour whose port is somewhere else
    /// entirely, so the two feeds do not share a plane and no single standard describes them; metal
    /// on the port's own net; a coplanar or conformally cut port; a neighbour that is not uniform
    /// over the run the standard reproduces; and a group larger than
    /// <paramref name="maxGroupSize"/>.</para>
    /// </summary>
    /// <param name="drivenRequiredM">The lateral distance a neighbour carrying a port has to be
    /// beyond before the two calibrate independently —
    /// <see cref="PlanarCalibrationSettings.DrivenNeighbourClearanceHeights"/> × h.</param>
    /// <param name="maxGroupSize">
    /// How many conductors one group may hold. <b>It is a cost gate rather than an algebraic one</b>
    /// (R-pcal4-7): the standard is a 2N-port whose mesh carries every conductor, so N conductors
    /// cost N times the transverse extent on every standard of the set and N² entries in every block
    /// of the error box, and the modes have to stay separable on top of that.
    /// </param>
    public static PlanarPortGroupProfile? TryFormCalibrationGroup(
        PlanarMesh mesh, PlanarPortResolution port,
        IReadOnlyList<PlanarPortResolution> allPorts,
        int endRunCells, double drivenRequiredM, int maxGroupSize,
        PlanarConductors conductors, out string? declined)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(allPorts);
        ArgumentNullException.ThrowIfNull(conductors);
        declined = null;

        string Who() => $"Port {port.Number}'s feed";

        if (!port.IsDeembeddable || endRunCells < 1) return null;

        if (port.CrossSection is not null)
        {
            declined =
                $"{Who()} returns through drawn metal, and a conductor-referenced port cannot join a " +
                "calibration group: its own standard already drives two conductors at ±½ V, so a " +
                "third driven conductor beside them is a differential-port question rather than this " +
                "one (mom-engine.md §10.6).";
            return null;
        }

        if (port.CutCellCount > 0)
        {
            declined =
                $"{Who()} is meshed with conformal boundary cells ({port.CutCellCount} of the port's " +
                "own cells are cut), so its profile is the METAL's extents rather than the grid's and " +
                "a neighbour's gridlines would not line up with it. Set Boundary cells to " +
                "\"Staircase\" for this run, or separate the feeds.";
            return null;
        }

        var probe = FeedProfileProbe.For(mesh, port, endRunCells, conductors);
        if (probe is null) return null;

        var gTran = probe.GTran;
        int nTran = probe.NTran;
        int outer = probe.Outer;

        // ── Walk outward on each side, taking in every conductor that carries a port of its own at
        //    THIS plane and is still inside the driven threshold ────────────────────────────────
        //
        // R-pcal7-3 — THE PORT'S OWN NET IS NOT DECLINED HERE ANY MORE. It used to be, by name:
        // "a standard reproducing it would be two conductors the structure shorts together
        // somewhere this profile cannot see". <b>The objection is not borne out.</b> The error box
        // is a local property of the cross-section and the excitation at the plane, and where the
        // DUT joins its two conductors beyond the plane does not enter it — measured on the
        // `Coupled` fixture, two 800 µm arms 50 µm apart shorted at the far end, which read 135 nH
        // and non-passive with the decline standing and 1.2 nH and passive with it lifted, against
        // a two-wire estimate of 1.0-1.3 nH (§PCAL7-OWNNET). Two terminals of one inductor brought
        // out side by side is one of the two common ways to draw a coil, and it is exactly what
        // this branch used to refuse.
        var driven = conductors.LabelsCarryingAPort(mesh, allPorts);

        int spanLo = probe.OwnLo, spanHi = probe.OwnHi;
        double nearest = double.PositiveInfinity;
        var members = new List<(int Lo, int Hi, PlanarPortResolution Port)>
        {
            (probe.OwnLo, probe.OwnHi, port),
        };

        double planeTol = 1e-9 * Math.Max(1.0, Math.Abs(port.ReferencePlaneM));

        for (int dir = 0; dir < 2 && declined is null; dir++)
        {
            bool up = dir == 0;
            while (true)
            {
                int edge = up ? spanHi + 1 : spanLo;
                int t = up ? spanHi + 1 : spanLo - 1;
                while (t >= 0 && t < nTran && !probe.Metal(outer, t)) t += up ? 1 : -1;
                if (t < 0 || t >= nTran) break;

                double gap = up ? gTran[t] - gTran[edge] : gTran[edge] - gTran[t + 1];
                if (gap >= drivenRequiredM) break;       // clear, and a group is never wider than
                                                          // it has to be

                int nLoT = t, nHiT = t;
                while (nLoT - 1 >= 0    && probe.Metal(outer, nLoT - 1)) nLoT--;
                while (nHiT + 1 < nTran && probe.Metal(outer, nHiT + 1)) nHiT++;

                int label = conductors.LabelOf(probe.CellAt(outer, t));

                if (!driven.Contains(label))
                {
                    declined =
                        $"{Who()} has a conductor {SurfaceMesher.Eng(gap)}m away that carries NO port, " +
                        "beside one that does. A calibration group's conductors are all driven and a " +
                        "widened profile's neighbour is floating at zero net charge, and one standard " +
                        "cannot hold both rules at once — the reference impedance it measures would " +
                        "belong to neither structure. Separate the undriven metal from this pair, or " +
                        "give it a port of its own so the group describes it.";
                    break;
                }

                // The neighbour's own port has to be AT THIS PLANE, or the two feeds are not one
                // port region and one standard cannot describe them both.
                PlanarPortResolution? peer = null;
                int found = 0;
                foreach (var q in allPorts)
                {
                    if (q.Number == port.Number) continue;
                    bool onIt = false;
                    foreach (int l in conductors.LabelsOf(mesh, q)) if (l == label) { onIt = true; break; }
                    if (!onIt) continue;
                    found++;
                    if (q.IsDeembeddable && q.Side == port.Side && q.Direction == port.Direction &&
                        q.LayerIndex == port.LayerIndex &&
                        Math.Abs(q.ReferencePlaneM - port.ReferencePlaneM) <= planeTol)
                        peer = q;
                }

                if (peer is null)
                {
                    declined =
                        $"{Who()} has a conductor {SurfaceMesher.Eng(gap)}m away carrying a port of " +
                        (found == 0 ? "its own that does not reach this reference plane"
                                    : $"its own ({found} port(s) on it), none of which sits at this " +
                                      "reference plane facing the same way") +
                        ". A calibration group is one standard cut at ONE plane, so its ports have to " +
                        "share that plane: the two modes it separates are the modes AT the plane. " +
                        "Bring the two ports to the same station, or separate the feeds.";
                    break;
                }

                if (peer.CrossSection is not null || peer.CutCellCount > 0)
                {
                    declined =
                        $"{Who()} is coupled to port {peer.Number}, which is " +
                        (peer.CrossSection is not null
                            ? "conductor-referenced (it returns through drawn metal)"
                            : "meshed with conformal boundary cells") +
                        ". Every conductor of a calibration group has to be cut the same way, because " +
                        "the standard is one mesh.";
                    break;
                }

                if (members.Count >= maxGroupSize)
                {
                    declined =
                        $"{Who()} would be in a CALIBRATION GROUP of more than {maxGroupSize} " +
                        "conductors: it is coupled to more driven metal than that. A group of N " +
                        $"conductors needs a {2 * maxGroupSize}-port " +
                        "standard carrying all of them, solved at every frequency, and its N modes " +
                        "have to stay separable — so the group size is capped rather than left to " +
                        "grow. Separate the feeds, or de-embed fewer of these ports.";
                    break;
                }

                if (gap < nearest) nearest = gap;
                members.Add((nLoT, nHiT, peer));
                if (up) spanHi = nHiT; else spanLo = nLoT;
            }
        }

        if (declined is not null) return null;
        if (members.Count < 2) return null;          // a group of one is not a group

        // ── R-pcal4-6 — EVERY conductor of the group has to survive the extrusion ──────────────
        int n = spanHi - spanLo + 1;
        var lines  = new double[n + 1];
        var labels = new int[n];
        for (int k = 0; k <= n; k++) lines[k] = gTran[spanLo + k];
        for (int k = 0; k < n; k++)
            labels[k] = probe.Metal(outer, spanLo + k) ? conductors.LabelOf(probe.CellAt(outer, spanLo + k)) : -1;

        for (int j = 1; j < endRunCells; j++)
        {
            int col = probe.Column(j);
            for (int k = 0; k < n; k++)
            {
                bool m = probe.Metal(col, spanLo + k);
                int lab = m ? conductors.LabelOf(probe.CellAt(col, spanLo + k)) : -1;
                if (lab == labels[k]) continue;

                double station = 0.5 * (probe.GLong[col] + probe.GLong[col + 1]);
                declined =
                    $"{Who()} is in a calibration group with a conductor that is not uniform over the " +
                    $"{SurfaceMesher.Eng(Math.Abs(station - port.OuterEdgeM))}m of line the " +
                    "calibration standard reproduces: at " +
                    $"{(probe.AlongX ? "x" : "y")} = {SurfaceMesher.Eng(station)}m the metal " +
                    $"{SurfaceMesher.Eng(0.5 * (lines[k] + lines[k + 1]))}m across " +
                    (m ? labels[k] < 0 ? "appears where the reference plane has none"
                                       : "belongs to a different conductor"
                       : "has ended or moved") +
                    ". A calibration standard is a uniform extrusion, so every conductor in it has to " +
                    "run straight past the plane. Separate the feeds, or lengthen the coupled section.";
                return null;
            }
        }

        // ── The profile: conductors numbered by transverse position, ports in the same order ────
        members.Sort((a, b) => a.Lo.CompareTo(b.Lo));
        var conductorOf = new int[n];
        Array.Fill(conductorOf, -1);
        var portNumbers = new int[members.Count];
        for (int k = 0; k < members.Count; k++)
        {
            portNumbers[k] = members[k].Port.Number;
            for (int t = members[k].Lo; t <= members[k].Hi; t++) conductorOf[t - spanLo] = k;
        }

        return new PlanarPortGroupProfile(lines, conductorOf, portNumbers, nearest);
    }

    private static string LayerName(PlanarMesh mesh, int layerIndex)
        => layerIndex >= 0 && layerIndex < mesh.LayerNames.Count
            ? $" ('{mesh.LayerNames[layerIndex]}')"
            : "";

    private static int IndexOf(IReadOnlyList<double> grid, double v)
    {
        int n = grid.Count - 1;
        if (n <= 0) return -1;
        if (v <= grid[0])  return 0;
        if (v >= grid[^1]) return n - 1;
        int lo = 0, hi = n - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (grid[mid] <= v) lo = mid; else hi = mid - 1;
        }
        return lo;
    }
}
