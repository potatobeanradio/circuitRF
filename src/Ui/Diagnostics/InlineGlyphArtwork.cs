using System;
using System.Collections.Generic;
using System.IO;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Theming;
using SkiaSharp;

namespace CircuitRF.Ui.Diagnostics;

/// <summary>
/// Small vector marks the documentation puts INSIDE a table cell, drawn by the same code the
/// application draws them with.
///
/// <para><b>Why these are not words.</b> A feature table that says "diamond", "square", "×" in a
/// Glyph column is asking the reader to match a description against a picture — and it is a copy of
/// a fact the renderer already owns, so it goes stale the first time a shape is changed and says
/// nothing when it does (owner, 2026-08-20). Drawing the real glyph makes the column answer the
/// question the reader actually has.</para>
///
/// <para>These are not <see cref="FigureCatalog"/> rows: a figure is a captured window with a
/// caption, and one of these is a 16-pixel mark with no frame, no caption and no interface around
/// it. They share the emit path — <see cref="SvgPostPass"/>, and the same id scoping every other
/// inlined SVG needs — and nothing else.</para>
/// </summary>
public static class InlineGlyphArtwork
{
    /// <summary>File stem prefix. The page cites <c>{{snapglyph: pin}}</c>; the file is this + id.</summary>
    public const string SnapGlyphStem = "snap-glyph-";

    /// <summary>The docs stylesheet's own <c>--text</c>, light and dark.</summary>
    /// <remarks>
    /// The editor draws this glyph in its SOURCE LAYER's colour, which is the whole point there —
    /// it tells you which layer you are about to snap to. In a table cell there is no layer and no
    /// canvas, so layer colour would be arbitrary; the owner asked for the surrounding text's colour
    /// (2026-08-20), which also keeps the mark legible in both themes without a second decision.
    /// </remarks>
    private static SKColor TextColor(ColorVariant variant)
        => variant == ColorVariant.Dark ? new SKColor(0xE6, 0xED, 0xF3) : new SKColor(0x1A, 0x26, 0x32);

    /// <summary>Every snap glyph the feature table cites, keyed by the id a page writes.</summary>
    public static readonly IReadOnlyList<(string Id, SnapFeatureKind Kind)> SnapGlyphs =
    [
        ("pin",          SnapFeatureKind.Pin),
        ("corner",       SnapFeatureKind.CornerEndpoint),
        ("intersection", SnapFeatureKind.Intersection),
        ("midpoint",     SnapFeatureKind.Midpoint),
        ("centroid",     SnapFeatureKind.Centroid),
        ("nearest",      SnapFeatureKind.Nearest),
    ];

    private const int Size   = 18;    // box side, device-independent pixels
    private const float Half = 6f;    // glyph half-size inside it
    private const float Stroke = 1.6f;

    /// <summary>Write every snap glyph, light and dark, into <paramref name="outDir"/>.</summary>
    /// <returns>The paths written.</returns>
    public static IEnumerable<string> GenerateSnapGlyphs(string outDir)
    {
        Directory.CreateDirectory(outDir);
        foreach (var (id, kind) in SnapGlyphs)
            foreach (var variant in (ColorVariant[])[ColorVariant.Light, ColorVariant.Dark])
            {
                string stem = UiArtworkGenerator.FileStem(SnapGlyphStem + id, variant);
                string path = Path.Combine(outDir, stem + ".svg");
                Write(path, stem, canvas => LayoutRenderer.DrawSnapGlyph(
                    canvas, kind, Size / 2f, Size / 2f, Half, Stroke, TextColor(variant)));
                yield return path;
            }
    }

    // ── Emit ──────────────────────────────────────────────────────────────────

    private static void Write(string path, string stem, Action<SKCanvas> draw)
    {
        string raw;
        using (var stream = new SKDynamicMemoryWStream())
        {
            using (var canvas = SKSvgCanvas.Create(new SKRect(0, 0, Size, Size), stream))
                draw(canvas);
            using var data = stream.DetachAsData();
            raw = System.Text.Encoding.UTF8.GetString(data.ToArray());
        }

        if (!SvgLint.HasDrawingElements(raw))
            throw new InvalidOperationException(
                $"The inline glyph '{stem}' drew nothing. A blank table cell is exactly what this "
              + "replaced, so an empty one is a failure and not an acceptable fallback.");

        string svg = SvgPostPass.Run(raw, stem, out _);
        File.WriteAllText(path, UiArtworkGenerator.Banner(path) + svg + "\n");
    }
}
