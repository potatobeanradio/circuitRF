// brief-emi-4 — the lowering: an Em3dProblem as EMerge's input. Two files and the kernel solids' B-reps go into the run
// directory: the DRIVER (circuitrf_emerge.py, a constant program embedded in this assembly) and problem.json, which is pure
// data — every object by index, every length in metres, every decision already made here (R-emi4-0b). The driver builds
// each solid with the same OpenCASCADE calls GmshGeoWriter writes for Palace, so the two FEM solvers mesh one geometry.
//
// R-emi4-0a: this file and EmergeScriptWriter.Emit.cs never compose text. Refusals, notes and explain's rows are
// EmergeLoweringRules'; a string reaches the input only through PyStr.

using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Em3d;

/// <summary>EMerge's input for one problem, or why there is none.</summary>
/// <param name="ProblemJson">problem.json's text; null with a refusal.</param>
/// <param name="Files">The kernel solids' B-reps, written beside it under the names it gives them.</param>
/// <param name="Objects">explain's rows: every object and what EMerge is told it is.</param>
/// <param name="EdgeSizeM">The element size along conductor and sheet edges, metres.</param>
/// <param name="LargestSizeM">The largest element of any meshed material, metres.</param>
public sealed record EmergeLowering(string? ProblemJson, IReadOnlyList<Em3dKernelFile> Files, string? Refusal,
                                    IReadOnlyList<EmergeObjectMapping> Objects, double EdgeSizeM, double LargestSizeM)
{
    public bool Ok => ProblemJson is not null;
}

public static partial class EmergeScriptWriter
{
    /// <summary>The input's data file, in the run directory.</summary>
    public const string ProblemFile = "problem.json";

    /// <summary>The driver's file name, in the run directory beside <see cref="ProblemFile"/>.</summary>
    public const string DriverFile = "circuitrf_emerge.py";

    /// <summary>problem.json's format. The driver refuses one it does not know. brief-emw-2: 2 adds <c>wavePorts</c>.</summary>
    public const int FormatVersion = 2;

    private const string FormatName = "circuitRF EMerge problem";

    private const string DriverResource = "CircuitRF.Design.Em3d.Solvers.EMerge.circuitrf_emerge.py";

    /// <summary>The driver's text, exactly as it is written into a run directory.</summary>
    public static string Driver { get; } = ReadDriver();

    private static string ReadDriver()
    {
        using var stream = typeof(EmergeScriptWriter).Assembly.GetManifestResourceStream(DriverResource)
            ?? throw new InvalidOperationException("EMerge's driver is missing from this assembly's resources.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    }

    /// <summary>The EMerge versions circuitRF has tested, oldest first: the driver warns on any other, and runs it.</summary>
    public static IReadOnlyList<string> TestedVersions => [.. SolverDiscovery.Emerge.ValidatedVersions.Select(v => v.Release)];

    /// <summary>brief-emi-7 — why EMerge computes no radiation pattern for this setup, or null when it does (or none was asked
    /// for): its far field is integrated over the air box's absorbing faces, which must close round the whole problem.</summary>
    public static string? RadiationPatternNote(EmSetup setup, Em3dProblem problem)
        => setup.RadiationPattern && problem.Type == Em3dProblemType.Driven ? EmergeFields.FarFieldRefusal(problem) : null;

    /// <summary>
    /// <paramref name="problem"/> as EMerge's input, or the first thing of it EMerge cannot be told
    /// (<see cref="EmergeLoweringRules.Refusal"/>). Byte-deterministic: the same problem and settings write the same bytes.
    /// brief-emi-7 — <paramref name="fields"/> is what the run saves of its fields (<see cref="EmergeFields.Request"/>); null
    /// writes no <c>fields</c> block, the input as it was before fields existed.
    /// </summary>
    public static EmergeLowering Write(Em3dProblem problem, EmergeSettings settings, EmergeFieldRequest? fields = null)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(settings);
        if (EmergeLoweringRules.Refusal(problem, settings) is { } refused) return new(null, [], refused, [], 0, 0);

        var materials = new Materials(problem);
        var sizes = Sizes(problem, settings, materials);
        var priority = EmergeLoweringRules.Priorities(problem);
        var files = problem.Solids.Select(s => s.Primitive).OfType<Em3dShapeSolid>()
                           .Select(k => new Em3dKernelFile(GmshGeoWriter.KernelFileName(k), k.Brep.ToArray()))
                           .DistinctBy(f => f.FileName).ToList();

        var e = new Emitter();
        e.BeginObject();
        e.Key("format").Str(FormatName);
        e.Key("version").Int(FormatVersion);
        e.Key("testedEmerge").Strings(TestedVersions);
        e.Key("problem").Str(problem.Type == Em3dProblemType.Eigenmode ? "eigenmode" : "driven");
        if (problem.Type == Em3dProblemType.Eigenmode)
            e.Key("eigenmode").BeginObject().Key("count").Int(problem.EigenmodeCount).Key("targetHz").Number(problem.EigenmodeTargetHz).EndObject();
        else
            e.Key("frequenciesHz").Numbers(Em3dRunService.FrequenciesHz(problem.Frequency));
        e.Key("sizingHz").Number(GmshGeoWriter.SizingFrequencyHz(problem));

        e.Key("settings").BeginObject()
         .Key("elementOrder").Int(settings.ElementOrder)
         .Key("resolution").Number(settings.MaxElementWavelengths)
         .Key("edgeSizeM").Number(sizes.Edge)
         .Key("largestSizeM").Number(sizes.Largest)
         .Key("adaptiveSweep").Bool(settings.AdaptiveSweep)
         .Key("tolerance").Number(settings.Tolerance)
         .Key("initialSamples").Int(settings.InitialSamples)
         .Key("maxSamples").Int(settings.MaxSamples)
         .Key("adaptiveMesh").Int(settings.AdaptiveMesh);
        // brief-sdl-8 D7 — written only when held, so every other run's problem.json is the bytes it was.
        if (settings.HoldMeshPasses) e.Key("holdMesh").Bool(true);
        e.EndObject();

        if (fields is not null)
        {
            e.Key("fields").BeginObject().Key("write").Bool(fields.Write);
            if (problem.Type == Em3dProblemType.Eigenmode) e.Key("modeEnergyJ").Number(fields.ModeEnergyJ);
            else
            {
                e.Key("frequenciesHz").Numbers(fields.FrequenciesHz).Key("incidentPowerW").Number(PalaceDrive.IncidentPowerW);
                if (fields.FarField)
                    e.Key("farField").BeginObject().Key("thetaStepDeg").Number(EmergeFields.FarFieldStepDeg)
                     .Key("phiStepDeg").Number(EmergeFields.FarFieldStepDeg).EndObject();
            }
            e.EndObject();
        }

        e.Key("materials").BeginArray();
        foreach (var (m, pec) in materials.Entries) WriteMaterial(e, m, pec);
        e.EndArray();

        var box = problem.Boundary;
        e.Key("box").BeginObject().Key("min").Point(box.Min).Key("max").Point(box.Max)
         .Key("material").Int(materials.Background).Key("faces").BeginObject();
        foreach (var (key, kind) in EmergeLoweringRules.FaceKinds(problem)) e.Key(key).Str(FaceWord(kind));
        e.EndObject().EndObject();

        e.Key("solids").BeginArray();
        foreach (var s in problem.Solids)
        {
            e.BeginObject().Key("name").Str(s.Name).Key("role").Str(RoleWord(s.Role))
             .Key("material").Int(materials.Of(s.Material, s.Role == Em3dRole.Conductor)).Key("priority").Int(priority[s])
             .Key("shape");
            WriteShape(e, s.Primitive);
            e.EndObject();
        }
        e.EndArray();

        e.Key("sheets").BeginArray();
        foreach (var sh in problem.Sheets)
            e.BeginObject().Key("name").Str(sh.Name).Key("material").Int(materials.Of(sh.Material, conductor: true))
             .Key("thicknessM").Number(sh.ThicknessM)
             // brief-emi-11 — its +n (the local +z through World, the side Plot Field ▸ Top side names): the driver orients the
             // sheet's surface current and its triangles by it
             .Key("normal").Point(sh.Frame?.Normal ?? new Point3(0, 0, 1))
             .Key("rings").Rings(GmshGeoWriter.PlanarRings(new[] { sh.Outline }.Concat(sh.Holes)).Select(r => r.Select(sh.World)))
             .EndObject();
        e.EndArray();

        e.Key("ports").BeginArray();
        foreach (var p in problem.Ports.Where(p => p.Kind == Em3dPortKind.Lumped))
        {
            var r = EmergeLoweringRules.PortRect(p)!.Value;
            e.BeginObject().Key("number").Int(p.Number).Key("name").Str(p.Name)
             .Key("origin").Point(r.Origin).Key("u").Point(r.U).Key("v").Point(r.V)
             .Key("widthM").Number(r.Width).Key("heightM").Number(r.Height)
             .Key("direction").Point(p.Direction).Key("z0").Number(p.Z0.Real)
             .EndObject();
        }
        e.EndArray();

        // brief-emw-2 R-emw2-1 — a wave port of one terminal: its region on the air box's face, circuitRF's voltage path, the
        // reference it is renormalised to, how far its plane moves and whether its mode is solved per frequency
        // brief-emw-3 R-emw3-3 — a terminal group is ONE entry, its leader's (the lowest number): the region and the reference
        // are shared, and `terminals` lists each terminal's number, name and path in terminal order, the driver's modes 1…N.
        // A one-terminal wave port is written exactly as EMW-2 writes it.
        e.Key("wavePorts").BeginArray();
        foreach (var p in problem.Ports.Where(p => p.Kind == Em3dPortKind.Wave))
        {
            var group = p.FaceGroup is { } g ? problem.Ports.Where(q => q.FaceGroup == g).OrderBy(q => q.Number).ToList() : null;
            if (group is not null && group[0] != p) continue;
            var r = EmergeLoweringRules.Region(p.Min, p.Max);
            var path = p.VoltagePath!.Value;
            var faceMaterials = EmergeLoweringRules.RegionMaterials(problem, p);
            // a group's entry is named by its port's label, which the driver's refusals name it by
            e.BeginObject().Key("number").Int(p.Number).Key("name").Str(group is not null ? p.FaceGroupLabel ?? p.Name : p.Name)
             .Key("face").Str(problem.FaceOf(p.Min, p.Max)!)
             .Key("origin").Point(r.Origin).Key("u").Point(r.U).Key("v").Point(r.V)
             .Key("path").BeginObject().Key("from").Point(path.From).Key("to").Point(path.To).EndObject()
             .Key("z0").Number(p.Z0.Real).Key("shiftM").Number(p.ReferencePlane.ShiftM)
             .Key("mixed").Bool(faceMaterials.Count > 1);
            if (group is not null)
            {
                // A face of one material: its modes are solved once, before the sweep, around this effective index
                // (R-emw3-4: 0.9 of the material's own, off the TEM eigenvalue EMerge's own shift sits on).
                if (faceMaterials.Count == 1)
                    e.Key("presolveNeff").Number(EmergeLoweringRules.PresolveNeff(problem, faceMaterials[0]));
                e.Key("terminals").BeginArray();
                foreach (var t in group)
                {
                    var tp = t.VoltagePath!.Value;
                    e.BeginObject().Key("number").Int(t.Number).Key("name").Str(t.SourceLabel ?? t.Name)
                     .Key("path").BeginObject().Key("from").Point(tp.From).Key("to").Point(tp.To).EndObject()
                     .EndObject();
                }
                e.EndArray();
            }
            e.EndObject();
        }
        e.EndArray();

        e.Key("lumpedElements").BeginArray();
        foreach (var le in problem.LumpedElements)
        {
            var r = EmergeLoweringRules.Rect(le.Min, le.Max, le.Direction)!.Value;
            e.BeginObject().Key("name").Str(le.Name)
             .Key("origin").Point(r.Origin).Key("u").Point(r.U).Key("v").Point(r.V)
             .Key("widthM").Number(r.Width).Key("heightM").Number(r.Height)
             .Key("topology").Str(le.Topology == Em3dRlcTopology.Series ? "series" : "parallel")
             .Key("r").OptionalNumber(le.R).Key("l").OptionalNumber(le.L).Key("c").OptionalNumber(le.C)
             .EndObject();
        }
        e.EndArray();

        e.Key("meshRegions").BeginArray();
        foreach (var region in problem.MeshRegions)
        {
            // GmshGeoWriter.BoxField's sizes: the region's own inside, the largest element outside, and a transition over
            // which the size grows at the region's grading (Palace's default when it states none).
            double inside = Math.Min(region.SizeM, sizes.Largest);
            double grading = region.Grading is { } g && g > 1 ? g : PalaceSettings.Default.Grading;
            e.BeginObject().Key("name").Str(region.Name).Key("min").Point(region.Min).Key("max").Point(region.Max)
             .Key("sizeM").Number(inside).Key("thicknessM").Number((sizes.Largest - inside) / (grading - 1))
             .EndObject();
        }
        e.EndArray();

        e.Key("files").BeginArray();
        foreach (var f in files) e.BeginObject().Key("name").Str(f.FileName).Key("sha256").Str(f.Sha256).EndObject();
        e.EndArray();
        e.EndObject();

        return new(e + "\n", files, null, EmergeLoweringRules.Mappings(problem, settings, sizes.Edge), sizes.Edge, sizes.Largest);
    }

    /// <summary>Writes the driver, problem.json and the kernel files into <paramref name="runDirectory"/>.</summary>
    public static void WriteInto(EmergeLowering lowering, string runDirectory)
    {
        if (lowering.ProblemJson is not { } json) throw new InvalidOperationException("A refused lowering writes nothing.");
        Directory.CreateDirectory(runDirectory);
        Cells.AtomicFile.WriteAllText(Path.Combine(runDirectory, DriverFile), Driver);
        Cells.AtomicFile.WriteAllText(Path.Combine(runDirectory, ProblemFile), json);
        foreach (var f in lowering.Files) f.WriteInto(runDirectory);
    }

    // ── sizes ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// R-emi4-3 — the largest element EMerge allows in each meshed material is its resolution times the wavelength there
    /// at the sizing frequency (EMerge's own rule, from the material's index); the edge size is the section's fraction of
    /// the smallest of those, as Palace's EdgeRefinement is. Conductors are not meshed for the answer and do not count.
    /// </summary>
    private static (double Edge, double Largest) Sizes(Em3dProblem problem, EmergeSettings settings, Materials materials)
    {
        double f = GmshGeoWriter.SizingFrequencyHz(problem);
        var asPalace = PalaceSettings.Default with { MaxElementWavelengths = settings.MaxElementWavelengths };
        var meshed = problem.Solids.Where(s => s.Role != Em3dRole.Conductor).Select(s => materials.Named(s.Material))
                            .Append(materials.BackgroundMaterial)
                            .Select(m => GmshGeoWriter.MaxElementSizeM(m, f, asPalace)).ToList();
        return (settings.EdgeRefinement * meshed.Min(), meshed.Max());
    }

    // ── materials ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The input's materials: one entry per material and use. A conductor whose material has no finite positive σ is
    /// EMerge's PEC (as Palace reads it), so the same material can be two entries — a perfect conductor where a conductor
    /// uses it, and itself where a dielectric does.
    /// </summary>
    private sealed class Materials
    {
        private readonly Dictionary<string, Em3dMaterial> _byName;
        private readonly Dictionary<(string, bool), int> _index = [];

        public List<(Em3dMaterial Material, bool Pec)> Entries { get; } = [];
        public int Background { get; }
        public Em3dMaterial BackgroundMaterial { get; }

        public Materials(Em3dProblem problem)
        {
            _byName = problem.Materials.ToDictionary(m => m.Name, StringComparer.Ordinal);
            string fill = problem.Boundary.Material ?? GmshGeoWriter.BackgroundMaterial(problem.Solids, problem.Materials);
            BackgroundMaterial = _byName.TryGetValue(fill, out var m) ? m : GmshGeoWriter.FreeSpace;
            _byName.TryAdd(BackgroundMaterial.Name, BackgroundMaterial);
            Background = Of(BackgroundMaterial.Name, conductor: false);
            foreach (var s in problem.Solids) Of(s.Material, s.Role == Em3dRole.Conductor);
            foreach (var sh in problem.Sheets) Of(sh.Material, conductor: true);
        }

        public Em3dMaterial Named(string name) => _byName[name];

        public int Of(string name, bool conductor)
        {
            var m = _byName[name];
            bool pec = conductor && EmergeLoweringRules.IsPerfect(m);
            if (_index.TryGetValue((name, pec), out int i)) return i;
            _index[(name, pec)] = Entries.Count;
            Entries.Add((m, pec));
            return Entries.Count - 1;
        }
    }

    private static void WriteMaterial(Emitter e, Em3dMaterial m, bool pec)
    {
        e.BeginObject().Key("name").Str(m.Name).Key("pec").Bool(pec);
        if (!pec)
        {
            if (m.EpsrTensor is { Count: 3 } t) e.Key("erTensor").Numbers(t);
            else e.Key("er").Number(m.Epsr);
            e.Key("tand").Number(m.TanD).Key("ur").Number(m.Mur).Key("cond").Number(m.SigmaSm);
        }
        e.EndObject();
    }

    // ── shapes ────────────────────────────────────────────────────────────────────────────────

    private static void WriteShape(Emitter e, Em3dPrimitive primitive)
    {
        e.BeginObject();
        switch (primitive)
        {
            case Em3dBox b:
                e.Key("kind").Str("box").Key("min").Point(b.Min).Key("max").Point(b.Max);
                break;
            case Em3dCylinder c:
                e.Key("kind").Str("cylinder").Key("start").Point(c.AxisStart)
                 .Key("axis").Point(new Point3(c.AxisEnd.X - c.AxisStart.X, c.AxisEnd.Y - c.AxisStart.Y, c.AxisEnd.Z - c.AxisStart.Z))
                 .Key("radius").Number(c.Radius);
                break;
            case Em3dSphere s:
                e.Key("kind").Str("sphere").Key("center").Point(s.Center).Key("radius").Number(s.Radius);
                break;
            case Em3dTruncatedSphere t:
            {
                // GmshGeoWriter's: the sphere, kept inside a box 1 % wider than it between the two heights.
                double z0 = Math.Max(t.ZMin, t.Center.Z - t.Radius), z1 = Math.Min(t.ZMax, t.Center.Z + t.Radius);
                bool whole = z0 <= t.Center.Z - t.Radius && z1 >= t.Center.Z + t.Radius;
                e.Key("kind").Str(whole ? "sphere" : "truncatedSphere").Key("center").Point(t.Center).Key("radius").Number(t.Radius);
                if (!whole)
                {
                    double r = 1.01 * t.Radius;
                    e.Key("clipMin").Point(new Point3(t.Center.X - r, t.Center.Y - r, z0))
                     .Key("clipMax").Point(new Point3(t.Center.X + r, t.Center.Y + r, z1));
                }
                break;
            }
            case Em3dEllipsoid el:
            {
                e.Key("kind").Str("ellipsoid").Key("center").Point(el.Center).Key("semiAxes").Numbers([el.A, el.B, el.C]);
                if (GmshGeoWriter.AxisAngle(el) is var (ax, ay, az, angle))
                    e.Key("rotationAxis").Point(new Point3(ax, ay, az)).Key("rotationAngle").Number(angle);
                break;
            }
            case Em3dExtrudedPolygon x:
                e.Key("kind").Str("extrusion")
                 .Key("rings").Rings(GmshGeoWriter.PlanarRings(new[] { x.Outline }.Concat(x.Holes))
                                     .Select(r => r.Select(q => new Point3(q.X, q.Y, x.ZBottom))))
                 .Key("height").Number(x.ZTop - x.ZBottom);
                break;
            case Em3dSweep w:
                e.Key("kind").Str("sweep").Key("round").Bool(w.Section == Em3dSection.Circle).Key("rings").Rings(w.Rings);
                break;
            case Em3dPolyhedron ph:
                e.Key("kind").Str("polyhedron").Key("vertices").BeginArray();
                foreach (var v in ph.Vertices) e.Point(v);
                e.EndArray().Key("faces").BeginArray();
                foreach (var face in GmshGeoWriter.PolyhedronLoops(ph))
                {
                    e.BeginArray(inline: true);
                    foreach (var ring in face) e.Ints(ring);
                    e.EndArray();
                }
                e.EndArray();
                break;
            case Em3dShapeSolid k:
                // D12: the kernel's B-rep is in micrometres, so the driver scales it into metres after importing it.
                e.Key("kind").Str("brep").Key("file").Str(GmshGeoWriter.KernelFileName(k)).Key("scale").Number(1e-6);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(primitive), primitive.GetType().Name, "unknown primitive");
        }
        e.EndObject();
    }

    private static string RoleWord(Em3dRole role) => role switch
    {
        Em3dRole.Conductor => "conductor",
        Em3dRole.Air       => "air",
        _                  => "dielectric",
    };

    private static string FaceWord(Em3dBoundaryKind kind) => kind switch
    {
        Em3dBoundaryKind.Pmc       => "pmc",
        Em3dBoundaryKind.Absorbing => "absorbing",
        _                          => "pec",
    };
}
