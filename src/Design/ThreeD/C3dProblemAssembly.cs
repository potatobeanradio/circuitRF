// brief-em3d-42 R-em3d42-5 — a .c3d's setups, and the problem a setup makes of its elaboration.
//
// SETUPS ARE IN TWO PLACES AND ONE SCHEMA (owner decision D5). A .c3d embeds EmSetup objects in its Setups
// list, written and read by EmSetupPersistence's own serializer less LayoutRef (the geometry is the
// document). And a .cem whose LayoutRef names a .c3d elaborates that document; then the .c3d's embedded
// setups are NOT consulted — the .cem is the setup. Either way a planar analysis is refused: a .c3d has
// no stackup for a planar kernel to read, only solids.
//
// THE PROBLEM is the elaboration plus the setup: the sweep, the temperature, the problem type, and the air
// box padded around the elaborated content by the generator's own padding code (Em3dGenerator.PaddedAirBox
// — shared, so a .cem and a .c3d cannot disagree about a box). There is no air solid: the background is
// air. brief-em3d-49: the document's ports (C3dPorts — polarity by contact, measured against THIS box, so a
// wave port is checked against the setup being run) and its face boundaries join it. A static setup's terminals
// name nets — a layout instance's nets, or a drawn conductor's own name — or, from brief 49, objects by name.
//
// WHERE RESULTS LAND (R-em3d42-5d). EmRunService.ResolveSnpPath is predictable by design. An embedded
// setup's result is named as if a .cem called "<c3d stem> <setup name>.cem" stood beside the .c3d — so
// the run's setup is named "<c3d stem> <setup name>", which is the key that path derives from.

using System.Globalization;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.ThreeD;

/// <summary>One embedded setup, read — or why it could not be.</summary>
public sealed record C3dEmbeddedSetup(int Index, string Name, EmSetup? Setup, string? Refusal);

/// <summary>brief-em3d-98 — what of a document one setup's run reads (<see cref="C3dSetups.ScopeOf"/>).</summary>
public sealed record C3dRunScope(IReadOnlySet<string> Setups, bool Thermal, bool Gmsh, IReadOnlySet<int>? Ports);

public static class C3dSetups
{
    /// <summary>The sentence a planar setup gets, from either container.</summary>
    public const string PlanarRefusal = "a .c3d is solved by the 3D solvers; set Solver3D to Palace or openEMS";

    /// <summary>Every embedded setup, in file order, each read by the .cem reader and checked: a name,
    /// unique, and a 3D solver — or, brief-em3d-73, a thermal setup, which names none.</summary>
    public static IReadOnlyList<C3dEmbeddedSetup> Read(C3dDocument doc)
    {
        var read = new List<C3dEmbeddedSetup>();
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < doc.Setups.Count; i++)
        {
            EmSetup setup;
            try { setup = EmSetupPersistence.FromEmbedded(doc.Setups[i]); }
            catch (Exception e)
            {
                read.Add(new C3dEmbeddedSetup(i, $"#{i + 1}", null, $"Embedded setup #{i + 1} cannot be read: {e.Message}"));
                continue;
            }
            string name = setup.Name;
            if (name.Length == 0)
                read.Add(new C3dEmbeddedSetup(i, $"#{i + 1}", null,
                    $"Embedded setup #{i + 1} has no Name; a .c3d's setups are chosen by name."));
            else if (names.TryGetValue(name, out int first))
                read.Add(new C3dEmbeddedSetup(i, name, null,
                    $"Embedded setups #{first + 1} and #{i + 1} are both named '{name}'; a setup's name is unique in its document."));
            // brief-em3d-73 R-em3d73-5a — a thermal setup names no EM solver, and is no planar analysis either.
            else if (setup.IsThermal && !setup.Solver3D.IsNone)
                read.Add(new C3dEmbeddedSetup(i, name, null, C3dThermal.SolverStated(name, setup.Solver3D)));
            else if (setup.IsThermal)
                read.Add(new C3dEmbeddedSetup(i, name, setup, null));
            else if (!setup.Is3D)
                read.Add(new C3dEmbeddedSetup(i, name, null, $"Embedded setup '{name}' is a planar analysis: {PlanarRefusal}."));
            else
                read.Add(new C3dEmbeddedSetup(i, name, C3dPlaneWaves.Apply(setup, doc), null));   // brief-hzn-12 — the view's wave
            names.TryAdd(name, i);
        }
        return read;
    }

    /// <summary>
    /// The setup <c>em x.c3d</c> runs: the one there is, or the one <paramref name="name"/> names. Several and
    /// no name is a refusal LISTING them (render --view's pattern), answered by <c>--setup</c>.
    /// </summary>
    public static (EmSetup? Setup, string? Refusal) Select(C3dDocument doc, string? name)
    {
        var all = Read(doc);
        string List() => string.Join(", ", all.Select(s => $"'{s.Name}'"));
        if (all.Count == 0)
            return (null, "This 3D view embeds no EM setup. Add one to its Setups, or point a .cem's LayoutRef at it.");
        C3dEmbeddedSetup chosen;
        if (name is not null)
        {
            if (all.FirstOrDefault(s => s.Name == name) is not { } hit)
                return (null, $"This 3D view has no setup named '{name}'; it has {List()}.");
            chosen = hit;
        }
        else if (all.Count > 1)
            return (null, $"This 3D view embeds {all.Count} setups ({List()}); name one with --setup <name>.");
        else chosen = all[0];
        return chosen.Refusal is { } why ? (null, why) : (chosen.Setup, null);
    }

    /// <summary>R-em3d42-5d — the embedded setup as its run uses it: named "<c>&lt;c3d stem&gt; &lt;setup name&gt;</c>",
    /// as if a <c>.cem</c> of that name stood beside the <c>.c3d</c>, so its result path is predictable.</summary>
    public static EmSetup ForRun(EmSetup embedded, string c3dPath)
    {
        var s = embedded.Clone();
        s.Name = $"{Path.GetFileNameWithoutExtension(c3dPath)} {embedded.Name}";
        s.LayoutRef = Path.GetFileName(c3dPath);
        // brief-sar-3 R-sar3-5 — a relative grid file is read beside the .c3d that embeds the setup.
        s.OpenEms = CemOpenEms.Anchored(s.OpenEms, c3dPath);
        return s;
    }

    /// <summary>
    /// brief-em3d-98 — the setups a run of <paramref name="setupName"/> is solved from: itself, and a thermal submodel's
    /// <c>From</c> setup (whose result fixes its cut faces). Every other setup is not part of its model, so editing, adding
    /// or removing one never makes this setup's result out of date (<see cref="C3dPersistence.SerializeForRun(C3dDocument, string?)"/>).
    /// A name no setup has (an external <c>.cem</c>'s run, spelt <c>""</c>) is solved from none of them.
    /// </summary>
    public static IReadOnlySet<string> SolvedWith(C3dDocument doc, string setupName)
    {
        var names = new HashSet<string>(StringComparer.Ordinal) { setupName };
        if (Read(doc).FirstOrDefault(s => s.Name == setupName)?.Setup?.Thermal?.Submodel?.From is { Length: > 0 } from) names.Add(from);
        return names;
    }

    /// <summary>
    /// brief-em3d-98 — what of the document a run of <paramref name="setupName"/> reads, beyond the geometry every run reads:
    /// which setups (<see cref="SolvedWith"/>), whether it is a thermal run, whether it meshes with Gmsh (and so reads the
    /// mesh regions), and which ports (null: all of them). A thermal run reads only the ports its currents name — all of them
    /// when a current comes from a circuit, whose pins map onto the ports by number. A setup that is not embedded (an external
    /// <c>.cem</c>'s, which is never thermal) or cannot be read is taken as an EM run reading every port and the mesh regions.
    /// </summary>
    public static C3dRunScope ScopeOf(C3dDocument doc, string setupName)
    {
        var names = SolvedWith(doc, setupName);
        var read = Read(doc);
        if (read.FirstOrDefault(s => s.Name == setupName)?.Setup is not { } mine) return new C3dRunScope(names, false, true, null);
        if (!mine.IsThermal)
            return new C3dRunScope(names, false, mine.Solver3D.IsNone || Em3d.Em3dSolvers.Any(mine.Solver3D, c => c.MeshRegions) ||
                                                 mine.OpenEms?.MeshRegions == true, null);
        var currents = names.Select(n => read.FirstOrDefault(s => s.Name == n)?.Setup).OfType<EmSetup>()
                            .SelectMany(s => s.Thermal?.Currents ?? []).ToList();
        IReadOnlySet<int>? ports = currents.Any(c => c.FromCircuit is not null) ? null : currents.Select(c => c.Port).OfType<int>().ToHashSet();
        return new C3dRunScope(names, true, true, ports);
    }

    /// <summary>True when a <c>.cem</c>'s resolved geometry is a 3D view rather than a layout.</summary>
    public static bool IsThreeDView(string? geometryPath)
        => geometryPath is not null && geometryPath.EndsWith(C3dPersistence.Extension, StringComparison.OrdinalIgnoreCase);
}

public static class C3dProblemAssembly
{
    /// <summary>
    /// R-em3d49-2b — the setup's ground set: the conductors on each of its Ground3D nets (a drawn conductor's net is its own name)
    /// and a layout instance's ground-reference conductors. A PEC air-box face joins it in <see cref="C3dPortContext"/>.
    /// </summary>
    public static IReadOnlyList<string> GroundSet(EmSetup? setup, C3dElaboration e)
    {
        var set = new List<string>(e.GroundBandObjects);
        // brief-dfr34-4 R-dfr34-4-1 — Ground3D is a list; each name is a net or an object.
        foreach (string net in Em3dGroundList.Names(setup?.Ground3D))
        {
            set.AddRange(e.ObjectNets.Where(kv => kv.Value.Contains(net)).Select(kv => kv.Key));
            if (e.ObjectNets.ContainsKey(net)) set.Add(net);
        }
        return [.. set.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The port context <paramref name="setup"/>'s run measures ports against: this elaboration, this box.</summary>
    public static C3dPortContext PortContext(EmSetup? setup, C3dDocument document, C3dElaboration e, Em3dAirBox? box)
        => new(e, document.DbuPerMicron, box, GroundSet(setup, e));

    /// <summary>Why an elaboration with no solved content has nothing to solve — counting the objects the solver ignores
    /// for having no material, when that is why (3D editor bugs round 2).</summary>
    public static string NothingToSolve(C3dElaboration e)
    {
        int ignored = e.UnassignedSolids.Count + e.UnassignedSheets.Count;
        return ignored == 0
            ? "This 3D view holds no solid or sheet, so there is nothing to solve."
            : $"None of this 3D view's {ignored} object{(ignored == 1 ? "" : "s")} has a material, and the solver ignores an " +
              "object with none, so there is nothing to solve. Give one a material.";
    }

    /// <summary>brief-em3d-93 — every object of the view is turned off.</summary>
    public const string EverythingOff =
        "Every object of this 3D view is turned off (Model), so there is nothing to solve. Turn one on.";

    /// <summary>brief-em3d-93 — a face boundary on an object that is not modelled, naming both; null when none is.</summary>
    public static string? NotModelledBoundary(C3dDocument document, C3dElaboration drawn)
    {
        foreach (var b in document.FaceBoundaries)
        {
            string target = C3dKernelUse.Resolve(document, b.Object, b.Face).Object;
            if (C3dModelled.IsOff(drawn, target))
                return C3dModelled.Reference($"The boundary on face '{b.Face}' of '{b.Object}' is on", target, "remove the boundary");
        }
        return null;
    }

    /// <summary>
    /// brief-em3d-88 R-em3d88-2 — the problem a 3D VIEW draws: the solids, sheets and materials it elaborated (the 3D editor
    /// adds its pushed-in context and its unassigned objects to them), in <paramref name="box"/>, with <paramref name="ports"/>,
    /// at a nominal frequency and temperature no picture reads. The editor builds its scene from this whatever the active
    /// setup's kind, and <c>render</c> builds the picture of a view with no EM problem from it — a thermal setup's section,
    /// or a view with no setup at all — so neither borrows another setup's problem.
    /// </summary>
    public static Em3dProblem ViewProblem(IReadOnlyList<Em3dSolid> solids, IReadOnlyList<Em3dSheet> sheets, IReadOnlyList<Em3dMaterial> materials,
                                          IReadOnlyList<Em3dPort> ports, Em3dAirBox box, IReadOnlyList<Em3dCylinderSheet>? cylinderSheets = null)
        => new(solids, sheets, materials, ports, box, new Em3dFrequency(1e9, 1e9, 1, Em3dSweepKind.Linear), EmSetup.DefaultOperatingTempC)
           { CylinderSheets = cylinderSheets ?? [] };

    /// <summary>A box at <paramref name="extent"/> with every face absorbing: what a view is drawn in when no setup pads one.</summary>
    public static Em3dAirBox ExtentBox((double X0, double Y0, double Z0, double X1, double Y1, double Z1) extent)
    {
        var a = Em3dBoundaryKind.Absorbing;
        return new Em3dAirBox(new Point3(extent.X0, extent.Y0, extent.Z0), new Point3(extent.X1, extent.Y1, extent.Z1), new Em3dFaces(a, a, a, a, a, a));
    }

    /// <summary>
    /// The air box <paramref name="setup"/> puts around <paramref name="e"/> — the one a run of it solves in, which is also
    /// the one the editor draws (R-em3d49-3a) and a wave port must lie on. Null when there is no content.
    /// </summary>
    public static Em3dAirBox? AirBox(EmSetup setup, C3dElaboration e, out string? refusal)
    {
        refusal = null;
        if (e.ModelledExtent() is not { } extent) return null;     // brief-em3d-93 — the run's box: modelled content only
        double fMin;
        try { fMin = setup.ExpandFrequencies().Where(f => f > 0).DefaultIfEmpty(1e9).Min(); }
        catch (Exception) { fMin = 1e9; }
        return Em3dGenerator.PaddedAirBox(setup, (extent.X0, extent.Y0, extent.X1, extent.Y1, extent.Z0, extent.Z1),
                                          fMin, PlaneFloor(setup, e), waves: [], [], out refusal, PaddingHeight(e),
                                          PaddingHeightBelow(e));
    }

    /// <summary>
    /// Designer round 33 — the underside of an unbroken ground plane at the bottom of what a run models, where the air box's
    /// floor goes (a PEC face) when the setup states nothing for ZMin: a layout instance's object on its lowest
    /// ground-reference conductor, with no hole in it, flush with the bottom of everything, covering
    /// <see cref="Em3dGenerator.PlaneFloorCover"/> of the modelled footprint. Null otherwise — the floor is then padded like any face.
    /// </summary>
    public static double? PlaneFloor(EmSetup setup, C3dElaboration e) => PlaneFloor(setup, e, out _);

    /// <summary><see cref="PlaneFloor(EmSetup, C3dElaboration)"/>, and whether any of the floor's metal is on a conductor the
    /// technology does not mark ground-reference (brief-dfr34-2 R-dfr34-2-4: coverage decides, the flag does not).</summary>
    public static double? PlaneFloor(EmSetup setup, C3dElaboration e, out bool unmarked)
    {
        unmarked = false;
        if (setup.AirBox?.ZMin is not null || !Em3dGenerator.ByHeightApplies(setup) || e.ModelledExtent() is not { } x) return null;
        // brief-dfr34-2 R-dfr34-2-4 — the bottom-most layout conductor flush with the bottom of everything is the candidate,
        // marked ground-reference or not: the flags on an imported board describe the technology file, not the board.
        var marked = e.GroundBandObjects.ToHashSet(StringComparer.Ordinal);
        var ground = e.Solids.Where(s => s.Role == Em3dRole.Conductor && !e.NotModelled.Contains(s.Name)
                                         && (marked.Contains(s.Name) || e.Origins.TryGetValue(s.Name, out var o)
                                             && o.Kind == Em3dObjectKind.Conductor && o.StackupEntry is not null)
                                         && s.Primitive is Em3dExtrudedPolygon ep && Math.Abs(ep.ZBottom - x.Z0) <= 1e-9)
                             .Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var planeSolids = e.Solids.Where(s => ground.Contains(s.Name)).ToList();
        var plane = planeSolids.Select(s => (Em3dExtrudedPolygon)s.Primitive).ToList();
        if (plane.Count == 0) return null;
        // brief-emb-2 R-emb2-5 — a hole is allowed where no other conductor (outside the ground set) lies over it. Metal on the
        // plane's own net (a ground via's barrel, a ground pin) is joined to the plane already, so it does not count either.
        if (plane.Any(p => p.Holes.Count > 0))
        {
            var planeNets = planeSolids.SelectMany(s => e.ObjectNets.TryGetValue(s.Name, out var n) ? n : []).ToHashSet(StringComparer.Ordinal);
            bool OwnNet(string name) => e.ObjectNets.TryGetValue(name, out var n) && n.Count > 0 && n.All(planeNets.Contains);
            var others = e.Solids.Where(s => s.Role == Em3dRole.Conductor && !ground.Contains(s.Name) && !marked.Contains(s.Name) && !e.NotModelled.Contains(s.Name)
                                             && !OwnNet(s.Name)).ToList();
            static bool Upright(Em3dPrimitive p) => p is Em3dCylinder c &&
                Math.Abs(c.AxisStart.X - c.AxisEnd.X) < 1e-12 && Math.Abs(c.AxisStart.Y - c.AxisEnd.Y) < 1e-12;
            var drills = others.Select(s => s.Primitive).Where(Upright).Cast<Em3dCylinder>()
                               .Select(c => (c.AxisStart.X, c.AxisStart.Y, c.Radius)).ToList();
            var metal = others.Where(s => !Upright(s.Primitive))
                              .Select(s => s.Primitive is Em3dExtrudedPolygon ep ? ep.Outline : Rect(Em3dProblem.Bounds(s.Primitive)));
            if (Em3dGenerator.CoveredHole(plane.SelectMany(p => p.Holes), metal, drills) is not null) return null;
        }
        if (!Em3dGenerator.PlaneCoversFootprint(plane.Select(p => (IReadOnlyList<(double X, double Y)>)[.. p.Outline.Select(q => (q.X, q.Y))]),
                                                x.X0, x.Y0, x.X1, x.Y1, PaddingHeight(e)))
            return null;
        unmarked = planeSolids.Any(s => !marked.Contains(s.Name));
        return x.Z0;

        static IReadOnlyList<Point2> Rect((double X0, double Y0, double Z0, double X1, double Y1, double Z1) b)
            => [new(b.X0, b.Y0), new(b.X1, b.Y0), new(b.X1, b.Y1), new(b.X0, b.Y1)];
    }

    /// <summary>Designer round 33 — H for the default padding, read off the content a run models.</summary>
    private static double? PaddingHeight(C3dElaboration e)
        => Em3dGenerator.PaddingHeight(e.Solids.Where(s => !e.NotModelled.Contains(s.Name)),
                                       e.Sheets.Where(s => !e.NotModelled.Contains(s.Name)), floorZ: null);

    /// <summary>brief-dfr34-2 R-dfr34-2-3 — the floor's H, from the bottom metal upward, read off the content a run models.</summary>
    private static double? PaddingHeightBelow(C3dElaboration e)
        => Em3dGenerator.PaddingHeightBelow(e.Solids.Where(s => !e.NotModelled.Contains(s.Name)),
                                            e.Sheets.Where(s => !e.NotModelled.Contains(s.Name)));

    /// <summary>Designer feedback round 11 — why a face the setup leaves unstated stands where it does ("λ/8 at 2 GHz, the
    /// sweep's lowest frequency"), on the same sweep and extent <see cref="AirBox"/> pads by. Null when there is no content.</summary>
    public static string? DefaultPaddingBasis(EmSetup setup, C3dElaboration e, string face)
    {
        if (e.ModelledExtent() is not { } x) return null;
        double fMin;
        try { fMin = setup.ExpandFrequencies().Where(f => f > 0).DefaultIfEmpty(1e9).Min(); }
        catch (Exception) { fMin = 1e9; }
        return Em3dGenerator.DefaultFacePadding(setup, fMin, Math.Max(Math.Max(x.X1 - x.X0, x.Y1 - x.Y0), x.Z1 - x.Z0),
                                                PaddingHeight(e), PaddingHeightBelow(e)).Basis(face, face[0] switch
                                                {
                                                    'x' => x.X1 - x.X0, 'y' => x.Y1 - x.Y0, _ => x.Z1 - x.Z0,
                                                });
    }

    /// <summary>brief-dfr34-2 R-dfr34-2-5 — the same rule in a few words ("10 × H", "λ/4 at 2.45 GHz"), as the panel prints it
    /// after the length. Null when there is no content.</summary>
    public static string? DefaultPaddingShortBasis(EmSetup setup, C3dElaboration e, string face)
    {
        if (e.ModelledExtent() is not { } x) return null;
        double fMin;
        try { fMin = setup.ExpandFrequencies().Where(f => f > 0).DefaultIfEmpty(1e9).Min(); }
        catch (Exception) { fMin = 1e9; }
        return Em3dGenerator.DefaultFacePadding(setup, fMin, Math.Max(Math.Max(x.X1 - x.X0, x.Y1 - x.Y0), x.Z1 - x.Z0),
                                                PaddingHeight(e), PaddingHeightBelow(e)).ShortBasis(face, face[0] switch
                                                {
                                                    'x' => x.X1 - x.X0, 'y' => x.Y1 - x.Y0, _ => x.Z1 - x.Z0,
                                                });
    }

    /// <summary>
    /// brief-em3d-49 R-em3d49-4d — each face boundary on its own, for drawing: its pieces in the world, or why it cannot
    /// be placed. The same resolution <see cref="FaceBoundaries"/> makes, one boundary at a time, so one refusal does not
    /// hide the others.
    /// </summary>
    public static IReadOnlyList<(C3dFaceBoundary Boundary, IReadOnlyList<Em3dFacePolygon> Pieces, string? Refusal)> FaceBoundaryPreview(
        C3dDocument document, C3dElaboration e)
    {
        var list = new List<(C3dFaceBoundary, IReadOnlyList<Em3dFacePolygon>, string?)>();
        foreach (var b in document.FaceBoundaries)
        {
            var one = new C3dDocument { Objects = document.Objects, FaceBoundaries = [b] };
            var materials = e.Materials.ToList();
            if (FaceBoundaries(one, e, materials, EmSetup.DefaultOperatingTempC, out var resolved) is { } why)
            {
                list.Add((b, [], why));
                continue;
            }
            var solid = e.Solids.First(s => s.Name == b.Object);
            list.Add((b, [.. resolved.SelectMany(r => Em3dFaceGeometry.Pieces(solid.Primitive, r.Face, out _) ?? [])], null));
        }
        return list;
    }

    /// <summary>
    /// R-em3d49-4 — the document's face boundaries as the neutral problem states them: each on its object's elaborated
    /// solid, the document's face name carried to the primitive's own through the provenance map (a box turned a quarter
    /// turn has its document xmin on its world ymin). A conductive face's metal resolves in the document's technology.
    /// A refusal names the object and the face — never a guess (§6.4).
    /// </summary>
    public static string? FaceBoundaries(C3dDocument document, C3dElaboration e, List<Em3dMaterial> materials, double tempC,
                                         out List<Em3dFaceBoundary> boundaries)
    {
        boundaries = [];
        foreach (var b in document.FaceBoundaries)
        {
            string where = $"The boundary on face '{b.Face}' of '{b.Object}'";
            if (b.Kind is Em3dFaceBoundaryKind.Absorbing or Em3dFaceBoundaryKind.Pmc or Em3dFaceBoundaryKind.Symmetry)
                return $"{where} is {b.Kind}, which both 3D solvers state only on the problem's outer boundary: set it on a face " +
                       "of the setup's air box. A solid's face may be Pec or Conductive.";
            var obj = document.Objects.FirstOrDefault(o => o.Name == b.Object);
            if (obj is null) return $"{where} names no object of this 3D view.";
            bool sheet = C3dSheetBooleans.IsSheet(obj) || obj is C3dCylinderSheet;
            if (sheet || obj is C3dPolyline)
                return $"{where} is on a {(sheet ? "sheet, which is already a conductor's surface" : "polyline, which is construction geometry")}: " +
                       "a boundary goes on a face of a dielectric or air solid.";
            // brief-em3d-64 R-em3d64-2f — through a disabled operation (lid, bore:side) is the standalone bore's side;
            // nothing in the file is rewritten.
            var (target, targetFace) = C3dKernelUse.Resolve(document, b.Object, b.Face);
            if (e.Solids.FirstOrDefault(s => s.Name == target) is not { } solid || !e.Provenance.TryGetValue(target, out var prov))
                return $"{where} is on an object the elaboration did not make a solid of; its own refusal says why.";
            if (solid.Role == Em3dRole.Conductor)
                return $"{where} is on a conductor, which is a void bounded by its own metal: a boundary on it has nothing to add. " +
                       "Put boundaries on dielectric and air objects.";
            var primNames = Em3dFaceGeometry.FaceNames(solid.Primitive);
            // The fold rule: a reference to zmax covers every piece an operation split it into (zmax#1, zmax#2).
            var mine = Enumerable.Range(0, Math.Min(prov.FaceNames.Count, primNames.Count))
                                 .Where(i => C3dKernelUse.Covers(targetFace, prov.FaceNames[i])).Select(i => primNames[i]).Distinct().ToList();
            // brief-em3d-67 R-em3d67-6d — a fillet's or chamfer's own face, while that feature is disabled: said as that.
            if (mine.Count == 0 && C3dFillets.Chain(obj).FirstOrDefault(f => !f.Feature.Enabled &&
                    b.Face.StartsWith(f.Feature is C3dChamfer ? "chamfer(" : "fillet(", StringComparison.Ordinal)).Feature is { } off)
                return "A face boundary cannot be placed: " + C3dFillets.OnlyWhileEnabled(b.Face, b.Object, off);
            if (mine.Count == 0)
                return $"{where} names a face '{b.Object}' does not have (it has " +
                       $"{string.Join(", ", (solid.Primitive is Em3dShapeSolid ? prov.FaceNames : obj.FaceNames()).Distinct())}). " +
                       "A face's name is kept through every edit; one that no longer exists is refused, never guessed.";
            string? material = null;
            if (b.Kind == Em3dFaceBoundaryKind.Conductive)
            {
                if (b.Material is not { Length: > 0 } m) return $"{where} is Conductive and names no Material.";
                if (e.Technology?.FindMaterial(m) is not { } tm)
                    return $"{where} is made of '{m}', which the 3D view's technology does not define.";
                var values = C3dElaborator.MaterialValues(tm, tempC);
                if (!(values.SigmaSm > 0)) return $"{where} is made of '{m}', which states no conductivity.";
                material = materials.FirstOrDefault(x => x.Name == tm.Name && x with { Name = "" } == values with { Name = "" })?.Name
                           ?? materials.FirstOrDefault(x => x.Name.StartsWith(tm.Name + "@", StringComparison.Ordinal) &&
                                                            x with { Name = "" } == values with { Name = "" })?.Name;
                if (material is null)
                {
                    material = materials.Any(x => x.Name == tm.Name) ? $"{tm.Name}@face" : tm.Name;
                    materials.Add(values with { Name = material });
                }
            }
            foreach (string face in mine)
            {
                if (Em3dFaceGeometry.Pieces(solid.Primitive, face, out string? why) is null)
                    return $"{where} cannot be placed: {why}.";
                boundaries.Add(new Em3dFaceBoundary(target, face, b.Kind, material));
            }
        }
        return null;
    }

    /// <summary>
    /// The 3D problem <paramref name="setup"/> makes of <paramref name="document"/>: elaborated at the setup's
    /// top frequency and temperature, then the air box, sweep and type. A refusal names what is missing.
    /// </summary>
    /// <param name="fromCem">True when the setup is a <c>.cem</c> pointing at the document (its embedded setups
    /// are then not consulted, and a note says so).</param>
    public static Em3dGenerationResult Assemble(EmSetup setup, C3dDocument document, string path, string? workspaceCws,
                                                C3dElaborator? elaborator = null, bool fromCem = false)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(document);
        var notes = new List<string>();
        Em3dGenerationResult No(string why, IReadOnlyList<string>? warnings = null)
            => new(null, why, notes) { Warnings = warnings ?? [] };

        if (setup.IsThermal) return No(fromCem ? C3dThermal.CemRefusal : C3dThermal.RunRefusal(setup.Name));
        if (!setup.Is3D) return No($"This setup is a planar analysis, and {C3dSetups.PlanarRefusal}.");
        // brief-hzn-12 — the view's enabled plane wave drives a Driven setup, a .cem's pointing at the view included
        setup = C3dPlaneWaves.Apply(setup.Clone(), document);

        // ── The sweep and the temperature, as the generator reads them ──────────────────────────
        double[] freqs;
        try { freqs = setup.ExpandFrequencies(); }
        catch (Exception ex) { return No(EmDiagnostics.FrequencySweepUnresolvable(ex.Message).Render()); }
        var positive = freqs.Where(f => f > 0).ToArray();
        if (positive.Length == 0) return No(EmDiagnostics.FrequencySweepEmpty().Render());
        double fMin = positive.Min(), fMax = positive.Max();
        // brief-emdc-2 R-emdc2-1 — a driven sweep's 0 Hz point is circuitRF's conduction solve (Em3dDcRow), not the solver's.
        bool wantsDc = positive.Length < freqs.Length && setup.Problem3D == Em3dProblemType.Driven;
        if (wantsDc) notes.Add(CircuitRF.Design.Em3d.Em3dDcRow.ZeroHzNote);
        var frequency = setup.ToEm3dFrequency(positive);
        double tempC = setup.OperatingTempC ?? EmSetup.DefaultOperatingTempC;
        if (fromCem && document.Setups.Count > 0)
            notes.Add($"The 3D view's own {document.Setups.Count} embedded setup(s) are not consulted: this .cem is the setup.");

        // ── The geometry: the elaboration the editor draws, less what is not modelled (brief-em3d-93) ──
        // brief-sar-2 R-sar2-3 — every dispersive material is evaluated once, before lowering: at the sweep's centre, or — for a
        // setup that requests SAR (brief-sar-6 R-sar6-1) — at its SAR frequency, and the elaboration's note says which.
        var sar = setup.RequestsSar ? SarSettings.Resolve(setup.Sar!) : null;
        if (sar is not null && SarFrequencyRefusal(sar, fMin, fMax) is { } outside) return No(outside);
        double materialHz = sar is { FrequenciesHz: [var sarHz, ..] } ? sarHz : MaterialFrequency(frequency);
        var drawn = (elaborator ?? new C3dElaborator()).Elaborate(document, path, workspaceCws, new C3dElaborationOptions(fMax, tempC)
        {
            MaterialFrequencyHz = materialHz, MaterialFrequencyBasis = sar is null ? "the sweep's centre" : "the SAR frequency",
            RobustMeshing = setup.RobustMeshing,
            Conductors = setup.Conductors,
            SolidVias = setup.Problem3D != Em3dProblemType.Thermal,
        });
        var e = C3dModelled.Filter(document, drawn);
        notes.AddRange(e.Notes);
        if (!e.Ok) return No(string.Join(" ", e.Refusals), e.Warnings);
        // brief-hzn-12 R-hzn12-1 — a scattering setup is driven by its plane wave alone: its ports (those modelled) refuse it.
        if (CircuitRF.Design.Em3d.Em3dPlaneWaves.SetupRefusal(setup, document.Ports.Count(p => p.Model) + setup.ComponentPorts.Count)
            is { } waveRefusal)
            return No(waveRefusal, e.Warnings);
        // brief-emb-1 R-emb1-3/4 — the component ports, built on the parts' own pads and verified before anything is meshed: their
        // landing rectangles and connector shells join the lowered problem (and only it), and the ports follow the document's own.
        var portDocument = document;
        C3dComponentPortBuild? components = null;
        if (setup.ComponentPorts.Count > 0 && setup.Problem3D != Em3dProblemType.Electrostatic)
        {
            // The pads come from the layout the copper came from: an open editor's, when the elaborator was handed one.
            components = C3dComponentPorts.Build(setup, document, drawn, FirstComponentPortNumber(document),
                                                 layoutsInMemory: elaborator?.LayoutsInMemory, layoutRegions: elaborator?.LayoutRegions);
            // A part whose port cannot be built is left out with a warning; the run refuses only when the build itself cannot be
            // made, or when nothing is left to drive.
            if (components is { Refusal: { } whole }) return No(whole, e.Warnings) with { ComponentPorts = components };
            if (components is not null && components.DocumentPorts.Count == 0 && document.Ports.Count == 0)
                return No(components.FirstRefusal ?? "No component port could be built.", e.Warnings) with { ComponentPorts = components };
            if (components is not null)
            {
                drawn = components.Elaboration;
                e = C3dModelled.Filter(document, drawn);
                notes.AddRange(components.Notes);
                portDocument = WithComponentPorts(document, components);
            }
        }
        if (sar is not null && SarDispersiveRefusal(sar, e) is { } twoFrequencies) return No(twoFrequencies, e.Warnings);
        if (e.Extent() is not { } extent)
            return No(drawn.Extent() is not null ? EverythingOff : NothingToSolve(e), e.Warnings);
        if (NotModelledBoundary(document, drawn) is { } offBoundary) return No(offBoundary, e.Warnings);

        // ── The air box, padded by the generator's own rule ─────────────────────────────────────
        double? paddingH = PaddingHeight(e);
        double? paddingHBelow = PaddingHeightBelow(e);
        double? planeFloor = PlaneFloor(setup, e, out bool unmarkedFloor);
        var box = Em3dGenerator.PaddedAirBox(setup, (extent.X0, extent.Y0, extent.X1, extent.Y1, extent.Z0, extent.Z1),
                                             fMin, planeFloor, waves: [], notes, out string? boxRefusal, paddingH, paddingHBelow);
        if (boxRefusal is not null || box is null) return No(boxRefusal ?? "The air box could not be built.", e.Warnings);
        notes.Add(planeFloor is not null
            ? "The air box's floor is the underside of the ground plane at the bottom of the geometry, as a PEC face: the plane " +
              "has no hole with other metal over it and lies under everything, so nothing below it carries field. State the AirBox's ZMin to pad below it instead." +
              (unmarkedFloor ? " The bottom conductor is treated as a floor because its metal covers at least " +
                               $"{(Em3dGenerator.PlaneFloorCover * 100).ToString("G3", System.Globalization.CultureInfo.InvariantCulture)} % of the footprint, " +
                               "though the technology does not mark it ground-reference." : "")
            : "The air box's floor is below the geometry: a 3D view states no floor of its own, so every face is " +
              "the setup's (absorbing unless its AirBox says otherwise).");

        var solids = e.Solids.ToList();
        var sheets = e.Sheets.ToList();
        var materials = e.Materials.ToList();
        // 3D editor round 3 — the air box's fill (owner decision): the document's AirBoxMaterial, Air when it names none — the
        // technology's material of that name, else (for Air and Vacuum) a built-in one with free space's values.
        var materialSources = new Dictionary<string, string>(e.MaterialSources, StringComparer.Ordinal);
        if (AirFill(document.AirBoxMaterial, e.Technology, tempC, out string fillWhy, materialHz) is not { } fill)
            return No(fillWhy, e.Warnings);
        if (!materials.Any(m => m.Name == fill.Material.Name))
        {
            materials.Add(fill.Material);
            materialSources.TryAdd(fill.Material.Name, fill.Source);
        }
        box = box with { Material = fill.Material.Name };
        // brief-hzn-12 — a plane wave enters through absorbing faces and its scattered field leaves through them.
        if (setup.IsScattering3D && CircuitRF.Design.Em3d.Em3dPlaneWaves.BoxRefusal(box) is { } closedBox) return No(closedBox, e.Warnings);

        // ── Ports (R-em3d49-2): the document's, polarity by contact ─────────────────────────────
        // The generator's rule by problem (R-em3d22-1c): an electrostatic solve has none, and a magnetostatic one only
        // the ports its terminals are driven through — so a port no solve reads can never refuse the run.
        // brief-em3d-93 — measured against the DRAWN conductors in the solve's box, so a modelled port on a conductor that is
        // not modelled is refused naming it (C3dPorts.Resolve) rather than inferring something else; a port that is off is
        // left out, and the rest are renumbered 1…N (C3dModelled.Renumber) — for a magnetostatic solve too they are not, since
        // its terminals name ports by their document numbers.
        var context = PortContext(setup, document, drawn, box);
        var ports = new List<Em3dPort>();
        // brief-dfr31-1 R-dfr31-1-7 — said only once the problem is built: under a refusal it read as a second error.
        string? noPortsNote = setup.Problem3D == Em3dProblemType.Electrostatic && document.Ports.Count > 0
            ? $"An electrostatic solve has no ports: the 3D view's {document.Ports.Count} port(s) are not in the problem." : null;
        if (setup.Problem3D == Em3dProblemType.Electrostatic && setup.ComponentPorts.Count > 0)
            noPortsNote = (noPortsNote is null ? "" : noPortsNote + " ") + "Its component ports are not built either.";
        if (setup.Problem3D != Em3dProblemType.Electrostatic && !setup.IsScattering3D)
        {
            var sources = setup.Problem3D == Em3dProblemType.Magnetostatic
                ? setup.Terminals3D.Select(t => t.Source?.Trim()).OfType<string>()
                       .Select(src => src.StartsWith("port/", StringComparison.Ordinal) ? src : "port/" + src).ToHashSet(StringComparer.Ordinal)
                : null;
            var modelled = new List<(C3dPort Port, Em3dPort Resolved)>();
            var off = new List<C3dPort>();
            var seams = new List<C3dSeam>();
            foreach (var r in C3dPorts.Resolve(portDocument, context))
            {
                if (sources is not null && !sources.Contains(C3dPorts.ProblemName(r.Port.Number))) continue;
                if (!r.Port.Model)
                {
                    if (sources is not null)
                        return No($"{C3dPorts.Label(r.Port)} drives a terminal of this magnetostatic setup and is not modelled (its Model " +
                                  "is off). Turn it back on, or drive the terminal through another port.", e.Warnings);
                    off.Add(r.Port);
                    continue;
                }
                if (r.Resolved is null) return No(r.Refusal!, e.Warnings);
                if (r.Note is { } note) notes.Add(note);                  // brief-em3d-121 D1
                foreach (var q in r.All) modelled.Add((r.Port, q));      // brief-em3d-114 — a terminal is a port
                seams.AddRange(r.Seams);
            }
            // A turned port's ends become points of its copper's outline, so the sheet and the copper meet exactly
            // (C3dTurnedPorts): moved by at most the contact tolerance, in the solved problem only.
            if (seams.Count > 0)
            {
                double reach = C3dTurnedPorts.Tolerance(context);
                if (C3dTurnedPorts.Apply(solids, seams, reach) is { } seamRefusal) return No(seamRefusal, e.Warnings);
                notes.Add($"{seams.Count / 2} turned port(s): the copper at each end takes the port's corners, moving its side by at most " +
                          $"{reach * 1e9:0.###} nm, so the sheet meets it exactly.");
            }
            if (setup.Problem3D == Em3dProblemType.Driven && portDocument.Ports.Count > 0 && modelled.Count == 0)
                return No(C3dModelled.AllPortsOff, e.Warnings);
            ports.AddRange(sources is null ? C3dModelled.Renumber(modelled, off, notes) : modelled.Select(m => m.Resolved));
            if (sources is not null && portDocument.Ports.Count > ports.Count)
                notes.Add($"{portDocument.Ports.Count - ports.Count} port(s) drive no terminal and are not in the magnetostatic problem.");
            if (EdgePortSheet.SheetClash(ports) is { } oneSheet) return No(oneSheet, e.Warnings);
        }

        // ── brief-sar-3 R-sar3-3 — lumped elements: a port's gap, by the port's own rules, carrying R, L and C ──────
        var lumped = new List<Em3dLumpedElement>();
        if (setup.IsStatic3D)
        {
            if (document.LumpedElements.Any(l => l.Model))
                notes.Add($"A static solve has no frequency for R, L and C: the 3D view's {document.LumpedElements.Count(l => l.Model)} lumped " +
                          "element(s) are not in the problem.");
        }
        else
            foreach (var r in C3dLumpedElements.Resolve(document, context, drawn.Resolution))
            {
                if (r.Resolved is null) return No(r.Refusal!, e.Warnings);
                lumped.Add(r.Resolved);
            }

        // ── Face boundaries (R-em3d49-4) ────────────────────────────────────────────────────────
        if (FaceBoundaries(document, e, materials, tempC, out var faceBoundaries) is { } boundaryRefusal)
            return No(boundaryRefusal, e.Warnings);

        // ── Terminals and ground (R-em3d22-2), by net ───────────────────────────────────────────
        List<Em3dTerminal> terminals = [];
        List<string> ground = [];
        if (setup.IsStatic3D && Em3dGenerator.ViaNetRefusal(setup, e.ViaNetConflicts) is { } viaNetRefusal)
            return No(viaNetRefusal, e.Warnings);
        // brief-dfr34-4 D1 — a via given its copper's net is said, with the layout edit that makes it so.
        var viaNetFixes = new Dictionary<string, Em3dViaNetFix>(StringComparer.Ordinal);
        if (setup.IsStatic3D)
            foreach (var a in e.ViaNetAdoptions.Values)
                if (viaNetFixes.TryAdd(a.Sentence, a.Fix)) notes.Add(a.Sentence);
        if (setup.IsStatic3D &&
            Em3dGenerator.Terminals(setup, solids, sheets, ports, pecFloor: false, e.ObjectNets.ToDictionary(kv => kv.Key, kv => kv.Value),
                                    [.. e.GroundBandObjects], out terminals, out ground, notes, e.CylinderSheets) is { } terminalRefusal)
            return No(terminalRefusal, e.Warnings);

        if (noPortsNote is not null) notes.Add(noPortsNote);
        var problem = new Em3dProblem(solids, sheets, materials, ports, box, frequency, tempC)
        {
            ThinMetalCorrection = setup.ThinMetalCorrection,
            Type = setup.Problem3D,
            PaddingHeightM = paddingH,
            PaddingHeightBelowM = paddingHBelow,
            FaceBoundaries = faceBoundaries,
            MeshRegions = MeshRegions(document),
            Terminals = terminals,
            GroundObjects = ground,
            EigenmodeCount = setup.Eigenmode?.Count ?? EmEigenmode3D.DefaultCount,
            EigenmodeTargetHz = setup.Eigenmode?.TargetGHz is { } target ? target * 1e9 : fMin,
            MeshFrequencyHz = setup.Problem3D == Em3dProblemType.Driven ? setup.MeshFrequencyGHz * 1e9 : null,
            PlaneWave = setup.IsScattering3D ? setup.PlaneWave!.Resolve(out _) : null,
            LumpedElements = lumped,
            CylinderSheets = e.CylinderSheets,
            SolidDensities = sar is null ? new Dictionary<string, double>() : SolidDensities(e, solids),
            ArtworkBarrels = Em3dGenerator.ArtworkBarrels(solids, e.Origins),
        };
        if (Em3dGenerator.MeshFrequencyNote(problem, setup) is { } meshedAt) notes.Add(meshedAt);
        notes.AddRange(Em3dThinMetalLoss.NotesForRun(problem, setup));
        // brief-dfr35-1 R-dfr35-1-7 — what is known to stop Gmsh, as a layout's run says it.
        var geometry = Em3dGeometryCheck.ForRun(problem, setup);
        return new Em3dGenerationResult(problem, null, notes)
        {
            ViaNetFixes = viaNetFixes,
            WantsDc = wantsDc,
            ComponentPorts = components,
            CoSimPorts = components is null ? null : EmCoSimNetlist.PortsOf(problem.Ports, document, components),
            ResultPorts = EmCoSimNetlist.PortsOf(problem.Ports, document, components),
            GeometryFindings = geometry,
            Warnings = [.. e.Warnings, .. components?.Warnings ?? [], .. Em3dGeometryCheck.Sentences(geometry), .. Em3dThinMetalLoss.ForRun(problem, setup)],
            Wires = e.Wires,
            Origins = e.Origins,
            MaterialSources = materialSources,
            FilesRead = e.FilesRead,
            Phantoms = [.. document.Objects.OfType<C3dPhantom>()
                .Select(ph => (ph.Name, (IReadOnlyList<string>)[.. ph.Layers.Select(l => C3dPhantoms.LayerName(ph.Name, l.Name))
                                                                         .Where(n => solids.Any(s => s.Name == n))]))
                .Where(ph => ph.Item2.Count > 0)
                // brief-sar-11 — a voxel body stands for its tissues in Sar.Region as a phantom does for its layers, in row order.
                .Concat(document.Objects.OfType<C3dVoxelBody>()
                    .Select(b => (b.Name, (IReadOnlyList<string>)[.. b.Labels.Select(l => C3dPhantoms.LayerName(b.Name, l.Name))
                                                                        .Where(n => solids.Any(s => s.Name == n)).Distinct()]))
                    .Where(b => b.Item2.Count > 0))],
        };
    }

    /// <summary>brief-emb-1 R-emb1-2 — component ports are numbered after every number the document's own ports use.</summary>
    public static int FirstComponentPortNumber(C3dDocument document)
        => C3dPorts.UsedNumbers(document).DefaultIfEmpty(0).Max() + 1;

    /// <summary>brief-emb-1 — the document's ports followed by the component ports, in a document carrying nothing else: what
    /// the port resolution reads (its ports and DBU). The document itself is never changed.</summary>
    public static C3dDocument WithComponentPorts(C3dDocument document, C3dComponentPortBuild build)
        => new() { DbuPerMicron = document.DbuPerMicron, Ports = [.. document.Ports, .. build.DocumentPorts] };

    /// <summary>
    /// brief-em3d-74 R-em3d74-4b — the document's mesh regions in world metres, as their fields resolved (elaboration resolves
    /// the document in place). A region whose box or size is not positive is left out: <c>check</c> reports it
    /// (<see cref="C3dThermal.Places(C3dDocument)"/>).
    /// </summary>
    public static IReadOnlyList<Em3dMeshRegion> MeshRegions(C3dDocument document)
    {
        double m = 1e-6 / document.DbuPerMicron;
        var list = new List<Em3dMeshRegion>();
        foreach (var r in document.MeshRegions)
        {
            if (r.Size.X <= 0 || r.Size.Y <= 0 || r.Size.Z <= 0 || !(r.SizeUm > 0)) continue;
            list.Add(new Em3dMeshRegion(r.Name, new Point3(r.Min.X * m, r.Min.Y * m, r.Min.Z * m),
                                        new Point3((r.Min.X + r.Size.X) * m, (r.Min.Y + r.Size.Y) * m, (r.Min.Z + r.Size.Z) * m),
                                        r.SizeUm * 1e-6, r.Grading));
        }
        return list;
    }

    /// <summary>The material name Vacuum — built in, like Air, when the technology does not define it.</summary>
    public const string VacuumMaterial = "Vacuum";

    /// <summary>3D editor round 3 — the material the air box is filled with, as the tree heads its row and a run fills it.</summary>
    public static string BoxFill(C3dDocument document)
        => document.AirBoxMaterial is { Length: > 0 } m ? m : Em3dGenerator.AirMaterial;

    /// <summary>
    /// The material an object named <paramref name="name"/> is made of: the technology's own record, else — for Air and
    /// Vacuum only — built-in free space, the rule <see cref="AirFill"/> already applies to the air box. A flattened
    /// plated via's core is Air, and a technology that never defined Air must still hold it. Null: nothing defines it.
    /// </summary>
    public static TechMaterial? ObjectMaterial(Technology? tech, string? name)
        => tech?.FindMaterial(name) ?? (IsFreeSpace(name) ? new TechMaterial { Name = name!, Epsr = 1, TanD = 0, Mur = 1 } : null);

    /// <summary>brief-sar-6 R-sar6-1 — a SAR frequency outside the sweep: an openEMS run's excitation carries no energy there.</summary>
    public static string? SarFrequencyRefusal(SarSettings sar, double fMinHz, double fMaxHz)
    {
        foreach (double f in sar.FrequenciesHz)
            if (f < fMinHz * (1 - 1e-9) || f > fMaxHz * (1 + 1e-9))
                return $"Sar.FrequenciesGHz asks for SAR at {MaterialDispersion.Hz(f)}, outside the sweep ({MaterialDispersion.Hz(fMinHz)} to " +
                       $"{MaterialDispersion.Hz(fMaxHz)}): the excitation carries no energy there. Choose a frequency inside the sweep.";
        return null;
    }

    /// <summary>
    /// brief-sar-6 R-sar6-1 (overview L6) — two SAR frequencies with a frequency-dependent material: an openEMS run sees each
    /// material at one frequency, so SAR at the second would be computed in tissue that has the first frequency's values.
    /// </summary>
    public static string? SarDispersiveRefusal(SarSettings sar, C3dElaboration e)
    {
        if (sar.FrequenciesHz.Count < 2) return null;
        var dispersive = e.SolidMaterials.Values.Select(v => v.Technology.FindMaterial(v.Material)).OfType<TechMaterial>()
                          .Where(MaterialDispersion.IsDispersive).Select(m => m.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (dispersive.Count == 0) return null;
        return $"Sar.FrequenciesGHz names {sar.FrequenciesHz.Count} frequencies, and {string.Join(", ", dispersive.Select(n => $"'{n}'"))} " +
               $"{(dispersive.Count == 1 ? "is" : "are")} frequency-dependent: an openEMS run sees each material at one frequency " +
               $"({MaterialDispersion.Hz(sar.FrequenciesHz[0])} here), so SAR at the others would be computed in tissue that is not " +
               "what it is there. Name one frequency, and run a second setup for the next.";
    }

    /// <summary>brief-sar-6 — each solid's material density, kg/m³, where its record states one (the record it was built from,
    /// in the technology it was built from).</summary>
    private static Dictionary<string, double> SolidDensities(C3dElaboration e, IReadOnlyList<Em3dSolid> solids)
    {
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var s in solids)
            if (e.SolidMaterials.TryGetValue(s.Name, out var rec) && rec.Technology.FindMaterial(rec.Material)?.DensityKgM3 is { } rho)
                map[s.Name] = rho;
        return map;
    }

    /// <summary>
    /// brief-sar-2 R-sar2-3 — the frequency a setup with no SAR frequency evaluates its dispersive materials at: the sweep's
    /// centre — the arithmetic middle of a linear sweep, the geometric middle of a log one.
    /// </summary>
    public static double MaterialFrequency(Em3dFrequency f)
        => f.Kind == Em3dSweepKind.Log && f.StartHz > 0 ? Math.Sqrt(f.StartHz * f.StopHz) : 0.5 * (f.StartHz + f.StopHz);

    /// <summary>Whether <paramref name="name"/> is one of the built-in free-space names, Air or Vacuum.</summary>
    public static bool IsFreeSpace(string? name)
        => string.Equals(name, Em3dGenerator.AirMaterial, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(name, VacuumMaterial, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The air box's fill as a problem material: <paramref name="name"/> (Air when null) from the technology at
    /// <paramref name="tempC"/>; Air and Vacuum fall back to built-in free space when the technology lacks them. A material
    /// that neither the technology nor the built-ins know is a refusal naming it, never a guess.
    /// </summary>
    public static (Em3dMaterial Material, string Source)? AirFill(string? name, Technology? tech, double tempC, out string why,
                                                                  double? materialHz = null)
    {
        why = "";
        string want = name is { Length: > 0 } n ? n : Em3dGenerator.AirMaterial;
        if (tech?.FindMaterial(want) is { } m)
        {
            if (materialHz is { } f && MaterialDispersion.IsDispersive(m) && MaterialDispersion.At(m, f, out string? refused) is null && refused is not null)
            {
                why = "The air box's fill cannot be evaluated: " + refused;
                return null;
            }
            return (C3dElaborator.MaterialValues(m, tempC, materialHz) with { Name = m.Name }, $"technology '{tech.Name}'");
        }
        if (string.Equals(want, Em3dGenerator.AirMaterial, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(want, VacuumMaterial, StringComparison.OrdinalIgnoreCase))
            return (new Em3dMaterial(want, 1, null, 0, 1, 0), "built in (free space)");
        why = $"The air box is filled with '{want}', which the technology does not define. Pick one of its materials for the " +
              "air box, or Air or Vacuum.";
        return null;
    }
}

/// <summary>brief-em3d-49 R-em3d49-5c — one port as a setup's run resolves it; <see cref="Setup"/> is null when the
/// document embeds no setup (lumped ports are still inferred; a wave port needs a box).</summary>
public sealed record C3dPortReport(string? Setup, C3dPortResult Result)
{
    /// <summary>What <c>check</c> prints: the polarity and why, or the refusal.</summary>
    public string Text => (Setup is { } s ? $"setup '{s}': " : "") +
                          (!Result.Port.Model ? $"{Result.Label} is not modelled (its Model is off): no run has it, and the result's ports are the others, renumbered."
                           : Result.Refusal ?? C3dPortReports.Describe(Result));

    /// <summary>A refusal a run would make: a modelled port's. A port that is off is never one.</summary>
    public bool Refused => Result.Port.Model && Result.Refusal is not null;
}

/// <summary>
/// brief-em3d-49 R-em3d49-5c — what <c>check</c> and <c>explain</c> say about a 3D view's ports and face boundaries: the
/// SAME resolution a run makes (C3dPorts through the setup's own box and ground set, C3dProblemAssembly.FaceBoundaries),
/// so a port that checks clean is the port the solver gets.
/// </summary>
public static class C3dPortReports
{
    /// <summary>Every port under every readable embedded setup — each setup's box and ground set can change a port's
    /// polarity or, for a wave port, whether it lies on the box at all. Identical lines from two setups are said once.</summary>
    /// <param name="elaborationFor">brief-emb-2 R-emb2-3 — the elaboration a setup's run measures its ports against (its Robust
    /// meshing's outline), so <c>check</c> infers on the outline the run meshes; null measures every setup against
    /// <paramref name="e"/>.</param>
    public static IReadOnlyList<C3dPortReport> For(C3dDocument doc, C3dElaboration e, Func<EmSetup?, C3dElaboration>? elaborationFor = null)
    {
        var reports = new List<C3dPortReport>();
        if (doc.Ports.Count == 0 || !e.Ok) return reports;
        C3dElaboration Of(EmSetup? setup) => elaborationFor?.Invoke(setup) is { Ok: true } own ? own : e;
        // brief-em3d-73 — a thermal setup drives no port and puts no air box round anything: it has no port to report.
        var setups = C3dSetups.Read(doc).Where(s => s.Setup is { IsThermal: false }).ToList();
        if (setups.Count == 0)
        {
            var ctx = C3dProblemAssembly.PortContext(null, doc, Of(null), null);
            reports.AddRange(C3dPorts.Resolve(doc, ctx).Select(r => new C3dPortReport(null, RingNote(r, Of(null)))));
            return reports;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in setups)
        {
            var es = Of(s.Setup);
            var box = C3dProblemAssembly.AirBox(s.Setup!, es, out _);
            var ctx = C3dProblemAssembly.PortContext(s.Setup, doc, es, box);
            foreach (var resolved in C3dPorts.Resolve(doc, ctx))
            {
                var r = RingNote(resolved, es);
                if (seen.Add(r.Refusal ?? Describe(r))) reports.Add(new C3dPortReport(setups.Count == 1 ? null : s.Name, r));
            }
        }
        return reports;
    }

    /// <summary>
    /// brief-cxp-1 R-cxp1-6 — a coaxial port's ring, as a TEM line: η₀/(2π√εr)·ln(b/a) for the dielectric the ring lies on, beside
    /// the port's Z0. A NOTE, never a warning: a port of any Z0 is legal, and the difference is a mismatch the result shows. The
    /// dielectric is the highest-ordered non-conductor solid holding a point midway between the radii, a hair to the side the
    /// reference plane faces; none is the air box's fill.
    /// </summary>
    internal static C3dPortResult RingNote(C3dPortResult r, C3dElaboration e)
    {
        if (RingImpedance(r, e) is not var (z, name, er)) return r;
        string G(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        string note = $"As a coax filled with '{name}' (εr {G(er)}), its ring is {G(z)} Ω; its Z0 is {C3dPorts.FormatZ0(r.Resolved!.Z0)} Ω.";
        return r with { Note = r.Note is { } prior ? prior + " " + note : note };
    }

    /// <summary>brief-cxp-1/2 — a resolved ring's TEM impedance, the material it lies on and that material's εr; null on any other
    /// port (<see cref="RingNote"/> says how the material is found).</summary>
    public static (double Ohms, string Material, double Epsr)? RingImpedance(C3dPortResult r, C3dElaboration e)
    {
        if (r.Resolved is not { Annulus: { } ring } p) return null;
        int normal = Em3dAnnulus.NormalAxis(p.Min, p.Max);
        var c = Em3dAnnulus.Centre(p.Min, p.Max);
        double gap = ring.OuterRadiusM - ring.InnerRadiusM, step = 0.1 * gap;
        var mid = Em3dAnnulus.At(c, normal, (ring.InnerRadiusM + ring.OuterRadiusM) / 2, 0);
        var n = p.ReferencePlane.Normal;
        var q = new Point3(mid.X + n.X * step, mid.Y + n.Y * step, mid.Z + n.Z * step);
        var fill = e.Solids.Where(s => s.Role != Em3dRole.Conductor && C3dThermal.Inside(s.Primitive, q, 0))
                           .OrderByDescending(s => s.Order).FirstOrDefault();
        string name = fill?.Material ?? "Air";
        double er = e.Materials.FirstOrDefault(m => m.Name == fill?.Material)?.Epsr ?? 1.0;
        double z = 376.730313668 / (2 * Math.PI * Math.Sqrt(er)) * Math.Log(ring.OuterRadiusM / ring.InnerRadiusM);
        return (z, name, er);
    }

    /// <summary>The face boundaries' refusals, in the document's technology at the default temperature.</summary>
    public static IReadOnlyList<string> FaceBoundaryRefusals(C3dDocument doc, C3dElaboration e)
    {
        if (doc.FaceBoundaries.Count == 0 || !e.Ok) return [];
        if (C3dProblemAssembly.NotModelledBoundary(doc, e) is { } off) return [off];
        var materials = e.Materials.ToList();
        return C3dProblemAssembly.FaceBoundaries(doc, e, materials, EmSetup.DefaultOperatingTempC, out _) is { } why ? [why] : [];
    }

    /// <summary>A resolved port in a sentence: <c>P1 (lumped) runs from 'ground' to 'trace' along +z: 'ground' is in the
    /// ground set.</c></summary>
    public static string Describe(C3dPortResult r)
    {
        if (r.Resolved is not { } p) return r.Refusal ?? "";
        // brief-em3d-114 R-em3d114-4 — a multi-terminal port: its reference and why, then each terminal as a port of the result
        if (r.Terminals is { } ts)
            return $"{r.Label} (wave, {ts.Count} terminals) on the {Em3dProblemFace(p)} face: the reference is '{p.NegativeObject}' — " +
                   $"{r.Reason}; " + string.Join("; ", ts.Select(t => $"terminal {t.Number}{(t.SourceLabel is { } n ? $" '{n}'" : "")} runs to " +
                                                                    $"'{t.PositiveObject}' along {Along(Vec(t))}")) + ".";
        string normalised = p.ModeNormalised ? "; its S-parameters are normalised to its mode" : "";
        // brief-hzn-11 R-hzn11-2 — a hollow guide has no ends: its walls, its broad side and its polarity line
        if (p.Hollow)
            return $"{r.Label} (wave, hollow guide) on the {Em3dProblemFace(p)} face: {r.Reason}{normalised}.";
        // brief-cxp-1 R-cxp1-6 — a ring's radii and its log ratio, which is what sets its field
        if (p.Annulus is { } ring)
        {
            string Mm(double m) => (m * 1e3).ToString("0.####", CultureInfo.InvariantCulture) + " mm";
            return $"{r.Label} (coaxial, a = {Mm(ring.InnerRadiusM)}, b = {Mm(ring.OuterRadiusM)}, ln(b/a) = " +
                   $"{Math.Log(ring.OuterRadiusM / ring.InnerRadiusM).ToString("0.###", CultureInfo.InvariantCulture)}) runs from " +
                   $"'{p.NegativeObject}' to '{p.PositiveObject}' radially{(ring.Outward ? ", outward" : "")}: {r.Reason}." +
                   (r.Note is { } ringNote ? " " + ringNote : "");
        }
        var d = p.Kind == Em3dPortKind.Wave && p.VoltagePath is { } v
            ? new Point3(v.To.X - v.From.X, v.To.Y - v.From.Y, v.To.Z - v.From.Z) : p.Direction;
        return $"{r.Label} ({(p.Kind == Em3dPortKind.Wave ? "wave" : "lumped")}) runs from '{p.NegativeObject}' to '{p.PositiveObject}' " +
               $"along {Along(d)}: {r.Reason}{normalised}." + (r.Note is { } note ? " " + note : "");
    }

    private static Point3 Vec(Em3dPort t) => t.VoltagePath is { } v ? new Point3(v.To.X - v.From.X, v.To.Y - v.From.Y, v.To.Z - v.From.Z) : t.Direction;

    private static string Along(Point3 d)
    {
        double ax = Math.Abs(d.X), ay = Math.Abs(d.Y), az = Math.Abs(d.Z);
        return ax >= ay && ax >= az ? (d.X >= 0 ? "+x" : "-x") : ay >= az ? (d.Y >= 0 ? "+y" : "-y") : (d.Z >= 0 ? "+z" : "-z");
    }

    /// <summary>The air-box face a wave port's rectangle lies on (<c>xmin</c> …), from its inward reference-plane normal.</summary>
    public static string Em3dProblemFace(Em3dPort p)
    {
        var n = p.ReferencePlane.Normal;
        return Math.Abs(n.X) > 0.5 ? (n.X > 0 ? "xmin" : "xmax") : Math.Abs(n.Y) > 0.5 ? (n.Y > 0 ? "ymin" : "ymax") : (n.Z > 0 ? "zmin" : "zmax");
    }
}
