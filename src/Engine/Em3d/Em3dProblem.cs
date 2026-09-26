// brief-em3d-3 R-em3d3-1 — the solver-neutral 3D problem (docs/design/em-3d.md §4.1).
//
// A SIBLING of Mom/EmProblem and Mom/PlanarProblem, never derived from either (overview §1a):
// EmProblem is a 2D cross-section with laterally infinite slabs, and nothing in it can grow a third
// axis. This type is what both 3D backends lower — Palace through a .geo script (brief 7), openEMS
// through CSXCAD XML (brief 9) — so everything a backend needs is RESOLVED here and nothing is
// inferred there. Two backends inferring separately would disagree on exactly the geometry a
// cross-check exists to test.
//
// R-mom-1 applies unchanged: metres, S/m, Hz, °C, doubles. No DBU, no LayerKey, no document type,
// no technology. The Tier A generator (src/Design/Layout/Em3d/Em3dGenerator.cs) produces it.
//
// Nothing here names a mesh entity (§6.4). Ports and boundaries attach to NAMED objects, and the
// mapping to solver entities is rebuilt on every run by the backend.

using System.Globalization;
using System.Numerics;

namespace CircuitRF.Engine.Em3d;

/// <summary>A point in the plane of a layout, metres.</summary>
public readonly record struct Point2(double X, double Y);

/// <summary>A point or a direction in 3D, metres. z is up.</summary>
public readonly record struct Point3(double X, double Y, double Z);

/// <summary>What a solid IS to a solver: a lossy dielectric volume, a conductor, or the air the
/// problem is immersed in.</summary>
public enum Em3dRole { Dielectric, Conductor, Air }

/// <summary>What one face of the air box does to the field.</summary>
public enum Em3dBoundaryKind { Absorbing, Pec, Pmc, Symmetry }

/// <summary>How the frequency points are spaced between start and stop.</summary>
public enum Em3dSweepKind { Linear, Log }

/// <summary>A bond wire's cross-section (brief 4).</summary>
public enum Em3dSection { Hexagon, Circle }

/// <summary>
/// brief-em3d-22 R-em3d22-1a — what a 3D problem asks: S over a sweep (<see cref="Driven"/>), or a
/// capacitance or inductance matrix over named terminals; brief-em3d-23 R-em3d23-4a — or the resonant
/// frequencies and Q of the structure (<see cref="Eigenmode"/>).
/// </summary>
public enum Em3dProblemType { Driven, Electrostatic, Magnetostatic, Eigenmode }

/// <summary>
/// brief-em3d-23 R-em3d23-2a — how a port is fed. <see cref="Lumped"/> is a sheet between two conductors
/// with a resistance across it; <see cref="Wave"/> is a region of an air-box face, excited and measured by
/// the line's own first 2D mode.
/// </summary>
public enum Em3dPortKind { Lumped, Wave }

/// <summary>A straight path between two points, metres.</summary>
public readonly record struct Em3dSegment(Point3 From, Point3 To);

// ── Construction primitives ──────────────────────────────────────────────────────────────────
//
// Tier A's vocabulary, which is almost exactly CSXCAD's primitive set (§6.3). There is deliberately
// NO boolean here: overlap is resolved by construction order (R-em3d3-1d), which both backends can
// state — CSXCAD as a priority, Palace by subtracting. Tier B (F4) adds booleans.

/// <summary>A construction primitive. Every coordinate is metres.</summary>
public abstract record Em3dPrimitive;

/// <summary>A polygon with holes, extruded between two heights — the layout's workhorse.
/// Rings are implicitly closed and carry no winding requirement.</summary>
public sealed record Em3dExtrudedPolygon(
    IReadOnlyList<Point2>                 Outline,
    IReadOnlyList<IReadOnlyList<Point2>>  Holes,
    double                                ZBottom,
    double                                ZTop) : Em3dPrimitive;

/// <summary>An axis-aligned box between two corners.</summary>
public sealed record Em3dBox(Point3 Min, Point3 Max) : Em3dPrimitive;

/// <summary>A right circular cylinder — a round via.</summary>
public sealed record Em3dCylinder(Point3 AxisStart, Point3 AxisEnd, double Radius) : Em3dPrimitive;

/// <summary>
/// A bond wire's section swept along its axis polyline (brief-em3d-4 R-em3d4-2), <b>resolved</b>:
/// <paramref name="Rings"/> holds the section at every vertex of <paramref name="Path"/> — mitred at an
/// interior vertex, square at the two ends — with vertex k of each ring joined to vertex k of the next
/// by the solid's lateral edges. The rings ARE the geometry, decided once by the generator (the roll,
/// the near-vertical rule, a foot's bottom lying exactly on its pad), so no backend re-derives a
/// frame. A backend that sweeps a true section along the path may use <paramref name="Section"/> and
/// <paramref name="Diameter"/> (the ROUND wire's d; <see cref="Em3dWireSection"/> sizes the hexagon
/// from it) and must then take the section's orientation from the rings.
/// </summary>
public sealed record Em3dSweep(
    IReadOnlyList<Point3>                Path,
    Em3dSection                          Section,
    double                               Diameter,
    IReadOnlyList<IReadOnlyList<Point3>> Rings) : Em3dPrimitive;

/// <summary>A sphere (brief 4's balls; bumps, later).</summary>
public sealed record Em3dSphere(Point3 Center, double Radius) : Em3dPrimitive;

/// <summary>A sphere kept only between two heights — a ball flattened on its pad.</summary>
public sealed record Em3dTruncatedSphere(Point3 Center, double Radius, double ZMin, double ZMax)
    : Em3dPrimitive;

// ── The problem's parts ──────────────────────────────────────────────────────────────────────

/// <summary>
/// One named solid. <paramref name="Order"/> is its construction order (R-em3d3-1d): where two
/// solids overlap, the higher order wins the volume. It is part of the problem so that no backend
/// ever infers it.
/// </summary>
public sealed record Em3dSolid(string Name, string Material, Em3dRole Role, Em3dPrimitive Primitive,
                               int Order);

/// <summary>
/// A conductor thin enough to be a surface (§6.1): a polygon at one height carrying its
/// conductivity (through <paramref name="Material"/>) and its real thickness. The decision is made
/// once, by the generator, so both backends inherit the same one.
/// </summary>
public sealed record Em3dSheet(
    string                                Name,
    string                                Material,
    IReadOnlyList<Point2>                 Outline,
    IReadOnlyList<IReadOnlyList<Point2>>  Holes,
    double                                Z,
    double                                ThicknessM,
    int                                   Order);

/// <summary>
/// A material, RESOLVED: the values in force at the problem's operating temperature, so a backend
/// never consults a technology. <paramref name="EpsrTensor"/>, when present, is xx/yy/zz and wins
/// over <paramref name="Epsr"/> for a solver that can take it.
/// </summary>
public sealed record Em3dMaterial(
    string                 Name,
    double                 Epsr,
    IReadOnlyList<double>? EpsrTensor,
    double                 TanD,
    double                 Mur,
    double                 SigmaSm);

/// <summary>
/// Where a port's S-parameters are referred to (§4.4). For a Tier A lumped port it is the port
/// sheet itself and <paramref name="ShiftM"/> is zero; the field exists from the start so a wave
/// port can move it later with no format change, and so brief 10 can say which plane two results
/// share.
/// </summary>
/// <param name="Origin">A point on the plane.</param>
/// <param name="Normal">Unit normal pointing INTO the structure.</param>
/// <param name="ShiftM">De-embedding length along <paramref name="Normal"/>, metres.</param>
public sealed record Em3dReferencePlane(Point3 Origin, Point3 Normal, double ShiftM);

/// <summary>
/// A lumped port (R-em3d3-2): an axis-aligned rectangular sheet from <paramref name="Min"/> to
/// <paramref name="Max"/> (exactly one axis has zero extent) between two NAMED objects — a solid, a
/// sheet, or an air-box face (<see cref="Em3dAirBox.FaceName"/>). <paramref name="Direction"/> is a
/// unit vector from the negative object toward the positive one, so the port voltage is positive
/// minus negative.
/// </summary>
public sealed record Em3dPort(
    int                Number,
    string             Name,
    string             PositiveObject,
    string             NegativeObject,
    Point3             Min,
    Point3             Max,
    Point3             Direction,
    Complex            Z0,
    Em3dReferencePlane ReferencePlane)
{
    /// <summary>
    /// brief-em3d-22 — a COAXIAL port: its sheet is this annulus, centred in the rectangle's plane,
    /// instead of the rectangle (which is then the annulus' bounding square). Null — every port the
    /// Tier A generator builds — is the rectangle. Only a z-normal annulus is supported.
    /// </summary>
    public Em3dAnnulus? Annulus { get; init; }

    /// <summary>
    /// brief-em3d-23 R-em3d23-2a — <see cref="Em3dPortKind.Wave"/>: the rectangle is a region of one
    /// air-box face (the port's face), and <see cref="Direction"/> is not read. The de-embedding
    /// distance is <see cref="Em3dReferencePlane.ShiftM"/>.
    /// </summary>
    public Em3dPortKind Kind { get; init; } = Em3dPortKind.Lumped;

    /// <summary>
    /// A wave port's voltage path, across its face from the negative object to the positive one — the
    /// sense <see cref="Direction"/> has for a lumped port, so the two kinds of port share one polarity.
    /// The mode's impedance (Palace's Z_PV) is measured along it. Null on a lumped port.
    /// </summary>
    public Em3dSegment? VoltagePath { get; init; }
}

/// <summary>
/// A coaxial port's sheet, between two radii. The current (or field) runs radially between the two
/// objects: <paramref name="Outward"/> when the negative object is the inner one, which is Palace's
/// <c>+R</c>.
/// </summary>
public sealed record Em3dAnnulus(double InnerRadiusM, double OuterRadiusM, bool Outward);

/// <summary>
/// brief-em3d-22 R-em3d22-2 — one terminal of a static solve: a name, and the conductors (solids or
/// sheets, BY NAME — never a face index) that are held at one potential or carry one current.
/// <paramref name="SourcePort"/> names the port whose sheet a magnetostatic solve drives its current
/// through; an electrostatic solve reads none.
/// </summary>
public sealed record Em3dTerminal(string Name, IReadOnlyList<string> Objects, string? SourcePort = null);

/// <summary>The six faces of the air box, each saying what it does to the field.</summary>
public sealed record Em3dFaces(
    Em3dBoundaryKind XMin, Em3dBoundaryKind XMax,
    Em3dBoundaryKind YMin, Em3dBoundaryKind YMax,
    Em3dBoundaryKind ZMin, Em3dBoundaryKind ZMax);

/// <summary>The air box: its extent and what each face is. Every solid lies inside it — the
/// generator clips nothing (R-em3d3-6b).</summary>
public sealed record Em3dAirBox(Point3 Min, Point3 Max, Em3dFaces Faces)
{
    /// <summary>
    /// The name a port uses to end on a face of the box — <c>airbox/zmin</c> and so on. A port may
    /// only end on a <see cref="Em3dBoundaryKind.Pec"/> face: that is the one kind that is a
    /// conductor.
    /// </summary>
    public static string FaceName(string face) => "airbox/" + face;

    /// <summary>The face a <see cref="FaceName"/> names, or null for a name that is not a face.</summary>
    public Em3dBoundaryKind? FaceKind(string name) => name switch
    {
        "airbox/xmin" => Faces.XMin, "airbox/xmax" => Faces.XMax,
        "airbox/ymin" => Faces.YMin, "airbox/ymax" => Faces.YMax,
        "airbox/zmin" => Faces.ZMin, "airbox/zmax" => Faces.ZMax,
        _ => null,
    };
}

/// <summary>The sweep, Hz.</summary>
public sealed record Em3dFrequency(double StartHz, double StopHz, int Points, Em3dSweepKind Kind);

/// <summary>
/// <b>The resolved 3D problem.</b> Immutable, SI, and complete: a backend reads nothing else.
/// Call <see cref="Validate"/> first.
/// </summary>
public sealed record Em3dProblem(
    IReadOnlyList<Em3dSolid>    Solids,
    IReadOnlyList<Em3dSheet>    Sheets,
    IReadOnlyList<Em3dMaterial> Materials,
    IReadOnlyList<Em3dPort>     Ports,
    Em3dAirBox                  Boundary,
    Em3dFrequency               Frequency,
    double                      OperatingTempC)
{
    /// <summary>brief-em3d-22 — what the problem asks. <see cref="Em3dProblemType.Driven"/> is what
    /// every problem before static solves existed asks.</summary>
    public Em3dProblemType Type { get; init; } = Em3dProblemType.Driven;

    /// <summary>A static problem's terminals, in index order (terminal k is index k + 1).</summary>
    public IReadOnlyList<Em3dTerminal> Terminals { get; init; } = [];

    /// <summary>The conductors (by name) that are the ground terminal — the matrix's reference. A PEC
    /// face of the air box is ground too, without being listed.</summary>
    public IReadOnlyList<string> GroundObjects { get; init; } = [];

    /// <summary>True for an electrostatic or magnetostatic problem.</summary>
    public bool IsStatic => Type is Em3dProblemType.Electrostatic or Em3dProblemType.Magnetostatic;

    /// <summary>brief-em3d-23 R-em3d23-4a — how many modes an eigenmode problem finds.</summary>
    public int EigenmodeCount { get; init; } = 1;

    /// <summary>brief-em3d-23 R-em3d23-4a — the frequency above which an eigenmode problem looks for
    /// modes, Hz.</summary>
    public double EigenmodeTargetHz { get; init; }

    /// <summary>True when any port is a wave port.</summary>
    public bool HasWavePorts => Ports.Any(p => p.Kind == Em3dPortKind.Wave);

    /// <summary>
    /// brief-em3d-23 — the air-box face a rectangle lies in (<c>xmin</c> … <c>zmax</c>), or null when it
    /// lies in none. A wave port must lie in one.
    /// </summary>
    public string? FaceOf(Point3 min, Point3 max)
    {
        var b = Boundary;
        double tol = 1e-9 * Math.Max(1.0, Math.Max(b.Max.X - b.Min.X, Math.Max(b.Max.Y - b.Min.Y, b.Max.Z - b.Min.Z)));
        bool On(double lo, double hi, double at) => Math.Abs(lo - at) <= tol && Math.Abs(hi - at) <= tol;
        if (On(min.X, max.X, b.Min.X)) return "xmin";
        if (On(min.X, max.X, b.Max.X)) return "xmax";
        if (On(min.Y, max.Y, b.Min.Y)) return "ymin";
        if (On(min.Y, max.Y, b.Max.Y)) return "ymax";
        if (On(min.Z, max.Z, b.Min.Z)) return "zmin";
        if (On(min.Z, max.Z, b.Max.Z)) return "zmax";
        return null;
    }

    /// <summary>
    /// R-em3d22-2b — the conductors (solids and sheets) in no terminal and not ground, in problem
    /// order. Floating is a choice the problem states, not a default: what it means is the solve's.
    /// </summary>
    public IReadOnlyList<string> FloatingConductors()
    {
        var held = new HashSet<string>(Terminals.SelectMany(t => t.Objects).Concat(GroundObjects), StringComparer.Ordinal);
        return [.. Solids.Where(s => s.Role == Em3dRole.Conductor).Select(s => s.Name)
                         .Concat(Sheets.Select(s => s.Name)).Where(n => !held.Contains(n))];
    }

    /// <summary>
    /// Every structural problem this problem has, as sentences naming the object — empty when it is
    /// sound. <b>All of them, not the first</b> (R-em3d3-1c): a backend calls this before lowering,
    /// and a list that stopped at the first would make a user fix defects one run at a time.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var materials = new HashSet<string>(StringComparer.Ordinal);

        foreach (var m in Materials)
            if (!materials.Add(m.Name))
                problems.Add($"Material '{m.Name}' is defined twice.");

        void Name(string kind, string name)
        {
            if (!names.Add(name))
                problems.Add($"The name '{name}' is used by more than one object (a {kind} among " +
                             "them). Solids, sheets and ports share one namespace, because a port " +
                             "or a boundary attaches to an object by name.");
        }

        void Material(string owner, string material)
        {
            if (!materials.Contains(material))
                problems.Add($"'{owner}' is made of '{material}', which this problem does not resolve " +
                             "to any values.");
        }

        var box = Boundary;
        bool Inside(double x0, double y0, double z0, double x1, double y1, double z1)
        {
            double tol = 1e-9 * Math.Max(1.0, Math.Max(box.Max.X - box.Min.X,
                                  Math.Max(box.Max.Y - box.Min.Y, box.Max.Z - box.Min.Z)));
            return x0 >= box.Min.X - tol && y0 >= box.Min.Y - tol && z0 >= box.Min.Z - tol &&
                   x1 <= box.Max.X + tol && y1 <= box.Max.Y + tol && z1 <= box.Max.Z + tol;
        }

        foreach (var s in Solids)
        {
            Name("solid", s.Name);
            Material(s.Name, s.Material);

            if (Degenerate(s.Primitive) is { } why)
                problems.Add($"Solid '{s.Name}' {why}. A conductor that thin belongs among the " +
                             "problem's sheets, and anything else that thin is not a solid.");
            else if (Bounds(s.Primitive) is var (x0, y0, z0, x1, y1, z1) &&
                     !Inside(x0, y0, z0, x1, y1, z1))
                problems.Add($"Solid '{s.Name}' extends outside the air box. Nothing is clipped to " +
                             "the box, so a solid crossing it is a problem the box has to be enlarged " +
                             "for, not one a backend may quietly cut.");
        }

        foreach (var sh in Sheets)
        {
            Name("sheet", sh.Name);
            Material(sh.Name, sh.Material);
            var (x0, y0, x1, y1) = RingBounds(sh.Outline);
            if (sh.Outline.Count < 3)
                problems.Add($"Sheet '{sh.Name}' has fewer than three vertices.");
            else if (!Inside(x0, y0, sh.Z, x1, y1, sh.Z))
                problems.Add($"Sheet '{sh.Name}' extends outside the air box.");
        }

        var objects = new HashSet<string>(Solids.Select(s => s.Name).Concat(Sheets.Select(s => s.Name)),
                                          StringComparer.Ordinal);
        foreach (var p in Ports)
        {
            Name("port", p.Name);
            foreach (string end in new[] { p.PositiveObject, p.NegativeObject })
            {
                if (objects.Contains(end)) continue;
                if (box.FaceKind(end) is { } face)
                {
                    if (face != Em3dBoundaryKind.Pec)
                        problems.Add($"Port {p.Number} ends on '{end}', which is a {face} face of the " +
                                     "air box. Only a PEC face is a conductor a port can end on.");
                    continue;
                }
                problems.Add($"Port {p.Number} ends on '{end}', which names no solid, sheet or air-box " +
                             "face in this problem.");
            }

            int zeroAxes = (p.Max.X == p.Min.X ? 1 : 0) + (p.Max.Y == p.Min.Y ? 1 : 0) +
                           (p.Max.Z == p.Min.Z ? 1 : 0);
            if (zeroAxes != 1)
                problems.Add($"Port {p.Number}'s sheet is not a rectangle: exactly one of its axes must " +
                             $"have zero extent, and {zeroAxes} do.");
            else if (!Inside(p.Min.X, p.Min.Y, p.Min.Z, p.Max.X, p.Max.Y, p.Max.Z))
                problems.Add($"Port {p.Number}'s sheet extends outside the air box.");
            else if (p.Annulus is { } a)
            {
                double side = p.Max.X - p.Min.X;
                if (p.Max.Z != p.Min.Z || Math.Abs((p.Max.Y - p.Min.Y) - side) > 1e-9 * Math.Max(side, 1e-12))
                    problems.Add($"Port {p.Number} is coaxial, and its rectangle is not a square in a plane of " +
                                 "constant z: an annulus is supported only facing z, bounded by its square.");
                else if (!(a.InnerRadiusM > 0) || !(a.OuterRadiusM > a.InnerRadiusM) ||
                         Math.Abs(2 * a.OuterRadiusM - side) > 1e-9 * side)
                    problems.Add($"Port {p.Number}'s annulus needs 0 < inner radius < outer radius, and an outer " +
                                 "diameter equal to its square's side.");
            }

            if (p.Kind != Em3dPortKind.Wave) continue;
            if (IsStatic)
                problems.Add($"Port {p.Number} is a wave port, and a static problem has no wave: its source is a sheet.");
            else if (FaceOf(p.Min, p.Max) is null)
                problems.Add($"Port {p.Number} is a wave port, and its rectangle does not lie in a face of the air box. " +
                             "A wave port is a region of the problem's outer boundary, never inside it.");
            else if (p.Annulus is not null)
                problems.Add($"Port {p.Number} is a wave port with an annulus; a wave port's region is its rectangle.");
            if (p.VoltagePath is not { } v)
                problems.Add($"Port {p.Number} is a wave port with no voltage path, so its mode's impedance cannot be " +
                             "measured and its S-parameters could not be stated against a reference impedance.");
            else if (!InRect(v.From) || !InRect(v.To) || v.From == v.To)
                problems.Add($"Port {p.Number}'s voltage path does not run across its own face between two distinct points.");

            bool InRect(Point3 q)
            {
                double tol = 1e-9 * Math.Max(1.0, Math.Max(p.Max.X - p.Min.X, Math.Max(p.Max.Y - p.Min.Y, p.Max.Z - p.Min.Z)));
                return q.X >= p.Min.X - tol && q.X <= p.Max.X + tol && q.Y >= p.Min.Y - tol && q.Y <= p.Max.Y + tol &&
                       q.Z >= p.Min.Z - tol && q.Z <= p.Max.Z + tol;
            }
        }

        if (IsStatic) ValidateTerminals(problems);
        else if (Type == Em3dProblemType.Eigenmode)
        {
            if (EigenmodeCount < 1)
                problems.Add($"The eigenmode problem asks for {EigenmodeCount} modes; it needs at least one.");
            if (!(EigenmodeTargetHz > 0) || double.IsInfinity(EigenmodeTargetHz))
                problems.Add("The eigenmode problem's target frequency is not a positive frequency: modes are found above it.");
        }
        else if (!(Frequency.StartHz > 0) || !(Frequency.StopHz >= Frequency.StartHz) || Frequency.Points < 1)
            problems.Add($"The sweep {Frequency.StartHz.ToString("R", CultureInfo.InvariantCulture)} to " +
                         $"{Frequency.StopHz.ToString("R", CultureInfo.InvariantCulture)} Hz at " +
                         $"{Frequency.Points} point(s) is not a sweep a 3D solver can run: it needs a " +
                         "positive start, a stop at or above it, and at least one point.");

        return problems;
    }

    /// <summary>
    /// R-em3d22-2 — a static problem's terminals: at least one; each named once and holding at least
    /// one conductor; no conductor in two of them or in a terminal and ground; and, for a magnetostatic
    /// solve, each driven through a port that touches it (R-em3d22-4a).
    /// </summary>
    private void ValidateTerminals(List<string> problems)
    {
        string kind = Type == Em3dProblemType.Electrostatic ? "electrostatic" : "magnetostatic";
        if (Terminals.Count == 0)
            problems.Add($"This {kind} problem names no terminal, so there is no matrix to compute. List the " +
                         "conductors' nets in the setup's Terminals3D.");

        var conductors = new HashSet<string>(Solids.Where(s => s.Role == Em3dRole.Conductor).Select(s => s.Name)
                                                   .Concat(Sheets.Select(s => s.Name)), StringComparer.Ordinal);
        var owner = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string g in GroundObjects)
        {
            if (!conductors.Contains(g))
                problems.Add($"The ground names '{g}', which is not a conductor or sheet of this problem.");
            owner[g] = "the ground";
        }

        var terminalNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in Terminals)
        {
            if (string.IsNullOrWhiteSpace(t.Name))
                problems.Add("A terminal has no name; a matrix row is labelled by its terminal's name.");
            else if (!terminalNames.Add(t.Name))
                problems.Add($"Terminal '{t.Name}' is listed twice.");
            if (t.Objects.Count == 0)
                problems.Add($"Terminal '{t.Name}' holds no conductor, so it has no surface to hold at a potential " +
                             "or to carry a current.");
            foreach (string o in t.Objects)
            {
                if (!conductors.Contains(o))
                    problems.Add($"Terminal '{t.Name}' names '{o}', which is not a conductor or sheet of this problem.");
                else if (owner.TryGetValue(o, out string? other))
                    problems.Add($"'{o}' is in terminal '{t.Name}' and in {(other == "the ground" ? other : $"terminal '{other}'")}; " +
                                 "a conductor is at one potential.");
                else owner[o] = t.Name;
            }

            if (Type != Em3dProblemType.Magnetostatic) continue;
            if (t.SourcePort is not { Length: > 0 } src)
            {
                problems.Add($"Magnetostatic terminal '{t.Name}' names no source: an inductance needs a path for its " +
                             "current, entering and returning. Name the port whose sheet drives it (Source).");
                continue;
            }
            var port = Ports.FirstOrDefault(p => p.Name == src);
            if (port is null)
                problems.Add($"Terminal '{t.Name}' is driven through '{src}', which is not a port of this problem.");
            else if (!t.Objects.Contains(port.PositiveObject) && !t.Objects.Contains(port.NegativeObject))
                problems.Add($"Terminal '{t.Name}' is driven through {src}, which runs from '{port.NegativeObject}' to " +
                             $"'{port.PositiveObject}' — neither is one of the terminal's conductors, so its current " +
                             "would not flow in them.");
        }
    }

    /// <summary>Why a primitive encloses no volume, or null when it does.</summary>
    private static string? Degenerate(Em3dPrimitive p) => p switch
    {
        Em3dExtrudedPolygon e when !(e.ZTop > e.ZBottom) => "has zero thickness",
        Em3dExtrudedPolygon e when e.Outline.Count < 3 => "has fewer than three outline vertices",
        Em3dBox b when !(b.Max.X > b.Min.X && b.Max.Y > b.Min.Y && b.Max.Z > b.Min.Z) =>
            "has zero extent on at least one axis",
        Em3dCylinder c when !(c.Radius > 0) => "has zero radius",
        Em3dCylinder c when c.AxisStart == c.AxisEnd => "has zero length",
        Em3dSweep w when !(w.Diameter > 0) || w.Path.Count < 2 => "has no section or no path",
        Em3dSweep w when w.Rings.Count != w.Path.Count || w.Rings.Any(r => r.Count < 3) =>
            "does not have one section ring of at least three vertices per path vertex",
        Em3dSphere s when !(s.Radius > 0) => "has zero radius",
        Em3dTruncatedSphere t when !(t.Radius > 0) || !(t.ZMax > t.ZMin) => "has zero volume",
        _ => null,
    };

    /// <summary>A conservative axis-aligned bound of a primitive, metres.</summary>
    public static (double X0, double Y0, double Z0, double X1, double Y1, double Z1) Bounds(Em3dPrimitive p)
    {
        switch (p)
        {
            case Em3dExtrudedPolygon e:
            {
                var (x0, y0, x1, y1) = RingBounds(e.Outline);
                return (x0, y0, e.ZBottom, x1, y1, e.ZTop);
            }
            case Em3dBox b:
                return (b.Min.X, b.Min.Y, b.Min.Z, b.Max.X, b.Max.Y, b.Max.Z);
            case Em3dCylinder c:
            {
                // The end discs' exact bound: a disc of radius r normal to unit axis a reaches
                // r·√(1 − aᵢ²) along axis i — r across a vertical via, 0 along it, and in between for a
                // tilted one (which the earlier "0 in z unless horizontal" missed).
                double r = c.Radius;
                double dx = c.AxisEnd.X - c.AxisStart.X, dy = c.AxisEnd.Y - c.AxisStart.Y, dz = c.AxisEnd.Z - c.AxisStart.Z;
                double sq = dx * dx + dy * dy + dz * dz;       // not √ then squared: a vertical axis must give 1 exactly
                double Reach(double ai) => sq > 0 ? r * Math.Sqrt(Math.Max(0, 1 - ai * ai / sq)) : r;
                double rx = Reach(dx), ry = Reach(dy), rz = Reach(dz);
                return (Math.Min(c.AxisStart.X, c.AxisEnd.X) - rx, Math.Min(c.AxisStart.Y, c.AxisEnd.Y) - ry,
                        Math.Min(c.AxisStart.Z, c.AxisEnd.Z) - rz,
                        Math.Max(c.AxisStart.X, c.AxisEnd.X) + rx, Math.Max(c.AxisStart.Y, c.AxisEnd.Y) + ry,
                        Math.Max(c.AxisStart.Z, c.AxisEnd.Z) + rz);
            }
            case Em3dSweep w:
            {
                // The rings are the solid's vertices, so their bound is exact.
                double x0 = double.PositiveInfinity, y0 = x0, z0 = x0;
                double x1 = double.NegativeInfinity, y1 = x1, z1 = x1;
                foreach (var ring in w.Rings)
                    foreach (var q in ring)
                    {
                        x0 = Math.Min(x0, q.X); y0 = Math.Min(y0, q.Y); z0 = Math.Min(z0, q.Z);
                        x1 = Math.Max(x1, q.X); y1 = Math.Max(y1, q.Y); z1 = Math.Max(z1, q.Z);
                    }
                return (x0, y0, z0, x1, y1, z1);
            }
            case Em3dSphere s:
                return (s.Center.X - s.Radius, s.Center.Y - s.Radius, s.Center.Z - s.Radius,
                        s.Center.X + s.Radius, s.Center.Y + s.Radius, s.Center.Z + s.Radius);
            case Em3dTruncatedSphere t:
                return (t.Center.X - t.Radius, t.Center.Y - t.Radius, Math.Max(t.ZMin, t.Center.Z - t.Radius),
                        t.Center.X + t.Radius, t.Center.Y + t.Radius, Math.Min(t.ZMax, t.Center.Z + t.Radius));
            default:
                throw new ArgumentOutOfRangeException(nameof(p), p.GetType().Name, "unknown primitive");
        }
    }

    private static (double X0, double Y0, double X1, double Y1) RingBounds(IReadOnlyList<Point2> ring)
    {
        double x0 = double.PositiveInfinity, y0 = x0, x1 = double.NegativeInfinity, y1 = x1;
        foreach (var q in ring)
        {
            x0 = Math.Min(x0, q.X); y0 = Math.Min(y0, q.Y);
            x1 = Math.Max(x1, q.X); y1 = Math.Max(y1, q.Y);
        }
        return (x0, y0, x1, y1);
    }
}
