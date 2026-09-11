using System.Globalization;
using System.Text.Json;
using CircuitRF.Render.DataDisplay;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf plot &lt;result&gt; -o out.{svg,pdf,png} --trace cube=…</c> — one picture out of one
/// result file, with no <c>.cdd</c> to hand-author first (brief-automation-11-missing-verbs.md
/// R-aut11-2).
///
/// <para><b>Why it exists.</b> Hand-authoring a data display to draw a single trace was the largest
/// piece of incidental work in an otherwise short task: a document with a tab, a plot container, a
/// placement, a source reference and a slice, every field of which has to be right before anything
/// appears. This verb takes the four things a caller actually has — a result, a cube, a matrix entry
/// and a format — and writes the picture.</para>
///
/// <para><b>It is the convenience over `.cdd` authoring, not a replacement for it.</b> Everything a
/// display can express stays reachable by writing one and calling <c>render</c>; what is here is one
/// plot with one axis pair.</para>
///
/// <para><b>There is ONE plotting path.</b> This file builds a <see cref="DataDisplayConfig"/> — the
/// document a `.cdd` deserializes to — and hands it to <see cref="RenderDataDisplay.Draw(string,
/// DataDisplayConfig, Request, string?)"/>, which is the same function <c>render</c>'s `.cdd` half
/// calls. So a plot drawn here is byte-identical to the same plot drawn from the equivalent
/// hand-authored display, and <c>--write-cdd</c> hands over that document so a caller can check it,
/// edit it, and go on with the full surface. A second composer here would have drifted invisibly: a
/// picture that is PLAUSIBLE is indistinguishable from one that is RIGHT.</para>
///
/// <para><b>The cube spec is the trace card's own.</b> <c>cube=</c> is parsed by
/// <see cref="CubeTraceSpecParser"/>, which is what the spec box on a trace card parses — so
/// <c>S[:,1,0]</c>, <c>Pout</c> and <c>mag(V[:,"X1.drain"])</c> mean here exactly what they mean
/// there, and an unparseable one is refused in the parser's own words. <c>i=</c>/<c>j=</c> are the
/// convenience over it: they pin the cube's axes NAMED <c>i</c> and <c>j</c> by PORT NUMBER, which is
/// what a caller holding an S-matrix has, and they are refused with a spec that already carries
/// brackets rather than silently losing to it.</para>
/// </summary>
internal static class PlotVerb
{
    /// <summary>One <c>--trace</c>, after parsing.</summary>
    private sealed class TraceSpec
    {
        public string  Text  = "";     // the cube spec as typed, before i/j and y are folded in
        public int?    I;
        public int?    J;
        public string? YText;
        public bool    Secondary;
        public string  Raw = "";       // the whole --trace argument, for refusals

        // WSProbe (WSP-4 R-wsp4-12). `probe=` is what turns a `cube=wsp` trace into a probe metric;
        // everything else here is the option some metrics read.
        // ANT-7 §3 — the two pattern spellings, both shorthand over the general slice mechanism.
        public string? Cut;      // a phi in degrees, or "all" for the whole pattern as a family
        public int?    Port;     // a 1-based PORT NUMBER on a `port` axis, never an index
        public double? FreqHz;   // pins the freq axis to its nearest sample

        public string? Probe;
        public string? With;
        public string? Set;
        public string? Metric;
        public string? Z0;
        public string? Side;
        public int?    Gi;

        // The Envelope sub-card (R-wsp4-9). Its four quantities are metrics like any other; what
        // they need beyond a metric name is the GRID — which probe each side is pulled at, the two
        // |Gamma| ladders and the angular step.
        public string? SrcProbe;
        public string? LoadProbe;
        public string? GammaS;
        public string? GammaL;
        public double? Theta;
        public string? Passive;
    }

    private sealed class Options
    {
        public string?         Result;
        public string?         Output;
        public string?         Format;
        public List<TraceSpec> Traces = new();
        public PlotType        Type = PlotType.Rect;
        public FreqUnit        FreqUnit = FreqUnit.GHz;
        public string?         Title;
        public string?         XLabel, YLabel, Y2Label;
        public (double Lo, double Hi)? X, Y, Y2;
        public int?            Width, Height;
        public double          Scale = 1.0;
        public string?         ScaleOption;
        public bool?           Transparent;
        public bool            Dark;
        public string?         WriteCdd;

        // ANT-7 §2 — the dB radial mode, which the Data Display and this verb gain together.
        public PolarRadialMode      Radial     = PolarRadialMode.Linear;
        public double               DbFloor    = -40.0;
        public double               DbRingStep = 10.0;
        public PolarDbReferenceMode DbRef      = PolarDbReferenceMode.Peak;
        public double               DbRefValue;
        public string               DbUnit     = "";
        public List<string>         DbOptions  = new();   // what was typed, for the refusals
    }

    /// <summary>
    /// The plot box, in the canvas's logical units. Not a page size — <c>PlotComposer</c> normalizes
    /// by the bounding box of the placed plots, so what these decide is the drawn ASPECT. They are the
    /// application's own defaults (<c>DataDisplayViewModel.DefaultSquareSize</c> /
    /// <c>DefaultPlotWidth</c>, and <c>AppSettings.RectAspectRatio</c>'s golden ratio), so a plot this
    /// verb writes has the shape one added in the window has.
    /// </summary>
    private const double SquareSize = 420;
    private const double RectWidth  = 520;

    public static int Run(string[] args)
    {
        var o = new Options();
        if (Parse(args, o) is { } bad) return bad;

        if (o.Result is null) { JsonRun.Report(CliDiagnostics.PlotResultRequired()); return Usage(); }
        if (o.Output is null) { JsonRun.Report(CliDiagnostics.PlotOutputRequired()); return Usage(); }
        JsonRun.InputPath = o.Result;

        if (!File.Exists(o.Result)) return JsonRun.Fail(CliDiagnostics.PlotResultNotFound(o.Result));

        string format = o.Format ?? Path.GetExtension(o.Output).ToLowerInvariant() switch
        {
            ".svg" => "svg", ".pdf" => "pdf", ".png" => "png", _ => "",
        };
        if (format.Length == 0)
            return JsonRun.Fail(CliDiagnostics.PlotUnknownOutputFormat(
                o.Output, Path.GetExtension(o.Output) is { Length: > 0 } e ? e : "(none)"));

        if (o.ScaleOption is not null && format != "png")
            return JsonRun.Fail(CliDiagnostics.PlotScaleOnVector(o.ScaleOption, format));

        if (o.Traces.Count == 0)
        {
            // R-rnd4-4's rule, at the one place this verb can break it: an empty plot is a valid
            // picture that exports cleanly and looks exactly like a measurement that came back
            // empty. A plot with no trace is that picture, and it is refused rather than drawn.
            JsonRun.Report(CliDiagnostics.PlotTraceRequired());
            return Usage();
        }

        // A Smith or a Polar chart's window is the complex plane and is framed on the unit circle;
        // there is no X and no Y for a range to be a range of. Refused rather than dropped, for
        // `render`'s reason: a flag that did nothing leaves a caller with a picture it cannot tell
        // from the one it asked for.
        if (o.Type is PlotType.Smith or PlotType.Polar
            && (o.X is not null || o.Y is not null || o.Y2 is not null))
            return JsonRun.Fail(CliDiagnostics.PlotWindowOnComplex(o.Type.ToString().ToLowerInvariant()));

        // The dB radial mode is a property of the POLAR plot's radius (ANT-7 §2). On any other plot
        // type there is no radius for it to be, and a flag that did nothing would leave a caller with
        // a picture it cannot tell from the one it asked for — `render`'s own rule, applied here.
        if (o.Radial == PolarRadialMode.Db && o.Type != PlotType.Polar)
            return JsonRun.Fail(CliDiagnostics.PlotRadialNeedsPolar(o.Type.ToString().ToLowerInvariant()));
        if (o.Radial != PolarRadialMode.Db && o.DbOptions.Count > 0
            && o.DbOptions.Exists(f => f != "--radial"))
            return JsonRun.Fail(CliDiagnostics.PlotDbOptionWithoutRadial(
                string.Join(", ", o.DbOptions.FindAll(f => f != "--radial"))));

        // ── the data, read once, before anything is authored ─────────────────
        //
        // A spec has to be checked against the cubes that are actually in the file: "no cube 'Pout'"
        // is an answer a caller can act on, where a picture with no curve in it is not.
        var (data, _, error) = CddSources.LoadResult(o.Result);
        if (error is not null) return JsonRun.Fail(CliDiagnostics.PlotResultUnreadable(o.Result, error));
        if (data is null || data.Groups.Count == 0)
            return JsonRun.Fail(CliDiagnostics.PlotResultUnreadable(o.Result, "it holds no cubes"));

        var traces = new List<TraceConfig>(o.Traces.Count);
        for (int i = 0; i < o.Traces.Count; i++)
        {
            var (tc, refusal) = BuildTrace(o.Traces[i], data, Path.GetFileName(o.Result), i, o.Type);
            if (refusal is { } r) return r;
            traces.Add(tc!);
        }

        var config = BuildConfig(o, traces);

        if (o.WriteCdd is { } cddPath)
        {
            // The document, as a document. It is exactly what was rendered — serialized from the
            // same object — so a caller can open it, edit it and carry on with the full surface.
            try
            {
                string? dir = Path.GetDirectoryName(Path.GetFullPath(cddPath));
                if (dir is { Length: > 0 }) Directory.CreateDirectory(dir);
                File.WriteAllText(cddPath,
                    JsonSerializer.Serialize(config, DataDisplayJson.Options),
                    new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.PlotWriteFailed(cddPath, ex.Message)); }

            JsonRun.AddOutput("cdd", cddPath);
            Console.Error.WriteLine($"[circuitRF] wrote {cddPath}");
        }

        // The result file is BOTH the document's directory (so its own bare-name reference resolves
        // beside it) and the bound --data, which is how `render --data` binds the same file.
        var request = new RenderDataDisplay.Request(
            o.Output, format, [o.Result], Tab: null, PlotIndex: null, AllTabs: false,
            o.Width, o.Height, o.Scale, o.Transparent, o.Dark);

        return RenderDataDisplay.Draw(o.Result, config, request, reportKind: "result");
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            "Usage: circuitrf plot <result.npy|.sNp> -o <out.svg|.pdf|.png> --trace <spec> [--trace <spec>]...\n" +
            "                     [--type rect|smith|polar] [--freq-unit Hz|kHz|MHz|GHz]\n" +
            "                     [--title T] [--xlabel T] [--ylabel T] [--y2label T]\n" +
            "                     [--x lo:hi] [--y lo:hi] [--y2 lo:hi]\n" +
            "                     [--size WxH] [--scale n | --dpi n] [--variant light|dark]\n" +
            "                     [--background opaque|transparent] [--write-cdd out.cdd]\n" +
            "                     [--radial linear|db] [--db-floor -40] [--db-ring 10]\n" +
            "                     [--db-ref peak|<dB>] [--db-unit dBi]\n" +
            "  a trace spec is comma-separated key=value: cube=S i=2 j=1 y=db axis=left|right\n" +
            "  an antenna pattern: cube=farfield.U cut=<phi deg>|all [port=<n>] [freq=2.45G] y=db10\n" +
            "                      cut=<deg> sweeps theta at one phi; cut=all keeps every phi as a family\n" +
            "  cube= takes the trace card's own shorthand — S[:,1,0], Pout, mag(V[:,\"X1.drain\"])\n" +
            "  a WSProbe quantity: cube=<analysis>.wsp probe=<label> metric=<name> [with=<label>]\n" +
            "                      [set=A;B] [z0=50] [side=G|L] [gi=1]\n" +
            "  its stability envelope: add src=<label> and/or load=<label> with gammaS=/gammaL=\n" +
            "                      a |Gamma| ladder (0.9;0.875;0.874), [theta=15] [passive=SP2.wsp]");
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
                        case "svg": o.Format = "svg"; break;
                        case "pdf": o.Format = "pdf"; break;
                        case "png": o.Format = "png"; break;
                        default: return JsonRun.Fail(CliDiagnostics.PlotUnknownFormatName(args[i]));
                    }
                    continue;

                case "--trace" when i + 1 < args.Length:
                {
                    var (spec, refusal) = ParseTrace(args[++i]);
                    if (refusal is { } r) return r;
                    o.Traces.Add(spec!);
                    continue;
                }

                case "--type" when i + 1 < args.Length:
                    switch (args[++i].ToLowerInvariant())
                    {
                        case "rect":  o.Type = PlotType.Rect;  break;
                        case "smith": o.Type = PlotType.Smith; break;
                        case "polar": o.Type = PlotType.Polar; break;
                        case "table": o.Type = PlotType.Table; break;
                        default: return JsonRun.Fail(CliDiagnostics.PlotUnknownType(args[i]));
                    }
                    continue;

                case "--freq-unit" when i + 1 < args.Length:
                    switch (args[++i].ToLowerInvariant())
                    {
                        case "hz":  o.FreqUnit = FreqUnit.Hz;  break;
                        case "khz": o.FreqUnit = FreqUnit.kHz; break;
                        case "mhz": o.FreqUnit = FreqUnit.MHz; break;
                        case "ghz": o.FreqUnit = FreqUnit.GHz; break;
                        default: return JsonRun.Fail(CliDiagnostics.PlotUnknownFreqUnit(args[i]));
                    }
                    continue;

                case "--title"   when i + 1 < args.Length: o.Title   = args[++i]; continue;
                case "--xlabel"  when i + 1 < args.Length: o.XLabel  = args[++i]; continue;
                case "--ylabel"  when i + 1 < args.Length: o.YLabel  = args[++i]; continue;
                case "--y2label" when i + 1 < args.Length: o.Y2Label = args[++i]; continue;

                case "--x"  when i + 1 < args.Length:
                    if (ParseRange("--x", args[++i]) is not { } xr) return 1;
                    o.X = xr; continue;
                case "--y"  when i + 1 < args.Length:
                    if (ParseRange("--y", args[++i]) is not { } yr) return 1;
                    o.Y = yr; continue;
                case "--y2" when i + 1 < args.Length:
                    if (ParseRange("--y2", args[++i]) is not { } y2r) return 1;
                    o.Y2 = y2r; continue;

                case "--size" when i + 1 < args.Length:
                {
                    var parts = args[++i].Split('x', 'X');
                    if (parts.Length != 2
                        || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int w)
                        || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h)
                        || w < 8 || h < 8 || w > 20000 || h > 20000)
                        return JsonRun.Fail(CliDiagnostics.PlotSizeMalformed(args[i]));
                    o.Width = w; o.Height = h;
                    continue;
                }

                case "--scale" when i + 1 < args.Length:
                {
                    if (o.ScaleOption is not null) return JsonRun.Fail(CliDiagnostics.PlotScaleAndDpi());
                    if (!double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out double s)
                        || !(s > 0) || s > 16)
                        return JsonRun.Fail(CliDiagnostics.PlotScaleMalformed("--scale", args[i]));
                    o.Scale = s; o.ScaleOption = "--scale";
                    continue;
                }
                case "--dpi" when i + 1 < args.Length:
                {
                    if (o.ScaleOption is not null) return JsonRun.Fail(CliDiagnostics.PlotScaleAndDpi());
                    if (!double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                        || !(d > 0) || d > 1536)
                        return JsonRun.Fail(CliDiagnostics.PlotScaleMalformed("--dpi", args[i]));
                    o.Scale = d / 96.0; o.ScaleOption = "--dpi";
                    continue;
                }

                case "--variant" when i + 1 < args.Length:
                    switch (args[++i].ToLowerInvariant())
                    {
                        case "light": o.Dark = false; break;
                        case "dark":  o.Dark = true;  break;
                        default: return JsonRun.Fail(CliDiagnostics.PlotUnknownVariant(args[i]));
                    }
                    continue;
                case "--background" when i + 1 < args.Length:
                    switch (args[++i].ToLowerInvariant())
                    {
                        case "opaque":      o.Transparent = false; break;
                        case "transparent": o.Transparent = true;  break;
                        default: return JsonRun.Fail(CliDiagnostics.PlotUnknownBackground(args[i]));
                    }
                    continue;

                case "--radial" when i + 1 < args.Length:
                    o.DbOptions.Add(a);
                    switch (args[++i].ToLowerInvariant())
                    {
                        case "linear": o.Radial = PolarRadialMode.Linear; break;
                        case "db":     o.Radial = PolarRadialMode.Db;     break;
                        default: return JsonRun.Fail(CliDiagnostics.PlotUnknownRadial(args[i]));
                    }
                    continue;

                case "--db-floor" when i + 1 < args.Length:
                {
                    o.DbOptions.Add(a);
                    if (!double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out double f)
                        || !(f < 0) || f < -200)
                        return JsonRun.Fail(CliDiagnostics.PlotDbFloorMalformed(args[i]));
                    o.DbFloor = f;
                    continue;
                }
                case "--db-ring" when i + 1 < args.Length:
                {
                    o.DbOptions.Add(a);
                    if (!double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out double r)
                        || !(r > 0) || r > 100)
                        return JsonRun.Fail(CliDiagnostics.PlotDbRingMalformed(args[i]));
                    o.DbRingStep = r;
                    continue;
                }
                case "--db-ref" when i + 1 < args.Length:
                {
                    o.DbOptions.Add(a);
                    string v = args[++i];
                    if (v.Equals("peak", StringComparison.OrdinalIgnoreCase))
                    { o.DbRef = PolarDbReferenceMode.Peak; continue; }
                    if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double rv))
                        return JsonRun.Fail(CliDiagnostics.PlotDbRefMalformed(v));
                    o.DbRef = PolarDbReferenceMode.Absolute; o.DbRefValue = rv;
                    continue;
                }
                case "--db-unit" when i + 1 < args.Length:
                    o.DbOptions.Add(a); o.DbUnit = args[++i]; continue;

                case "--write-cdd" when i + 1 < args.Length: o.WriteCdd = args[++i]; continue;

                default:
                    if (a.StartsWith('-'))
                    { JsonRun.Report(CliDiagnostics.PlotUnknownOption(a)); return Usage(); }
                    if (o.Result is not null)
                    { JsonRun.Report(CliDiagnostics.PlotMultipleResults()); return Usage(); }
                    o.Result = a;
                    continue;
            }
        }
        return null;
    }

    private static (double Lo, double Hi)? ParseRange(string option, string text)
    {
        int colon = text.LastIndexOf(':');
        if (colon > 0
            && double.TryParse(text[..colon], NumberStyles.Float, CultureInfo.InvariantCulture, out double lo)
            && double.TryParse(text[(colon + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double hi)
            && hi > lo)
            return (lo, hi);

        JsonRun.Fail(CliDiagnostics.PlotRangeMalformed(option, text));
        return null;
    }

    /// <summary>
    /// One <c>--trace</c>: comma-separated <c>key=value</c>, split on TOP-LEVEL commas only. The
    /// separator has to be bracket- and quote-aware because the cube shorthand it carries is full of
    /// commas — <c>cube=S[:,1,0],y=db</c> is two fields, not four.
    /// </summary>
    private static (TraceSpec? Spec, int? Refusal) ParseTrace(string raw)
    {
        var spec = new TraceSpec { Raw = raw };

        foreach (string field in SplitFields(raw))
        {
            int eq = field.IndexOf('=');
            if (eq <= 0) return (null, JsonRun.Fail(CliDiagnostics.PlotTraceFieldMalformed(raw, field)));

            string key   = field[..eq].Trim().ToLowerInvariant();
            string value = field[(eq + 1)..].Trim();

            switch (key)
            {
                case "cube": spec.Text  = value; break;
                case "y":    spec.YText = value; break;
                case "i" or "j":
                {
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1)
                        return (null, JsonRun.Fail(CliDiagnostics.PlotTracePortMalformed(raw, key, value)));
                    if (key == "i") spec.I = n; else spec.J = n;
                    break;
                }
                case "axis":
                    switch (value.ToLowerInvariant())
                    {
                        case "left":  spec.Secondary = false; break;
                        case "right": spec.Secondary = true;  break;
                        default: return (null, JsonRun.Fail(CliDiagnostics.PlotTraceAxisUnknown(raw, value)));
                    }
                    break;
                case "cut":    spec.Cut    = value; break;
                case "port":
                {
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pn))
                        return (null, JsonRun.Fail(CliDiagnostics.PlotTracePortMalformed(raw, key, value)));
                    spec.Port = pn;
                    break;
                }
                case "freq":
                {
                    if (!CircuitRF.Core.Netlist.Spice.SpiceNumber.TryParse(value, out double fhz) || !(fhz > 0))
                        return (null, JsonRun.Fail(CliDiagnostics.PlotTraceFreqMalformed(raw, value)));
                    spec.FreqHz = fhz;
                    break;
                }
                case "probe":  spec.Probe  = value; break;
                case "with":   spec.With   = value; break;
                case "set":    spec.Set    = value; break;
                case "metric": spec.Metric = value; break;
                case "z0":     spec.Z0     = value; break;
                case "side":
                    if (!value.Equals("G", StringComparison.OrdinalIgnoreCase)
                     && !value.Equals("L", StringComparison.OrdinalIgnoreCase))
                        return (null, JsonRun.Fail(CliDiagnostics.PlotWspSideUnknown(raw, value)));
                    spec.Side = value.ToUpperInvariant();
                    break;
                case "src":     spec.SrcProbe  = value; break;
                case "load":    spec.LoadProbe = value; break;
                case "gammas":  spec.GammaS    = value; break;
                case "gammal":  spec.GammaL    = value; break;
                case "passive": spec.Passive   = value; break;
                case "theta":
                {
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double th)
                        || !(th > 0.0) || th > 360.0)
                        return (null, JsonRun.Fail(CliDiagnostics.PlotTracePortMalformed(raw, key, value)));
                    spec.Theta = th;
                    break;
                }
                case "gi":
                {
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int gi) || gi < 1)
                        return (null, JsonRun.Fail(CliDiagnostics.PlotTracePortMalformed(raw, key, value)));
                    spec.Gi = gi;
                    break;
                }
                default:
                    return (null, JsonRun.Fail(CliDiagnostics.PlotTraceUnknownKey(raw, key)));
            }
        }

        if (spec.Text.Length == 0)
            return (null, JsonRun.Fail(CliDiagnostics.PlotTraceCubeRequired(raw)));

        return (spec, null);
    }

    /// <summary>
    /// One side's <c>|\u0393|</c> ladder, semicolon separated as <c>set=</c> is (a comma is the field
    /// separator). An unparsable rung is DROPPED rather than refusing the trace: the ladder as a
    /// whole still has to leave the side pulled, and an empty result is that side's off state, which
    /// the resolve then names.
    /// </summary>
    private static List<double> Ladder(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var outp = new List<double>();
        foreach (string tok in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                outp.Add(v);
        return outp;
    }

    /// <summary>Top-level commas only — a comma inside <c>[…]</c> or <c>"…"</c> is part of a cube
    /// shorthand, not a field separator.</summary>
    private static List<string> SplitFields(string text)
    {
        var fields  = new List<string>();
        int start   = 0, depth = 0;
        bool quoted = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"') quoted = !quoted;
            else if (!quoted && c == '[') depth++;
            else if (!quoted && c == ']') depth--;
            else if (!quoted && depth == 0 && c == ',')
            {
                if (i > start) fields.Add(text[start..i].Trim());
                start = i + 1;
            }
        }
        if (start < text.Length) fields.Add(text[start..].Trim());
        return fields;
    }

    // ── the document ─────────────────────────────────────────────────────────

    /// <summary>
    /// One <c>--trace</c> as a <see cref="TraceConfig"/>, with its slice resolved against the cubes
    /// the result actually holds.
    /// </summary>
    private static (TraceConfig? Trace, int? Refusal) BuildTrace(
        TraceSpec spec, DataSet data, string sourceRef, int index, PlotType plotType)
    {
        string text = spec.Text;

        // The cube NAME, checked first and in every branch. The parser's own answer for a bare name
        // it does not recognise is "Missing '['" — correct from where it stands, and useless to a
        // caller who simply mistyped a cube or is looking at the wrong run. What that caller needs
        // is the list of what the file holds, so this refusal is made here rather than forwarded.
        if (BareCubeName(text) is { Length: > 0 } bare && !data.Contains(bare))
            return (null, JsonRun.Fail(CliDiagnostics.PlotNoSuchCube(bare, CubeNames(data))));

        // ── a WSProbe metric (WSP-4 R-wsp4-12) ──────────────────────────────
        //
        //  `cube=` names the run's own wsp MATRIX and the metric is taken of it, which is exactly
        //  what the trace card authors — the document this verb writes is the same `.cdd` the GUI
        //  writes, so there is no second probe path here any more than there is a second plotting
        //  one. The refusals are the library's own sentences, which name the run's probes.
        if (spec.Probe is not null || spec.Metric is not null || spec.With is not null
            || spec.Set is not null || spec.Side is not null || spec.Gi is not null
            || spec.SrcProbe is not null || spec.LoadProbe is not null
            || spec.GammaS is not null || spec.GammaL is not null
            || spec.Theta is not null || spec.Passive is not null)
        {
            if (text.Contains('['))
                return (null, JsonRun.Fail(CliDiagnostics.PlotTracePortsWithSlice(spec.Raw)));
            if (spec.Metric is null)
                return (null, JsonRun.Fail(CliDiagnostics.PlotWspMetricRequired(spec.Raw)));
            if (!WspMetrics.TryParse(spec.Metric, out var metric))
                return (null, JsonRun.Fail(CliDiagnostics.PlotWspMetricUnknown(
                    spec.Raw, spec.Metric, string.Join(", ", WspMetrics.All.Select(m => m.Name)))));

            if (WspSource.LeadingAxes(data, text) is not { Length: > 0 } leading)
                return (null, JsonRun.Fail(CliDiagnostics.PlotWspNotAWspCube(spec.Raw, text)));

            var wspSpec = new WspTraceSpec
            {
                Probe  = spec.Probe ?? "",
                With   = spec.With  ?? "",
                Set    = spec.Set is { Length: > 0 } set
                    ? [.. set.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
                    : [],
                Metric = metric,
                Z0     = spec.Z0 is { Length: > 0 } z0 && double.TryParse(
                             z0, NumberStyles.Float, CultureInfo.InvariantCulture, out double z0v)
                         ? new System.Numerics.Complex(z0v, 0)
                         : System.Numerics.Complex.Zero,
                ActiveSide = spec.Side == "L" ? RfCore.Stability.WspSide.L : RfCore.Stability.WspSide.G,
                SetIndex   = spec.Gi ?? 1,
                SourceProbe   = spec.SrcProbe  ?? "",
                LoadProbe     = spec.LoadProbe ?? "",
                GammaSMags    = Ladder(spec.GammaS),
                GammaLMags    = Ladder(spec.GammaL),
                ThetaStepDeg  = spec.Theta ?? 15.0,
                PassiveSource = spec.Passive ?? "",
            };

            // Resolved HERE rather than at draw time, for the reason every other refusal in this
            // verb is made here: "this run has no probe named GATE, it has DRAIN and SOURCE" is an
            // answer a caller can act on, and a picture with no curve in it is not.
            if (!WspSource.TryEvaluate(data, text, wspSpec, out var wspCube, out string wspErr))
                return (null, JsonRun.Fail(CliDiagnostics.PlotWspUnresolved(spec.Raw, wspErr)));

            // `y=` on a probe trace is the ordinary transform over the metric's own values — the
            // same table the trace card's combo uses, resolved through the spec parser so there is
            // one list of transform names. Absent, the metric's own default applies.
            var wspTransform = WspMetrics.DefaultTransform(metric, plotType);
            if (spec.YText is { } wy)
            {
                string t = wy.Equals("imaginary", StringComparison.OrdinalIgnoreCase) ? "imag" : wy;
                // The parser's OWN table, by name — not by handing it `db(wsp)`, which is not a spec
                // it can read: the cube here is the 2N x 2N matrix and the values are a METRIC of it.
                if (!CubeTraceSpecParser.TryParseTransformName(t, out wspTransform))
                    return (null, JsonRun.Fail(CliDiagnostics.PlotTraceUnresolved(
                        spec.Raw, $"{t}(…)", $"'{wy}' is not a transform. "
                      + "They are: db20, db10, db, mag, phase, real, imag, conj, none.")));
            }

            // The slice is authored against the METRIC cube's own axes, not the wsp matrix's leading
            // ones: an envelope metric carries the four grid axes (rhoS, thetaS, rhoL, thetaL) and
            // SMenv has no freq axis at all, so slicing against `leading` would pin axes the cube
            // does not have and leave its own unpinned. `wspCube` is what the resolve above produced,
            // which is exactly what the display will read.
            var wspAxes = wspCube!.Axes;
            int wspRank = wspAxes.Count;
            int xIdx = WspMetrics.NeedsEnvelope(metric) ? -1 : 0;
            for (int d = 0; d < wspRank; d++)
                if (wspAxes[d].Name == "freq") { xIdx = d; break; }
            if (xIdx < 0)
                // An envelope quantity with no frequency axis is read AGAINST PHASE ([E] Fig. 6-9);
                // the pulled side's theta is the x axis, and a length-1 axis is not one.
                for (int d = 0; d < wspRank; d++)
                    if (wspAxes[d].Name is "thetaS" or "thetaL" && wspAxes[d].Length > 1) { xIdx = d; break; }
            if (xIdx < 0) xIdx = 0;

            var wspSlice = new AxisSlice[wspRank];
            for (int d = 0; d < wspRank; d++)
                wspSlice[d] = d == xIdx
                    ? new AxisSlice(wspAxes[d].Name, AxisRole.KeepAsX, 0)
                    : new AxisSlice(wspAxes[d].Name, AxisRole.PinToIndex, 0);

            return (new TraceConfig
            {
                SourcePath       = sourceRef,
                CubeName         = text,
                CubeSlice        = [.. wspSlice.Select(AxisSliceConfig.From)],
                CubeTransform    = wspTransform,
                UseSecondaryAxis = spec.Secondary,
                WsProbe          = new WspTraceConfig
                {
                    Probe      = wspSpec.Probe,
                    With       = wspSpec.With,
                    Set        = [.. wspSpec.Set],
                    Metric     = wspSpec.Metric,
                    Z0         = wspSpec.Z0.Real.ToString("G6", CultureInfo.InvariantCulture),
                    ActiveSide = wspSpec.ActiveSide,
                    SetIndex   = wspSpec.SetIndex,
                    SourceProbe   = wspSpec.SourceProbe,
                    LoadProbe     = wspSpec.LoadProbe,
                    GammaSMags    = [.. wspSpec.GammaSMags],
                    GammaLMags    = [.. wspSpec.GammaLMags],
                    ThetaStepDeg  = wspSpec.ThetaStepDeg,
                    PassiveSource = wspSpec.PassiveSource,
                },
                Properties       = new TracePropertiesConfig
                {
                    LineColorIndex   = WheelColor(index),
                    MarkerColorIndex = WheelColor(index),
                },
            }, null);
        }

        // ── the two pattern spellings (ANT-7 §3) ─────────────────────────────
        //
        //  Both are shorthand over the SAME general slice mechanism everything else uses — they
        //  build the bracket text and hand it to the trace card's own parser, so what is authored
        //  here is a `.cdd` the window would have written and there is no second resolve path.
        //
        //    cut=<deg>  pin freq and phi, sweep theta   — the E-plane / H-plane plot
        //    cut=all    pin freq,         sweep theta, iterate phi as a family — the whole pattern
        if (spec.Cut is not null)
        {
            if (text.Contains('['))
                return (null, JsonRun.Fail(CliDiagnostics.PlotTracePortsWithSlice(spec.Raw)));

            var pcube = data[text];
            if (!HasAxis(pcube, "theta") || !HasAxis(pcube, "phi"))
                return (null, JsonRun.Fail(CliDiagnostics.PlotCutNeedsPatternAxes(
                    spec.Raw, text, AxisNames(pcube))));

            bool wholePattern = spec.Cut.Equals("all", StringComparison.OrdinalIgnoreCase);
            int  phiIndex     = 0;
            if (!wholePattern)
            {
                if (!double.TryParse(spec.Cut, NumberStyles.Float, CultureInfo.InvariantCulture, out double phiDeg))
                    return (null, JsonRun.Fail(CliDiagnostics.PlotCutMalformed(spec.Raw, spec.Cut)));
                phiIndex = NearestIndex(AxisOf(pcube, "phi")!.Values, phiDeg, out double gotPhi);
                Console.Error.WriteLine(
                    $"[circuitRF] cut at phi = {gotPhi.ToString("0.###", CultureInfo.InvariantCulture)}°"
                  + (Math.Abs(gotPhi - phiDeg) > 1e-9
                        ? $" (nearest sample to the {phiDeg.ToString("0.###", CultureInfo.InvariantCulture)}° asked for)"
                        : ""));
            }

            var ptok = new string[pcube.Rank];
            for (int d = 0; d < pcube.Rank; d++)
            {
                var ax = pcube.Axes[d];
                ptok[d] = ax.Name switch
                {
                    "theta" => ":",
                    "phi"   => wholePattern ? "~" : phiIndex.ToString(CultureInfo.InvariantCulture),
                    "port"  => PortToken(ax, spec.Port),
                    "freq"  => FreqToken(ax, spec.FreqHz),
                    _       => "0",
                };
            }
            if (spec.Port is { } wantPort && !HasAxis(pcube, "port"))
                return (null, JsonRun.Fail(CliDiagnostics.PlotNoPortAxis(text, "port", AxisNames(pcube))));

            text = $"{text}[{string.Join(", ", ptok)}]";
        }
        // `port=`/`freq=` alone are the same convenience without a cut — they pin one named axis on
        // a cube whose other axes are already one sample deep, which every metric cube is.
        else if (spec.Port is not null || spec.FreqHz is not null)
        {
            if (text.Contains('['))
                return (null, JsonRun.Fail(CliDiagnostics.PlotTracePortsWithSlice(spec.Raw)));

            var qcube = data[text];
            if (spec.Port is not null && !HasAxis(qcube, "port"))
                return (null, JsonRun.Fail(CliDiagnostics.PlotNoPortAxis(text, "port", AxisNames(qcube))));
            if (spec.FreqHz is not null && !HasAxis(qcube, "freq"))
                return (null, JsonRun.Fail(CliDiagnostics.PlotNoPortAxis(text, "freq", AxisNames(qcube))));

            var qtok = new string[qcube.Rank];
            bool xTaken = false;
            for (int d = 0; d < qcube.Rank; d++)
            {
                var ax = qcube.Axes[d];
                if (ax.Name == "port" && spec.Port is not null) { qtok[d] = PortToken(ax, spec.Port); continue; }
                if (ax.Name == "freq" && spec.FreqHz is not null) { qtok[d] = FreqToken(ax, spec.FreqHz); continue; }
                // The first axis nobody pinned is the sweep. Anything after it is pinned, exactly as
                // the parser's own positional convention would have resolved a bare name.
                qtok[d] = xTaken ? "0" : ":";
                xTaken  = true;
            }
            text = $"{text}[{string.Join(", ", qtok)}]";
        }

        // i/j pin the axes NAMED i and j, by port number. They are the convenience over the
        // shorthand, so a spec that already carries a slice is a caller saying both things at once,
        // and it is refused rather than one of them being dropped.
        if (spec.I is not null || spec.J is not null)
        {
            if (text.Contains('['))
                return (null, JsonRun.Fail(CliDiagnostics.PlotTracePortsWithSlice(spec.Raw)));

            var cube = data[text];

            if (spec.I is not null && !cube.Axes.Any(a => a.Name.Equals("i", StringComparison.OrdinalIgnoreCase)))
                return (null, JsonRun.Fail(CliDiagnostics.PlotNoPortAxis(text, "i", AxisNames(cube))));
            if (spec.J is not null && !cube.Axes.Any(a => a.Name.Equals("j", StringComparison.OrdinalIgnoreCase)))
                return (null, JsonRun.Fail(CliDiagnostics.PlotNoPortAxis(text, "j", AxisNames(cube))));

            var tokens = new string[cube.Rank];
            for (int d = 0; d < cube.Rank; d++)
            {
                var  axis = cube.Axes[d];
                int? want = axis.Name.Equals("i", StringComparison.OrdinalIgnoreCase) ? spec.I
                            : axis.Name.Equals("j", StringComparison.OrdinalIgnoreCase) ? spec.J
                            : null;
                // The PORT NUMBER, verbatim: the shorthand's own integer token on an `i`/`j` axis is
                // 1-based (S[:,2,1] is S21, which is what makes that spelling readable), and the
                // range check that goes with it is the parser's. Emitting a 0-based index here would
                // have been the same number shifted by one — the quietest possible wrong answer,
                // since S12 and S21 are both legal.
                tokens[d] = want is null ? ":" : want.Value.ToString(CultureInfo.InvariantCulture);
            }

            text = $"{text}[{string.Join(", ", tokens)}]";
        }

        // `y=` becomes the transform PREFIX on the spec, in the function-call form the trace card's
        // own picker emits — so there is one table of transform names and it is the parser's.
        if (spec.YText is { } y)
        {
            string t = y.Equals("imaginary", StringComparison.OrdinalIgnoreCase) ? "imag" : y.ToLowerInvariant();
            text = $"{t}({text})";
        }

        if (!CubeTraceSpecParser.TryParse(text, data, out string cubeName, out var slice,
                                          out var transform, out string parseError))
            return (null, JsonRun.Fail(CliDiagnostics.PlotTraceUnresolved(spec.Raw, text, parseError)));

        // A rank-0 cube is one number. It is a legal thing to put on a Table and not a curve, and a
        // plot of it would be an empty picture — R-rnd4-4's most dangerous output.
        if (slice is null || slice.Length == 0)
            return (null, JsonRun.Fail(CliDiagnostics.PlotCubeIsScalar(cubeName)));

        return (new TraceConfig
        {
            SourcePath       = sourceRef,
            CubeName         = cubeName,
            CubeSlice        = [.. slice.Select(AxisSliceConfig.From)],
            CubeTransform    = transform,
            UseSecondaryAxis = spec.Secondary,
            // The colour wheel the window walks, in its own order — TraceProperties.LineColorOrder,
            // whose first entry is 12 (red) rather than 0. Using the trace's ordinal directly would
            // have given the first trace colour 0, which is black: invisible on a dark variant, and
            // nothing would have said so.
            Properties       = new TracePropertiesConfig
            {
                LineColorIndex   = WheelColor(index),
                MarkerColorIndex = WheelColor(index),
            },
        }, null);
    }

    /// <summary>
    /// The cube a spec names, before any slice or transform — the whole of <c>S</c>, <c>S[:,2,1]</c>,
    /// <c>mag(Pout)</c> and <c>db S[:,2,1]</c>. Deliberately loose: it exists only to make a
    /// mistyped NAME a refusal that lists the real ones, and anything it gets wrong falls through to
    /// the parser, which is the authority on the rest of the syntax.
    /// </summary>
    private static string BareCubeName(string spec)
    {
        string t = spec.Trim();

        int bracket = t.IndexOf('[');
        if (bracket >= 0) t = t[..bracket];

        // `mag(Pout)` and the half-open `mag(S` left behind by dropping the slice.
        int paren = t.LastIndexOf('(');
        if (paren >= 0) t = t[(paren + 1)..];

        // `db S` — the space-separated transform form.
        int space = t.LastIndexOfAny([' ', '\t']);
        if (space >= 0) t = t[(space + 1)..];

        return t.Trim().TrimEnd(')');
    }

    /// <summary>The nth trace's colour, from the order the window rotates through.</summary>
    private static int WheelColor(int index)
        => TraceProperties.LineColorOrder[index % TraceProperties.LineColorOrder.Length];

    private static string AxisNames(DataCube cube) => string.Join(", ", cube.Axes.Select(a => a.Name));

    private static Axis? AxisOf(DataCube cube, string name) =>
        cube.Axes.FirstOrDefault(a => a.Name.Equals(name, StringComparison.Ordinal));

    private static bool HasAxis(DataCube cube, string name) => AxisOf(cube, name) is not null;

    /// <summary>The index of the sample nearest <paramref name="want"/>, and the value it actually
    /// landed on — which the caller REPORTS, because a pin that silently moved is a plot of a
    /// different cut.</summary>
    private static int NearestIndex(double[] values, double want, out double got)
    {
        int best = 0;
        double bestD = double.PositiveInfinity;
        for (int k = 0; k < values.Length; k++)
        {
            double d = Math.Abs(values[k] - want);
            if (d < bestD) { bestD = d; best = k; }
        }
        got = values.Length > 0 ? values[best] : double.NaN;
        return best;
    }

    /// <summary>
    /// The token for a <c>port</c> axis. <b>A 1-based PORT NUMBER, verbatim</b> — the same trap
    /// <c>i</c>/<c>j</c> already record, and the reason the parser resolves it against the axis's own
    /// VALUES rather than by subtracting one: a far-field cube's port axis carries the numbers of the
    /// ports that were driven, which need not start at 1. With no <c>port=</c> given, the FIRST port
    /// the run holds, written as its number so the authored `.cdd` reads the same way.
    /// </summary>
    private static string PortToken(Axis axis, int? port)
    {
        double n = port ?? (axis.Values.Length > 0 ? axis.Values[0] : 1.0);
        return n.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>The token for a <c>freq</c> axis: the nearest sample's INDEX (a frequency axis is
    /// indexed, not numbered), or 0 when the caller named none.</summary>
    private static string FreqToken(Axis axis, double? fHz)
    {
        if (fHz is not double f) return "0";
        int idx = NearestIndex(axis.Values, f, out double got);
        Console.Error.WriteLine(
            $"[circuitRF] freq pinned to {got.ToString("G6", CultureInfo.InvariantCulture)} Hz"
          + (Math.Abs(got - f) > 1e-6 ? " (nearest sample)" : ""));
        return idx.ToString(CultureInfo.InvariantCulture);
    }

    private static string CubeNames(DataSet ds)
        => string.Join(", ", ds.Groups.SelectMany(g => ds.CubesIn(g).Keys
            .Select(c => g == DataSet.DefaultGroup ? c : $"{g}.{c}")));

    /// <summary>
    /// The whole document: one tab, one plot, the traces given. Written as the CURRENT format
    /// version with a tab, rather than as the legacy top-level plot list, so <c>--write-cdd</c>
    /// produces a file the application opens as itself.
    /// </summary>
    private static DataDisplayConfig BuildConfig(Options o, List<TraceConfig> traces)
    {
        bool   square = o.Type is PlotType.Smith or PlotType.Polar;
        double w      = square ? SquareSize : RectWidth;
        double ratio  = AppSettings.Current.RectAspectRatio;
        double h      = square ? SquareSize
                      : o.Type == PlotType.Rect && ratio > 0 ? w / ratio
                      : 360;

        var container = new PlotContainerConfig
        {
            Left     = 0,
            Top      = 0,
            Width    = w,
            Height   = h,
            PlotType = o.Type,
            FreqUnit = o.FreqUnit,
            Traces   = traces,
            Axes     = BuildAxes(o),
        };

        container.PolarRadial           = o.Radial;
        container.PolarDbFloor          = o.DbFloor;
        container.PolarDbRingStep       = o.DbRingStep;
        container.PolarDbReference      = o.DbRef;
        container.PolarDbReferenceValue = o.DbRefValue;
        container.PolarDbUnit           = o.DbUnit;

        if (o.Title   is { } title)  { container.CustomTitle   = title;  container.CustomTitleOn   = true; }
        if (o.XLabel  is { } xl)     { container.CustomXLabel  = xl;     container.CustomXLabelOn  = true; }
        if (o.YLabel  is { } yl)     { container.CustomYLabel  = yl;     container.CustomYLabelOn  = true; }
        if (o.Y2Label is { } y2l)    { container.CustomY2Label = y2l;    container.CustomY2LabelOn = true; }

        return new DataDisplayConfig
        {
            FormatVersion      = DataDisplayConfig.CurrentFormatVersion,
            SelectedDataSource = Path.GetFileName(o.Result!),
            Tabs               = [new TabConfig { Name = "Tab 1", Plots = [container] }],
        };
    }

    /// <summary>
    /// The axis window, or null for full autoscale.
    ///
    /// <para><b>A HALF window is deliberate and it works.</b> <c>Plot.RestoreAxesFromConfig</c> tests
    /// the window's validity as width AND height, and re-autoscales only the axes whose own flag is
    /// still set — and <c>AutoscaleCore("x")</c> preserves Y while <c>("y")</c> preserves X. So
    /// <c>--x</c> alone pins X and autoscales Y, which is what a caller asking for one of them
    /// means.</para>
    /// </summary>
    private static AxesConfig? BuildAxes(Options o)
    {
        if (o.X is null && o.Y is null && o.Y2 is null) return null;

        var axes = new AxesConfig
        {
            AutoscaleX      = o.X is null,
            AutoscaleY      = o.Y is null,
            AutoscaleRightY = o.Y2 is null,
            AutoscaleMag    = true,
            WindowX      = o.X?.Lo ?? 0, WindowWidth  = o.X is { } x ? x.Hi - x.Lo : 0,
            WindowY      = o.Y?.Lo ?? 0, WindowHeight = o.Y is { } y ? y.Hi - y.Lo : 0,
            WindowSecondaryX      = o.X?.Lo ?? 0,
            WindowSecondaryWidth  = o.X is { } sx ? sx.Hi - sx.Lo : 0,
            WindowSecondaryY      = o.Y2?.Lo ?? 0,
            WindowSecondaryHeight = o.Y2 is { } y2 ? y2.Hi - y2.Lo : 0,
        };
        return axes;
    }
}
