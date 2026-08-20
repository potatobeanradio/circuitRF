using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Skia.Helpers;
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
/// <para><b>Overflow is a generation error, not a silent clip.</b> There is no browser here to
/// reflow a slide. If the content does not fit the template, the generator says so and names the
/// slide — a clipped bullet in a deck is exactly the kind of defect nobody notices until it is on a
/// projector.</para>
/// </summary>
public static class SlideEmitter
{
    /// <summary>13.33 x 7.5 inches at 72 pt/in — 16:9 landscape.</summary>
    public const float PageWidth  = 960f;
    public const float PageHeight = 540f;

    private const float Margin = 48f;

    /// <summary>One slide: a title (the <c>##</c> heading) and the lines under it.</summary>
    private sealed record Slide(string Title, IReadOnlyList<string> Bullets, string? FigureId);

    /// <summary>Render a <c>kind: slides</c> source page to a PDF at <paramref name="outPath"/>.</summary>
    /// <param name="figuresDir">Where the captured figure SVGs live, for a slide that shows one.</param>
    public static void Render(DocPage page, string figuresDir, string outPath)
    {
        var slides = Split(page);
        if (slides.Count == 0)
            throw new InvalidOperationException(
                $"{page.SourcePath}: a 'kind: slides' page produced no slides. One slide per '##' heading — " +
                "a deck with no level-two headings has nothing to render.");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        using var stream = new SKFileWStream(outPath);
        using var doc = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata
        {
            Title = page.Title,
            Creator = "circuitRF DocGen",
            Producer = "circuitRF DocGen (" + UiArtworkGenerator.RegenerateCommand + ")",
        });

        Title(doc, page);
        foreach (var slide in slides) One(doc, page, slide, figuresDir);
        doc.Close();
    }

    // ── Splitting the shared content into slides ──────────────────────────────

    private static IReadOnlyList<Slide> Split(DocPage page)
    {
        var slides = new List<Slide>();
        string? title = null;
        var bullets = new List<string>();
        string? figure = null;

        void Flush()
        {
            if (title is null) return;
            slides.Add(new Slide(title, bullets.ToList(), figure));
            bullets.Clear();
            figure = null;
        }

        foreach (var raw in page.Body.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                title = line[3..].Trim();
                continue;
            }

            var fig = Regex.Match(line, @"^\s*\{\{\s*ui\s*:\s*(?<id>[^}]+?)\s*\}\}\s*$");
            if (fig.Success) { figure = fig.Groups["id"].Value; continue; }

            if (line.Length == 0) continue;
            if (line.StartsWith("- ", StringComparison.Ordinal)) bullets.Add(line[2..].Trim());
            else bullets.Add(line.Trim());
        }
        Flush();
        return slides;
    }

    // ── Drawing ───────────────────────────────────────────────────────────────

    private static void Title(SKDocument doc, DocPage page)
    {
        var canvas = doc.BeginPage(PageWidth, PageHeight);
        canvas.Clear(SKColors.White);
        Rule(canvas, PageHeight * 0.42f);
        using var font = new SKFont(Typeface(bold: true), 40f);
        using var paint = new SKPaint { Color = new SKColor(0x0F, 0x22, 0x33), IsAntialias = true };
        canvas.DrawText(page.Title, Margin, PageHeight * 0.42f + 52, SKTextAlign.Left, font, paint);

        if (page.Lede.Length > 0)
        {
            using var sub = new SKFont(Typeface(bold: false), 18f);
            using var subPaint = new SKPaint { Color = new SKColor(0x5C, 0x6B, 0x7A), IsAntialias = true };
            canvas.DrawText(page.Lede, Margin, PageHeight * 0.42f + 86, SKTextAlign.Left, sub, subPaint);
        }
        doc.EndPage();
    }

    private static void One(SKDocument doc, DocPage page, Slide slide, string figuresDir)
    {
        var canvas = doc.BeginPage(PageWidth, PageHeight);
        canvas.Clear(SKColors.White);

        using var titleFont = new SKFont(Typeface(bold: true), 26f);
        using var ink = new SKPaint { Color = new SKColor(0x0F, 0x22, 0x33), IsAntialias = true };
        canvas.DrawText(slide.Title, Margin, Margin + 26, SKTextAlign.Left, titleFont, ink);
        Rule(canvas, Margin + 40);

        float y = Margin + 84;
        using var bodyFont = new SKFont(Typeface(bold: false), 17f);
        using var body = new SKPaint { Color = new SKColor(0x1A, 0x26, 0x32), IsAntialias = true };
        float maxWidth = slide.FigureId is null ? PageWidth - 2 * Margin : PageWidth * 0.46f - Margin;

        foreach (var bullet in slide.Bullets)
        {
            foreach (var line in Wrap(bullet, bodyFont, maxWidth - 18))
            {
                if (y > PageHeight - Margin)
                    throw new InvalidOperationException(
                        $"{page.SourcePath}: slide \"{slide.Title}\" overflows the template. There is no " +
                        "browser here to reflow it, so this is a generation error rather than a silent clip — " +
                        "shorten the slide or split it in two.");
                canvas.DrawText(line, Margin + 18, y, SKTextAlign.Left, bodyFont, body);
                y += 26;
            }
            y += 6;
        }

        if (slide.FigureId is { } id) Figure(canvas, id, figuresDir, page);
        doc.EndPage();
    }

    /// <summary>
    /// Place a captured figure on the right half of the slide. The figure is re-rendered from its
    /// fixture rather than pasted as a bitmap, so the slide stays vector throughout.
    /// </summary>
    private static void Figure(SKCanvas canvas, string id, string figuresDir, DocPage page)
    {
        var row = FigureCatalog.Catalog.FirstOrDefault(r => r.Id == id);
        if (row.Id is null)
            throw new InvalidOperationException($"{page.SourcePath}: slide figure '{id}' is not in FigureCatalog.");

        float boxX = PageWidth * 0.5f, boxY = Margin + 60f;
        float boxW = PageWidth * 0.5f - Margin, boxH = PageHeight - boxY - Margin;

        int contentH = row.Height + (row.Chrome is null ? 0 : WindowFrame.TitleBarHeight);
        float scale = Math.Min(boxW / row.Width, boxH / contentH);

        UiArtworkGenerator.ApplyVariant(ColorVariant.Light);
        using var scene = row.Build();
        var content = row.Chrome is null
            ? scene.Content
            : row.Chrome.Wrap(scene.Content, row.Width, row.Height, ColorVariant.Light);

        var window = new Window
        {
            WindowDecorations = WindowDecorations.None,
            Width = row.Width, Height = contentH, Content = content,
            Background = Brushes.White,
        };
        window.Show();
        UiArtworkGenerator.Pump();
        window.Measure(new Size(row.Width, contentH));
        window.Arrange(new Rect(0, 0, row.Width, contentH));
        UiArtworkGenerator.Pump();

        // Recorded, then drawn with a matrix: RenderAsync installs the visual's own transform and
        // ignores whatever is on the canvas, so translating and scaling around the call does nothing.
        using (var picture = UiArtworkGenerator.Record(window))
        {
            var m = SKMatrix.CreateScale(scale, scale);
            m.TransX = boxX; m.TransY = boxY;
            canvas.DrawPicture(picture, m);
        }

        window.Content = null;
        window.Close();
        UiArtworkGenerator.Pump();
    }

    private static void Rule(SKCanvas canvas, float y)
    {
        using var cyan = new SKPaint { Color = new SKColor(0x22, 0xD3, 0xEE), StrokeWidth = 3 };
        using var coral = new SKPaint { Color = new SKColor(0xFF, 0x6A, 0x3D), StrokeWidth = 3 };
        canvas.DrawLine(Margin, y, PageWidth * 0.55f, y, cyan);
        canvas.DrawLine(PageWidth * 0.55f, y, PageWidth - Margin, y, coral);
    }

    private static IEnumerable<string> Wrap(string text, SKFont font, float maxWidth)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var line = "";
        foreach (var w in words)
        {
            var probe = line.Length == 0 ? w : line + " " + w;
            if (font.MeasureText(probe) <= maxWidth) { line = probe; continue; }
            if (line.Length > 0) yield return line;
            line = w;
        }
        if (line.Length > 0) yield return line;
    }

    private static readonly Dictionary<bool, SKTypeface> _faces = [];

    /// <summary>
    /// The deck's typeface, read straight out of the application's own embedded assets — the same
    /// bytes the captured figures were drawn with, and the bytes Skia will EMBED in the PDF. Falls
    /// back to the platform default rather than failing a whole deck over a font.
    /// </summary>
    private static SKTypeface Typeface(bool bold)
    {
        if (_faces.TryGetValue(bold, out var cached)) return cached;

        var uri = new Uri("avares://CircuitRF.Ui/Assets/Fonts/IBM_Plex_Sans/static/IBMPlexSans-"
                        + (bold ? "SemiBold" : "Regular") + ".ttf");
        SKTypeface? face = null;
        if (Avalonia.Platform.AssetLoader.Exists(uri))
        {
            using var s = Avalonia.Platform.AssetLoader.Open(uri);
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            ms.Position = 0;
            face = SKTypeface.FromStream(ms);
        }
        return _faces[bold] = face ?? SKTypeface.Default;
    }
}
