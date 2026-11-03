// ================================================================
//  SnapTrackingTests.cs — snap tracking's rules, with no window: a point snap held for the dwell becomes a reference and a
//  second dwell lets it go; an unused reference is forgotten; the cursor near a reference's axis line locks onto it — an
//  edge snap's own coordinate along the line, else the line's point nearest the ray on the grid; two references' crossing
//  lines give their crossing; and a raw point snap always wins.
// ================================================================

using System.Numerics;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class SnapTrackingTests
{
    private const double Mm = 1e-3;
    private const float W = 400, H = 300;

    private static readonly Scene3DModel Scene = Scene3DModel.Empty();
    private static readonly Camera3D Cam = Camera3D.Fit(new Vector3(-1e-3f, -1e-3f, 0), new Vector3(1e-3f, 1e-3f, 0.5e-3f), W / H);

    private static Snap3DResult Point(Snap3DKind kind, Point3 p) => new(kind, p, 7, 0, 0, 0, 0, 0, 0);

    private static Snap3DTrackView ViewAt(Point3 aim, Snap3DGrid grid = default)
    {
        var (x, y, _) = Cam.Project(Scene.ToLocal(aim.X, aim.Y, aim.Z), W, H);
        var (o, d) = Cam.Ray(x - 0.5f, y - 0.5f, W, H);                  // Ray adds half a pixel, as the viewer's CursorRay takes back
        return new Snap3DTrackView(Scene, Cam, W, H, x, y, new Point3(o.X, o.Y, o.Z), new Point3(d.X, d.Y, d.Z), 8, grid);
    }

    private static SnapTracker3D Acquired(params Point3[] refs)
    {
        var t = new SnapTracker3D();
        long now = 0;
        foreach (var r in refs)
        {
            t.Observe(Point(Snap3DKind.Vertex, r), now);
            Assert.True(t.Tick(now + SnapReferences<Snap3DReference>.DwellMs));
            now += SnapReferences<Snap3DReference>.DwellMs + 1;
        }
        t.Observe(default, now);
        return t;
    }

    [Fact]
    public void AHeldPointIsAcquired_ASecondDwellLetsItGo_AndAnUnusedOneIsForgotten()
    {
        var a = new Point3(0.5 * Mm, 0, 0.5 * Mm);
        var t = new SnapTracker3D();
        t.Observe(Point(Snap3DKind.Vertex, a), 0);
        Assert.False(t.Tick(SnapReferences<Snap3DReference>.DwellMs - 1));                  // passing over is not acquiring
        Assert.True(t.Tick(SnapReferences<Snap3DReference>.DwellMs));
        Assert.Equal(a, Assert.Single(t.References).Point.World);
        t.Observe(Point(Snap3DKind.Vertex, a), 600);                       // still there: no second toggle
        Assert.False(t.Tick(2000));
        t.Observe(default, 2100);
        t.Observe(Point(Snap3DKind.Vertex, a), 2200);
        Assert.True(t.Tick(2200 + SnapReferences<Snap3DReference>.DwellMs));
        Assert.Empty(t.References);

        t = Acquired(a);
        Assert.False(t.Tick(SnapReferences<Snap3DReference>.ForgetMs));
        Assert.True(t.Tick(SnapReferences<Snap3DReference>.DwellMs + SnapReferences<Snap3DReference>.ForgetMs + 1));
        Assert.Empty(t.References);
    }

    [Fact]
    public void DownFromACorner_ItStopsOnAnEdge_OrOnTheGrid_AndAPointSnapWins()
    {
        var corner = new Point3(0.5 * Mm, 0, 0.5 * Mm);
        var t = Acquired(corner);
        var below = new Point3(0.5 * Mm, 0, 0.1 * Mm);

        // Near an edge: the edge snap's own z, the corner's x and y — exactly.
        var edge = Point(Snap3DKind.Edge, new Point3(0.3 * Mm, 0.2 * Mm, 0));
        var s = t.Apply(edge, ViewAt(below), 1000);
        Assert.Equal(Snap3DKind.Track, s.Kind);
        Assert.Equal(new Point3(corner.X, corner.Y, 0), s.World);
        Assert.Equal(new Snap3DTrackLine(0, 2), t.LineA);

        // Nothing under the cursor: the line's point nearest the ray, its step from the corner on a 10 µm grid.
        var grid = new Snap3DGrid(Design.ThreeD.C3dPlane.XY, 0, 10_000, 1000);
        s = t.Apply(default, ViewAt(below, grid), 1000);
        Assert.Equal(Snap3DKind.Track, s.Kind);
        Assert.Equal((corner.X, corner.Y), (s.World.X, s.World.Y));
        Assert.Equal(0.1 * Mm, s.World.Z, 12);

        var vertex = Point(Snap3DKind.Vertex, new Point3(0.4 * Mm, 0, 0.1 * Mm));
        Assert.Equal(vertex, t.Apply(vertex, ViewAt(below), 1000));
        Assert.Null(t.LineA);
    }

    [Fact]
    public void TwoReferences_GiveTheCrossingOfTheirLines()
    {
        var a = new Point3(0.5 * Mm, -0.5 * Mm, 0);
        var b = new Point3(-0.5 * Mm, 0.5 * Mm, 0);
        var t = Acquired(a, b);
        var s = t.Apply(default, ViewAt(new Point3(0.5 * Mm, 0.5 * Mm, 0)), 2000);
        Assert.Equal(Snap3DKind.Track, s.Kind);
        Assert.Equal(new Point3(0.5 * Mm, 0.5 * Mm, 0), s.World);
        Assert.NotNull(t.LineB);
    }
}
