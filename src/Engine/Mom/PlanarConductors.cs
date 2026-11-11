// PCAL2 — WHICH PIECE OF METAL IS WHICH, ON THE MESH RATHER THAN ON THE ARTWORK.
//
// The port-calibration clearance check has to answer two questions about a cell of metal near a
// port: is it the port's OWN conductor, and if it is not, does it carry a port of its own. PCAL1
// measured that the second answer is worth a factor of 2-3 in the clearance a port needs
// (`src/Engine/Mom/RESOLVED.md`, "PCAL1"), and the first is what keeps the check off a taper's own
// flare — which is R-fed-1's job and not a neighbour at all.
//
// **The connectivity comes from the BASES, not from the polygons.** A rooftop basis exists for
// exactly those pairs of adjacent cells that are both metal, so the basis list already IS the
// conduction graph of the meshed structure — including a via's ẑ basis, which joins two levels.
// Re-deriving it from the drawn polygons would be a second answer to a question the mesh has
// already answered, and the two would disagree wherever the mesher staircased, cut or dropped a
// sliver. What the solve drives is cells, so what "the same conductor" means is cells — the same
// rule `PlanarPortCrossSection` states for the port profile one file over.

namespace CircuitRF.Engine.Mom;

/// <summary>
/// Connected runs of metal on a <see cref="PlanarMesh"/>: which cells conduct to which, and which of
/// those runs carry a port.
/// </summary>
public sealed class PlanarConductors
{
    private readonly int[]  _parent;
    private readonly bool[] _inBasis;

    /// <summary>
    /// <b>Does this cell carry current at all?</b> A cell that appears in no rooftop basis is not
    /// in the solve: no unknown is attached to it, nothing flows through it, and it contributes
    /// nothing to any answer.
    ///
    /// <para><b>This is not an edge case, it is the ordinary conformal mesh.</b> The Klopfenstein
    /// taper the ceiling tests run on meshes to 3,504 cells of which <b>32 are single cut cells with
    /// no basis</b> — slivers of the taper's OWN metal that R-cut-4 declines to drive, and which the
    /// basis graph therefore shows as 32 separate one-cell "conductors". Counting those as
    /// neighbours made PCAL2's clearance check refuse the taper at 0.13 substrate heights from its
    /// own artwork. A piece of metal that carries no current cannot make a calibration wrong, so it
    /// is not a neighbour.</para>
    /// </summary>
    public bool CarriesCurrent(int cellIndex)
        => (uint)cellIndex < (uint)_inBasis.Length && _inBasis[cellIndex];

    /// <summary>One label per cell of <see cref="PlanarMesh.Cells"/>: cells with the same label are
    /// joined by a chain of rooftop bases. Labels are arbitrary, dense over nothing in particular,
    /// and only ever compared for equality.</summary>
    public int LabelOf(int cellIndex) => Find(cellIndex);

    /// <summary>Do these two cells belong to one conductor?</summary>
    public bool SameConductor(int cellA, int cellB) => Find(cellA) == Find(cellB);

    private PlanarConductors(int cellCount)
    {
        _parent  = new int[cellCount];
        _inBasis = new bool[cellCount];
    }

    /// <summary>
    /// Label every cell of <paramref name="mesh"/>. O(N·α) over the basis list, which on the largest
    /// mesh this kernel will accept is a few thousand entries — this is asked once per solve, before
    /// the first fill, and never inside the frequency loop.
    /// </summary>
    public static PlanarConductors Of(PlanarMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var c = new PlanarConductors(mesh.Cells.Count);
        for (int i = 0; i < c._parent.Length; i++) c._parent[i] = i;

        foreach (var b in mesh.Bases)
        {
            // A ground-attachment basis has one meshed foot and names it twice; the union is a
            // no-op there rather than a special case.
            if ((uint)b.CellA >= (uint)c._parent.Length || (uint)b.CellB >= (uint)c._parent.Length)
                continue;
            c._inBasis[b.CellA] = true;
            c._inBasis[b.CellB] = true;
            c.Union(b.CellA, b.CellB);
        }
        return c;
    }

    /// <summary>
    /// The labels of the conductors <paramref name="ports"/> stand on — one per port, deduplicated.
    /// <b>Every port, including the one being asked about</b>: the caller separates "mine" from
    /// "someone else's" by comparing against that port's own label, which it has.
    /// </summary>
    public HashSet<int> LabelsCarryingAPort(PlanarMesh mesh, IEnumerable<PlanarPortResolution> ports)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(ports);
        var labels = new HashSet<int>();
        foreach (var p in ports)
            foreach (int label in LabelsOf(mesh, p))
                labels.Add(label);
        return labels;
    }

    /// <summary>The labels of the conductor(s) one port's own bases stand on. More than one only for
    /// a conductor-referenced port, whose two terminals are by construction not connected.</summary>
    public IEnumerable<int> LabelsOf(PlanarMesh mesh, PlanarPortResolution port)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(port);
        var seen = new HashSet<int>();
        foreach (int bi in port.BasisIndices)
        {
            if ((uint)bi >= (uint)mesh.Bases.Count) continue;
            var b = mesh.Bases[bi];
            if ((uint)b.CellA < (uint)_parent.Length && seen.Add(Find(b.CellA))) yield return Find(b.CellA);
            if ((uint)b.CellB < (uint)_parent.Length && seen.Add(Find(b.CellB))) yield return Find(b.CellB);
        }
    }

    private int Find(int i)
    {
        while (_parent[i] != i) i = _parent[i] = _parent[_parent[i]];
        return i;
    }

    private void Union(int a, int b)
    {
        a = Find(a); b = Find(b);
        if (a != b) _parent[b] = a;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // A DRAWN CONDUCTOR THE MESH HAS SEVERED (2026-09-12)
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Two ports on ONE drawn polygon that cannot reach each other through the basis graph.</b>
    /// The metal the user drew is continuous and the solve's conduction graph is not, so no current
    /// can pass between them however the structure is excited.
    /// </summary>
    /// <param name="LayerIndex">The conductor level the polygon is on.</param>
    /// <param name="PolygonIndex">Which of that level's polygons.</param>
    /// <param name="Islands">The port numbers, grouped by what each group can reach. Two or more
    /// groups is the finding; the ports within one group are fine with each other.</param>
    public sealed record PlanarSeveredConductor(
        int LayerIndex, int PolygonIndex, IReadOnlyList<IReadOnlyList<int>> Islands);

    /// <summary>
    /// <b>Does the meshed structure conduct where the drawn artwork does?</b> Returns one entry per
    /// drawn polygon whose ports have been split into more than one conduction island, and an empty
    /// list — the overwhelmingly common answer — when nothing is severed.
    ///
    /// <para><b>Why this is asked at all.</b> A rooftop exists only where the shared edge of two
    /// adjacent cells is swept by metal in both (R-cut-4's <c>Anchored</c> test, which is
    /// all-or-nothing over a support's strips). On a conformally cut mesh a shallow oblique rim can
    /// therefore decline EVERY rooftop across a bend, and the conductor is cut in two with nothing
    /// to say so: the matrix is well formed, the solve converges, and the published s-parameters are
    /// a smooth, plausible OPEN CIRCUIT. Measured on a 45° bend at cells/λ 5 with the edge mesh off:
    /// S₂₁ = −42 dB and S₁₁ = −0.02 dB on a 3.8 mm through line whose answer is −0.08 dB and
    /// −19.3 dB, <b>passive at every frequency</b>, so R-prt-15 cannot see it either. The same file
    /// with <c>Staircase</c> boundary cells, or with the edge mesh on, is correct.</para>
    ///
    /// <para><b>The artwork is the ground truth and the PORTS are the probe</b>, which is what keeps
    /// this off every legitimately undriven sliver. A conformal taper routinely meshes with dozens of
    /// cut cells that carry no basis at all — <see cref="CarriesCurrent"/>'s own note — and those are
    /// separate one-cell "conductors" in the basis graph on every such run. Comparing the whole basis
    /// graph against the whole artwork would fire on all of them. Asking only whether two PORTS on
    /// one drawn polygon can reach each other asks exactly the question whose answer changes a
    /// published number.</para>
    ///
    /// <para>It can only UNDER-report, deliberately: two ports on polygons that merely touch are
    /// physically one conductor and are not grouped here, and a one-port polygon has nothing to be
    /// disconnected from. An under-report leaves today's behaviour, which is what shipped.</para>
    /// </summary>
    public static IReadOnlyList<PlanarSeveredConductor> FindSeveredConductors(
        PlanarProblem problem, PlanarMesh mesh, IReadOnlyList<PlanarPortResolution> ports)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(ports);
        mesh.EnsureNoBlocks("the conduction check");   // PMS-4 — refused by name until it reads blocks

        var conn = Of(mesh);

        // (layer, polygon) → the ports standing on it, each with the label it can reach.
        var byPolygon = new Dictionary<(int Layer, int Poly), List<(int Number, int Label)>>();

        foreach (var port in ports)
        {
            // A point that is certainly INSIDE the metal: a cell's metal centroid, not the port's
            // own location, which sits on the rim where containment is a coin toss.
            if (!TryInteriorPoint(conn, mesh, port, out var probe, out int label)) continue;
            if ((uint)port.LayerIndex >= (uint)problem.Layers.Count) continue;

            var polys = problem.Layers[port.LayerIndex].Polygons;
            for (int k = 0; k < polys.Count; k++)
            {
                if (!Polygon2D.ContainsStrict(polys[k].Outer, probe)) continue;

                bool inHole = false;
                foreach (var hole in polys[k].HoleRings)
                    if (Polygon2D.ContainsStrict(hole, probe)) { inHole = true; break; }
                if (inHole) continue;

                var key = (port.LayerIndex, k);
                if (!byPolygon.TryGetValue(key, out var list))
                    byPolygon[key] = list = [];
                list.Add((port.Number, label));
                break;
            }
        }

        var severed = new List<PlanarSeveredConductor>();
        foreach (var (key, list) in byPolygon)
        {
            if (list.Count < 2) continue;

            var islands = new Dictionary<int, List<int>>();
            foreach (var (number, label) in list)
            {
                if (!islands.TryGetValue(label, out var group)) islands[label] = group = [];
                group.Add(number);
            }
            if (islands.Count < 2) continue;

            var groups = new List<IReadOnlyList<int>>();
            foreach (var group in islands.Values) { group.Sort(); groups.Add(group); }
            groups.Sort((a, b) => a[0].CompareTo(b[0]));
            severed.Add(new PlanarSeveredConductor(key.Layer, key.Poly, groups));
        }

        severed.Sort((a, b) => a.LayerIndex != b.LayerIndex
            ? a.LayerIndex.CompareTo(b.LayerIndex)
            : a.PolygonIndex.CompareTo(b.PolygonIndex));
        return severed;
    }

    /// <summary>A point inside one of the port's own cells, and the conduction label it carries.
    /// False for a port whose bases do not resolve onto cells at all.</summary>
    private static bool TryInteriorPoint(
        PlanarConductors conn, PlanarMesh mesh, PlanarPortResolution port,
        out EmPoint point, out int label)
    {
        point = default;
        label = -1;

        foreach (int bi in port.BasisIndices)
        {
            if ((uint)bi >= (uint)mesh.Bases.Count) continue;
            var b = mesh.Bases[bi];
            if ((uint)b.CellA >= (uint)mesh.Cells.Count) continue;

            var cell = mesh.Cells[b.CellA];
            point = new EmPoint(cell.CentroidX, cell.CentroidY);
            label = conn.LabelOf(b.CellA);
            return true;
        }
        return false;
    }
}
