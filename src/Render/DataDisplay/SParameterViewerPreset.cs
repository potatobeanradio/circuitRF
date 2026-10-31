// ================================================================
//  SParameterViewerPreset.cs  —  the S-Parameter Viewer's four plots
//
//  A quick look at one Touchstone file: the reflection terms on a Smith
//  chart, the transmission terms on a polar chart, and every chosen term
//  as magnitude in dB and as phase against frequency. The window shows
//  them, `circuitrf snp -o picture` draws them and `circuitrf snp -o
//  display.cdd` / Save as Data Display write them — all four from this
//  composition, so they cannot disagree.
//
//  THE FILE'S OWN AXIS, NOT A NICE ONE. The frequency axis is pinned to
//  exactly the file's lowest and highest point, and a log-spaced file
//  gets a log axis. Every trace is a network trace, which draws the
//  file's own samples and nothing between them; the sample markers make
//  the step visible.
//
//  THIS COMPOSES, IT DOES NOT DRAW AND IT COMPUTES NOTHING: every trace
//  is one a user could build on the Data Display's own trace card.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CircuitRF.Diagnostics;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Render.DataDisplay;

public static class SParameterViewerPreset
{
    /// <summary>The tab a saved display opens on.</summary>
    public const string TabName = "S-parameters";

    /// <summary>Above this port count the default selection is the reflections and the forward
    /// neighbours rather than every term — an <c>.s16p</c> has 256.</summary>
    public const int AllTermsUpToPorts = 4;

    /// <summary>The ports the default selection reaches. A file can have hundreds, and the reflections and
    /// neighbours of all of them would open as hundreds of curves no one can tell apart.</summary>
    public const int DefaultPortsShown = 8;

    /// <summary>The phase axis' fixed span, degrees: the whole of what a wrapped phase can be.</summary>
    public const double PhaseLimitDeg = 180.0;

    // The Data Display's own default sizes — a square plot 420, a rectangular one 520 at the golden
    // ratio — so the text, which is laid out in absolute sizes, reads the same here as there.
    private const double Gap = 24, SmithSize = 420, RectWidth = 520, RectHeight = 321;

    /// <summary>Above this many terms a rectangular plot draws no legend: sixteen entries cover the curves they
    /// name, and the viewer's tick boxes carry each term's colour anyway.</summary>
    public const int LegendUpToTerms = 6;

    /// <summary>The four plots, in the order they are laid out on a saved Data Display.</summary>
    public enum PlotKind { Smith, Polar, Magnitude, Phase }

    /// <summary>One placed plot and what it is.</summary>
    public sealed record ComposedPlot(PlotKind Kind, PlotContainerConfig Config);

    /// <summary>One term of the matrix, zero-based.</summary>
    public readonly record struct Term(int Row, int Col)
    {
        /// <summary>True on the diagonal.</summary>
        public bool IsReflection => Row == Col;
    }

    // ── naming and choosing terms ─────────────────────────────────────────────

    /// <summary><c>S21</c> below ten ports, <c>S(10,1)</c> from ten up, where <c>S101</c> would be ambiguous.</summary>
    public static string Name(Term t, int ports)
        => ports < 10 ? $"S{t.Row + 1}{t.Col + 1}" : $"S({t.Row + 1},{t.Col + 1})";

    /// <summary>Every term, row-major.</summary>
    public static IReadOnlyList<Term> AllTerms(int ports)
    {
        var all = new List<Term>(ports * ports);
        for (int i = 0; i < ports; i++)
            for (int j = 0; j < ports; j++)
                all.Add(new Term(i, j));
        return all;
    }

    /// <summary>
    /// What opens ticked: every term up to <see cref="AllTermsUpToPorts"/> ports; above that the
    /// reflections and each forward neighbour S(i+1,i) of the first <see cref="DefaultPortsShown"/> ports.
    /// </summary>
    public static IReadOnlyList<Term> DefaultSelection(int ports)
    {
        if (ports <= AllTermsUpToPorts) return AllTerms(ports);
        int shown = Math.Min(ports, DefaultPortsShown);
        var sel = new List<Term>();
        for (int i = 0; i < shown; i++)
        {
            sel.Add(new Term(i, i));
            if (i + 1 < shown) sel.Add(new Term(i + 1, i));
        }
        return Ordered(sel, ports);
    }

    /// <summary>
    /// Reads a selection as typed: comma-separated <c>S21</c> or <c>S(2,1)</c> terms, or the words
    /// <c>all</c>, <c>reflections</c> and <c>default</c>. Null with <paramref name="error"/> set for
    /// a term the file does not have.
    /// </summary>
    public static IReadOnlyList<Term>? ParseSelection(string text, int ports, out Diagnostic? error)
    {
        error = null;
        var sel = new List<Term>();
        foreach (string raw in SplitTerms(text))
        {
            switch (raw.ToLowerInvariant())
            {
                case "all":         sel.AddRange(AllTerms(ports)); continue;
                case "default":     sel.AddRange(DefaultSelection(ports)); continue;
                case "reflections": for (int i = 0; i < ports; i++) sel.Add(new Term(i, i)); continue;
            }
            if (ParseTerm(raw, ports) is not { } t)
            {
                error = TouchstoneDiagnostics.NoSuchParameter(raw, ports);
                return null;
            }
            sel.Add(t);
        }
        return Ordered(sel, ports);
    }

    /// <summary>The list split on its top-level commas: the one inside <c>S(2,1)</c> is part of the term.</summary>
    private static IEnumerable<string> SplitTerms(string text)
    {
        int depth = 0, start = 0;
        for (int k = 0; k <= text.Length; k++)
        {
            char c = k < text.Length ? text[k] : ',';
            if (c == '(') depth++;
            else if (c == ')') depth = Math.Max(0, depth - 1);
            else if (c == ',' && depth == 0)
            {
                string term = text[start..k].Trim();
                if (term.Length > 0) yield return term;
                start = k + 1;
            }
        }
    }

    private static Term? ParseTerm(string raw, int ports)
    {
        string s = raw.Trim();
        if (s.Length < 3 || char.ToUpperInvariant(s[0]) != 'S') return null;
        string body = s[1..].Trim();
        int i, j;
        if (body.StartsWith('(') && body.EndsWith(')'))
        {
            var parts = body[1..^1].Split(',');
            if (parts.Length != 2
                || !int.TryParse(parts[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out i)
                || !int.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out j))
                return null;
        }
        else if (ports < 10 && body.Length == 2 && char.IsAsciiDigit(body[0]) && char.IsAsciiDigit(body[1]))
        {
            i = body[0] - '0';
            j = body[1] - '0';
        }
        else return null;
        return i >= 1 && j >= 1 && i <= ports && j <= ports ? new Term(i - 1, j - 1) : null;
    }

    /// <summary>Row-major, each term once — so a selection reads the same however it was typed.</summary>
    private static IReadOnlyList<Term> Ordered(IEnumerable<Term> terms, int ports)
        => [.. terms.Distinct().OrderBy(t => t.Row * ports + t.Col)];

    // ── the file pane ─────────────────────────────────────────────────────────

    /// <summary>
    /// What the file states, one labelled line each — the viewer's file pane and <c>circuitrf snp</c>'s
    /// report are both these lines, so a terminal and a window cannot spell a file differently.
    /// </summary>
    public static IReadOnlyList<(string Label, string Value)> FileFacts(TouchstoneFacts f)
    {
        var inv = CultureInfo.InvariantCulture;
        var rows = new List<(string, string)>
        {
            ("Ports", f.ExtensionPorts is { } n && n != f.Ports ? $"{f.Ports} (the name says {n})" : f.Ports.ToString(inv)),
            ("Parameters", $"{f.Type}, {f.Format} ({FormatName(f.Format)})"),
            ("Unit", f.FrequencyUnit.ToString()),
            ("Points", f.FrequencyCount.ToString(inv)),
            ("Start", Hz(f.FirstFrequencyHz)),
            ("Stop", Hz(f.LastFrequencyHz)),
            ("DC point", f.HasDcPoint ? "Yes" : "No ⚠"),
            ("Spacing", Spacing(f)),
            ("Reference", Reference(f)),
            ("Noise data", f.NoisePoints > 0 ? $"{f.NoisePoints} point(s)" : "none"),
        };
        if (f.CommentLines > 0) rows.Add(("Comments", f.CommentLines == 1 ? "1 line" : $"{f.CommentLines} lines"));
        return rows;
    }

    /// <summary>How the points are spaced, in words.</summary>
    public static string Spacing(TouchstoneFacts f) => f.Grid switch
    {
        FrequencyGrid.SinglePoint => "one point",
        FrequencyGrid.Uniform     => $"uniform, step {Hz(f.StepHz)}",
        FrequencyGrid.Logarithmic => $"logarithmic, {f.PointsPerDecade.ToString("0.#", CultureInfo.InvariantCulture)} per decade",
        _ => f.FrequenciesOutOfOrder
            ? $"not increasing from point {f.FirstNonIncreasingIndex + 1}"
            : $"irregular, {Hz(f.SmallestStepHz)} to {Hz(f.LargestStepHz)}",
    };

    /// <summary>A frequency with an SI prefix.</summary>
    public static string Hz(double hz) =>
        !double.IsFinite(hz) ? "?"
        : Math.Abs(hz) >= 1e9 ? $"{(hz / 1e9).ToString("0.######", CultureInfo.InvariantCulture)} GHz"
        : Math.Abs(hz) >= 1e6 ? $"{(hz / 1e6).ToString("0.######", CultureInfo.InvariantCulture)} MHz"
        : Math.Abs(hz) >= 1e3 ? $"{(hz / 1e3).ToString("0.######", CultureInfo.InvariantCulture)} kHz"
        : $"{hz.ToString("0.######", CultureInfo.InvariantCulture)} Hz";

    private static string FormatName(MatrixFormat m) => m switch
    {
        MatrixFormat.RI => "real, imaginary",
        MatrixFormat.DB => "dB, angle",
        _               => "magnitude, angle",
    };

    private static string Reference(TouchstoneFacts f)
        => f.Z0PerPort is { } perPort && perPort.Distinct().Count() > 1
            ? string.Join(", ", perPort.Select((z, i) => $"{i + 1}: {Ohms(z)}"))
            : Ohms(f.Z0);

    /// <summary>
    /// Γ at one Smith-chart sample: rectangular, then magnitude and angle.
    /// <c>Γ=21+j30 (36.62 ∠ 55.01°)</c>.
    /// </summary>
    public static string Gamma(System.Numerics.Complex g)
    {
        var inv = CultureInfo.InvariantCulture;
        if (!double.IsFinite(g.Real) || !double.IsFinite(g.Imaginary)) return "Γ=—";
        string sign = g.Imaginary < 0 ? "-" : "+";
        double deg = g.Phase * 180.0 / Math.PI;
        return $"Γ={g.Real.ToString("0.####", inv)}{sign}j{Math.Abs(g.Imaginary).ToString("0.####", inv)} " +
               $"({g.Magnitude.ToString("0.00", inv)} ∠ {deg.ToString("0.##", inv)}°)";
    }

    /// <summary>
    /// The Smith hover: the term at its frequency, <see cref="Gamma"/>, then the impedance as
    /// <c>Z=…</c>, ending in Ω.
    /// </summary>
    public static string SmithHover(string term, double hz, System.Numerics.Complex gamma, string? impedance)
    {
        string head = double.IsFinite(hz) ? $"{term} @ {Hz(hz)}" : term;
        if (string.IsNullOrEmpty(impedance)) return head + "\n" + Gamma(gamma);
        return head + "\n" + Gamma(gamma) + "\n" + AsZ(impedance);
    }

    /// <summary>
    /// The marker's impedance sentence, spelled <c>Z=…</c> and ending in Ω.
    /// <c>impedance=Z0*(1+j0)</c> becomes <c>Z=Z0*(1+j0) Ω</c>.
    /// </summary>
    public static string AsZ(string impedance)
    {
        string body = impedance;
        if (body.StartsWith("impedance", StringComparison.OrdinalIgnoreCase))
            body = body["impedance".Length..];
        body = body.TrimStart();
        if (body.StartsWith('='))
            body = "Z=" + body[1..].TrimStart();
        else
            body = "Z " + body;
        if (!body.EndsWith("Ω", StringComparison.Ordinal))
            body += " Ω";
        return body;
    }

    private static string Ohms(System.Numerics.Complex z) =>
        Math.Abs(z.Imaginary) < 1e-12
            ? $"{z.Real.ToString("0.###", CultureInfo.InvariantCulture)} Ω"
            : $"{z.Real.ToString("0.###", CultureInfo.InvariantCulture)}{z.Imaginary.ToString("+0.###;-0.###", CultureInfo.InvariantCulture)}j Ω";

    // ── the plots ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The engineering unit the file's highest frequency reads best in. Not the option line's: a file
    /// written in Hz is still a 1 to 5 GHz sweep, and an axis of 1E+09 … 5E+09 says so badly.
    /// </summary>
    public static FreqUnit DisplayUnit(TouchstoneFacts facts)
    {
        double top = Math.Max(Math.Abs(facts.FirstFrequencyHz), Math.Abs(facts.LastFrequencyHz));
        return !double.IsFinite(top) ? FreqUnit.GHz
             : top >= 1e12 ? FreqUnit.THz
             : top >= 1e9  ? FreqUnit.GHz
             : top >= 1e6  ? FreqUnit.MHz
             : top >= 1e3  ? FreqUnit.kHz
             :               FreqUnit.Hz;
    }

    /// <summary>
    /// The four plots over <paramref name="source"/>, every trace bound to its path, laid out as the window lays
    /// out all four: the Smith and polar charts across the top, magnitude and phase across the bottom.
    /// </summary>
    /// <remarks>
    /// <b>Each round chart is given the room it draws in, not just its disc.</b> A Data Display grows a Smith
    /// or polar canvas upward for its title and downward for the frequency row, and draws the axis-label
    /// strips to the left of the disc, outside the canvas. Those extents are measured here by the functions
    /// the Data Display and <c>circuitrf render</c> place them by, so two plots cannot be written overlapping.
    /// </remarks>
    /// <param name="colorIndexOf">The palette index each term is drawn in. Null takes
    /// <see cref="ColorIndexOf"/> — its place in the selection, which suits a one-shot picture; the viewer
    /// passes its own, so a term keeps its colour while others are ticked and unticked.</param>
    public static IReadOnlyList<ComposedPlot> Compose(TouchstoneSource source, IReadOnlyList<Term> selection,
                                                      Func<Term, int>? colorIndexOf = null)
    {
        colorIndexOf ??= t => ColorIndexOf(t, selection);
        var unit  = DisplayUnit(source.Facts);
        string sourceRef = source.Path;
        string freqLabel = FrequencyLabel(source, unit);

        var reflections   = selection.Where(t => t.IsReflection).ToList();
        var transmissions = selection.Where(t => !t.IsReflection).ToList();

        var smith = Round(PlotType.Smith, "Reflection", unit, freqLabel, reflections,
            [.. reflections.Select(t => Network(t, colorIndexOf(t), sourceRef, DependentVarFormat.Complex))]);
        var polar = Round(PlotType.Polar, "Transmission", unit, freqLabel, transmissions,
            [.. transmissions.Select(t => Network(t, colorIndexOf(t), sourceRef, DependentVarFormat.Complex))]);

        var magnitude = Rect("Magnitude", "Magnitude (dB)", unit,
            [.. selection.Select(t => Network(t, colorIndexOf(t), sourceRef, DependentVarFormat.Db))],
            FrequencyAxis(source, unit, phase: false));
        var phase = Rect("Phase", "Phase (deg)", unit,
            [.. selection.Select(t => Network(t, colorIndexOf(t), sourceRef, DependentVarFormat.Phase))],
            FrequencyAxis(source, unit, phase: true));

        // ── the page ──
        var (smithAbove, smithBelow, smithStrips) = RoundExtents(smith, reflections.Count);
        var (polarAbove, polarBelow, polarStrips) = RoundExtents(polar, transmissions.Count);
        double above = Math.Max(smithAbove, polarAbove);
        double below = Math.Max(smithBelow, polarBelow);

        double topRow    = smithStrips + SmithSize + Gap + polarStrips + SmithSize;
        double bottomRow = RectWidth + Gap + RectWidth;
        double topIndent    = Gap + Math.Max(0, (bottomRow - topRow) / 2);
        double bottomIndent = Gap + Math.Max(0, (topRow - bottomRow) / 2);

        smith.Left = topIndent + smithStrips;
        polar.Left = smith.Left + SmithSize + Gap + polarStrips;
        smith.Top  = polar.Top = Gap + above;

        magnitude.Left = bottomIndent;
        phase.Left     = bottomIndent + RectWidth + Gap;
        magnitude.Top  = phase.Top = smith.Top + SmithSize + below + Gap;

        return
        [
            new ComposedPlot(PlotKind.Smith, smith),
            new ComposedPlot(PlotKind.Polar, polar),
            new ComposedPlot(PlotKind.Magnitude, magnitude),
            new ComposedPlot(PlotKind.Phase, phase),
        ];
    }

    /// <summary>
    /// A Smith or polar chart. It names its terms in the strips beside the disc, as a Data Display chart does,
    /// one per term up to <see cref="LegendUpToTerms"/>; past that one strip lists the first few, since a strip
    /// per term would run off the page. Every term shares one frequency range, so one row says it, not one per term.
    /// </summary>
    private static PlotContainerConfig Round(PlotType type, string title, FreqUnit unit, string freqLabel,
                                             IReadOnlyList<Term> terms, List<TraceConfig> traces)
    {
        bool oneStrip = terms.Count > LegendUpToTerms;
        return new PlotContainerConfig
        {
            PlotType = type, FreqUnit = unit,
            Width = SmithSize, Height = SmithSize,
            CustomTitle = title, CustomTitleOn = true,
            CustomXLabel = freqLabel, CustomXLabelOn = true,
            CustomYLabel = oneStrip ? TermList(terms) : "", CustomYLabelOn = oneStrip,
            Legend = LegendPlacement.Off,
            Traces = traces,
        };
    }

    /// <summary>How far a round chart's title reaches above its canvas, its frequency row below it, and its
    /// label strips to the left of it.</summary>
    private static (double Above, double Below, double Left) RoundExtents(PlotContainerConfig pc, int terms)
    {
        var probe = new Plot(pc.PlotType, pc.FreqUnit)
        {
            CustomTitle = pc.CustomTitle, CustomTitleOn = pc.CustomTitleOn,
            CustomXLabel = pc.CustomXLabel, CustomXLabelOn = pc.CustomXLabelOn,
            Legend = pc.Legend,
        };
        double above = PlotCanvasGeometry.TopLabelExtraLogical(probe, pc.Width);
        double below = PlotCanvasGeometry.BottomLabelExtraLogical(probe, pc.Width);
        int strips = terms == 0 ? 0 : pc.CustomYLabelOn ? 1 : terms;
        double sw = PlotCanvasGeometry.StripLogicalWidth(pc.Height);
        double inset = PlotCanvasGeometry.ChartInset(probe, pc.Width, pc.Height + above + below);
        double left = strips == 0 ? 0 : Math.Max(0, strips * sw + PlotCanvasGeometry.StripGap(sw) - inset);
        return (above, below, left);
    }

    /// <summary>The first few names, then how many there are: <c>S(1,2), S(1,3), S(1,4), S(2,1) … 12 terms</c> —
    /// spelled as the chart spells a trace, so the strip reads like the ones it stands in for.</summary>
    private static string TermList(IReadOnlyList<Term> terms)
    {
        const int named = 4;
        string head = string.Join(", ", terms.Take(named).Select(t => $"S({t.Row + 1},{t.Col + 1})"));
        return terms.Count > named ? $"{head} … {terms.Count} terms" : head;
    }

    /// <summary>The row a Data Display chart writes under its disc for each trace, said once.</summary>
    private static string FrequencyLabel(TouchstoneSource source, FreqUnit unit)
    {
        var f = source.AsRead.Frequencies;
        if (f.Length == 0) return "freq";
        double scale = unit.Scale();
        return $"freq ({(f.Min() * scale).ToString("G4", CultureInfo.InvariantCulture)} to " +
               $"{(f.Max() * scale).ToString("G4", CultureInfo.InvariantCulture)} {unit.Description()})";
    }

    /// <summary>The plots as a Data Display document: one tab, its source the file itself.</summary>
    public static DataDisplayConfig Build(TouchstoneSource source, IReadOnlyList<Term> selection,
                                          Func<Term, int>? colorIndexOf = null) => new()
    {
        FormatVersion      = DataDisplayConfig.CurrentFormatVersion,
        SelectedDataSource = source.Path,
        Tabs = [new TabConfig { Name = TabName, Plots = [.. Compose(source, selection, colorIndexOf).Select(p => p.Config)] }],
    };

    /// <summary>
    /// The frequency axis: pinned to the file's own lowest and highest point, Y left to autoscale
    /// (magnitude) or fixed to ±180° (phase). A one-point file has no span to pin and autoscales.
    /// </summary>
    private static AxesConfig FrequencyAxis(TouchstoneSource source, FreqUnit unit, bool phase)
    {
        var f = source.AsRead.Frequencies;
        double lo = f.Length > 0 ? f.Min() * unit.Scale() : 0;
        double hi = f.Length > 0 ? f.Max() * unit.Scale() : 0;
        bool pinX = hi > lo;
        return new AxesConfig
        {
            AutoscaleX      = !pinX,
            AutoscaleY      = !phase,
            AutoscaleRightY = true,
            AutoscaleMag    = true,
            WindowX         = pinX ? lo : 0,
            WindowWidth     = pinX ? hi - lo : 0,
            WindowY         = phase ? -PhaseLimitDeg : 0,
            WindowHeight    = phase ? 2 * PhaseLimitDeg : 0,
            XScale          = source.Facts.Grid == FrequencyGrid.Logarithmic && lo > 0 ? AxisScale.Log : AxisScale.Linear,
        };
    }

    private static PlotContainerConfig Rect(string title, string yLabel, FreqUnit unit,
                                            List<TraceConfig> traces, AxesConfig axes) => new()
    {
        PlotType = PlotType.Rect, FreqUnit = unit,
        Width = RectWidth, Height = RectHeight,
        CustomTitle = title, CustomTitleOn = true,
        CustomYLabel = yLabel, CustomYLabelOn = true,
        Legend = traces.Count <= LegendUpToTerms ? LegendPlacement.Inside : LegendPlacement.Off,
        Traces = traces,
        Axes = axes,
    };

    /// <summary>One network trace, in palette slot <paramref name="c"/>.</summary>
    private static TraceConfig Network(Term t, int c, string sourceRef, DependentVarFormat y) => new()
    {
        SourcePath = sourceRef,
        MatrixType = MatrixType.S,
        Row        = t.Row,
        Col        = t.Col,
        YAxis      = y,
        Properties = new TracePropertiesConfig { LineColorIndex = c, MarkerColorIndex = c },
    };

    /// <summary>
    /// The palette slot a term is drawn in: its place in the selection, so the terms on show take the
    /// palette's first, most distinct colours and one term has one colour on all four plots.
    /// </summary>
    public static int ColorIndexOf(Term t, IReadOnlyList<Term> selection)
    {
        int k = Math.Max(0, IndexOf(selection, t));
        return TraceProperties.LineColorOrder[k % TraceProperties.LineColorOrder.Length];
    }

    private static int IndexOf(IReadOnlyList<Term> selection, Term t)
    {
        for (int k = 0; k < selection.Count; k++) if (selection[k] == t) return k;
        return -1;
    }
}
