// ================================================================
//  SimulateGateTests.cs — the headless gate for brief-em3d-49: ports by contact (1), equivalence with a
//  .cem (2), a closed-form cavity by face boundaries (3), Palace's exact face count (4), and static terminals
//  by object (§8). Gate 7 (every golden byte-identical) is the existing backend tests; gates 5, 6 and 8 are the
//  editor's, in SimulateEditorGateTests. The Palace runs skip, naming what is missing, without it.
// ================================================================

using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Ui.Tests.Em3d;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ThreeD;

// The Palace gates start Palace: in the collection every Palace-running class shares.
[Collection(SkiaFontsTypefaceCollection.Name)]
public sealed class SimulateGateTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-c3d49-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private const long Um = 1000;                 // DBU per µm
    private const double C0 = 299_792_458.0, Eps0 = 8.8541878128e-12;

    // ── 1. polarity by contact ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate1_TraceToGround_InferredFromContact_GroundIsNegative_UpIsPositive()
    {
        var doc = Microstrip();
        doc.Ports.Add(EndPort(0, 100));
        var (r, ctx) = Resolve(doc);
        Assert.Null(r.Refusal);
        Assert.Equal(("gnd", "trace"), (r.Resolved!.NegativeObject, r.Resolved.PositiveObject));
        Assert.Equal(new Point3(0, 0, 1), r.Resolved.Direction);
        Assert.Contains("larger surface", r.Reason);
        Assert.Equal(1, ctx.InferenceRuns);

        // Flip swaps them, and the arrow turns round.
        doc.Ports[0].Flip = true;
        (r, _) = Resolve(doc);
        Assert.Equal(("trace", "gnd"), (r.Resolved!.NegativeObject, r.Resolved.PositiveObject));
        Assert.Equal(new Point3(0, 0, -1), r.Resolved.Direction);
    }

    [Fact]
    public void Gate1_AGapInsideAnInstance_NamesTheSubCellsConductor()
    {
        string ws = Workspace();
        WriteC3d(ws, "Die", new C3dDocument { Objects = [Box("pad", "Copper", 0, 0, 0, 100, 100, 20)] });
        var doc = new C3dDocument
        {
            Objects = [Sheet("gnd", -500, -500, 1000, 1000, 0)],
            Instances = [new C3dInstance { Name = "U1", CellRef = "../../Die", Placement = new C3dPlacement { Origin = new C3dPoint3(0, 0, 50 * Um) } }],
        };
        doc.Ports.Add(new C3dPort { Number = 1, Name = "P1", Plane = C3dPlane.XZ, Offset = 0, Rect = Rect(0, 0, 100, 50) });
        var (r, _) = Resolve(doc, WriteC3d(ws, "Pkg", doc));
        Assert.Null(r.Refusal);
        Assert.Equal(("gnd", "U1/pad"), (r.Resolved!.NegativeObject, r.Resolved.PositiveObject));
        Assert.Equal(new Point3(0, 0, 1), r.Resolved.Direction);
    }

    [Fact]
    public void Gate1_APecFaceOfTheAirBox_IsTheGroundEnd()
    {
        var doc = Microstrip(ground: false);
        doc.Ports.Add(EndPort(0, 100));
        var setup = Setup();
        setup.AirBox = new EmAirBox(ZMin: new EmAirBoxFace(0, Em3dBoundaryKind.Pec));
        var (r, _) = Resolve(doc, setup: setup);
        Assert.Null(r.Refusal);
        Assert.Equal(("airbox/zmin", "trace"), (r.Resolved!.NegativeObject, r.Resolved.PositiveObject));
        Assert.Contains("PEC face of the air box", r.Reason);
    }

    [Fact]
    public void Gate1_EachRefusal_NamesWhatWasFound()
    {
        // Nothing on one edge: a port that stops short of the trace.
        var doc = Microstrip();
        doc.Ports.Add(EndPort(0, 50));
        Assert.Equal("P1 touches 'gnd' on its bottom edge and nothing on its top. Its top edge misses 'trace' by 50 µm. A lumped port lies " +
                     "between two conductors, one on each of two opposite edges.", Resolve(doc).Result.Refusal);

        // Two conductors on one edge.
        doc = Microstrip();
        doc.Objects.Add(Sheet("stub", 0, 550, 1000, 50, 100));
        doc.Ports.Add(new C3dPort { Number = 1, Name = "P1", Plane = C3dPlane.YZ, Offset = 0, Rect = Rect(450, 0, 150, 100) });
        Assert.StartsWith("P1's top edge touches both 'trace' and 'stub'", Resolve(doc).Result.Refusal);

        // A conductor on every edge.
        doc = new C3dDocument
        {
            Objects = [Sheet("a", -10, 0, 10, 10, 0), Sheet("b", 10, 0, 10, 10, 0), Sheet("c", 0, -10, 10, 10, 0), Sheet("d", 0, 10, 10, 10, 0)],
        };
        doc.Ports.Add(new C3dPort { Number = 1, Name = "P1", Plane = C3dPlane.XY, Offset = 0, Rect = Rect(0, 0, 10, 10) });
        Assert.StartsWith("P1 touches conductors on all four edges", Resolve(doc).Result.Refusal);
        Assert.EndsWith("state Positive and Negative.", Resolve(doc).Result.Refusal);
    }

    [Fact]
    public void Gate1_WithBothEndsStated_InferenceIsNotConsulted()
    {
        var doc = Microstrip();
        var port = EndPort(0, 100);
        port.Positive = "gnd";
        port.Negative = "trace";
        doc.Ports.Add(port);
        var (r, ctx) = Resolve(doc);
        Assert.Equal(0, ctx.InferenceRuns);
        Assert.Equal(("trace", "gnd"), (r.Resolved!.NegativeObject, r.Resolved.PositiveObject));
        Assert.Equal(new Point3(0, 0, -1), r.Resolved.Direction);
        Assert.Equal("both ends are stated", r.Reason);

        port.Negative = null;
        Assert.Contains("states its Positive end only", Resolve(doc).Result.Refusal);
    }

    /// <summary>R-em3d49-5c — check's line per port is the run's own resolution: the polarity and why, or the refusal.</summary>
    [Fact]
    public void CheckReportsEachPortsPolarity_AndEachRefusal_FromTheRunsOwnResolution()
    {
        var doc = Microstrip();
        doc.Ports.Add(EndPort(0, 100));
        var short2 = EndPort(0, 50);
        short2.Number = 2; short2.Name = "P2";
        doc.Ports.Add(short2);
        string path = WriteC3d(Workspace(), "Cell", doc);
        var reports = C3dPortReports.For(doc, C3dElaborator.ElaborateOnce(doc, path, null));
        Assert.Equal("P1 (lumped) runs from 'gnd' to 'trace' along +z: neither is in the ground set, and 'gnd' has the larger " +
                     "surface (2 mm² against 0.2 mm²).", reports[0].Text);
        Assert.StartsWith("P2 touches 'gnd' on its bottom edge and nothing on its top.", reports[1].Text);
        Assert.Null(reports[0].Result.Refusal);
        Assert.NotNull(reports[1].Result.Refusal);
    }

    // ── 2. equivalence with a .cem ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Brief 42's oracle: the Package example's layout, placed by New 3D View from Layout (brief 48 §7 translates its
    /// setups' ports), gives the ports the .cem route gives — through the lid-modes setup, the one of its three whose
    /// problem has ports (an electrostatic solve has none, and Package L solves another layout) — numbers, rectangles, conductors and directions. The one
    /// named difference: the .cem's PEC floor (<c>airbox/zmin</c>) is the undrawn ground plane, and a placed layout brings
    /// no metal it does not draw, so its port first returns to nothing; a floor drawn in the 3D view is what it returns to.
    /// </summary>
    [Fact]
    public void Gate2_TheLayoutsPortsDrawnAsC3dPorts_GiveTheCemRoutesPorts()
    {
        string ws = CopyExample();
        string cws = Path.Combine(ws, ".cws");
        string cell = Path.Combine(ws, "Package");
        string path = Path.Combine(cell, "3d", "Package.c3d");
        var made = C3dHierarchy.NewFromLayout(cell, path, cws);
        Assert.Null(made.Refusal);
        Assert.NotEmpty(made.Document!.Ports);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        C3dPersistence.SaveToFile(path, made.Document!);

        string cem = Path.Combine(cell, "em", "Package lid modes.cem");
        var cemSetup = EmSetupPersistence.LoadFromFile(cem);
        var resolved = EmSetupResolver.Resolve(cem, cemSetup.LayoutRef, cws, new TechnologyCache());
        var p = Em3dGenerator.Generate(cemSetup, resolved.Source!, resolved.Source!.Technology!).Problem!;

        var doc = C3dPersistence.LoadFromFile(path);
        var (s, why) = C3dSetups.Select(doc, "Package lid modes");
        Assert.Null(why);
        var refused = C3dProblemAssembly.Assemble(C3dSetups.ForRun(s!, path), doc, path, cws);
        Assert.False(refused.Ok);
        Assert.Contains("nothing on its bottom", refused.Refusal!, StringComparison.Ordinal);

        // The floor, drawn: the stackup's 3 µm gold Floor entry under the base, as wide as the base.
        var bases = Em3dProblem.Bounds(C3dElaborator.ElaborateOnce(doc, path, cws).Solids.Single(x => x.Name == "U1/Base").Primitive);
        long Dbu(double m) => (long)Math.Round(m * 1e6 * doc.DbuPerMicron);
        doc.Objects.Add(new C3dBox
        {
            Name = "Floor", Material = "Gold",
            Min = new(Dbu(bases.X0), Dbu(bases.Y0), 0), Size = new(Dbu(bases.X1 - bases.X0), Dbu(bases.Y1 - bases.Y0), 3 * doc.DbuPerMicron),
        });
        var g = C3dProblemAssembly.Assemble(C3dSetups.ForRun(s!, path), doc, path, cws);
        Assert.True(g.Ok, g.Refusal);
        var q = g.Problem!;

        // The .c3d's frame puts the stack's bottom at z = 0: one offset, read off a solid both problems hold.
        var shared = p.Solids.First(x => q.Solids.Any(y => y.Name == "U1/" + x.Name));
        double dz = Em3dProblem.Bounds(q.Solids.First(y => y.Name == "U1/" + shared.Name).Primitive).Z0 - Em3dProblem.Bounds(shared.Primitive).Z0;
        Assert.Equal(p.Ports.Select(x => x.Number), q.Ports.Select(x => x.Number));
        foreach (var (a, b) in p.Ports.Zip(q.Ports))
        {
            output.WriteLine($"port {a.Number}: .cem {a.NegativeObject} → {a.PositiveObject}; .c3d {b.NegativeObject} → {b.PositiveObject}");
            Assert.Equal(a.Min.X, b.Min.X, 9); Assert.Equal(a.Min.Y, b.Min.Y, 9); Assert.Equal(a.Min.Z + dz, b.Min.Z, 9);
            Assert.Equal(a.Max.X, b.Max.X, 9); Assert.Equal(a.Max.Y, b.Max.Y, 9); Assert.Equal(a.Max.Z + dz, b.Max.Z, 9);
            Assert.Equal("U1/" + a.PositiveObject, b.PositiveObject);
            if (a.NegativeObject == "airbox/zmin") Assert.Equal("Floor", b.NegativeObject);
            else Assert.Equal("U1/" + a.NegativeObject, b.NegativeObject);
            Assert.Equal(a.Direction, b.Direction);
            Assert.Equal(a.Z0, b.Z0);
        }
    }

    // ── 3. a closed-form cavity, by face boundaries ─────────────────────────────────────────────

    /// <summary>
    /// An air block whose six faces carry Pec face boundaries, in an air box padded by nothing: brief 23's cavity with its
    /// walls stated on the document's faces rather than the box's. TE101 at (c/2)√((1/a)² + (1/d)²) within brief 23's
    /// 0.1 % — an external reference, the closed form. The mesh's entity check passing is gate 4's exact count.
    /// </summary>
    [PalaceFact]
    public void Gate3_ACavityWalledByPecFaceBoundaries_ResonatesAtTheClosedForm()
    {
        const double a = 22.86e-3, b = 10.16e-3, d = 25e-3;
        var doc = new C3dDocument { Objects = [Box("cavity", "Air", 0, 0, 0, 22860, 10160, 25000)] };
        foreach (string face in C3dBox.FaceNameList)
            doc.FaceBoundaries.Add(new C3dFaceBoundary { Object = "cavity", Face = face, Kind = Em3dFaceBoundaryKind.Pec });
        var none = new EmAirBoxFace(0, null);
        var setup = new EmSetup
        {
            Name = "modes", Solver3D = Em3dSolverId.Palace, Problem3D = Em3dProblemType.Eigenmode,
            AirBox = new EmAirBox(none, none, none, none, none, none),
            Eigenmode = new EmEigenmode3D(1, 5),
            Frequency = new CircuitRF.Core.Design.FrequencySpec("5", "15", 3, CircuitRF.Core.Design.SweepKind.Linear, "GHz", "GHz"),
        };
        string path = WriteC3d(Workspace(), "Cavity", doc);
        var g = C3dProblemAssembly.Assemble(setup, doc, path, null);
        Assert.True(g.Ok, g.Refusal);
        var problem = g.Problem!;
        Assert.Equal(6, problem.FaceBoundaries.Count);

        string run = Solve(problem, "cavity");
        var modes = PalaceRun.ReadModes(Path.Combine(run, "postpro", PalaceRun.EigFile), out string? error);
        Assert.Null(error);
        double te101 = C0 / 2 * Math.Sqrt(1 / (a * a) + 1 / (d * d));
        double rel = modes![0].FrequencyHz / te101 - 1;
        output.WriteLine($"TE101 {modes[0].FrequencyHz / 1e9:F5} GHz, closed form {te101 / 1e9:F5} GHz, {100 * rel:F4} % (b = {b})");
        Assert.True(Math.Abs(rel) < 1e-3, $"{100 * rel:F4} %");
    }

    // ── 4. Palace's recovery counts the face exactly ────────────────────────────────────────────

    [Fact]
    public void Gate4_EachFaceBoundaryIsCountedExactly_AndACoplanarNeighbourIsRefusedNotMerged()
    {
        var box = new Em3dAirBox(new Point3(-1e-3, -1e-3, -1e-3), new Point3(2e-3, 2e-3, 2e-3),
                                 new Em3dFaces(Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing,
                                               Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing));
        Em3dSolid Block(string name, double x0, double z0, double x1, double z1, int order)
            => new(name, "fill", Em3dRole.Dielectric, new Em3dBox(new(x0, 0, z0), new(x1, 1e-3, z1)), order);
        Em3dProblem Problem(params Em3dSolid[] solids) => new(solids, [], [new Em3dMaterial("fill", 4, null, 0, 1, 0)], [], box,
                                                               new Em3dFrequency(1e9, 1e9, 1, Em3dSweepKind.Linear), 20)
        {
            Type = Em3dProblemType.Eigenmode, EigenmodeTargetHz = 1e9,
            FaceBoundaries = [new Em3dFaceBoundary("a", "zmax", Em3dFaceBoundaryKind.Pec)],
        };

        var alone = GmshGeoWriter.Write(Problem(Block("a", 0, 0, 1e-3, 1e-3, 1)), PalaceSettings.Default);
        Assert.True(alone.Ok, alone.Refusal);
        var group = Assert.Single(alone.Groups, gr => gr.Kind == Em3dGroupKind.FaceBoundary);
        Assert.Equal(("a/zmax", 1, false, (Em3dBoundaryKind?)Em3dBoundaryKind.Pec), (group.Name, group.Expected, group.AtLeast, group.Boundary));

        // A block standing on half of a's top: its bottom face is coplanar with a's zmax and inside its box.
        var neighbour = GmshGeoWriter.Write(Problem(Block("a", 0, 0, 1e-3, 1e-3, 1), Block("b", 0, 1e-3, 5e-4, 2e-3, 2)), PalaceSettings.Default);
        Assert.False(neighbour.Ok);
        Assert.Contains("face 'zmin' of 'b' lies in the same plane and overlaps it", neighbour.Refusal);
        // One beside it, sharing only an edge, is not a neighbour.
        var beside = GmshGeoWriter.Write(Problem(Block("a", 0, 0, 1e-3, 1e-3, 1), Block("b", 1e-3, 1e-3, 1.5e-3, 2e-3, 2)), PalaceSettings.Default);
        Assert.True(beside.Ok, beside.Refusal);

        // The entity check refuses a count that differs, naming the boundary.
        var table = new GmshGeoWriter.EntityTable(alone.Groups.ToDictionary(gr => gr.Attribute, gr => (gr.Kind == Em3dGroupKind.FaceBoundary ? 2 : Math.Max(gr.Expected, 1), 0)), 1, 1, 0);
        Assert.Contains("'a/zmax' (a face boundary) selected 2 surface(s) where 1 were expected", GmshGeoWriter.CheckEntities(alone.Groups, table));
    }

    // ── §8. static terminals by object ──────────────────────────────────────────────────────────

    /// <summary>
    /// Brief 22's parallel-plate fixture drawn as boxes: a 1 mm² plate 100 µm over a bottom plate, εr 2 between, PMC walls
    /// at the plates' edges so the field is uniform. The terminal names its plate by OBJECT and the ground is the bottom
    /// plate's own name (a drawn conductor's net): C = ε₀εᵣA/d within 1 %, gate 2 of PalaceStaticTests' bound.
    /// </summary>
    [PalaceFact]
    public void Section8_TwoPlatesDrawnAsBoxes_WithATerminalByObject_GiveEps0EpsrAOverD()
    {
        const long a = 1000, dd = 100, t = 20;
        var doc = new C3dDocument
        {
            Objects =
            [
                Box("bottom", "Copper", 0, 0, -t, a, a, t),
                Box("fill", "Fill", 0, 0, 0, a, a, dd),
                Box("top", "Copper", 0, 0, dd, a, a, t),
            ],
        };
        var pmc = new EmAirBoxFace(0, Em3dBoundaryKind.Pmc);
        var setup = new EmSetup
        {
            Name = "plates", Solver3D = Em3dSolverId.Palace, Problem3D = Em3dProblemType.Electrostatic,
            AirBox = new EmAirBox(pmc, pmc, pmc, pmc, pmc, new EmAirBoxFace(180, Em3dBoundaryKind.Pmc)),
            Terminals3D = [new EmTerminal3D("top", "", null, ["top"])],
            Ground3D = "bottom",
        };
        string path = WriteC3d(Workspace(), "Plates", doc);
        var g = C3dProblemAssembly.Assemble(setup, doc, path, null);
        Assert.True(g.Ok, g.Refusal);
        Assert.Equal(["top"], Assert.Single(g.Problem!.Terminals).Objects);
        Assert.Equal(["bottom"], g.Problem.GroundObjects);

        // Both at once is refused, and so is a name that is no conductor.
        setup.Terminals3D = [new EmTerminal3D("top", "top", null, ["top"])];
        Assert.Contains("states both a net ('top') and objects", C3dProblemAssembly.Assemble(setup, doc, path, null).Refusal);
        setup.Terminals3D = [new EmTerminal3D("top", "", null, ["fill"])];
        Assert.Contains("names 'fill', which is not a conductor", C3dProblemAssembly.Assemble(setup, doc, path, null).Refusal);

        string run = Solve(g.Problem, "plates");
        var c = PalaceRun.ReadTerminalMatrix(Path.Combine(run, PalaceConfigWriter.OutputDirectory, PalaceRun.CapacitanceFile), "C", "(F)", [1], out string? error);
        Assert.True(c is not null, error);
        double expected = Eps0 * 2 * 1e-6 / 100e-6;
        output.WriteLine($"C = {c![0, 0]:G6} F, closed form {expected:G6} F, {100 * (c[0, 0] / expected - 1):F4} %");
        Assert.InRange(c[0, 0] / expected, 0.99, 1.01);
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    /// <summary>A 1 mm square of 100 µm dielectric, a ground sheet under it and a 100 µm trace along x on top.</summary>
    private static C3dDocument Microstrip(bool ground = true)
    {
        var doc = new C3dDocument { Objects = [Box("sub", "Fill", 0, 0, 0, 1000, 1000, 100), Sheet("trace", 0, 450, 1000, 100, 100)] };
        if (ground) doc.Objects.Add(Sheet("gnd", 0, 0, 1000, 1000, 0));
        return doc;
    }

    /// <summary>A port at the trace's x = 0 end, on YZ, across the trace's width, from z = 0 up <paramref name="height"/> µm.</summary>
    private static C3dPort EndPort(long z0, long height)
        => new() { Number = 1, Name = "P1", Plane = C3dPlane.YZ, Offset = 0, Rect = Rect(450, z0, 100, height) };

    private static C3dRect Rect(long u, long v, long du, long dv)
        => new() { Min = new C3dPoint2(u * Um, v * Um), Size = new C3dPoint2(du * Um, dv * Um) };

    private static C3dBox Box(string name, string material, long x, long y, long z, long dx, long dy, long dz)
        => new() { Name = name, Material = material, Min = new C3dPoint3(x * Um, y * Um, z * Um), Size = new C3dPoint3(dx * Um, dy * Um, dz * Um) };

    private static C3dSheet Sheet(string name, long u, long v, long du, long dv, long z)
        => new() { Name = name, Material = "Copper", Plane = C3dPlane.XY, Offset = z * Um, Rect = Rect(u, v, du, dv) };

    private static EmSetup Setup() => new() { Name = "S1", Solver3D = Em3dSolverId.Palace };

    /// <summary>The document's one port resolved against <paramref name="setup"/>'s box (or none), in a scratch workspace.</summary>
    private (C3dPortResult Result, C3dPortContext Context) Resolve(C3dDocument doc, string? path = null, EmSetup? setup = null)
    {
        path ??= WriteC3d(Workspace(), "Cell", doc);
        var e = C3dElaborator.ElaborateOnce(doc, path, null);
        Assert.True(e.Ok, string.Join(" ", e.Refusals));
        var box = setup is null ? null : C3dProblemAssembly.AirBox(setup, e, out _);
        var ctx = C3dProblemAssembly.PortContext(setup, doc, e, box);
        return (C3dPorts.Resolve(doc, ctx).Single(), ctx);
    }

    /// <summary>A workspace whose default technology holds Copper, Fill (εr 2), and Air.</summary>
    private string Workspace()
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials =
            [
                new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 },
                new TechMaterial { Name = "Fill", Epsr = 2 },
                new TechMaterial { Name = "Air", Epsr = 1 },
            ],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        return ws;
    }

    private static string WriteC3d(string ws, string cell, C3dDocument doc)
    {
        string dir = Path.Combine(ws, cell, "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, cell + ".c3d");
        C3dPersistence.SaveToFile(path, doc);
        return path;
    }

    private string CopyExample()
    {
        string src = Path.Combine(PalaceBackendTests.RepoRoot(), "examples", "3D EM");
        string dst = Path.Combine(_root, "3D EM");
        foreach (string f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            string to = Path.Combine(dst, Path.GetRelativePath(src, f));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to);
        }
        return dst;
    }

    /// <summary>Lowers, meshes and runs Palace serially on one fixed mesh — brief 23's cavity settings: 15 % of the
    /// shortest wavelength, no grading, no refinement. Returns the run directory.</summary>
    private string Solve(Em3dProblem problem, string name)
    {
        var settings = PalaceSettings.Default with { AdaptiveMaxIterations = 0, MaxElementWavelengths = 0.15, EdgeRefinement = 1 };
        Assert.Empty(problem.Validate());
        var low = GmshGeoWriter.Write(problem, settings);
        Assert.True(low.Ok, low.Refusal);
        var cfg = PalaceConfigWriter.Write(problem, low.Groups, settings);
        Assert.True(cfg.Ok, cfg.Refusal);
        string dir = Path.Combine(_root, name);
        var mesh = PalaceRun.Mesh(dir, low, SolverDiscovery.Gmsh.Find(out _)!.Path, null, default);
        Assert.True(mesh.Ok, mesh.Message);
        output.WriteLine($"{name}: {mesh.Tetrahedra} tetrahedra");
        string palace = SolverDiscovery.ReadinessFor(Em3dSolverId.Palace).Single(r => r.Tool == SolverTool.Palace).Installation!.Path;
        var tracker = new PalaceStageTracker(0, settings.SweepAdaptiveTol, null, problem.Type == Em3dProblemType.Electrostatic);
        var solved = PalaceRun.Solve(dir, cfg.Json!, palace, 1, null, default, out _, tracker);
        Assert.True(solved.Ok, solved.Message);
        return dir;
    }
}
