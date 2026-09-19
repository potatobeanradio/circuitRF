using System.Text;
using System.Text.Json;
using CircuitRF.Render.DataDisplay;
using RfCore.Export;
using SkiaSharp;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf render &lt;path.cdd&gt; -o &lt;out.svg|.pdf|.png&gt;</c> — the Data Display half of
/// <c>render</c> (brief-render-4-data-display.md).
///
/// <para><b>Three jobs, and this file only does the first.</b> Rendering a <c>.cdd</c> is: resolve
/// its sources to <c>DataSet</c>s; resolve each trace against its cube; compose and draw. Only the
/// first is this verb's — the other two are <c>CircuitRF.Render.DataDisplay</c>'s
/// <see cref="PlotConfigLoader"/>, <see cref="TraceResolve"/> and <see cref="PlotComposer"/>, which
/// are the same functions the window opens and exports a display with (R-rnd4-2). What is here is
/// argument parsing, source binding, refusals and reporting — <c>Authoring.cs</c>' terms, which
/// <c>Render.cs</c> already follows for the drawing documents.</para>
///
/// <para><b>An empty plot is the single most dangerous output in this brief</b> (R-rnd4-4). It is a
/// valid picture, it exports cleanly, and it looks exactly like a measurement that came back empty.
/// So every source the document names is resolved BEFORE anything is drawn, and one that cannot be
/// is a refusal naming <c>--data</c> — including the <c>run.npy</c> sentinel, which means "whatever
/// this document has SELECTED" and has no answer where there is no window to select in.</para>
///
/// <para><b>It writes nothing until the bytes are complete</b>, for <c>Render.Emit</c>'s reason: a
/// cancelled run leaves no output file.</para>
/// </summary>
internal static class RenderDataDisplay
{
    /// <summary>Everything <c>Render</c>'s own argument parse decided that this half needs.</summary>
    internal readonly record struct Request(
        string       Output,
        string       Format,
        List<string> Data,
        string?      Tab,
        int?         PlotIndex,
        bool         AllTabs,
        int?         PageWidth,
        int?         PageHeight,
        double       Scale,
        /// <summary>Null when --background was not typed: the display then takes the application's
        /// own export default, which is what §5.1's byte-identity gate compares against.</summary>
        bool?        Transparent,
        bool         Dark);

    public static int Draw(string path, Request req)
    {
        JsonRun.InputPath = path;

        // ── the document ─────────────────────────────────────────────────────

        DataDisplayConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<DataDisplayConfig>(
                File.ReadAllText(path), DataDisplayJson.Options);
        }
        catch (Exception ex)
        {
            return JsonRun.Fail(CliDiagnostics.RenderCddUnreadable(path, ex.Message));
        }
        if (config is null) return JsonRun.Fail(CliDiagnostics.RenderCddUnreadable(path, "it is not JSON"));

        return Draw(path, config, req);
    }

    /// <summary>
    /// The same render, over a document already in memory (R-aut11-2).
    ///
    /// <para><b>This is what makes `plot` one plotting path rather than two.</b> That verb builds a
    /// <see cref="DataDisplayConfig"/> — the very document a `.cdd` deserializes to — and hands it
    /// here, so every decision below (source binding, plot loading, placement, page, theme, encoding)
    /// is made once, by this function, for a hand-authored display and a generated one alike. A
    /// second composer in `plot` would have drifted, and a plausible picture is indistinguishable
    /// from a right one.</para>
    /// </summary>
    /// <param name="path">
    /// The document's own path for the purposes of resolving its sources — the directory a relative
    /// reference is looked for beside. `plot` passes the RESULT file it was given, which is where
    /// the source it names actually is.
    /// </param>
    /// <param name="reportKind">
    /// What the report calls this input. A `.cdd` is a data display; `plot`'s input is a result file,
    /// and calling it a data display in the document would be a claim about a file that is not one.
    /// </param>
    public static int Draw(string path, DataDisplayConfig config, Request req,
                           string? reportKind = null)
    {
        // A v1 file (or a clipboard fragment) carries its plots in the legacy top-level list; a v2
        // one carries tabs. Both are read here for the reason the application reads both — the
        // format is the contract, and a caller that has a `.cdd` did not choose its version.
        var tabs = config.Tabs.Count > 0
            ? config.Tabs
            : config.Plots.Count > 0
                ? [new TabConfig { Name = "Tab 1", Plots = config.Plots,
                                   ZoomLevel = config.ZoomLevel,
                                   ViewOffsetX = config.ViewOffsetX, ViewOffsetY = config.ViewOffsetY }]
                : (IReadOnlyList<TabConfig>)Array.Empty<TabConfig>();

        if (tabs.Count == 0 || tabs.All(t => t.Plots.Count == 0))
            return JsonRun.Fail(CliDiagnostics.RenderCddEmpty(path));

        // ── R-rnd4-5: which tabs, and whether the format can hold them ────────

        if (req.AllTabs && req.Format != "pdf")
            return JsonRun.Fail(CliDiagnostics.RenderAllTabsNotMultiPage(req.Format));

        // Two answers to "which tab", refused together rather than ordered — R-rnd2-3's rule for the
        // viewport modes, and the same reason: a precedence nobody stated is an invention, and the
        // one that would have lost is the one the caller typed LAST and most specifically.
        if (req.AllTabs && req.Tab is not null)
            return JsonRun.Fail(CliDiagnostics.RenderTabAndAllTabs());

        int tabIndex = 0;
        if (req.Tab is { } wanted)
        {
            int byName = IndexOfTab(tabs, wanted);
            if (byName < 0) return JsonRun.Fail(CliDiagnostics.RenderNoSuchTab(wanted, DescribeTabs(tabs)));
            tabIndex = byName;
        }
        else if (!req.AllTabs)
        {
            // The tab the document OPENS on, which is what "the default is the whole canvas of the
            // first tab" means for a file that remembers which one was active.
            tabIndex = Math.Clamp(config.ActiveTabIndex, 0, tabs.Count - 1);
        }

        var pages = req.AllTabs ? Enumerable.Range(0, tabs.Count).ToList() : [tabIndex];

        // ── sources ──────────────────────────────────────────────────────────

        var (sources, refusal) = CddSources.Bind(path, config, tabs, pages, req.Data);
        if (refusal is { } sr) return sr;

        RunHost.Cancellation.ThrowIfCancellationRequested();
        RunHost.Control?.BeginStage("resolve");
        Console.Error.WriteLine("[circuitRF] resolve...");

        // ── plots ────────────────────────────────────────────────────────────

        var placedPages = new List<List<PlacedPlot>>();
        int plotCount   = 0;

        foreach (int ti in pages)
        {
            var tabPlots = tabs[ti].Plots;

            if (req.PlotIndex is { } n)
            {
                if (n > tabPlots.Count)
                    return JsonRun.Fail(CliDiagnostics.RenderNoSuchPlot(n, tabPlots.Count));
                tabPlots = [tabPlots[n - 1]];
            }

            var placed = new List<PlacedPlot>(tabPlots.Count);
            foreach (var pc in tabPlots)
            {
                var plot = PlotConfigLoader.LoadPlot(pc, sources!);
                PlotConfigLoader.ResolveDerived(plot, sources!);
                placed.Add(Place(plot, pc, sources!));
                plotCount++;
            }
            placedPages.Add(placed);
        }

        return Emit(path, reportKind ?? DocumentKinds.Name(DocumentKind.DataDisplay), req, placedPages,
                    new RenderDataDisplayJson(
                        tabs[tabIndex].Name, tabIndex, tabs.Count, pages.Count, plotCount,
                        sources!.Report()),
                    $"  {plotCount} plot(s) on {pages.Count} page(s), {sources.Report().Count} data source(s)");
    }

    /// <summary>
    /// The page, the theme, the encoder, the write and the report — <b>every picture this program
    /// composes out of <c>Plot</c>s goes through here</b>.
    /// </summary>
    /// <remarks>
    /// <b>Shared rather than copied, for <see cref="VectorPage"/>'s own reason.</b> A `.cdd`, a
    /// generated `plot` document and a `.csmith` chart are three ways of ARRIVING at a set of
    /// <see cref="PlacedPlot"/>s; from that point on there is exactly one set of decisions — the
    /// application's own Letter landscape, the export settings <c>AppSettings.Current</c> carries,
    /// <see cref="PlotComposer"/>, <c>PlotDocumentWriter</c>'s three encoders, and a write that
    /// happens only once the bytes are complete. A second copy of them would differ in one field
    /// nobody chose, which is the difference that gets excluded from a gate rather than fixed.
    /// </remarks>
    /// <param name="inputPath">The document the picture is OF, for the report.</param>
    /// <param name="reportKind">What the report calls that document — <c>DocumentKinds.Name</c>'s
    /// spelling, never a claim about a file that is not one.</param>
    /// <param name="pages">One list of placed plots per PAGE. More than one page is PDF only, which
    /// the caller has already refused otherwise.</param>
    /// <param name="display">The data-display block of the report, or null for an input that is not
    /// one.</param>
    /// <param name="summaryLine">The second stdout line, after "Wrote …", or null for none.</param>
    internal static int Emit(
        string inputPath, string reportKind, Request req,
        IReadOnlyList<List<PlacedPlot>> pages,
        RenderDataDisplayJson? display,
        string? summaryLine)
    {
        RunHost.Cancellation.ThrowIfCancellationRequested();
        RunHost.Control?.BeginStage("draw");
        Console.Error.WriteLine("[circuitRF] draw...");

        // ── the page ─────────────────────────────────────────────────────────

        // R-rnd4-6: the default STAYS the application's own 792x612 pt landscape with 36 pt margins,
        // so an unadorned `render` of a `.cdd` produces the byte-identical file the GUI's Export
        // does. --size replaces it, and the margin scales with the page rather than staying 36 pt on
        // a 4000-pixel one, which would put a hairline border on a poster.
        var    letter = PagePlacement.Letter;
        float  pageW  = req.PageWidth  ?? letter.Width;
        float  pageH  = req.PageHeight ?? letter.Height;
        float  margin = req.PageWidth is null && req.PageHeight is null
                      ? letter.Margin
                      : letter.Margin * Math.Min(pageW / letter.Width, pageH / letter.Height);
        var    page   = new PagePlacement(pageW, pageH, margin);

        var theme    = req.Dark ? RenderTheme.Dark : RenderTheme.Light;
        // The application's own export settings, with --background allowed to override one of them.
        //
        // Taking them from AppSettings.Current rather than inventing a set is what makes an
        // unadorned `render` of a `.cdd` byte-identical to the GUI's Export (§5.1) — the exported
        // background is TRANSPARENT by default there, and a run that quietly made it opaque would
        // have differed in exactly one rect and nowhere else, which is the sort of difference that
        // gets excluded from a gate rather than fixed.
        //
        // It is also reproducible: AppSettings has no disk persistence (its own Load() returns
        // in-memory defaults), so Current is the shipped values in this process and in the window's.
        // If that ever changes, this is where a headless run would start depending on a preference
        // file, and it must not.
        var current  = AppSettings.Current;
        var settings = new AppSettings
        {
            ExportTheme                    = current.ExportTheme,
            ExportTransparentBackground    = req.Transparent ?? current.ExportTransparentBackground,
            MarkerBoxTransparentBackground = current.MarkerBoxTransparentBackground,
            AlwaysDisplayDataSourcePrefix  = current.AlwaysDisplayDataSourcePrefix,
        };

        byte[] bytes = Encode(req, pages, theme, settings, page);

        // ── write and report ─────────────────────────────────────────────────

        RunHost.Cancellation.ThrowIfCancellationRequested();
        RunHost.Control?.BeginStage("encode");
        Console.Error.WriteLine("[circuitRF] encode...");

        if (bytes.Length == 0)
            return JsonRun.Fail(CliDiagnostics.RenderWriteFailed(req.Output, "the encoder produced no bytes"));

        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(req.Output));
            if (dir is { Length: > 0 }) Directory.CreateDirectory(dir);
            File.WriteAllBytes(req.Output, bytes);
        }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.RenderWriteFailed(req.Output, ex.Message)); }

        string unitKind = req.Format == "png" ? "device-pixels" : "points";
        int    outW     = req.Format == "png" ? (int)(pageW * req.Scale) : (int)pageW;
        int    outH     = req.Format == "png" ? (int)(pageH * req.Scale) : (int)pageH;

        JsonRun.AddOutput(req.Format, req.Output);
        JsonRun.Render = new RenderReportJson(
            inputPath, reportKind, View: null, req.Format,
            Viewport: null, Extents: null,
            new RenderSizeJson(outW, outH, unitKind,
                                              req.Format == "png" ? req.Scale : 1.0),
            new RenderThemeJson(
                req.Dark ? "Dark" : "Light", req.Dark ? "dark" : "light", "built-in"),
            Layers: null, Detail: null, Counters: null, bytes.Length,
            display);

        Console.WriteLine($"Wrote {req.Output} ({outW}x{outH} {unitKind}, {bytes.Length:N0} bytes)");
        if (summaryLine is { Length: > 0 }) Console.WriteLine(summaryLine);
        return 0;
    }

    // ── encoding ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The three formats, all through <see cref="PlotDocumentWriter"/> — the same functions the
    /// application's own Export and Copy write with (§5.1's gate). The PDF's metadata Title is taken
    /// from the OUTPUT path, which is what Export does, so the two files are identical field for
    /// field when written to the same name.
    /// </summary>
    private static byte[] Encode(
        Request req, IReadOnlyList<List<PlacedPlot>> pages,
        RenderTheme theme, AppSettings settings, PagePlacement page)
    {
        Action<SKCanvas> PageRender(List<PlacedPlot> plots)
            => canvas => PlotComposer.Render(canvas, plots, theme, settings, page);

        return req.Format switch
        {
            "pdf" when pages.Count > 1
                => PlotDocumentWriter.BuildPdfBytes(pages.Select(PageRender).ToList(), page),
            "pdf"
                => PlotDocumentWriter.BuildPdfBytes(PageRender(pages[0]), page,
                                                    PlotDocumentWriter.PdfTitleFor(req.Output)),
            "png"
                => PlotDocumentWriter.BuildPngBytes(PageRender(pages[0]), page, (float)req.Scale) ?? [],
            _
                => new UTF8Encoding(false).GetBytes(
                       PlotDocumentWriter.BuildSvgString(PageRender(pages[0]), page)),
        };
    }

    // ── placement ────────────────────────────────────────────────────────────

    /// <summary>
    /// One saved plot as the composition needs it. The application reads these six numbers off a
    /// live <c>PlotContainerViewModel</c>, which applies the canvas zoom and pan; there is no canvas
    /// here, so they are the LOGICAL coordinates — zoom 1, pan 0. That is not an approximation: the
    /// composition normalizes by its own bounding box, so zoom cancels out of every product
    /// (<see cref="PlotComposer"/>'s header says where).
    /// </summary>
    private static PlacedPlot Place(Plot plot, PlotContainerConfig pc, CddSources sources)
        => Place(plot, pc.Left, pc.Top, pc.Width, pc.Height, pc.FreqUnit,
                 sources.HasMultipleSources,
                 t => sources.AliasFor(t.EffectiveSourcePath ?? ""));

    /// <summary>
    /// The same placement over the six numbers themselves, for a plot that no
    /// <c>PlotContainerConfig</c> describes — <c>smith</c>'s chart, which is BUILT rather than loaded
    /// (brief-smith-10-cli-verb.md R-smith10-3).
    /// </summary>
    /// <param name="hasMultipleSources">Whether a trace label carries its source's name. False for a
    /// chart with no data-source library behind it, which is what the Smith window's own host is.</param>
    /// <param name="aliasFor">A trace's source alias, or null for "no aliases".</param>
    /// <param name="overlay">Transient chrome drawn above this plot's traces. <b>Without it an
    /// overlay the window draws on every frame is silently absent from the picture</b> —
    /// <see cref="PlacedPlot.Overlay"/>'s own remark.</param>
    internal static PlacedPlot Place(
        Plot plot, double left, double top, double width, double height, FreqUnit freqUnit,
        bool hasMultipleSources, Func<Trace, string?>? aliasFor,
        Action<SKCanvas, TransformSet, RenderTheme>? overlay = null)
    {
        double stripW = PlotCanvasGeometry.StripLogicalWidth(height);
        double topX   = PlotCanvasGeometry.TopLabelExtraLogical(plot, width);
        double botX   = PlotCanvasGeometry.BottomLabelExtraLogical(plot, width);

        // Which traces get a strip is PlotLabelStrips' rule, the same one the window's own
        // container follows (RND-4 R-rnd4-7: the strips are content and come out in an export).
        var (leftStrips, rightStrips) = PlotLabelStrips.For(plot, hasMultipleSources);

        var boxes = new List<PlacedMarkerBox>();
        foreach (var t in plot.Traces)
        foreach (var m in t.Markers)
        {
            if (!m.ShowInfoBox) continue;

            // A marker whose box was never dragged has no stored position — the window places one
            // 15/10 px from the glyph the first time it builds the box, and PERSISTS it. A `.cdd`
            // written by the application therefore always carries a finite position; this is the
            // same rule for one that does not (a hand-written file, or a marker added by a client),
            // through the same function, so the two agree.
            var pos = double.IsNaN(m.InfoBoxPos.X) || double.IsNaN(m.InfoBoxPos.Y)
                ? PlotCanvasGeometry.DefaultInfoBoxPosition(
                      m, t, plot, width, height + topX + botX, left, top, zoom: 1.0)
                : m.InfoBoxPos;
            double bx = pos.X;
            double by = pos.Y;
            var (w, h) = MarkerRenderer.MeasureInfoBox(m, t, freqUnit,
                                                       hasMultipleSources, plot.Traces);
            boxes.Add(new PlacedMarkerBox(m, t, freqUnit, bx, by, w, h, plot.Traces));
        }

        return new PlacedPlot
        {
            Plot                = plot,
            ViewLeft            = left,
            ViewTop             = top - topX,
            ViewWidth           = width,
            ViewHeight          = height + topX + botX,
            LogicalWidth        = width,
            LabelStripViewWidth = stripW,
            LeftLabelStrips     = leftStrips,
            RightLabelStrips    = rightStrips,
            MarkerBoxes         = boxes,
            ShowFilePrefix      = hasMultipleSources,
            AlwaysShowSource    = hasMultipleSources,
            AliasFor            = aliasFor,
            Overlay             = overlay,
        };
    }

    // ── tabs ─────────────────────────────────────────────────────────────────

    /// <summary>By name first, then by 1-based number — a tab literally called "2" wins over the
    /// second tab, because the name is what the user gave it.</summary>
    private static int IndexOfTab(IReadOnlyList<TabConfig> tabs, string asked)
    {
        for (int i = 0; i < tabs.Count; i++)
            if (string.Equals(tabs[i].Name, asked, StringComparison.OrdinalIgnoreCase)) return i;

        if (int.TryParse(asked, out int n) && n >= 1 && n <= tabs.Count) return n - 1;
        return -1;
    }

    private static string DescribeTabs(IReadOnlyList<TabConfig> tabs)
        => string.Join(", ", tabs.Select((t, i) => $"'{t.Name}' ({i + 1})"));
}
