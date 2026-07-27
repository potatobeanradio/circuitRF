using Clipper2Lib;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

// ── Phase L1e: LayoutClipper — the single ToClipperPaths/FromClipperTree conversion point (§6.1).

public class LayoutClipperTests
{
    private static readonly LayerKey Layer1 = new(1, 0);

    [Fact]
    public void ToClipperPaths_Rect_OneRing_ExactDbuCoordinates()
    {
        var r = new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 5000, Y2 = 3000 };
        var paths = LayoutClipper.ToClipperPaths(r, 1000);
        var path = Assert.Single(paths);
        Assert.Equal(4, path.Count);
        Assert.Equal(new Point64(0, 0), path[0]);
        Assert.Equal(new Point64(5000, 0), path[1]);
    }

    [Fact]
    public void ToClipperPaths_PolygonWithHole_TwoRings()
    {
        var p = new PolygonShape
        {
            Layer = Layer1,
            Xy = [0, 0, 100_000, 0, 100_000, 100_000, 0, 100_000],
            Holes = [[30_000, 30_000, 30_000, 70_000, 70_000, 70_000, 70_000, 30_000]],
        };
        var paths = LayoutClipper.ToClipperPaths(p, 1000);
        Assert.Equal(2, paths.Count);
    }

    [Fact]
    public void ToClipperPaths_Path_ProducesClosedOutlineNotTheOpenCenterline()
    {
        var path = new PathShape { Layer = Layer1, Xy = [0, 0, 10_000, 0], Width = 2000, End = PathEndStyle.Flush };
        var paths = LayoutClipper.ToClipperPaths(path, 1000);
        var outline = Assert.Single(paths);
        // A flush-capped 2000-wide, 10000-long straight trace outlines to a 10000x2000 rect.
        var bounds = Clipper.GetBounds(outline);
        Assert.Equal(0, bounds.left);
        Assert.Equal(10_000, bounds.right);
        Assert.Equal(-1000, bounds.top);
        Assert.Equal(1000, bounds.bottom);
    }

    [Fact]
    public void ToClipperPaths_ExtendedAndSquare_BothExtendByHalfWidth()
    {
        var square = new PathShape { Layer = Layer1, Xy = [0, 0, 10_000, 0], Width = 2000, End = PathEndStyle.Square };
        var extended = new PathShape { Layer = Layer1, Xy = [0, 0, 10_000, 0], Width = 2000, End = PathEndStyle.Extended };

        var squareBounds = Clipper.GetBounds(LayoutClipper.ToClipperPaths(square, 1000)[0]);
        var extendedBounds = Clipper.GetBounds(LayoutClipper.ToClipperPaths(extended, 1000)[0]);

        Assert.Equal(-1000, squareBounds.left);
        Assert.Equal(11_000, squareBounds.right);
        Assert.Equal(squareBounds, extendedBounds);
    }

    [Fact]
    public void FromClipperTree_SimplePolygon_OneShapeNoHoles()
    {
        var paths = new Paths64 { new([new(0, 0), new(100, 0), new(100, 100), new(0, 100)]) };
        var tree = new PolyTree64();
        Clipper.BooleanOp(ClipType.Union, paths, new Paths64(), tree, FillRule.NonZero);

        var shapes = LayoutClipper.FromClipperTree(tree, Layer1, "netA");
        var poly = Assert.IsType<PolygonShape>(Assert.Single(shapes));
        Assert.Equal(Layer1, poly.Layer);
        Assert.Equal("netA", poly.Net);
        Assert.Null(poly.Holes);
    }

    [Fact]
    public void FromClipperTree_RectMinusCircle_OneShapeOneHole()
    {
        var rect = new PolygonShape { Layer = Layer1, Xy = [0, 0, 100_000, 0, 100_000, 100_000, 0, 100_000] };
        var circle = new CircleShape { Layer = Layer1, Cx = 50_000, Cy = 50_000, R = 20_000 };

        var subject = LayoutClipper.ToClipperPaths(rect, 1000);
        var clip = LayoutClipper.ToClipperPaths(circle, 1000);
        var tree = new PolyTree64();
        Clipper.BooleanOp(ClipType.Difference, subject, clip, tree, FillRule.NonZero);

        var shapes = LayoutClipper.FromClipperTree(tree, Layer1, null);
        var poly = Assert.IsType<PolygonShape>(Assert.Single(shapes));
        Assert.NotNull(poly.Holes);
        Assert.Single(poly.Holes!);
    }

    [Fact]
    public void FromClipperTree_DisjointUnion_TwoShapes()
    {
        var subject = new Paths64
        {
            new([new(0, 0), new(100, 0), new(100, 100), new(0, 100)]),
            new([new(200, 0), new(300, 0), new(300, 100), new(200, 100)]),
        };
        var tree = new PolyTree64();
        Clipper.BooleanOp(ClipType.Union, subject, new Paths64(), tree, FillRule.NonZero);

        var shapes = LayoutClipper.FromClipperTree(tree, Layer1, null);
        Assert.Equal(2, shapes.Count);
    }

    // ── R-L1e-0 / §3.1a R10b ──────────────────────────────────────────────────────

    [Fact]
    public void EnsureValidHoles_AlreadyValid_ReturnsSameInstanceUnchanged()
    {
        var p = new PolygonShape
        {
            Layer = Layer1,
            Xy = [0, 0, 100_000, 0, 100_000, 100_000, 0, 100_000],
            Holes = [[30_000, 30_000, 30_000, 70_000, 70_000, 70_000, 70_000, 30_000]],
        };
        var result = LayoutClipper.EnsureValidHoles(p);
        Assert.Same(p, Assert.Single(result));
    }

    [Fact]
    public void EnsureValidHoles_NoHoles_ReturnsSameInstance()
    {
        var p = new PolygonShape { Layer = Layer1, Xy = [0, 0, 1000, 0, 1000, 1000] };
        Assert.Same(p, Assert.Single(LayoutClipper.EnsureValidHoles(p)));
    }

    [Fact]
    public void EnsureValidHoles_HoleEscapesOuterRing_RepairedViaUnion()
    {
        // A "hole" that pokes entirely outside the outer ring — invalid per R10b.
        var p = new PolygonShape
        {
            Layer = Layer1,
            Xy = [0, 0, 100_000, 0, 100_000, 100_000, 0, 100_000],
            Holes = [[150_000, 30_000, 150_000, 70_000, 250_000, 70_000, 250_000, 30_000]],
        };
        var result = LayoutClipper.EnsureValidHoles(p);
        // Repaired via Union(outer, "hole") with NonZero fill — the escaping hole just becomes
        // extra solid area (both rings wound the same way after a raw union with no true
        // containment), so nothing throws and the result is well-formed geometry.
        Assert.NotEmpty(result);
        foreach (var s in result) Assert.IsType<PolygonShape>(s);
    }

    [Fact]
    public void EnsureValidHoles_OverlappingHoles_Repaired()
    {
        var p = new PolygonShape
        {
            Layer = Layer1,
            Xy = [0, 0, 100_000, 0, 100_000, 100_000, 0, 100_000],
            Holes =
            [
                [10_000, 10_000, 10_000, 50_000, 50_000, 50_000, 50_000, 10_000],
                [30_000, 30_000, 30_000, 70_000, 70_000, 70_000, 70_000, 30_000],
            ],
        };
        var result = LayoutClipper.EnsureValidHoles(p);
        Assert.NotEmpty(result);
    }
}
