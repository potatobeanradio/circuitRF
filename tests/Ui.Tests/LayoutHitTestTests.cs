using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Tests;

// ── Phase L1c gates 3, 4, 5: docs/sonnet-briefs/brief-L1c-selection-and-properties.md

public class LayoutHitTestTests
{
    private static LayerDef Layer(int layer, int zOrder, bool visible = true, bool selectable = true) => new()
    {
        Key = new LayerKey(layer, 0),
        Name = $"L{layer}",
        Color = new Rgba(100, 100, 100),
        ZOrder = zOrder,
        Visible = visible,
        Selectable = selectable,
    };

    private static Technology TechWith(params LayerDef[] layers) => new() { Layers = [.. layers] };

    // ── Gate 3: hit ordering ──────────────────────────────────────────────────

    [Fact]
    public void SmallShapeOnLargeShape_SameLayer_SmallIsFirst()
    {
        var view = new LayoutView();
        view.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 100_000 });   // index 0: large
        view.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 40_000, Y1 = 40_000, X2 = 60_000, Y2 = 60_000 }); // index 1: small, on top

        var stack = LayoutHitTest.HitStack(view, TechWith(Layer(1, 0)), 50_000, 50_000, 100);
        Assert.Equal(2, stack.Count);
        Assert.Equal(1, stack[0]); // small first
        Assert.Equal(0, stack[1]);
    }

    [Fact]
    public void DifferentLayers_OrderByZOrderDescending()
    {
        var view = new LayoutView();
        view.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 }); // low ZOrder
        view.Shapes.Add(new RectShape { Layer = new LayerKey(2, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 }); // high ZOrder

        var tech = TechWith(Layer(1, 0), Layer(2, 100));
        var stack = LayoutHitTest.HitStack(view, tech, 5000, 5000, 100);

        Assert.Equal(2, stack.Count);
        Assert.Equal(1, stack[0]); // layer 2 (higher ZOrder) on top
        Assert.Equal(0, stack[1]);
    }

    [Fact]
    public void Ties_BreakByAscendingListIndex_Reproducibly()
    {
        var view = new LayoutView();
        view.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 });
        view.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 }); // identical bbox/layer

        var tech = TechWith(Layer(1, 0));
        var stack = LayoutHitTest.HitStack(view, tech, 5000, 5000, 100);
        Assert.Equal(new[] { 0, 1 }, stack);

        // Reproducible across repeated calls.
        var again = LayoutHitTest.HitStack(view, tech, 5000, 5000, 100);
        Assert.Equal(stack, again);
    }

    // ── Gate 4: per-primitive hit accuracy ────────────────────────────────────

    [Fact]
    public void Rect_Inside_Outside_NearEdge()
    {
        var view = new LayoutView();
        view.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 });
        var tech = TechWith(Layer(1, 0));

        Assert.Single(LayoutHitTest.HitStack(view, tech, 5000, 5000, 100));   // inside
        Assert.Empty(LayoutHitTest.HitStack(view, tech, 20_000, 20_000, 100)); // well outside
        Assert.Single(LayoutHitTest.HitStack(view, tech, 10_050, 5000, 100));  // just outside, within tol of edge
    }

    [Fact]
    public void Circle_Inside_Outside_NearEdge()
    {
        var view = new LayoutView();
        view.Shapes.Add(new CircleShape { Layer = new LayerKey(1, 0), Cx = 0, Cy = 0, R = 10_000 });
        var tech = TechWith(Layer(1, 0));

        Assert.Single(LayoutHitTest.HitStack(view, tech, 0, 0, 100));         // center
        Assert.Single(LayoutHitTest.HitStack(view, tech, 10_050, 0, 100));    // just outside boundary, within tol
        Assert.Empty(LayoutHitTest.HitStack(view, tech, 15_000, 0, 100));     // well outside
    }

    [Fact]
    public void RoundedRect_CornerArc_Inside_Outside_NearEdge()
    {
        var view = new LayoutView();
        var rr = new RoundedRectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 100_000, CornerRadius = 20_000 };
        view.Shapes.Add(rr);
        var tech = TechWith(Layer(1, 0));

        Assert.Single(LayoutHitTest.HitStack(view, tech, 50_000, 50_000, 100));  // well inside
        Assert.Empty(LayoutHitTest.HitStack(view, tech, 2000, 2000, 100));       // in the clipped corner — outside the rounded shape

        // Near the corner arc boundary: center at (80000,80000), radius 20000 -> point at 45° just outside.
        double d = 20_000 + 60; // just past the arc radius
        long px = (long)(80_000 + d / System.Math.Sqrt(2));
        long py = (long)(80_000 + d / System.Math.Sqrt(2));
        Assert.Single(LayoutHitTest.HitStack(view, tech, px, py, 100));
    }

    [Fact]
    public void ArcBearingCurve_Inside_Outside_NearEdge()
    {
        // A closed curve: two straight edges + a bulging arc edge forming a "D" shape.
        var curve = new CurveShape
        {
            Layer = new LayerKey(1, 0),
            Xy = [0, 0, 0, 100_000, 100_000, 50_000], // straight up, then an arc back down-right
            Edges =
            [
                new LayoutEdge { Kind = EdgeKind.Line },
                new LayoutEdge { Kind = EdgeKind.Arc, Bulge = 0.6 },
                new LayoutEdge { Kind = EdgeKind.Line },
            ],
        };
        var view = new LayoutView();
        view.Shapes.Add(curve);
        var tech = TechWith(Layer(1, 0));

        Assert.Single(LayoutHitTest.HitStack(view, tech, 20_000, 50_000, 100)); // interior point, inside the "D"
        Assert.Empty(LayoutHitTest.HitStack(view, tech, -50_000, 50_000, 100)); // well outside to the left
    }

    [Fact]
    public void Path_OnCenterline_Hits_FarBeyondWidthPlusTolerance_DoesNot()
    {
        var path = new PathShape { Layer = new LayerKey(1, 0), Xy = [0, 0, 100_000, 0], Width = 4000 };
        var view = new LayoutView();
        view.Shapes.Add(path);
        var tech = TechWith(Layer(1, 0));

        Assert.Single(LayoutHitTest.HitStack(view, tech, 50_000, 0, 100));                 // on centerline
        Assert.Single(LayoutHitTest.HitStack(view, tech, 50_000, 2000 + 50, 100));          // just within Width/2 + tol
        Assert.Empty(LayoutHitTest.HitStack(view, tech, 50_000, 2000 + 2 * 100 + 500, 100)); // Width/2 + 2*tol beyond
    }

    // ── Gate 5: non-selectable / hidden layers never hit ──────────────────────

    [Fact]
    public void HiddenLayer_NeverHit()
    {
        var view = new LayoutView();
        view.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 });
        var tech = TechWith(Layer(1, 0, visible: false));
        Assert.Empty(LayoutHitTest.HitStack(view, tech, 5000, 5000, 100));
    }

    [Fact]
    public void NonSelectableLayer_NeverHit()
    {
        var view = new LayoutView();
        view.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 });
        var tech = TechWith(Layer(1, 0, selectable: false));
        Assert.Empty(LayoutHitTest.HitStack(view, tech, 5000, 5000, 100));
    }

    [Fact]
    public void UnknownLayer_ResolvesThroughFallbackPalette_IsSelectable()
    {
        var view = new LayoutView();
        view.Shapes.Add(new RectShape { Layer = new LayerKey(99, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 });
        Assert.Single(LayoutHitTest.HitStack(view, tech: null, 5000, 5000, 100));
    }
}
