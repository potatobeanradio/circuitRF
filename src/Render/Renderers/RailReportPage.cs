// The railRF report page — ONE route from a rail result to a page, and not two
// (docs/sonnet-briefs/brief-railrf-10-cli-verb.md R-rail10-4; railrf.md §11.5, §11.7).
//
// ══ WHY THE PAGE IS COMPOSED HERE AND NOT IN src/Cli OR src/Ui ════════════════════════════════
//
// §11.7's last line: "the same render path produces the picture in the `Report ▸` menu and in the
// headless report." Brief 9 wrote that intent down and put its composition in
// `src/Ui/RailRf/RailGraphicExport.cs`, which is where the CLIPBOARD copy belongs — that path goes
// through `LayoutClipboard`, and `LayoutClipboard` lives above the UI firewall because it performs
// `IClipboard` traffic and hands back an Avalonia `Bitmap`.
//
// `src/Cli` may not reference `src/Ui`. So a headless `-o report.svg` written against brief 9's
// function is not merely awkward, it does not COMPILE — and the alternative a CLI verb would reach
// for on its own (draw the board, then draw some text beside it) is precisely the second route
// §11.7 exists to forbid: two compositions that agree today and drift the first time either is
// touched, with the drift invisible because both produce a plausible page.
//
// This file is that one composition, below the firewall, where both callers can reach it:
//
//   * `circuitrf rail <doc> -o report.svg|.pdf` calls it and encodes the canvas;
//   * `Report ▸` calls it and puts the same bytes on the clipboard or in a file.
//
// ══ IT DECIDES THE LAYOUT OF A PAGE AND NOTHING ELSE ══════════════════════════════════════════
//
// Every pixel of the PICTURE is `LayoutRenderer.Draw` plus `RailMapRenderer` — the same call the
// board panel makes each frame and the same call the clipboard copy makes. Every number in the TEXT
// arrives already formatted, because the sentences are `RailDcResult`'s own (`RailPortDrop.Describe`,
// `RailRegulatorHeadroom.Describe`, the breakdown rows) and a page that re-formatted them would make
// the window and the report disagree about the same result.
//
// ══ THE EXPORT LEVEL-OF-DETAIL RULE IS NOT OPTIONAL ═══════════════════════════════════════════
//
// All SEVEN tiers off, exactly as `LayoutClipboard.ExportOptions` and `render --detail full` set
// them, and for the reason recorded in both: a page has no device pixels to budget against, and the
// one direction the mistake produces a PLAUSIBLE picture is a picture of less geometry than the
// document holds. 240 stored rects drew 2 elements when only `DetailPixelThreshold` was set.

using SkiaSharp;

namespace CircuitRF.Render;

/// <summary>One block of the report's text: a heading and the lines under it.</summary>
/// <param name="Heading">What the block is — "Ports", "Where the drop is", "Vias".</param>
/// <param name="Lines">The rows, already worded. <b>Never re-formatted here</b> — see this file's
/// header.</param>
public sealed record RailReportSection(string Heading, IReadOnlyList<string> Lines);

/// <summary>
/// Everything one report page is a function of.
/// </summary>
/// <remarks>
/// <b>The provenance is a first-class field rather than one more section</b> (R-rail10-5). Overview
/// §4 rule 1: a file read six months later has no status strip, so which model produced it, which
/// reference extent was used and what temperature it was computed at have to be ON the page. Making
/// it a section would let a caller omit it by accident; making it a field means the page always has
/// somewhere to put it and a caller that supplies none gets a visibly empty banner.
/// </remarks>
public sealed record RailReportPageRequest
{
    /// <summary>The page's title — the document's name, or the file's.</summary>
    public required string Title { get; init; }

    /// <summary>R-rail10-5's line(s), drawn under the title. See this record's remarks.</summary>
    public IReadOnlyList<string> Provenance { get; init; } = [];

    /// <summary>The text blocks, in the order they are drawn.</summary>
    public IReadOnlyList<RailReportSection> Sections { get; init; } = [];

    /// <summary>The artwork, or null — a report of a document whose artwork did not resolve is still
    /// a report, and it says so rather than not being written.</summary>
    public LayoutView? Board { get; init; }

    /// <summary>The stackup, for layer colours and visibility.</summary>
    public Technology? Technology { get; init; }

    /// <summary>The scene the board panel would be showing, or null for the bare artwork.</summary>
    public RailMapScene? Map { get; init; }

    /// <summary>Every part the document says is NOT FITTED, marked on the board — empty for none.
    /// <b>Document state, not window state</b>: see <c>LayoutRenderOptions.RailNotFitted</c> for why
    /// this travels into a report while the parts table's selection does not.</summary>
    public IReadOnlyList<RailPartHighlight> NotFitted { get; init; } = [];

    /// <summary>The colour theme both halves of the page are resolved from — ONE variant, because a
    /// light board under a dark legend is how a page ends up not looking like one picture.</summary>
    public ColorTheme Theme { get; init; } = ColorTheme.BuiltIn;

    /// <summary>Which variant of it.</summary>
    public ColorVariant Variant { get; init; } = ColorVariant.Light;

    /// <summary>Where an instance's <c>CellRef</c> resolves from.</summary>
    public string BaseDir { get; init; } = "";
}

/// <summary>
/// Composes a railRF report page. <b>Draws no geometry of its own</b> — see this file's header.
/// </summary>
public static class RailReportPage
{
    /// <summary>The page's outer margin, in points/device pixels.</summary>
    private const float Margin = 28f;

    /// <summary>How much of the page height the picture takes. The rest is text.</summary>
    private const float PictureFraction = 0.52f;

    private const float TitleSizePx      = 19f;
    private const float ProvenanceSizePx = 9.5f;
    private const float HeadingSizePx    = 11.5f;
    private const float BodySizePx       = 9.5f;
    private const float LineGap          = 1.35f;

    /// <summary>
    /// Draws the whole page onto <paramref name="canvas"/> at <paramref name="pageW"/> x
    /// <paramref name="pageH"/>.
    /// </summary>
    /// <remarks>
    /// <b>The caller owns the surface and the encoder.</b> A PDF page, an SVG canvas and a bitmap are
    /// three encoders over one drawing, which is the shape <c>render</c> already has
    /// (<c>Render.Emit</c>) — so this method never opens a file and never chooses a format.
    /// </remarks>
    public static void Draw(SKCanvas canvas, RailReportPageRequest request, int pageW, int pageH)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(request);

        var layoutTheme = LayoutRenderTheme.FromTheme(request.Theme, request.Variant);
        var railTheme   = RailMapTheme.FromTheme(request.Theme, request.Variant);

        using (var page = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill, Color = layoutTheme.Background })
            canvas.DrawRect(SKRect.Create(0, 0, pageW, pageH), page);

        using var ink = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = railTheme.LegendInk };

        float y = Margin;

        using (var title = Font(SkiaFonts.PlexSemiBold, TitleSizePx))
        {
            y += TitleSizePx;
            canvas.DrawText(request.Title, Margin, y, SKTextAlign.Left, title, ink);
        }

        // R-rail10-5, and it sits directly under the title rather than at the foot of the page: it is
        // the thing a reader has to know BEFORE reading the numbers, not a colophon.
        using (var prov = Font(SkiaFonts.PlexRegular, ProvenanceSizePx))
        {
            foreach (string line in request.Provenance)
            {
                y += ProvenanceSizePx * LineGap;
                canvas.DrawText(line, Margin, y, SKTextAlign.Left, prov, ink);
            }
        }

        y += 10f;

        float pictureH = Math.Max(0, pageH * PictureFraction - (y - Margin));
        if (pictureH > 24f)
        {
            DrawPicture(canvas, request, layoutTheme, railTheme,
                        new SKRect(Margin, y, pageW - Margin, y + pictureH));
            y += pictureH + 14f;
        }

        DrawSections(canvas, request, ink, Margin, y, pageW - 2 * Margin, pageH - Margin - y);
    }

    /// <summary>
    /// The board and its map, framed on what is PAINTED.
    /// </summary>
    /// <remarks>
    /// <b>The frame unions the map's own bounds</b> — brief 9's R-rail9-2, seen from the headless
    /// side. A drop map is co-extensive with the copper, so framing on the artwork alone looks
    /// harmless right up until the legend, a source marker or a flagged-via callout sits outside the
    /// copper's own bbox and is quietly cut off the page. <see cref="RailMapScene.Bounds"/> is the
    /// scene's own union of everything it paints, computed from unculled content, which is the same
    /// answer the canvas's Zoom to Fit gives.
    /// </remarks>
    private static void DrawPicture(
        SKCanvas canvas, RailReportPageRequest request,
        LayoutRenderTheme layoutTheme, RailMapTheme railTheme, SKRect box)
    {
        int w = (int)Math.Round(box.Width);
        int h = (int)Math.Round(box.Height);
        if (w < 8 || h < 8) return;

        var bbox = request.Board is { } board
            ? DocumentExtents.LayoutBox(board, request.Technology, request.BaseDir)
            : Bbox.Empty;

        if (request.Map is { } map && !map.Bounds.IsEmpty)
            bbox = bbox.IsEmpty ? map.Bounds : bbox.Union(map.Bounds);

        var vp = LayoutViewport.ZoomToFit(bbox, w, h, marginFrac: 0.04);

        canvas.Save();
        try
        {
            canvas.Translate(box.Left, box.Top);
            LayoutRenderer.Draw(canvas, request.Board, request.Technology, vp, new LayoutRenderOptions
            {
                Theme      = layoutTheme,
                ShowGrid   = false,
                Overlay    = null,
                BaseDir    = request.BaseDir,
                PathCache  = null,
                RailMap    = request.Map,
                RailTheme  = railTheme,
                RailNotFitted = request.NotFitted,

                // See this file's header: ALL SEVEN, and the list is the one
                // `LayoutClipboard.ExportOptions` and `render --detail full` set.
                DetailPixelThreshold          = -1,
                LodPixelThreshold             = -1,
                MergeShapeCountThreshold      = -1,
                OutlineVertexBudget           = -1,
                InstanceRasterMaxDevicePixels = -1,
                StrokeElisionPixelThreshold   = -1,
                HairlineFillPixelThreshold    = -1,
                CoarseCoverageThreshold       = -1,
            });
        }
        finally { canvas.Restore(); }
    }

    /// <summary>
    /// The text blocks, in two columns, clipped to the space that is left.
    /// </summary>
    /// <remarks>
    /// <b>A line that does not fit is DROPPED and the block says how many</b>, rather than being
    /// drawn off the page. A report whose last rows fell off the bottom reads exactly like a report
    /// that had no more rows, and the ranked breakdown is ordered worst-first precisely so a reader
    /// acts on the top of it — so the truncation has to be visible where it happens.
    /// </remarks>
    private static void DrawSections(
        SKCanvas canvas, RailReportPageRequest request, SKPaint ink,
        float x, float y, float width, float height)
    {
        if (height < BodySizePx * 2 || request.Sections.Count == 0) return;

        const float ColumnGap = 18f;
        float columnW = (width - ColumnGap) / 2f;

        using var heading = Font(SkiaFonts.PlexSemiBold, HeadingSizePx);
        using var body    = Font(SkiaFonts.PlexRegular, BodySizePx);

        float cursorY = y;
        float cursorX = x;
        int column = 0;

        foreach (var section in request.Sections)
        {
            if (!Fits(HeadingSizePx * (LineGap + 0.6f)) && !NextColumn()) return;

            cursorY += HeadingSizePx * LineGap;
            canvas.DrawText(section.Heading, cursorX, cursorY, SKTextAlign.Left, heading, ink);
            cursorY += HeadingSizePx * 0.4f;

            int drawn = 0;
            foreach (string line in section.Lines)
            {
                if (!Fits(BodySizePx * LineGap))
                {
                    if (!NextColumn())
                    {
                        Truncated(section.Lines.Count - drawn);
                        return;
                    }
                    // A column break re-states the heading, so a block that spans two columns is not
                    // a run of unlabelled rows.
                    cursorY += HeadingSizePx * LineGap;
                    canvas.DrawText(section.Heading + " (continued)", cursorX, cursorY,
                                    SKTextAlign.Left, heading, ink);
                    cursorY += HeadingSizePx * 0.4f;
                }

                cursorY += BodySizePx * LineGap;
                canvas.DrawText(Clip(line, body, columnW), cursorX, cursorY, SKTextAlign.Left, body, ink);
                drawn++;
            }

            cursorY += BodySizePx * 0.9f;
        }

        bool Fits(float need) => cursorY + need <= y + height;

        bool NextColumn()
        {
            if (column >= 1) return false;
            column++;
            cursorX = x + columnW + ColumnGap;
            cursorY = y;
            return true;
        }

        void Truncated(int remaining)
        {
            if (remaining <= 0) return;
            canvas.DrawText($"… {remaining} more row(s) — the CSV or the .npy carries all of them",
                            cursorX, y + height, SKTextAlign.Left, body, ink);
        }
    }

    /// <summary>Trims a row to the column, with an ellipsis, so nothing is drawn past the gutter.</summary>
    private static string Clip(string text, SKFont font, float width)
    {
        if (font.MeasureText(text) <= width) return text;

        for (int n = text.Length - 1; n > 1; n--)
        {
            string candidate = text[..n] + "…";
            if (font.MeasureText(candidate) <= width) return candidate;
        }
        return "…";
    }

    /// <summary>Greyscale antialiasing, for <see cref="RailMapRenderer"/>'s own stated reason: this
    /// picture does not stay on a screen.</summary>
    private static SKFont Font(SKTypeface typeface, float size) =>
        new(typeface, size) { Edging = SKFontEdging.Antialias, Subpixel = false };
}
