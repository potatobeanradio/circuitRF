using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CircuitRF.Ui.Diagnostics;
using CircuitRF.Ui.Theming;
using SkiaSharp;

namespace CircuitRF.DocGen.Pipeline;

/// <summary>
/// The slide backend: the SAME content tree, rendered to a landscape PDF deck.
///
/// <para><c>SKDocument.CreatePdf</c> gives a canvas per page exactly as <c>SKSvgCanvas</c> gives one
/// per figure — no new dependency, and <b>Skia embeds the fonts</b>, so a PDF is correct on any
/// machine with no <c>@font-face</c> question at all. The alternative, HTML slides printed by a
/// headless browser, adds a browser to the build for no gain.</para>
///
/// <para><b>A deck is rendered per colour variant, and the figures follow it.</b> A light deck
/// carries light captures and a dark deck carries dark ones — the same pairing the HTML docs make
/// with <c>.sym-light</c>/<c>.sym-dark</c>. A dark slide holding a light screenshot is the one
/// failure mode a themed deck has, so the variant is threaded all the way down to
/// <see cref="UiArtworkGenerator.ApplyVariant"/> rather than being a background colour swap.</para>
///
/// <para><b>Overflow is a generation error, not a silent clip.</b> There is no browser here to
/// reflow a slide. The body is auto-fitted down a short ladder of type sizes first — a deck built
/// out of documentation prose has genuinely variable slide weights — but if the smallest step still
/// does not fit, the generator says so and names the slide. A clipped bullet in a deck is exactly
/// the kind of defect nobody notices until it is on a projector.</para>
///
/// <para><b>Source markup</b> (a <c>kind: slides</c> page under <c>docs/user/src/slides/</c>):</para>
/// <list type="bullet">
///   <item><term><c># Heading</c></term><description>a full-bleed section divider</description></item>
///   <item><term><c>## Heading</c></term><description>one content slide</description></item>
///   <item><term><c>### Heading</c></term><description>a sub-head inside the current slide</description></item>
///   <item><term><c>- text</c></term><description>a bullet; indent two spaces for a sub-bullet</description></item>
///   <item><term><c>&gt; **Label** text</c></term><description>a tinted callout band</description></item>
///   <item><term>``` fence</term><description>a command / code band</description></item>
///   <item><term><c>{{ui: id}}</c></term><description>a captured figure, on the right half</description></item>
///   <item><term><c>{{ui: id | full}}</c></term><description>the figure across the slide, under the bullets</description></item>
///   <item><term><c>{{caption: text}}</c></term><description>the caption under that slide's figure</description></item>
///   <item><term><c>{{stats: 4::analyses | 3::platforms}}</c></term><description>a row of headline figures</description></item>
/// </list>
/// Inline, <c>**bold**</c> and <c>`code`</c> are honoured.
/// </summary>
public static class SlideEmitter
{
    /// <summary>13.33 x 7.5 inches at 72 pt/in — 16:9 landscape.</summary>
    public const float PageWidth  = 960f;
    public const float PageHeight = 540f;

    private const float Margin      = 52f;
    private const float FooterBand  = 30f;
    private const float FigurePad   = 11f;
    private const float MinFigureH  = 150f;

    // ── Theme ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The deck palette. Every value is lifted from the documentation stylesheet's <c>[VARS]</c> /
    /// <c>[VARS-DARK]</c> blocks so a slide and the page it was written from are the same colour;
    /// the brand cyan and coral are the two the app itself uses.
    /// </summary>
    private sealed record Theme(
        SKColor Bg, SKColor Surface, SKColor Surface2, SKColor Border,
        SKColor Ink, SKColor Text, SKColor Muted, SKColor CodeInk,
        SKColor NoteBg, SKColor Cover, SKColor CoverInk, SKColor CoverMuted);

    private static readonly SKColor Cyan  = new(0x22, 0xD3, 0xEE);
    private static readonly SKColor Coral = new(0xFF, 0x6A, 0x3D);

    private static SKColor Hex(uint rgb) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private static Theme Palette(ColorVariant v) => v == ColorVariant.Dark
        ? new Theme(Hex(0x0E1820), Hex(0x15222D), Hex(0x1C2C39), Hex(0x283A48),
                    Hex(0xF2F7FA), Hex(0xE6EDF3), Hex(0x9DB0C0), Hex(0x56D7EE),
                    Hex(0x102832), Hex(0x08121A), Hex(0xF2F7FA), Hex(0x9DB0C0))
        : new Theme(Hex(0xFFFFFF), Hex(0xF6F8FA), Hex(0xEEF2F5), Hex(0xDCE3EA),
                    Hex(0x0F2233), Hex(0x1A2632), Hex(0x5C6B7A), Hex(0x0E7C99),
                    Hex(0xEAF6FA), Hex(0x0F2233), Hex(0xF7FAFC), Hex(0x9FB2C2));

    // ── The content model ─────────────────────────────────────────────────────

    private enum BlockKind { Para, Bullet, Sub, Callout, Code, Stats }

    private sealed record Block(BlockKind Kind, string Text, int Indent = 0, string Label = "");

    /// <summary>One figure on a slide, with the caption written under it.</summary>
    private sealed record Fig(string Id, string? Caption = null);

    private sealed record Slide(
        string Title, IReadOnlyList<Block> Blocks,
        IReadOnlyList<Fig> Figures, bool FigureFull, bool IsSection)
    {
        /// <summary>
        /// A slide that is nothing BUT its figure gets a tighter frame — smaller title, narrower
        /// margins, no footer — because the screenshot is the whole point of it. Measured: the
        /// workspace capture gained 29% in width from this alone.
        /// </summary>
        public bool FigureOnly => FigureFull && Figures.Count > 0 && Blocks.Count == 0;
    }

    private enum RunStyle { Body, Bold, Code, Label }

    /// <summary>
    /// One wrappable token. <paramref name="Glue"/> means "no space before me" — the boundary this
    /// word sat on in the source had no whitespace, as in <c>**bold**: rest</c>. Without it a bold
    /// run followed immediately by punctuation renders as "bold : rest", because splitting on spaces
    /// loses the one piece of information that says the two touch.
    /// </summary>
    private sealed record Word(string Text, RunStyle Style, bool Glue = false);

    // ── Entry point ───────────────────────────────────────────────────────────

    /// <summary>Render a <c>kind: slides</c> source page to a PDF at <paramref name="outPath"/>.</summary>
    /// <param name="figuresDir">Unused today; kept so a future backend can reuse the emitted SVGs.</param>
    /// <param name="variant">Which colour variant the deck AND its captures are rendered in.</param>
    public static void Render(DocPage page, string figuresDir, string outPath, ColorVariant variant)
    {
        var slides = Split(page);
        if (slides.Count == 0)
            throw new InvalidOperationException(
                $"{page.SourcePath}: a 'kind: slides' page produced no slides. One slide per '##' heading — " +
                "a deck with no level-two headings has nothing to render.");

        var theme = Palette(variant);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        using var stream = new SKFileWStream(outPath);
        using var doc = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata
        {
            Title = page.Title + (variant == ColorVariant.Dark ? " (dark)" : ""),
            Creator = "circuitRF DocGen",
            Producer = "circuitRF DocGen (" + UiArtworkGenerator.RegenerateCommand + ")",
        });

        Cover(doc, page, theme);
        int number = 1;
        foreach (var slide in slides)
        {
            if (slide.IsSection) Section(doc, slide, theme);
            else                 One(doc, page, slide, theme, variant, number);
            number++;
        }
        doc.Close();
    }

    // ── Splitting the shared content into slides ──────────────────────────────

    private static IReadOnlyList<Slide> Split(DocPage page)
    {
        var slides  = new List<Slide>();
        string? title = null;
        bool section  = false;
        var blocks    = new List<Block>();
        var figures   = new List<Fig>();
        bool figureFull = false;

        var code = new List<string>();
        bool inCode = false;

        void Flush()
        {
            if (title is null) return;
            slides.Add(new Slide(title, blocks.ToList(), figures.ToList(), figureFull, section));
            blocks.Clear();
            figures.Clear();
            figureFull = false; section = false;
        }

        foreach (var raw in page.Body.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (inCode)
                {
                    blocks.Add(new Block(BlockKind.Code, string.Join("\n", code)));
                    code.Clear();
                }
                inCode = !inCode;
                continue;
            }
            if (inCode) { code.Add(line); continue; }

            if (line.StartsWith("### ", StringComparison.Ordinal))
            { blocks.Add(new Block(BlockKind.Sub, line[4..].Trim())); continue; }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            { Flush(); title = line[3..].Trim(); section = false; continue; }

            if (line.StartsWith("# ", StringComparison.Ordinal))
            { Flush(); title = line[2..].Trim(); section = true; continue; }

            var fig = Regex.Match(line, @"^\s*\{\{\s*ui\s*:\s*(?<id>[^}|]+?)\s*(\|\s*(?<mode>[^}]+?)\s*)?\}\}\s*$");
            if (fig.Success)
            {
                if (figures.Count == 2)
                    throw new InvalidOperationException(
                        $"{page.SourcePath}: slide \"{title}\" cites a third figure. Two is the ceiling — a "
                      + "third leaves each one too small to read from the back of a room, which is a defect "
                      + "no reader would report and everyone would notice.");
                figures.Add(new Fig(fig.Groups["id"].Value));

                // The mode belongs to the SLIDE, not to one figure, so it accumulates: writing
                // "| full" on the first of a pair and leaving it off the second used to reset the
                // slide to the right-hand column and shrink both to a quarter of the room they asked
                // for — a silent demotion with nothing to report it.
                figureFull |= fig.Groups["mode"].Success
                           && fig.Groups["mode"].Value.Trim().Equals("full", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            var cap = Regex.Match(line, @"^\s*\{\{\s*caption\s*:\s*(?<t>[^}]+?)\s*\}\}\s*$");
            if (cap.Success)
            {
                if (figures.Count == 0)
                    throw new InvalidOperationException(
                        $"{page.SourcePath}: a caption on slide \"{title}\" has no figure above it to "
                      + "belong to, so it would never be drawn.");
                figures[^1] = figures[^1] with { Caption = cap.Groups["t"].Value };
                continue;
            }

            var stats = Regex.Match(line, @"^\s*\{\{\s*stats\s*:\s*(?<t>[^}]+?)\s*\}\}\s*$");
            if (stats.Success) { blocks.Add(new Block(BlockKind.Stats, stats.Groups["t"].Value)); continue; }

            var stray = Regex.Match(line, @"\{\{\s*(?<kind>[a-z]+)\s*:");
            if (stray.Success)
                throw new InvalidOperationException(
                    $"{page.SourcePath}: slide placeholder '{stray.Groups["kind"].Value}' is not one the deck " +
                    "backend knows. A deck supports ui, caption and stats — anything else would reach the PDF " +
                    "as literal braces, which is exactly what this pipeline exists to prevent.");

            if (line.Length == 0) continue;

            if (line.StartsWith("> ", StringComparison.Ordinal))
            {
                var text  = line[2..].Trim();
                var label = Regex.Match(text, @"^\*\*(?<l>[^*]+)\*\*\s*(?<rest>.*)$");
                blocks.Add(label.Success
                    ? new Block(BlockKind.Callout, label.Groups["rest"].Value.TrimStart('—', '-', ' '),
                                Label: label.Groups["l"].Value)
                    : new Block(BlockKind.Callout, text));
                continue;
            }

            int indent = raw.Length - raw.TrimStart(' ').Length;
            var trimmed = line.TrimStart(' ');
            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
                blocks.Add(new Block(BlockKind.Bullet, trimmed[2..].Trim(), indent >= 2 ? 1 : 0));
            else
                blocks.Add(new Block(BlockKind.Para, line.Trim()));
        }

        if (inCode)
            throw new InvalidOperationException($"{page.SourcePath}: a ``` code fence is never closed.");

        Flush();
        return slides;
    }

    // ── Cover and section slides ──────────────────────────────────────────────

    private static void Cover(SKDocument doc, DocPage page, Theme t)
    {
        var canvas = doc.BeginPage(PageWidth, PageHeight);
        canvas.Clear(t.Cover);

        // The brand mark, drawn rather than imported: "circuit" plain, "RF" in coral, which is how
        // the application writes its own name.
        using var mark   = new SKFont(Typeface(bold: false), 22f);
        using var markRf = new SKFont(Typeface(bold: true), 22f);
        using var markInk = new SKPaint { Color = t.CoverMuted, IsAntialias = true };
        using var coral   = new SKPaint { Color = Coral, IsAntialias = true };
        float x = Margin;
        canvas.DrawText("circuit", x, Margin + 20, SKTextAlign.Left, mark, markInk);
        canvas.DrawText("RF", x + mark.MeasureText("circuit"), Margin + 20, SKTextAlign.Left, markRf, coral);

        Rule(canvas, PageHeight * 0.44f);

        using var titleFont = new SKFont(Typeface(bold: true), 44f);
        using var ink = new SKPaint { Color = t.CoverInk, IsAntialias = true };
        canvas.DrawText(page.Title, Margin, PageHeight * 0.44f + 56, SKTextAlign.Left, titleFont, ink);

        if (page.Lede.Length > 0)
        {
            using var sub = new SKFont(Typeface(bold: false), 18f);
            using var subPaint = new SKPaint { Color = t.CoverMuted, IsAntialias = true };
            float y = PageHeight * 0.44f + 92;
            foreach (var line in WrapPlain(page.Lede, sub, PageWidth - 2 * Margin))
            {
                canvas.DrawText(line, Margin, y, SKTextAlign.Left, sub, subPaint);
                y += 25;
            }
        }

        using var foot = new SKFont(Typeface(bold: false), 11f);
        using var footPaint = new SKPaint { Color = t.CoverMuted, IsAntialias = true };
        canvas.DrawText("circuitRF " + CircuitRF.Ui.AppVersion.Display + "  ·  every figure is a vector capture of the running application",
                        Margin, PageHeight - Margin + 8, SKTextAlign.Left, foot, footPaint);
        doc.EndPage();
    }

    private static void Section(SKDocument doc, Slide slide, Theme t)
    {
        var canvas = doc.BeginPage(PageWidth, PageHeight);
        canvas.Clear(t.Cover);
        Rule(canvas, PageHeight * 0.48f);

        using var titleFont = new SKFont(Typeface(bold: true), 36f);
        using var ink = new SKPaint { Color = t.CoverInk, IsAntialias = true };
        canvas.DrawText(slide.Title, Margin, PageHeight * 0.48f + 50, SKTextAlign.Left, titleFont, ink);

        var lede = slide.Blocks.FirstOrDefault(b => b.Kind is BlockKind.Para or BlockKind.Bullet);
        if (lede is not null)
        {
            using var sub = new SKFont(Typeface(bold: false), 17f);
            using var subPaint = new SKPaint { Color = t.CoverMuted, IsAntialias = true };
            float y = PageHeight * 0.48f + 86;
            foreach (var line in WrapPlain(Plain(lede.Text), sub, PageWidth - 2 * Margin))
            {
                canvas.DrawText(line, Margin, y, SKTextAlign.Left, sub, subPaint);
                y += 24;
            }
        }
        doc.EndPage();
    }

    // ── Content slides ────────────────────────────────────────────────────────

    private static void One(SKDocument doc, DocPage page, Slide slide, Theme t,
                            ColorVariant variant, int number)
    {
        var canvas = doc.BeginPage(PageWidth, PageHeight);
        canvas.Clear(t.Bg);

        // A figure-only slide is framed tighter than a bullet slide: the picture is the content, so
        // it gets the margins and the title size back.
        bool  only   = slide.FigureOnly;
        float margin = only ? 30f : Margin;
        float titleY = only ? margin + 20f : Margin + 24f;

        using var titleFont = new SKFont(Typeface(bold: true), only ? 23f : 27f);
        using var ink = new SKPaint { Color = t.Ink, IsAntialias = true };
        canvas.DrawText(slide.Title, margin, titleY, SKTextAlign.Left, titleFont, ink);
        float ruleY = titleY + 14f;
        Rule(canvas, ruleY, margin);

        float top    = ruleY + (only ? 12f : 36f);
        float bottom = PageHeight - Margin - FooterBand;

        // The figure band runs past the footer's line and almost to the paper edge. The footer is one
        // 10 pt line on a baseline BELOW it, so the space between the two was doing nothing but making
        // every screenshot smaller than it needed to be.
        float figBottom = PageHeight - (only ? 22f : 26f);
        bool  right     = slide.Figures.Count > 0 && !slide.FigureFull;

        float bodyWidth = right ? PageWidth * 0.46f - Margin : PageWidth - 2 * Margin;
        float reserved  = slide.Figures.Count > 0 && slide.FigureFull ? MinFigureH : 0;

        // Auto-fit: documentation prose does not arrive in equal-weight slides, so step the body
        // type down before declaring an overflow. The floor is a readable projector size, not an
        // arbitrarily small one — past it, the slide really is too full and must be split.
        List<Laid> laid = [];
        float used = 0;
        if (slide.Blocks.Count > 0)
        {
            foreach (float scale in (float[])[1f, 0.94f, 0.88f, 0.82f, 0.76f, 0.70f])
            {
                laid = LayoutBody(slide.Blocks, scale, bodyWidth, out float h);
                used = h;
                if (h <= bottom - top - reserved) break;
            }

            if (used > bottom - top - reserved)
                throw new InvalidOperationException(
                    $"{page.SourcePath}: slide \"{slide.Title}\" overflows the template even at the " +
                    "smallest body size. There is no browser here to reflow it, so this is a generation " +
                    "error rather than a silent clip — shorten the slide or split it in two.");
        }

        float y = top;
        foreach (var block in laid) y = DrawBlock(canvas, block, Margin, y, bodyWidth, t);

        if (slide.Figures.Count > 0)
        {
            var band = slide.FigureFull
                ? new SKRect(margin, Math.Max(top, y + 8), PageWidth - margin, figBottom)
                : new SKRect(PageWidth * 0.48f, top, PageWidth - Margin, figBottom);

            // Two figures share the band along its LONG axis: side by side across a full-width slide,
            // stacked in the right-hand column. Splitting the other way in either case would halve the
            // dimension that was already binding.
            foreach (var (fig, box) in Boxes(slide, band))
                Figure(canvas, fig.Id, box, fig.Caption, t, variant, page);
        }

        if (!only) Footer(canvas, page, t, number);
        doc.EndPage();
    }

    private static IEnumerable<(Fig Fig, SKRect Box)> Boxes(Slide slide, SKRect band)
    {
        const float Gap = 12f;

        if (slide.Figures.Count == 1)
        {
            yield return (slide.Figures[0], band);
            yield break;
        }

        if (slide.FigureFull)
        {
            float w = (band.Width - Gap) / 2f;
            yield return (slide.Figures[0], new SKRect(band.Left, band.Top, band.Left + w, band.Bottom));
            yield return (slide.Figures[1], new SKRect(band.Right - w, band.Top, band.Right, band.Bottom));
        }
        else
        {
            float h = (band.Height - Gap) / 2f;
            yield return (slide.Figures[0], new SKRect(band.Left, band.Top, band.Right, band.Top + h));
            yield return (slide.Figures[1], new SKRect(band.Left, band.Bottom - h, band.Right, band.Bottom));
        }
    }

    private static void Footer(SKCanvas canvas, DocPage page, Theme t, int number)
    {
        using var font = new SKFont(Typeface(bold: false), 10f);
        using var paint = new SKPaint { Color = t.Muted, IsAntialias = true };
        canvas.DrawText($"{page.Title}  ·  circuitRF {CircuitRF.Ui.AppVersion.Display}",
                        Margin, PageHeight - Margin + 12, SKTextAlign.Left, font, paint);
        canvas.DrawText(number.ToString(), PageWidth - Margin, PageHeight - Margin + 12,
                        SKTextAlign.Right, font, paint);
    }

    // ── Body layout ───────────────────────────────────────────────────────────

    private sealed class Laid
    {
        public BlockKind Kind;
        public string Label = "";
        public List<List<Word>> Lines = [];
        public List<(string Value, string Label)> Stats = [];
        public float Size, Indent, SpaceBefore, LineHeight, Height;
    }

    private static List<Laid> LayoutBody(IReadOnlyList<Block> blocks, float scale, float maxWidth,
                                         out float total)
    {
        var laid = new List<Laid>();
        total = 0;
        bool first = true;

        foreach (var block in blocks)
        {
            float size = block.Kind switch
            {
                BlockKind.Sub     => 15.5f,
                BlockKind.Callout => 15f,
                BlockKind.Code    => 14f,
                _                 => 17f,
            } * scale;

            var item = new Laid
            {
                Kind = block.Kind,
                Label = block.Label,
                Size = size,
                LineHeight = MathF.Round(size * 1.42f),
                Indent = block.Kind == BlockKind.Bullet ? (block.Indent == 0 ? 18f : 40f) : 0f,
                SpaceBefore = first ? 0f : block.Kind switch
                {
                    BlockKind.Sub     => 16f * scale,
                    BlockKind.Callout => 13f * scale,
                    BlockKind.Code    => 13f * scale,
                    BlockKind.Stats   => 12f * scale,
                    BlockKind.Bullet  => 7f * scale,
                    _                 => 10f * scale,
                },
            };
            first = false;

            if (block.Kind == BlockKind.Stats)
            {
                foreach (var part in block.Text.Split('|', StringSplitOptions.RemoveEmptyEntries))
                {
                    var bits = part.Split("::", 2, StringSplitOptions.TrimEntries);
                    item.Stats.Add((bits[0], bits.Length > 1 ? bits[1] : ""));
                }
                item.Height = 74f * scale;
            }
            else
            {
                using var fonts = new FontSet(size);
                float inner = maxWidth - item.Indent
                            - (block.Kind is BlockKind.Callout or BlockKind.Code ? 32f : 0f);
                // The label is PART of the word stream, not a prefix painted over it: drawn
                // separately it consumed first-line width the wrap knew nothing about, and the
                // first line of every labelled callout ran off the right edge of its own band.
                var prefix = block.Label.Length == 0
                    ? null
                    : block.Label.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                 .Select(w => new Word(w, RunStyle.Label))
                                 .Append(new Word("—", RunStyle.Label));

                item.Lines = block.Kind == BlockKind.Code
                    ? block.Text.Split('\n').Select(l => (List<Word>)[new Word(l, RunStyle.Code)]).ToList()
                    : WrapWords(Words(block.Text, prefix), fonts, inner);
                float pad = block.Kind is BlockKind.Callout or BlockKind.Code ? 11f : 0f;
                item.Height = item.Lines.Count * item.LineHeight + 2 * pad;
            }

            total += item.SpaceBefore + item.Height;
            laid.Add(item);
        }
        return laid;
    }

    private static float DrawBlock(SKCanvas canvas, Laid b, float x, float y, float width, Theme t)
    {
        y += b.SpaceBefore;

        if (b.Kind == BlockKind.Stats) { DrawStats(canvas, b, x, y, width, t); return y + b.Height; }

        if (b.Kind is BlockKind.Callout or BlockKind.Code)
        {
            var rect = new SKRoundRect(new SKRect(x, y, x + width, y + b.Height), 6f);
            using var fill = new SKPaint
            { Color = b.Kind == BlockKind.Callout ? t.NoteBg : t.Surface2, IsAntialias = true };
            canvas.DrawRoundRect(rect, fill);
            using var bar = new SKPaint
            { Color = b.Kind == BlockKind.Callout ? Cyan : Coral, IsAntialias = true };
            canvas.DrawRect(new SKRect(x, y + 3, x + 3.5f, y + b.Height - 3), bar);
        }

        using var fonts = new FontSet(b.Size);
        using var text  = new SKPaint { Color = t.Text, IsAntialias = true };
        using var muted = new SKPaint { Color = t.Muted, IsAntialias = true };
        using var codeInk = new SKPaint { Color = t.CodeInk, IsAntialias = true };
        using var ink   = new SKPaint { Color = t.Ink, IsAntialias = true };

        float pad = b.Kind is BlockKind.Callout or BlockKind.Code ? 11f : 0f;
        float tx  = x + b.Indent + (pad > 0 ? 16f : 0f);
        float ty  = y + pad + b.Size;

        if (b.Kind == BlockKind.Bullet)
        {
            using var dot = new SKPaint { Color = b.Indent < 20f ? Coral : t.Muted, IsAntialias = true };
            float r = b.Indent < 20f ? 3.2f : 2.2f;
            canvas.DrawCircle(x + b.Indent - 11f, ty - b.Size * 0.32f, r, dot);
        }

        var basePaint = b.Kind switch
        {
            BlockKind.Sub     => ink,
            BlockKind.Code    => codeInk,
            BlockKind.Callout => text,
            _                 => b.Indent >= 40f ? muted : text,
        };

        foreach (var line in b.Lines)
        {
            float cx = tx;
            bool firstOnLine = true;
            foreach (var w in line)
            {
                if (!firstOnLine && !w.Glue) cx += fonts.Space;
                firstOnLine = false;

                var font = fonts.ForWord(w, forceBold: b.Kind == BlockKind.Sub);
                var paint = w.Style switch
                {
                    RunStyle.Label                            => codeInk,
                    RunStyle.Code when b.Kind != BlockKind.Code => codeInk,
                    _                                         => basePaint,
                };
                canvas.DrawText(w.Text, cx, ty, SKTextAlign.Left, font, paint);
                cx += font.MeasureText(w.Text);
            }
            ty += b.LineHeight;
        }
        return y + b.Height;
    }

    /// <summary>A row of headline figures — the one thing an evaluation deck needs that prose is bad at.</summary>
    private static void DrawStats(SKCanvas canvas, Laid b, float x, float y, float width, Theme t)
    {
        int n = Math.Max(1, b.Stats.Count);
        float gap = 12f, w = (width - gap * (n - 1)) / n;
        using var valueFont = new SKFont(Typeface(bold: true), MathF.Round(b.Height * 0.36f));
        using var labelFont = new SKFont(Typeface(bold: false), MathF.Round(b.Height * 0.155f));
        using var valuePaint = new SKPaint { Color = t.Ink, IsAntialias = true };
        using var labelPaint = new SKPaint { Color = t.Muted, IsAntialias = true };
        using var fill = new SKPaint { Color = t.Surface, IsAntialias = true };
        using var edge = new SKPaint
        { Color = t.Border, IsAntialias = true, IsStroke = true, StrokeWidth = 1 };

        for (int i = 0; i < n; i++)
        {
            float cx = x + i * (w + gap);
            var rect = new SKRoundRect(new SKRect(cx, y, cx + w, y + b.Height), 7f);
            canvas.DrawRoundRect(rect, fill);
            canvas.DrawRoundRect(rect, edge);
            canvas.DrawRect(new SKRect(cx, y, cx + w, y + 3f), new SKPaint { Color = i % 2 == 0 ? Cyan : Coral });
            canvas.DrawText(b.Stats[i].Value, cx + w / 2, y + b.Height * 0.55f, SKTextAlign.Center,
                            valueFont, valuePaint);
            canvas.DrawText(b.Stats[i].Label, cx + w / 2, y + b.Height * 0.82f, SKTextAlign.Center,
                            labelFont, labelPaint);
        }
    }

    // ── Figures ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Place a captured figure inside <paramref name="box"/>, matted on the theme surface. The
    /// figure is re-rendered from its fixture rather than pasted as a bitmap, so the slide stays
    /// vector throughout — and it is rendered in <paramref name="variant"/>, so a dark deck holds
    /// dark screenshots.
    /// </summary>
    private static void Figure(SKCanvas canvas, string id, SKRect box, string? caption,
                               Theme t, ColorVariant variant, DocPage page)
    {
        var row = FigureCatalog.Catalog.FirstOrDefault(r => r.Id == id);
        if (row.Id is null)
            throw new InvalidOperationException(
                $"{page.SourcePath}: slide figure '{id}' is not in FigureCatalog. Known: " +
                string.Join(", ", FigureCatalog.Catalog.Select(r => r.Id)));

        using var capFont = new SKFont(Typeface(bold: false), 10.5f);
        // Measured against the box, not the card: the card is sized FROM the caption height, so the
        // two cannot both wait for the other. Two lines is the ceiling — a caption longer than that
        // is a bullet that wandered into the wrong place.
        var capLines = caption is null
            ? []
            : WrapPlain(caption, capFont, box.Width - 4 * FigurePad).Take(2).ToList();
        float capH = capLines.Count == 0 ? 0f : capLines.Count * 13f + 4f;

        int contentH = row.Height + (row.Chrome is null ? 0 : WindowFrame.TitleBarHeight);
        float availW = box.Width  - 2 * FigurePad;
        float availH = box.Height - 2 * FigurePad - capH;
        if (availW < 40 || availH < 40)
            throw new InvalidOperationException(
                $"{page.SourcePath}: slide figure '{id}' has no room left on the slide " +
                $"({availW:F0}x{availH:F0} pt). Move bullets off it or make it a figure-only slide.");

        float scale = Math.Min(availW / row.Width, availH / contentH);
        float figW = row.Width * scale, figH = contentH * scale;
        float capW = capLines.Count == 0 ? 0f : capLines.Max(l => capFont.MeasureText(l));
        float cardW = Math.Max(figW, capW) + 2 * FigurePad;
        float cardH = figH + 2 * FigurePad + capH;
        float cardX = box.Left + (box.Width - cardW) / 2f;
        float cardY = box.Top  + (box.Height - cardH) / 2f;

        var card = new SKRoundRect(new SKRect(cardX, cardY, cardX + cardW, cardY + cardH), 8f);
        using (var fill = new SKPaint { Color = t.Surface, IsAntialias = true })
        using (var edge = new SKPaint { Color = t.Border, IsAntialias = true, IsStroke = true, StrokeWidth = 1 })
        {
            canvas.DrawRoundRect(card, fill);
            canvas.DrawRoundRect(card, edge);
        }

        UiArtworkGenerator.ApplyVariant(variant);
        using var scene = row.Build();
        var content = row.Chrome is null
            ? scene.Content
            : row.Chrome.Wrap(scene.Content, row.Width, row.Height, variant);

        var window = new Window
        {
            WindowDecorations = WindowDecorations.None,
            Width = row.Width, Height = contentH, Content = content,
            Background = new SolidColorBrush(Color.FromRgb(t.Bg.Red, t.Bg.Green, t.Bg.Blue)),
        };
        window.Show();
        UiArtworkGenerator.Pump();
        window.Measure(new Size(row.Width, contentH));
        window.Arrange(new Rect(0, 0, row.Width, contentH));
        UiArtworkGenerator.Pump();

        // The SAME post-layout settle the SVG path runs (UiArtworkGenerator.RenderScene). Omitting it
        // here is not a cosmetic difference: Zoom to Fit is a viewport operation, so a figure whose
        // fixture asks for it was drawn fitted in the documentation and unfitted on the slide — the
        // same catalog row, two different pictures, with nothing to say which was right.
        if (scene.AfterLayout is { } settle)
        {
            settle(scene.Content);
            UiArtworkGenerator.Pump();
            window.Measure(new Size(row.Width, contentH));
            window.Arrange(new Rect(0, 0, row.Width, contentH));
            UiArtworkGenerator.Pump();
        }

        if (scene.Popups is { } open)
        {
            var popups = open(scene.Content) ?? [];
            UiArtworkGenerator.Pump();
            window.Measure(new Size(row.Width, contentH));
            window.Arrange(new Rect(0, 0, row.Width, contentH));
            UiArtworkGenerator.Pump();

            // An overlay-hosted popup is already inside the window's tree and needs nothing more. One
            // that got its own top level would have to be composited, which this backend does not do —
            // and a menu figure quietly missing its menu is exactly the silent-wrong-picture failure
            // the whole pipeline exists to refuse.
            if (popups.Any(x => x.SeparateRoot is not null))
                throw new InvalidOperationException(
                    $"{page.SourcePath}: figure '{id}' opens a popup with a top level of its own, which the "
                  + "deck backend cannot composite. Use it on a documentation page, or add compositing here.");
        }

        // Recorded, then drawn with a matrix: RenderAsync installs the visual's own transform and
        // ignores whatever is on the canvas, so translating and scaling around the call does nothing.
        using (var picture = UiArtworkGenerator.Record(window))
        {
            var m = SKMatrix.CreateScale(scale, scale);
            m.TransX = cardX + (cardW - figW) / 2f; m.TransY = cardY + FigurePad;
            canvas.DrawPicture(picture, m);
        }

        window.Content = null;
        window.Close();
        UiArtworkGenerator.Pump();

        if (capLines.Count > 0)
        {
            using var capPaint = new SKPaint { Color = t.Muted, IsAntialias = true };
            float cy = cardY + cardH - capH + 11f;
            foreach (var line in capLines)
            {
                canvas.DrawText(line, cardX + cardW / 2f, cy, SKTextAlign.Center, capFont, capPaint);
                cy += 13f;
            }
        }
    }

    // ── Text mechanics ────────────────────────────────────────────────────────

    private static void Rule(SKCanvas canvas, float y, float margin = Margin)
    {
        using var cyan = new SKPaint { Color = Cyan, StrokeWidth = 3 };
        using var coral = new SKPaint { Color = Coral, StrokeWidth = 3 };
        canvas.DrawLine(margin, y, PageWidth * 0.55f, y, cyan);
        canvas.DrawLine(PageWidth * 0.55f, y, PageWidth - margin, y, coral);
    }

    /// <summary>Strip the inline markup, for the places that draw a single unstyled run.</summary>
    private static string Plain(string text)
        => text.Replace("**", "").Replace("`", "");

    private static List<Word> Words(string text, IEnumerable<Word>? prefix = null)
    {
        var words = new List<Word>();
        if (prefix is not null) words.AddRange(prefix);

        // A prefix (a callout's label) is always followed by a space, and at the very start of a
        // block there is nothing to glue to.
        bool prevEndedWithSpace = true;

        foreach (Match m in Regex.Matches(text, @"\*\*(?<b>[^*]+)\*\*|`(?<c>[^`]+)`|(?<t>(?:[^*`]|\*(?!\*))+)"))
        {
            var style = m.Groups["b"].Success ? RunStyle.Bold
                      : m.Groups["c"].Success ? RunStyle.Code
                      : RunStyle.Body;
            var seg = m.Groups["b"].Success ? m.Groups["b"].Value
                    : m.Groups["c"].Success ? m.Groups["c"].Value
                    : m.Groups["t"].Value;

            // The boundary must be judged on the RAW match, not on the captured group: a bold run's
            // raw text is "**bold**", so its first character is an asterisk and never the space that
            // separates it from the word before. That space lives at the end of the PREVIOUS raw
            // segment, which is why both sides are tested.
            string raw = m.Value;
            bool glue = words.Count > 0 && !prevEndedWithSpace
                     && raw.Length > 0 && !char.IsWhiteSpace(raw[0]);

            foreach (var w in seg.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                words.Add(new Word(w, style, glue));
                glue = false;
            }
            prevEndedWithSpace = raw.Length > 0 && char.IsWhiteSpace(raw[^1]);
        }
        return words;
    }

    private static List<List<Word>> WrapWords(List<Word> words, FontSet fonts, float maxWidth)
    {
        var lines = new List<List<Word>>();
        var line = new List<Word>();
        float w = 0;

        foreach (var word in words)
        {
            float ww = fonts.ForWord(word, forceBold: false).MeasureText(word.Text);
            float probe = line.Count == 0 ? ww : w + (word.Glue ? 0f : fonts.Space) + ww;
            if (probe <= maxWidth || line.Count == 0)
            {
                line.Add(word);
                w = probe;
                continue;
            }

            // A glued word may not start a line: it is punctuation or a suffix that belongs to the
            // word before it, and breaking there reads as ", never to a cell" on its own line.
            // Carry the whole glue chain down instead.
            var carry = new List<Word>();
            while (line.Count > 1 && word.Glue)
            {
                carry.Insert(0, line[^1]);
                bool chained = line[^1].Glue;
                line.RemoveAt(line.Count - 1);
                if (!chained) break;
            }

            lines.Add(line);
            line = [.. carry, word];
            w = Measure(line, fonts);
        }
        if (line.Count > 0) lines.Add(line);
        return lines;
    }

    private static float Measure(IReadOnlyList<Word> line, FontSet fonts)
    {
        float w = 0;
        for (int i = 0; i < line.Count; i++)
        {
            if (i > 0 && !line[i].Glue) w += fonts.Space;
            w += fonts.ForWord(line[i], forceBold: false).MeasureText(line[i].Text);
        }
        return w;
    }

    private static IEnumerable<string> WrapPlain(string text, SKFont font, float maxWidth)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var line = "";
        foreach (var word in words)
        {
            var probe = line.Length == 0 ? word : line + " " + word;
            if (font.MeasureText(probe) <= maxWidth) { line = probe; continue; }
            if (line.Length > 0) yield return line;
            line = word;
        }
        if (line.Length > 0) yield return line;
    }

    private sealed class FontSet : IDisposable
    {
        public readonly SKFont Body, Bold, Code, Fallback, FallbackBold;
        public readonly float Space;

        public FontSet(float size)
        {
            Body = new SKFont(Typeface(bold: false), size);
            Bold = new SKFont(Typeface(bold: true), size);
            Code = new SKFont(Typeface(bold: false), size * 0.96f);
            Fallback     = new SKFont(FallbackFace(bold: false), size);
            FallbackBold = new SKFont(FallbackFace(bold: true), size);
            Space = Body.MeasureText(" ");
        }

        public SKFont For(RunStyle style) => style switch
        {
            RunStyle.Bold or RunStyle.Label => Bold,
            RunStyle.Code                   => Code,
            _                               => Body,
        };

        /// <summary>
        /// The font one WORD is drawn in, honouring the glyph fallback.
        ///
        /// <para>Documentation prose uses menu arrows (<c>File ▸ New Schematic</c>) and the
        /// occasional Ω or µ. IBM Plex Sans has none of the geometric shapes, and Skia's answer to a
        /// missing glyph in a PDF is a hollow box — which is silent, survives review, and looks like
        /// a broken build on a projector. DejaVu Sans ships with the application for exactly this
        /// reason, so a word Plex cannot set is set in DejaVu instead of not being set.</para>
        /// </summary>
        public SKFont ForWord(Word w, bool forceBold)
        {
            var primary = forceBold ? Bold : For(w.Style);
            foreach (var rune in w.Text.EnumerateRunes())
                if (primary.Typeface.GetGlyph(rune.Value) == 0)
                    return forceBold || w.Style is RunStyle.Bold or RunStyle.Label
                        ? FallbackBold : Fallback;
            return primary;
        }

        public void Dispose()
        {
            Body.Dispose(); Bold.Dispose(); Code.Dispose();
            Fallback.Dispose(); FallbackBold.Dispose();
        }
    }

    private static readonly Dictionary<bool, SKTypeface> _faces = [];
    private static readonly Dictionary<bool, SKTypeface> _fallbacks = [];

    /// <summary>DejaVu Sans, shipped with the app, for the glyphs IBM Plex Sans does not carry.</summary>
    private static SKTypeface FallbackFace(bool bold)
    {
        if (_fallbacks.TryGetValue(bold, out var cached)) return cached;
        return _fallbacks[bold] =
            Load("avares://CircuitRF.Ui/Assets/Fonts/DejaVuSans" + (bold ? "-Bold" : "") + ".ttf")
            ?? Typeface(bold);
    }

    private static SKTypeface? Load(string uri)
    {
        var u = new Uri(uri);
        if (!Avalonia.Platform.AssetLoader.Exists(u)) return null;
        using var s = Avalonia.Platform.AssetLoader.Open(u);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        ms.Position = 0;
        return SKTypeface.FromStream(ms);
    }

    /// <summary>
    /// The deck's typeface, read straight out of the application's own embedded assets — the same
    /// bytes the captured figures were drawn with, and the bytes Skia will EMBED in the PDF. Falls
    /// back to the platform default rather than failing a whole deck over a font.
    /// </summary>
    private static SKTypeface Typeface(bool bold)
    {
        if (_faces.TryGetValue(bold, out var cached)) return cached;

        var face = Load("avares://CircuitRF.Ui/Assets/Fonts/IBM_Plex_Sans/static/IBMPlexSans-"
                      + (bold ? "SemiBold" : "Regular") + ".ttf");
        return _faces[bold] = face ?? SKTypeface.Default;
    }
}
