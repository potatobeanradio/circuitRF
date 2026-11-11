using CircuitRF.Core.Design;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

// A pour with a round hole tangent to its edge, a 72-sided hole with a corner exactly on that edge (designer round 37). OpenCASCADE does
// not split a face's outer wire against its own holes, so Gmsh's 1D mesh crosses at the corner. In the Gmsh-counting collection: these start Gmsh.
[Collection(Em3dProcessCountersCollection.Name)]
public sealed class HoleTouchingOutlineTests : IDisposable
{
    private const int Dbu = 1000;
    private readonly string _root = Directory.CreateTempSubdirectory("hole-touch-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static long Um(double v) => (long)Math.Round(v * Dbu);

    private static (EmSetup Setup, EmLayoutSource Source) Board(bool robust)
    {
        // The pour from x = 500 µm, its hole of radius 250 µm centred 250 µm in, so a corner of the hole lies on the pour's left edge.
        var hole = new long[144];
        for (int i = 0; i < 72; i++)
        {
            hole[2 * i] = Um(750 + 250 * Math.Cos(Math.PI + i * Math.PI / 36));
            hole[2 * i + 1] = Um(1000 + 250 * Math.Sin(Math.PI + i * Math.PI / 36));
        }
        var tech = ShippedTechnologies.Load("pcb-4layer_FR-4_62mil_1oz");
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new RectShape { Layer = new LayerKey(11, 0), X1 = 0, Y1 = 0, X2 = Um(2000), Y2 = Um(2000) });   // Outline
        view.Shapes.Add(new RectShape { Layer = new LayerKey(2, 0), X1 = 0, Y1 = 0, X2 = Um(2000), Y2 = Um(2000), Net = "GND" });
        view.Shapes.Add(new PolygonShape
        {
            Layer = new LayerKey(1, 0), Net = "SIG",
            Xy = [Um(500), Um(500), Um(1500), Um(500), Um(1500), Um(1500), Um(500), Um(1500)],
            Holes = [hole],
        });
        var face = new EmAirBoxFace(500, null);
        var setup = new EmSetup
        {
            Name = "touch", LayoutRef = "touch/layout/touch.clay",
            Frequency = new FrequencySpec("1", "3", 3, SweepKind.Linear, "GHz", "GHz"),
            Solver3D = Em3dSolverId.Palace,
            Problem3D = Em3dProblemType.Electrostatic,
            Terminals3D = [new("SIG", "SIG")],
            Ground3D = "GND",
            AirBox = new EmAirBox(face, face, face, face, face, face),
            RobustMeshing = robust,
        };
        return (setup, new EmLayoutSource("/nowhere/touch.clay", view, tech, Dbu));
    }

    private PalaceStep Mesh(bool robust, out Em3dGenerationResult g)
    {
        var (setup, source) = Board(robust);
        g = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(g.Ok, g.Refusal);
        var low = GmshGeoWriter.Write(g.Problem!, PalaceSettings.Resolve(setup.Palace));
        Assert.True(low.Ok, low.Refusal);
        string gmsh = SolverDiscovery.Gmsh.Check(SolverDiscovery.CapabilitiesFor(SolverTool.Gmsh)).Installation!.Path;
        return PalaceRun.Mesh(Path.Combine(_root, "mesh"), low, gmsh, null, default);
    }

    /// <summary>As drawn, Gmsh stops, the check said so first, and the refusal names the pour.</summary>
    [GmshFact]
    public void AsDrawn_GmshRefuses_TheCheckWarns_AndTheRefusalNamesThePour()
    {
        var step = Mesh(robust: false, out var g);

        Assert.False(step.Ok);
        Assert.Matches("overlapping facets|Could not recover boundary mesh", step.Message);
        Assert.Contains("'Top Copper (1 oz)/SIG'", step.Message);
        var touch = Assert.Single(Em3dGeometryCheck.Check(g.Problem!, 0), f => f.Kind == Em3dGeometryKind.PointTouch);
        Assert.Equal(500, touch.XUm, 6);
        Assert.Equal(1000, touch.YUm, 6);
    }

    /// <summary>Robust meshing moves the corner by its tolerance into the hole, says so, and the board meshes.</summary>
    [GmshFact]
    public void Robust_OpensTheTouch_AndTheBoardMeshes()
    {
        var step = Mesh(robust: true, out var g);

        Assert.True(step.Ok, step.Message);
        Assert.Contains(g.Notes, n => n.Contains("moved 1 corner off a stretch", StringComparison.Ordinal));
        var pour = Assert.IsType<Em3dExtrudedPolygon>(g.Problem!.Solids.Single(s => s.Name.Contains("/SIG", StringComparison.Ordinal)).Primitive);
        Assert.Contains(pour.Holes.Single(), q => Math.Abs(q.X - 500.5e-6) < 1e-9 && Math.Abs(q.Y - 1000e-6) < 1e-9);
        Assert.DoesNotContain(Em3dGeometryCheck.Check(g.Problem!, 0), f => f.Kind == Em3dGeometryKind.PointTouch);
    }
}
