using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Theming;
using SkiaSharp;

namespace CircuitRF.Ui.Chain;

/// <summary>
/// The RF Chain Budget's Blocks view ▸ Copy: the block lineup on the system clipboard as a PICTURE (PDF, SVG and a 2x
/// bitmap at once, so a slide or a document takes whichever it understands), and the lineup as tab-separated text.
///
/// <para><b>No clipboard code here.</b> The write is <see cref="PlotExporter.SetClipboardDataAsync"/>, with the Windows
/// bypass and the text fallback; the PDF, SVG and PNG writers are <c>PlotDocumentWriter</c>'s; the colours and the
/// background come from <see cref="ClipboardRenderPolicy"/>. <see cref="Layout.StackupGraphicExport"/> is the model: the
/// page is the picture plus <see cref="PagePad"/>, so a pasted lineup is not a small drawing in a large empty box.</para>
///
/// <para>The picture is the WHOLE lineup, never the scrolled view, and it is artwork rather than the window: no
/// selection outline. Each block carries what the window's block does — the stage's name, its System block's glyph
/// (drawn by <see cref="PaletteGlyphControl.DrawFitted"/>, the window's own glyph framing, so a filter shows its band
/// form here too), the array's "× N", the part's own line, its numbers and the cascade at its output.</para>
/// </summary>
public static class ChainBlocksGraphicExport
{
    public const float PagePad = 8f;
    public const float BitmapScale = 2.0f;

    // The window's Blocks view, in DIPs: a block is at least 104 wide, padded 6,4; a glyph tile is 64 × 40.
    private const float BlockMinWidth = 104f, BlockPadX = 6f, BlockPadY = 4f, BlockRadius = 4f;
    private const float GlyphW = 64f, GlyphH = 40f, GlyphGap = 2f;
    private const float SmallSize = 10.5f, NameSize = 12f, TimesSize = 13f, EndSize = 13f;
    private const float LineGap = 1f, ArrowGapX = 4f;

    // The arrow between blocks is DRAWN, not a "⟶" character: the export faces have no glyph for it, and a picture
    // pasted into a slide must not depend on which fonts the receiving machine has.
    private const float ArrowLength = 20f, ArrowHead = 5f, ArrowRowHeight = 24f;

    /// <summary>One block's content, read off its stage row as the window shows it.</summary>
    public sealed record Block(string Name, IReadOnlyList<(IReadOnlyList<SymbolPrimitive> Artwork, string Caption)> Glyphs,
                                 string TimesN, string Extra, string KeyLine, string CascadeLine);

    public static IReadOnlyList<Block> BlocksOf(ChainBudgetViewModel vm) =>
        [.. vm.Stages.Select(s => new Block(
            s.Name,
            [.. s.Glyphs.Select(g => (g.Artwork ?? BuiltInSymbols.Primitives(g.Kind).Primitives, g.Caption))],
            s.TimesN, s.Extra, s.KeyLine, s.CascadeLine))];

    private sealed record Colors(SKColor Text, SKColor Fill, SKColor Border, SKColor Page, SchematicRenderTheme Glyph);

    private static Colors ColorsFor(ColorVariant variant)
    {
        bool light = variant == ColorVariant.Light;
        var glyph = SchematicRenderTheme.FromTheme(ThemeService.Active, variant).WithMonochrome(light);
        return light
            ? new Colors(new SKColor(0x1E, 0x1E, 0x1E), new SKColor(0xF4, 0xF4, 0xF4), new SKColor(0xC4, 0xC4, 0xC4), SKColors.White, glyph)
            : new Colors(new SKColor(0xE6, 0xE6, 0xE6), new SKColor(0x2B, 0x2B, 0x2B), new SKColor(0x58, 0x58, 0x58), new SKColor(0x1E, 0x1E, 0x1E), glyph);
    }

    private static SKFont Regular(float size) => new(SkiaFonts.PlexRegular, size);
    private static SKFont SemiBold(float size) => new(SkiaFonts.PlexSemiBold, size);

    private static IEnumerable<string> Lines(string text)
        => string.IsNullOrEmpty(text) ? [] : text.Split('\n');

    /// <summary>One line of a block: its text, its font size, whether it is semibold, and its opacity.</summary>
    private readonly record struct Line(string Text, float Size, bool Bold, float Alpha);

    /// <summary>The lines under a block's glyph row, top to bottom, as the window stacks them.</summary>
    private static List<Line> LinesUnderGlyphs(Block b)
    {
        var lines = new List<Line>();
        if (b.TimesN.Length > 0) lines.Add(new(b.TimesN, TimesSize, true, 1f));
        foreach (var l in Lines(b.Extra)) lines.Add(new(l, SmallSize, false, 0.8f));
        foreach (var l in Lines(b.KeyLine)) lines.Add(new(l, SmallSize, false, 1f));
        foreach (var l in Lines(b.CascadeLine)) lines.Add(new(l, SmallSize, false, 0.75f));
        return lines;
    }

    private static float LineHeight(float size) => size * 1.3f;

    private static float Measure(string text, float size, bool bold)
    {
        using var f = bold ? SemiBold(size) : Regular(size);
        return f.MeasureText(text);
    }

    private static bool HasCaptions(Block b) => b.Glyphs.Any(g => g.Caption.Length > 0);

    private static (float W, float H) SizeOf(Block b)
    {
        float glyphRow = b.Glyphs.Count * (GlyphW + GlyphGap);
        float w = Math.Max(BlockMinWidth, Math.Max(glyphRow, Measure(b.Name, NameSize, true)));
        foreach (var g in b.Glyphs) w = Math.Max(w, Measure(g.Caption, SmallSize, false));
        var lines = LinesUnderGlyphs(b);
        foreach (var l in lines) w = Math.Max(w, Measure(l.Text, l.Size, l.Bold));
        float h = LineHeight(NameSize) + LineGap + GlyphH + (HasCaptions(b) ? LineHeight(SmallSize) : 0)
                + lines.Sum(l => LineHeight(l.Size) + LineGap);
        return (w + 2 * BlockPadX, h + 2 * BlockPadY);
    }

    /// <summary>The lineup's own extent: IN, an arrow and a block per stage, an arrow and OUT.</summary>
    public static (float W, float H) Extent(IReadOnlyList<Block> blocks)
    {
        float arrow = ArrowLength + 2 * ArrowGapX;
        float w = Measure("IN", EndSize, true) + blocks.Sum(b => arrow + SizeOf(b).W) + arrow + Measure("OUT", EndSize, true);
        float h = Math.Max(ArrowRowHeight, blocks.Count == 0 ? 0 : blocks.Max(b => SizeOf(b).H));
        return (w, h);
    }

    public static PagePlacement PageFor(IReadOnlyList<Block> blocks)
    {
        var (w, h) = Extent(blocks);
        return new(Math.Max(w, 1f) + 2f * PagePad, Math.Max(h, 1f) + 2f * PagePad, PagePad);
    }

    private static void Text(SKCanvas canvas, string text, float cx, float baseline, float size, bool bold, SKColor color, float alpha)
    {
        using var font = bold ? SemiBold(size) : Regular(size);
        using var paint = new SKPaint { IsAntialias = true, Color = color.WithAlpha((byte)Math.Round(255 * alpha)) };
        canvas.DrawText(text, cx, baseline, SKTextAlign.Center, font, paint);
    }

    /// <summary>Draws the lineup on a page-sized canvas, centred vertically in the page's padding.</summary>
    internal static void Render(SKCanvas canvas, IReadOnlyList<Block> blocks, ColorVariant variant, bool transparentBackground)
    {
        var c = ColorsFor(variant);
        var page = PageFor(blocks);
        if (!transparentBackground) canvas.DrawRect(0, 0, page.Width, page.Height, new SKPaint { Color = c.Page });

        var (_, totalH) = Extent(blocks);
        float midY = PagePad + totalH / 2f;
        float x = PagePad;

        // The row's words and arrows sit on the blocks' centre line, as the window's StackPanel centres them.
        float EndBaseline(float size) => midY + size * 0.35f;

        using var arrowPaint = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, StrokeCap = SKStrokeCap.Round,
            Color = c.Text.WithAlpha((byte)Math.Round(255 * 0.7)),
        };
        void Arrow()
        {
            float x0 = x + ArrowGapX, x1 = x0 + ArrowLength;
            canvas.DrawLine(x0, midY, x1, midY, arrowPaint);
            canvas.DrawLine(x1 - ArrowHead, midY - ArrowHead * 0.7f, x1, midY, arrowPaint);
            canvas.DrawLine(x1 - ArrowHead, midY + ArrowHead * 0.7f, x1, midY, arrowPaint);
            x += ArrowLength + 2 * ArrowGapX;
        }

        float inW = Measure("IN", EndSize, true);
        Text(canvas, "IN", x + inW / 2f, EndBaseline(EndSize), EndSize, true, c.Text, 1f);
        x += inW;

        using var fill = new SKPaint { IsAntialias = true, Color = c.Fill, Style = SKPaintStyle.Fill };
        using var border = new SKPaint { IsAntialias = true, Color = c.Border, Style = SKPaintStyle.Stroke, StrokeWidth = 1f };

        foreach (var b in blocks)
        {
            Arrow();
            var (bw, bh) = SizeOf(b);
            float top = midY - bh / 2f, cx = x + bw / 2f;
            var rect = new SKRect(x + 0.5f, top + 0.5f, x + bw - 0.5f, top + bh - 0.5f);
            canvas.DrawRoundRect(rect, BlockRadius, BlockRadius, fill);
            canvas.DrawRoundRect(rect, BlockRadius, BlockRadius, border);

            float y = top + BlockPadY;
            Text(canvas, b.Name, cx, y + NameSize, NameSize, true, c.Text, 1f);
            y += LineHeight(NameSize) + LineGap;

            float gx = cx - b.Glyphs.Count * (GlyphW + GlyphGap) / 2f + GlyphGap / 2f;
            foreach (var (art, caption) in b.Glyphs)
            {
                int saved = canvas.Save();
                canvas.Translate(gx, y);
                PaletteGlyphControl.DrawFitted(canvas, GlyphW, GlyphH, art, [], SymbolRotation.R0, c.Glyph);
                canvas.RestoreToCount(saved);
                if (caption.Length > 0)
                    Text(canvas, caption, gx + GlyphW / 2f, y + GlyphH + SmallSize, SmallSize, false, c.Text, 1f);
                gx += GlyphW + GlyphGap;
            }
            y += GlyphH + (HasCaptions(b) ? LineHeight(SmallSize) : 0);

            foreach (var l in LinesUnderGlyphs(b))
            {
                Text(canvas, l.Text, cx, y + l.Size, l.Size, l.Bold, c.Text, l.Alpha);
                y += LineHeight(l.Size) + LineGap;
            }
            x += bw;
        }

        Arrow();
        float outW = Measure("OUT", EndSize, true);
        Text(canvas, "OUT", x + outW / 2f, EndBaseline(EndSize), EndSize, true, c.Text, 1f);
    }

    /// <summary>The lineup as text, one stage a line: its name, its own numbers and the cascade at its output.</summary>
    public static string TextOf(IReadOnlyList<Block> blocks)
    {
        var sb = new StringBuilder();
        foreach (var b in blocks)
            sb.Append(b.Name).Append('\t').Append(b.KeyLine.Replace('\n', ' ')).Append('\t')
              .AppendLine(b.CascadeLine.Replace('\n', ' '));
        return sb.ToString();
    }

    /// <summary>The page composition and the page, together, so no writer is handed one without the other.</summary>
    public static (Action<SKCanvas> Compose, PagePlacement Page) Composition(
        IReadOnlyList<Block> blocks, ColorVariant variant, bool transparentBackground)
        => (canvas => Render(canvas, blocks, variant, transparentBackground), PageFor(blocks));

    /// <summary>Renders the lineup and places PDF, SVG, a 2x bitmap and the text on the clipboard together.</summary>
    internal static async Task CopyToClipboardAsync(Control anchor, ChainBudgetViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(vm);

        var blocks = BlocksOf(vm);
        var (variant, transparent) = ClipboardRenderPolicy.Resolve();
        var (compose, page) = Composition(blocks, variant, transparent);

        byte[] pdf = PlotDocumentWriter.BuildPdfBytes(compose, page);
        string svg = PlotDocumentWriter.BuildSvgString(compose, page);
        var bitmap = BuildBitmap(compose, page);

        // The page dimensions travel with the bytes: the Windows bypass tells the receiving application how big the
        // picture is, and the exporter's letter landscape would put the lineup back inside an empty box.
        await PlotExporter.SetClipboardDataAsync(anchor, pdf, svg, TextOf(blocks), bitmap, page.Width, page.Height);
    }

    /// <summary>A 2x raster, best effort: a raster failure must never cost the vector formats.</summary>
    private static Avalonia.Media.Imaging.Bitmap? BuildBitmap(Action<SKCanvas> compose, PagePlacement page)
    {
        try
        {
            byte[]? png = PlotDocumentWriter.BuildPngBytes(compose, page, BitmapScale);
            if (png is null) return null;
            using var ms = new MemoryStream(png);
            return new Avalonia.Media.Imaging.Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }
}
