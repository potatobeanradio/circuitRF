using System.Diagnostics;
using CircuitRF.Core.Design;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Matching;
using CircuitRF.Design.Optimization;
using CircuitRF.Diagnostics;
using CircuitRF.Engine.Optimization;
using CircuitRF.Engine.Statistics;
using RfCore.Data;

namespace CircuitRF.Design.Statistics;

/// <summary>What a caller may change about a centering run beyond what the design's setup says.</summary>
public sealed record CenteringOptions
{
    /// <summary>The setup to run; null runs the one the circuit's netlist carries.</summary>
    public TuningSetup? Setup { get; init; }

    /// <summary><c>--set name=expr</c> overrides, applied before the candidate's values as a run verb applies them.</summary>
    public IReadOnlyList<(string Name, string Expression)> Sets { get; init; } = [];

    /// <summary>Told after every iteration, and once as the verification starts, on the run's thread.</summary>
    public Action<CenteringProgress>? Progress { get; init; }

    /// <summary>Cancelling abandons the run and writes nothing (exit 130); Stop keeps the best point and verifies it.</summary>
    public CancellationToken Cancellation { get; init; }

    /// <summary>Where the best point's verification DataSet is written (<see cref="StatisticalRun.ResultPathFor"/>);
    /// null writes nothing.</summary>
    public string? ResultPath { get; init; }

    /// <summary>The preferred-value ladders a <c>discrete=preferred</c> entry snaps to; null is the shipped ones.</summary>
    public PreferredLadders? Ladders { get; init; }
}

/// <summary>One iteration of the search (R-ya11-7): the best point's plain yield and smooth objective on the common
/// trials, and the simulations run so far.</summary>
/// <param name="RSquared">Under the quadratic surrogate (brief-yield-12 R-ya12-3), the poorest fit R² per yield goal over
/// the iteration's candidates; null when the iteration's candidates were simulated.</param>
public sealed record CenteringIteration(int Iteration, double BestYield, double BestObjective, long Evaluations,
                                        IReadOnlyDictionary<string, double>? RSquared = null);

/// <summary>What a centering run reports after every iteration (R-ya11-8), and once as the verification starts.</summary>
/// <param name="Stage"><c>search</c>, or <c>verify</c> for the closing verification.</param>
public sealed record CenteringProgress(
    string                              Stage,
    int                                 Iteration,
    long                                Evaluations,
    TimeSpan                            Elapsed,
    double                              BestYield,
    double                              BestObjective,
    IReadOnlyDictionary<string, string> BestValues,
    IReadOnlyList<RailedVariable>       Railed,
    IReadOnlyDictionary<string, double>? RSquared = null);

/// <summary>One candidate scored on the common trials (R-ya11-2/3).</summary>
/// <param name="Values">The candidate's designable values, as the schematic would hold them.</param>
/// <param name="Objective">The smooth objective: the mean over the counted trials of the logistic of each trial's
/// smallest normalized margin. NaN when nothing could be scored.</param>
/// <param name="Yield">The plain yield on the same trials, with its interval.</param>
/// <param name="Records">Each trial, in trial order; empty for an infeasible candidate.</param>
public sealed record CandidateScore(
    IReadOnlyDictionary<string, string> Values,
    double                              Objective,
    YieldEstimate                       Yield,
    int                                 DidNotEvaluate,
    IReadOnlyList<TrialRecord>          Records,
    bool                                Infeasible)
{
    /// <summary>Nothing could be scored: infeasible, or no trial evaluated (and, under <c>nonconverged=fail</c>,
    /// every trial failed to).</summary>
    public bool Failed => Infeasible || double.IsNaN(Objective) || Records.Count == DidNotEvaluate;

    /// <summary>Scored on the quadratic surrogate's virtual trials (brief-yield-12): the fit R² per yield goal. Null
    /// when the candidate was simulated on the common trials; its <see cref="Records"/> are then the design's points.</summary>
    public IReadOnlyDictionary<string, double>? Fit { get; init; }
}

/// <summary>The closing verification (R-ya11-5): the start and the best point on the same fresh trials.</summary>
/// <param name="Seed">The verification's seed — the centering seed plus one.</param>
/// <param name="WithinOverlap">The two intervals overlap: the verified gain is not resolved at this many trials.</param>
public sealed record CenteringVerification(int Seed, int Trials, YieldEstimate Start, YieldEstimate Best, bool WithinOverlap, string Sentence);

/// <summary>How a centering run ended (yield overview D13 gives each its exit code).</summary>
public enum CenteringOutcome
{
    /// <summary>Finished or stopped, and the verified yield is at or above the target, or there is none — exit 0.</summary>
    Finished,
    /// <summary>The verified yield is below the target — exit 3.</summary>
    BelowTarget,
    /// <summary>Refused before the first candidate — exit 1.</summary>
    Refused,
    /// <summary>No candidate evaluated a trial — exit 2.</summary>
    NoneEvaluated,
    /// <summary>Cancelled — exit 130, and nothing is written.</summary>
    Cancelled,
}

/// <summary>What <c>explain --analysis</c> states about a centering run before anything runs (R-ya11-6).</summary>
/// <param name="PointsPerIteration">The candidates the algorithm's first batch holds — its population.</param>
/// <param name="Estimate">The total as a sentence.</param>
public sealed record CenteringEstimate(
    string Algorithm, int Trials, int Verify, int Variables, int PointsPerIteration, long EvaluationsPerIteration,
    int MaxIterations, long? MaxEvaluations, long Total, string Estimate);

/// <summary>What a centering run produced.</summary>
public sealed class CenteringResult
{
    public required CenteringOutcome Outcome { get; init; }

    /// <summary>Yield overview D13: 0, 3, 1, 2 or 130.</summary>
    public int ExitCode => Outcome switch
    {
        CenteringOutcome.Finished      => 0,
        CenteringOutcome.BelowTarget   => 3,
        CenteringOutcome.Refused       => 1,
        CenteringOutcome.NoneEvaluated => 2,
        _                              => 130,
    };

    /// <summary>Why the search stopped, as a sentence.</summary>
    public string FinishReason { get; init; } = "";

    public Diagnostic? Refusal { get; init; }

    public IReadOnlyList<Diagnostic> Notes { get; init; } = [];

    public string Algorithm { get; init; } = "";

    /// <summary>The search accelerator the run was asked for (brief-yield-12).</summary>
    public CenteringSurrogate Surrogate { get; init; }

    /// <summary>The iteration after which three poor fits running switched the search back to simulated trials;
    /// null when it never did.</summary>
    public int? SwitchedBackAt { get; init; }

    public int Iterations { get; init; }

    /// <summary>Simulations the search ran — the common trials, the verification not counted.</summary>
    public long Evaluations { get; init; }

    /// <summary>The verification's simulations, both points together.</summary>
    public long VerifyEvaluations { get; init; }

    public IReadOnlyList<CenteringIteration> History { get; init; } = [];

    /// <summary>The start point scored on the common trials.</summary>
    public CandidateScore? Start { get; init; }

    /// <summary>The best point (by the smooth objective) scored on the common trials.</summary>
    public CandidateScore? Best { get; init; }

    /// <summary>The centred nominals — what <c>--save-preset</c> stores and Push writes. Empty when nothing evaluated.</summary>
    public IReadOnlyDictionary<string, string> BestValues => Best?.Values ?? new Dictionary<string, string>();

    public IReadOnlyList<RailedVariable> Railed { get; init; } = [];

    /// <summary>Null when the search found nothing to verify or the run was cancelled.</summary>
    public CenteringVerification? Verification { get; init; }

    /// <summary>The best point's verification run, its full yield DataSet among it.</summary>
    public StatisticalResult? Verified { get; init; }

    /// <summary>Where the verification DataSet was written; null when it was not.</summary>
    public string? WrittenPath => Verified?.WrittenPath;
}

/// <summary>
/// Design centering (brief-yield-11, docs/design/yield.md §15): the nominals of the <c>opt=1</c> entries move to
/// maximize yield while the tolerances ride along. Each candidate is scored on ONE fixed set of M trials — trial t's
/// z-vector, drawn once from (seed, t, stream) and re-applied around the candidate's nominal (yield overview D5) —
/// so two candidates' yields differ by the design, not by sampling noise. The algorithm (the optimizer's registry,
/// ask/tell over the unit box, those suited to a noisy objective) minimizes the negated SMOOTH objective; the plain
/// yield on the same trials is reported beside it. At the end the start and the best point are each run on the same
/// fresh trials (the next seed) by an ordinary <see cref="StatisticalRun"/>, whose DataSet is the result file.
///
/// <para><b>One evaluator.</b> The common trials are a <see cref="StatisticalRun"/> over the setup with the centering
/// seed and M trials, evaluated through its <see cref="StatisticalRun.EvaluateAt"/> — the optimizer's evaluator,
/// cache and parallelism. A candidate seen before costs nothing.</para>
///
/// <para><b>Pause, Resume, Stop</b> act between iterations as the optimizer's (tuning D16): Stop keeps the best point
/// and still verifies it; cancelling abandons the run and writes nothing.</para>
/// </summary>
public sealed class CenteringRun
{
    private readonly CenteringOptions _options;
    private readonly TuningSetup _setup = new();
    private readonly CenteringSettings _center = new();
    private readonly StatisticsSettings _statistics = new();
    private readonly StatisticalRun? _trials;
    private readonly PreparedCircuit _circuit;
    private readonly List<Diagnostic> _notes = [];
    private readonly double[] _scales = [];
    private readonly int[] _trialNumbers = [];

    // brief-yield-12: the quadratic surrogate — its coordinates, design, check trials and virtual trials.
    private readonly IReadOnlyList<SurrogateDimension> _dims = [];
    private readonly IReadOnlyList<double[]> _design = [];
    private readonly int[] _checkTrials = [];
    private double[][]? _virtual;
    private bool _surrogate;

    private readonly ManualResetEventSlim _resume = new(true);
    private readonly ManualResetEventSlim _held = new(false);
    private volatile bool _stop;

    private CenteringRun(PreparedCircuit circuit, CenteringOptions options)
    {
        _circuit = circuit;
        _options = options;
        if (circuit.ReadError is { } readError || circuit.Lib is not { } lib || circuit.Tb is not { } tb)
        {
            Refusal = OptimizationDiagnostics.EvaluationFailed(circuit.ReadError ?? "Netlist read failed.");
            return;
        }
        _setup      = options.Setup ?? tb.Tuning ?? new TuningSetup();
        _center     = _setup.Centering ?? new CenteringSettings();
        _statistics = _setup.Statistics ?? new StatisticsSettings();
        Algorithm   = _center.EffectiveAlgorithm;

        // R-ya11-1: the designable variables first — a setup with none is centering's own refusal.
        if (!_setup.Variables.Any(e => e.Opt)) { Refusal = StatisticsDiagnostics.CenterNoDesignable(); return; }

        // The common trials: the setup's statistics with M trials at the centering seed — no auto-stop and no corners,
        // since every candidate is scored on all M at the nominal corner.
        _trials = StatisticalRun.Create(circuit, new StatisticalOptions
        {
            Mode = StatisticalMode.Yield, Setup = WithTrials(_center.EffectiveTrials, _center.EffectiveSeed),
            Sets = options.Sets, Cancellation = options.Cancellation,
        });
        if (_trials.Refusal is { } refused) { Refusal = refused; return; }
        _notes.AddRange(_trials.Notes);

        var bench = tb;
        if (options.Setup is not null)
        {
            bench = new TestBench(tb.Name) { Tuning = options.Setup };
            bench.Instances.AddRange(tb.Instances);
            bench.GlobalVariables.AddRange(tb.GlobalVariables);
            bench.Analyses.AddRange(tb.Analyses);
        }
        Variables = OptimizationVariables.Build(_setup, TunableCatalog.FromNetlist(bench, lib, parts: circuit.PartTunables), out var notBuilt, options.Ladders);
        if (Variables is null || Variables.Coordinates.Count == 0)
        {
            Refusal = notBuilt ?? StatisticsDiagnostics.CenterNoDesignable();
            return;
        }
        _notes.AddRange(Variables.Notes);

        // R-ya11-4: the registry's metadata, not a list here, says which methods centering offers.
        var suited = OptimizerAlgorithms.ForNoisyObjective.Where(OptimizerFactory.IsBuilt).ToList();
        if (!suited.Contains(Algorithm)) { Refusal = StatisticsDiagnostics.CenterAlgorithm(Algorithm, string.Join(", ", suited)); return; }
        if (Algorithm == Discrete.AlgorithmId && Variables.DiscreteUnavailable() is { } notDiscrete) { Refusal = notDiscrete; return; }

        _scales = [.. _trials.Goals.Select(g => GoalResiduals.Scale(g) is { } s && s > 0 ? s : 1.0)];
        _trialNumbers = [.. Enumerable.Range(1, _center.EffectiveTrials)];

        // R-ya12-2: the surrogate's coordinates — kit mismatch grouped per instance — and a design no cheaper than the
        // common trials is refused with the counts, never truncated.
        if (_center.EffectiveSurrogate == CenteringSurrogate.Quadratic)
        {
            _dims = _trials.SurrogateDimensions();
            int k = _dims.Count, per = QuadraticSurrogate.PointsPerCandidate(k);
            if (per >= _center.EffectiveTrials)
            {
                Refusal = StatisticsDiagnostics.CenterSurrogateTooMany(
                    k, _dims.Count(d => d.Kind == SurrogateDimensionKind.Entry), _dims.Count(d => d.Kind == SurrogateDimensionKind.Process),
                    _dims.Where(d => d.Kind == SurrogateDimensionKind.Mismatch).Sum(d => d.Streams.Count),
                    _dims.Count(d => d.Kind == SurrogateDimensionKind.Mismatch), per, _center.EffectiveTrials);
                return;
            }
            if (!QuadraticSurrogate.IsFull(k)) _notes.Add(StatisticsDiagnostics.CenterSurrogateDiagonal(k, QuadraticSurrogate.FullQuadraticLimit));
            _design = QuadraticSurrogate.Design(k);
            _checkTrials = [.. Enumerable.Range(1, QuadraticSurrogate.CheckTrials(k))];
            _surrogate = true;
        }
    }

    /// <summary>Prepares a centering run of <paramref name="circuit"/>'s setup. A run that cannot start carries its
    /// <see cref="Refusal"/> — <c>check</c> asks the same question — and <see cref="Run"/> returns it at once.</summary>
    public static CenteringRun Create(PreparedCircuit circuit, CenteringOptions? options = null)
        => new(circuit, options ?? new CenteringOptions());

    public Diagnostic? Refusal { get; }

    /// <summary>The registry id the run uses.</summary>
    public string Algorithm { get; } = CenteringSettings.DefaultAlgorithm;

    /// <summary>The designable variables, decoded from the unit box as the optimizer decodes them.</summary>
    public OptimizationVariables? Variables { get; }

    /// <summary>The yield goals every trial is scored against.</summary>
    public IReadOnlyList<OptimizationGoal> Goals => _trials?.Goals ?? [];

    /// <summary>The statistical entries drawn — designer tolerances; a designable one is drawn around its candidate.</summary>
    public IReadOnlyList<TunableEntry> Entries => _trials?.Entries ?? [];

    /// <summary>The trials every candidate is scored on — the same numbers in every iteration (R-ya11-2).</summary>
    public IReadOnlyList<int> TrialNumbers => _trialNumbers;

    public CenteringSettings Settings => _center;

    /// <summary>The surrogate's coordinates (brief-yield-12); empty when the run simulates every candidate.</summary>
    public IReadOnlyList<SurrogateDimension> SurrogateDimensions => _dims;

    /// <summary>Whether candidates are being scored on the quadratic surrogate — false from the start without one, and
    /// after three poor fits running switched the search back to simulated trials.</summary>
    public bool UsesSurrogate => _surrogate;

    /// <summary>The statistics line's yield target in percent — what the verified yield is held to; null for none.</summary>
    public double? Target => _statistics.Target;

    public IReadOnlyList<Diagnostic> Notes => _notes;

    /// <summary>Simulations run on the common trials so far.</summary>
    public long Evaluations => _trials?.Evaluations ?? 0;

    public void Pause() => _resume.Reset();

    public void Resume() => _resume.Set();

    /// <summary>Ends the search after the iteration in flight; the best point is still verified.</summary>
    public void Stop() { _stop = true; _resume.Set(); }

    public bool IsPaused => _held.IsSet;

    public WaitHandle Held => _held.WaitHandle;

    /// <summary>The last run's result; null before one finishes.</summary>
    public CenteringResult? Result { get; private set; }

    // ── Scoring ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The candidate at <paramref name="u"/> (a point of the unit box) scored on the common trials — what the
    /// algorithm is told. Two calls with the same point return the same score: the trials are fixed, and the
    /// evaluator's cache answers the second.
    /// </summary>
    public CandidateScore Score(double[] u, CancellationToken ct = default) => ScoreBatch([u], ct)[0];

    private CandidateScore[] ScoreBatch(IReadOnlyList<double[]> batch, CancellationToken ct, bool snap = false)
        => _surrogate ? SurrogateBatch(batch, ct, snap) : SimulatedBatch(batch, ct, snap);

    /// <summary>A candidate's values: a standard-value nominal on its rung only when <paramref name="snap"/> asks (the
    /// snap stage), or throughout under Discrete, whose points are rungs already.</summary>
    private DecodedPoint Decode(double[] u, bool snap)
        => Variables!.Decode(u, snapPreferred: snap || Algorithm == Discrete.AlgorithmId);

    /// <summary>Each candidate simulated on all M common trials (brief-yield-11).</summary>
    private CandidateScore[] SimulatedBatch(IReadOnlyList<double[]> batch, CancellationToken ct, bool snap = false)
    {
        var decoded = batch.Select(u => Decode(u, snap)).ToArray();
        var points = new List<(int, IReadOnlyDictionary<string, string>)>();
        foreach (var d in decoded)
            if (!d.Infeasible)
                foreach (int t in _trialNumbers) points.Add((t, d.Values));
        var records = _trials!.EvaluateAt(points, ct);

        var scores = new CandidateScore[batch.Count];
        int next = 0;
        for (int k = 0; k < batch.Count; k++)
        {
            if (decoded[k].Infeasible)
            {
                scores[k] = new CandidateScore(decoded[k].Values, double.NaN, YieldEstimate.None, 0, [], true);
                continue;
            }
            var mine = records.AsSpan(next, _trialNumbers.Length).ToArray();
            next += _trialNumbers.Length;
            scores[k] = ScoreOf(decoded[k].Values, mine);
        }
        return scores;
    }

    /// <summary>
    /// R-ya12-2: each candidate's design points and check trials as one batch each; per yield goal a quadratic fit over
    /// them; the smooth objective and the yield on the virtual trials. A candidate whose design did not evaluate whole
    /// — a point that failed, a goal with no value — is simulated on the common trials instead.
    /// </summary>
    private CandidateScore[] SurrogateBatch(IReadOnlyList<double[]> batch, CancellationToken ct, bool snap)
    {
        var decoded = batch.Select(u => Decode(u, snap)).ToArray();
        var zPoints = new List<(double[], IReadOnlyDictionary<string, string>)>();
        var checks  = new List<(int, IReadOnlyDictionary<string, string>)>();
        foreach (var d in decoded)
        {
            if (d.Infeasible) continue;
            foreach (var x in _design) zPoints.Add((x, d.Values));
            foreach (int t in _checkTrials) checks.Add((t, d.Values));
        }
        var designRecords = _trials!.EvaluateAtZ(_dims, zPoints, ct);
        var checkRecords  = _trials.EvaluateAt(checks, ct);
        _virtual ??= _trials.VirtualCoordinates(_dims, QuadraticSurrogate.VirtualTrials);

        var scores = new CandidateScore[batch.Count];
        var simulate = new List<int>();
        int nextZ = 0, nextC = 0;
        for (int k = 0; k < batch.Count; k++)
        {
            if (decoded[k].Infeasible)
            {
                scores[k] = new CandidateScore(decoded[k].Values, double.NaN, YieldEstimate.None, 0, [], true);
                continue;
            }
            var design = designRecords.AsSpan(nextZ, _design.Count).ToArray();
            var trials = checkRecords.AsSpan(nextC, _checkTrials.Length).ToArray();
            nextZ += _design.Count;
            nextC += _checkTrials.Length;
            if (SurrogateScoreOf(decoded[k].Values, design, trials) is { } s) scores[k] = s;
            else simulate.Add(k);
        }
        if (simulate.Count > 0)
        {
            var simulated = SimulatedBatch([.. simulate.Select(k => batch[k])], ct, snap);
            for (int i = 0; i < simulate.Count; i++) scores[simulate[i]] = simulated[i];
        }
        return scores;
    }

    /// <summary>The fits and the virtual-trial score of one candidate; null when a point or a goal has no value.</summary>
    private CandidateScore? SurrogateScoreOf(IReadOnlyDictionary<string, string> values, TrialRecord[] design, TrialRecord[] checks)
    {
        var all = design.Concat(checks).ToArray();
        if (all.Any(r => !r.Evaluated)) return null;
        var z = new List<double[]>(_design);
        foreach (var r in checks)
            z.Add(StatisticalRun.CoordinatesOf(_dims, r.Z, s => r.Kit.TryGetValue(s, out var d) ? d.Z : 0));

        var goals = Goals;
        var models = new QuadraticModel[goals.Count];
        var rules  = new Func<double, double>[goals.Count];
        var fit    = new Dictionary<string, double>(StringComparer.Ordinal);
        for (int g = 0; g < goals.Count; g++)
        {
            var scored = all.Select(r => r.Goals.FirstOrDefault(x => x.Name == goals[g].Name)).ToArray();
            if (scored.Any(x => x is null || x.Error is not null)) return null;
            bool value = scored.All(x => QuadraticSurrogate.FitsValue(x!));
            var y = scored.Select(x => QuadraticSurrogate.Fitted(x!, value)).ToArray();
            if (y.Any(v => !double.IsFinite(v))) return null;
            models[g] = QuadraticSurrogate.Fit(z, y);
            rules[g]  = QuadraticSurrogate.MarginRule(goals[g], value);
            fit[goals[g].Name] = models[g].RSquared;
        }
        var v = QuadraticSurrogate.Score(models, rules, _scales, _center.EffectiveWidth, _virtual!);
        var yield = YieldEstimate.Of(v.Passes, v.Trials, _statistics.EffectiveConfidence / 100);
        return new CandidateScore(values, v.Objective, yield, 0, all, false) { Fit = fit };
    }

    /// <summary>
    /// R-ya11-3: per trial, the smallest margin over the yield goals divided by each goal's scale, through a logistic
    /// of width w; the objective is the mean over the counted trials. A trial that did not evaluate scores 0 and is
    /// counted under <c>nonconverged=fail</c>, and is left out under <c>warn</c> (yield overview D7).
    /// </summary>
    private CandidateScore ScoreOf(IReadOnlyDictionary<string, string> values, TrialRecord[] trials)
    {
        bool countFails = _statistics.NonConverged == NonConvergedPolicy.Fail;
        double w = _center.EffectiveWidth, sum = 0;
        int counted = 0, passes = 0, dne = 0;
        var goals = Goals;
        foreach (var r in trials)
        {
            if (!r.Evaluated)
            {
                dne++;
                if (countFails) counted++;
                continue;
            }
            double m = double.PositiveInfinity;
            for (int g = 0; g < goals.Count; g++)
            {
                var s = r.Goals.FirstOrDefault(x => x.Name == goals[g].Name);
                m = Math.Min(m, s is null || double.IsNaN(s.Margin) ? double.NegativeInfinity : s.Margin / _scales[g]);
            }
            sum += 1 / (1 + Math.Exp(-m / w));
            counted++;
            if (r.Pass) passes++;
        }
        double objective = counted == 0 ? double.NaN : sum / counted;
        var yield = counted == 0 ? YieldEstimate.None : YieldEstimate.Of(passes, counted, _statistics.EffectiveConfidence / 100);
        return new CandidateScore(values, objective, yield, dne, trials, false);
    }

    /// <summary>
    /// The snap stage: each standard-value nominal of <paramref name="from"/> to the series values either side of it — every
    /// combination with up to six such nominals, else the nearest only — scored on the same trials, the best kept. Null
    /// when no coordinate takes standard values; a null point when none of the snapped candidates evaluated.
    /// </summary>
    private (int Count, (CandidateScore Score, double[] U)? Best)? SnapPreferred(double[] from, CancellationToken ct)
    {
        var vars = Variables!;
        int[] preferred = [.. Enumerable.Range(0, vars.Coordinates.Count)
                                        .Where(i => vars.Coordinates[i] is { Preferred: true, Levels.Count: > 0 })];
        if (preferred.Length == 0) return null;

        var batch = new List<double[]>();
        if (preferred.Length <= 6)
        {
            var choices = preferred.Select(i =>
            {
                var c = vars.Coordinates[i];
                var (below, above) = PreferredValues.Bracket(c.Decode(from[i]), c.Levels!);
                return below == above ? new[] { c.Encode(below) } : [c.Encode(below), c.Encode(above)];
            }).ToArray();
            int combos = choices.Aggregate(1, (p, ch) => p * ch.Length);
            for (int k = 0; k < combos; k++)
            {
                var x = (double[])from.Clone();
                int rest = k;
                for (int d = 0; d < preferred.Length; d++)
                {
                    x[preferred[d]] = choices[d][rest % choices[d].Length];
                    rest /= choices[d].Length;
                }
                batch.Add(x);
            }
        }
        else
        {
            var x = (double[])from.Clone();
            foreach (int i in preferred)
            {
                var c = vars.Coordinates[i];
                x[i] = c.Encode(PreferredValues.Snap(c.Decode(x[i]), c.Levels!));
            }
            batch.Add(x);
        }

        ct.ThrowIfCancellationRequested();
        var scores = ScoreBatch(batch, ct, snap: true);
        int bestK = -1;
        for (int k = 0; k < scores.Length; k++)
            if (!scores[k].Failed && (bestK < 0 || scores[k].Objective > scores[bestK].Objective)) bestK = k;
        return (preferred.Length, bestK < 0 ? null : (scores[bestK], batch[bestK]));
    }

    private static string Smooth(double objective) => objective.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture);

    // ── The estimate ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// What a run would cost, before anything runs (R-ya11-6): evaluations per iteration are the algorithm's first
    /// batch × M, and the total is the start, the iteration limit's worth of those (the evaluation limit, when it is
    /// lower) and the two verification runs. Null when the run is refused.
    /// </summary>
    public CenteringEstimate? Estimate()
    {
        if (Refusal is not null || Variables is null) return null;
        int m = _center.EffectiveTrials, verify = _center.EffectiveVerify;
        int population = Math.Max(1, CreateAlgorithm().Ask().Count);
        // brief-yield-12: under the surrogate a candidate costs its design and check trials, not M.
        int perCandidate = _surrogate ? QuadraticSurrogate.PointsPerCandidate(_dims.Count) : m;
        string what = _surrogate ? $"{perCandidate} surrogate points" : $"{m} trials";
        long perIteration = (long)population * perCandidate;
        int maxIter = _center.MaxIterations ?? OptimizationRun.DefaultMaxIterations;
        long search = perIteration * maxIter;
        if (_center.MaxEvaluations is { } me) search = Math.Min(search, me);
        long total = perCandidate + search + 2L * verify;
        string sentence = $"about {total} simulations: the start on the {(_surrogate ? what : what.Replace("trials", "common trials"))}, " +
                          $"up to {maxIter} iterations of {population} candidate(s) × {what} ({perIteration} each)" +
                          (_center.MaxEvaluations is { } cap ? $", at most {cap} in all," : "") +
                          $" then {verify} verification trials at each of the start and the best point (an estimate; " +
                          "a candidate seen before costs nothing)";
        return new CenteringEstimate(Algorithm, m, verify, Variables.Coordinates.Count, population, perIteration,
                                     maxIter, _center.MaxEvaluations, total, sentence);
    }

    private IOptimizerAlgorithm CreateAlgorithm()
    {
        IReadOnlyList<double[]>? levels = null;
        if (Algorithm == Discrete.AlgorithmId)
            levels = [.. Variables!.Coordinates.Select(c => (c.Levels ?? []).Select(c.Encode).ToArray())];
        return OptimizerFactory.Create(Algorithm, Variables!.Start, unchecked((ulong)_center.EffectiveSeed), null, levels)!;
    }

    // ── The run ──────────────────────────────────────────────────────────────────────

    /// <summary>Runs to the end on the calling thread: the search, then the verification.</summary>
    public CenteringResult Run()
    {
        if (Refusal is { } refused) return Result = Refusing(refused);
        var ct = _options.Cancellation;
        var sw = Stopwatch.StartNew();
        var history = new List<CenteringIteration>();
        CandidateScore start;
        CandidateScore? best = null;
        double[]? bestU = null;
        int iterations = 0;
        string reason;
        double stallTol = OptionDefault("stall_tol"), stallIters = OptionDefault("stall_iters");
        double? limitS = _center.TimeLimit is { } tl ? TuningValidator.TimeLimitSeconds(tl) : null;
        int maxIter = _center.MaxIterations ?? OptimizationRun.DefaultMaxIterations;
        var notes = new List<Diagnostic>(_notes);
        // R-ya12-3: the poorest fit per goal over the iteration in flight, and the poor iterations running.
        var iterationFit = new Dictionary<string, double>(StringComparer.Ordinal);
        int poorRunning = 0;
        int? switchedAt = null;

        try
        {
            start = Score(Variables!.Start, ct);
            if (!start.Failed) (best, bestU) = (start, Variables.Start);

            var alg = CreateAlgorithm();
            int told = 0;
            while (true)
            {
                if (_stop) { reason = "stopped"; break; }
                if (!_resume.IsSet)
                {
                    _held.Set();
                    _resume.Wait(ct);
                    _held.Reset();
                    if (_stop) { reason = "stopped"; break; }
                }
                ct.ThrowIfCancellationRequested();

                var asked = alg.Ask();
                if (asked.Count == 0) { reason = alg.FinishReason ?? "the algorithm finished"; break; }
                var scores = ScoreBatch(asked, ct);
                var results = new Evaluation[asked.Count];
                for (int k = 0; k < asked.Count; k++)
                {
                    var s = scores[k];
                    // The algorithms minimize: the negated objective, so a candidate far from every window still ranks
                    // by its tiny logistic tail rather than by 1 − (a number that rounds to 1). A failed point costs 1,
                    // above every real one.
                    results[k] = s.Failed ? new Evaluation(1, null, Failed: true) : new Evaluation(-s.Objective);
                    if (!s.Failed && (best is null || s.Objective > best.Objective)) (best, bestU) = (s, asked[k]);
                    if (s.Fit is { } fit)
                        foreach (var (goal, r2) in fit)
                            iterationFit[goal] = iterationFit.TryGetValue(goal, out double low) ? Math.Min(low, r2) : r2;
                }
                alg.Tell(results);
                if (alg.Iterations > told)
                {
                    iterations += alg.Iterations - told;
                    told = alg.Iterations;
                    IReadOnlyDictionary<string, double>? r2s = iterationFit.Count > 0 ? new Dictionary<string, double>(iterationFit) : null;
                    iterationFit.Clear();
                    history.Add(new CenteringIteration(iterations, best?.Yield.Yield ?? double.NaN, best?.Objective ?? double.NaN, Evaluations, r2s));
                    _options.Progress?.Invoke(new CenteringProgress("search", iterations, Evaluations, sw.Elapsed,
                        best?.Yield.Yield ?? double.NaN, best?.Objective ?? double.NaN,
                        best?.Values ?? new Dictionary<string, string>(), bestU is null ? [] : Variables.Railed(bestU), r2s));

                    // R-ya12-3: a fit below 0.9 on any goal is a warning on the iteration; three running, the search
                    // continues on simulated trials, the best point re-scored on them so it competes on equal terms.
                    if (_surrogate && r2s is not null)
                    {
                        var poor = QuadraticSurrogate.PoorFits(r2s);
                        foreach (var (goal, r2) in poor) notes.Add(StatisticsDiagnostics.CenterSurrogatePoorFit(iterations, goal, r2));
                        poorRunning = poor.Count > 0 ? poorRunning + 1 : 0;
                        if (poorRunning >= QuadraticSurrogate.PoorFitsBeforeSwitch)
                        {
                            _surrogate = false;
                            switchedAt = iterations;
                            notes.Add(StatisticsDiagnostics.CenterSurrogateSwitchedBack(iterations, QuadraticSurrogate.PoorFitsBeforeSwitch));
                            if (bestU is not null && SimulatedBatch([bestU], ct)[0] is { Failed: false } rescored) best = rescored;
                        }
                    }
                }

                if (alg.IsFinished) { reason = alg.FinishReason ?? "the algorithm finished"; break; }
                if (iterations >= maxIter) { reason = $"the iteration limit ({maxIter}) was reached"; break; }
                if (_center.MaxEvaluations is { } me && Evaluations >= me) { reason = $"the evaluation limit ({me}) was reached"; break; }
                if (limitS is { } secs && sw.Elapsed.TotalSeconds >= secs) { reason = $"the time limit ({_center.TimeLimit}) was reached"; break; }
                int k0 = history.Count - 1 - (int)stallIters;
                if (k0 >= 0 && history[^1].BestObjective - history[k0].BestObjective <= stallTol * Math.Abs(history[^1].BestObjective))
                { reason = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"the best smooth yield improved by less than {stallTol:G3} of itself over {stallIters} iterations"); break; }
            }

            // A standard-value (E-series) nominal is searched continuously and ends on the series, as the Optimizer's
            // runs end; the draws around it stay continuous, since a built part varies continuously within its
            // tolerance. A Stop ends with no snap, as the Optimizer's does.
            if (!_stop && bestU is not null && best is not null && Algorithm != Discrete.AlgorithmId
                && SnapPreferred(bestU, ct) is { } snap)
            {
                if (snap.Best is { } s)
                {
                    notes.Add(StatisticsDiagnostics.CenterSnapped(snap.Count, Smooth(best.Objective), Smooth(s.Score.Objective)));
                    (best, bestU) = s;
                }
                else notes.Add(StatisticsDiagnostics.CenterSnapFailed(snap.Count));
            }
        }
        catch (OperationCanceledException)
        {
            return Result = new CenteringResult { Outcome = CenteringOutcome.Cancelled, FinishReason = "cancelled", Algorithm = Algorithm, Notes = notes };
        }

        long searchEvaluations = Evaluations;
        if (best is null || bestU is null)
        {
            var first = start.Records.FirstOrDefault(r => r.Reason is not null)?.Reason?.Render() ?? "every candidate was infeasible";
            return Result = new CenteringResult
            {
                Outcome = CenteringOutcome.NoneEvaluated, FinishReason = reason, Algorithm = Algorithm, Notes = notes,
                Surrogate = _center.EffectiveSurrogate, SwitchedBackAt = switchedAt,
                Refusal = StatisticsDiagnostics.CenterNoneEvaluated(searchEvaluations, first), Iterations = iterations,
                Evaluations = searchEvaluations, History = history, Start = start,
            };
        }

        // ── R-ya11-5: the start and the best point on the same fresh trials ─────────────
        var railed = Variables!.Railed(bestU);
        _options.Progress?.Invoke(new CenteringProgress("verify", iterations, searchEvaluations, sw.Elapsed,
                                                        best.Yield.Yield, best.Objective, best.Values, railed));
        var (startRun, startVerified) = Verify(start.Values, null, notes, "start");
        var (bestRun, bestVerified) = Verify(best.Values, _options.ResultPath, notes, "best");
        if (startVerified?.Outcome == StatisticalOutcome.Cancelled || bestVerified?.Outcome == StatisticalOutcome.Cancelled
            || ct.IsCancellationRequested)
        {
            if (bestVerified?.WrittenPath is { } partial && File.Exists(partial)) File.Delete(partial);
            return Result = new CenteringResult { Outcome = CenteringOutcome.Cancelled, FinishReason = "cancelled", Algorithm = Algorithm, Notes = _notes };
        }

        CenteringVerification? verification = null;
        var outcome = CenteringOutcome.Finished;
        if (startVerified?.Data is not null && bestVerified?.Data is not null)
        {
            var a = startVerified.Yield;
            var b = bestVerified.Yield;
            bool overlap = b.Lower <= a.Upper && a.Lower <= b.Upper;
            string Pct(double f) => (f * 100).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " %";
            string sentence = $"yield {Pct(a.Yield)} [{Pct(a.Lower)}, {Pct(a.Upper)}] → {Pct(b.Yield)} [{Pct(b.Lower)}, {Pct(b.Upper)}] " +
                              $"on the same {bestVerified.Trials} fresh trials; " +
                              (overlap ? "the gain is within the intervals' overlap — not resolved at this many trials"
                                       : "the intervals do not overlap");
            verification = new CenteringVerification(_center.EffectiveSeed + 1, bestVerified.Trials, a, b, overlap, sentence);
            if (_statistics.Target is { } target && !double.IsNaN(b.Yield) && b.Yield < target / 100) outcome = CenteringOutcome.BelowTarget;
        }
        else if (bestVerified?.Outcome == StatisticalOutcome.NoneEvaluated) outcome = CenteringOutcome.NoneEvaluated;

        return Result = new CenteringResult
        {
            Outcome = outcome, FinishReason = reason, Algorithm = Algorithm, Notes = notes, Iterations = iterations,
            Surrogate = _center.EffectiveSurrogate, SwitchedBackAt = switchedAt,
            Evaluations = searchEvaluations, VerifyEvaluations = (startRun?.Evaluations ?? 0) + (bestRun?.Evaluations ?? 0),
            History = history, Start = start, Best = best, Railed = railed, Verification = verification, Verified = bestVerified,
        };
    }

    /// <summary>One point's verification: an ordinary yield run with the point's values bound — the nominal each spread
    /// is drawn around — on <c>verify</c> trials at the next seed.</summary>
    private (StatisticalRun? Run, StatisticalResult? Result) Verify(IReadOnlyDictionary<string, string> values, string? resultPath,
                                                                    List<Diagnostic> notes, string which)
    {
        var run = StatisticalRun.Create(_circuit, new StatisticalOptions
        {
            Mode = StatisticalMode.Yield, Setup = WithTrials(_center.EffectiveVerify, _center.EffectiveSeed + 1),
            Sets = _options.Sets, Cancellation = _options.Cancellation, Bindings = values, ResultPath = resultPath,
        });
        if (run.Refusal is { } refused) { notes.Add(StatisticsDiagnostics.CenterVerifyFailed(which, refused.Render())); return (run, null); }
        var result = run.Run();
        if (result.Outcome == StatisticalOutcome.Refused)
            notes.Add(StatisticsDiagnostics.CenterVerifyFailed(which, result.Refusal?.Render() ?? result.FinishReason));
        return (run, result);
    }

    /// <summary>The setup with its statistics line set to <paramref name="trials"/> trials at <paramref name="seed"/>:
    /// no auto-stop, no corners, the centering's parallelism.</summary>
    private TuningSetup WithTrials(int trials, int seed)
    {
        var setup = _setup.Clone();
        var st = setup.Statistics?.Clone() ?? new StatisticsSettings();
        st.Trials = trials;
        st.Seed = seed;
        st.AutoStop = false;
        st.Corners = null;
        if (_center.Parallelism is { } p) st.Parallelism = p;
        setup.Statistics = st;
        return setup;
    }

    private static double OptionDefault(string name)
        => OptimizerAlgorithms.CommonOptions.First(o => o.Name == name).NumericDefault ?? 0;

    private CenteringResult Refusing(Diagnostic why) => new()
    {
        Outcome = CenteringOutcome.Refused, Refusal = why, FinishReason = why.Render(), Algorithm = Algorithm, Notes = _notes,
    };
}
