// brief-em3d-5 R-em3d5-3c — the size of a 3D run before it starts.
//
// Two kinds of number, never confused: Palace's is an ESTIMATE (its mesher and its adaptive refinement
// decide the real count, and no Gmsh is run to find out), openEMS's is EXACT (brief 8's grid generator
// IS the grid). Every per-unknown figure below is a measurement, named with the F0 run it came from
// (docs/design/em-3d-f0-findings.md §4, R-em3d1-2b), and a figure with no measurement behind it is not
// a constant here at all — so it cannot be printed.

namespace CircuitRF.Engine.Em3d;

/// <summary>One region's share of a Palace estimate.</summary>
public sealed record Em3dRegionEstimate(string Solid, double VolumeM3, double ElementEdgeM, double Tetrahedra);

/// <summary>A Palace mesh size, ESTIMATED, and the peak memory it implies (orders 1 and 2 each have a
/// measured per-unknown figure; the record keeps the field nullable for an order that has none).</summary>
/// <summary>
/// Designer feedback round 11 — the mesh script's refinement, as the estimate prices it: the element at conductors and
/// sheets, the element at each port sheet, the growth away from them, and the largest element they grow to. The script's
/// own figures (GmshGeoWriter), passed in so the two cannot disagree.
/// </summary>
public sealed record Em3dRefinementSizing(double ConductorEdgeM, Func<Em3dPort, double> PortEdgeM, double Grading, double MaxM);

public sealed record Em3dPalaceEstimate(
    long Tetrahedra, long Unknowns, int Order, long? MemoryBytes, IReadOnlyList<Em3dRegionEstimate> Regions)
{
    /// <summary>EMB-4 R-emb4-1 — of <see cref="Tetrahedra"/>, the share priced for the outlines' SHORT EDGES
    /// (<see cref="Em3dSizeEstimate.OutlineTetrahedraPerShortEdge"/>); 0 with no refinement sizing.</summary>
    public long OutlineTetrahedra { get; init; }

    /// <summary>The short-edge term is a third or more of the estimate — then the largest single correction in it, the
    /// least certain one, and the case an imported outline produces. <c>explain</c> says so.</summary>
    public bool OutlineDominates => OutlineTetrahedra > 0 && 3 * OutlineTetrahedra >= Tetrahedra;
}

public static class Em3dSizeEstimate
{
    /// <summary>Nédélec unknowns per tetrahedron at element order 1: F0 case A, the same 146,769-tet
    /// mesh run at order 1 (<c>A palace-round-order1</c>, 178,142 unknowns).</summary>
    public const double UnknownsPerTetOrder1 = 178_142.0 / 146_769.0;

    /// <summary>…and at order 2, Palace's default: F0 case A (<c>A palace-round</c>, 953,946 unknowns
    /// on 146,769 tets). Case B's 593,548 on 87,467 gives 6.79, within 5 %.</summary>
    public const double UnknownsPerTetOrder2 = 953_946.0 / 146_769.0;

    /// <summary>
    /// brief-dfr26-8 R-dfr26-8-3 — a STATIC problem's unknowns per tetrahedron at order 2. Palace solves
    /// electrostatics and magnetostatics for a scalar potential on H1 elements, not for a field on Nédélec edges, so a
    /// mesh carries about a quarter of a driven solve's unknowns. Measured on the round-26 ten-layer board's
    /// electrostatic run: 399,300 unknowns on 237,080 tetrahedra.
    /// </summary>
    public const double UnknownsPerTetStaticOrder2 = 399_300.0 / 237_080.0;

    /// <summary>…and at order 1: the same run's first multigrid level (p = 1), 59,099 unknowns on the same mesh — one
    /// per vertex.</summary>
    public const double UnknownsPerTetStaticOrder1 = 59_099.0 / 237_080.0;

    /// <summary>
    /// brief-dfr26-8 R-dfr26-8-3 — Palace's peak memory per unknown for a static problem at order 2, as Palace reports it
    /// (the <c>Peak Memory</c> table's total high-water mark): 4.9 GB for those 399,300 unknowns, ten ranks, with the error
    /// estimator's construction (1.1 GB) included. Order 1 has no measurement, so a static order-1 run has no memory
    /// figure rather than a borrowed one.
    /// </summary>
    public const double PalaceBytesPerUnknownStaticOrder2 = 4.9e9 / 399_300.0;

    /// <summary>
    /// Palace's peak memory per unknown at order 2, all eight ranks together, as Palace reports it:
    /// F0's <c>B palace</c>, 7.0 GB for 593,548 unknowns — the HIGHEST of the order-2 runs without
    /// adaptive refinement (case A's is 7.4 GB for 953,946), so an estimate errs toward not fitting.
    /// Order 1 has its own figure below, which is mostly fixed cost.
    /// </summary>
    public const double PalaceBytesPerUnknownOrder2 = 7.0e9 / 593_548.0;

    /// <summary>
    /// brief-em3d-21 R-em3d21-2 — Palace's peak memory per unknown at order 1: F0's
    /// <c>A palace-round-order1</c>, 6.6 GB for 178,142 unknowns (eight ranks). That run's memory is
    /// mostly what the ranks cost before the first unknown, so as a per-unknown figure it errs high on
    /// any larger problem — the direction a fit check wants — and low only on problems far too small
    /// for memory to be the question. It is what makes a Draft run's memory printable at all.
    /// </summary>
    public const double PalaceBytesPerUnknownOrder1 = 6.6e9 / 178_142.0;

    /// <summary>
    /// Palace's peak memory per unknown with the DIRECT linear solver (SuperLU, Palace.LinearSolver: Direct) at order 2, ten
    /// ranks, as Palace reports it: a four-layer via transition, 6.2 GB for 192,984 unknowns and 4.7 GB for 148,348 (31.7 KB
    /// an unknown, so linear over that range). The same meshes on the iterative solver took 3.0 and 2.3 GB. The factor's
    /// fill-in grows faster than the unknowns on a much larger mesh, so far past these sizes this reads low.
    /// </summary>
    public const double PalaceDirectBytesPerUnknownOrder2 = 6.2e9 / 192_984.0;

    /// <summary>…and at order 1: the same transition's first-order mesh, 1.6 GB for 36,546 unknowns (the iterative solver
    /// 1.3 GB), mostly what the ranks cost before the first unknown, as the iterative order-1 figure is.</summary>
    public const double PalaceDirectBytesPerUnknownOrder1 = 1.6e9 / 36_546.0;

    /// <summary>
    /// brief-em3d-21 R-em3d21-2 — how much adaptive mesh refinement multiplies Palace's peak: F0's
    /// case A on one starting mesh, 11.9 GB with two passes (<c>A palace-round-amr</c>) against 7.4 GB
    /// with none (<c>A palace-round</c>). Most of it is the error estimator, which costs the same from
    /// the first pass on (that run's first solve alone peaked at 10.8 GB); the passes themselves add
    /// the refined elements.
    /// </summary>
    public const double PalaceRefinementMemoryFactor = 11.9 / 7.4;

    /// <summary>
    /// openEMS's peak resident memory per cell: F0's wire ladder at its finest rung, 182 MB for
    /// 1,558,730 cells — the largest run F0 made, so the one whose fixed cost is the smallest share.
    /// Smaller runs read higher per cell (47 MB for 199,200) and are small in absolute terms.
    /// </summary>
    public const double OpenEmsBytesPerCell = 182e6 / 1_558_730.0;

    /// <summary>
    /// Designer feedback round 11 — how many tetrahedra Gmsh puts in a volume sized uniformly at an edge h, as a share of
    /// the volume over one REGULAR tetrahedron of edge h: measured on the shipped connector launch with its air box at the
    /// default padding, with every refined shell priced as below and taken off, 0.60 (88,700 mm³ of air at 1.67 mm, sweep
    /// from 2 GHz; Gmsh 4.15.2 made 190,367) and 0.55 (553,500 mm³, sweep from 1 GHz; 648,947). The larger is kept, so
    /// an estimate errs high. Without it, a background-dominated estimate read 1.6× Gmsh's count.
    /// </summary>
    public const double GmshVolumeFill = 0.6;

    /// <summary>
    /// EMB-4 R-emb4-1 — the tetrahedra an outline edge SHORTER than the element the script asks for there adds:
    /// <c>K·√(e/h)·(1 − e/h)</c> for an edge of length e against an element h, this being K. Gmsh meshes every curve
    /// with at least one segment, so a short edge puts nodes e apart on the faces that meet it and the mesh has to grow
    /// from e back to h around it. Measured with Gmsh 4.15 on the shipped microstrip beside an 8 mm pour whose top edge
    /// is a sampled sinusoid (12.9 mm of outline at a 313 µm conductor element), against the same pour with a straight
    /// edge: 120, 400 and 800 edges added 2,784, 7,109 and 10,751 tetrahedra, which is K = 61.7, 62.4 and 63.0 — the
    /// largest is kept, so the term errs high. A per-edge constant (the linear form) read 35, 20 and 14 on the same
    /// three: the count per edge falls as the edges shorten, as the square root of their length.
    /// Without the term an imported board read 43,800 elements against Gmsh's 185,000 (src/Design/RESOLVED.md, EMB-4).
    /// </summary>
    public const double OutlineTetrahedraPerShortEdge = 63;

    /// <summary>
    /// EMB-4 R-emb4-1 — the short-edge term for <paramref name="rings"/> meshed at <paramref name="edgeM"/>:
    /// <see cref="OutlineTetrahedraPerShortEdge"/> × √(e/h) × (1 − e/h) for every edge of length e below h.
    /// </summary>
    public static double ShortEdgeTetrahedra(IEnumerable<IReadOnlyList<Point2>> rings, double edgeM)
    {
        if (!(edgeM > 0)) return 0;
        double n = 0;
        foreach (var r in rings)
            for (int i = 0, j = r.Count - 1; i < r.Count; j = i++)
            {
                double e = Math.Sqrt((r[i].X - r[j].X) * (r[i].X - r[j].X) + (r[i].Y - r[j].Y) * (r[i].Y - r[j].Y));
                if (e > 0 && e < edgeM) n += OutlineTetrahedraPerShortEdge * Math.Sqrt(e / edgeM) * (1 - e / edgeM);
            }
        return n;
    }

    /// <summary>
    /// The initial Palace mesh, estimated: every MESHED region's volume divided by the volume of one
    /// regular tetrahedron at that region's initial element edge, times <see cref="GmshVolumeFill"/>, summed,
    /// plus the refined shells round conductors, sheets and ports when <c>refinement</c> is given. Conductors are holes in a
    /// Palace mesh (em-3d.md §6.1), so only dielectric and air solids count. Each solid is counted as
    /// drawn: where a later solid overlaps an earlier one the overlap is counted twice, so the
    /// estimate errs high. Adaptive refinement grows the real count, and the run's first line reports
    /// it.
    /// </summary>
    /// <param name="initialEdgeM">A solid's initial element edge, metres — the Palace section's
    /// size fields (brief 7) resolved for that solid.</param>
    /// <param name="order">The Nédélec element order, 1 or 2.</param>
    /// <param name="backgroundEdgeM">The initial edge of the BACKGROUND — the part of the air box no
    /// solid claims, which the mesher fills as air (brief 7's <c>background</c> group). On a setup whose
    /// floor is absorbing that is everything below the stack, often as large as the air above it, so
    /// leaving it out halved the count. Null leaves it out.</param>
    /// <param name="refinementPasses">The most adaptive refinement passes the run allows; any at all
    /// multiplies the memory by <see cref="PalaceRefinementMemoryFactor"/>.</param>
    /// <param name="refinement">Designer feedback round 11 — the mesh script's refinement at conductors, sheets and ports
    /// (<see cref="Em3dRefinementSizing"/>), priced as a shell over each surface. Null leaves it out, as every estimate did
    /// before: the shipped connector launch then read 2,037 tetrahedra against Gmsh's 93,371.</param>
    /// <remarks>A static problem (<see cref="Em3dProblem.IsStatic"/>) is priced in Palace's scalar unknowns
    /// (<see cref="UnknownsPerTetStaticOrder2"/>), brief-dfr26-8.</remarks>
    /// <param name="direct">Priced for the direct linear solver (<see cref="PalaceDirectBytesPerUnknownOrder2"/>).</param>
    public static Em3dPalaceEstimate Palace(Em3dProblem problem, Func<Em3dSolid, double> initialEdgeM, int order,
                                            double? backgroundEdgeM = null, int refinementPasses = 0,
                                            Em3dRefinementSizing? refinement = null, bool direct = false)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(initialEdgeM);
        if (order is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(order), order, "F0 measured element orders 1 and 2 only");

        var regions = new List<Em3dRegionEstimate>();
        double tets = 0, outline = 0;
        foreach (var s in problem.Solids.Where(s => s.Role != Em3dRole.Conductor))
        {
            double h = initialEdgeM(s);
            double v = Volume(s.Primitive);
            double n = h > 0 ? GmshVolumeFill * v / RegularTetVolume(h) : 0;
            regions.Add(new Em3dRegionEstimate(s.Name, v, h, n));
            tets += n;
        }
        if (backgroundEdgeM is { } hb && hb > 0)
        {
            // The box less every solid, conductors included (a hole is still not background). Solids
            // counted twice where they overlap make this smaller, never larger, so it cannot go past
            // the real background — and it is clamped at zero.
            var (lo, hi) = (problem.Boundary.Min, problem.Boundary.Max);
            double box = (hi.X - lo.X) * (hi.Y - lo.Y) * (hi.Z - lo.Z);
            double v = Math.Max(0, box - problem.Solids.Sum(s => Volume(s.Primitive)));
            double n = GmshVolumeFill * v / RegularTetVolume(hb);
            if (v > 0) regions.Add(new Em3dRegionEstimate("background", v, hb, n));
            tets += n;
        }
        if (refinement is { } r && r.Grading > 1 && r.MaxM > 0)
        {
            // Each refined surface's shell REPLACES the coarse elements it overlaps: the shells' volume is taken off the count
            // at the LARGEST element (the smallest deduction, so the estimate errs high), never below zero.
            double shells = 0, overlap = 0;
            void Shell(string name, double areaM2, double nearM)
            {
                if (!(areaM2 > 0) || !(nearM > 0)) return;
                double near = Math.Min(nearM, r.MaxM);
                double n = ShellTetrahedra(areaM2, near, r.Grading, r.MaxM);
                double depth = near + (r.MaxM - near) / (r.Grading - 1);
                regions.Add(new Em3dRegionEstimate(name, areaM2 * depth, near, n));
                shells += n;
                overlap += GmshVolumeFill * areaM2 * depth / RegularTetVolume(r.MaxM);
            }
            foreach (var s in problem.Solids.Where(s => s.Role == Em3dRole.Conductor))
                Shell($"{s.Name} (surface)", SurfaceArea(s.Primitive), r.ConductorEdgeM);
            foreach (var sh in problem.Sheets)
                Shell($"{sh.Name} (sheet, both sides)",
                      2 * (Math.Abs(Area(sh.Outline)) - sh.Holes.Sum(h => Math.Abs(Area(h)))), r.ConductorEdgeM);
            foreach (var p in problem.Ports)
                Shell($"port {p.Name} (both sides)", 2 * PortArea(p), r.PortEdgeM(p));
            tets = Math.Max(tets - overlap, 0) + shells;

            // EMB-4 R-emb4-1 — the outlines' short edges: a conductor's or a sheet's at the conductor element, a
            // dielectric's at its own.
            foreach (var s in problem.Solids)
                if (s.Primitive is Em3dExtrudedPolygon e)
                    outline += ShortEdgeTetrahedra([e.Outline, .. e.Holes],
                                                   s.Role == Em3dRole.Conductor ? r.ConductorEdgeM : initialEdgeM(s));
            foreach (var sh in problem.Sheets)
                outline += ShortEdgeTetrahedra([sh.Outline, .. sh.Holes], r.ConductorEdgeM);
            if (outline > 0) regions.Add(new Em3dRegionEstimate("outline short edges", 0, r.ConductorEdgeM, outline));
            tets += outline;
        }
        long unknowns = (long)Math.Round(tets * UnknownsPerTet(order, problem.IsStatic));
        return new Em3dPalaceEstimate((long)Math.Round(tets), unknowns, order,
                                      PalaceMemoryBytes(unknowns, order, refinementPasses, problem.IsStatic, direct), regions)
               { OutlineTetrahedra = (long)Math.Round(outline) };
    }

    /// <summary>Palace's unknowns per tetrahedron: Nédélec for a field (driven, eigenmode), H1 for a static potential.</summary>
    public static double UnknownsPerTet(int order, bool isStatic) => (order, isStatic) switch
    {
        (1, false) => UnknownsPerTetOrder1,
        (_, false) => UnknownsPerTetOrder2,
        (1, true)  => UnknownsPerTetStaticOrder1,
        _          => UnknownsPerTetStaticOrder2,
    };

    /// <summary>
    /// brief-em3d-21 — Palace's peak memory for a mesh whose tetrahedra are KNOWN (Gmsh has printed its
    /// count), through the same measured unknowns-per-tetrahedron and bytes-per-unknown figures. After
    /// meshing this is the check that counts: the volume estimate above leaves out the refinement at
    /// conductors and ports, which on a bond wire is nearly the whole mesh (F0's case A: 146,769
    /// tetrahedra, where the volumes alone give about 500).
    /// </summary>
    public static long PalaceMemoryBytesForMesh(long tetrahedra, int order, int refinementPasses, bool direct = false)
        => PalaceMemoryBytes((long)Math.Round(tetrahedra * UnknownsPerTet(order, false)), order, refinementPasses, direct);

    /// <summary>brief-dfr26-8 — the same for a static problem, in its scalar unknowns; null at order 1, which has no
    /// measured memory figure.</summary>
    public static long? PalaceStaticMemoryBytesForMesh(long tetrahedra, int order, int refinementPasses)
        => PalaceMemoryBytes((long)Math.Round(tetrahedra * UnknownsPerTet(order, true)), order, refinementPasses, isStatic: true);

    /// <summary>
    /// brief-dfr27-5 R-dfr27-5-5 — the process count the driven per-unknown figures were measured on (F0's runs, eight ranks).
    /// </summary>
    public const int PalaceMeasuredProcesses = 8;

    /// <summary>
    /// brief-dfr27-5 R-dfr27-5-5 — Palace's peak memory on ONE process, as a share of its peak on
    /// <see cref="PalaceMeasuredProcesses"/>. Measured on the round-26 ten-layer board (218,437 order-1 unknowns, one
    /// excitation at 3.5 GHz, Palace's own Total HWM): 2.5, 3.0, 3.5 and 4.3 GB on 1, 2, 4 and 8 processes, and 4.5 GB on
    /// ten (six excitations). Every rank carries its own preconditioner and its share of the mesh's halo, so the peak
    /// climbs about evenly with each doubling of the count; a line in log2 of the count through 1 and 8 lies on or above
    /// every one-excitation point, and reads 4.49 GB for the ten-process run of six excitations.
    /// </summary>
    public const double PalaceOneProcessShare = 2.5 / 4.3;

    /// <summary>Palace's peak memory on <paramref name="processes"/> processes, as a share of its peak on
    /// <see cref="PalaceMeasuredProcesses"/> — the factor a driven estimate is scaled by for the run's own count.</summary>
    public static double PalaceProcessFactor(int processes)
        => PalaceOneProcessShare + (1 - PalaceOneProcessShare) * Math.Log2(Math.Max(1, processes)) / Math.Log2(PalaceMeasuredProcesses);

    /// <summary>Palace's peak memory for <paramref name="unknowns"/> at <paramref name="order"/>, with
    /// <see cref="PalaceRefinementMemoryFactor"/> when any refinement pass is allowed (measured on the iterative solver, and
    /// borrowed for the direct one: what it multiplies is mostly the error estimator, which the two share).</summary>
    public static long PalaceMemoryBytes(long unknowns, int order, int refinementPasses, bool direct = false)
        => (long)Math.Round(unknowns * (direct
                                ? order == 1 ? PalaceDirectBytesPerUnknownOrder1 : PalaceDirectBytesPerUnknownOrder2
                                : order == 1 ? PalaceBytesPerUnknownOrder1 : PalaceBytesPerUnknownOrder2)
                            * (refinementPasses > 0 ? PalaceRefinementMemoryFactor : 1));

    /// <summary>…for a field or, with <paramref name="isStatic"/>, a static potential — null for a static order-1 run,
    /// which nothing has measured. A static problem's figure is the iterative solver's whatever <paramref name="direct"/>
    /// says: nothing has measured the direct one there.</summary>
    public static long? PalaceMemoryBytes(long unknowns, int order, int refinementPasses, bool isStatic, bool direct = false)
        => !isStatic ? PalaceMemoryBytes(unknowns, order, refinementPasses, direct)
         : order == 1 ? null
         : (long)Math.Round(unknowns * PalaceBytesPerUnknownStaticOrder2 * (refinementPasses > 0 ? PalaceRefinementMemoryFactor : 1));

    /// <summary>openEMS's memory for a grid of <paramref name="cells"/> cells — brief 8 supplies the
    /// count.</summary>
    public static long OpenEmsMemoryBytes(long cells) => (long)Math.Round(cells * OpenEmsBytesPerCell);

    /// <summary>A primitive's volume, m³. A sweep's comes from its tessellation (divergence
    /// theorem over the closed mesh), so it is the volume of exactly what a backend is handed.</summary>
    public static double Volume(Em3dPrimitive p) => p switch
    {
        Em3dExtrudedPolygon e => (Math.Abs(Area(e.Outline)) - e.Holes.Sum(h => Math.Abs(Area(h)))) * (e.ZTop - e.ZBottom),
        Em3dBox b             => (b.Max.X - b.Min.X) * (b.Max.Y - b.Min.Y) * (b.Max.Z - b.Min.Z),
        Em3dCylinder c        => Math.PI * c.Radius * c.Radius * Distance(c.AxisStart, c.AxisEnd),
        Em3dSphere s          => 4.0 / 3.0 * Math.PI * s.Radius * s.Radius * s.Radius,
        Em3dTruncatedSphere t => Cap(t),
        Em3dEllipsoid e       => e.Volume,
        _                     => MeshVolume(Em3dTessellation.Of(new Em3dSolid("", "", Em3dRole.Conductor, p, 0))),
    };

    /// <summary>
    /// The tetrahedra of a refined shell over <paramref name="areaM2"/> of surface, one side, as the mesh script grades it (a
    /// Gmsh Threshold field): <paramref name="nearM"/> out to that distance, then growing linearly by
    /// <paramref name="grading"/> − 1 per unit distance until <paramref name="farM"/>. Integrating
    /// A·dd / (h(d)³/6√2) over the shell gives 6√2·A·[1/n² + (1/n² − 1/H²) / (2(g − 1))]: the term at the surface
    /// dominates, so the count goes as the area over the square of the edge element. Curvature (a convex surface's shell
    /// growing outward) is left out.
    /// </summary>
    public static double ShellTetrahedra(double areaM2, double nearM, double grading, double farM)
    {
        double n2 = 1 / (nearM * nearM), h2 = 1 / (farM * farM);
        return 6 * Math.Sqrt(2) * areaM2 * (n2 + Math.Max(n2 - h2, 0) / (2 * (grading - 1)));
    }

    private static double RegularTetVolume(double edgeM) => edgeM * edgeM * edgeM / (6 * Math.Sqrt(2));

    /// <summary>A primitive's surface area, m², from the tessellation a backend is handed.</summary>
    public static double SurfaceArea(Em3dPrimitive p)
    {
        var mesh = Em3dTessellation.Of(new Em3dSolid("", "", Em3dRole.Conductor, p, 0));
        double area = 0;
        foreach (var t in mesh.Triangles)
        {
            var a = mesh.Vertices[t.A]; var b = mesh.Vertices[t.B]; var c = mesh.Vertices[t.C];
            double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z, vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
            double cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
            area += 0.5 * Math.Sqrt(cx * cx + cy * cy + cz * cz);
        }
        return area;
    }

    private static double PortArea(Em3dPort p)
    {
        var d = new[] { p.Max.X - p.Min.X, p.Max.Y - p.Min.Y, p.Max.Z - p.Min.Z }.Where(v => v > 0).OrderDescending().ToArray();
        return d.Length >= 2 ? d[0] * d[1] : 0;
    }

    /// <summary>The volume a closed, consistently wound mesh encloses.</summary>
    public static double MeshVolume(Em3dTriangleMesh mesh)
    {
        double six = 0;
        foreach (var t in mesh.Triangles)
        {
            var a = mesh.Vertices[t.A]; var b = mesh.Vertices[t.B]; var c = mesh.Vertices[t.C];
            six += a.X * (b.Y * c.Z - b.Z * c.Y) - a.Y * (b.X * c.Z - b.Z * c.X) + a.Z * (b.X * c.Y - b.Y * c.X);
        }
        return Math.Abs(six) / 6;
    }

    private static double Cap(Em3dTruncatedSphere t)
    {
        double r = t.Radius;
        double a = Math.Max(t.ZMin - t.Center.Z, -r), b = Math.Min(t.ZMax - t.Center.Z, r);
        return b > a ? Math.PI * (r * r * (b - a) - (b * b * b - a * a * a) / 3) : 0;
    }

    private static double Area(IReadOnlyList<Point2> r)
    {
        double s = 0;
        for (int i = 0, j = r.Count - 1; i < r.Count; j = i++) s += r[j].X * r[i].Y - r[i].X * r[j].Y;
        return s / 2;
    }

    private static double Distance(Point3 a, Point3 b)
        => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
}
