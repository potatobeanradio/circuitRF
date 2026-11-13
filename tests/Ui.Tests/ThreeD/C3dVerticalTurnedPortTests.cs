// brief-hzn-14 R-hzn14-1 — a lumped port turned in an upright plane: an edge port standing between a ground sheet and the end of a
// trace sheet whose end edge is turned 30° from the x axis. The trace's corners are ROUNDED to the 1 nm grid, as a layout's are,
// so the port's top edge is near, not on, the trace's side, and the seam is what makes them meet exactly.

using CircuitRF.Core.Design;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class C3dVerticalTurnedPortTests : IDisposable
{
    private const long Um = 1000;          // DBU per µm
    private const double Turn = 30;
    private const long H = 508 * Um;       // the substrate's height: the trace sheet is on it, the ground sheet under it
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-vturned-" + Guid.NewGuid().ToString("N")[..10]);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private static readonly double C = Math.Cos(Turn * Math.PI / 180), S = Math.Sin(Turn * Math.PI / 180);

    /// <summary>A point given along the end edge (e) and along the trace (n, away from the end) about (1500, 1500) µm, in DBU rounded
    /// to the grid.</summary>
    private static C3dPoint2 At(double e, double n)
        => new((long)Math.Round((1500 + e * C - n * S) * Um), (long)Math.Round((1500 + e * S + n * C) * Um));

    /// <summary>The board and its port, saved in a workspace.</summary>
    private (C3dDocument Doc, string Path, string Cws) Board(C3dPort port)
    {
        string ws = System.IO.Path.Combine(_root, "ws");
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(System.IO.Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials = [new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 }, new TechMaterial { Name = "Fill", Epsr = 3.5 }],
        });
        WorkspacePersistence.SaveToFile(System.IO.Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        var doc = new C3dDocument
        {
            Objects =
            [
                new C3dBox { Name = "sub", Material = "Fill", Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(3000 * Um, 3000 * Um, H) },
                new C3dSheet { Name = "ground", Material = "Copper", Plane = C3dPlane.XY, Offset = 0,
                               Rect = new C3dRect { Size = new C3dPoint2(3000 * Um, 3000 * Um) } },
                // The trace: its end edge 800 µm long, turned 30°, centred on (1500, 1500) µm; it runs 1000 µm from it.
                new C3dSheet { Name = "trace", Material = "Copper", Plane = C3dPlane.XY, Offset = H,
                               Outline = [At(-400, 0), At(400, 0), At(400, 1000), At(-400, 1000)] },
            ],
            Ports = [port],
        };
        string dir = System.IO.Path.Combine(ws, "Cell", "3d");
        Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, "Cell.c3d");
        C3dPersistence.SaveToFile(path, doc);
        return (doc, path, System.IO.Path.Combine(ws, ".cws"));
    }

    /// <summary>The port standing under the trace's end edge, in the XZ plane turned 30°: its rectangle before the turn is centred on
    /// the edge's centre, <paramref name="width"/> µm along the edge and the substrate's height up.</summary>
    private static C3dPort EdgePort(double width = 800, Em3dPortKind kind = Em3dPortKind.Lumped)
        => new()
        {
            Number = 1, Name = "P1", Kind = kind, Plane = C3dPlane.XZ, Offset = 1500 * Um, Angle = Turn,
            Rect = new C3dRect { Min = new C3dPoint2((long)Math.Round((1500 - width / 2) * Um), 0), Size = new C3dPoint2((long)Math.Round(width * Um), H) },
        };

    private static EmSetup Setup() => new()
    {
        Name = "S1", Solver3D = Em3dSolverId.Palace,
        Frequency = new FrequencySpec("1", "5", 3, SweepKind.Linear, "GHz", "GHz"),
    };

    [Fact]
    public void AVerticalTurnedPort_LowersToTheQuadItsAngleTurns()
    {
        var (doc, path, cws) = Board(EdgePort());
        var g = C3dProblemAssembly.Assemble(Setup(), doc, path, cws);
        Assert.True(g.Problem is not null, g.Refusal);
        var quad = Assert.IsType<Em3dPortQuad>(Assert.Single(g.Problem!.Ports).Quad);
        Assert.Null(quad.Problem());
        Assert.Equal((0.0, 508e-6), (quad.Corners.Min(c => c.Z), quad.Corners.Max(c => c.Z)));
        var (u, _) = quad.Sides;
        double len = Math.Sqrt(u.X * u.X + u.Y * u.Y + u.Z * u.Z);
        Assert.Equal(1, Math.Abs(u.X * C + u.Y * S) / len, 9);        // its horizontal side runs along the turned edge
    }

    [Fact]
    public void FromGroundSheetToTraceSheet_TheGroundIsNegative_AndTheTracePositive()
    {
        var (doc, path, cws) = Board(EdgePort());
        var port = Assert.Single(C3dProblemAssembly.Assemble(Setup(), doc, path, cws).Problem!.Ports);
        Assert.Equal(("ground", "trace"), (port.NegativeObject, port.PositiveObject));
        Assert.Equal(1, port.Direction.Z, 9);
    }

    [Fact]
    public void AnEndAlongASheetsSide_SeamsThatSheetsOutline()
    {
        var (doc, path, cws) = Board(EdgePort());
        var problem = C3dProblemAssembly.Assemble(Setup(), doc, path, cws).Problem!;
        var port = Assert.Single(problem.Ports);
        var (_, (a, b)) = port.Quad!.Ends(port.Direction);
        var outline = problem.Sheets.Single(s => s.Name == "trace").Outline;
        Assert.Contains(new Point2(a.X, a.Y), outline);
        Assert.Contains(new Point2(b.X, b.Y), outline);
    }

    [Fact]
    public void AnEndOffItsSurface_IsRefused()
    {
        var (doc, path, cws) = Board(EdgePort(width: 1200));        // wider than the trace's 800 µm end
        var ctx = C3dProblemAssembly.PortContext(Setup(), doc, new C3dElaborator().Elaborate(doc, path, cws), null);
        var r = C3dPorts.Resolve(doc.Ports[0], doc.DbuPerMicron, ctx);
        Assert.Null(r.Resolved);
        Assert.Contains("leaves 'trace'", r.Refusal);
    }

    [Fact]
    public void OpenEms_RefusesIt()
    {
        var (doc, path, cws) = Board(EdgePort());
        var g = C3dProblemAssembly.Assemble(Setup(), doc, path, cws);
        var grid = FdtdGrid.Build(g.Problem!, OpenEmsGridSettings.Default, long.MaxValue);
        Assert.Contains("turned in its plane", CsxcadWriter.Write(g.Problem!, grid, OpenEmsGridSettings.Default, OpenEmsRunSettings.Default).Refusal);
    }

    [Fact]
    public void AWavePortWithAnAngleInAVerticalPlane_IsStillRefused()
    {
        var (doc, path, cws) = Board(EdgePort(kind: Em3dPortKind.Wave));
        var ctx = C3dProblemAssembly.PortContext(Setup(), doc, new C3dElaborator().Elaborate(doc, path, cws), null);
        Assert.Contains("is a wave port with an Angle", C3dPorts.Resolve(doc.Ports[0], doc.DbuPerMicron, ctx).Refusal);
    }
}
