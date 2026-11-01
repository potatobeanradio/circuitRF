using System.Diagnostics;
using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Matching;
using CircuitRF.Design.Optimization;
using CircuitRF.Diagnostics;
using CircuitRF.Engine.Optimization;
using CircuitRF.Engine.Statistics;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Design.Statistics;

/// <summary>What a caller may change about a design of experiments beyond what the design's setup says.</summary>
public sealed record DoeOptions
{
    /// <summary>The setup to run; null runs the one the circuit's netlist carries.</summary>
    public TuningSetup? Setup { get; init; }

    /// <summary><c>--set name=expr</c> overrides, applied before each run's values as a run verb applies them.</summary>
    public IReadOnlyList<(string Name, string Expression)> Sets { get; init; } = [];

    /// <summary>Told after every batch of runs, on the run's thread.</summary>
    public Action<DoeProgress>? Progress { get; init; }

    /// <summary>Cancelling abandons the design and writes nothing (exit 130); Stop keeps the runs done and analyses
    /// them.</summary>
    public CancellationToken Cancellation { get; init; }

    /// <summary>Where the result DataSet is written (<see cref="DoeRun.ResultPathFor"/>); null writes nothing.</summary>
    public string? ResultPath { get; init; }

    /// <summary>The preferred-value ladders a <c>discrete=preferred</c> factor snaps to; null is the shipped ones.</summary>
    public PreferredLadders? Ladders { get; init; }
}

/// <summary>Runs done of the design's total.</summary>
public sealed record DoeProgress(int Done, int Total, TimeSpan Elapsed);

/// <summary>One factor (R-ya14-1): its letter in the effect names, the entry it moves, and its three levels as value
/// text — low (coded −1), centre (0) and high (+1).</summary>
public sealed record DoeFactor(string Letter, string Key, string Unit, string Low, string Centre, string High);

/// <summary>Where a run sits in its design.</summary>
public enum DoeRunKind { Cube, Axial, Centre }

/// <summary>One run of the design.</summary>
/// <param name="Coded">Per factor, its coded level.</param>
/// <param name="Values">The value map evaluated — the factors' values as a schematic would hold them.</param>
/// <param name="Actual">Per factor, the value set, in the factor's own unit.</param>
/// <param name="Responses">Each response's value at this run; absent when the run did not evaluate it.</param>
public sealed record DoeRunRecord(
    int                                 Run,
    DoeRunKind                          Kind,
    double[]                            Coded,
    IReadOnlyDictionary<string, string> Values,
    double[]                            Actual,
    PointStatus                         Status,
    Diagnostic?                         Reason,
    IReadOnlyDictionary<string, double> Responses)
{
    public bool Evaluated => Status == PointStatus.Evaluated;
}

/// <summary>What a response is.</summary>
public enum DoeResponseKind { Worst, Margin, Measure }

/// <summary>One factor's main-effect line: the response's mean at each coded level.</summary>
public sealed record DoeMainEffect(string Factor, double[] Levels, double[] Means);

/// <summary>One pair's interaction cells: the response's mean at each (B level, A level), rows B low then high.</summary>
public sealed record DoeInteraction(string A, string B, double[,] Means);

/// <summary>One response analysed (R-ya14-4): its fit — null when too few runs evaluated it — and the means the plots
/// draw.</summary>
public sealed record DoeResponse(
    string                         Name,
    DoeResponseKind                Kind,
    string?                        Goal,
    DoeFit?                        Fit,
    IReadOnlyList<DoeMainEffect>   Main,
    IReadOnlyList<DoeInteraction>  Interactions);

/// <summary>How a design of experiments ended.</summary>
public enum DoeOutcome
{
    /// <summary>Every run was attempted (or Stop kept those done) and the responses analysed — exit 0.</summary>
    Finished,
    /// <summary>Refused before the first run — exit 1.</summary>
    Refused,
    /// <summary>No run evaluated — exit 2.</summary>
    NoneEvaluated,
    /// <summary>Cancelled — exit 130, and nothing is written.</summary>
    Cancelled,
}

/// <summary>What a design of experiments produced.</summary>
public sealed class DoeResult
{
    public required DoeOutcome Outcome { get; init; }

    /// <summary>0, 1, 2 or 130 (yield overview D13; there is no target, so no 3).</summary>
    public int ExitCode => Outcome switch
    {
        DoeOutcome.Finished      => 0,
        DoeOutcome.Refused       => 1,
        DoeOutcome.NoneEvaluated => 2,
        _                        => 130,
    };

    public string FinishReason { get; init; } = "";
    public Diagnostic? Refusal { get; init; }
    public IReadOnlyList<Diagnostic> Notes { get; init; } = [];
    public IReadOnlyList<DoeRunRecord> Records { get; init; } = [];
    public IReadOnlyList<DoeResponse> Responses { get; init; } = [];

    /// <summary>Per goal, whether it was fitted on its value (a goal on one number) rather than its margin.</summary>
    public IReadOnlyDictionary<string, bool> GoalOnValue { get; init; } = new Dictionary<string, bool>();

    /// <summary>Simulations run — a repeated centre point is one.</summary>
    public long Evaluations { get; init; }

    public DataSet? Data { get; init; }
    public string? WrittenPath { get; init; }
}

/// <summary>One goal at the model optimum (R-ya14-7): what the model predicts there and what the confirmation run
/// simulated. A goal fitted on its margin has no predicted value.</summary>
public sealed record DoeGoalPrediction(
    string Goal, double? PredictedValue, double PredictedMargin, double? SimulatedValue, double? SimulatedMargin, bool? Met);

/// <summary>The model optimum and its confirmation (R-ya14-7). The model is never the answer — the confirmation is.</summary>
public sealed class DoeOptimum
{
    public Diagnostic? Refusal { get; init; }

    /// <summary>The registry algorithms that searched the model, in order.</summary>
    public string Algorithm { get; init; } = "";

    /// <summary>The optimum in coded units, after snapping to allowed values.</summary>
    public double[] Coded { get; init; } = [];

    /// <summary>The optimum's values as a schematic would hold them — what Send to Tuning loads.</summary>
    public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>();

    /// <summary>The model's objective there: the smallest predicted margin over the goals, each over its scale.</summary>
    public double PredictedObjective { get; init; } = double.NaN;

    public IReadOnlyList<DoeGoalPrediction> Goals { get; init; } = [];

    /// <summary>Whether the confirmation run evaluated, and why not.</summary>
    public PointStatus? Confirmation { get; init; }
    public Diagnostic? ConfirmationReason { get; init; }

    /// <summary>The model evaluations the search spent — microseconds each, no simulation.</summary>
    public int ModelEvaluations { get; init; }
}

/// <summary>
/// A design of experiments (brief-yield-14, docs/design/yield.md §17): the factors — the <c>opt=1</c> entries at the
/// ends of their ranges, or the <c>stat=1</c> entries at nominal ± k σ — set at the points of a structured design
/// (<see cref="DoeDesigns"/>), every run evaluated through the one evaluator (<see cref="OptimizationRun.EvaluateValues(IReadOnlyList{ValuePoint}, bool, CancellationToken)"/>),
/// and each response — a goal's worst value and margin, a scalar measure — analysed for its effects
/// (<see cref="DoeEffects"/>). The result is one <see cref="DataSet"/>, <c>&lt;design&gt;.doe.npy</c>.
///
/// <para><b>Deterministic, so no replication and no randomized order.</b> Both exist to average out and to decorrelate
/// noise from run to run; a simulation repeats its answer exactly. A repeated centre point therefore costs nothing —
/// the evaluator's cache answers it — and its value is the curvature check, not an error estimate.</para>
/// </summary>
public sealed class DoeRun
{
    private readonly PreparedCircuit _circuit;
    private readonly DoeOptions _options;
    private readonly OptimizationRun? _eval;
    private readonly TuningSetup _setup = new();
    private readonly DoeSettings _doe = new();
    private readonly List<Diagnostic> _notes = [];
    private readonly List<DoeFactor> _factors = [];
    private readonly List<TunableEntry> _statEntries = [];
    private readonly SigmaLevels? _sigmaLevels;
    private readonly IReadOnlyList<Tunable> _nominals = [];
    private readonly List<(double[] X, DoeRunKind Kind)> _points = [];
    private readonly IReadOnlyList<DoeModelTerm> _model = [];
    private readonly double _sigma = 1;

    private readonly ManualResetEventSlim _resume = new(true);
    private readonly ManualResetEventSlim _held = new(false);
    private volatile bool _stop;

    private DoeRun(PreparedCircuit circuit, DoeOptions options)
    {
        _circuit = circuit;
        _options = options;
        if (circuit.ReadError is { } readError || circuit.Lib is not { } lib || circuit.Tb is not { } tb)
        {
            Refusal = OptimizationDiagnostics.EvaluationFailed(circuit.ReadError ?? "Netlist read failed.");
            return;
        }
        _setup = options.Setup ?? tb.Tuning ?? new TuningSetup();
        _doe   = _setup.Doe ?? new DoeSettings();
        var source = _doe.EffectiveFactors;
        string sourceWord = AnalysisWord(source);

        // R-ya14-1: the levels belong to the factors.
        bool sigmaLevels = _doe.EffectiveLevels.StartsWith("sigma:", StringComparison.OrdinalIgnoreCase);
        if (sigmaLevels != (source == DoeFactorSource.Stat)) { Refusal = StatisticsDiagnostics.DoeLevelsMismatch(_doe.EffectiveLevels, sourceWord); return; }
        _sigma = _doe.SigmaK ?? DoeSettings.DefaultSigma;

        var bench = tb;
        if (options.Setup is not null)
        {
            bench = new TestBench(tb.Name) { Tuning = options.Setup };
            bench.Instances.AddRange(tb.Instances);
            bench.GlobalVariables.AddRange(tb.GlobalVariables);
            bench.Analyses.AddRange(tb.Analyses);
        }
        var catalog = TunableCatalog.FromNetlist(bench, lib, parts: circuit.PartTunables);

        if (source == DoeFactorSource.Opt)
        {
            if (!_setup.Variables.Any(e => e.Opt)) { Refusal = StatisticsDiagnostics.DoeNoFactors(sourceWord); return; }
            Variables = OptimizationVariables.Build(_setup, catalog, out var notBuilt, options.Ladders);
            if (Variables is null || Variables.Coordinates.Count == 0) { Refusal = notBuilt ?? StatisticsDiagnostics.DoeNoFactors(sourceWord); return; }
            _notes.AddRange(Variables.Notes);
            if (MixedPair(Variables.Coordinates.Select(c => (c.ValueKey, c.Part))) is { } mixed) { Refusal = mixed; return; }
            for (int i = 0; i < Variables.Coordinates.Count; i++)
            {
                var c = Variables.Coordinates[i];
                string Text(double u) => c.Text(c.Decode(u, snapPreferred: true));
                _factors.Add(new DoeFactor(DoeDesigns.Letter(i), c.Key, c.Unit, Text(0), Text(0.5), Text(1)));
                if (c.Integer || c.Step is > 0 || c.Preferred) _notes.Add(StatisticsDiagnostics.DoeSnapped(c.Key, Text(0), Text(1)));
            }
        }
        else
        {
            _statEntries.AddRange(_setup.Variables.Where(e => e.IsStatistical));
            if (_statEntries.Count == 0) { Refusal = StatisticsDiagnostics.DoeNoFactors(sourceWord); return; }
            // A level is the nominal ± k of the entry's OWN σ (brief-yield-15 R-ya15-9) — SigmaLevels, which sensitivity's
            // ±kσ step shares (brief-sens-1 D3).
            _sigmaLevels = new SigmaLevels(_statEntries, _sigma);
            foreach (var d in StatisticsValidator.ValidateSetup(_setup, catalog))
                if (d.Severity == DiagnosticSeverity.Error) { Refusal = d; return; }
            _nominals = SampleValues.Nominals(catalog);
            if (MixedPair(_statEntries.Select(e => catalog.Find(e.Key)).OfType<Tunable>().Select(t => (t.WholeKey ?? t.Key, t.Part))) is { } mixed)
            { Refusal = mixed; return; }
            if (_setup.Correlations.Count > 0) _notes.Add(StatisticsDiagnostics.DoeUncorrelated(_setup.Correlations.Count));
            for (int i = 0; i < _statEntries.Count; i++)
            {
                var e = _statEntries[i];
                var t = catalog.Find(e.Key);
                string Text(double coded)
                {
                    var v = LevelValues(i, coded);
                    return v.Draws.TryGetValue(e.Key, out double x) && t is not null ? TunableValue.Format(x, t.Unit) : "—";
                }
                _factors.Add(new DoeFactor(DoeDesigns.Letter(i), e.Key, t?.Unit ?? "", Text(-1), Text(0), Text(1)));
                if (t is { IsInteger: true } || e.Distribution == StatDistribution.Discrete)
                    _notes.Add(StatisticsDiagnostics.DoeSnapped(e.Key, Text(-1), Text(1)));
            }
        }

        // R-ya14-2: the design's points — refused, never truncated, when the table or the limits hold no design.
        int k = _factors.Count;
        var kind = _doe.EffectiveDesign;
        double[][] cube;
        switch (kind)
        {
            case DoeDesignKind.Full2:
                if (k > DoeDesigns.Full2Limit)
                { Refusal = StatisticsDiagnostics.DoeTooMany("full2", k, Math.Pow(2, k).ToString("N0", CultureInfo.InvariantCulture), "Screen them with design=pb, or design=frac."); return; }
                cube = DoeDesigns.FullFactorial(k);
                break;
            case DoeDesignKind.Frac:
                Plan = DoeDesigns.Fraction(k, _doe.EffectiveResolution);
                if (Plan is null)
                {
                    var held = DoeDesigns.FractionTable.Where(p => p.Factors == k).Select(p => $"2^({k}-{p.Fraction}) resolution {p.Resolution}").ToList();
                    Refusal = StatisticsDiagnostics.DoeNoFraction(k, _doe.EffectiveResolution, held.Count > 0
                        ? $"It holds {string.Join(", ", held)}."
                        : k < 4 ? "With fewer than 4 factors no fraction keeps main effects clear: use design=full2."
                                : "It covers 4 to 11 factors: screen more with design=pb.");
                    return;
                }
                cube = DoeDesigns.Fractional(Plan);
                break;
            case DoeDesignKind.Pb:
                if (DoeDesigns.PlackettBurmanRuns(k) is null)
                { Refusal = StatisticsDiagnostics.DoeTooMany("pb", k, "more than 24", "The Plackett–Burman designs here take at most 23 factors."); return; }
                cube = DoeDesigns.PlackettBurman(k);
                break;
            default:
                if (k > DoeDesigns.CcfLimit)
                { Refusal = StatisticsDiagnostics.DoeTooMany("ccf", k, "a composite of more than 128", "Screen first with design=pb, then fit the few active factors with design=ccf."); return; }
                (cube, Plan) = DoeDesigns.CompositeCube(k);
                break;
        }
        foreach (var x in cube) _points.Add((x, DoeRunKind.Cube));
        if (kind == DoeDesignKind.Ccf)
            foreach (var x in DoeDesigns.CentreAndAxial(k, 1).Skip(1)) _points.Add((x, DoeRunKind.Axial));
        for (int c = 0; c < _doe.EffectiveCentre; c++) _points.Add((new double[k], DoeRunKind.Centre));
        _model = DoeEffects.Model(kind, k, Plan, cube);

        // The evaluator: the goals the factors are for, every analysis when a measure is a response, the doe line's
        // parallelism.
        var use = _doe.EffectiveResponses == DoeResponseSet.All ? GoalUse.Both
                : source == DoeFactorSource.Opt ? GoalUse.Opt : GoalUse.Yield;
        var evalSetup = _setup.Clone();
        bool measures = tb.Measurements.Count > 0;
        if (use == GoalUse.Opt)
        {
            var o = evalSetup.Optimizer?.Clone() ?? new OptimizerSettings();
            if (measures) o.Scope = OptimizerScope.All;
            if (_doe.Parallelism is { } p) o.Parallelism = p;
            evalSetup.Optimizer = o;
        }
        else
        {
            var st = evalSetup.Statistics?.Clone() ?? new StatisticsSettings();
            if (measures) st.Scope = OptimizerScope.All;
            if (_doe.Parallelism is { } p) st.Parallelism = p;
            evalSetup.Statistics = st;
        }
        _eval = OptimizationRun.ForEvaluation(circuit,
            new OptimizationOptions { Setup = evalSetup, Sets = options.Sets, Cancellation = options.Cancellation }, use);
        if (_eval.Refusal is { } refused) { Refusal = refused; return; }
        _notes.AddRange(_eval.Notes);
        if (_eval.Goals.Count == 0 && !measures) Refusal = StatisticsDiagnostics.DoeNoResponse();
    }

    /// <summary>Prepares a design of <paramref name="circuit"/>'s setup. A design that cannot start carries its
    /// <see cref="Refusal"/> — <c>check</c> asks the same question — and <see cref="Run"/> returns it at once.</summary>
    public static DoeRun Create(PreparedCircuit circuit, DoeOptions? options = null) => new(circuit, options ?? new DoeOptions());

    /// <summary><c>&lt;design&gt;.doe.npy</c> beside the schematic or netlist.</summary>
    public static string ResultPathFor(string sourcePath)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? ".",
                        Path.GetFileNameWithoutExtension(sourcePath) + ".doe.npy");

    public Diagnostic? Refusal { get; }

    public DoeSettings Settings => _doe;

    /// <summary>The factors, lettered in order.</summary>
    public IReadOnlyList<DoeFactor> Factors => _factors;

    /// <summary>The fraction the design uses (<c>frac</c>, or a <c>ccf</c>'s cube beyond 4 factors); null otherwise.</summary>
    public FractionalPlan? Plan { get; }

    /// <summary>The designable variables of an opt design, decoded as the optimizer decodes them; null for stat.</summary>
    public OptimizationVariables? Variables { get; }

    /// <summary>The terms every response is fitted on, with their alias sets.</summary>
    public IReadOnlyList<DoeModelTerm> Model => _model;

    /// <summary>The goals analysed.</summary>
    public IReadOnlyList<OptimizationGoal> Goals => _eval?.Goals ?? [];

    /// <summary>The runs the design holds, centre points included — shown before running.</summary>
    public int RunCount => _points.Count;

    /// <summary>The distinct runs it simulates: repeated centre points are one.</summary>
    public int DistinctRuns => _points.Select(p => string.Join(",", p.X)).Distinct().Count();

    public IReadOnlyList<Diagnostic> Notes => _notes;

    public long Evaluations => _eval?.Evaluations ?? 0;

    public void Pause() => _resume.Reset();
    public void Resume() => _resume.Set();

    /// <summary>Ends the design after the batch in flight; the runs done are analysed.</summary>
    public void Stop() { _stop = true; _resume.Set(); }

    public bool IsPaused => _held.IsSet;
    public WaitHandle Held => _held.WaitHandle;

    public DoeResult? Result { get; private set; }

    /// <summary>The design's one-line description: <c>full2, 3 factors, 9 runs (1 centre)</c>.</summary>
    public string Describe()
    {
        var d = _doe;
        string what = d.EffectiveDesign switch
        {
            DoeDesignKind.Frac when Plan is { } p => $"2^({p.Factors}-{p.Fraction}) fractional factorial, resolution {Roman(p.Resolution)} ({string.Join(", ", p.Generators)})",
            DoeDesignKind.Pb  => $"Plackett–Burman {_points.Count(p => p.Kind == DoeRunKind.Cube)}-run",
            DoeDesignKind.Ccf => "face-centred central composite" + (Plan is { } c ? $" on a 2^({c.Factors}-{c.Fraction}) cube ({string.Join(", ", c.Generators)})" : ""),
            _                 => "2-level full factorial",
        };
        int centres = _points.Count(p => p.Kind == DoeRunKind.Centre);
        return $"{what}: {_factors.Count} factor(s), {_points.Count} runs" + (centres > 0 ? $" ({centres} centre)" : "");
    }

    /// <summary>A resolution as the textbook writes it.</summary>
    public static string Roman(int r) => r switch { 3 => "III", 4 => "IV", 5 => "V", 6 => "VI", 7 => "VII", _ => r.ToString(CultureInfo.InvariantCulture) };

    private static string AnalysisWord(DoeFactorSource s) => s == DoeFactorSource.Stat ? "stat" : "opt";

    private static Diagnostic? MixedPair(IEnumerable<(string Whole, ComplexPart? Part)> parts)
    {
        foreach (var g in parts.Where(p => p.Part is not null).GroupBy(p => p.Whole, StringComparer.Ordinal))
        {
            bool rect = g.Any(p => p.Part is ComplexPart.Real or ComplexPart.Imag);
            bool polar = g.Any(p => p.Part is ComplexPart.Mag or ComplexPart.Phase);
            if (rect && polar)
                return StatisticsDiagnostics.DoeMixedPair(g.Key, string.Join(" and ", g.Select(p => p.Part!.Value.ToString().ToLowerInvariant())));
        }
        return null;
    }

    /// <summary>A stat design's values at one coded point: z = k × level, through each factor's own distribution.</summary>
    private SampledValues LevelValues(int run, double[] coded) => _sigmaLevels!.At(_nominals, run, coded);

    private SampledValues LevelValues(int factor, double coded)
    {
        var x = new double[_statEntries.Count];
        x[factor] = coded;
        return LevelValues(0, x);
    }

    // ── The run ──────────────────────────────────────────────────────────────────────

    /// <summary>Runs to the end on the calling thread: every run, then the analysis.</summary>
    public DoeResult Run()
    {
        if (Refusal is { } refused)
            return Result = new DoeResult { Outcome = DoeOutcome.Refused, Refusal = refused, FinishReason = refused.Render(), Notes = _notes };

        var ct = _options.Cancellation;
        var sw = Stopwatch.StartNew();
        var notes = new List<Diagnostic>(_notes);
        int k = _factors.Count, n = _points.Count;

        // Each run's value map; identical maps (the centre points) are simulated once.
        var maps = new IReadOnlyDictionary<string, string>[n];
        var actual = new double[n][];
        var blocked = new Diagnostic?[n];
        for (int r = 0; r < n; r++)
        {
            var x = _points[r].X;
            actual[r] = new double[k];
            if (Variables is { } vars)
            {
                var u = x.Select(c => (c + 1) / 2).ToArray();
                var d = vars.Decode(u, snapPreferred: true);
                maps[r] = d.Values;
                for (int i = 0; i < k; i++) actual[r][i] = vars.Coordinates[i].Decode(u[i], snapPreferred: true);
                if (d.Infeasible) blocked[r] = OptimizationDiagnostics.EvaluationFailed("these levels describe no complex value");
            }
            else
            {
                var v = LevelValues(r + 1, x);
                maps[r] = v.Values;
                for (int i = 0; i < k; i++) actual[r][i] = v.Draws.TryGetValue(_statEntries[i].Key, out double a) ? a : double.NaN;
                blocked[r] = v.Refusal;
            }
        }

        var unique = new List<int>();                       // first run of each distinct map
        var firstOf = new int[n];
        var byKey = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int r = 0; r < n; r++)
        {
            if (blocked[r] is not null) { firstOf[r] = -1; continue; }
            string key = OptimizationRun.CacheKeyOf(maps[r]);
            if (byKey.TryGetValue(key, out int first)) { firstOf[r] = first; continue; }
            byKey[key] = r;
            firstOf[r] = r;
            unique.Add(r);
        }

        var evaluated = new Dictionary<int, PointEvaluation>();
        string reason = $"{n} runs";
        try
        {
            int batch = Math.Max(1, _eval!.Parallelism);
            for (int next = 0; next < unique.Count; next += batch)
            {
                if (!_resume.IsSet)
                {
                    _held.Set();
                    WaitHandle.WaitAny([_resume.WaitHandle, ct.WaitHandle]);
                    _held.Reset();
                }
                ct.ThrowIfCancellationRequested();
                if (_stop) { reason = $"stopped after {evaluated.Count} of {unique.Count} distinct runs"; break; }
                var chunk = unique.Skip(next).Take(batch).ToList();
                var results = _eval.EvaluateValues([.. chunk.Select(r => new ValuePoint(maps[r]))], keepData: true, ct);
                for (int j = 0; j < chunk.Count; j++) evaluated[chunk[j]] = results[j];
                int done = Enumerable.Range(0, n).Count(r => firstOf[r] >= 0 && evaluated.ContainsKey(firstOf[r]));
                _options.Progress?.Invoke(new DoeProgress(done, n, sw.Elapsed));
            }
        }
        catch (OperationCanceledException)
        {
            return Result = new DoeResult { Outcome = DoeOutcome.Cancelled, FinishReason = "cancelled", Notes = notes };
        }

        // ── The records ─────────────────────────────────────────────────────────────
        var goals = Goals;
        var measureNames = new List<string>();
        var onValue = goals.ToDictionary(g => g.Name, _ => true, StringComparer.Ordinal);
        var records = new List<DoeRunRecord>(n);
        for (int r = 0; r < n; r++)
        {
            var responses = new Dictionary<string, double>(StringComparer.Ordinal);
            PointStatus status;
            Diagnostic? why;
            if (blocked[r] is { } b) { status = PointStatus.DidNotEvaluate; why = b; }
            else if (!evaluated.TryGetValue(firstOf[r], out var p)) { status = PointStatus.DidNotEvaluate; why = OptimizationDiagnostics.EvaluationFailed("not run: the design was stopped first"); }
            else
            {
                status = p.Status;
                why = p.Reason;
                foreach (var s in p.Goals)
                {
                    if (s.Error is not null) continue;
                    responses[$"goal:{s.Name}:worst"] = s.WorstValue;
                    responses[$"goal:{s.Name}:margin"] = s.Margin;
                    if (!QuadraticSurrogate.FitsValue(s) && onValue.ContainsKey(s.Name)) onValue[s.Name] = false;
                }
                if (p.Data is { } data && data.ContainsGroup(DataSet.MeasurementsGroup))
                    foreach (var (name, cube) in data.CubesIn(DataSet.MeasurementsGroup))
                        if (!name.StartsWith("__", StringComparison.Ordinal) && StatisticalRun.IsScalar(cube))
                        {
                            responses[name] = cube.RealValues[0];
                            if (!measureNames.Contains(name)) measureNames.Add(name);
                        }
            }
            records.Add(new DoeRunRecord(r + 1, _points[r].Kind, _points[r].X, maps[r], actual[r], status, why, responses));
        }

        if (!records.Any(rec => rec.Evaluated))
        {
            string first = records.FirstOrDefault(rec => rec.Reason is not null)?.Reason?.Render() ?? "no reason given";
            var none = StatisticsDiagnostics.DoeNoneEvaluated(n, first);
            return Result = new DoeResult
            {
                Outcome = DoeOutcome.NoneEvaluated, Refusal = none, FinishReason = reason, Notes = notes, Records = records,
                Evaluations = Evaluations,
            };
        }

        // ── The analysis ────────────────────────────────────────────────────────────
        var names = new List<(string Name, DoeResponseKind Kind, string? Goal)>();
        foreach (var g in goals)
        {
            names.Add(($"goal:{g.Name}:worst", DoeResponseKind.Worst, g.Name));
            names.Add(($"goal:{g.Name}:margin", DoeResponseKind.Margin, g.Name));
        }
        foreach (var m in measureNames) names.Add((m, DoeResponseKind.Measure, null));
        if (names.Count == 0)
            return Result = new DoeResult
            {
                Outcome = DoeOutcome.Refused, Refusal = StatisticsDiagnostics.DoeNoResponse(), FinishReason = reason, Notes = notes,
                Records = records, Evaluations = Evaluations,
            };

        bool twoLevel = _doe.EffectiveDesign != DoeDesignKind.Ccf;
        var xs = records.Select(rec => rec.Coded).ToList();
        var centre = twoLevel ? records.Select(rec => rec.Kind == DoeRunKind.Centre).ToList() : null;
        var cubeRuns = records.Where(rec => rec.Kind == DoeRunKind.Cube).ToList();
        var responsesOut = new List<DoeResponse>();
        foreach (var (name, kind, goal) in names)
        {
            var y = records.Select(rec => rec.Evaluated && rec.Responses.TryGetValue(name, out double v) ? v : double.NaN).ToList();
            var fit = DoeEffects.Fit(_model, xs, y, centre);
            if (fit is null) notes.Add(StatisticsDiagnostics.DoeNotFitted(name, y.Count(double.IsFinite), _model.Count + 1));
            else if (fit.CurvatureActive && kind != DoeResponseKind.Margin) notes.Add(StatisticsDiagnostics.DoeCurvature(name));
            var cy = cubeRuns.Select(rec => rec.Evaluated && rec.Responses.TryGetValue(name, out double v) ? v : double.NaN).ToList();
            var cx = cubeRuns.Select(rec => rec.Coded).ToList();
            // A two-level design's main effect is its cube's: a centre point would put the curvature into every
            // factor's line, an inert one's included. A composite's three levels are all its own.
            var main = _factors.Select((f, i) =>
            {
                var (lv, mean) = twoLevel ? DoeEffects.LevelMeans(cx, cy, i) : DoeEffects.LevelMeans(xs, y, i);
                return new DoeMainEffect(f.Key, lv, mean);
            }).ToList();
            var pairs = _model.Where(t => !t.Term.Squared && t.Term.Factors.Length == 2)
                .Select(t => new DoeInteraction(_factors[t.Term.Factors[0]].Key, _factors[t.Term.Factors[1]].Key,
                                                DoeEffects.InteractionMeans(cx, cy, t.Term.Factors[0], t.Term.Factors[1])))
                .ToList();
            responsesOut.Add(new DoeResponse(name, kind, goal, fit, main, pairs));
        }

        var result = new DoeResult
        {
            Outcome = DoeOutcome.Finished, FinishReason = reason, Notes = notes, Records = records, Responses = responsesOut,
            GoalOnValue = onValue, Evaluations = Evaluations,
        };
        var ds = DoeDataSet.Build(this, result);
        string? written = null;
        if (_options.ResultPath is { } path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                DataSetExporter.Export(ds, path, ExportFormat.Npy);
                written = Path.GetFullPath(path);
            }
            catch (Exception ex) { notes.Add(StatisticsDiagnostics.DoeWriteFailed(path, ex.Message)); }
        }
        return Result = new DoeResult
        {
            Outcome = DoeOutcome.Finished, FinishReason = reason, Notes = notes, Records = records, Responses = responsesOut,
            GoalOnValue = onValue, Evaluations = Evaluations, Data = ds, WrittenPath = written,
        };
    }

    // ── The model optimum (R-ya14-7) ───────────────────────────────────────────────

    /// <summary>The registry algorithms that search the fitted model, in turn: a global search, then a polish.</summary>
    public static readonly IReadOnlyList<string> OptimumAlgorithms = [DifferentialEvolution.AlgorithmId, BfgsB.AlgorithmId];

    /// <summary>
    /// The point inside the factor ranges where the fitted models say the goals are best met — the smallest predicted
    /// margin over the goals, each over its scale, made as large as it can be — found by the optimizer's own registry
    /// algorithms over the unit box (a model evaluation is microseconds), then a CONFIRMATION run by real simulation at
    /// that point, reported as predicted vs simulated for every goal. A goal on one number is predicted from its
    /// value's model through the goal's own rule; a goal over a sweep, from its margin's model.
    /// </summary>
    public DoeOptimum ModelOptimum(DoeResult result, CancellationToken ct = default)
    {
        if (Variables is not { } vars || _doe.EffectiveFactors != DoeFactorSource.Opt) return new DoeOptimum { Refusal = StatisticsDiagnostics.DoeOptimumNeedsOpt() };
        var goals = Goals;
        if (goals.Count == 0) return new DoeOptimum { Refusal = StatisticsDiagnostics.DoeOptimumNoGoal() };

        var fits = new DoeFit[goals.Count];
        var rules = new Func<double, double>[goals.Count];
        var onValue = new bool[goals.Count];
        var scales = new double[goals.Count];
        for (int g = 0; g < goals.Count; g++)
        {
            onValue[g] = result.GoalOnValue.TryGetValue(goals[g].Name, out bool v) && v;
            string name = $"goal:{goals[g].Name}:{(onValue[g] ? "worst" : "margin")}";
            if (result.Responses.FirstOrDefault(r => r.Name == name)?.Fit is not { } fit)
                return new DoeOptimum { Refusal = StatisticsDiagnostics.DoeOptimumNotFitted(goals[g].Name) };
            fits[g] = fit;
            rules[g] = QuadraticSurrogate.MarginRule(goals[g], onValue[g]);
            scales[g] = GoalResiduals.Scale(goals[g]) is { } s && s > 0 ? s : 1.0;
        }

        int k = _factors.Count, evaluations = 0;
        double Objective(double[] x)
        {
            double m = double.PositiveInfinity;
            for (int g = 0; g < fits.Length; g++)
            {
                double margin = rules[g](fits[g].Predict(x));
                m = Math.Min(m, double.IsNaN(margin) ? double.NegativeInfinity : margin / scales[g]);
            }
            return m;
        }
        double[] Coded(double[] u) => [.. u.Select(c => 2 * Math.Clamp(c, 0, 1) - 1)];

        // A global search from the centre, then a polish from its best.
        double[] best = [.. Enumerable.Repeat(0.5, k)];
        double bestCost = -Objective(Coded(best));
        evaluations++;
        foreach (var id in OptimumAlgorithms)
        {
            var alg = OptimizerFactory.Create(id, best, 1)!;
            for (int it = 0; it < 400 && !alg.IsFinished; it++)
            {
                ct.ThrowIfCancellationRequested();
                var asked = alg.Ask();
                if (asked.Count == 0) break;
                var told = new Evaluation[asked.Count];
                for (int j = 0; j < asked.Count; j++)
                {
                    double cost = -Objective(Coded(asked[j]));
                    evaluations++;
                    if (!double.IsFinite(cost)) { told[j] = new Evaluation(1e300, null, Failed: true); continue; }
                    told[j] = new Evaluation(cost);
                    if (cost < bestCost) { bestCost = cost; best = [.. asked[j].Select(c => Math.Clamp(c, 0, 1))]; }
                }
                alg.Tell(told);
            }
        }

        // A coordinate within round-off of a range end IS that end — a search that rails stops a hair short of it.
        for (int i = 0; i < k; i++)
            best[i] = best[i] < 1e-9 ? 0 : best[i] > 1 - 1e-9 ? 1 : best[i];

        // Snapped to the values a factor allows, and predicted THERE — what the confirmation simulates.
        var decoded = vars.Decode(best, snapPreferred: true);
        var snapped = new double[k];
        for (int i = 0; i < k; i++)
        {
            var c = vars.Coordinates[i];
            snapped[i] = 2 * c.Encode(c.Decode(best[i], snapPreferred: true)) - 1;
        }

        var confirm = _eval!.EvaluateValues([new ValuePoint(decoded.Values)], keepData: false, ct)[0];
        var predictions = new List<DoeGoalPrediction>();
        for (int g = 0; g < goals.Count; g++)
        {
            double p = fits[g].Predict(snapped);
            double? value = onValue[g] ? p : null;
            double margin = rules[g](p);
            var s = confirm.Goals.FirstOrDefault(x => x.Name == goals[g].Name);
            bool scored = confirm.Status == PointStatus.Evaluated && s is { Error: null };
            predictions.Add(new DoeGoalPrediction(goals[g].Name, value, margin,
                scored && onValue[g] ? s!.WorstValue : null, scored ? s!.Margin : null, scored ? s!.Met : null));
        }
        return new DoeOptimum
        {
            Algorithm = string.Join(" + ", OptimumAlgorithms), Coded = snapped, Values = decoded.Values,
            PredictedObjective = Objective(snapped), Goals = predictions, Confirmation = confirm.Status,
            ConfirmationReason = confirm.Status == PointStatus.Evaluated ? null
                : StatisticsDiagnostics.DoeConfirmationFailed(confirm.Reason?.Render() ?? "no reason given"),
            ModelEvaluations = evaluations,
        };
    }
}
