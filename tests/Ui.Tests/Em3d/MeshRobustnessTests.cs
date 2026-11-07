using System.Diagnostics;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-emb-2 — 3D meshing of imported artwork. One test per claim, none of them meshing anything real: the geometry
//  script is read as text, the post-mesh check reads a committed hand-made mesh, Gmsh's progress is fed canned lines, and
//  the curving cap's restart runs a stand-in for Gmsh that takes a second.
// ══════════════════════════════════════════════════════════════════════════════════════════════

[Collection(Em3dProcessCountersCollection.Name)]
public sealed class MeshRobustnessTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("emb2-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // ── R-emb2-1 ───────────────────────────────────────────────────────────────────────────────

    /// <summary>A layout's problem, whose only curves are its via barrels, is meshed straight-sided; the same problem with
    /// its cylinder not a layout barrel (a .c3d's) keeps the curving, and the restart's settings take it away again.</summary>
    [Fact]
    public void ALayoutOnlyProblem_IsStraightSided_AndACylinderOfItsOwnKeepsTheCurving()
    {
        var (setup, source) = Em3dGeneratorTests.CaseB(plated: false);
        var problem = Em3dGenerator.Generate(setup, source, source.Technology!).Problem!;
        Assert.Contains("via/1", problem.ArtworkBarrels);

        string layout = GmshGeoWriter.Write(problem, PalaceSettings.Default).Geo!;
        Assert.Contains(GmshGeoWriter.StraightSidedLine, layout);
        Assert.DoesNotContain("HighOrderOptimize", layout);

        var ownCylinder = problem with { ArtworkBarrels = [] };
        string curved = GmshGeoWriter.Write(ownCylinder, PalaceSettings.Default).Geo!;
        Assert.Contains(GmshGeoWriter.CurvingLine, curved);
        Assert.DoesNotContain("SecondOrderLinear", curved);
        Assert.Contains(GmshGeoWriter.StraightSidedLine,
                        GmshGeoWriter.Write(ownCylinder, PalaceSettings.Default with { StraightSided = true }).Geo!);
    }

    /// <summary>Past the cap on Gmsh's high-order stage the mesh is made again straight-sided, and the run says so. The
    /// stand-in for Gmsh curves for 30 s, which the 0.3 s cap cuts short, and meshes at once from a straight-sided script.</summary>
    [Fact]
    public void ACurvingStagePastItsCap_IsRestartedStraightSided_AndSaysSo()
    {
        if (OperatingSystem.IsWindows()) return;            // the stand-in is a shell script
        string fake = Path.Combine(_root, "fake-gmsh");
        File.WriteAllText(fake, """
            #!/bin/sh
            if grep -q "HighOrderOptimize" model.geo; then
              echo "Info    : Optimizing mesh (HighOrderElastic)..."
              sleep 30
              exit 0
            fi
            echo "Info    : Meshing order 2 (curvilinear off)..."
            printf 'all_volumes 0\nclassified_volumes 0\nunclassified_single_sided 0\n' > entities.txt
            printf 'x' > model.msh
            exit 0
            """.Replace("\r", ""));
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        static GmshLowering Lowering(string line) => new($"// stand-in\nMesh.ElementOrder = 2;\n{line}\n", [], "{}", null);
        var curved = Lowering(GmshGeoWriter.CurvingLine);
        long before = PalaceRun.GmshInvocations;
        var clock = Stopwatch.StartNew();
        var step = PalaceRun.Mesh(Path.Combine(_root, "run"), curved, fake, null, default,
                                  straight: () => Lowering(GmshGeoWriter.StraightSidedLine), curvingMinutes: 0.005);
        Assert.True(step.Ok, step.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(25), "the curving was not cut short");
        Assert.Equal(2, PalaceRun.GmshInvocations - before);
        Assert.Contains("ran past 0.005 min (Palace.MeshCurvingMinutes)", step.Note);
        Assert.Contains(GmshGeoWriter.StraightSidedLine, File.ReadAllText(Path.Combine(_root, "run", GmshGeoWriter.GeoFile)));

        // The next run of the same curved script reuses the straight-sided mesh rather than curving again.
        var again = PalaceRun.Mesh(Path.Combine(_root, "run"), curved, fake, null, default,
                                   straight: () => Lowering(GmshGeoWriter.StraightSidedLine), curvingMinutes: 0.005);
        Assert.True(again.Reused);
        Assert.StartsWith("The reused mesh is the one made when", again.Note);
    }

    /// <summary>A flat element makes the run remake the mesh once on the Delaunay surface mesher, say so, and reuse that mesh next
    /// time. The stand-in for Gmsh writes the committed mesh with a flat tetrahedron until the script asks for that mesher.</summary>
    [Fact]
    public void AFlatElement_RemakesTheMeshOnDelaunaySurfaces_AndTheNextRunReusesIt()
    {
        if (OperatingSystem.IsWindows()) return;            // the stand-in is a shell script
        string flat = Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "mesh-check", "flat-and-orphan.msh");
        string fake = Path.Combine(_root, "fake-gmsh");
        File.WriteAllText(fake, $$"""
            #!/bin/sh
            printf 'group 1 1 0\nall_volumes 0\nclassified_volumes 0\nunclassified_single_sided 0\n' > entities.txt
            if grep -q "{{GmshGeoWriter.DelaunaySurfacesLine}}" model.geo; then printf 'x' > model.msh; else cp "{{flat}}" model.msh; fi
            exit 0
            """.Replace("\r", ""));
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var low = new GmshLowering("// stand-in\n", [new("board", 1, 3, Em3dGroupKind.Volume, 1, false)], "{}", null, Built: "built-x");
        string run = Path.Combine(_root, "flat");

        var step = PalaceRun.Mesh(run, low, fake, null, default);
        Assert.True(step.Ok, step.Message);
        Assert.Equal(PalaceRun.FlatElementNote(reused: false), step.Note);
        Assert.EndsWith(GmshGeoWriter.DelaunaySurfacesLine + "\n", File.ReadAllText(Path.Combine(run, GmshGeoWriter.GeoFile)));
        Assert.Equal("x", File.ReadAllText(Path.Combine(run, GmshGeoWriter.MeshFile)));

        var again = PalaceRun.Mesh(run, low, fake, null, default);
        Assert.True(again.Reused);
        Assert.Equal(PalaceRun.FlatElementNote(reused: true), again.Note);
    }

    // ── R-emb2-2 ───────────────────────────────────────────────────────────────────────────────

    /// <summary>An EM problem builds a plated via without its air core and says so per entry; a thermal problem's lowering
    /// (the technology's wall) keeps it.</summary>
    [Fact]
    public void APlatedVia_IsSolidInAnEmProblem_AndKeepsItsWallForAThermalOne()
    {
        var (setup, source) = Em3dGeneratorTests.CaseB(plated: true);
        var em = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(em.Ok, em.Refusal);
        Assert.DoesNotContain(em.Problem!.Solids, s => s.Name.EndsWith("/fill", StringComparison.Ordinal));
        Assert.Contains(em.Notes, n => n.StartsWith("Via barrels in this EM problem are solid:", StringComparison.Ordinal) &&
                                       n.Contains("(Fill Plated, 25 µm wall) is built solid", StringComparison.Ordinal));

        var thermal = Em3dLayoutSolids.From(source, source.Technology!, null,
                                            new Em3dLayoutSolidsOptions(10e9, EmSetup.DefaultOperatingTempC, Instance: false));
        Assert.Equal(Em3dRole.Air, thermal.Solids.Single(s => s.Name == "via/1/fill").Role);
    }

    // ── R-emb2-3 ───────────────────────────────────────────────────────────────────────────────

    // Persistence (unstated reads as on, only off is written): RobustMeshingTests.ANewSetup_TurnsItOn_AndAFileThatDoesNotSayReadsOn.

    /// <summary>A port whose edge touches a 0.3 µm bump on a trace's end, which Robust meshing removes: check (the port
    /// reports) and the run (the assembled problem) infer the same port, and its edge follows the outline onto the trace.</summary>
    [Fact]
    public void APortOnAPointRobustMeshingMoves_IsInferredTheSameByCheckAndTheRun()
    {
        const int Dbu = 1000;
        static long Um(double v) => (long)Math.Round(v * Dbu);
        var top = new LayerKey(1, 0);
        string ws = Path.Combine(_root, "ws");
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), ShippedTechnologies.Load("pcb-2layer_RO4350B_20mil_1oz"));
        string cws = Path.Combine(ws, ".cws");
        WorkspacePersistence.SaveToFile(cws, new CwsFile { DefaultTechRef = "tech.ctech" });
        string cell = CellFolder.CreateCellFolder(ws, "Gap");
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new PolygonShape
        {
            Layer = top,
            Xy = [0, Um(-150), Um(1000), Um(-150), Um(1000.3), 0, Um(1000), Um(150), 0, Um(150)],
        });
        view.Shapes.Add(new RectShape { Layer = top, X1 = Um(1100), Y1 = Um(-150), X2 = Um(2000), Y2 = Um(150) });
        CellCreate.WriteLayoutView(cell, "Gap", view);
        string c3d = Path.Combine(CellFolder.SubFolderPath(cell, ViewType.ThreeD), "Gap" + C3dPersistence.Extension);
        var doc = C3dHierarchy.NewFromLayout(cell, c3d, cws).Document!;

        var robust = C3dElaborator.ElaborateOnce(doc, c3d, cws, new C3dElaborationOptions { RobustMeshing = true });
        Assert.Equal(0.3e-6, robust.OutlineMoveM, 9);
        double zTop = robust.Solids.Where(s => s.Role == Em3dRole.Conductor).Select(s => s.Primitive).OfType<Em3dExtrudedPolygon>().Max(p => p.ZTop);
        long z = (long)Math.Round(zTop * 1e6 * doc.DbuPerMicron);
        doc.Ports.Add(new C3dPort
        {
            Number = 1, Name = "P1", Plane = C3dPlane.XY, Offset = z,
            Rect = new C3dRect { Min = new C3dPoint2(Um(1000.3), Um(-100)), Size = new C3dPoint2(Um(99.7), Um(200)) },
        });

        // check's reports, as Check.cs asks for them: each setup's ports against the outline its run meshes.
        var drawn = C3dElaborator.ElaborateOnce(doc, c3d, cws);
        var report = Assert.Single(C3dPortReports.For(doc, drawn, s => s?.RobustMeshing == false ? drawn : robust)).Result;
        Assert.True(report.Resolved is not null, report.Refusal);

        var run = C3dProblemAssembly.Assemble(new EmSetup { Name = "s", Solver3D = Em3dSolverId.Palace }, doc, c3d, cws);
        Assert.True(run.Ok, run.Refusal);
        var solved = Assert.Single(run.Problem!.Ports);
        Assert.Equal((solved.PositiveObject, solved.NegativeObject, solved.Min, solved.Max),
                     (report.Resolved!.PositiveObject, report.Resolved.NegativeObject, report.Resolved.Min, report.Resolved.Max));
        Assert.Equal(1000e-6, solved.Min.X, 12);               // the edge followed the trace's end back from 1000.3 µm
        Assert.Contains("follows Robust meshing's outline by 0.3 µm", report.Reason, StringComparison.Ordinal);
    }

    // ── R-emb2-4 ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The committed hand-made mesh: a flat tetrahedron on a conductor's face, an inverted one in the board, and
    /// a port triangle that is no element's face. Each is one sentence naming its group and where it is.</summary>
    [Fact]
    public void ThePostMeshCheck_NamesAFlatElement_AnInvertedOne_AndAnOrphanTriangle_ByTheirGroups()
    {
        Em3dGroup[] groups =
        [
            new("board/FR-4", 1, 3, Em3dGroupKind.Volume, 1, false),
            new("board/Top Copper (1 oz)/6", 2, 2, Em3dGroupKind.Conductor, 1, true),
            new("port/1", 3, 2, Em3dGroupKind.Port, 1, true),
        ];
        string msh = Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "mesh-check", "flat-and-orphan.msh");
        var r = Em3dMeshCheck.Check(msh, groups)!;
        Assert.Equal((3, 1, 1, 1), (r.Tetrahedra, r.ZeroVolume, r.Inverted, r.OrphanTriangles));
        Assert.Equal(
        [
            "1 zero-volume element lies in board/Top Copper (1 oz)/6 around (5.35, 0.35) mm; " + Em3dMeshCheck.ElementRemedy,
            "1 inverted element lies in board/FR-4 around (20.25, 0.25) mm; " + Em3dMeshCheck.ElementRemedy,
            "1 surface triangle of port/1 near (1.00, 1.00) mm is a face of no element: that surface is not embedded in the " +
            "volume mesh, which the solver cannot read; " + Em3dMeshCheck.OrphanRemedy,
        ], r.Sentences);
        Assert.StartsWith("The mesh has elements the solver cannot use. 1 zero-volume element", r.Refusal);

        // A run with Robust meshing on is not told to turn it on.
        var on = Em3dMeshCheck.Check(msh, groups, robustMeshing: true)!;
        Assert.Equal(r.Sentences.Count, on.Sentences.Count);
        Assert.All(on.Sentences, x => Assert.DoesNotContain("robust", x, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A via's Fill is offered only where the elements touch a via: elsewhere it changes nothing.</summary>
    [Fact]
    public void TheElementRemedy_NamesAViaOnlyWhereTheElementsTouchOne()
    {
        string msh = Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "mesh-check", "flat-and-orphan.msh");
        var mesh = MshReader.Read(msh);
        List<Em3dGroup> Groups(string metal) =>
        [
            new("board", 1, 3, Em3dGroupKind.Volume, 1, false),
            new(metal, 2, 2, Em3dGroupKind.Conductor, 1, true),
            new("port/1", 3, 2, Em3dGroupKind.Port, 1, true),
        ];
        Assert.Contains(Em3dMeshCheck.Check(mesh, Groups("via/1")).Sentences, x => x.EndsWith(Em3dMeshCheck.ElementRemedyVia, StringComparison.Ordinal));
        Assert.DoesNotContain(Em3dMeshCheck.Check(mesh, Groups("Top Copper (1 oz)/6")).Sentences, x => x.Contains("via entry", StringComparison.Ordinal));
    }

    // ── R-emb2-5 ───────────────────────────────────────────────────────────────────────────────

    /// <summary>A drawn bottom plane with a hole nothing lies over is the floor; a bottom-layer trace inside that hole keeps
    /// the floor off the plane, and the note names the hole.</summary>
    [Fact]
    public void ABottomPlaneWithAClearHole_IsTheFloor_AndOneWithATraceInTheHoleIsNot()
    {
        (EmSetup, EmLayoutSource) Board(bool traceInHole)
        {
            var (setup, source) = Em3dGeneratorTests.Microstrip();
            var ground = source.Technology!.Stackup.Layers.Last(l => l.Kind == StackupKind.Conductor && l.IsGroundReference);
            var layer = ground.DrawingLayers[0];
            source.View.Shapes.Add(new PolygonShape
            {
                Layer = layer,
                Xy = [0, -3_000_000, 10_000_000, -3_000_000, 10_000_000, 3_000_000, 0, 3_000_000],
                Holes = [[2_000_000, 1_500_000, 3_000_000, 1_500_000, 3_000_000, 2_500_000, 2_000_000, 2_500_000]],
            });
            if (traceInHole)
                source.View.Shapes.Add(new RectShape { Layer = layer, X1 = 2_200_000, Y1 = 1_900_000, X2 = 2_800_000, Y2 = 2_100_000 });
            return (setup, source);
        }
        var (s1, src1) = Board(traceInHole: false);
        var clear = Em3dGenerator.Generate(s1, src1, src1.Technology!);
        Assert.Equal(Em3dBoundaryKind.Pec, clear.Problem!.Boundary.Faces.ZMin);
        Assert.Contains(clear.Notes, n => n.Contains("whose 1 hole(s) have no other metal over them", StringComparison.Ordinal));

        var (s2, src2) = Board(traceInHole: true);
        var covered = Em3dGenerator.Generate(s2, src2, src2.Technology!);
        Assert.NotEqual(Em3dBoundaryKind.Pec, covered.Problem!.Boundary.Faces.ZMin);
        Assert.Contains(covered.Notes, n => n.Contains("has a hole at (2.5, 2) mm", StringComparison.Ordinal));
    }

    /// <summary>A via on the plane's own net through one of its holes (a ground via's drill) leaves the floor on the plane: it is
    /// joined to the plane already, so closing the hole shorts nothing. The same via on another net keeps the floor off.</summary>
    [Fact]
    public void AGroundViaInAHoleOfTheBottomPlane_LeavesTheFloorOnIt_AndASignalViaDoesNot()
    {
        Em3dGenerationResult Board(string viaNet)
        {
            var (setup, source) = Em3dGeneratorTests.Microstrip();
            var ground = source.Technology!.Stackup.Layers.Last(l => l.Kind == StackupKind.Conductor && l.IsGroundReference);
            source.View.Shapes.Add(new PolygonShape
            {
                Layer = ground.DrawingLayers[0], Net = "GND",
                Xy = [0, -3_000_000, 10_000_000, -3_000_000, 10_000_000, 3_000_000, 0, 3_000_000],
                Holes = [[2_000_000, 1_500_000, 3_000_000, 1_500_000, 3_000_000, 2_500_000, 2_000_000, 2_500_000]],
            });
            source.View.Shapes.Add(new ViaShape { X = 2_500_000, Y = 2_000_000, PadSize = 600_000, DrillSize = 300_000,
                                                  Layer = new LayerKey(7, 0), Net = viaNet });
            return Em3dGenerator.Generate(setup, source, source.Technology!);
        }
        Assert.Equal(Em3dBoundaryKind.Pec, Board("GND").Problem!.Boundary.Faces.ZMin);
        Assert.NotEqual(Em3dBoundaryKind.Pec, Board("SIG").Problem!.Boundary.Faces.ZMin);
    }

    // ── R-emb2-6 ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Gmsh 4.15's own lines, as it prints them at verbosity 5: every stage is named, the high-order stage with its
    /// node count, and the time spent curving is measured from its first line until the write.</summary>
    [Fact]
    public void GmshsStageLines_NameEveryStage_AndTheHighOrderOneCarriesItsNodeCount()
    {
        long now = 0;
        var log = new GmshLogProgress(null) { Clock = () => now };
        var seen = new List<string?>();
        foreach (string line in new[]
                 {
                     "Info    : Meshing 1D...", "Info    : Meshing 2D...", "Info    : Meshing 3D...",
                     "Info    :  - 2995 tetrahedra created in 0.00412279 sec. (726449 tets/s)",
                     "Info    : Done meshing 3D (Wall 0.0158171s, CPU 0.015495s)", "Info    : Optimizing mesh...",
                     "Info    : Meshing order 2 (curvilinear on)...", "Info    : Optimizing mesh (HighOrderElastic)...",
                     "Info    : High-order tools set up: 330000 nodes are considered",
                 })
        {
            log.Line(line);
            if (seen.Count == 0 || seen[^1] != log.Stage) seen.Add(log.Stage);
        }
        Assert.Equal(["meshing curves", "meshing surfaces", "Meshing 3D", "Done meshing 3D", "optimising the mesh",
                      "second-order elements", GmshLogProgress.HighOrderWords, "high-order optimisation, 330,000 nodes"], seen);
        Assert.Equal(2995, log.Tetrahedra);

        now += 6 * 60 * Stopwatch.Frequency;
        Assert.True(log.CurvingPast(5));
        log.Line("Info    : Writing 'model.msh'...");
        Assert.Null(log.CurvingFor);
        Assert.Equal("writing the mesh", log.Stage);
    }
}
