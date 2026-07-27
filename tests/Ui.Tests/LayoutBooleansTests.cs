using System.Linq;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

// ── Phase L1e gates 4/5/8/9/12: docs/sonnet-briefs/brief-L1e-clipper-operations.md §3/§4

public class LayoutBooleansTests
{
    private static readonly LayerKey Layer1 = new(1, 0);
    private static readonly LayerKey Layer2 = new(2, 0);

    private static RectShape Rect(long x1, long y1, long x2, long y2, LayerKey? layer = null, string? net = null) =>
        new() { Layer = layer ?? Layer1, Net = net, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 };

    // ── Gate 4: the canonical case ────────────────────────────────────────────────

    [Fact]
    public void Difference_RectMinusFullyInteriorCircle_OneShapeOneHole_NotTwoShapes_NotKeyholed()
    {
        var rect = Rect(0, 0, 100_000, 100_000);
        var circle = new CircleShape { Layer = Layer1, Cx = 50_000, Cy = 50_000, R = 20_000 };

        var result = LayoutBooleans.Difference([rect, circle], null);

        var poly = Assert.IsType<PolygonShape>(Assert.Single(result.Shapes));
        Assert.NotNull(poly.Holes);
        Assert.Single(poly.Holes!);
    }

    // ── Gate 5: every boolean, overlapping / disjoint / fully-contained ──────────

    [Fact]
    public void Union_TwoOverlappingRects_OneShape()
    {
        var a = Rect(0, 0, 100_000, 100_000);
        var b = Rect(50_000, 50_000, 150_000, 150_000);
        var result = LayoutBooleans.Union([a, b], null);
        Assert.Single(result.Shapes);
    }

    [Fact]
    public void Union_TwoDisjointRects_TwoShapes()
    {
        var a = Rect(0, 0, 100, 100);
        var b = Rect(1000, 0, 1100, 100);
        var result = LayoutBooleans.Union([a, b], null);
        Assert.Equal(2, result.Shapes.Count);
    }

    [Fact]
    public void Intersect_TwoOverlappingRects_CommonRegion()
    {
        var a = Rect(0, 0, 100_000, 100_000);
        var b = Rect(50_000, 50_000, 150_000, 150_000);
        var result = LayoutBooleans.Intersect([a, b], null);
        var poly = Assert.IsType<PolygonShape>(Assert.Single(result.Shapes));
        var bb = LayoutGeometry.BboxOf(poly);
        Assert.Equal(new Bbox(50_000, 50_000, 100_000, 100_000), bb);
    }

    [Fact]
    public void Intersect_DisjointRects_EmptyResult_NoThrow()
    {
        var a = Rect(0, 0, 100, 100);
        var b = Rect(1000, 0, 1100, 100);
        var result = LayoutBooleans.Intersect([a, b], null);
        Assert.Empty(result.Shapes);
    }

    [Fact]
    public void Xor_TwoOverlappingRects_TwoDisjointPieces()
    {
        var a = Rect(0, 0, 100_000, 100_000);
        var b = Rect(50_000, 0, 150_000, 100_000);
        var result = LayoutBooleans.Xor([a, b], null);
        Assert.Equal(2, result.Shapes.Count);
    }

    [Fact]
    public void Difference_FullyContained_Annihilates()
    {
        // Subtracting a rect that fully contains the subject leaves nothing.
        var a = Rect(10, 10, 20, 20);
        var b = Rect(0, 0, 1000, 1000);
        var result = LayoutBooleans.Difference([a, b], null);
        Assert.Empty(result.Shapes);
    }

    [Fact]
    public void Difference_Disjoint_ReturnsOriginalSubjectShapeUnchangedInExtent()
    {
        var a = Rect(0, 0, 100, 100);
        var b = Rect(1000, 0, 1100, 100);
        var result = LayoutBooleans.Difference([a, b], null);
        var poly = Assert.IsType<PolygonShape>(Assert.Single(result.Shapes));
        Assert.Equal(new Bbox(0, 0, 100, 100), LayoutGeometry.BboxOf(poly));
    }

    // ── Gate 6: multiple disjoint results ────────────────────────────────────────

    [Fact]
    public void Difference_SplitsShapeInTwo_TwoDisjointPieces()
    {
        // A wide bar minus a thin vertical strip through the middle splits it in two.
        var bar = Rect(0, 0, 100_000, 10_000);
        var strip = Rect(45_000, -10_000, 55_000, 20_000);
        var result = LayoutBooleans.Difference([bar, strip], null);
        Assert.Equal(2, result.Shapes.Count);
    }

    // ── Gate 7: net propagation (§3.4 R10a) ──────────────────────────────────────

    [Fact]
    public void Union_SameNet_PropagatesNet()
    {
        var a = Rect(0, 0, 100_000, 100_000, net: "VCC");
        var b = Rect(50_000, 50_000, 150_000, 150_000, net: "VCC");
        var result = LayoutBooleans.Union([a, b], null);
        Assert.False(result.NetsDiffered);
        Assert.Equal("VCC", Assert.Single(result.Shapes).Net);
    }

    [Fact]
    public void Union_DifferingNets_ClearsNetAndReports()
    {
        var a = Rect(0, 0, 100_000, 100_000, net: "VCC");
        var b = Rect(50_000, 50_000, 150_000, 150_000, net: "GND");
        var result = LayoutBooleans.Union([a, b], null);
        Assert.True(result.NetsDiffered);
        Assert.Null(Assert.Single(result.Shapes).Net);
    }

    [Fact]
    public void Union_LayerAttribution_IsFirstOperandsLayer()
    {
        var a = Rect(0, 0, 100_000, 100_000, layer: Layer2);
        var b = Rect(50_000, 50_000, 150_000, 150_000, layer: Layer1);
        var result = LayoutBooleans.Union([a, b], null);
        Assert.Equal(Layer2, Assert.Single(result.Shapes).Layer);
    }

    [Fact]
    public void Difference_LayerAttribution_IsSubjectsLayer_NotTheSubtractedShapes()
    {
        var a = Rect(0, 0, 100_000, 100_000, layer: Layer1);
        var b = Rect(50_000, 0, 150_000, 100_000, layer: Layer2);
        var result = LayoutBooleans.Difference([a, b], null);
        Assert.Equal(Layer1, Assert.Single(result.Shapes).Layer);
    }

    [Fact]
    public void Difference_SelectionOrder_FirstSelectedIsSubject()
    {
        // A - B leaves the LEFT half; B - A (swapped selection order) leaves the RIGHT half.
        var a = Rect(0, 0, 100_000, 100_000);
        var b = Rect(50_000, 0, 150_000, 100_000);

        var aMinusB = LayoutBooleans.Difference([a, b], null);
        var bMinusA = LayoutBooleans.Difference([b, a], null);

        var bbA = LayoutGeometry.BboxOf(Assert.Single(aMinusB.Shapes));
        var bbB = LayoutGeometry.BboxOf(Assert.Single(bMinusA.Shapes));
        Assert.Equal(new Bbox(0, 0, 50_000, 100_000), bbA);
        Assert.Equal(new Bbox(100_000, 0, 150_000, 100_000), bbB);
    }

    // ── Merge (§3): union restricted to shapes sharing a layer, applied per layer ───

    [Fact]
    public void Merge_TwoLayers_OneUnionPerLayer()
    {
        var a1 = Rect(0, 0, 100_000, 100_000, layer: Layer1);
        var a2 = Rect(50_000, 50_000, 150_000, 150_000, layer: Layer1);
        var b1 = Rect(0, 0, 100_000, 100_000, layer: Layer2);
        var groups = LayoutBooleans.Merge([a1, a2, b1], null);

        Assert.Equal(2, groups.Count);
        var g1 = groups.Single(g => g.Layer == Layer1);
        var g2 = groups.Single(g => g.Layer == Layer2);
        Assert.Single(g1.Result.Shapes);   // a1 union a2 merges to one piece
        Assert.Single(g2.Result.Shapes);   // lone b1 passes through cleanly
    }

    // ── Gate 8: offset ────────────────────────────────────────────────────────────

    [Fact]
    public void Offset_Positive_Grows()
    {
        var r = Rect(0, 0, 10_000, 10_000);
        var result = LayoutBooleans.Offset(r, 1000, null);
        var poly = Assert.IsType<PolygonShape>(Assert.Single(result.Shapes));
        var bb = LayoutGeometry.BboxOf(poly);
        Assert.Equal(new Bbox(-1000, -1000, 11_000, 11_000), bb);
    }

    [Fact]
    public void Offset_Negative_Shrinks()
    {
        var r = Rect(0, 0, 10_000, 10_000);
        var result = LayoutBooleans.Offset(r, -1000, null);
        var poly = Assert.IsType<PolygonShape>(Assert.Single(result.Shapes));
        var bb = LayoutGeometry.BboxOf(poly);
        Assert.Equal(new Bbox(1000, 1000, 9000, 9000), bb);
    }

    [Fact]
    public void Offset_OverShrink_AnnihilatesWithoutThrowing()
    {
        var r = Rect(0, 0, 1000, 1000);
        var result = LayoutBooleans.Offset(r, -10_000, null);
        Assert.Empty(result.Shapes);
    }

    // ── Gate 12: self-intersection repair ────────────────────────────────────────

    [Fact]
    public void Repair_BowtiePolygon_RepairsToCleanResult()
    {
        // A classic self-crossing "bowtie": (0,0)-(100,100)-(100,0)-(0,100) closed.
        var bowtie = new PolygonShape { Layer = Layer1, Xy = [0, 0, 100, 100, 100, 0, 0, 100] };
        Assert.True(LayoutSelfIntersection.Test(bowtie, null));

        var result = LayoutBooleans.Repair(bowtie, null);
        Assert.NotEmpty(result.Shapes);
        foreach (var s in result.Shapes)
            Assert.False(LayoutSelfIntersection.Test(s, null));
    }

    // ── Gate 9: determinism ───────────────────────────────────────────────────────

    [Fact]
    public void Union_SameInputs_RepeatedlyByteIdentical()
    {
        var a = Rect(0, 0, 100_000, 100_000);
        var b = Rect(50_000, 50_000, 150_000, 150_000);

        string? first = null;
        for (int i = 0; i < 20; i++)
        {
            var result = LayoutBooleans.Union([Clone(a), Clone(b)], null);
            var view = new LayoutView();
            view.Shapes.AddRange(result.Shapes);
            var json = LayoutPersistence.Serialize(view);
            first ??= json;
            Assert.Equal(first, json);
        }
    }

    [Fact]
    public void Difference_AfterSerializeReloadRoundTrip_ByteIdentical()
    {
        var a = Rect(0, 0, 100_000, 100_000);
        var b = new CircleShape { Layer = Layer1, Cx = 50_000, Cy = 50_000, R = 20_000 };

        var view1 = new LayoutView();
        view1.Shapes.AddRange(LayoutBooleans.Difference([a, b], null).Shapes);
        var json1 = LayoutPersistence.Serialize(view1);

        var reloaded = LayoutPersistence.Deserialize(json1);
        var view2 = new LayoutView();
        view2.Shapes.AddRange(LayoutBooleans.Difference([Clone(a), Clone(b)], null).Shapes);
        var json2 = LayoutPersistence.Serialize(view2);

        Assert.Equal(json1, LayoutPersistence.Serialize(reloaded));
        Assert.Equal(json1, json2);
    }

    private static RectShape Clone(RectShape r) => new() { Layer = r.Layer, Net = r.Net, X1 = r.X1, Y1 = r.Y1, X2 = r.X2, Y2 = r.Y2 };
    private static CircleShape Clone(CircleShape c) => new() { Layer = c.Layer, Net = c.Net, Cx = c.Cx, Cy = c.Cy, R = c.R };
}
