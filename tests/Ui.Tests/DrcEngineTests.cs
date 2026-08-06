using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Drc;
using CircuitRF.Ui.Theming;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// L5b gate (docs/design/layout-view.md §9A): both rules fire correctly on a seeded layout and stay
/// silent on a clean one; markers locate the violating REGION; a waiver suppresses; spacing respects
/// nets so a single pour is never reported against itself.
/// </summary>
public class DrcEngineTests
{
    private static readonly LayerKey M1 = new(1, 0);
    private static readonly LayerKey M2 = new(2, 0);

    /// <param name="minWidth">DBU. 0 = no width rule.</param>
    /// <param name="minSpacing">DBU. 0 = no spacing rule.</param>
    private static Technology Tech(long minWidth = 100, long minSpacing = 100, LayerKey? layer = null)
    {
        var key = layer ?? M1;
        var t = new Technology
        {
            Name   = "Test",
            Layers = [new LayerDef { Key = key, Name = "M1", Color = new Rgba(200, 200, 200, 255) }],
        };
        if (minWidth   > 0) t.DrcRules.Add(new DrcRule { Name = "M1 min width",   Kind = DrcRuleKind.MinWidth,   Layer = key, ValueDbu = minWidth });
        if (minSpacing > 0) t.DrcRules.Add(new DrcRule { Name = "M1 min spacing", Kind = DrcRuleKind.MinSpacing, Layer = key, ValueDbu = minSpacing });
        return t;
    }

    private static RectShape Rect(long x1, long y1, long x2, long y2, LayerKey? layer = null, string? net = null)
        => new() { Layer = layer ?? M1, Net = net, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 };

    // ── Gate: both rules fire on a seeded layout ─────────────────────────────

    [Fact]
    public void MinWidth_NarrowTrace_IsReported_WithARegionMarkerNotAPoint()
    {
        // 60 DBU wide against a 100 DBU rule.
        var result = DrcEngine.Run([Rect(0, 0, 1000, 60)], Tech(minWidth: 100, minSpacing: 0));

        var v = Assert.Single(result.Violations);
        Assert.Equal(DrcRuleKind.MinWidth, v.Kind);
        Assert.Equal("M1 min width", v.RuleName);

        // §9A.1: "a geometric marker (the region that actually violates), not just a point".
        Assert.NotEmpty(v.MarkerRings);
        Assert.True(v.Marker.MaxX > v.Marker.MinX, "marker must have extent, not be a point");
        Assert.True(v.Marker.MaxY > v.Marker.MinY, "marker must have extent, not be a point");
    }

    [Fact]
    public void MinSpacing_TwoNetsTooClose_IsReported_AndTheMarkerSitsInTheGap()
    {
        // 40 DBU apart against a 100 DBU rule.
        var shapes = new List<LayoutShape>
        {
            Rect(0,   0, 100, 100, net: "A"),
            Rect(140, 0, 240, 100, net: "B"),
        };

        var result = DrcEngine.Run(shapes, Tech(minWidth: 0, minSpacing: 100));

        var v = Assert.Single(result.Violations);
        Assert.Equal(DrcRuleKind.MinSpacing, v.Kind);

        // The marker is centred on the GAP the user has to open, not parked on one conductor. It
        // reaches a little into both sides (each region is inflated by half the rule with ROUND
        // joins, so the overlap laps onto both edges) — which is what makes it read as "these two
        // things are too close" rather than "this one shape is wrong".
        long centre = (v.Marker.MinX + v.Marker.MaxX) / 2;
        Assert.InRange(centre, 100, 140);
        Assert.True(v.Marker.MinX > 0 && v.Marker.MaxX < 240,
            $"marker must not span either whole conductor; got {v.Marker.MinX}..{v.Marker.MaxX}");

        Assert.Contains(v.NetA, new[] { "A", "B" });
        Assert.Contains(v.NetB, new[] { "A", "B" });
        Assert.NotEqual(v.NetA, v.NetB);
    }

    // ── Gate: silent on a clean layout ───────────────────────────────────────

    [Fact]
    public void CleanLayout_ReportsNothing()
    {
        var shapes = new List<LayoutShape>
        {
            Rect(0,   0, 500, 500, net: "A"),   // comfortably wide
            Rect(700, 0, 1200, 500, net: "B"),  // 200 apart against a 100 rule
        };

        var result = DrcEngine.Run(shapes, Tech(minWidth: 100, minSpacing: 100));

        Assert.Empty(result.Violations);
        Assert.True(result.IsClean);
        Assert.Equal(2, result.RulesEvaluated);
    }

    /// <summary>
    /// A trace drawn EXACTLY at the minimum must pass. This is the commonest geometry on a board and
    /// the failure mode the engine's own erode-by-w/2-minus-one-DBU backoff exists to prevent — a
    /// check that fails every at-limit trace is one nobody runs twice.
    /// </summary>
    [Fact]
    public void MinWidth_TraceExactlyAtTheLimit_Passes()
    {
        var result = DrcEngine.Run([Rect(0, 0, 5000, 100)], Tech(minWidth: 100, minSpacing: 0));
        Assert.Empty(result.Violations);
    }

    /// <summary>The opposite half of the same boundary: a gap exactly at the rule must pass too.</summary>
    [Fact]
    public void MinSpacing_GapExactlyAtTheLimit_Passes()
    {
        var shapes = new List<LayoutShape>
        {
            Rect(0,   0, 100, 100, net: "A"),
            Rect(200, 0, 300, 100, net: "B"),   // exactly 100 apart
        };

        var result = DrcEngine.Run(shapes, Tech(minWidth: 0, minSpacing: 100));
        Assert.Empty(result.Violations);
    }

    /// <summary>
    /// R8a-shaped regression: an L-shaped conductor is a plain rectilinear corner, not a width
    /// violation. Opening with round joins would report four corner slivers on every shape drawn.
    /// </summary>
    [Fact]
    public void MinWidth_RectilinearCorner_IsNotAViolation()
    {
        var l = new PolygonShape
        {
            Layer = M1,
            Xy = [0, 0, 1000, 0, 1000, 300, 300, 300, 300, 1000, 0, 1000],
        };

        var result = DrcEngine.Run([l], Tech(minWidth: 200, minSpacing: 0));
        Assert.Empty(result.Violations);
    }

    // ── Curved geometry must not shed false positives ───────────────────────

    /// <summary>
    /// The bug this exists for, found by running a real process's rules over real artwork: Clipper2
    /// offsets onto the integer DBU grid, so erode-then-dilate is not an exact identity. On an
    /// axis-aligned rectangle the rounding is exact; on a FLATTENED CURVE every one of its many
    /// oblique vertices lands back a fraction of a DBU off, and the difference came back as a rash of
    /// sub-DBU slivers around the whole perimeter — reported as width violations on a shape twice the
    /// minimum width. **The count scaled with vertex count**, so the check was unusable on any layout
    /// containing circles, rounded corners, arcs or round-capped traces.
    /// </summary>
    [Theory]
    [InlineData(160)]      // exactly the rule across a diameter's worth of margin
    [InlineData(1000)]
    [InlineData(5000)]     // many vertices — this is the case that reported ~20 violations
    public void MinWidth_ACircleWiderThanTheRule_ReportsNothing(long radius)
    {
        var v = new CircleShape { Layer = M1, Cx = 0, Cy = 0, R = radius };
        Assert.Empty(DrcEngine.Run([v], Tech(minWidth: 160, minSpacing: 0)).Violations);
    }

    /// <summary>The same artifact reached every curved primitive, so every one is pinned.</summary>
    [Fact]
    public void MinWidth_RoundedRectAndRoundCappedPath_WiderThanTheRule_ReportNothing()
    {
        var tech = Tech(minWidth: 160, minSpacing: 0);

        var rrect = new RoundedRectShape { Layer = M1, X1 = 0, Y1 = 0, X2 = 4000, Y2 = 900, CornerRadius = 200 };
        Assert.Empty(DrcEngine.Run([rrect], tech).Violations);

        var path = new PathShape
        {
            Layer = M1, Xy = [0, 0, 3000, 0, 3000, 3000], Width = 600, End = PathEndStyle.Round,
        };
        Assert.Empty(DrcEngine.Run([path], tech).Violations);
    }

    /// <summary>
    /// The other half of the fix: absorbing the rounding must not absorb a real violation. A curved
    /// shape genuinely under the rule is still caught.
    /// </summary>
    [Fact]
    public void MinWidth_ACurvedShapeGenuinelyUnderTheRule_IsStillCaught()
    {
        var tech = Tech(minWidth: 160, minSpacing: 0);

        var circle = new CircleShape { Layer = M1, Cx = 0, Cy = 0, R = 60 };   // 120 across
        Assert.NotEmpty(DrcEngine.Run([circle], tech).Violations);

        var rrect = new RoundedRectShape { Layer = M1, X1 = 0, Y1 = 0, X2 = 4000, Y2 = 120, CornerRadius = 40 };
        Assert.NotEmpty(DrcEngine.Run([rrect], tech).Violations);
    }

    /// <summary>
    /// Pins the detection threshold the dilate overshoot could plausibly have widened. It did not:
    /// a trace 2 DBU under the rule is still reported, and one exactly at the rule still passes.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(10)]
    public void MinWidth_ATraceJustUnderTheRule_IsStillCaught(long under)
    {
        var trace = Rect(0, 0, 40_000, 160 - under);
        Assert.NotEmpty(DrcEngine.Run([trace], Tech(minWidth: 160, minSpacing: 0)).Violations);
    }

    /// <summary>
    /// The second half of the same finding, and the more dangerous one. A technology that states no
    /// flatten tolerance falls back to a fixed 1 µm — a sane default for DRAWING, and six times the
    /// minimum-width rule of a fine-geometry process. Measured before the cap existed: a via pad on
    /// such a technology flattened to a TRIANGLE, whose corners really are narrower than the rule, so
    /// the check reported three violations on a perfectly legal pad. **The `Tech()` helper here
    /// deliberately leaves `DefaultFlattenTolDbu` unset, so this is the fallback path.**
    /// </summary>
    [Fact]
    public void MinWidth_OnATechnologyStatingNoFlattenTolerance_StillFlattensFinelyEnoughToBeRight()
    {
        var tech = Tech(minWidth: 160, minSpacing: 0);
        Assert.Equal(0, tech.DefaultFlattenTolDbu);   // the fallback path is what is under test

        foreach (long r in new long[] { 160, 1000, 5000 })
            Assert.Empty(DrcEngine.Run([new CircleShape { Layer = M1, Cx = 0, Cy = 0, R = r }], tech).Violations);
    }

    /// <summary>A via pad is a curve too — this is the shape the bug was actually found on.</summary>
    [Fact]
    public void MinWidth_ALoneViaPadWiderThanTheRule_ReportsNothing()
    {
        var tech = Tech(minWidth: 160, minSpacing: 0);
        tech.Layers.Add(new LayerDef { Key = M2, Name = "Via" });

        var via = new ViaShape
        {
            Layer = M2, LandingLayer = M1, X = 0, Y = 0, PadSize = 320, DrillSize = 120,
        };

        Assert.Empty(DrcEngine.Run([via], tech).Violations);
    }

    /// <summary>Spacing's own threshold, on the parallel edges that actually occur on a layout.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(4, true)]
    public void MinSpacing_ParallelEdges_DetectAtOneDbuUnderTheRule(long under, bool expectViolation)
    {
        var shapes = new List<LayoutShape>
        {
            Rect(0, 0, 10_000, 1_000, net: "A"),
            Rect(0, 1_000 + 180 - under, 10_000, 3_000, net: "B"),
        };

        var result = DrcEngine.Run(shapes, Tech(minWidth: 0, minSpacing: 180));
        Assert.Equal(expectViolation, result.Violations.Count > 0);
    }

    // ── Gate: spacing respects nets (no false hits within one pour) ──────────

    [Fact]
    public void MinSpacing_TwoShapesOfTheSameNamedNet_AreNotReported()
    {
        var shapes = new List<LayoutShape>
        {
            Rect(0,   0, 100, 100, net: "GND"),
            Rect(140, 0, 240, 100, net: "GND"),   // 40 apart, same net
        };

        var result = DrcEngine.Run(shapes, Tech(minWidth: 0, minSpacing: 100));
        Assert.Empty(result.Violations);
    }

    /// <summary>
    /// The case that decides whether the check is usable on real artwork: a pour drawn as several
    /// OVERLAPPING unnamed rectangles is one conductor. Treating each unnamed shape as its own net
    /// would report a violation at every overlap — §9A.1's own named failure mode.
    /// </summary>
    [Fact]
    public void MinSpacing_OverlappingUnnamedShapes_AreOneConductor_NotAViolation()
    {
        var shapes = new List<LayoutShape>
        {
            Rect(0,   0, 500, 500),
            Rect(400, 0, 900, 500),   // overlaps the first
            Rect(800, 0, 1300, 500),  // overlaps the second
        };

        var result = DrcEngine.Run(shapes, Tech(minWidth: 0, minSpacing: 100));
        Assert.Empty(result.Violations);
    }

    /// <summary>
    /// The other half of the same rule: two DISJOINT unnamed regions are two conductors, so a
    /// too-small gap between them is still reported even though neither carries a net name. Treating
    /// all unnamed geometry as one net would silently pass every board drawn before nets are stamped.
    /// </summary>
    [Fact]
    public void MinSpacing_DisjointUnnamedShapes_AreSeparateConductors_AndAreReported()
    {
        var shapes = new List<LayoutShape>
        {
            Rect(0,   0, 100, 100),
            Rect(140, 0, 240, 100),   // 40 apart, neither named
        };

        var result = DrcEngine.Run(shapes, Tech(minWidth: 0, minSpacing: 100));

        var v = Assert.Single(result.Violations);
        Assert.Null(v.NetA);
        Assert.Null(v.NetB);
    }

    [Fact]
    public void MinSpacing_ShapesOnDifferentLayers_AreNeverCompared()
    {
        var tech = Tech(minWidth: 0, minSpacing: 100);
        tech.Layers.Add(new LayerDef { Key = M2, Name = "M2", Color = new Rgba(100, 100, 100, 255) });

        var shapes = new List<LayoutShape>
        {
            Rect(0,  0, 100, 100, M1, "A"),
            Rect(40, 0, 140, 100, M2, "B"),   // overlapping, but a different layer
        };

        Assert.Empty(DrcEngine.Run(shapes, tech).Violations);
    }

    // ── Gate: waivers ────────────────────────────────────────────────────────

    [Fact]
    public void Waiver_SuppressesTheViolation_ButKeepsItListedAndVisible()
    {
        var shapes = new List<LayoutShape> { Rect(0, 0, 1000, 60) };
        var tech   = Tech(minWidth: 100, minSpacing: 0);

        var first = DrcEngine.Run(shapes, tech);
        var key   = Assert.Single(first.Violations).Key;

        var waived = DrcEngine.Run(shapes, tech,
            [new DrcWaiver { Key = key, Reason = "deliberate taper", RuleName = "M1 min width" }]);

        // §9A.1: waiving must be "persisted, and visible" — the violation is still REPORTED, it just
        // no longer counts against the design.
        var v = Assert.Single(waived.Violations);
        Assert.True(v.Waived);
        Assert.Equal("deliberate taper", v.WaiverReason);
        Assert.True(waived.IsClean);
        Assert.Equal(0, waived.ErrorCount);
        Assert.Equal(1, waived.WaivedCount);
    }

    [Fact]
    public void ViolationKey_IsStableAcrossRuns_SoAWaiverKeepsMatching()
    {
        var shapes = new List<LayoutShape> { Rect(0, 0, 1000, 60), Rect(0, 500, 1000, 555) };
        var tech   = Tech(minWidth: 100, minSpacing: 0);

        var a = DrcEngine.Run(shapes, tech);
        var b = DrcEngine.Run(shapes, tech);

        Assert.Equal(a.Violations.Select(v => v.Key), b.Violations.Select(v => v.Key));
    }

    [Fact]
    public void WaiverForAnUnrelatedKey_SuppressesNothing()
    {
        var result = DrcEngine.Run([Rect(0, 0, 1000, 60)], Tech(minWidth: 100, minSpacing: 0),
            [new DrcWaiver { Key = "not-a-real-key", Reason = "", RuleName = "x" }]);

        Assert.Single(result.Violations);
        Assert.False(result.Violations[0].Waived);
        Assert.Equal(1, result.ErrorCount);
    }

    // ── Guards and reporting ────────────────────────────────────────────────

    [Fact]
    public void NoTechnology_ReportsWhyRatherThanRunningOrThrowing()
    {
        var result = DrcEngine.Run([Rect(0, 0, 100, 100)], tech: null);

        Assert.Empty(result.Violations);
        Assert.NotEmpty(result.Diagnostics);
        Assert.Null(result.TechnologyName);
    }

    [Fact]
    public void TechnologyWithNoRules_SaysSoRatherThanReportingClean()
    {
        var tech   = new Technology { Name = "Bare" };
        var result = DrcEngine.Run([Rect(0, 0, 100, 100)], tech);

        Assert.Empty(result.Violations);
        Assert.NotEmpty(result.Diagnostics);
        Assert.Equal("Bare", result.TechnologyName);
    }

    [Fact]
    public void AboveTheShapeCeiling_RefusesOutright_AndSaysSo()
    {
        var shapes = new List<LayoutShape>();
        for (int i = 0; i < 20; i++) shapes.Add(Rect(i * 1000, 0, i * 1000 + 100, 100));

        var result = DrcEngine.Run(shapes, Tech(), settings: new DrcRunSettings(MaxShapes: 5));

        Assert.Empty(result.Violations);
        Assert.Contains(result.Diagnostics, d => d.Contains("ceiling"));
    }

    /// <summary>Labels and reference images carry no manufacturable area — see BitmapShape's own
    /// doc comment, which has promised "L5b: skipped by DRC" since the bitmap phase.</summary>
    [Fact]
    public void LabelsAndBitmaps_AreNotChecked_AndTheSkipIsReported()
    {
        var shapes = new List<LayoutShape>
        {
            Rect(0, 0, 500, 500),
            new LabelShape  { Layer = M1, X = 100, Y = 100, Text = "hello", Height = 50 },
            new BitmapShape { Layer = M1, X = 0, Y = 0, W = 100, H = 100 },
        };

        var result = DrcEngine.Run(shapes, Tech());

        Assert.Empty(result.Violations);
        Assert.Equal(1, result.ShapesChecked);
        Assert.Contains(result.Diagnostics, d => d.Contains("manufacturable"));
    }

    [Fact]
    public void Results_AreOrderedDeterministically()
    {
        var shapes = new List<LayoutShape>
        {
            Rect(5000, 0, 6000, 60),
            Rect(0,    0, 1000, 60),
            Rect(2000, 0, 3000, 60),
        };
        var tech = Tech(minWidth: 100, minSpacing: 0);

        var xs = DrcEngine.Run(shapes, tech).Violations.Select(v => v.Marker.MinX).ToList();
        Assert.Equal(xs.OrderBy(x => x).ToList(), xs);
    }

    /// <summary>
    /// R-via-9's decomposition, reused: a via's PAD is metal on its landing layer, so a via placed
    /// too close to a trace on that layer is a real spacing violation. Deriving this differently from
    /// the exporters would make DRC and export disagree about what a via is.
    /// </summary>
    [Fact]
    public void Via_IsCheckedAsMetalOnItsLandingLayer()
    {
        var tech = Tech(minWidth: 0, minSpacing: 200);

        var shapes = new List<LayoutShape>
        {
            Rect(0, 0, 100, 100, M1, "A"),
            new ViaShape { Layer = M2, LandingLayer = M1, Net = "B", X = 150, Y = 50, PadSize = 60, DrillSize = 30 },
        };

        var v = Assert.Single(DrcEngine.Run(shapes, tech).Violations);
        Assert.Equal(DrcRuleKind.MinSpacing, v.Kind);
        Assert.Equal(M1, v.Layer);
    }
}
