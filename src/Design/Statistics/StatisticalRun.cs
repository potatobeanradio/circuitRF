using System.Diagnostics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Optimization;
using CircuitRF.Diagnostics;
using CircuitRF.Engine.Statistics;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Design.Statistics;

/// <summary>
/// One Monte Carlo or yield run of one prepared circuit (brief-yield-4, docs/design/yield.md §8). The nominal is
/// evaluated first; then trials 1…N are drawn (<see cref="StatisticalSampler"/>, <see cref="SampleValues"/>,
/// <see cref="ExpressionDraws"/>), evaluated in batches through the optimizer's own evaluator
/// (<see cref="OptimizationRun.EvaluateValues(IReadOnlyList{ValuePoint}, bool, CancellationToken)"/>), scored
/// against the goals, counted into a yield with its Clopper–Pearson interval, and assembled into one
/// <see cref="DataSet"/> that the CLI, MCP, the Data Display and the Yield panel read without computing anything of
/// their own.
///
/// <para><b>Trials are numbered in the order they are drawn, never finished</b>, and slotted by number, so the
/// result is identical for any parallelism. Auto-stop is decided at every trial count a batch reached, in trial
/// order — the count it stops at is the first at which the interval clears the target, whatever the batch size, and
/// trials past it are discarded.</para>
///
/// <para><b>Pause, Resume, Stop</b> act between batches (yield overview D14): Pause finishes the trials in flight and
/// holds, Resume continues as if never paused (every draw is a pure function of the trial number), Stop keeps every
/// finished trial and reports over them. Cancelling abandons the run and writes nothing.</para>
/// </summary>
public sealed class StatisticalRun
{
    /// <summary>The analysis results the <c>auto</c> save policy keeps at most (R-ya4-5).</summary>
    public const long AutoSaveBudget = 256L * 1024 * 1024;

    /// <summary>A kit stream's key in the sampler, so a planned (<c>lhs</c>/<c>sobol</c>) kit draw can never share a
    /// stream with an entry spelled like it.</summary>
    private const string KitStreamPrefix = "kit:";

    private readonly PreparedCircuit _circuit;
    private readonly StatisticalOptions _options;
    private readonly OptimizationRun? _eval;
    private readonly TuningSetup _setup = new();
    private readonly StatisticsSettings _settings = new();
    private readonly List<TunableEntry> _entries = [];
    private readonly IReadOnlyList<Tunable> _nominals = [];
    private readonly TunableCatalog? _catalog;
    private readonly IReadOnlyDictionary<string, string> _bindings = new Dictionary<string, string>();
    private readonly IReadOnlyList<string> _heldPaths = [];
    private readonly Dictionary<string, Tunable> _byKey = new(StringComparer.Ordinal);
    private readonly StatisticalSampler? _sampler;
    private readonly List<string> _streams = [];
    private readonly IReadOnlyList<StatisticalCall> _calls = [];
    private readonly List<Diagnostic> _notes = [];
    private readonly object _evalLock = new();

    private readonly ManualResetEventSlim _resume = new(true);
    private readonly ManualResetEventSlim _held = new(false);
    private volatile bool _stop;

    private StatisticalRun(PreparedCircuit circuit, StatisticalOptions options)
    {
        _circuit = circuit;
        _options = options;
        Mode = options.Mode;

        _eval = OptimizationRun.ForEvaluation(circuit,
            new OptimizationOptions { Setup = options.Setup, Sets = options.Sets, Cancellation = options.Cancellation },
            options.Mode == StatisticalMode.Yield ? GoalUse.Yield : GoalUse.Both);
        if (_eval.Refusal is { } refused) { Refusal = refused; return; }
        if (circuit.Lib is not { } lib || circuit.Tb is not { } tb) { Refusal = OptimizationDiagnostics.EvaluationFailed(circuit.ReadError ?? ""); return; }

        _setup    = options.Setup ?? tb.Tuning ?? new TuningSetup();
        _settings = _setup.Statistics ?? new StatisticsSettings();
        _entries.AddRange(_setup.Variables.Where(e => e.IsStatistical));
        _notes.AddRange(_eval.Notes);

        if (Mode == StatisticalMode.Yield && _eval.Goals.Count == 0) { Refusal = StatisticsDiagnostics.RunNoYieldGoal(); return; }

        _calls = NominalCalls(circuit, lib, tb, options.Sets);
        if (_entries.Count == 0 && _calls.Count == 0) { Refusal = StatisticsDiagnostics.RunNothingVaries(); return; }

        var bench = tb;
        if (options.Setup is not null)
        {
            bench = new TestBench(tb.Name) { Tuning = options.Setup };
            bench.Instances.AddRange(tb.Instances);
            bench.GlobalVariables.AddRange(tb.GlobalVariables);
            bench.Analyses.AddRange(tb.Analyses);
        }
        // A corner's bindings (brief-yield-6 R-ya6-3) are the nominal every spread is drawn around: a percent
        // spread on a value the corner moves follows it, as it follows a value centering moves.
        _bindings = options.Bindings ?? _bindings;
        // Library parts whose own draws stay at nominal (brief vendorlib-13): those the statistics line holds
        // (R-vl13-1c), and those a corner sets the corner of — the corner is that part's limit (R-vl13-2g).
        var heldParts = _settings.NominalPartList.Concat(_bindings.Keys
            .Where(k => k.StartsWith(CellCorner.PartPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(k => k[CellCorner.PartPrefix.Length..]));
        _heldPaths = LibraryParts.HeldPaths(tb, lib, heldParts);
        _catalog  = TunableCatalog.FromNetlist(bench, lib, parts: circuit.PartTunables);
        _nominals = SampleValues.Nominals(_catalog, _bindings);
        foreach (var t in _nominals) _byKey.TryAdd(t.Key, t);

        // Kit streams join the plan only where the plan places trials jointly (lhs, sobol); under random each
        // draws natively from its own stream, so a trial is the same drawn alone or with every other stream.
        var streams = _entries.Select(e => e.Key).ToList();
        if (_settings.Sampling != StatSampling.Random)
            streams.AddRange(_calls.Select(c => KitStreamPrefix + c.Stream));
        _streams.AddRange(streams);
        _sampler = new StatisticalSampler(streams, _settings.Sampling, _settings.EffectiveSeed, _settings.EffectiveTrials,
                                          StatisticsValidator.CorrelationOf(_setup));
        _notes.AddRange(_sampler.Notes);
    }

    /// <summary>Prepares a run of <paramref name="circuit"/>'s setup. A run that cannot start carries its
    /// <see cref="Refusal"/> — in <c>check</c>'s words, since both read the same validator — and <see cref="Run"/>
    /// returns it at once.</summary>
    public static StatisticalRun Create(PreparedCircuit circuit, StatisticalOptions? options = null)
        => new(circuit, options ?? new StatisticalOptions());

    /// <summary><c>&lt;design&gt;.yield.npy</c> beside the schematic or netlist (yield overview D9) — never
    /// <c>run.npy</c>, which Simulate owns.</summary>
    public static string ResultPathFor(string sourcePath)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? ".",
                        Path.GetFileNameWithoutExtension(sourcePath) + ".yield.npy");

    public StatisticalMode Mode { get; }
    public Diagnostic? Refusal { get; }

    /// <summary>The goals the run scores, in setup order.</summary>
    public IReadOnlyList<OptimizationGoal> Goals => _eval?.Goals ?? [];

    /// <summary>The statistical entries, in setup order.</summary>
    public IReadOnlyList<TunableEntry> Entries => _entries;

    /// <summary>Every distribution call the nominal design reached.</summary>
    public IReadOnlyList<StatisticalCall> KitCalls => _calls;

    public IReadOnlyList<Diagnostic> Notes => _notes;

    /// <summary>The statistics settings the run applies — the setup's, defaults held as null.</summary>
    public StatisticsSettings Settings => _settings;

    /// <summary>Trials a batch evaluates at once: the statistics line's <c>parallel=</c>, else the evaluator's.</summary>
    public int Parallelism => Math.Max(1, _options.BatchSize ?? _eval?.Parallelism ?? 1);

    /// <summary>Simulations actually run (the nominal, trials, re-runs).</summary>
    public long Evaluations => _eval?.Evaluations ?? 0;

    /// <summary>The last run's result; null before one finishes.</summary>
    public StatisticalResult? Result { get; private set; }

    /// <summary>Holds the run after the batch in flight.</summary>
    public void Pause() => _resume.Reset();

    public void Resume() => _resume.Set();

    /// <summary>Ends the run after the batch in flight, keeping every finished trial.</summary>
    public void Stop() { _stop = true; _resume.Set(); }

    public bool IsPaused => _held.IsSet;

    /// <summary>Signalled while the run holds at a pause.</summary>
    public WaitHandle Held => _held.WaitHandle;

    // ── The run ─────────────────────────────────────────────────────────────────────

    /// <summary>Runs to the end on the calling thread.</summary>
    public StatisticalResult Run()
    {
        if (Refusal is { } refused) return Result = Refused(refused);

        var ct = _options.Cancellation;
        var sw = Stopwatch.StartNew();
        var records = new List<TrialRecord>();
        PointEvaluation nominal;
        SaveReport save;
        AutoStopVerdict? verdict = null;
        string reason;
        var lastPublish = Stopwatch.StartNew();
        try
        {
            lock (_evalLock)
                nominal = _eval!.EvaluateValues([new ValuePoint(_bindings, null, "nominal")], true, ct)[0];
            if (nominal.Status != PointStatus.Evaluated)
                return Result = Refused(StatisticsDiagnostics.RunNominalFailed(nominal.Reason?.Render() ?? "no reason given"));

            int cap = _settings.EffectiveTrials;
            save = SavePolicy(nominal.Data, cap);
            int batch = Math.Max(1, _options.BatchSize ?? _eval.Parallelism);
            double? target = Mode == StatisticalMode.Yield && _settings.Target is { } tp ? tp / 100 : null;
            bool autostop = Mode == StatisticalMode.Yield && _settings.AutoStop && target is not null;
            double confidence = _settings.EffectiveConfidence / 100;
            bool countFails = _settings.NonConverged == NonConvergedPolicy.Fail;
            int passes = 0, counted = 0;

            int next = 1;
            reason = $"{cap} trials";
            while (next <= cap)
            {
                if (!_resume.IsSet)
                {
                    _held.Set();
                    WaitHandle.WaitAny([_resume.WaitHandle, ct.WaitHandle]);
                    _held.Reset();
                }
                ct.ThrowIfCancellationRequested();
                if (_stop) { reason = $"stopped after {records.Count} trials"; break; }

                int count = Math.Min(batch, cap - next + 1);
                var trials = Enumerable.Range(next, count).ToList();
                records.AddRange(EvaluateTrials(trials, keepData: next <= save.KeptTrials, save.KeptTrials, ct));
                next += count;

                if (autostop)
                {
                    // In trial order, at every count the batch reached: the first count that decides is where the
                    // run stops, whatever the batch size was.
                    for (int i = records.Count - count; i < records.Count; i++)
                    {
                        var r = records[i];
                        if (r.Evaluated || countFails) counted++;
                        if (r.Pass) passes++;
                        var v = YieldEstimate.Of(passes, counted, confidence).Decide(target!.Value);
                        if (v == AutoStopVerdict.Continue) continue;
                        verdict = v;
                        records.RemoveRange(i + 1, records.Count - i - 1);
                        reason = AutoStopReason(records.Count, _settings.EffectiveConfidence, v, target!.Value);
                        break;
                    }
                    if (verdict is not null) break;
                }

                _options.Progress?.Report(Progress(records, sw.Elapsed));
                if (_options.Publish is { } publish && lastPublish.Elapsed >= _options.PublishInterval)
                {
                    publish(Assemble(records, nominal, save, verdict, reason, partial: true));
                    lastPublish.Restart();
                }
            }
        }
        catch (OperationCanceledException)
        {
            return Result = new StatisticalResult { Outcome = StatisticalOutcome.Cancelled, Mode = Mode, FinishReason = "cancelled", Notes = _notes };
        }

        return Result = Finish(records, nominal, save, verdict, reason);
    }

    /// <summary>
    /// Trial <paramref name="trial"/>'s full results regardless of the save policy (R-ya4-7) — the same draws, the
    /// same values and the same evaluation as inside a full run (yield overview D5), simulated afresh. What
    /// "Re-run trial", Send-to-Tuning and <c>yield trial</c> use. Safe to call before, between or after runs; it
    /// waits for a batch in flight.
    /// </summary>
    public TrialRecord EvaluateTrial(int trial, CancellationToken ct = default)
    {
        if (Refusal is { } refused)
            return Blank(trial, refused);
        if (trial < 1 || (_settings.Sampling == StatSampling.Lhs && trial > _settings.EffectiveTrials))
            return Blank(trial, StatisticsDiagnostics.TrialOutOfRange(trial,
                trial < 1 ? "trials are numbered from 1." : $"a Latin hypercube of {_settings.EffectiveTrials} trials places no more."));
        return EvaluateTrials([trial], keepData: true, int.MaxValue, ct, rerun: true)[0];
    }

    /// <summary>
    /// Trials evaluated at moved designs (brief-yield-11 R-ya11-2): each (trial, at) pair draws trial t's z-vector —
    /// the same numbers whatever the design — applied to the nominal at <c>at</c>, a candidate's values, which the map
    /// evaluated holds under the trial's draws. The pairs run as one batch through the evaluator; no analysis results
    /// are kept. Design centering's door: two candidates scored on the same trials differ by the design alone.
    /// </summary>
    internal TrialRecord[] EvaluateAt(IReadOnlyList<(int Trial, IReadOnlyDictionary<string, string> At)> points, CancellationToken ct)
    {
        var result  = new TrialRecord[points.Count];
        var batch   = new List<ValuePoint>();
        var slots   = new List<(int Index, SampledValues Values, StatisticalSample Sample, ExpressionDraws Draws)>();
        var moved   = new Dictionary<IReadOnlyDictionary<string, string>, IReadOnlyList<Tunable>>(ReferenceEqualityComparer.Instance);
        double scale = _settings.SigmaScale ?? 1;
        for (int i = 0; i < points.Count; i++)
        {
            var (t, at) = points[i];
            if (!moved.TryGetValue(at, out var nominals)) moved[at] = nominals = NominalsAt(at);
            var (values, entrySample, draws) = Draw(t, scale, nominals);
            if (values.Refused)
            {
                result[i] = Record(t, PointStatus.DidNotEvaluate, values.Refusal, values, entrySample, null, null, false);
                continue;
            }
            slots.Add((i, values, entrySample, draws));
            batch.Add(new ValuePoint(PointValues(values.Values, at), draws, $"trial {t}"));
        }

        IReadOnlyList<PointEvaluation> evaluated;
        lock (_evalLock) evaluated = _eval!.EvaluateValues(batch, keepData: false, ct);
        for (int k = 0; k < slots.Count; k++)
        {
            var (i, values, sample, draws) = slots[k];
            var p = evaluated[k];
            result[i] = Record(points[i].Trial, p.Status, p.Reason, values, sample, draws, p, false);
        }
        return result;
    }

    // ── z-space (brief-yield-12) ────────────────────────────────────────────────────

    /// <summary>
    /// The coordinates a surrogate sees (R-ya12-2): each statistical entry, each kit process stream, and ONE per
    /// instance for all of its kit mismatch streams — YA-4 R-ya4-9's grouping. An instance's coordinate moves every
    /// one of its streams by x/√n, so it is itself a standard normal (Σz/√n of n independent ones). A kind switched
    /// off (<c>process=0</c>, <c>mismatch=0</c>) draws nothing and has no coordinate.
    /// </summary>
    internal IReadOnlyList<SurrogateDimension> SurrogateDimensions()
    {
        var dims = new List<SurrogateDimension>();
        foreach (var e in _entries) dims.Add(new SurrogateDimension(e.Key, SurrogateDimensionKind.Entry, [e.Key]));
        var streams = _calls.GroupBy(c => c.Stream, StringComparer.Ordinal).Select(g => g.First()).ToList();
        if (_settings.Process ?? true)
            foreach (var c in streams.Where(c => c.Kind == StatisticalKind.Process).OrderBy(c => c.Stream, StringComparer.Ordinal))
                dims.Add(new SurrogateDimension(c.Stream, SurrogateDimensionKind.Process, [c.Stream]));
        if (_settings.Mismatch ?? true)
            foreach (var g in streams.Where(c => c.Kind == StatisticalKind.Mismatch)
                                     .GroupBy(c => StatisticalContributions.InstanceOf(c.Stream), StringComparer.Ordinal)
                                     .OrderBy(g => g.Key, StringComparer.Ordinal))
                dims.Add(new SurrogateDimension(g.Key, SurrogateDimensionKind.Mismatch, [.. g.Select(c => c.Stream)]));
        return dims;
    }

    /// <summary>A trial's place on <paramref name="dims"/>: entries and process streams by their own z, an instance's
    /// mismatch by Σz/√n. A stream the trial did not draw sat at its nominal (z = 0).</summary>
    internal static double[] CoordinatesOf(IReadOnlyList<SurrogateDimension> dims, IReadOnlyDictionary<string, double> entryZ,
                                           Func<string, double> kitZ)
    {
        var x = new double[dims.Count];
        for (int i = 0; i < dims.Count; i++)
        {
            var d = dims[i];
            if (d.Kind == SurrogateDimensionKind.Entry) { x[i] = entryZ.TryGetValue(d.Name, out double z) ? z : 0; continue; }
            double sum = 0;
            foreach (var s in d.Streams) sum += kitZ(s);
            x[i] = sum / Math.Sqrt(d.Streams.Count);
        }
        return x;
    }

    /// <summary>
    /// The first <paramref name="count"/> trials of this run's seed and sampling as coordinates on
    /// <paramref name="dims"/> — the surrogate's virtual trials (R-ya12-2), drawn exactly as trials 1…count would be
    /// (under <c>random</c>, trials 1…M ARE the common trials).
    /// </summary>
    internal double[][] VirtualCoordinates(IReadOnlyList<SurrogateDimension> dims, int count)
    {
        var sampler = count == _settings.EffectiveTrials
            ? _sampler!
            : new StatisticalSampler(_streams, _settings.Sampling, _settings.EffectiveSeed, count, StatisticsValidator.CorrelationOf(_setup));
        ulong seed = unchecked((ulong)_settings.EffectiveSeed);
        var result = new double[count][];
        for (int t = 1; t <= count; t++)
        {
            var z = sampler.Sample(t).Z;
            int trial = t;
            result[t - 1] = CoordinatesOf(dims, z, stream =>
                z.TryGetValue(KitStreamPrefix + stream, out double planned) ? planned
                    : StatStreams.Normal(seed, trial, StatStreams.Id(stream), ExpressionDraws.StreamSlot));
        }
        return result;
    }

    /// <summary>
    /// Points of z-space evaluated at moved designs (R-ya12-2): coordinate vector x on <paramref name="dims"/> applied
    /// to the nominal at <c>at</c>, as a trial's z-vector is. One batch through the evaluator; nothing is kept. The
    /// records carry trial number 0 — a design point is not a trial.
    /// </summary>
    internal TrialRecord[] EvaluateAtZ(IReadOnlyList<SurrogateDimension> dims,
                                       IReadOnlyList<(double[] X, IReadOnlyDictionary<string, string> At)> points, CancellationToken ct)
    {
        var result = new TrialRecord[points.Count];
        var batch  = new List<ValuePoint>();
        var slots  = new List<(int Index, SampledValues Values, StatisticalSample Sample, ExpressionDraws Draws)>();
        var moved  = new Dictionary<IReadOnlyDictionary<string, string>, IReadOnlyList<Tunable>>(ReferenceEqualityComparer.Instance);
        double scale = _settings.SigmaScale ?? 1;
        for (int i = 0; i < points.Count; i++)
        {
            var (x, at) = points[i];
            if (!moved.TryGetValue(at, out var nominals)) moved[at] = nominals = NominalsAt(at);
            var entryZ = new Dictionary<string, double>(StringComparer.Ordinal);
            var kitZ   = new Dictionary<string, double>(StringComparer.Ordinal);
            for (int k = 0; k < dims.Count; k++)
            {
                var d = dims[k];
                if (d.Kind == SurrogateDimensionKind.Entry) entryZ[d.Name] = x[k];
                else foreach (var s in d.Streams) kitZ[s] = x[k] / Math.Sqrt(d.Streams.Count);
            }
            foreach (var e in _entries) entryZ.TryAdd(e.Key, 0);
            var sample = new StatisticalSample(0, entryZ);
            var values = SampleValues.Apply(_entries, nominals, sample, scale);
            if (values.Refused)
            {
                result[i] = Record(0, PointStatus.DidNotEvaluate, values.Refusal, values, sample, null, null, false);
                continue;
            }
            var draws = new ExpressionDraws(_settings.EffectiveSeed, 1, _settings.Process ?? true, _settings.Mismatch ?? true,
                                            scale, kitZ, unplannedAtNominal: true, held: _heldPaths);
            slots.Add((i, values, sample, draws));
            string tag = "z " + string.Join(",", x.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
            batch.Add(new ValuePoint(PointValues(values.Values, at), draws, tag));
        }

        IReadOnlyList<PointEvaluation> evaluated;
        lock (_evalLock) evaluated = _eval!.EvaluateValues(batch, keepData: false, ct);
        for (int k = 0; k < slots.Count; k++)
        {
            var (i, values, sample, draws) = slots[k];
            var p = evaluated[k];
            result[i] = Record(0, p.Status, p.Reason, values, sample, draws, p, false);
        }
        return result;
    }

    // ── Trials ──────────────────────────────────────────────────────────────────────

    private TrialRecord[] EvaluateTrials(IReadOnlyList<int> trials, bool keepData, int keptLimit, CancellationToken ct,
                                         bool rerun = false)
    {
        var result  = new TrialRecord[trials.Count];
        var points  = new List<ValuePoint>();
        var slots   = new List<(int Index, SampledValues Values, StatisticalSample Sample, ExpressionDraws Draws)>();
        double scale = _settings.SigmaScale ?? 1;
        for (int i = 0; i < trials.Count; i++)
        {
            int t = trials[i];
            var (values, entrySample, draws) = Draw(t, scale);
            if (values.Refused)
            {
                result[i] = Record(t, PointStatus.DidNotEvaluate, values.Refusal, values, entrySample, null, null, false);
                continue;
            }
            slots.Add((i, values, entrySample, draws));
            // brief-sar-10 R-sar10-5 — a re-run trial (the worst one, say) writes its recombined fields for the 3D view
            points.Add(new ValuePoint(PointValues(values.Values), draws, rerun ? $"trial {t} (re-run)" : $"trial {t}")
                       { WriteFieldsAs = rerun ? $"trial {t}" : null });
        }

        IReadOnlyList<PointEvaluation> evaluated;
        lock (_evalLock) evaluated = _eval!.EvaluateValues(points, keepData, ct);
        for (int k = 0; k < slots.Count; k++)
        {
            var (i, values, sample, draws) = slots[k];
            var p = evaluated[k];
            result[i] = Record(trials[i], p.Status, p.Reason, values, sample, draws, p, trials[i] <= keptLimit);
        }
        return result;
    }

    /// <summary>Trial <paramref name="t"/>'s draws — a pure function of t: the plan's sample, the entries' values at
    /// it, and the expression draws.</summary>
    private (SampledValues Values, StatisticalSample Sample, ExpressionDraws Draws) Draw(int t, double scale,
                                                                                       IReadOnlyList<Tunable>? nominals = null)
    {
        var sample = _sampler!.Sample(t);
        var entryZ = new Dictionary<string, double>(StringComparer.Ordinal);
        var kitZ   = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (stream, z) in sample.Z)
        {
            if (stream.StartsWith(KitStreamPrefix, StringComparison.Ordinal)) kitZ[stream[KitStreamPrefix.Length..]] = z;
            else entryZ[stream] = z;
        }
        var entrySample = new StatisticalSample(t, entryZ);
        return (SampleValues.Apply(_entries, nominals ?? _nominals, entrySample, scale), entrySample,
                ExpressionDraws.For(_settings, t, kitZ.Count == 0 ? null : kitZ, held: _heldPaths));
    }

    /// <summary>The value map a point evaluates: the moved design's values, the corner's bindings over them, then the
    /// trial's drawn values over those.</summary>
    private IReadOnlyDictionary<string, string> PointValues(IReadOnlyDictionary<string, string> drawn,
                                                            IReadOnlyDictionary<string, string>? at = null)
    {
        if (_bindings.Count == 0 && at is not { Count: > 0 }) return drawn;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (at is not null) foreach (var (k, v) in at) map[k] = v;
        foreach (var (k, v) in _bindings) map[k] = v;
        foreach (var (k, v) in drawn) map[k] = v;
        return map;
    }

    /// <summary>The nominals at a moved design (<paramref name="at"/>, a candidate's values) with the corner's
    /// bindings over them — what a percent spread is drawn around there (brief-yield-7 R-ya7-3).</summary>
    private IReadOnlyList<Tunable> NominalsAt(IReadOnlyDictionary<string, string>? at)
    {
        if (at is not { Count: > 0 } || _catalog is null) return _nominals;
        var moved = new Dictionary<string, string>(at, StringComparer.Ordinal);
        foreach (var (k, v) in _bindings) moved[k] = v;
        return SampleValues.Nominals(_catalog, moved);
    }

    /// <summary>
    /// A statistical corner's replay (brief-yield-6 R-ya6-4): trial <paramref name="trial"/>'s z-vector applied to
    /// the CURRENT nominal. With <paramref name="recorded"/> — the z-vector the run that made the corner recorded —
    /// that vector is used as it stands: an entry or distribution call the recording has no draw for takes its nominal,
    /// and a recorded stream the design no longer has is named in a warning. Without one, the vector is drawn afresh
    /// from (seed, trial, stream) — the same numbers wherever the streams are the ones the run had. With
    /// <paramref name="at"/> — a moved design's values, an optimizer's candidate or the Tuning panel's sliders — the
    /// vector is applied to THAT nominal, and the map evaluated holds those values under the corner's (brief-yield-7
    /// R-ya7-3).
    /// </summary>
    internal StatisticalReplay Replay(int trial, RecordedTrial? recorded, IReadOnlyDictionary<string, string>? at = null)
    {
        if (Refusal is { } refused) return new StatisticalReplay(trial, new Dictionary<string, string>(), null, [], refused);
        if (trial < 1 || (_settings.Sampling == StatSampling.Lhs && trial > _settings.EffectiveTrials))
            return new StatisticalReplay(trial, new Dictionary<string, string>(), null, [], StatisticsDiagnostics.TrialOutOfRange(trial,
                trial < 1 ? "trials are numbered from 1." : $"a Latin hypercube of {_settings.EffectiveTrials} trials places no more."));
        double scale = _settings.SigmaScale ?? 1;
        var nominals = NominalsAt(at);
        if (recorded is null)
        {
            var (values, _, draws) = Draw(trial, scale, nominals);
            return new StatisticalReplay(trial, PointValues(values.Values, at), draws, [], values.Refusal);
        }

        var notes = new List<Diagnostic>();
        var gone = recorded.EntryZ.Keys.Where(k => !_entries.Any(e => e.Key == k))
            .Concat(recorded.KitZ.Keys.Where(k => !_calls.Any(c => c.Stream == k)))
            .ToList();
        if (gone.Count > 0) notes.Add(StatisticsDiagnostics.ReplayStreamsGone(trial, gone));

        var kept = _entries.Where(e => recorded.EntryZ.ContainsKey(e.Key)).ToList();
        var sample = new StatisticalSample(trial, recorded.EntryZ);
        var drawn = SampleValues.Apply(kept, nominals, sample, scale);
        var replayDraws = new ExpressionDraws(_settings.EffectiveSeed, trial, _settings.Process ?? true, _settings.Mismatch ?? true,
                                              scale, recorded.KitZ, unplannedAtNominal: true, held: _heldPaths);
        return new StatisticalReplay(trial, PointValues(drawn.Values, at), replayDraws, notes, drawn.Refusal);
    }

    private TrialRecord Record(int trial, PointStatus status, Diagnostic? reason, SampledValues values, StatisticalSample sample,
                               ExpressionDraws? draws, PointEvaluation? p, bool keep)
    {
        var si = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (key, x) in values.Draws)
            si[key] = _byKey.TryGetValue(key, out var t) ? x * UnitScale(t.Unit) : x;
        var kit = draws is null
            ? new Dictionary<string, (StatisticalKind, double)>(StringComparer.Ordinal)
            : draws.Drawn.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        return new TrialRecord(trial, status, reason, values.Values, si, sample.Z, kit,
                               p?.Goals ?? [], Scalars(p?.Data), keep ? p?.Data : null);
    }

    private TrialRecord Blank(int trial, Diagnostic why) => new(
        trial, PointStatus.DidNotEvaluate, why, new Dictionary<string, string>(), new Dictionary<string, double>(),
        new Dictionary<string, double>(), new Dictionary<string, (StatisticalKind, double)>(), [], new Dictionary<string, double>(), null);

    /// <summary>Every real scalar the measurements group holds — kept for every trial whatever the save policy, since
    /// they are scalars.</summary>
    private static IReadOnlyDictionary<string, double> Scalars(DataSet? data)
    {
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        if (data is null || !data.ContainsGroup(DataSet.MeasurementsGroup)) return map;
        foreach (var (name, cube) in data.CubesIn(DataSet.MeasurementsGroup))
            if (IsScalar(cube)) map[name] = cube.RealValues[0];
        return map;
    }

    internal static bool IsScalar(DataCube c) => c.Rank == 0 && c.DataKind == DataKind.Real;

    /// <summary>The finish reason of an auto-stopped run — the <c>yield.stopped</c> label and the CLI's line — formatted
    /// culture-invariantly, so 99.5 % is never written "99,5 %" (brief-yield-15 R-ya15-6).</summary>
    internal static string AutoStopReason(int trials, double confidencePercent, AutoStopVerdict verdict, double target)
        => string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"auto-stopped after {trials} trials: the {confidencePercent:G4} % interval lies {(verdict == AutoStopVerdict.Above ? "above" : "below")} the {target * 100:G4} % target");

    internal static double UnitScale(string unit) => unit.Length == 0 ? 1 : Units.Scale(unit) ?? 1;

    // ── Counting ────────────────────────────────────────────────────────────────────

    private (YieldEstimate Overall, IReadOnlyList<GoalYield> Goals) Count(IReadOnlyList<TrialRecord> records)
    {
        var goals = Goals;
        if (goals.Count == 0) return (YieldEstimate.None, []);
        double confidence = _settings.EffectiveConfidence / 100;
        bool countFails = _settings.NonConverged == NonConvergedPolicy.Fail;
        int counted = records.Count(r => r.Evaluated || countFails);
        var overall = YieldEstimate.Of(records.Count(r => r.Pass), counted, confidence);
        var per = goals.Select(g => new GoalYield(g.Name, YieldEstimate.Of(
            records.Count(r => r.Evaluated && r.Goals.Any(s => s.Name == g.Name && s.Met)), counted, confidence))).ToList();
        return (overall, per);
    }

    private StatisticalProgress Progress(IReadOnlyList<TrialRecord> records, TimeSpan elapsed)
    {
        var (overall, per) = Count(records);
        return new StatisticalProgress(records.Count, records.Count(r => !r.Evaluated), overall, per, elapsed);
    }

    // ── Save policy (R-ya4-5) ─────────────────────────────────────────────────────────

    private SaveReport SavePolicy(DataSet? nominal, int trials)
    {
        long perTrial = 0;
        if (nominal is not null)
            foreach (var g in nominal.Groups)
                foreach (var (name, cube) in nominal.CubesIn(g))
                    if (!name.StartsWith("__", StringComparison.Ordinal) && !(g == DataSet.MeasurementsGroup && IsScalar(cube)))
                        perTrial += (long)cube.BufferLength * (cube.DataKind == DataKind.Complex ? 16 : 8);

        string text = _settings.Save ?? "auto";
        static string MB(long b) => b < 1024 * 1024 ? $"{Math.Max(1, b / 1024)} KB"
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{b / (1024.0 * 1024):0.#} MB");
        switch (text.ToLowerInvariant())
        {
            case "scalars":
                return new SaveReport(text, 0, perTrial, "save=scalars: no trial's analysis results are kept.");
            case "all":
                return new SaveReport(text, trials, perTrial, $"save=all: every trial's analysis results are kept ({MB(perTrial * trials)}).");
        }
        if (int.TryParse(text, out int n) && n >= 0)
            return new SaveReport(text, Math.Min(n, trials), perTrial, $"save={n}: the first {Math.Min(n, trials)} trials' analysis results are kept.");

        long fit = perTrial == 0 ? trials : AutoSaveBudget / perTrial;
        if (fit >= trials)
            return new SaveReport("auto", trials, perTrial, $"save=auto: every trial's analysis results are kept ({MB(perTrial * trials)}).");
        int kept = (int)fit;
        return new SaveReport("auto", kept, perTrial,
            $"save=auto: the first {kept} trials' analysis results are kept — all {trials} would take {MB(perTrial * trials)}, over {MB(AutoSaveBudget)}.");
    }

    // ── Finishing ──────────────────────────────────────────────────────────────────

    private StatisticalResult Refused(Diagnostic why) => new()
    {
        Outcome = StatisticalOutcome.Refused, Mode = Mode, Refusal = why, FinishReason = why.Render(), Notes = _notes,
    };

    private StatisticalResult Finish(List<TrialRecord> records, PointEvaluation nominal, SaveReport save,
                                     AutoStopVerdict? verdict, string reason)
    {
        var notes = new List<Diagnostic>(_notes) { StatisticsDiagnostics.RunSaved(save.Sentence) };
        int dne = records.Count(r => !r.Evaluated);
        bool countFails = _settings.NonConverged == NonConvergedPolicy.Fail;
        if (dne > 0) notes.Add(StatisticsDiagnostics.RunDidNotEvaluate(dne, records.Count, countFails));

        var (overall, per) = Count(records);
        StatisticalOutcome outcome;
        Diagnostic? refusal = null;
        if (records.Count > 0 && dne == records.Count)
        {
            outcome = StatisticalOutcome.NoneEvaluated;
            refusal = StatisticsDiagnostics.RunNoneEvaluated(records.Count, records[0].Reason?.Render() ?? "no reason given");
        }
        else if (Mode == StatisticalMode.Yield && _settings.Target is { } tp && !double.IsNaN(overall.Yield)
                 && (verdict == AutoStopVerdict.Below || (verdict is null && overall.Yield < tp / 100)))
            outcome = StatisticalOutcome.BelowTarget;
        else
            outcome = StatisticalOutcome.Finished;

        var data = Assemble(records, nominal, save, verdict, reason, partial: false);
        string? written = null;
        if (_options.ResultPath is { } path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                DataSetExporter.Export(data, path, ExportFormat.Npy);
                written = Path.GetFullPath(path);
            }
            catch (Exception ex) { notes.Add(StatisticsDiagnostics.RunWriteFailed(path, ex.Message)); }
        }
        _options.Publish?.Invoke(data);

        return new StatisticalResult
        {
            Outcome = outcome, Mode = Mode, FinishReason = reason, Refusal = refusal, Notes = notes,
            Trials = records.Count, DidNotEvaluate = dne, Yield = overall, Goals = per, AutoStop = verdict,
            Save = save, Nominal = nominal, Records = records, Data = data, WrittenPath = written, Settings = _settings,
        };
    }

    private DataSet Assemble(IReadOnlyList<TrialRecord> records, PointEvaluation nominal, SaveReport save,
                             AutoStopVerdict? verdict, string reason, bool partial)
    {
        var (overall, per) = Count(records);
        return StatisticalDataSet.Build(new StatisticalDataSet.Inputs(
            Mode, _settings, Goals, _entries, _byKey, records, nominal, save, overall, per, verdict,
            partial ? "running" : reason));
    }

    // ── Contributions and worst trials ────────────────────────────────────────────────

    /// <summary>
    /// Which statistical variables drive <paramref name="goalOrMeasure"/> over the last run (R-ya4-9): a goal's
    /// value at its tightest point, or a scalar measurement, regressed on the trials' z-values. Never run unasked.
    /// </summary>
    public ContributionReport Contributions(string goalOrMeasure)
        => StatisticalContributions.Of(goalOrMeasure, Result?.Records ?? [], Goals);

    /// <summary>The <paramref name="k"/> evaluated trials with the smallest margin on <paramref name="goal"/>, tightest
    /// first (R-ya4-10).</summary>
    public IReadOnlyList<WorstTrial> WorstTrials(string goal, int k = 10)
        => [.. (Result?.Records ?? [])
            .Where(r => r.Evaluated)
            .Select(r => (r, s: r.Goals.FirstOrDefault(g => g.Name == goal)))
            .Where(x => x.s is not null && !double.IsNaN(x.s.Margin))
            .OrderBy(x => x.s!.Margin).ThenBy(x => x.r.Trial)
            .Take(k)
            .Select(x => new WorstTrial(x.r.Trial, x.s!.Margin, x.s.WorstValue, x.r.Values))];

    // ── The nominal's distribution calls ──────────────────────────────────────────────

    internal static IReadOnlyList<StatisticalCall> NominalCalls(PreparedCircuit circuit, Library lib, TestBench tb,
                                                               IReadOnlyList<(string Name, string Expression)> sets)
    {
        try
        {
            var tuned = TunableOverrides.Apply(tb, lib, new Dictionary<string, string>(),
                                               sets.Select(s => s.Name).ToHashSet(StringComparer.Ordinal));
            if (tuned.TestBench is not { } bench || tuned.Library is not { } l) return [];
            foreach (var (name, expr) in sets) HbCircuitRun.ApplySet(bench, name, expr);
            using var nl = new Elaborator(l) { BaseDirectory = circuit.BaseDirectory }.Elaborate(bench);
            return [.. nl.StatisticalCalls];
        }
        catch (Exception)
        {
            return [];   // the nominal evaluation reports an elaboration failure in its own words
        }
    }
}
