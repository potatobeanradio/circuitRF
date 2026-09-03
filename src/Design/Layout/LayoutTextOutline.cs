// Bridges a Label's text into DBU-space glyph contours (R-lbl-4/R-lbl-6, docs/sonnet-briefs/
// brief-layout-label-fix-and-text-flatten.md) — the ONE place text-to-polygon flattening touches
// SkiaSharp. Everything downstream (flattening the resulting Cubic edges via LayoutFlattener, nesting
// via Clipper2, emitting PolygonShapes, undo) is framework-free, in
// CircuitRF.Design.Layout.LayoutTextFlatten — this file only builds its INPUT.

using System.Collections.Generic;
using SkiaSharp;

namespace CircuitRF.Design.Layout;

public static class LayoutTextOutline
{
    /// <summary>Test-only override (set only by <c>CircuitRF.Ui.Tests</c>, via <c>InternalsVisibleTo</c>)
    /// substituting <c>SKTypeface.Default</c> — the one typeface guaranteed loadable without a live
    /// Avalonia app host — so the FULL flatten pipeline, including every VM entry point
    /// (<c>LayoutEditorViewModel.FlattenSelectionToPolygon</c>/<c>FlattenAllCurves</c>), can be
    /// exercised headlessly. Production code (and <c>LayoutRenderer.DrawLabelText</c>) never
    /// sets this — every real label still flattens with the exact typeface it renders with.</summary>
    internal static SKTypeface? TestOverrideTypeface;

    /// <summary>
    /// Where the four real typefaces come from. <c>src/Ui</c> installs the embedded IBM Plex faces
    /// into this the moment its assembly loads (<c>UiTypefaceInstaller</c>, a module initializer), and
    /// nothing else ever sets it.
    ///
    /// <para><b>Why it is a seam rather than a direct call.</b> The faces load through Avalonia's
    /// <c>AssetLoader</c>, and this file now lives in <c>CircuitRF.Design</c>, on the far side of the
    /// UI firewall — <c>circuitrf convert</c> flattens labels to polygons with no Avalonia in the
    /// process at all. Unset, it falls back to <see cref="SKTypeface.Default"/>, which is the one
    /// typeface guaranteed loadable with no asset system.</para>
    ///
    /// <para><b>And that fallback is a real difference, not a formality</b>: glyph outlines from a
    /// different face are a different SHAPE, so label geometry flattened headlessly is not byte-identical
    /// to the same label flattened in the app. Every export that carries a label says so.</para>
    /// </summary>
    public static Func<LabelFontStyle, SKTypeface>? TypefaceSource;

    /// <summary>True when the real embedded faces are available — <c>false</c> in a headless process,
    /// which is what an exporter reports rather than silently substituting a face.</summary>
    public static bool HasEmbeddedTypefaces => TypefaceSource is not null;

    /// <summary>The ONE mapping from a label's <see cref="LabelFontStyle"/> to the typeface it
    /// renders/flattens with — shared by <c>LayoutRenderer.DrawLabelText</c>,
    /// <c>LayoutRenderer.MeasureLabelWorldBbox</c>, and <see cref="BuildGlyphContours"/>, so all
    /// three can never disagree about what a given label looks like.
    /// <see cref="TestOverrideTypeface"/> always wins when set, so a headless test never needs to know
    /// or vary a label's style to substitute a loadable typeface.</summary>
    public static SKTypeface ResolveTypeface(LabelFontStyle style) =>
        TestOverrideTypeface ?? TypefaceSource?.Invoke(style) ?? SKTypeface.Default;

    /// <summary>
    /// One <see cref="CurveShape"/> per glyph CONTOUR — 'O' has two (outer ring + inner counter),
    /// '8' has three, 'i' has two disconnected pieces (dot + stem). Nesting (which contour is a hole
    /// vs. a separate outer boundary) is NOT decided here — that is <c>LayoutTextFlatten</c>'s job, via
    /// Clipper2. Uses the SAME typeface
    /// <c>LayoutRenderer.DrawLabelText</c> renders with, and mirrors that method's transform EXACTLY
    /// (including its Y-down-path-space rotation-sign negation) so the flattened result matches what the
    /// user actually sees rather than a re-derived approximation. <paramref name="label"/>'s own DBU
    /// <c>Height</c> is used directly as the SkFont size — Skia doesn't care about units, so the
    /// returned glyph path is already in exact DBU-scale coordinates, needing no further scaling
    /// (only rotation + translation, both applied below). Returns an empty list for blank text or a
    /// non-positive height. <paramref name="typeface"/> is test-only visibility into the algorithm
    /// (headless xunit tests cannot resolve the embedded faces at all — they load via
    /// Avalonia's <c>AssetLoader</c>, which throws <c>InvalidOperationException</c> with no live
    /// Avalonia app host; confirmed empirically, not assumed) — production code never passes it, so
    /// every real label still flattens with the exact typeface it renders with.
    /// </summary>
    public static List<CurveShape> BuildGlyphContours(LabelShape label, SKTypeface? typeface = null)
    {
        var contours = new List<CurveShape>();
        if (string.IsNullOrEmpty(label.Text) || label.Height <= 0) return contours;

        using var font = new SKFont(typeface ?? ResolveTypeface(label.Style), label.Height);

        // The label's own anchor, from the SAME resolver DrawLabelText uses, so a flattened label lands
        // exactly where the rendered one was. `centred: false` deliberately — the port override is a
        // RENDER-time decision about a port's mark, and a label with no alignment of its own (every
        // .clay written before those fields existed) resolves to left-of-baseline, i.e. offset (0, 0),
        // which is precisely what this method did before.
        var (align, baselineDy) = ResolveLabelAnchor(label, font, centred: false);
        float alignDx = align switch
        {
            SKTextAlign.Center => -font.MeasureText(label.Text) / 2f,
            SKTextAlign.Right  => -font.MeasureText(label.Text),
            _                  => 0f,
        };
        using var glyphPath = font.GetTextPath(label.Text, new SKPoint(alignDx, baselineDy));

        // Mirrors LayoutRenderer.DrawLabelText exactly — path space is Y-down, so the DBU-space (Y-up)
        // counter-clockwise angle is simply negated.
        float rotationDeg = -(float)label.RotationDegrees;
        if (rotationDeg != 0f)
            glyphPath.Transform(SKMatrix.CreateRotationDegrees(rotationDeg));

        // DrawLabelText draws at path-space (label.X, label.Y) with Y flipped once (DBU is Y-up, path
        // space is Y-down) — the same single negation applied here, directly in DBU, no PathSpace/zoom
        // involved (flattening must be viewport-independent, unlike rendering).
        (long X, long Y) ToDbu(SKPoint p) =>
            (label.X + (long)System.Math.Round(p.X), label.Y - (long)System.Math.Round(p.Y));

        var xy = new List<long>();
        var edges = new List<LayoutEdge>();

        void FlushContour()
        {
            if (xy.Count >= 6) // >= 3 vertices — anything smaller is a degenerate/empty contour
                contours.Add(new CurveShape { Layer = label.Layer, Net = label.Net, Xy = [.. xy], Edges = [.. edges] });
            xy = [];
            edges = [];
        }

        using var iter = glyphPath.CreateIterator(false);
        var pts = new SKPoint[4];
        SKPathVerb verb;
        while ((verb = iter.Next(pts)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move:
                    FlushContour();
                    var (mx, my) = ToDbu(pts[0]);
                    xy.Add(mx); xy.Add(my);
                    break;

                case SKPathVerb.Line:
                {
                    var (lx, ly) = ToDbu(pts[1]);
                    xy.Add(lx); xy.Add(ly);
                    edges.Add(new LayoutEdge { Kind = EdgeKind.Line });
                    break;
                }

                case SKPathVerb.Quad:
                {
                    // Exact quadratic-to-cubic degree elevation — LayoutEdge has no Quad kind, and
                    // elevation (unlike flattening) loses nothing: C1 = P0 + 2/3(Pc-P0), C2 = P2 + 2/3(Pc-P2).
                    var p0 = pts[0]; var pc = pts[1]; var p2 = pts[2];
                    var c1 = new SKPoint(p0.X + 2f / 3f * (pc.X - p0.X), p0.Y + 2f / 3f * (pc.Y - p0.Y));
                    var c2 = new SKPoint(p2.X + 2f / 3f * (pc.X - p2.X), p2.Y + 2f / 3f * (pc.Y - p2.Y));
                    AddCubicEdge(xy, edges, ToDbu(c1), ToDbu(c2), ToDbu(p2));
                    break;
                }

                case SKPathVerb.Cubic:
                    AddCubicEdge(xy, edges, ToDbu(pts[1]), ToDbu(pts[2]), ToDbu(pts[3]));
                    break;

                case SKPathVerb.Close:
                    // Glyph contours are drawn fully closed (the last on-curve point already equals the
                    // Move point) — drop that duplicate closing vertex so Xy matches every other closed
                    // shape's "never repeat vertex 0" convention. Deliberately does NOT also drop the
                    // trailing edge: that edge (leaving the now-second-to-last vertex) IS the implicit
                    // closing edge LayoutFlattener.FlattenClosedEdgeList expects at index n-1 — removing
                    // it would silently straight-line what may be a curved closing segment.
                    if (xy.Count >= 4 && xy[^2] == xy[0] && xy[^1] == xy[1])
                        xy.RemoveRange(xy.Count - 2, 2);
                    break;
            }
        }
        FlushContour();
        return contours;
    }

    private static void AddCubicEdge(List<long> xy, List<LayoutEdge> edges,
        (long X, long Y) c1, (long X, long Y) c2, (long X, long Y) end)
    {
        xy.Add(end.X); xy.Add(end.Y);
        edges.Add(new LayoutEdge { Kind = EdgeKind.Cubic, C1X = c1.X, C1Y = c1.Y, C2X = c2.X, C2Y = c2.Y });
    }

    /// <summary>The anchor arithmetic <c>LayoutRenderer.DrawLabelText</c> draws with and
    /// <see cref="BuildGlyphContours"/> flattens with — ONE copy, so a flattened label can never
    /// land somewhere the rendered one did not.</summary>
    internal static (SKTextAlign Align, float BaselineDy) ResolveLabelAnchor(
        LabelShape label, SKFont font, bool centred)
    {
        var h = centred ? LabelHAlign.Center : label.HAlign ?? LabelHAlign.Left;
        var v = centred ? LabelVAlign.Middle : label.VAlign ?? LabelVAlign.Baseline;

        var align = h switch
        {
            LabelHAlign.Center => SKTextAlign.Center,
            LabelHAlign.Right  => SKTextAlign.Right,
            _                  => SKTextAlign.Left,
        };

        // Skia's Ascent is NEGATIVE (up from the baseline) and Descent positive, both in this Y-down
        // frame — so "hang the text below the anchor" is a POSITIVE baseline shift of -Ascent.
        float dy = v switch
        {
            LabelVAlign.Top    => -font.Metrics.Ascent,
            LabelVAlign.Bottom => -font.Metrics.Descent,
            // Half an x-height, not half the em box: a baseline through the anchor puts the whole
            // glyph above it. This is the exact expression every port has always used.
            LabelVAlign.Middle => -0.5f * font.Metrics.Ascent * 0.5f,
            _                  => 0f,
        };
        return (align, dy);
    }
}
