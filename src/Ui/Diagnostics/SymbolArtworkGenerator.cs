using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkiaSharp;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Theming;
using CircuitRF.WBond;

namespace CircuitRF.Ui.Diagnostics;

/// <summary>
/// Generates the component-symbol artwork used by the User Documentation, straight from the live
/// circuitRF drawing engine (the same <see cref="SchematicRenderer.DrawSymbol"/> the schematic and the
/// palette use), so the docs never drift from the app. Each component produces TWO SVGs — a light-mode
/// and a dark-mode file (<c>resistor.svg</c> / <c>resistor-dark.svg</c>) — each containing the symbol
/// glyph (including any text baked into the symbol), its connection leads, its pins, plus the
/// component's display-name caption.
///
/// Run it from the CLI (no GUI window opens):
/// <code>dotnet run --project src/Ui -- --generate-symbols docs/user/assets/symbols</code>
/// or, as part of a full docs regeneration, through <c>tools/DocGen</c>, which calls this same
/// generator rather than reimplementing it.
///
/// circuitRF is alpha and symbols change — re-run this one command to refresh every doc image at once.
/// To document a NEW component, add one row to <see cref="Catalog"/> below; nothing else changes, and
/// <c>SymbolCatalogCompletenessTests</c> fails until you do.
///
/// <para><b>The figures show LEADS and PINS, not just the body.</b> <c>DrawSymbol</c> renders a
/// symbol's primitive list only; on a real schematic the pin markers come from the render loop's
/// <c>DrawPortMarkers</c> and the variadic stubs from <c>DrawVariadicPortLeads</c>, so a
/// primitives-only figure showed no pins at all. What the user needs to recognise is the thing they
/// are about to place — before anything is wired to it — so every pin is drawn in the UNCONNECTED
/// appearance, through the renderer's own helpers rather than a second copy of the geometry.</para>
///
/// Why it lives in src/Ui (not the CLI or a test): it needs the internal <c>SchematicRenderer</c> +
/// <c>SkiaFonts</c>, and fonts resolve through Avalonia's AssetLoader — which is why the entry point in
/// <c>Program.cs</c> calls <c>BuildAvaloniaApp().SetupWithoutStarting()</c> before invoking this.
/// </summary>
public static class SymbolArtworkGenerator
{
    /// <summary>
    /// The two <see cref="SymbolKind"/>s that are deliberately NOT documented, because the user
    /// cannot place either: <see cref="SymbolKind.Generic"/> is an internal fallback glyph and
    /// <see cref="SymbolKind.Unknown"/> is the sentinel a newer file's unrecognised component loads
    /// as. Hard-coded here so that adding a kind FAILS the completeness test until somebody decides
    /// which side of this line it falls on.
    /// </summary>
    public static readonly IReadOnlyList<SymbolKind> NotUserPlaceable =
        [SymbolKind.Generic, SymbolKind.Unknown];

    /// <summary>The components to render: (kind, output file stem, representative port count).</summary>
    public static readonly (SymbolKind Kind, string File, int Ports)[] Catalog =
    {
        (SymbolKind.Resistor,        "resistor",          2),
        (SymbolKind.Inductor,        "inductor",          2),
        (SymbolKind.Capacitor,       "capacitor",         2),
        (SymbolKind.NonlinearC,      "nonlinear-c",       2),
        (SymbolKind.Vdc,             "vdc",               2),
        (SymbolKind.ToneSource,      "tone-source",       2),
        (SymbolKind.CurrentToneSource, "current-tone-source", 2),
        (SymbolKind.Vccs,            "vccs",              4),
        (SymbolKind.P1Tone,          "p1tone",            2),
        (SymbolKind.PnTone,          "pntone",            2),
        (SymbolKind.Ground,          "ground",            1),
        (SymbolKind.Term,            "term",              1),
        (SymbolKind.TermG,           "termg",             1),
        (SymbolKind.Pin,             "pin",               1),
        (SymbolKind.IProbe,          "iprobe",            2),
        (SymbolKind.Tline,           "tline",             2),
        (SymbolKind.Mutual,          "mutual",            2),
        (SymbolKind.Snp,             "snp",               2),
        (SymbolKind.ZPort,           "zport",             2),
        (SymbolKind.Sdd,             "sdd",               2),
        (SymbolKind.Tuner,           "tuner",             1),
        (SymbolKind.SourceTuner,     "source-tuner",      1),
        (SymbolKind.LoadTuner,       "load-tuner",        1),
        (SymbolKind.Var,             "var",               0),
        (SymbolKind.Meas,            "meas",              0),

        // ── Added by the docs factory (brief-docs-factory-infrastructure.md DF3) ─────────────
        // Fifteen real, placeable components that had no documentation figure at all.
        (SymbolKind.Diode,           "diode",             2),
        (SymbolKind.Match,           "match",             2),
        (SymbolKind.Mlin,            "mlin",              2),
        (SymbolKind.MBend,           "mbend",             2),
        (SymbolKind.MTee,            "mtee",              3),
        (SymbolKind.MCross,          "mcross",            4),
        (SymbolKind.Mtaper,          "mtaper",            2),
        (SymbolKind.Mklopf,          "mklopf",            2),
        (SymbolKind.VerilogA,        "verilog-a",         3),
        (SymbolKind.WBond,           "wbond",             0),
        // The five large-signal FET laws SHARE one glyph and one 3-pin geometry on purpose (the
        // topology genuinely is identical; only the drain-current equation differs, and the type
        // label names it). They are five separate figures anyway, because they are five separate
        // components with five separate parameter sets, and a page that documents FET_Statz must be
        // able to show FET_Statz. This is also what retires the hand-made, generated-by-nothing
        // fet.svg / fet-dark.svg pair.
        (SymbolKind.FetCurtice,      "fet-curtice",       3),
        (SymbolKind.FetCurticeCubic, "fet-curtice-cubic", 3),
        (SymbolKind.FetStatz,        "fet-statz",         3),
        (SymbolKind.FetMaterka,      "fet-materka",       3),
        (SymbolKind.FetAngelov,      "fet-angelov",       3),
        // The two bipolar polarities get two figures because they get two GLYPHS — unlike the FET
        // laws above, the arrow is the only thing that tells them apart, so one shared figure would
        // document exactly the wrong half.
        (SymbolKind.BjtNpn,          "bjt-npn",           3),
        (SymbolKind.BjtPnp,          "bjt-pnp",           3),
        // The ideal mixer, in both packagings. Two figures for what is ONE engine component,
        // because the two glyphs are the whole of the difference the user is choosing between —
        // the same reasoning as the BJT pair above, and the opposite of the FET laws.
        (SymbolKind.Mixer,           "mixer",             3),
        (SymbolKind.MixerD,          "mixer-d",           6),
        // NEW COMPONENT: add (SymbolKind.Xxx, "file-stem", representativePortCount) here, then re-run.
    };

    // Layout of one figure (world→SVG): a glyph box with a caption strip beneath it.
    private const float GlyphW = 240f, GlyphH = 150f, Pad = 24f, CapH = 36f;

    /// <summary>
    /// Caption point size. FIXED, and deliberately not derived from the figure's own zoom.
    ///
    /// <para>On the canvas the type label is sized <c>zoom * LabelWorldHeight</c>, so it grows and
    /// shrinks with the view. A figure has no single "the" zoom: each glyph is fitted to the box on
    /// its own, and across this catalog that fit ranges from 0.189 (wBond) to 1.308 (GND) — measured,
    /// not estimated. Deriving the caption from it would set GND's caption at 92 px and wBond's at
    /// 13 px on the same page. A constant keeps the doc set legible; 15 pt sits just under the 17.2 px
    /// the two-terminal symbols (the majority of the catalog) would produce at their own fit.</para>
    ///
    /// <para>The TYPEFACE is a different matter and does have one right answer — see
    /// <see cref="CaptionTypeface"/>.</para>
    /// </summary>
    private const float CaptionPt = 15f;

    /// <summary>
    /// The caption typeface. <b>SemiBold on purpose, and not the canvas's own weight</b> — owner
    /// decision, 2026-08-20.
    ///
    /// <para>The canvas sets the type label in <c>SkiaFonts.PlexRegular</c>
    /// (<see cref="SchematicRenderer"/>'s <c>textFont</c>, whose label row 0 is the type name), so
    /// this deliberately differs. A figure caption is a caption: it names the component under a
    /// glyph on a documentation page, where it reads as a heading rather than as an annotation
    /// floating beside a symbol on a canvas. The colour is the canvas's
    /// (<c>theme.ComponentNameText</c>, the brush <c>compNamePaint</c> carries).</para>
    ///
    /// <para>This being a face the schematic does not otherwise draw with is what exposed the
    /// face-name/@font-face gap recorded in <c>src/Ui/RESOLVED.md</c>: Skia writes the concrete face
    /// name first (<c>IBM Plex Sans SemiBold</c>), and unless the docs declare THAT name the browser
    /// falls back to the base family and renders the caption regular.</para>
    /// </summary>
    private static SKTypeface CaptionTypeface => SkiaFonts.PlexSemiBold;

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
            var symbol  = SymbolFor(kind, ports);
            var caption = ComponentTypeRegistry.DisplayName(kind, ports);
            written.Add(RenderOne(kind, symbol, caption, Path.Combine(outDir, file + ".svg"),      light));
            written.Add(RenderOne(kind, symbol, caption, Path.Combine(outDir, file + "-dark.svg"), dark));
        }
        return written;
    }

    /// <summary>
    /// The symbol a placed instance of <paramref name="kind"/> would draw with.
    ///
    /// <para><see cref="SymbolKind.WBond"/> is the one kind whose symbol is not a built-in: it is
    /// GENERATED from the wirebond design the component carries, so the documentation figure is
    /// generated from the shipped DEFAULT design — the very thing a user gets when they place one.</para>
    /// </summary>
    public static Symbol SymbolFor(SymbolKind kind, int ports)
    {
        if (kind == SymbolKind.WBond)
        {
            var resolution = WBondSymbolProvider.Resolve(
                WBondSymbolProvider.RefFor(WBondEmbedding.DefaultPayload), schematicDir: null);
            if (resolution.Symbol is { } generated) return generated;
            throw new InvalidOperationException(
                "The shipped default wBond design produced no symbol, so the wBond documentation " +
                "figure cannot be generated. That is a real defect in the default payload, not a " +
                "reason to fall back to a generic glyph.");
        }

        return BuiltInSymbols.Primitives(kind, ports);
    }


    /// <summary>
    /// Draw one symbol — glyph, variadic port leads and the UNCONNECTED port markers — fitted and
    /// centred in a <paramref name="w"/> x <paramref name="h"/> box on <paramref name="canvas"/>.
    ///
    /// <para>Shared so the emitted <c>assets/symbols/*.svg</c> and any on-screen documentation figure
    /// of the same symbol are the same drawing. The markers matter: they are how a reader recognises
    /// a pin as a CONNECTION POINT rather than as a line ending, and a glyph-only rendering that
    /// leaves them out is a different picture of the same part (owner, 2026-08-24).</para>
    /// </summary>
    internal static void DrawFitted(SKCanvas canvas, SymbolKind kind, Symbol symbol,
                                    float w, float h, float pad, SchematicRenderTheme theme)
    {
        var prims = symbol.Primitives;
        var pins  = symbol.Pins;

        var (minX, minY, maxX, maxY) = SymbolGeometry.ComputeBb(prims);

        // The pins and their markers are part of the picture, so they are part of the fit. Without
        // this the marker on an outlying pin is drawn outside the glyph box and silently clipped.
        float half = SchematicRenderer.PortMarkerWorldHalf;
        foreach (var pin in pins)
        {
            minX = Math.Min(minX, pin.LocalX - half); maxX = Math.Max(maxX, pin.LocalX + half);
            minY = Math.Min(minY, pin.LocalY - half); maxY = Math.Max(maxY, pin.LocalY + half);
        }

        double bw = Math.Max(maxX - minX, 1.0);
        double bh = Math.Max(maxY - minY, 1.0);

        double zoom = Math.Min((w - 2 * pad) / bw, (h - 2 * pad) / bh);
        if (double.IsInfinity(zoom) || double.IsNaN(zoom) || zoom <= 0) zoom = 1.0;
        double worldCx = (minX + maxX) / 2.0, worldCy = (minY + maxY) / 2.0;
        double panX = worldCx - (w / 2.0) / zoom;
        double panY = worldCy - (h / 2.0) / zoom;

        SchematicRenderer.DrawSymbol(
            canvas, prims, compX: 0, compY: 0,
            SymbolRotation.R0, mirrorX: false, panX, panY, zoom, theme);

        // SDD/ZPort carry their port stubs in the render loop, not in the primitive list — the same
        // call the schematic makes, so the figure cannot disagree with the canvas.
        using (var leadPaint = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = (float)Math.Max(1.0, zoom * 6), Color = theme.SymbolLine,
        })
            SchematicRenderer.DrawVariadicPortLeads(
                canvas, kind, pins.Select(x => (x.LocalX, x.LocalY)).ToList(),
                compX: 0, compY: 0, SymbolRotation.R0, mirrorX: false, panX, panY, zoom, leadPaint);

        using (var unconnPaint = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = (float)Math.Max(1.0, zoom * 2), Color = theme.UnconnectedPort,
        })
            foreach (var pin in pins)
                SchematicRenderer.DrawUnconnectedPortMarker(
                    canvas, pin.LocalX, pin.LocalY, compX: 0, compY: 0,
                    SymbolRotation.R0, mirrorX: false, panX, panY, zoom, unconnPaint);
    }

    private static string RenderOne(
        SymbolKind kind, Symbol symbol, string caption, string path, SchematicRenderTheme theme)
    {
        var prims = symbol.Primitives;
        var pins  = symbol.Pins;

        // Transparent background (the doc figure frame supplies the surface color per theme).
        using var stream = new SKDynamicMemoryWStream();
        using (var canvas = SKSvgCanvas.Create(new SKRect(0, 0, GlyphW, GlyphH + CapH), stream))
        {
            DrawFitted(canvas, kind, symbol, GlyphW, GlyphH, Pad, theme);

            using var font  = new SKFont(CaptionTypeface, CaptionPt);
            using var paint = new SKPaint { Color = theme.ComponentNameText, IsAntialias = true };
            canvas.DrawText(caption, GlyphW / 2f, GlyphH + CapH * 0.62f, SKTextAlign.Center, font, paint);
        }

        using var data = stream.DetachAsData();
        string raw = System.Text.Encoding.UTF8.GetString(data.ToArray());

        if (!SvgLint.HasDrawingElements(raw))
            throw new InvalidOperationException(
                $"Symbol figure '{Path.GetFileName(path)}' produced an SVG with no drawing elements.");

        string svg = SvgPostPass.Run(raw, Path.GetFileNameWithoutExtension(path), out _);
        var findings = SvgLint.DroppedPaint(svg);
        if (findings.Count > 0)
            throw new InvalidOperationException(SvgLint.Explain(Path.GetFileName(path), findings));

        File.WriteAllText(path, SymbolBanner() + svg + "\n");
        return path;
    }

    private static string SymbolBanner() =>
        "<!-- GENERATED FILE - do not edit. Regenerate every component symbol with:\n"
      + "     dotnet run ––project src/Ui –– ––generate-symbols docs/user/assets/symbols\n"
      + "     (the flags above are ordinary double hyphens; XML comments cannot contain them.)\n"
      + "     Source: src/Ui/Diagnostics/SymbolArtworkGenerator.cs -->\n";
}
