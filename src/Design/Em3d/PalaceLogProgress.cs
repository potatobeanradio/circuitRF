// brief-em3d-21 R-em3d21-1 — progress from Palace's and Gmsh's own log lines.
//
// Three pieces, kept apart so each can be tested alone:
//
//   PalaceLogProgress   one line in, zero or one event out. No process, no file, no clock — the gate
//                       feeds it F0's committed logs (testdata/em3d/f0/*/palace*/palace.log, Palace
//                       0.18.1) with nothing installed. Every pattern is anchored on that wording.
//   PalaceStageTracker  events -> RunControl: the stage a user reads, and the stage fraction where
//                       Palace stated a total. Also the run's summary fields, from the same events.
//   GmshLogProgress     Gmsh's own "Meshing 3D" / "Done meshing 3D" lines and its final count.
//
// THE RULE: every figure shown is one Palace printed, and a fraction is shown only where Palace
// stated the total. Refinement passes are numbered as Palace runs them (a setup allowing N passes
// makes up to N + 1 solves; the log's "AMR iteration k" line comes AFTER solve k). The sampling phase
// has no total, so it shows the greedy error converging on its tolerance on a log scale — labelled as
// convergence, never as a percentage done. The online phase ("It k/N") has a total and shows it.
//
// UNKNOWN WORDING DEGRADES, IT NEVER LIES (R-em3d21-1d). A log with no mesh summary in its first
// MeshSummaryWindow lines, a line that opens like a known one but does not parse, or a phase that ends
// without one of the lines it always prints, turns the parser off for the rest of the run: the
// tracker falls back to an indeterminate stage counting lines, and the run's notes say the log format
// was not recognised. That is the path a new Palace version takes before it is validated.

using System.Globalization;
using System.Text.RegularExpressions;
using CircuitRF.Engine;

namespace CircuitRF.Design.Em3d;

/// <summary>One thing Palace's log said.</summary>
public abstract record PalaceLogEvent;

/// <summary>The mesh summary's <c>elements</c> row: the total tetrahedra over every rank.</summary>
public sealed record PalaceMeshElements(long Elements) : PalaceLogEvent;

/// <summary>The Nédélec unknown count, <c>ND (p = n): N</c>.</summary>
public sealed record PalaceUnknowns(int Order, long Count) : PalaceLogEvent;

/// <summary><c>Adaptive mesh refinement (AMR) iteration k:</c> — printed after solve k, before solve k + 1.</summary>
public sealed record PalaceRefinementIteration(int Iteration) : PalaceLogEvent;

/// <summary>
/// <c>Adding excitation index k (i/n):</c> (sampling) or <c>Sweeping excitation index k (i/n):</c>
/// (frequencies): the i-th of n port excitations begins. Palace prints these only when there is more
/// than one excitation — and circuitRF excites every port, so any multi-port run has them.
/// </summary>
public sealed record PalaceExcitation(int Index, int Ordinal, int Count, bool Sampling) : PalaceLogEvent;

/// <summary><c>Greedy iteration k (n = m): … error = e</c> — one adaptive-sampling step.</summary>
public sealed record PalaceGreedyIteration(int Iteration, int Dimension, double FrequencyGHz, double Error) : PalaceLogEvent;

/// <summary><c>Adaptive sampling converged with n frequency samples</c> (or <c>reached maximum</c>).</summary>
public sealed record PalaceSamplingDone(int Samples, bool Converged) : PalaceLogEvent;

/// <summary><c>It k/N: ω/2π = f GHz</c> — one frequency of the online phase, or of a sweep with no sampling.</summary>
public sealed record PalaceFrequency(int Index, int Count, double FrequencyGHz) : PalaceLogEvent;

/// <summary>The closing <c>Completed k iterations of adaptive mesh refinement (AMR)</c> block.</summary>
public sealed record PalaceRefinementDone(int Iterations, double Indicator, long Unknowns, int MaxIterations,
                                          double Tolerance) : PalaceLogEvent;

/// <summary>An <c>Elapsed Time Report</c> table's <c>Total</c> row: the slowest rank's seconds.</summary>
public sealed record PalaceElapsed(double Seconds) : PalaceLogEvent;

/// <summary>A <c>Peak Memory</c> table's <c>Total</c> row: its <c>Total HWM</c> column, in bytes.</summary>
public sealed record PalacePeakMemory(double Bytes) : PalaceLogEvent;

/// <summary>The log stopped matching: from here on the parser emits nothing.</summary>
public sealed record PalaceLogUnrecognised(long Line, string Reason) : PalaceLogEvent;

/// <summary>
/// R-em3d21-1a — one line of Palace's log in, zero or one event out. Stateful only across the
/// lines of a multi-line block (the tables, the closing refinement block) and for the checks that
/// turn it off; it holds no process, file or clock.
/// </summary>
public sealed class PalaceLogProgress
{
    /// <summary>How many lines may pass before the first mesh summary. F0's logs print it by line 34,
    /// after the banner, the device lines and the partitioning note.</summary>
    public const int MeshSummaryWindow = 400;

    private const string Num = @"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?";

    private static readonly Regex Elements   = new(@"^\s*elements\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex Nd         = new(@"\bND \(p = (\d+)\): (\d+)", RegexOptions.CultureInvariant);
    private static readonly Regex Amr        = new(@"^Adaptive mesh refinement \(AMR\) iteration (\d+):\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex Greedy     = new($@"^Greedy iteration (\d+) \(n = (\d+)\): \S+ = ({Num}) GHz \({Num}\), error = ({Num}|inf|nan)\b", RegexOptions.CultureInvariant);
    private static readonly Regex Sampling   = new(@"^Adaptive sampling (converged with|reached maximum) (\d+) frequency samples:\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex Excitation = new(@"^(Adding|Sweeping) excitation index (\d+) \((\d+)/(\d+)\):\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex Online     = new($@"^It (\d+)/(\d+): \S+ = ({Num}) GHz\b", RegexOptions.CultureInvariant);
    private static readonly Regex Completed  = new(@"^Completed (\d+) iterations? of adaptive mesh refinement \(AMR\):\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex Indicator  = new($@"^\s*Indicator norm = ({Num}), global unknowns = (\d+)", RegexOptions.CultureInvariant);
    private static readonly Regex MaxIts     = new($@"^\s*Max\. iterations = (\d+), tol\. = ({Num})", RegexOptions.CultureInvariant);
    private static readonly Regex TimeTotal  = new($@"^Total\s+({Num})\s+({Num})\s+({Num})\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex MemTotal   = new($@"^Total\s+({Num})([KMGT]?)\s+({Num})([KMGT]?)\s+({Num})([KMGT]?)\s*$", RegexOptions.CultureInvariant);

    private enum Block { None, TimeTable, MemoryTable, CompletedIndicator, CompletedMaxIts }

    private long _line;
    private bool _off, _sawMesh;
    private Block _block;
    private int _blockLines;
    private int _completedIts;
    private double _completedIndicator;
    private long _completedUnknowns;
    // A phase header seen, and whether the lines it always prints have come.
    private bool _inOffline, _offlineSpoke, _inOnline, _onlineSpoke;

    /// <summary>True once the log stopped matching (R-em3d21-1d).</summary>
    public bool Unrecognised => _off;

    /// <summary>Lines read so far.</summary>
    public long Lines => _line;

    public PalaceLogEvent? Feed(string? raw)
    {
        if (raw is null) return null;
        _line++;
        if (_off) return null;
        string line = raw.TrimEnd('\r');

        if (!_sawMesh && _line > MeshSummaryWindow)
            return Off($"no mesh summary (an 'elements' row) in the first {MeshSummaryWindow} lines");

        switch (_block)
        {
            case Block.TimeTable or Block.MemoryTable:
                if (++_blockLines > 60) return Off("a timing or memory table had no 'Total' row");
                if (!line.StartsWith("Total", StringComparison.Ordinal)) return null;
                var kind = _block;
                _block = Block.None;
                if (kind == Block.TimeTable)
                {
                    var t = TimeTotal.Match(line);
                    return t.Success ? new PalaceElapsed(D(t.Groups[2].Value)) : Off($"unreadable 'Total' row: “{line.Trim()}”");
                }
                var m = MemTotal.Match(line);
                return m.Success ? new PalacePeakMemory(Bytes(m.Groups[5].Value, m.Groups[6].Value))
                                 : Off($"unreadable 'Total' row: “{line.Trim()}”");
            case Block.CompletedIndicator:
            {
                var i = Indicator.Match(line);
                if (!i.Success) return Off($"the refinement summary's indicator line is unreadable: “{line.Trim()}”");
                (_completedIndicator, _completedUnknowns, _block) = (D(i.Groups[1].Value), long.Parse(i.Groups[2].Value, CultureInfo.InvariantCulture), Block.CompletedMaxIts);
                return null;
            }
            case Block.CompletedMaxIts:
            {
                var i = MaxIts.Match(line);
                _block = Block.None;
                return i.Success
                    ? new PalaceRefinementDone(_completedIts, _completedIndicator, _completedUnknowns,
                                               int.Parse(i.Groups[1].Value, CultureInfo.InvariantCulture), D(i.Groups[2].Value))
                    : Off($"the refinement summary's limits line is unreadable: “{line.Trim()}”");
            }
        }

        string t0 = line.TrimStart();

        if (t0.StartsWith("elements", StringComparison.Ordinal))
        {
            var e = Elements.Match(line);
            if (!e.Success) return Off($"unreadable mesh summary row: “{t0}”");
            _sawMesh = true;
            return new PalaceMeshElements(long.Parse(e.Groups[4].Value, CultureInfo.InvariantCulture));
        }
        if (line.Contains("ND (p", StringComparison.Ordinal))
        {
            var n = Nd.Match(line);
            return n.Success
                ? new PalaceUnknowns(int.Parse(n.Groups[1].Value, CultureInfo.InvariantCulture), long.Parse(n.Groups[2].Value, CultureInfo.InvariantCulture))
                : Off($"unreadable unknown count: “{t0}”");
        }
        if (line.StartsWith("Beginning PROM construction offline phase", StringComparison.Ordinal))
        {
            (_inOffline, _offlineSpoke) = (true, false);
            return null;
        }
        if (line.StartsWith("Beginning fast frequency sweep online phase", StringComparison.Ordinal))
        {
            if (_inOffline && !_offlineSpoke) return Off("the sampling phase ended without a greedy iteration or its summary");
            (_inOffline, _inOnline, _onlineSpoke) = (false, true, false);
            return null;
        }
        if (line.StartsWith("Adding excitation", StringComparison.Ordinal) ||
            line.StartsWith("Sweeping excitation", StringComparison.Ordinal))
        {
            var x = Excitation.Match(line);
            return x.Success
                ? new PalaceExcitation(I(x.Groups[2].Value), I(x.Groups[3].Value), I(x.Groups[4].Value), x.Groups[1].Value == "Adding")
                : Off($"unreadable excitation line: “{line}”");
        }
        if (line.StartsWith("Greedy iteration", StringComparison.Ordinal))
        {
            var g = Greedy.Match(line);
            if (!g.Success) return Off($"unreadable greedy iteration: “{line}”");
            _offlineSpoke = true;
            return new PalaceGreedyIteration(I(g.Groups[1].Value), I(g.Groups[2].Value), D(g.Groups[3].Value), D(g.Groups[4].Value));
        }
        if (line.StartsWith("Adaptive sampling", StringComparison.Ordinal))
        {
            var s = Sampling.Match(line);
            if (!s.Success) return Off($"unreadable sampling summary: “{line}”");
            _offlineSpoke = true;
            return new PalaceSamplingDone(I(s.Groups[2].Value), s.Groups[1].Value == "converged with");
        }
        if (line.StartsWith("It ", StringComparison.Ordinal) && line.Length > 3 && char.IsAsciiDigit(line[3]))
        {
            var o = Online.Match(line);
            if (!o.Success) return Off($"unreadable frequency line: “{line}”");
            _onlineSpoke = true;
            return new PalaceFrequency(I(o.Groups[1].Value), I(o.Groups[2].Value), D(o.Groups[3].Value));
        }
        if (line.StartsWith("Adaptive mesh refinement", StringComparison.Ordinal))
        {
            if (EndOfSolve() is { } off) return off;
            var a = Amr.Match(line);
            return a.Success ? new PalaceRefinementIteration(I(a.Groups[1].Value)) : Off($"unreadable refinement line: “{line}”");
        }
        if (line.StartsWith("Completed", StringComparison.Ordinal) && line.Contains("adaptive mesh refinement", StringComparison.Ordinal))
        {
            if (EndOfSolve() is { } off) return off;
            var c = Completed.Match(line);
            if (!c.Success) return Off($"unreadable refinement summary: “{line}”");
            (_completedIts, _block) = (I(c.Groups[1].Value), Block.CompletedIndicator);
            return null;
        }
        if (line.StartsWith("Elapsed Time Report", StringComparison.Ordinal))
        {
            if (EndOfSolve() is { } off) return off;
            (_block, _blockLines) = (Block.TimeTable, 0);
            return null;
        }
        if (line.StartsWith("Peak Memory", StringComparison.Ordinal))
        {
            (_block, _blockLines) = (Block.MemoryTable, 0);
            return null;
        }
        return null;
    }

    /// <summary>A solve is over: the online phase it started must have printed a frequency.</summary>
    private PalaceLogUnrecognised? EndOfSolve()
    {
        if (_inOnline && !_onlineSpoke) return Off("the frequency sweep ended without an 'It k/N' line");
        (_inOffline, _inOnline) = (false, false);
        return null;
    }

    private PalaceLogUnrecognised Off(string reason)
    {
        _off = true;
        return new PalaceLogUnrecognised(_line, reason);
    }

    private static int I(string s) => int.Parse(s, CultureInfo.InvariantCulture);

    private static double D(string s) => s switch
    {
        "inf" => double.PositiveInfinity,
        "nan" => double.NaN,
        _     => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture),
    };

    /// <summary>Palace's memory figures: a number and a K/M/G/T suffix, read as decimal multiples —
    /// the convention Em3dSizeEstimate's per-unknown figures were taken in.</summary>
    internal static double Bytes(string number, string suffix)
        => D(number) * suffix switch { "K" => 1e3, "M" => 1e6, "G" => 1e9, "T" => 1e12, _ => 1 };
}

/// <summary>
/// R-em3d21-5 — what a Palace run's log said about the run, gathered from the parser's events: the
/// summary line and the <c>.sNp</c> provenance fields come from here and never from a second read.
/// A null field is one the log did not print (or printed after the parser stopped recognising it).
/// </summary>
public sealed record PalaceRunSummary(
    long?   InitialElements,
    long?   FinalElements,
    long?   Unknowns,
    int?    RefinementPasses,
    int?    SweepSamples,
    double? PeakMemoryBytes,
    double? PalaceSeconds,
    bool    LogRecognised);

/// <summary>
/// R-em3d21-1b/c/f — turns the parser's events into the stage a user reads and the stage fraction,
/// on the one <see cref="RunControl"/> the run already carries. Fed one line at a time on the run's
/// own thread (never from a pipe reader), so a cancellation thrown by <see cref="RunControl.TickStage"/>
/// lands where the run can kill its processes.
/// </summary>
public sealed class PalaceStageTracker
{
    /// <summary>The stage fraction's resolution: 1000 steps, so a log-scale fraction moves visibly.</summary>
    public const long ConvergenceSteps = 1000;

    /// <summary>The unit the sampling stage declares. Not a noun to put after a counter: the bar is a
    /// convergence on a LOG scale, so the row shows this instead of "k / 1000".</summary>
    public const string ConvergenceUnit = "(log scale)";

    private readonly PalaceLogProgress _parser = new();
    private readonly RunControl? _control;
    private readonly int _maxPasses;          // N: the setup's AdaptiveMaxIterations
    private readonly double _sweepTol;
    private readonly List<string> _notes = [];

    private int _pass = 1;
    private long? _unknowns, _initialElements, _finalElements;
    private int? _refinementPasses, _samples;
    private double? _peak, _seconds;
    private double _firstError = double.NaN;
    private long _stageDone;
    private string _phase = "";
    private bool _degraded;
    private string _excitation = "";          // ", excitation i of n" when Palace names more than one

    /// <param name="maxRefinementPasses">The setup's <c>AdaptiveMaxIterations</c>: Palace makes up
    /// to that many plus one solves — the total the stage states, because it is the one Palace was
    /// given.</param>
    /// <param name="sweepTolerance">The setup's <c>SweepAdaptiveTol</c>: what the greedy error converges on.</param>
    public PalaceStageTracker(int maxRefinementPasses, double sweepTolerance, RunControl? control)
    {
        _maxPasses = Math.Max(0, maxRefinementPasses);
        _sweepTol  = sweepTolerance;
        _control   = control;
    }

    /// <summary>Solves Palace may make: N + 1.</summary>
    public int TotalPasses => _maxPasses + 1;

    /// <summary>Notes for the run: an early refinement convergence, an unrecognised log.</summary>
    public IReadOnlyList<string> Notes => _notes;

    public PalaceRunSummary Summary => new(_initialElements, _finalElements, _unknowns, _refinementPasses, _samples,
                                           _peak, _seconds, !_parser.Unrecognised);

    /// <summary>The stage every Palace run opens on.</summary>
    public void Begin() => _control?.BeginStage(PassLabel());

    /// <summary>One line of Palace's output.</summary>
    public void Line(string line)
    {
        var e = _parser.Feed(line);
        if (_degraded)
        {
            _control?.TickStage();
            return;
        }
        switch (e)
        {
            case PalaceMeshElements m:
                _initialElements ??= m.Elements;
                _finalElements = m.Elements;
                break;
            case PalaceUnknowns u:
                _unknowns = u.Count;
                if (_phase == "") _control?.SetStageLabel(PassLabel());
                break;
            case PalaceRefinementIteration r:
                _pass = r.Iteration + 1;
                _phase = "";
                _excitation = "";
                _unknowns = null;       // the refined mesh's count arrives with its own ND line
                _firstError = double.NaN;
                _control?.BeginStage(PassLabel());
                break;
            case PalaceExcitation x:
                // Each excitation samples, and sweeps, on its own: a new stage, from the start.
                _excitation = x.Count > 1 ? $", excitation {x.Ordinal} of {x.Count}" : "";
                _phase = x.Sampling ? "" : "sweeping";
                break;
            case PalaceGreedyIteration g:
                Sampling(g.Error);
                break;
            case PalaceSamplingDone s:
                _samples = s.Samples;
                break;
            case PalaceFrequency f:
                if (_phase != "online")
                {
                    _phase = "online";
                    _stageDone = 0;
                    _control?.BeginStage(EvaluatingLabel + _excitation, f.Count, "frequencies");
                }
                if (f.Index > _stageDone)
                {
                    long step = f.Index - _stageDone;
                    _stageDone = f.Index;
                    _control?.TickStage(step);
                }
                break;
            case PalaceRefinementDone d:
                _refinementPasses = d.Iterations;
                if (d.Iterations < d.MaxIterations)
                    _notes.Add(d.Indicator < d.Tolerance
                        ? $"Palace's mesh refinement converged after pass {d.Iterations + 1} of up to {d.MaxIterations + 1}: its " +
                          $"error indicator {Sci(d.Indicator)} is below the tolerance {Sci(d.Tolerance)}."
                        : $"Palace's mesh refinement stopped after pass {d.Iterations + 1} of up to {d.MaxIterations + 1}, with its " +
                          $"error indicator {Sci(d.Indicator)} still above the tolerance {Sci(d.Tolerance)}: the mesh reached " +
                          "the size limit.");
                break;
            case PalaceElapsed t:
                _seconds = t.Seconds;
                break;
            case PalacePeakMemory p:
                _peak = p.Bytes;
                break;
            case PalaceLogUnrecognised u:
                _degraded = true;
                _notes.Add($"Palace's log was not in the wording circuitRF reads (Palace 0.18.1's), so the run's progress was " +
                           $"shown as a count of log lines, with no fraction: at line {u.Line:N0}, {u.Reason}. The run itself " +
                           "is not affected.");
                _control?.BeginStage(UnrecognisedLabel, 0, "lines");
                _control?.TickStage(_parser.Lines);
                break;
        }
    }

    /// <summary>The stage name while the log is unrecognised.</summary>
    public const string UnrecognisedLabel = "Solving (Palace) — log format not recognised";

    /// <summary>The online phase's stage name.</summary>
    public const string EvaluatingLabel = "Sweep: evaluating frequencies";

    /// <summary>The sampling stage's name, before the error figures are appended.</summary>
    public const string SamplingLabel = "Sweep: sampling";

    private string PassLabel()
    {
        string pass = TotalPasses > 1 ? $"Solving: refinement pass {_pass} of {TotalPasses}"
                                      : "Solving: pass 1 of 1 (no refinement)";
        return _unknowns is { } n ? $"{pass} · {n.ToString("N0", CultureInfo.InvariantCulture)} unknowns" : pass;
    }

    /// <summary>
    /// R-em3d21-1c — the greedy error against the sweep tolerance on a log scale:
    /// <c>log(e₁/e) / log(e₁/tol)</c>, clamped to [0, 1]. The bar shows the best convergence so far
    /// (it never runs backwards); the label shows Palace's current error, which may rise between steps.
    /// </summary>
    public static double ConvergenceFraction(double firstError, double error, double tolerance)
    {
        if (!(tolerance > 0) || double.IsNaN(error)) return 0;
        if (!double.IsFinite(firstError) || firstError <= tolerance) return error <= tolerance ? 1 : 0;
        if (!(error > 0)) return 1;
        double f = Math.Log(firstError / error) / Math.Log(firstError / tolerance);
        return Math.Clamp(f, 0, 1);
    }

    private void Sampling(double error)
    {
        if (_phase != "sampling")
        {
            _phase = "sampling";
            _stageDone = 0;
            _firstError = error;
            _control?.BeginStage(SamplingText(error), ConvergenceSteps, ConvergenceUnit);
        }
        long target = (long)Math.Round(ConvergenceFraction(_firstError, error, _sweepTol) * ConvergenceSteps);
        long step = Math.Max(0, target - _stageDone);
        _stageDone += step;
        _control?.TickStage(step, SamplingText(error));
    }

    private string SamplingText(double error)
        => $"{SamplingLabel}{_excitation} · error {Sci(error)} → tolerance {Sci(_sweepTol)}";

    internal static string Sci(double v)
        => double.IsFinite(v) ? v.ToString("0.0#e+0", CultureInfo.InvariantCulture) : v.ToString(CultureInfo.InvariantCulture);
}

/// <summary>R-em3d21-1e — Gmsh's own stage lines and its final count, one line in.</summary>
public sealed class GmshLogProgress(RunControl? control)
{
    public const string MeshingLabel = "Meshing (Gmsh)";

    private static readonly Regex Count = new(@"^Info\s*:\s*(\d+) nodes (\d+) elements\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex Tets  = new(@"^Info\s*:\s*-\s*(\d+) tetrahedra created\b", RegexOptions.CultureInvariant);

    /// <summary>Gmsh's final node and element counts, when it printed them.</summary>
    public (long Nodes, long Elements)? Final { get; private set; }

    /// <summary>
    /// The tetrahedra Gmsh's 3D mesher reported creating (<c>- N tetrahedra created</c>, summed over
    /// its passes), or null. Optimisation removes a few afterwards — F0's case A printed 149,252 and
    /// Palace read 146,769 — so as a size for the memory check it errs high, by about 2 %.
    /// </summary>
    public long? Tetrahedra { get; private set; }

    public void Begin() => control?.BeginStage(MeshingLabel);

    public void Line(string line)
    {
        string t = line.TrimEnd('\r');
        if (!t.StartsWith("Info", StringComparison.Ordinal)) return;
        string said = t[(t.IndexOf(':') + 1)..].Trim();
        if (said.StartsWith("Meshing 3D", StringComparison.Ordinal) || said.StartsWith("Done meshing 3D", StringComparison.Ordinal))
        {
            int paren = said.IndexOf(" (", StringComparison.Ordinal);
            control?.SetStageLabel($"{MeshingLabel}: {(paren > 0 ? said[..paren] : said).TrimEnd('.')}");
            return;
        }
        if (Tets.Match(t) is { Success: true } made)
        {
            Tetrahedra = (Tetrahedra ?? 0) + long.Parse(made.Groups[1].Value, CultureInfo.InvariantCulture);
            return;
        }
        var c = Count.Match(t);
        if (!c.Success) return;
        Final = (long.Parse(c.Groups[1].Value, CultureInfo.InvariantCulture), long.Parse(c.Groups[2].Value, CultureInfo.InvariantCulture));
        control?.SetStageLabel($"{MeshingLabel}: {Final.Value.Nodes.ToString("N0", CultureInfo.InvariantCulture)} nodes, " +
                               $"{Final.Value.Elements.ToString("N0", CultureInfo.InvariantCulture)} elements");
    }
}
