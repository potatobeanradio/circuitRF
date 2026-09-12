using System;
using System.Collections.Generic;
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

    // ── Clip / Cut Out (brief-layout-clip-and-cut-out.md §10) ────────────────────

    private static string SerializeOne(LayoutShape shape)
    {
        var view = new LayoutView();
        view.Shapes.Add(shape);
        return LayoutPersistence.Serialize(view);
    }

    /// <summary>§10 gate 1, geometry half — the REPORTED case. 67 mutually disjoint polygons plus a
    /// Rect region of interest: 65 lie fully outside it, 1 fully inside, 1 straddles (the board-wide
    /// pour). Clip keeps the inside one, the clipped part of the straddler, and nothing else — while
    /// Intersect over the SAME operand set still returns empty, because that is the correct answer to
    /// the different question §1 describes.</summary>
    [Fact]
    public void Clip_SixtySevenDisjointPolygons_KeepsOnlyTheInsideOneAndTheClippedStraddler_IntersectStillEmpty()
    {
        var stencil = Rect(0, 0, 100_000, 100_000);

        var operands = new List<LayoutShape>();
        // 65 fully outside, marched well clear of the stencil.
        for (int i = 0; i < 65; i++)
            operands.Add(Rect(200_000 + i * 20_000, 0, 210_000 + i * 20_000, 10_000));
        // 1 fully inside.
        var inside = Rect(20_000, 20_000, 40_000, 40_000);
        operands.Add(inside);
        // 1 straddling — the board-wide pour.
        operands.Add(Rect(-500_000, 40_000, 500_000, 60_000));

        var result = LayoutBooleans.Clip(operands, stencil, null);

        Assert.Equal(2, result.Shapes.Count);
        Assert.Equal(65, result.OperandsRemoved);
        Assert.Equal(1, result.OperandsChanged);
        Assert.Equal(1, result.OperandsUntouched);
        Assert.Equal(operands.Count, result.OperandsRemoved + result.OperandsChanged + result.OperandsUntouched);

        // The fully-inside operand is passed through as the SAME object (R-clip-6), not polygonized.
        Assert.Same(inside, result.Shapes[0]);
        // The straddler is clipped to the stencil's own span.
        var clipped = Assert.IsType<PolygonShape>(result.Shapes[1]);
        Assert.Equal(new Bbox(0, 40_000, 100_000, 60_000), LayoutGeometry.BboxOf(clipped));

        // §1 unchanged: Intersect over the same operands (plus the rect) is still empty.
        var withStencil = new List<LayoutShape>(operands) { stencil };
        Assert.Empty(LayoutBooleans.Intersect(withStencil, null).Shapes);
    }

    /// <summary>R-clip-5 — each result carries its OWN operand's net, never a cleared one. Clipping a
    /// 40-net copper layer must not silently strip 40 nets, which is what <c>Combine</c>'s
    /// <c>NetsDiffered</c> rule would have done.</summary>
    [Fact]
    public void Clip_OperandsOnFourDifferentNets_EachResultKeepsItsOwnNet()
    {
        var stencil = Rect(0, 0, 100_000, 100_000);
        var operands = new List<LayoutShape>
        {
            Rect(-10_000, 10_000, 10_000, 20_000, net: "VDD"),
            Rect(-10_000, 30_000, 10_000, 40_000, net: "GND"),
            Rect(-10_000, 50_000, 10_000, 60_000, net: "RFin"),
            Rect(-10_000, 70_000, 10_000, 80_000, net: "RFout"),
        };

        var result = LayoutBooleans.Clip(operands, stencil, null);

        Assert.Equal(4, result.OperandsChanged);
        Assert.Equal(new[] { "VDD", "GND", "RFin", "RFout" }, result.Shapes.Select(sh => sh.Net));
    }

    /// <summary>§4 — one stencil, operands on three layers, one operation: each result stays on its
    /// OWN operand's layer. The stencil contributes no layer.</summary>
    [Fact]
    public void Clip_OperandsOnThreeLayers_EachResultKeepsItsOwnLayer()
    {
        var layer3 = new LayerKey(3, 0);
        var stencil = Rect(0, 0, 100_000, 100_000);
        var operands = new List<LayoutShape>
        {
            Rect(-10_000, 10_000, 10_000, 20_000, Layer1),
            Rect(-10_000, 30_000, 10_000, 40_000, Layer2),
            Rect(-10_000, 50_000, 10_000, 60_000, layer3),
        };

        var result = LayoutBooleans.Clip(operands, stencil, null);

        Assert.Equal(new[] { Layer1, Layer2, layer3 }, result.Shapes.Select(sh => sh.Layer));
    }

    /// <summary>§10 gate 6 / R-clip-6 — the test that catches a SILENT FLATTEN. A Circle and a
    /// RoundedRect wholly outside the stencil survive Cut Out as the same objects, still
    /// <c>CircleShape</c>/<c>RoundedRectShape</c>, byte-identical through
    /// <c>LayoutPersistence.Serialize</c>.</summary>
    [Fact]
    public void CutOut_CurvedOperandsWhollyOutsideTheStencil_PassThroughAsTheSameObjects_NotPolygonized()
    {
        var stencil = Rect(0, 0, 10_000, 10_000);
        var circle = new CircleShape { Layer = Layer1, Cx = 500_000, Cy = 500_000, R = 20_000 };
        var rrect = new RoundedRectShape { Layer = Layer1, X1 = 900_000, Y1 = 0, X2 = 950_000, Y2 = 50_000, CornerRadius = 5_000 };
        string circleJson = SerializeOne(circle), rrectJson = SerializeOne(rrect);

        var result = LayoutBooleans.CutOut([circle, rrect], stencil, null);

        Assert.Equal(2, result.OperandsUntouched);
        Assert.Equal(0, result.OperandsChanged);
        Assert.Equal(0, result.OperandsRemoved);
        Assert.False(result.AnyCurvedOperand);   // nothing was flattened, so nothing to warn about
        Assert.Same(circle, result.Shapes[0]);
        Assert.Same(rrect, result.Shapes[1]);
        Assert.IsType<CircleShape>(result.Shapes[0]);
        Assert.IsType<RoundedRectShape>(result.Shapes[1]);
        Assert.Equal(circleJson, SerializeOne(result.Shapes[0]));
        Assert.Equal(rrectJson, SerializeOne(result.Shapes[1]));
    }

    /// <summary>The Rect-stencil containment shortcut's other half: an operand wholly INSIDE a Rect
    /// stencil is removed by Cut Out with no Clipper2 call, and passed through unchanged by Clip.</summary>
    [Fact]
    public void CutOut_OperandWhollyInsideARectStencil_IsRemoved()
    {
        var stencil = Rect(0, 0, 100_000, 100_000);
        var inside = new CircleShape { Layer = Layer1, Cx = 50_000, Cy = 50_000, R = 10_000 };

        var result = LayoutBooleans.CutOut([inside], stencil, null);

        Assert.Empty(result.Shapes);
        Assert.Equal(1, result.OperandsRemoved);
    }

    // ── R-clip-10: point-anchored operands (via, label) ───────────────────────

    /// <summary>
    /// The owner-reported defect: clipping an imported board left every via on it standing, including
    /// the 125 of 189 that lay far outside the stencil. A via was not an operand at all, so "keep what
    /// is inside" silently meant "keep every via anywhere".
    /// </summary>
    [Fact]
    public void Clip_KeepsOnlyTheViasInsideTheStencil_AndCutOutKeepsOnlyTheOnesOutside()
    {
        var stencil = Rect(0, 0, 100_000, 100_000);
        ViaShape Via(long x, long y) => new() { Layer = Layer1, X = x, Y = y, PadSize = 20_000, DrillSize = 10_000 };

        var inside = Via(50_000, 50_000);
        var outside = Via(500_000, 500_000);
        // Straddling the edge: the via is AT a point, so its pad hanging over the boundary decides
        // nothing — the anchor is inside, so it is kept whole.
        var onEdge = Via(99_000, 99_000);

        var clipped = LayoutBooleans.Clip([inside, outside, onEdge], stencil, null);
        Assert.Equal([inside, onEdge], clipped.Shapes);
        Assert.Equal(1, clipped.OperandsRemoved);
        Assert.Equal(2, clipped.OperandsUntouched);
        Assert.Equal(0, clipped.OperandsChanged);   // never rebuilt, never polygonized

        var cut = LayoutBooleans.CutOut([inside, outside, onEdge], stencil, null);
        Assert.Equal([outside], cut.Shapes);
        Assert.Equal(2, cut.OperandsRemoved);
    }

    /// <summary>A label is point-anchored for the same reason and by the same code path.</summary>
    [Fact]
    public void Clip_KeepsOnlyTheLabelsInsideTheStencil()
    {
        var stencil = Rect(0, 0, 100_000, 100_000);
        var inside = new LabelShape { Layer = Layer1, X = 10_000, Y = 10_000, Text = "IN" };
        var outside = new LabelShape { Layer = Layer1, X = -10_000, Y = 10_000, Text = "OUT" };

        var result = LayoutBooleans.Clip([inside, outside], stencil, null);

        Assert.Equal([inside], result.Shapes);
    }

    /// <summary>The stencil's HOLE is real for a via too — the bbox alone would keep it. This is what
    /// the probe-square test buys over a bounding-box containment shortcut on a non-Rect stencil.</summary>
    [Fact]
    public void Clip_AViaInsideAHoleOfAPolygonStencil_IsRemoved()
    {
        // A 100k square with a 40k..60k square hole.
        var stencil = new PolygonShape
        {
            Layer = Layer1,
            Xy = [0, 0, 100_000, 0, 100_000, 100_000, 0, 100_000],
            Holes = [[40_000, 40_000, 40_000, 60_000, 60_000, 60_000, 60_000, 40_000]],  // wound opposite the outer ring
        };
        var inHole = new ViaShape { Layer = Layer1, X = 50_000, Y = 50_000, PadSize = 2_000, DrillSize = 1_000 };
        var inSolid = new ViaShape { Layer = Layer1, X = 20_000, Y = 20_000, PadSize = 2_000, DrillSize = 1_000 };

        var result = LayoutBooleans.Clip([inHole, inSolid], stencil, null);

        Assert.Equal([inSolid], result.Shapes);
        Assert.Equal(1, result.OperandsRemoved);
    }

    /// <summary>Clip and Cut Out stay exact complements over the point-anchored kinds too — every
    /// operand lands in exactly one of the two results, and never in both.</summary>
    [Fact]
    public void ClipAndCutOut_ArePartitionsOverPointAnchoredOperands()
    {
        var stencil = new PolygonShape
        {
            Layer = Layer1,
            Xy = [0, 0, 100_000, 0, 100_000, 100_000, 0, 100_000],
            Holes = [[40_000, 40_000, 40_000, 60_000, 60_000, 60_000, 60_000, 40_000]],  // wound opposite the outer ring
        };
        var operands = new List<LayoutShape>();
        for (long x = -20_000; x <= 120_000; x += 10_000)
            for (long y = -20_000; y <= 120_000; y += 10_000)
                operands.Add(new ViaShape { Layer = Layer1, X = x, Y = y, PadSize = 2_000, DrillSize = 1_000 });

        var kept = LayoutBooleans.Clip(operands, stencil, null).Shapes;
        var dropped = LayoutBooleans.CutOut(operands, stencil, null).Shapes;

        Assert.Equal(operands.Count, kept.Count + dropped.Count);
        Assert.Empty(kept.Intersect(dropped));
        Assert.Equal(operands.OrderBy(o => o.GetHashCode()), kept.Concat(dropped).OrderBy(o => o.GetHashCode()));
    }

    /// <summary>The two operand tests are deliberately different, and this states which is which: a via
    /// can be decided in or out of a region but can never be flattened INTO one, so it is a clip
    /// operand and not a clipper operand. Sharing one test is what caused the defect above.</summary>
    [Fact]
    public void IsClipOperand_AddsThePointAnchoredKinds_ButNotTheBitmap()
    {
        var via = new ViaShape { Layer = Layer1, X = 0, Y = 0, PadSize = 2_000, DrillSize = 1_000 };
        var label = new LabelShape { Layer = Layer1, X = 0, Y = 0, Text = "L1" };
        var bitmap = new BitmapShape { Layer = Layer1, X = 0, Y = 0, W = 1_000, H = 1_000 };

        Assert.True(LayoutBooleans.IsClipOperand(via));
        Assert.True(LayoutBooleans.IsClipOperand(label));
        Assert.False(LayoutBooleans.IsClipperOperand(via));
        Assert.False(LayoutBooleans.IsClipperOperand(label));

        // A bitmap has extent and no anchor — clipping one would mean cropping the image (R-bmp-3).
        Assert.False(LayoutBooleans.IsClipOperand(bitmap));

        // Every region kind is still a clip operand.
        Assert.True(LayoutBooleans.IsClipOperand(Rect(0, 0, 10, 10)));
    }

    /// <summary>§10's property test: Clip and Cut Out are COMPLEMENTS. For any operand and stencil,
    /// Clip ∪ Cut Out reconstructs the operand and their intersection is empty — one assertion that
    /// catches a fill-rule or hole-nesting mistake in either.</summary>
    [Theory]
    [InlineData(0, 0, 60_000, 60_000)]        // straddles one corner
    [InlineData(-50_000, 20_000, 50_000, 30_000)] // a bar straight through
    [InlineData(20_000, 20_000, 80_000, 80_000)]  // wholly inside
    [InlineData(500_000, 0, 600_000, 10_000)]     // wholly outside
    public void ClipAndCutOut_AreComplements_OverAPolygonStencil(long x1, long y1, long x2, long y2)
    {
        // A NON-rect stencil on purpose — the Rect containment shortcut must not be what makes this
        // pass, and an L-shaped stencil exercises concavity.
        var stencil = new PolygonShape
        {
            Layer = Layer1,
            Xy = [0, 0, 100_000, 0, 100_000, 40_000, 40_000, 40_000, 40_000, 100_000, 0, 100_000],
        };
        var operand = Rect(x1, y1, x2, y2);

        var kept = LayoutBooleans.Clip([operand], stencil, null).Shapes;
        var dropped = LayoutBooleans.CutOut([operand], stencil, null).Shapes;

        // Union of the two halves is the operand back again.
        var rebuilt = LayoutBooleans.Union([.. kept, .. dropped], null).Shapes;
        Assert.Equal(AreaOf([operand]), AreaOf(rebuilt), 1e-6 * Math.Max(1.0, AreaOf([operand])));
        // ...and the two halves share no area.
        Assert.Equal(0.0, AreaOf(LayoutBooleans.Intersect([.. Wrap(kept), .. Wrap(dropped)], null).Shapes), 1.0);
    }

    // A one-element list stays itself; an empty half is represented by a degenerate zero-area rect so
    // the n-ary Intersect below still has two operands to fold.
    private static IReadOnlyList<LayoutShape> Wrap(IReadOnlyList<LayoutShape> shapes) =>
        shapes.Count > 0 ? shapes : [Rect(0, 0, 0, 0)];

    private static double AreaOf(IReadOnlyList<LayoutShape> shapes)
    {
        double sum = 0;
        foreach (var shape in shapes)
        {
            var rings = LayoutFlattener.Flatten(shape, LayoutFlattener.ResolveTolDbu(shape, null));
            for (int i = 0; i < rings.Count; i++)
                sum += i == 0 ? Math.Abs(LayoutGeometry.SignedArea(rings[i])) : -Math.Abs(LayoutGeometry.SignedArea(rings[i]));
        }
        return sum;
    }

    /// <summary>§10 gate 10 / R-clip-8 — EVERY <c>LayoutShape</c> subclass in a boolean operand set,
    /// no throw. The exclusion is a positive test for what the flattener accepts
    /// (<see cref="LayoutBooleans.IsClipperOperand"/>), so a new non-region shape kind cannot
    /// reintroduce the <c>ArgumentOutOfRangeException</c> §8 found.</summary>
    [Fact]
    public void EveryLayoutShapeSubclass_IsEitherAcceptedByTheClipperOrExcludedByIsClipperOperand()
    {
        LayoutShape[] all =
        [
            Rect(0, 0, 10_000, 10_000),
            new PolygonShape { Layer = Layer1, Xy = [0, 0, 10_000, 0, 10_000, 10_000] },
            new RoundedRectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000, CornerRadius = 1_000 },
            new CircleShape { Layer = Layer1, Cx = 0, Cy = 0, R = 5_000 },
            new CurveShape { Layer = Layer1, Xy = [0, 0, 10_000, 0, 10_000, 10_000] },
            new PathShape { Layer = Layer1, Xy = [0, 0, 10_000, 0], Width = 1_000 },
            new ViaShape { Layer = Layer1, X = 0, Y = 0, PadSize = 2_000, DrillSize = 1_000 },
            new LabelShape { Layer = Layer1, X = 0, Y = 0, Text = "L1" },
            new BitmapShape { Layer = Layer1, X = 0, Y = 0, W = 1_000, H = 1_000 },
        ];

        // Every subclass is covered — a new one added to the model without a decision here fails this.
        var kinds = typeof(LayoutShape).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(LayoutShape)) && !t.IsAbstract).ToList();
        Assert.Equal(kinds.Count, all.Select(sh => sh.GetType()).Distinct().Count());

        var accepted = all.Where(LayoutBooleans.IsClipperOperand).ToList();
        Assert.Equal(6, accepted.Count);   // Rect/Polygon/RoundedRect/Circle/Curve/Path

        // Nothing IsClipperOperand accepts may throw out of any boolean, offset or clip.
        foreach (var shape in accepted)
        {
            LayoutBooleans.Union([shape, Rect(0, 0, 5_000, 5_000)], null);
            LayoutBooleans.Intersect([shape, Rect(0, 0, 5_000, 5_000)], null);
            LayoutBooleans.Difference([shape, Rect(0, 0, 5_000, 5_000)], null);
            LayoutBooleans.Xor([shape, Rect(0, 0, 5_000, 5_000)], null);
            LayoutBooleans.Offset(shape, 100, null);
            LayoutBooleans.Clip([shape], Rect(0, 0, 5_000, 5_000), null);
            LayoutBooleans.CutOut([shape], Rect(0, 0, 5_000, 5_000), null);
        }

        // ...and everything it rejects is exactly what LayoutFlattener refuses.
        foreach (var shape in all.Where(sh => !LayoutBooleans.IsClipperOperand(sh)))
            Assert.Throws<ArgumentOutOfRangeException>(() => LayoutClipper.ToClipperPaths(shape, 1000));
    }

    private static RectShape Clone(RectShape r) => new() { Layer = r.Layer, Net = r.Net, X1 = r.X1, Y1 = r.Y1, X2 = r.X2, Y2 = r.Y2 };
    private static CircleShape Clone(CircleShape c) => new() { Layer = c.Layer, Net = c.Net, Cx = c.Cx, Cy = c.Cy, R = c.R };
}
