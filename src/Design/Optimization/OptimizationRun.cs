using System.Diagnostics;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Matching;
using CircuitRF.Design.Statistics;
using CircuitRF.Diagnostics;
using CircuitRF.Engine;
using CircuitRF.Engine.Optimization;
using RfCore.Data;

namespace CircuitRF.Design.Optimization;

/// <summary>What a caller may change about a run beyond what the design's own setup says.</summary>
public sealed record OptimizationOptions
{
    /// <summary>The setup to run; null runs the one the circuit's netlist carries.</summary>
    public TuningSetup? Setup { get; init; }

    /// <summary><c>--set name=expr</c> overrides, applied before the tuned values as a run verb applies them.</summary>
    public IReadOnlyList<(string Name, string Expression)> Sets { get; init; } = [];

    /// <summary>Called after every completed iteration, on the run's thread.</summary>
    public Action<OptimizationProgress>? Progress { get; init; }

    /// <summary>Cancelling abandons the run (exit 130); Stop keeps the best point.</summary>
    public CancellationToken Cancellation { get; init; }

    /// <summary>The preferred-value ladders <c>discrete=preferred</c> snaps to — the user's, from the
    /// preferences, in the GUI; null is the shipped ones (brief-tuneopt-8 R-to8-2).</summary>
    public PreferredLadders? Ladders { get; init; }

    /// <summary>After a continuous run, snap every integer, stepped and preferred value and polish
    /// the rest (R-to8-4) — the CLI's flag. The Optimizer's action calls
    /// <see cref="OptimizationRun.SnapAndPolish"/> on a finished run instead.</summary>
    public bool SnapAndPolish { get; init; }

    /// <summary>The run statistical corners came from (<c>&lt;design&gt;.yield.npy</c>), when at hand: their recorded
    /// z-vectors are replayed as they stand (brief-yield-7 R-ya7-3). Null draws them afresh, with a note.</summary>
    public DataSet? Recorded { get; init; }

    /// <summary>brief-sdl-7 D8 — for an evaluator of value maps (yield, Monte Carlo, sensitivity): false runs every
    /// evaluation by circuit models (<c>--em off</c>); null as each analysis and instance says.</summary>
    public bool? EvaluateEm { get; init; }

    /// <summary>brief-sdl-7 — an inner run of em=correct: its box, start, offsets and connectivity check.</summary>
    internal EmInner? Inner { get; init; }

    /// <summary>brief-sdl-7 — the em=correct run an inner run belongs to; its Stop and Pause hold the inner run too.</summary>
    internal OptimizationRun? Parent { get; init; }
}

/// <summary>The stages a run names in its progress (brief-tuneopt-8 R-to8-5). A run of one named
/// algorithm has no stage (null).</summary>
public static class OptimizationStages
{
    public const string Global     = "global search (CMA-ES)";
    public const string PolishLm   = "local polish (Levenberg–Marquardt)";
    public const string PolishMax  = "local polish (Minimax)";
    public const string Snap       = "snap to legal values";
    /// <summary>Auto on a problem of vendor parts alone (brief vendorlib-4 R-vl4-3c).</summary>
    public const string PartSearch = "discrete search over the candidate parts";
    public const string SnapPolish = "polish the continuous values";
    /// <summary>brief-sdl-7 D2 — em=correct: the circuit model searches, a few EM solves correct it.</summary>
    public const string EmCorrect  = "circuit model corrected by EM";
}

/// <summary>
/// What snap-and-polish did (R-to8-4): the cost at the continuous best, at the best snapped
/// neighbour, and after polishing the continuous values (the snapped cost when there were none).
/// </summary>
/// <param name="Snapped">The integer, stepped and preferred values snapped.</param>
/// <param name="Neighbours">The snapped points evaluated: 2^k when k ≤ 6, else the nearest only.</param>
/// <param name="Parts">How many of the snapped values are vendor parts — when any are, <c>CostBefore</c> is the cost
/// with IDEAL components in their place and <c>CostSnapped</c> the cost with the real parts (brief vendorlib-4 R-vl4-3b).</param>
public sealed record SnapResult(int Snapped, int Neighbours, double CostBefore, double CostSnapped, double CostAfter, bool Polished,
                                int Parts = 0);

/// <summary>The part an optimization chose for one vendor part entry (brief vendorlib-4 R-vl4-4a).</summary>
/// <param name="Key">The entry's key (<c>C1.Part</c>).</param>
/// <param name="Instance">The instance, as the key names it.</param>
/// <param name="Part">The part number.</param>
/// <param name="ValueText">Its stated value (<c>10 pF</c>), or empty.</param>
/// <param name="Library">The vendor library it comes from.</param>
/// <param name="File">The file it is in, relative to the library root.</param>
public sealed record ChosenPart(string Key, string? Cell, string Instance, string Part, string ValueText, string? Description,
                                string Library, string File, string Glyph, bool Changed);

/// <summary>One coordinate's sensitivity at a point (R-to8-6).</summary>
/// <param name="Key">The entry — a part of a complex value is its own coordinate.</param>
/// <param name="PerRange">∂cost/∂u: the cost change across the variable's whole range, in its own
/// scale (lin or log), to first order — the normalized derivative.</param>
/// <param name="Share">|PerRange| as a fraction of the sum over all coordinates.</param>
public sealed record VariableSensitivity(string Key, double PerRange, double Share);

/// <summary>Per goal, the coordinate that moves its cost most, and by how much per range.</summary>
public sealed record GoalSensitivity(string Goal, string? MostSensitive, double PerRange);

/// <summary>A sensitivity pass (R-to8-6): n difference evaluations at one point, cached ones free.</summary>
public sealed record SensitivityReport(
    double Cost, IReadOnlyList<VariableSensitivity> Variables, IReadOnlyList<GoalSensitivity> Goals,
    long Evaluations, long CacheHits);

/// <summary>How a run ended (overview D15 gives each its exit code).</summary>
public enum OptimizationOutcome
{
    /// <summary>Finished, and every enabled goal is met — exit 0.</summary>
    GoalsMet,
    /// <summary>Finished with at least one goal unmet — exit 3.</summary>
    GoalsUnmet,
    /// <summary>Refused before or at the first evaluation — exit 1.</summary>
    Refused,
    /// <summary>No evaluation converged — exit 2.</summary>
    NoConvergence,
    /// <summary>Cancelled — exit 130, and nothing is written.</summary>
    Cancelled,
}

/// <summary>One goal at the best point: its worst violation, where, and whether it is met. A met goal
/// reports its tightest point instead, and <paramref name="Margin"/> how far inside its limit that is
/// (<see cref="GoalScore.Margin"/>). Across corners (brief-yield-7 R-ya7-2) every member is the
/// <paramref name="Corner"/>'s — the BINDING corner, where the worst violation (met: the tightest margin) is — and
/// <paramref name="PerCorner"/> is the goal at each corner, in evaluation order. Both null without corners.</summary>
public sealed record GoalReport(string Name, double WorstViolation, double? WorstAt, string? Axis, double WorstValue, bool Met,
                                double Margin = double.NaN, string? Corner = null,
                                IReadOnlyList<CornerGoalScore>? PerCorner = null);

/// <summary>One goal at one corner of a point (brief-yield-7 R-ya7-2).</summary>
public sealed record CornerGoalScore(string Corner, bool Met, double WorstViolation, double WorstValue, double? WorstAt, double Margin)
{
    internal static CornerGoalScore Of(string corner, GoalScore s)
        => new(corner, s.Met, s.WorstViolation, s.WorstValue, s.WorstAt, s.Margin);
}

/// <summary>What a run reports after every iteration (R-to6-9). The Optimizer window and MCP read the same one.</summary>
public sealed record OptimizationProgress(
    int                                 Iteration,
    long                                Evaluations,
    long                                Failures,
    long                                Infeasible,
    long                                CacheHits,
    TimeSpan                            Elapsed,
    double                              CurrentCost,
    double                              BestCost,
    IReadOnlyDictionary<string, string> BestValues,
    IReadOnlyList<GoalReport>           Goals,
    IReadOnlyList<RailedVariable>       Railed,
    DataSet?                            BestData,
    string?                             Stage = null);

/// <summary>One point the algorithm was told about, in order.</summary>
public sealed record EvaluationRecord(
    double[] Point, DecodedPoint Decoded, double Cost, bool Failed, bool Infeasible, bool Cached)
{
    /// <summary>brief-sdl-7 D9 — why an infeasible point was not simulated, when the reason is its connectivity
    /// (<c>L90 = 3.912 mm: ML7 overlaps MT2 (nets n4, n9 touch)</c>); null otherwise.</summary>
    public string? Why { get; init; }
}

/// <summary>Whether a point evaluated (yield overview D7).</summary>
public enum PointStatus
{
    /// <summary>It simulated and every goal could be scored.</summary>
    Evaluated,
    /// <summary>It did not: an analysis failed or did not converge, a goal had no value, or the run
    /// was refused — <see cref="PointEvaluation.Reason"/> says which.</summary>
    DidNotEvaluate,
}

/// <summary>
/// One value map evaluated through <see cref="OptimizationRun.EvaluateValues"/>.
/// </summary>
/// <param name="Values">The value map as given.</param>
/// <param name="Reason">Why it did not evaluate; null when it did.</param>
/// <param name="Goals">Each goal's score — its worst violation, whether it is met, its margin
/// (<see cref="GoalScore.Margin"/>). Empty when the point did not reach scoring.</param>
/// <param name="Cost">The run's cost form over the goals' residuals; NaN when it did not evaluate.</param>
/// <param name="Data">The point's grouped results; null when discarded, when the cache answered it
/// (the cache keeps scores, not results), or when its goals read only variables.</param>
/// <param name="Cached">The cache answered it, from an earlier call or an identical map in this one.</param>
public sealed record PointEvaluation(
    IReadOnlyDictionary<string, string> Values,
    PointStatus                         Status,
    Diagnostic?                         Reason,
    IReadOnlyList<GoalScore>            Goals,
    double                              Cost,
    DataSet?                            Data,
    bool                                Cached)
{
    /// <summary>It evaluated and every goal is met (yield overview D4) — vacuously, with no goals.</summary>
    public bool Pass => Status == PointStatus.Evaluated && Goals.All(g => g.Met);

    /// <summary>brief-sdl-7 — each goal's expression value, before scoring; null when it did not evaluate or the cache
    /// answered. What em=correct's goal-space offset is made of.</summary>
    public IReadOnlyList<Value?>? GoalValues { get; init; }
}

/// <summary>
/// One point for <see cref="OptimizationRun.EvaluateValues(IReadOnlyList{ValuePoint}, bool, CancellationToken)"/>:
/// a value map, and optionally one Monte Carlo trial's draws for the design's distribution calls
/// (<see cref="CircuitEvaluationRequest.Statistics"/>) and a tag that keeps its cache key apart from every other
/// point's. A point with draws needs a tag: its value map alone does not say what it simulates.
/// </summary>
public sealed record ValuePoint(
    IReadOnlyDictionary<string, string> Values,
    IStatisticalDraws?                  Draws = null,
    string?                             Tag   = null)
{
    /// <summary>brief-sar-10 R-sar10-5 — set: the point's fields analyses are recombined and written in full, under this label
    /// beside their own results (a re-run trial, for the 3D view). Null: they evaluate on demand and write nothing.</summary>
    public string? WriteFieldsAs { get; init; }
}

/// <summary>What a run produced.</summary>
public sealed class OptimizationResult
{
    public required OptimizationOutcome Outcome { get; init; }

    /// <summary>Overview D15: 0, 3, 1, 2 or 130.</summary>
    public int ExitCode => Outcome switch
    {
        OptimizationOutcome.GoalsMet      => 0,
        OptimizationOutcome.GoalsUnmet    => 3,
        OptimizationOutcome.Refused       => 1,
        OptimizationOutcome.NoConvergence => 2,
        _                                 => 130,
    };

    /// <summary>Why it stopped, as a sentence.</summary>
    public string FinishReason { get; init; } = "";

    /// <summary>The refusal, for <see cref="OptimizationOutcome.Refused"/> and
    /// <see cref="OptimizationOutcome.NoConvergence"/>.</summary>
    public Diagnostic? Refusal { get; init; }

    public string Algorithm { get; init; } = "";

    /// <summary>The stages that ran, in order (Auto, snap-and-polish); empty for one algorithm alone.</summary>
    public IReadOnlyList<string> Stages { get; init; } = [];

    /// <summary>What snap-and-polish did; null when it did not run.</summary>
    public SnapResult? Snap { get; init; }

    /// <summary>The part chosen for each vendor part entry, in the setup's order; empty when none is optimized.</summary>
    public IReadOnlyList<ChosenPart> Parts { get; init; } = [];

    public IReadOnlyList<Diagnostic> Notes { get; init; } = [];

    /// <summary>The best point's tuned values, value key → text (a complex value whole) — what lock-in
    /// stores and Push writes. Empty when nothing succeeded.</summary>
    public IReadOnlyDictionary<string, string> BestValues { get; init; } = new Dictionary<string, string>();
    public double[]? BestPoint { get; init; }
    public double? BestCost { get; init; }
    public IReadOnlyList<GoalReport> Goals { get; init; } = [];
    public IReadOnlyList<RailedVariable> Railed { get; init; } = [];
    public DataSet? BestData { get; init; }

    public int Iterations { get; init; }
    public long Evaluations { get; init; }
    public long Failures { get; init; }
    public long Infeasible { get; init; }
    public long CacheHits { get; init; }

    /// <summary>Every point the algorithm was told about, in order.</summary>
    public IReadOnlyList<EvaluationRecord> Log { get; init; } = [];

    /// <summary>R-to6-10: group <c>opt</c> — cubes over <c>eval</c> and over <c>iter</c>. The caller
    /// decides whether to write it.</summary>
    public DataSet? History { get; init; }

    // ── brief-sdl-7 ──────────────────────────────────────────────────────────

    /// <summary>How analyses simulated by EM were treated.</summary>
    public OptimizerEmMode EmMode { get; init; }

    /// <summary>Every EM evaluation the run made, in order (R-sdl7-4); empty under em=off.</summary>
    public IReadOnlyList<EmSolveRow> EmSolves { get; init; } = [];

    /// <summary>The uncached EM solves the run spent.</summary>
    public int EmBudgetUsed { get; init; }

    /// <summary>The run stopped at its EM budget; its best point is the best EM-verified one.</summary>
    public bool StoppedByBudget { get; init; }

    /// <summary>The geometry key of the best EM-verified point — what Apply checks the saved layout against.</summary>
    public string? BestEmKey { get; init; }

    /// <summary>D10 — each variable's connected interval around its start.</summary>
    public IReadOnlyList<ConnectedRange> ConnectedRanges { get; init; } = [];
}

/// <summary>
/// One optimization of one prepared circuit (brief-tuneopt-6): the variables decoded from the unit
/// box, the goals scored into residuals, the evaluations cached, run in parallel where the design
/// allows, stopped by its limits, paused and resumed, reported per iteration, and kept as a history.
/// The algorithm only ever sees a cost, a residual vector and a failed flag.
///
/// <para><b>Evaluations</b> counts simulations actually run. A point the cache already holds costs
/// nothing (<see cref="CacheHits"/>); an infeasible point (D18) is not simulated
/// (<see cref="Infeasible"/>); a non-converged or failed simulation is a <see cref="Failures"/>.</para>
///
/// <para><b>Two doors, one evaluator.</b> <see cref="EvaluateValues"/> evaluates value maps (key →
/// value text) — the Monte Carlo and yield runs' door, built with <see cref="ForEvaluation"/>. The
/// optimizer's own batches are unit-box points, decoded into value maps and handed to the same
/// method, so the cache, the parallelism and the not-re-entrant rule are shared.</para>
///
/// <para><b>Penalties.</b> A failed point costs 10·(1 + the largest successful cost seen so far,
/// this batch included), so it ranks below every real point; an infeasible one costs that plus its
/// normalized distance to the region, so ranking pushes toward feasibility. Penalties are assigned
/// after the whole batch, in batch order, so concurrency cannot change them.</para>
/// </summary>
public sealed partial class OptimizationRun
{
    /// <summary>The default iteration limit when the setup states none.</summary>
    public const int DefaultMaxIterations = 100;

    // Stall — a run whose best cost improved by less than stall_tol of itself over stall_iters
    // iterations stops — reads its defaults from OptimizerAlgorithms.CommonOptions.

    /// <summary>The registry's algorithms this build implements, in menu order (R-to7-7).</summary>
    public static IReadOnlyList<string> Available { get; } =
        [.. OptimizerAlgorithms.Ids.Where(OptimizerFactory.IsBuilt)];

    private sealed record PointOutcome(
        bool Failed, Diagnostic? Error, double Cost, double[] Residuals, IReadOnlyList<GoalScore> Scores, bool GoalError)
    {
        /// <summary>Across corners: per goal, the binding corner's name.</summary>
        public IReadOnlyList<string>? Binding { get; init; }

        /// <summary>Across corners: each corner's goal scores, in evaluation order.</summary>
        public IReadOnlyList<(string Corner, IReadOnlyList<GoalScore> Scores)>? PerCorner { get; init; }

        /// <summary>brief-sdl-7 — each goal's value before scoring (and before any EM offset).</summary>
        public Value?[]? GoalValues { get; init; }
    }

    private readonly PreparedCircuit _circuit;
    private readonly OptimizationOptions _options;
    private readonly TuningSetup _setup = new();
    private readonly OptimizerSettings _settings = new();
    private readonly OptimizerCost _cost;
    private readonly List<OptimizationGoal> _goals = [];
    private readonly IReadOnlyList<string>? _analyses;
    private readonly bool _simulates;
    private readonly int _parallelism;
    private readonly List<Diagnostic> _notes = [];

    /// <summary>brief-sar-10 — a goal expression that reads a full SAR average (psSAR, or SarEff through it).</summary>
    private static readonly System.Text.RegularExpressions.Regex FullAverage = new(@"\b(psSAR|SarEff)\s*\(",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private readonly Dictionary<string, PointOutcome> _cache = new(StringComparer.Ordinal);

    // Across corners (brief-yield-7): the points each candidate is evaluated at, the nominal first when it is one;
    // the merged outcome of each candidate, by its own key (_cache holds one entry per (values, corner)); the
    // replay notes already reported.
    private readonly IReadOnlyList<CornerPoint>? _corners;
    private readonly Dictionary<string, PointOutcome> _combined = new(StringComparer.Ordinal);
    private readonly HashSet<string> _cornerNotes = new(StringComparer.Ordinal);
    private readonly List<EvaluationRecord> _log = [];
    private readonly List<(double Best, double[] Worst)> _iterHistory = [];
    private readonly ManualResetEventSlim _resume = new(true);
    private readonly ManualResetEventSlim _held = new(false);
    private volatile bool _stop;
    private bool _serialNoted;
    private double _maxFeasible;
    private long _evaluations, _failures, _infeasible, _cacheHits;

    private int _bestIndex = -1;
    private DataSet? _bestData;
    private Diagnostic? _firstFailure;

    // Per-run state the stages share (brief-tuneopt-8): the decode mode, the iteration count across
    // stages, the limits and where this invocation's count of them started.
    private readonly PreferredLadders _ladders;
    private bool _snapPreferred;
    private int _iterations;
    private string? _stage;
    private readonly List<string> _stages = [];
    private SnapResult? _snapResult;
    // Whether a vendor part decodes to a real candidate (true) or, in a continuous stage, to its ideal stand-in.
    private bool _snapParts = true;
    private Stopwatch _sw = new();
    private Dictionary<string, string>? _algOptions;
    private ulong _seed = 1;
    private int _maxIter = DefaultMaxIterations, _iterBase;
    private long _evalBase;
    private double? _limitS;
    private double _stallTol;
    private int _stallIters;

    internal OptimizationRun(PreparedCircuit circuit, OptimizationOptions options, GoalUse? evaluateFor = null)
    {
        _circuit = circuit;
        _options = options;
        _ladders = options.Ladders ?? PreferredLadders.Shipped;

        if (circuit.ReadError is { } readError || circuit.Lib is not { } lib || circuit.Tb is not { } tb)
        {
            Refusal = OptimizationDiagnostics.EvaluationFailed(circuit.ReadError ?? "Netlist read failed.");
            return;
        }

        _setup    = options.Setup ?? tb.Tuning ?? new TuningSetup();
        _settings = _setup.Optimizer ?? new OptimizerSettings();

        var bench = tb;
        if (options.Setup is not null)
        {
            bench = new TestBench(tb.Name) { Tuning = options.Setup };
            bench.Instances.AddRange(tb.Instances);
            bench.GlobalVariables.AddRange(tb.GlobalVariables);
            bench.Analyses.AddRange(tb.Analyses);
        }
        var catalog = TunableCatalog.FromNetlist(bench, lib, parts: circuit.PartTunables);
        foreach (var d in TuningValidator.Validate(bench, catalog, lib: lib))
        {
            if (d.Severity == DiagnosticSeverity.Error) { Refusal = d; return; }
            _notes.Add(d);
        }

        int cores = Math.Max(1, Environment.ProcessorCount - 1);
        if (evaluateFor is { } use)
        {
            // An evaluation of value maps (EvaluateValues): its goals, no variables, no algorithm. With
            // no goal — a Monte Carlo of spreads alone — every runnable analysis runs.
            _goals.AddRange(_setup.Goals.Where(g => g.Enabled && use switch
            {
                GoalUse.Opt   => g.ForOptimizer,
                GoalUse.Yield => g.ForYield,
                _             => true,
            }));
            var scope = use == GoalUse.Opt ? _settings.Scope : _setup.Statistics?.Scope ?? OptimizerScope.GoalAnalyses;
            var goalAnalyses = _goals.Select(g => g.Analysis).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
            bool all = scope == OptimizerScope.All || _goals.Count == 0;
            _simulates = all || goalAnalyses.Count > 0;
            _analyses  = all ? null : goalAnalyses;
            _cost      = _settings.Cost;
            _parallelism = Math.Max(1, (use == GoalUse.Opt ? null : _setup.Statistics?.Parallelism) ?? _settings.Parallelism ?? cores);
            _evalEm = options.EvaluateEm;
            return;
        }

        // A use=yield goal is a yield spec only (yield overview D4); the optimizer does not aim for it.
        _goals.AddRange(_setup.Goals.Where(g => g.Enabled && g.ForOptimizer));
        if (_goals.Count == 0)
        {
            var yieldOnly = _setup.Goals.Where(g => g.Enabled && !g.ForOptimizer).Select(g => g.Name).ToList();
            Refusal = yieldOnly.Count > 0 ? OptimizationDiagnostics.OnlyYieldGoals(string.Join(", ", yieldOnly))
                                          : OptimizationDiagnostics.NoGoals();
            return;
        }

        Variables = OptimizationVariables.Build(_setup, catalog, out var refusal, _ladders);
        if (refusal is not null || Variables is null) { Refusal = refusal ?? OptimizationDiagnostics.NoVariables(); return; }
        _notes.AddRange(Variables.Notes);

        AlgorithmId = _settings.Algorithm;
        if (!Available.Contains(AlgorithmId))
        {
            Refusal = OptimizerAlgorithms.Ids.Contains(AlgorithmId)
                ? OptimizationDiagnostics.AlgorithmUnavailable(AlgorithmId, string.Join(", ", Available))
                : TuningDiagnostics.UnknownAlgorithm(AlgorithmId, string.Join(", ", OptimizerAlgorithms.Ids));
            return;
        }
        if (AlgorithmId == Discrete.AlgorithmId && Variables.DiscreteUnavailable() is { } notDiscrete)
        {
            Refusal = notDiscrete;
            return;
        }
        if (Variables.ContinuousUnavailable(AlgorithmId) is { } partsOnly)
        {
            Refusal = partsOnly;
            return;
        }
        // The cost form (R-to7-6, R-to7-7): choosing Minimax sets minimax — least squares is the
        // default form, never one a setup can be said to insist on — while a least-squares method
        // refuses an explicit cost=minimax.
        var info = OptimizerAlgorithms.Find(AlgorithmId)!;
        if (_settings.Cost == OptimizerCost.Minimax && !info.Accepts(OptimizerCost.Minimax))
        {
            Refusal = OptimizationDiagnostics.LeastSquaresOnly(info.Label);
            return;
        }
        _cost = info.Accepts(_settings.Cost) ? _settings.Cost : info.Costs[0];
        if (InitEm(bench, lib, options) is { } emRefusal) { Refusal = emRefusal; return; }

        var named = _goals.Select(g => g.Analysis).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        _simulates = named.Count > 0;
        _analyses  = _settings.Scope == OptimizerScope.All ? null : named;
        var averaged = _goals.Where(g => FullAverage.IsMatch(g.Expression)).Select(g => $"'{g.Name}'").ToList();
        if (averaged.Count > 0) _notes.Add(OptimizationDiagnostics.FullAverageGoals(string.Join(", ", averaged)));

        _parallelism = Math.Max(1, _settings.Parallelism ?? cores);

        // Across corners (brief-yield-7 R-ya7-1): every goal met at the nominal and at each corner at once.
        var cornerNames = _settings.CornerNames;
        if (cornerNames is not { Count: 0 })
        {
            var prepared = CornerPoint.Prepare(circuit, _setup, cornerNames, _settings.Nominal != false, options.Recorded, options.Sets);
            if (prepared.Refusal is { } cornerRefusal) { Refusal = cornerRefusal; return; }
            _corners = prepared.Points;
            _notes.AddRange(prepared.Notes);
        }
    }

    /// <summary>
    /// The points one optimizer point is evaluated at under <paramref name="setup"/>'s optimize line (brief-yield-7
    /// R-ya7-5): <c>nominal</c> and the corners <c>corners=</c> names (all: every enabled one), or the nominal alone.
    /// Its count is what one point costs — what the Optimizer's status and <c>explain --analysis</c> show; a name
    /// that is not an enabled corner is counted, and refused when the run starts.
    /// </summary>
    public static IReadOnlyList<string> EvaluationPointsOf(TuningSetup setup)
    {
        var s = setup.Optimizer ?? new OptimizerSettings();
        if (s.CornerNames is { Count: 0 }) return [CornerRun.NominalName];
        var names = s.CornerNames ?? [.. setup.Corners.Where(c => c.Enabled).Select(c => c.Name)];
        return s.Nominal == false ? [.. names] : [CornerRun.NominalName, .. names];
    }

    /// <summary>
    /// The analysis chains an evaluation runs under <paramref name="scope"/> (brief-tuneopt-11 R-to11-7,
    /// <c>explain --analysis</c>): <see cref="OptimizerScope.GoalAnalyses"/> is the chains of the
    /// analyses the enabled goals name, each promoted to the sweep that wraps it as an evaluation
    /// promotes it; <see cref="OptimizerScope.All"/> is every runnable chain. Names of the chains' top
    /// analyses, in the bench's order; a goal naming no declared analysis contributes nothing.
    /// </summary>
    /// <param name="use">Whose goals: the optimizer's (<see cref="GoalUse.Opt"/>, the default), a yield run's
    /// (<see cref="GoalUse.Yield"/>) or a Monte Carlo's (<see cref="GoalUse.Both"/>, every enabled goal) — with none,
    /// a statistical run evaluates every runnable chain, as <see cref="ForEvaluation"/> does.</param>
    public static IReadOnlyList<string> AnalysesUnder(TestBench tb, TuningSetup setup, OptimizerScope scope,
                                                      GoalUse use = GoalUse.Opt)
    {
        var goals = setup.Goals.Where(g => g.Enabled && use switch
        {
            GoalUse.Opt   => g.ForOptimizer,
            GoalUse.Yield => g.ForYield,
            _             => true,
        }).ToList();
        if (scope == OptimizerScope.All || (use != GoalUse.Opt && goals.Count == 0))
            return [.. AnalysisChain.RunnableTops(tb).Select(a => a.Name)];
        var tops = new List<string>();
        foreach (var name in goals.Select(g => g.Analysis).OfType<string>())
        {
            var one = tb.Analyses.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
            if (one is null || !one.Enabled || !AnalysisChain.IsChainRunnable(one, tb)) continue;
            string top = AnalysisChain.PromoteToRunnableTop(one, tb).Name;
            if (!tops.Contains(top, StringComparer.Ordinal)) tops.Add(top);
        }
        return tops;
    }

    /// <summary>Prepares a run of <paramref name="circuit"/>'s setup. A run that cannot start carries
    /// its <see cref="Refusal"/>; <see cref="Run"/> then returns it at once.</summary>
    public static OptimizationRun Create(PreparedCircuit circuit, OptimizationOptions? options = null)
        => new(circuit, options ?? new OptimizationOptions());

    /// <summary>
    /// Prepares an evaluator of value maps (<see cref="EvaluateValues"/>) over <paramref name="circuit"/>:
    /// the setup is validated as for a run, and its enabled goals for <paramref name="goals"/> are scored —
    /// <see cref="GoalUse.Yield"/> the yield specs (D4), <see cref="GoalUse.Opt"/> the optimizer's,
    /// <see cref="GoalUse.Both"/> every enabled goal. It needs no optimized variable and no algorithm, and
    /// <see cref="Run"/> on it refuses. The analyses are the goals' (or, under the statistics' or the
    /// optimizer's <c>analyses=all</c>, or with no goal at all, every runnable one).
    /// </summary>
    public static OptimizationRun ForEvaluation(PreparedCircuit circuit, OptimizationOptions? options = null,
                                                GoalUse goals = GoalUse.Yield)
        => new(circuit, options ?? new OptimizationOptions(), goals);

    /// <summary>The goals this run scores, in setup order.</summary>
    public IReadOnlyList<OptimizationGoal> Goals => _goals;

    /// <summary>The points each candidate is evaluated at (brief-yield-7): <c>nominal</c> alone without corners.</summary>
    public IReadOnlyList<string> CornerNames => _corners is null ? [CornerRun.NominalName] : [.. _corners.Select(c => c.Name)];

    /// <summary>Simulations one optimizer point costs: 1, or one per point of <see cref="CornerNames"/>.</summary>
    public int EvaluationsPerPoint => _corners?.Count ?? 1;

    public Diagnostic? Refusal { get; }
    public OptimizationVariables? Variables { get; }

    /// <summary>The algorithm the setup names (<c>auto</c> runs the stages <see cref="OptimizationStages"/> lists).</summary>
    public string AlgorithmId { get; private set; } = "";

    /// <summary>What the run noted: a key that names nothing, a start moved into range, one point at a
    /// time and why.</summary>
    public IReadOnlyList<Diagnostic> Notes => _notes;

    public long Evaluations => Interlocked.Read(ref _evaluations);
    public long Failures    => Interlocked.Read(ref _failures);

    /// <summary>How many points an evaluation runs at once: the setup's <c>parallel=</c>, or the cores less one;
    /// 1 when the circuit is not re-entrant (<see cref="PreparedCircuit.NotReentrantReason"/>).</summary>
    public int Parallelism => _simulates && _circuit.NotReentrantReason is not null ? 1 : _parallelism;
    public long Infeasible  => _infeasible;
    public long CacheHits   => _cacheHits;

    /// <summary>Holds the run after the batch in flight (D16). An em=correct run's inner runs hold with it.</summary>
    public void Pause() => _resume.Reset();

    public void Resume() => _resume.Set();

    /// <summary>Ends the run after the batch in flight, keeping the best point.</summary>
    public void Stop() { _stop = true; _resume.Set(); }

    /// <summary>Whether the run is holding at a pause.</summary>
    public bool IsPaused => _held.IsSet;

    /// <summary>Signalled while the run holds at a pause.</summary>
    public WaitHandle Held => _held.WaitHandle;

    /// <summary>Runs to the end on the calling thread.</summary>
    public OptimizationResult Run()
    {
        if (Refusal is not null || Variables is null) return Refused(Refusal ?? OptimizationDiagnostics.NoVariables());

        var ct = _options.Cancellation;
        _sw = Stopwatch.StartNew();
        _seed = (ulong)(_settings.Seed ?? 1);
        _algOptions = _settings.Options is null ? null
            : _settings.Options.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        // A population schedule planned for an evaluation budget plans for the run's own limit.
        if (_settings.MaxEvaluations is { } maxEvals
            && OptimizerAlgorithms.Find(AlgorithmId)?.Option("budget") is not null
            && _algOptions?.ContainsKey("budget") != true)
            (_algOptions ??= new Dictionary<string, string>(StringComparer.Ordinal))["budget"] =
                maxEvals.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // Bayesian chooses one point per iteration unless the setup asks for parallel runs (R-to8-1).
        if (AlgorithmId == Bayesian.AlgorithmId && _settings.Parallelism is > 1 and var par
            && _algOptions?.ContainsKey("batch") != true)
            (_algOptions ??= new Dictionary<string, string>(StringComparer.Ordinal))["batch"] =
                par.ToString(System.Globalization.CultureInfo.InvariantCulture);

        _maxIter    = _settings.MaxIterations ?? DefaultMaxIterations;
        _limitS     = _settings.TimeLimit is { } tl ? TuningValidator.TimeLimitSeconds(tl) : null;
        _stallTol   = Option(_algOptions, "stall_tol");
        _stallIters = (int)Option(_algOptions, "stall_iters");
        _iterBase   = 0;
        _evalBase   = 0;

        string reason;
        try
        {
            // brief-sdl-7: an EM run checks its start, its variables and their ranges before anything is solved.
            if (EmMode != OptimizerEmMode.Off && _inner is null)
            {
                if (PrepareEm(ct) is { } emRefusal) return Refused(emRefusal);
                if (EmMode == OptimizerEmMode.Correct) return RunEmCorrect(ct);
                FloorSteps(AlgorithmId, _dbuStep.DefaultIfEmpty(0).Max());
            }
            if (_inner is { MinStep: > 0 } inner)
                foreach (var id in new[] { AlgorithmId, LevenbergMarquardt.AlgorithmId, Minimax.AlgorithmId }) FloorSteps(id, inner.MinStep);

            if (AlgorithmId == OptimizerAlgorithms.Auto)
            {
                var (why, refused) = RunAuto(ct);
                if (refused is { } r) return Refused(r);
                reason = why;
            }
            else
            {
                _snapPreferred = SearchesOnSeries(AlgorithmId);
                // A vendor part is a real candidate for Discrete, and its ideal stand-in for every continuous search,
                // which then ends on real parts (brief vendorlib-4 R-vl4-3b, owner decision D3).
                _snapParts = AlgorithmId == Discrete.AlgorithmId;
                var alg = Create(AlgorithmId, StartPoint, sub: null);
                var end = RunStage(alg, null, null, null, firstOfRun: true, ct);
                if (end.Refusal is { } r) return Refused(r);
                reason = end.Reason;
                // A standard-value row is searched continuously and must still END on a rung, whatever the
                // algorithm: a result between rungs is not the value the row asked for. An ideal stand-in for a
                // vendor part is not a part anyone can buy, so the same holds for it.
                bool snap = (_options.SnapAndPolish || SeriesRows || PartRows) && _inner is null;
                if (snap && AlgorithmId != Discrete.AlgorithmId && (end.Kind != StageEnd.Stopped || SeriesRows || PartRows)
                    && _bestIndex >= 0 && Variables.DiscreteCoordinates.Any())
                    reason = SnapStage(ct) ?? reason;
            }
        }
        catch (OperationCanceledException)
        {
            return Finish(OptimizationOutcome.Cancelled, "cancelled", null);
        }
        return Complete(reason);
    }

    /// <summary>
    /// Snap and polish (brief-tuneopt-8 R-to8-4) on a finished run — the Optimizer's action: every
    /// integer, stepped and preferred value to its legal neighbours, the best of them kept, the
    /// continuous values re-optimized from there. The run's limits apply afresh, without its time
    /// limit; the result replaces the run's.
    /// </summary>
    public OptimizationResult SnapAndPolish(CancellationToken ct = default)
    {
        if (Refusal is not null || Variables is null) return Refused(Refusal ?? OptimizationDiagnostics.NoVariables());
        if (_bestIndex < 0 || !Variables.DiscreteCoordinates.Any()) return Refused(OptimizationDiagnostics.NothingToSnap());
        _sw = Stopwatch.StartNew();
        _limitS = null;
        _iterBase = _iterations;
        _evalBase = Evaluations;
        _stop = false;
        string reason;
        try { reason = SnapStage(ct) ?? "snapped"; }
        catch (OperationCanceledException) { return Finish(OptimizationOutcome.Cancelled, "cancelled", null); }
        return Complete(reason);
    }

    private OptimizationResult Complete(string reason)
    {
        if (_bestIndex < 0)
            return Finish(OptimizationOutcome.NoConvergence, reason,
                          OptimizationDiagnostics.NoneConverged(Evaluations,
                              _firstFailure?.Render() ?? "every point was infeasible"));
        var best = OutcomeOf(_log[_bestIndex].Decoded.CacheKey);
        return Finish(best.Scores.All(s => s.Met) ? OptimizationOutcome.GoalsMet : OptimizationOutcome.GoalsUnmet,
                      reason, null);
    }

    // ── Stages ───────────────────────────────────────────────────────────────

    private enum StageEnd { GoalsMet, Finished, Budget, Stalled, Limit, Stopped }

    /// <summary>
    /// Whether any row asked for a standard series (E6…E96 or the preferred ladder). Such a run snaps even
    /// when STOPPED: its search is continuous, so the best point at a Stop lies between rungs, and Push
    /// would write a value the row never allowed (designer report, round 20 — the series showed as a label
    /// and the values kept were not on it). The snap alone runs; its polish already gives way to the Stop.
    /// </summary>
    private bool SeriesRows => Variables!.Coordinates.Any(c => c.Preferred);

    /// <summary>Whether any optimized value is a vendor part with an ideal stand-in — a continuous search over one
    /// must end snapped to real parts, Stop or not.</summary>
    private bool PartRows => Variables!.Coordinates.Any(c => c.VendorPart is { HasProxy: true });

    /// <summary>
    /// Whether an algorithm searches a standard-series row ON its series — every point it simulates has that
    /// row on a rung — rather than continuously with the snap at the end. Every algorithm that estimates no
    /// derivative does (owner decision, designer report round 20: the series should be what the run uses, not
    /// a label on its result); one that estimates derivatives cannot, since a rung has none, and keeps the
    /// continuous search and the snap. So does the trust-region MODEL, which estimates no derivative but fits a
    /// smooth surface to the points it has: on a series' staircase the fit is flat and it stalled where it
    /// started (an E12 L-section: 8.2 nH / 1.5 pF at cost 0.258, where every other algorithm found 15 nH /
    /// 1.2 pF at 0.0041). Integers and steps are on their grid for every algorithm already.
    /// </summary>
    /// <summary>Whether the best point so far has a standard-series row BETWEEN its rungs — true only while a
    /// continuous search is under way (a gradient algorithm, or Auto's polish); a finished or stopped run is
    /// snapped. What the Optimizer asks before it lets a paused best be kept.</summary>
    public bool BestOffSeries =>
        Variables is not null && (SeriesRows || PartRows) && _bestIndex >= 0 && _bestIndex < _log.Count &&
        DecodeAt(_log[_bestIndex].Point, snapPreferred: true, snapParts: true).CacheKey != _log[_bestIndex].Decoded.CacheKey;

    internal static bool SearchesOnSeries(string algorithmId) =>
        algorithmId == Discrete.AlgorithmId ||
        algorithmId != TrustRegionModel.AlgorithmId && OptimizerAlgorithms.Find(algorithmId) is { NeedsGradients: false };

    private sealed record StageOutcome(StageEnd Kind, string Reason, Diagnostic? Refusal = null)
    {
        /// <summary>Whether the run ends here, whatever stages remain.</summary>
        public bool EndsRun => Kind is StageEnd.Limit or StageEnd.Stopped;
    }

    /// <summary>The algorithm for this run's options and seed, over <paramref name="sub"/>'s
    /// coordinates (all when null), with the discrete levels it needs.</summary>
    private IOptimizerAlgorithm Create(string id, double[] start, int[]? sub)
    {
        IReadOnlyList<double[]>? levels = null;
        if (id == Discrete.AlgorithmId)
            levels = [.. Variables!.Coordinates.Select(c => (c.Levels ?? []).Select(c.Encode).ToArray())];
        return OptimizerFactory.Create(id, sub is null ? start : [.. sub.Select(i => start[i])], _seed, _algOptions, levels)!;
    }

    /// <summary>
    /// Auto (R-to8-5): CMA-ES for a modest budget — 50(n + 1) evaluations, or half the run's
    /// <c>maxevals</c> when that is fewer — then Levenberg–Marquardt (Minimax under minimax) from its
    /// best point, then snap-and-polish when any value is integer, stepped or preferred. A stage ends
    /// on its own finish, its budget or a stall; every enabled goal met skips to the snap, and a run
    /// limit or Stop ends the run.
    /// </summary>
    private (string Reason, Diagnostic? Refusal) RunAuto(CancellationToken ct)
    {
        int n = Variables!.Coordinates.Count;

        // Vendor parts alone — or parts with only other listed values — are searched on their candidates by
        // Discrete, with no new algorithm (brief vendorlib-4 R-vl4-3a/3c).
        if (Variables.PartCoordinates.Any() && Variables.DiscreteUnavailable() is null)
        {
            _snapPreferred = _snapParts = true;
            var parts = RunStage(Create(Discrete.AlgorithmId, StartPoint, null), null, OptimizationStages.PartSearch,
                                 null, firstOfRun: true, ct);
            return parts.Refusal is { } pr ? ("refused", pr) : (parts.Reason, null);
        }
        _snapParts = false;

        // Fifty points per coordinate, whatever each point costs: across corners a point is several evaluations.
        long budget = 50L * (n + 1) * EvaluationsPerPoint;
        if (_settings.MaxEvaluations is { } me) budget = Math.Min(budget, Math.Max(1, me / 2));

        _snapPreferred = SearchesOnSeries(CmaEs.AlgorithmId);
        var global = RunStage(Create(CmaEs.AlgorithmId, StartPoint, null), null, OptimizationStages.Global,
                              budget, firstOfRun: true, ct);
        if (global.Refusal is { } r) return ("refused", r);
        var last = global;
        // The polish estimates derivatives, which a value held on its series does not have: it moves every value
        // continuously again, and the snap below brings the series values back.
        _snapPreferred = false;

        if (!global.EndsRun && global.Kind != StageEnd.GoalsMet && _bestIndex >= 0)
        {
            bool minimax = _cost == OptimizerCost.Minimax;
            string id = minimax ? Minimax.AlgorithmId : LevenbergMarquardt.AlgorithmId;
            last = RunStage(Create(id, _log[_bestIndex].Point, null), null,
                            minimax ? OptimizationStages.PolishMax : OptimizationStages.PolishLm, null, false, ct);
        }

        string reason = last.Reason;
        if ((last.Kind != StageEnd.Stopped || SeriesRows || PartRows) && _bestIndex >= 0 && Variables.DiscreteCoordinates.Any()
            && _inner is null)
            reason = SnapStage(ct) ?? reason;
        return (reason, null);
    }

    /// <summary>
    /// The snap (R-to8-4): the continuous best's integer, stepped and preferred values to the legal
    /// values either side — every combination (2^k points) when k ≤ 6, else the nearest only — the
    /// best kept, then the continuous values re-optimized from it with the snapped ones held. From
    /// here on the run's best is a SNAPPED point: the continuous best is not a design anyone can build.
    /// Returns the reason the last stage ended, or null when no polish ran.
    /// </summary>
    private string? SnapStage(CancellationToken ct)
    {
        var vars = Variables!;
        var discrete = vars.DiscreteCoordinates.ToArray();
        var from = _log[_bestIndex];
        double before = from.Cost;

        // Each discrete coordinate's legal values either side of where the continuous run left it.
        var choices = new List<double[]>();
        foreach (int i in discrete)
        {
            var c = vars.Coordinates[i];
            double v = c.Scale == TuneScale.Log ? c.Min * Math.Pow(c.Max / c.Min, from.Point[i]) : c.Min + from.Point[i] * (c.Max - c.Min);
            var (below, above) = PreferredValues.Bracket(v, c.Levels!);
            choices.Add(below == above ? [c.Encode(below)] : [c.Encode(below), c.Encode(above)]);
        }
        var batch = new List<double[]>();
        if (discrete.Length <= 6)
        {
            int combos = choices.Aggregate(1, (p, ch) => p * ch.Length);
            for (int k = 0; k < combos; k++)
            {
                var x = (double[])from.Point.Clone();
                int rest = k;
                for (int d = 0; d < discrete.Length; d++)
                {
                    x[discrete[d]] = choices[d][rest % choices[d].Length];
                    rest /= choices[d].Length;
                }
                batch.Add(x);
            }
        }
        else
        {
            var x = (double[])from.Point.Clone();
            foreach (int i in discrete)
            {
                var c = vars.Coordinates[i];
                double v = c.Decode(x[i]);
                x[i] = c.Encode(c.Preferred ? PreferredValues.Snap(v, c.Levels!) : NearestLevel(c, v));
            }
            batch.Add(x);
        }

        // From here the best is among snapped points only — real parts included.
        _snapPreferred = true;
        bool relaxedParts = !_snapParts && discrete.Any(i => vars.Coordinates[i].VendorPart is { HasProxy: true });
        _snapParts = true;
        _bestIndex = -1;
        _bestData = null;
        _stage = OptimizationStages.Snap;
        _stages.Add(_stage);
        ct.ThrowIfCancellationRequested();
        int first = _log.Count;
        EvaluateBatch(batch, ct);
        _iterations++;
        RecordIteration(_iterations, _sw.Elapsed, first);
        if (_bestIndex < 0)
        {
            _snapResult = new SnapResult(discrete.Length, batch.Count, before, double.NaN, double.NaN, false,
                                         vars.PartCoordinates.Count());
            return "no snapped point could be evaluated";
        }
        double snapped = _log[_bestIndex].Cost;

        // Polish the continuous values with the snapped ones held.
        var continuous = Enumerable.Range(0, vars.Coordinates.Count).Except(discrete).ToArray();
        string? reason = null;
        bool polished = false;
        if (continuous.Length > 0 && snapped > 0 && !_stop && !LimitReached(out _))
        {
            bool minimax = _cost == OptimizerCost.Minimax;
            var basePoint = _log[_bestIndex].Point;
            var alg = Create(minimax ? Minimax.AlgorithmId : LevenbergMarquardt.AlgorithmId, basePoint, continuous);
            var end = RunStage(alg, (continuous, basePoint), OptimizationStages.SnapPolish, null, false, ct);
            reason = end.Reason;
            polished = true;
        }
        double after = _log[_bestIndex].Cost;
        int partCount = relaxedParts ? vars.PartCoordinates.Count() : 0;
        _snapResult = new SnapResult(discrete.Length, batch.Count, before, snapped, after, polished, partCount);
        _notes.Add(OptimizationDiagnostics.SnapReport(discrete.Length, before, snapped, after, polished));
        if (partCount > 0) _notes.Add(OptimizationDiagnostics.PartSnapReport(partCount, before, snapped));
        return reason ?? (snapped == 0 ? "every enabled goal is met" : $"the best of {batch.Count} snapped point(s) was kept");
    }

    private static double NearestLevel(OptimizationCoordinate c, double v)
    {
        double best = c.Levels![0];
        foreach (double l in c.Levels) if (Math.Abs(l - v) < Math.Abs(best - v)) best = l;
        return best;
    }

    /// <summary>
    /// One stage: ask, evaluate, tell, until the algorithm finishes, the stage's evaluation
    /// <paramref name="budget"/> is spent, it stalls, every goal is met, a run limit is reached, or
    /// Stop. <paramref name="sub"/> embeds an algorithm over some coordinates into the full point.
    /// </summary>
    private StageOutcome RunStage(IOptimizerAlgorithm alg, (int[] Coords, double[] Base)? sub, string? stage,
                                  long? budget, bool firstOfRun, CancellationToken ct)
    {
        _stage = stage;
        if (stage is not null) _stages.Add(stage);
        long evalStart = Evaluations;
        int historyStart = _iterHistory.Count;
        int algIterations = 0;
        bool first = firstOfRun;

        var ctl = Ctl;
        while (true)
        {
            if (ctl._stop) return new(StageEnd.Stopped, "stopped");
            if (!ctl._resume.IsSet)
            {
                ctl._held.Set();
                ctl._resume.Wait(ct);
                ctl._held.Reset();
                if (ctl._stop) return new(StageEnd.Stopped, "stopped");
            }
            ct.ThrowIfCancellationRequested();

            var asked = alg.Ask();
            if (asked.Count == 0) return new(StageEnd.Finished, alg.FinishReason ?? "the algorithm finished");
            var batch = sub is { } e ? asked.Select(x => Embed(x, e.Coords, e.Base)).ToList() : asked;
            int before = _log.Count;
            var results = EvaluateBatch(batch, ct);
            // brief-sdl-7 D4: em=direct stops at its budget, keeping its best EM point.
            if (_budgetHit) return new(StageEnd.Limit, $"its EM budget of {_emBudget} solve{(_emBudget == 1 ? "" : "s")} was spent");

            // A goal that cannot be scored at the start point (a complex value, an axis it does not
            // have) will not be scored anywhere: that is a refusal, not 100 failed evaluations.
            if (first)
            {
                first = false;
                if (TryOutcome(_log[before].Decoded.CacheKey, out var start) && start.GoalError)
                    return new(StageEnd.Stopped, "refused", start.Error!);
            }

            alg.Tell(results);
            if (alg.Iterations > algIterations)
            {
                _iterations += alg.Iterations - algIterations;
                algIterations = alg.Iterations;
                RecordIteration(_iterations, _sw.Elapsed, before);
            }

            if (BestCost() == 0)                       return new(StageEnd.GoalsMet, "every enabled goal is met");
            if (alg.IsFinished)                        return new(StageEnd.Finished, alg.FinishReason ?? "the algorithm finished");
            if (LimitReached(out var limit))           return new(StageEnd.Limit, limit!);
            if (budget is { } b && Evaluations - evalStart >= b)
                return new(StageEnd.Budget, $"its budget of {b} evaluations was spent");
            if (Stalled(_stallIters, _stallTol, historyStart))
                return new(StageEnd.Stalled, $"the best cost improved by less than {_stallTol:G3} of itself over {_stallIters} iterations");
        }
    }

    private static double[] Embed(double[] x, int[] coords, double[] basePoint)
    {
        var full = (double[])basePoint.Clone();
        for (int k = 0; k < coords.Length; k++) full[coords[k]] = x[k];
        return full;
    }

    /// <summary>The run's iteration, evaluation and time limits, counted from this invocation's start.</summary>
    private bool LimitReached(out string? reason)
    {
        reason = null;
        if (_iterations - _iterBase >= _maxIter) reason = $"the iteration limit ({_maxIter}) was reached";
        else if (_settings.MaxEvaluations is { } me && Evaluations - _evalBase >= me) reason = $"the evaluation limit ({me}) was reached";
        else if (_limitS is { } s && _sw.Elapsed.TotalSeconds >= s) reason = $"the time limit ({_settings.TimeLimit}) was reached";
        return reason is not null;
    }

    // ── Sensitivity (R-to8-6) ────────────────────────────────────────────────

    /// <summary>
    /// The sensitivity at the run's best point (or <paramref name="at"/>): one batch of n
    /// forward-difference points — 1e-4 of the box for a continuous coordinate, the next legal value
    /// for a discrete one, backward at the top — evaluated through the cache, so a point already known
    /// costs nothing. Nothing it evaluates changes the run's best or its log. Null when the point has
    /// no successful evaluation.
    /// </summary>
    public SensitivityReport? Sensitivity(double[]? at = null, CancellationToken ct = default)
    {
        if (Variables is null) return null;
        var u0 = at ?? (_bestIndex >= 0 ? _log[_bestIndex].Point : null);
        if (u0 is null) return null;
        long evals0 = Evaluations, hits0 = _cacheHits;

        var points = new List<double[]> { u0 };
        var du = new double[u0.Length];
        for (int i = 0; i < u0.Length; i++)
        {
            var c = Variables.Coordinates[i];
            var x = (double[])u0.Clone();
            if (c.Levels is { Count: > 1 } levels)
            {
                double v = c.Decode(u0[i], _snapPreferred);
                int k = 0;
                for (int j = 1; j < levels.Count; j++) if (Math.Abs(levels[j] - v) < Math.Abs(levels[k] - v)) k = j;
                x[i] = c.Encode(levels[k + 1 < levels.Count ? k + 1 : k - 1]);
            }
            else x[i] = u0[i] + (u0[i] + 1e-4 <= 1 ? 1e-4 : -1e-4);
            du[i] = x[i] - u0[i];
            points.Add(x);
        }
        EvaluateBatch(points, ct, record: false);

        var keys = points.Select(p => DecodeAt(p, _snapPreferred, _snapParts)).ToArray();
        if (keys[0].Infeasible || !TryOutcome(keys[0].CacheKey, out var o0) || o0.Failed) return null;

        double GoalCost(PointOutcome o, int g)
        {
            var r = o.Scores[g].Residuals;
            return _cost == OptimizerCost.Minimax ? (r.Length == 0 ? 0 : r.Max()) : r.Sum(v => v * v);
        }
        var perRange = new double[u0.Length];
        var perGoal = new double[_goals.Count, u0.Length];
        for (int i = 0; i < u0.Length; i++)
        {
            if (du[i] == 0 || keys[i + 1].Infeasible || !TryOutcome(keys[i + 1].CacheKey, out var oi) || oi.Failed)
            { perRange[i] = double.NaN; continue; }
            perRange[i] = (oi.Cost - o0.Cost) / du[i];
            for (int g = 0; g < _goals.Count; g++) perGoal[g, i] = (GoalCost(oi, g) - GoalCost(o0, g)) / du[i];
        }
        double total = perRange.Where(double.IsFinite).Sum(Math.Abs);
        var variables = Variables.Coordinates.Select((c, i) => new VariableSensitivity(c.Key, perRange[i],
            double.IsFinite(perRange[i]) && total > 0 ? Math.Abs(perRange[i]) / total : 0)).ToList();
        var goals = new List<GoalSensitivity>();
        for (int g = 0; g < _goals.Count; g++)
        {
            int arg = -1;
            for (int i = 0; i < u0.Length; i++)
                if (double.IsFinite(perGoal[g, i]) && perGoal[g, i] != 0 && (arg < 0 || Math.Abs(perGoal[g, i]) > Math.Abs(perGoal[g, arg]))) arg = i;
            goals.Add(new GoalSensitivity(_goals[g].Name, arg < 0 ? null : Variables.Coordinates[arg].Key, arg < 0 ? 0 : perGoal[g, arg]));
        }
        return new SensitivityReport(o0.Cost, variables, goals, Evaluations - evals0, _cacheHits - hits0);
    }

    // ── One batch ────────────────────────────────────────────────────────────

    /// <summary>
    /// Evaluates value maps (key → value text, a complex value whole or by its parts, as
    /// <see cref="TunableOverrides"/> takes them) — the Monte Carlo and yield door (yield overview D11).
    /// Each map is evaluated as Simulate would evaluate the design with those values typed, through the
    /// same cache (keyed by the map's text in its own order), concurrency and not-re-entrant rule as the
    /// optimizer's own batches; nothing here touches the run's log or best point. With
    /// <paramref name="keepData"/> false each point's results are dropped once its goals are scored.
    /// A refused run reports every point as not evaluated, with the refusal. Cancelling throws
    /// <see cref="OperationCanceledException"/>. One call at a time: a call runs its points in
    /// parallel itself.
    /// </summary>
    public IReadOnlyList<PointEvaluation> EvaluateValues(
        IReadOnlyList<IReadOnlyDictionary<string, string>> points, bool keepData = true, CancellationToken ct = default)
        => EvaluateValues([.. points.Select(p => new ValuePoint(p))], keepData, ct);

    /// <summary>
    /// <see cref="EvaluateValues(IReadOnlyList{IReadOnlyDictionary{string, string}}, bool, CancellationToken)"/> over
    /// points that may carry a trial's draws: each is simulated with them, and its cache key is its value map's plus
    /// its <see cref="ValuePoint.Tag"/>.
    /// </summary>
    public IReadOnlyList<PointEvaluation> EvaluateValues(
        IReadOnlyList<ValuePoint> points, bool keepData = true, CancellationToken ct = default)
    {
        if (Refusal is { } refused)
            return [.. points.Select(p => new PointEvaluation(p.Values, PointStatus.DidNotEvaluate, refused, [], double.NaN, null, false))];

        var core = EvaluateKeyed([.. points.Select(p => (KeyedPoint?)new KeyedPoint(
                                     p.Tag is null ? CacheKeyOf(p.Values) : CacheKeyOf(p.Values) + "\n#" + p.Tag, p.Values, p.Draws) { WriteFieldsAs = p.WriteFieldsAs })],
                                 keepData, ct);
        var result = new PointEvaluation[points.Count];
        for (int k = 0; k < points.Count; k++)
        {
            var (o, data, cached, _) = core[k];
            result[k] = o.Failed
                ? new PointEvaluation(points[k].Values, PointStatus.DidNotEvaluate, o.Error, o.Scores, double.NaN, null, cached)
                : new PointEvaluation(points[k].Values, PointStatus.Evaluated, null, o.Scores, o.Cost, data, cached)
                  { GoalValues = o.GoalValues };
        }
        return result;
    }

    /// <summary>A point the evaluator runs: its cache key, its value map, its trial's draws.</summary>
    private sealed record KeyedPoint(string Key, IReadOnlyDictionary<string, string> Values, IStatisticalDraws? Draws = null)
    {
        public string? WriteFieldsAs { get; init; }
    }

    /// <summary>A value map's cache key: its lines <c>key=value</c> in the map's own order — the form a
    /// decoded unit-box point's key has always had.</summary>
    public static string CacheKeyOf(IEnumerable<KeyValuePair<string, string>> values)
        => string.Join("\n", values.Select(kv => kv.Key + "=" + kv.Value));

    /// <summary>Whether <see cref="EvaluateValues(IReadOnlyList{ValuePoint}, bool, CancellationToken)"/> would answer this
    /// value map from the cache — what a run states before it spends (brief-sens-1 R-sens1-3). Not while a call is in
    /// flight: the evaluator is one call at a time.</summary>
    public bool IsCached(IReadOnlyDictionary<string, string> values) => _cache.ContainsKey(CacheKeyOf(values));

    /// <summary>
    /// Evaluates one batch of unit-box points: decode each into its value map, then
    /// <see cref="EvaluateKeyed"/> what is feasible, then assign penalties in batch order.
    /// </summary>
    internal IReadOnlyList<Evaluation> EvaluateBatch(IReadOnlyList<double[]> batch, CancellationToken ct = default,
                                                     bool record = true)
    {
        var decoded = batch.Select(u => DecodeAt(u, _snapPreferred, _snapParts)).ToArray();

        // brief-sdl-7 D9: a candidate whose copper would join or part different nets is infeasible, and nothing is
        // simulated for it — under em=direct, and in em=correct's inner search, whose circuit model knows no geometry.
        var check = _inner?.Feasibility ?? (EmMode == OptimizerEmMode.Direct && _startSignatures.Count > 0 ? Feasible : null);
        if (check is not null)
            for (int k = 0; k < decoded.Length; k++)
                if (!decoded[k].Infeasible && check(decoded[k]) is { } why)
                {
                    _infeasibleWhy[decoded[k].CacheKey] = why;
                    decoded[k] = decoded[k] with { Infeasible = true, Distance = 0 };
                }
        // brief-sdl-7 D4: under em=direct only the points within the solve budget are simulated.
        var cachedBefore = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (EmMode == OptimizerEmMode.Direct && _probe is not null)
            foreach (var d in decoded)
                if (!d.Infeasible) cachedBefore[d.CacheKey] = !_probe.Fresh(d.Values).Uncached.Any();
        int keep = BudgetPrefix(decoded);

        foreach (var d in decoded.Take(keep)) if (d.Infeasible) _infeasible++;
        var core = _corners is null
            ? EvaluateKeyed([.. decoded.Select((d, k) => d.Infeasible || k >= keep ? null : (KeyedPoint?)new KeyedPoint(d.CacheKey, d.Values))],
                            keepData: true, ct)
            : EvaluateAtCorners(decoded, ct);

        double penalty = 10 * (1 + _maxFeasible);
        var results = new Evaluation[batch.Count];
        if (!record) return results;
        for (int k = 0; k < batch.Count; k++)
        {
            var d = decoded[k];
            if (k >= keep)
            {
                results[k] = new Evaluation(penalty, null, Failed: true);
                continue;
            }
            if (d.Infeasible)
            {
                results[k] = new Evaluation(penalty + d.Distance, null, Failed: true);
                _log.Add(new EvaluationRecord(batch[k], d, results[k].Cost, false, true, false)
                         { Why = _infeasibleWhy.GetValueOrDefault(d.CacheKey) });
                continue;
            }
            var (o, data, cached, ranHere) = core[k];
            results[k] = o.Failed ? new Evaluation(penalty, null, Failed: true) : new Evaluation(o.Cost, o.Residuals);
            _log.Add(new EvaluationRecord(batch[k], d, results[k].Cost, o.Failed, false, cached));
            if (EmMode == OptimizerEmMode.Direct && _probe is not null && ranHere && !cached)
                RecordDirectRow(d, o, cachedBefore.GetValueOrDefault(d.CacheKey));

            if (!o.Failed && (_bestIndex < 0 || o.Cost < _log[_bestIndex].Cost))
            {
                _bestIndex = _log.Count - 1;
                if (ranHere) _bestData = data;
            }
        }
        return results;
    }

    /// <summary>
    /// The one evaluator both doors share: take what the cache holds (or an identical key earlier in
    /// the batch), simulate the rest — concurrently when the design allows — and cache the outcomes.
    /// A null entry is skipped. Per entry: the outcome; the results when <paramref name="keepData"/> and
    /// its key was simulated in this batch; whether the cache answered it; whether its key was simulated
    /// in this batch.
    /// </summary>
    private (PointOutcome Outcome, DataSet? Data, bool Cached, bool RanHere)[] EvaluateKeyed(
        IReadOnlyList<KeyedPoint?> points, bool keepData, CancellationToken ct)
    {
        var cached = new bool[points.Count];
        var toRun  = new List<KeyedPoint>();
        var queued = new HashSet<string>(StringComparer.Ordinal);
        for (int k = 0; k < points.Count; k++)
        {
            if (points[k] is not { } p) continue;
            if (_cache.ContainsKey(p.Key) || !queued.Add(p.Key)) { _cacheHits++; cached[k] = true; continue; }
            toRun.Add(p);
        }

        var outcomes = new (PointOutcome Outcome, DataSet? Data)[toRun.Count];
        int degree = Math.Min(_parallelism, toRun.Count);
        string? serialWhy = _simulates ? _circuit.NotReentrantReason : null;
        if (serialWhy is not null && degree > 1 && !_serialNoted)
        {
            _serialNoted = true;
            _notes.Add(OptimizationDiagnostics.Serial(serialWhy));
        }
        if (serialWhy is not null || degree <= 1)
            for (int i = 0; i < toRun.Count; i++) outcomes[i] = Keep(EvaluatePoint(toRun[i].Values, toRun[i].Draws, ct, toRun[i].WriteFieldsAs));
        else
            Parallel.For(0, toRun.Count, new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = ct },
                         i => outcomes[i] = Keep(EvaluatePoint(toRun[i].Values, toRun[i].Draws, ct, toRun[i].WriteFieldsAs)));

        var ran = new Dictionary<string, DataSet?>(StringComparer.Ordinal);
        for (int i = 0; i < toRun.Count; i++)
        {
            _cache[toRun[i].Key] = outcomes[i].Outcome;
            ran[toRun[i].Key] = outcomes[i].Data;
            if (outcomes[i].Outcome is { Failed: false } ok) _maxFeasible = Math.Max(_maxFeasible, ok.Cost);
            else _firstFailure ??= outcomes[i].Outcome.Error;
        }

        var result = new (PointOutcome, DataSet?, bool, bool)[points.Count];
        for (int k = 0; k < points.Count; k++)
        {
            if (points[k] is not { } p) continue;
            bool ranHere = ran.TryGetValue(p.Key, out var data);
            result[k] = (_cache[p.Key], data, cached[k], ranHere);
        }
        return result;

        (PointOutcome, DataSet?) Keep((PointOutcome Outcome, DataSet? Data) o) => keepData ? o : (o.Outcome, null);
    }

    // ── Across corners (brief-yield-7) ───────────────────────────────────────

    /// <summary>A candidate's outcome by its own key: the merged one across corners, else the cache's.</summary>
    private bool TryOutcome(string key, out PointOutcome outcome)
        => (_corners is null ? _cache : _combined).TryGetValue(key, out outcome!);

    private PointOutcome OutcomeOf(string key) => (_corners is null ? _cache : _combined)[key];

    /// <summary>
    /// <see cref="EvaluateKeyed"/> for candidates across corners (R-ya7-1): each feasible candidate at each corner is
    /// one keyed point — its values at that corner (a statistical corner replayed around them, R-ya7-3), keyed by
    /// (values, corner) — and the whole batch × corners goes through the evaluator at once, in parallel. Per
    /// candidate, the merged outcome (<see cref="Combine"/>); the results kept are the first corner's (the nominal's,
    /// when it is evaluated). A corner that fails fails the candidate.
    /// </summary>
    private (PointOutcome Outcome, DataSet? Data, bool Cached, bool RanHere)[] EvaluateAtCorners(
        IReadOnlyList<DecodedPoint> decoded, CancellationToken ct)
    {
        var corners = _corners!;
        int n = corners.Count;
        var keyed   = new List<KeyedPoint?>(decoded.Count * n);
        var refused = new Diagnostic?[decoded.Count * n];
        for (int k = 0; k < decoded.Count; k++)
            for (int c = 0; c < n; c++)
            {
                var d = decoded[k];
                if (d.Infeasible) { keyed.Add(null); continue; }
                var at = corners[c].At(d.Values);
                foreach (var note in at.Notes)
                    if (_cornerNotes.Add(corners[c].Name + "\n" + note.Render())) _notes.Add(note);
                if (at.Refusal is { } why) { refused[k * n + c] = why; keyed.Add(null); continue; }
                keyed.Add(new KeyedPoint(d.CacheKey + "\n#corner " + corners[c].Name, at.Values, at.Draws));
            }
        var core = EvaluateKeyed(keyed, keepData: true, ct);

        var result = new (PointOutcome, DataSet?, bool, bool)[decoded.Count];
        for (int k = 0; k < decoded.Count; k++)
        {
            if (decoded[k].Infeasible) continue;
            PointOutcome? failed = null;
            bool cached = true;
            for (int c = 0; c < n && failed is null; c++)
            {
                string name = corners[c].Name;
                if (refused[k * n + c] is { } why)
                {
                    Interlocked.Increment(ref _failures);
                    failed = new PointOutcome(true, StatisticsDiagnostics.CornerPointFailed(name, why.Render()), 0, [], [], false);
                    break;
                }
                var o = core[k * n + c].Outcome;
                cached &= core[k * n + c].Cached;
                if (o.Failed)
                    failed = o with
                    {
                        Error = corners[c].Definition is null || o.Error is null ? o.Error
                              : StatisticsDiagnostics.CornerPointFailed(name, o.Error.Render()),
                    };
            }
            var merged = failed ?? Combine([.. Enumerable.Range(0, n).Select(c => (corners[c].Name, core[k * n + c].Outcome))]);
            _combined[decoded[k].CacheKey] = merged;
            if (merged.Failed) _firstFailure ??= merged.Error;
            else _maxFeasible = Math.Max(_maxFeasible, merged.Cost);
            result[k] = (merged, core[k * n].Data, failed is null && cached, core[k * n].RanHere);
        }
        return result;
    }

    /// <summary>
    /// One candidate's corners merged (R-ya7-1, R-ya7-2): the residual vector is every corner's residuals in turn, so
    /// least squares and minimax keep their meaning — minimax then minimizes the worst violation over the corners. Per
    /// goal: met only when met at every corner; its report is the binding corner's — the largest violation, or when
    /// met everywhere the smallest margin.
    /// </summary>
    private PointOutcome Combine(IReadOnlyList<(string Corner, PointOutcome Outcome)> at)
    {
        double[] residuals = [.. at.SelectMany(x => x.Outcome.Residuals)];
        var scores  = new List<GoalScore>(_goals.Count);
        var binding = new List<string>(_goals.Count);
        for (int g = 0; g < _goals.Count; g++)
        {
            int bind = 0;
            for (int c = 1; c < at.Count; c++)
            {
                GoalScore s = at[c].Outcome.Scores[g], b = at[bind].Outcome.Scores[g];
                bool worse = s.WorstViolation > b.WorstViolation
                          || (s.WorstViolation == b.WorstViolation && b.Met && (double.IsNaN(b.Margin) || s.Margin < b.Margin));
                if (worse) bind = c;
            }
            var bound = at[bind].Outcome.Scores[g];
            scores.Add(bound with { Residuals = [.. at.SelectMany(x => x.Outcome.Scores[g].Residuals)] });
            binding.Add(at[bind].Corner);
        }
        return new PointOutcome(false, null, GoalResiduals.Cost(residuals, _cost), residuals, scores, false)
        {
            Binding   = binding,
            PerCorner = [.. at.Select(x => (x.Corner, x.Outcome.Scores))],
        };
    }

    private (PointOutcome, DataSet?) EvaluatePoint(IReadOnlyDictionary<string, string> values, IStatisticalDraws? draws,
                                                   CancellationToken ct, string? writeFieldsAs = null)
    {
        Interlocked.Increment(ref _evaluations);
        try
        {
            var (goalValues, errors, data, failure) = _simulates ? Simulate(values, draws, ct, writeFieldsAs) : VariablesOnly(values, draws);
            if (failure is not null)
            {
                Interlocked.Increment(ref _failures);
                return (new PointOutcome(true, failure, 0, [], [], false), null);
            }

            // brief-sdl-7 D2: em=correct's inner search scores the circuit model plus each goal's fixed EM offset.
            var scores = _goals.Select((g, i) => GoalResiduals.Score(g, WithOffset(i, goalValues[i]), errors[i])).ToList();
            if (scores.FirstOrDefault(s => s.Error is not null) is { } bad)
            {
                Interlocked.Increment(ref _failures);
                return (new PointOutcome(true, bad.Error, 0, [], scores, GoalError: true), null);
            }
            double[] residuals = [.. scores.SelectMany(s => s.Residuals)];
            double cost = GoalResiduals.Cost(residuals, _cost);
            return (new PointOutcome(false, null, cost, residuals, scores, false) { GoalValues = goalValues }, data);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failures);
            return (new PointOutcome(true, OptimizationDiagnostics.EvaluationFailed(ex.Message), 0, [], [], false), null);
        }
    }

    private (Value?[] Values, string?[] Errors, DataSet? Data, Diagnostic? Failure) Simulate(
        IReadOnlyDictionary<string, string> tuned, IStatisticalDraws? draws, CancellationToken ct, string? writeFieldsAs = null)
    {
        var rr = CircuitEvaluation.Evaluate(_circuit, new CircuitEvaluationRequest
        {
            // brief-sar-10 R-sar10-2 — a fields analysis writes nothing per evaluation; each field measurement a goal names is
            // computed when it is scored (psSAR always in full). A re-run trial writes its field for the 3D view.
            Fields       = writeFieldsAs is null ? FieldsOutput.OnDemand : FieldsOutput.Written,
            FieldsLabel  = writeFieldsAs,
            Statistics   = draws,
            Sets         = _options.Sets,
            Tunables     = tuned,
            Analyses     = _analyses,
            Em           = _evalEm,
            Measurements = true,
            Expressions  = [.. _goals.Select(g => g.Expression)],
            Control      = new RunControl { Token = ct },
        });
        if (rr.Status == RunStatus.Cancelled) throw new OperationCanceledException(ct);
        if (rr.Status != RunStatus.Success)
            return ([], [], null, OptimizationDiagnostics.EvaluationFailed(rr.StatusMessage));
        if (rr.Convergence.FirstOrDefault(c => c.Converged == false) is { } nc)
            return ([], [], null, OptimizationDiagnostics.EvaluationFailed($"'{nc.Name}' did not converge{(nc.Detail is null ? "" : ": " + nc.Detail)}"));

        var values = new Value?[_goals.Count];
        var errors = new string?[_goals.Count];
        for (int i = 0; i < _goals.Count; i++)
        {
            var o = i < rr.Expressions.Count ? rr.Expressions[i] : null;
            values[i] = o?.Value;
            errors[i] = o is null ? "the run evaluated no expressions" : o.Error;
        }
        return (values, errors, rr.GroupedResults, null);
    }

    /// <summary>Goals that read only variables cost no simulation (D10): they are evaluated in the
    /// bench's own variable scope with the tuned values applied.</summary>
    private (Value?[] Values, string?[] Errors, DataSet? Data, Diagnostic? Failure) VariablesOnly(
        IReadOnlyDictionary<string, string> point, IStatisticalDraws? draws = null)
    {
        var tuned = TunableOverrides.Apply(_circuit.Tb!, _circuit.Lib!, point,
                                           _options.Sets.Select(s => s.Name).ToHashSet(StringComparer.Ordinal),
                                           _circuit.PartTunables);
        if (tuned.Refusal is { } refusal || tuned.TestBench is not { } tb)
            return ([], [], null, OptimizationDiagnostics.EvaluationFailed(tuned.Refusal ?? ""));
        foreach (var (name, expr) in _options.Sets) HbCircuitRun.ApplySet(tb, name, expr);

        var scope = new Scope("globals");
        foreach (var v in tb.GlobalVariables) scope.Bind(v.Name, v.Expression, v.Unit);
        var ev = new Evaluator { Statistics = draws };
        foreach (var fn in tb.Functions) ev.RegisterFunction(fn);

        var values = new Value?[_goals.Count];
        var errors = new string?[_goals.Count];
        for (int i = 0; i < _goals.Count; i++)
        {
            try { values[i] = ev.Eval(_goals[i].Expression, scope); }
            catch (Exception ex) { errors[i] = ex.Message; }
        }
        return (values, errors, null, null);
    }

    // ── Bookkeeping ──────────────────────────────────────────────────────────

    private double BestCost() => _bestIndex < 0 ? double.PositiveInfinity : _log[_bestIndex].Cost;

    private bool Stalled(int k, double tol, int from = 0)
    {
        if (k <= 0 || _iterHistory.Count - from <= k) return false;
        double then = _iterHistory[^(k + 1)].Best, now = _iterHistory[^1].Best;
        return double.IsFinite(then) && then - now <= tol * Math.Abs(then);
    }

    private IReadOnlyList<GoalReport> BestGoals()
    {
        if (_bestIndex < 0) return [];
        var o = OutcomeOf(_log[_bestIndex].Decoded.CacheKey);
        return [.. o.Scores.Select((s, g) => new GoalReport(s.Name, s.WorstViolation, s.WorstAt, s.Axis, s.WorstValue, s.Met, s.Margin,
            o.Binding?[g], o.PerCorner is null ? null : [.. o.PerCorner.Select(pc => CornerGoalScore.Of(pc.Corner, pc.Scores[g]))]))];
    }

    private void RecordIteration(int iteration, TimeSpan elapsed, int firstOfIteration)
    {
        var goals = BestGoals();
        _iterHistory.Add((BestCost(), [.. _goals.Select(g => goals.FirstOrDefault(r => r.Name == g.Name)?.WorstViolation ?? double.NaN)]));
        if (_options.Progress is not { } progress) return;

        double current = double.PositiveInfinity;
        for (int i = firstOfIteration; i < _log.Count; i++)
            if (!_log[i].Failed && !_log[i].Infeasible) current = Math.Min(current, _log[i].Cost);
        progress(new OptimizationProgress(
            iteration, Evaluations, Failures, _infeasible, _cacheHits, elapsed, current, BestCost(),
            _bestIndex < 0 ? new Dictionary<string, string>() : _log[_bestIndex].Decoded.Values,
            goals, _bestIndex < 0 ? [] : Variables!.Railed(Box(_log[_bestIndex].Point)), _bestData, _stage));
    }

    private OptimizationResult Refused(Diagnostic why) => new()
    {
        Outcome = OptimizationOutcome.Refused, Refusal = why, FinishReason = why.Render(),
        Algorithm = AlgorithmId, Notes = _notes,
        Evaluations = Evaluations, Failures = Failures, Infeasible = _infeasible, CacheHits = _cacheHits,
    };

    private OptimizationResult Finish(OptimizationOutcome outcome, string reason, Diagnostic? refusal)
    {
        int iterations = _iterations;
        bool cancelled = outcome == OptimizationOutcome.Cancelled;
        var bestRecord = _bestIndex >= 0 && !cancelled ? _log[_bestIndex] : null;
        var parts = bestRecord is null ? [] : ChosenParts(bestRecord.Decoded.Values);
        return new OptimizationResult
        {
            Outcome      = outcome,
            FinishReason = reason,
            Refusal      = refusal,
            Algorithm    = AlgorithmId,
            Stages       = [.. _stages],
            Snap         = _snapResult,
            Parts        = parts,
            Notes        = _notes,
            BestValues   = bestRecord?.Decoded.Values ?? new Dictionary<string, string>(),
            BestPoint    = bestRecord is null ? null : Box(bestRecord.Point),
            BestCost     = bestRecord?.Cost,
            Goals        = cancelled ? [] : BestGoals(),
            Railed       = bestRecord is null ? [] : Variables!.Railed(Box(bestRecord.Point)),
            BestData     = cancelled ? null : _bestData,
            Iterations   = iterations,
            Evaluations  = Evaluations,
            Failures     = Failures,
            Infeasible   = _infeasible,
            CacheHits    = _cacheHits,
            Log          = _log,
            History      = cancelled ? null : History(_log, _iterHistory),
            EmMode       = EmMode,
            EmSolves     = _emRows,
            EmBudgetUsed = _solvedKeys.Count,
            StoppedByBudget = _emStop == EmStop.Budget,
            BestEmKey    = EmMode == OptimizerEmMode.Direct && bestRecord is not null && _probe is not null
                               ? _probe.At(bestRecord.Decoded.Values).KeyText : null,
            ConnectedRanges = _ranges,
        };
    }

    /// <summary>
    /// The part each vendor part entry ended on (R-vl4-4a), and — once — the frequency-range warning for each CHOSEN
    /// part the analyses leave (R-vl4-4b); a part that was only tried says nothing.
    /// </summary>
    private List<ChosenPart> ChosenParts(IReadOnlyDictionary<string, string> best)
    {
        var chosen = new List<ChosenPart>();
        var entries = new List<VendorLibraries.VendorLibraryEntry>();
        foreach (var c in Variables!.Coordinates)
        {
            if (c.VendorPart is not { } vp || !best.TryGetValue(c.ValueKey, out var cell)) continue;
            if (vp.CandidateOfCell(cell) is not { } cand) continue;
            entries.Add(cand.Entry);
            chosen.Add(new ChosenPart(vp.Key, vp.Cell, vp.Instance, cand.Part, cand.ValueText, cand.Description,
                                      vp.Library, cand.Entry.File, vp.Glyph,
                                      !cand.Part.Equals(vp.PlacedPart, StringComparison.OrdinalIgnoreCase)));
        }
        if (_circuit.Tb is { } tb)
            foreach (var w in VendorLibraries.VendorPartRange.Warnings(tb, entries))
                if (!_notes.Any(n => n.Render() == w))
                    _notes.Add(OptimizationDiagnostics.ChosenPartRange(w));
        return chosen;
    }

    /// <summary>R-to6-10: the run as a <c>DataSet</c> group <c>opt</c>, so a convergence plot is an
    /// ordinary trace. Values are in base SI with their base unit.</summary>
    private DataSet History(IReadOnlyList<EvaluationRecord> log, IReadOnlyList<(double Best, double[] Worst)> iterHistory)
    {
        var ds = new DataSet();
        const string group = "opt";
        AddEmHistory(ds, group);
        var _log = log;
        var _iterHistory = iterHistory;
        int e = _log.Count;
        var evalAxis = new Axis("eval", [.. Enumerable.Range(1, e).Select(i => (double)i)]);
        ds.AddToGroup(group, "cost", new DataCube([evalAxis], [.. _log.Select(r => r.Cost)]));
        ds.AddToGroup(group, "failed", new DataCube([evalAxis], [.. _log.Select(r => r.Failed ? 1.0 : 0.0)]));
        ds.AddToGroup(group, "infeasible", new DataCube([evalAxis], [.. _log.Select(r => r.Infeasible ? 1.0 : 0.0)]));
        ds.AddToGroup(group, "cached", new DataCube([evalAxis], [.. _log.Select(r => r.Cached ? 1.0 : 0.0)]));

        foreach (var key in Variables!.ValueKeys)
        {
            string unit = Variables.UnitOf(key);
            double scale = unit.Length == 0 ? 1 : Units.Scale(unit) ?? 1;
            string baseUnit = unit.Length == 0 ? "" : Units.BaseUnit(unit);
            var q = _log.Select(r => r.Decoded.Quantities[key] * scale).ToArray();
            var cube = Variables.IsComplex(key)
                ? new DataCube([evalAxis], q)
                : new DataCube([evalAxis], [.. q.Select(c => c.Real)]);
            cube.Unit = baseUnit;
            ds.AddToGroup(group, key, cube);
        }

        if (_iterHistory.Count > 0)
        {
            var iterAxis = new Axis("iter", [.. Enumerable.Range(1, _iterHistory.Count).Select(i => (double)i)]);
            ds.AddToGroup(group, "best_cost", new DataCube([iterAxis], [.. _iterHistory.Select(h => h.Best)]));
            for (int g = 0; g < _goals.Count; g++)
                ds.AddToGroup(group, "worst_" + _goals[g].Name, new DataCube([iterAxis], [.. _iterHistory.Select(h => h.Worst[g])]));
        }
        return ds;
    }

    /// <summary>One of the registry's common options (stall), the value given or its default.</summary>
    private static double Option(IReadOnlyDictionary<string, string>? options, string name)
        => options is not null && options.TryGetValue(name, out var text)
           && double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v)
            ? v : OptimizerAlgorithms.CommonOptions.First(o => o.Name == name).NumericDefault!.Value;
}
