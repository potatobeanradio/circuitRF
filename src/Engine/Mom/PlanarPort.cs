// L8d — the port: what one IS, how it resolves onto L8b's mesh, and the refusals.
//
// D2 — THE PORT CUT IS THE OUTERMOST ROOFTOP ROW OF THE FEED, AND NOTHING IS USER-POSITIONABLE.
// A port names an end of a conductor; the reference plane is then the shared edge of the two
// outermost cells there, one cell in from the drawn metal, and the half-cell beyond it is part of
// the error box. There is no port-offset setting, no reference-plane coordinate and no de-embedding
// distance to choose, because ALL of that is what the calibration removes (PlanarCalibration) — and
// offering a knob for it would offer a way to get a different answer for the same structure.
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
/// D3 — the only ground reference v1 has. The enum exists so the refusal can be worded against a
/// named alternative rather than against "not implemented", which is R-mom-17's whole point.
/// </summary>
public enum PlanarPortReference
{
    /// <summary>The stackup's ground plane. The only one L8's kernel can represent.</summary>
    GroundPlane,
    /// <summary>Coplanar ground conductors either side of the signal line — §10.6, later work.</summary>
    CoplanarGround,
    /// <summary>A second signal conductor (a differential/balanced port) — §10.6, later work.</summary>
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
/// <param name="Location">A point on (or just inside) the conductor at the port's end. Only its
/// TRANSVERSE coordinate is used to pick the conductor run; the longitudinal one is not, because
/// D2 fixes the cut at the outermost row.</param>
/// <param name="Side">Which end of the conductor this is.</param>
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
public sealed record PlanarPort(
    int                 Number,
    EmPoint             Location,
    PlanarPortSide      Side,
    Complex             Z0,
    int?                LayerIndex = null,
    PlanarPortReference Reference  = PlanarPortReference.GroundPlane)
{
    /// <summary>The basis direction the port's rooftop row is drawn from.</summary>
    public PlanarBasisDirection Direction =>
        Side is PlanarPortSide.MinX or PlanarPortSide.MaxX
            ? PlanarBasisDirection.X
            : PlanarBasisDirection.Y;

    /// <summary>
    /// D1's ±1. A rooftop's current is positive along +x̂ (or +ŷ); current flowing INTO the structure
    /// is +x̂ at a MinX port and −x̂ at a MaxX one. The SAME sign is used for the excitation and for
    /// reading the current back — that is what makes <c>Y = BᵀZ⁻¹B</c> the actual admittance matrix
    /// rather than a sign-scrambled relative of one.
    /// </summary>
    public double IncidenceSign =>
        Side is PlanarPortSide.MinX or PlanarPortSide.MinY ? +1.0 : -1.0;
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
/// <param name="UndrivenMetalM">Metal on the reference plane, adjacent to the port's own run, that
/// carries NO rooftop and is therefore not driven — R-cut-4 declining the outermost cell pair of a
/// conformal feed. Zero under the staircase and on any Manhattan feed. It is reported rather than
/// silently absorbed because it is the one thing a conformal port does WORSE than a staircased one,
/// and refining the transverse mesh is what shrinks it.</param>
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
    double                 UndrivenMetalM = 0)
{
    public int BasisCount => BasisIndices.Count;

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
        $"Port {Number} resolved to {BasisCount} basis function(s) across " +
        $"{SurfaceMesher.Eng(WidthM)}m of conductor; reference plane at " +
        $"{(Direction == PlanarBasisDirection.X ? "x" : "y")} = {SurfaceMesher.Eng(ReferencePlaneM)}m, " +
        $"one cell in from the metal edge at {SurfaceMesher.Eng(OuterEdgeM)}m." +
        (CutCellCount == 0 && UndrivenMetalM <= 0 ? "" : ConformalNote());

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
        return list;
    }

    public static bool TryResolve(PlanarMesh mesh, PlanarPort port,
                                  out PlanarPortResolution? resolution, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(port);
        resolution = null;

        // ── D3: the ground reference is not negotiable, and the refusal names the alternative ────
        if (port.Reference != PlanarPortReference.GroundPlane)
        {
            refusal = port.Reference switch
            {
                // L9 has now arrived and these are still not built — which is the point of naming
                // the arrival place rather than a phase number. §10.6 lists differential and
                // multi-mode ports as later work, and L9's own out-of-scope list keeps them there.
                PlanarPortReference.CoplanarGround =>
                    $"Port {port.Number} asks for a coplanar ground reference, and every port this " +
                    "kernel builds returns through the stackup's ground plane — the delta gap is " +
                    "across a conductor's own cut, with the plane as the second terminal. A coplanar " +
                    "return is a different port model, not a different level: §10.6 lists coplanar, " +
                    "differential and multi-mode ports as later work, and nothing in this repository provides one.",
                PlanarPortReference.SecondConductor =>
                    $"Port {port.Number} asks for a second-conductor (differential) reference, and " +
                    "every port this kernel builds returns through the stackup's ground plane. A " +
                    "differential pair is a two-mode port, not a two-level one: §10.6 lists coplanar, " +
                    "differential and multi-mode ports as later work, and nothing in this repository provides one.",
                // L9d/§0.2 item 2 — the one genuinely new port question this phase had to answer.
                _ => ViaPortRefusal(port.Number),
            };
            return false;
        }

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

    private static bool TryResolveOnLayer(PlanarMesh mesh, PlanarPort port, int layerIndex,
                                          out PlanarPortResolution? resolution, out string? refusal)
    {
        resolution = null;

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

        // ── March in from the named side until metal appears: that column IS the outer one (D2) ──
        bool fromLow = port.Side is PlanarPortSide.MinX or PlanarPortSide.MinY;
        int  outer   = -1;
        for (int k = 0; k < nLong; k++)
        {
            int i = fromLow ? k : nLong - 1 - k;
            if (CellAt(i, seedT) >= 0) { outer = i; break; }
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

        // ── Basis lookup. Built by ONE forward pass over Bases; queried, never iterated. ─────────
        var byPair = new Dictionary<(int A, int B, PlanarBasisDirection D), int>(mesh.Bases.Count);
        for (int b = 0; b < mesh.Bases.Count; b++)
        {
            var bs = mesh.Bases[b];
            byPair[(bs.CellA, bs.CellB, bs.Direction)] = b;
        }

        int lowCol = Math.Min(outer, inner), highCol = Math.Max(outer, inner);
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
        bool HasBasis(int t)
        {
            int a = CellAt(lowCol, t), b = CellAt(highCol, t);
            return a >= 0 && b >= 0 && byPair.ContainsKey((a, b, port.Direction));
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
        while (mLo - 1 >= 0    && CellAt(lowCol, mLo - 1) >= 0 && CellAt(highCol, mLo - 1) >= 0) mLo--;
        while (mHi + 1 < nTran && CellAt(lowCol, mHi + 1) >= 0 && CellAt(highCol, mHi + 1) >= 0) mHi++;

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
        double gridWidth = gTran[hi + 1] - gTran[lo];
        double plane     = fromLow ? gLong[outer + 1] : gLong[outer];
        double edge      = fromLow ? gLong[outer]     : gLong[outer + 1];

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

        var run = new List<double>();
        for (int k = 0; ; k++)
        {
            int i = fromLow ? outer + k : outer - k;
            if (i < 0 || i >= nLong || CellAt(i, seedT) < 0) break;
            run.Add(gLong[i + 1] - gLong[i]);
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
            UndrivenMetalM:    undriven);

        refusal = null;
        return true;
    }

    /// <summary>
    /// R-prt-3 — the feed must be uniform and isolated for the distance the calibration replaces.
    /// Returns a WARNING, not a refusal: a user may knowingly accept a crowded feed, and the number
    /// they need in order to decide is the measured clearance, not a yes/no.
    /// </summary>
    public static string? CheckFeedClearance(PlanarMesh mesh, PlanarPortResolution port,
                                             double requiredM)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(port);
        if (!(requiredM > 0)) return null;

        bool alongX = port.Direction == PlanarBasisDirection.X;
        bool fromLow = port.Side is PlanarPortSide.MinX or PlanarPortSide.MinY;

        double tLo = port.TransverseLines[0], tHi = port.TransverseLines[^1];
        double nearest = double.PositiveInfinity;

        foreach (var c in mesh.Cells)
        {
            double t0 = alongX ? c.YMin : c.XMin;
            double t1 = alongX ? c.YMax : c.XMax;
            if (t1 > tLo + 1e-15 && t0 < tHi - 1e-15) continue;   // inside the feed's own width

            // ── IS THIS CELL IN THE FEED REGION AT ALL? (fixed 2026-08-12) ──────────────────────
            //
            // `along` used to be measured to the cell's NEAR edge and then used only to skip cells
            // BEHIND the port — there was no upper bound at all, so the scan ran to the far end of
            // the structure and `nearest` came back as the smallest lateral gap ANYWHERE on the
            // board. That is not the quantity this warning's own text describes ("inside the
            // {required}m the calibration standard assumes is empty"), and the difference is not
            // cosmetic: it fired on every part that is ever wider than its port — every taper, stub
            // and tee — including feeds that are demonstrably clean. A warning that cannot be
            // cleared is one users learn to skip, and this is the one that has to stay readable,
            // because it is what R-fed-1's automatic lead CANNOT fix: a lead lengthens a feed, it
            // cannot move a neighbour sideways.
            //
            // The station is the cell's MIDPOINT, not its near edge, and that is load-bearing rather
            // than tidy. R-fed-1 sizes the lead so the feed is uniform for EXACTLY `requiredM`, so
            // the DUT's own flare always begins at the region's far boundary and the cell straddling
            // it always has a lateral gap of zero. On a near-edge test that cell re-fires the warning
            // on every extended taper — reintroducing the unclearable warning one line below the fix
            // for it. A cell is judged by where most of it sits.
            double l0 = alongX ? c.XMin : c.YMin;
            double l1 = alongX ? c.XMax : c.YMax;
            double mid = 0.5 * (l0 + l1);
            double along = fromLow ? mid - port.OuterEdgeM : port.OuterEdgeM - mid;
            if (along < -requiredM || along > requiredM) continue;

            double across = t0 >= tHi ? t0 - tHi : tLo - t1;
            nearest = Math.Min(nearest, Math.Max(across, 0));
        }

        if (double.IsInfinity(nearest) || nearest >= requiredM) return null;

        return $"Port {port.Number}'s feed has other metal {SurfaceMesher.Eng(nearest)}m away, inside " +
               $"the {SurfaceMesher.Eng(requiredM)}m the calibration standard assumes is empty. The " +
               "de-embedding replaces the port's neighbourhood with an isolated line of the same " +
               "width, so whatever is closer than that is not removed correctly. Move the feed away, " +
               "or read the result knowing this.";
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
