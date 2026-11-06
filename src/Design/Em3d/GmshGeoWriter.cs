// brief-em3d-7 R-em3d7-2 — the Palace lowering's geometry half: the resolved 3D problem as a Gmsh
// .geo script (docs/design/em-3d.md §6.1, §6.2 Route A, §6.4).
//
// TEXT, written here and run by the `gmsh` executable (R-em3d7-2a). Nothing in circuitRF links Gmsh,
// loads its library or calls its scripting API — those are GPL, and the firewall test in
// tests/Firewall.Tests/SolverBoundaryTests holds the line. Communication is this file, the mesh Gmsh
// writes, and the entity table the script prints about itself.
//
// BYTE-DETERMINISTIC (R-em3d7-2b): every double through Num (invariant culture, round-trip form, a
// negative zero written as 0), '\n' line endings on every platform, solids in construction order,
// groups in problem order. The same problem is the same bytes, which is what makes the writer
// testable with no solver installed and what lets a re-run reuse a mesh.
//
// THE RECIPE (overview §1e, confirmed by F0 Q5 — docs/design/em-3d-f0-findings.md):
//   1. Every solid is cut by every higher-precedence solid it overlaps (Em3dPrecedence: metal over
//      dielectric, then construction order), so the volumes are disjoint BEFORE any fragment. A conductor is then deleted: it is a VOID whose
//      surface carries Palace's conductivity boundary. What no solid claims inside the box is the
//      background, which is air: the box minus every solid, in ONE BooleanDifference (brief-em3d-137).
//   2. Sheets and port sheets are plane surfaces, and ONE BooleanFragments imprints them and the
//      shared faces. With OCCBooleanPreserveNumbering a volume it leaves whole keeps its tag, so a VOLUME
//      group is its construction tags, checked to have survived. A sheet reaching right across a solid
//      splits it, and that solid's tag is replaced by its pieces' (brief-dfr35-1 R-dfr35-1-9).
//   3. SURFACES cannot be followed by tag through a fragment, so each is recovered from what
//      circuitRF knows of it: a tight BoundingBox query around geometry it placed itself (F0 Q5's
//      caveat 1: OCCBoundsUseStl, or a curved face's box is loose). Classification is set algebra and
//      each face is claimed once: box faces, then ports, then sheets, then conductors — whose faces
//      are drawn only from the faces that bound exactly ONE volume (the voids and the box), smallest
//      conductor first so a pad inside a plane's box keeps its own faces.
//   4. The script prints its own entity table and closes the books (F0 Q5 caveat 3): a single-sided
//      face that no group claimed would become Palace's natural boundary — a PMC, silently — so the
//      count of those is printed and must be zero. PalaceRun reads the table back (R-em3d7-3b).
//
// Lengths are written in MICROMETRES and the mesh's length unit (config "L0") is 1e-6 m. OCCT's
// tolerances are absolute (1e-7 in model units): in metres a bond wire's 25 µm section would sit two
// orders of magnitude from them, in micrometres it sits nine.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Em3d;

/// <summary>brief-dfr35-1 R-dfr35-1-8 — a point a mesher failure names, in layout micrometres, and the size of what is there.</summary>
public sealed record Em3dLayoutPlace(double XUm, double YUm, double SpanUm);

/// <summary>What a mesh group is, to the entity check and to the Palace configuration.</summary>
public enum Em3dGroupKind
{
    /// <summary>A dielectric or air solid, meshed.</summary>
    Volume,
    /// <summary>What no solid claims inside the air box: air.</summary>
    Background,
    /// <summary>A conductor's void surface, carrying its conductivity.</summary>
    Conductor,
    /// <summary>A thin conductor as a surface, carrying its conductivity and thickness.</summary>
    Sheet,
    /// <summary>A lumped port's sheet.</summary>
    Port,
    /// <summary>One face of the air box.</summary>
    Face,
    /// <summary>brief-em3d-49 — a boundary on a named face of a dielectric or air solid: a perfect conductor, or a
    /// conductive surface carrying its metal.</summary>
    FaceBoundary,
}

/// <summary>
/// One physical group of the mesh: the named object it came from, the Palace attribute circuitRF
/// assigned it, and how many Gmsh entities it must yield (R-em3d7-3b).
/// </summary>
/// <param name="Expected">The count expected; with <paramref name="AtLeast"/> it is a floor.</param>
/// <param name="Material">A volume's material, a conductor's or a sheet's metal; null otherwise.</param>
/// <param name="Boundary">An air-box face's kind; null otherwise.</param>
/// <param name="PortNumber">A port's number; null otherwise.</param>
/// <param name="ThicknessM">A sheet's real thickness, or the h of a conductor whose faces carry it (brief-em3d-139), metres;
/// null otherwise.</param>
/// <param name="Terminal">brief-em3d-22 — the static terminal a conductor belongs to ("" for the
/// ground); null otherwise, and for every driven problem.</param>
public sealed record Em3dGroup(
    string Name, int Attribute, int Dimension, Em3dGroupKind Kind, int Expected, bool AtLeast,
    string? Material = null, Em3dBoundaryKind? Boundary = null, int? PortNumber = null, double? ThicknessM = null,
    string? Terminal = null);

/// <summary>The lowering: the script, its groups, and the groups as the file written beside the
/// mesh — or the reason the problem cannot be lowered.</summary>
/// <param name="KernelFiles">brief-em3d-65 R-em3d65-2a — the B-rep of each kernel solid the script imports, to be written
/// beside it; empty for a problem with none.</param>
public sealed record GmshLowering(string? Geo, IReadOnlyList<Em3dGroup> Groups, string? GroupsJson, string? Refusal,
                                  IReadOnlyList<Em3dKernelFile>? KernelFiles = null)
{
    public bool Ok => Refusal is null;
}

public static partial class GmshGeoWriter
{
    public const string GeoFile      = "model.geo";
    public const string MeshFile     = "model.msh";
    public const string EntitiesFile = "entities.txt";
    /// <summary>brief-dfr31-6 — which group each surface tag belongs to or bounds, read when Gmsh fails.</summary>
    public const string FacesFile    = "faces.txt";
    public const string GroupsFile   = "groups.json";

    /// <summary>The script's length unit, metres — Palace's <c>Model.L0</c>.</summary>
    public const double LengthUnitM = 1e-6;

    /// <summary>brief-dfr35-1 R-dfr35-1-8 — surfaces to one write of the face table's boxes.</summary>
    private const int SurfaceBoxBatch = 200;

    /// <summary>The group that holds the air no named solid claims.</summary>
    public const string BackgroundName = "background";

    /// <summary>Elements per full turn on a curved surface — a via barrel, a round wire. Twelve puts a
    /// 1 mil wire's surface elements at about its quarter-diameter, F0 case A's hand-tuned size. brief-em3d-65: held in
    /// <see cref="Em3dFidelity.PalaceCurvatureElements"/>, so the mesh-size note reads the number the script writes.</summary>
    public const int CurvatureElements = Em3dFidelity.PalaceCurvatureElements;

    /// <summary>Elements across a port sheet's smaller side, at least (R-em3d7-2e).</summary>
    public const int PortCellsAcross = 4;

    /// <summary>The query tolerance, micrometres (F0's).</summary>
    private const double Eps = 1e-3;

    private const double C0 = 299_792_458.0;

    /// <summary>What an empty background is meshed as when the problem names no air.</summary>
    internal static readonly Em3dMaterial FreeSpace = new("(free space)", 1, null, 0, 1, 0);

    /// <summary>
    /// The material the air box is filled with — what no solid claims inside it: the problem's air solid's, else the
    /// technology's "Air" when the problem carries it, else <see cref="FreeSpace"/>. The one rule the script and the
    /// 3D editor's object tree (3D editor round 3: its row is headed by it) both read.
    /// </summary>
    public static string BackgroundMaterial(IEnumerable<Em3dSolid> solids, IEnumerable<Em3dMaterial> materials)
        => solids.FirstOrDefault(s => s.Role == Em3dRole.Air)?.Material
           ?? (materials.Any(m => m.Name == "Air") ? "Air" : FreeSpace.Name);

    /// <summary>Whether <paramref name="material"/> is the built-in free space — a vacuum, not a technology's material.</summary>
    public static bool IsFreeSpace(string material) => material == FreeSpace.Name;

    private static readonly string[] FaceKeys = ["xmin", "xmax", "ymin", "ymax", "zmin", "zmax"];

    /// <summary>
    /// The script for <paramref name="problem"/>. Refuses — never guesses — a problem that does not
    /// validate or that states something Palace cannot be told.
    /// </summary>
    public static GmshLowering Write(Em3dProblem problem, PalaceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(settings);

        if (problem.Type == Em3dProblemType.Thermal) return No(Em3dProblem.ThermalIsNotEm);
        // brief-sar-8b — a sheet or gap on a cylinder is a box of openEMS's cylindrical grid, and nothing Gmsh meshes.
        if (problem.FirstCylindrical() is { } curved) return No(Em3dCylinderGap.NotHere(curved, "Palace"));
        // brief-sar-3 §3 — a lumped element on Palace is its own later series.
        if (problem.LumpedElements.Count > 0)
            return No(LumpedElementsOpenEmsOnly(problem.LumpedElements[0].Name));
        if (ImportedOpenEmsOnly(problem.Solids, "Palace") is { } imported) return No(imported);
        var problems = problem.Validate().Concat(settings.Problems()).ToList();
        if (problems.Count > 0)
            return No("The 3D problem cannot be lowered for Palace: " + string.Join(" ", problems));
        var f = problem.Boundary.Faces;
        var faceKinds = new[] { f.XMin, f.XMax, f.YMin, f.YMax, f.ZMin, f.ZMax };
        for (int k = 0; k < 6; k++)
            if (faceKinds[k] == Em3dBoundaryKind.Symmetry)
                return No($"The air box's {FaceKeys[k]} face is a Symmetry face, which does not say whether the " +
                          "field's electric or magnetic wall lies there. Set it to Pec (an electric wall) or Pmc " +
                          "(a magnetic one) in the setup's AirBox.");
        if (problem.Ports.Count == 0 && problem.Type == Em3dProblemType.Driven)
            return No("The 3D problem has no ports, so there is nothing for Palace to excite.");

        var materials = problem.Materials.ToDictionary(m => m.Name, StringComparer.Ordinal);

        // ── The groups, attributes in problem order (R-em3d7-2d) ─────────────────────────────
        var groups = new List<Em3dGroup>();
        int attr = 0;
        var solidGroup = new Dictionary<int, Em3dGroup>();
        // brief-em3d-22 — a static problem's conductors carry their terminal, so the entity check names it.
        var terminalOf = new Dictionary<string, string>(StringComparer.Ordinal);
        if (problem.IsStatic)
        {
            foreach (string gnd in problem.GroundObjects) terminalOf[gnd] = "";
            foreach (var t in problem.Terminals) foreach (string o in t.Objects) terminalOf[o] = t.Name;
        }
        // brief-em3d-139 R-em3d139-2 — a thin conductor's h, on its group, which the configuration writes as a sheet's is.
        var thinFaces = Em3dThinMetalLoss.CorrectedFaces(problem);
        for (int i = 0; i < problem.Solids.Count; i++)
        {
            var s = problem.Solids[i];
            solidGroup[i] = s.Role == Em3dRole.Conductor
                ? new Em3dGroup(s.Name, ++attr, 2, Em3dGroupKind.Conductor, 1, AtLeast: true, s.Material,
                                ThicknessM: thinFaces.TryGetValue(s, out double h) ? h : null,
                                Terminal: terminalOf.GetValueOrDefault(s.Name))
                : new Em3dGroup(s.Name, ++attr, 3, Em3dGroupKind.Volume, 1, AtLeast: false, s.Material);
            groups.Add(solidGroup[i]);
        }
        // The background is the problem's air; a problem with no air solid (a box closed by a PEC face
        // right at the stack) has at most an empty background, meshed as free space.
        string backgroundMaterial = problem.Boundary.Material ?? BackgroundMaterial(problem.Solids, problem.Materials);
        if (!materials.ContainsKey(backgroundMaterial)) materials[backgroundMaterial] = FreeSpace;
        var background = new Em3dGroup(BackgroundName, ++attr, 3, Em3dGroupKind.Background, 0, AtLeast: true,
                                       backgroundMaterial);
        groups.Add(background);

        var sheetGroups = problem.Sheets.Select(sh =>
            new Em3dGroup(sh.Name, ++attr, 2, Em3dGroupKind.Sheet, 1, AtLeast: true, sh.Material,
                          ThicknessM: sh.ThicknessM, Terminal: terminalOf.GetValueOrDefault(sh.Name))).ToList();
        groups.AddRange(sheetGroups);

        // A port sheet is AT LEAST one surface: the fragment makes it conformal with every volume it
        // passes through, so a sheet crossing an interface between two slabs (prepreg on core, a trace
        // over an inner-layer plane) comes back as one piece per slab. The query is the sheet's own
        // zero-thickness box, so only coplanar pieces inside the rectangle can match — the sheet itself.
        // brief-em3d-115 R-em3d115-2a — a multi-terminal wave port's terminals share one rectangle (brief 114 validates it), so
        // the face is ONE surface and one attribute, claimed once, under its lowest-numbered terminal: every other terminal of
        // the group has no surface of its own (the configuration puts its entry on the same attribute). A problem with no
        // group keeps every port, and its script its bytes.
        var portIndex = Enumerable.Range(0, problem.Ports.Count).Where(k => !SharesAnotherTerminalsFace(problem, problem.Ports[k])).ToList();
        var portGroups = portIndex.Select(k => problem.Ports[k]).Select(p =>
            new Em3dGroup(p.Name, ++attr, 2, Em3dGroupKind.Port, 1, AtLeast: true, PortNumber: p.Number)).ToList();
        groups.AddRange(portGroups);

        // brief-em3d-23 — a face that a wave port or a conductor covers whole keeps no surface of its own:
        // it expects none, and the configuration names no attribute for it.
        var faceGroups = FaceKeys.Select((key, k) =>
            new Em3dGroup(Em3dAirBox.FaceName(key), ++attr, 2, Em3dGroupKind.Face, Covered(problem, key) ? 0 : 1,
                          AtLeast: !Covered(problem, key), Boundary: faceKinds[k])).ToList();
        groups.AddRange(faceGroups);

        // brief-em3d-49 R-em3d49-4c — a face boundary is recovered by its pieces' bounding boxes and counted EXACTLY:
        // a box that would also take a neighbour's coplanar surface is refused here, before Gmsh, and a count that
        // still comes back different is refused by the entity check. Never a guess. None present: nothing below
        // changes by a byte.
        var faceBoundaries = problem.FaceBoundaryPieces();
        if (CoplanarNeighbour(problem, faceBoundaries) is { } clash) return No(clash);
        var boundaryGroups = faceBoundaries.Select(fb =>
            new Em3dGroup(fb.Boundary.Name, ++attr, 2, Em3dGroupKind.FaceBoundary, fb.Pieces.Count, AtLeast: false,
                          fb.Boundary.Kind == Em3dFaceBoundaryKind.Conductive ? fb.Boundary.Material : null,
                          Boundary: fb.Boundary.Kind == Em3dFaceBoundaryKind.Pec ? Em3dBoundaryKind.Pec : null)).ToList();
        groups.AddRange(boundaryGroups);

        // ── The script ───────────────────────────────────────────────────────────────────────
        var g = new StringBuilder();
        void L(string line = "") => g.Append(line).Append('\n');

        L("// Generated by circuitRF for Palace (brief-em3d-7). Do not edit: circuitRF rewrites this file from");
        L("// the setup, and an unchanged file reuses the mesh beside it. Units: micrometres (Palace L0 = 1e-6).");
        L("// Run by circuitRF as:  gmsh model.geo -3 -o model.msh    (writes entities.txt beside it)");
        L();
        L("SetFactory(\"OpenCASCADE\");");
        L("Geometry.OCCBooleanPreserveNumbering = 1;");
        L("Geometry.OCCBoundsUseStl = 1;");
        L($"e = {Num(Eps)};");
        L();

        L("// ---- solids, in construction order -------------------------------------------------------");
        for (int i = 0; i < problem.Solids.Count; i++)
        {
            var s = problem.Solids[i];
            L($"// {Comment(s.Name)}: {s.Role}, {Comment(s.Material)}, order {s.Order}");
            EmitPrimitive(g, $"s{i}", s.Primitive);
        }
        L();
        // brief-em3d-65 R-em3d65-2a — each kernel solid's B-rep, written beside the script under its hash.
        var kernelFiles = problem.Solids.Select(x => x.Primitive).OfType<Em3dShapeSolid>()
                                 .Select(k => new Em3dKernelFile(KernelFileName(k), k.Brep.ToArray()))
                                 .DistinctBy(x => x.FileName).ToList();

        L("// ---- disjoint by construction order: each solid loses what a higher-order solid takes -----");
        var bounds = problem.Solids.Select(s => Em3dProblem.Bounds(s.Primitive)).ToList();
        // 3D editor round 3 — and metal over dielectric whatever the order (em-3d.md §6.3a). Said in the script only when it
        // changed something, so a problem that already obeyed it is the same bytes.
        var precedence = Em3dPrecedence.Of(problem);
        if (precedence.Shift > 0) L("// metal takes precedence over dielectric: every metal is ranked above every dielectric (em-3d.md §6.3a)");
        for (int i = 0; i < problem.Solids.Count; i++)
        {
            var tools = new List<int>();
            int pi = precedence.Of(problem.Solids[i]);
            for (int j = 0; j < problem.Solids.Count; j++)
            {
                if (j == i) continue;
                int pj = precedence.Of(problem.Solids[j]);
                bool higher = pj > pi || (pj == pi && j > i);
                if (higher && Overlap(bounds[i], bounds[j])) tools.Add(j);
            }
            if (tools.Count == 0) continue;
            L($"s{i}[] = BooleanDifference{{ Volume{{s{i}[]}}; Delete; }}{{ Volume{{{string.Join(", ", tools.Select(j => $"s{j}[]"))}}}; }};");
        }
        L();

        var box = problem.Boundary;
        L("// ---- the air box, and the background: what no solid claims inside it --------------------");
        L($"bx = newv; Box(bx) = {{{Um(box.Min.X)}, {Um(box.Min.Y)}, {Um(box.Min.Z)}, " +
          $"{Um(box.Max.X - box.Min.X)}, {Um(box.Max.Y - box.Min.Y)}, {Um(box.Max.Z - box.Min.Z)}}};");
        string allSolids = string.Join(", ", Enumerable.Range(0, problem.Solids.Count).Select(i => $"s{i}[]"));
        // brief-em3d-137 — ONE difference with every solid as a tool. Cut one solid at a time (859dda96), every step
        // re-processed the whole background, cavities and all: 580 s of a ten-layer board's 650 s of geometry, against
        // 8 s for this. The wrong region that chaining was meant to avoid was a plane's clockwise hole (PlanarRings).
        // With no solid there is nothing to cut, and Gmsh takes an empty tool list as a syntax error: a document of sheets alone.
        L(problem.Solids.Count == 0 ? "bg[] = {bx};" : $"bg[] = BooleanDifference{{ Volume{{bx}}; Delete; }}{{ Volume{{{allSolids}}}; }};");
        var conductors = Enumerable.Range(0, problem.Solids.Count)
                                   .Where(i => problem.Solids[i].Role == Em3dRole.Conductor).ToList();
        if (conductors.Count > 0)
        {
            L("// Conductors are voids: their surfaces carry Palace's conductivity boundary.");
            L($"Recursive Delete {{ Volume{{{string.Join(", ", conductors.Select(i => $"s{i}[]"))}}}; }}");
        }
        L();

        L("// ---- sheets and port sheets --------------------------------------------------------------");
        for (int k = 0; k < problem.Sheets.Count; k++)
        {
            var sh = problem.Sheets[k];
            L($"// sheet {Comment(sh.Name)}: {Comment(sh.Material)}, {Num(sh.ThicknessM * 1e6)} um thick");
            EmitPlanar(g, $"h{k}", [sh.Outline, .. sh.Holes], sh.World);
        }
        foreach (int k in portIndex)
        {
            var p = problem.Ports[k];
            L($"// {Comment(p.Name)}: from {Comment(p.NegativeObject)} to {Comment(p.PositiveObject)}");
            if (p.FaceGroup is { } fg)
                L($"// ... the face of port {Comment(p.FaceGroupLabel ?? "")}'s terminals " +
                  string.Join(", ", problem.Ports.Where(q => q.FaceGroup == fg).OrderBy(q => q.Number).Select(q => q.Number.ToString(CultureInfo.InvariantCulture))));
            if (p.Annulus is { } ring) EmitAnnulus(g, $"p{k}", p, ring);
            else if (p.Quad is { } quad) EmitCorners(g, $"p{k}", quad.Corners);
            else EmitRectangle(g, $"p{k}", p.Min, p.Max);
        }
        L();

        var meshed = Enumerable.Range(0, problem.Solids.Count).Where(i => problem.Solids[i].Role != Em3dRole.Conductor).ToList();
        var surfaces = Enumerable.Range(0, problem.Sheets.Count).Select(k => $"h{k}[]")
                                 .Concat(portIndex.Select(k => $"p{k}[]")).ToList();
        L("// ---- one fragment: imprints the shared faces and the sheets, splits no volume -------------");
        L($"frag[] = BooleanFragments{{ Volume{{{string.Join(", ", meshed.Select(i => $"s{i}[]").Append("bg[]"))}}}; Delete; }}" +
          (surfaces.Count == 0 ? "{ };" : $"{{ Surface{{{string.Join(", ", surfaces)}}}; Delete; }};"));
        L("allV[] = Volume{:};");
        L("allS[] = Surface{:};");
        EmitSplitVolumes(g, meshed, bounds, box.Min, box.Max);
        L("single[] = CombinedBoundary{ Volume{allV[]}; };");
        L("For k In {0:#single[]-1}");
        L("  single[k] = Abs(single[k]);");
        L("EndFor");
        L("claimed[] = {};");
        L();

        L("// ---- surfaces, recovered by what circuitRF placed; each face claimed once ------------------");
        var faceQuery = new[]
        {
            (box.Min.X, box.Min.Y, box.Min.Z, box.Min.X, box.Max.Y, box.Max.Z),
            (box.Max.X, box.Min.Y, box.Min.Z, box.Max.X, box.Max.Y, box.Max.Z),
            (box.Min.X, box.Min.Y, box.Min.Z, box.Max.X, box.Min.Y, box.Max.Z),
            (box.Min.X, box.Max.Y, box.Min.Z, box.Max.X, box.Max.Y, box.Max.Z),
            (box.Min.X, box.Min.Y, box.Min.Z, box.Max.X, box.Max.Y, box.Min.Z),
            (box.Min.X, box.Min.Y, box.Max.Z, box.Max.X, box.Max.Y, box.Max.Z),
        };
        // brief-dfr26-8 R-dfr26-8-4 — a lumped port whose sheet lies IN an air-box face: an edge port at a board edge the
        // box meets with no padding (a cropped board's). Claimed after the faces, the face took its sheet and the port
        // selected nothing.
        bool OnBoxFace(Em3dPort p)
        {
            const double tol = 1e-9;
            static bool Flat(double lo, double hi, double boxLo, double boxHi)
                => hi - lo <= tol && (Math.Abs(lo - boxLo) <= tol || Math.Abs(hi - boxHi) <= tol);
            return Flat(p.Min.X, p.Max.X, box.Min.X, box.Max.X) || Flat(p.Min.Y, p.Max.Y, box.Min.Y, box.Max.Y)
                || Flat(p.Min.Z, p.Max.Z, box.Min.Z, box.Max.Z);
        }
        // A turned port's query box is wider than its sheet, so it is claimed after every upright port: a neighbour's sheet
        // inside that box is the neighbour's. With no turned port the order, and the script, are as they were.
        var claimOrder = portIndex.OrderBy(k => problem.Ports[k].Quad is null ? 0 : 1).ToList();
        void ClaimPorts(Func<Em3dPort, bool> which)
        {
            foreach (int k in claimOrder)
            {
                var p = problem.Ports[k];
                if (!which(p)) continue;
                L($"// {Comment(p.Name)}");
                if (p.Kind == Em3dPortKind.Wave)
                {
                    // brief-em3d-23 — a wave port's rectangle crosses the line's own end, and the line is a void:
                    // the piece over it bounds no volume, and a boundary element that is no element's face is
                    // an MFEM abort (STable3D). So a wave port keeps only faces bounding the meshed space, and the
                    // piece it leaves is marked claimed too, or the face's own query (next) would take it instead.
                    L($"q{k}[] = {Query((p.Min.X, p.Min.Y, p.Min.Z, p.Max.X, p.Max.Y, p.Max.Z), 0)};");
                    L($"x[] = q{k}[];");
                    L("x[] -= single[];");
                    L($"q{k}[] -= x[];");
                    L($"q{k}[] -= claimed[];");
                    L($"claimed[] += q{k}[];");
                    L("claimed[] += x[];");
                }
                else Claim(g, $"q{k}", [Query((p.Min.X, p.Min.Y, p.Min.Z, p.Max.X, p.Max.Y, p.Max.Z), 0)], []);
            }
        }
        // brief-em3d-22 — a static problem's source sheets are claimed BEFORE the air-box faces, so a
        // coaxial source can lie on the face its line ends at (Palace's own way to feed a coax). A driven
        // problem keeps its order, and its script its bytes. brief-em3d-23 — a WAVE port lies on an air-box
        // face by definition, so it is claimed before the faces too.
        bool Lumped(Em3dPort p) => p.Kind != Em3dPortKind.Wave;
        if (problem.IsStatic) ClaimPorts(_ => true);
        else
        {
            if (problem.HasWavePorts) ClaimPorts(p => !Lumped(p));
            ClaimPorts(p => Lumped(p) && OnBoxFace(p));
        }
        // brief-em3d-49 — a face boundary is claimed before the air-box faces, so one lying ON the box (a cavity whose
        // walls are its dielectric's faces) keeps its own surfaces.
        for (int k = 0; k < faceBoundaries.Count; k++)
        {
            L($"// face boundary {Comment(faceBoundaries[k].Boundary.Name)}: {faceBoundaries[k].Boundary.Kind}");
            Claim(g, $"b{k}", [.. faceBoundaries[k].Pieces.Select(pc => Query(pc.Bounds(), 0))], []);
        }
        for (int k = 0; k < 6; k++)
        {
            L($"// air box {FaceKeys[k]}");
            Claim(g, $"f{k}", [Query(faceQuery[k], 0)], []);
        }
        if (!problem.IsStatic) ClaimPorts(p => Lumped(p) && !OnBoxFace(p));
        // Sheets: smallest bounding box first, for the same reason the conductors below are ordered that
        // way. A sheet is recovered by the surfaces inside its WORLD bound, and one sheet's box can sit
        // entirely inside another's on the same plane (a piece of a winding inside the box of the whole
        // winding). Claimed in document order, the big box takes the small sheet's faces and that sheet
        // selects nothing; claimed smallest first, every sheet gets its own and the big one gets the rest.
        var sheetOrder = Enumerable.Range(0, problem.Sheets.Count)
            .OrderBy(k => SheetSpan(WorldBounds(problem.Sheets[k], problem.Sheets[k].Outline)))
            .ThenBy(k => k)
            .ToList();
        foreach (int k in sheetOrder)
        {
            var sh = problem.Sheets[k];
            // brief-em3d-42 — a sheet in any plane is recovered by its WORLD bound; a horizontal one's is
            // its plan bound at its height, exactly as before.
            var holes = sh.Holes.Select(h => Query(WorldBounds(sh, h), 0)).ToList();
            L($"// sheet {Comment(sh.Name)}");
            Claim(g, $"w{k}", [Query(WorldBounds(sh, sh.Outline), 0)], holes);
        }
        // Conductors: only single-sided faces, smallest bounding box first.
        L("voids[] = single[];");
        L("voids[] -= claimed[];");
        var conductorOrder = conductors
            .OrderBy(i => Volume(bounds[i]))
            .ThenBy(i => i)
            .ToList();
        foreach (int i in conductorOrder)
        {
            var s = problem.Solids[i];
            L($"// conductor {Comment(s.Name)}");
            // brief-em3d-65 R-em3d65-2b — a kernel solid's faces by the kernel's own tight box per face, not one box
            // around the whole solid: a pin standing in a lid's bore lies inside the lid's box, and outside its faces'.
            if (s.Primitive is Em3dShapeSolid kernel && kernel.Faces.Count > 0)
            {
                L($"c{i}[] = {Query(kernel.Faces[0].Box, 0)};");
                foreach (var face in kernel.Faces.Skip(1)) L($"c{i}[] += {Query(face.Box, 0)};");
            }
            else L($"c{i}[] = {Query(bounds[i], Margin(s.Primitive))};");
            L($"x[] = c{i}[];");
            L("x[] -= voids[];");
            L($"c{i}[] -= x[];");
            L($"c{i}[] -= claimed[];");
            L($"claimed[] += c{i}[];");
        }
        // brief-dfr26-1 — conductors that touch: where two share a plane (a via's end flush with the plane it lands on),
        // the fragment can leave ONE face across both, and when one sticks out past the other's edge that face lies in
        // neither box — every conductor face was found, and this one was left a magnetic wall and refused. What is left of
        // the voids inside a touching group's joint box is that group's metal; it goes to the group's largest member.
        foreach (var group in TouchingConductors(conductors, bounds))
        {
            var joint = group.Select(i => bounds[i]).Aggregate((a, b) => (Math.Min(a.X0, b.X0), Math.Min(a.Y0, b.Y0),
                Math.Min(a.Z0, b.Z0), Math.Max(a.X1, b.X1), Math.Max(a.Y1, b.Y1), Math.Max(a.Z1, b.Z1)));
            int owner = group.OrderByDescending(i => Volume(bounds[i])).ThenBy(i => i).First();
            L($"// conductors {string.Join(", ", group.Select(i => Comment(problem.Solids[i].Name)))}, touching: what their boxes left");
            L($"y[] = {Query(joint, 0)};");
            L("x[] = y[];");
            L("x[] -= voids[];");
            L("y[] -= x[];");
            L("y[] -= claimed[];");
            L($"c{owner}[] += y[];");
            L("claimed[] += y[];");
        }
        L("rest[] = single[];");
        L("rest[] -= claimed[];");
        L();

        L("// ---- physical groups: attributes are circuitRF's (groups.json) --------------------------");
        string List(Em3dGroup gr, int index) => gr.Kind switch
        {
            Em3dGroupKind.Volume     => $"s{index}[]",
            Em3dGroupKind.Conductor  => $"c{index}[]",
            _                        => "",
        };
        for (int i = 0; i < problem.Solids.Count; i++)
        {
            var gr = solidGroup[i];
            L(gr.Dimension == 3
                ? $"Physical Volume(\"{PhysicalName(gr.Name)}\", {gr.Attribute}) = {{{List(gr, i)}}};"
                : $"Physical Surface(\"{PhysicalName(gr.Name)}\", {gr.Attribute}) = {{{List(gr, i)}}};");
        }
        L($"Physical Volume(\"{BackgroundName}\", {background.Attribute}) = {{bg[]}};");
        for (int k = 0; k < sheetGroups.Count; k++)
            L($"Physical Surface(\"{PhysicalName(sheetGroups[k].Name)}\", {sheetGroups[k].Attribute}) = {{w{k}[]}};");
        for (int k = 0; k < portGroups.Count; k++)
            L($"Physical Surface(\"{PhysicalName(portGroups[k].Name)}\", {portGroups[k].Attribute}) = {{q{portIndex[k]}[]}};");
        for (int k = 0; k < 6; k++)
            L($"Physical Surface(\"{PhysicalName(faceGroups[k].Name)}\", {faceGroups[k].Attribute}) = {{f{k}[]}};");
        for (int k = 0; k < boundaryGroups.Count; k++)
            L($"Physical Surface(\"{PhysicalName(boundaryGroups[k].Name)}\", {boundaryGroups[k].Attribute}) = {{b{k}[]}};");
        L();

        L("// ---- the entity table circuitRF checks before it believes this mesh (R-em3d7-3b) ---------");
        L($"Printf(\"# circuitRF entity table: group <attribute> <count> <volume tags lost in the fragment>\") > \"{EntitiesFile}\";");
        string classifiedVolumes = string.Join(" + ", meshed.Select(i => $"#s{i}[]").Append("#bg[]"));
        for (int i = 0; i < problem.Solids.Count; i++)
        {
            var gr = solidGroup[i];
            if (gr.Dimension == 3)
            {
                L($"lost[] = s{i}[];");
                L("lost[] -= allV[];");
                L($"Printf(\"group {gr.Attribute} %g %g\", #s{i}[], #lost[]) >> \"{EntitiesFile}\";");
            }
            else L($"Printf(\"group {gr.Attribute} %g 0\", #c{i}[]) >> \"{EntitiesFile}\";");
        }
        L("lost[] = bg[];");
        L("lost[] -= allV[];");
        L($"Printf(\"group {background.Attribute} %g %g\", #bg[], #lost[]) >> \"{EntitiesFile}\";");
        for (int k = 0; k < sheetGroups.Count; k++)
            L($"Printf(\"group {sheetGroups[k].Attribute} %g 0\", #w{k}[]) >> \"{EntitiesFile}\";");
        for (int k = 0; k < portGroups.Count; k++)
            L($"Printf(\"group {portGroups[k].Attribute} %g 0\", #q{portIndex[k]}[]) >> \"{EntitiesFile}\";");
        for (int k = 0; k < 6; k++)
            L($"Printf(\"group {faceGroups[k].Attribute} %g 0\", #f{k}[]) >> \"{EntitiesFile}\";");
        for (int k = 0; k < boundaryGroups.Count; k++)
            L($"Printf(\"group {boundaryGroups[k].Attribute} %g 0\", #b{k}[]) >> \"{EntitiesFile}\";");
        // brief-em3d-65 R-em3d65-2a — a kernel file read as anything but one volume is named by the check.
        for (int i = 0; i < problem.Solids.Count; i++)
            if (problem.Solids[i].Primitive is Em3dShapeSolid)
                L($"If (ns{i} != 1) Printf(\"kernel_import_count {solidGroup[i].Attribute} %g\", ns{i}) >> \"{EntitiesFile}\"; EndIf");
        L($"Printf(\"all_volumes %g\", #allV[]) >> \"{EntitiesFile}\";");
        L($"Printf(\"classified_volumes %g\", {classifiedVolumes}) >> \"{EntitiesFile}\";");
        L($"Printf(\"all_surfaces %g\", #allS[]) >> \"{EntitiesFile}\";");
        L($"Printf(\"single_sided %g\", #single[]) >> \"{EntitiesFile}\";");
        L($"Printf(\"unclassified_single_sided %g\", #rest[]) >> \"{EntitiesFile}\";");
        L();

        // brief-dfr31-6 R-dfr31-6-4 — Gmsh names a failing surface by its tag; this table is how the refusal names the
        // object instead. One line per group, so the file is opened once per group rather than once per surface.
        L("// ---- the face table: whose each surface is, so a mesher error can name the object (brief-dfr31-6) ----");
        L($"Printf(\"# circuitRF face table: f <attribute> <its surfaces> | v <attribute> <the surfaces of its volumes> | b <surface> <x0 y0 z0 x1 y1 z1>;...\") > \"{FacesFile}\";");
        L("Macro CrfFaces");
        L("  crft = \"\";");
        L("  For crfk In {0:#crfl[]-1}");
        L("    crft = StrCat(crft, Sprintf(\" %g\", Fabs(crfl[crfk])));");
        L("  EndFor");
        L($"  Printf(StrCat(crfh, crft)) >> \"{FacesFile}\";");
        L("Return");
        void Faces(string kind, int attribute, string list)
        {
            // brief-dfr35-1 R-dfr35-1-9 — a tag no volume has is left out: this table is diagnostic, and a Boundary{} on one is an
            // error that stops the run, ahead of the entity check that names what was lost.
            if (kind == "v")
            {
                L($"crfv[] = {{{list}}};");
                L("crfg[] = crfv[];");
                L("crfg[] -= allV[];");
                L("crfv[] -= crfg[];");
                L("crfl[] = {};");
                L("If (#crfv[])");
                L("  crfl[] = Boundary{ Volume{crfv[]}; };");
                L("EndIf");
            }
            else L($"crfl[] = {{{list}}};");
            L($"crfh = \"{kind} {attribute}\";");
            L("Call CrfFaces;");
        }
        for (int i = 0; i < problem.Solids.Count; i++)
            if (solidGroup[i].Dimension == 3) Faces("v", solidGroup[i].Attribute, $"s{i}[]");
            else Faces("f", solidGroup[i].Attribute, $"c{i}[]");
        Faces("v", background.Attribute, "bg[]");
        for (int k = 0; k < sheetGroups.Count; k++) Faces("f", sheetGroups[k].Attribute, $"w{k}[]");
        for (int k = 0; k < portGroups.Count; k++) Faces("f", portGroups[k].Attribute, $"q{portIndex[k]}[]");
        for (int k = 0; k < 6; k++) Faces("f", faceGroups[k].Attribute, $"f{k}[]");
        for (int k = 0; k < boundaryGroups.Count; k++) Faces("f", boundaryGroups[k].Attribute, $"b{k}[]");
        // brief-dfr35-1 R-dfr35-1-8 — where each surface is, so a refusal can say where. Measured on a 3,705-surface board:
        // 0.3 s on a 23.4 s parse, against parsing the script a second time (23 s) to ask only after a failure. It is written
        // in batches, so the file is opened about once per SurfaceBoxBatch surfaces.
        L($"crfb = \"b\";");
        L("For crfk In {0:#allS[]-1}");
        L("  crfbb[] = BoundingBox Surface{allS[crfk]};");
        L("  crfb = StrCat(crfb, Sprintf(\" %g %.1f %.1f %.1f %.1f %.1f %.1f;\", allS[crfk], crfbb[0], crfbb[1], crfbb[2], crfbb[3], crfbb[4], crfbb[5]));");
        L($"  If (crfk % {SurfaceBoxBatch} == {SurfaceBoxBatch - 1})");
        L($"    Printf(crfb) >> \"{FacesFile}\";");
        L("    crfb = \"b\";");
        L("  EndIf");
        L("EndFor");
        L($"Printf(crfb) >> \"{FacesFile}\";");
        L();

        // ── Mesh size (R-em3d7-2e): an initial mesh only ─────────────────────────────────────
        double fMax = SizingFrequencyHz(problem);
        // brief-em3d-22 — a static solve has no wavelength: its largest element is the same fraction of
        // the air box's largest side, in every material.
        double boxSide = Math.Max(box.Max.X - box.Min.X, Math.Max(box.Max.Y - box.Min.Y, box.Max.Z - box.Min.Z));
        double SizeUm(string material) => (problem.IsStatic ? StaticMaxElementSizeM(boxSide, settings)
                                                            : MaxElementSizeM(materials[material], fMax, settings)) * 1e6;
        var volumeSizes = new List<(string List, double Size)>();
        for (int i = 0; i < problem.Solids.Count; i++)
            if (solidGroup[i].Dimension == 3) volumeSizes.Add(($"s{i}[]", SizeUm(problem.Solids[i].Material)));
        volumeSizes.Add(("bg[]", SizeUm(backgroundMaterial)));
        double sizeMax = volumeSizes.Max(v => v.Size);
        double sizeEdge = settings.EdgeRefinement * volumeSizes.Min(v => v.Size);
        // The PORTS get a floor of their own: a lumped port's field is only what Palace assumes if its
        // sheet is resolved, so its smaller side gets PortCellsAcross elements whatever EdgeRefinement
        // says. F0 sized its hand meshes the same way (case A: 15 µm at 100 µm ports; case B: 60 µm at
        // a 200 µm port height). Applied around the port sheets only, so EdgeRefinement stays the knob
        // everywhere else — on case B, applying it to every conductor put 50 µm elements over the whole
        // ground plane and took a minute and a half to mesh.
        double sizePort = problem.Ports.Count == 0 ? sizeEdge
                        : Math.Min(sizeEdge, problem.Ports.Min(SmallerSide) * 1e6 / PortCellsAcross);
        double DistMax(double near) => near + (sizeMax - near) / (settings.Grading - 1);

        L("// ---- the initial mesh: Palace's adaptive refinement converges the answer ------------------");
        L(problem.IsStatic
            ? $"// Largest element: {Num(settings.MaxElementWavelengths)} of the air box's largest side (a static solve has no wavelength);"
            : $"// Largest element per material: {Num(settings.MaxElementWavelengths)} of its wavelength at {Num(fMax / 1e9)} GHz" +
              (problem.Type == Em3dProblemType.Eigenmode ? " (twice the eigenmode target);" : ";"));
        L($"// {Num(settings.EdgeRefinement)} of the smallest at conductors and sheets; at most 1/{PortCellsAcross} of each port");
        L($"// sheet's smaller side at the ports; growing by {Num(settings.Grading)}.");
        L($"Mesh.MeshSizeMax = {Num(Round(sizeMax))};");
        L("Mesh.MeshSizeFromPoints = 0;");
        L("Mesh.MeshSizeExtendFromBoundary = 0;");
        L($"Mesh.MeshSizeFromCurvature = {CurvatureElements};");
        int field = 0;
        var fields = new List<int>();
        foreach (var (list, size) in volumeSizes)
        {
            field++;
            fields.Add(field);
            L($"Field[{field}] = Constant; Field[{field}].VIn = {Num(Round(size))}; Field[{field}].VolumesList = {{{list}}};");
        }
        void Refine(IReadOnlyList<string> surfaces, double near)
        {
            if (surfaces.Count == 0) return;
            int d = ++field, t = ++field;
            fields.Add(t);
            L($"Field[{d}] = Distance; Field[{d}].SurfacesList = {{{string.Join(", ", surfaces)}}}; Field[{d}].Sampling = 50;");
            L($"Field[{t}] = Threshold; Field[{t}].InField = {d}; Field[{t}].SizeMin = {Num(Round(near))}; " +
              $"Field[{t}].SizeMax = {Num(Round(sizeMax))}; Field[{t}].DistMin = {Num(Round(near))}; " +
              $"Field[{t}].DistMax = {Num(Round(DistMax(near)))};");
        }
        Refine([.. conductors.Select(i => $"c{i}[]"), .. Enumerable.Range(0, problem.Sheets.Count).Select(k => $"w{k}[]"),
                .. Enumerable.Range(0, boundaryGroups.Count).Select(k => $"b{k}[]")], sizeEdge);
        Refine([.. portIndex.Select(k => $"q{k}[]")], sizePort);
        // brief-em3d-74 R-em3d74-4b — each mesh region a Box field in the same Min; none present, not a byte changes.
        foreach (var region in problem.MeshRegions)
            L(BoxField(++field, region, sizeMax, settings.Grading, fields));
        int min = ++field;
        L($"Field[{min}] = Min; Field[{min}].FieldsList = {{{string.Join(", ", fields)}}};");
        L($"Background Field = {min};");
        // Second-order elements, saved as MSH 2.2 binary — the format the validated Palace read in F0.
        // brief-emb-2 R-emb2-1 — CURVED and optimised only when the geometry is genuinely curved: F0 chose HighOrderOptimize = 2
        // on case A's round bond wire, where HighOrderOptimize = 1 left three negative-Jacobian elements. Layout artwork's only
        // curves are its drilled barrels, already faceted, and on an imported board (330k nodes) the elastic optimisation ran
        // 17+ minutes on one core where straight-sided order 2 meshed in 10-30 s; so straight sides there, and for the restart
        // after PalaceSettings.MeshCurvingMinutes.
        L("Mesh.ElementOrder = 2;");
        L(Curved(problem, settings) ? CurvingLine : StraightSidedLine);
        L("Mesh.MshFileVersion = 2.2;");
        L("Mesh.Binary = 1;");

        return new GmshLowering(g.ToString(), groups, GroupsJson(groups), null, kernelFiles);
    }

    /// <summary>
    /// brief-em3d-74 R-em3d74-4b — a mesh region as a Gmsh <c>Box</c> field: <c>VIn</c> its size, <c>VOut</c> the script's
    /// largest, and a transition <c>Thickness</c> over which the size may grow from one to the other at the grading's rate,
    /// (VOut − VIn)/(grading − 1). Adds the field to <paramref name="fields"/> (the Min field's list).
    /// </summary>
    private static string BoxField(int field, Em3dMeshRegion region, double sizeMaxUm, double defaultGrading, List<int> fields)
    {
        fields.Add(field);
        double vin = Math.Min(region.SizeM * 1e6, sizeMaxUm);
        double grading = region.Grading is { } gr && gr > 1 ? gr : defaultGrading;
        double thickness = (sizeMaxUm - vin) / (grading - 1);
        return $"Field[{field}] = Box; Field[{field}].VIn = {Num(Round(vin))}; Field[{field}].VOut = {Num(Round(sizeMaxUm))}; " +
               $"Field[{field}].XMin = {Um(region.Min.X)}; Field[{field}].XMax = {Um(region.Max.X)}; " +
               $"Field[{field}].YMin = {Um(region.Min.Y)}; Field[{field}].YMax = {Um(region.Max.Y)}; " +
               $"Field[{field}].ZMin = {Um(region.Min.Z)}; Field[{field}].ZMax = {Um(region.Max.Z)}; " +
               $"Field[{field}].Thickness = {Num(Round(thickness))};  // mesh region {Comment(region.Name)}";
    }

    /// <summary>brief-emb-2 R-emb2-1 — the high-order curving line the script writes: <c>HighOrderOptimize = 2</c> when
    /// <see cref="Curved"/>, otherwise <c>SecondOrderLinear = 1</c>. What a test and the cap's restart look for.</summary>
    public const string CurvingLine = "Mesh.HighOrderOptimize = 2;", StraightSidedLine = "Mesh.SecondOrderLinear = 1;";

    /// <summary>
    /// brief-emb-2 R-emb2-1 — whether Gmsh curves and optimises the second-order elements: true when a solid has a curved
    /// surface that is not one of the problem's <see cref="Em3dProblem.ArtworkBarrels"/> (a <c>.c3d</c> cylinder, sphere, ellipsoid,
    /// round bond wire or curved kernel/STEP face), and <paramref name="settings"/> does not ask for straight sides.
    /// </summary>
    public static bool Curved(Em3dProblem problem, PalaceSettings settings)
    {
        if (settings.StraightSided) return false;
        var barrels = problem.ArtworkBarrels as IReadOnlySet<string> ?? problem.ArtworkBarrels.ToHashSet(StringComparer.Ordinal);
        return problem.Solids.Any(s => !barrels.Contains(s.Name) && s.Primitive switch
        {
            Em3dCylinder or Em3dSphere or Em3dTruncatedSphere or Em3dEllipsoid => true,
            Em3dSweep w => w.Section == Em3dSection.Circle,
            Em3dShapeSolid k => k.Faces.Count == 0 || k.Faces.Any(f => Em3dShapeSolid.IsCurved(f.Kind)),
            _ => false,
        });
    }

    /// <summary>brief-emb-2 R-emb2-1 — whether <paramref name="lowering"/>'s script curves its second-order elements.</summary>
    public static bool Curves(GmshLowering lowering) => lowering.Geo?.Contains(CurvingLine, StringComparison.Ordinal) == true;

    /// <summary>brief-em3d-65 R-em3d65-2a — the file a kernel solid's B-rep is handed to Gmsh in: named by its hash, so the
    /// script names it byte-deterministically and an unchanged solid reuses the mesh beside it.</summary>
    public static string KernelFileName(Em3dShapeSolid k) => $"kernel-{k.BrepHash[..Math.Min(16, k.BrepHash.Length)]}.brep";

    /// <summary>
    /// brief-em3d-65 R-em3d65-4c — the smallest element size, metres, the script asks for anywhere but a curved face's own
    /// curvature sizing: the conductor and sheet refinement (<c>EdgeRefinement</c> times the smallest volume's size).
    /// </summary>
    public static double SmallestRequestedSizeM(Em3dProblem problem, PalaceSettings settings)
    {
        var materials = problem.Materials.ToDictionary(m => m.Name, StringComparer.Ordinal);
        string backgroundMaterial = problem.Boundary.Material ?? BackgroundMaterial(problem.Solids, problem.Materials);
        if (!materials.ContainsKey(backgroundMaterial)) materials[backgroundMaterial] = FreeSpace;
        var box = problem.Boundary;
        double boxSide = Math.Max(box.Max.X - box.Min.X, Math.Max(box.Max.Y - box.Min.Y, box.Max.Z - box.Min.Z));
        double fMax = SizingFrequencyHz(problem);
        double Size(string material) => problem.IsStatic ? StaticMaxElementSizeM(boxSide, settings)
                                                         : MaxElementSizeM(materials[material], fMax, settings);
        double least = Size(backgroundMaterial);
        foreach (var s in problem.Solids)
            if (s.Role != Em3dRole.Conductor && materials.ContainsKey(s.Material)) least = Math.Min(least, Size(s.Material));
        return settings.EdgeRefinement * least;
    }

    /// <summary>
    /// The frequency a problem's initial mesh is sized at: the sweep's top — or, for an eigenmode problem,
    /// which has no sweep, <b>twice its target</b>: the modes it finds lie above the target, and the first
    /// few of a cavity lie within an octave of the fundamental. A driven setup may state another frequency to size at
    /// (<see cref="Em3dProblem.MeshFrequencyHz"/>): EMerge's script sizes from this too.
    /// </summary>
    public static double SizingFrequencyHz(Em3dProblem problem)
        => problem.Type == Em3dProblemType.Eigenmode ? 2 * problem.EigenmodeTargetHz
         : problem.MeshFrequencyHz is { } stated and > 0 ? stated : problem.Frequency.StopHz;

    /// <summary>
    /// brief-em3d-23 — true when nothing of face <paramref name="key"/> is left to be a boundary of the
    /// meshed space: a wave port's rectangle covers it whole (a waveguide fed across its full cross-section),
    /// or a conductor's bound reaches it and covers it whole (a cavity whose metal walls ARE the box), in which
    /// case the face is the conductor's outside and is deleted with it.
    /// </summary>
    public static bool Covered(Em3dProblem problem, string key)
    {
        var b = problem.Boundary;
        int axis = key[0] - 'x';
        bool high = key.EndsWith("max", StringComparison.Ordinal);
        double at = high ? Get(b.Max, axis) : Get(b.Min, axis);
        double tol = 1e-9 * Math.Max(1.0, Math.Max(b.Max.X - b.Min.X, Math.Max(b.Max.Y - b.Min.Y, b.Max.Z - b.Min.Z)));
        bool Spans(double x0, double y0, double z0, double x1, double y1, double z1)
        {
            double[] lo = [x0, y0, z0], hi = [x1, y1, z1];
            if (high ? hi[axis] < at - tol : lo[axis] > at + tol) return false;
            for (int a = 0; a < 3; a++)
                if (a != axis && (lo[a] > Get(b.Min, a) + tol || hi[a] < Get(b.Max, a) - tol)) return false;
            return true;
        }
        foreach (var p in problem.Ports)
            if (p.Kind == Em3dPortKind.Wave && problem.FaceOf(p.Min, p.Max) == key &&
                Spans(p.Min.X, p.Min.Y, p.Min.Z, p.Max.X, p.Max.Y, p.Max.Z))
                return true;
        // brief-em3d-52 — or conductor boxes cover it TOGETHER: a drawn package's side is its floor's edge, its wall and
        // its lid's edge, three boxes, none of which spans the face alone. The face's rectangle is cut at every box edge
        // on it, and each cell must lie inside a box that reaches the face.
        int u = axis == 0 ? 1 : 0, v = axis == 2 ? 1 : 2;
        var rects = new List<(double U0, double V0, double U1, double V1)>();
        foreach (var s in problem.Solids)
        {
            if (s.Role != Em3dRole.Conductor || s.Primitive is not Em3dBox) continue;
            var (x0, y0, z0, x1, y1, z1) = Em3dProblem.Bounds(s.Primitive);
            double[] lo = [x0, y0, z0], hi = [x1, y1, z1];
            if (high ? hi[axis] < at - tol : lo[axis] > at + tol) continue;
            if (Spans(x0, y0, z0, x1, y1, z1)) return true;
            rects.Add((lo[u], lo[v], hi[u], hi[v]));
        }
        if (rects.Count > 1 && CoveredBy(rects, Get(b.Min, u), Get(b.Min, v), Get(b.Max, u), Get(b.Max, v), tol)) return true;
        // brief-em3d-49 — or a face boundary's own piece lies on the box face and covers it whole: the surface is the
        // boundary's, claimed before the face.
        foreach (var (_, pieces) in problem.FaceBoundaryPieces())
            foreach (var pc in pieces)
                if (pc.NormalAxis == axis && pc.Bounds() is var (bx0, by0, bz0, bx1, by1, bz1) &&
                    Math.Abs((axis == 0 ? bx0 : axis == 1 ? by0 : bz0) - at) <= tol && Spans(bx0, by0, bz0, bx1, by1, bz1))
                    return true;
        return false;

        static double Get(Point3 q, int a) => a == 0 ? q.X : a == 1 ? q.Y : q.Z;
    }

    /// <summary>Whether the rectangles together cover [u0, u1] × [v0, v1]: every cell of the grid their edges cut it into
    /// lies inside one of them.</summary>
    private static bool CoveredBy(List<(double U0, double V0, double U1, double V1)> rects, double u0, double v0, double u1, double v1, double tol)
    {
        List<double> Cuts(Func<(double U0, double V0, double U1, double V1), double> lo, Func<(double U0, double V0, double U1, double V1), double> hi,
                          double a, double b)
            => [.. rects.SelectMany(r => new[] { lo(r), hi(r) }).Append(a).Append(b)
                        .Where(c => c >= a - tol && c <= b + tol).Order().Distinct()];
        var us = Cuts(r => r.U0, r => r.U1, u0, u1);
        var vs = Cuts(r => r.V0, r => r.V1, v0, v1);
        for (int i = 0; i + 1 < us.Count; i++)
        {
            double cu = (us[i] + us[i + 1]) / 2;
            if (us[i + 1] - us[i] <= tol) continue;
            for (int j = 0; j + 1 < vs.Count; j++)
            {
                double cv = (vs[j] + vs[j + 1]) / 2;
                if (vs[j + 1] - vs[j] <= tol) continue;
                if (!rects.Any(r => r.U0 <= cu && cu <= r.U1 && r.V0 <= cv && cv <= r.V1)) return false;
            }
        }
        return true;
    }

    /// <summary>brief-em3d-22 — a static problem's largest initial element, metres: the Palace section's
    /// <c>MaxElementWavelengths</c> read as a fraction of the air box's largest side.</summary>
    public static double StaticMaxElementSizeM(double boxLargestSideM, PalaceSettings settings)
        => settings.MaxElementWavelengths * boxLargestSideM;

    /// <summary>
    /// R-em3d7-2e — the largest initial element in <paramref name="material"/>, metres: the Palace
    /// section's fraction of the wavelength in that material at the sweep's top frequency (the largest
    /// εr of a tensor). The one formula the script and <c>explain</c>'s size estimate share.
    /// </summary>
    /// <summary>
    /// Designer feedback round 11 — the refinement the script writes (the Threshold fields round conductors, sheets and
    /// ports), as the size estimate prices it: <see cref="PalaceSettings.EdgeRefinement"/> of the smallest meshed element at
    /// conductors and sheets, at most 1/<see cref="PortCellsAcross"/> of each port's smaller side there, growing by
    /// <see cref="PalaceSettings.Grading"/> to the largest. The same arithmetic as <see cref="Write"/>'s, from the same
    /// per-material sizes.
    /// </summary>
    public static Em3dRefinementSizing RefinementSizing(Em3dProblem problem, Func<Em3dSolid, double> solidSizeM,
                                                        double backgroundSizeM, PalaceSettings settings)
    {
        var sizes = problem.Solids.Where(s => s.Role != Em3dRole.Conductor).Select(solidSizeM).Append(backgroundSizeM).ToList();
        double edge = settings.EdgeRefinement * sizes.Min();
        return new Em3dRefinementSizing(edge, port => Math.Min(edge, SmallerSide(port) / PortCellsAcross), settings.Grading,
                                        sizes.Max());
    }

    public static double MaxElementSizeM(Em3dMaterial material, double fMaxHz, PalaceSettings settings)
    {
        double er = material.EpsrTensor is { Count: 3 } t ? t.Max() : material.Epsr;
        double lambda = C0 / (fMaxHz * Math.Sqrt(Math.Max(er, 1e-9) * Math.Max(material.Mur, 1e-9)));
        return settings.MaxElementWavelengths * lambda;
    }

    // ── The entity table, read back ───────────────────────────────────────────────────────────

    /// <summary>What <see cref="EntitiesFile"/> says: per attribute, the count and the volume tags lost;
    /// and the books' totals.</summary>
    public sealed record EntityTable(IReadOnlyDictionary<int, (int Count, int Lost)> Groups,
                                     int AllVolumes, int ClassifiedVolumes, int UnclassifiedSingleSided)
    {
        /// <summary>brief-em3d-65 — per attribute, a kernel solid whose file was read as other than one volume.</summary>
        public IReadOnlyDictionary<int, int> KernelImports { get; init; } = new Dictionary<int, int>();
    }

    /// <summary>Parses <see cref="EntitiesFile"/>, or null when it is not one this writer's script wrote.</summary>
    public static EntityTable? ReadEntities(string text)
    {
        var groups = new Dictionary<int, (int, int)>();
        var imports = new Dictionary<int, int>();
        int? all = null, classified = null, rest = null;
        foreach (string raw in text.Split('\n'))
        {
            var t = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (t.Length == 0 || t[0].StartsWith('#')) continue;
            if (t[0] == "group" && t.Length == 4 && int.TryParse(t[1], CultureInfo.InvariantCulture, out int a) &&
                TryInt(t[2], out int c) && TryInt(t[3], out int lost))
                groups[a] = (c, lost);
            else if (t[0] == "kernel_import_count" && t.Length == 3 && int.TryParse(t[1], CultureInfo.InvariantCulture, out int ka) &&
                     TryInt(t[2], out int kc))
                imports[ka] = kc;
            else if (t.Length == 2 && TryInt(t[1], out int v))
                switch (t[0])
                {
                    case "all_volumes":               all = v; break;
                    case "classified_volumes":        classified = v; break;
                    case "unclassified_single_sided": rest = v; break;
                }
        }
        return all is null || classified is null || rest is null ? null
            : new EntityTable(groups, all.Value, classified.Value, rest.Value) { KernelImports = imports };

        static bool TryInt(string s, out int v)
        {
            bool ok = double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) &&
                      d >= 0 && d == Math.Floor(d) && d < int.MaxValue;
            v = ok ? (int)d : 0;
            return ok;
        }
    }

    /// <summary>
    /// <b>R-em3d7-3b — §6.4's rule made executable.</b> Null when every group got what it expected;
    /// otherwise the refusal, naming each object. Never downgraded to a warning: this is the only thing
    /// between a changed geometry and a boundary condition silently landing on the wrong face.
    /// </summary>
    public static string? CheckEntities(IReadOnlyList<Em3dGroup> groups, EntityTable? table)
    {
        if (table is null)
            return $"Gmsh finished but its entity table ({EntitiesFile}) is missing or unreadable, so nothing " +
                   "shows which mesh entities belong to which named object. The mesh is not used.";
        var faults = new List<string>();
        foreach (var gr in groups)
        {
            if (table.KernelImports.TryGetValue(gr.Attribute, out int read))
                faults.Add($"'{gr.Name}' was read from its kernel file as {read} volume(s) where one solid was written.");
            if (!table.Groups.TryGetValue(gr.Attribute, out var got))
            {
                faults.Add($"'{gr.Name}' is missing from the entity table.");
                continue;
            }
            if (got.Lost > 0)
                faults.Add($"'{gr.Name}' lost {got.Lost} volume(s) in the fragment, so its tags no longer name what " +
                           "they named.");
            else if (got.Count == 0 && gr.Expected > 0)
                faults.Add($"'{gr.Name}' ({Describe(gr)}) selected nothing in the mesh" +
                           (gr.Terminal is { } term
                               ? $", so {(term.Length == 0 ? "the ground" : $"terminal '{term}'")} has no surface there."
                               : "."));
            // brief-dfr35-1 R-dfr35-1-9 — a solid split by a sheet right across it is its pieces, so a volume group's count is a floor.
            else if (gr.AtLeast || gr.Dimension == 3 ? got.Count < gr.Expected : got.Count != gr.Expected)
                faults.Add($"'{gr.Name}' ({Describe(gr)}) selected {got.Count} {(gr.Dimension == 3 ? "volume" : "surface")}(s) " +
                           $"where {(gr.AtLeast || gr.Dimension == 3 ? "at least " : "")}{gr.Expected} were expected.");
        }
        if (table.ClassifiedVolumes != table.AllVolumes)
            faults.Add($"{table.AllVolumes} volume(s) were meshed and {table.ClassifiedVolumes} belong to a named object, " +
                       "so part of the space would be missing from the mesh.");
        if (table.UnclassifiedSingleSided > 0)
            faults.Add($"{table.UnclassifiedSingleSided} surface(s) bound the meshed space and belong to no named conductor, " +
                       "sheet, port or air-box face; Palace would treat them as a magnetic wall, silently.");
        return faults.Count == 0 ? null
            : "The mesh does not match the problem, so it is not solved (em-3d.md §6.4: a boundary is never " +
              "guessed onto a face). " + string.Join(" ", faults);
    }

    /// <summary>
    /// brief-dfr31-6 R-dfr31-6-4 — one sentence naming the objects behind the surfaces Gmsh's <paramref name="errors"/>
    /// name ("… on surface 22561 surface 22184"), from the script's <see cref="FacesFile"/>; null when they name none it
    /// holds. A tag alone gives the user nothing to act on.
    /// </summary>
    public static string? NameSurfaces(IEnumerable<string> errors, string facesText, IReadOnlyList<Em3dGroup> groups)
    {
        var lines = errors.ToList();
        var tags = lines.SelectMany(l => Regex.Matches(l, @"\bsurface (\d+)").Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)))
                        .Distinct().Take(4).ToList();
        if (tags.Count == 0) return null;
        var own = new Dictionary<int, List<int>>();
        var bounds = new Dictionary<int, List<int>>();
        foreach (string raw in facesText.Split('\n'))
        {
            var t = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 2 || t[0] is not ("f" or "v") || !int.TryParse(t[1], CultureInfo.InvariantCulture, out int attribute)) continue;
            var into = t[0] == "f" ? own : bounds;
            foreach (string s in t.Skip(2))
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                {
                    if (!into.TryGetValue((int)d, out var held)) into[(int)d] = held = [];
                    if (!held.Contains(attribute)) held.Add(attribute);
                }
        }
        var byAttribute = groups.GroupBy(g => g.Attribute).ToDictionary(g => g.Key, g => g.First());
        string Name(int attribute) => byAttribute.TryGetValue(attribute, out var g)
            ? g.Kind == Em3dGroupKind.Background ? "the surrounding air" : $"'{g.Name}'" : $"group {attribute}";
        string? Surface(int tag)
        {
            if (own.TryGetValue(tag, out var o)) return string.Join(" and ", o.Select(Name));
            if (!bounds.TryGetValue(tag, out var b)) return null;
            return b.Count >= 2 ? $"the face where {Name(b[0])} meets {Name(b[1])}" : $"a face of {Name(b[0])}";
        }
        var named = tags.Select(Surface).OfType<string>().ToList();
        if (named.Count == 0) return null;
        var listed = named.Distinct().ToList();
        string list = listed.Count == 1 ? listed[0] : string.Join(", ", listed.SkipLast(1)) + " and " + listed[^1];
        // brief-dfr35-1 R-dfr35-1-8 — where, since two faces of one pour read as "between X and X".
        string at = SurfacePlace(lines, facesText) is { } p ? $", at ({Coordinate(p.XUm)}, {Coordinate(p.YUm)}) µm" : "";
        if (lines.Any(l => l.Contains("overlapping facets", StringComparison.Ordinal)) && named.Count == 2)
            return named[0] == named[1]
                ? $"The overlap is between two faces of {named[0].Replace("a face of ", "", StringComparison.Ordinal)}{at}."
                : $"The overlap is between {named[0]} and {named[1]}{at}.";
        return $"The surface{(listed.Count == 1 ? " it names is" : "s it names are")} {list}{at}.";
    }

    /// <summary>
    /// brief-dfr35-1 R-dfr35-1-8 — where the surfaces Gmsh's <paramref name="errors"/> name are, from the boxes in the script's
    /// <see cref="FacesFile"/>; null when it holds none of them. In the layout's coordinates: a layout's run keeps them.
    /// </summary>
    public static Em3dLayoutPlace? SurfacePlace(IEnumerable<string> errors, string facesText)
    {
        var tags = errors.SelectMany(l => Regex.Matches(l, @"\bsurface (\d+)").Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)))
                         .ToHashSet();
        if (tags.Count == 0) return null;
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        bool found = false;
        foreach (string raw in facesText.Split('\n'))
        {
            if (!raw.StartsWith("b ", StringComparison.Ordinal)) continue;
            foreach (string row in raw[2..].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var t = row.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (t.Length != 7 || !int.TryParse(t[0], CultureInfo.InvariantCulture, out int tag) || !tags.Contains(tag)) continue;
                var v = t.Skip(1).Select(c => double.Parse(c, CultureInfo.InvariantCulture)).ToArray();
                (x0, y0, x1, y1) = (Math.Min(x0, v[0]), Math.Min(y0, v[1]), Math.Max(x1, v[3]), Math.Max(y1, v[4]));
                found = true;
            }
        }
        return found ? new Em3dLayoutPlace((x0 + x1) / 2, (y0 + y1) / 2, Math.Max(x1 - x0, y1 - y0)) : null;
    }

    private static string Coordinate(double um) => um.ToString("#,0.#", CultureInfo.InvariantCulture);

    private static string Describe(Em3dGroup gr) => gr.Kind switch
    {
        Em3dGroupKind.FaceBoundary => "a face boundary",
        Em3dGroupKind.Volume     => "a dielectric or air solid",
        Em3dGroupKind.Background => "the background air",
        Em3dGroupKind.Conductor  => "a conductor's surface",
        Em3dGroupKind.Sheet      => "a sheet",
        Em3dGroupKind.Port       => $"port {gr.PortNumber}'s sheet",
        _                        => "an air-box face",
    };

    // ── groups.json ────────────────────────────────────────────────────────────────────────────

    private static string GroupsJson(IReadOnlyList<Em3dGroup> groups)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            w.WriteStartObject();
            w.WriteString("Generator", "circuitRF brief-em3d-7: the mapping from named objects to Palace attributes");
            w.WriteNumber("LengthUnitM", LengthUnitM);
            w.WriteStartArray("Groups");
            foreach (var gr in groups)
            {
                w.WriteStartObject();
                w.WriteNumber("Attribute", gr.Attribute);
                w.WriteString("Name", gr.Name);
                w.WriteNumber("Dimension", gr.Dimension);
                w.WriteString("Kind", gr.Kind.ToString());
                w.WriteNumber("Expected", gr.Expected);
                if (gr.AtLeast) w.WriteBoolean("AtLeast", true);
                if (gr.Material is { } m) w.WriteString("Material", m);
                if (gr.Boundary is { } b) w.WriteString("Boundary", b.ToString());
                if (gr.PortNumber is { } n) w.WriteNumber("Port", n);
                if (gr.ThicknessM is { } t) w.WriteNumber("ThicknessM", t);
                if (gr.Terminal is { } term) w.WriteString("Terminal", term.Length == 0 ? "(ground)" : term);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    // ── primitives ─────────────────────────────────────────────────────────────────────────────

    private static void EmitPrimitive(StringBuilder g, string list, Em3dPrimitive primitive)
    {
        void L(string line) => g.Append(line).Append('\n');
        switch (primitive)
        {
            case Em3dBox b:
                L($"v = newv; Box(v) = {{{Um(b.Min.X)}, {Um(b.Min.Y)}, {Um(b.Min.Z)}, " +
                  $"{Um(b.Max.X - b.Min.X)}, {Um(b.Max.Y - b.Min.Y)}, {Um(b.Max.Z - b.Min.Z)}}};");
                L($"{list}[] = {{v}};");
                break;
            case Em3dCylinder c:
                L($"v = newv; Cylinder(v) = {{{Um(c.AxisStart.X)}, {Um(c.AxisStart.Y)}, {Um(c.AxisStart.Z)}, " +
                  $"{Um(c.AxisEnd.X - c.AxisStart.X)}, {Um(c.AxisEnd.Y - c.AxisStart.Y)}, " +
                  $"{Um(c.AxisEnd.Z - c.AxisStart.Z)}, {Um(c.Radius)}}};");
                L($"{list}[] = {{v}};");
                break;
            case Em3dSphere s:
                L($"v = newv; Sphere(v) = {{{Um(s.Center.X)}, {Um(s.Center.Y)}, {Um(s.Center.Z)}, {Um(s.Radius)}}};");
                L($"{list}[] = {{v}};");
                break;
            case Em3dEllipsoid el:
            {
                // brief-sar-3 R-sar3-1 — the OCC kernel's own construction: a unit sphere, dilated along its axes, turned onto
                // its frame and moved to its centre (the lengths are micrometres, as every other line here).
                L("v = newv; Sphere(v) = {0, 0, 0, 1};");
                L($"Dilate {{{{0, 0, 0}}, {{{Um(el.A)}, {Um(el.B)}, {Um(el.C)}}}}} {{ Volume{{v}}; }}");
                if (AxisAngle(el) is var (ax, ay, az, angle))
                    L($"Rotate {{{{{Num(ax)}, {Num(ay)}, {Num(az)}}}, {{0, 0, 0}}, {Num(angle)}}} {{ Volume{{v}}; }}");
                L($"Translate {{{Um(el.Center.X)}, {Um(el.Center.Y)}, {Um(el.Center.Z)}}} {{ Volume{{v}}; }}");
                L($"{list}[] = {{v}};");
                break;
            }
            case Em3dTruncatedSphere t:
            {
                L($"v = newv; Sphere(v) = {{{Um(t.Center.X)}, {Um(t.Center.Y)}, {Um(t.Center.Z)}, {Um(t.Radius)}}};");
                double z0 = Math.Max(t.ZMin, t.Center.Z - t.Radius), z1 = Math.Min(t.ZMax, t.Center.Z + t.Radius);
                if (z0 <= t.Center.Z - t.Radius && z1 >= t.Center.Z + t.Radius) { L($"{list}[] = {{v}};"); break; }
                double r = 1.01 * t.Radius;
                L($"b = newv; Box(b) = {{{Um(t.Center.X - r)}, {Um(t.Center.Y - r)}, {Um(z0)}, {Um(2 * r)}, {Um(2 * r)}, {Um(z1 - z0)}}};");
                L($"{list}[] = BooleanIntersection{{ Volume{{v}}; Delete; }}{{ Volume{{b}}; Delete; }};");
                break;
            }
            case Em3dPolyhedron ph:
                EmitPolyhedron(g, list, ph);
                break;
            case Em3dShapeSolid k:
                // brief-em3d-65 R-em3d65-2a — the kernel's own B-rep (D12: version 1, no triangles, micrometres, so no
                // unit line), under the same name a managed primitive would take: the cuts, the fragment and the entity
                // table below never know the difference. Its count is kept for the entity table (a later cut reassigns
                // the list, and the table is started afresh after the solids).
                L($"{list}[] = ShapeFromFile(\"{KernelFileName(k)}\");");
                L($"n{list} = #{list}[];");
                break;
            case Em3dExtrudedPolygon e:
                EmitPlanar(g, "bs", [e.Outline, .. e.Holes], q => new Point3(q.X, q.Y, e.ZBottom));
                L($"ex[] = Extrude {{0, 0, {Um(e.ZTop - e.ZBottom)}}} {{ Surface{{bs[]}}; }};");
                L($"{list}[] = {{ex[1]}};");
                break;
            case Em3dSweep w:
            {
                // The rings ARE the geometry (Em3dSweep's own note): a ruled loft through them joins
                // vertex k of each ring to vertex k of the next, which is exactly the problem's lateral
                // edges. A round wire's ring is a closed spline through its facet vertices, so the
                // surface is round again rather than a sixteen-sided prism.
                L("tl[] = {};");
                foreach (var ring in w.Rings)
                {
                    var pts = ring.ToList();
                    L("p = newp;");
                    for (int k = 0; k < pts.Count; k++)
                        L($"Point(p + {k}) = {{{Um(pts[k].X)}, {Um(pts[k].Y)}, {Um(pts[k].Z)}}};");
                    L("l = newc;");
                    if (w.Section == Em3dSection.Circle)
                    {
                        L($"Spline(l) = {{p:p + {pts.Count - 1}, p}};");
                        L("c = newcl; Curve Loop(c) = {l};");
                    }
                    else
                    {
                        for (int k = 0; k < pts.Count; k++)
                            L($"Line(l + {k}) = {{p + {k}, p + {(k + 1) % pts.Count}}};");
                        L($"c = newcl; Curve Loop(c) = {{l:l + {pts.Count - 1}}};");
                    }
                    L("tl[] += {c};");
                }
                L("v = newv; Ruled ThruSections(v) = {tl[]};");
                L($"{list}[] = {{v}};");
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(primitive), primitive.GetType().Name, "unknown primitive");
        }
    }

    /// <summary>A planar region: the first ring is the outline, the rest holes, each point placed in the
    /// world by <paramref name="world"/> (a horizontal region's is its height). Leaves its surface in
    /// <c>&lt;list&gt;[]</c>.</summary>
    private static void EmitPlanar(StringBuilder g, string list, IReadOnlyList<IReadOnlyList<Point2>> rings, Func<Point2, Point3> world)
    {
        void L(string line) => g.Append(line).Append('\n');
        L("cl[] = {};");
        foreach (var ring in PlanarRings(rings))
        {
            L("p = newp;");
            for (int k = 0; k < ring.Count; k++)
            {
                var w = world(ring[k]);
                L($"Point(p + {k}) = {{{Um(w.X)}, {Um(w.Y)}, {Um(w.Z)}}};");
            }
            L("l = newc;");
            for (int k = 0; k < ring.Count; k++)
                L($"Line(l + {k}) = {{p + {k}, p + {(k + 1) % ring.Count}}};");
            L($"c = newcl; Curve Loop(c) = {{l:l + {ring.Count - 1}}};");
            L("cl[] += {c};");
        }
        L("s = news; Plane Surface(s) = {cl[]};");
        L($"{list}[] = {{s}};");
    }

    /// <summary>
    /// brief-em3d-42 R-em3d42-1c — a polyhedron as the OCC factory builds one from its boundary: one Point per
    /// vertex, one Line per edge SHARED by the two faces that meet there (so the shell closes with no sewing),
    /// a Curve Loop per ring (holes as further loops), a Plane Surface per face, then a Surface Loop and a
    /// Volume. A face that is not planar to within a tenth of OCCT's own tolerance once in micrometres is
    /// written as its triangles instead, since a Plane Surface on it would be refused or silently bent.
    /// </summary>
    private static void EmitPolyhedron(StringBuilder g, string list, Em3dPolyhedron ph)
    {
        void L(string line) => g.Append(line).Append('\n');
        L("p = newp;");
        for (int k = 0; k < ph.Vertices.Count; k++)
            L($"Point(p + {k}) = {{{Um(ph.Vertices[k].X)}, {Um(ph.Vertices[k].Y)}, {Um(ph.Vertices[k].Z)}}};");

        var faces = PolyhedronLoops(ph);
        var edge = new Dictionary<(int, int), int>();
        foreach (var rings in faces)
            foreach (var ring in rings)
                for (int i = 0; i < ring.Count; i++)
                {
                    int a = ring[i], b = ring[(i + 1) % ring.Count];
                    var key = a < b ? (a, b) : (b, a);
                    if (!edge.ContainsKey(key)) edge[key] = edge.Count;
                }
        L("l = newc;");
        foreach (var ((a, b), e) in edge.OrderBy(kv => kv.Value))
            L($"Line(l + {e}) = {{p + {a}, p + {b}}};");
        L("sf[] = {};");
        foreach (var rings in faces)
        {
            L("cl[] = {};");
            foreach (var ring in rings)
            {
                var curves = new List<string>();
                for (int i = 0; i < ring.Count; i++)
                {
                    int a = ring[i], b = ring[(i + 1) % ring.Count];
                    int e = edge[a < b ? (a, b) : (b, a)];
                    curves.Add(a < b ? $"l + {e}" : $"-(l + {e})");
                }
                L($"c = newcl; Curve Loop(c) = {{{string.Join(", ", curves)}}};");
                L("cl[] += {c};");
            }
            L("s = news; Plane Surface(s) = {cl[]};");
            L("sf[] += {s};");
        }
        L("sl = newsl; Surface Loop(sl) = {sf[]};");
        L("v = newv; Volume(v) = {sl};");
        L($"{list}[] = {{v}};");
    }

    /// <summary>
    /// A polyhedron's faces as the OCC factory is handed them, each a list of vertex rings (outline, then holes): a planar
    /// face's own rings, and a face bent past <see cref="PlanarityToleranceUm"/> as its triangles. Shared with EMerge's
    /// lowering (brief-emi-4), which builds the same faces.
    /// </summary>
    internal static List<List<IReadOnlyList<int>>> PolyhedronLoops(Em3dPolyhedron ph)
    {
        var faces = new List<List<IReadOnlyList<int>>>();
        var mesh = Em3dTessellation.Of(new Em3dSolid("", "", Em3dRole.Conductor, ph, 0));
        var meshVertex = new Dictionary<Point3, int>();
        for (int k = 0; k < ph.Vertices.Count; k++) meshVertex.TryAdd(ph.Vertices[k], k);
        for (int f = 0; f < ph.Faces.Count; f++)
        {
            var face = ph.Faces[f];
            if (ph.IsPlanar(face, PlanarityToleranceUm * 1e-6))
                faces.Add([face.Outer, .. face.Holes]);
            else
                foreach (var t in mesh.Triangles.Where(t => t.Face == f))
                    faces.Add([new[] { meshVertex[mesh.Vertices[t.A]], meshVertex[mesh.Vertices[t.B]], meshVertex[mesh.Vertices[t.C]] }]);
        }
        return faces;
    }

    /// <summary>A polyhedron face bent by more than this, in micrometres, is written as triangles: a tenth of
    /// OCCT's default tolerance (1e-7 in model units).</summary>
    public const double PlanarityToleranceUm = 1e-8;

    /// <summary>The world bound of one of a sheet's rings, zero-thickness along a horizontal sheet's z.</summary>
    private static (double, double, double, double, double, double) WorldBounds(Em3dSheet sh, IReadOnlyList<Point2> ring)
    {
        double x0 = double.PositiveInfinity, y0 = x0, z0 = x0, x1 = double.NegativeInfinity, y1 = x1, z1 = x1;
        foreach (var q in ring)
        {
            var w = sh.World(q);
            x0 = Math.Min(x0, w.X); y0 = Math.Min(y0, w.Y); z0 = Math.Min(z0, w.Z);
            x1 = Math.Max(x1, w.X); y1 = Math.Max(y1, w.Y); z1 = Math.Max(z1, w.Z);
        }
        return (x0, y0, z0, x1, y1, z1);
    }

    /// <summary>brief-em3d-115 — whether <paramref name="p"/> is a terminal whose face belongs to a lower-numbered terminal of
    /// its group (the group's face is that terminal's surface).</summary>
    internal static bool SharesAnotherTerminalsFace(Em3dProblem problem, Em3dPort p)
        => p.FaceGroup is { } g && problem.Ports.Any(q => q.FaceGroup == g && q.Number < p.Number);

    /// <summary>An axis-aligned rectangle with one zero axis — a port sheet.</summary>
    /// <summary>
    /// brief-dfr35-1 R-dfr35-1-9 — a sheet reaching right across a solid cuts it in two, and the fragment then gives the pieces
    /// new tags: on a crop whose edges met a port's sheet on both sides, the dielectric's tag was simply gone. Each solid that
    /// lost its tag takes the volumes the fragment made inside its own box, smallest solid first and the background last, so a
    /// piece goes to the tightest box that holds it. Nothing changes for a solid the fragment left whole.
    /// </summary>
    private static void EmitSplitVolumes(StringBuilder g, IReadOnlyList<int> meshed,
                                         IReadOnlyList<(double X0, double Y0, double Z0, double X1, double Y1, double Z1)> bounds,
                                         Point3 boxMin, Point3 boxMax)
    {
        void L(string line) => g.Append(line).Append('\n');
        L("// ---- a sheet right across a solid splits it: the solid takes its pieces (brief-dfr35-1) -----");
        L($"made[] = allV[];");
        L($"made[] -= {{{string.Join(", ", meshed.Select(i => $"s{i}[]").Append("bg[]"))}}};");
        L("Macro CrfSplit");
        L("  crfp[] = {};");
        L("  crfg[] -= allV[];");
        L("  If (#crfg[])");
        L("    crfp[] = Volume In BoundingBox{crfx0 - e, crfy0 - e, crfz0 - e, crfx1 + e, crfy1 + e, crfz1 + e};");
        L("    crfo[] = crfp[];");
        L("    crfo[] -= made[];");
        L("    crfp[] -= crfo[];");
        L("    made[] -= crfp[];");
        L("  EndIf");
        L("Return");
        void Split(string list, double x0, double y0, double z0, double x1, double y1, double z1)
        {
            L($"crfg[] = {list}; crfx0 = {Um(x0)}; crfy0 = {Um(y0)}; crfz0 = {Um(z0)}; crfx1 = {Um(x1)}; crfy1 = {Um(y1)}; crfz1 = {Um(z1)};");
            L("Call CrfSplit;");
            L($"{list} -= crfg[];");
            L($"{list} += crfp[];");
        }
        foreach (int i in meshed.OrderBy(i => (bounds[i].X1 - bounds[i].X0) * (bounds[i].Y1 - bounds[i].Y0) * (bounds[i].Z1 - bounds[i].Z0)))
            Split($"s{i}[]", bounds[i].X0, bounds[i].Y0, bounds[i].Z0, bounds[i].X1, bounds[i].Y1, bounds[i].Z1);
        Split("bg[]", boxMin.X, boxMin.Y, boxMin.Z, boxMax.X, boxMax.Y, boxMax.Z);
    }

    private static void EmitRectangle(StringBuilder g, string list, Point3 min, Point3 max)
    {
        Point3[] corners = max.X == min.X
            ? [new(min.X, min.Y, min.Z), new(min.X, max.Y, min.Z), new(min.X, max.Y, max.Z), new(min.X, min.Y, max.Z)]
            : max.Y == min.Y
            ? [new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z), new(max.X, min.Y, max.Z), new(min.X, min.Y, max.Z)]
            : [new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z), new(max.X, max.Y, min.Z), new(min.X, max.Y, min.Z)];
        EmitCorners(g, list, corners);
    }

    /// <summary>A flat four-cornered sheet through <paramref name="corners"/> in order: an upright port's rectangle, or a turned
    /// one's, whose end corners are points of its copper's outline written by the same <see cref="Um"/>.</summary>
    private static void EmitCorners(StringBuilder g, string list, Point3[] corners)
    {
        void L(string line) => g.Append(line).Append('\n');
        L("p = newp;");
        for (int k = 0; k < 4; k++)
            L($"Point(p + {k}) = {{{Um(corners[k].X)}, {Um(corners[k].Y)}, {Um(corners[k].Z)}}};");
        L("l = newc;");
        for (int k = 0; k < 4; k++)
            L($"Line(l + {k}) = {{p + {k}, p + {(k + 1) % 4}}};");
        L("c = newcl; Curve Loop(c) = {l:l + 3};");
        L($"s = news; Plane Surface(s) = {{c}}; {list}[] = {{s}};");
    }

    /// <summary>brief-em3d-22 — a coaxial port's annulus, in its plane of constant z.</summary>
    private static void EmitAnnulus(StringBuilder g, string list, Em3dPort p, Em3dAnnulus ring)
    {
        void L(string line) => g.Append(line).Append('\n');
        double cx = (p.Min.X + p.Max.X) / 2, cy = (p.Min.Y + p.Max.Y) / 2;
        L($"d1 = news; Disk(d1) = {{{Um(cx)}, {Um(cy)}, {Um(p.Min.Z)}, {Um(ring.OuterRadiusM)}}};");
        L($"d2 = news; Disk(d2) = {{{Um(cx)}, {Um(cy)}, {Um(p.Min.Z)}, {Um(ring.InnerRadiusM)}}};");
        L($"{list}[] = BooleanDifference{{ Surface{{d1}}; Delete; }}{{ Surface{{d2}}; Delete; }};");
    }

    /// <summary><c>claimed[]</c> grows by what <paramref name="queries"/> select, less
    /// <paramref name="minus"/>'s selections and anything already claimed.</summary>
    private static void Claim(StringBuilder g, string list, IReadOnlyList<string> queries, IReadOnlyList<string> minus)
    {
        void L(string line) => g.Append(line).Append('\n');
        L($"{list}[] = {queries[0]};");
        foreach (string q in queries.Skip(1)) L($"{list}[] += {q};");
        foreach (string q in minus) { L($"x[] = {q};"); L($"{list}[] -= x[];"); }
        L($"{list}[] -= claimed[];");
        L($"claimed[] += {list}[];");
    }

    private static string Query((double X0, double Y0, double Z0, double X1, double Y1, double Z1) b, double marginM)
    {
        string lo = marginM > 0 ? $" - e - {Num(Round(marginM * 1e6))}" : " - e";
        string hi = marginM > 0 ? $" + e + {Num(Round(marginM * 1e6))}" : " + e";
        return $"Surface In BoundingBox{{{Um(b.X0)}{lo}, {Um(b.Y0)}{lo}, {Um(b.Z0)}{lo}, " +
               $"{Um(b.X1)}{hi}, {Um(b.Y1)}{hi}, {Um(b.Z1)}{hi}}}";
    }

    /// <summary>
    /// How much room a sheet's world bound takes, for claiming the smallest first. A sheet is flat, so its
    /// box has no volume: the measure is the largest product of two of its sides, which is the area of the
    /// plane it lies in. Zero for a degenerate box, which sorts first and claims nothing.
    /// </summary>
    private static double SheetSpan((double X0, double Y0, double Z0, double X1, double Y1, double Z1) b)
    {
        double dx = b.X1 - b.X0, dy = b.Y1 - b.Y0, dz = b.Z1 - b.Z0;
        return Math.Max(dx * dy, Math.Max(dy * dz, dz * dx));
    }

    /// <summary>
    /// brief-em3d-49 R-em3d49-4c — why a face boundary's box query would take more than its own face, or null: another
    /// solid's flat face, a sheet or a port sheet lies in the same plane and overlaps one of the boundary's pieces, so the
    /// fragment makes its surface coplanar with the face and inside the same box. Refused, naming both, rather than
    /// merged. An oblique piece has no zero-thickness box; its count is left to the entity check.
    /// </summary>
    internal static string? CoplanarNeighbour(Em3dProblem problem,
                                              IReadOnlyList<(Em3dFaceBoundary Boundary, IReadOnlyList<Em3dFacePolygon> Pieces)> boundaries)
    {
        if (boundaries.Count == 0) return null;
        var b = problem.Boundary;
        double tol = 1e-9 * Math.Max(1.0, Math.Max(b.Max.X - b.Min.X, Math.Max(b.Max.Y - b.Min.Y, b.Max.Z - b.Min.Z)));
        // Every flat surface of the problem that is not the boundary's own: (what it is, its axis, its bound).
        var others = new List<(string What, string Solid, string Face, int Axis, (double, double, double, double, double, double) Box)>();
        foreach (var s in problem.Solids)
            foreach (string face in Em3dFaceGeometry.FaceNames(s.Primitive).Distinct())
                if (Em3dFaceGeometry.Pieces(s.Primitive, face, out _) is { } pieces)
                    foreach (var pc in pieces)
                        if (pc.NormalAxis is int ax) others.Add(($"face '{face}' of '{s.Name}'", s.Name, face, ax, pc.Bounds()));
        foreach (var sh in problem.Sheets)
        {
            int? ax = sh.Frame is { } f ? f.NormalAxis : 2;
            if (ax is int a) others.Add(($"sheet '{sh.Name}'", sh.Name, "", a, sh.WorldBounds()));
        }
        foreach (var p in problem.Ports)
        {
            int a = p.Max.X == p.Min.X ? 0 : p.Max.Y == p.Min.Y ? 1 : 2;
            others.Add(($"port {p.Number}'s sheet", "", "", a, (p.Min.X, p.Min.Y, p.Min.Z, p.Max.X, p.Max.Y, p.Max.Z)));
        }
        foreach (var (boundary, pieces) in boundaries)
            foreach (var pc in pieces)
            {
                if (pc.NormalAxis is not int axis) continue;
                var box = pc.Bounds();
                double[] lo = [box.X0, box.Y0, box.Z0], hi = [box.X1, box.Y1, box.Z1];
                foreach (var o in others)
                {
                    if (o.Axis != axis || (o.Solid == boundary.Object &&
                                           (o.Face == boundary.Face || Em3dFaceGeometry.IsPiece(boundary.Face, o.Face)))) continue;
                    var (ox0, oy0, oz0, ox1, oy1, oz1) = o.Box;
                    double[] olo = [ox0, oy0, oz0], ohi = [ox1, oy1, oz1];
                    if (Math.Abs(olo[axis] - lo[axis]) > tol) continue;
                    bool overlaps = true;
                    for (int k = 0; k < 3 && overlaps; k++)
                        if (k != axis) overlaps = olo[k] < hi[k] - tol && lo[k] < ohi[k] - tol;
                    if (overlaps)
                        return $"The boundary on face '{boundary.Face}' of '{boundary.Object}' cannot be recovered for Palace: " +
                               $"{o.What} lies in the same plane and overlaps it, so the query that finds the face's surfaces " +
                               "would take that one too, and a boundary is never guessed onto a surface (em-3d.md §6.4). Move one " +
                               "of them, or put the boundary on a face nothing else touches.";
                }
            }
        return null;
    }

    /// <summary>How far a conductor's faces may stand outside its problem bound: a round wire's spline
    /// bulges past the facet vertices its bound is taken from by up to 2 % of its radius.</summary>
    private static double Margin(Em3dPrimitive p)
        => p is Em3dSweep { Section: Em3dSection.Circle } w ? 0.05 * w.Diameter : 0;

    /// <summary>
    /// The conductors a single face can span outside every one of their boxes, as connected groups of two or more, in index
    /// order (see the conductor claims). Two are joined when their boxes touch, share a face plane on one axis, and neither
    /// box holds the other on the other two axes — a via's end flush with a plane and sticking out past its edge. A via
    /// standing inside a plane's outline is not: a face across the two lies inside the plane's box, which claims it.
    /// </summary>
    private static List<List<int>> TouchingConductors(IReadOnlyList<int> conductors,
        IReadOnlyList<(double X0, double Y0, double Z0, double X1, double Y1, double Z1)> bounds)
    {
        var parent = conductors.ToDictionary(i => i, i => i);
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        const double tol = 1e-12;
        static bool Touch(double a0, double a1, double b0, double b1) => a0 <= b1 + tol && b0 <= a1 + tol;
        static bool Holds(double a0, double a1, double b0, double b1) => a0 <= b0 + tol && b1 <= a1 + tol;
        static bool SharePlane(double a0, double a1, double b0, double b1) =>
            Math.Abs(a0 - b0) <= tol || Math.Abs(a1 - b1) <= tol || Math.Abs(a0 - b1) <= tol || Math.Abs(a1 - b0) <= tol;
        for (int a = 0; a < conductors.Count; a++)
            for (int b = a + 1; b < conductors.Count; b++)
            {
                var (p, q) = (bounds[conductors[a]], bounds[conductors[b]]);
                if (!(Touch(p.X0, p.X1, q.X0, q.X1) && Touch(p.Y0, p.Y1, q.Y0, q.Y1) && Touch(p.Z0, p.Z1, q.Z0, q.Z1))) continue;
                (double, double, double, double)[] axes = [(p.X0, p.X1, q.X0, q.X1), (p.Y0, p.Y1, q.Y0, q.Y1), (p.Z0, p.Z1, q.Z0, q.Z1)];
                bool escapes = false;
                for (int k = 0; k < 3 && !escapes; k++)
                {
                    if (!SharePlane(axes[k].Item1, axes[k].Item2, axes[k].Item3, axes[k].Item4)) continue;
                    var (u, v) = (axes[(k + 1) % 3], axes[(k + 2) % 3]);
                    bool pHolds = Holds(u.Item1, u.Item2, u.Item3, u.Item4) && Holds(v.Item1, v.Item2, v.Item3, v.Item4);
                    bool qHolds = Holds(u.Item3, u.Item4, u.Item1, u.Item2) && Holds(v.Item3, v.Item4, v.Item1, v.Item2);
                    escapes = !pHolds && !qHolds;
                }
                if (escapes) parent[Find(conductors[a])] = Find(conductors[b]);
            }
        return [.. conductors.GroupBy(Find).Where(g => g.Count() > 1).Select(g => g.Order().ToList()).OrderBy(g => g[0])];
    }

    private static bool Overlap((double X0, double Y0, double Z0, double X1, double Y1, double Z1) a,
                                (double X0, double Y0, double Z0, double X1, double Y1, double Z1) b)
    {
        double tol = 1e-12;
        return a.X0 < b.X1 - tol && b.X0 < a.X1 - tol &&
               a.Y0 < b.Y1 - tol && b.Y0 < a.Y1 - tol &&
               a.Z0 < b.Z1 - tol && b.Z0 < a.Z1 - tol;
    }

    private static double SmallerSide(Em3dPort p)
    {
        if (p.Quad is { } quad)
        {
            var (length, width) = quad.Extent(p.Direction);
            return Math.Min(length, width);
        }
        double[] d = [p.Max.X - p.Min.X, p.Max.Y - p.Min.Y, p.Max.Z - p.Min.Z];
        return d.Where(v => v > 0).Min();
    }

    private static double Volume((double X0, double Y0, double Z0, double X1, double Y1, double Z1) b)
        => (b.X1 - b.X0) * (b.Y1 - b.Y0) * (b.Z1 - b.Z0);

    /// <summary>A ring with its closing duplicate and any repeated vertex removed.</summary>
    /// <summary>
    /// <b>A planar region's rings as the OCC factory needs them: cleaned, and every hole wound the way the outline is.</b>
    /// Clipper hands a polygon over in the usual convention — outline counter-clockwise, holes clockwise — and a face OCC
    /// builds from loops wound opposite ways is valid on its own, so an extrusion of it meshes. But cut from a solid it
    /// shares a face with, TOGETHER with any second tool, it empties the solid: OCC reports
    /// <c>BOPAlgo_AlertSolidBuilderUnusedFaces</c> and returns nothing (brief-dfr26-1). On a multilayer board that is
    /// every dielectric under a plane with an antipad, cut by the plane and a via at once: the slab vanished, the
    /// background lost it next, and Gmsh stopped on <c>BOPAlgo_AlertTooFewArguments</c>. Wound alike, the same cut is
    /// right — in the <c>.geo</c> script and through Gmsh's Python API (EMerge's script) alike.
    /// </summary>
    internal static List<List<Point2>> PlanarRings(IEnumerable<IReadOnlyList<Point2>> rings)
    {
        var o = rings.Select(Clean).ToList();
        if (o.Count < 2) return o;
        bool outerCcw = SignedArea(o[0]) > 0;
        for (int k = 1; k < o.Count; k++)
            if (SignedArea(o[k]) > 0 != outerCcw) o[k].Reverse();
        return o;

        static double SignedArea(List<Point2> r)
        {
            double a = 0;
            for (int i = 0, j = r.Count - 1; i < r.Count; j = i++) a += r[j].X * r[i].Y - r[i].X * r[j].Y;
            return a;
        }
    }

    internal static List<Point2> Clean(IReadOnlyList<Point2> ring)
    {
        var o = new List<Point2>(ring.Count);
        foreach (var q in ring)
            if (o.Count == 0 || !Same(o[^1], q)) o.Add(q);
        while (o.Count > 1 && Same(o[0], o[^1])) o.RemoveAt(o.Count - 1);
        return o;

        static bool Same(Point2 a, Point2 b) => Math.Abs(a.X - b.X) < 1e-12 && Math.Abs(a.Y - b.Y) < 1e-12;
    }

    /// <summary>brief-sar-3 §3 — what Palace says to a problem holding a lumped element.</summary>
    /// <summary>brief-sar-11 — the refusal for a voxel body's tissues or an imported surface mesh among <paramref name="solids"/>: the
    /// geometry worker builds no solid from either, so only openEMS runs them. Null when there is none.</summary>
    public static string? ImportedOpenEmsOnly(IEnumerable<Em3dSolid> solids, string solver)
    {
        var names = solids.Where(s => s.Primitive is Em3dShapeSolid { IsMesh: true })
                          .Select(s => s.Labels?.Body ?? s.Name).Distinct(StringComparer.Ordinal).ToList();
        return names.Count == 0 ? null
            : $"{string.Join(", ", names.Select(n => $"'{n}'"))} {(names.Count == 1 ? "is an imported voxel body or surface mesh" : "are imported voxel bodies or surface meshes")}, " +
              $"which openEMS runs and {solver} does not in this version (nothing builds a meshable solid from them). Run the setup on openEMS, " +
              "or set the object's Model off.";
    }

    public static string LumpedElementsOpenEmsOnly(string name)
        => $"Lumped element '{name}' is solved by openEMS only in this version: Palace's lumped elements are a later series. " +
           "Set Solver3D to openEMS.";

    /// <summary>brief-sar-3 — the rotation taking the world's axes onto an ellipsoid's (U, V, W) as an axis and an angle
    /// (radians), or null when there is none to make.</summary>
    internal static (double X, double Y, double Z, double Angle)? AxisAngle(Em3dEllipsoid e)
    {
        var (u, v, w) = (e.U, e.V, e.W);
        double trace = u.X + v.Y + w.Z;
        double angle = Math.Acos(Math.Clamp((trace - 1) / 2, -1, 1));
        if (angle < 1e-12) return null;
        double x = v.Z - w.Y, y = w.X - u.Z, z = u.Y - v.X;           // the skew part of R = [U V W]
        double n = Math.Sqrt(x * x + y * y + z * z);
        if (n < 1e-9)
        {
            // A half turn: the axis is R + I's largest column.
            double[][] c = [[u.X + 1, u.Y, u.Z], [v.X, v.Y + 1, v.Z], [w.X, w.Y, w.Z + 1]];
            var best = c.MaxBy(k => k[0] * k[0] + k[1] * k[1] + k[2] * k[2])!;
            double m = Math.Sqrt(best[0] * best[0] + best[1] * best[1] + best[2] * best[2]);
            return (best[0] / m, best[1] / m, best[2] / m, Math.PI);
        }
        return (x / n, y / n, z / n, angle);
    }

    // ── text ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Metres as micrometres, rounded to a femtometre so a conversion's last-bit noise does not
    /// reach the file.</summary>
    private static string Um(double metres) => Num(Round(metres * 1e6));

    private static double Round(double v) => Math.Round(v, 9);

    /// <summary>Invariant round-trip text; a negative zero is written as 0.</summary>
    internal static string Num(double v) => (v == 0 ? 0.0 : v).ToString("R", CultureInfo.InvariantCulture);

    /// <summary>A name inside a <c>//</c> comment: one line.</summary>
    private static string Comment(string s) => s.Replace('\n', ' ').Replace('\r', ' ');

    /// <summary>A name inside a Gmsh string: printable ASCII with no quote or backslash. The exact name
    /// is in groups.json; this is only what the mesh file's $PhysicalNames shows.</summary>
    private static string PhysicalName(string s)
        => new(s.Select(ch => ch is >= ' ' and <= '~' and not '"' and not '\\' ? ch : '_').ToArray());

    private static GmshLowering No(string refusal) => new(null, [], null, refusal);
}
