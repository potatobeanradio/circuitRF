// A lumped port turned in the XY plane (C3dTurnedPorts): Make Port's rectangle fit, the turned resolution, the copper
// taking the port's corners, and the refusals that say why a shape is not a port.
//
// The board: two copper pads 800 × 1000 µm, turned 30°, with a 400 µm gap between them along the turn. Their corners are
// ROUNDED to the 1 nm grid, as a layout's copper is, so no line through them is the port's exact side.

using CircuitRF.Core.Design;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class TurnedPortTests : IDisposable
{
    private const long Um = 1000;          // DBU per µm
    private const double Turn = 30;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-turned-" + Guid.NewGuid().ToString("N")[..10]);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private static readonly double C = Math.Cos(Turn * Math.PI / 180), S = Math.Sin(Turn * Math.PI / 180);

    /// <summary>A rectangle of half-sides (a along the turn, b across it) about (cx, cy) µm, its corners in DBU rounded to the grid.</summary>
    private static List<C3dPoint2> Turned(double cx, double cy, double a, double b) => Turned(cx, cy, (-a, -b), (a, -b), (a, b), (-a, b));

    /// <summary>Points given along the turn and across it about (cx, cy) µm, in DBU rounded to the grid.</summary>
    private static List<C3dPoint2> Turned(double cx, double cy, params (double Along, double Across)[] local)
        => [.. local.Select(q => new C3dPoint2((long)Math.Round((cx + q.Along * C - q.Across * S) * Um), (long)Math.Round((cy + q.Along * S + q.Across * C) * Um)))];

    private static (double X, double Y) Along(double cx, double cy, double d) => (cx + d * C, cy + d * S);

    /// <summary>The board, its port (or not), saved in a workspace; the gap's centre is at (1500, 1500) µm.</summary>
    private (C3dDocument Doc, string Path, string Cws) Board(Action<C3dDocument>? more = null, bool roundEnd = false)
    {
        string ws = System.IO.Path.Combine(_root, "ws");
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(System.IO.Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials = [new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 }, new TechMaterial { Name = "Fill", Epsr = 3.5 }, new TechMaterial { Name = "Air", Epsr = 1 }],
        });
        WorkspacePersistence.SaveToFile(System.IO.Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        var (ax, ay) = Along(1500, 1500, -600);
        var (bx, by) = Along(1500, 1500, 600);
        // roundEnd: pad A's facing end bowed toward the gap, as a round cap is: its tip where its side was, its corners 30 µm back.
        var padA = roundEnd ? Turned(ax, ay, (-400, -500), (370, -500), (400, 0), (370, 500), (-400, 500)) : Turned(ax, ay, 400, 500);
        var doc = new C3dDocument
        {
            Objects =
            [
                new C3dBox { Name = "sub", Material = "Fill", Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(3000 * Um, 3000 * Um, 508 * Um) },
                new C3dPrism { Name = "padA", Material = "Copper", Plane = C3dPlane.XY, Offset = 508 * Um, Height = 35 * Um, Outline = padA },
                new C3dPrism { Name = "padB", Material = "Copper", Plane = C3dPlane.XY, Offset = 508 * Um, Height = 35 * Um, Outline = Turned(bx, by, 400, 500) },
            ],
        };
        more?.Invoke(doc);
        string dir = System.IO.Path.Combine(ws, "Cell", "3d");
        Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, "Cell.c3d");
        C3dPersistence.SaveToFile(path, doc);
        return (doc, path, System.IO.Path.Combine(ws, ".cws"));
    }

    /// <summary>The port across the gap, 400 µm along the turn and 800 µm across it, as Make Port writes it.</summary>
    private static C3dPort GapPort()
        => new()
        {
            Number = 1, Name = "P1", Kind = Em3dPortKind.Lumped, Plane = C3dPlane.XY, Offset = 525500, Angle = Turn,
            Rect = new C3dRect { Min = new C3dPoint2(1300 * Um, 1100 * Um), Size = new C3dPoint2(400 * Um, 800 * Um) },
        };

    private static EmSetup Setup(Em3dSolverId solver = Em3dSolverId.Palace) => new()
    {
        Name = "S1", Solver3D = solver,
        Frequency = new FrequencySpec("1", "5", 3, SweepKind.Linear, "GHz", "GHz"),
        AirBox = new EmAirBox(ZMin: new EmAirBoxFace(0, Em3dBoundaryKind.Pec)),
    };

    /// <summary>Make Port's fit: a polygon that is a rectangle at 30°, its corners rounded to the grid, is a port turned 30° of
    /// that size about that centre.</summary>
    [Fact]
    public void ARectangleAtAnAngle_FitsAsATurnedPort()
    {
        var ring = Turned(1500, 1500, 200, 400).Select(p => new Point2(p.U * 1e-9, p.V * 1e-9)).ToList();
        var fit = C3dTurnedPorts.Fit(ring, 1000, 2e-9, out string? why);
        Assert.True(fit is not null, why);
        Assert.Equal(Turn, fit!.Value.Angle, 4);
        Assert.InRange(fit.Value.Rect.Size.U, 400 * Um - 2, 400 * Um + 2);
        Assert.InRange(fit.Value.Rect.Size.V, 800 * Um - 2, 800 * Um + 2);
        Assert.InRange(fit.Value.Rect.Min.U + fit.Value.Rect.Size.U / 2, 1500 * Um - 2, 1500 * Um + 2);
    }

    /// <summary>A shape whose width changes along it, or whose ends are offset, is not a port, and the refusal says which.</summary>
    [Fact]
    public void ATrapezoidAndAParallelogram_AreRefusedSayingWhy()
    {
        static List<Point2> Mm(params (double X, double Y)[] p) => [.. p.Select(q => new Point2(q.X * 1e-3, q.Y * 1e-3))];
        Assert.Null(C3dTurnedPorts.Fit(Mm((0, 0), (1, 0), (0.9, 0.5), (0.1, 0.5)), 1000, 2e-9, out string? trapezoid));
        Assert.Contains("width runs from", trapezoid);
        Assert.Null(C3dTurnedPorts.Fit(Mm((0, 0), (1, 0), (1.2, 0.5), (0.2, 0.5)), 1000, 2e-9, out string? parallelogram));
        Assert.Contains("corners are", parallelogram);
        Assert.Null(C3dTurnedPorts.Fit(Mm((0, 0), (1, 0), (1, 0.5), (0.5, 0.7), (0, 0.5)), 1000, 2e-9, out string? five));
        Assert.Contains("5 corners", five);
    }

    /// <summary>The turned port resolves across the gap, is an exact rectangle along the turn, and in the solved problem each pad's
    /// outline has the port's end corners as two of its own points — the same numbers, so the mesher joins them.</summary>
    [Fact]
    public void ATurnedPort_ResolvesAndItsCopperTakesItsCorners()
    {
        var (doc, path, cws) = Board(d => d.Ports.Add(GapPort()));
        var g = C3dProblemAssembly.Assemble(Setup(), doc, path, cws);
        Assert.True(g.Problem is not null, g.Refusal);
        var port = Assert.Single(g.Problem!.Ports);
        var quad = Assert.IsType<Em3dPortQuad>(port.Quad);
        Assert.Null(quad.Problem());
        Assert.Empty(g.Problem.Validate());
        Assert.Equal(1, Math.Abs(port.Direction.X * C + port.Direction.Y * S), 9);
        Assert.Equal(new HashSet<string> { "padA", "padB" }, new HashSet<string> { port.PositiveObject, port.NegativeObject });
        var (neg, pos) = quad.Ends(port.Direction);
        foreach (var (name, (p, q)) in new[] { (port.NegativeObject, neg), (port.PositiveObject, pos) })
        {
            var outline = ((Em3dExtrudedPolygon)g.Problem.Solids.Single(s => s.Name == name).Primitive).Outline;
            Assert.Contains(new Point2(p.X, p.Y), outline);
            Assert.Contains(new Point2(q.X, q.Y), outline);
        }
    }

    /// <summary>An end that meets its copper on a curve, not along a straight side, is refused naming the copper.</summary>
    [Fact]
    public void AnEndOnACurve_IsRefused()
    {
        var (doc, path, cws) = Board(d => d.Ports.Add(GapPort()), roundEnd: true);
        var ctx = C3dProblemAssembly.PortContext(Setup(), doc, new C3dElaborator().Elaborate(doc, path, cws), null);
        var r = C3dPorts.Resolve(doc.Ports[0], doc.DbuPerMicron, ctx);
        Assert.Null(r.Resolved);
        Assert.Contains("does not lie along one straight side of 'padA'", r.Refusal);
    }

    /// <summary>openEMS's grid runs along the axes, so it refuses a turned port by name.</summary>
    [Fact]
    public void OpenEms_RefusesATurnedPort()
    {
        var (doc, path, cws) = Board(d => d.Ports.Add(GapPort()));
        var g = C3dProblemAssembly.Assemble(Setup(), doc, path, cws);
        Assert.True(g.Problem is not null, g.Refusal);
        var grid = FdtdGrid.Build(g.Problem!, OpenEmsGridSettings.Default, long.MaxValue);
        var written = CsxcadWriter.Write(g.Problem!, grid, OpenEmsGridSettings.Default, OpenEmsRunSettings.Default);
        Assert.Contains("turned in its plane", written.Refusal);
    }
}
