// A plot picture's flags, as `recognize` reads them — brief-img-14-sparameter-plots-to-touchstone.md R-im14-14.
//
// THIS FILE OWNS NO PLOT READING (R-im14-15). Every flag here is the spelling of one field of PlotReadingOptions or
// TouchstoneAssemblyOptions — the options the dialog's rows fill in — and nothing more. The reading is PlotReading's,
// the file TouchstoneAssembly's, the CSV PlotCsv's.
//
// EVERY PICTURE COORDINATE IS IN PIXELS, as --region's are: --x-axis takes `120px=1GHz,900px=5GHz` (the `px` is optional
// here, because the value side carries the meaning and nothing else on this flag could be a length).
//
// DIGITISING BY HAND (brief-pdg-4-digitise-by-hand.md §4) is the same records the dialog's Manual steps build:
// --manual-box is the ManualPlot's box (--region's spelling, px required, `@deg` turning it about its centre), --x-range /
// --y-range / --y2-range its typed scales, --curve and --follow its ManualCurves, --sample / --sample-step / --method its
// grid. A curve's points carry no meaning but pixels, so their `px` is optional, as --x-axis's is.

using System.Globalization;
using CircuitRF.Design.Imaging.Plots;
using CircuitRF.Design.Layout.Recognition;
using RfCore;

namespace CircuitRF.Cli;

internal sealed class PlotFlags
{
    public readonly List<int> Plots = [];
    public readonly List<(int Plot, PlotAxis Axis, double P0, double V0, double P1, double V1)> Clicks = [];
    public readonly List<(int Plot, PlotAxis Axis, bool Log)> Scales = [];
    public readonly List<PlotTraceOverride> Traces = [];
    public readonly List<PlotFrequencyOverride> Frequencies = [];
    public readonly List<PlotFitOverride> Fits = [];

    /// <summary>The x values <c>--probe</c> reads every rectangular trace at (brief-pdg-3-snap-to-curve.md R-pdg3-3): as
    /// typed, and in the axis's own units — hertz on a frequency axis.</summary>
    public readonly List<(string Text, double X)> Probes = [];
    public readonly Dictionary<(int I, int J), PhaseRule> Phase = [];
    public readonly Dictionary<(int I, int J), FillRule> Fill = [];
    public readonly Dictionary<(int I, int J), string> Prefer = [];
    public double? SmithZ0;
    public AssemblyGrid? Grid;
    public MatrixFormat? Format;
    public int? Ports;
    public string? Csv;
    public bool KeepSources = true;

    // ── by hand (R-pdg4-1 … R-pdg4-3) ──
    public (string Text, ManualQuad Quad)? ManualBox;
    public (ManualAxis Axis, double Unit, bool Frequency)? XRange;
    public ManualAxis? YRange;
    public ManualAxis? Y2Range;

    /// <summary>The curves: name, points (file pixels), axis, assignment — in the order given.</summary>
    public readonly List<(string Name, List<(double X, double Y)> Points, PlotSide Axis, PlotAssignment Assignment)> Curves = [];

    /// <summary>Find curve from one click: the curve's name, the click (file pixels), and the y scale and quantity a
    /// <c>,axis=</c> / <c>,is=</c> suffix gave — null when none did (EMB-5 R-emb5-11).</summary>
    public readonly List<(string Name, double X, double Y, PlotSide? Axis, PlotAssignment? Assignment)> Follows = [];

    /// <summary><c>--all-traces</c>: a refused or unassigned reading lists every trace rather than the count and the
    /// longest few (EMB-5 R-emb5-9).</summary>
    public bool AllTraces;
    public int? SamplePoints;
    public double? SampleStep;
    public ManualSampleMethod? Method;

    /// <summary>A flag of the by-hand reading was given: the picture is read by hand.</summary>
    public bool Manual => ManualBox is not null || XRange is not null || YRange is not null || Y2Range is not null || Curves.Count > 0
                          || Follows.Count > 0 || SamplePoints is not null || SampleStep is not null || Method is not null;

    /// <summary>The plot flags given, by spelling — refused on any other kind of input.</summary>
    public readonly List<string> Given = [];

    /// <summary>Reads <c>args[i]</c> when it is a plot flag. Null when it is not one, 0 when taken, else a refusal's
    /// exit code.</summary>
    public int? Take(string verb, string[] args, ref int i)
    {
        string a = args[i];
        // The three bare flags first: they take no value.
        if (a == "--all-traces")
        {
            AllTraces = true;
            Given.Add(a);
            return 0;
        }
        if (a is "--x-log" or "--y-log" or "--y2-log")
        {
            Scales.Add((0, AxisOf(a), true));
            Given.Add(a);
            return 0;
        }
        if (i + 1 >= args.Length) return null;
        string v = args[i + 1];
        int? Bad(string expected) => JsonRun.Fail(CliDiagnostics.PictureBadValue(verb, a, v, expected));
        switch (a)
        {
            case "--plot":
                foreach (string part in v.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1)
                        return Bad("the session's plot numbers to read, comma-separated (1,3)");
                    Plots.Add(n);
                }
                break;
            case "--x-axis" or "--y-axis" or "--y2-axis":
            {
                var axis = AxisOf(a);
                var (text, plot) = At(v);
                var pairs = text.Split(',', StringSplitOptions.TrimEntries);
                var parsed = new List<(double P, double V)>();
                foreach (string pair in pairs)
                {
                    int eq = pair.IndexOf('=');
                    if (eq < 0 || Px(pair[..eq]) is not { } px || AxisValue(pair[(eq + 1)..], axis) is not { } value) { parsed.Clear(); break; }
                    parsed.Add((px, value));
                }
                if (parsed.Count != 2 || plot is null)
                    return Bad(axis == PlotAxis.X
                        ? "<px>=<value>,<px>=<value>[@plot]: two pixel positions on the axis and their values (120px=1GHz,900px=5GHz)"
                        : axis == PlotAxis.Y2
                        ? "<px>=<value>,<px>=<value>[@plot]: two pixel positions on the right-hand y axis and their values (80px=180,560px=-180)"
                        : "<px>=<value>,<px>=<value>[@plot]: two pixel positions on the axis and their values (80px=0,560px=-40)");
                Clicks.Add((plot.Value, axis, parsed[0].P, parsed[0].V, parsed[1].P, parsed[1].V));
                break;
            }
            case "--trace":
            {
                // <selector>=<assignment>[,axis=left|right] — or the axis alone, <selector>=axis=right (R-pdg1-4).
                int eq = v.IndexOf('=');
                var parts = eq > 0 ? v[(eq + 1)..].Split(',', StringSplitOptions.TrimEntries) : [];
                PlotAssignment? assignment = null;
                PlotSide? side = null;
                bool ok = parts.Length > 0;
                for (int k = 0; k < parts.Length && ok; k++)
                {
                    if (parts[k].StartsWith("axis=", StringComparison.OrdinalIgnoreCase))
                    {
                        side = parts[k][5..].Trim().ToLowerInvariant() switch { "left" => PlotSide.Left, "right" => PlotSide.Right, _ => null };
                        ok = side is not null;
                    }
                    else if (k == 0) ok = (assignment = PlotQuantity.ParseAssignment(parts[k])) is not null;
                    else ok = false;
                }
                if (!ok || (assignment is null && side is null))
                    return Bad("<T3|#rrggbb|solid|dashed|dotted>[@plot]=S21:db (or ignore), optionally ,axis=left|right (or axis=right alone); "
                             + "quantities db, mag, phase, rad, re, im, complex, vswr, rl, il");
                Traces.Add(new PlotTraceOverride(v[..eq].Trim(), assignment, side));
                break;
            }
            case "--fit":
            {
                // <selector>=<model>[:<degree or smoothing>], the selector --trace's or all (brief-pdg-2-curve-fit.md R-pdg2-5).
                int eq = v.LastIndexOf('=');
                var spec = eq > 0 ? TraceFitSpec.Parse(v[(eq + 1)..]) : null;
                if (spec is null)
                    return Bad("<T3|#rrggbb|solid|dashed|dotted|all>[@plot]=<model>: linear, exp, poly[:<degree 1-8>] (3 by default), "
                             + "spline[:<RMS in the trace's units>] (one pixel by default) or none");
                Fits.Add(new PlotFitOverride(v[..eq].Trim(), spec));
                break;
            }
            case "--probe":
                if (TraceProbe.ParseX(v) is not { } x)
                    return Bad("an x to read every rectangular trace at: a frequency with its unit (2.4GHz), or a plain number in the axis's own units");
                Probes.Add((v.Trim(), x));
                break;
            case "--smith-z0":
            {
                string t = v.Trim();
                foreach (string unit in new[] { "Ohm", "ohm", "Ω" })
                    if (t.EndsWith(unit, StringComparison.Ordinal)) { t = t[..^unit.Length]; break; }
                if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double z) || !(z > 0) || !double.IsFinite(z))
                    return Bad("a reference impedance in ohms (50Ohm)");
                SmithZ0 = z;
                break;
            }
            case "--freq":
            {
                int eq = v.LastIndexOf('=');
                var range = eq > 0 ? Range(v[(eq + 1)..]) : null;
                if (range is not { } r) return Bad("<trace>=<start>..<stop>, each with its unit (T4=1GHz..5GHz)");
                Frequencies.Add(new PlotFrequencyOverride(v[..eq].Trim(), r.Start, r.Stop));
                break;
            }
            case "--phase":
                foreach (string entry in v.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = entry.IndexOf('=');
                    var at = eq > 0 ? PlotQuantity.Entry(entry[..eq]) : null;
                    var rule = eq > 0 ? PhaseOf(entry[(eq + 1)..]) : null;
                    if (at is null || rule is null) return Bad("S21=min|zero|delay:<time>, comma-separated (S21=delay:250ps)");
                    Phase[at.Value] = rule;
                }
                break;
            case "--fill":
                foreach (string entry in v.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = entry.IndexOf('=');
                    var at = eq > 0 ? PlotQuantity.Entry(entry[..eq]) : null;
                    var rule = at is { } e ? FillOf(e, entry[(eq + 1)..]) : null;
                    if (at is null || rule is null) return Bad("S12=S21|zero|default|unassigned, S22=S11, comma-separated");
                    Fill[at.Value] = rule.Value;
                }
                break;
            case "--prefer":
                foreach (string entry in v.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = entry.IndexOf('=');
                    var at = eq > 0 ? PlotQuantity.Entry(entry[..eq]) : null;
                    string which = eq > 0 ? entry[(eq + 1)..].Trim() : "";
                    if (at is null || !(which is "chart" or "rect" || (which.Length > 1 && which[0] is 'T' or 't' && int.TryParse(which[1..], out _))))
                        return Bad("S11=chart|rect|T3, comma-separated: which of two traces of one entry is written");
                    Prefer[at.Value] = which;
                }
                break;
            case "--grid":
            {
                string t = v.Trim();
                bool log = t.EndsWith("log", StringComparison.OrdinalIgnoreCase);
                if (log) t = t[..^3];
                int slash = t.LastIndexOf('/');
                var range = slash > 0 ? Range(t[..slash]) : null;
                if (range is not { } r || !int.TryParse(t[(slash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 2
                    || (log && !(r.Start > 0)))
                    return Bad("<start>..<stop>/<points>, log appended for a logarithmic grid (1GHz..5GHz/81)");
                Grid = new AssemblyGrid(r.Start, r.Stop, n, log);
                break;
            }
            case "--format":
                Format = v.Trim().ToLowerInvariant() switch { "db" => MatrixFormat.DB, "ma" => MatrixFormat.MA, "ri" => MatrixFormat.RI, _ => null };
                if (Format is null) return Bad("db, ma or ri");
                break;
            case "--ports":
                if (!int.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int ports)
                    || ports < 1 || ports > TouchstoneAssembly.MaxPorts)
                    return Bad($"a whole number of ports from 1 to {TouchstoneAssembly.MaxPorts}");
                Ports = ports;
                break;
            case "--csv":
                Csv = v;
                break;
            case "--manual-box":
            {
                string t = v.Trim();
                double angle = 0;
                int at = t.IndexOf('@');
                if (at >= 0)
                {
                    string deg = t[(at + 1)..].Trim().TrimEnd('°');
                    if (deg.EndsWith("deg", StringComparison.OrdinalIgnoreCase)) deg = deg[..^3];
                    if (!double.TryParse(deg, NumberStyles.Float, CultureInfo.InvariantCulture, out angle) || !double.IsFinite(angle))
                        return Bad("x0px,y0px,x1px,y1px[@deg]: two opposite corners of the plot area, in pixels, turned counter-clockwise by deg");
                    t = t[..at];
                }
                var parts = t.Split(',', StringSplitOptions.TrimEntries);
                var c = new double[4];
                bool ok = parts.Length == 4;
                for (int k = 0; k < 4 && ok; k++)
                {
                    if (parts[k].Length > 0 && !char.IsLetter(parts[k][^1]))
                        return JsonRun.Fail(CliDiagnostics.PictureCoordinateNeedsPx(verb, a, parts[k]));
                    ok = parts[k].EndsWith("px", StringComparison.OrdinalIgnoreCase) && Px(parts[k]) is { } n && (c[k] = n) == n;
                }
                if (!ok || c[0] == c[2] || c[1] == c[3])
                    return Bad("x0px,y0px,x1px,y1px[@deg]: two opposite corners of the plot area, in pixels, turned counter-clockwise by deg");
                ManualBox = (v.Trim(), ManualQuad.Rectangle(c[0], c[1], c[2], c[3], angle));
                break;
            }
            case "--x-range":
            {
                var (text, log) = LogSuffix(v);
                int dots = text.IndexOf("..", StringComparison.Ordinal);
                var lo = dots > 0 ? RangeValue(text[..dots]) : null;
                var hi = dots > 0 ? RangeValue(text[(dots + 2)..]) : null;
                if (lo is not { } l || hi is not { } h || l.Frequency != h.Frequency)
                    return Bad("<low>..<high>[:log]: the values at the plot area's left and right edges — frequencies with their units "
                             + "(100MHz..10GHz), or plain numbers for an x that is not frequency");
                XRange = (new ManualAxis(l.Value, h.Value, log ? AxisScale.Log : AxisScale.Linear), h.Unit, h.Frequency);
                break;
            }
            case "--y-range" or "--y2-range":
            {
                var (text, log) = LogSuffix(v);
                int dots = text.IndexOf("..", StringComparison.Ordinal);
                bool ok = dots > 0;
                double lo = 0, hi = 0;
                ok = ok && double.TryParse(text[..dots].Trim().Replace('−', '-'), NumberStyles.Float, CultureInfo.InvariantCulture, out lo)
                        && double.TryParse(text[(dots + 2)..].Trim().Replace('−', '-'), NumberStyles.Float, CultureInfo.InvariantCulture, out hi)
                        && double.IsFinite(lo) && double.IsFinite(hi);
                if (!ok)
                    return Bad(a == "--y-range" ? "<low>..<high>[:log]: the values at the plot area's bottom and top edges (-40..0)"
                                                : "<low>..<high>[:log]: the right-hand scale's values at the bottom and top edges (-180..180)");
                var axis = new ManualAxis(lo, hi, log ? AxisScale.Log : AxisScale.Linear);
                if (a == "--y-range") YRange = axis; else Y2Range = axis;
                break;
            }
            case "--curve":
            {
                if (Curve(v) is not { } curve)
                    return Bad("<name>=<px>,<py>;<px>,<py>;…[,axis=left|right][,is=S21:db]: a curve's name and its points in pixels, "
                             + "optionally the y scale it is read on and what it is (data by default)");
                Curves.Add(curve);
                break;
            }
            case "--follow":
            {
                // <name>=<px>,<py>[,axis=left|right][,is=S11:db] — the same suffixes --curve takes, so a followed curve
                // reaches a Touchstone as a hand-drawn one does (R-emb5-11).
                int eq = v.IndexOf('=');
                var xy = eq > 0 ? v[(eq + 1)..].Split(',', StringSplitOptions.TrimEntries) : [];
                PlotSide? side = null;
                PlotAssignment? assignment = null;
                if (xy.Length < 2 || Px(xy[0]) is not { } fx || Px(xy[1]) is not { } fy || v[..eq].Trim().Length == 0
                    || !Suffixes(xy.Skip(2), ref side, ref assignment))
                    return Bad("<name>=<px>,<py>[,axis=left|right][,is=S11:db]: a curve's name and one click on it, in pixels — the curve is "
                             + "followed from there — optionally the y scale it is read on and what it is (data by default)");
                Follows.Add((v[..eq].Trim(), fx, fy, side, assignment));
                break;
            }
            case "--sample":
                if (!int.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int points) || points < 2)
                    return Bad("the number of x values each curve is sampled at, at least 2 (101)");
                SamplePoints = points;
                break;
            case "--sample-step":
            {
                string t = v.Trim();
                double? step = double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : RecognitionSweep.Parse(t)?.Hz;
                if (step is not { } st || !(st > 0) || !double.IsFinite(st))
                    return Bad("the step between x values, in the x axis's own units: a frequency with its unit (50MHz), or a number");
                SampleStep = st;
                break;
            }
            case "--method":
                Method = v.Trim().ToLowerInvariant() switch { "linear" => ManualSampleMethod.Linear, "smooth" => ManualSampleMethod.Smooth, _ => null };
                if (Method is null) return Bad("linear or smooth (a monotone cubic: it never overshoots between two points)");
                break;
            case "--keep-sources":
                switch (v.Trim().ToLowerInvariant())
                {
                    case "on" or "true" or "1": KeepSources = true; break;
                    case "off" or "false" or "0": KeepSources = false; break;
                    default: return Bad("on or off");
                }
                break;
            default:
                return null;
        }
        Given.Add(a);
        i++;
        return 0;
    }

    /// <summary>The reading's options these flags spell. <paramref name="factor"/> carries file pixels onto a reduced
    /// reading, as the trace's pixel flags are carried.</summary>
    public PlotReadingOptions ReadingOptions(Func<int, double> factor) => new()
    {
        Plots = Plots.Count > 0 ? [.. Plots] : null,
        Axes = [.. Clicks.Select(c => new PlotAxisOverride(c.Plot, c.Axis,
                    new AxisClicks(c.P0 * factor(c.Plot), c.V0, c.P1 * factor(c.Plot), c.V1,
                                   Scales.Any(s => s.Axis == c.Axis && (s.Plot == 0 || s.Plot == c.Plot) && s.Log) ? AxisScale.Log : AxisScale.Linear)))],
        Scales = [.. Scales],
        Traces = [.. Traces],
        SmithZ0 = SmithZ0,
        Frequencies = [.. Frequencies],
    };

    /// <summary>The file's options these flags spell.</summary>
    public TouchstoneAssemblyOptions AssemblyOptions() => new()
    {
        Phase = new Dictionary<(int, int), PhaseRule>(Phase),
        Fill = new Dictionary<(int, int), FillRule>(Fill),
        Prefer = new Dictionary<(int, int), string>(Prefer),
        Grid = Grid,
        Format = Format ?? MatrixFormat.DB,
        Ports = Ports,
        Fits = [.. Fits],
    };

    /// <summary>The plot drawn by hand these flags spell, on picture 0, in reading pixels (<paramref name="factor"/> carries
    /// file pixels onto a reduced reading). Null without a box and both ranges — the caller refuses that.</summary>
    public ManualPlot? ManualPlot(double factor)
    {
        if (ManualBox is not { } box || XRange is not { } x || YRange is not { } y) return null;
        var q = box.Quad;
        (double, double) F((double X, double Y) p) => (p.X * factor, p.Y * factor);
        return new ManualPlot(0, new ManualQuad(F(q.BottomLeft), F(q.BottomRight), F(q.TopRight), F(q.TopLeft)), x.Axis, y, Y2Range,
                              x.Frequency ? x.Unit : 1, x.Frequency)
        {
            Sampling = new ManualSampleSpec(Points: SamplePoints ?? ManualSampleSpec.DefaultPoints, Step: SampleStep,
                                            Method: Method ?? ManualSampleMethod.Linear),
        };
    }

    /// <summary>A default colour for a curve given only as points: a qualitative palette, by its place in the list.</summary>
    public static int CurveColour(int index) => Palette[index % Palette.Length];

    private static readonly int[] Palette = [0x1F77B4, 0xD62728, 0x2CA02C, 0x9467BD, 0xFF7F0E, 0x17BECF];

    // ── spellings ────────────────────────────────────────────────────────────

    /// <summary><c>text:log</c> → (text, true).</summary>
    private static (string Text, bool Log) LogSuffix(string v)
    {
        string t = v.Trim();
        if (t.EndsWith(":log", StringComparison.OrdinalIgnoreCase)) return (t[..^4], true);
        if (t.EndsWith(":lin", StringComparison.OrdinalIgnoreCase) || t.EndsWith(":linear", StringComparison.OrdinalIgnoreCase))
            return (t[..t.LastIndexOf(':')], false);
        return (t, false);
    }

    /// <summary>An x range end: a frequency with its unit (hertz, and the unit's multiplier), or a plain number.</summary>
    private static (double Value, double Unit, bool Frequency)? RangeValue(string s)
    {
        s = s.Trim().Replace('−', '-');
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d)) return (d, 1, false);
        if (RecognitionSweep.Parse(s) is not { } f || !double.IsFinite(f.Hz)) return null;
        return (f.Hz, f.Unit switch { "kHz" => 1e3, "MHz" => 1e6, "GHz" => 1e9, "THz" => 1e12, _ => 1 }, true);
    }

    /// <summary><c>S21=120,300;140,290[,axis=right][,is=S21:db]</c>.</summary>
    private static (string Name, List<(double X, double Y)> Points, PlotSide Axis, PlotAssignment Assignment)? Curve(string v)
    {
        int eq = v.IndexOf('=');
        if (eq <= 0) return null;
        string name = v[..eq].Trim();
        var points = new List<(double, double)>();
        var side = PlotSide.Left;
        var assignment = PlotAssignment.None;
        foreach (string part in v[(eq + 1)..].Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var bits = part.Split(',', StringSplitOptions.TrimEntries);
            int k = 0;
            if (bits.Length >= 2 && Px(bits[0]) is { } x && Px(bits[1]) is { } y) { points.Add((x, y)); k = 2; }
            PlotSide? s = side;
            PlotAssignment? a = assignment;
            if (!Suffixes(bits.Skip(k), ref s, ref a)) return null;
            side = s ?? side;
            assignment = a ?? assignment;
        }
        return name.Length == 0 ? null : (name, points, side, assignment);
    }

    /// <summary>A curve's <c>axis=left|right</c> and <c>is=S21:db</c> (or <c>is=data</c>) suffixes — shared by
    /// <c>--curve</c> and <c>--follow</c>. False on anything else.</summary>
    private static bool Suffixes(IEnumerable<string> bits, ref PlotSide? side, ref PlotAssignment? assignment)
    {
        foreach (string b in bits)
        {
            if (b.StartsWith("axis=", StringComparison.OrdinalIgnoreCase))
            {
                switch (b[5..].Trim().ToLowerInvariant())
                {
                    case "left": side = PlotSide.Left; break;
                    case "right": side = PlotSide.Right; break;
                    default: return false;
                }
            }
            else if (b.StartsWith("is=", StringComparison.OrdinalIgnoreCase))
            {
                string what = b[3..].Trim();
                if (what.Equals("data", StringComparison.OrdinalIgnoreCase)) assignment = PlotAssignment.None;
                else if (PlotQuantity.ParseAssignment(what) is { Assigned: true } got) assignment = got;
                else return false;
            }
            else return false;
        }
        return true;
    }

    /// <summary>The axis a flag names: <c>--x-…</c>, <c>--y-…</c>, <c>--y2-…</c>.</summary>
    private static PlotAxis AxisOf(string flag) =>
        flag.StartsWith("--y2-", StringComparison.Ordinal) ? PlotAxis.Y2 : flag.StartsWith("--y-", StringComparison.Ordinal) ? PlotAxis.Y : PlotAxis.X;

    /// <summary><c>text@n</c> → (text, n); no <c>@</c> is plot 1.</summary>
    private static (string Text, int? Plot) At(string v)
    {
        int at = v.LastIndexOf('@');
        if (at < 0) return (v, 1);
        return int.TryParse(v[(at + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 1 ? (v[..at], n) : (v, null);
    }

    private static double? Px(string s)
    {
        s = s.Trim();
        if (s.EndsWith("px", StringComparison.OrdinalIgnoreCase)) s = s[..^2];
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d) ? d : null;
    }

    /// <summary>An x value is a frequency with its unit (or a plain number, read as hertz); a y value is a plain number
    /// in the axis's own units.</summary>
    private static double? AxisValue(string s, PlotAxis axis)
    {
        s = s.Trim();
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d)) return d;
        return axis == PlotAxis.X && RecognitionSweep.Parse(s) is { } f ? f.Hz : null;
    }

    private static (double Start, double Stop)? Range(string s)
    {
        int dots = s.IndexOf("..", StringComparison.Ordinal);
        if (dots <= 0) return null;
        if (RecognitionSweep.Parse(s[..dots].Trim()) is not { } a || RecognitionSweep.Parse(s[(dots + 2)..].Trim()) is not { } b) return null;
        return b.Hz > a.Hz ? (a.Hz, b.Hz) : null;
    }

    private static PhaseRule? PhaseOf(string s)
    {
        s = s.Trim();
        switch (s.ToLowerInvariant())
        {
            case "min" or "minimum": return new PhaseRule(MissingPhase.MinimumPhase);
            case "zero": return new PhaseRule(MissingPhase.Zero);
        }
        if (!s.StartsWith("delay:", StringComparison.OrdinalIgnoreCase)) return null;
        return Seconds(s[6..]) is { } t ? new PhaseRule(MissingPhase.Delay, t) : null;
    }

    /// <summary>A time with its unit: <c>250ps</c>, <c>1.2ns</c>, <c>3e-10s</c>.</summary>
    private static double? Seconds(string s)
    {
        s = s.Trim();
        (string Suffix, double Scale)[] units = [("ps", 1e-12), ("ns", 1e-9), ("us", 1e-6), ("µs", 1e-6), ("ms", 1e-3), ("fs", 1e-15), ("s", 1)];
        foreach (var (suffix, scale) in units)
            if (s.EndsWith(suffix, StringComparison.Ordinal)
                && double.TryParse(s[..^suffix.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v))
                return v * scale;
        return null;
    }

    private static FillRule? FillOf((int I, int J) at, string s)
    {
        s = s.Trim();
        switch (s.ToLowerInvariant())
        {
            case "zero" or "matched": return FillRule.Zero;
            case "default": return FillRule.Default;
            case "unassigned": return FillRule.Unassigned;
            case "reciprocal": return FillRule.Reciprocal;
            case "symmetric": return FillRule.Symmetric;
        }
        if (s.StartsWith('=')) s = s[1..];
        if (PlotQuantity.Entry(s) is not { } from) return null;
        if (from == (at.J, at.I) && at.I != at.J) return FillRule.Reciprocal;
        if (at.I == at.J && from.I == from.J && from != at) return FillRule.Symmetric;
        return null;
    }
}
