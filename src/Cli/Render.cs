using System.Globalization;
using System.Text;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Symbol;
using CircuitRF.Design.Theming;
using CircuitRF.Design.Workspace;
using CircuitRF.Diagnostics;
using CircuitRF.Render;
using RfCore.Export;
using SkiaSharp;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf render &lt;path&gt; -o &lt;out.svg|.pdf|.png&gt;</c> — turn a schematic, a symbol or a
/// layout into a picture, headlessly (brief-render-2-render-verb.md).
///
/// <para><b>This file draws nothing and it must never start.</b> R-rnd0-1: there is one renderer, and
/// every pixel here comes out of <c>SchematicRenderer</c>, <c>SymbolEditorRenderer</c> and
/// <c>LayoutRenderer</c> in <c>CircuitRF.Render</c> — the same ~7,000 lines of measured Skia that draw
/// every frame the application shows and produce the SVG and PDF on its clipboard. What is here is
/// argument parsing, viewport arithmetic, refusals and reporting, which is what
/// <c>src/Cli/Authoring.cs</c> already established a CLI verb is allowed to be. A CLI that
/// re-implemented any of the drawing would drift, and the drift would be invisible: a picture that is
/// PLAUSIBLE is indistinguishable from a picture that is RIGHT.</para>
///
/// <para><b>One verb over every document kind</b> (R-rnd0-4/R-rnd2-1), with the kind inferred from the
/// path through <see cref="DocumentKinds.Classify"/> — the same function <c>check</c> and
/// <c>explain</c> infer with. There is no <c>render-schematic</c>.</para>
///
/// <para><b>An orphan document is a first-class input, not a degraded one.</b> A <c>.clay</c> with no
/// workspace above it resolves no technology, renders on the fallback palette exactly as the layout
/// editor does with an unresolved technology, and says so as a NOTE. A caller rendering a bare
/// <c>.clay</c> handed to it by a converter already knows there is no workspace; making that a warning
/// would train it to ignore warnings.</para>
///
/// <para><b>Every question the GUI would have asked in a dialog is a refusal naming the flag that
/// answers it</b> (R-rnd0-6) — a cell folder holding three views, an output extension this verb does
/// not write, a layer the technology does not define, a bare number where a layout coordinate belongs.
/// Nothing here defaults its way past a question the caller has not answered.</para>
/// </summary>
internal static class Render
{
    // ── the shapes of the argument surface ───────────────────────────────────

    private enum Format { Svg, Pdf, Png }

    /// <summary>Which of R-rnd2-3's three the caller asked for. They are refused TOGETHER rather than
    /// ordered, so exactly one is ever in force.</summary>
    private enum ViewportMode { Fit, Window, Center }

    private enum Detail { Full, Screen, Budget }

    /// <summary>Everything the run decided, gathered so the report and the picture cannot disagree
    /// about what was drawn.</summary>
    private sealed class Options
    {
        public string?      Path;
        public string?      Output;
        public Format?      Format;
        public ViewportMode Mode = ViewportMode.Fit;
        /// <summary>Whether <c>--fit</c> was actually TYPED. It is also the default, so "the mode is
        /// fit" cannot stand in for "the caller asked for fit" — without this the three-modes refusal
        /// would never fire, because the flag that lost the race sets the mode away from Fit.</summary>
        public bool         FitStated;
        public string?      WindowText;
        public string?      CenterText;
        public string?      SpanText;
        public double       Margin = DefaultMargin;
        public int          Width  = DefaultWidth;
        public int          Height = DefaultHeight;
        public double       Scale  = 1.0;
        public string?      ScaleOption;          // which spelling asked for it, for the refusal
        public ViewType?    View;
        public string?      Cell;
        public string[]?    OnlyLayers;
        public string[]?    HideLayers;
        /// <summary>R-aut12-2: what a FIT is framed on, when that is not what is drawn. An imported
        /// board's drill-map fabrication drawing sits far outside the board and shrinks it to a
        /// fraction of the frame; hiding it removes the content too, which is a different picture
        /// from the one that was wanted.</summary>
        public string[]?    FitLayers;
        /// <summary>R-aut12-1: <c>name=#rrggbb</c> entries, accumulated across repeats of the flag
        /// and across the commas inside one. A RENDER-time override — nothing is written to the
        /// technology, because changing a design to change a picture of it is not a fix.</summary>
        public List<string> LayerColors = new();
        public Detail       Detail = Detail.Full;
        public string       DetailText = "full";
        public double       DetailBudget;
        public string?      Theme;
        public ColorVariant Variant = ColorVariant.Light;
        public bool         Transparent;
        /// <summary>Whether <c>--background</c> was TYPED. A `.cdd` otherwise takes the
        /// application's own export default rather than this verb's, which is what makes an
        /// unadorned render byte-identical to the GUI's Export (R-rnd4-6 / §5.1).</summary>
        public bool         BackgroundStated;
        public bool         Grid;
        public bool         NoRulers;

        // ── .cdd only (RND-4) ────────────────────────────────────────────────
        /// <summary>Files bound with <c>--data</c>, in the order given. R-rnd4-4: the FIRST binds the
        /// document's `run.npy` sentinel, and every one of them can satisfy a path the document names
        /// but that does not resolve here.</summary>
        public List<string> Data = new();
        public string?      Tab;
        public int?         PlotIndex;
        public bool         AllTabs;
        /// <summary>Whether <c>--size</c> was TYPED. A `.cdd` defaults to the 792x612 pt page the
        /// application's own Export writes (R-rnd4-6) rather than this verb's 1600x1200, and without
        /// this flag those two defaults cannot be told apart.</summary>
        public bool         SizeStated;
    }

    /// <summary>R-rnd2-3's default page. Points for a vector format, device pixels for a raster one —
    /// 1600x1200 is a 4:3 page that reads at a glance in either.</summary>
    private const int DefaultWidth  = 1600;
    private const int DefaultHeight = 1200;

    /// <summary><c>LayoutViewport.ZoomToFit</c>'s own default, which is the margin the application's
    /// Zoom to Fit uses. Named in <see cref="DocumentExtents"/>, beside the fit arithmetic it belongs
    /// to, so <c>explain --extents</c> and this verb cannot frame a page differently.</summary>
    private const double DefaultMargin = DocumentExtents.DefaultMargin;

    // ── entry ────────────────────────────────────────────────────────────────

    public static int Run(string[] args)
    {
        var o = new Options();
        if (Parse(args, o) is { } bad) return bad;

        if (o.Path is null)   { JsonRun.Report(CliDiagnostics.RenderPathRequired());   return Usage(); }
        if (o.Output is null) { JsonRun.Report(CliDiagnostics.RenderOutputRequired()); return Usage(); }
        JsonRun.InputPath = o.Path;

        if (!File.Exists(o.Path) && !Directory.Exists(o.Path))
            return JsonRun.Fail(CliDiagnostics.RenderPathNotFound(o.Path));

        if (ResolveFormat(o) is { } formatRefusal) return formatRefusal;

        // The three viewport modes, refused together. Done before anything is read: a caller that
        // asked two incompatible questions gets the same answer whether or not the file parses.
        if (ViewportRefusal(o) is { } viewportRefusal) return viewportRefusal;

        try
        {
            return Draw(o);
        }
        catch (OperationCanceledException)
        {
            // §7 / gate 7: a cancelled run abandons its result rather than publishing a partial one.
            // Nothing is written because nothing is written until the bytes are complete — see Emit.
            JsonRun.Report(CliDiagnostics.RenderCancelled());
            return 130;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            "Usage: circuitrf render <path> -o <out.svg|.pdf|.png> [--view V] [--cell N]\n" +
            "                        [--fit | --window x0,y0,x1,y1 | --center x,y --span w]\n" +
            "                        [--margin f] [--size WxH] [--scale n | --dpi n]\n" +
            "                        [--layers a,b | --hide-layers a,b] [--fit-layers a,b]\n" +
            "                        [--layer-colors name=#rrggbb,...] [--detail full|screen|<px>]\n" +
            "                        [--theme name|file.ccolor] [--variant light|dark]\n" +
            "                        [--background opaque|transparent] [--grid] [--no-rulers]\n" +
            "  a .cdd adds:          [--data file]... [--tab name|n] [--plot n] [--all-tabs]");
        return 1;
    }

    // ── arguments ────────────────────────────────────────────────────────────

    private static int? Parse(string[] args, Options o)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "-o" or "--output" when i + 1 < args.Length: o.Output = args[++i]; continue;
                case "--format" when i + 1 < args.Length:
                    switch (args[++i].ToLowerInvariant())
                    {
                        case "svg": o.Format = Format.Svg; break;
                        case "pdf": o.Format = Format.Pdf; break;
                        case "png": o.Format = Format.Png; break;
                        default: return JsonRun.Fail(CliDiagnostics.RenderUnknownFormatName(args[i]));
                    }
                    continue;

                case "--view" when i + 1 < args.Length:
                    switch (args[++i].ToLowerInvariant())
                    {
                        case "schematic": o.View = ViewType.Schematic; break;
                        case "symbol":    o.View = ViewType.Symbol;    break;
                        case "layout":    o.View = ViewType.Layout;    break;
                        default: return JsonRun.Fail(CliDiagnostics.RenderNoSuchView("--view", args[i]));
                    }
                    continue;

                case "--cell" when i + 1 < args.Length: o.Cell = args[++i]; continue;

                case "--fit":    o.FitStated = true; continue;
                case "--window" when i + 1 < args.Length:
                    o.WindowText = args[++i]; o.Mode = ViewportMode.Window; continue;
                case "--center" when i + 1 < args.Length:
                    o.CenterText = args[++i]; o.Mode = ViewportMode.Center; continue;
                case "--span" when i + 1 < args.Length:
                    o.SpanText = args[++i]; o.Mode = ViewportMode.Center; continue;

                case "--margin" when i + 1 < args.Length:
                    if (!double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out double m)
                        || m < 0 || m > 0.45)
                        return JsonRun.Fail(CliDiagnostics.RenderMarginMalformed(args[i]));
                    o.Margin = m;
                    continue;

                case "--size" when i + 1 < args.Length:
                {
                    var parts = args[++i].Split('x', 'X');
                    if (parts.Length != 2
                        || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int w)
                        || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h)
                        || w < 8 || h < 8 || w > 20000 || h > 20000)
                        return JsonRun.Fail(CliDiagnostics.RenderSizeMalformed(args[i]));
                    o.Width = w; o.Height = h; o.SizeStated = true;
                    continue;
                }

                case "--scale" when i + 1 < args.Length:
                {
                    if (o.ScaleOption is not null) return JsonRun.Fail(CliDiagnostics.RenderScaleAndDpi());
                    if (!double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out double s)
                        || !(s > 0) || s > 16)
                        return JsonRun.Fail(CliDiagnostics.RenderScaleMalformed("--scale", args[i]));
                    o.Scale = s; o.ScaleOption = "--scale";
                    continue;
                }

                case "--dpi" when i + 1 < args.Length:
                {
                    if (o.ScaleOption is not null) return JsonRun.Fail(CliDiagnostics.RenderScaleAndDpi());
                    if (!double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                        || !(d > 0) || d > 1536)
                        return JsonRun.Fail(CliDiagnostics.RenderScaleMalformed("--dpi", args[i]));
                    // A spelling, not a second mechanism: 96 dpi is 1x, which is what every desktop
                    // toolkit means by an unscaled pixel.
                    o.Scale = d / 96.0; o.ScaleOption = "--dpi";
                    continue;
                }

                case "--layers" when i + 1 < args.Length:      o.OnlyLayers = SplitList(args[++i]); continue;
                case "--hide-layers" when i + 1 < args.Length: o.HideLayers = SplitList(args[++i]); continue;
                case "--fit-layers" when i + 1 < args.Length:  o.FitLayers  = SplitList(args[++i]); continue;
                // Repeatable AND comma-separated, because both spellings arrive: a shell writes one
                // quoted list, and `serve` emits the flag once per entry of a JSON object so that a
                // layer name carrying a comma survives the trip.
                case "--layer-colors" or "--layer-colours" when i + 1 < args.Length:
                    o.LayerColors.AddRange(SplitList(args[++i])); continue;

                case "--detail" when i + 1 < args.Length:
                    o.DetailText = args[++i];
                    switch (o.DetailText.ToLowerInvariant())
                    {
                        case "full":   o.Detail = Detail.Full;   break;
                        case "screen": o.Detail = Detail.Screen; break;
                        default:
                            if (!double.TryParse(o.DetailText, NumberStyles.Float, CultureInfo.InvariantCulture,
                                                 out double px) || !(px > 0) || px > 4096)
                                return JsonRun.Fail(CliDiagnostics.RenderDetailMalformed(o.DetailText));
                            o.Detail = Detail.Budget; o.DetailBudget = px;
                            break;
                    }
                    continue;

                case "--theme" when i + 1 < args.Length: o.Theme = args[++i]; continue;
                case "--variant" when i + 1 < args.Length:
                    switch (args[++i].ToLowerInvariant())
                    {
                        case "light": o.Variant = ColorVariant.Light; break;
                        case "dark":  o.Variant = ColorVariant.Dark;  break;
                        default: return JsonRun.Fail(CliDiagnostics.RenderUnknownVariant(args[i]));
                    }
                    continue;
                case "--background" when i + 1 < args.Length:
                    switch (args[++i].ToLowerInvariant())
                    {
                        case "opaque":      o.Transparent = false; break;
                        case "transparent": o.Transparent = true;  break;
                        default: return JsonRun.Fail(CliDiagnostics.RenderUnknownBackground(args[i]));
                    }
                    o.BackgroundStated = true;
                    continue;
                case "--grid":      o.Grid = true;     continue;
                case "--no-rulers": o.NoRulers = true; continue;

                // ── .cdd only (RND-4) ─────────────────────────────────────────
                case "--data" when i + 1 < args.Length: o.Data.Add(args[++i]); continue;
                case "--tab"  when i + 1 < args.Length: o.Tab = args[++i];     continue;
                case "--plot" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pi)
                        || pi < 1)
                        return JsonRun.Fail(CliDiagnostics.RenderPlotMalformed(args[i]));
                    o.PlotIndex = pi;
                    continue;
                case "--all-tabs": o.AllTabs = true; continue;

                default:
                    if (a.StartsWith('-'))
                    { JsonRun.Report(CliDiagnostics.RenderUnknownOption(a)); return Usage(); }
                    if (o.Path is not null)
                    { JsonRun.Report(CliDiagnostics.RenderMultiplePaths()); return Usage(); }
                    o.Path = a;
                    continue;
            }
        }
        return null;
    }

    private static string[] SplitList(string s)
        => [.. s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>R-rnd2-2: <c>-o</c>'s extension chooses the format, exactly as <c>convert</c> infers
    /// one from a path; <c>--format</c> overrides. An extension this verb does not write is a refusal
    /// listing the three, never a default.</summary>
    private static int? ResolveFormat(Options o)
    {
        if (o.Format is not null) return null;

        string ext = Path.GetExtension(o.Output!).ToLowerInvariant();
        o.Format = ext switch
        {
            ".svg" => Format.Svg,
            ".pdf" => Format.Pdf,
            ".png" => Format.Png,
            _      => null,
        };
        return o.Format is null
            ? JsonRun.Fail(CliDiagnostics.RenderUnknownOutputFormat(o.Output!, ext.Length == 0 ? "(none)" : ext))
            : null;
    }

    private static int? ViewportRefusal(Options o)
    {
        // R-rnd2-3: the three are refused TOGETHER rather than ordered. --fit is also the DEFAULT, so
        // it is counted only when it was actually typed — otherwise every invocation would look like
        // two modes.
        var stated = new List<string>();
        if (o.FitStated) stated.Add("--fit");
        if (o.WindowText is not null) stated.Add("--window");
        if (o.CenterText is not null || o.SpanText is not null) stated.Add("--center/--span");
        if (stated.Count > 1) return JsonRun.Fail(CliDiagnostics.RenderViewportModes(string.Join(" and ", stated)));

        if (o.Mode == ViewportMode.Center && (o.CenterText is null || o.SpanText is null))
            return JsonRun.Fail(CliDiagnostics.RenderSpanRequired());

        if (o.ScaleOption is not null && o.Format is not Format.Png)
            return JsonRun.Fail(CliDiagnostics.RenderScaleOnVector(
                o.ScaleOption, o.Format == Format.Svg ? "svg" : "pdf"));

        if (o.OnlyLayers is not null && o.HideLayers is not null)
            return JsonRun.Fail(CliDiagnostics.RenderLayersConflict());

        // R-aut12-2. --fit-layers decides what a FIT frames on, so it has nothing to say about a
        // window that was stated outright. Refused rather than ignored, on R-rnd0-6's rule: a flag
        // that silently did nothing produces a picture the caller believes it narrowed.
        if (o.FitLayers is not null && o.Mode != ViewportMode.Fit)
            return JsonRun.Fail(CliDiagnostics.RenderFitLayersNotFitting(
                o.Mode == ViewportMode.Window ? "--window" : "--center/--span"));

        return null;
    }

    // ── which document ───────────────────────────────────────────────────────

    /// <summary>The one document this run draws, and how it was arrived at.</summary>
    private readonly record struct Target(string File, ViewType View, string? ViewName);

    /// <summary>
    /// R-rnd2-1's three inputs: a view FILE, a cell FOLDER (the view chosen by <c>--view</c>, or the
    /// sole one it holds), and a WORKSPACE with <c>--cell</c>. Primacy inside a view sub-folder is
    /// <c>CellFolder.ResolvePrimary</c>'s answer and no other.
    /// </summary>
    private static (Target? Target, int? Refusal) ResolveTarget(Options o)
    {
        string path = o.Path!;
        var kind = DocumentKinds.Classify(path);

        switch (kind)
        {
            case DocumentKind.Schematic: return (new Target(path, ViewType.Schematic, null), null);
            case DocumentKind.Symbol:    return (new Target(path, ViewType.Symbol,    null), null);
            case DocumentKind.Layout:    return (new Target(path, ViewType.Layout,    null), null);

            case DocumentKind.Cell:
                return ResolveCellFolder(path, o);

            case DocumentKind.Workspace:
            {
                string root = Directory.Exists(path)
                    ? Path.GetFullPath(path)
                    : Path.GetDirectoryName(Path.GetFullPath(path))!;
                if (o.Cell is null)
                    return (null, JsonRun.Fail(CliDiagnostics.RenderCellRequired(path)));

                var found = CellLookup.Find(root, o.Cell);
                if (found.Count == 0)
                    return (null, JsonRun.Fail(CliDiagnostics.RenderNoSuchCell(
                        path, o.Cell, Join(CellLookup.Names(root)))));
                if (found.Count > 1)
                    return (null, JsonRun.Fail(CliDiagnostics.RenderAmbiguousCell(o.Cell, Join(found))));
                return ResolveCellFolder(found[0], o);
            }

            case DocumentKind.Interchange:
                return (null, JsonRun.Fail(CliDiagnostics.RenderNotDrawable(
                    path, DocumentKinds.InterchangeFormat(path) ?? "interchange")));

            default:
                return (null, JsonRun.Fail(CliDiagnostics.RenderNotDrawable(path, DocumentKinds.Name(kind))));
        }
    }

    private static (Target? Target, int? Refusal) ResolveCellFolder(string cellDir, Options o)
    {
        var held = new List<ViewType>();
        foreach (var v in DocumentKinds.AllViewTypes)
            if (CellFolder.ResolvePrimary(cellDir, v).State != PrimaryState.NoView) held.Add(v);

        if (held.Count == 0)
            return (null, JsonRun.Fail(CliDiagnostics.RenderNothingToDraw(cellDir)));

        ViewType view;
        if (o.View is { } asked)
        {
            if (!held.Contains(asked))
                return (null, JsonRun.Fail(CliDiagnostics.RenderNoSuchView(cellDir, asked.ToString().ToLowerInvariant())));
            view = asked;
        }
        else if (held.Count > 1)
        {
            // R-rnd0-6: the dialog's own question, so it is a refusal that LISTS them and names --view.
            return (null, JsonRun.Fail(CliDiagnostics.RenderViewRequired(
                cellDir, Join([.. held.Select(v => CellFolder.SubFolderName(v))]))));
        }
        else view = held[0];

        var primary = CellFolder.ResolvePrimary(cellDir, view);
        if (primary.ResolvedName is not { Length: > 0 } name)
            return (null, JsonRun.Fail(CliDiagnostics.RenderNoPrimary(
                cellDir, CellFolder.SubFolderName(view), primary.State.ToString())));

        return (new Target(Path.Combine(CellFolder.SubFolderPath(cellDir, view), name), view, name), null);
    }

    private static string Join(IReadOnlyList<string> items)
        => items.Count == 0 ? "(none)" : string.Join(", ", items);

    // ── the run ──────────────────────────────────────────────────────────────

    private static int Draw(Options o)
    {
        var control = RunHost.Control;
        control?.BeginStage("resolve");
        Progress("resolve");

        // A `.cdd` is a different document with a different anatomy — it holds no geometry, it names
        // its data, and it lays out several plots on a page rather than framing one drawing in a
        // viewport. It is still THIS verb (R-rnd0-4), inferred through the same classifier, but it
        // branches before ResolveTarget, which is about a cell's views.
        if (File.Exists(o.Path!) && DocumentKinds.Classify(o.Path!) == DocumentKind.DataDisplay)
        {
            if (CddInapplicable(o) is { } inapplicable)
                return JsonRun.Fail(CliDiagnostics.RenderCddViewportUnsupported(inapplicable));

            // --theme is its own refusal because its reason is a different one: a display's colours
            // are not a `.ccolor` at all. See CddInapplicable for why an ignored flag is refused
            // rather than dropped.
            if (o.Theme is { } themeName)
                return JsonRun.Fail(CliDiagnostics.RenderCddThemeUnsupported(themeName));

            return RenderDataDisplay.Draw(o.Path!, new RenderDataDisplay.Request(
                o.Output!, o.Format == Format.Pdf ? "pdf" : o.Format == Format.Png ? "png" : "svg",
                o.Data, o.Tab, o.PlotIndex, o.AllTabs,
                o.SizeStated ? o.Width  : null,
                o.SizeStated ? o.Height : null,
                o.Scale, o.BackgroundStated ? o.Transparent : null, o.Variant == ColorVariant.Dark));
        }

        var (target, refusal) = ResolveTarget(o);
        if (refusal is { } r) return r;
        var t = target!.Value;

        RunHost.Cancellation.ThrowIfCancellationRequested();

        return t.View switch
        {
            ViewType.Layout => DrawLayout(o, t),
            ViewType.Symbol => DrawSymbol(o, t),
            _               => DrawSchematic(o, t),
        };
    }

    /// <summary>
    /// The options that mean something for a DRAWING and nothing for a data display, named rather
    /// than ignored (R-rnd0-6). A caller that passed <c>--window</c> expecting a crop, or
    /// <c>--layers</c> expecting a filter, would otherwise get a full picture back and no hint that
    /// its flag did nothing — which is the same failure mode as a plausible-but-wrong picture.
    /// Returns the first one typed, or null.
    /// </summary>
    private static string? CddInapplicable(Options o)
    {
        if (o.WindowText is not null) return "--window";
        if (o.CenterText is not null) return "--center";
        if (o.SpanText   is not null) return "--span";
        if (o.FitStated)              return "--fit";
        if (LayerOptionNamed(o) is { } layerOption) return layerOption;
        if (o.View       is not null) return "--view";
        if (o.Cell       is not null) return "--cell";
        if (o.Detail != Detail.Full)  return "--detail";
        if (o.Grid)                   return "--grid";
        if (o.NoRulers)               return "--no-rulers";
        // The fit margin is part of the same arithmetic: a display's page is laid out to its plots'
        // own bounding box with a margin that scales with --size, and there is nothing here for a
        // fraction of an extent to be a fraction OF.
        if (o.Margin != DefaultMargin) return "--margin";
        return null;
    }

    /// <summary>The first of the four layer options the caller typed, or null. Said once so the
    /// three places that refuse them — a schematic, a symbol and a data display, none of which has
    /// drawing layers — cannot fall out of step with the set as it grows.</summary>
    private static string? LayerOptionNamed(Options o)
    {
        if (o.OnlyLayers      is not null) return "--layers";
        if (o.HideLayers      is not null) return "--hide-layers";
        if (o.FitLayers       is not null) return "--fit-layers";
        if (o.LayerColors.Count > 0)       return "--layer-colors";
        return null;
    }

    /// <summary>
    /// R-rnd2-10's stderr half. §3.1: stdout is the result, stderr is everything else — so the stage
    /// lines go here and cost the <c>--json</c> document nothing.
    /// </summary>
    private static void Progress(string stage) => Console.Error.WriteLine($"[circuitRF] {stage}...");

    // ── layout ───────────────────────────────────────────────────────────────

    private static int DrawLayout(Options o, Target t)
    {
        LayoutView view;
        try { view = LayoutPersistence.LoadFromFile(t.File); }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.RenderDocumentUnreadable(t.File, ex.Message)); }

        string full    = Path.GetFullPath(t.File);
        // The directory the `.clay` ITSELF lives in — a cell folder's `layout/` sub-folder — which is
        // what an instance's CellRef was written relative to and what the canvas passes as
        // LayoutRenderOptions.BaseDir. Named through CellHierarchy so this cannot drift from the
        // editor's answer; taking the CELL folder instead resolves every reference one level too
        // shallow and draws a placeholder where the sub-cell should be, with nothing reported.
        string baseDir = CellHierarchy.BaseDirOfDocument(full);
        var cache      = new TechnologyCache();
        var (resolved, _) = TechnologyResolver.ResolveForDocument(view.TechRef, full, null, cache);

        foreach (var d in resolved.Diagnostics)
            Note(CliDiagnostics.RenderResolverNote(t.File, d));

        // R-rnd2-1: a NOTE, never a warning and never a refusal.
        if (resolved.Source == TechResolutionSource.None)
            Note(CliDiagnostics.RenderNoTechnology(t.File));

        var tech = resolved.Tech;

        // R-rnd2-8: the selection is applied to a CLONE. TechnologyCache hands back a SHARED instance,
        // and flipping LayerDef.Visible on it would leak into the next render in the same process —
        // which is not hypothetical, because `serve` runs many calls in one. This is the class of
        // defect that only ever appears on the SECOND call.
        var plan = ApplyLayerSelection(tech, view, baseDir, o);
        if (plan.Refusal is { } lr) return lr;
        var drawTech   = plan.DrawTech;
        var layerReport = plan.Report;

        RunHost.Cancellation.ThrowIfCancellationRequested();
        RunHost.Control?.BeginStage("measure");
        Progress("measure");

        int pxW = (int)Math.Round(o.Width  * o.Scale);
        int pxH = (int)Math.Round(o.Height * o.Scale);

        // R-rnd3-9: ONE measurement, in CircuitRF.Render, called by this verb and by
        // `explain --extents`. Two boxes, not two functions — `extents` is the document's own
        // zoom-independent size (what the report says, and what `explain` answers with), `fitBox` adds
        // the room a Fixed-mode ruler's screen-point readout needs AT THIS PAGE, which is what a fit
        // has to be solved against. On a document with no Fixed ruler in it they are the same box.
        var extents = DocumentExtents.LayoutBox(view, drawTech, baseDir);
        if (extents.IsEmpty) return JsonRun.Fail(CliDiagnostics.RenderNothingToDraw(t.File));

        // R-aut12-2. A FIT frames what is DRAWN — `DocumentExtents.LayoutBox` gates on
        // `LayerDef.Visible`, which is what `--layers`/`--hide-layers` wrote on this same clone, so
        // hiding a layer removes it from the framing as well as from the picture. `--fit-layers`
        // narrows the framing FURTHER, without narrowing the picture: an imported board's drill map
        // sits far outside the board and shrinks it to a fraction of the frame, and hiding it is a
        // different picture from the one that was wanted.
        var fitBox = DocumentExtents.LayoutFitBox(view, plan.FitTech, baseDir, pxW, pxH, o.Margin);
        if (fitBox.IsEmpty)
            // Only reachable through --fit-layers: the box above came from the same measurement over
            // the DRAW technology and was already checked. The other arm is there so this cannot ever
            // report a flag the caller did not pass.
            return JsonRun.Fail(o.FitLayers is { } fl
                ? CliDiagnostics.RenderFitLayersEmpty(Join(fl))
                : CliDiagnostics.RenderNothingToDraw(t.File));

        Console.Error.WriteLine(
            $"[circuitRF] {view.Shapes.Count} shape(s), {view.Instances.Count} instance placement(s), " +
            $"{(drawTech?.Layers.Count ?? 0)} layer(s)");

        var (vp, letterboxed, vpRefusal) = LayoutViewportFor(o, view, fitBox, pxW, pxH);
        if (vpRefusal is { } vr) return vr;

        var (theme, themeName, themeFrom, themeRefusal) = ResolveTheme(o, full);
        if (themeRefusal is { } tr) return tr;

        var opts = LayoutOptionsFor(o, theme, baseDir);

        RunHost.Cancellation.ThrowIfCancellationRequested();
        RunHost.Control?.BeginStage("draw");
        Progress("draw");

        LayoutRenderResult result = default;
        byte[] bytes = Emit(o, pxW, pxH,
            canvas => result = LayoutRenderer.Draw(canvas, view, drawTech, vp, opts));

        foreach (var missing in result.MissingInstanceCellRefs ?? [])
            Note(CliDiagnostics.RenderResolverNote(t.File, $"instance reference '{missing}' did not resolve"));

        double metresPerDbu = 1e-6 / Math.Max(1, view.DbuPerMicron);
        double displayScale = MetresPerUnit(view.DisplayUnit);

        long tolerance = o.Detail switch
        {
            Detail.Budget => LayoutRenderDetail.ToleranceDbu(o.DetailBudget, vp.Zoom),
            Detail.Screen => LayoutRenderDetail.ToleranceDbu(LayoutRenderer.DefaultDetailPixelThreshold, vp.Zoom),
            _             => 0,
        };

        return Publish(o, t, DocumentKind.Layout, bytes,
            new RenderViewportJson(
                ModeName(o.Mode),
                vp.VisibleMinX * metresPerDbu, vp.VisibleMinY * metresPerDbu,
                vp.VisibleMaxX * metresPerDbu, vp.VisibleMaxY * metresPerDbu,
                "m", displayScale, vp.Zoom, letterboxed),
            new RenderExtentsJson(
                extents.MinX * metresPerDbu, extents.MinY * metresPerDbu,
                extents.MaxX * metresPerDbu, extents.MaxY * metresPerDbu,
                "m", displayScale),
            new RenderThemeJson(themeName, o.Variant == ColorVariant.Dark ? "dark" : "light", themeFrom),
            layerReport,
            new RenderDetailJson(o.DetailText.ToLowerInvariant(), tolerance > 0 ? tolerance : null),
            new RenderCountersJson(
                result.ShapesExamined, result.ShapesDrawn, result.VerticesEmitted,
                result.InstancesExamined, result.InstancesDrawn,
                result.PathsConstructed, result.DrawCalls, result.LayersVisited),
            pxW, pxH);
    }

    /// <summary>
    /// R-rnd2-6. <c>full</c> turns every level-of-detail tier off, so what is STORED is what is drawn;
    /// <c>screen</c> leaves every knob at 0, which is what the interactive canvas passes, so the tiers
    /// engage exactly as they would at this zoom; a budget sets the decimation tier and leaves the
    /// others at their measured defaults.
    ///
    /// <para><b><c>PathCache</c> is null on this path</b> — one-shot render, nothing to persist across
    /// frames — which is what every existing export already passes.</para>
    ///
    /// <para><b>The overlay is null and stays null.</b> That alone suppresses the ghost, the selection
    /// outlines, the handles and the marquee; the PCell pins, the EM mesh, the reference planes and the
    /// DRC markers all default to false and this verb never sets one. <b>Rulers are the exception and
    /// they stay ON</b>, because <c>ShowRulers</c>' own remarks say a ruler is document content rather
    /// than overlay state: it is in the <c>.clay</c> and an export that dropped it would contradict the
    /// layout design doc.</para>
    /// </summary>
    private static LayoutRenderOptions LayoutOptionsFor(Options o, ColorTheme theme, string baseDir)
    {
        var t = LayoutRenderTheme.FromTheme(theme, o.Variant);
        bool full = o.Detail == Detail.Full;

        return new LayoutRenderOptions
        {
            Theme                 = t,
            ShowGrid              = o.Grid,
            Overlay               = null,
            ShowRulers            = !o.NoRulers,
            TransparentBackground = o.Transparent,
            BaseDir               = baseDir,
            PathCache             = null,
            DetailPixelThreshold  = o.Detail switch
            {
                Detail.Full   => -1,
                Detail.Budget => o.DetailBudget,
                _             => 0,
            },
            // The other six tiers, each on its own documented "a NEGATIVE value disables the tier
            // outright" contract. DetailPixelThreshold < 0 already implies the outline and raster
            // tiers; the rest are set explicitly rather than relied upon, so `full` means what it says
            // even if one of those implications is ever narrowed.
            LodPixelThreshold            = full ? -1 : 0,
            MergeShapeCountThreshold     = full ? -1 : 0,
            OutlineVertexBudget          = full ? -1 : 0,
            InstanceRasterMaxDevicePixels = full ? -1 : 0,
            StrokeElisionPixelThreshold  = full ? -1 : 0,
            HairlineFillPixelThreshold   = full ? -1 : 0,
            CoarseCoverageThreshold      = full ? -1 : 0,
        };
    }

    private static (LayoutViewport Vp, bool Letterboxed, int? Refusal) LayoutViewportFor(
        Options o, LayoutView view, Bbox extents, int pxW, int pxH)
    {
        switch (o.Mode)
        {
            case ViewportMode.Window:
            {
                var (rect, refusal) = ParseWorldRect(o.WindowText!, "--window", view);
                if (refusal is { } f) return (default, false, f);
                return Letterbox(rect!.Value, pxW, pxH);
            }
            case ViewportMode.Center:
            {
                var (centre, cRefusal) = ParseWorldPoint(o.CenterText!, "--center", view);
                if (cRefusal is { } cf) return (default, false, cf);
                var (span, sRefusal) = ParseWorldLength(o.SpanText!, "--span", view);
                if (sRefusal is { } sf) return (default, false, sf);
                double halfW = span / 2.0;
                double halfH = halfW * pxH / Math.Max(1, pxW);
                return Letterbox(new WorldRect(centre.X - halfW, centre.Y - halfH,
                                               centre.X + halfW, centre.Y + halfH), pxW, pxH);
            }
            default:
                return (LayoutViewport.ZoomToFit(extents, pxW, pxH, marginFrac: o.Margin), false, null);
        }

        static (LayoutViewport, bool, int?) Letterbox(WorldRect w, int pxW, int pxH)
        {
            // R-rnd2-5: the requested window is honoured EXACTLY. Where its aspect differs from the
            // output's, the extra is filled — never cropped and never stretched. A caller that asked
            // for a region and silently got less of it than it asked for has no way to notice.
            double zoom = Math.Min(pxW / w.W, pxH / w.H);
            double cx = (w.X0 + w.X1) / 2.0, cy = (w.Y0 + w.Y1) / 2.0;
            var vp = new LayoutViewport(cx - pxW / (2 * zoom), cy - pxH / (2 * zoom), zoom, pxW, pxH);
            bool letterboxed = Math.Abs(w.W / w.H - (double)pxW / pxH) > 1e-9;
            return (vp, letterboxed, null);
        }
    }

    /// <summary>What the four layer flags decided. <paramref name="FitTech"/> differs from
    /// <paramref name="DrawTech"/> only when <c>--fit-layers</c> narrowed the framing.</summary>
    private readonly record struct LayerPlan(
        Technology? DrawTech, Technology? FitTech,
        IReadOnlyList<RenderLayerJson>? Report, int? Refusal);

    /// <summary>
    /// R-rnd2-8: the selection is applied to a CLONE of the resolved technology, never to the cached
    /// one, and the clone is a reflective field-for-field copy rather than a hand-written one — a
    /// hand-written copy silently drops any field added to <c>LayerDef</c> afterwards, and the symptom
    /// would be a layer that renders differently only when <c>--layers</c> is passed.
    ///
    /// <para>R-aut12-1 and R-aut12-2 join it: which layers DRAW, how they draw, and which of them a
    /// FIT is framed on are all decided here, and the first two go through the SAME clone in one pass
    /// — see <c>TechnologyLayerSelection.WithLayers</c> for why chaining two passes is subtly
    /// wrong.</para>
    /// </summary>
    private static LayerPlan ApplyLayerSelection(Technology? tech, LayoutView view, string baseDir, Options o)
    {
        // HIERARCHY INCLUDED, arrays multiplied (RND-3 R-rnd3-6) — and through CellHierarchy's own
        // walk, which is also what `explain --layers` counts with. Counting `view.Shapes` alone here
        // reported 1 where the picture drew 7, and a caller comparing the two verbs' answers for the
        // same layer would have had to choose which to believe.
        //
        // No visibility filter: this field is "shapes on that layer in the document, DRAWN OR NOT",
        // which is what makes an empty layer and an excluded one two different answers. Whether it was
        // painted is the `rendered` flag beside it.
        var counted = CellHierarchy.ShapeCountsByLayer(view, baseDir);
        var counts  = counted.Shapes;

        // ── the layers the technology does NOT define, which are drawn anyway ─────────────────────
        //
        // A key the document draws on that the resolved technology does not declare is an ordinary
        // and common state after an import (layout-view.md §2.4). `LayoutRenderer` resolves it
        // through FallbackPalette.For and paints it, because that definition's Visible is true — so
        // as far as the PICTURE is concerned the layer exists, and `explain --layers` lists it by
        // that same generated name. Leaving it out of this verb's own report and out of the
        // selection made all three of its answers wrong in the same direction:
        //
        //   * `render --json` reported a set of layers the drawing did not match, while
        //     `explain --layers` reported the right one — the two verbs this series pointed at ONE
        //     walk precisely so they could not disagree;
        //   * `--layers L99/0` was refused NAMING `explain --layers`, which is the verb that had
        //     just listed it;
        //   * `--layers "Top Copper"` drew Top Copper AND L99/0, because the clone had no LayerDef
        //     for the second to turn off. A caller that asked for one layer and silently got two has
        //     no way to notice, which is R-rnd2-7's own failure mode with the sign reversed.
        //
        // The definitions handed over are the palette's own, so with nothing selected the drawing is
        // byte-for-byte what it was.
        HashSet<LayerKey> defined = tech is null ? [] : [.. tech.Layers.Select(l => l.Key)];
        List<LayerDef> generated = tech is null
            ? []
            : [.. counts.Keys.Where(k => !defined.Contains(k))
                             .OrderBy(k => k.Layer).ThenBy(k => k.Datatype)
                             .Select(FallbackPalette.For)];

        string[]? named = o.OnlyLayers ?? o.HideLayers;
        bool anything = named is not null || o.FitLayers is not null || o.LayerColors.Count > 0;
        if (!anything)
            return new LayerPlan(tech, tech, LayerReport(tech, generated, counts, static l => l.Visible, null), null);

        if (tech is null)
            return new LayerPlan(null, null, null, JsonRun.Fail(CliDiagnostics.RenderNoTechnologyForLayers()));

        var known = new Dictionary<string, LayerKey>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in tech.Layers.Concat(generated))
        {
            known[l.Name] = l.Key;
            known[l.Key.ToString()] = l.Key;   // a caller that has only the numeric key from an import
        }

        // R-rnd2-7: not a silent skip. A misspelling that produced a picture without that layer is
        // indistinguishable from a layer that is genuinely empty — and the same is true of a layer
        // whose colour override or fit selection quietly missed.
        int? Unknown(string name) => JsonRun.Fail(CliDiagnostics.RenderUnknownLayer(
            name, Join([.. tech.Layers.Concat(generated).Select(l => l.Name)
                                      .Where(n => n.Length > 0).Distinct().Order(StringComparer.Ordinal)])));

        Func<LayerDef, bool>? visible = null;
        if (named is not null)
        {
            var chosen = new HashSet<LayerKey>();
            foreach (string name in named)
            {
                if (!known.TryGetValue(name, out var key)) return new LayerPlan(null, null, null, Unknown(name));
                chosen.Add(key);
            }
            bool only = o.OnlyLayers is not null;
            visible = l => only ? chosen.Contains(l.Key) : l.Visible && !chosen.Contains(l.Key);
        }

        var (colors, colorRefusal) = ParseLayerColors(o, known, Unknown);
        if (colorRefusal is { } cf) return new LayerPlan(null, null, null, cf);

        HashSet<LayerKey>? fitKeys = null;
        if (o.FitLayers is not null)
        {
            fitKeys = [];
            foreach (string name in o.FitLayers)
            {
                if (!known.TryGetValue(name, out var key)) return new LayerPlan(null, null, null, Unknown(name));
                fitKeys.Add(key);
            }
        }

        // R-rnd2-8's copy lives in src/Design beside Technology itself, not here: it is data
        // manipulation on the design model rather than a CLI concern, RND-3's `explain --layers` and
        // RND-4 both want it, and a second copy of it would be free to disagree about what was drawn.
        // ONE clone carries both the visibility and the colour, so the colour predicate reads the
        // technology's own definition rather than whatever a first pass left behind.
        var drawTech = TechnologyLayerSelection.WithLayers(
            tech, visible,
            colors.Count == 0 ? null : l => colors.TryGetValue(l.Key, out var a) ? a : null,
            generated);

        Technology fitTech = drawTech;
        if (fitKeys is not null)
            // Framed on these, drawn as before — and only where the layer is drawn at all, so
            // --fit-layers cannot resurrect something --hide-layers took out of the picture.
            fitTech = TechnologyLayerSelection.WithLayers(
                drawTech, l => l.Visible && fitKeys.Contains(l.Key), null);

        return new LayerPlan(drawTech, fitTech,
            LayerReport(drawTech, [], counts, static l => l.Visible,
                        fitKeys is null ? null : l => fitKeys.Contains(l.Key) && l.Visible),
            null);
    }

    /// <summary>
    /// R-aut12-1's <c>--layer-colors</c>, parsed against the same name map <c>--layers</c> resolves
    /// through — so a layer the technology does not define but the document draws on is nameable here
    /// under exactly the generated name <c>explain --layers</c> prints for it.
    ///
    /// <para><b>An eight-digit colour sets the layer's FILL OPACITY, not the colour's alpha.</b>
    /// <c>LayoutRenderer</c> builds its <c>SKColor</c> from R, G and B alone and takes the alpha from
    /// <see cref="LayerDef.FillOpacity"/>, at all four of its call sites. Writing the alpha into the
    /// colour and stopping there would be an override that parses, reports itself as applied, and
    /// changes nothing in the picture.</para>
    /// </summary>
    private static (Dictionary<LayerKey, TechnologyLayerSelection.LayerAppearance> Colors, int? Refusal)
        ParseLayerColors(Options o, Dictionary<string, LayerKey> known, Func<string, int?> unknown)
    {
        var colors = new Dictionary<LayerKey, TechnologyLayerSelection.LayerAppearance>();
        foreach (string entry in o.LayerColors)
        {
            int eq = entry.LastIndexOf('=');
            if (eq <= 0 || eq == entry.Length - 1)
                return (colors, JsonRun.Fail(CliDiagnostics.RenderLayerColorMalformed(entry)));

            string name = entry[..eq].Trim();
            string value = entry[(eq + 1)..].Trim();
            if (!known.TryGetValue(name, out var key)) return (colors, unknown(name));
            if (!Rgba.TryParseHex(value, out var rgba))
                return (colors, JsonRun.Fail(CliDiagnostics.RenderLayerColorBadValue(name, value)));

            colors[key] = new TechnologyLayerSelection.LayerAppearance(
                rgba, rgba.A == 255 ? null : rgba.A / 255.0);
        }
        return (colors, null);
    }

    /// <summary>
    /// One row per layer that would be reported by <c>explain --layers</c> for the same document —
    /// the technology's own table, then the generated definitions for the keys it does not declare.
    /// <paramref name="generated"/> is empty when the caller has already folded those into
    /// <paramref name="tech"/>.
    /// </summary>
    private static IReadOnlyList<RenderLayerJson>? LayerReport(
        Technology? tech, IReadOnlyList<LayerDef> generated,
        IReadOnlyDictionary<LayerKey, long> counts, Func<LayerDef, bool> rendered,
        Func<LayerDef, bool>? framed)
    {
        if (tech is null)
        {
            // No technology at all: every layer the document draws on is a fallback-palette layer, and
            // reporting an empty list would read as "this document uses none". The colour is the
            // palette's own, which is what the picture was actually drawn in.
            return [.. counts.OrderBy(kv => kv.Key.ToString(), StringComparer.Ordinal)
                             .Select(kv => Row(FallbackPalette.For(kv.Key), kv.Value))];
        }
        return [.. tech.Layers.Concat(generated).Select(l =>
            Row(l, counts.TryGetValue(l.Key, out long n) ? n : 0))];

        // The palette's own definitions are what the renderer paints an undeclared key with, so
        // `rendered` reads the same flag on both paths and neither needs a special case.
        RenderLayerJson Row(LayerDef l, long shapes) => new(
            l.Name.Length > 0 ? l.Name : l.Key.ToString(),
            rendered(l),
            shapes,
            l.Color.ToHex(),
            l.FillOpacity,
            framed?.Invoke(l));
    }

    // ── schematic ────────────────────────────────────────────────────────────

    private static int DrawSchematic(Options o, Target t)
    {
        if (LayerOptionNamed(o) is { } layerOption)
            return JsonRun.Fail(CliDiagnostics.RenderLayersNotApplicable(layerOption, "schematic"));
        if (o.Detail != Detail.Full && o.DetailText != "full")
            return JsonRun.Fail(CliDiagnostics.RenderDetailNotApplicable("schematic"));

        SchematicEditModel model;
        try { (model, _, _) = SchematicPersistence.LoadFromFile(t.File); }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.RenderDocumentUnreadable(t.File, ex.Message)); }

        RunHost.Cancellation.ThrowIfCancellationRequested();
        RunHost.Control?.BeginStage("measure");
        Progress("measure");

        var (rm, idx) = model.BuildRenderModel();
        var extents = DocumentExtents.SchematicBox(model, rm);
        if (extents is not { } bb) return JsonRun.Fail(CliDiagnostics.RenderNothingToDraw(t.File));

        Console.Error.WriteLine(
            $"[circuitRF] {model.Components.Count} component(s), {model.Wires.Count} wire(s)");

        int pxW = (int)Math.Round(o.Width  * o.Scale);
        int pxH = (int)Math.Round(o.Height * o.Scale);

        var (pan, zoom, letterboxed, refusal) = ScreenSenseViewport(o, bb, pxW, pxH, null);
        if (refusal is { } f) return f;

        var (theme, themeName, themeFrom, themeRefusal) = ResolveTheme(o, Path.GetFullPath(t.File));
        if (themeRefusal is { } tr) return tr;
        var rt = SchematicRenderTheme.FromTheme(theme, o.Variant);

        RunHost.Cancellation.ThrowIfCancellationRequested();
        RunHost.Control?.BeginStage("draw");
        Progress("draw");

        byte[] bytes = Emit(o, pxW, pxH, canvas =>
            SchematicRenderer.Draw(canvas, (pxW, pxH), rm, idx, pan.X, pan.Y, zoom, rt,
                overlay: null, useTransparentBackground: o.Transparent, excludeGrid: !o.Grid));

        return Publish(o, t, DocumentKind.Schematic, bytes,
            ScreenSenseViewportJson(o, pan, zoom, pxW, pxH, letterboxed),
            new RenderExtentsJson(bb.X0, bb.Y0, bb.X1, bb.Y1, DesignUnits, 1.0),
            new RenderThemeJson(themeName, o.Variant == ColorVariant.Dark ? "dark" : "light", themeFrom),
            null, null, null, pxW, pxH);
    }

    // ── symbol ───────────────────────────────────────────────────────────────

    private static int DrawSymbol(Options o, Target t)
    {
        if (LayerOptionNamed(o) is { } layerOption)
            return JsonRun.Fail(CliDiagnostics.RenderLayersNotApplicable(layerOption, "symbol"));
        if (o.Detail != Detail.Full && o.DetailText != "full")
            return JsonRun.Fail(CliDiagnostics.RenderDetailNotApplicable("symbol"));

        Symbol symbol;
        try { symbol = SymbolPersistence.LoadFromFile(t.File); }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.RenderDocumentUnreadable(t.File, ex.Message)); }

        if (symbol.Primitives.Count == 0 && symbol.Pins.Count == 0)
            return JsonRun.Fail(CliDiagnostics.RenderNothingToDraw(t.File));

        RunHost.Cancellation.ThrowIfCancellationRequested();
        RunHost.Control?.BeginStage("measure");
        Progress("measure");

        int pxW = (int)Math.Round(o.Width  * o.Scale);
        int pxH = (int)Math.Round(o.Height * o.Scale);

        // TWO boxes, and the difference is R-rnd3-10's whole point: `body` is the GEOMETRY — the
        // primitives and the pin anchors, zoom-independent, and what the report says the document's
        // size is. `fitBox` is that SOLVED for the pin NAMES, which are drawn in pixels at a size with
        // a floor and therefore have no world extent until a zoom is chosen. A fit is framed on the
        // second; a zoom-independent answer is the first.
        var bodyBb = DocumentExtents.SymbolBox(symbol);
        if (bodyBb is not { } body) return JsonRun.Fail(CliDiagnostics.RenderNothingToDraw(t.File));

        var fitBox = DocumentExtents.SymbolFitBox(symbol, body, pxW, pxH, o.Margin);

        Console.Error.WriteLine(
            $"[circuitRF] {symbol.Primitives.Count} primitive(s), {symbol.Pins.Count} pin(s)");

        var (pan, zoom, letterboxed, refusal) = ScreenSenseViewport(o, fitBox, pxW, pxH, null);
        if (refusal is { } f) return f;

        var (theme, themeName, themeFrom, themeRefusal) = ResolveTheme(o, Path.GetFullPath(t.File));
        if (themeRefusal is { } tr) return tr;
        var rt = SchematicRenderTheme.FromTheme(theme, o.Variant);

        RunHost.Cancellation.ThrowIfCancellationRequested();
        RunHost.Control?.BeginStage("draw");
        Progress("draw");

        byte[] bytes = Emit(o, pxW, pxH, canvas =>
        {
            canvas.Clear(o.Transparent ? SKColors.Transparent : rt.Background);
            SchematicRenderer.DrawSymbol(
                canvas, symbol.Primitives, compX: 0, compY: 0,
                rotation: SymbolRotation.R0, mirrorX: false,
                panX: pan.X, panY: pan.Y, zoom: zoom, theme: rt);
            // The same dot-and-port-label pass the editor and the symbol clipboard export both use, so
            // a symbol rendered here and the same symbol opened afterwards look alike.
            SymbolEditorRenderer.DrawPinMarkersPlain(canvas, symbol.Pins, pan.X, pan.Y, zoom, rt);
        });

        return Publish(o, t, DocumentKind.Symbol, bytes,
            ScreenSenseViewportJson(o, pan, zoom, pxW, pxH, letterboxed),
            new RenderExtentsJson(body.X0, body.Y0, body.X1, body.Y1, DesignUnits, 1.0),
            new RenderThemeJson(themeName, o.Variant == ColorVariant.Dark ? "dark" : "light", themeFrom),
            null, null, null, pxW, pxH);
    }

    // ── the schematic/symbol viewport, which is Y-DOWN ───────────────────────

    /// <summary>The spelling the schematic and symbol halves report as their coordinate space
    /// (R-rnd2-4): dimensionless design units, reported as what they are rather than dressed up in
    /// metres.</summary>
    private const string DesignUnits = "design-units";

    /// <summary>
    /// The schematic and symbol canvases are SCREEN-SENSE (+y down), unlike the layout's physical
    /// Y-up, so their viewport is a pan/zoom pair rather than a <c>LayoutViewport</c>. The letterbox
    /// rule is the same one (R-rnd2-5): the requested window is honoured exactly and the extra is
    /// filled.
    /// </summary>
    private static ((double X, double Y) Pan, double Zoom, bool Letterboxed, int? Refusal)
        ScreenSenseViewport(Options o, WorldRect extents, int pxW, int pxH, LayoutView? _)
    {
        switch (o.Mode)
        {
            case ViewportMode.Window:
            {
                var (rect, refusal) = ParseWorldRect(o.WindowText!, "--window", null);
                if (refusal is { } f) return (default, 0, false, f);
                return Box(rect!.Value);
            }
            case ViewportMode.Center:
            {
                var (centre, cRefusal) = ParseWorldPoint(o.CenterText!, "--center", null);
                if (cRefusal is { } cf) return (default, 0, false, cf);
                var (span, sRefusal) = ParseWorldLength(o.SpanText!, "--span", null);
                if (sRefusal is { } sf) return (default, 0, false, sf);
                double halfW = span / 2.0;
                double halfH = halfW * pxH / Math.Max(1, pxW);
                return Box(new WorldRect(centre.X - halfW, centre.Y - halfH,
                                         centre.X + halfW, centre.Y + halfH));
            }
            default:
            {
                double zoom = DocumentExtents.FitZoom(extents.W, extents.H, pxW, pxH, o.Margin);
                double panX = (extents.X0 + extents.X1) * 0.5 - pxW / (2.0 * zoom);
                double panY = (extents.Y0 + extents.Y1) * 0.5 - pxH / (2.0 * zoom);
                return ((panX, panY), zoom, false, null);
            }
        }

        ((double, double), double, bool, int?) Box(WorldRect w)
        {
            double zoom = Math.Min(pxW / w.W, pxH / w.H);
            double cx = (w.X0 + w.X1) / 2.0, cy = (w.Y0 + w.Y1) / 2.0;
            bool letterboxed = Math.Abs(w.W / w.H - (double)pxW / pxH) > 1e-9;
            return ((cx - pxW / (2 * zoom), cy - pxH / (2 * zoom)), zoom, letterboxed, null);
        }
    }

    private static RenderViewportJson ScreenSenseViewportJson(
        Options o, (double X, double Y) pan, double zoom, int pxW, int pxH, bool letterboxed)
        => new(ModeName(o.Mode),
               pan.X, pan.Y, pan.X + pxW / zoom, pan.Y + pxH / zoom,
               DesignUnits, 1.0, zoom, letterboxed);

    private static string ModeName(ViewportMode m) => m switch
    {
        ViewportMode.Window => "window",
        ViewportMode.Center => "center",
        _                   => "fit",
    };

    // ── coordinates, and the unit rule ───────────────────────────────────────

    /// <summary>
    /// R-rnd2-4 / R-rnd0-5. <b>On a layout, every coordinate carries an SI unit and a bare number is a
    /// refusal.</b> <c>--window 0,0,500,300</c> could mean DBU, micrometres or millimetres; those are
    /// three pictures six orders of magnitude apart and all three are plausible, and the picture that
    /// comes back from the wrong one is a plausible picture of the wrong thing. This is
    /// <c>sweep-unit-scale-and-mark</c>'s failure class exactly.
    ///
    /// <para>A schematic or a symbol takes bare numbers, because its coordinates ARE dimensionless
    /// design units — a unit suffix there would be the invention.</para>
    /// </summary>
    private static (double Value, int? Refusal) ParseCoordinate(string text, string option, LayoutView? view)
    {
        if (view is null)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                ? (v, null)
                : (0, JsonRun.Fail(CliDiagnostics.RenderCoordinateMalformed(option, text)));
        }

        string trimmed = text.Trim();
        bool hasUnit = trimmed.Length > 0 && char.IsLetter(trimmed[^1]);
        if (!hasUnit)
            return (0, JsonRun.Fail(CliDiagnostics.RenderCoordinateNeedsUnit(
                option, text, SuggestUnits(trimmed, view))));

        return LayoutUnits.TryParse(trimmed, LayoutUnit.Um, view.DbuPerMicron, out long dbu)
            ? (dbu, null)
            : (0, JsonRun.Fail(CliDiagnostics.RenderCoordinateMalformed(option, text)));
    }

    /// <summary>What the refusal offers instead — the same number spelled in the two units a caller
    /// most plausibly meant, plus the document's own display unit when it is neither.</summary>
    private static string SuggestUnits(string bare, LayoutView view)
    {
        var units = new List<LayoutUnit> { LayoutUnit.Um, LayoutUnit.Mm };
        if (!units.Contains(view.DisplayUnit)) units.Add(view.DisplayUnit);
        // LayoutUnits' own ASCII table — the same one `explain --extents` spells its `--window`
        // string with, so this verb cannot offer a suffix that verb does not emit, or the reverse.
        return string.Join(" or ", units.Select(u => $"'{bare}{LayoutUnits.AsciiSuffix(u)}'"));
    }

    private static (WorldRect? Rect, int? Refusal) ParseWorldRect(string text, string option, LayoutView? view)
    {
        var parts = text.Split(',');
        if (parts.Length != 4) return (null, JsonRun.Fail(CliDiagnostics.RenderWindowMalformed(text)));

        var v = new double[4];
        for (int i = 0; i < 4; i++)
        {
            var (value, refusal) = ParseCoordinate(parts[i], option, view);
            if (refusal is { } f) return (null, f);
            v[i] = value;
        }

        var rect = new WorldRect(Math.Min(v[0], v[2]), Math.Min(v[1], v[3]),
                                 Math.Max(v[0], v[2]), Math.Max(v[1], v[3]));
        if (!(rect.W > 0) || !(rect.H > 0))
            return (null, JsonRun.Fail(CliDiagnostics.RenderWindowEmpty(text)));
        return (rect, null);
    }

    private static ((double X, double Y) Point, int? Refusal) ParseWorldPoint(
        string text, string option, LayoutView? view)
    {
        var parts = text.Split(',');
        if (parts.Length != 2) return (default, JsonRun.Fail(CliDiagnostics.RenderCenterMalformed(text)));

        var (x, xr) = ParseCoordinate(parts[0], option, view);
        if (xr is { } xf) return (default, xf);
        var (y, yr) = ParseCoordinate(parts[1], option, view);
        if (yr is { } yf) return (default, yf);
        return ((x, y), null);
    }

    private static (double Value, int? Refusal) ParseWorldLength(string text, string option, LayoutView? view)
    {
        var (v, refusal) = ParseCoordinate(text, option, view);
        if (refusal is { } f) return (0, f);
        return v > 0 ? (v, null) : (0, JsonRun.Fail(CliDiagnostics.RenderCoordinateMalformed(option, text)));
    }

    private static double MetresPerUnit(LayoutUnit u) => u switch
    {
        LayoutUnit.Nm   => 1e-9,
        LayoutUnit.Um   => 1e-6,
        LayoutUnit.Mm   => 1e-3,
        LayoutUnit.Mil  => 2.54e-5,
        _               => 2.54e-2,
    };

    // ── the theme ────────────────────────────────────────────────────────────

    /// <summary>
    /// R-rnd2-9: <c>ThemeResolver</c>'s existing chain and nothing new — an explicit file first, then,
    /// for a name, workspace directory, user themes directory, shipped <c>.ccolor</c>. With no
    /// <c>--theme</c>, the workspace's own recorded theme; with no workspace, the shipped default.
    ///
    /// <para><b>Resolvability is decided HERE, before the chain runs, and that is not a duplicate of
    /// it.</b> <c>ThemeResolver.Resolve</c> cannot fail: its last step is <c>ColorTheme.BuiltIn</c>,
    /// which always succeeds. So a misspelled name would resolve to a DIFFERENT PICTURE and be reported
    /// as a success — the same silent fallback R-rnd1-5 removed from the resolver itself. What this
    /// adds is the answer to "did a step actually match", which the resolver does not return.</para>
    /// </summary>
    private static (ColorTheme Theme, string Name, string From, int? Refusal) ResolveTheme(
        Options o, string documentPath)
    {
        string? cws = DocumentKinds.AncestorCws(documentPath);
        string? workspaceDir = cws is null ? null : Path.GetDirectoryName(Path.GetFullPath(cws));

        if (o.Theme is { } asked)
        {
            // A path is taken as a path — the ONE step of the chain that is not a name lookup.
            if (asked.EndsWith(".ccolor", StringComparison.OrdinalIgnoreCase) || File.Exists(asked))
            {
                if (!File.Exists(asked))
                    return (ColorTheme.BuiltIn, asked, "file",
                            JsonRun.Fail(CliDiagnostics.RenderThemeUnresolved(asked, $"'{asked}'")));
                try { return (ColorThemeIo.LoadFile(asked), Path.GetFileNameWithoutExtension(asked), "file", null); }
                catch (Exception ex)
                {
                    return (ColorTheme.BuiltIn, asked, "file",
                            JsonRun.Fail(CliDiagnostics.RenderThemeFileUnreadable(asked, ex.Message)));
                }
            }

            string? from = WhereItResolves(asked, workspaceDir);
            if (from is null)
                return (ColorTheme.BuiltIn, asked, "none", JsonRun.Fail(CliDiagnostics.RenderThemeUnresolved(
                    asked, LookedIn(workspaceDir))));
            return (ThemeResolver.Resolve(asked, workspaceDir), asked, from, null);
        }

        string name = ThemeResolver.DefaultThemeName;
        if (cws is not null)
        {
            try
            {
                if (WorkspacePersistence.LoadFromFile(cws).ColorSchemeName is { Length: > 0 } recorded)
                    name = recorded;
            }
            catch { /* a .cws that will not parse is `check`'s finding, not a reason to refuse a picture */ }
        }

        // A recorded name that no longer resolves is the workspace's own state, not the caller's
        // mistake, so it falls through the chain exactly as the application's does and says where it
        // landed rather than refusing.
        return (ThemeResolver.Resolve(name, workspaceDir), name,
                WhereItResolves(name, workspaceDir) ?? "built-in", null);
    }

    private static string? WhereItResolves(string name, string? workspaceDir)
    {
        if (workspaceDir is not null && File.Exists(Path.Combine(workspaceDir, name + ".ccolor")))
            return "workspace";
        if (File.Exists(Path.Combine(ThemeResolver.UserThemesDir, name + ".ccolor")))
            return "user";
        foreach (string builtIn in ThemeResolver.BuiltInThemeNames)
            if (string.Equals(builtIn, name, StringComparison.OrdinalIgnoreCase)) return "shipped";
        return null;
    }

    private static string LookedIn(string? workspaceDir)
    {
        var places = new List<string>();
        if (workspaceDir is not null) places.Add($"the workspace ({workspaceDir})");
        places.Add($"the user themes folder ({ThemeResolver.UserThemesDir})");
        places.Add($"the shipped themes ({string.Join(", ", ThemeResolver.BuiltInThemeNames)})");
        return string.Join(", ", places);
    }

    // ── encoding, writing and reporting ──────────────────────────────────────

    /// <summary>
    /// R-rnd2-2's three formats, each drawn ONCE by the caller's own delegate onto whichever surface
    /// the format needs — so there is one drawing path and three encoders rather than three renders.
    ///
    /// <para><b>The bytes are complete before anything reaches the filesystem.</b> That is what makes
    /// gate 7 true by construction: a cancelled run leaves no output file, because the file is not
    /// opened until the picture is finished.</para>
    /// </summary>
    private static byte[] Emit(Options o, int pxW, int pxH, Action<SKCanvas> draw) => o.Format switch
    {
        Format.Pdf => VectorPage.Pdf(pxW, pxH, draw),
        Format.Png => VectorPage.Png(pxW, pxH, draw),
        _          => VectorPage.Svg(pxW, pxH, draw),
    };

    private static int Publish(
        Options o, Target t, DocumentKind kind, byte[] bytes,
        RenderViewportJson viewport, RenderExtentsJson extents, RenderThemeJson theme,
        IReadOnlyList<RenderLayerJson>? layers, RenderDetailJson? detail, RenderCountersJson? counters,
        int pxW, int pxH)
    {
        RunHost.Cancellation.ThrowIfCancellationRequested();
        RunHost.Control?.BeginStage("encode");
        Progress("encode");

        if (bytes.Length == 0)
            return JsonRun.Fail(CliDiagnostics.RenderWriteFailed(o.Output!, "the encoder produced no bytes"));

        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(o.Output!));
            if (dir is { Length: > 0 }) Directory.CreateDirectory(dir);
            File.WriteAllBytes(o.Output!, bytes);
        }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.RenderWriteFailed(o.Output!, ex.Message)); }

        string format = o.Format switch { Format.Pdf => "pdf", Format.Png => "png", _ => "svg" };
        string unitKind = o.Format == Format.Png ? "device-pixels" : "points";

        JsonRun.AddOutput(format, o.Output!);
        JsonRun.Render = new RenderReportJson(
            t.File, DocumentKinds.Name(kind), t.ViewName, format,
            viewport, extents,
            new RenderSizeJson(pxW, pxH, unitKind, o.Scale),
            theme, layers, detail, counters, bytes.Length);

        // §3.1: the file written IS the result, so it goes to stdout — the same rule the authoring
        // verbs follow, and for the same reason: a caller's next step is to read or ship that file.
        Console.WriteLine($"Wrote {o.Output} ({pxW}x{pxH} {unitKind}, {bytes.Length:N0} bytes)");
        if (counters is { } c)
            Console.WriteLine(
                $"  {c.ShapesDrawn:N0} of {c.ShapesExamined:N0} shape(s) drawn, " +
                $"{c.VerticesEmitted:N0} vertices emitted, {c.DrawCalls:N0} draw call(s)");
        return 0;
    }

    private static void Note(Diagnostic d)
    {
        Console.Error.WriteLine("note: " + d.Render());
        JsonRun.Note(d);
    }
}
