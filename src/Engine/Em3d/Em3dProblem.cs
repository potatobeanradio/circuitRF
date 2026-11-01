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
/// frequencies and Q of the structure (<see cref="Eigenmode"/>). brief-em3d-73 R-em3d73-5a — or the temperature
/// (<see cref="Thermal"/>), which circuitRF's own thermal solver answers and no EM solver ever sees: every lowering
/// refuses a problem of that type with <see cref="Em3dProblem.ThermalIsNotEm"/>.
/// </summary>
public enum Em3dProblemType { Driven, Electrostatic, Magnetostatic, Eigenmode, Thermal }

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
// NO boolean here: overlap is resolved by Em3dPrecedence — metal over dielectric, then construction order
// (R-em3d3-1d; em-3d.md §6.3a) — which both backends can state — CSXCAD as a priority, Palace by subtracting. Tier B (F4) adds booleans.

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

/// <summary>
/// brief-sar-3 R-sar3-1 — an ellipsoid: its centre and its semi-axes <paramref name="A"/>, <paramref name="B"/> and
/// <paramref name="C"/>, metres, along its own axes <paramref name="U"/>, <paramref name="V"/> and <see cref="W"/> = U × V (unit
/// vectors; the world's x and y for one that was never rotated). A point p is inside when Σ ((p − c)·axis / semi-axis)² ≤ 1.
/// </summary>
public sealed record Em3dEllipsoid(Point3 Center, double A, double B, double C, Point3 U, Point3 V) : Em3dPrimitive
{
    /// <summary>An ellipsoid whose axes are the world's.</summary>
    public static Em3dEllipsoid Aligned(Point3 center, double a, double b, double c)
        => new(center, a, b, c, new Point3(1, 0, 0), new Point3(0, 1, 0));

    /// <summary>The third axis, U × V.</summary>
    public Point3 W => new(U.Y * V.Z - U.Z * V.Y, U.Z * V.X - U.X * V.Z, U.X * V.Y - U.Y * V.X);

    /// <summary>4/3·π·A·B·C, m³.</summary>
    public double Volume => 4.0 / 3.0 * Math.PI * A * B * C;

    /// <summary>
    /// The semi-axis along world x, y and z when each of the ellipsoid's own axes lies along a world axis (to 1e-12) — any
    /// placement made of quarter turns — or null when one does not. Such an ellipsoid is exactly a unit sphere scaled per
    /// world axis, which is what openEMS's <c>Scale3</c> transform states.
    /// </summary>
    public (double X, double Y, double Z)? AlignedSemiAxes()
    {
        double[] s = new double[3];
        bool[] taken = new bool[3];
        foreach (var (axis, semi) in new[] { (U, A), (V, B), (W, C) })
        {
            double[] a = [Math.Abs(axis.X), Math.Abs(axis.Y), Math.Abs(axis.Z)];
            int k = a[0] > 1 - 1e-12 ? 0 : a[1] > 1 - 1e-12 ? 1 : a[2] > 1 - 1e-12 ? 2 : -1;
            if (k < 0 || taken[k]) return null;
            taken[k] = true;
            s[k] = semi;
        }
        return (s[0], s[1], s[2]);
    }

    /// <summary>The point of the ellipsoid's surface that the unit sphere's point (x, y, z) maps to.</summary>
    public Point3 Map(double x, double y, double z)
    {
        var w = W;
        return new Point3(Center.X + A * x * U.X + B * y * V.X + C * z * w.X,
                          Center.Y + A * x * U.Y + B * y * V.Y + C * z * w.Y,
                          Center.Z + A * x * U.Z + B * y * V.Z + C * z * w.Z);
    }

    /// <summary>True when <paramref name="p"/> is inside or on the surface.</summary>
    public bool Contains(Point3 p)
    {
        var d = new Point3(p.X - Center.X, p.Y - Center.Y, p.Z - Center.Z);
        var w = W;
        double u = (d.X * U.X + d.Y * U.Y + d.Z * U.Z) / A, v = (d.X * V.X + d.Y * V.Y + d.Z * V.Z) / B,
               t = (d.X * w.X + d.Y * w.Y + d.Z * w.Z) / C;
        return u * u + v * v + t * t <= 1;
    }

    /// <summary>The exact half-extent along each world axis: √((A·Uᵢ)² + (B·Vᵢ)² + (C·Wᵢ)²).</summary>
    public (double X, double Y, double Z) HalfExtent()
    {
        var w = W;
        double R(double u, double v, double t) => Math.Sqrt(A * A * u * u + B * B * v * v + C * C * t * t);
        return (R(U.X, V.X, w.X), R(U.Y, V.Y, w.Y), R(U.Z, V.Z, w.Z));
    }
}

/// <summary>
/// brief-em3d-64 R-em3d64-3a — a solid the geometry kernel built (a boolean, a fillet, a chamfer, a STEP part): an OPAQUE
/// B-rep only the worker reads, the kernel's display tessellation of it, and its named faces and edges. It is a
/// primitive, not a second kind of solid, so precedence, construction order, materials and provenance need no new case.
/// The Engine never parses <see cref="Brep"/>. Every length is metres.
/// </summary>
/// <param name="Brep">The B-rep bytes, as the worker wrote them.</param>
/// <param name="BrepHash">SHA-256 of <see cref="Brep"/>, lower-case hex — what the solid is content-hashed by.</param>
/// <param name="Display">The display tessellation: each triangle's <see cref="Em3dTriangle.Face"/> is an index into <see cref="Faces"/>.</param>
public sealed record Em3dShapeSolid(
    ReadOnlyMemory<byte>          Brep,
    string                        BrepHash,
    Em3dTriangleMesh              Display,
    IReadOnlyList<Em3dShapeFace>  Faces,
    IReadOnlyList<Em3dShapeEdge>  Edges) : Em3dPrimitive
{
    /// <summary>The linear deflection <see cref="Display"/> was made at, metres.</summary>
    public double DisplayDeflectionM { get; init; }

    /// <summary>brief-sar-11 — a solid with no B-rep: an imported triangle mesh (its <see cref="Display"/> IS the solid) or a voxel
    /// body's tissue (its display is a drawing; <see cref="Em3dSolid.Labels"/> is the solid). The geometry worker never sees one,
    /// so only openEMS lowers it.</summary>
    public bool IsMesh => Brep.Length == 0;

    /// <summary>
    /// brief-em3d-65 R-em3d65-3a — the same solid tessellated again at a linear deflection (metres) and an angular one
    /// (radians), with each triangle's <see cref="Em3dTriangle.Face"/> an index into <see cref="Faces"/>: how the FDTD
    /// lowering asks for a tessellation fitted to ITS grid rather than the screen's. Set by whoever built the solid (the
    /// elaboration, over the geometry kernel); the Engine only calls it. Null — a solid built by hand — has no other
    /// tessellation than <see cref="Display"/>. Not part of equality: it re-asks for the same B-rep.
    /// </summary>
    public Func<double, double, Em3dTriangleMesh>? Tessellator { get; init; }

    /// <summary>
    /// brief-em3d-120 R-em3d120-4b — when this solid is a Subtract whose Blank and Tools are plain primitives: those operands,
    /// so a cylindrical openEMS grid can write the Blank and cut each Tool by priority instead of a tessellation (a bore then
    /// lies on the grid's own circle). Set by the elaboration; null for every other kernel solid. A HINT only: the B-rep is
    /// the solid, and nothing but that one lowering reads this. Not part of equality.
    /// </summary>
    public Em3dOperands? Operands { get; init; }

    /// <summary>The bound of the faces' tight boxes, metres.</summary>
    public (double X0, double Y0, double Z0, double X1, double Y1, double Z1) Bounds()
    {
        double x0 = double.PositiveInfinity, y0 = x0, z0 = x0, x1 = double.NegativeInfinity, y1 = x1, z1 = x1;
        foreach (var f in Faces)
        {
            x0 = Math.Min(x0, f.Box.X0); y0 = Math.Min(y0, f.Box.Y0); z0 = Math.Min(z0, f.Box.Z0);
            x1 = Math.Max(x1, f.Box.X1); y1 = Math.Max(y1, f.Box.Y1); z1 = Math.Max(z1, f.Box.Z1);
        }
        if (double.IsInfinity(x0))
            foreach (var q in Display.Vertices)
            {
                x0 = Math.Min(x0, q.X); y0 = Math.Min(y0, q.Y); z0 = Math.Min(z0, q.Z);
                x1 = Math.Max(x1, q.X); y1 = Math.Max(y1, q.Y); z1 = Math.Max(z1, q.Z);
            }
        return (x0, y0, z0, x1, y1, z1);
    }

    /// <summary>Equal when the B-rep and the display deflection are: what lets a tessellation cache keyed by primitive
    /// hit across re-elaborations of an unchanged object.</summary>
    public bool Equals(Em3dShapeSolid? other)
        => other is not null && BrepHash == other.BrepHash && DisplayDeflectionM.Equals(other.DisplayDeflectionM);

    public override int GetHashCode() => HashCode.Combine(BrepHash, DisplayDeflectionM);

    /// <summary>The surface kinds a face table calls curved (everything but <c>plane</c> and <c>other</c>).</summary>
    public static bool IsCurved(string kind) => kind is "cylinder" or "cone" or "sphere" or "torus" or "bspline";

    /// <summary>brief-em3d-65 — the axis (0 x, 1 y, 2 z) a PLANAR face is normal to, read from its tight box having no
    /// thickness along it; null for a curved or an oblique face.</summary>
    public static int? NormalAxis(Em3dShapeFace face)
    {
        if (face.Kind != "plane") return null;
        var b = face.Box;
        double[] d = [b.X1 - b.X0, b.Y1 - b.Y0, b.Z1 - b.Z0];
        double tol = 1e-9 * Math.Max(d.Max(), 1e-9);
        int? axis = null;
        for (int k = 0; k < 3; k++)
            if (d[k] <= tol) { if (axis is not null) return null; axis = k; }
        return axis;
    }
}

/// <summary>brief-em3d-120 — a Subtract's operands as primitives (metres, placed): its Blank, and its Tools.</summary>
public sealed record Em3dOperands(Em3dPrimitive Blank, IReadOnlyList<Em3dOperandTool> Tools);

/// <summary>One Tool of a Subtract: its primitive, its name, and — when the Subtract keeps its Tools — the name of the solid it
/// is kept as (it follows the result in the problem, in its own material).</summary>
public sealed record Em3dOperandTool(string Name, Em3dPrimitive Primitive, string? KeptAs);

/// <summary>One named face of a kernel solid: its surface kind (plane, cylinder, cone, sphere, torus, bspline, other),
/// tight box and smallest radius of curvature (0 for a plane), metres, and its triangles in the display mesh.</summary>
public sealed record Em3dShapeFace(string Name, string Kind,
                                   (double X0, double Y0, double Z0, double X1, double Y1, double Z1) Box,
                                   double MinRadiusM, int FirstTriangle, int TriangleCount);

/// <summary>One named feature edge of a kernel solid: the two faces it separates, its curve kind, smallest radius
/// (0 for a line), metres, and a polyline for drawing and snapping.</summary>
public sealed record Em3dShapeEdge(string Name, string FaceA, string FaceB, string Kind, double MinRadiusM,
                                   IReadOnlyList<Point3> Polyline)
{
    /// <summary>brief-em3d-67 — a closed edge (a circle): one vertex, no midpoint.</summary>
    public bool Closed { get; init; }

    /// <summary>brief-em3d-67 — its length along the curve, metres; 0 when the kernel did not say.</summary>
    public double LengthM { get; init; }

    /// <summary>brief-em3d-67 — the unit tangents at the polyline's first and last points, along its direction; null when
    /// the kernel did not say.</summary>
    public (Point3 Start, Point3 End)? Tangents { get; init; }

    /// <summary>brief-em3d-67 — the point halfway along the curve (from the curve, not the polyline); null when closed.</summary>
    public Point3? Mid { get; init; }

    /// <summary>brief-em3d-67 — a circle's or an arc's centre, and its radius (metres); null for any other curve.</summary>
    public Point3? Centre { get; init; }
    public double RadiusM { get; init; }
}

// ── The problem's parts ──────────────────────────────────────────────────────────────────────

/// <summary>
/// One named solid. <paramref name="Order"/> is its construction order (R-em3d3-1d): where two
/// solids overlap, the higher order wins the volume — after the rule that metal takes precedence over
/// dielectric (<see cref="Em3dPrecedence"/>, em-3d.md §6.3a). It is part of the problem so that no backend
/// ever infers it.
/// </summary>
public sealed record Em3dSolid(string Name, string Material, Em3dRole Role, Em3dPrimitive Primitive,
                               int Order)
{
    /// <summary>brief-sar-8 — the voxel lattice of the phantom this solid is a layer of, when the phantom is voxelized: the
    /// solid holds a point exactly when its primitive holds the centre of the voxel the point lies in. Null — every other
    /// solid — is the primitive itself.</summary>
    public Em3dVoxelLattice? Voxel { get; init; }

    /// <summary>brief-sar-11 — the label volume this solid is one tissue of, when it came from a segmented voxel model: the solid
    /// is every voxel whose label is <see cref="Label"/>, and its primitive is drawing only (a surface extracted from the voxels
    /// at a reduced resolution). Null for every other solid.</summary>
    public Em3dLabelVolume? Labels { get; init; }

    /// <summary>brief-sar-11 — the entry of <see cref="Labels"/> this solid is (1-based; 0 is the volume's air).</summary>
    public int Label { get; init; }

    /// <summary>Whether the solid holds <paramref name="q"/>: <paramref name="inside"/> asked of the primitive at <paramref name="q"/>,
    /// or at its voxel's centre when the solid is voxelized; a label volume's tissue holds the voxels carrying its label.</summary>
    public bool Holds(Point3 q, Func<Em3dPrimitive, Point3, bool> inside)
        => Labels is { } v ? v.EntryAt(q) == Label : inside(Primitive, Voxel?.CentreOf(q) ?? q);
}

/// <summary>
/// brief-sar-11 — a segmented voxel model, placed: voxel boundaries along the world's axes (metres, strictly increasing, one more
/// line than voxels per axis) and one entry per voxel, C order over (x, y, z), 0 for air and k for <c>Tissues[k − 1]</c>. Every
/// solid of one body shares the one instance. <paramref name="GridStepM"/> is the FDTD cell asked for inside the body: 0 puts a
/// line on every voxel boundary; a positive step lets the grid place lines at most that far apart, and each cell then takes the
/// voxel its own centre lies in (CSXCAD's sampling under cell-constant material).
/// </summary>
public sealed record Em3dLabelVolume(string Body, double[] X, double[] Y, double[] Z, byte[] Entries,
                                     IReadOnlyList<string> Tissues, double GridStepM)
{
    public int Nx => X.Length - 1;
    public int Ny => Y.Length - 1;
    public int Nz => Z.Length - 1;

    /// <summary>The entry of the voxel holding <paramref name="q"/> (a point on a boundary belongs to the voxel above it); 0
    /// outside the volume.</summary>
    public int EntryAt(Point3 q)
    {
        int i = Cell(X, q.X), j = Cell(Y, q.Y), k = Cell(Z, q.Z);
        return i < 0 || j < 0 || k < 0 ? 0 : Entries[((long)i * Ny + j) * Nz + k];
    }

    private static int Cell(double[] lines, double v)
    {
        if (!(v >= lines[0]) || !(v <= lines[^1])) return -1;
        int at = Array.BinarySearch(lines, v);
        int i = at >= 0 ? at : ~at - 1;
        return Math.Min(i, lines.Length - 2);
    }

    /// <summary>Equal by identity: one body's solids share one instance, and its arrays are too large to compare.</summary>
    public bool Equals(Em3dLabelVolume? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}

/// <summary>
/// brief-sar-8 — a voxelized phantom's lattice: cubes of side <paramref name="PitchM"/> on the world's axes, with a corner at
/// <paramref name="Anchor"/> (the phantom's centre). Each voxel is the innermost layer whose shape holds the voxel's centre —
/// the rule openEMS's bundled phantoms are made by — so an FDTD cell takes the voxel its own centre lies in.
/// </summary>
public sealed record Em3dVoxelLattice(string Phantom, Point3 Anchor, double PitchM)
{
    /// <summary>The centre of the voxel holding <paramref name="q"/>.</summary>
    public Point3 CentreOf(Point3 q) => new(Centre(q.X, Anchor.X), Centre(q.Y, Anchor.Y), Centre(q.Z, Anchor.Z));

    /// <summary>The voxel index along one axis of the voxel holding <paramref name="v"/>, counted from the anchor.</summary>
    public long Index(double v, double anchor) => (long)Math.Floor((v - anchor) / PitchM);

    private double Centre(double v, double anchor) => anchor + (Index(v, anchor) + 0.5) * PitchM;
}

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
    int                                   Order)
{
    /// <summary>
    /// brief-em3d-42 R-em3d42-1b — the plane the sheet lies in, when it is not horizontal: the outline's
    /// (x, y) at height <see cref="Z"/> are the frame's own coordinates (<see cref="Em3dPlaneFrame.World"/>).
    /// Null — every sheet a layout makes — is today's horizontal sheet at <see cref="Z"/>, so every problem
    /// written before the frame existed is unchanged.
    /// </summary>
    public Em3dPlaneFrame? Frame { get; init; }

    /// <summary>A point of the outline, in the world.</summary>
    public Point3 World(Point2 q) => Frame is { } f ? f.World(q.X, q.Y, Z) : new Point3(q.X, q.Y, Z);

    /// <summary>The sheet's bound in the world, metres.</summary>
    public (double X0, double Y0, double Z0, double X1, double Y1, double Z1) WorldBounds()
    {
        double x0 = double.PositiveInfinity, y0 = x0, z0 = x0, x1 = double.NegativeInfinity, y1 = x1, z1 = x1;
        foreach (var q in Outline)
        {
            var w = World(q);
            x0 = Math.Min(x0, w.X); y0 = Math.Min(y0, w.Y); z0 = Math.Min(z0, w.Z);
            x1 = Math.Max(x1, w.X); y1 = Math.Max(y1, w.Y); z1 = Math.Max(z1, w.Z);
        }
        return (x0, y0, z0, x1, y1, z1);
    }
}

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

    /// <summary>
    /// brief-em3d-93 — the number this port has in the document it came from, when the problem renumbered its ports (a port
    /// turned off leaves the rest numbered 1…N, as a Touchstone file's must be); null when <see cref="Number"/> is the
    /// document's own. What the result's port map records.
    /// </summary>
    public int? SourceNumber { get; init; }

    /// <summary>brief-em3d-93 — the port's label in that document (<c>P3</c>), set with <see cref="SourceNumber"/>; a terminal's
    /// is its own name (brief-em3d-114), set always.</summary>
    public string? SourceLabel { get; init; }

    /// <summary>
    /// brief-em3d-114 R-em3d114-2a — the group of a multi-terminal wave port's terminals: every terminal of one port carries
    /// the same value (its lowest document number), shares its rectangle and its reference (<see cref="NegativeObject"/>),
    /// and is otherwise a port like any other (overview rule 1). Null on every port with one terminal. A terminal's
    /// <see cref="VoltagePath"/> runs from the reference to its conductor, or the other way when the terminal is flipped.
    /// </summary>
    public int? FaceGroup { get; init; }

    /// <summary>brief-em3d-114 — the label of the port a terminal belongs to (<c>Left</c>), set with <see cref="FaceGroup"/>:
    /// what a refusal and the result's port map name it by.</summary>
    public string? FaceGroupLabel { get; init; }

    /// <summary>
    /// brief-sar-8b R-sar8b-1 — a lumped port whose sheet is a patch of a cylinder: <see cref="Min"/>/<see cref="Max"/> are then the
    /// patch's bounding box and <see cref="Direction"/> the gap's direction at its centre. Null on every other port. Only openEMS's
    /// cylindrical grid about the same axis writes it; every other lowering refuses it (<see cref="Em3dCylinderGap.NotHere"/>).
    /// </summary>
    public Em3dCylinderGap? Cylinder { get; init; }
}

/// <summary>brief-sar-3 R-sar3-3 — how a lumped element's R, L and C are joined.</summary>
public enum Em3dRlcTopology
{
    /// <summary>R ∥ L ∥ C — openEMS's <c>LEtype</c> 0, and its default.</summary>
    Parallel,
    /// <summary>R + L + C in series — openEMS's <c>LEtype</c> 1.</summary>
    Series,
}

/// <summary>
/// brief-sar-3 R-sar3-3 — a lumped R, L and/or C across a gap: an axis-aligned rectangle from <paramref name="Min"/> to
/// <paramref name="Max"/> (exactly one axis has zero extent), carrying current along <paramref name="Direction"/> — the same
/// sheet a lumped port is, found by the same rules. Each of <paramref name="R"/> (Ω), <paramref name="L"/> (H) and
/// <paramref name="C"/> (F) may be absent (null). It is <b>not a port</b>: it is excited by nothing and adds no row to S.
/// </summary>
public sealed record Em3dLumpedElement(
    string          Name,
    Point3          Min,
    Point3          Max,
    Point3          Direction,
    double?         R,
    double?         L,
    double?         C,
    Em3dRlcTopology Topology)
{
    /// <summary>brief-sar-8b R-sar8b-1 — the gap is a patch of a cylinder, as a lumped port's may be (<see cref="Em3dPort.Cylinder"/>).</summary>
    public Em3dCylinderGap? Cylinder { get; init; }

    /// <summary>
    /// Why openEMS 0.37.0 cannot state this element, or null when it can. Its lumped element takes R, L and C each optional,
    /// in parallel or in series — but a value that is negative is clamped to zero, an element with none is skipped, a zero R
    /// or L in parallel is a short it treats as absent, a zero C in series is treated as absent, and a series element of R =
    /// 0 alone divides by zero. Each is refused here by name rather than written as something else.
    /// </summary>
    public string? Inexpressible()
    {
        string who = $"Lumped element '{Name}'";
        if (R is null && L is null && C is null)
            return $"{who} states no R, L or C: an element with none is nothing, and openEMS skips it. Give it a value.";
        foreach (var (key, v) in new[] { ("R", R), ("L", L), ("C", C) })
            if (v is { } x && (!double.IsFinite(x) || x < 0))
                return $"{who}'s {key} is {x.ToString("G6", System.Globalization.CultureInfo.InvariantCulture)}: a lumped value is a " +
                       "finite number of 0 or more (openEMS would clamp a negative one to zero).";
        if (Topology == Em3dRlcTopology.Parallel)
        {
            if (R == 0) return $"{who} is parallel with R = 0, a short circuit openEMS treats as absent (open). Draw a conductor for a short, or remove R.";
            if (L == 0) return $"{who} is parallel with L = 0, a short circuit openEMS treats as absent. Draw a conductor for a short, or remove L.";
        }
        else
        {
            if (C == 0) return $"{who} is series with C = 0, which openEMS treats as absent rather than as an open. Remove C, or give it a value.";
            if ((R ?? 0) == 0 && (L ?? 0) == 0 && C is null)
                return $"{who} is series with nothing but a zero R or L, which openEMS 0.37.0 cannot state (its update divides by it). " +
                       "Draw a conductor for a short.";
        }
        return null;
    }
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
public sealed record Em3dTerminal(string Name, IReadOnlyList<string> Objects, string? SourcePort = null)
{
    /// <summary>brief-dfr31-1 R-dfr31-1-6 — a FLOATING body of an electrostatic solve: solved as one more terminal and then
    /// eliminated from the matrix (<see cref="Em3dStaticResult.EliminateFloating"/>), which is exact for metal carrying no net
    /// charge. It is not a row of the result.</summary>
    public bool Floating { get; init; }
}

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
    /// 3D editor round 3 — the material that fills the box where no solid is (one of the problem's materials): Air by
    /// default, and whatever the user gave the air box — Vacuum, say — otherwise. Null keeps the older rule (the problem's
    /// air solid's material, else Air, else free space), which every layout-generated problem uses.
    /// </summary>
    public string? Material { get; init; }

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
public sealed record Em3dFrequency(double StartHz, double StopHz, int Points, Em3dSweepKind Kind)
{
    /// <summary>GitHub discussion 26 — a frequency PLAN (several segments, or single points) is not one even sweep, so it
    /// travels as the exact sorted frequencies it asks for; null for one even sweep, which the four fields describe.
    /// <see cref="StartHz"/>, <see cref="StopHz"/> and <see cref="Points"/> are then its lowest, highest and count,
    /// so everything that sizes a mesh or an excitation from them is unchanged.</summary>
    public IReadOnlyList<double>? ListHz { get; init; }
}

/// <summary>
/// brief-em3d-74 R-em3d74-4b — a named box, metres, inside which the mesh's elements are at most <paramref name="SizeM"/>;
/// they grow by <paramref name="Grading"/> leaving it (null: the lowering's own grading).
/// </summary>
public sealed record Em3dMeshRegion(string Name, Point3 Min, Point3 Max, double SizeM, double? Grading);

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

    /// <summary>brief-dfr32-1 — the content extent the air box was padded round, when the problem's builder knows it better than
    /// its solids say: a <c>.cem</c>'s dielectric slabs are built out TO the box, so their bounds are the box's. Null — every other
    /// problem — means the solids' and sheets' own bounds. Nothing lowers it.</summary>
    public (Point3 Lo, Point3 Hi)? PaddedContent { get; init; }

    /// <summary>Designer round 33 — the dielectric height H under the top metal that the default air box was padded by (20 H on
    /// the top and sides, 10 H below), when its builder found one; null when the box took the wavelength rule. Carried so
    /// <c>explain</c> and a run's remedies name the same default the box was built with. Nothing lowers it.</summary>
    public double? PaddingHeightM { get; init; }

    /// <summary>A static problem's terminals, in index order (terminal k is index k + 1).</summary>
    public IReadOnlyList<Em3dTerminal> Terminals { get; init; } = [];

    /// <summary>The conductors (by name) that are the ground terminal — the matrix's reference. A PEC
    /// face of the air box is ground too, without being listed.</summary>
    public IReadOnlyList<string> GroundObjects { get; init; } = [];

    /// <summary>True for an electrostatic or magnetostatic problem.</summary>
    public bool IsStatic => Type is Em3dProblemType.Electrostatic or Em3dProblemType.Magnetostatic;

    /// <summary>brief-em3d-73 R-em3d73-5a — what every EM lowering says to a <see cref="Em3dProblemType.Thermal"/>
    /// problem. The run service never sends one there; this is the second line, so a thermal problem can never be
    /// written as an EM input by a path that forgot to ask.</summary>
    public const string ThermalIsNotEm =
        "A thermal problem is solved by circuitRF's own thermal solver, never by an EM solver: it cannot be lowered for Palace or openEMS.";

    /// <summary>brief-em3d-23 R-em3d23-4a — how many modes an eigenmode problem finds.</summary>
    public int EigenmodeCount { get; init; } = 1;

    /// <summary>brief-em3d-23 R-em3d23-4a — the frequency above which an eigenmode problem looks for
    /// modes, Hz.</summary>
    public double EigenmodeTargetHz { get; init; }

    /// <summary>True when any port is a wave port.</summary>
    public bool HasWavePorts => Ports.Any(p => p.Kind == Em3dPortKind.Wave);

    /// <summary>brief-sar-3 R-sar3-3 — lumped R, L, C across gaps. They are not ports. Empty — every problem written before
    /// them — lowers byte for byte as before.</summary>
    public IReadOnlyList<Em3dLumpedElement> LumpedElements { get; init; } = [];

    /// <summary>brief-sar-8b R-sar8b-2 — conductors that are patches of a cylinder (Em3dCylinderSheet.cs). Empty — every problem
    /// written before them — lowers byte for byte as before; a lowering that cannot state one refuses it by name.</summary>
    public IReadOnlyList<Em3dCylinderSheet> CylinderSheets { get; init; } = [];

    /// <summary>brief-sar-8b — the first cylindrical sheet, port gap or lumped-element gap, by what a refusal names it; null when
    /// the problem has none. Every lowering but openEMS's cylindrical grid refuses a problem for which this is not null.</summary>
    public string? FirstCylindrical()
        => CylinderSheets.Count > 0 ? $"Sheet '{CylinderSheets[0].Name}'"
         : Ports.FirstOrDefault(p => p.Cylinder is not null) is { } port ? $"Port {port.SourceNumber ?? port.Number}"
         : LumpedElements.FirstOrDefault(l => l.Cylinder is not null) is { } le ? $"Lumped element '{le.Name}'" : null;

    /// <summary>brief-sar-6 R-sar6-1 — each solid's material density, kg/m³, by solid name, where its material states one. Only a
    /// SAR run reads it (it writes the tissue cells' density, which openEMS's raw SAR dump carries); every other lowering ignores
    /// it, so a problem without it lowers byte for byte as before.</summary>
    public IReadOnlyDictionary<string, double> SolidDensities { get; init; } = new Dictionary<string, double>();

    /// <summary>brief-em3d-74 R-em3d74-4b — boxes with a target element size, honoured by every lowering that meshes with Gmsh
    /// (Palace and thermal). Empty — every problem written before them — lowers byte for byte as before.</summary>
    public IReadOnlyList<Em3dMeshRegion> MeshRegions { get; init; } = [];

    /// <summary>brief-emb-2 R-emb2-1 — the solids whose curved faces are a layout's drilled barrels: a via's barrel, its air
    /// core, a drilled hole. Gmsh meshes a problem whose ONLY curved faces are these with straight-sided second-order
    /// elements, since curving a faceted barrel buys almost nothing and its optimisation ran for 17+ minutes on an imported
    /// board. Set by whoever builds the problem from layout artwork; empty — every other problem — keeps the curving.</summary>
    public IReadOnlyCollection<string> ArtworkBarrels { get; init; } = [];

    /// <summary>brief-em3d-49 R-em3d49-4c — boundaries on named faces of dielectric and air solids. Empty — every
    /// problem written before them — lowers to exactly what it lowered before (overview §1h).</summary>
    public IReadOnlyList<Em3dFaceBoundary> FaceBoundaries { get; init; } = [];

    /// <summary>
    /// brief-em3d-49 — each face boundary's planar pieces, in <see cref="FaceBoundaries"/> order. Call after
    /// <see cref="Validate"/>: a boundary that does not resolve has no entry.
    /// </summary>
    public IReadOnlyList<(Em3dFaceBoundary Boundary, IReadOnlyList<Em3dFacePolygon> Pieces)> FaceBoundaryPieces()
    {
        var list = new List<(Em3dFaceBoundary, IReadOnlyList<Em3dFacePolygon>)>();
        foreach (var b in FaceBoundaries)
            if (Solids.FirstOrDefault(s => s.Name == b.Object) is { } solid &&
                Em3dFaceGeometry.Pieces(solid.Primitive, b.Face, out _) is { } pieces)
                list.Add((b, pieces));
        return list;
    }

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
                         .Concat(Sheets.Select(s => s.Name)).Concat(CylinderSheets.Select(s => s.Name)).Where(n => !held.Contains(n))];
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

            if (s.Primitive is Em3dPolyhedron poly && poly.Problems(s.Name) is { Count: > 0 } polyProblems)
                problems.AddRange(polyProblems);
            else if (Degenerate(s.Primitive) is { } why)
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
            if (sh.Outline.Count < 3)
                problems.Add($"Sheet '{sh.Name}' has fewer than three vertices.");
            else if (sh.WorldBounds() is var (x0, y0, z0, x1, y1, z1) && !Inside(x0, y0, z0, x1, y1, z1))
                problems.Add($"Sheet '{sh.Name}' extends outside the air box.");
            if (sh.Frame is { } fr && (Math.Abs(Dot(fr.U, fr.U) - 1) > 1e-9 || Math.Abs(Dot(fr.V, fr.V) - 1) > 1e-9 ||
                                       Math.Abs(Dot(fr.U, fr.V)) > 1e-9))
                problems.Add($"Sheet '{sh.Name}''s plane is not stated by two perpendicular unit vectors.");
        }

        foreach (var cs in CylinderSheets)
        {
            Name("sheet", cs.Name);
            Material(cs.Name, cs.Material);
            if (cs.Patch.Problems($"Sheet '{cs.Name}'") is { Count: > 0 } patchProblems) problems.AddRange(patchProblems);
            else if (cs.WorldBounds() is var (x0, y0, z0, x1, y1, z1) && !Inside(x0, y0, z0, x1, y1, z1))
                problems.Add($"Sheet '{cs.Name}' extends outside the air box.");
        }

        var objects = new HashSet<string>(Solids.Select(s => s.Name).Concat(Sheets.Select(s => s.Name)).Concat(CylinderSheets.Select(s => s.Name)),
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
            // brief-sar-8b — a gap on a cylinder is its patch, not an axis-aligned rectangle.
            if (p.Cylinder is { } pc)
            {
                if (p.Kind == Em3dPortKind.Wave) problems.Add($"Port {p.Number} is a wave port on a cylinder; a gap on a cylinder is a lumped port.");
                if (pc.Patch.Problems($"Port {p.Number}") is { Count: > 0 } gapProblems) problems.AddRange(gapProblems);
                else if (!Inside(p.Min.X, p.Min.Y, p.Min.Z, p.Max.X, p.Max.Y, p.Max.Z))
                    problems.Add($"Port {p.Number}'s sheet extends outside the air box.");
                continue;
            }
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

        // brief-sar-3 R-sar3-3 — a lumped element is a port's sheet that carries R, L, C instead of a source.
        foreach (var le in LumpedElements)
        {
            Name("lumped element", le.Name);
            int zeroAxes = (le.Max.X == le.Min.X ? 1 : 0) + (le.Max.Y == le.Min.Y ? 1 : 0) + (le.Max.Z == le.Min.Z ? 1 : 0);
            if (le.Cylinder is { } lc)
            {
                if (lc.Patch.Problems($"Lumped element '{le.Name}'") is { Count: > 0 } gapProblems) problems.AddRange(gapProblems);
                else if (!Inside(le.Min.X, le.Min.Y, le.Min.Z, le.Max.X, le.Max.Y, le.Max.Z))
                    problems.Add($"Lumped element '{le.Name}''s sheet extends outside the air box.");
            }
            else if (zeroAxes != 1)
                problems.Add($"Lumped element '{le.Name}''s sheet is not a rectangle: exactly one of its axes must have zero extent, " +
                             $"and {zeroAxes} do.");
            else if (!Inside(le.Min.X, le.Min.Y, le.Min.Z, le.Max.X, le.Max.Y, le.Max.Z))
                problems.Add($"Lumped element '{le.Name}''s sheet extends outside the air box.");
            if (le.R is null && le.L is null && le.C is null)
                problems.Add($"Lumped element '{le.Name}' states no R, L or C.");
            if (IsStatic)
                problems.Add($"Lumped element '{le.Name}' is in a static problem, which has no frequency for its R, L and C to act at.");
        }

        ValidateFaceGroups(problems);
        ValidateFaceBoundaries(problems);

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
    /// brief-em3d-114 R-em3d114-2b — a multi-terminal wave port's terminals: every one a wave port with a voltage path, on one
    /// rectangle of one face, with one reference, and each its own conductor.
    /// </summary>
    private void ValidateFaceGroups(List<string> problems)
    {
        foreach (var g in Ports.Where(p => p.FaceGroup is not null).GroupBy(p => p.FaceGroup!.Value))
        {
            var ps = g.OrderBy(p => p.Number).ToList();
            string name = ps[0].FaceGroupLabel is { } l ? $"Port '{l}'" : $"Face group {g.Key}";
            string numbers = string.Join(", ", ps.Select(p => p.Number));
            if (ps.Count < 2)
                problems.Add($"{name} has one terminal (port {numbers}); a face group is two or more terminals.");
            if (ps.FirstOrDefault(p => p.Kind != Em3dPortKind.Wave) is { } lumped)
                problems.Add($"{name}'s terminal port {lumped.Number} is not a wave port; every terminal of a face group is one.");
            if (ps.FirstOrDefault(p => p.VoltagePath is null) is { } pathless)
                problems.Add($"{name}'s terminal port {pathless.Number} has no voltage path; every terminal needs its own.");
            if (ps.Any(p => p.Min != ps[0].Min || p.Max != ps[0].Max) || ps.Select(p => FaceOf(p.Min, p.Max)).Distinct().Count() > 1)
                problems.Add($"{name}'s terminals (ports {numbers}) do not share one rectangle on one face of the air box.");
            if (ps.Select(p => p.NegativeObject).Distinct(StringComparer.Ordinal).Count() > 1)
                problems.Add($"{name}'s terminals (ports {numbers}) do not share one reference: " +
                             $"{string.Join(", ", ps.Select(p => $"'{p.NegativeObject}'").Distinct())}.");
            foreach (var dup in ps.GroupBy(p => p.PositiveObject, StringComparer.Ordinal).Where(d => d.Count() > 1))
                problems.Add($"{name}'s terminals {string.Join(" and ", dup.Select(p => $"port {p.Number}"))} are all on '{dup.Key}'; " +
                             "each terminal is its own conductor.");
        }
    }

    /// <summary>
    /// brief-em3d-49 R-em3d49-4b — every face boundary: on a face that exists and is flat, of a dielectric or air
    /// solid, as a perfect conductor or a conductive surface of a metal the problem resolves; each face once.
    /// </summary>
    private void ValidateFaceBoundaries(List<string> problems)
    {
        var seen = new HashSet<(string, string)>();
        foreach (var b in FaceBoundaries)
        {
            string where = $"The boundary on face '{b.Face}' of '{b.Object}'";
            if (!seen.Add((b.Object, b.Face)))
                problems.Add($"{where} is stated twice; a face has one boundary.");
            if (b.Kind is Em3dFaceBoundaryKind.Absorbing or Em3dFaceBoundaryKind.Pmc or Em3dFaceBoundaryKind.Symmetry)
            {
                problems.Add($"{where} is {b.Kind}, which both 3D solvers state only on the outer boundary of the problem. " +
                             "Set it on the air box's face in the setup's AirBox; a solid's face may be Pec or Conductive.");
                continue;
            }
            var solid = Solids.FirstOrDefault(s => s.Name == b.Object);
            if (solid is null)
            {
                problems.Add(Sheets.Any(s => s.Name == b.Object)
                    ? $"{where} is on a sheet, which is already a conductor's surface: a boundary on it has nothing to add."
                    : $"{where} names no solid of this problem.");
                continue;
            }
            if (solid.Role == Em3dRole.Conductor)
            {
                problems.Add($"{where} is on a conductor. A conductor is a void bounded by its own metal, so a boundary on " +
                             "its face has nothing to add; put boundaries on dielectric and air solids.");
                continue;
            }
            if (Em3dFaceGeometry.Pieces(solid.Primitive, b.Face, out string? why) is null)
                problems.Add($"{where} cannot be placed: {why}.");
            if (b.Kind == Em3dFaceBoundaryKind.Conductive)
            {
                var metal = Materials.FirstOrDefault(m => m.Name == b.Material);
                if (b.Material is not { Length: > 0 })
                    problems.Add($"{where} is Conductive and names no material; a conductive surface is made of a metal.");
                else if (metal is null)
                    problems.Add($"{where} is made of '{b.Material}', which this problem does not resolve to any values.");
                else if (!(metal.SigmaSm > 0))
                    problems.Add($"{where} is made of '{b.Material}', which does not conduct (σ is 0).");
            }
        }
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
        Em3dEllipsoid e when !(e.A > 0 && e.B > 0 && e.C > 0) => "has a semi-axis that is not positive",
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
            case Em3dPolyhedron ph:
            {
                double x0 = double.PositiveInfinity, y0 = x0, z0 = x0;
                double x1 = double.NegativeInfinity, y1 = x1, z1 = x1;
                foreach (var q in ph.Vertices)
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
            case Em3dEllipsoid e:
            {
                var (hx, hy, hz) = e.HalfExtent();
                return (e.Center.X - hx, e.Center.Y - hy, e.Center.Z - hz, e.Center.X + hx, e.Center.Y + hy, e.Center.Z + hz);
            }
            case Em3dShapeSolid k:
                return k.Bounds();
            default:
                throw new ArgumentOutOfRangeException(nameof(p), p.GetType().Name, "unknown primitive");
        }
    }

    private static double Dot(Point3 a, Point3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

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
