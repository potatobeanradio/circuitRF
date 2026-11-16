using System.Text.Json.Nodes;
using CircuitRF.Design.Interchange.Hzn;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Ui.Tests.Hzn;

/// <summary>
/// brief-hzn-15 — Hertzon geometry in tilted planes, and what an exported antenna model needs beside it. The fixture
/// (<c>tilted-shapes.hzn</c>) was written for these tests by a script, from a frame turned 10° about z, 4° about y and 3° about x and
/// moved to (1, 2, 3) mm: a copper board in that frame swept along a vector off its normal, a trace sheet in a parallel plane, and a
/// lumped port joining the trace's edge to the board's side face as a trapezoid whose parallel sides differ by 0.75 %. Beside them,
/// faceted cylinders on Z and Y, a rectangle swept off its normal, a Unite whose tools the file does not all record as consumed, and a
/// steel Finite Conductivity boundary on the copper board, in a Terminal design.
/// </summary>
public sealed class HznTiltedGeometryTests : IDisposable
{
    private const double Nm = 1e-9;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf_hzn15_" + Guid.NewGuid().ToString("N")[..8]);

    public HznTiltedGeometryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string Fixture => Path.Combine(FixturePaths.Require("tests/Ui.Tests/Hzn/Fixtures"), "tilted-shapes.hzn");

    private static C3dTranslation Translate(string path)
    {
        var read = HznFile.Read(path);
        Assert.True(read.Ok, read.Refusal?.Render());
        return HznToC3d.Translate(read.File!.Project, "Tilted");
    }

    private static C3dDocument Doc()
    {
        var t = Translate(Fixture);
        Assert.True(t.Ok, t.Refusal?.Render());
        return t.Document!;
    }

    /// <summary>The fixture's frame (the generator's), in millimetres.</summary>
    private static double[] World(double u, double v, double w)
    {
        static double[,] Rot(char axis, double deg)
        {
            double c = Math.Cos(deg * Math.PI / 180), s = Math.Sin(deg * Math.PI / 180);
            return axis switch
            {
                'x' => new double[,] { { 1, 0, 0 }, { 0, c, -s }, { 0, s, c } },
                'y' => new double[,] { { c, 0, s }, { 0, 1, 0 }, { -s, 0, c } },
                _ => new double[,] { { c, -s, 0 }, { s, c, 0 }, { 0, 0, 1 } },
            };
        }
        static double[,] Mul(double[,] a, double[,] b)
        {
            var r = new double[3, 3];
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) for (int k = 0; k < 3; k++) r[i, j] += a[i, k] * b[k, j];
            return r;
        }
        var m = Mul(Rot('z', 10), Mul(Rot('y', 4), Rot('x', 3)));
        double[] o = [1, 2, 3];
        return [.. Enumerable.Range(0, 3).Select(i => o[i] + m[i, 0] * u + m[i, 1] * v + m[i, 2] * w)];
    }

    /// <summary>Where a point of a shape's own (u, v, h) on its plane lands, in millimetres.</summary>
    private static double[] Placed(C3dObject o, C3dPlane plane, double u, double v, double h)
    {
        var q = C3dLowering.OnPlane(plane, u, v, h);
        var (x, y, z) = o.Placement.ToTransform().Apply(new C3dPoint3((long)Math.Round(q.X), (long)Math.Round(q.Y), (long)Math.Round(q.Z)));
        return [x * 1e-6, y * 1e-6, z * 1e-6];
    }

    private static void Near(double[] expected, double[] got, double tolMm = 3e-6)
    {
        for (int i = 0; i < 3; i++) Assert.True(Math.Abs(expected[i] - got[i]) <= tolMm, $"[{i}] expected {expected[i]}, got {got[i]}");
    }

    [Fact]
    public void ATiltedPolygon_IsASheetOnTheNearestAxisPlane_WhosePlacementPutsEveryPointWhereItWasDrawn()
    {
        var trace = Assert.IsType<C3dSheet>(Doc().Objects.Single(o => o.Name == "Trace"));
        Assert.Equal(C3dPlane.XY, trace.Plane);
        Assert.NotEmpty(trace.Placement.Rotate);
        (double U, double V)[] local = [(8, 11), (12, 11), (12, 14), (8, 14)];
        for (int k = 0; k < 4; k++)
            Near(World(local[k].U, local[k].V, 0.5), Placed(trace, trace.Plane, trace.Outline[k].U, trace.Outline[k].V, trace.Offset));
    }

    [Fact]
    public void ATiltedPolygonSweptOffItsNormal_IsAShearedPrismInTheSameFrame()
    {
        var board = Assert.IsType<C3dPrism>(Doc().Objects.Single(o => o.Name == "Board"));
        (double U, double V)[] local = [(0, 0), (20, 0), (20, 10), (0, 10)];
        for (int k = 0; k < 4; k++)
        {
            var p = board.Outline[k];
            Near(World(local[k].U, local[k].V, 0), Placed(board, board.Plane, p.U, p.V, board.Offset));
            // the top: the outline moved by the shear, at the height
            Near(World(local[k].U + 0.3, local[k].V, -1.0), Placed(board, board.Plane, p.U + board.Shear.U, p.V + board.Shear.V, board.Offset + board.Height));
        }
    }

    [Fact]
    public void ARectangleSweptOffItsNormal_IsAPrismWithThatShear()
    {
        var slant = Assert.IsType<C3dPrism>(Doc().Objects.Single(o => o.Name == "Slant"));
        Assert.Equal((C3dPlane.XY, 2_000_000L, new C3dPoint2(1_000_000, 0)), (slant.Plane, slant.Height, slant.Shear));
        Assert.Equal(new C3dPoint2(28_000_000, 10_000_000), slant.Outline[0]);
    }

    [Fact]
    public void AFacetedCylinder_IsAPrism_ItsFirstVertexAlongTheNextAxisRound()
    {
        var doc = Doc();
        var pin = Assert.IsType<C3dPrism>(doc.Objects.Single(o => o.Name == "Pin8"));
        Assert.Equal(8, pin.Outline.Count);
        Assert.Equal(new C3dPoint2(30_500_000, 0), pin.Outline[0]);                     // Z: the first vertex along +x
        var post = Assert.IsType<C3dPrism>(doc.Objects.Single(o => o.Name == "Post7"));
        Assert.Equal((C3dPlane.XZ, 7), (post.Plane, post.Outline.Count));
        Assert.Equal(new C3dPoint2(30_000_000, 500_000), post.Outline[0]);              // Y: along +z (XZ's v)
        Assert.All(post.Outline, q => Assert.InRange(Math.Sqrt(Math.Pow(q.U - 30e6, 2) + Math.Pow(q.V, 2)), 500_000 - 1, 500_000 + 1));
    }

    [Fact]
    public void AUnitesToolTheFileDoesNotRecordAsConsumed_IsDrawnInTheUniteOnly()
    {
        var doc = Doc();
        var ant = Assert.IsType<C3dBoolean>(doc.Objects.Single(o => o.Name == "Ant"));
        Assert.Equal(2, ant.Tools.Count);
        Assert.DoesNotContain(doc.Objects, o => o.Name is "AntA" or "AntB");
    }

    [Fact]
    public void AFiniteConductivityBoundaryOnACopperSolid_MakesItThatMetal()
    {
        var t = Translate(Fixture);
        var board = t.Document!.Objects.Single(o => o.Name == "Board");
        var steel = t.Technology!.Materials.Single(m => m.Name == board.Material);
        Assert.Equal(1.1e6, steel.Sigma20);
    }

    [Fact]
    public void ANearRectangularTiltedPort_IsItsCorners_AndRunsFromTheBoardsSideToTheTrace()
    {
        var doc = Doc();
        var port = doc.Ports.Single();
        Assert.Equal(4, port.Corners!.Count);
        Near(World(12.03, 10, -0.2), [port.Corners[2].X * 1e-6, port.Corners[2].Y * 1e-6, port.Corners[2].Z * 1e-6], 1e-6);

        var (plan, refusal) = HznEmergeRun.Plan(Fixture, "Tilted", null);
        Assert.True(plan is not null, refusal?.Render());
        var made = C3dProblemAssembly.Assemble(plan.Setup, plan.Document, plan.DocumentPath, null, HznEmergeRun.Elaborator(plan));
        Assert.True(made.Ok, made.Refusal);
        var p = made.Problem!.Ports.Single();
        Assert.Equal(("Board", "Trace"), (p.NegativeObject, p.PositiveObject));
        Assert.NotNull(p.Quad);
        Assert.Empty(made.Problem.Validate());
    }

    [Fact]
    public void ALumpedPortInATerminalDesign_IsAModalPort_UnlessWavePortsAreBesideIt()
    {
        Assert.Equal("50", Doc().Ports.Single().Z0);

        var json = JsonNode.Parse(File.ReadAllText(Fixture))!.AsObject();
        var design = json["designs"]![0]!.AsObject();
        design["excitations"]!.AsArray().Add(JsonNode.Parse("""
            {"name": "W", "kind": "Wave Port", "props": {"PortNumber": "2", "NumberOfModes": "1", "Renormalize": "True", "RenormImpedance": "50ohm"},
             "faces": [], "objects": ["Trace"], "edges": [], "enabled": true}
            """));
        string path = Path.Combine(_root, "mixed.hzn");
        File.WriteAllText(path, json.ToJsonString());
        Assert.Contains(Translate(path).Refusals, r => r.What == "Lumped Port beside wave ports in a Terminal design");
    }
}
