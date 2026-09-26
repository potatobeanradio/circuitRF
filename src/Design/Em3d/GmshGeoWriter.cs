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
//   1. Every solid is cut by every higher-order solid it overlaps, so the volumes are disjoint by
//      construction order BEFORE any fragment. A conductor is then deleted: it is a VOID whose
//      surface carries Palace's conductivity boundary. What no solid claims inside the box is the
//      background, which is air.
//   2. Sheets and port sheets are plane surfaces, and ONE BooleanFragments imprints them and the
//      shared faces. It splits no volume, and with OCCBooleanPreserveNumbering every volume keeps its
//      tag — so a VOLUME group is its construction tags, checked to have survived.
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
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Em3d;

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
}

/// <summary>
/// One physical group of the mesh: the named object it came from, the Palace attribute circuitRF
/// assigned it, and how many Gmsh entities it must yield (R-em3d7-3b).
/// </summary>
/// <param name="Expected">The count expected; with <paramref name="AtLeast"/> it is a floor.</param>
/// <param name="Material">A volume's material, a conductor's or a sheet's metal; null otherwise.</param>
/// <param name="Boundary">An air-box face's kind; null otherwise.</param>
/// <param name="PortNumber">A port's number; null otherwise.</param>
/// <param name="ThicknessM">A sheet's real thickness, metres; null otherwise.</param>
/// <param name="Terminal">brief-em3d-22 — the static terminal a conductor belongs to ("" for the
/// ground); null otherwise, and for every driven problem.</param>
public sealed record Em3dGroup(
    string Name, int Attribute, int Dimension, Em3dGroupKind Kind, int Expected, bool AtLeast,
    string? Material = null, Em3dBoundaryKind? Boundary = null, int? PortNumber = null, double? ThicknessM = null,
    string? Terminal = null);

/// <summary>The lowering: the script, its groups, and the groups as the file written beside the
/// mesh — or the reason the problem cannot be lowered.</summary>
public sealed record GmshLowering(string? Geo, IReadOnlyList<Em3dGroup> Groups, string? GroupsJson, string? Refusal)
{
    public bool Ok => Refusal is null;
}

public static class GmshGeoWriter
{
    public const string GeoFile      = "model.geo";
    public const string MeshFile     = "model.msh";
    public const string EntitiesFile = "entities.txt";
    public const string GroupsFile   = "groups.json";

    /// <summary>The script's length unit, metres — Palace's <c>Model.L0</c>.</summary>
    public const double LengthUnitM = 1e-6;

    /// <summary>The group that holds the air no named solid claims.</summary>
    public const string BackgroundName = "background";

    /// <summary>Elements per full turn on a curved surface — a via barrel, a round wire. Twelve puts a
    /// 1 mil wire's surface elements at about its quarter-diameter, F0 case A's hand-tuned size.</summary>
    public const int CurvatureElements = 12;

    /// <summary>Elements across a port sheet's smaller side, at least (R-em3d7-2e).</summary>
    public const int PortCellsAcross = 4;

    /// <summary>The query tolerance, micrometres (F0's).</summary>
    private const double Eps = 1e-3;

    private const double C0 = 299_792_458.0;

    /// <summary>What an empty background is meshed as when the problem names no air.</summary>
    internal static readonly Em3dMaterial FreeSpace = new("(free space)", 1, null, 0, 1, 0);

    private static readonly string[] FaceKeys = ["xmin", "xmax", "ymin", "ymax", "zmin", "zmax"];

    /// <summary>
    /// The script for <paramref name="problem"/>. Refuses — never guesses — a problem that does not
    /// validate or that states something Palace cannot be told.
    /// </summary>
    public static GmshLowering Write(Em3dProblem problem, PalaceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(settings);

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
        for (int i = 0; i < problem.Solids.Count; i++)
        {
            var s = problem.Solids[i];
            solidGroup[i] = s.Role == Em3dRole.Conductor
                ? new Em3dGroup(s.Name, ++attr, 2, Em3dGroupKind.Conductor, 1, AtLeast: true, s.Material,
                                Terminal: terminalOf.GetValueOrDefault(s.Name))
                : new Em3dGroup(s.Name, ++attr, 3, Em3dGroupKind.Volume, 1, AtLeast: false, s.Material);
            groups.Add(solidGroup[i]);
        }
        // The background is the problem's air; a problem with no air solid (a box closed by a PEC face
        // right at the stack) has at most an empty background, meshed as free space.
        string backgroundMaterial = problem.Solids.FirstOrDefault(s => s.Role == Em3dRole.Air)?.Material
                                    ?? (materials.ContainsKey("Air") ? "Air" : FreeSpace.Name);
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
        var portGroups = problem.Ports.Select(p =>
            new Em3dGroup(p.Name, ++attr, 2, Em3dGroupKind.Port, 1, AtLeast: true, PortNumber: p.Number)).ToList();
        groups.AddRange(portGroups);

        // brief-em3d-23 — a face that a wave port or a conductor covers whole keeps no surface of its own:
        // it expects none, and the configuration names no attribute for it.
        var faceGroups = FaceKeys.Select((key, k) =>
            new Em3dGroup(Em3dAirBox.FaceName(key), ++attr, 2, Em3dGroupKind.Face, Covered(problem, key) ? 0 : 1,
                          AtLeast: !Covered(problem, key), Boundary: faceKinds[k])).ToList();
        groups.AddRange(faceGroups);

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

        L("// ---- disjoint by construction order: each solid loses what a higher-order solid takes -----");
        var bounds = problem.Solids.Select(s => Em3dProblem.Bounds(s.Primitive)).ToList();
        for (int i = 0; i < problem.Solids.Count; i++)
        {
            var tools = new List<int>();
            for (int j = 0; j < problem.Solids.Count; j++)
            {
                if (j == i) continue;
                bool higher = problem.Solids[j].Order > problem.Solids[i].Order ||
                              (problem.Solids[j].Order == problem.Solids[i].Order && j > i);
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
        L($"bg[] = BooleanDifference{{ Volume{{bx}}; Delete; }}{{ Volume{{{allSolids}}}; }};");
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
            EmitPlanar(g, $"h{k}", [sh.Outline, .. sh.Holes], sh.Z);
        }
        for (int k = 0; k < problem.Ports.Count; k++)
        {
            var p = problem.Ports[k];
            L($"// {Comment(p.Name)}: from {Comment(p.NegativeObject)} to {Comment(p.PositiveObject)}");
            if (p.Annulus is { } ring) EmitAnnulus(g, $"p{k}", p, ring);
            else EmitRectangle(g, $"p{k}", p.Min, p.Max);
        }
        L();

        var meshed = Enumerable.Range(0, problem.Solids.Count).Where(i => problem.Solids[i].Role != Em3dRole.Conductor).ToList();
        var surfaces = Enumerable.Range(0, problem.Sheets.Count).Select(k => $"h{k}[]")
                                 .Concat(Enumerable.Range(0, problem.Ports.Count).Select(k => $"p{k}[]")).ToList();
        L("// ---- one fragment: imprints the shared faces and the sheets, splits no volume -------------");
        L($"frag[] = BooleanFragments{{ Volume{{{string.Join(", ", meshed.Select(i => $"s{i}[]").Append("bg[]"))}}}; Delete; }}" +
          (surfaces.Count == 0 ? "{ };" : $"{{ Surface{{{string.Join(", ", surfaces)}}}; Delete; }};"));
        L("allV[] = Volume{:};");
        L("allS[] = Surface{:};");
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
        void ClaimPorts(bool wave)
        {
            for (int k = 0; k < problem.Ports.Count; k++)
            {
                var p = problem.Ports[k];
                if (!problem.IsStatic && (p.Kind == Em3dPortKind.Wave) != wave) continue;
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
        if (problem.IsStatic) ClaimPorts(wave: false);
        else if (problem.HasWavePorts) ClaimPorts(wave: true);
        for (int k = 0; k < 6; k++)
        {
            L($"// air box {FaceKeys[k]}");
            Claim(g, $"f{k}", [Query(faceQuery[k], 0)], []);
        }
        if (!problem.IsStatic) ClaimPorts(wave: false);
        for (int k = 0; k < problem.Sheets.Count; k++)
        {
            var sh = problem.Sheets[k];
            var (x0, y0, x1, y1) = RingBounds(sh.Outline);
            var holes = sh.Holes.Select(h => RingBounds(h))
                               .Select(b => Query((b.X0, b.Y0, sh.Z, b.X1, b.Y1, sh.Z), 0)).ToList();
            L($"// sheet {Comment(sh.Name)}");
            Claim(g, $"w{k}", [Query((x0, y0, sh.Z, x1, y1, sh.Z), 0)], holes);
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
            L($"c{i}[] = {Query(bounds[i], Margin(s.Primitive))};");
            L($"x[] = c{i}[];");
            L("x[] -= voids[];");
            L($"c{i}[] -= x[];");
            L($"c{i}[] -= claimed[];");
            L($"claimed[] += c{i}[];");
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
            L($"Physical Surface(\"{PhysicalName(portGroups[k].Name)}\", {portGroups[k].Attribute}) = {{q{k}[]}};");
        for (int k = 0; k < 6; k++)
            L($"Physical Surface(\"{PhysicalName(faceGroups[k].Name)}\", {faceGroups[k].Attribute}) = {{f{k}[]}};");
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
            L($"Printf(\"group {portGroups[k].Attribute} %g 0\", #q{k}[]) >> \"{EntitiesFile}\";");
        for (int k = 0; k < 6; k++)
            L($"Printf(\"group {faceGroups[k].Attribute} %g 0\", #f{k}[]) >> \"{EntitiesFile}\";");
        L($"Printf(\"all_volumes %g\", #allV[]) >> \"{EntitiesFile}\";");
        L($"Printf(\"classified_volumes %g\", {classifiedVolumes}) >> \"{EntitiesFile}\";");
        L($"Printf(\"all_surfaces %g\", #allS[]) >> \"{EntitiesFile}\";");
        L($"Printf(\"single_sided %g\", #single[]) >> \"{EntitiesFile}\";");
        L($"Printf(\"unclassified_single_sided %g\", #rest[]) >> \"{EntitiesFile}\";");
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
        Refine([.. conductors.Select(i => $"c{i}[]"), .. Enumerable.Range(0, problem.Sheets.Count).Select(k => $"w{k}[]")], sizeEdge);
        Refine([.. Enumerable.Range(0, problem.Ports.Count).Select(k => $"q{k}[]")], sizePort);
        int min = ++field;
        L($"Field[{min}] = Min; Field[{min}].FieldsList = {{{string.Join(", ", fields)}}};");
        L($"Background Field = {min};");
        // Curved second-order elements, optimised (F0: HighOrderOptimize = 1 left three negative-Jacobian
        // elements on case A), saved as MSH 2.2 binary — the format the validated Palace read in F0.
        L("Mesh.ElementOrder = 2;");
        L("Mesh.HighOrderOptimize = 2;");
        L("Mesh.MshFileVersion = 2.2;");
        L("Mesh.Binary = 1;");

        return new GmshLowering(g.ToString(), groups, GroupsJson(groups), null);
    }

    /// <summary>
    /// The frequency a problem's initial mesh is sized at: the sweep's top — or, for an eigenmode problem,
    /// which has no sweep, <b>twice its target</b>: the modes it finds lie above the target, and the first
    /// few of a cavity lie within an octave of the fundamental.
    /// </summary>
    public static double SizingFrequencyHz(Em3dProblem problem)
        => problem.Type == Em3dProblemType.Eigenmode ? 2 * problem.EigenmodeTargetHz : problem.Frequency.StopHz;

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
        foreach (var s in problem.Solids)
            if (s.Role == Em3dRole.Conductor && s.Primitive is Em3dBox &&
                Em3dProblem.Bounds(s.Primitive) is var (x0, y0, z0, x1, y1, z1) && Spans(x0, y0, z0, x1, y1, z1))
                return true;
        return false;

        static double Get(Point3 q, int a) => a == 0 ? q.X : a == 1 ? q.Y : q.Z;
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
                                     int AllVolumes, int ClassifiedVolumes, int UnclassifiedSingleSided);

    /// <summary>Parses <see cref="EntitiesFile"/>, or null when it is not one this writer's script wrote.</summary>
    public static EntityTable? ReadEntities(string text)
    {
        var groups = new Dictionary<int, (int, int)>();
        int? all = null, classified = null, rest = null;
        foreach (string raw in text.Split('\n'))
        {
            var t = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (t.Length == 0 || t[0].StartsWith('#')) continue;
            if (t[0] == "group" && t.Length == 4 && int.TryParse(t[1], CultureInfo.InvariantCulture, out int a) &&
                TryInt(t[2], out int c) && TryInt(t[3], out int lost))
                groups[a] = (c, lost);
            else if (t.Length == 2 && TryInt(t[1], out int v))
                switch (t[0])
                {
                    case "all_volumes":               all = v; break;
                    case "classified_volumes":        classified = v; break;
                    case "unclassified_single_sided": rest = v; break;
                }
        }
        return all is null || classified is null || rest is null ? null
            : new EntityTable(groups, all.Value, classified.Value, rest.Value);

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
            else if (gr.AtLeast ? got.Count < gr.Expected : got.Count != gr.Expected)
                faults.Add($"'{gr.Name}' ({Describe(gr)}) selected {got.Count} {(gr.Dimension == 3 ? "volume" : "surface")}(s) " +
                           $"where {(gr.AtLeast ? "at least " : "")}{gr.Expected} were expected.");
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

    private static string Describe(Em3dGroup gr) => gr.Kind switch
    {
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
            case Em3dExtrudedPolygon e:
                EmitPlanar(g, "bs", [e.Outline, .. e.Holes], e.ZBottom);
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

    /// <summary>A planar region at height <paramref name="z"/>: the first ring is the outline, the
    /// rest holes. Leaves its surface in <c>&lt;list&gt;[]</c>.</summary>
    private static void EmitPlanar(StringBuilder g, string list, IReadOnlyList<IReadOnlyList<Point2>> rings, double z)
    {
        void L(string line) => g.Append(line).Append('\n');
        L("cl[] = {};");
        foreach (var raw in rings)
        {
            var ring = Clean(raw);
            L("p = newp;");
            for (int k = 0; k < ring.Count; k++)
                L($"Point(p + {k}) = {{{Um(ring[k].X)}, {Um(ring[k].Y)}, {Um(z)}}};");
            L("l = newc;");
            for (int k = 0; k < ring.Count; k++)
                L($"Line(l + {k}) = {{p + {k}, p + {(k + 1) % ring.Count}}};");
            L($"c = newcl; Curve Loop(c) = {{l:l + {ring.Count - 1}}};");
            L("cl[] += {c};");
        }
        L("s = news; Plane Surface(s) = {cl[]};");
        L($"{list}[] = {{s}};");
    }

    /// <summary>An axis-aligned rectangle with one zero axis — a port sheet.</summary>
    private static void EmitRectangle(StringBuilder g, string list, Point3 min, Point3 max)
    {
        void L(string line) => g.Append(line).Append('\n');
        Point3[] corners = max.X == min.X
            ? [new(min.X, min.Y, min.Z), new(min.X, max.Y, min.Z), new(min.X, max.Y, max.Z), new(min.X, min.Y, max.Z)]
            : max.Y == min.Y
            ? [new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z), new(max.X, min.Y, max.Z), new(min.X, min.Y, max.Z)]
            : [new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z), new(max.X, max.Y, min.Z), new(min.X, max.Y, min.Z)];
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

    /// <summary>How far a conductor's faces may stand outside its problem bound: a round wire's spline
    /// bulges past the facet vertices its bound is taken from by up to 2 % of its radius.</summary>
    private static double Margin(Em3dPrimitive p)
        => p is Em3dSweep { Section: Em3dSection.Circle } w ? 0.05 * w.Diameter : 0;

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
        double[] d = [p.Max.X - p.Min.X, p.Max.Y - p.Min.Y, p.Max.Z - p.Min.Z];
        return d.Where(v => v > 0).Min();
    }

    private static double Volume((double X0, double Y0, double Z0, double X1, double Y1, double Z1) b)
        => (b.X1 - b.X0) * (b.Y1 - b.Y0) * (b.Z1 - b.Z0);

    private static (double X0, double Y0, double X1, double Y1) RingBounds(IReadOnlyList<Point2> ring)
        => (ring.Min(q => q.X), ring.Min(q => q.Y), ring.Max(q => q.X), ring.Max(q => q.Y));

    /// <summary>A ring with its closing duplicate and any repeated vertex removed.</summary>
    private static List<Point2> Clean(IReadOnlyList<Point2> ring)
    {
        var o = new List<Point2>(ring.Count);
        foreach (var q in ring)
            if (o.Count == 0 || !Same(o[^1], q)) o.Add(q);
        while (o.Count > 1 && Same(o[0], o[^1])) o.RemoveAt(o.Count - 1);
        return o;

        static bool Same(Point2 a, Point2 b) => Math.Abs(a.X - b.X) < 1e-12 && Math.Abs(a.Y - b.Y) < 1e-12;
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
