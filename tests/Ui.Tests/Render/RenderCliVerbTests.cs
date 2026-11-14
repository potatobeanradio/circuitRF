// ================================================================
//  RND-2's gates (docs/sonnet-briefs/brief-render-2-render-verb.md §7) for `circuitrf render`.
//
//  ── Why two ways of driving the verb, and which gate uses which ──────────────────────────────────
//
//  Gate 1 launches the real CLI DLL as a PROCESS, on the EmCliVerbTests / ConvertCliVerbTests
//  standard, and compares the bytes it wrote against an in-process CircuitRF.Render call on the same
//  document, theme and viewport. That call is itself gated against the GUI's own clipboard export by
//  RND-1 (tests/Ui.Tests/Render/RenderLayerBelowFirewallTests.cs), which is what makes this the
//  end-to-end claim rather than a comparison of the verb with itself. A same-process call could never
//  show the failure that mattered here — a second process with no Avalonia host under it drawing in a
//  substituted typeface — so the process is not an inconvenience, it is the measurement.
//
//  Gates 5 and 7 are about what happens INSIDE one process: a layer selection leaking into a LATER
//  render through a cached, shared Technology, and a cancellation delivered through RunHost. Neither
//  is observable across two processes that each answer one command and exit, which is why
//  CircuitRF.Cli grants InternalsVisibleTo to this assembly (see its .csproj).
// ================================================================

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CircuitRF.Cli;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Symbol;
using CircuitRF.Design.Theming;
using CircuitRF.Design.Workspace;
using CircuitRF.Render;
using SkiaSharp;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Render;

// This class renders a LabelShape, so it reads LayoutTextOutline.TestOverrideTypeface — a shared
// static several other classes set. Same collection, same reason, as RenderLayerBelowFirewallTests.
[Collection(LayoutTextOutlineTypefaceCollection.Name)]
public sealed class RenderCliVerbTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-render-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    private static readonly LayerKey Metal1 = new(1, 0);
    private static readonly LayerKey Metal2 = new(2, 0);
    private static readonly LayerKey Silk   = new(5, 0);

    // ── gate 1: byte identity against the in-process CircuitRF.Render call ────

    /// <summary>
    /// The verb, as a process, writes the bytes <c>LayoutRenderer.Draw</c> writes for the same
    /// document, theme and viewport.
    ///
    /// <para><b>The viewport is stated rather than fitted</b>, and that is what makes the comparison
    /// exact: <c>--window 0nm,0nm,160000nm,120000nm</c> at <c>--size 1600x1200</c> is one DBU per
    /// hundred nanometres on both axes, so the zoom is 0.01 exactly and both sides construct the same
    /// <see cref="LayoutViewport"/> with no rounding to argue about. A fitted viewport would compare
    /// the verb's framing arithmetic with a copy of itself, which is a different (and much weaker)
    /// claim.</para>
    ///
    /// <para><b>The options are spelled out here on purpose.</b> They are what <c>--detail full</c>
    /// means — every level-of-detail tier off, so what is stored is what is drawn — and writing them
    /// down is what turns a silent change to the verb's defaults into a failing test rather than into
    /// a quietly different picture.</para>
    /// </summary>
    [Fact]
    public void RenderingALayoutAsAProcess_WritesTheBytesTheRendererWrites()
    {
        var ws = BuildWorkspace();
        string outPath = Path.Combine(_root, "cli.svg");

        var (exit, stdout, stderr) = RunCli(
            "render", ws.Clay, "-o", outPath,
            "--window", "0nm,0nm,160000nm,120000nm", "--size", "1600x1200");
        output.WriteLine(stdout + stderr);
        Assert.Equal(0, exit);

        string fromCli = File.ReadAllText(outPath);
        string inProcess = InProcessLayoutSvg(ws, new LayoutViewport(0, 0, 0.01, 1600, 1200));

        AssertSameSvg(fromCli, inProcess, "layout");
    }

    [Fact]
    public void RenderingASchematicAsAProcess_WritesTheBytesTheRendererWrites()
    {
        var ws = BuildWorkspace();
        string outPath = Path.Combine(_root, "sch.svg");

        var (exit, stdout, stderr) = RunCli(
            "render", ws.Csch, "-o", outPath,
            "--window", "0,0,1600,1200", "--size", "1600x1200");
        output.WriteLine(stdout + stderr);
        Assert.Equal(0, exit);

        var (model, _, _) = SchematicPersistence.LoadFromFile(ws.Csch);
        var (rm, idx) = model.BuildRenderModel();
        var theme = SchematicRenderTheme.FromTheme(
            ThemeResolver.Resolve(ThemeResolver.DefaultThemeName, ws.Root), ColorVariant.Light);

        string inProcess = Svg(1600, 1200, canvas =>
            SchematicRenderer.Draw(canvas, (1600, 1200), rm, idx, 0, 0, 1.0, theme,
                overlay: null, useTransparentBackground: false, excludeGrid: true));

        AssertSameSvg(File.ReadAllText(outPath), inProcess, "schematic");
    }

    [Fact]
    public void RenderingASymbolAsAProcess_WritesTheBytesTheRendererWrites()
    {
        var ws = BuildWorkspace();
        string outPath = Path.Combine(_root, "sym.svg");

        var (exit, stdout, stderr) = RunCli(
            "render", ws.Csym, "-o", outPath,
            "--window", "0,0,800,600", "--size", "1600x1200");
        output.WriteLine(stdout + stderr);
        Assert.Equal(0, exit);

        var symbol = SymbolPersistence.LoadFromFile(ws.Csym);
        var theme = SchematicRenderTheme.FromTheme(
            ThemeResolver.Resolve(ThemeResolver.DefaultThemeName, ws.Root), ColorVariant.Light);

        string inProcess = Svg(1600, 1200, canvas =>
        {
            canvas.Clear(theme.Background);
            SchematicRenderer.DrawSymbol(canvas, symbol.Primitives, 0, 0,
                SymbolRotation.R0, false, panX: 0, panY: 0, zoom: 2.0, theme: theme);
            SymbolEditorRenderer.DrawPinMarkersPlain(canvas, symbol.Pins, 0, 0, 2.0, theme);
        });

        AssertSameSvg(File.ReadAllText(outPath), inProcess, "symbol");
    }

    /// <summary>
    /// The PDF half of gate 1. RND-1 measured that two PDF renders of one fixture come back
    /// byte-identical with no exclusion at all — the <c>SKDocumentPdfMetadata</c> creation date §5.2
    /// predicted did not appear — so the stripper below is applied only where the raw bytes actually
    /// differ. An exclusion that stops being needed stops being applied.
    /// </summary>
    [Fact]
    public void RenderingALayoutToPdfAsAProcess_WritesTheBytesTheRendererWrites()
    {
        var ws = BuildWorkspace();
        string outPath = Path.Combine(_root, "cli.pdf");

        var (exit, _, stderr) = RunCli(
            "render", ws.Clay, "-o", outPath,
            "--window", "0nm,0nm,160000nm,120000nm", "--size", "1600x1200");
        output.WriteLine(stderr);
        Assert.Equal(0, exit);

        byte[] fromCli = File.ReadAllBytes(outPath);
        byte[] inProcess = Pdf(1600, 1200, canvas => DrawLayoutLikeTheVerb(ws, new LayoutViewport(0, 0, 0.01, 1600, 1200), canvas));

        string a = Convert.ToHexString(fromCli), b = Convert.ToHexString(inProcess);
        output.WriteLine($"pdf: {fromCli.Length} vs {inProcess.Length} bytes, {(a == b ? "byte-identical" : "differ")}");
        if (a != b) Assert.Equal(StripPdfDates(fromCli), StripPdfDates(inProcess));
    }

    // ── gate 3: the orphan document ───────────────────────────────────────────

    /// <summary>
    /// R-rnd2-1 / gate 3. A <c>.clay</c> in a bare directory with no workspace above it produces a
    /// file, exits 0, and says the technology did not resolve as a NOTE — not a warning and certainly
    /// not a refusal. A caller rendering a bare <c>.clay</c> handed to it by a converter already knows
    /// there is no workspace; calling that a warning teaches it to ignore warnings.
    /// </summary>
    [Fact]
    public void AnOrphanLayoutRenders_AndSaysSoAsANote()
    {
        string dir = Path.Combine(_root, "orphan");
        Directory.CreateDirectory(dir);
        string clay = Path.Combine(dir, "Loose.clay");
        LayoutPersistence.SaveToFile(clay, LayoutFixture());

        string outPath = Path.Combine(dir, "loose.png");
        var (exit, stdout, stderr) = RunCli("render", clay, "-o", outPath);
        output.WriteLine(stdout + stderr);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(outPath));
        Assert.Contains("note: ", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("warning: ", stderr, StringComparison.Ordinal);
        Assert.Contains("fallback palette", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOrphanSchematicRenders()
    {
        string dir = Path.Combine(_root, "orphan-sch");
        Directory.CreateDirectory(dir);
        string csch = Path.Combine(dir, "Loose.csch");
        SchematicPersistence.SaveToFile(csch, SchematicFixture(), "Loose");

        string outPath = Path.Combine(dir, "loose.svg");
        var (exit, _, stderr) = RunCli("render", csch, "-o", outPath);
        output.WriteLine(stderr);
        Assert.Equal(0, exit);
        Assert.True(File.Exists(outPath));
    }

    // ── gate 2: every refusal, by id and by the flag it names ─────────────────

    /// <summary>
    /// R-rnd0-6 in one table: every question the GUI would have asked in a dialog is a refusal that
    /// NAMES the flag answering it. The id is the contract and the sentence is not (§7A), so both are
    /// asserted — the id so a caller can match on it, the flag's spelling so the sentence is actually
    /// actionable by a person.
    /// </summary>
    [Theory]
    // a cell folder holding more than one view
    [InlineData("cell-folder",     "render.cell.view-required",              "--view")]
    // an output extension this verb does not write
    [InlineData("bad-extension",   "render.output.unknown-format",           "--format")]
    // R-rnd2-4: a bare number where a layout coordinate belongs
    [InlineData("bare-window",     "render.viewport.unit-required",          "--window")]
    // R-rnd2-7: a layer the resolved technology does not define
    [InlineData("unknown-layer",   "render.layers.unknown",                  "explain --layers")]
    // R-rnd2-3: two viewport modes, refused together rather than ordered
    [InlineData("two-viewports",   "render.viewport.multiple-modes",         "--window")]
    // a raster multiplier asked of a format with no pixels
    [InlineData("scale-on-svg",    "render.scale.vector",                    "--size")]
    // §4: layer selection asked of a document that has no layers
    [InlineData("layers-on-sch",   "render.layers.not-applicable",           "--layers")]
    // R-rnd2-9: a theme name that resolves to nothing, listing what was looked at
    [InlineData("bad-theme",       "render.theme.unresolved",                "themes")]
    // rendering "the workspace" is not a picture of anything
    [InlineData("workspace-no-cell", "render.workspace.cell-required",       "--cell")]
    public void EveryRefusal_CarriesItsIdAndNamesTheFlagThatAnswersIt(string which, string id, string names)
    {
        var ws = BuildWorkspace();
        string outPath = Path.Combine(_root, "refused.svg");

        string[] args = which switch
        {
            "cell-folder"       => ["render", ws.CellDir, "-o", outPath],
            "bad-extension"     => ["render", ws.Clay, "-o", Path.Combine(_root, "x.jpg")],
            "bare-window"       => ["render", ws.Clay, "-o", outPath, "--window", "0,0,500,300"],
            "unknown-layer"     => ["render", ws.Clay, "-o", outPath, "--layers", "Metal9"],
            "two-viewports"     => ["render", ws.Clay, "-o", outPath, "--fit", "--window", "0um,0um,5um,3um"],
            "scale-on-svg"      => ["render", ws.Clay, "-o", outPath, "--scale", "2"],
            "layers-on-sch"     => ["render", ws.Csch, "-o", outPath, "--layers", "Metal1"],
            "bad-theme"         => ["render", ws.Clay, "-o", outPath, "--theme", "NoSuchTheme"],
            _                   => ["render", ws.Root, "-o", outPath],
        };

        var (exit, stdout, stderr) = RunCli([.. args, "--json"]);
        output.WriteLine(stdout);
        output.WriteLine(stderr);

        Assert.Equal(1, exit);
        Assert.Contains($"\"id\": \"{id}\"", stdout, StringComparison.Ordinal);
        Assert.Contains(names, stderr, StringComparison.Ordinal);
        // A refusal writes nothing. A caller that got a refusal AND a file has no way to know which
        // of the two to believe.
        Assert.False(File.Exists(outPath), $"{which} wrote a file as well as refusing");
    }

    /// <summary>The two directions of one choice, and the two spellings of one number. Both are
    /// refused rather than silently resolved, for the reason <c>lpp --grid</c> is.</summary>
    [Theory]
    [InlineData("render.layers.conflict", "--layers", "Metal1", "--hide-layers", "Metal2")]
    [InlineData("render.scale.conflict",  "--scale",  "2",      "--dpi",         "192")]
    public void TwoSpellingsOfOneChoice_AreRefusedTogether(string id, string a, string av, string b, string bv)
    {
        var ws = BuildWorkspace();
        var (exit, stdout, _) = RunCli(
            "render", ws.Clay, "-o", Path.Combine(_root, "x.png"), a, av, b, bv, "--json");
        Assert.Equal(1, exit);
        Assert.Contains($"\"id\": \"{id}\"", stdout, StringComparison.Ordinal);
    }

    // ── gate 4: the counters, which are what --detail and --layers actually move ──

    /// <summary>
    /// R-rnd2-6's claim, asserted rather than asserted-about-time: <c>--detail screen</c> emits
    /// materially fewer vertices than <c>--detail full</c> on the same document, at the same viewport.
    ///
    /// <para>The fixture is a machine-generated contour — 4,000 vertices on a 400 µm arc, which is what
    /// an imported Gerber's flattened copper actually looks like — because the decimation tier has a
    /// hard floor at <c>LayoutRenderDetail.MinVerticesToDecimate</c> and cannot engage on an authored
    /// rectangle. A gate built on a hand-drawn polygon would pass whatever the tier did.</para>
    /// </summary>
    [Fact]
    public void DetailScreen_EmitsMateriallyFewerVerticesThanDetailFull()
    {
        var ws = BuildWorkspace(denseContour: true);

        long full   = VerticesEmitted(ws, "full");
        long screen = VerticesEmitted(ws, "screen");
        output.WriteLine($"vertices: full={full:N0} screen={screen:N0} ratio={(double)full / screen:F1}x");

        Assert.True(full > 3000, $"the fixture is not dense enough to show the tier: {full} vertices");
        Assert.True(screen * 2 < full,
            $"--detail screen emitted {screen} vertices against full's {full} — the tier did not engage");
    }

    /// <summary>
    /// R-rnd2-8's other half, and the one that says the narrowing is real: <c>--layers</c> reduces
    /// <c>shapesDrawn</c>, not merely <c>shapesExamined</c>. A tier that culled candidates and then
    /// drew them anyway would move only the second number.
    /// </summary>
    [Fact]
    public void NarrowingTheLayers_ReducesShapesDrawn()
    {
        var ws = BuildWorkspace();

        var all = RenderJson(ws.Clay, "--size", "800x600");
        var one = RenderJson(ws.Clay, "--size", "800x600", "--layers", "Metal1");

        output.WriteLine($"all: drawn={all.ShapesDrawn} examined={all.ShapesExamined}");
        output.WriteLine($"M1:  drawn={one.ShapesDrawn} examined={one.ShapesExamined}");

        Assert.True(one.ShapesDrawn < all.ShapesDrawn,
            $"--layers drew {one.ShapesDrawn} of the document's shapes, against {all.ShapesDrawn} with no selection");
    }

    /// <summary>Every layer the technology defines is reported with whether it was drawn AND how many
    /// shapes the document has on it — so an EXCLUDED layer and an EMPTY one are two different
    /// answers, which is the whole reason R-rnd2-7 refuses a misspelling.</summary>
    [Fact]
    public void TheLayerReport_TellsAnExcludedLayerFromAnEmptyOne()
    {
        var ws = BuildWorkspace();
        string json = RenderRawJson(ws.Clay, "--layers", "Metal1");

        Assert.Contains("\"name\": \"Metal1\"", json, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"""name"":\s*""Metal2"",\s*""rendered"":\s*false,\s*""shapes"":\s*[1-9]"), json);
        Assert.Matches(new Regex(@"""name"":\s*""Silk"",\s*""rendered"":\s*false,\s*""shapes"":\s*[0-9]"), json);
    }

    // ── the layers a technology does NOT define, which are drawn anyway ───────

    /// <summary>
    /// <b>A key the document draws on that the technology does not declare is a first-class layer of
    /// this verb, because the renderer paints it.</b> <c>LayoutRenderer</c> resolves such a key
    /// through <c>FallbackPalette.For</c>, whose <c>Visible</c> is true — the ordinary state after an
    /// import (<c>layout-view.md</c> §2.4).
    ///
    /// <para>Three things were wrong together before this, all one omission, and the third is the one
    /// that produced a wrong PICTURE rather than a wrong report: the layer report left the generated
    /// row out while <c>explain --layers</c> listed it; <c>--layers L99/0</c> was refused naming the
    /// verb that had just listed it; and <c>--layers Metal1</c> drew Metal1 <i>and</i> the generated
    /// layer, because the technology copy had no <c>LayerDef</c> for the second to turn off. A caller
    /// that asked for one layer and silently got two has no way to notice — R-rnd2-7's own failure
    /// mode with the sign reversed.</para>
    /// </summary>
    [Fact]
    public void ALayerTheTechnologyDoesNotDefine_IsReportedAndSelectableAndExcludable()
    {
        var ws = BuildWorkspace(undefinedLayer: true);

        // Reported, under the generated palette's own name — the same one `explain --layers` prints.
        string all = RenderRawJson(ws.Clay, "--size", "400x300");
        Assert.Matches(new Regex(@"""name"":\s*""L99/0"",\s*""rendered"":\s*true,\s*""shapes"":\s*1"), all);

        // Selectable BY that name, rather than refused as one the technology does not define.
        var only = RenderJson(ws.Clay, "--size", "400x300", "--layers", "L99/0");
        Assert.Equal(1, only.ShapesDrawn);

        // And excludable: a selection that names another layer must not leave it in the picture.
        var metal = RenderJson(ws.Clay, "--size", "400x300", "--layers", "Metal1");
        var whole = RenderJson(ws.Clay, "--size", "400x300");
        output.WriteLine($"whole={whole.ShapesDrawn} metal1={metal.ShapesDrawn} generated={only.ShapesDrawn}");
        Assert.True(metal.ShapesDrawn < whole.ShapesDrawn,
            $"--layers Metal1 drew {metal.ShapesDrawn} shapes; the whole document draws {whole.ShapesDrawn}");
        Assert.Matches(
            new Regex(@"""name"":\s*""L99/0"",\s*""rendered"":\s*false"),
            RenderRawJson(ws.Clay, "--size", "400x300", "--layers", "Metal1"));
    }

    // ── gate 5: the layer selection does not leak ─────────────────────────────

    /// <summary>
    /// R-rnd2-8 / gate 5, in the only form that can fail: TWO renders in ONE process, the second with
    /// no <c>--layers</c>, rendering every layer.
    ///
    /// <para><b>This is what the obvious implementation gets wrong.</b> <see cref="TechnologyCache"/>
    /// hands back a shared instance, so flipping <c>LayerDef.Visible</c> on the resolved technology
    /// narrows every LATER render taken through that cache. Across two processes the defect is
    /// invisible by construction, which is why this one runs <c>CliEntry.Run</c> directly.</para>
    /// </summary>
    [Fact]
    public void ALayerSelection_DoesNotLeakIntoTheNextRenderInTheSameProcess()
    {
        var ws = BuildWorkspace();
        string a = Path.Combine(_root, "leak-a.png");
        string b = Path.Combine(_root, "leak-b.png");
        string reference = Path.Combine(_root, "leak-ref.png");

        Assert.Equal(0, InProcessCli("render", ws.Clay, "-o", reference, "--size", "400x300"));
        Assert.Equal(0, InProcessCli("render", ws.Clay, "-o", a, "--size", "400x300", "--layers", "Metal1"));
        Assert.Equal(0, InProcessCli("render", ws.Clay, "-o", b, "--size", "400x300"));

        Assert.NotEqual(File.ReadAllBytes(reference), File.ReadAllBytes(a));   // vacuity guard
        Assert.Equal(File.ReadAllBytes(reference), File.ReadAllBytes(b));
    }

    /// <summary>
    /// The same claim one level down, where the leak would actually happen: the clone is a copy and the
    /// CACHED technology every later document resolves to is untouched.
    /// </summary>
    [Fact]
    public void TheLayerSelectionCopiesTheTechnology_LeavingTheCachedOneAlone()
    {
        var ws = BuildWorkspace();
        var cache = new TechnologyCache();

        var first  = TechnologyResolver.ResolveForDocument(null, ws.Clay, null, cache).Resolution.Tech!;
        var narrow = TechnologyLayerSelection.WithVisibility(first, l => l.Key == Metal1);

        Assert.False(narrow.Layers.Single(l => l.Key == Metal2).Visible);
        Assert.True(first.Layers.Single(l => l.Key == Metal2).Visible);

        var second = TechnologyResolver.ResolveForDocument(null, ws.Clay, null, cache).Resolution.Tech!;
        Assert.Same(first, second);                                    // the premise: one shared instance
        Assert.All(second.Layers, l => Assert.True(l.Visible));
    }

    // ══ AUT-12 ══════════════════════════════════════════════════════════════
    //
    //  R-aut12-1 (a render-time colour override) and R-aut12-2 (framing on a subset) — the two
    //  places the artwork half of the automation surface made a caller edit a document or do
    //  arithmetic to get the picture it wanted.

    /// <summary>
    /// <b>R-aut12-1's own gate, in the only form that can fail:</b> three renders in ONE process —
    /// a reference, one with a colour override, then a plain one again. The third must be
    /// byte-identical to the first.
    ///
    /// <para>This is the same trap <c>--layers</c> has and it is worth re-asserting rather than
    /// assuming: <see cref="TechnologyCache"/> hands back a SHARED <see cref="Technology"/>, so
    /// writing <c>LayerDef.Color</c> on the resolved instance would recolour every later render taken
    /// through that cache — in a process that renders many, which <c>circuitrf serve</c> is. Across
    /// two processes the defect is invisible by construction, which is why this drives
    /// <c>CliEntry.Run</c> directly.</para>
    /// </summary>
    [Fact]
    public void ALayerColourOverride_DoesNotLeakIntoTheNextRenderInTheSameProcess()
    {
        var ws = BuildWorkspace();
        string reference = Path.Combine(_root, "colour-ref.png");
        string coloured  = Path.Combine(_root, "colour-a.png");
        string after     = Path.Combine(_root, "colour-b.png");

        Assert.Equal(0, InProcessCli("render", ws.Clay, "-o", reference, "--size", "400x300"));
        Assert.Equal(0, InProcessCli("render", ws.Clay, "-o", coloured, "--size", "400x300",
                                     "--layer-colors", "Metal1=#00ff00"));
        Assert.Equal(0, InProcessCli("render", ws.Clay, "-o", after, "--size", "400x300"));

        // The vacuity guard: without it a no-op override would pass this test perfectly.
        Assert.NotEqual(File.ReadAllBytes(reference), File.ReadAllBytes(coloured));
        Assert.Equal(File.ReadAllBytes(reference), File.ReadAllBytes(after));
    }

    /// <summary>The same claim one level down, where the leak would actually happen — the twin of
    /// <see cref="TheLayerSelectionCopiesTheTechnology_LeavingTheCachedOneAlone"/>, because a colour
    /// override goes through the same clone and would otherwise be free to write in place.</summary>
    [Fact]
    public void TheColourOverrideCopiesTheTechnology_LeavingTheCachedOneAlone()
    {
        var ws = BuildWorkspace();
        var cache = new TechnologyCache();

        var first = TechnologyResolver.ResolveForDocument(null, ws.Clay, null, cache).Resolution.Tech!;
        var repainted = TechnologyLayerSelection.WithLayers(
            first, null,
            l => l.Key == Metal1
                ? new TechnologyLayerSelection.LayerAppearance(new Rgba(0, 255, 0), 0.9)
                : null);

        Assert.Equal(new Rgba(0, 255, 0), repainted.Layers.Single(l => l.Key == Metal1).Color);
        Assert.Equal(0.9, repainted.Layers.Single(l => l.Key == Metal1).FillOpacity);
        Assert.Equal(new Rgba(200, 30, 30), first.Layers.Single(l => l.Key == Metal1).Color);

        var second = TechnologyResolver.ResolveForDocument(null, ws.Clay, null, cache).Resolution.Tech!;
        Assert.Same(first, second);                                   // the premise: one shared instance
        Assert.Equal(new Rgba(200, 30, 30), second.Layers.Single(l => l.Key == Metal1).Color);
        Assert.Equal(0.5, second.Layers.Single(l => l.Key == Metal1).FillOpacity);
    }

    /// <summary>
    /// The override reaches the REPORT as well as the picture, and the eight-digit form moves the
    /// layer's fill opacity rather than an alpha nothing paints through — <c>LayoutRenderer</c> builds
    /// its <c>SKColor</c> from R, G and B alone at all four of its call sites, so an override that
    /// wrote the alpha into the colour and stopped would parse, report itself as applied, and change
    /// nothing.
    /// </summary>
    [Fact]
    public void ALayerColourOverride_IsReportedAsDrawn_AndItsAlphaIsTheFillOpacity()
    {
        var ws = BuildWorkspace();

        var plain = LayerRow(RenderRawJson(ws.Clay, "--size", "400x300"), "Metal1");
        Assert.Equal("#c81e1e", plain.GetProperty("color").GetString());
        Assert.Equal(0.5, plain.GetProperty("fillOpacity").GetDouble(), 12);

        var painted = LayerRow(
            RenderRawJson(ws.Clay, "--size", "400x300", "--layer-colors", "Metal1=#00ff0080"),
            "Metal1");
        Assert.Equal("#00ff00", painted.GetProperty("color").GetString());
        Assert.Equal(128 / 255.0, painted.GetProperty("fillOpacity").GetDouble(), 12);

        // A layer nobody named keeps the technology's own paint — the override is not a repaint of
        // everything that happened to be near that shade.
        var untouched = LayerRow(
            RenderRawJson(ws.Clay, "--size", "400x300", "--layer-colors", "Metal1=#00ff0080"),
            "Metal2");
        Assert.Equal("#1e78c8", untouched.GetProperty("color").GetString());
        Assert.Equal(0.5, untouched.GetProperty("fillOpacity").GetDouble(), 12);
    }

    /// <summary>R-aut12-1's refusals. A name that is not a layer, and a value that is not a colour,
    /// are each refused rather than skipped — a layer left at its own colour is a picture that looks
    /// exactly like the override having worked on something already near that shade.</summary>
    [Fact]
    public void ALayerColourOverride_RefusesAnUnknownLayerAndANonColour()
    {
        var ws = BuildWorkspace();
        string outPath = Path.Combine(_root, "never.png");

        foreach ((string entry, string id) in new[]
        {
            ("Nope=#ff0000",   "render.layers.unknown"),
            ("Metal1=puce",    "render.layer-colors.bad-color"),
            ("Metal1",         "render.layer-colors.malformed"),
        })
        {
            var (exit, stdout, _) = RunCli("render", ws.Clay, "-o", outPath,
                                           "--layer-colors", entry, "--json");
            Assert.Equal(1, exit);
            Assert.Contains($"\"id\": \"{id}\"", stdout, StringComparison.Ordinal);
            Assert.False(File.Exists(outPath), "a refused render wrote a file");
        }
    }

    /// <summary>
    /// R-aut12-2's first half, which was the BEHAVIOUR all along and had never been stated or
    /// tested: <b>a fit frames what is DRAWN.</b> <c>DocumentExtents.LayoutBox</c> gates on
    /// <c>LayerDef.Visible</c>, and that is the same flag <c>--hide-layers</c> writes on the clone the
    /// drawing is taken through — so hiding the outlier removes it from the framing too.
    ///
    /// <para>The fixture is the shape the exercise hit: a small mark 100 mm away, which framed with
    /// everything else shrinks the real content to a fraction of the page.</para>
    /// </summary>
    [Fact]
    public void AFit_FramesOnlyWhatIsDrawn()
    {
        var ws = BuildWorkspace(outlier: true);

        var whole  = Viewport(RenderRawJson(ws.Clay, "--size", "800x600"));
        var hidden = Viewport(RenderRawJson(ws.Clay, "--size", "800x600", "--hide-layers", "Silk"));

        output.WriteLine($"whole:  {whole.X0:G4}..{whole.X1:G4}");
        output.WriteLine($"hidden: {hidden.X0:G4}..{hidden.X1:G4}");

        // The outlier is at 100 mm; the rest of the fixture ends at 900 µm. Framing without it is
        // therefore two orders of magnitude tighter.
        Assert.True(whole.X1 - whole.X0 > 0.05, $"the outlier did not dominate the fit: {whole.X1 - whole.X0} m");
        Assert.True(hidden.X1 - hidden.X0 < 0.005, $"hiding the outlier did not tighten the fit: {hidden.X1 - hidden.X0} m");
    }

    /// <summary>
    /// R-aut12-2's second half, and the one <c>--hide-layers</c> cannot do: <b>frame on these, draw
    /// everything.</b> The viewport is the one hiding the outlier produces, and the shape count is
    /// the one drawing everything produces — which is the combination a caller wants and could not
    /// previously ask for.
    /// </summary>
    [Fact]
    public void FitLayers_FramesOnThoseAndStillDrawsTheRest()
    {
        var ws = BuildWorkspace(outlier: true);

        var hidden  = RenderRawJson(ws.Clay, "--size", "800x600", "--hide-layers", "Silk");
        var framed  = RenderRawJson(ws.Clay, "--size", "800x600", "--fit-layers", "Metal1,Metal2");

        var a = Viewport(hidden);
        var b = Viewport(framed);
        Assert.Equal(a.X0, b.X0, 12);
        Assert.Equal(a.X1, b.X1, 12);

        // The picture is NOT narrowed. At that identical viewport, hiding Silk loses its in-board
        // shape and narrowing the FRAMING keeps it — which is the whole difference between the two
        // flags, and the one thing the far outlier alone could not show, because content outside a
        // viewport is culled either way.
        output.WriteLine($"hidden drew {Shapes(hidden)}, framed drew {Shapes(framed)}");
        Assert.True(Shapes(hidden) < Shapes(framed),
            $"--fit-layers drew {Shapes(framed)} shapes and --hide-layers drew {Shapes(hidden)}: "
          + "narrowing the frame must not narrow the picture");

        // And the report says which layers the frame was taken from, so a caller can check it.
        Assert.True(LayerRow(framed, "Metal1").GetProperty("framed").GetBoolean());
        Assert.False(LayerRow(framed, "Silk").GetProperty("framed").GetBoolean());
        Assert.True(LayerRow(framed, "Silk").GetProperty("rendered").GetBoolean());

        // Absent, not false, when nothing narrowed the framing: "framed on everything drawn" is the
        // ordinary rule and reporting it per layer would read as a choice somebody made.
        Assert.False(LayerRow(RenderRawJson(ws.Clay, "--size", "800x600"), "Metal1")
                        .TryGetProperty("framed", out _));
    }

    /// <summary>R-aut12-2's refusals: a frame that was stated outright has nothing for
    /// <c>--fit-layers</c> to decide, and framing on layers that draw nothing is not a page.</summary>
    [Fact]
    public void FitLayers_IsRefusedWithAStatedWindowAndOnLayersThatDrawNothing()
    {
        var ws = BuildWorkspace();
        string outPath = Path.Combine(_root, "never-fit.png");

        var stated = RunCli("render", ws.Clay, "-o", outPath, "--fit-layers", "Metal1",
                            "--window", "0um,0um,400um,300um", "--json");
        Assert.Equal(1, stated.ExitCode);
        Assert.Contains("\"id\": \"render.fit-layers.not-fitting\"", stated.StdOut, StringComparison.Ordinal);

        var empty = RunCli("render", ws.Clay, "-o", outPath, "--fit-layers", "Silk", "--json");
        Assert.Equal(1, empty.ExitCode);
        Assert.Contains("\"id\": \"render.fit-layers.empty\"", empty.StdOut, StringComparison.Ordinal);

        Assert.False(File.Exists(outPath), "a refused render wrote a file");
    }

    /// <summary>Both new options are drawing-layer options, so a schematic, a symbol and a data
    /// display each refuse them BY NAME rather than ignoring them — the rule <c>--layers</c> already
    /// follows, kept in step by <c>Render.LayerOptionNamed</c>.</summary>
    [Theory]
    [InlineData("--fit-layers", "Metal1")]
    [InlineData("--layer-colors", "Metal1=#ff0000")]
    public void TheNewLayerOptions_AreRefusedOnADocumentWithNoLayers(string option, string value)
    {
        var ws = BuildWorkspace();
        foreach (string path in new[] { ws.Csch, ws.Csym })
        {
            var run = RunCli("render", path, "-o", Path.Combine(_root, "never-kind.png"),
                             option, value, "--json");
            Assert.Equal(1, run.ExitCode);
            Assert.Contains("\"id\": \"render.layers.not-applicable\"", run.StdOut, StringComparison.Ordinal);
            Assert.Contains(option, run.StdOut, StringComparison.Ordinal);
        }
    }

    // ── gate 6: the viewport is honoured, measured from the raster ────────────

    /// <summary>
    /// A <c>--window</c> covering a known quarter of the fixture's extents produces an image whose
    /// drawn content matches that quarter — <b>measured from the pixels</b>, as
    /// <c>ComponentPreviewTests</c>' own probe does, not asserted from the transform arithmetic. The
    /// arithmetic is what is under test; re-deriving the expected answer from it would test nothing.
    /// </summary>
    [Fact]
    public void AWindowOverOneQuarter_DrawsThatQuarterAndNoOther()
    {
        var ws = BuildWorkspace();
        // The fixture's Metal1 rectangle is 0..400 µm x 0..200 µm; Metal2's is 500..700 x 50..150.
        // A window over the left half therefore holds Metal1's copper and none of Metal2's.
        string left = Path.Combine(_root, "left.png");
        Assert.Equal(0, RunCli("render", ws.Clay, "-o", left, "--size", "400x400",
                               "--window", "0um,0um,400um,400um").ExitCode);

        string right = Path.Combine(_root, "right.png");
        Assert.Equal(0, RunCli("render", ws.Clay, "-o", right, "--size", "400x400",
                               "--window", "450um,0um,850um,400um").ExitCode);

        var leftInk  = InkBounds(left);
        var rightInk = InkBounds(right);
        output.WriteLine($"left ink: {leftInk}   right ink: {rightInk}");

        // Metal1 spans the left window's full width at its bottom half; Metal2 sits in the middle of
        // the right window. Each picture holds ink, and the two are not the same picture.
        Assert.NotNull(leftInk);
        Assert.NotNull(rightInk);
        Assert.True(leftInk!.Value.MinX < 20, "the left window should start drawing at its left edge");
        Assert.True(rightInk!.Value.MinX > 20, "the right window's shape starts well inside it");
        Assert.NotEqual(File.ReadAllBytes(left), File.ReadAllBytes(right));
    }

    /// <summary>R-rnd2-5: a window whose aspect differs from the output's is LETTERBOXED — the whole
    /// window is shown with the extra filled, never cropped and never stretched — and the resolved
    /// window comes back in the document so a caller can see it happened.</summary>
    [Fact]
    public void AWindowOfADifferentAspect_IsLetterboxedAndSaysSo()
    {
        var ws = BuildWorkspace();
        string json = RenderRawJson(ws.Clay, "--size", "800x400", "--window", "0um,0um,400um,400um");
        output.WriteLine(json);

        Assert.Contains("\"letterboxed\": true", json, StringComparison.Ordinal);

        // The requested window is CONTAINED, exactly: 400 µm across 400 px of height gives 1 px/µm, so
        // the 800 px width shows 800 µm — twice what was asked for, centred, and nothing cropped.
        var m = Regex.Match(json, @"""viewport"":\s*\{[^}]*""x0"":\s*(?<x0>[-\d.E]+)[^}]*""x1"":\s*(?<x1>[-\d.E]+)");
        Assert.True(m.Success, json);
        double x0 = double.Parse(m.Groups["x0"].Value), x1 = double.Parse(m.Groups["x1"].Value);
        Assert.True(x0 <= 0.0 + 1e-12 && x1 >= 400e-6 - 1e-12,
            $"the requested window was not contained: [{x0}, {x1}]");
    }

    // ── gate 7: cancellation ─────────────────────────────────────────────────

    /// <summary>
    /// Gate 7. A render cancelled through <see cref="RunControl"/> exits 130 and leaves NO output
    /// file. A half-written png that a caller reads as a finished one is the failure this prevents,
    /// and it is why the bytes are complete before the file is ever opened.
    /// </summary>
    [Fact]
    public void ACancelledRender_Exits130AndWritesNothing()
    {
        var ws = BuildWorkspace();
        string outPath = Path.Combine(_root, "cancelled.png");

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using (RunHost.Install(cts.Token, observer: null))
            Assert.Equal(130, InProcessCli("render", ws.Clay, "-o", outPath));

        Assert.False(File.Exists(outPath), "a cancelled render published a partial result");
    }

    /// <summary>The vacuity guard for the gate above: the same call with no cancellation writes the
    /// file, so the 130 is the cancellation and not a refusal that happens to share its shape.</summary>
    [Fact]
    public void TheSameRenderWithNoCancellation_Succeeds()
    {
        var ws = BuildWorkspace();
        string outPath = Path.Combine(_root, "not-cancelled.png");
        Assert.Equal(0, InProcessCli("render", ws.Clay, "-o", outPath));
        Assert.True(File.Exists(outPath));
    }

    /// <summary>Progress reaches a HOST as <c>RunProgress</c> observations, through the same
    /// <c>RunControl</c> the <c>em</c> verb uses — which is what will give <c>serve</c> its
    /// <c>notifications/progress</c> with no plumbing in the verb (R-rnd2-10).</summary>
    [Fact]
    public void AHostedRender_ReportsItsStagesThroughRunControl()
    {
        var ws = BuildWorkspace();
        var stages = new List<string>();

        using (RunHost.Install(CancellationToken.None, p => stages.Add(p.Stage)))
            Assert.Equal(0, InProcessCli("render", ws.Clay, "-o", Path.Combine(_root, "hosted.svg")));

        output.WriteLine(string.Join(" -> ", stages.Distinct()));
        Assert.Contains("resolve", stages);
        Assert.Contains("measure", stages);
        Assert.Contains("draw", stages);
        Assert.Contains("encode", stages);
    }

    // ── the document ─────────────────────────────────────────────────────────

    /// <summary>§3.1 and R-rnd2-11 together: with <c>--json</c>, stdout is ONE document and the
    /// progress, notes and stage lines all stay on stderr — so a caller parsing stdout sees the
    /// report and nothing else, exactly as it does for every other verb.</summary>
    [Fact]
    public void UnderJson_StdoutIsOneDocumentAndProgressStaysOnStderr()
    {
        var ws = BuildWorkspace();
        var (exit, stdout, stderr) = RunCli(
            "render", ws.Clay, "-o", Path.Combine(_root, "doc.svg"), "--json");

        Assert.Equal(0, exit);
        Assert.StartsWith("{", stdout.TrimStart(), StringComparison.Ordinal);
        Assert.Contains("[circuitRF] draw", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("[circuitRF]", stdout, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"svg\"", stdout, StringComparison.Ordinal);
        Assert.Contains("\"unit\": \"m\"", stdout, StringComparison.Ordinal);
    }

    /// <summary>R-rnd2-4's other half: a schematic's coordinates are dimensionless design units, and
    /// the document says so rather than leaving a caller to assume metres.</summary>
    [Fact]
    public void ASchematicsCoordinates_AreReportedAsDesignUnits()
    {
        var ws = BuildWorkspace();
        string json = RenderRawJson(ws.Csch);
        Assert.Contains("\"unit\": \"design-units\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"unit\": \"m\"", json, StringComparison.Ordinal);
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    private sealed record Workspace(string Root, string CellDir, string Clay, string Csch, string Csym, string Tech);

    /// <param name="denseContour">Adds a machine-generated 4,000-vertex arc — what an imported
    /// Gerber's flattened copper looks like, and the only kind of geometry the decimation tier can
    /// engage on (it has a hard floor at 16 vertices, so an authored rectangle never decimates).</param>
    /// <param name="undefinedLayer">Adds a shape on a layer key the technology does not declare —
    /// what an import routinely produces, and what the fallback palette exists for.</param>
    /// <param name="outlier">Puts TWO shapes on Silk: one 100 mm away — the shape of an imported
    /// board's drill-map fabrication drawing, which sits far outside the board and, framed with
    /// everything else, shrinks the board to a fraction of the page — and one inside the board
    /// itself. The second is what makes "frame on these, DRAW everything" measurable: with only the
    /// far one, a narrowed frame and a hidden layer produce the same picture, because content
    /// outside the viewport is culled either way (R-aut12-2).</param>
    private Workspace BuildWorkspace(bool denseContour = false, bool undefinedLayer = false,
                                     bool outlier = false)
    {
        string root = Path.Combine(_root, "Demo");
        string cell = Path.Combine(root, "Stage1");
        Directory.CreateDirectory(Path.Combine(cell, "layout"));
        Directory.CreateDirectory(Path.Combine(cell, "schematic"));
        Directory.CreateDirectory(Path.Combine(cell, "symbol"));
        Directory.CreateDirectory(Path.Combine(root, "tech"));

        string tech = Path.Combine(root, "tech", "Fixture.ctech");
        TechPersistence.SaveToFile(tech, TechFixture());

        WorkspacePersistence.SaveToFile(
            Path.Combine(root, ".cws"), new CwsFile { DefaultTechRef = Path.Combine("tech", "Fixture.ctech") });

        string clay = Path.Combine(cell, "layout", "Stage1.clay");
        LayoutPersistence.SaveToFile(clay, LayoutFixture(denseContour, undefinedLayer, outlier));

        string csch = Path.Combine(cell, "schematic", "Stage1.csch");
        SchematicPersistence.SaveToFile(csch, SchematicFixture(), "Stage1");

        string csym = Path.Combine(cell, "symbol", "Stage1.csym");
        SymbolPersistence.SaveToFile(csym, SymbolFixture());

        return new Workspace(root, cell, clay, csch, csym, tech);
    }

    private static Technology TechFixture() => new()
    {
        Name = "RND-2 fixture",
        DefaultDisplayUnit = LayoutUnit.Um,
        Layers =
        [
            new LayerDef { Key = Metal1, Name = "Metal1", Color = new Rgba(200, 30, 30), FillOpacity = 0.5, ZOrder = 3 },
            new LayerDef { Key = Metal2, Name = "Metal2", Color = new Rgba(30, 120, 200), FillOpacity = 0.5, ZOrder = 2 },
            new LayerDef { Key = Silk,   Name = "Silk",   Color = new Rgba(230, 230, 230), FillOpacity = 0.5, ZOrder = 1 },
        ],
    };

    private static LayoutView LayoutFixture(bool denseContour = false, bool undefinedLayer = false,
                                            bool outlier = false)
    {
        var view = new LayoutView { DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Um, SnapDbu = Dbu };
        view.Shapes.Add(new RectShape { Layer = Metal1, X1 = 0, Y1 = 0, X2 = 400 * Dbu, Y2 = 200 * Dbu });
        view.Shapes.Add(new RectShape { Layer = Metal2, X1 = 500 * Dbu, Y1 = 50 * Dbu, X2 = 700 * Dbu, Y2 = 150 * Dbu });
        // Text on purpose: a picture with no glyphs in it cannot show a typeface substitution, which
        // is the one difference RND-1 fixed and gate 1 exists to keep fixed.
        view.Shapes.Add(new LabelShape { Layer = Metal1, X = 20 * Dbu, Y = 100 * Dbu, Text = "PAD 1", Height = 40 * Dbu });

        if (undefinedLayer)
            view.Shapes.Add(new RectShape
            {
                Layer = new LayerKey(99, 0), X1 = 800 * Dbu, Y1 = 0, X2 = 900 * Dbu, Y2 = 100 * Dbu,
            });

        if (outlier)
        {
            view.Shapes.Add(new RectShape
            {
                Layer = Silk, X1 = 100_000 * Dbu, Y1 = 100_000 * Dbu,
                X2 = 100_100 * Dbu, Y2 = 100_100 * Dbu,
            });
            view.Shapes.Add(new RectShape
            {
                Layer = Silk, X1 = 50 * Dbu, Y1 = 50 * Dbu, X2 = 150 * Dbu, Y2 = 150 * Dbu,
            });
        }

        if (denseContour)
        {
            const int n = 4000;
            var xy = new long[2 * n];
            for (int i = 0; i < n; i++)
            {
                double a = 2 * Math.PI * i / n;
                xy[2 * i]     = (long)(200 * Dbu + 190 * Dbu * Math.Cos(a));
                xy[2 * i + 1] = (long)(400 * Dbu + 190 * Dbu * Math.Sin(a));
            }
            view.Shapes.Add(new PolygonShape { Layer = Silk, Xy = xy });
        }
        return view;
    }

    private static SchematicEditModel SchematicFixture()
    {
        var m = new SchematicEditModel { GridSize = 100 };
        m.Components.Add(new EditableComponent
        {
            Symbol = SymbolKind.Resistor, X = 200, Y = 200, InstanceName = "R1",
            Parameters = { new EditableParameter { Name = "R", Expression = "50" } },
        });
        m.Components.Add(new EditableComponent
        {
            Symbol = SymbolKind.Capacitor, X = 600, Y = 200, InstanceName = "C1",
            Parameters = { new EditableParameter { Name = "C", Expression = "1p" } },
        });
        return m;
    }

    private static Symbol SymbolFixture() => new(
        [
            new LinePrimitive { X1 = 20, Y1 = 20, X2 = 180, Y2 = 20 },
            new LinePrimitive { X1 = 180, Y1 = 20, X2 = 180, Y2 = 100 },
            new LinePrimitive { X1 = 180, Y1 = 100, X2 = 20, Y2 = 100 },
            new LinePrimitive { X1 = 20, Y1 = 100, X2 = 20, Y2 = 20 },
            new TextPrimitive { Content = "DUT", AnchorX = 100, AnchorY = 60, FontSize = 14 },
        ],
        [new SymbolPin(20, 60, 0, "in"), new SymbolPin(180, 60, 1, "out")]);

    // ── brief-icp-10: the EM panel's marks, headless ─────────────────────────

    /// <summary><c>render --component-ports</c> draws a setup's cut instance and its face ports from the builder the EM panel calls,
    /// and <c>--solver-geometry</c> the geometry its run solves; <c>--solver-geometry</c> alone, or a setup of another layout, is
    /// refused rather than drawn over geometry it does not describe.</summary>
    [Fact]
    public void AComponentPortSetup_IsMarkedOnItsLayout_AndRefusedOnAnother()
    {
        var m1 = new LayerKey(1, 0);
        Directory.CreateDirectory(_root);
        CircuitRF.Design.Layout.TechPersistence.SaveToFile(Path.Combine(_root, "tech.ctech"), ShippedTechnologies.Load("mmic-GaAs_2LM_100um"));
        WorkspacePersistence.SaveToFile(Path.Combine(_root, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        RectShape R(double x0, double y0, double x1, double y1) => new() { Layer = m1, X1 = (long)(x0 * 1000), Y1 = (long)(y0 * 1000), X2 = (long)(x1 * 1000), Y2 = (long)(y1 * 1000) };
        string seg = CircuitRF.Design.Cells.CellFolder.CreateCellFolder(_root, "Seg");
        var segView = new LayoutView { DbuPerMicron = 1000 };
        segView.Shapes.Add(R(0, -36, 200, 36));
        segView.Pins.Add(new LayoutPin { Name = "A", X = 0, Y = 0, WidthDbu = 72000, OutwardDeg = 180, Layer = m1 });
        segView.Pins.Add(new LayoutPin { Name = "B", X = 200000, Y = 0, WidthDbu = 72000, OutwardDeg = 0, Layer = m1 });
        CircuitRF.Design.Cells.CellCreate.WriteLayoutView(seg, "Seg", segView);
        string top = CircuitRF.Design.Cells.CellFolder.CreateCellFolder(_root, "Top");
        var topView = new LayoutView { DbuPerMicron = 1000, TechRef = "../../tech.ctech" };
        topView.Shapes.Add(R(0, -36, 500, 36));
        topView.Shapes.Add(R(700, -36, 1200, 36));
        topView.Instances.Add(new LayoutInstance { CellRef = "../../Seg", X = 500000, Mag = 1.0, SchematicId = "X1" });
        CircuitRF.Design.Cells.CellCreate.WriteLayoutView(top, "Top", topView);
        string clay = Directory.GetFiles(top, "*.clay", SearchOption.AllDirectories).Single();
        Directory.CreateDirectory(Path.Combine(top, "em"));
        string cem = Path.Combine(top, "em", "cut.cem");
        CircuitRF.Design.Layout.Em.EmSetupPersistence.SaveToFile(cem, new CircuitRF.Design.Layout.Em.EmSetup
        {
            Name = "cut", LayoutRef = "Top/layout/" + Path.GetFileName(clay), AnalysisKind = CircuitRF.Engine.Mom.EmAnalysisKind.Planar,
            ComponentPorts = [new CircuitRF.Design.Layout.Em.EmComponentPort("X1")],
        });
        string png = Path.Combine(_root, "marks.png");

        var (exit, _, stderr) = RunCli("render", clay, "-o", png, "--component-ports", cem, "--solver-geometry");
        output.WriteLine(stderr);
        Assert.True(exit == 0, stderr);
        Assert.Contains("marks: 2 component port(s), 1 cut instance(s)", stderr, StringComparison.Ordinal);

        Assert.NotEqual(0, RunCli("render", clay, "-o", png, "--solver-geometry").ExitCode);
        string seg2 = Directory.GetFiles(seg, "*.clay", SearchOption.AllDirectories).Single();
        var other = RunCli("render", seg2, "-o", png, "--component-ports", cem);
        Assert.NotEqual(0, other.ExitCode);
        Assert.Contains("EM setup of another layout", other.StdErr + other.StdOut, StringComparison.Ordinal);
    }

    // ── driving the verb ─────────────────────────────────────────────────────

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory       = RepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.ArgumentList.Add(CliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        // Both pipes drained CONCURRENTLY: reading one to the end and only then the other deadlocks
        // the moment the child fills the pipe it is not being read from, and `render` writes a stage
        // line per phase to stderr.
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    /// <summary>The same dispatch <c>Program.cs</c> hands the real command line to — for the two gates
    /// that are about what happens inside ONE process. <c>JsonRun</c> is reset first, exactly as
    /// <c>serve</c> resets it between tool calls, because its collectors outlive a call.</summary>
    private static int InProcessCli(params string[] args)
    {
        JsonRun.Reset();
        return CliEntry.Run(args);
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(RenderCliVerbTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        string path = Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
        Assert.True(File.Exists(path), $"the CLI was not built beside these tests: {path}");
        return path;
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir.Length > 0 ? dir : AppContext.BaseDirectory;
    }

    // ── reading the report back ──────────────────────────────────────────────

    private readonly record struct Counters(long ShapesExamined, long ShapesDrawn, long VerticesEmitted);

    private string RenderRawJson(string path, params string[] extra)
    {
        string outPath = Path.Combine(_root, "probe-" + Guid.NewGuid().ToString("N")[..8] + ".png");
        var (exit, stdout, stderr) = RunCli([.. new[] { "render", path, "-o", outPath }, .. extra, "--json"]);
        Assert.True(exit == 0, stderr + stdout);
        return stdout;
    }

    /// <summary>One layer's row of the <c>--json</c> layer report, by name.</summary>
    private static JsonElement LayerRow(string json, string name)
        => JsonDocument.Parse(json).RootElement
               .GetProperty("result").GetProperty("render").GetProperty("layers").EnumerateArray()
               .Single(l => l.GetProperty("name").GetString() == name).Clone();

    private static (double X0, double X1) Viewport(string json)
    {
        var vp = JsonDocument.Parse(json).RootElement
                     .GetProperty("result").GetProperty("render").GetProperty("viewport");
        return (vp.GetProperty("x0").GetDouble(), vp.GetProperty("x1").GetDouble());
    }

    private static long Shapes(string json)
        => JsonDocument.Parse(json).RootElement
               .GetProperty("result").GetProperty("render").GetProperty("counters")
               .GetProperty("shapesDrawn").GetInt64();

    private Counters RenderJson(string path, params string[] extra)
    {
        string json = RenderRawJson(path, extra);
        return new Counters(Field(json, "shapesExamined"), Field(json, "shapesDrawn"), Field(json, "verticesEmitted"));

        static long Field(string json, string name)
        {
            var m = Regex.Match(json, $@"""{name}"":\s*(-?\d+)");
            Assert.True(m.Success, $"{name} missing from the document:\n{json}");
            return long.Parse(m.Groups[1].Value);
        }
    }

    private long VerticesEmitted(Workspace ws, string detail)
        => RenderJson(ws.Clay, "--size", "800x600", "--detail", detail).VerticesEmitted;

    // ── the in-process render gate 1 compares against ────────────────────────

    private static string InProcessLayoutSvg(Workspace ws, LayoutViewport vp)
        => Svg((int)vp.Width, (int)vp.Height, canvas => DrawLayoutLikeTheVerb(ws, vp, canvas));

    private static void DrawLayoutLikeTheVerb(Workspace ws, LayoutViewport vp, SKCanvas canvas)
    {
        var view = LayoutPersistence.LoadFromFile(ws.Clay);
        var tech = TechnologyResolver.ResolveForDocument(
            view.TechRef, Path.GetFullPath(ws.Clay), null, new TechnologyCache()).Resolution.Tech;

        var theme = LayoutRenderTheme.FromTheme(
            ThemeResolver.Resolve(ThemeResolver.DefaultThemeName, ws.Root), ColorVariant.Light);

        // WHAT `--detail full` MEANS, written out: every level-of-detail tier off, so what is stored
        // is what is drawn. Spelled here rather than fetched from the verb on purpose — a silent
        // change to the verb's defaults must fail this test rather than change the picture quietly.
        var opts = new LayoutRenderOptions
        {
            Theme                         = theme,
            ShowGrid                      = false,
            Overlay                       = null,
            ShowRulers                    = true,
            TransparentBackground         = false,
            BaseDir                       = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(ws.Clay))!),
            PathCache                     = null,
            DetailPixelThreshold          = -1,
            LodPixelThreshold             = -1,
            MergeShapeCountThreshold      = -1,
            OutlineVertexBudget           = -1,
            InstanceRasterMaxDevicePixels = -1,
            StrokeElisionPixelThreshold   = -1,
            HairlineFillPixelThreshold    = -1,
            CoarseCoverageThreshold       = -1,
        };

        LayoutRenderer.Draw(canvas, view, tech, vp, opts);
    }

    private static string Svg(int w, int h, Action<SKCanvas> draw)
    {
        using var stream = new SKDynamicMemoryWStream();
        using (var canvas = SKSvgCanvas.Create(new SKRect(0, 0, w, h), stream))
            draw(canvas);
        return SvgFontNormalizer.RepairPositionLists(
            Encoding.UTF8.GetString(stream.DetachAsData().ToArray()));
    }

    private static byte[] Pdf(int w, int h, Action<SKCanvas> draw)
    {
        var metadata = new SKDocumentPdfMetadata { Creator = "circuitRF" };
        using var stream = new SKDynamicMemoryWStream();
        using (var doc = SKDocument.CreatePdf(stream, metadata))
        {
            var canvas = doc.BeginPage(w, h);
            draw(canvas);
            doc.EndPage();
            doc.Close();
        }
        return stream.DetachAsData().ToArray();
    }

    /// <summary>
    /// Byte identity, with the ONE exclusion RND-1 measured and named: Skia's SVG device numbers its
    /// <c>clipPath</c> elements from a counter it does not reset per canvas, so two renders in
    /// DIFFERENT processes carry different ids for the same clip. It is a property of neither code
    /// path. Applied only where the raw bytes actually differ, and reported when it is.
    /// </summary>
    private void AssertSameSvg(string fromCli, string inProcess, string what)
    {
        if (fromCli == inProcess) { output.WriteLine($"{what}: byte-identical"); return; }

        output.WriteLine($"{what}: differ before Skia-id normalisation ({fromCli.Length} vs {inProcess.Length} bytes)");
        output.WriteLine(FirstDifference(fromCli, inProcess) ?? "(lengths only)");
        Assert.Equal(StripSkiaIds(inProcess), StripSkiaIds(fromCli));
    }

    private static string StripSkiaIds(string svg) => Regex.Replace(svg, @"\b(cl|img|gr|fp)_[0-9a-z]+\b", "$1_N");

    private static string StripPdfDates(byte[] pdf) =>
        Regex.Replace(Encoding.Latin1.GetString(pdf), @"D:\d{14}[^)]*", "D:<stamp>");

    private static string? FirstDifference(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
            if (a[i] != b[i])
                return $"first difference at {i}: …{a.Substring(Math.Max(0, i - 60), Math.Min(120, a.Length - Math.Max(0, i - 60)))}…"
                     + $"\n                 vs …{b.Substring(Math.Max(0, i - 60), Math.Min(120, b.Length - Math.Max(0, i - 60)))}…";
        return a.Length == b.Length ? null : $"lengths differ: {a.Length} vs {b.Length}";
    }

    // ── measuring a raster, for gate 6 ───────────────────────────────────────

    /// <summary>The bounding box of everything that is not the background colour — the pixel probe
    /// gate 6 asks for, so the framing is read out of the picture rather than re-derived from the
    /// arithmetic that produced it.</summary>
    private static (int MinX, int MinY, int MaxX, int MaxY)? InkBounds(string pngPath)
    {
        using var bitmap = SKBitmap.Decode(pngPath);
        Assert.NotNull(bitmap);

        var background = bitmap.GetPixel(0, 0);
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < bitmap.Height; y++)
        for (int x = 0; x < bitmap.Width;  x++)
        {
            if (bitmap.GetPixel(x, y) == background) continue;
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
        }
        return maxX < 0 ? null : (minX, minY, maxX, maxY);
    }
}
