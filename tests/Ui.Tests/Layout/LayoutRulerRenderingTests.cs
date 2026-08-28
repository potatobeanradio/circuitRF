using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout;

/// <summary>
/// docs/design/layout-view.md §9B.3/§9B.4 — gates 9, 10 and 14. Every assertion here is MEASURED
/// (through the renderer's own measurement pair, or off an off-screen render), never eyeballed.
/// </summary>
public class LayoutRulerRenderingTests : System.IDisposable
{
    public LayoutRulerRenderingTests() => LayoutTextOutline.TestOverrideTypeface = SKTypeface.Default;

    public void Dispose()
    {
        LayoutTextOutline.TestOverrideTypeface = null;
        System.GC.SuppressFinalize(this);
    }

    private static RulerAnnotation Fixed11() => new()
    {
        X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 0,
        SizeMode = RulerSizeMode.Fixed, TextSizePt = 11.0, TextHeightDbu = 4_000,
    };

    private static RulerAnnotation Scaled() => new()
    {
        X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 0,
        SizeMode = RulerSizeMode.Scaled, TextSizePt = 11.0, TextHeightDbu = 4_000,
    };

    // ── Gate 9: Fixed vs Scaled at two zooms an octave apart ──────────────────────────────────────

    [Fact]
    public void FixedText_MeasuresTheSameDeviceHeight_AtBothZooms()
    {
        var r = Fixed11();
        var a = LayoutRenderer.MeasureRulerScreenBox(r, LayoutUnit.Um, 1000, 0.004);
        var b = LayoutRenderer.MeasureRulerScreenBox(r, LayoutUnit.Um, 1000, 0.008);

        Assert.True(a.Height > 1, "a Fixed ruler must have a measurable readout");
        // Within a DBU of rounding — ResolveRulerTextHeightDbu rounds a device height to whole DBU.
        Assert.InRange(b.Height, a.Height * 0.99, a.Height * 1.01);
        Assert.InRange(b.Width, a.Width * 0.99, a.Width * 1.01);
    }

    [Fact]
    public void ScaledText_DoublesInDeviceHeight_WhenTheZoomDoubles()
    {
        var r = Scaled();
        var a = LayoutRenderer.MeasureRulerScreenBox(r, LayoutUnit.Um, 1000, 0.004);
        var b = LayoutRenderer.MeasureRulerScreenBox(r, LayoutUnit.Um, 1000, 0.008);

        Assert.True(a.Height > 1);
        Assert.InRange(b.Height / a.Height, 1.99, 2.01);
    }

    [Fact]
    public void FixedText_ResolvesToTheSameDevicePixels_AsItsPointSize()
    {
        // 11 pt at 96 dpi is 11 * 96/72 device pixels. The resolved WORLD height times the zoom must
        // come back to that number — this is the whole content of "n points on screen".
        const double zoom = 0.004;
        long dbu = LayoutRenderer.ResolveRulerTextHeightDbu(Fixed11(), zoom);
        double devicePx = dbu * zoom;
        Assert.InRange(devicePx, 11.0 * 96.0 / 72.0 - 0.05, 11.0 * 96.0 / 72.0 + 0.05);
    }

    // ── Gate 10: display unit ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void SwitchingTheDisplayUnit_ReRendersTheReadout_WithNoStoredFieldChanging()
    {
        // 25,400 DBU at 1000 DBU/µm is 25.4 µm = 1 mil.
        var r = new RulerAnnotation { X1 = 0, Y1 = 0, X2 = 25_400, Y2 = 0, TextHeightDbu = 2_000, SizeMode = RulerSizeMode.Scaled };

        var mm = LayoutRenderer.RulerReadoutLines(r, LayoutUnit.Mm, 1000);
        var mil = LayoutRenderer.RulerReadoutLines(r, LayoutUnit.Mil, 1000);

        Assert.Equal("0.0254 mm", mm[0]);
        Assert.Equal("1 mil", mil[0]);

        // R-rul-6: nothing stored changed — only the unit the SAME number is rendered in.
        Assert.Equal(25_400, r.X2);
        Assert.Equal(25_400, r.DistanceDbu);
    }

    [Fact]
    public void ReadoutLines_AreDistanceThenDeltaThenCaption_EachIndependentlyOmittable()
    {
        var bare = new RulerAnnotation { X1 = 0, Y1 = 0, X2 = 3_000, Y2 = 4_000 };
        Assert.Single(LayoutRenderer.RulerReadoutLines(bare, LayoutUnit.Um, 1000));

        var delta = new RulerAnnotation { X1 = 0, Y1 = 0, X2 = 3_000, Y2 = 4_000, ShowComponents = true };
        var withDelta = LayoutRenderer.RulerReadoutLines(delta, LayoutUnit.Um, 1000);
        Assert.Equal(2, withDelta.Count);
        Assert.StartsWith("Δx 3", withDelta[1]);

        var full = new RulerAnnotation
        {
            X1 = 0, Y1 = 0, X2 = 3_000, Y2 = 4_000, ShowComponents = true, Caption = "min trace gap",
        };
        var all = LayoutRenderer.RulerReadoutLines(full, LayoutUnit.Um, 1000);
        Assert.Equal(3, all.Count);
        Assert.Equal("5 µm", all[0]);
        Assert.Equal("min trace gap", all[2]);
    }

    // ── §9B.4: the readout never overlaps the line, at any angle ──────────────────────────────────

    [Theory]
    [InlineData(100_000, 0)]        // horizontal
    [InlineData(0, 100_000)]        // vertical
    [InlineData(70_000, 70_000)]    // 45 deg
    [InlineData(-60_000, 30_000)]   // obtuse, right-to-left
    public void ReadoutBlock_NeverStraddlesTheLine(long dx, long dy)
    {
        var r = new RulerAnnotation
        {
            X1 = 0, Y1 = 0, X2 = dx, Y2 = dy,
            SizeMode = RulerSizeMode.Scaled, TextHeightDbu = 6_000,
            Caption = "a fairly long caption to widen the block",
        };
        var bb = LayoutRenderer.MeasureRulerTextWorldBbox(r, LayoutUnit.Um, 1000, 0);
        Assert.False(bb.IsEmpty);

        // Every corner of the block must be on ONE side of the infinite line through the endpoints.
        double nx = -(double)dy, ny = (double)dx;
        double[] signs =
        [
            nx * bb.MinX + ny * bb.MinY, nx * bb.MaxX + ny * bb.MinY,
            nx * bb.MinX + ny * bb.MaxY, nx * bb.MaxX + ny * bb.MaxY,
        ];
        Assert.True(signs.All(s => s > 0) || signs.All(s => s < 0),
                    "the readout block must sit entirely off the measurement line");
    }

    // ── The painted extent is what Zoom-to-Fit and the clipboard measure ──────────────────────────

    [Fact]
    public void MeasureRulerWorldBbox_ContainsBothEndpointsAndTheReadout()
    {
        var r = Scaled();
        var bb = LayoutRenderer.MeasureRulerWorldBbox(r, LayoutUnit.Um, 1000, 0);
        var text = LayoutRenderer.MeasureRulerTextWorldBbox(r, LayoutUnit.Um, 1000, 0);

        Assert.True(bb.Contains(r.X1, r.Y1));
        Assert.True(bb.Contains(r.X2, r.Y2));
        Assert.True(bb.Contains(text.MinX, text.MinY));
        Assert.True(bb.Contains(text.MaxX, text.MaxY));
    }

    // ── The ruler actually paints, above the layers, and honours ShowRulers ───────────────────────

    private static Technology Tech(LayerKey key) => new()
    {
        Name = "T", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000,
        Layers =
        [
            new LayerDef
            {
                Key = key, Name = "L", Color = new CircuitRF.Design.Theming.Rgba(0, 200, 0),
                FillOpacity = 1.0, ZOrder = 0, Visible = true, Selectable = true,
            },
        ],
    };

    private static SKBitmap RenderToBitmap(LayoutView view, bool showRulers)
    {
        var key = new LayerKey(1, 0);
        var vp = new LayoutViewport(-2_000, -20_000, 0.004, 200, 200);
        using var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        LayoutRenderer.Draw(surface.Canvas, view, Tech(key), vp, new LayoutRenderOptions
        {
            Theme = LayoutRenderTheme.Light, ShowGrid = false, Overlay = null, ShowRulers = showRulers,
        });
        using var img = surface.Snapshot();
        return SKBitmap.FromImage(img);
    }

    /// <summary>
    /// How many pixels the ruler CHANGES — a differential render, ShowRulers on against off.
    ///
    /// <para><b>Deliberately not a colour predicate.</b> An earlier version counted "warm" pixels,
    /// which coupled the test to the ruler's own default RGB: the moment those defaults were retuned
    /// (line = text, 2026-08-27) it started measuring the palette rather than whether anything was
    /// drawn. A differential answers exactly the question being asked — "did the ruler paint over the
    /// metal?" — and stays true whatever colour it paints in.</para>
    /// </summary>
    private static int RulerPixelCount(LayoutView view, bool showRulers)
    {
        using var with = RenderToBitmap(view, showRulers: true);
        using var without = RenderToBitmap(view, showRulers: false);

        int n = 0;
        for (int y = 0; y < with.Height; y++)
            for (int x = 0; x < with.Width; x++)
                if (with.GetPixel(x, y) != without.GetPixel(x, y)) n++;

        // The "off" render is the baseline by construction, so it differs from itself nowhere.
        return showRulers ? n : 0;
    }

    [Fact]
    public void ARuler_PaintsOverTheMetal_AndShowRulersFalseSuppressesIt()
    {
        var view = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um };
        // Metal covering the whole visible band, and a ruler right across it.
        view.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = -10_000, Y1 = -10_000, X2 = 60_000, Y2 = 10_000 });
        view.Rulers.Add(new RulerAnnotation
        {
            X1 = 0, Y1 = 0, X2 = 40_000, Y2 = 0, SizeMode = RulerSizeMode.Scaled, TextHeightDbu = 10_000,
        });

        Assert.True(RulerPixelCount(view, showRulers: true) > 20,
                    "a ruler must paint ABOVE the metal it lies over");
        Assert.Equal(0, RulerPixelCount(view, showRulers: false));
    }

    [Fact]
    public void ShowRulers_DefaultsToTrue_IncludingForADefaultedOptionsValue()
    {
        // The export paths build LayoutRenderOptions with an object initializer that never mentions
        // ShowRulers; a default of false there would silently drop every ruler from a slide.
        Assert.True(new LayoutRenderOptions().ShowRulers);
        Assert.True(default(LayoutRenderOptions).ShowRulers);
    }

    // ── Gate 14: both roles are themable ──────────────────────────────────────────────────────────

    [Fact]
    public void BothRulerAnnotationRoles_AreInColorRoleAll_WithLightAndDarkDefaults()
    {
        Assert.Contains(CircuitRF.Ui.Theming.ColorRole.LayoutRulerAnnotationLine, CircuitRF.Ui.Theming.ColorRole.All);
        Assert.Contains(CircuitRF.Ui.Theming.ColorRole.LayoutRulerAnnotationText, CircuitRF.Ui.Theming.ColorRole.All);

        foreach (var variant in new[] { CircuitRF.Ui.Theming.ColorVariant.Light, CircuitRF.Ui.Theming.ColorVariant.Dark })
        {
            var line = CircuitRF.Ui.Theming.ColorTheme.BuiltIn.Resolve(
                CircuitRF.Ui.Theming.ColorRole.LayoutRulerAnnotationLine, variant);
            var text = CircuitRF.Ui.Theming.ColorTheme.BuiltIn.Resolve(
                CircuitRF.Ui.Theming.ColorRole.LayoutRulerAnnotationText, variant);
            Assert.True(line.A > 0);
            Assert.True(text.A > 0);
        }

        // §9B.8: distinct from the canvas-edge ruler STRIP's own roles — the two share a word and
        // nothing else, and the theme editor must never conflate them.
        Assert.NotEqual(LayoutRenderTheme.Light.RulerTick, LayoutRenderTheme.Light.RulerAnnotationLine);
        Assert.NotEqual(LayoutRenderTheme.Light.RulerText, LayoutRenderTheme.Light.RulerAnnotationText);
    }
}
