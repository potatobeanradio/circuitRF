using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

// ── Phase L1f gates 2/4/5/6/7: docs/sonnet-briefs/brief-L1f-clipboard.md
// Framework-free fragment logic: build/serialize/round-trip, DBU rescale, layer reconciliation,
// the "nothing is ever dropped" invariant, and the marker guard.

public class LayoutFragmentTests
{
    private static readonly LayerKey Layer1 = new(1, 0);
    private static readonly LayerKey Layer2 = new(2, 0);
    private static readonly LayerKey UnknownLayer = new(9, 0);

    private static string SerializeShapes(IEnumerable<LayoutShape> shapes)
    {
        var view = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 100 };
        foreach (var s in shapes) view.Shapes.Add(s);
        return LayoutPersistence.Serialize(view);
    }

    /// <summary>One of every shape kind, including a polygon with a hole, an arc-bearing Curve, a
    /// curved Path, and shapes carrying nets — gate 2's fixture.</summary>
    private static List<LayoutShape> OneOfEverything() =>
    [
        new RectShape { Layer = Layer1, Net = "VDD", X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 5_000 },
        new PolygonShape
        {
            Layer = Layer1,
            Xy = [0, 0, 100_000, 0, 100_000, 100_000, 0, 100_000],
            Holes = [[30_000, 30_000, 30_000, 70_000, 70_000, 70_000, 70_000, 30_000]],
        },
        new RoundedRectShape { Layer = Layer2, X1 = -5_000, Y1 = -5_000, X2 = 5_000, Y2 = 5_000, CornerRadius = 1_000 },
        new CircleShape { Layer = Layer2, Net = "GND", Cx = 50_000, Cy = 50_000, R = 20_000 },
        new CurveShape
        {
            Layer = Layer1,
            Xy = [0, 0, 40_000, 0, 40_000, 40_000],
            Edges =
            [
                new LayoutEdge { Kind = EdgeKind.Arc, Bulge = 0.5 },
                new LayoutEdge { Kind = EdgeKind.Line },
                new LayoutEdge { Kind = EdgeKind.Line },
            ],
        },
        new PathShape
        {
            Layer = Layer2,
            Net = "RF_OUT",
            Xy = [0, 0, 20_000, 0, 40_000, 20_000],
            Edges = [new LayoutEdge { Kind = EdgeKind.Arc, Bulge = -0.3 }, new LayoutEdge { Kind = EdgeKind.Line }],
            Width = 2_000,
        },
        new ViaShape { Layer = Layer1, X = 1_000, Y = 1_000, PadSize = 800, DrillSize = 400 },
        new LabelShape { Layer = Layer2, X = 500, Y = 500, Text = "M1", Height = 1_000 },
    ];

    private static Technology TechWithLayers() => new()
    {
        Name = "TestTech",
        Layers =
        [
            new LayerDef { Key = Layer1, Name = "Metal1" },
            new LayerDef { Key = Layer2, Name = "Metal2" },
        ],
    };

    // ── Gate 2: round-trip fidelity ──────────────────────────────────────────────

    [Fact]
    public void Build_Serialize_Deserialize_RoundTripsEveryShapeKindByteIdentical()
    {
        var shapes = OneOfEverything();
        var payload = LayoutFragment.Build(shapes, TechWithLayers(), 1000);

        string json = LayoutFragment.Serialize(payload);
        Assert.True(LayoutFragment.TryDeserialize(json, out var reloaded));

        Assert.Equal(SerializeShapes(shapes), SerializeShapes(reloaded!.Shapes));
        Assert.Equal(LayoutFragment.Marker, reloaded.Marker);
        Assert.Equal(1000, reloaded.DbuPerMicron);
    }

    [Fact]
    public void Build_CapturesOnlyLayersActuallyUsed_AndTheirAnchorIsTheSelectionBboxMin()
    {
        var shapes = new List<LayoutShape>
        {
            new RectShape { Layer = Layer1, X1 = 10_000, Y1 = 20_000, X2 = 30_000, Y2 = 40_000 },
            new RectShape { Layer = Layer2, X1 = -5_000, Y1 = 15_000, X2 = 5_000, Y2 = 25_000 },
        };
        var tech = new Technology
        {
            Layers =
            [
                new LayerDef { Key = Layer1, Name = "Metal1" },
                new LayerDef { Key = Layer2, Name = "Metal2" },
                new LayerDef { Key = new LayerKey(3, 0), Name = "Unused" },
            ],
        };

        var payload = LayoutFragment.Build(shapes, tech, 1000);

        Assert.Equal(2, payload.Layers.Count);
        Assert.DoesNotContain(payload.Layers, l => l.Key == new LayerKey(3, 0));
        Assert.Equal(-5_000, payload.AnchorX);
        Assert.Equal(15_000, payload.AnchorY);
    }

    [Fact]
    public void Build_DeepClonesShapes_LaterMutationOfSourceNeverAffectsFragment()
    {
        var rect = new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 1_000, Y2 = 1_000 };
        var payload = LayoutFragment.Build([rect], null, 1000);

        rect.X2 = 999_999;

        var clonedRect = Assert.IsType<RectShape>(Assert.Single(payload.Shapes));
        Assert.Equal(1_000, clonedRect.X2);
    }

    // ── Gate 7: marker guard — clean no-op, never an exception ───────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{\"Marker\":\"circuitrf/layout-clipboard-v1\"")]           // truncated JSON
    [InlineData("{\"Components\":[],\"Wires\":[],\"GridSize\":100.0}")]     // symbol/schematic-clipboard-shaped JSON
    [InlineData("{\"Marker\":\"circuitrf/symbol-clipboard-v1\",\"Shapes\":[]}")]
    public void TryDeserialize_NonFragmentText_ReturnsFalse_NeverThrows(string? text)
    {
        bool ok = LayoutFragment.TryDeserialize(text, out var payload);
        Assert.False(ok);
        Assert.Null(payload);
    }

    [Fact]
    public void TryDeserialize_GenuineFragment_Succeeds()
    {
        var payload = LayoutFragment.Build([new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 }], null, 1000);
        string json = LayoutFragment.Serialize(payload);

        Assert.True(LayoutFragment.TryDeserialize(json, out var reloaded));
        Assert.NotNull(reloaded);
    }

    // ── Gate 4: DBU rescale ───────────────────────────────────────────────────────

    [Fact]
    public void Rescale_SameResolution_ShapesUnchanged_NoWarnings()
    {
        var payload = LayoutFragment.Build([new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 1_000, Y2 = 1_000 }], null, 1000);
        var result = LayoutFragment.Rescale(payload, 1000);

        Assert.Empty(result.Warnings);
        var r = Assert.IsType<RectShape>(Assert.Single(result.Shapes));
        Assert.Equal(1_000, r.X2);
    }

    [Fact]
    public void Rescale_1nmInto0Point1nm_ExactIntegerRatio_LosslessAndSilent()
    {
        // 1 nm layout: DbuPerMicron = 1000. 0.1 nm layout: DbuPerMicron = 10000. Ratio = 10x, exact.
        var payload = LayoutFragment.Build(
            [new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 1_234, Y2 = 5_678 }], null, dbuPerMicron: 1000);

        var result = LayoutFragment.Rescale(payload, destDbuPerMicron: 10_000);

        Assert.Empty(result.Warnings);
        var r = Assert.IsType<RectShape>(Assert.Single(result.Shapes));
        Assert.Equal(12_340, r.X2);
        Assert.Equal(56_780, r.Y2);
    }

    [Fact]
    public void Rescale_ReverseDirection_NonDividingCoordinate_WarnsNamesTheShape_AndStillPastes()
    {
        // 0.1 nm layout (DbuPerMicron=10000) into a 1 nm layout (DbuPerMicron=1000): ratio = 1/10.
        // X1=5 does not divide evenly by 10 -> lossy, must warn and still produce a shape.
        var payload = LayoutFragment.Build(
            [new RectShape { Layer = Layer1, X1 = 5, Y1 = 0, X2 = 100, Y2 = 100 }], null, dbuPerMicron: 10_000);

        var result = LayoutFragment.Rescale(payload, destDbuPerMicron: 1_000);

        Assert.Single(result.Shapes);
        Assert.NotEmpty(result.Warnings);
        Assert.Contains("RectShape", result.Warnings[0]);
    }

    [Fact]
    public void Rescale_CubicControlPointsAndHoles_AreScaled_NotJustXy()
    {
        var curve = new CurveShape
        {
            Layer = Layer1,
            Xy = [0, 0, 100_000, 0],
            Edges = [new LayoutEdge { Kind = EdgeKind.Cubic, C1X = 10_000, C1Y = 20_000, C2X = 30_000, C2Y = 40_000 }],
        };
        var poly = new PolygonShape
        {
            Layer = Layer1,
            Xy = [0, 0, 100_000, 0, 100_000, 100_000, 0, 100_000],
            Holes = [[10_000, 10_000, 10_000, 20_000, 20_000, 20_000]],
        };
        var payload = LayoutFragment.Build([curve, poly], null, dbuPerMicron: 1000);

        var result = LayoutFragment.Rescale(payload, destDbuPerMicron: 2000); // exact x2

        var rc = Assert.IsType<CurveShape>(result.Shapes[0]);
        Assert.Equal(20_000, rc.Edges![0].C1X);
        Assert.Equal(40_000, rc.Edges![0].C1Y);
        var rp = Assert.IsType<PolygonShape>(result.Shapes[1]);
        Assert.Equal(20_000, rp.Holes![0][0]);
    }

    // ── Gate 5: layer reconciliation — three branches ────────────────────────────
    // The trigger question ("which layers need confirmation?") moved to LayoutLayerMapping.Propose
    // (docs/sonnet-briefs/brief-L1g-technology-retarget.md §1 — see LayoutLayerMappingTests.cs for its
    // match-kind coverage); ApplyReconciliation itself (below) is unchanged by L1g.

    [Fact]
    public void ApplyReconciliation_KeepUnknown_ShapeKeepsItsOriginalLayerKey()
    {
        var shapes = new List<LayoutShape> { new RectShape { Layer = UnknownLayer, X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 } };
        var choices = new Dictionary<LayerKey, LayoutFragment.LayerReconciliationChoice>
        {
            [UnknownLayer] = new(LayoutFragment.LayerReconciliationAction.KeepUnknown),
        };

        var result = LayoutFragment.ApplyReconciliation(shapes, [], choices);

        Assert.Equal(UnknownLayer, Assert.Single(result.Shapes).Layer);
        Assert.Empty(result.LayersToAdd);
    }

    [Fact]
    public void ApplyReconciliation_NoChoiceAtAll_DefaultsToKeepUnknown()
    {
        var shapes = new List<LayoutShape> { new RectShape { Layer = UnknownLayer, X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 } };

        var result = LayoutFragment.ApplyReconciliation(shapes, [], choices: null);

        Assert.Equal(UnknownLayer, Assert.Single(result.Shapes).Layer);
    }

    [Fact]
    public void ApplyReconciliation_MapToExisting_RewritesLayerKey()
    {
        var shapes = new List<LayoutShape> { new RectShape { Layer = UnknownLayer, X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 } };
        var choices = new Dictionary<LayerKey, LayoutFragment.LayerReconciliationChoice>
        {
            [UnknownLayer] = new(LayoutFragment.LayerReconciliationAction.MapToExisting, Layer2),
        };

        var result = LayoutFragment.ApplyReconciliation(shapes, [], choices);

        Assert.Equal(Layer2, Assert.Single(result.Shapes).Layer);
    }

    [Fact]
    public void ApplyReconciliation_AddToTechnology_LeavesKeyAlone_ReturnsLayerDefOncePerKey()
    {
        var fragmentLayerDef = new LayerDef { Key = UnknownLayer, Name = "SourceLayerName" };
        var shapes = new List<LayoutShape>
        {
            new RectShape { Layer = UnknownLayer, X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 },
            new RectShape { Layer = UnknownLayer, X1 = 200, Y1 = 0, X2 = 300, Y2 = 100 },
        };
        var choices = new Dictionary<LayerKey, LayoutFragment.LayerReconciliationChoice>
        {
            [UnknownLayer] = new(LayoutFragment.LayerReconciliationAction.AddToTechnology),
        };

        var result = LayoutFragment.ApplyReconciliation(shapes, [fragmentLayerDef], choices);

        Assert.All(result.Shapes, s => Assert.Equal(UnknownLayer, s.Layer));
        var added = Assert.Single(result.LayersToAdd);
        Assert.Equal("SourceLayerName", added.Name);
    }

    // ── Gate 6: nothing is ever dropped ───────────────────────────────────────────

    [Theory]
    [InlineData(LayoutFragment.LayerReconciliationAction.KeepUnknown)]
    [InlineData(LayoutFragment.LayerReconciliationAction.MapToExisting)]
    [InlineData(LayoutFragment.LayerReconciliationAction.AddToTechnology)]
    public void ApplyReconciliation_AnyChoice_ShapeCountNeverChanges(LayoutFragment.LayerReconciliationAction action)
    {
        var shapes = OneOfEverything().Select(s => { s.Layer = UnknownLayer; return s; }).ToList();
        var choice = new LayoutFragment.LayerReconciliationChoice(action, action == LayoutFragment.LayerReconciliationAction.MapToExisting ? Layer1 : null);
        var choices = new Dictionary<LayerKey, LayoutFragment.LayerReconciliationChoice> { [UnknownLayer] = choice };

        var result = LayoutFragment.ApplyReconciliation(shapes, [new LayerDef { Key = UnknownLayer }], choices);

        Assert.Equal(shapes.Count, result.Shapes.Count);
    }

    // ── Placement ────────────────────────────────────────────────────────────────

    [Fact]
    public void Translate_MovesEveryCoordinate_IncludingCubicControlPointsAndHoles_NeverMutatesInput()
    {
        var original = new CurveShape
        {
            Layer = Layer1,
            Xy = [0, 0, 100, 0],
            Edges = [new LayoutEdge { Kind = EdgeKind.Cubic, C1X = 10, C1Y = 20, C2X = 30, C2Y = 40 }],
        };

        var translated = LayoutFragment.Translate([original], 1_000, 2_000);

        var t = Assert.IsType<CurveShape>(Assert.Single(translated));
        Assert.Equal(1_000, t.Xy[0]);
        Assert.Equal(1_010, t.Edges![0].C1X);
        Assert.Equal(2_020, t.Edges![0].C1Y);

        // Original untouched.
        Assert.Equal(0, original.Xy[0]);
        Assert.Equal(10, original.Edges![0].C1X);
    }

    [Fact]
    public void Translate_ZeroDelta_StillClones_DoesNotAliasInput()
    {
        var original = new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 };
        var translated = LayoutFragment.Translate([original], 0, 0);

        var t = Assert.IsType<RectShape>(Assert.Single(translated));
        Assert.NotSame(original, t);
        Assert.Equal(100, t.X2);
    }
}
