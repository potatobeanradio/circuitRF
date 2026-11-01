using System.Diagnostics;
using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Optimization;
using CircuitRF.Diagnostics;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Design.Statistics;

/// <summary>What a caller may change about a sensitivity run beyond what the design's setup says.</summary>
public sealed record SensitivityOptions
{
    /// <summary>The setup to run; null runs the one the circuit's netlist carries.</summary>
    public TuningSetup? Setup { get; init; }

    /// <summary><c>--set name=expr</c> overrides, applied before each evaluation's values as a run verb applies them.</summary>
    public IReadOnlyList<(string Name, string Expression)> Sets { get; init; } = [];

    /// <summary><c>--vars</c>: only these of the factors the run would step; null steps them all.</summary>
    public IReadOnlyList<string>? Vars { get; init; }

    /// <summary><c>--goals</c>: exactly these enabled goals, whatever their <c>use</c>; null takes R-sens1-2's rule.</summary>
    public IReadOnlyList<string>? Goals { get; init; }

    /// <summary>Told after every batch of evaluations, on the run's thread.</summary>
    public Action<SensitivityProgress>? Progress { get; init; }

    /// <summary>Cancelling abandons the run and writes nothing (exit 130); Stop ranks what has evaluated.</summary>
    public CancellationToken Cancellation { get; init; }

    /// <summary>Where the result DataSet is written (<see cref="SensitivityRun.ResultPathFor"/>); null writes nothing.</summary>
    public string? ResultPath { get; init; }
}

/// <summary>Evaluations done of the run's total.</summary>
public sealed record SensitivityProgress(int Done, int Total, TimeSpan Elapsed);

/// <summary>What a factor's step is measured in (D3): its own spread, a percent of its value, or degrees of a phase.</summary>
public enum SensitivityBasis { Sigma, Percent, Degree }

/// <summary>One factor (R-sens1-1): the value it moves, what its step is in, and the value text at nominal, + and −.</summary>
/// <param name="Key">The tunable key (<c>R1.R</c>, <c>mag(ZL)</c>).</param>
/// <param name="Label">What a table calls it.</param>
/// <param name="BasisText"><c>σ</c>, <c>2σ</c>, <c>1 %</c>, <c>1°</c> — what every contributor states (D3).</param>
public sealed record SensitivityFactor(
    string Key, string Label, string Unit, SensitivityBasis Basis, string BasisText, string Nominal, string Plus, string Minus);

/// <summary>A contributor's standing: both steps ran and agree, they disagree (D6), one ran, or neither did.</summary>
public enum SensitivityStatus { Ok, Nonlinear, OneSided, Failed }

/// <summary>One factor's effect on one response (D4–D6).</summary>
/// <param name="Delta">(m⁺ − m⁻)/2, or the one-sided difference; NaN when it failed. A goal's is in normalized-margin
/// units; a measure's in its own unit.</param>
/// <param name="Share">Δ² / ΣΔ² over the response's contributors; 0 when it failed.</param>
/// <param name="Plus">The response at the + step; NaN when that step did not evaluate.</param>
/// <param name="Asymmetry">|(m⁺ − m₀) + (m⁻ − m₀)| / max(|m⁺ − m₀|, |m⁻ − m₀|); NaN unless both steps ran.</param>
/// <param name="Reason">Why a step did not evaluate, in the evaluator's words; null when both ran.</param>
public sealed record SensitivityContributor(
    SensitivityFactor Factor, double Delta, double Share, double Plus, double Minus, double Asymmetry,
    SensitivityStatus Status, string? Reason);

/// <summary>One response ranked (D5): a goal's normalized margin, or a scalar measure's value.</summary>
/// <param name="Nominal">m₀ — the goal's normalized margin at the nominal, or the measure's value.</param>
/// <param name="Spread">σ = √ΣΔ², in the response's units.</param>
/// <param name="Estimate">Φ(m₀/σ) — a goal only, and only when every contributor's basis is σ (D5); labelled
/// <c>estimated</c>, because the Monte Carlo is the yield.</param>
/// <param name="Contributors">Largest |Δ| first; failed contributors last.</param>
public sealed record SensitivityResponse(
    string Name, bool IsGoal, string Unit, double Nominal, double Spread, double? Estimate,
    IReadOnlyList<SensitivityContributor> Contributors);

/// <summary>How a sensitivity run ended.</summary>
public enum SensitivityOutcome
{
    /// <summary>The nominal evaluated and every response was ranked — exit 0.</summary>
    Finished,
    /// <summary>Refused before the first evaluation — exit 1.</summary>
    Refused,
    /// <summary>The nominal did not evaluate — exit 2.</summary>
    NominalFailed,
    /// <summary>Cancelled — exit 130, and nothing is written.</summary>
    Cancelled,
}

/// <summary>What a sensitivity run produced.</summary>
public sealed class SensitivityResult
{
    public required SensitivityOutcome Outcome { get; init; }

    /// <summary>0, 1, 2 or 130 — there is no target, so no 3 (R-sens1-7).</summary>
    public int ExitCode => Outcome switch
    {
        SensitivityOutcome.Finished      => 0,
        SensitivityOutcome.Refused       => 1,
        SensitivityOutcome.NominalFailed => 2,
        _                                => 130,
    };

    public string FinishReason { get; init; } = "";
    public Diagnostic? Refusal { get; init; }
    public IReadOnlyList<Diagnostic> Notes { get; init; } = [];
    public IReadOnlyList<SensitivityResponse> Goals { get; init; } = [];
    public IReadOnlyList<SensitivityResponse> Measures { get; init; } = [];

    /// <summary>Simulations this run spent — the cache's known points cost none.</summary>
    public long Evaluations { get; init; }

    public DataSet? Data { get; init; }
    public string? WrittenPath { get; init; }
}

/// <summary>
/// Sensitivity at the nominal design (brief-sens-1, docs/design/yield.md §18): each factor stepped + and − on its own,
/// every other value at nominal — 2k + 1 evaluations through the one evaluator
/// (<see cref="OptimizationRun.EvaluateValues(IReadOnlyList{ValuePoint}, bool, CancellationToken)"/>, D1), with no
/// random draw, so the result is deterministic and needs no seed (D2). A toleranced factor steps ±kσ through its own
/// distribution (<see cref="SigmaLevels"/>, the code DOE's <c>levels=sigma:k</c> uses); any other ±step % of its value,
/// a phase ±1° (D3). Each goal's metric is its margin at the tightest point over its scale (D4); per goal the
/// contributors are ranked by |Δ| with their shares, the predicted spread, and — only when every basis is σ — a
/// first-order pass estimate (D5). A step pair that disagrees is flagged nonlinear, not hidden (D6). The result is one
/// <see cref="DataSet"/>, <c>&lt;design&gt;.sens.npy</c>; the design is never changed.
/// </summary>
public sealed class SensitivityRun
{
    /// <summary>Above this asymmetry a contributor is flagged nonlinear (D6).</summary>
    public const double NonlinearThreshold = 0.5;

    /// <summary>The step of a phase with no tolerance, in degrees (R-sens1-1).</summary>
    public const double PhaseStepDegrees = 1;

    private readonly SensitivityOptions _options;
    private readonly OptimizationRun? _eval;
    private readonly SensitivitySettings _settings = new();
    private readonly List<Diagnostic> _notes = [];
    private readonly List<SensitivityFactor> _factors = [];
    private readonly List<(IReadOnlyDictionary<string, string> Map, Diagnostic? Blocked)> _plus = [];
    private readonly List<(IReadOnlyDictionary<string, string> Map, Diagnostic? Blocked)> _minus = [];
    private readonly List<string> _goalNames = [];
    private bool _measures;

    private readonly ManualResetEventSlim _resume = new(true);
    private readonly ManualResetEventSlim _held = new(false);
    private volatile bool _stop;

    private SensitivityRun(PreparedCircuit circuit, SensitivityOptions options)
    {
        _options = options;
        if (circuit.ReadError is not null || circuit.Lib is not { } lib || circuit.Tb is not { } tb)
        {
            Refusal = OptimizationDiagnostics.EvaluationFailed(circuit.ReadError ?? "Netlist read failed.");
            return;
        }
        var setup = options.Setup ?? tb.Tuning ?? new TuningSetup();
        _settings = setup.Sensitivity ?? new SensitivitySettings();
        var source = _settings.EffectiveFactors;

        var bench = tb;
        if (options.Setup is not null)
        {
            bench = new TestBench(tb.Name) { Tuning = options.Setup };
            bench.Instances.AddRange(tb.Instances);
            bench.GlobalVariables.AddRange(tb.GlobalVariables);
            bench.Analyses.AddRange(tb.Analyses);
            bench.Measurements.AddRange(tb.Measurements);
        }
        var catalog = TunableCatalog.FromNetlist(bench, lib, parts: circuit.PartTunables);
        var nominals = SampleValues.Nominals(catalog);

        // ── R-sens1-2: the goals ranked, then every scalar measure ───────────────────
        var enabled = setup.Goals.Where(g => g.Enabled).ToList();
        if (options.Goals is { } named)
        {
            foreach (var name in named)
                if (!enabled.Any(g => g.Name == name))
                { Refusal = StatisticsDiagnostics.SensGoalsUnknown(name, enabled.Count == 0 ? "none" : string.Join(", ", enabled.Select(g => g.Name))); return; }
            _goalNames.AddRange(named);
        }
        else if (_settings.EffectiveResponses == DoeResponseSet.All) _goalNames.AddRange(enabled.Select(g => g.Name));
        else
        {
            var forYield = enabled.Where(g => g.Use is GoalUse.Yield or GoalUse.Both).ToList();
            _goalNames.AddRange((forYield.Count > 0 ? forYield : enabled.Where(g => g.Use is GoalUse.Opt or GoalUse.Both)).Select(g => g.Name));
        }
        _measures = tb.Measurements.Count > 0;
        if (_goalNames.Count == 0 && !_measures) { Refusal = StatisticsDiagnostics.SensNoResponse(); return; }

        // ── R-sens1-1: the factors ───────────────────────────────────────────────────
        var stat = setup.Variables.Where(e => e.IsStatistical).ToList();
        var statKeys = stat.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        var candidates = new List<(Tunable T, TunableEntry? Stat)>();

        void LeftOut(string key, string reason) => _notes.Add(StatisticsDiagnostics.SensLeftOut(key, reason));
        IEnumerable<(string Key, Tunable? T)> Named(IEnumerable<TunableEntry> entries)
            => entries.Select(e => (e.Key, catalog.Find(e.Key)));

        switch (source)
        {
            case SensitivityFactorSource.Stat:
                foreach (var (key, t) in Named(stat))
                    if (t is null) LeftOut(key, "it names no value of the design");
                    else candidates.Add((t, stat.First(e => e.Key == key)));
                break;
            case SensitivityFactorSource.Opt:
                foreach (var (key, t) in Named(setup.Variables.Where(e => e.Opt)))
                    if (t is null) LeftOut(key, "it names no value of the design");
                    else candidates.Add((t, null));
                break;
            default:
                // A complex value whose parts carry a tolerance steps those parts; one with none, its magnitude and
                // phase (D18's parts, never the whole value, and one coordinate system per value).
                // A port's values (its reference impedance) are the measurement's reference, not the design's: off
                // unless ports=on. A source's tone frequency is the analysis's: harmonic balance needs it on its tone
                // grid, so a step of it alone never evaluates. Each kind is named once rather than per instance.
                var types = InstanceTypes(tb, lib);
                var portsOut = new List<string>();
                var tonesOut = new List<string>();
                var tolerancedWholes = catalog.Tunables.Where(t => t.WholeKey is not null && statKeys.Contains(t.Key))
                                                       .Select(t => t.WholeKey!).ToHashSet(StringComparer.Ordinal);
                foreach (var t in catalog.Tunables)
                {
                    var entry = stat.FirstOrDefault(e => e.Key == t.Key);
                    string? type = types.GetValueOrDefault((t.Cell, t.Owner));
                    if (!_settings.EffectivePorts && type == "Port") { if (!t.IsInteger) portsOut.Add(t.Key); continue; }
                    if (entry is null && IsToneFrequency(type, t.Parameter)) { tonesOut.Add(t.Key); continue; }
                    if (entry is null && t.WholeKey is { } whole
                        && (tolerancedWholes.Contains(whole) || t.Part is ComplexPart.Real or ComplexPart.Imag)) continue;
                    candidates.Add((t, entry));
                }
                foreach (var e in stat.Where(e => catalog.Find(e.Key) is null)) LeftOut(e.Key, "it names no value of the design");
                if (portsOut.Count > 0) LeftOut(string.Join(", ", portsOut), "a port's impedance is the measurement's reference (ports=on steps it)");
                if (tonesOut.Count > 0) LeftOut(string.Join(", ", tonesOut),
                    "a source's tone frequency belongs to the analysis — harmonic balance needs it on its tone grid, so it cannot move alone (sweep it with the analysis)");
                break;
        }

        var accepted = new List<(Tunable T, TunableEntry? Stat)>();
        foreach (var (t, entry) in candidates)
        {
            if (t.DisabledReason is { } disabled) { LeftOut(t.Key, disabled.TrimEnd('.')); continue; }
            if (entry is null)
            {
                if (t.IsVendorPart) { LeftOut(t.Key, "a vendor part's choice of part is not a number"); continue; }
                if (t.IsPartValue) { LeftOut(t.Key, "a vendor part's value moves only with its tolerance (tol=part)"); continue; }
                if (t.IsInteger) { LeftOut(t.Key, "it takes whole numbers only"); continue; }
                if (t.Part != ComplexPart.Phase && t.Value == 0) { LeftOut(t.Key, "its value is 0, so a step of a percent of it moves nothing"); continue; }
            }
            accepted.Add((t, entry));
        }

        // ±kσ through each factor's own distribution — the shared helper, truncation clip included (D3).
        var levels = new SigmaLevels(accepted.Where(a => a.Stat is not null).Select(a => a.Stat!), _settings.EffectiveSigma);
        string sigmaText = _settings.EffectiveSigma == 1 ? "σ" : G(_settings.EffectiveSigma) + "σ";
        int level = 0;
        foreach (var (t, e) in accepted)
        {
            if (e is null) { AddStepped(t); continue; }
            int i = level++;
            var plus = levels.One(nominals, 2 * i + 1, i, +1);
            var minus = levels.One(nominals, 2 * i + 2, i, -1);
            string Text(SampledValues v) => v.Draws.TryGetValue(e.Key, out double x) ? TunableValue.Format(x, t.Unit, "G6") : "—";
            _factors.Add(new SensitivityFactor(e.Key, e.Key, t.Unit, SensitivityBasis.Sigma, sigmaText,
                                               TunableValue.Format(t.Value, t.Unit, "G6"), Text(plus), Text(minus)));
            _plus.Add((plus.Values, plus.Refusal));
            _minus.Add((minus.Values, minus.Refusal));
        }

        if (options.Vars is { } vars)
        {
            var offered = _factors.Select(f => f.Key).ToList();
            foreach (var key in vars)
                if (!offered.Contains(key, StringComparer.Ordinal))
                { Refusal = StatisticsDiagnostics.SensVarsNotOffered(key, offered.Count == 0 ? "none" : string.Join(", ", offered)); return; }
            for (int i = _factors.Count - 1; i >= 0; i--)
                if (!vars.Contains(_factors[i].Key, StringComparer.Ordinal)) { _factors.RemoveAt(i); _plus.RemoveAt(i); _minus.RemoveAt(i); }
        }
        if (_factors.Count == 0) { Refusal = StatisticsDiagnostics.SensNoFactors(AnalysisWord(source)); return; }

        // D8: each factor moves alone, and a kit's statistics are not factors.
        if (setup.Correlations.Count > 0 && _factors.Any(f => f.Basis == SensitivityBasis.Sigma))
            _notes.Add(StatisticsDiagnostics.SensUncorrelated(setup.Correlations.Count));
        var kit = StatisticalRun.NominalCalls(circuit, lib, tb, options.Sets);
        if (kit.Count > 0)
            _notes.Add(StatisticsDiagnostics.SensKitLeftOut(string.Join(", ", kit.Select(c => c.Stream).Distinct(StringComparer.Ordinal))));

        // The evaluator (D1): exactly the goals ranked, every analysis when a measure is a response, the line's parallelism.
        var evalSetup = setup.Clone();
        foreach (var g in evalSetup.Goals) g.Enabled = g.Enabled && _goalNames.Contains(g.Name);
        var st = evalSetup.Statistics?.Clone() ?? new StatisticsSettings();
        if (_measures) st.Scope = OptimizerScope.All;
        if (_settings.Parallelism is { } p) st.Parallelism = p;
        evalSetup.Statistics = st;
        _eval = OptimizationRun.ForEvaluation(circuit,
            new OptimizationOptions { Setup = evalSetup, Sets = options.Sets, Cancellation = options.Cancellation }, GoalUse.Both);
        if (_eval.Refusal is { } refused) { Refusal = refused; return; }
        // A yield run's warning that nothing varies is about trials; a sensitivity run moves the values itself.
        _notes.AddRange(_eval.Notes.Where(n => n.Id != "yield.goal.nothing-varies"));

        void AddStepped(Tunable t)
        {
            bool phase = t.Part == ComplexPart.Phase;
            double step = _settings.EffectiveStep;
            double up = phase ? t.Value + PhaseStepDegrees : t.Value * (1 + step / 100);
            double down = phase ? t.Value - PhaseStepDegrees : t.Value * (1 - step / 100);
            _factors.Add(new SensitivityFactor(t.Key, t.Key, t.Unit, phase ? SensitivityBasis.Degree : SensitivityBasis.Percent,
                phase ? G(PhaseStepDegrees) + "°" : G(step) + " %",
                TunableValue.Format(t.Value, t.Unit, "G6"), TunableValue.Format(up, t.Unit, "G6"), TunableValue.Format(down, t.Unit, "G6")));
            _plus.Add(MapOf(t, up));
            _minus.Add(MapOf(t, down));
        }
    }

    /// <summary>Prepares a run of <paramref name="circuit"/>'s setup. A run that cannot start carries its
    /// <see cref="Refusal"/> — <c>check</c> asks the same question — and <see cref="Run()"/> returns it at once.</summary>
    public static SensitivityRun Create(PreparedCircuit circuit, SensitivityOptions? options = null) => new(circuit, options ?? new SensitivityOptions());

    /// <summary><c>&lt;design&gt;.sens.npy</c> beside the schematic or netlist.</summary>
    public static string ResultPathFor(string sourcePath)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? ".",
                        Path.GetFileNameWithoutExtension(sourcePath) + ".sens.npy");

    public Diagnostic? Refusal { get; }

    public SensitivitySettings Settings => _settings;

    /// <summary>The factors, in the order they are stepped.</summary>
    public IReadOnlyList<SensitivityFactor> Factors => _factors;

    /// <summary>The goals ranked, by name.</summary>
    public IReadOnlyList<string> GoalNames => _goalNames;

    /// <summary>The goals ranked.</summary>
    public IReadOnlyList<OptimizationGoal> Goals => _eval?.Goals ?? [];

    public IReadOnlyList<Diagnostic> Notes => _notes;

    /// <summary>Simulations the evaluator has run, over this object's lifetime.</summary>
    public long Evaluations => _eval?.Evaluations ?? 0;

    /// <summary>How many evaluations run at once.</summary>
    public int Parallelism => _eval?.Parallelism ?? 1;

    /// <summary>The distinct value maps a run evaluates: the nominal and each factor's two steps, less duplicates and
    /// any step that cannot be set.</summary>
    public int PlannedEvaluations => Maps().Count;

    /// <summary>What <see cref="Run()"/> will simulate: <see cref="PlannedEvaluations"/> less what the evaluator's cache
    /// already holds (R-sens1-3).</summary>
    public int EvaluationsToSpend => _eval is null ? 0 : Maps().Count(m => !_eval.IsCached(m));

    public void Pause() => _resume.Reset();
    public void Resume() => _resume.Set();

    /// <summary>Ends the run after the batch in flight; what has evaluated is ranked.</summary>
    public void Stop() { _stop = true; _resume.Set(); }

    public bool IsPaused => _held.IsSet;
    public WaitHandle Held => _held.WaitHandle;

    public SensitivityResult? Result { get; private set; }

    /// <summary>The run's one-line description: <c>3 factors (2 at ±σ, 1 at ±1 %), 7 evaluations (2 already known)</c>.</summary>
    public string Describe()
    {
        int planned = PlannedEvaluations, spend = EvaluationsToSpend;
        var bases = _factors.GroupBy(f => f.BasisText).Select(g => $"{g.Count()} at ±{g.Key}");
        return $"{_factors.Count} factor(s) ({string.Join(", ", bases)}), {planned} evaluations" +
               (spend < planned ? $" ({planned - spend} already known)" : "");
    }

    /// <summary>Every instance's type, keyed as a tunable names its owner: (the cell — null at the top — and the
    /// instance's name).</summary>
    private static Dictionary<(string? Cell, string Owner), string> InstanceTypes(TestBench tb, Library lib)
    {
        var map = new Dictionary<(string?, string), string>();
        foreach (var i in tb.Instances) map.TryAdd((null, i.InstanceName), i.Reference);
        foreach (var cell in lib.Cells)
            foreach (var i in cell.Instances) map.TryAdd((cell.Name, i.InstanceName), i.Reference);
        return map;
    }

    /// <summary>The sources whose tone frequencies an HB analysis's tone grid fixes.</summary>
    private static readonly HashSet<string> ToneSources =
        new(["V_1Tone", "V_nTone", "I_1Tone", "I_nTone", "P1Tone", "PnTone", "MOD_Source"], StringComparer.OrdinalIgnoreCase);

    /// <summary>A tone source's frequency parameter: <c>Freq</c>, <c>Freq2</c> …, or a modulated source's <c>Fc</c>.</summary>
    private static bool IsToneFrequency(string? type, string? parameter)
        => type is not null && parameter is not null && ToneSources.Contains(type)
        && (parameter.StartsWith("Freq", StringComparison.OrdinalIgnoreCase) || parameter.Equals("Fc", StringComparison.OrdinalIgnoreCase));

    private static string AnalysisWord(SensitivityFactorSource s) => AnalysisDirectiveSchema.SensFactorTokens[(int)s];

    private static string G(double v) => v.ToString("G6", CultureInfo.InvariantCulture);

    /// <summary>The value map that sets one tunable to <paramref name="x"/> in its own unit — a complex part's whole
    /// value composed with the other part as the design holds it.</summary>
    private static (IReadOnlyDictionary<string, string>, Diagnostic?) MapOf(Tunable t, double x)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (t.Part is { } part && t.WholeKey is { } whole)
        {
            if (ComplexValue.Compose(t.Whole, new Dictionary<ComplexPart, double> { [part] = x }) is not { } z)
                return (map, StatisticsDiagnostics.TrialNoComplexValue(whole));
            map[whole] = ComplexValue.Format(z, t.WholeUnit, t.Form, "G15");
        }
        else map[t.Key] = TunableValue.Format(x, t.Unit);
        return (map, null);
    }

    /// <summary>The nominal (an empty map: the design exactly as it holds itself), then each factor's + and − step.</summary>
    private List<IReadOnlyDictionary<string, string>> Maps()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var maps = new List<IReadOnlyDictionary<string, string>>();
        void Add(IReadOnlyDictionary<string, string> m) { if (seen.Add(OptimizationRun.CacheKeyOf(m))) maps.Add(m); }
        Add(new Dictionary<string, string>(StringComparer.Ordinal));
        for (int i = 0; i < _factors.Count; i++)
        {
            if (_plus[i].Blocked is null) Add(_plus[i].Map);
            if (_minus[i].Blocked is null) Add(_minus[i].Map);
        }
        return maps;
    }

    // ── The run ──────────────────────────────────────────────────────────────────────

    /// <summary>Runs to the end on the calling thread: the evaluations, then the ranking.</summary>
    public SensitivityResult Run()
    {
        if (Refusal is { } refused)
            return Result = new SensitivityResult { Outcome = SensitivityOutcome.Refused, Refusal = refused, FinishReason = refused.Render(), Notes = _notes };

        var ct = _options.Cancellation;
        var sw = Stopwatch.StartNew();
        var notes = new List<Diagnostic>(_notes);
        long before = Evaluations;
        _stop = false;

        var maps = Maps();
        var byKey = new Dictionary<string, PointEvaluation>(StringComparer.Ordinal);
        string reason = $"{maps.Count} evaluations";
        try
        {
            int batch = Math.Max(1, _eval!.Parallelism);
            for (int next = 0; next < maps.Count; next += batch)
            {
                if (!_resume.IsSet)
                {
                    _held.Set();
                    WaitHandle.WaitAny([_resume.WaitHandle, ct.WaitHandle]);
                    _held.Reset();
                }
                ct.ThrowIfCancellationRequested();
                if (_stop && next > 0) { reason = $"stopped after {byKey.Count} of {maps.Count} evaluations"; break; }
                var chunk = maps.Skip(next).Take(batch).ToList();
                var results = _eval.EvaluateValues([.. chunk.Select(m => new ValuePoint(m))], keepData: _measures, ct);
                for (int j = 0; j < chunk.Count; j++) byKey[OptimizationRun.CacheKeyOf(chunk[j])] = results[j];
                _options.Progress?.Invoke(new SensitivityProgress(byKey.Count, maps.Count, sw.Elapsed));
            }
        }
        catch (OperationCanceledException)
        {
            return Result = new SensitivityResult { Outcome = SensitivityOutcome.Cancelled, FinishReason = "cancelled", Notes = notes };
        }

        // ── The nominal must evaluate (R-sens1-3) ───────────────────────────────────
        var nominal = byKey[OptimizationRun.CacheKeyOf(maps[0])];
        if (nominal.Status != PointStatus.Evaluated)
        {
            var failed = StatisticsDiagnostics.SensNominalFailed(nominal.Reason?.Render() ?? "no reason given");
            return Result = new SensitivityResult
            {
                Outcome = SensitivityOutcome.NominalFailed, Refusal = failed, FinishReason = failed.Render(), Notes = notes,
                Evaluations = Evaluations - before,
            };
        }

        // ── Each response's value at each point ─────────────────────────────────────
        var goals = Goals;
        var scales = goals.ToDictionary(g => g.Name, g => GoalResiduals.Scale(g) is { } s && s > 0 ? s : 1.0, StringComparer.Ordinal);
        double Goal(PointEvaluation p, string goal)
            => p.Status == PointStatus.Evaluated && p.Goals.FirstOrDefault(s => s.Name == goal) is { Error: null } s
               ? s.Margin / scales[goal] : double.NaN;
        var measureNames = new List<string>();
        var measureUnits = new Dictionary<string, string>(StringComparer.Ordinal);
        double Measure(PointEvaluation p, string name)
            => p.Status == PointStatus.Evaluated && p.Data is { } d && d.ContainsGroup(DataSet.MeasurementsGroup)
               && d.CubesIn(DataSet.MeasurementsGroup).TryGetValue(name, out var c) && StatisticalRun.IsScalar(c)
               ? c.RealValues[0] : double.NaN;
        if (nominal.Data is { } nd && nd.ContainsGroup(DataSet.MeasurementsGroup))
            foreach (var (name, cube) in nd.CubesIn(DataSet.MeasurementsGroup))
                if (!name.StartsWith("__", StringComparison.Ordinal) && StatisticalRun.IsScalar(cube))
                { measureNames.Add(name); measureUnits[name] = cube.Unit ?? ""; }

        // A bench whose only responses are measures over a sweep has nothing a single margin or number can rank —
        // said, not answered with an empty table.
        if (goals.Count == 0 && measureNames.Count == 0)
        {
            var none = StatisticsDiagnostics.SensNoScalarResponse();
            return Result = new SensitivityResult
            {
                Outcome = SensitivityOutcome.Refused, Refusal = none, FinishReason = none.Render(), Notes = notes,
                Evaluations = Evaluations - before,
            };
        }

        PointEvaluation? At(int factor, bool plus)
        {
            var (map, blocked) = plus ? _plus[factor] : _minus[factor];
            return blocked is null && byKey.TryGetValue(OptimizationRun.CacheKeyOf(map), out var p) ? p : null;
        }
        string? WhyNot(int factor, bool plus)
        {
            var (_, blocked) = plus ? _plus[factor] : _minus[factor];
            if (blocked is not null) return blocked.Render();
            var p = At(factor, plus);
            return p is null ? "not run: the run was stopped first" : p.Status == PointStatus.Evaluated ? null : p.Reason?.Render() ?? "did not evaluate";
        }

        SensitivityResponse Rank(string name, bool isGoal, string unit, Func<PointEvaluation, double> value)
        {
            double m0 = value(nominal);
            var list = new List<SensitivityContributor>();
            for (int i = 0; i < _factors.Count; i++)
            {
                var pp = At(i, true);
                var pm = At(i, false);
                double mp = pp is null ? double.NaN : value(pp), mm = pm is null ? double.NaN : value(pm);
                bool hasP = double.IsFinite(mp), hasM = double.IsFinite(mm);
                string? why = string.Join("; ", new[] { hasP ? null : "+: " + (WhyNot(i, true) ?? "the response has no value there"),
                                                        hasM ? null : "−: " + (WhyNot(i, false) ?? "the response has no value there") }
                                                    .OfType<string>());
                if (why.Length == 0) why = null;
                double delta, asym = double.NaN;
                SensitivityStatus status;
                if (hasP && hasM)
                {
                    delta = (mp - mm) / 2;
                    double dp = mp - m0, dm = mm - m0, big = Math.Max(Math.Abs(dp), Math.Abs(dm));
                    asym = big > 0 ? Math.Abs(dp + dm) / big : 0;
                    status = asym > NonlinearThreshold ? SensitivityStatus.Nonlinear : SensitivityStatus.Ok;
                }
                else if (hasP) { delta = mp - m0; status = SensitivityStatus.OneSided; }
                else if (hasM) { delta = m0 - mm; status = SensitivityStatus.OneSided; }
                else { delta = double.NaN; status = SensitivityStatus.Failed; }
                list.Add(new SensitivityContributor(_factors[i], delta, 0, mp, mm, asym, status, why));
            }
            double sum = list.Where(c => double.IsFinite(c.Delta)).Sum(c => c.Delta * c.Delta);
            var ranked = list.Select(c => c with { Share = double.IsFinite(c.Delta) && sum > 0 ? c.Delta * c.Delta / sum : 0 })
                             .OrderBy(c => double.IsFinite(c.Delta) ? 0 : 1)
                             .ThenByDescending(c => double.IsFinite(c.Delta) ? Math.Abs(c.Delta) : 0)
                             .ToList();
            double spread = Math.Sqrt(sum);
            double? estimate = isGoal && _factors.All(f => f.Basis == SensitivityBasis.Sigma) && double.IsFinite(m0)
                ? (spread > 0 ? NormalDistribution.Cdf(m0 / spread) : m0 >= 0 ? 1.0 : 0.0) : null;
            return new SensitivityResponse(name, isGoal, unit, m0, spread, estimate, ranked);
        }

        var goalResponses = goals.Select(g => Rank(g.Name, true, "", p => Goal(p, g.Name))).ToList();
        var measureResponses = measureNames.Select(m => Rank(m, false, measureUnits[m], p => Measure(p, m))).ToList();

        // D5: an estimate only when every basis is σ — said once, with the bases that keep it out.
        var otherBases = _factors.Where(f => f.Basis != SensitivityBasis.Sigma).Select(f => f.BasisText).Distinct().ToList();
        if (goalResponses.Count > 0 && otherBases.Count > 0)
            notes.Add(StatisticsDiagnostics.SensNoEstimate(string.Join(" and ", otherBases)));

        var result = new SensitivityResult
        {
            Outcome = SensitivityOutcome.Finished, FinishReason = reason, Notes = notes, Goals = goalResponses,
            Measures = measureResponses, Evaluations = Evaluations - before,
        };
        var ds = SensitivityDataSet.Build(this, result);
        string? written = null;
        if (_options.ResultPath is { } path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                DataSetExporter.Export(ds, path, ExportFormat.Npy);
                written = Path.GetFullPath(path);
            }
            catch (Exception ex) { notes.Add(StatisticsDiagnostics.SensWriteFailed(path, ex.Message)); }
        }
        return Result = new SensitivityResult
        {
            Outcome = SensitivityOutcome.Finished, FinishReason = reason, Notes = notes, Goals = goalResponses,
            Measures = measureResponses, Evaluations = result.Evaluations, Data = ds, WrittenPath = written,
        };
    }

    /// <summary>The text a status is written as: <c>ok</c>, <c>nonlinear</c>, <c>one-sided</c>, <c>failed</c>.</summary>
    public static string StatusWord(SensitivityStatus s) => s switch
    {
        SensitivityStatus.Ok        => "ok",
        SensitivityStatus.Nonlinear => "nonlinear",
        SensitivityStatus.OneSided  => "one-sided",
        _                           => "failed",
    };
}
