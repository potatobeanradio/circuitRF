// The Trace Impedance Analysis report as a PDF — ONE composition, called by the layout editor's
// Export and by `circuitrf impedance -o report.pdf` alike, for the reason RailReportPage gives: two
// compositions agree on the day they are written and drift the first time either is touched.
//
// ══ WHAT IS ON IT ═════════════════════════════════════════════════════════════════════════════
//
//   * A SUMMARY page: what was analysed, against what, by which method, and the verdict per layer —
//     the page a reviewer files with the fabrication package.
//   * Per layer, ONE MAP page (the board designer's own ask: one page per layer): the layer's copper
//     in grey, every trace found drawn over it coloured by its Z0 station by station, green inside
//     target ± tolerance, blue below, red above, with each trace's id and a numbered marker at every
//     finding. The colour scale and the tolerance band are on the page.
//   * Per layer, the TABLE of its traces and the numbered FINDINGS, continued over as many pages as
//     they need. A marker's number on the map is the finding's number in this list.
//
// Every number and sentence arrives from TraceImpedanceReport; nothing is recomputed here.

using SkiaSharp;

namespace CircuitRF.Render;

public static class TraceImpedanceReportDocument
{
    // A4 landscape, in points.
    public const int PageW = 842;
    public const int PageH = 595;
    private const float Margin = 32f;

    private static readonly SKColor Ink        = new(0x1d, 0x23, 0x2b);
    private static readonly SKColor Muted      = new(0x5f, 0x6b, 0x78);
    private static readonly SKColor Rule       = new(0xd5, 0xdb, 0xe1);
    private static readonly SKColor Band       = new(0xf3, 0xf5, 0xf7);
    private static readonly SKColor Accent     = new(0x1f, 0x5f, 0xa8);
    private static readonly SKColor CopperFill = new(0xdc, 0xdf, 0xe3);
    private static readonly SKColor CopperEdge = new(0xb9, 0xbf, 0xc6);
    private static readonly SKColor PassInk    = new(0x1e, 0x7b, 0x3c);
    private static readonly SKColor FailInk    = new(0xb4, 0x23, 0x18);
    private static readonly SKColor Unsolved   = new(0x9a, 0xa0, 0xa6);
    private static readonly SKColor GroundMark = new(0x7b, 0x2c, 0xbf);
    private static readonly SKColor ZMark      = new(0xd9, 0x6c, 0x06);

    /// <summary>The whole report as PDF bytes. Complete before the caller writes anything, so a
    /// failed or cancelled export leaves no half-written file.</summary>
    public static byte[] Pdf(TraceImpedanceReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var metadata = new SKDocumentPdfMetadata
        {
            Creator = "circuitRF",
            Title = $"Trace Impedance Analysis — {report.Title}",
            Subject = "Trace impedance review",
        };
        using var stream = new SKDynamicMemoryWStream();
        using (var document = SKDocument.CreatePdf(stream, metadata))
        {
            var writer = new Writer(document, report);
            writer.Summary();
            foreach (var layer in report.Layers)
            {
                writer.LayerMap(layer);
                writer.LayerTable(layer);
            }
            writer.Finish();
            document.Close();
        }
        return stream.DetachAsData().ToArray();
    }

    /// <summary>The colour a Z0 is drawn in: green inside the band, blue below it, red above,
    /// saturating at a third outside the band.</summary>
    public static SKColor Z0Color(double z0, double target, double tolerancePercent)
    {
        double lo = target * (1 - tolerancePercent / 100), hi = target * (1 + tolerancePercent / 100);
        if (z0 >= lo && z0 <= hi)
        {
            // Inside: from a mid green at the edges of the band to a deeper green at the target.
            double t = 1 - Math.Abs(z0 - target) / Math.Max(1e-9, hi - target);
            return Lerp(new SKColor(0x6c, 0xc0, 0x6a), new SKColor(0x1e, 0x8e, 0x3e), t);
        }
        if (z0 < lo)
        {
            double t = Math.Clamp((lo - z0) / (0.33 * target), 0, 1);
            return Lerp(new SKColor(0x6f, 0xa8, 0xdc), new SKColor(0x1b, 0x3f, 0x9b), t);
        }
        double u = Math.Clamp((z0 - hi) / (0.33 * target), 0, 1);
        return Lerp(new SKColor(0xf0, 0x9a, 0x4c), new SKColor(0xb4, 0x1f, 0x1f), u);
    }

    private static SKColor Lerp(SKColor a, SKColor b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new SKColor((byte)(a.Red + (b.Red - a.Red) * t), (byte)(a.Green + (b.Green - a.Green) * t),
                           (byte)(a.Blue + (b.Blue - a.Blue) * t));
    }

    private static SKFont Font(SKTypeface face, float size) =>
        new(face, size) { Edging = SKFontEdging.Antialias, Subpixel = false };

    private static string Ohms(double? z) => z is { } v ? $"{v:0.0}" : "—";

    private static string VerdictText(TraceVerdict v) => v switch
    {
        TraceVerdict.Pass => "PASS",
        TraceVerdict.Fail => "FAIL",
        _ => "—",
    };

    /// <summary>The page stream: the current page, a cursor, and the running header and footer.</summary>
    private sealed class Writer(SKDocument document, TraceImpedanceReport report)
    {
        private SKCanvas? _canvas;
        private int _page;
        private float _y;
        private string _section = "";

        private readonly SKFont _title = Font(SkiaFonts.PlexSemiBold, 20);
        private readonly SKFont _h1 = Font(SkiaFonts.PlexSemiBold, 14);
        private readonly SKFont _h2 = Font(SkiaFonts.PlexSemiBold, 10.5f);
        private readonly SKFont _body = Font(SkiaFonts.PlexRegular, 8.5f);
        private readonly SKFont _small = Font(SkiaFonts.PlexRegular, 7f);
        private readonly SKFont _bold = Font(SkiaFonts.PlexSemiBold, 8.5f);
        private readonly SKFont _big = Font(SkiaFonts.PlexSemiBold, 22);

        private SKCanvas C => _canvas!;
        private float Bottom => PageH - Margin - 14;

        private static SKPaint Fill(SKColor c) => new() { IsAntialias = true, Style = SKPaintStyle.Fill, Color = c };
        private static SKPaint Stroke(SKColor c, float w) =>
            new() { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = c, StrokeWidth = w };

        private void NewPage(string section)
        {
            EndPage();
            _section = section;
            _canvas = document.BeginPage(PageW, PageH);
            _page++;
            using (var bg = Fill(SKColors.White)) C.DrawRect(0, 0, PageW, PageH, bg);

            // Running header.
            using var muted = Fill(Muted);
            using var accent = Fill(Accent);
            C.DrawRect(Margin, Margin - 10, 26, 3, accent);
            C.DrawText($"Trace Impedance Analysis — {report.Title}", Margin + 32, Margin - 5, SKTextAlign.Left, _small, muted);
            C.DrawText(section, PageW - Margin, Margin - 5, SKTextAlign.Right, _small, muted);
            _y = Margin + 12;
        }

        private void EndPage()
        {
            if (_canvas is null) return;
            using var muted = Fill(Muted);
            using var rule = Stroke(Rule, 0.6f);
            C.DrawLine(Margin, PageH - Margin + 2, PageW - Margin, PageH - Margin + 2, rule);
            // The units on EVERY page (owner, 2026-09-25): a page pulled out of the report and
            // filed on its own still says what its numbers are in.
            C.DrawText($"Target {report.TargetOhms:0.##} Ω ± {report.TolerancePercent:0.##} %  ·  " +
                       $"Units: coordinates, lengths and widths in {report.Unit}; Z0 in Ω  ·  " +
                       $"{report.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}  ·  circuitRF",
                       Margin, PageH - Margin + 13, SKTextAlign.Left, _small, muted);
            C.DrawText($"Page {_page}", PageW - Margin, PageH - Margin + 13, SKTextAlign.Right, _small, muted);
            document.EndPage();
            _canvas = null;
        }

        public void Finish() => EndPage();

        // ── text helpers ────────────────────────────────────────────────────────────────────────

        private List<string> Wrap(string text, SKFont font, float width)
        {
            var lines = new List<string>();
            foreach (var para in text.Split('\n'))
            {
                var line = "";
                foreach (var word in para.Split(' '))
                {
                    string candidate = line.Length == 0 ? word : line + " " + word;
                    if (font.MeasureText(candidate) <= width || line.Length == 0) line = candidate;
                    else { lines.Add(line); line = word; }
                }
                lines.Add(line);
            }
            return lines;
        }

        /// <summary>Wrapped text from <paramref name="y"/> down; a line that would pass
        /// <paramref name="maxY"/> is not drawn, and the last one drawn ends in an ellipsis.</summary>
        private float Paragraph(string text, float x, float y, float width, SKFont font, SKColor color,
                                float lead = 1.4f, float maxY = float.MaxValue)
        {
            using var ink = Fill(color);
            var lines = Wrap(text, font, width);
            for (int i = 0; i < lines.Count; i++)
            {
                if (y + font.Size * lead > maxY) break;
                y += font.Size * lead;
                bool last = i + 1 < lines.Count && y + font.Size * lead > maxY;
                C.DrawText(last ? Clip(lines[i] + " …", font, width) : lines[i], x, y, SKTextAlign.Left, font, ink);
            }
            return y;
        }

        private static string Clip(string text, SKFont font, float width)
        {
            if (font.MeasureText(text) <= width) return text;
            for (int n = text.Length - 1; n > 1; n--)
            {
                string c = text[..n] + "…";
                if (font.MeasureText(c) <= width) return c;
            }
            return "…";
        }

        // ── the summary page ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The summary — ALWAYS one page (owner, 2026-09-25). Two columns: what was analysed and what
        /// was found on the left, the stackup it was analysed against on the right, drawn by the
        /// renderer the Technology editor's Stackup tab paints with, labels and all. Every block on the
        /// left is written against the page's bottom and clipped there rather than continued, in
        /// order of what a reviewer needs first; the method text is last because it is the same on
        /// every report.
        /// </summary>
        public void Summary()
        {
            NewPage("Summary");
            using var ink = Fill(Ink);
            using var muted = Fill(Muted);
            using var accent = Fill(Accent);

            const float leftW = 408f, gap = 20f;
            float x0 = Margin, rightX = Margin + leftW + gap, rightW = PageW - Margin - rightX;

            _y += 16;
            C.DrawText("Trace Impedance Analysis", x0, _y, SKTextAlign.Left, _title, ink);
            _y += 18;
            C.DrawText(Clip(report.Title, _h1, leftW), x0, _y, SKTextAlign.Left, _h1, accent);
            float columnsTop = _y + 8;

            // ── left: the facts ─────────────────────────────────────────────────────────────────
            var facts = new List<(string, string)>
            {
                ("Target", $"{report.TargetOhms:0.##} Ω ± {report.TolerancePercent:0.##} %  " +
                           $"(pass band {report.LowOhms:0.0}–{report.HighOhms:0.0} Ω)"),
                ("Technology", report.TechnologyPath is { Length: > 0 } tp
                    ? $"{report.TechnologyName}  ({Path.GetFileName(tp)})" : report.TechnologyName),
                ("Layers", report.LayersRequested.Count > 0 ? string.Join(", ", report.LayersRequested)
                                                            : string.Join(", ", report.Layers.Select(l => l.Name))),
                ("Source", report.SourcePath is { Length: > 0 } sp ? Path.GetFileName(sp) : report.Title),
                ("Created", $"{report.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}  ·  {report.StationCount:N0} cross-sections, " +
                            $"{report.SolveCount:N0} solved, {report.Elapsed.TotalSeconds:0.#} s"),
            };
            float y = columnsTop;
            foreach (var (k, v) in facts)
            {
                y += 13;
                C.DrawText(k, x0, y, SKTextAlign.Left, _bold, muted);
                C.DrawText(Clip(v, _body, leftW - 72), x0 + 72, y, SKTextAlign.Left, _body, ink);
            }
            _y = y + 12;

            if (report.Cancelled)
            {
                using var warn = Fill(new SKColor(0xfd, 0xf1, 0xe6));
                using var warnInk = Fill(new SKColor(0x9a, 0x4a, 0x00));
                C.DrawRoundRect(new SKRect(x0, _y, x0 + leftW, _y + 20), 3, 3, warn);
                C.DrawText(Clip($"Cancelled — the {report.Layers.Count} of {report.LayersRequested.Count} layers that " +
                                "finished before the run was stopped.", _bold, leftW - 16),
                           x0 + 8, _y + 13.5f, SKTextAlign.Left, _bold, warnInk);
                _y += 28;
            }

            // Tiles.
            var tiles = new (string Label, string Value, SKColor Color)[]
            {
                ("Traces analysed", report.TraceCount.ToString(), Ink),
                ("Pass", report.PassCount.ToString(), PassInk),
                ("Fail", report.FailCount.ToString(), FailInk),
                ("Layers", report.Layers.Count.ToString(), Ink),
            };
            float tileW = (leftW - 3 * 10) / 4f;
            for (int i = 0; i < tiles.Length; i++)
            {
                float x = x0 + i * (tileW + 10);
                using var band = Fill(Band);
                using var valueInk = Fill(tiles[i].Color);
                C.DrawRoundRect(new SKRect(x, _y, x + tileW, _y + 46), 4, 4, band);
                C.DrawText(tiles[i].Value, x + 10, _y + 27, SKTextAlign.Left, _big, valueInk);
                C.DrawText(tiles[i].Label, x + 10, _y + 40, SKTextAlign.Left, _small, muted);
            }
            _y += 60;

            // Per-layer table — every layer, however many; a layer is one short row.
            C.DrawText("By layer", x0, _y, SKTextAlign.Left, _h2, ink);
            _y += 5;
            float[] cols = [x0, x0 + 120, x0 + 166, x0 + 204, x0 + 242, x0 + 290];
            string[] heads = ["Layer", "Traces", "Pass", "Fail", "Pours", "Worst Z0 excursion"];
            Header(cols, heads, x0 + leftW);
            foreach (var l in report.Layers)
            {
                double? worst = l.Traces.SelectMany(t => new[] { t.Z0Min, t.Z0Max })
                                        .Where(z => z is not null)
                                        .Select(z => z!.Value)
                                        .OrderByDescending(z => Math.Abs(z - report.TargetOhms))
                                        .Cast<double?>().FirstOrDefault();
                Row(cols, [l.Name, l.Traces.Count.ToString(),
                           l.Traces.Count(t => t.Verdict == TraceVerdict.Pass).ToString(),
                           l.Traces.Count(t => t.Verdict == TraceVerdict.Fail).ToString(),
                           l.PoursSkipped.ToString(),
                           worst is { } w ? $"{w:0.0} Ω ({(w - report.TargetOhms) / report.TargetOhms:+0%;-0%})" : "—"],
                    [Ink, Ink, PassInk, FailInk, Muted, Ink], right: x0 + leftW);
            }
            _y += 16;

            // Line types.
            if (_y + 20 < Bottom)
            {
                C.DrawText("Line types", x0, _y, SKTextAlign.Left, _h2, ink);
                using var smallBold = Font(SkiaFonts.PlexSemiBold, _small.Size);
                _y += 1;
                foreach (var (shortName, meaning) in TraceImpedanceReport.TypeLegend)
                {
                    if (_y + _small.Size * 1.5f > Bottom) break;
                    _y += _small.Size * 1.5f;
                    C.DrawText(shortName, x0, _y, SKTextAlign.Left, smallBold, ink);
                    C.DrawText(Clip(meaning, _small, leftW - 72), x0 + 72, _y, SKTextAlign.Left, _small, muted);
                }
                _y += 12;
            }

            foreach (var note in report.Notes)
                _y = Paragraph("• " + note, x0, _y, leftW, _small, Ink, 1.35f, Bottom) + 2;

            // Method — last, and clipped to the page.
            if (_y + 24 < Bottom)
            {
                _y += 8;
                C.DrawText("How it was measured", x0, _y, SKTextAlign.Left, _h2, ink);
                string method =
                    "Traces are found in the copper as drawn: two long parallel edges facing each other are a trace, and " +
                    "stretches meeting within about a width — through a jog, a bend, a mitre or a width step — are one " +
                    "trace. A junction, a via, a pad and the end of the copper end a trace; what continues on another layer " +
                    "is that layer's trace. Pours and planes are not analysed. Each trace is cut once per width, and each cut " +
                    "is a quasi-static cross-section solve over the stackup at right: the reference below (and above) is " +
                    "the nearest layer whose copper covers the trace's whole width there, and every other conductor near " +
                    "it is held at ground. Bend corners are not cut and not flagged. A trace FAILS when any of it is " +
                    "outside target ± tolerance, when the nearest layer under (or over) it stops covering it part of the " +
                    "way, or when its reference steps to another layer. The solve is lossless and frequency-independent: " +
                    "a review of the geometry, not a replacement for an EM run.";
                _y = Paragraph(method, x0, _y + 1, leftW, _small, Muted, 1.35f, Bottom);
            }

            // ── right: the stackup ──────────────────────────────────────────────────────────────
            DrawStackup(new SKRect(rightX, columnsTop, rightX + rightW, Bottom));
        }

        /// <summary>The stackup the analysis cut through, as the Technology editor's Stackup tab
        /// draws it — bands, vias and the tab's own text labels — scaled uniformly into
        /// <paramref name="box"/>, with the technology's name and <c>.ctech</c> file above it.</summary>
        private void DrawStackup(SKRect box)
        {
            using var ink = Fill(Ink);
            using var muted = Fill(Muted);
            float y = box.Top + 13;
            C.DrawText("Stackup", box.Left, y, SKTextAlign.Left, _h2, ink);
            y += 12;
            C.DrawText(Clip(report.TechnologyName, _body, box.Width), box.Left, y, SKTextAlign.Left, _body, ink);
            if (report.TechnologyPath is { Length: > 0 } path)
            {
                y += 11;
                C.DrawText(Clip(Path.GetFileName(path), _small, box.Width), box.Left, y, SKTextAlign.Left, _small, muted);
            }
            y += 8;

            if (report.Technology is not { } tech)
            {
                C.DrawText("No stackup.", box.Left, y + 12, SKTextAlign.Left, _body, muted);
                return;
            }

            // Laid out wide enough that no spec wraps — the width the tab's own copy-as-picture uses —
            // then scaled down to the column, uniformly: a stackup stretched on one axis misstates
            // every thickness in it.
            // The narrowest layout that keeps the tab's label column BESIDE the bands (a narrower one
            // stacks the specs beneath the drawing, which the page has no height for), widened until no
            // spec wraps — so the scale-down to the column is as small as it can be.
            float w = 400f;
            while (w < 1600f && StackupScene.Build(tech, w).LabelColumnDropped) w += 20f;
            var scene = StackupScene.Build(tech, StackupScene.WidthThatFitsLabels(tech, w, 1600f));
            var area = new SKRect(box.Left, y, box.Right, box.Bottom);
            float scale = Math.Min(area.Width / Math.Max(1f, scene.Width), area.Height / Math.Max(1f, scene.Height));
            scale = Math.Min(scale, 1f);

            using (var frame = Stroke(Rule, 0.6f))
                C.DrawRoundRect(new SKRect(area.Left, area.Top, area.Left + scene.Width * scale, area.Top + scene.Height * scale), 3, 3, frame);
            C.Save();
            C.Translate(area.Left, area.Top);
            C.Scale(scale);
            StackupRenderer.Draw(C, scene, StackupRenderTheme.Light, transparentBackground: true);
            C.Restore();
        }

        private void Header(float[] cols, string[] heads, float right = PageW - Margin)
        {
            using var band = Fill(Band);
            using var muted = Fill(Muted);
            C.DrawRect(cols[0], _y, right - cols[0], 15, band);
            using var headFont = Font(SkiaFonts.PlexSemiBold, 7.5f);
            for (int i = 0; i < heads.Length; i++)
                C.DrawText(heads[i], cols[i] + 3, _y + 10.5f, SKTextAlign.Left, headFont, muted);
            _y += 15;
        }

        private void Row(float[] cols, string[] cells, SKColor[] colors, SKColor? background = null,
                         float right = PageW - Margin)
        {
            if (background is { } bg) using (var p = Fill(bg)) C.DrawRect(cols[0], _y, right - cols[0], 13, p);
            for (int i = 0; i < cells.Length; i++)
            {
                float width = (i + 1 < cols.Length ? cols[i + 1] : right) - cols[i] - 6;
                using var ink = Fill(colors[Math.Min(i, colors.Length - 1)]);
                C.DrawText(Clip(cells[i], _body, width), cols[i] + 3, _y + 9.5f, SKTextAlign.Left, _body, ink);
            }
            _y += 13;
            using var rule = Stroke(Rule, 0.4f);
            C.DrawLine(cols[0], _y, right, _y, rule);
        }

        // ── the map page ────────────────────────────────────────────────────────────────────────

        /// <summary>Every finding on the layer, numbered in the order the table lists them.</summary>
        private static List<(int N, TraceRun Trace, TraceIssue Issue)> Findings(TraceLayerResult layer)
        {
            var list = new List<(int, TraceRun, TraceIssue)>();
            int n = 0;
            foreach (var t in layer.Traces)
                foreach (var i in t.Issues) list.Add((++n, t, i));
            return list;
        }

        public void LayerMap(TraceLayerResult layer)
        {
            NewPage($"{layer.Name} — map");
            using var ink = Fill(Ink);
            using var muted = Fill(Muted);

            _y += 14;
            C.DrawText(layer.Name, Margin, _y, SKTextAlign.Left, _h1, ink);
            int pass = layer.Traces.Count(t => t.Verdict == TraceVerdict.Pass);
            int fail = layer.Traces.Count(t => t.Verdict == TraceVerdict.Fail);
            C.DrawText($"{layer.Traces.Count} traces  ·  {pass} pass  ·  {fail} fail" +
                       (layer.PoursSkipped > 0 ? $"  ·  {layer.PoursSkipped} pours not analysed" : ""),
                       Margin + _h1.MeasureText(layer.Name) + 12, _y, SKTextAlign.Left, _body, muted);
            _y += 8;

            const float sideW = 170;
            var box = new SKRect(Margin, _y, PageW - Margin - sideW - 14, Bottom);
            DrawMap(layer, box);
            DrawLegend(new SKRect(box.Right + 14, _y, PageW - Margin, Bottom), layer);
        }

        private void DrawMap(TraceLayerResult layer, SKRect box)
        {
            using (var frame = Stroke(Rule, 0.6f)) C.DrawRect(box, frame);

            var ext = report.Extent;
            double extW = ext.MaxX - (double)ext.MinX, extH = ext.MaxY - (double)ext.MinY;
            if (ext.IsEmpty || extW <= 0 || extH <= 0) return;
            float pad = 8;
            double sx = (box.Width - 2 * pad) / extW, sy = (box.Height - 2 * pad) / extH;
            double s = Math.Min(sx, sy);
            double ox = box.Left + pad + 0.5 * (box.Width - 2 * pad - extW * s);
            double oy = box.Top + pad + 0.5 * (box.Height - 2 * pad - extH * s);
            SKPoint P(double x, double y) => new((float)(ox + (x - ext.MinX) * s), (float)(oy + (ext.MaxY - y) * s));

            C.Save();
            C.ClipRect(box);

            // The copper.
            using (var path = new SKPath { FillType = SKPathFillType.EvenOdd })
            {
                foreach (var ring in layer.Copper)
                {
                    if (ring.Length < 6) continue;
                    path.MoveTo(P(ring[0], ring[1]));
                    for (int i = 2; i < ring.Length; i += 2) path.LineTo(P(ring[i], ring[i + 1]));
                    path.Close();
                }
                using var fill = Fill(CopperFill);
                using var edge = Stroke(CopperEdge, 0.3f);
                C.DrawPath(path, fill);
                C.DrawPath(path, edge);
            }

            // The traces, station by station.
            foreach (var t in layer.Traces)
                foreach (var st in t.Stations)
                {
                    double dx = st.Uy, dy = -st.Ux;   // along the trace
                    var a = P(st.X - 0.5 * st.Length * dx, st.Y - 0.5 * st.Length * dy);
                    var b = P(st.X + 0.5 * st.Length * dx, st.Y + 0.5 * st.Length * dy);
                    float w = (float)Math.Max(st.Width * s, 1.4);
                    using var paint = Stroke(st.Z0 is { } z ? Z0Color(z, report.TargetOhms, report.TolerancePercent) : Unsolved, w);
                    paint.StrokeCap = SKStrokeCap.Butt;
                    C.DrawLine(a, b, paint);
                }

            // Finding markers: the stretch outlined, the number in a disc.
            foreach (var (n, trace, issue) in Findings(layer))
            {
                // An out-of-band stretch is already in its colour, so only the number marks it; a
                // return-path finding has no colour of its own and gets its stretch outlined.
                bool ground = issue.Kind != TraceIssueKind.OutOfTolerance;
                var color = ground ? GroundMark : ZMark;
                if (ground)
                {
                    var a = P(issue.X0, issue.Y0); var b = P(issue.X1, issue.Y1);
                    using var span = Stroke(color, 1.1f);
                    span.StrokeCap = SKStrokeCap.Round;
                    span.PathEffect = SKPathEffect.CreateDash([2.5f, 1.5f], 0);
                    float half = (float)Math.Max(trace.Stations.FirstOrDefault()?.Width * s ?? 2, 2) / 2 + 2.2f;
                    double len = Math.Max(1e-6, Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2)));
                    float nx = (float)(-(b.Y - a.Y) / len * half), ny = (float)((b.X - a.X) / len * half);
                    if (len < 1) { C.DrawCircle(a, half + 1, span); }
                    else
                    {
                        using var path = new SKPath();
                        path.MoveTo(a.X + nx, a.Y + ny); path.LineTo(b.X + nx, b.Y + ny);
                        path.LineTo(b.X - nx, b.Y - ny); path.LineTo(a.X - nx, a.Y - ny); path.Close();
                        C.DrawPath(path, span);
                    }
                }
                var m = P(issue.X, issue.Y);
                using var disc = Fill(color);
                using var ring = Stroke(SKColors.White, 0.7f);
                C.DrawCircle(m.X + 5, m.Y - 5, 4.3f, disc);
                C.DrawCircle(m.X + 5, m.Y - 5, 4.3f, ring);
                using var white = Fill(SKColors.White);
                using var f = Font(SkiaFonts.PlexSemiBold, n >= 100 ? 3.4f : 4.6f);
                C.DrawText(n.ToString(), m.X + 5, m.Y - 3.4f, SKTextAlign.Center, f, white);
            }

            // Trace ids, at each trace's longest piece, with a white halo.
            using var idFont = Font(SkiaFonts.PlexSemiBold, 5.5f);
            foreach (var t in layer.Traces)
            {
                var p = t.Pieces.OrderByDescending(q => Math.Pow(q.X1 - q.X0, 2) + Math.Pow(q.Y1 - q.Y0, 2)).First();
                var at = P(0.5 * (p.X0 + p.X1), 0.5 * (p.Y0 + p.Y1));
                using var halo = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = SKColors.White };
                using var inkId = Fill(Ink);
                C.DrawText(t.Id, at.X + 3, at.Y - 2, SKTextAlign.Left, idFont, halo);
                C.DrawText(t.Id, at.X + 3, at.Y - 2, SKTextAlign.Left, idFont, inkId);
            }
            C.Restore();

            // Scale bar.
            double target = extW * 0.2;
            double nice = Math.Pow(10, Math.Floor(Math.Log10(target)));
            foreach (double m in (ReadOnlySpan<double>)[5, 2, 1]) if (m * nice <= target) { nice *= m; break; }
            float barW = (float)(nice * s);
            float bx = box.Left + 10, by = box.Bottom - 10;
            using (var bar = Stroke(Ink, 1.2f))
            {
                C.DrawLine(bx, by, bx + barW, by, bar);
                C.DrawLine(bx, by - 3, bx, by + 1, bar);
                C.DrawLine(bx + barW, by - 3, bx + barW, by + 1, bar);
            }
            using var inkS = Fill(Ink);
            C.DrawText(report.Len(nice), bx + barW + 5, by + 2.5f, SKTextAlign.Left, _small, inkS);
        }

        private void DrawLegend(SKRect r, TraceLayerResult layer)
        {
            using var ink = Fill(Ink);
            using var muted = Fill(Muted);
            float y = r.Top + 12;
            C.DrawText("Z0 along each trace", r.Left, y, SKTextAlign.Left, _h2, ink);
            y += 10;

            // The scale: target ± 45 %, with the pass band marked.
            double t = report.TargetOhms, lo = t * 0.55, hi = t * 1.45;
            float barH = 150, barW = 14;
            var bar = new SKRect(r.Left, y, r.Left + barW, y + barH);
            for (int i = 0; i < 60; i++)
            {
                double z = hi - (hi - lo) * (i + 0.5) / 60;
                using var p = Fill(Z0Color(z, t, report.TolerancePercent));
                C.DrawRect(bar.Left, bar.Top + barH * i / 60f, barW, barH / 60f + 0.5f, p);
            }
            float Yz(double z) => (float)(bar.Top + (hi - z) / (hi - lo) * barH);
            using (var br = Stroke(Ink, 0.8f))
            {
                foreach (var z in new[] { report.HighOhms, report.LowOhms })
                    C.DrawLine(bar.Left - 3, Yz(z), bar.Right + 3, Yz(z), br);
            }
            foreach (var z in new[] { hi, report.HighOhms, t, report.LowOhms, lo })
                C.DrawText($"{z:0.#} Ω" + (z == hi ? "+" : z == lo ? "−" : ""), bar.Right + 7, Yz(z) + 3, SKTextAlign.Left, _small, z == t ? ink : muted);
            C.DrawText("pass band", bar.Right + 50, Yz(t) + 3, SKTextAlign.Left, _small, Fill(PassInk));
            y = bar.Bottom + 12;

            void Key(SKColor c, string text, bool disc)
            {
                using var p = Fill(c);
                float top = y;
                if (disc) C.DrawCircle(r.Left + 5, top + 5.5f, 4.5f, p);
                else C.DrawRect(r.Left, top + 3.5f, 12, 4, p);
                y = Paragraph(text, r.Left + 16, top - 1.5f, r.Width - 16, _small, Muted, 1.35f) + 6;
            }
            Key(Unsolved, "not solved (no return path, or the cut left the copper)", false);
            Key(ZMark, "numbered: Z0 outside the pass band", true);
            Key(GroundMark, "numbered, with the stretch outlined: return path broken or partial, or the reference steps to another layer", true);
            y += 10;

            // The worst findings on this page, briefly; the full list follows the table.
            var findings = Findings(layer);
            if (findings.Count > 0)
            {
                C.DrawText("Findings", r.Left, y, SKTextAlign.Left, _h2, ink);
                y += 3;
                foreach (var (n, trace, issue) in findings)
                {
                    string text = $"{n}. {trace.Id} — {Brief(issue)}";
                    var lines = Wrap(text, _small, r.Width);
                    if (y + lines.Count * 9.5f > r.Bottom - 12)
                    {
                        C.DrawText($"… {findings.Count - n + 1} more, listed after the table", r.Left, y + 9, SKTextAlign.Left, _small, muted);
                        break;
                    }
                    y = Paragraph(text, r.Left, y, r.Width, _small, Ink, 1.35f) + 1.5f;
                }
            }
            else
            {
                C.DrawText(layer.Traces.Count == 0 ? "No traces on this layer." : "No findings on this layer.",
                           r.Left, y + 2, SKTextAlign.Left, _body, layer.Traces.Count == 0 ? muted : Fill(PassInk));
            }
        }

        private static string Brief(TraceIssue i) => i.Kind switch
        {
            TraceIssueKind.OutOfTolerance => i.Text.Split(" from ")[0].Replace("Z0 ", "Z0 "),
            TraceIssueKind.ReturnBroken   => "return broken: " + i.Text.Split(" is missing")[0] + " missing",
            TraceIssueKind.PartialReference => "partial reference: " + i.Text.Split(" for ")[0],
            TraceIssueKind.ReferenceStep  => i.Text.Split(" at (")[0],
            TraceIssueKind.NoReference    => "no return path",
            _                             => "not solved",
        };

        // ── the table and the findings ──────────────────────────────────────────────────────────

        private static readonly float[] Cols =
            [Margin, Margin + 26, Margin + 96, Margin + 166, Margin + 254, Margin + 302, Margin + 350, Margin + 474, Margin + 516, Margin + 558, Margin + 600, Margin + 634, Margin + 710, Margin + 742];
        private string[] Heads
        {
            get
            {
                string u = report.Unit;
                return ["ID", $"Start ({u})", $"End ({u})", "Ends", $"Length ({u})", $"Width ({u})", "Type",
                        "Z0 min (Ω)", "Z0 max (Ω)", "Z0 avg (Ω)", "In tol.", "Reference", "Result", "#"];
            }
        }

        public void LayerTable(TraceLayerResult layer)
        {
            string section = $"{layer.Name} — traces";
            NewPage(section);
            using var ink = Fill(Ink);
            _y += 14;
            C.DrawText($"{layer.Name}: traces", Margin, _y, SKTextAlign.Left, _h1, ink);
            _y += 10;

            if (layer.Traces.Count == 0)
            {
                Paragraph("No traces were found on this layer: its copper is a plane or a pour, or nothing on it has two " +
                          "long parallel edges facing each other.", Margin, _y, PageW - 2 * Margin, _body, Muted);
                return;
            }

            Header(Cols, Heads);
            int row = 0;
            foreach (var t in layer.Traces)
            {
                if (_y + 13 > Bottom) { NewPage(section + " (continued)"); _y += 6; Header(Cols, Heads); }
                string Pt(long x, long y) =>
                    $"{report.Num(x, 0)}, {report.Num(y, 0)}";
                string width = Math.Abs(t.WidthMax - t.WidthMin) < 0.5 * report.DbuPerMicron
                    ? report.Num(t.WidthMin, 1)
                    : $"{report.Num(t.WidthMin, 0)}–{report.Num(t.WidthMax, 0)}";
                var verdictColor = t.Verdict switch { TraceVerdict.Pass => PassInk, TraceVerdict.Fail => FailInk, _ => Muted };
                Row(Cols,
                    [t.Id, Pt(t.StartX, t.StartY), Pt(t.EndX, t.EndY), $"{t.StartsAt} / {t.EndsAt}",
                     report.Num(t.Length, 0), width,
                     t.TypeSummary, Ohms(t.Z0Min), Ohms(t.Z0Max), Ohms(t.Z0Mean), $"{t.InTolerance:0%}",
                     string.Join(", ", t.References), VerdictText(t.Verdict), t.Issues.Count == 0 ? "" : t.Issues.Count.ToString()],
                    [Ink, Muted, Muted, Muted, Ink, Ink, Ink,
                     Zc(t.Z0Min), Zc(t.Z0Max), Zc(t.Z0Mean), Ink, Muted, verdictColor, Ink],
                    row++ % 2 == 1 ? new SKColor(0xfa, 0xfb, 0xfc) : null);
            }
            using var muted = Fill(Muted);
            _y += 12;
            C.DrawText($"Coordinates and lengths in {report.Unit}; Z0 in Ω. " +
                       "\"In tol.\" is the share of the trace's solved length inside the pass band. \"Type\" is the line type " +
                       "along the trace, with each type's share where it is more than one.",
                       Margin, _y, SKTextAlign.Left, _small, muted);
            _y += 10;

            // The findings, numbered as on the map.
            var findings = Findings(layer);
            var notes = layer.Traces.SelectMany(t => t.Notes.Select(n => (t.Id, n))).ToList();
            if (findings.Count == 0 && notes.Count == 0) return;
            if (_y + 40 > Bottom) { NewPage(section + " (continued)"); }
            _y += 10;
            C.DrawText("Findings", Margin, _y, SKTextAlign.Left, _h2, ink);
            _y += 2;
            foreach (var (n, trace, issue) in findings)
            {
                var lines = Wrap($"{trace.Id}: {issue.Text}", _body, PageW - 2 * Margin - 24);
                if (_y + lines.Count * 12 + 2 > Bottom) { NewPage(section + " (continued)"); _y += 6; }
                var color = issue.Kind == TraceIssueKind.OutOfTolerance ? ZMark : GroundMark;
                using (var disc = Fill(color)) C.DrawCircle(Margin + 6, _y + 8.5f, 5.5f, disc);
                using (var white = Fill(SKColors.White))
                using (var f = Font(SkiaFonts.PlexSemiBold, n >= 100 ? 4.5f : 6f))
                    C.DrawText(n.ToString(), Margin + 6, _y + 10.6f, SKTextAlign.Center, f, white);
                _y = Paragraph($"{trace.Id}: {issue.Text}", Margin + 18, _y - 1, PageW - 2 * Margin - 24, _body, Ink) + 4;
            }
            if (notes.Count > 0)
            {
                if (_y + 30 > Bottom) NewPage(section + " (continued)");
                _y += 8;
                C.DrawText("Notes", Margin, _y, SKTextAlign.Left, _h2, ink);
                foreach (var (tid, note) in notes)
                {
                    if (_y + 24 > Bottom) { NewPage(section + " (continued)"); _y += 6; }
                    _y = Paragraph($"{tid}: {note}", Margin, _y, PageW - 2 * Margin, _body, Muted) + 2;
                }
            }
        }

        private SKColor Zc(double? z) =>
            z is not { } v ? Muted : v >= report.LowOhms && v <= report.HighOhms ? PassInk : FailInk;
    }
}
