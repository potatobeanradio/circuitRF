using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Diagnostics;

/// <summary>
/// Generates the component-symbol artwork used by the User Documentation, straight from the live
/// circuitRF drawing engine (the same <see cref="SchematicRenderer.DrawSymbol"/> the schematic and the
/// palette use), so the docs never drift from the app. Each component produces TWO SVGs — a light-mode
/// and a dark-mode file (<c>resistor.svg</c> / <c>resistor-dark.svg</c>) — each containing the symbol
/// glyph (including any text baked into the symbol) plus the component's display-name caption.
///
/// Run it from the CLI (no GUI window opens):
/// <code>dotnet run --project src/Ui -- --generate-symbols docs/user/assets/symbols</code>
///
/// circuitRF is alpha and symbols change — re-run this one command to refresh every doc image at once.
/// To document a NEW component, add one row to <see cref="Catalog"/> below; nothing else changes.
///
/// Why it lives in src/Ui (not the CLI or a test): it needs the internal <c>SchematicRenderer</c> +
/// <c>SkiaFonts</c>, and fonts resolve through Avalonia's AssetLoader — which is why the entry point in
/// <c>Program.cs</c> calls <c>BuildAvaloniaApp().SetupWithoutStarting()</c> before invoking this.
/// </summary>
public static class SymbolArtworkGenerator
{
    /// <summary>The components to render: (kind, output file stem, representative port count).</summary>
    private static readonly (SymbolKind Kind, string File, int Ports)[] Catalog =
    {
        (SymbolKind.Resistor,    "resistor",     2),
        (SymbolKind.Inductor,    "inductor",     2),
        (SymbolKind.Capacitor,   "capacitor",    2),
        (SymbolKind.NonlinearC,  "nonlinear-c",  2),
        (SymbolKind.Vdc,         "vdc",          2),
        (SymbolKind.ToneSource,  "tone-source",  2),
        (SymbolKind.P1Tone,      "p1tone",       2),
        (SymbolKind.Ground,      "ground",       1),
        (SymbolKind.Term,        "term",         1),
        (SymbolKind.Pin,         "pin",          1),
        (SymbolKind.IProbe,      "iprobe",       2),
        (SymbolKind.Tline,       "tline",        2),
        (SymbolKind.Mutual,      "mutual",       2),
        (SymbolKind.Snp,         "snp",          2),
        (SymbolKind.ZPort,       "zport",        2),
        (SymbolKind.Sdd,         "sdd",          2),
        (SymbolKind.FetSdd,      "fet",          3),
        (SymbolKind.Tuner,       "tuner",        1),
        (SymbolKind.SourceTuner, "source-tuner", 1),
        (SymbolKind.LoadTuner,   "load-tuner",   1),
        (SymbolKind.Var,         "var",          0),
        (SymbolKind.Meas,        "meas",         0),
        // NEW COMPONENT: add (SymbolKind.Xxx, "file-stem", representativePortCount) here, then re-run.
    };

    // Layout of one figure (world→SVG): a glyph box with a caption strip beneath it.
    private const float GlyphW = 240f, GlyphH = 150f, Pad = 24f, CapH = 36f, CaptionPt = 15f;

    /// <summary>
    /// Render every catalog component to <paramref name="outDir"/> as light + dark SVGs.
    /// Returns the list of files written. The Avalonia asset loader must already be initialized
    /// (the caller runs <c>BuildAvaloniaApp().SetupWithoutStarting()</c> first).
    /// </summary>
    public static IReadOnlyList<string> GenerateAll(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var light = SchematicRenderTheme.FromTheme(ColorTheme.BuiltIn, ColorVariant.Light);
        var dark  = SchematicRenderTheme.FromTheme(ColorTheme.BuiltIn, ColorVariant.Dark);

        var written = new List<string>();
        foreach (var (kind, file, ports) in Catalog)
        {
            var prims   = BuiltInSymbols.Primitives(kind, ports).Primitives;
            var caption = ComponentTypeRegistry.DisplayName(kind, ports);
            written.Add(RenderOne(prims, caption, Path.Combine(outDir, file + ".svg"),      light));
            written.Add(RenderOne(prims, caption, Path.Combine(outDir, file + "-dark.svg"), dark));
        }
        return written;
    }

    private static string RenderOne(
        IReadOnlyList<SymbolPrimitive> prims, string caption, string path, SchematicRenderTheme theme)
    {
        var (minX, minY, maxX, maxY) = SymbolGeometry.ComputeBb(prims);
        double bw = Math.Max(maxX - minX, 1.0);
        double bh = Math.Max(maxY - minY, 1.0);

        // Fit the glyph bbox into the glyph box with padding; map its center to the box center.
        double zoom = Math.Min((GlyphW - 2 * Pad) / bw, (GlyphH - 2 * Pad) / bh);
        if (double.IsInfinity(zoom) || double.IsNaN(zoom) || zoom <= 0) zoom = 1.0;
        double worldCx = (minX + maxX) / 2.0, worldCy = (minY + maxY) / 2.0;
        double panX = worldCx - (GlyphW / 2.0) / zoom;
        double panY = worldCy - (GlyphH / 2.0) / zoom;

        // Transparent background (the doc figure frame supplies the surface color per theme).
        using var stream = new SKFileWStream(path);
        using (var canvas = SKSvgCanvas.Create(new SKRect(0, 0, GlyphW, GlyphH + CapH), stream))
        {
            SchematicRenderer.DrawSymbol(
                canvas, prims, compX: 0, compY: 0,
                SymbolRotation.R0, mirrorX: false,
                panX, panY, zoom, theme);

            using var font  = new SKFont(SkiaFonts.PlexSemiBold, CaptionPt);
            using var paint = new SKPaint { Color = theme.ComponentNameText, IsAntialias = true };
            canvas.DrawText(caption, GlyphW / 2f, GlyphH + CapH * 0.62f, SKTextAlign.Center, font, paint);
        }
        return path;
    }
}
