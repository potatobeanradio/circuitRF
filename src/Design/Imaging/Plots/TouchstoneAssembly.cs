// A session's traces as one Touchstone file — brief-img-14-sparameter-plots-to-touchstone.md R-im14-9, R-im14-11.
//
// PORTS are the largest index any assigned trace names (S11 alone → .s1p, S21 → .s2p), up to four.
//
// THE GRID is where every trace is: from the overlap of the traces' frequency ranges, at the densest trace's pixel
// resolution, rounded to a 1-2-5 step (or log-spaced when that trace's axis is). A grid point outside any trace's own
// range is never extrapolated — the range shrinks, and the report says which trace bounded it.
//
// RESAMPLING keeps each quantity in the space it was drawn in: dB linearly in dB, phase unwrapped then linearly, a
// chart's Γ in the complex plane, and along log f where the frequency axis was logarithmic. A trace given a FIT
// (brief-pdg-2-curve-fit.md R-pdg2-3) is not resampled: its fit is evaluated at each grid frequency in its place, in the
// same plotted units, and everything after that point is unchanged. A refused fit leaves the trace as read and says so.
//
// WHAT IS NOT PLOTTED IS NEVER A SILENT GUESS (overview D6 applied here). Each missing phase and each missing entry
// takes a stated default, recorded in the file's header and shown in the dialog's matrix grid one click from being
// changed: a magnitude with no phase takes the minimum phase it implies (an estimate, so labelled); a missing S12 is S21
// when |S21| stays at or below 0 dB (a passive, reciprocal part) and zero when it does not (an amplifier's isolation is
// not plotted); a missing S22 is S11 (symmetric). Three- and four-port matrices take zero for every unplotted entry.
//
// THE FILE IS REFUSED only while a trace is unassigned, a chart trace has no frequency, or an axis is uncalibrated —
// each refusal names the control (and the flag) that answers it.

using System.Globalization;
using System.Numerics;
using NumFlat;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Design.Imaging.Plots;

/// <summary>What a magnitude with no phase is given (R-im14-9).</summary>
public enum MissingPhase { MinimumPhase, Zero, Delay }

/// <summary>A missing phase rule; <paramref name="DelaySeconds"/> for <see cref="MissingPhase.Delay"/>.</summary>
public sealed record PhaseRule(MissingPhase Rule, double DelaySeconds = 0)
{
    public static PhaseRule Default { get; } = new(MissingPhase.MinimumPhase);

    /// <summary>The <c>--phase</c> spelling: <c>min</c>, <c>zero</c>, <c>delay:&lt;t&gt;</c>.</summary>
    public override string ToString() => Rule switch
    {
        MissingPhase.Zero => "zero",
        MissingPhase.Delay => "delay:" + DelaySeconds.ToString("G6", CultureInfo.InvariantCulture) + "s",
        _ => "min",
    };
}

/// <summary>What an unplotted entry is (R-im14-9).</summary>
public enum FillRule
{
    /// <summary>The stated default for the entry.</summary>
    Default,
    /// <summary>Sij = Sji.</summary>
    Reciprocal,
    /// <summary>S22 = S11 (S11 = S22).</summary>
    Symmetric,
    Zero,
    /// <summary>Left for the user: the file waits.</summary>
    Unassigned,
}

/// <summary>The output frequency grid (R-im14-9).</summary>
public sealed record AssemblyGrid(double StartHz, double StopHz, int Points, bool Log)
{
    public double[] Frequencies()
    {
        var f = new double[Points];
        for (int i = 0; i < Points; i++)
        {
            double u = Points == 1 ? 0 : (double)i / (Points - 1);
            f[i] = Log ? StartHz * Math.Pow(StopHz / StartHz, u) : StartHz + u * (StopHz - StartHz);
        }
        return f;
    }

    /// <summary>The <c>--grid</c> spelling: <c>1GHz..5GHz/81</c>, with <c>log</c> appended when logarithmic.</summary>
    public override string ToString() =>
        $"{Hz(StartHz)}..{Hz(StopHz)}/{Points.ToString(CultureInfo.InvariantCulture)}{(Log ? "log" : "")}";

    internal static string Hz(double f)
    {
        var (div, unit) = f >= 1e9 ? (1e9, "GHz") : f >= 1e6 ? (1e6, "MHz") : f >= 1e3 ? (1e3, "kHz") : (1, "Hz");
        return (f / div).ToString("0.######", CultureInfo.InvariantCulture) + unit;
    }
}

/// <summary>Everything the user may change about the file.</summary>
public sealed record TouchstoneAssemblyOptions
{
    /// <summary>Per entry (i, j): the missing-phase rule; absent is minimum phase.</summary>
    public IReadOnlyDictionary<(int I, int J), PhaseRule> Phase { get; init; } = new Dictionary<(int, int), PhaseRule>();

    /// <summary>Per unplotted entry: what it is; absent is the stated default.</summary>
    public IReadOnlyDictionary<(int I, int J), FillRule> Fill { get; init; } = new Dictionary<(int, int), FillRule>();

    /// <summary>Per entry plotted twice: <c>chart</c> or <c>rect</c> (or a trace name, <c>T3</c>) — which is written.
    /// Absent: a chart for a reflection entry (it carries phase), else the rectangular traces.</summary>
    public IReadOnlyDictionary<(int I, int J), string> Prefer { get; init; } = new Dictionary<(int, int), string>();

    /// <summary>The grid; null is the stated default (R-im14-9).</summary>
    public AssemblyGrid? Grid { get; init; }

    /// <summary>The file's ports, 1 to <see cref="TouchstoneAssembly.MaxPorts"/>; null is the highest port any trace names.
    /// A trace naming a port beyond it waits, as an unassigned one does (PTU-7: a picture's misread label made a 2-port
    /// plot a 4-port file nobody could finish).</summary>
    public int? Ports { get; init; }

    public MatrixFormat Format { get; init; } = MatrixFormat.DB;

    /// <summary>The fits asked of traces (<c>--fit</c>, the traces table's Fit combo); a trace none names is resampled.</summary>
    public IReadOnlyList<PlotFitOverride> Fits { get; init; } = [];
}

/// <summary>Where one entry of the matrix came from — the dialog's matrix grid and the header both say it.</summary>
/// <param name="Source"><c>T2 (plot 1)</c>, <c>= S21</c>, <c>zero</c>, <c>unassigned</c>.</param>
/// <param name="Rule">The rule applied, in words (<c>reciprocal: |S21| ≤ 0 dB throughout</c>), or empty.</param>
/// <param name="Phase">How its phase was set when it was not drawn: <c>min phase</c>, <c>zero</c>, <c>delay</c>; empty
/// when drawn.</param>
/// <param name="Plotted">It was drawn (any trace), not filled.</param>
/// <param name="Estimated">Its phase is a minimum-phase estimate.</param>
public sealed record MatrixCell(int I, int J, string Source, string Rule, string Phase, bool Plotted, bool Estimated);

/// <summary>Why the file is not written yet, and what answers it.</summary>
/// <param name="Kind"><c>axis</c>, <c>unassigned</c>, <c>frequency</c>, <c>fill</c>, <c>range</c>, <c>empty</c>.</param>
/// <param name="Flag">The CLI flag that answers it.</param>
/// <param name="Control">The dialog control that answers it.</param>
public sealed record AssemblyRefusal(string Kind, string Sentence, string Flag, string Control);

public sealed record TouchstoneAssemblyResult(
    SNP? Snp, AssemblyRefusal? Refusal, int Ports, IReadOnlyList<MatrixCell> Matrix, AssemblyGrid? Grid,
    IReadOnlyList<string> Header, IReadOnlyList<string> Report, TouchstoneHealthReport? Health, MatrixFormat Format)
{
    public bool Ok => Snp is not null;

    /// <summary>The fits made, by trace name — every piece of a joined trace under its own name (R-pdg2-3); refused ones
    /// too. Set whether or not the file was refused.</summary>
    public IReadOnlyDictionary<string, TraceFitResult> Fits { get; init; } = new Dictionary<string, TraceFitResult>();

    /// <summary>The file's extension, <c>.s2p</c>.</summary>
    public string Extension => $".s{Ports}p";
}

public static class TouchstoneAssembly
{
    public const int MaxPorts = 4;

    /// <summary>Builds the file the session's traces describe. Pure and deterministic.</summary>
    public static TouchstoneAssemblyResult Build(PlotSession session, TouchstoneAssemblyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var o = options ?? new TouchstoneAssemblyOptions();
        var report = new List<string>();
        // The fits first, so a refused file still reports them (R-pdg2-3).
        var fits = TraceFit.Session(session, o.Fits);
        foreach (var fit in fits.Values.Distinct()) report.Add(TraceFit.Line(fit));
        TouchstoneAssemblyResult Refuse(string kind, string sentence, string flag, string control, int ports = 0, IReadOnlyList<MatrixCell>? matrix = null) =>
            new(null, new AssemblyRefusal(kind, sentence, flag, control), ports, matrix ?? [], null, [], report, null, o.Format) { Fits = fits };

        // A curve placed by hand and left as plain data is not waited for: it is a CSV's, never the file's (R-pdg4-4).
        var traces = session.Traces.Where(t => !t.Assignment.Ignored && !(t.Manual && !t.Assignment.Assigned)
                                               && !session.Plots[t.Plot - 1].Excluded).ToList();
        // ── refusals: an uncalibrated axis, an unassigned trace, a chart trace with no frequency ──
        foreach (var p in session.Plots.Where(p => !p.Excluded && traces.Any(t => t.Plot == p.Number)))
        {
            // The x axis, and every y axis one of its traces is read against (R-pdg1-2).
            if (p.Rect is { } r && (r.X is null || traces.Any(t => t.Plot == p.Number && r.AxisOn(t.Axis) is null)))
            {
                string axis = r.X is null ? "x" : RectAxes.Name(r.AxisNamed(traces.First(t => t.Plot == p.Number && r.AxisOn(t.Axis) is null).Axis));
                return Refuse("axis", $"plot {p.Number}'s {axis} axis is not calibrated: fewer than two of its tick labels were read",
                              $"--{axis}-axis (or read the plot by hand: --manual-box with --x-range and --y-range)",
                              $"the {axis} axis of plot {p.Number}: two clicks and two values");
            }
            if (p.Rect is { XIsFrequency: false } nf)
                return Refuse("axis", $"plot {p.Number}'s x axis is not frequency ({nf.XTitle}): it digitises (CSV) but makes no Touchstone",
                              "--csv", "Make: CSV");
        }
        foreach (var t in traces.Where(t => !t.Assignment.Assigned))
            return Refuse("unassigned", $"{t.Name} (plot {t.Plot}, {t.Curve.Hex} {t.Curve.StyleName}) is unassigned: {t.Evidence}",
                          $"--trace \"{t.Name}={(t.Parametric ? "S11" : "S21:db")}\"", $"the entry and quantity of {t.Name} in the traces table");
        foreach (var t in traces.Where(t => t.Parametric && t.Frequency is null))
            return Refuse("frequency", $"{t.Name} (plot {t.Plot}) has no frequency: no markers with readouts and no rectangular trace of "
                                       + $"{t.Assignment.Entry} to match", $"--freq {t.Name}=<start>..<stop>",
                          $"the start and stop of {t.Name} in the frequency row");
        if (traces.Count == 0) return Refuse("empty", "no trace was read that could be written", "--trace", "the traces table");

        int ports = traces.Max(t => Math.Max(t.Assignment.I, t.Assignment.J));
        if (o.Ports is { } given)
        {
            if (traces.FirstOrDefault(t => Math.Max(t.Assignment.I, t.Assignment.J) > given) is { } beyond)
                return Refuse("unassigned", $"{beyond.Name} (plot {beyond.Plot}, {beyond.Curve.Hex} {beyond.Curve.StyleName}) is "
                                            + $"{beyond.Assignment.Entry}, beyond the file's {given} port{(given == 1 ? "" : "s")}",
                              $"--trace \"{beyond.Name}=S21:db\"", $"the entry of {beyond.Name} in the traces table, or Ports");
            ports = given;
        }
        if (ports > MaxPorts)
            return Refuse("unassigned", $"a trace names port {ports}; a Touchstone from plots is up to {MaxPorts} ports", "--trace", "the traces table");

        // ── each trace as frequency → value, sorted, in its own resampling space ──
        var series = traces.Select(t => Series.Of(t, session.Plots[t.Plot - 1], fits.GetValueOrDefault(t.Name))).Where(s => s.F.Length >= 2).ToList();
        if (series.Count == 0) return Refuse("empty", "no trace has two points to resample", "--trace", "the traces table");

        // An entry drawn only as a phase (or only one of re/im) has no magnitude to put the phase on.
        foreach (var e in series.GroupBy(s => (s.Trace.Assignment.I, s.Trace.Assignment.J)))
        {
            var a = e.Select(s => s.Trace.Assignment).ToList();
            bool whole = e.Any(s => s.Chart) || a.Any(x => x.IsMagnitude)
                         || (a.Any(x => x.Quantity == PlotQuantityKind.Real) && a.Any(x => x.Quantity == PlotQuantityKind.Imag));
            if (!whole)
                return Refuse("magnitude", $"S{e.Key.Item1}{e.Key.Item2} is drawn only as {string.Join(" and ", a.Select(x => PlotQuantity.Name(x.Quantity)).Distinct())}"
                                           + $" ({string.Join(", ", e.Select(s => s.Trace.Name))}): add the picture of its magnitude, or assign the trace otherwise",
                              "--trace", "the traces table");
        }

        // ── the grid ──
        double lo = series.Max(s => s.F[0]), hi = series.Min(s => s.F[^1]);
        var loBy = series.First(s => s.F[0] == lo).Trace;
        var hiBy = series.First(s => s.F[^1] == hi).Trace;
        if (!(hi > lo))
            return Refuse("range", "the traces' frequency ranges do not overlap: there is no frequency every trace has", "--grid", "the output grid");
        var densest = series.OrderByDescending(s => s.F.Count(f => f >= lo && f <= hi)).First();
        AssemblyGrid grid;
        if (o.Grid is { } g)
        {
            double a = Math.Max(g.StartHz, lo), b = Math.Min(g.StopHz, hi);
            if (!(b > a)) return Refuse("range", $"the grid {g} lies outside the traces' common range {AssemblyGrid.Hz(lo)}..{AssemblyGrid.Hz(hi)}",
                                        "--grid", "the output grid");
            grid = g with { StartHz = a, StopHz = b };
            if (a > g.StartHz || b < g.StopHz) report.Add($"grid narrowed to {grid}: the traces have nothing outside {AssemblyGrid.Hz(lo)}..{AssemblyGrid.Hz(hi)}");
        }
        // Curves placed by hand were sampled on the grid the user asked for (R-pdg4-3): that grid is the file's, as it is.
        // Its spacing is the spec's, not the axis's: a step grid is linear, and a typed Log overrides the axis.
        else if (series.All(s => s.Trace.Manual))
            grid = new AssemblyGrid(lo, hi, Math.Max(2, densest.F.Count(f => f >= lo && f <= hi)),
                                    session.Plots[densest.Trace.Plot - 1].Manual?.Sampling is { } spec
                                        ? !(spec.Step > 0) && (spec.Log ?? densest.Log) : densest.Log);
        else grid = DefaultGrid(lo, hi, densest.F.Count(f => f >= lo && f <= hi), densest.Log);
        report.Add($"grid {grid} (the common range starts at {loBy.Name}'s first point and ends at {hiBy.Name}'s last)");
        var freqs = grid.Frequencies();
        int n = freqs.Length;

        // ── per entry ──
        var values = new Dictionary<(int, int), Complex[]>();
        var cells = new Dictionary<(int, int), MatrixCell>();
        var header = new List<string>();
        for (int i = 1; i <= ports; i++)
            for (int j = 1; j <= ports; j++)
            {
                var mine = series.Where(s => s.Trace.Assignment.I == i && s.Trace.Assignment.J == j).ToList();
                if (mine.Count == 0) continue;
                var (v, cell) = Entry(i, j, mine, freqs, o, report);
                values[(i, j)] = v;
                cells[(i, j)] = cell;
            }

        // ── the entries nobody drew ── a fill copies a DRAWN entry, never another fill: with neither S11 nor S22 drawn,
        // S22 is not "= S11" of a zero S11 was given a moment before.
        var drawn = new Dictionary<(int, int), Complex[]>(values);
        for (int i = 1; i <= ports; i++)
            for (int j = 1; j <= ports; j++)
            {
                if (values.ContainsKey((i, j))) continue;
                o.Fill.TryGetValue((i, j), out var rule);
                if (rule == FillRule.Default) rule = DefaultFill(i, j, ports, drawn);
                string why = "";
                Complex[]? v = null;
                string source;
                switch (rule)
                {
                    case FillRule.Reciprocal when drawn.TryGetValue((j, i), out var t):
                        v = t;
                        source = $"= S{j}{i}";
                        why = t.All(c => c.Magnitude <= 1 + 1e-9) ? $"reciprocal: |S{j}{i}| <= 0 dB throughout" : "reciprocal";
                        break;
                    case FillRule.Symmetric when ports == 2 && i == j && drawn.TryGetValue((3 - i, 3 - i), out var d):
                        v = d;
                        source = $"= S{3 - i}{3 - i}";
                        why = "symmetric";
                        break;
                    case FillRule.Unassigned:
                        cells[(i, j)] = new MatrixCell(i, j, "unassigned", "", "", false, false);
                        return Refuse("fill", $"S{i}{j} is not plotted and is left unassigned", $"--fill S{i}{j}=zero",
                                      $"S{i}{j} in the matrix grid", ports, Cells(ports, cells));
                    case FillRule.Reciprocal or FillRule.Symmetric:
                        return Refuse("fill", $"S{i}{j} cannot be {rule.ToString().ToLowerInvariant()}: the entry it would copy is not plotted",
                                      $"--fill S{i}{j}=zero", $"S{i}{j} in the matrix grid", ports, Cells(ports, cells));
                    default:
                        v = new Complex[n];
                        source = "zero";
                        why = i != j && drawn.ContainsKey((j, i)) ? $"|S{j}{i}| rises above 0 dB: an amplifier's isolation is not plotted" : "not plotted";
                        break;
                }
                values[(i, j)] = v!;
                cells[(i, j)] = new MatrixCell(i, j, source, why, "", false, false);
            }

        // ── the SNP ──
        var mats = new Mat<Complex>[n];
        for (int k = 0; k < n; k++)
        {
            mats[k] = new Mat<Complex>(ports, ports);
            for (int i = 1; i <= ports; i++)
                for (int j = 1; j <= ports; j++)
                    mats[k][i - 1, j - 1] = values[(i, j)][k];
        }
        var smithZ0 = session.Plots.Where(p => p.Smith is not null && !p.Excluded && traces.Any(t => t.Plot == p.Number))
                                   .Select(p => p.Smith!.Z0).Distinct().ToList();
        double z0 = smithZ0.Count > 0 ? smithZ0[0] : SmithFrame.DefaultZ0;
        if (smithZ0.Count > 1) report.Add($"the Smith charts state different Z0s ({string.Join(", ", smithZ0)}); the file is referenced to {z0} ohm");
        var snp = new SNP(freqs, mats, MatrixType.S, o.Format, new Complex(z0, 0)) { FreqUnit = UnitFor(grid.StopHz) };

        // ── the header (R-im14-11) ──
        header.Add("Digitised by circuitRF from plot pictures (Create Touchstone from Plot)");
        foreach (var p in session.Pictures)
            header.Add($"picture {p.Index + 1}: {Ascii(p.Name)} sha256 {p.Sha256} ({p.Width} x {p.Height} px)");
        foreach (var p in session.Plots.Where(p => !p.Excluded && traces.Any(t => t.Plot == p.Number)))
            header.Add($"plot {p.Number} (picture {p.Picture + 1}): {Calibration(p)}");
        foreach (var t in session.Traces)
        {
            if (t.Assignment.Ignored) { header.Add($"{t.Name} (plot {t.Plot}, {t.Curve.Hex}): ignored"); continue; }
            string freq = t.Frequency is { } fr ? $"; frequency from {fr.Source}, direction by {fr.Direction}" : "";
            string axis = t.AxisEvidence.Length == 0 ? ""
                : $"; {PlotReading.Side(t.Axis)} y axis ({PlotReading.AxisEvidenceText(t.AxisEvidence)})";
            header.Add($"{t.Name} (plot {t.Plot}, {t.Curve.Hex} {t.Curve.StyleName}): {t.Assignment} from the {t.Assignment.Evidence}; "
                       + $"{t.Samples.Count} points; resolution {Fmt(t.Resolution)} {Ascii(t.ResolutionUnit)} per pixel{freq}{axis}");
        }
        foreach (var c in Cells(ports, cells))
        {
            if (c.Plotted) { if (c.Phase.Length > 0) header.Add($"S{c.I}{c.J} phase: {c.Rule}"); continue; }
            header.Add($"S{c.I}{c.J}: {c.Source}{(c.Rule.Length > 0 ? $" ({c.Rule})" : "")}");
        }
        header.Add($"grid: {grid}");
        foreach (string line in report) header.Add(Ascii(line));
        foreach (string line in header) snp.Comments.Add(new CommentEntry(-1, Ascii(line)));

        var health = TouchstoneHealth.Analyze(snp);
        report.Add(HealthLine(health));
        return new TouchstoneAssemblyResult(snp, null, ports, Cells(ports, cells), grid, header, report, health, o.Format) { Fits = fits };
    }

    /// <summary>Writes the file as Touchstone 1.1 (R-im14-11). <paramref name="dated"/> adds the time line
    /// TouchstoneIO writes — the one line two otherwise identical runs differ in.</summary>
    public static void Write(TouchstoneAssemblyResult result, TextWriter writer, bool dated = true)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Snp is not { } snp) throw new InvalidOperationException(result.Refusal?.Sentence ?? "Nothing to write.");
        TouchstoneIO.Write(snp, writer, result.Format, writeComments: true, touchstone11Compatible: true, precision: "G10",
                           includeDateComment: dated);
    }

    /// <summary>
    /// Writes the file to <paramref name="path"/> (atomically) and, when <paramref name="keepSources"/>, keeps each
    /// source picture beside it as <c>&lt;name&gt;.source.&lt;ext&gt;</c>, <c>.source-2</c> … (D4) — the one write Create,
    /// the CLI's <c>-o</c> and the MCP tool all make (R-im14-15). The extension must be the one the reading makes.
    /// </summary>
    /// <returns>The kept copies of the pictures, in session order (empty when not kept).</returns>
    public static IReadOnlyList<KeptPicture> WriteFile(TouchstoneAssemblyResult result, string path, IReadOnlyList<PlotPictureInput> pictures,
                                                       bool keepSources = true, bool dated = true)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(pictures);
        if (!string.Equals(System.IO.Path.GetExtension(path), result.Extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"A {result.Ports}-port reading is written as {result.Extension}, not {System.IO.Path.GetExtension(path)}.", nameof(path));
        string full = System.IO.Path.GetFullPath(path);
        string dir = System.IO.Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);
        CircuitRF.Design.Cells.AtomicFile.WriteAllText(full, Text(result, dated));
        if (!keepSources) return [];
        string stem = System.IO.Path.GetFileNameWithoutExtension(full);
        return [.. pictures.Select(p => ImageKeep.Into(dir, stem, p.Source))];
    }

    /// <summary>The file's text.</summary>
    public static string Text(TouchstoneAssemblyResult result, bool dated = true)
    {
        using var w = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" };
        Write(result, w, dated);
        return w.ToString();
    }

    /// <summary>The health check's verdict in one line (R-im14-10, R-im14-11).</summary>
    public static string HealthLine(TouchstoneHealthReport h)
    {
        string passive = double.IsNaN(h.WorstSigmaMax) ? "passivity not measured"
            : h.WorstSigmaMax <= 1 + TouchstoneHealth.PassivityTolerance ? "passive"
            : $"active (sigma max {Fmt(h.WorstSigmaMax)} at {AssemblyGrid.Hz(h.WorstSigmaMaxFrequencyHz)})";
        string recip = double.IsNaN(h.WorstReciprocityError) ? "" : h.WorstReciprocityError <= TouchstoneHealth.ReciprocityTolerance
            ? "; reciprocal" : $"; not reciprocal (worst |Sij - Sji| {Fmt(h.WorstReciprocityError)})";
        string causal = h.Causality switch
        {
            CausalityVerdict.Consistent => "; causality consistent",
            CausalityVerdict.PrecursorEnergyHigh => $"; pre-cursor energy high ({Fmt(100 * h.CausalityPrecursorRatio)} %)",
            _ => $"; causality not evaluated ({h.CausalitySkipReason})",
        };
        return $"health: {passive}{recip}{causal}";
    }

    // ── internals ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A trace as sorted frequency → value, in the space it resamples in; <paramref name="Fit"/> evaluated in place
    /// of the resampling when it was made.</summary>
    private sealed record Series(PlotTraceRead Trace, double[] F, double[] V, Complex[] C, bool Log, bool Chart, TraceFitResult? Fit = null)
    {
        public static Series Of(PlotTraceRead t, PlotRead p, TraceFitResult? fit)
        {
            bool log = p.Rect?.X?.Scale == AxisScale.Log;
            var pts = t.Samples.Where(s => !double.IsNaN(s.X) && s.X > 0).OrderBy(s => s.X).ToList();
            // One value per frequency: the exact (marker) one where there is one, else the mean.
            var merged = pts.GroupBy(s => s.X).Select(g => g.FirstOrDefault(s => s.Exact) is { Exact: true } e ? e
                                                       : g.First() with { Y = g.Average(s => s.Y), Value = Mean(g.Select(s => s.Value)) }).ToList();
            var f = merged.Select(s => s.X).ToArray();
            return new Series(t, f, Space(t.Assignment, [.. merged.Select(s => s.Y)]), [.. merged.Select(s => s.Value)], log, t.Parametric,
                              fit is { Fitted: true } && !t.Parametric ? fit : null);
        }

        /// <summary>Plotted values in the space they resample in: dB for a magnitude, unwrapped radians for a phase.</summary>
        private static double[] Space(PlotAssignment a, double[] y) =>
            a.IsMagnitude ? [.. y.Select(v => 20 * Math.Log10(Math.Max(1e-12, PlotQuantity.Magnitude(a.Quantity, v))))]
            : a.IsPhase ? Unwrap([.. y.Select(v => PlotQuantity.Radians(a.Quantity, v))])
            : y;

        private static Complex Mean(IEnumerable<Complex> c)
        {
            var l = c.ToList();
            return l.Count == 0 ? Complex.Zero : new Complex(l.Average(x => x.Real), l.Average(x => x.Imaginary));
        }

        public double[] At(double[] freqs) => Fit is { } fit
            // The grid lies inside every trace's range; the clamp only answers rounding at its ends.
            ? Space(Trace.Assignment, [.. freqs.Select(f => TraceFit.Evaluate(fit, Math.Clamp(f, fit.XMin, fit.XMax)))])
            : [.. freqs.Select(f => Interp(F, V, f, Log))];

        public Complex[] ComplexAt(double[] freqs) =>
            [.. freqs.Select(f => new Complex(Interp(F, [.. C.Select(c => c.Real)], f, Log), Interp(F, [.. C.Select(c => c.Imaginary)], f, Log)))];
    }

    private static double[] Unwrap(double[] p)
    {
        var r = (double[])p.Clone();
        for (int i = 1; i < r.Length; i++)
        {
            double d = r[i] - r[i - 1];
            r[i] -= 2 * Math.PI * Math.Round(d / (2 * Math.PI));
        }
        return r;
    }

    private static double Interp(double[] x, double[] y, double at, bool log)
    {
        if (at <= x[0]) return y[0];
        if (at >= x[^1]) return y[^1];
        int k = Array.BinarySearch(x, at);
        if (k >= 0) return y[k];
        k = ~k;
        double x0 = x[k - 1], x1 = x[k];
        double u = log ? Math.Log(at / x0) / Math.Log(x1 / x0) : (at - x0) / (x1 - x0);
        return y[k - 1] + u * (y[k] - y[k - 1]);
    }

    private static (Complex[] Values, MatrixCell Cell) Entry(int i, int j, List<Series> mine, double[] freqs, TouchstoneAssemblyOptions o,
                                                            List<string> report)
    {
        var chart = mine.Where(s => s.Chart).ToList();
        var mag = mine.Where(s => !s.Chart && s.Trace.Assignment.IsMagnitude).ToList();
        var phase = mine.Where(s => !s.Chart && s.Trace.Assignment.IsPhase).ToList();
        var re = mine.FirstOrDefault(s => !s.Chart && s.Trace.Assignment.Quantity == PlotQuantityKind.Real);
        var im = mine.FirstOrDefault(s => !s.Chart && s.Trace.Assignment.Quantity == PlotQuantityKind.Imag);
        string entry = $"S{i}{j}";
        string Names(IEnumerable<Series> ss) => string.Join(" + ", ss.Select(s => $"{s.Trace.Name} (plot {s.Trace.Plot})"));

        o.Prefer.TryGetValue((i, j), out var prefer);
        bool useChart = chart.Count > 0 && (prefer switch
        {
            "chart" or "smith" or "polar" => true,
            "rect" or "rectangular" => mag.Count == 0 && (re is null || im is null),
            { } name when name.StartsWith('T') => chart.Any(c => string.Equals(c.Trace.Name, name, StringComparison.OrdinalIgnoreCase)),
            _ => i == j || mag.Count == 0,
        });

        Complex[]? fromChart = chart.Count > 0 ? chart[0].ComplexAt(freqs) : null;
        Complex[]? fromRect = null;
        string phaseRule = "";
        bool estimated = false;
        if (re is not null && im is not null)
        {
            var r = re.At(freqs);
            var m = im.At(freqs);
            fromRect = [.. r.Select((x, k) => new Complex(x, m[k]))];
        }
        else if (mag.Count > 0)
        {
            var db = mag[0].At(freqs);
            var lin = db.Select(d => Math.Pow(10, d / 20)).ToArray();
            double[] ph;
            if (phase.Count > 0) ph = phase[0].At(freqs);
            else
            {
                o.Phase.TryGetValue((i, j), out var rule);
                rule ??= PhaseRule.Default;
                switch (rule.Rule)
                {
                    case MissingPhase.Zero:
                        ph = new double[freqs.Length];
                        phaseRule = "zero";
                        break;
                    case MissingPhase.Delay:
                        ph = [.. freqs.Select(f => -2 * Math.PI * f * rule.DelaySeconds)];
                        phaseRule = $"linear, delay {rule.DelaySeconds.ToString("G4", CultureInfo.InvariantCulture)} s";
                        break;
                    default:
                    {
                        var mp = MinimumPhase.FromMagnitude(freqs, lin);
                        ph = mp.Phase;
                        estimated = true;
                        phaseRule = $"min phase (estimated; trustworthy {AssemblyGrid.Hz(mp.TrustedFromHz)} to {AssemblyGrid.Hz(mp.TrustedToHz)})";
                        break;
                    }
                }
            }
            fromRect = [.. lin.Select((a, k) => Complex.FromPolarCoordinates(a, ph[k]))];
        }

        // Two readings of one entry are checked against each other (R-im14-9).
        if (fromChart is not null && fromRect is not null)
        {
            double worst = 0;
            for (int k = 0; k < freqs.Length; k++) worst = Math.Max(worst, Math.Abs(fromChart[k].Magnitude - fromRect[k].Magnitude));
            report.Add($"{entry}: the chart and the rectangular plot differ by up to {Fmt(worst)} in |S|; "
                       + $"{(useChart ? "the chart" : "the rectangular plot")} is written (--prefer {entry}={(useChart ? "rect" : "chart")} changes it)");
        }
        if (useChart || fromRect is null)
            return (fromChart!, new MatrixCell(i, j, Names(chart.Take(1)), "", "", true, false));
        var used = mag.Take(1).Concat(phase.Take(1)).Concat(re is null ? [] : [re]).Concat(im is null ? [] : [im]);
        string cellPhase = estimated ? "min phase" : phaseRule.Length > 0 ? phaseRule.Split(' ')[0].TrimEnd(',') : "";
        return (fromRect, new MatrixCell(i, j, Names(used), phaseRule, cellPhase, true, estimated));
    }

    private static FillRule DefaultFill(int i, int j, int ports, Dictionary<(int, int), Complex[]> plotted)
    {
        if (ports > 2) return FillRule.Zero;
        if (i != j)
            return plotted.TryGetValue((j, i), out var t) && t.All(c => c.Magnitude <= 1 + 1e-9) ? FillRule.Reciprocal : FillRule.Zero;
        return plotted.ContainsKey((3 - i, 3 - i)) ? FillRule.Symmetric : FillRule.Zero;
    }

    private static AssemblyGrid DefaultGrid(double lo, double hi, int points, bool log)
    {
        points = Math.Max(2, points);
        if (log)
        {
            double a = Round(lo, up: true), b = Round(hi, up: false);
            if (!(b > a)) (a, b) = (lo, hi);
            return new AssemblyGrid(a, b, points, true);
        }
        double raw = (hi - lo) / (points - 1);
        double step = Tidy(raw);
        double start = Math.Ceiling(lo / step - 1e-9) * step, stop = Math.Floor(hi / step + 1e-9) * step;
        int n = (int)Math.Round((stop - start) / step) + 1;
        if (n < 2) return new AssemblyGrid(lo, hi, 2, false);
        return new AssemblyGrid(Clean(start), Clean(stop), n, false);
    }

    /// <summary>The largest 1-2-5 step not above <paramref name="v"/>.</summary>
    internal static double Tidy(double v)
    {
        double p = Math.Pow(10, Math.Floor(Math.Log10(v)));
        double m = v / p;
        return (m >= 5 ? 5 : m >= 2 ? 2 : 1) * p;
    }

    /// <summary>Three significant digits, rounded inward.</summary>
    private static double Round(double v, bool up)
    {
        double p = Math.Pow(10, Math.Floor(Math.Log10(v)) - 2);
        return (up ? Math.Ceiling(v / p - 1e-9) : Math.Floor(v / p + 1e-9)) * p;
    }

    private static double Clean(double v) => double.Parse(v.ToString("G12", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private static FrequencyUnit UnitFor(double hz) => hz >= 1e9 ? FrequencyUnit.GHz : hz >= 1e6 ? FrequencyUnit.MHz : hz >= 1e3 ? FrequencyUnit.kHz : FrequencyUnit.Hz;

    private static IReadOnlyList<MatrixCell> Cells(int ports, Dictionary<(int, int), MatrixCell> cells)
    {
        var list = new List<MatrixCell>();
        for (int i = 1; i <= ports; i++)
            for (int j = 1; j <= ports; j++)
                if (cells.TryGetValue((i, j), out var c)) list.Add(c);
        return list;
    }

    internal static string Calibration(PlotRead p) => p.Type switch
    {
        PlotType.Rectangular when p.Rect is { X: { } x } r =>
            $"rectangular; x {Axis(x)}; y{(r.YSide == PlotSide.Right ? " (right)" : "")} {(r.Y is { } y ? Axis(y) : "uncalibrated")}"
            + (r.Y2 is { } y2 ? $"; y2 (right) {Axis(y2)}" : ""),
        PlotType.Smith when p.Smith is { } s =>
            $"Smith{(s.Admittance ? " (admittance)" : "")}; circle residual {Fmt(s.ResidualPx)} px; Z0 {Fmt(s.Z0)} ohm{(s.Z0Stated ? "" : " (assumed)")}",
        PlotType.Polar when p.Polar is { } pl => $"polar; {(pl.Db ? "dB" : "linear")} radius {Fmt(pl.InnerValue)} to {Fmt(pl.OuterValue)}",
        _ => PlotKind.Name(p.Type),
    };

    private static string Axis(AxisCalibration a) =>
        $"{a.Scale.ToString().ToLowerInvariant()}, " + (a.Manual ? "typed" : $"{a.Used} labels, residual {Fmt(a.ResidualPx)} px");

    internal static string Fmt(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>Touchstone is ASCII: the few non-ASCII signs a sentence here may carry are spelled out.</summary>
    internal static string Ascii(string s)
    {
        var b = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
            b.Append(c switch
            {
                'Γ' => "Gamma", '°' => " deg", 'Ω' => " ohm", '±' => "+/-", '≤' => "<=", '≥' => ">=", '—' or '–' => "-", '…' => "...",
                '×' => "x", 'µ' or 'μ' => "u", '∠' => "/",
                _ => c < 128 ? c.ToString() : "?",
            });
        // A unit's own leading space after a value's (0.5 ° per pixel) is one space, not two.
        return b.ToString().Replace("  deg", " deg").Replace("  ohm", " ohm");
    }
}
