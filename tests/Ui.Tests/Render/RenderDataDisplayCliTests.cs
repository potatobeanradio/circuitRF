// ================================================================
//  RND-4's gates (docs/sonnet-briefs/brief-render-4-data-display.md §5) for
//  `circuitrf render <path.cdd>`.
//
//  ── WHAT DID NOT MOVE BELOW THE FIREWALL, so the next person reads the boundary rather than
//     re-measuring it (gate 6) ─────────────────────────────────────────────────────────────────────
//
//  MOVED to CircuitRF.Render.DataDisplay: all fourteen Models (Plot, Trace, Axes, Marker,
//  ContourData, DataDisplayConfig, EngineeringFormat, LoadpullRecognition, Misc/TraceProperties,
//  SummaryColumn*, TraceLabeler, AppSettings), all eight Renderers (Axes, Contour, ContourColormaps,
//  Plot, RendererText, RenderTheme, Table, TraceRenderer_MarkerRenderer), and the seven parsers and
//  resolvers beside them (CubeTraceSpecParser, SliceTokenParser, TraceExpression, VersusResolver,
//  VersusSpec, DbFloor, DataSourceRef) plus ComplexStringHelper. Then four EXTRACTIONS, each of
//  which was a view model's and is now a function both sides call: TraceResolve (was
//  PlotInspectorViewModel's cube slicing), ContourResolve (was TraceRowViewModel.RebuildContour),
//  SummaryResolve (was PlotInspectorViewModel.RebuildSummary), PlotConfigLoader (was
//  DataDisplayViewModel.LoadPlotContainerConfigAsync), plus the composition itself — PlotComposer,
//  PlotDocumentWriter, PlotCanvasGeometry, PlotLabelStrips — out of PlotExporter and
//  PlotContainerViewModel.
//
//  STAYED IN src/Ui, deliberately: every VIEW MODEL (DataDisplayViewModel, DisplayWindowViewModel,
//  PlotInspectorViewModel, TraceRowViewModel, PlotContainerViewModel, MarkerInfoBoxViewModel,
//  DataSourceLibraryViewModel and the rest), every Control and Converter, the undo stack
//  (Models/UndoRedo.cs), DataDisplayDocument (a Dock document), Gesture, NpyFileDragPayload,
//  PlotExporter's own file dialog and clipboard plumbing, PlotAccentColor (it reads
//  Application.Current's resources, and no renderer ever called it), and AppSettingsViewModel, which
//  now wraps the AppSettings that came down.
//
//  The Avalonia surface that had to be replaced to do it was 17 `Rect`, 3 `Point` and a handful of
//  `Media.Color` — PlotRect, PlotPoint and SKColor now. `PlotGeometryParityTests` holds the first
//  two to Avalonia's own semantics.
// ================================================================

using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Text.Json;
using CircuitRF.Cli;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using RfCore.Data;
using RfCore.Export;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Render;

[Collection(CircuitRF.Ui.Tests.SkiaFontsTypefaceCollection.Name)]
public sealed class RenderDataDisplayCliTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-cdd-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    // ── gate 1: byte identity against PlotExporter ────────────────────────────

    /// <summary>
    /// The verb, run as a PROCESS, writes the SVG the application's own Export writes for the same
    /// display — byte for byte, at the default page.
    ///
    /// <para><b>The process is the measurement, not an inconvenience</b> (RenderCliVerbTests' own
    /// note): a second process with no Avalonia host under it is exactly where a substituted
    /// typeface, an uninstalled theme provider or a defaulted enum converter would show up, and none
    /// of those is visible from a same-process call.</para>
    ///
    /// <para><b>SVG rather than PDF for the primary claim</b> because a difference is READABLE — the
    /// failure message can name the glyph run that moved. The PDF case follows and is compared with
    /// no exclusion at all: <c>PlotDocumentWriter</c> writes no CreationDate, so two runs of one
    /// composition are identical.</para>
    /// </summary>
    [Fact]
    public async Task RenderingACddAsAProcess_WritesTheBytesTheApplicationsExportWrites()
    {
        var f = await BuildFixtureAsync();

        string fromApp = PlotExporter.BuildSvgStringForContainers(
            f.Window.DataDisplay!.Plots.ToList(), f.Window.DataDisplay.Theme);

        string outPath = Path.Combine(_root, "cli.svg");
        var (exit, stdout, stderr) = RunCli("render", f.Cdd, "-o", outPath);
        output.WriteLine(stdout + stderr);
        Assert.Equal(0, exit);

        string fromCli = File.ReadAllText(outPath);

        // The one normalisation, named and justified (RND-1 §5.2's precedent, and the same
        // StripSkiaIds RenderCliVerbTests already applies): Skia's SVG element ids come from a
        // counter that lives in the PROCESS, not in the picture. This test process has already
        // emitted other SVGs, so its ids are past `cl_29` while a freshly started CLI's start at
        // `cl_3` — and the counter is HEX, so the ids are not even the same LENGTH. Nothing else is
        // excluded; every coordinate, colour and glyph run is compared as written.
        Assert.Equal(StripSkiaIds(fromApp), StripSkiaIds(fromCli));
    }

    [Fact]
    public async Task RenderingACddToPdf_WritesTheBytesTheApplicationsExportWrites()
    {
        var f = await BuildFixtureAsync();

        byte[] fromApp = PlotExporter.BuildPdfBytesForContainers(
            f.Window.DataDisplay!.Plots.ToList(), f.Window.DataDisplay.Theme);

        string outPath = Path.Combine(_root, "cli.pdf");
        var (exit, stdout, stderr) = RunCli("render", f.Cdd, "-o", outPath, "--format", "pdf");
        output.WriteLine(stdout + stderr);
        Assert.Equal(0, exit);

        byte[] fromCli = File.ReadAllBytes(outPath);

        // The one field that differs by construction: a WRITTEN pdf carries the output file's own
        // name as its metadata Title (which is what Export does), and the clipboard form has none.
        // Compared by rendering the application's side through the same writer with the same title,
        // rather than by excluding bytes — an exclusion that grows is how "byte identity" becomes
        // "close enough".
        byte[] fromAppTitled = PlotDocumentWriter.BuildPdfBytes(
            canvas => PlotComposer.Render(
                canvas, f.Window.DataDisplay.Plots.Select(PlotExporter.Place).ToList(),
                f.Window.DataDisplay.Theme, AppSettings.Current, PagePlacement.Letter),
            PagePlacement.Letter, PlotDocumentWriter.PdfTitleFor(outPath));

        Assert.Equal(fromAppTitled.Length, fromCli.Length);
        Assert.True(fromAppTitled.AsSpan().SequenceEqual(fromCli),
                    "the pdf the verb wrote differs from the one the application's export writes");
        Assert.NotEmpty(fromApp);
    }


    // ── gate 2: a trace of every kind renders identically ─────────────────────

    /// <summary>
    /// One fixture per trace kind the resolution path supports, each authored through the
    /// application's own view models, saved as a <c>.cdd</c>, and then rendered BOTH by the verb (as
    /// a process) and by the application's own export — byte for byte.
    ///
    /// <para><b>A kind with no test is a kind that will drift</b> (§5.2), and this is the list §2's
    /// measurement found the closure of. The seven here are every kind
    /// <c>PlotConfigLoader</c> can construct: a plain cube slice, a typed expression, a "plot
    /// versus", a derived scalar metric, an S→Z conversion, a Γ-plane stability circle, and — from a
    /// loadpull source — a contour and a summary-table column, which are the two whose data is not
    /// in the `.cdd` at all and is re-derived on every open.</para>
    /// </summary>
    [Theory]
    [InlineData(TraceKind.Cube)]
    [InlineData(TraceKind.Expression)]
    [InlineData(TraceKind.Versus)]
    [InlineData(TraceKind.DerivedMetric)]
    [InlineData(TraceKind.NetworkConversion)]
    [InlineData(TraceKind.StabilityCircle)]
    [InlineData(TraceKind.Contour)]
    [InlineData(TraceKind.SummaryTable)]
    public async Task EveryTraceKind_RendersTheBytesTheApplicationsExportWrites(TraceKind kind)
    {
        var f = await BuildFixtureAsync(kind: kind);

        string fromApp = PlotExporter.BuildSvgStringForContainers(
            f.Window.DataDisplay!.Plots.ToList(), f.Window.DataDisplay.Theme);

        string outPath = Path.Combine(_root, $"{kind}.svg");
        var (exit, stdout, stderr) = RunCli("render", f.Cdd, "-o", outPath);
        output.WriteLine(stdout + stderr);
        Assert.Equal(0, exit);

        Assert.Equal(StripSkiaIds(fromApp), StripSkiaIds(File.ReadAllText(outPath)));

        // And the picture is not empty — a gate that passes because both sides drew nothing would be
        // the worst possible outcome of this brief (R-rnd4-4's reasoning, applied to the test).
        Assert.Contains("<path", File.ReadAllText(outPath));
    }

    public enum TraceKind
    {
        /// <summary>A picker-authored cube slice — S(1,1) out of the simulated S cube.</summary>
        Cube,
        /// <summary>A typed multi-cube expression, evaluated by TraceExpression.</summary>
        Expression,
        /// <summary>Y against a cube X rather than against frequency.</summary>
        Versus,
        /// <summary>A real scalar versus frequency out of NetworkMetrics — Max Gain.</summary>
        DerivedMetric,
        /// <summary>The same network read as Z rather than S, through the virtual Z cube.</summary>
        NetworkConversion,
        /// <summary>A Γ-plane locus: one circle per frequency, geometry re-derived on every open.</summary>
        StabilityCircle,
        /// <summary>A loadpull contour — re-fitted from the surface, nothing of it stored.</summary>
        Contour,
        /// <summary>A loadpull summary-table column — likewise re-derived, at the plot's compression.</summary>
        SummaryTable,
    }

    // ── gate 3: an unresolvable source is refused, not drawn ──────────────────

    /// <summary>
    /// R-rnd4-4. A display whose traces read the <c>run.npy</c> sentinel, with no <c>--data</c> and
    /// nothing beside it to bind, exits 1 naming BOTH the sentinel and the flag — and writes no file.
    ///
    /// <para><b>This is the most important refusal in the brief.</b> The alternative is an empty
    /// plot, which is a valid picture, exports cleanly, and looks exactly like a measurement that
    /// came back empty.</para>
    /// </summary>
    [Fact]
    public async Task ACddWhoseSourceIsNotHere_IsRefusedNamingTheFlag_AndWritesNothing()
    {
        var f = await BuildFixtureAsync();

        // Move the display somewhere its source cannot be found from.
        string orphanDir = Path.Combine(_root, "orphan");
        Directory.CreateDirectory(orphanDir);
        string orphan = Path.Combine(orphanDir, "display.cdd");
        File.Copy(f.Cdd, orphan);

        string outPath = Path.Combine(orphanDir, "out.svg");
        var (exit, _, stderr) = RunCli("render", orphan, "-o", outPath);
        output.WriteLine(stderr);

        Assert.Equal(1, exit);
        Assert.Contains("--data", stderr);
        Assert.False(File.Exists(outPath), "a refused render must write no file");
    }

    /// <summary>R-rnd4-4's last clause: a <c>--data</c> that binds nothing is a refusal, not a shrug.</summary>
    [Fact]
    public async Task DataThatBindsNothing_IsRefused()
    {
        var f = await BuildFixtureAsync();

        string stray = Path.Combine(_root, "unrelated.npy");
        WriteRun(stray);

        // Two --data where the document reads one source: the second binds nothing.
        var (exit, _, stderr) = RunCli("render", f.Cdd, "-o", Path.Combine(_root, "x.svg"),
                                       "--data", f.Npy, "--data", stray);
        output.WriteLine(stderr);
        Assert.Equal(1, exit);
        Assert.Contains("matches nothing", stderr, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A named source the document CAN use is bound, and reported as coming from the flag.</summary>
    [Fact]
    public async Task DataBindsTheSentinel_AndTheReportSaysSo()
    {
        var f = await BuildFixtureAsync();

        string orphanDir = Path.Combine(_root, "orphan2");
        Directory.CreateDirectory(orphanDir);
        string orphan = Path.Combine(orphanDir, "display.cdd");
        File.Copy(f.Cdd, orphan);

        string outPath = Path.Combine(orphanDir, "out.svg");
        var (exit, stdout, stderr) = RunCli("render", orphan, "-o", outPath,
                                            "--data", f.Npy, "--json");
        output.WriteLine(stdout + stderr);
        Assert.Equal(0, exit);
        Assert.True(File.Exists(outPath));

        using var doc = JsonDocument.Parse(stdout);
        var dd = doc.RootElement.GetProperty("result").GetProperty("render").GetProperty("dataDisplay");
        var src = dd.GetProperty("sources").EnumerateArray().Single();
        Assert.Equal("--data", src.GetProperty("boundBy").GetString());
        Assert.Equal(1, dd.GetProperty("pages").GetInt32());
    }

    // ── gate 4: --all-tabs is PDF's alone ─────────────────────────────────────

    /// <summary>
    /// R-rnd4-5. <c>SKDocument</c> is a multi-page format and SVG and PNG are not, and writing
    /// <c>out-1.svg</c>, <c>out-2.svg</c> from one <c>-o</c> is a filename the tool invented, which
    /// R-rnd0-6 forbids. So it is a refusal naming <c>--tab</c>, and the PDF produces one page per
    /// tab.
    /// </summary>
    [Fact]
    public async Task AllTabsOnSvg_IsRefused_AndOnPdfProducesOnePagePerTab()
    {
        var f = await BuildFixtureAsync(tabs: 3);

        var (svgExit, _, svgErr) = RunCli("render", f.Cdd, "-o", Path.Combine(_root, "a.svg"),
                                          "--all-tabs");
        output.WriteLine(svgErr);
        Assert.Equal(1, svgExit);
        Assert.Contains("--tab", svgErr);

        string pdf = Path.Combine(_root, "a.pdf");
        var (pdfExit, pdfOut, pdfErr) = RunCli("render", f.Cdd, "-o", pdf, "--all-tabs", "--json");
        output.WriteLine(pdfOut + pdfErr);
        Assert.Equal(0, pdfExit);

        using var doc = JsonDocument.Parse(pdfOut);
        Assert.Equal(3, doc.RootElement.GetProperty("result").GetProperty("render")
                              .GetProperty("dataDisplay").GetProperty("pages").GetInt32());

        // Three /Type /Page objects, which is what a three-page PDF has.
        string raw = File.ReadAllText(pdf, Encoding.Latin1);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(raw, @"/Type\s*/Page[^s]").Count);
    }

    // ── selection ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoSuchTab_IsRefusedListingTheTabs()
    {
        var f = await BuildFixtureAsync(tabs: 2);
        var (exit, _, stderr) = RunCli("render", f.Cdd, "-o", Path.Combine(_root, "b.svg"),
                                       "--tab", "Nope");
        output.WriteLine(stderr);
        Assert.Equal(1, exit);
        // The refusal LISTS what there is, which is the half a caller cannot otherwise see. The
        // first tab's name is the display's own, so the assertion is on the second and on the
        // 1-based numbering rather than on a literal this test would have to keep in step.
        Assert.Contains("Tab 2", stderr);
        Assert.Contains("(1)", stderr);
    }

    /// <summary>
    /// An option that describes a DRAWING is named, not ignored — R-rnd0-6's rule.
    ///
    /// <para><c>--margin</c> and <c>--theme</c> were both being read, both being dropped and both
    /// producing a picture with nothing said. They are here for the reason the others are: a caller
    /// that passed a flag and got a full, correct-looking picture back has no way to learn its flag
    /// did nothing. <c>--theme</c> refuses with its own sentence, because its reason is different —
    /// a display's palette is not a <c>.ccolor</c> at all, and the remedy is <c>--variant</c>.</para>
    /// </summary>
    [Theory]
    [InlineData("--window", "0,0,1,1")]
    [InlineData("--layers", "M1")]
    [InlineData("--detail", "screen")]
    [InlineData("--margin", "0.3")]
    [InlineData("--theme", "Default")]
    public async Task AnOptionThatMeansNothingForADisplay_IsRefusedByName(string option, string value)
    {
        var f = await BuildFixtureAsync();
        var (exit, _, stderr) = RunCli("render", f.Cdd, "-o", Path.Combine(_root, "c.svg"),
                                       option, value);
        output.WriteLine(stderr);
        Assert.Equal(1, exit);
        Assert.Contains(option, stderr);
        Assert.False(File.Exists(Path.Combine(_root, "c.svg")), "a refused render wrote a file");
    }

    /// <summary>Both spellings of "which tab" are refused TOGETHER, not ordered — R-rnd2-3's rule for
    /// the viewport modes, and the same reason: silently honouring one of them writes a file the
    /// caller did not ask for.</summary>
    [Fact]
    public async Task NamingATabAndAskingForAllOfThem_IsRefused()
    {
        var f = await BuildFixtureAsync(tabs: 2);
        string outPath = Path.Combine(_root, "both.pdf");
        var (exit, stdout, _) = RunCli("render", f.Cdd, "-o", outPath,
                                       "--all-tabs", "--tab", "Tab 2", "--json");
        Assert.Equal(1, exit);
        Assert.Contains("\"id\": \"render.tab.conflict\"", stdout, StringComparison.Ordinal);
        Assert.False(File.Exists(outPath));
    }

    /// <summary>The remedy the <c>--theme</c> refusal names actually works: a display HAS two
    /// palettes and <c>--variant</c> is how they are chosen. Without this the refusal above would be
    /// satisfied by a verb that simply had no colour control at all.</summary>
    [Fact]
    public async Task TheVariantTheThemeRefusalNames_ChangesThePicture()
    {
        var f = await BuildFixtureAsync();
        string light = Path.Combine(_root, "light.png");
        string dark  = Path.Combine(_root, "dark.png");

        Assert.Equal(0, RunCli("render", f.Cdd, "-o", light, "--variant", "light").ExitCode);
        Assert.Equal(0, RunCli("render", f.Cdd, "-o", dark,  "--variant", "dark").ExitCode);
        Assert.NotEqual(File.ReadAllBytes(light), File.ReadAllBytes(dark));
    }

    // ── R-rnd4-6: the page is a parameter, and the composition survived it ────

    /// <summary>
    /// A non-default page is not a crop and not a re-layout: the composition scales uniformly, so
    /// every object keeps its position RELATIVE to the others — which is what makes a dragged marker
    /// info box land where it sits on screen. Asserted by rendering the same display at 1x and at 2x
    /// the page and comparing the plot rectangle's corners, which must land at exactly twice the
    /// coordinates.
    ///
    /// <para>This is the regression R-rnd4-6 asks to look for, and it is invisible at the default.</para>
    /// </summary>
    [Fact]
    public async Task DoublingThePage_ScalesTheCompositionUniformly()
    {
        var f = await BuildFixtureAsync();

        var placed = PlotConfigLoader.LoadPlot(FirstPlotConfig(f.Cdd), new NullSources(f.Npy));
        _ = placed;

        string small = Path.Combine(_root, "small.svg");
        string big   = Path.Combine(_root, "big.svg");
        Assert.Equal(0, RunCli("render", f.Cdd, "-o", small).ExitCode);
        Assert.Equal(0, RunCli("render", f.Cdd, "-o", big, "--size", "1584x1224").ExitCode);

        (double W, double H) sizeSmall = SvgSize(File.ReadAllText(small));
        (double W, double H) sizeBig   = SvgSize(File.ReadAllText(big));
        Assert.Equal(792, sizeSmall.W, 3);
        Assert.Equal(1584, sizeBig.W, 3);

        // Every path coordinate doubles. Comparing the FIRST moveto of the first path is enough to
        // catch a composition that re-fitted rather than scaled — a re-fit changes the pad, and the
        // pad is what moves an object away from 2x.
        var (sx, sy) = FirstMoveTo(File.ReadAllText(small));
        var (bx, by) = FirstMoveTo(File.ReadAllText(big));
        Assert.Equal(sx * 2, bx, 2);
        Assert.Equal(sy * 2, by, 2);
    }

    // ── the fixture ───────────────────────────────────────────────────────────

    private sealed record Fixture(string Cdd, string Npy, DisplayWindowViewModel Window);

    /// <summary>
    /// A real display, authored through the application's own view models and saved with its own
    /// writer — so the `.cdd` under test is the file the application produces, not one this test
    /// composed by hand.
    /// </summary>
    private async Task<Fixture> BuildFixtureAsync(int tabs = 1, TraceKind kind = TraceKind.Cube)
    {
        Directory.CreateDirectory(_root);

        // NOT "run.npy". The flat results directory names a run after its schematic, and "run.npy"
        // is ALSO the string DataSourceRef.Selected uses for "whatever is selected" — so a source
        // literally called that resolves through the sentinel branch of the library's own
        // ResolveAbs and never gets selected at all. A fixture built on it silently authors a
        // display with no traces, which is a display this brief's gates cannot say anything about.
        bool   loadpull = kind is TraceKind.Contour or TraceKind.SummaryTable;
        string npy      = Path.Combine(_root, "ampA.npy");
        if (loadpull) WriteLoadpullRun(npy); else WriteRun(npy);

        var window = await OpenAsync(npy);

        // Table for a summary column (it is a Table-only construct), Smith for a contour and for a
        // stability circle (both are Γ-plane), Rect for everything else.
        if (kind == TraceKind.SummaryTable)
            window.DataDisplay!.Plots[0].Inspector.PlotType = PlotType.Table;
        else if (kind is TraceKind.Contour or TraceKind.StabilityCircle)
            window.DataDisplay!.Plots[0].Inspector.PlotType = PlotType.Smith;
        else
            window.DataDisplay!.Plots[0].Inspector.PlotType = PlotType.Rect;

        var first = window.DataDisplay!.Plots[0];
        AddTraceOfKind(window.DataDisplay, first, kind);

        // A second plot, so the composition is a real bounding-box fit over more than one object
        // rather than a single centred plot — which is the arithmetic R-rnd4-6 asks to be sure of.
        if (!loadpull)
        {
            var rect = window.DataDisplay.AddPlot(PlotType.Rect, left: 600, top: 40);
            rect.Inspector.AddTraceCommand.Execute(null);
        }

        for (int i = 1; i < tabs; i++) window.NewTabCommand.Execute(null);

        // The fixture is worth nothing if its display is empty — which is exactly what a silently
        // unselected source produces, and what this assertion exists to catch.
        Assert.All(window.DataDisplay.Plots, c => Assert.NotEmpty(c.PlotVM.Plot.Traces));

        // …and a marker with a shown info box on the plot under test, for every kind that can carry
        // one. The info box is the only object in the composition that can sit OUTSIDE the plot it
        // belongs to, so it is the part of the bounding-box fit most worth comparing; a guard that
        // silently skipped it would leave that untested and the gate would still be green.
        if (kind is not (TraceKind.Contour or TraceKind.SummaryTable))
            Assert.Contains(first.PlotVM.Plot.Traces, t => t.Markers.Any(m => m.ShowInfoBox));

        string cdd = Path.Combine(_root, "display.cdd");
        await window.SaveAllAsync(cdd);

        // Re-open so the in-process side under test is a LOADED display, exactly as the CLI's is —
        // comparing a freshly authored display with a loaded one would be comparing two different
        // states of the same document.
        var reopened = await OpenAsync(npy);
        await reopened.LoadAllAsync(cdd);

        return new Fixture(cdd, npy, reopened);
    }

    private async Task<DisplayWindowViewModel> OpenAsync(string npy)
    {
        var window = new DisplayWindowViewModel();
        window.DataSourceLibrary.ResultsRootProvider = () => _root;
        window.GetResultsRootAction = () => _root;
        await window.DataSourceLibrary.LoadFileAsync(npy);
        window.DataSourceLibrary.RefreshAvailableDataSources();
        await window.DataSourceLibrary.SelectDataSourceAsync(Path.GetFileName(npy));
        return window;
    }

    /// <summary>
    /// Adds one trace of the requested kind through the application's own commands, then states
    /// what makes it that kind on the MODEL — which is what a `.cdd` persists, and therefore what
    /// the verb has to re-resolve.
    /// </summary>
    private static void AddTraceOfKind(DataDisplayViewModel display, PlotContainerViewModel c, TraceKind kind)
    {
        var inspector = c.Inspector;
        var plot      = c.PlotVM.Plot;

        if (kind == TraceKind.Contour)      { inspector.AddContourTraceCommand.Execute(null); return; }
        if (kind == TraceKind.SummaryTable) { inspector.AddSummaryTraceCommand.Execute(null); return; }

        inspector.AddTraceCommand.Execute(null);
        var t = plot.Traces[^1];

        switch (kind)
        {
            case TraceKind.Cube:
                break;   // the seeded cube slice, unchanged

            case TraceKind.Expression:
                t.CubeName   = null;
                t.Expression = "dB20(SP1.S[:, 1, 1]) + 3";
                break;

            case TraceKind.Versus:
                // Y against another cube's values rather than against the sweep axis.
                t.Expression = "SP1.S[:, 2, 1]";
                t.XSpec      = "SP1.S[:, 1, 1]";
                break;

            case TraceKind.DerivedMetric:
                // A derived metric reads MATRICES, so the trace has to hold the source's network
                // view rather than the placeholder SNP a seeded CUBE trace carries. On the loading
                // side PlotConfigLoader does this from IPlotDataSources.NetworkFor; here it is done
                // by hand because the seed went down the cube branch.
                t.CubeName   = null;
                t.Expression = null;
                t.Data       = c.Library!.SelectedEntry!.NetworkView!;
                t.Derived    = DerivedParameters.MaxGain;
                break;

            case TraceKind.NetworkConversion:
                // The virtual Z cube — materialized from S + Z0 on first read, and an ordinary cube
                // from there on, which is why nothing about the render path special-cases it.
                t.CubeName = "SP1.Z";
                break;

            case TraceKind.StabilityCircle:
                t.CubeName   = null;
                t.Expression = null;
                t.Data       = c.Library!.SelectedEntry!.NetworkView!;
                t.Derived    = DerivedParameters.LoadStabilityCircle;
                break;
        }

        // The card does this after any edit; a test that skipped it would save a trace whose points
        // had not been rebuilt for its new spec.
        PlotInspectorViewModel.TrySetCubeData(t, c.Library, plot.PlotType, plot.FreqUnits);
        t.BuildPath(plot.PlotType, plot.FreqUnits);
        plot.Autoscale(force: true);

        // A MARKER with its info box shown, on every kind that can carry one. R-rnd4-7 lists the
        // marker glyph and its info box among the things that come OUT in an export, and the box is
        // the only object in the composition that can sit outside the plot it belongs to — so a
        // fixture without one would leave the part of the bounding-box fit that matters most
        // untested. Its position is left unset here, which exercises the default placement rule
        // (PlotCanvasGeometry.DefaultInfoBoxPosition) on both sides.
        if (t.Points.Count > 0 || t.IsStabilityCircle)
        {
            var m = new Marker(t, plot.PlotType == PlotType.Smith ? 0 : t.Points[0].X,
                               isMulti: false, isDelta: false, index: 1, plot.FreqUnits)
            { ShowInfoBox = true };
            display.InternalAddMarker(m, t, c);
        }
    }

    /// <summary>
    /// A simulated S-parameter run: a (freq, i, j) S cube and a per-port Z0, in a named analysis
    /// group — which is the shape a real <c>circuitrf sparam</c> writes, and the shape that makes
    /// the trace card offer S(i,j) items and the virtual Z/Y cubes materialize.
    /// </summary>
    private static void WriteRun(string path)
    {
        double[] freqs = [1e9, 1.5e9, 2e9];
        var f  = new Axis("freq", freqs, "Hz");
        var ai = new Axis("i", [1, 2]);
        var aj = new Axis("j", [1, 2]);

        var s = new Complex[freqs.Length * 2 * 2];
        for (int k = 0; k < freqs.Length; k++)
        {
            double t = k / (double)(freqs.Length - 1);
            s[k * 4 + 0] = new Complex(0.30 - 0.10 * t, -0.20 + 0.05 * t);   // S11
            s[k * 4 + 1] = new Complex(0.02, 0.01);                          // S12
            s[k * 4 + 2] = new Complex(2.50 - 0.40 * t,  0.30 - 0.10 * t);   // S21
            s[k * 4 + 3] = new Complex(0.15 + 0.05 * t, -0.10);              // S22
        }

        var ds = new DataSet();
        ds.AddToGroup("SP1", "S",  new DataCube([f, ai, aj], s));
        // COMPLEX, not real: DataSetBuilder.ClassifyZ0 reads ComplexValues, and a real Z0 cube
        // throws there before the source ever reaches the library.
        ds.AddToGroup("SP1", "Z0", new DataCube([new Axis("port", [1, 2])],
                                                new Complex[] { new(50, 0), new(50, 0) }));
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
    }

    /// <summary>
    /// A simulated loadpull run, in the shape <c>LoadpullEngine.BuildLoadpullDataSet</c> writes and
    /// <c>LoadpullRecognition</c> keys on: a termination cube over <c>gridPoint</c> and figure-of-
    /// merit cubes over <c>{gridPoint, pinStep}</c>, under a named analysis group.
    /// </summary>
    private static void WriteLoadpullRun(string path)
    {
        Complex[] gamma =
        [
            new(0.00, 0.00), new(0.40, 0.00), new(-0.40, 0.00), new(0.00, 0.40),
            new(0.00, -0.40), new(0.28, 0.28), new(-0.28, 0.28), new(0.28, -0.28),
            new(-0.28, -0.28),
        ];

        var grid = new Axis("gridPoint", Enumerable.Range(0, gamma.Length).Select(i => (double)i).ToArray());
        var pin  = new Axis("pinStep", [0.0, 1.0, 2.0]);

        var zLoad = gamma.Select(g => 50.0 * (1 + g) / (1 - g)).ToArray();
        var pout  = new double[gamma.Length * 3];
        var eff   = new double[gamma.Length * 3];
        for (int g = 0; g < gamma.Length; g++)
        for (int p = 0; p < 3; p++)
        {
            // A smooth bowl over the Γ grid, so the fit has something to contour rather than noise.
            double r = gamma[g].Magnitude;
            pout[g * 3 + p] = 30.0 - 8.0 * r * r + 0.8 * p;
            eff [g * 3 + p] = 55.0 - 20.0 * r * r - 1.5 * p;
        }

        var ds = new DataSet();
        ds.AddToGroup("LP1", "GammaLoad",  new DataCube([grid], gamma));
        ds.AddToGroup("LP1", "ZLoad",      new DataCube([grid], zLoad));
        ds.AddToGroup("LP1", "Pout_dBm",   new DataCube([grid, pin], pout));
        ds.AddToGroup("LP1", "Efficiency", new DataCube([grid, pin], eff));
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
    }

    private static PlotContainerConfig FirstPlotConfig(string cdd)
    {
        var cfg = JsonSerializer.Deserialize<DataDisplayConfig>(
            File.ReadAllText(cdd), DataDisplayJson.Options)!;
        return cfg.Tabs.Count > 0 ? cfg.Tabs[0].Plots[0] : cfg.Plots[0];
    }

    /// <summary>A source provider over one file, for the arithmetic-only assertions.</summary>
    private sealed class NullSources(string npy) : IPlotDataSources
    {
        private readonly DataSet _ds = DataSetImporter.Import(npy).DataSet;
        public string? ResolveAbs(string? sourceRef) => npy;
        public bool Contains(string absPath) => true;
        public SNP? NetworkFor(string absPath) => null;
        public DataSet? DataFor(string absPath) => _ds;
        public string? AliasFor(string absPath) => null;
        public string? DisplayNameFor(string absPath) => Path.GetFileName(npy);
        public bool HasMultipleSources => false;
        public DataSet? SelectedData => _ds;
    }

    /// <summary>See the call site: Skia's SVG ids are a per-PROCESS counter, in hex.</summary>
    private static string StripSkiaIds(string svg) =>
        System.Text.RegularExpressions.Regex.Replace(svg, @"\b(cl|img|gr|fp)_[0-9a-z]+\b", "$1_N");

    // ── reading an SVG back ───────────────────────────────────────────────────

    private static (double W, double H) SvgSize(string svg)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            svg, "width=\"([0-9.]+)\"\\s+height=\"([0-9.]+)\"");
        Assert.True(m.Success, "the svg carries no width/height");
        return (double.Parse(m.Groups[1].Value), double.Parse(m.Groups[2].Value));
    }

    private static (double X, double Y) FirstMoveTo(string svg)
    {
        var m = System.Text.RegularExpressions.Regex.Match(svg, @"[Mm]\s*(-?[0-9.]+)[, ](-?[0-9.]+)");
        Assert.True(m.Success, "the svg carries no path data");
        return (double.Parse(m.Groups[1].Value), double.Parse(m.Groups[2].Value));
    }

    // ── driving the verb ──────────────────────────────────────────────────────

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
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(
                typeof(RenderDataDisplayCliTests).Assembly)
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
}
