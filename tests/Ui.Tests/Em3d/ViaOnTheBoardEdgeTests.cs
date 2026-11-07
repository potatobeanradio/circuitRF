using CircuitRF.Core.Design;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

// brief-dfr28-1 — a through via whose centre is on the board edge: the 3D build keeps only the part of its barrel on the
// board, so the mesher never meets half a tube standing in the air beside the board's side face. Synthetic geometry on
// the shipped ten-layer technology: on the two-layer one the cylinder happened to mesh, and the gate has to be a board
// that failed. In the Gmsh-counting collection: one test here starts Gmsh.
[Collection(Em3dProcessCountersCollection.Name)]
public sealed class ViaOnTheBoardEdgeTests : IDisposable
{
    private const int Dbu = 1000;
    private readonly string _root = Directory.CreateTempSubdirectory("dfr28-1-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static long Mm(double v) => (long)Math.Round(v * 1000 * Dbu);

    /// <summary>A 4 × 3 mm ten-layer board: a GND plane on each inner layer, a SIG pad on the top (the electrostatic
    /// terminal), one GND through via at (<paramref name="xMm"/>, <paramref name="yMm"/>) mm, and a millimetre of air all
    /// round. Built as a cylinder at (2, 3), the via stopped Gmsh with the designer's own PLC error.</summary>
    private static (EmSetup Setup, EmLayoutSource Source, Technology Tech) Board(double xMm, double yMm)
    {
        var tech = ShippedTechnologies.Load("pcb-10layer_FR-4_1.7mm_1oz");
        var conductors = tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor).Select(l => l.DrawingLayers[0]).ToList();
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new RectShape { Layer = new LayerKey(8, 0), X1 = 0, Y1 = 0, X2 = Mm(4), Y2 = Mm(3) });   // Outline
        view.Shapes.Add(new RectShape { Layer = conductors[0], X1 = Mm(1), Y1 = Mm(0.3), X2 = Mm(3), Y2 = Mm(1.2), Net = "SIG" });
        foreach (var inner in conductors.Skip(1).SkipLast(1))
            view.Shapes.Add(new RectShape { Layer = inner, X1 = 0, Y1 = 0, X2 = Mm(4), Y2 = Mm(3), Net = "GND" });
        view.Shapes.Add(new ViaShape
        {
            Layer = new LayerKey(7, 0), X = Mm(xMm), Y = Mm(yMm), DrillSize = Mm(0.3), PadSize = Mm(0.6), Net = "GND",   // Via 1-10
        });
        var face = new EmAirBoxFace(1000, null);
        var setup = new EmSetup
        {
            Name = "edge", LayoutRef = "edge/layout/edge.clay",
            Frequency = new FrequencySpec("1", "3", 3, SweepKind.Linear, "GHz", "GHz"),
            Solver3D = Em3dSolverId.Palace,
            Problem3D = Em3dProblemType.Electrostatic,
            Terminals3D = [new("SIG", "SIG")],
            Ground3D = "GND",
            AirBox = new EmAirBox(face, face, face, face, face, face),
        };
        return (setup, new EmLayoutSource("/nowhere/edge.clay", view, tech, Dbu), tech);
    }

    /// <summary>The same board with its top edge slanted, so the edge is on no axis and the via at (2, y) mm on it is cut where
    /// the clip has to round the crossing.</summary>
    private static (EmSetup Setup, EmLayoutSource Source, Technology Tech) SlantedBoard()
    {
        var (setup, source, tech) = Board(2, 2.6585);
        long[] outline = [0, 0, Mm(4), 0, Mm(4), Mm(2), 0, 3317 * Dbu];
        var shapes = source.View.Shapes;
        for (int i = 0; i < shapes.Count; i++)
            if (shapes[i] is RectShape { X1: 0, Y1: 0 } full)
                shapes[i] = new PolygonShape { Layer = full.Layer, Net = full.Net, Xy = outline };
        return (setup, source, tech);
    }

    private static IEnumerable<Point2> Corners(Em3dSolid s) => s.Primitive is Em3dExtrudedPolygon e ? e.Outline : [];

    /// <summary>Where the slanted edge cuts the via, the via and every solid along that edge share a corner, coordinate for coordinate.
    /// Rounded apart by a picometre, the two sides are two faces parting at 10⁻⁵°, and Gmsh stops on them.</summary>
    [Fact]
    public void WhereASlantedEdgeCutsAVia_TheViaAndTheBoardShareTheCorner()
    {
        var (setup, source, tech) = SlantedBoard();
        var g = Em3dGenerator.Generate(setup, source, tech);
        Assert.True(g.Ok, g.Refusal);
        var via = g.Problem!.Solids.Single(s => s.Name == "via/1");
        var slabs = g.Problem.Solids.Where(s => s.Role == Em3dRole.Dielectric).ToList();
        Assert.NotEmpty(slabs);
        var crossings = Corners(via).Where(q => Math.Abs(q.Y - (2e-3 + (4e-3 - q.X) * 1.317 / 4)) < 1e-9).ToList();
        Assert.Equal(2, crossings.Count);
        foreach (var slab in slabs)
            foreach (var q in crossings)
                Assert.Contains(q, Corners(slab));
        Assert.DoesNotContain(g.GeometryFindings, f => f.Kind == Em3dGeometryKind.NearFaces);
    }

    /// <summary>The check before meshing names two sides starting from one point and parting by a picometre.</summary>
    [Fact]
    public void TheGeometryCheck_NamesASideAPicometreOffAnother()
    {
        var (setup, source, tech) = SlantedBoard();
        var g = Em3dGenerator.Generate(setup, source, tech);
        Assert.True(g.Ok, g.Refusal);
        var problem = g.Problem!;
        var via = problem.Solids.Single(s => s.Name == "via/1");
        var e = (Em3dExtrudedPolygon)via.Primitive;
        // The crossing whose neighbour along the edge is a board corner: lifted off the edge by 0.7 pm, as an unshared crossing is.
        var ring = e.Outline.ToList();
        var board = (Em3dExtrudedPolygon)problem.Solids.First(s => s.Role == Em3dRole.Dielectric).Primitive;
        int k = ring.FindIndex(q => board.Outline.Contains(q) && board.Outline.Contains(ring[(ring.IndexOf(q) + 1) % ring.Count]));
        Assert.True(k >= 0);
        var (a, b) = (ring[k], ring[(k + 1) % ring.Count]);
        double l = Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));
        ring[(k + 1) % ring.Count] = new Point2(b.X + 0.7e-12 * (a.Y - b.Y) / l, b.Y + 0.7e-12 * (b.X - a.X) / l);
        var solids = problem.Solids.Select(s => s.Name == via.Name ? s with { Primitive = e with { Outline = ring } } : s).ToList();

        var found = Em3dGeometryCheck.Check(problem with { Solids = solids }, 0);

        Assert.Contains(found, f => f.Kind == Em3dGeometryKind.NearFaces && f.Sentence.Contains("part by 0.7 pm", StringComparison.Ordinal));
    }

    /// <summary>
    /// R-dfr28-1-3 — the via centred on the board's top edge meshes. Built as a cylinder, its tube and air fill stood half in
    /// the air padding and Gmsh stopped on "a segment and a facet intersect"; now the barrel is the disc's part on the board
    /// and the run's note names the via.
    /// </summary>
    [GmshFact]
    public void AThroughViaCentredOnTheBoardEdge_Meshes_AndTheNoteNamesIt()
    {
        var (setup, source, tech) = Board(2, 3);
        var g = Em3dGenerator.Generate(setup, source, tech);
        Assert.True(g.Ok, g.Refusal);
        Assert.Contains(g.Notes, n => n.Contains("1 via(s) cross the board edge, the first ('via/1') at (2, 3) mm",
                                                 StringComparison.Ordinal));
        Assert.DoesNotContain(g.Problem!.Solids, s => s.Name.StartsWith("via/1", StringComparison.Ordinal) && s.Primitive is Em3dCylinder);
        var low = GmshGeoWriter.Write(g.Problem!, PalaceSettings.Resolve(setup.Palace));
        Assert.True(low.Ok, low.Refusal);

        var step = PalaceRun.Mesh(Path.Combine(_root, "mesh"), low, Gmsh(), null, default);

        Assert.True(step.Ok, step.Message);
    }

    /// <summary>R-dfr28-3-0a — the edge-cut via bounds the content by its part on the board, so the box's YMax is the board
    /// edge at 3 mm plus the fixture's 1 mm of padding, not the drill circle's far side plus it.</summary>
    [Fact]
    public void AnEdgeCutVia_BoundsTheBoxByItsPartOnTheBoard()
    {
        var (setup, source, tech) = Board(2, 3);
        var g = Em3dGenerator.Generate(setup, source, tech);
        Assert.True(g.Ok, g.Refusal);
        Assert.Equal(4e-3, g.Problem!.Boundary.Max.Y, 12);
    }

    /// <summary>A via wholly on the board is the cylinder it always was; one wholly off it is left out and named.</summary>
    [Fact]
    public void AViaOnTheBoardIsACylinder_AndOneOffItIsLeftOut()
    {
        var (setup, source, tech) = Board(2, 2.2);
        var on = Em3dGenerator.Generate(setup, source, tech);
        Assert.True(on.Ok, on.Refusal);
        Assert.IsType<Em3dCylinder>(on.Problem!.Solids.Single(s => s.Name == "via/1").Primitive);
        Assert.DoesNotContain(on.Notes, n => n.Contains("board edge", StringComparison.Ordinal));

        (setup, source, tech) = Board(2, 3.5);
        var off = Em3dGenerator.Generate(setup, source, tech);
        Assert.True(off.Ok, off.Refusal);
        Assert.DoesNotContain(off.Problem!.Solids, s => s.Name.StartsWith("via/", StringComparison.Ordinal));
        Assert.Contains(off.Notes, n => n.Contains("'via/1' lies wholly off the board", StringComparison.Ordinal));
    }

    /// <summary>
    /// brief-dfr31-5 R-dfr31-5-6 — Crop to Rectangle draws the cropped board as a stroke along its new edge, and a solve region
    /// drawn up to that edge cut the stroke into slivers of ink that the 3D build took for the board: 63 of the 70 vias inside
    /// the region were listed as "wholly off the board" and left out. The region rebuilds the outline as the crop does, so
    /// every via whose centre is inside the region is built, and none is called off the board.
    /// </summary>
    [Fact]
    public void ASolveRegionReachingACroppedEdge_KeepsTheBoard_AndEveryViaInsideIt()
    {
        var tech = ShippedTechnologies.Load("pcb-10layer_FR-4_1.7mm_1oz");
        var conductors = tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor).Select(l => l.DrawingLayers[0]).ToList();
        var shapes = new List<LayoutShape>
        {
            new PathShape { Layer = new LayerKey(8, 0), Width = Mm(0.1), Xy = [0, 0, Mm(20), 0, Mm(20), Mm(16), 0, Mm(16), 0, 0] },
        };
        foreach (var inner in conductors.Skip(1).SkipLast(1))
            shapes.Add(new RectShape { Layer = inner, X1 = 0, Y1 = 0, X2 = Mm(20), Y2 = Mm(16), Net = "GND" });
        for (int i = 1; i < 20; i++)
            for (int j = 1; j < 16; j++)
                shapes.Add(new ViaShape { Layer = new LayerKey(7, 0), X = Mm(i), Y = Mm(j), DrillSize = Mm(0.3), PadSize = Mm(0.6), Net = "GND" });
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.AddRange(LayoutCrop.Crop(shapes, [], new Bbox(Mm(3), Mm(3), Mm(13), Mm(13)), tech).Shapes);
        Assert.IsType<PathShape>(Assert.Single(view.Shapes, s => s.Layer == new LayerKey(8, 0)));

        var face = new EmAirBoxFace(1000, null);
        var setup = new EmSetup
        {
            Name = "region", LayoutRef = "region/layout/region.clay",
            Frequency = new FrequencySpec("1", "3", 3, SweepKind.Linear, "GHz", "GHz"),
            Solver3D = Em3dSolverId.Palace,
            AirBox = new EmAirBox(face, face, face, face, face, face),
            SolveRegion = EmSolveRegion.FromCorners(3000, 5000, 12000, 11000),
        };
        var g = Em3dGenerator.Generate(setup, new EmLayoutSource("/nowhere/region.clay", view, tech, Dbu), tech, portsOptional: true);

        Assert.NotNull(g.Problem);
        Assert.DoesNotContain(g.Notes, n => n.Contains("wholly off the board", StringComparison.Ordinal));
        // Centres at x = 3…12, y = 5…11 mm: ten columns of seven.
        Assert.Equal(70, g.Problem!.Solids.Select(s => s.Name.Split('/')).Where(p => p[0] == "via" && p.Length >= 2)
                                         .Select(p => p[1]).Distinct().Count());
    }

    /// <summary>
    /// The 3D view of that via (owner report, 2026-10-28): its barrel's flat face lies in the board's side, and the inner
    /// planes and the slabs it passes through ran on under it, so their edges and the face were two surfaces in one place —
    /// unseen in one colour, broken up at every zoom once the via was hovered and tinted. The view cuts the barrel's outline
    /// out of each of them, so nothing of theirs lies on the flat face.
    /// </summary>
    [Fact]
    public void AnEdgeCutVia_IsCutOutOfThePlanesAndSlabsItPassesThrough_InTheView()
    {
        var (setup, source, tech) = Board(2, 3);
        var g = Em3dGenerator.Generate(setup, source, tech);
        Assert.True(g.Ok, g.Refusal);
        var problem = g.Problem!;
        var bores = CircuitRF.Render.Scene3D.Scene3DBores.Of(problem,
            n => g.Origins.TryGetValue(n, out var o) && o.Kind == Em3dObjectKind.Via)!;
        var barrel = problem.Solids.Single(s => s.Name == "via/1");
        double z0 = ((Em3dExtrudedPolygon)barrel.Primitive).ZBottom, z1 = ((Em3dExtrudedPolygon)barrel.Primitive).ZTop;

        // Every conductor and dielectric the barrel passes right through: a GND plane on each inner layer, the slabs between.
        var through = problem.Solids.Where(s => s.Role is Em3dRole.Conductor or Em3dRole.Dielectric && s.Name != "via/1"
                                             && s.Primitive is Em3dExtrudedPolygon e && e.ZBottom >= z0 - 1e-12 && e.ZTop <= z1 + 1e-12
                                             && e.Outline.Max(q => q.Y) >= 3e-3 - 1e-12).ToList();
        Assert.Contains(through, s => s.Role == Em3dRole.Conductor);
        Assert.Contains(through, s => s.Role == Em3dRole.Dielectric);
        foreach (var s in through)
        {
            var mesh = bores.Carved(s)?.Make() ?? Em3dTessellation.Of(s);
            // No triangle of it reaches the via's axis at the board edge (x = 2 mm, y = 3 mm), where the flat face is.
            Assert.DoesNotContain(mesh.Triangles, t =>
                new[] { t.A, t.B, t.C }.All(v => Math.Abs(mesh.Vertices[v].Y - 3e-3) < 1e-12)
                && new[] { t.A, t.B, t.C }.Min(v => mesh.Vertices[v].X) < 2e-3 && new[] { t.A, t.B, t.C }.Max(v => mesh.Vertices[v].X) > 2e-3);
        }
    }

    private static string Gmsh() => SolverDiscovery.Gmsh.Check(SolverDiscovery.CapabilitiesFor(SolverTool.Gmsh)).Installation!.Path;
}
