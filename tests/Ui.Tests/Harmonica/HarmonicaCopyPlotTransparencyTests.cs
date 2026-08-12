using System.IO;
using System.Runtime.CompilerServices;
using CircuitRF.Ui.Harmonica.Renderers;
using CircuitRF.Ui.Renderers;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.Harmonica;

/// <summary>
/// R-h9a-12 — <i>Copy Plot</i>'s PDF/SVG/bitmap export must arrive transparent, never the
/// phosphor-on-black panel background <c>HarmonicaCanvasRenderer.FillBackground</c> paints for the
/// live canvas. Mirrors <c>LayoutRenderer</c>'s own <c>TransparentBackground</c> option (R-L1f-5) —
/// the same pattern, not a second mechanism, per the brief's own explicit instruction.
///
/// <para><b>Investigated, not assumed: <c>PlotRenderer.Draw</c> paints no unconditional background
/// anywhere.</b> Neither it, <c>AxesRenderer</c>'s Rect/Polar/Smith grid drawers, nor
/// <c>HarmonicaPanelRenderer.DrawSmithPanel</c> reference <c>RenderTheme.BackgroundColor</c> at
/// all — confirmed by direct source inspection, not by running the app (no visual driver here).
/// <c>TableRenderer</c> is the one exception, and only for its even-row stripe shading, which is the
/// SAME behaviour the Data Display's own transparent-export path already exhibits for a Table plot —
/// not a defect this brief introduces. Since nothing in <c>PlotRenderer.Draw</c> needs suppressing,
/// no <c>HarmonicaRenderTheme.ToPlotTheme</c> change was needed either; the fix is entirely in
/// <c>HarmonicaCanvasRenderer.FillBackground</c> and its two <c>HarmonicaClipboard</c> call sites.</para>
/// </summary>
public class HarmonicaCopyPlotTransparencyTests
{
    private static string ReadRepoFile(string relativePath, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root (no CLAUDE.md found walking up from this test file).");
        return File.ReadAllText(Path.Combine(dir!, relativePath));
    }

    // ── the pixel contract ────────────────────────────────────────────────────────────────────

    [Fact]
    public void FillBackground_Transparent_PaintsNothing_DestinationSurfaceIsLeftUntouched()
    {
        const int W = 40, H = 40;
        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        var canvas = surface.Canvas;

        // A sentinel the transparent path must never disturb — a real draw call (even one with
        // alpha-0 paint) would still be the wrong contract to ship, per the doc comment on
        // FillBackground: "it never draws a transparent rect, it skips the fill entirely."
        var sentinel = new SKColor(11, 22, 33, 255);
        using (var fill = new SKPaint { Color = sentinel, IsAntialias = false })
            canvas.DrawRect(new SKRect(0, 0, W, H), fill);

        HarmonicaCanvasRenderer.FillBackground(canvas, W, H, HarmonicaRenderTheme.Dark, transparent: true);

        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        Assert.Equal(sentinel, bmp.GetPixel(W / 2, H / 2));
        Assert.Equal(sentinel, bmp.GetPixel(2, 2));
        Assert.Equal(sentinel, bmp.GetPixel(W - 3, H - 3));
    }

    [Fact]
    public void FillBackground_DefaultsToOpaque_TheLiveCanvasCallSiteIsUnaffected()
    {
        const int W = 40, H = 40;
        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        var canvas = surface.Canvas;

        var theme = HarmonicaRenderTheme.Dark;
        // No `transparent:` argument at all — the exact call shape HarmonicaCanvas.cs's own draw
        // operation uses, and the one this test pins so a future signature change cannot silently
        // flip the live canvas transparent.
        HarmonicaCanvasRenderer.FillBackground(canvas, W, H, theme);

        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        var expected = new SKColor(theme.Background.Red, theme.Background.Green,
                                   theme.Background.Blue, theme.Background.Alpha);
        Assert.Equal(expected, bmp.GetPixel(W / 2, H / 2));
    }

    // ── the wiring (source-scan — HarmonicaClipboard's Render closure is reached only through an
    // async clipboard round trip this suite has no Avalonia runtime to drive) ──────────────────

    private static string HarmonicaClipboardSource() =>
        ReadRepoFile("src/Ui/Harmonica/HarmonicaClipboard.cs");

    private static string HarmonicaCanvasSource() =>
        ReadRepoFile("src/Ui/Controls/HarmonicaCanvas.cs");

    [Fact]
    public void CopyAsync_PassesTransparentTrue_ToFillBackground()
    {
        string src = HarmonicaClipboardSource();
        Assert.Contains(
            "HarmonicaCanvasRenderer.FillBackground(canvas, PlotExporter.PageW, PlotExporter.PageH,\n" +
            "                                                   snap.Theme, transparent: true);",
            src, System.StringComparison.Ordinal);
    }

    [Fact]
    public void BuildBitmap_ErasesToTransparent_BeforeRendering()
    {
        string src = HarmonicaClipboardSource();
        int eraseIdx  = src.IndexOf("bitmap.Erase(SKColors.Transparent);", System.StringComparison.Ordinal);
        int renderIdx = src.IndexOf("render(canvas);", System.StringComparison.Ordinal);
        Assert.True(eraseIdx >= 0, "BuildBitmap must erase the bitmap to transparent before rendering");
        Assert.True(renderIdx >= 0);
        Assert.True(eraseIdx < renderIdx, "the erase must happen BEFORE the render callback runs");
    }

    [Fact]
    public void LiveHarmonicaCanvas_CallsFillBackground_WithNoTransparentArgument()
    {
        // The live canvas's own draw operation must keep filling its rect normally — R-h9a-12's own
        // "the live canvas must be unaffected" requirement — pinned as a literal call-shape match so
        // a future edit that adds `transparent: true` here would fail this test immediately.
        string src = HarmonicaCanvasSource();
        Assert.Contains(
            "HarmonicaCanvasRenderer.FillBackground(canvas, Bounds.Width, Bounds.Height, _theme);",
            src, System.StringComparison.Ordinal);
        Assert.DoesNotContain("transparent: true", src, System.StringComparison.Ordinal);
    }
}
