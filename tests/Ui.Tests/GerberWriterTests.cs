using System.Text;
using CircuitRF.Ui.Layout;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Tests;

/// <summary>Gates 2/3/4/5/6/10 from brief-L4c-gerber-export.md — the per-layer RS-274X/X2 writer.</summary>
public class GerberWriterTests
{
    private static readonly GerberFormat Format = GerberUnits.Resolve(1000);

    private static string WriteToText(IReadOnlyList<LayoutShape> shapes, LayerDef? layerDef = null, Technology? tech = null)
    {
        using var ms = new MemoryStream();
        GerberWriter.Write(ms, layerDef, shapes, Format, tech, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        return Encoding.ASCII.GetString(ms.ToArray());
    }

    // ── Gate 2: coordinates are exact ─────────────────────────────────────────

    [Fact]
    public void RectShape_CoordinatesAppearAsLiteralDbuIntegers()
    {
        var text = WriteToText([new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1_234_500, Y2 = 987_650 }]);

        Assert.Contains("X0Y0D02*", text);
        Assert.Contains("X1234500Y0D01*", text);
        Assert.Contains("X1234500Y987650D01*", text);
        Assert.Contains("X0Y987650D01*", text);
    }

    [Fact]
    public void NegativeCoordinates_EmitExplicitSign()
    {
        var text = WriteToText([new RectShape { Layer = new LayerKey(1, 0), X1 = -500_000, Y1 = -250_000, X2 = 0, Y2 = 0 }]);
        Assert.Contains("X-500000Y-250000D01*", text);
    }

    // ── Gate 3: arcs — G75 always present; G02/G03 + I/J relative to start point ─────────────────

    [Fact]
    public void AnyGeometry_AlwaysEmitsG75BeforeBody()
    {
        var text = WriteToText([new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10, Y2 = 10 }]);
        int g75 = text.IndexOf("G75*", StringComparison.Ordinal);
        int firstDraw = text.IndexOf("D01*", StringComparison.Ordinal);
        Assert.True(g75 >= 0);
        Assert.True(g75 < firstDraw);
    }

    [Fact]
    public void CcwArcEdge_EmitsG03_WithIJRelativeToStartPoint()
    {
        // A quarter-circle from (1000,0) to (0,1000), bulging away from the origin so the arc's
        // own center lands at (0,0) exactly (both endpoints are exactly radius 1000 from the origin) —
        // the same construction RoundedRectRing's kappa constant already relies on.
        const double kappa = 0.41421356237309515;
        var curve = new CurveShape
        {
            Layer = new LayerKey(1, 0),
            Xy = [1000, 0, 0, 1000, -1000, -1000],
            Edges =
            [
                new LayoutEdge { Kind = EdgeKind.Arc, Bulge = kappa },
                new LayoutEdge { Kind = EdgeKind.Line },
                new LayoutEdge { Kind = EdgeKind.Line },
            ],
        };

        var arc = LayoutArc.FromBulge(1000, 0, 0, 1000, kappa);
        Assert.True(arc.Sweep > 0); // sanity: this bulge sign is CCW, per LayoutArc's own convention

        long i = (long)Math.Round(arc.Cx) - 1000;
        long j = (long)Math.Round(arc.Cy) - 0;

        var text = WriteToText([curve]);
        Assert.Contains("G03*", text);
        Assert.DoesNotContain("G02*", text);
        Assert.Contains($"X0Y1000I{i}J{j}D01*", text);
    }

    [Fact]
    public void CwArcEdge_EmitsG02_NotG03()
    {
        const double kappa = -0.41421356237309515; // opposite sign — sweeps the other way (CW)
        var curve = new CurveShape
        {
            Layer = new LayerKey(1, 0),
            Xy = [1000, 0, 0, 1000, -1000, -1000],
            Edges =
            [
                new LayoutEdge { Kind = EdgeKind.Arc, Bulge = kappa },
                new LayoutEdge { Kind = EdgeKind.Line },
                new LayoutEdge { Kind = EdgeKind.Line },
            ],
        };

        var arc = LayoutArc.FromBulge(1000, 0, 0, 1000, kappa);
        Assert.True(arc.Sweep < 0);

        var text = WriteToText([curve]);
        Assert.Contains("G02*", text);
        Assert.DoesNotContain("G03*", text);
    }

    // ── Gate 4: holes — one dark region, one clear region per hole ───────────────────────────────

    [Fact]
    public void PolygonWithTwoHoles_OneDarkRegionTwoClearRegions()
    {
        var poly = new PolygonShape
        {
            Layer = new LayerKey(1, 0),
            Xy = [0, 0, 10_000, 0, 10_000, 10_000, 0, 10_000],
            Holes =
            [
                [1000, 1000, 2000, 1000, 2000, 2000, 1000, 2000],
                [5000, 5000, 6000, 5000, 6000, 6000, 5000, 6000],
            ],
        };

        var text = WriteToText([poly]);
        Assert.Equal(2, Count(text, "%LPC*%"));
        // One %LPD*% before the outer boundary, one more restoring dark polarity after the holes.
        Assert.Equal(2, Count(text, "%LPD*%"));
        Assert.Equal(3, Count(text, "G36*")); // 1 outer + 2 holes
        Assert.Equal(3, Count(text, "G37*"));
    }

    // ── Gate 5: path end styles ────────────────────────────────────────────────────────────────────

    [Fact]
    public void RoundEndPath_WritesD01StrokeWithCircularAperture_NoRegion()
    {
        var path = new PathShape
        {
            Layer = new LayerKey(1, 0),
            Xy = [0, 0, 10_000, 0],
            Width = 2_000_000, // 2 mm
            End = PathEndStyle.Round,
        };

        var text = WriteToText([path]);
        Assert.Contains("%ADD10C,2.000000*%", text);
        Assert.Contains("D10*", text); // aperture select
        Assert.Contains("X0Y0D02*", text);
        Assert.Contains("X10000Y0D01*", text);
        Assert.DoesNotContain("G36*", text);
    }

    [Theory]
    [InlineData(PathEndStyle.Flush)]
    [InlineData(PathEndStyle.Square)]
    [InlineData(PathEndStyle.Extended)]
    public void NonRoundEndPath_WritesRegionOutline_NotAStroke(PathEndStyle end)
    {
        var path = new PathShape
        {
            Layer = new LayerKey(1, 0),
            Xy = [0, 0, 10_000, 0],
            Width = 2_000,
            End = end,
        };

        var text = WriteToText([path]);
        Assert.Contains("G36*", text);
        Assert.Contains("G37*", text);
        Assert.DoesNotContain("D03*", text);
    }

    // ── Gate 6: circles flash; aperture table deduped ────────────────────────────────────────────

    [Fact]
    public void CirclesSameDiameter_ShareOneApertureDefine_EachFlashesWithD03()
    {
        var shapes = new List<LayoutShape>
        {
            new CircleShape { Layer = new LayerKey(1, 0), Cx = 0, Cy = 0, R = 250_000 },
            new CircleShape { Layer = new LayerKey(1, 0), Cx = 500_000, Cy = 500_000, R = 250_000 },
        };

        var text = WriteToText(shapes);
        Assert.Equal(1, Count(text, "%ADD10C,"));
        Assert.Equal(2, Count(text, "D03*"));
        Assert.Contains("X0Y0D03*", text);
        Assert.Contains("X500000Y500000D03*", text);
    }

    [Fact]
    public void ViaShape_FlashesPadDiameterAsCircularAperture()
    {
        var via = new ViaShape { Layer = new LayerKey(1, 0), X = 1000, Y = 2000, PadSize = 600_000, DrillSize = 300_000 };
        var text = WriteToText([via]);
        Assert.Contains("%ADD10C,0.600000*%", text);
        Assert.Contains("X1000Y2000D03*", text);
    }

    // ── Gate 10: X2 attributes ────────────────────────────────────────────────────────────────────

    [Fact]
    public void FileFunctionAndPolarityAndSoftwareAttributes_Emitted()
    {
        var layerDef = new LayerDef { Key = new LayerKey(1, 0), Name = "Top Copper", Color = new Rgba(0, 0, 0), Interchange = new InterchangeMapping(null, null, null, "GTL", "Copper,L1,Top") };
        var text = WriteToText([new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10, Y2 = 10 }], layerDef);

        Assert.Contains("%TF.FileFunction,Copper,L1,Top*%", text);
        Assert.Contains("%TF.FilePolarity,Positive*%", text);
        Assert.Contains("%TF.GenerationSoftware,circuitRF,", text);
        Assert.Contains("%TF.CreationDate,2026-01-01T00:00:00Z*%", text);
        Assert.Contains("%MOMM*%", text);
        Assert.Contains($"%FSLAX{Format.DigitPair}Y{Format.DigitPair}*%", text);
    }

    [Fact]
    public void NoInterchangeMapping_OmitsFileFunction_StillWritesGeometry()
    {
        var text = WriteToText([new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10, Y2 = 10 }]);
        Assert.DoesNotContain("%TF.FileFunction", text);
        Assert.Contains("%TF.FilePolarity,Positive*%", text);
    }

    [Fact]
    public void NetAttribute_EmittedWhenShapeCarriesNet_ClearedWhenNext_ShapeHasNone()
    {
        var shapes = new List<LayoutShape>
        {
            new RectShape { Layer = new LayerKey(1, 0), Net = "GND", X1 = 0, Y1 = 0, X2 = 10, Y2 = 10 },
            new RectShape { Layer = new LayerKey(1, 0), X1 = 20, Y1 = 0, X2 = 30, Y2 = 10 },
        };

        var text = WriteToText(shapes);
        Assert.Contains("%TO.N,GND*%", text);
        Assert.Contains("%TD.N*%", text);
        // %TO.N,GND*% must precede %TD.N*% (net set, then cleared before the second shape).
        Assert.True(text.IndexOf("%TO.N,GND*%", StringComparison.Ordinal) < text.IndexOf("%TD.N*%", StringComparison.Ordinal));
    }

    // ── Guardrail: labels/bitmaps must never reach the writer directly ───────────────────────────

    [Fact]
    public void LabelShape_ThrowsNotSupported_MustBeConvertedUpstream()
    {
        Assert.Throws<NotSupportedException>(() => WriteToText([new LabelShape { Layer = new LayerKey(1, 0), Text = "X", Height = 100 }]));
    }

    [Fact]
    public void BitmapShape_ThrowsNotSupported_MustBeFilteredUpstream()
    {
        Assert.Throws<NotSupportedException>(() => WriteToText([new BitmapShape { Layer = new LayerKey(1, 0), ImagePathRef = "x.png", W = 10, H = 10 }]));
    }

    private static int Count(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0) { count++; idx += needle.Length; }
        return count;
    }
}
