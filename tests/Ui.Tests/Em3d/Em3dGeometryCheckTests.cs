using CircuitRF.Core.Design;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Mom;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-dfr35-1 R-dfr35-1-7/8/9 — each class the geometry check reports has a small board that stops Gmsh with Robust meshing
//  off, and the check names it, with the object and its place, before Gmsh runs. In the Gmsh-counting collection: these start Gmsh.
// ══════════════════════════════════════════════════════════════════════════════════════════════

[Collection(Em3dProcessCountersCollection.Name)]
public sealed class Em3dGeometryCheckTests : IDisposable
{
    private const int Dbu = 1000;
    private readonly string _root = Directory.CreateTempSubdirectory("dfr35-1-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static long Um(double v) => (long)Math.Round(v * Dbu);

    private static long[] Xy(params double[] um) => [.. um.Select(Um)];

    /// <summary>A 2 × 2 mm four-layer board with <paramref name="plane"/> drawn on Inner 2, meshed as drawn.</summary>
    private static (EmSetup Setup, EmLayoutSource Source) Board(long[] plane)
    {
        var tech = ShippedTechnologies.Load("pcb-4layer_FR-4_62mil_1oz");
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new RectShape { Layer = new LayerKey(11, 0), X1 = 0, Y1 = 0, X2 = Um(2000), Y2 = Um(2000) });   // Outline
        view.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = Um(400), Y1 = Um(800), X2 = Um(1200), Y2 = Um(1200), Net = "SIG" });
        view.Shapes.Add(new RectShape { Layer = new LayerKey(2, 0), X1 = 0, Y1 = 0, X2 = Um(2000), Y2 = Um(2000), Net = "GND" });
        view.Shapes.Add(new PolygonShape { Layer = new LayerKey(3, 0), Net = "GND", Xy = plane });
        var face = new EmAirBoxFace(500, null);
        var setup = new EmSetup
        {
            Name = "check", LayoutRef = "check/layout/check.clay",
            Frequency = new FrequencySpec("1", "3", 3, SweepKind.Linear, "GHz", "GHz"),
            Solver3D = Em3dSolverId.Palace,
            Problem3D = Em3dProblemType.Electrostatic,
            Terminals3D = [new("SIG", "SIG")],
            Ground3D = "GND",
            AirBox = new EmAirBox(face, face, face, face, face, face),
            RobustMeshing = false,
        };
        return (setup, new EmLayoutSource("/nowhere/check.clay", view, tech, Dbu));
    }

    private PalaceStep Mesh(EmSetup setup, EmLayoutSource source, out Em3dGenerationResult g)
    {
        g = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(g.Ok, g.Refusal);
        var low = GmshGeoWriter.Write(g.Problem!, PalaceSettings.Resolve(setup.Palace));
        Assert.True(low.Ok, low.Refusal);
        return PalaceRun.Mesh(Path.Combine(_root, "mesh"), low, Gmsh(), null, default);
    }

    private static string Gmsh() => SolverDiscovery.Gmsh.Check(SolverDiscovery.CapabilitiesFor(SolverTool.Gmsh)).Installation!.Path;

    private static void AssertNamed(Em3dGenerationResult g, Em3dGeometryKind kind, string obj, double xUm, double yUm, double withinUm)
    {
        var f = Assert.Single(g.GeometryFindings, x => x.Kind == kind);
        Assert.Equal(obj, f.Object);
        Assert.InRange(f.XUm, xUm - withinUm, xUm + withinUm);
        Assert.InRange(f.YUm, yUm - withinUm, yUm + withinUm);
        Assert.Contains(g.Warnings, w => w.StartsWith("Geometry that can stop the mesher", StringComparison.Ordinal) && w.Contains($"'{obj}'", StringComparison.Ordinal));
    }

    /// <summary>The field's needle: the outline runs up a slanted edge past where it turns, and straight back along it.</summary>
    [GmshFact]
    public void AnOutlineThatTurnsBack_IsNamed_AndGmshStopsThere_WhichTheFailureSays()
    {
        var (setup, source) = Board(Xy(1015.55, 1296.836, 1003.909, 1347.835, 1010.2, 1320.274, 1010.2, 200, 1800, 200, 1800, 1800, 1200, 1800));

        var step = Mesh(setup, source, out var g);

        AssertNamed(g, Em3dGeometryKind.Reversal, "Inner 2/GND", 1003.909, 1347.835, 0.01);
        Assert.False(step.Ok);
        Assert.Contains("overlapping facets", step.Message!, StringComparison.Ordinal);
        // R-dfr35-1-8 — two faces of one object read as such, and the failure says where: the needle's own place.
        Assert.Contains("The overlap is between two faces of 'Inner 2/GND', at (", step.Message!, StringComparison.Ordinal);
        Assert.NotNull(step.Place);
        Assert.InRange(step.Place!.XUm, 1000, 1020);
        Assert.InRange(step.Place.YUm, 1290, 1350);
    }

    /// <summary>A wedge-shaped slit, 0.6 µm at its mouth and 400 µm long: 0.086°, wider than a needle, and under the facet angle.</summary>
    [GmshFact]
    public void AWedgeShapedSlit_IsNamed_AndGmshStopsThere()
    {
        var (setup, source) = Board(Xy(200, 200, 1800, 200, 1800, 1000, 1400, 1000, 1800, 1000.6, 1800, 1800, 200, 1800));

        var step = Mesh(setup, source, out var g);

        AssertNamed(g, Em3dGeometryKind.Sliver, "Inner 2/GND", 1400, 1000, 0.01);
        Assert.False(step.Ok);
    }

    /// <summary>A cut of no width, from a plane's side round a hole and back: outlines stored as one ring without holes.</summary>
    [GmshFact]
    public void AKeyholeCut_IsNamed_AndGmshStopsThere()
    {
        var (setup, source) = Board(Xy(200, 200, 1800, 200, 1800, 1000, 1400, 1000, 1400, 600, 600, 600, 600, 1400, 1400, 1400, 1400, 1000, 1800, 1000, 1800, 1800, 200, 1800));

        var step = Mesh(setup, source, out var g);

        AssertNamed(g, Em3dGeometryKind.Sliver, "Inner 2/GND", 1600, 1000, 1);
        Assert.False(step.Ok);
    }

    /// <summary>The 0.0127° case of brief-dfr31-6: a plane's side leaving the board's edge by 0.109 µm over 492 µm.</summary>
    [GmshFact]
    public void TwoFacesPartingUnderTheFacetAngle_AreNamed_AndGmshStopsThere()
    {
        double xe = 2000;
        var (setup, source) = Board(Xy(1000, 500, xe, 500, xe - 0.109, 992.375, 1000, 992.375));

        var step = Mesh(setup, source, out var g);

        var f = Assert.Single(g.GeometryFindings, x => x.Kind == Em3dGeometryKind.NearFaces);
        Assert.True(f.Object == "Inner 2/GND" || f.Other == "Inner 2/GND", f.Sentence);
        Assert.InRange(f.XUm, xe - 1, xe + 1);
        Assert.False(step.Ok);
    }

    /// <summary>What was measured to mesh is not a warning: a parallel slit 0.2 µm wide, a parallel jog against the board's edge. A
    /// short edge meeting the board's edge meshes too, and is a note.</summary>
    [Theory]
    [InlineData(200, 200, 1800, 200, 1800, 1000, 1500, 1000, 1500, 1000.2, 1800, 1000.2, 1800, 1800, 200, 1800)]
    [InlineData(1000, 500, 2000, 500, 2000, 750, 1999.891, 750, 1999.891, 1000, 1000, 1000)]
    [InlineData(1000, 500, 1999.4, 500, 2000, 500.6, 2000, 1000, 1000, 1000)]
    public void WhatMeshes_IsNoWarning(params double[] plane)
    {
        var (setup, source) = Board(Xy(plane));
        var g = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(g.Ok, g.Refusal);
        Assert.DoesNotContain(g.Warnings, w => w.StartsWith("Geometry that can stop the mesher", StringComparison.Ordinal));
    }

    /// <summary>A short edge where a plane meets the board's edge, far below the element there, is a finding that is no warning.</summary>
    [Fact]
    public void AShortEdgeMeetingAnotherObject_IsANote()
    {
        var (setup, source) = Board(Xy(1000, 500, 1999.4, 500, 2000, 500.6, 2000, 1000, 1000, 1000));
        var g = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.Contains(g.GeometryFindings, f => f.Kind == Em3dGeometryKind.ShortEdge && f.Object == "Inner 2/GND");
        Assert.Contains(Em3dGeometryCheck.NoteSentences(g.GeometryFindings), n => n.StartsWith("An edge far below the element", StringComparison.Ordinal));
    }

    /// <summary>A plain board has nothing to report: a rectangle on the board's edge shares that line exactly.</summary>
    [Fact]
    public void ARectangleOnTheBoardEdge_IsNotAFinding()
    {
        var (setup, source) = Board(Xy(1000, 500, 2000, 500, 2000, 1000, 1000, 1000));
        var g = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(g.Ok, g.Refusal);
        Assert.Empty(g.GeometryFindings);
    }

    /// <summary>The setup panel says it too, off the UI thread, without being asked; and a refresh that replaces the setup drops it.</summary>
    [Fact]
    public async Task TheSetupPanel_WarnsOfGeometryThatCanStopTheMesher()
    {
        var (setup, source) = Board(Xy(1015.55, 1296.836, 1003.909, 1347.835, 1010.2, 1320.274, 1010.2, 200, 1800, 200, 1800, 1800, 1200, 1800));
        var vm = new CircuitRF.Ui.Layout.Em.EmSetupEditorViewModel(Path.Combine(_root, "check.cem"), setup) { ResolveLayout = _ => source };

        vm.Refresh();
        await vm.GeometryCheck;

        Assert.Contains(vm.Warnings, w => w.StartsWith("Geometry that can stop the mesher", StringComparison.Ordinal) && w.Contains("'Inner 2/GND'", StringComparison.Ordinal));
    }

    /// <summary>R-dfr35-1-8 — the row's action selects what is at the place and frames the place, not the whole pour.</summary>
    [Fact]
    public void ShowPlace_SelectsTheShapeThere_AndFramesThePlace()
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = Um(2000), Y2 = Um(2000) });
        var vm = new CircuitRF.Ui.Layout.LayoutEditorViewModel(view);
        Bbox? framed = null;
        vm.ZoomToRegionRequested += b => framed = b;

        vm.ShowPlace(1000, 1000, 10);

        Assert.Equal([0], vm.SelectedIndices);
        Assert.NotNull(framed);
        Assert.True(framed!.Value.MaxX - framed.Value.MinX < Um(100));
    }

    // ── R-dfr35-1-9 — a sheet right across a solid ───────────────────────────────────────────────

    /// <summary>
    /// The crop's case: a pour from one edge of the board to the other, ending in a port on its whole edge. The sheet reaches
    /// across the dielectric from side to side and the fragment splits it, so the dielectric's tag is gone after it. Before the fix
    /// the face table's Boundary{} on that tag stopped Gmsh ("Unknown model region"); the dielectric is now its two pieces.
    /// </summary>
    [GmshFact]
    public void APortSheetRightAcrossTheDielectric_Meshes_WithTheDielectricInTwoPieces()
    {
        var tech = ShippedTechnologies.Load("pcb-4layer_FR-4_62mil_1oz");
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new RectShape { Layer = new LayerKey(11, 0), X1 = 0, Y1 = 0, X2 = Um(2000), Y2 = Um(2000) });   // Outline
        view.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = Um(1000), Y2 = Um(2000), Net = "SIG" });
        view.Shapes.Add(new RectShape { Layer = new LayerKey(2, 0), X1 = 0, Y1 = 0, X2 = Um(2000), Y2 = Um(2000), Net = "GND" });
        view.Shapes.Add(new LabelShape
        {
            Layer = new LayerKey(1, 0), PortLayer = new LayerKey(1, 0), X = Um(1000), Y = Um(1000), Text = "P1", Height = Um(50), IsPort = true,
            PortKind = PlanarPortKind.Edge, PortDirection = LayoutRotation.R180,
        });
        var setup = new EmSetup
        {
            Name = "split", LayoutRef = "split/layout/split.clay", Solver3D = Em3dSolverId.Palace,
            Frequency = new FrequencySpec("1", "3", 3, SweepKind.Linear, "GHz", "GHz"),
        };
        var source = new EmLayoutSource("/nowhere/split.clay", view, tech, Dbu);

        var step = Mesh(setup, source, out var g);

        Assert.True(step.Ok, step.Message);
        // The top dielectric is the one the sheet crosses: two volumes, none lost, and every volume meshed belongs to a group.
        string table = File.ReadAllText(Path.Combine(_root, "mesh", GmshGeoWriter.EntitiesFile));
        var sheet = g.Problem!.Ports.Single();
        Assert.Equal(2e-3, sheet.Max.Y - sheet.Min.Y, 9);
        Assert.True(System.Text.RegularExpressions.Regex.IsMatch(table, @"(?m)^group \d+ 2 0$"), table);
    }

    /// <summary>The face table is diagnostic: no tag in a script can be handed to Boundary{} unchecked.</summary>
    [Fact]
    public void TheFaceTable_AsksOnlyAfterVolumesThatExist()
    {
        var (setup, source) = Board(Xy(1000, 500, 2000, 500, 2000, 1000, 1000, 1000));
        var g = Em3dGenerator.Generate(setup, source, source.Technology!);
        string geo = GmshGeoWriter.Write(g.Problem!, PalaceSettings.Resolve(setup.Palace)).Geo!;
        string table = geo[geo.IndexOf("the face table", StringComparison.Ordinal)..];
        Assert.DoesNotContain("Boundary{ Volume{s", table, StringComparison.Ordinal);
        Assert.Contains("crfv[] -= crfg[];", table, StringComparison.Ordinal);
    }
}
