// brief-em3d-116 — a wave port on openEMS, as a fed and probed transmission line (R-em3d116-1, -2).
//
// HOW IT WORKS is how openEMS's own authors build a line port, which brief 113-a reproduced and measured
// (src/Design/RESOLVED.md § "Terminal wave ports — brief-em3d-113-a"). Written from the physics and those
// measurements, never from openEMS's code (em-3d.md §5.2). Each TERMINAL (a one-terminal wave port is one) is
// fed from behind, on a uniform continuation of its line, by a soft E-field source shaped to the line; its
// voltage is measured on three planes around the reference plane and its current on the two half-cell planes
// between them. S then comes from all the runs at once, through FdtdPortTransform.Solve, unchanged. No mode is
// computed.
//
// TWO STAGES, because the feed must exist before the grid and the probes must sit on the grid:
//   Plan  (FdtdGrid.Build calls it) — per air-box face carrying a wave port: what each terminal is (coaxial,
//         stripline, microstrip, or a plain line), the feed length, and the uniform lattice of FIXED lines from
//         one cell outside the source to one cell past the reference plane. Those lines are what make the
//         extension exist: CsxcadWriter already carries every solid reaching an absorbing face out to the grid's
//         edge (its Context.Out), so a grid that reaches past the face extrudes the face's cross-section
//         through the feed and on through the PML.
//   Place (CsxcadWriter calls it) — every source and probe coordinate as an exact written grid line, or the
//         exact midpoint of two (113-a: an off-line zero-thickness source is dropped with only a warning, and a
//         current-loop edge on an unresolved tie snapped into a strip and read 55 % of the current).

using System.Globalization;

namespace CircuitRF.Engine.Em3d;

/// <summary>What a terminal is on its face — and so the shape of its source (R-em3d116-2a).</summary>
public enum FdtdTerminalShape
{
    /// <summary>A conductor its reference surrounds: a radial source ∝ 1/ρ over the annulus.</summary>
    Coaxial,
    /// <summary>A strip between two reference planes: two sheets, strip to each plane, pointing away from the strip.</summary>
    Stripline,
    /// <summary>A strip over one reference plane: one sheet, strip to the plane.</summary>
    Microstrip,
    /// <summary>Anything else: a source along the voltage path (113's line source, measured worse).</summary>
    Line,
}

/// <summary>
/// One terminal on its face, before the grid: its port, its shape, and the face-plane geometry the shape was read
/// from. <see cref="U"/> and <see cref="V"/> are the face's two in-plane world axes; <see cref="Foot"/> is the
/// conductor's section box in them. <see cref="PathAxis"/> is the in-plane axis the voltage path runs along (null
/// when it runs along neither); <see cref="RefSide"/> is the side of the conductor its reference end lies on, ±1.
/// <see cref="Below"/>/<see cref="Above"/> are the distances from the foot to the reference along −/+ the path axis
/// (a stripline has both, a microstrip the one on <see cref="RefSide"/>).
/// </summary>
public sealed record FdtdTerminal(
    Em3dPort                                        Port,
    FdtdTerminalShape                               Shape,
    double                                          SMaxM,
    int                                             U,
    int                                             V,
    (double U0, double V0, double U1, double V1)    Foot,
    int?                                            PathAxis,
    int                                             RefSide,
    double?                                         Below,
    double?                                         Above,
    double                                          InnerRadiusM,
    double                                          OuterRadiusM)
{
    /// <summary>The terminal's name in a sentence: its own label (<c>P1</c>), else its port's.</summary>
    public string Label => FdtdWavePorts.Label(Port);

    /// <summary>
    /// brief-em3d-125 — a stripline's media just past the strip on its − and + sides along the path axis (a material name,
    /// or null for the box's vacuum), set only when they differ: the strip lies on an interface, as a microstrip under a lid
    /// does. Null for a strip in one medium.
    /// </summary>
    public (string? Below, string? Above)? Interface { get; init; }

    /// <summary>
    /// brief-em3d-125 — the one voltage half the terminal's U is read from (<c>_up</c> or <c>_dn</c>: the half toward its
    /// reference), or null for the mean of both. Set exactly when <see cref="Interface"/> is: on an interface the mode is
    /// quasi-TEM and the two halves differ, and the voltage a terminal is defined by is strip → its stated reference.
    /// </summary>
    public string? VoltageHalf { get; init; }

    /// <summary>The foot's centre on <paramref name="axis"/> (one of <see cref="U"/>, <see cref="V"/>).</summary>
    public double Centre(int axis) => axis == U ? (Foot.U0 + Foot.U1) / 2 : (Foot.V0 + Foot.V1) / 2;
}

/// <summary>
/// One air-box face carrying wave ports: its feed. Positions along the face's axis are
/// <c>FaceAtM + Inward·k·CellM</c> for the lattice index k — the face at 0, the reference plane at
/// <see cref="ReferenceIndex"/>, the source at <see cref="SourceIndex"/> (negative: outside the box), the PML's
/// inner edge one cell outside it. Every lattice line from there to one cell past the reference is a fixed grid line.
/// </summary>
public sealed record FdtdFeed(
    FdtdAxis                      Axis,
    string                        Face,
    Em3dBoundaryKind              StatedKind,
    double                        FaceAtM,
    int                           Inward,
    double                        CellM,
    int                           ReferenceIndex,
    int                           SourceIndex,
    double                        SMaxM,
    string                        LengthSetBy,
    IReadOnlyList<FdtdTerminal>   Terminals,
    IReadOnlyList<string>         Ports)
{
    /// <summary>The coordinate of lattice index <paramref name="k"/>.</summary>
    public double At(int k) => k == 0 ? FaceAtM : FaceAtM + Inward * k * CellM;

    public double SourceAtM    => At(SourceIndex);
    public double ReferenceAtM => At(ReferenceIndex);
    /// <summary>The PML's inner edge: one cell outside the source (R-em3d116-1b).</summary>
    public double OuterAtM     => At(SourceIndex - 1);
    /// <summary>How far the grid grows outward past the face before its PML (R-em3d116-1a).</summary>
    public double ExtensionM   => (1 - SourceIndex) * CellM;
    /// <summary>The source-to-reference-plane distance (R-em3d116-1b).</summary>
    public double FeedM        => (ReferenceIndex - SourceIndex) * CellM;

    /// <summary>The lattice's fixed lines, outermost first.</summary>
    public IEnumerable<double> Lines()
    {
        for (int k = SourceIndex - 1; k <= ReferenceIndex + 1; k++) yield return At(k);
    }
}

/// <summary>The wave ports' feeds, the notes they carry, or why openEMS cannot build them.</summary>
public sealed record FdtdWavePortPlan(IReadOnlyList<FdtdFeed> Feeds, IReadOnlyList<string> Notes, string? Refusal)
{
    public static readonly FdtdWavePortPlan None = new([], [], null);

    /// <summary>The fixed lines the feeds put on <paramref name="axis"/>.</summary>
    public IReadOnlyList<FdtdRequiredLine> ExtraLines(FdtdAxis axis)
        => [.. Feeds.Where(f => f.Axis == axis).SelectMany(f => f.Lines().Select(at =>
               new FdtdRequiredLine(at, true, [new FdtdLineSource("feed/" + f.Face, FdtdLineKind.WavePortPlane, at)])))];
}

/// <summary>A source of one terminal: a box (zero thickness on the face's axis), its E direction, and its weight
/// expressions (null: flat). <see cref="Suffix"/> names it (<c>_up</c>, <c>_dn</c>, or empty). On a cylindrical grid
/// (brief-em3d-120) the box and the direction are in the grid's own (ρ, α, z).</summary>
public sealed record FdtdSourceBox(string Suffix, Point3 Min, Point3 Max, int[] Excite, string[]? Weight);

/// <summary>A voltage probe of one terminal on one plane: from the reference to the conductor, weight −1 (the lumped
/// port's sign), or the other way when the terminal is flipped. <see cref="Suffix"/>: empty, <c>_up</c> or <c>_dn</c>.</summary>
public sealed record FdtdVoltageProbe(string Suffix, Point3 From, Point3 To);

/// <summary>
/// One terminal's elements on the grid (R-em3d116-2): the planes, the sources, the voltage probes on each plane, the
/// current box (transverse corners on dual-grid lines) and the sign that makes I flow into the device, and the
/// clearance from its contour to the nearest other conductor. On a cylindrical grid (brief-em3d-120) every point is in the
/// grid's own (ρ, α, z) and the current box is (ρ0, α0, ρ1, α1): a full disc about the axis.
/// </summary>
public sealed record FdtdTerminalElements(
    FdtdTerminal                       Terminal,
    FdtdFeed                           Feed,
    double                             SourceAtM,
    double                             UaAtM,
    double                             UAtM,
    double                             UcAtM,
    double                             IaAtM,
    double                             IbAtM,
    IReadOnlyList<FdtdSourceBox>       Sources,
    IReadOnlyList<FdtdVoltageProbe>    Voltage,
    (double U0, double V0, double U1, double V1) CurrentBox,
    int                                CurrentWeight,
    double                             ClearanceM,
    double                             ClearanceCells,
    string?                            NearestConductor)
{
    public int Port => Terminal.Port.Number;
    /// <summary>The spacing of the three voltage planes.</summary>
    public double PlaneSpacingM => Math.Abs(UcAtM - UAtM);
}

public static class FdtdWavePorts
{
    /// <summary>R-em3d116-1b — the source-to-reference distance is at least this many s_max (113-a's smallest tested).</summary>
    public const double FeedSMax = 4.5;
    /// <summary>R-em3d116-1b — …and at least this many cells.</summary>
    public const int FeedCellsMin = 10;
    /// <summary>R-em3d116-1d — PEC side walls are noted when the empty section's first cutoff is below this × the top.</summary>
    public const double SideWallMargin = 1.5;
    /// <summary>A terminal surrounded on four sides at distances within this ratio is coaxial.</summary>
    private const double CoaxialRatio = 1.5;

    private static readonly string[] FaceKeys = ["xmin", "xmax", "ymin", "ymax", "zmin", "zmax"];

    /// <summary>A port's name in a sentence: its group's label, else its own document label, else <c>P{n}</c>.</summary>
    public static string Label(Em3dPort p) => p.SourceLabel ?? $"P{(p.SourceNumber ?? p.Number).ToString(CultureInfo.InvariantCulture)}";

    private static string PortLabel(Em3dPort p) => p.FaceGroupLabel ?? Label(p);

    /// <summary>R-em3d116-3 — the hollow waveguide's refusal: a wave port met by one conductor.</summary>
    public static string HollowRefusal(Em3dPort p)
        => $"Port {(p.FaceGroupLabel is { } g ? $"'{g}'" : (p.SourceNumber ?? p.Number).ToString(CultureInfo.InvariantCulture))} is a wave port met by one conductor (a hollow waveguide). openEMS needs a mode-matching port for " +
           "that, which circuitRF does not build yet; EMerge and Palace run it. Set the setup's Solver3D to EMerge or Palace, or " +
           "run it with `circuitrf em --solver emerge`.";

    /// <summary>
    /// R-em3d116-1 — the feeds of <paramref name="problem"/>'s wave ports: one per face carrying any, with its lattice and
    /// its terminals classified. <see cref="FdtdWavePortPlan.None"/> when there is no wave port; a refusal when openEMS
    /// cannot build one (a hollow waveguide, two reference planes on one face).
    /// </summary>
    public static FdtdWavePortPlan Plan(Em3dProblem problem, OpenEmsGridSettings settings, double minCellM = 0)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(settings);
        var wave = problem.Ports.Where(p => p.Kind == Em3dPortKind.Wave).OrderBy(p => p.Number).ToList();
        if (wave.Count == 0 || problem.IsStatic || problem.Type == Em3dProblemType.Eigenmode) return FdtdWavePortPlan.None;

        foreach (var p in wave)
            if (IsHollow(problem, p)) return new([], [], HollowRefusal(p));

        var box = problem.Boundary;
        double span = Math.Max(box.Max.X - box.Min.X, Math.Max(box.Max.Y - box.Min.Y, box.Max.Z - box.Min.Z));
        double tol = 1e-9 * Math.Max(span, 1e-6);
        var kinds = new[] { box.Faces.XMin, box.Faces.XMax, box.Faces.YMin, box.Faces.YMax, box.Faces.ZMin, box.Faces.ZMax };
        double minCell = minCellM;
        var feeds = new List<FdtdFeed>();
        var notes = new List<string>();

        foreach (var onFace in wave.GroupBy(p => problem.FaceOf(p.Min, p.Max)!).OrderBy(g => Array.IndexOf(FaceKeys, g.Key)))
        {
            string face = onFace.Key;
            int fi = Array.IndexOf(FaceKeys, face);
            int axis = fi / 2, inward = fi % 2 == 0 ? 1 : -1;
            double faceAt = Get(fi % 2 == 0 ? box.Min : box.Max, axis);
            int u = (axis + 1) % 3, v = (axis + 2) % 3;
            if (u > v) (u, v) = (v, u);
            // the face's cross-section, sliced a hair inside so an end cap lying in the face is not cut
            double sliceAt = faceAt + inward * 1e-6 * Math.Max(span, 1e-6);
            var sections = Sections(problem, axis, sliceAt, u, v);
            var ground = GroundSegments(problem, axis, u, v, kinds);

            var terminals = new List<FdtdTerminal>();
            foreach (var p in onFace)
                terminals.Add(OnInterface(problem, axis, sliceAt, Classify(p, sections, ground, u, v, tol), tol));
            double sMax = terminals.Max(t => t.SMaxM);

            // The lattice cell: no wider than the grid's own cell at the face (CellsPerWavelength in the densest medium
            // crossing it), and fine enough that the minimum feed is ten cells; never below MinCell, which would only
            // force cells the merge then warns about.
            double n = DensestIndex(problem, axis, faceAt, tol);
            double cap = FdtdGrid.C0 / (problem.Frequency.StopHz * n * settings.CellsPerWavelength);
            double h = Math.Max(Math.Min(cap, FeedSMax * sMax / FeedCellsMin), minCell);
            string setBy = h == cap ? $"the grid's cell at the face ({Fmt(cap)}, CellsPerWavelength {G(settings.CellsPerWavelength)})"
                         : h == minCell && minCell > 0 ? $"MinCell ({Fmt(minCell)})"
                         : $"a tenth of {G(FeedSMax)} × s_max";

            // Each reference plane on the lattice: the offsets of one face must agree, so one extension serves it.
            var offsets = onFace.Select(p => p.ReferencePlane.ShiftM).Distinct().ToList();
            if (offsets.Count > 1)
                return new([], [], $"The {face} face carries wave ports with different Offsets ({string.Join(", ", offsets.Select(o => Fmt(o)))}), " +
                                   "and openEMS feeds a face from one extension with one reference plane. Give the ports on that face one Offset.");
            double offset = Math.Max(0, offsets[0]);
            int refIndex = 0;
            if (offset > 0)
            {
                refIndex = (int)Math.Ceiling(offset / h - 1e-9);
                h = offset / refIndex;
            }
            int feedCells = Math.Max(FeedCellsMin, (int)Math.Ceiling(FeedSMax * sMax / h - 1e-9));
            int sourceIndex = Math.Min(refIndex - feedCells, -1);
            string length = Math.Max(FeedCellsMin, (int)Math.Ceiling(FeedSMax * sMax / h - 1e-9)) == FeedCellsMin && FeedCellsMin * h > FeedSMax * sMax
                ? $"{FeedCellsMin} cells of {Fmt(h)} (more than {G(FeedSMax)} × s_max, {Fmt(FeedSMax * sMax)})"
                : $"{G(FeedSMax)} × s_max ({Fmt(sMax)}, the largest distance on the face from a terminal to its reference), in cells of {Fmt(h)}";
            var feed = new FdtdFeed((FdtdAxis)axis, face, kinds[fi], faceAt, inward, h, refIndex, sourceIndex, sMax,
                                    $"{length}; the cell is {setBy}", terminals,
                                    [.. onFace.Select(PortLabel).Distinct()]);
            feeds.Add(feed);

            string ports = Ports(feed.Ports);
            notes.Add(kinds[fi] == Em3dBoundaryKind.Absorbing
                ? $"The {face} face carries {ports}, so openEMS feeds it from behind, through a {Fmt(feed.ExtensionM)} extension of the " +
                  "face's own cross-section ending in PML."
                : $"The {face} face carries {ports}, so openEMS terminates it in PML behind a {Fmt(feed.ExtensionM)} feed; the setup's " +
                  $"{(kinds[fi] == Em3dBoundaryKind.Pmc ? "PMC" : kinds[fi] == Em3dBoundaryKind.Pec ? "PEC" : kinds[fi].ToString())} applies to Palace only.");
            if (terminals.Any(t => t.Shape == FdtdTerminalShape.Line))
                notes.Add($"{string.Join(", ", terminals.Where(t => t.Shape == FdtdTerminalShape.Line).Select(t => $"Terminal {t.Label}"))} on the {face} " +
                          "face is neither coaxial nor a strip over or between reference planes, so openEMS feeds it along its voltage path. " +
                          "That source launches more evanescent field than a shaped one; the feed may need to be longer.");
            if (terminals.Where(t => t.Interface is not null).ToList() is { Count: > 0 } onIf)
                notes.Add($"{(onIf.Count == 1 ? "Terminal" : "Terminals")} {string.Join(", ", onIf.Select(t => t.Label))} on the {face} face " +
                          $"{(onIf.Count == 1 ? "is a strip" : "are strips")} between two reference planes on an interface " +
                          $"({string.Join("; ", onIf.Select(t => t.Interface!.Value).Distinct().Select(m => $"{Medium(m.Below)} and {Medium(m.Above)}"))}), " +
                          "so each voltage is the half from the strip to its reference, not the mean of both: on an interface the mode is " +
                          "quasi-TEM and the two halves differ.");
            if (SideWallNote(problem, feed, kinds, axis, u, v, tol) is { } side) notes.Add(side);
        }
        return new(feeds, notes, null);
    }

    /// <summary>
    /// R-em3d116-2 — every element of every terminal, on <paramref name="grid"/> (built from the same plan): or the
    /// refusal of a current box that cannot be placed in dielectric with a cell to spare (R-em3d116-2c).
    /// </summary>
    public static (IReadOnlyList<FdtdTerminalElements> Terminals, string? Refusal) Place(Em3dProblem problem, FdtdWavePortPlan plan,
                                                                                        FdtdGridResult grid)
    {
        if (grid.Cylinder is { } cylinder) return PlaceCylindrical(plan, grid, cylinder);
        var all = new List<FdtdTerminalElements>();
        var box = problem.Boundary;
        double span = Math.Max(box.Max.X - box.Min.X, Math.Max(box.Max.Y - box.Min.Y, box.Max.Z - box.Min.Z));
        double tol = 1e-9 * Math.Max(span, 1e-6);
        var kinds = new[] { box.Faces.XMin, box.Faces.XMax, box.Faces.YMin, box.Faces.YMax, box.Faces.ZMin, box.Faces.ZMax };
        foreach (var feed in plan.Feeds)
        {
            int axis = (int)feed.Axis;
            var lines = grid.Axis(feed.Axis).Lines;
            double Line(int k) => Snap(lines, feed.At(k));
            double source = Line(feed.SourceIndex), ua = Line(feed.ReferenceIndex - 1), uMid = Line(feed.ReferenceIndex),
                   uc = Line(feed.ReferenceIndex + 1);
            double ia = (ua + uMid) / 2, ib = (uMid + uc) / 2;
            int u = feed.Terminals[0].U, v = feed.Terminals[0].V;
            double sliceAt = feed.FaceAtM + feed.Inward * 1e-6 * Math.Max(span, 1e-6);
            var sections = Sections(problem, axis, sliceAt, u, v);
            var ground = GroundSegments(problem, axis, u, v, kinds);
            var lu = grid.Axis((FdtdAxis)u).Lines;
            var lv = grid.Axis((FdtdAxis)v).Lines;

            foreach (var t in feed.Terminals)
            {
                var p = t.Port;
                var sources = new List<FdtdSourceBox>();
                var probes = new List<FdtdVoltageProbe>();
                Point3 At3(double a, double cu, double cv) => Make(axis, a, u, cu, v, cv);

                // The voltage path: the probe's, snapped to the lines. Unflipped it runs from the reference to the conductor.
                var vp = p.VoltagePath!.Value;
                double fu = Snap(lu, Get(vp.From, u)), fv = Snap(lv, Get(vp.From, v)), tu = Snap(lu, Get(vp.To, u)), tv = Snap(lv, Get(vp.To, v));
                bool flipped = ReferenceEndIsTo(t, vp);

                switch (t.Shape)
                {
                    case FdtdTerminalShape.Stripline:
                    {
                        int pa = t.PathAxis!.Value, across = pa == u ? v : u;
                        var la = pa == u ? lu : lv;
                        var lx = pa == u ? lv : lu;
                        double lo = pa == u ? t.Foot.U0 : t.Foot.V0, hi = pa == u ? t.Foot.U1 : t.Foot.V1;
                        double c = Snap(lx, t.Centre(across));
                        double lowPlane = Snap(la, lo - t.Below!.Value), highPlane = Snap(la, hi + t.Above!.Value);
                        double sLo = Snap(la, lo), sHi = Snap(la, hi);
                        var (w0, w1) = Across(lx, across == u ? t.Foot.U0 : t.Foot.V0, across == u ? t.Foot.U1 : t.Foot.V1);
                        // _up: the strip's + side along the path axis; _dn: its − side. Fields point away from the strip.
                        foreach (var (suffix, plane, face, dir) in new[] { ("_up", highPlane, sHi, +1), ("_dn", lowPlane, sLo, -1) })
                        {
                            var e = new int[3];
                            e[pa] = flipped ? -dir : dir;
                            sources.Add(new FdtdSourceBox(suffix, Pt(axis, source, pa, face, across, w0), Pt(axis, source, pa, plane, across, w1), e, null));
                            var (from, to) = flipped ? (face, plane) : (plane, face);
                            probes.Add(new FdtdVoltageProbe(suffix, Pt(axis, 0, pa, from, across, c), Pt(axis, 0, pa, to, across, c)));
                        }
                        break;
                    }
                    case FdtdTerminalShape.Microstrip:
                    {
                        int pa = t.PathAxis!.Value, across = pa == u ? v : u;
                        var la = pa == u ? lu : lv;
                        var lx = pa == u ? lv : lu;
                        double lo = pa == u ? t.Foot.U0 : t.Foot.V0, hi = pa == u ? t.Foot.U1 : t.Foot.V1;
                        double face = Snap(la, t.RefSide > 0 ? hi : lo);
                        double plane = Snap(la, t.RefSide > 0 ? hi + t.Above!.Value : lo - t.Below!.Value);
                        var (w0, w1) = Across(lx, across == u ? t.Foot.U0 : t.Foot.V0, across == u ? t.Foot.U1 : t.Foot.V1);
                        var e = new int[3];
                        e[pa] = (flipped ? -1 : 1) * t.RefSide;
                        sources.Add(new FdtdSourceBox("", Pt(axis, source, pa, face, across, w0), Pt(axis, source, pa, plane, across, w1), e, null));
                        probes.Add(new FdtdVoltageProbe("", At3(0, fu, fv), At3(0, tu, tv)));
                        break;
                    }
                    case FdtdTerminalShape.Coaxial:
                    {
                        double cu = t.Centre(u), cv = t.Centre(v), ri = t.InnerRadiusM, ro = t.OuterRadiusM;
                        double u0 = Below(lu, cu - ro), u1 = Above(lu, cu + ro), v0 = Below(lv, cv - ro), v1 = Above(lv, cv + ro);
                        // E outward for a positive voltage (the path runs shield → pin), inward when flipped.
                        // The denominator never reaches 0: on the axis (often a grid line) 0/0 would be evaluated, and the
                        // mask is 0 there anyway.
                        var weight = RadialWeights(axis, u, v, cu, cv, ri, ro, inward: flipped);
                        var e = new int[3];
                        e[u] = 1; e[v] = 1;
                        sources.Add(new FdtdSourceBox("", At3(source, u0, v0), At3(source, u1, v1), e, weight));
                        probes.Add(new FdtdVoltageProbe("", At3(0, fu, fv), At3(0, tu, tv)));
                        break;
                    }
                    default:
                    {
                        // A line source along the path: E from the conductor's end toward the reference's (the lumped port's).
                        var d = new[] { 0.0, 0.0, 0.0 };
                        d[u] = tu - fu; d[v] = tv - fv;
                        int pa = Math.Abs(d[u]) >= Math.Abs(d[v]) ? u : v;
                        var e = new int[3];
                        e[pa] = d[pa] > 0 ? -1 : 1;
                        sources.Add(new FdtdSourceBox("", At3(source, Math.Min(fu, tu), Math.Min(fv, tv)), At3(source, Math.Max(fu, tu), Math.Max(fv, tv)), e, null));
                        probes.Add(new FdtdVoltageProbe("", At3(0, fu, fv), At3(0, tu, tv)));
                        break;
                    }
                }

                // The current box: the conductor's foot grown by at least one cell, corners on dual lines.
                double bu0 = DualOut(lu, t.Foot.U0, -1), bu1 = DualOut(lu, t.Foot.U1, +1);
                double bv0 = DualOut(lv, t.Foot.V0, -1), bv1 = DualOut(lv, t.Foot.V1, +1);
                var (clear, cells, nearest) = Clearance(sections, ground, p.PositiveObject, (bu0, bv0, bu1, bv1), lu, lv);
                if (cells < 1)
                {
                    double need = CellAround(lu, lv, (bu0 + bu1) / 2, (bv0 + bv1) / 2, bu0, bv0, bu1, bv1);
                    return ([], $"Terminal {t.Label}'s current probe on the {feed.Face} face would {(clear <= 0 ? "touch" : "come within " + FmtCells(cells) + " of")} " +
                                $"'{nearest}': its loop around '{p.PositiveObject}' must lie in dielectric with one cell to spare from any other " +
                                $"conductor, corners included, or the port reads a fraction of the current with no warning (113-a measured up to an " +
                                $"open circuit). It needs a clearance of one cell, {Fmt(need)} here, and has {Fmt(Math.Max(0, clear))}. Refine the " +
                                "openEMS grid there (CellsPerWavelength, MinCellUm), or move the conductors apart.");
                }
                // I into the device: the current along the inward direction; flipped with the voltage.
                int weight1 = feed.Inward * (flipped ? -1 : 1);
                // probes are planar paths: give them their planes
                var placed = new List<FdtdVoltageProbe>();
                foreach (double at in new[] { ua, uMid, uc })
                    foreach (var q in probes)
                        placed.Add(q with { From = With(q.From, axis, at), To = With(q.To, axis, at) });
                all.Add(new FdtdTerminalElements(t, feed, source, ua, uMid, uc, ia, ib, sources, placed, (bu0, bv0, bu1, bv1), weight1,
                                                 clear, cells, nearest));
            }
        }
        return (all, null);
    }

    /// <summary>
    /// brief-em3d-120 R-em3d120-4c/d — a coaxial terminal on a CYLINDRICAL grid (the grid refused every other kind), every
    /// coordinate in the grid's own (ρ, α, z): the source a Box spanning the dielectric annulus on the source plane, its weight
    /// E_ρ = r_i/ρ in the grid's own radial component (the same field as the Cartesian source, one component); each voltage
    /// line along ρ at α = 0, between the written radii of the path's two ends; each current probe a full disc about the axis
    /// whose rim is the DUAL line nearest midway between pin and shield, so it lies wholly in the dielectric and on no tie.
    /// </summary>
    private static (IReadOnlyList<FdtdTerminalElements> Terminals, string? Refusal) PlaceCylindrical(FdtdWavePortPlan plan, FdtdGridResult grid,
                                                                                                     FdtdCylinder cyl)
    {
        var all = new List<FdtdTerminalElements>();
        var rho = grid.X.Lines;
        var zl = grid.Z.Lines;
        double a0 = grid.Y.Lines[0], a1 = grid.Y.Lines[^1];
        foreach (var feed in plan.Feeds)
        {
            double Line(int k) => Snap(zl, feed.At(k));
            double source = Line(feed.SourceIndex), ua = Line(feed.ReferenceIndex - 1), uMid = Line(feed.ReferenceIndex),
                   uc = Line(feed.ReferenceIndex + 1);
            double ia = (ua + uMid) / 2, ib = (uMid + uc) / 2;
            foreach (var t in feed.Terminals)
            {
                var p = t.Port;
                var vp = p.VoltagePath!.Value;
                bool flipped = ReferenceEndIsTo(t, vp);
                double ri = Snap(rho, t.InnerRadiusM), ro = Snap(rho, t.OuterRadiusM);
                string s = flipped ? "-" : "";
                var sources = new List<FdtdSourceBox>
                {
                    new("", new Point3(ri, a0, source), new Point3(ro, a1, source), [1, 0, 0], [$"{s}{Ex(ri)}/rho", "0", "0"]),
                };
                double rFrom = Snap(rho, cyl.RadiusOf(vp.From)), rTo = Snap(rho, cyl.RadiusOf(vp.To));
                var placed = new List<FdtdVoltageProbe>();
                foreach (double at in new[] { ua, uMid, uc })
                    placed.Add(new FdtdVoltageProbe("", new Point3(rFrom, a0, at), new Point3(rTo, a0, at)));

                // The disc's rim: the dual line nearest the annulus' middle, a cell clear of the shield and outside the pin.
                double rim = double.NaN, best = double.PositiveInfinity;
                for (int i = 0; i + 1 < rho.Count; i++)
                {
                    double d = (rho[i] + rho[i + 1]) / 2;
                    if (d <= ri || d >= ro) continue;
                    if (Math.Abs(d - (ri + ro) / 2) < best) { best = Math.Abs(d - (ri + ro) / 2); rim = d; }
                }
                double cell = double.IsNaN(rim) ? ro - ri : CellAt(rho, rim);
                double clear = double.IsNaN(rim) ? 0 : ro - rim, cells = clear / cell;
                if (double.IsNaN(rim) || cells < 1 || (rim - ri) / cell < 1)
                    return ([], $"Terminal {t.Label}'s current probe on the {feed.Face} face has no room: its disc about '{p.PositiveObject}' must lie " +
                                $"in dielectric with one cell to spare from '{p.NegativeObject}' and from the conductor itself, and the annulus from " +
                                $"{Fmt(ri)} to {Fmt(ro)} holds {Math.Max(0, (int)Math.Round((ro - ri) / cell))} radial cell(s). Refine the openEMS grid " +
                                "there (CellsPerWavelength, MinCellUm).");
                int weight1 = feed.Inward * (flipped ? -1 : 1);
                all.Add(new FdtdTerminalElements(t, feed, source, ua, uMid, uc, ia, ib, sources, placed, (0, a0, rim, a1), weight1,
                                                 clear, cells, p.NegativeObject));
            }
        }
        return (all, null);
    }

    /// <summary>
    /// brief-em3d-120 R-em3d120-2 C1 / R-em3d120-5 — the one line every wave-port terminal of <paramref name="plan"/> is coaxial
    /// about (a world axis and a point on it, the axial coordinate 0), which a cylindrical grid could be built on; or null, with
    /// the reason there is none in <paramref name="whyNot"/> (null when the problem has no coaxial terminal at all).
    /// </summary>
    public static (FdtdAxis Axis, Point3 Origin)? CommonCoaxialAxis(Em3dProblem problem, FdtdWavePortPlan plan, out string? whyNot)
    {
        whyNot = null;
        var terminals = plan.Feeds.SelectMany(f => f.Terminals.Select(t => (Feed: f, Terminal: t))).ToList();
        if (!terminals.Any(x => x.Terminal.Shape == FdtdTerminalShape.Coaxial)) return null;
        if (problem.Ports.FirstOrDefault(p => p.Kind == Em3dPortKind.Lumped) is { } lumped)
        {
            whyNot = $"port {lumped.SourceNumber ?? lumped.Number} is a lumped port";
            return null;
        }
        if (terminals.FirstOrDefault(x => x.Terminal.Shape != FdtdTerminalShape.Coaxial) is { Terminal: { } flat })
        {
            whyNot = $"terminal {flat.Label} is not coaxial, and openEMS has one grid per run";
            return null;
        }
        if (plan.Feeds.Select(f => f.Axis).Distinct().Count() > 1)
        {
            whyNot = "the coaxial terminals sit on faces normal to different axes";
            return null;
        }
        var first = terminals[0].Terminal;
        double cu = first.Centre(first.U), cv = first.Centre(first.V);
        foreach (var (_, t) in terminals)
            if (Math.Abs(t.Centre(t.U) - cu) > 1e-3 * t.InnerRadiusM || Math.Abs(t.Centre(t.V) - cv) > 1e-3 * t.InnerRadiusM)
            {
                whyNot = $"terminals {first.Label} and {t.Label} are coaxial about different lines";
                return null;
            }
        var o = new double[3];
        o[first.U] = cu;
        o[first.V] = cv;
        return (plan.Feeds[0].Axis, new Point3(o[0], o[1], o[2]));
    }

    /// <summary>The problem with every face carrying a feed lowered absorbing (overview D9): what the grid and the writer use.</summary>
    public static Em3dProblem WithFeedFacesAbsorbing(Em3dProblem problem, FdtdWavePortPlan plan)
    {
        var f = problem.Boundary.Faces;
        foreach (var feed in plan.Feeds)
            f = feed.Face switch
            {
                "xmin" => f with { XMin = Em3dBoundaryKind.Absorbing }, "xmax" => f with { XMax = Em3dBoundaryKind.Absorbing },
                "ymin" => f with { YMin = Em3dBoundaryKind.Absorbing }, "ymax" => f with { YMax = Em3dBoundaryKind.Absorbing },
                "zmin" => f with { ZMin = Em3dBoundaryKind.Absorbing }, _ => f with { ZMax = Em3dBoundaryKind.Absorbing },
            };
        return problem with { Boundary = problem.Boundary with { Faces = f } };
    }

    // ── the terminal's shape ──────────────────────────────────────────────────────────────────

    private static FdtdTerminal Classify(Em3dPort p, Dictionary<string, List<(Point2 A, Point2 B)>> sections,
                                         List<(Point2 A, Point2 B)> ground, int u, int v, double tol)
    {
        double ru0 = Get(p.Min, u), ru1 = Get(p.Max, u), rv0 = Get(p.Min, v), rv1 = Get(p.Max, v);
        var foot = Foot(sections.GetValueOrDefault(p.PositiveObject) ?? [], ru0, rv0, ru1, rv1, tol);
        var vp = p.VoltagePath!.Value;
        double pathLen = Math.Sqrt(Sq(Get(vp.To, u) - Get(vp.From, u)) + Sq(Get(vp.To, v) - Get(vp.From, v)));
        if (foot is not { } f)
            return new FdtdTerminal(p, FdtdTerminalShape.Line, pathLen, u, v, (Get(vp.To, u), Get(vp.To, v), Get(vp.To, u), Get(vp.To, v)),
                                    null, 1, null, null, 0, 0);

        // the reference = the port's negative object, and every PEC air-box face (openEMS's boundary is one ground)
        var refSegs = new List<(Point2 A, Point2 B)>(ground);
        if (sections.TryGetValue(p.NegativeObject, out var rs)) refSegs.AddRange(rs);
        var others = sections.Where(kv => kv.Key != p.PositiveObject && kv.Key != p.NegativeObject).SelectMany(kv => kv.Value).ToList();
        double cu = (f.U0 + f.U1) / 2, cv = (f.V0 + f.V1) / 2;
        double? Hit(int dir, bool alongU)
        {
            double start = alongU ? (dir > 0 ? f.U1 : f.U0) : (dir > 0 ? f.V1 : f.V0);
            double limit = alongU ? (dir > 0 ? ru1 : ru0) : (dir > 0 ? rv1 : rv0);
            double? toRef = Ray(refSegs, cu, cv, alongU, dir, start, limit, tol);
            double? toOther = Ray(others, cu, cv, alongU, dir, start, limit, tol);
            return toRef is { } r && (toOther is not { } o || r <= o) ? r : null;
        }
        double? uMinus = Hit(-1, true), uPlus = Hit(+1, true), vMinus = Hit(-1, false), vPlus = Hit(+1, false);

        double du = Get(vp.To, u) - Get(vp.From, u), dv = Get(vp.To, v) - Get(vp.From, v);
        int? pathAxis = Math.Abs(du) > tol && Math.Abs(dv) <= tol ? u : Math.Abs(dv) > tol && Math.Abs(du) <= tol ? v : null;
        // the reference end: the path end farther from the foot's centre
        var refEnd = ReferenceEndIsTo(f, vp, u, v) ? vp.To : vp.From;
        int refSide = pathAxis is int pa0 ? Math.Sign(Get(refEnd, pa0) - (pa0 == u ? cu : cv)) : 1;
        if (refSide == 0) refSide = 1;

        double w = f.U1 - f.U0, hgt = f.V1 - f.V0;
        if (uMinus is { } a && uPlus is { } b && vMinus is { } c && vPlus is { } d)
        {
            double lo = Math.Min(Math.Min(a, b), Math.Min(c, d)), hi = Math.Max(Math.Max(a, b), Math.Max(c, d));
            if (lo > 0 && hi <= CoaxialRatio * lo && Math.Max(w, hgt) <= CoaxialRatio * Math.Min(w, hgt))
            {
                double ri = (w + hgt) / 4, ro = ri + (a + b + c + d) / 4;
                return new FdtdTerminal(p, FdtdTerminalShape.Coaxial, ro - ri, u, v, f, pathAxis, refSide, null, null, ri, ro);
            }
        }
        if (pathAxis is int pa)
        {
            double? below = pa == u ? uMinus : vMinus, above = pa == u ? uPlus : vPlus;
            if (below is { } bl && above is { } ab)
                return new FdtdTerminal(p, FdtdTerminalShape.Stripline, Math.Max(bl, ab), u, v, f, pa, refSide, bl, ab, 0, 0);
            double? onRef = refSide > 0 ? above : below;
            if (onRef is { } r)
                return new FdtdTerminal(p, FdtdTerminalShape.Microstrip, r, u, v, f, pa, refSide, below, above, 0, 0);
        }
        return new FdtdTerminal(p, FdtdTerminalShape.Line, pathLen, u, v, f, pathAxis, refSide, null, null, 0, 0);
    }

    /// <summary>
    /// brief-em3d-125 — a stripline whose strip lies on an interface: the medium just past the strip on each side along the
    /// path axis, at the strip's centre across, read from the dielectrics' sections by the face plane (the higher
    /// construction order winning, as it wins the volume). Different media make the mode quasi-TEM, so the strip → lid half
    /// no longer equals the strip → reference half, and the terminal reads the latter (measured against Palace,
    /// src/Design/RESOLVED.md § brief-em3d-125). In one medium both halves agree and the mean stays, which also keeps brief
    /// 116's cancellation of the parallel-plate mode a stripline with open sides supports. The source is unchanged.
    /// </summary>
    private static FdtdTerminal OnInterface(Em3dProblem problem, int axis, double at, FdtdTerminal t, double tol)
    {
        if (t.Shape != FdtdTerminalShape.Stripline || t.PathAxis is not int pa) return t;
        int across = pa == t.U ? t.V : t.U;
        double lo = pa == t.U ? t.Foot.U0 : t.Foot.V0, hi = pa == t.U ? t.Foot.U1 : t.Foot.V1, c = t.Centre(across);
        double d = Math.Max(1e-3 * Math.Min(t.Below!.Value, t.Above!.Value), 10 * tol);
        Point2 At(double along) => pa == t.U ? new(along, c) : new(c, along);
        string? below = MediumAt(problem, axis, at, t.U, t.V, At(lo - d)), above = MediumAt(problem, axis, at, t.U, t.V, At(hi + d));
        if (SameMedium(problem, below, above)) return t;
        return t with { Interface = (below, above), VoltageHalf = t.RefSide > 0 ? "_up" : "_dn" };
    }

    /// <summary>The material at <paramref name="p"/> on the face plane: the highest-order non-conductor solid containing it,
    /// else the box's fill (null: vacuum).</summary>
    private static string? MediumAt(Em3dProblem problem, int axis, double at, int u, int v, Point2 p)
    {
        foreach (var s in problem.Solids.Where(s => s.Role != Em3dRole.Conductor).OrderByDescending(s => s.Order))
        {
            var b = Em3dProblem.Bounds(s.Primitive);
            double lo = axis == 0 ? b.X0 : axis == 1 ? b.Y0 : b.Z0, hi = axis == 0 ? b.X1 : axis == 1 ? b.Y1 : b.Z1;
            if (lo > at || hi < at) continue;
            if (Inside(Slice(Em3dTessellation.Of(s), axis, at, u, v), p)) return s.Material;
        }
        return problem.Boundary.Material;
    }

    private static bool SameMedium(Em3dProblem problem, string? a, string? b)
    {
        if (a == b) return true;
        (double Er, double Mur, IReadOnlyList<double>? Tensor) Of(string? name)
            => name is not null && problem.Materials.FirstOrDefault(m => m.Name == name) is { } m ? (m.Epsr, m.Mur, m.EpsrTensor) : (1, 1, null);
        var (x, y) = (Of(a), Of(b));
        return x.Er == y.Er && x.Mur == y.Mur && x.Tensor is not { Count: > 0 } && y.Tensor is not { Count: > 0 };
    }

    private static string Medium(string? name) => name is null ? "vacuum" : $"'{name}'";

    /// <summary>Whether the path's reference end is its <c>To</c> (the terminal is flipped) — the end farther from the foot.</summary>
    private static bool ReferenceEndIsTo((double U0, double V0, double U1, double V1) f, Em3dSegment vp, int u, int v)
    {
        double cu = (f.U0 + f.U1) / 2, cv = (f.V0 + f.V1) / 2;
        double dFrom = Outside(f, Get(vp.From, u), Get(vp.From, v)), dTo = Outside(f, Get(vp.To, u), Get(vp.To, v));
        if (dFrom != dTo) return dTo > dFrom;
        return Sq(Get(vp.To, u) - cu) + Sq(Get(vp.To, v) - cv) > Sq(Get(vp.From, u) - cu) + Sq(Get(vp.From, v) - cv);
    }

    private static bool ReferenceEndIsTo(FdtdTerminal t, Em3dSegment vp) => ReferenceEndIsTo(t.Foot, vp, t.U, t.V);

    /// <summary>How far a point lies outside a box (0 inside or on it).</summary>
    private static double Outside((double U0, double V0, double U1, double V1) f, double pu, double pv)
        => Math.Sqrt(Sq(Math.Max(0, Math.Max(f.U0 - pu, pu - f.U1))) + Sq(Math.Max(0, Math.Max(f.V0 - pv, pv - f.V1))));

    /// <summary>A wave port met by one conductor: both its objects one (or both PEC faces of the box, which are one ground).
    /// Public for brief-emw-2: EMerge's lowering refuses a hollow waveguide by this same test.</summary>
    public static bool IsHollow(Em3dProblem problem, Em3dPort p)
        => p.Hollow || p.PositiveObject == p.NegativeObject ||
           (problem.Boundary.FaceKind(p.PositiveObject) is not null && problem.Boundary.FaceKind(p.NegativeObject) is not null);

    /// <summary>
    /// brief-emw-2 R-emw2-1 — the materials that meet a wave port's region: the region sampled on a 33 × 33 grid of points in
    /// the face's cross-section (sliced a hair inside, as <see cref="Plan"/> slices it), each point given to the solid that
    /// owns it by circuitRF's precedence (later wins a tie, as the solvers' priorities have it) or to the air box's fill when
    /// no solid does. A point a conductor owns is not meshed and names no material. Empty for a port on no face.
    /// </summary>
    public static IReadOnlyList<string> RegionMaterials(Em3dProblem problem, Em3dPort p, string fill)
    {
        ArgumentNullException.ThrowIfNull(problem);
        if (problem.FaceOf(p.Min, p.Max) is not { } face) return [];
        var box = problem.Boundary;
        double span = Math.Max(box.Max.X - box.Min.X, Math.Max(box.Max.Y - box.Min.Y, box.Max.Z - box.Min.Z));
        int fi = Array.IndexOf(FaceKeys, face);
        int axis = fi / 2, inward = fi % 2 == 0 ? 1 : -1;
        double sliceAt = Get(fi % 2 == 0 ? box.Min : box.Max, axis) + inward * 1e-6 * Math.Max(span, 1e-6);
        int u = (axis + 1) % 3, v = (axis + 2) % 3;
        if (u > v) (u, v) = (v, u);

        var precedence = Em3dPrecedence.Of(problem);
        var crossing = new List<(Em3dSolid Solid, int Rank, int Index, List<(Point2 A, Point2 B)> Section)>();
        for (int i = 0; i < problem.Solids.Count; i++)
        {
            var s = problem.Solids[i];
            var b = Em3dProblem.Bounds(s.Primitive);
            double lo = axis == 0 ? b.X0 : axis == 1 ? b.Y0 : b.Z0, hi = axis == 0 ? b.X1 : axis == 1 ? b.Y1 : b.Z1;
            if (lo > sliceAt || hi < sliceAt) continue;
            var segs = Slice(Em3dTessellation.Of(s), axis, sliceAt, u, v);
            if (segs.Count > 0) crossing.Add((s, precedence.Of(s), i, segs));
        }
        var ranked = crossing.OrderByDescending(c => c.Rank).ThenByDescending(c => c.Index).ToList();

        const int n = 33;
        double u0 = Get(p.Min, u), u1 = Get(p.Max, u), v0 = Get(p.Min, v), v1 = Get(p.Max, v);
        var found = new SortedSet<string>(StringComparer.Ordinal);
        for (int a = 0; a < n; a++)
            for (int c = 0; c < n; c++)
            {
                var q = new Point2(u0 + (u1 - u0) * (a + 0.5) / n, v0 + (v1 - v0) * (c + 0.5) / n);
                var owner = ranked.FirstOrDefault(r => Inside(r.Section, q)).Solid;
                if (owner is null) found.Add(fill);
                else if (owner.Role != Em3dRole.Conductor) found.Add(owner.Material);
            }
        return [.. found];
    }

    // ── the side walls (R-em3d116-1d) ────────────────────────────────────────────────────────

    private static string? SideWallNote(Em3dProblem problem, FdtdFeed feed, Em3dBoundaryKind[] kinds, int axis, int u, int v, double tol)
    {
        // A PEC face a strip's voltage path runs to is that strip's reference plane, not a wall beside it.
        var grounds = feed.Terminals.Where(t => t.Shape is FdtdTerminalShape.Stripline or FdtdTerminalShape.Microstrip && t.PathAxis is not null)
                                    .Select(t => t.PathAxis!.Value).ToHashSet();
        var pec = new List<string>();
        foreach (int a in new[] { u, v })
            foreach (int side in new[] { 0, 1 })
                if (kinds[2 * a + side] == Em3dBoundaryKind.Pec && !grounds.Contains(a)) pec.Add(FaceKeys[2 * a + side]);
        if (pec.Count == 0) return null;
        var box = problem.Boundary;
        double w = Math.Max(Get(box.Max, u) - Get(box.Min, u), Get(box.Max, v) - Get(box.Min, v));
        double eps = Math.Max(1, DensestIndex(problem, axis, feed.FaceAtM, tol));
        double fc = FdtdGrid.C0 / (2 * w * eps);       // eps here is √(εr μr): the densest index
        double top = problem.Frequency.StopHz;
        if (fc >= SideWallMargin * top) return null;
        return $"The faces beside {Ports(feed.Ports)} ({string.Join(", ", pec)}) are PEC, and the box they make has its first mode near " +
               $"{G(fc / 1e9)} GHz (c / (2·w·√εr) on the face's larger side, {Fmt(w)}), below {G(SideWallMargin)} × the sweep's top " +
               $"({G(top / 1e9)} GHz). The feed's source can excite that mode, which carries voltage but no strip current. Make those " +
               "faces PMC or Absorbing.";
    }

    private static double DensestIndex(Em3dProblem problem, int axis, double at, double tol)
    {
        var materials = problem.Materials.ToDictionary(m => m.Name, StringComparer.Ordinal);
        double n = 1;
        foreach (var s in problem.Solids)
        {
            if (s.Role == Em3dRole.Conductor || !materials.TryGetValue(s.Material, out var m)) continue;
            var b = Em3dProblem.Bounds(s.Primitive);
            double lo = axis == 0 ? b.X0 : axis == 1 ? b.Y0 : b.Z0, hi = axis == 0 ? b.X1 : axis == 1 ? b.Y1 : b.Z1;
            if (lo <= at + tol && hi >= at - tol)
                n = Math.Max(n, Math.Sqrt(Math.Max(m.Epsr, m.EpsrTensor is { Count: > 0 } t ? t.Max() : 0) * Math.Max(m.Mur, 1e-300)));
        }
        if (problem.Boundary.Material is { } fill && materials.TryGetValue(fill, out var air))
            n = Math.Max(n, Math.Sqrt(Math.Max(air.Epsr, 1e-300) * Math.Max(air.Mur, 1e-300)));
        return n;
    }

    // ── the face's cross-section ─────────────────────────────────────────────────────────────

    /// <summary>Every conductor's section by the plane at <paramref name="at"/> on <paramref name="axis"/>, as (u, v) segments.</summary>
    internal static Dictionary<string, List<(Point2 A, Point2 B)>> Sections(Em3dProblem problem, int axis, double at, int u, int v)
    {
        var map = new Dictionary<string, List<(Point2, Point2)>>(StringComparer.Ordinal);
        foreach (var s in problem.Solids.Where(s => s.Role == Em3dRole.Conductor))
        {
            var b = Em3dProblem.Bounds(s.Primitive);
            double lo = axis == 0 ? b.X0 : axis == 1 ? b.Y0 : b.Z0, hi = axis == 0 ? b.X1 : axis == 1 ? b.Y1 : b.Z1;
            if (lo > at || hi < at) continue;
            var segs = Slice(Em3dTessellation.Of(s), axis, at, u, v);
            if (segs.Count > 0) map[s.Name] = segs;
        }
        foreach (var sh in problem.Sheets)
        {
            var segs = Slice(Em3dTessellation.OfSheet(sh), axis, at, u, v);
            if (segs.Count > 0) map[sh.Name] = segs;
        }
        return map;
    }

    /// <summary>The PEC faces of the box, as segments in the face plane: the reference everywhere in openEMS.</summary>
    private static List<(Point2 A, Point2 B)> GroundSegments(Em3dProblem problem, int axis, int u, int v, Em3dBoundaryKind[] kinds)
    {
        var b = problem.Boundary;
        double u0 = Get(b.Min, u), u1 = Get(b.Max, u), v0 = Get(b.Min, v), v1 = Get(b.Max, v);
        var list = new List<(Point2, Point2)>();
        if (kinds[2 * u] == Em3dBoundaryKind.Pec)     list.Add((new(u0, v0), new(u0, v1)));
        if (kinds[2 * u + 1] == Em3dBoundaryKind.Pec) list.Add((new(u1, v0), new(u1, v1)));
        if (kinds[2 * v] == Em3dBoundaryKind.Pec)     list.Add((new(u0, v0), new(u1, v0)));
        if (kinds[2 * v + 1] == Em3dBoundaryKind.Pec) list.Add((new(u0, v1), new(u1, v1)));
        return list;
    }

    private static List<(Point2 A, Point2 B)> Slice(Em3dTriangleMesh m, int axis, double at, int u, int v)
    {
        var segs = new List<(Point2, Point2)>();
        Point2 UV(Point3 q) => new(Get(q, u), Get(q, v));
        foreach (var t in m.Triangles)
        {
            Point3 a = m.Vertices[t.A], b = m.Vertices[t.B], c = m.Vertices[t.C];
            var tri = new[] { a, b, c };
            var on = new List<Point3>(2);
            for (int i = 0; i < 3; i++)
            {
                var p = tri[i];
                var q = tri[(i + 1) % 3];
                double dp = Get(p, axis) - at, dq = Get(q, axis) - at;
                if ((dp < 0 && dq > 0) || (dp > 0 && dq < 0))
                {
                    double f = dp / (dp - dq);
                    on.Add(new Point3(p.X + (q.X - p.X) * f, p.Y + (q.Y - p.Y) * f, p.Z + (q.Z - p.Z) * f));
                }
                else if (dp == 0 && dq != 0) on.Add(p);
            }
            if (on.Count == 2) segs.Add((UV(on[0]), UV(on[1])));
        }
        return segs;
    }

    private static (double U0, double V0, double U1, double V1)? Foot(List<(Point2 A, Point2 B)> segs, double ru0, double rv0,
                                                                        double ru1, double rv1, double tol)
    {
        double u0 = double.PositiveInfinity, v0 = u0, u1 = double.NegativeInfinity, v1 = u1;
        foreach (var (a, b) in segs)
            foreach (var q in new[] { a, b })
            {
                if (q.X < ru0 - tol || q.X > ru1 + tol || q.Y < rv0 - tol || q.Y > rv1 + tol) continue;
                u0 = Math.Min(u0, q.X); u1 = Math.Max(u1, q.X); v0 = Math.Min(v0, q.Y); v1 = Math.Max(v1, q.Y);
            }
        return double.IsInfinity(u0) ? null : (u0, v0, u1, v1);
    }

    /// <summary>The distance from <paramref name="start"/> along a ray from the centre to the first segment it meets,
    /// within <paramref name="limit"/>; null when it meets none.</summary>
    private static double? Ray(List<(Point2 A, Point2 B)> segs, double cu, double cv, bool alongU, int dir, double start,
                               double limit, double tol)
    {
        double? best = null;
        foreach (var (a, b) in segs)
        {
            // crossing of the segment with the line through the centre along the ray's axis
            double ax = alongU ? a.Y : a.X, bx = alongU ? b.Y : b.X;     // coordinate across the ray
            double ay = alongU ? a.X : a.Y, by = alongU ? b.X : b.Y;     // coordinate along it
            double across = alongU ? cv : cu;
            double hit;
            if (Math.Abs(ax - bx) <= tol)
            {
                if (Math.Abs(ax - across) > tol) continue;
                hit = dir > 0 ? Math.Min(ay, by) : Math.Max(ay, by);
            }
            else
            {
                if (across < Math.Min(ax, bx) - tol || across > Math.Max(ax, bx) + tol) continue;
                hit = ay + (Math.Clamp(across, Math.Min(ax, bx), Math.Max(ax, bx)) - ax) / (bx - ax) * (by - ay);
            }
            double d = dir * (hit - start);
            if (d < -tol || dir * (hit - limit) > tol) continue;
            d = Math.Max(0, d);
            if (best is not { } bb || d < bb) best = d;
        }
        return best;
    }

    // ── the current box's clearance (R-em3d116-2c) ───────────────────────────────────────────

    /// <summary>
    /// The smallest distance from the box's contour to any conductor but <paramref name="self"/>, in metres and in cells of
    /// the grid where it is smallest, and that conductor's name. A conductor the contour crosses, or one inside the box,
    /// is at distance 0; a contour inside a conductor's metal is too (the even–odd count of its section).
    /// </summary>
    private static (double M, double Cells, string? Name) Clearance(Dictionary<string, List<(Point2 A, Point2 B)>> sections,
        List<(Point2 A, Point2 B)> ground, string self, (double U0, double V0, double U1, double V1) b,
        IReadOnlyList<double> lu, IReadOnlyList<double> lv)
    {
        double best = double.PositiveInfinity, bestCells = double.PositiveInfinity;
        string? name = null;
        var corners = new[] { new Point2(b.U0, b.V0), new Point2(b.U1, b.V0), new Point2(b.U1, b.V1), new Point2(b.U0, b.V1) };
        var named = sections.Where(kv => kv.Key != self).Select(kv => (kv.Key, kv.Value)).ToList();
        if (ground.Count > 0) named.Add(("the air box's PEC face", ground));
        foreach (var (n, segs) in named)
        {
            double d = double.PositiveInfinity;
            Point2 at = corners[0];
            foreach (var (a, c) in segs)
            {
                // inside the box?
                if (InBox(a, b) || InBox(c, b)) { d = 0; at = a; break; }
                for (int i = 0; i < 4; i++)
                {
                    var (dd, q) = SegmentDistance(corners[i], corners[(i + 1) % 4], a, c);
                    if (dd < d) { d = dd; at = q; }
                }
            }
            // the contour inside this conductor's metal (a section is closed curves: count crossings to the right)
            if (d > 0 && n != "the air box's PEC face" && Inside(segs, corners[0])) { d = 0; at = corners[0]; }
            if (double.IsInfinity(d)) continue;
            double cell = Math.Max(CellAt(lu, at.X), CellAt(lv, at.Y));
            double cells = cell > 0 ? d / cell : double.PositiveInfinity;
            if (cells < bestCells) { bestCells = cells; best = d; name = n; }
        }
        return (best, bestCells, name);
    }

    private static bool InBox(Point2 q, (double U0, double V0, double U1, double V1) b)
        => q.X > b.U0 && q.X < b.U1 && q.Y > b.V0 && q.Y < b.V1;

    private static bool Inside(List<(Point2 A, Point2 B)> segs, Point2 p)
    {
        int crossings = 0;
        foreach (var (a, b) in segs)
        {
            if ((a.Y > p.Y) == (b.Y > p.Y)) continue;
            double x = a.X + (p.Y - a.Y) * (b.X - a.X) / (b.Y - a.Y);
            if (x > p.X) crossings++;
        }
        return crossings % 2 == 1;
    }

    private static (double D, Point2 At) SegmentDistance(Point2 p0, Point2 p1, Point2 q0, Point2 q1)
    {
        if (Intersect(p0, p1, q0, q1)) return (0, q0);
        var best = (D: double.PositiveInfinity, At: q0);
        foreach (var (pt, a, b) in new[] { (p0, q0, q1), (p1, q0, q1), (q0, p0, p1), (q1, p0, p1) })
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
            double s = l2 > 0 ? Math.Clamp(((pt.X - a.X) * dx + (pt.Y - a.Y) * dy) / l2, 0, 1) : 0;
            var foot = new Point2(a.X + s * dx, a.Y + s * dy);
            double d = Math.Sqrt(Sq(pt.X - foot.X) + Sq(pt.Y - foot.Y));
            if (d < best.D) best = (d, foot);
        }
        return best;
    }

    private static bool Intersect(Point2 a, Point2 b, Point2 c, Point2 d)
    {
        static double Cross(Point2 o, Point2 p, Point2 q) => (p.X - o.X) * (q.Y - o.Y) - (p.Y - o.Y) * (q.X - o.X);
        double d1 = Cross(c, d, a), d2 = Cross(c, d, b), d3 = Cross(a, b, c), d4 = Cross(a, b, d);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    private static double CellAround(IReadOnlyList<double> lu, IReadOnlyList<double> lv, double cu, double cv, double u0, double v0, double u1, double v1)
        => Math.Max(Math.Max(CellAt(lu, u0), CellAt(lu, u1)), Math.Max(CellAt(lv, v0), CellAt(lv, v1)));

    // ── the grid ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The written line nearest <paramref name="x"/>.</summary>
    public static double Snap(IReadOnlyList<double> lines, double x)
    {
        int i = LowerBound(lines, x);
        if (i <= 0) return lines[0];
        if (i >= lines.Count) return lines[^1];
        return x - lines[i - 1] <= lines[i] - x ? lines[i - 1] : lines[i];
    }

    /// <summary>A source sheet's extent across its strip: the written lines on or inside the strip's width, or the nearest
    /// lines when no line lies inside it.</summary>
    private static (double Lo, double Hi) Across(IReadOnlyList<double> lines, double lo, double hi)
    {
        double a = Above(lines, lo), b = Below(lines, hi);
        return a < b ? (a, b) : (Snap(lines, lo), Snap(lines, hi));
    }

    /// <summary>The largest written line at or below <paramref name="x"/> (the first when none is).</summary>
    private static double Below(IReadOnlyList<double> lines, double x)
    {
        int i = LowerBound(lines, x);
        if (i < lines.Count && lines[i] == x) return x;
        return lines[Math.Max(0, i - 1)];
    }

    /// <summary>The smallest written line at or above <paramref name="x"/> (the last when none is).</summary>
    private static double Above(IReadOnlyList<double> lines, double x)
    {
        int i = LowerBound(lines, x);
        return lines[Math.Min(lines.Count - 1, i)];
    }

    /// <summary>
    /// The first dual line (midpoint of two written lines) on <paramref name="dir"/>'s side of the metal edge
    /// <paramref name="edge"/> at least one cell (that dual cell's own size) away from it.
    /// </summary>
    private static double DualOut(IReadOnlyList<double> lines, double edge, int dir)
    {
        if (dir < 0)
        {
            int k = LowerBound(lines, edge + 1e-12 * Math.Max(1, Math.Abs(edge)));
            for (int i = Math.Min(k, lines.Count - 1); i >= 1; i--)
            {
                double mid = (lines[i - 1] + lines[i]) / 2;
                if (edge - mid >= lines[i] - lines[i - 1]) return mid;
            }
            return (lines[0] + lines[Math.Min(1, lines.Count - 1)]) / 2;
        }
        int j = LowerBound(lines, edge - 1e-12 * Math.Max(1, Math.Abs(edge)));
        for (int i = Math.Max(j, 1); i < lines.Count; i++)
        {
            double mid = (lines[i - 1] + lines[i]) / 2;
            if (mid - edge >= lines[i] - lines[i - 1]) return mid;
        }
        return (lines[^2] + lines[^1]) / 2;
    }

    /// <summary>The size of the cell holding <paramref name="x"/>.</summary>
    private static double CellAt(IReadOnlyList<double> lines, double x)
    {
        int i = LowerBound(lines, x);
        if (i <= 0) return lines.Count > 1 ? lines[1] - lines[0] : 0;
        if (i >= lines.Count) return lines[^1] - lines[^2];
        return lines[i] - lines[i - 1];
    }

    private static int LowerBound(IReadOnlyList<double> a, double x)
    {
        int lo = 0, hi = a.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (a[mid] < x) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    private static string Ports(IReadOnlyList<string> labels)
        => labels.Count == 1 ? $"wave port '{labels[0]}'" : $"wave ports {string.Join(", ", labels.Select(l => $"'{l}'"))}";

    internal static double Get(Point3 q, int axis) => axis switch { 0 => q.X, 1 => q.Y, _ => q.Z };

    private static Point3 With(Point3 q, int axis, double x) => axis switch
    {
        0 => q with { X = x }, 1 => q with { Y = x }, _ => q with { Z = x },
    };

    private static Point3 Make(int a, double av, int b, double bv, int c, double cv)
    {
        var x = new double[3];
        x[a] = av; x[b] = bv; x[c] = cv;
        return new Point3(x[0], x[1], x[2]);
    }

    private static Point3 Pt(int a, double av, int b, double bv, int c, double cv) => Make(a, av, b, bv, c, cv);

    private static string AxisVar(int axis) => axis switch { 0 => "x", 1 => "y", _ => "z" };

    /// <summary>
    /// A coax's TEM source as openEMS weight functions about the centre (<paramref name="cu"/>, <paramref name="cv"/>) on axes
    /// <paramref name="u"/> and <paramref name="v"/> of a plane facing <paramref name="axis"/>: r_i·r̂/r between the radii, 0 outside them and along the axis; outward, or inward when
    /// <paramref name="inward"/>. The denominator never reaches 0: on the axis (often a grid line) 0/0 would be evaluated, and the
    /// mask is 0 there anyway. The coax wave port's source (brief-em3d-116), and brief-cxp-4's coaxial lumped port's.
    /// </summary>
    public static string[] RadialWeights(int axis, int u, int v, double cu, double cv, double ri, double ro, bool inward)
    {
        string s = inward ? "-" : "";
        string du = Shift(AxisVar(u), cu), dv = Shift(AxisVar(v), cv);
        string r2 = $"({du}^2+{dv}^2)";
        string mask = $"(sqrt{r2}>={Ex(ri)})*(sqrt{r2}<={Ex(ro)})";
        string den = $"max({r2},{Ex(ri * ri)})";
        var weight = new string[3];
        weight[axis] = "0";
        weight[u] = $"{s}{Ex(ri)}*{du}/{den}*{mask}";
        weight[v] = $"{s}{Ex(ri)}*{dv}/{den}*{mask}";
        return weight;
    }

    /// <summary>brief-cxp-4 — <see cref="Below"/>, <see cref="Above"/>, <see cref="DualOut"/> and <see cref="CellAt"/> for the ring port.</summary>
    internal static double BelowLine(IReadOnlyList<double> lines, double x) => Below(lines, x);
    internal static double AboveLine(IReadOnlyList<double> lines, double x) => Above(lines, x);
    internal static double DualOutLine(IReadOnlyList<double> lines, double edge, int dir) => DualOut(lines, edge, dir);
    internal static double CellAtLine(IReadOnlyList<double> lines, double x) => CellAt(lines, x);

    private static string Shift(string var, double c) => c >= 0 ? $"({var}-{Ex(c)})" : $"({var}+{Ex(-c)})";

    /// <summary>A number in a weight expression: round-trip, with a lowercase exponent (fparser's own spelling).</summary>
    private static string Ex(double v) => R(v).Replace('E', 'e');

    private static double Sq(double x) => x * x;

    internal static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    private static string G(double v) => v.ToString("G4", CultureInfo.InvariantCulture);
    private static string Fmt(double m) => FdtdGrid.FormatLength(m);
    private static string FmtCells(double c) => $"{c.ToString("0.##", CultureInfo.InvariantCulture)} cell{(c == 1 ? "" : "s")}";
}
