// ================================================================
//  MapLegendTests.cs — brief-railrf-21-two-numbers-one-name.md §4, gates 7-9
//
//  ── AN UNREADABLE LEGEND IS WORSE THAN NO LEGEND ────────────────────────────────────────────
//
//  R-rail18-4 fixed the colour bar's three labels OVERLAPPING on a small canvas — "a box in DBU
//  with text in points" — by shrinking them to fit, with no floor. On a real board's pane that
//  produced a caption of about six pixels and end labels barely a pixel tall: "text is not
//  readable in the scale, unless using heavy zoom" (2026-09-20).
//
//  Brief 21 reverses the half of R-rail18-4 that said the caption may never be dropped. Its own
//  reasoning had already stopped being true: the caption carries the MODEL KIND, and a six-pixel
//  caption names the model to nobody while taking the space the two end labels need. So, in order:
//  all three at whatever size fits and never below the floor; then the caption dropped and the end
//  labels kept AT the floor; then nothing at all. Never scaled below the floor at any step.
//
//  ── ONE TEST PER CLAIM ──────────────────────────────────────────────────────────────────────
//
//  The ladder that gates the OVERLAP rule is RailBoardViewTests' and stays there; what is here is
//  the floor, the drop, and the fact that the export gets both by construction rather than by a
//  second fix.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Render;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class MapLegendTests(ITestOutputHelper output)
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;   // 1000 DBU/µm
    private static readonly LayerKey Top = new(1, 0);

    private static long Mm(double v) => (long)Math.Round(v * 1e3 * Dbu);

    // ══ gate 7 — R-rail21-3a: the floor ═══════════════════════════════════════════════════════

    /// <summary>
    /// <b>At every canvas width a pane can be, the legend's text is at or above the floor.</b>
    /// </summary>
    /// <remarks>
    /// <b>The teeth are the last assertion.</b> At HEAD the same three widths produced text well
    /// under the floor — that is the reported defect — so the test reports what the old arithmetic
    /// would have chosen at each width and requires at least one of them to have been unreadable.
    /// Without that a floor set below anything the renderer ever produces would pass.
    /// </remarks>
    [Theory]
    [InlineData(200)]
    [InlineData(400)]
    [InlineData(1200)]
    public void LegendTextIsNeverDrawnBelowTheReadableFloor(int widthPx)
    {
        var (legend, layout, bar, baseline) = LayOut(widthPx);

        output.WriteLine($"{widthPx} px: drawn={layout.Drawn} caption={layout.CaptionDrawn} " +
                         $"size={layout.TextSizePx:0.##} px (floor {RailMapRenderer.LegendFloorPx}) " +
                         $"bar={bar.Width:0.#} band={baseline - bar.Bottom:0.#}");

        Assert.True(layout.Drawn, $"nothing was drawn at {widthPx} px, which no pane width should do.");
        Assert.True(layout.TextSizePx >= RailMapRenderer.LegendFloorPx,
                    $"{layout.TextSizePx:0.##} px is under the {RailMapRenderer.LegendFloorPx} px floor.");

        // What the pre-brief arithmetic would have chosen here: the fit, with no floor under it,
        // in the plate the WORLD box alone gave — which is the pair of changes together.
        float unfloored = Unfloored(legend, widthPx);
        output.WriteLine($"   before the floor it would have been {unfloored:0.##} px");
        // The narrowest rung is the one that HAS to have been unreadable before; the wider two are
        // reported rather than asserted, because a pane wide enough to fit the text honestly needs
        // no floor and gating on that would be gating the fixture's caption length.
        Assert.True(unfloored < RailMapRenderer.LegendFloorPx,
                    $"at {widthPx} px the old arithmetic already gave {unfloored:0.##} px, so this "
                  + "rung is not exercising the floor.");
    }

    // ══ gate 8 — R-rail21-3b: it drops rather than shrinks ════════════════════════════════════

    /// <summary>
    /// <b>On a canvas too small for all three at the floor, the caption goes and the end labels
    /// stay at the floor — and on one too small for those, nothing is drawn.</b>
    /// </summary>
    [Fact]
    public void WhereItCannotFitAtTheFloorItDropsTextRatherThanShrinkingIt()
    {
        Assert.True(TryLayOut(120, out var small, out _, out _, out _),
                    "the plate collapsed at 120 px, so this test says nothing about dropping text.");

        Assert.False(small.CaptionDrawn, "the caption survived a 120 px canvas, so nothing was dropped.");
        Assert.True(small.Caption.Width == 0, "a dropped caption still has a box.");
        Assert.True(small.TextSizePx >= RailMapRenderer.LegendFloorPx,
                    $"the end labels were shrunk to {small.TextSizePx:0.##} px instead of the "
                  + "caption being dropped.");

        // …and the end of the ladder: a plate with no room for two numbers at the floor draws
        // nothing at all, rather than a coloured box where the answer would be.
        bool plate = TryLayOut(36, out var tiny, out _, out _, out _);
        output.WriteLine($"36 px: plate={plate} drawn={(plate ? tiny.Drawn : false)}");
        Assert.False(plate && tiny.Drawn,
                     "a plate with no room for either end label still drew a legend.");
    }

    // ══ gate 9 — R-rail21-3c: the export gets it by construction ══════════════════════════════

    /// <summary>
    /// <b>The floor lives in the renderer, so every exported picture has it without a second
    /// fix</b> — and there is no second legend layout anywhere for it to be missing from.
    /// </summary>
    /// <remarks>
    /// The structural half is the real gate: <c>RailGraphicExport</c> and <c>RailReportPage</c>
    /// draw the map by calling <c>RailMapRenderer.Draw</c>, and <c>LayOutLabels</c> has exactly one
    /// production call site. A renderer-side floor therefore cannot be absent from an export unless
    /// somebody writes a second layout, which is what this scan would catch.
    ///
    /// <para>The rendered half is the same scene through Skia's SVG device at two canvas sizes: the
    /// caption is in the text of the large one and absent from the small one, drawn through exactly
    /// the call the export makes.</para>
    /// </remarks>
    [Fact]
    public void TheExportedPictureGetsTheFloorFromTheRendererRatherThanASecondFix()
    {
        string src = Directory.GetCurrentDirectory();
        while (!Directory.Exists(Path.Combine(src, "src"))) src = Path.GetDirectoryName(src)!;

        var callers = Directory
            .EnumerateFiles(Path.Combine(src, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => StripComments(File.ReadAllText(f)).Contains("LayOutLabels(", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f))
            .ToList();

        Assert.Equal(["RailMapRenderer.cs"], callers);

        var scene = Scene();
        Assert.Contains(scene.Legend!.Caption, TextOf(SvgOf(scene, 1200, 900)), StringComparison.Ordinal);
        Assert.DoesNotContain(scene.Legend.Caption, TextOf(SvgOf(scene, 120, 90)), StringComparison.Ordinal);
    }

    // ══ fixtures ═════════════════════════════════════════════════════════════════════════════

    /// <summary>The legend the renderer would lay out on a canvas of that width, and the geometry
    /// it laid it out in — <c>DrawLegend</c>'s own two calls, in its own order.</summary>
    private static (RailMapLegend Legend, RailMapLabelLayout Layout, SKRect Bar, float Baseline)
        LayOut(int widthPx)
    {
        Assert.True(TryLayOut(widthPx, out var layout, out var legend, out var bar, out float baseline),
                    $"the plate collapsed to nothing at {widthPx} px.");
        return (legend!, layout, bar, baseline);
    }

    private static bool TryLayOut(
        int widthPx, out RailMapLabelLayout layout, out RailMapLegend? legend,
        out SKRect bar, out float baseline)
    {
        var scene = Scene();
        legend = scene.Legend!;
        layout = default;

        var vp = LayoutViewport.ZoomToFit(scene.Bounds, widthPx, Math.Max(40, widthPx * 3 / 4));
        if (!RailMapRenderer.TryPlate(legend, vp, out _, out bar, out baseline)) return false;

        layout = RailMapRenderer.LayOutLabels(legend, bar.Left, bar.Right, baseline,
                                              baseline - bar.Bottom);
        return true;
    }

    /// <summary>
    /// What the renderer chose BEFORE this brief: the fit with no floor under it, inside the plate
    /// the world box alone gave — both halves of the old behaviour, written out here rather than
    /// read back out of the new code, so this is a comparison and not a tautology.
    /// </summary>
    private static float Unfloored(RailMapLegend legend, int widthPx)
    {
        var scene = Scene();
        var vp = LayoutViewport.ZoomToFit(scene.Bounds, widthPx, Math.Max(40, widthPx * 3 / 4));

        // The old TryPlate: the world box mapped to screen, with no minimum of any kind.
        float x0 = (float)vp.WorldToScreenX(legend.Box.MinX);
        float x1 = (float)vp.WorldToScreenX(legend.Box.MaxX);
        float y0 = (float)vp.WorldToScreenY(legend.Box.MaxY);
        float y1 = (float)vp.WorldToScreenY(legend.Box.MinY);

        float pad = (y1 - y0) * 0.12f;
        float available = Math.Max(0f, (x1 - pad) - (x0 + pad));
        float band = (y1 - pad) - (y0 + (y1 - y0) * 0.5f);

        using var regular  = new SKFont(SkiaFonts.PlexRegular,  RailMapRenderer.LegendSizePx);
        using var semibold = new SKFont(SkiaFonts.PlexSemiBold, RailMapRenderer.LegendSizePx);

        float perSide = Math.Max(regular.MeasureText(legend.ColdLabel),
                                 regular.MeasureText(legend.HotLabel))
                      + semibold.MeasureText(legend.Caption) / 2f
                      + RailMapRenderer.LegendSizePx * 0.6f;

        float size = perSide > 0 ? RailMapRenderer.LegendSizePx * (available / 2f / perSide)
                                 : RailMapRenderer.LegendSizePx;
        return band > 0 && size > band ? band : size;
    }

    /// <summary>A |Z| map with a caption of the length a real one has.</summary>
    private static RailMapScene Scene()
    {
        var cells = new List<PdnCellRef>();
        var map = new List<PdnPlaneMapCell>();

        for (int i = 0; i < 8; i++)
        {
            var cell = new PdnCellRef(Top, i, 0, Mm(2 + 4 * i), Mm(10), false);
            cells.Add(cell);
            map.Add(new PdnPlaneMapCell(cell, 100.0 * Math.Pow(1.7, i)));
        }

        var plane = new PdnPlaneAnswer(
            null, [], cells, map, 50e6, "U1.VDD", 1, 1e-3, PdnModelKind.Accurate, 0,
            [new PdnPlanePort(0, "U1.VDD", Mm(2), Mm(10), true)],
            [PdnImpedanceNames.PlanePairNote]);

        return RailMapScene.Build(result: null, RailMapKind.Impedance, Dbu, plane);
    }

    private static string SvgOf(RailMapScene scene, int w, int h)
    {
        using var stream = new SKDynamicMemoryWStream();
        var vp = LayoutViewport.ZoomToFit(scene.Bounds, w, h);
        using (var canvas = SKSvgCanvas.Create(new SKRect(0, 0, w, h), stream))
            RailMapRenderer.Draw(canvas, scene, vp, RailMapTheme.Light);
        return System.Text.Encoding.UTF8.GetString(stream.DetachAsData().ToArray());
    }

    /// <summary>The SVG's own text runs, the way the other rail export tests read them.</summary>
    private static string TextOf(string svg) => svg;

    private static string StripComments(string source)
    {
        source = System.Text.RegularExpressions.Regex.Replace(source, @"/\*.*?\*/", "",
                                                              System.Text.RegularExpressions.RegexOptions.Singleline);
        return System.Text.RegularExpressions.Regex.Replace(source, @"//[^\n]*", "");
    }
}
