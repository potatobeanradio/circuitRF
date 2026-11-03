using System.Diagnostics;
using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Optimization;
using CircuitRF.Diagnostics;
using CircuitRF.Engine.Statistics;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Design.Statistics;

/// <summary>
/// A corner analysis (brief-yield-6, docs/design/yield.md §10): the nominal design evaluated once per enabled corner,
/// or — with <see cref="CornerOptions.MonteCarlo"/> — a Monte Carlo or yield run at each (R-ya6-3). One implementation
/// for the GUI, the CLI and MCP, like <see cref="StatisticalRun"/>, whose evaluator it uses.
///
/// <para><b>A corner is a value map</b> (yield overview D10). Its kit axis selections were resolved at extraction into
/// the values its <c>.cnl</c> line binds — the schematic's other axes as the schematic has them — so here a corner is
/// its values and its <c>temp</c> (the elaborator's ambient global), applied as tuned values are
/// (<see cref="TunableOverrides.Apply"/>): <c>--set</c> and a corner naming the same variable is a refusal naming
/// both. A statistical corner replays its trial's z-vector against the current nominal
/// (<see cref="StatisticalCorner"/>). Every corner of a run is one point of one batch, so they evaluate in
/// parallel through the optimizer's own evaluator.</para>
/// </summary>
public sealed class CornerRun
{
    /// <summary>The corner the design itself is — always first on the <c>corner</c> axis.</summary>
    public const string NominalName = "nominal";

    private readonly PreparedCircuit _circuit;
    private readonly CornerOptions _options;
    private readonly TuningSetup _setup = new();
    private readonly List<CornerDefinition> _corners = [];
    private readonly List<Diagnostic> _notes = [];
    private readonly OptimizationRun? _eval;

    private CornerRun(PreparedCircuit circuit, CornerOptions options)
    {
        _circuit = circuit;
        _options = options;
        if (circuit.Tb is not { } tb) { Refusal = OptimizationDiagnostics.EvaluationFailed(circuit.ReadError ?? ""); return; }
        _setup = options.Setup ?? tb.Tuning ?? new TuningSetup();

        var enabled = _setup.Corners.Where(c => c.Enabled).ToList();
        if (options.Corners is { } names)
        {
            foreach (var name in names)
            {
                var c = enabled.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (c is null)
                {
                    Refusal = StatisticsDiagnostics.CornerUnknown(name, enabled.Count == 0 ? "none" : string.Join(", ", enabled.Select(e => e.Name)));
                    return;
                }
                if (!_corners.Contains(c)) _corners.Add(c);
            }
        }
        else _corners.AddRange(enabled);
        if (_corners.Count == 0) { Refusal = StatisticsDiagnostics.CornerNone(); return; }

        var setNames = options.Sets.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var c in _corners)
            foreach (var key in BindingsOf(c).Keys)
                if (setNames.Contains(key)) { Refusal = StatisticsDiagnostics.CornerSetConflict(c.Name, key); return; }

        if (!options.MonteCarlo)
        {
            _eval = OptimizationRun.ForEvaluation(circuit,
                new OptimizationOptions { Setup = options.Setup, Sets = options.Sets, Cancellation = options.Cancellation },
                options.Goals);
            if (_eval.Refusal is { } refused) { Refusal = refused; return; }
            _notes.AddRange(_eval.Notes);
        }
    }

    /// <summary>Prepares a corner run of <paramref name="circuit"/>'s setup; a run that cannot start carries its
    /// <see cref="Refusal"/>.</summary>
    public static CornerRun Create(PreparedCircuit circuit, CornerOptions? options = null)
        => new(circuit, options ?? new CornerOptions());

    /// <summary><c>&lt;design&gt;.corners.npy</c> beside the schematic or netlist (R-ya6-2) — Simulate's <c>run.npy</c>
    /// and the Monte Carlo file are untouched.</summary>
    public static string ResultPathFor(string sourcePath)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? ".",
                        Path.GetFileNameWithoutExtension(sourcePath) + ".corners.npy");

    /// <summary>A corner's bindings as the value map it evaluates with: its values, then its <c>temp</c> as the
    /// ambient global.</summary>
    public static IReadOnlyDictionary<string, string> BindingsOf(CornerDefinition corner)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in corner.Values) map[k] = v;
        if (corner.Temp is { } t) map[Temperature.AmbientGlobalName] = t;
        // A library part's corner (brief vendorlib-13 R-vl13-2e) travels as its own key; TunableOverrides applies it.
        foreach (var (part, c) in corner.PartCorners ?? []) map[CellCorner.PartPrefix + part] = c;
        return map;
    }

    public Diagnostic? Refusal { get; }

    /// <summary>The corners the run covers, in setup order (the nominal is not among them; it is always run).</summary>
    public IReadOnlyList<CornerDefinition> Corners => _corners;

    /// <summary>The goals a corner is scored against (not a Monte Carlo run: each of its runs holds its own).</summary>
    public IReadOnlyList<OptimizationGoal> Goals => _eval?.Goals ?? [];

    public IReadOnlyList<Diagnostic> Notes => _notes;

    /// <summary>Simulations actually run.</summary>
    public long Evaluations => _eval?.Evaluations ?? 0;

    public CornerResult? Result { get; private set; }

    /// <summary>Runs to the end on the calling thread.</summary>
    public CornerResult Run()
    {
        if (Refusal is { } refused) return Result = CornerResult.Refused(refused, _notes);
        try
        {
            return Result = _options.MonteCarlo ? RunMonteCarlo() : RunCorners();
        }
        catch (OperationCanceledException)
        {
            return Result = new CornerResult { Outcome = StatisticalOutcome.Cancelled, Notes = [.. _notes] };
        }
    }

    // ── One evaluation per corner (R-ya6-1, R-ya6-2) ─────────────────────────────

    private CornerResult RunCorners()
    {
        var ct = _options.Cancellation;
        var sw = Stopwatch.StartNew();
        var rows = new CornerEvaluation?[_corners.Count + 1];
        var points = new List<ValuePoint>();
        var slots = new List<(int Row, CornerDefinition? Def, IReadOnlyList<Diagnostic> Notes)>();

        points.Add(new ValuePoint(new Dictionary<string, string>(), null, NominalName));
        slots.Add((0, null, []));
        for (int i = 0; i < _corners.Count; i++)
        {
            var c = _corners[i];
            if (!c.IsStatistical)
            {
                points.Add(new ValuePoint(BindingsOf(c), null, "corner " + c.Name));
                slots.Add((i + 1, c, []));
                continue;
            }
            var replay = StatisticalCorner.Replay(_circuit, _setup, c, _options.Recorded, _options.Sets);
            if (replay.Refusal is { } why)
            {
                rows[i + 1] = new CornerEvaluation(c.Name, c, PointStatus.DidNotEvaluate, why, [], replay.Values, null, replay.Notes);
                continue;
            }
            points.Add(new ValuePoint(replay.Values, replay.Draws, "corner " + c.Name));
            slots.Add((i + 1, c, replay.Notes));
        }

        var evaluated = _eval!.EvaluateValues(points, keepData: true, ct);
        for (int k = 0; k < slots.Count; k++)
        {
            var (row, def, notes) = slots[k];
            var p = evaluated[k];
            rows[row] = new CornerEvaluation(def?.Name ?? NominalName, def, p.Status, p.Reason, p.Goals, p.Values, p.Data, notes);
        }
        _options.Progress?.Report(new CornerProgress(rows.Length, rows.Length, null, sw.Elapsed));

        var list = rows.Select(r => r!).ToList();
        bool countFails = (_setup.Statistics?.NonConverged ?? NonConvergedPolicy.Fail) == NonConvergedPolicy.Fail;

        StatisticalOutcome outcome;
        Diagnostic? refusal = null;
        if (list.All(r => !r.Evaluated))
        {
            outcome = StatisticalOutcome.NoneEvaluated;
            refusal = StatisticsDiagnostics.CornerNominalFailed(list[0].Reason?.Render() ?? "no reason given");
        }
        else
            outcome = list.Any(r => r.Evaluated ? !r.Pass : countFails && Goals.Count > 0)
                ? StatisticalOutcome.BelowTarget : StatisticalOutcome.Finished;

        var data = CornerDataSet.Build(list, Goals, countFails);
        // The run's own notes first, then what this run noticed — never added to Notes, which a caller has read.
        var notesOut = new List<Diagnostic>(_notes);
        foreach (var r in list) notesOut.AddRange(r.Notes);
        string? written = Write(data, notesOut);
        return new CornerResult
        {
            Outcome = outcome, Refusal = refusal, Notes = notesOut, Corners = list, Goals = Goals,
            Worst = WorstPerGoal(list, Goals), Data = data, WrittenPath = written, Evaluations = Evaluations,
        };
    }

    private static IReadOnlyList<CornerWorst> WorstPerGoal(IReadOnlyList<CornerEvaluation> rows, IReadOnlyList<OptimizationGoal> goals)
        => [.. goals.Select(g =>
        {
            var best = rows.Where(r => r.Evaluated)
                .Select(r => (r.Name, Score: r.Goals.FirstOrDefault(s => s.Name == g.Name)))
                .Where(x => x.Score is not null && !double.IsNaN(x.Score.Margin))
                .OrderBy(x => x.Score!.Margin).FirstOrDefault();
            return new CornerWorst(g.Name, best.Name, best.Score?.Margin ?? double.NaN, best.Score?.Met ?? false);
        })];

    // ── A Monte Carlo at each corner (R-ya6-3) ───────────────────────────────────

    private CornerResult RunMonteCarlo()
    {
        var ct = _options.Cancellation;
        var runs = new List<CornerYield>();
        var notesOut = new List<Diagnostic>(_notes);
        var all = new List<(string Name, CornerDefinition? Def)> { (NominalName, null) };
        all.AddRange(_corners.Select(c => (c.Name, (CornerDefinition?)c)));
        var sw = Stopwatch.StartNew();
        long evaluations = 0;

        foreach (var (name, def) in all)
        {
            ct.ThrowIfCancellationRequested();
            if (def is { IsStatistical: true })
            {
                notesOut.Add(StatisticsDiagnostics.CornerStatisticalNoMonteCarlo(def.Name));
                continue;
            }

            // At a corner the kit's process draws are off: the corner IS the process answer (R-ya6-3). The nominal row
            // is the ordinary run, process included.
            var setup = _setup.Clone();
            if (def is not null)
            {
                setup.Statistics = (setup.Statistics ?? new StatisticsSettings()).Clone();
                setup.Statistics.Process = false;
            }
            var run = StatisticalRun.Create(_circuit, new StatisticalOptions
            {
                Mode = _options.Mode, Setup = setup, Sets = _options.Sets, Cancellation = ct, BatchSize = _options.BatchSize,
                Bindings = def is null ? null : BindingsOf(def),
            });
            var result = run.Run();
            if (result.Outcome == StatisticalOutcome.Cancelled) throw new OperationCanceledException(ct);
            evaluations += run.Evaluations;
            runs.Add(new CornerYield(name, def, run, result));
            _options.Progress?.Report(new CornerProgress(runs.Count, all.Count, name, sw.Elapsed));
        }

        StatisticalOutcome outcome;
        Diagnostic? refusal = null;
        var finished = runs.Where(r => r.Result.Outcome is StatisticalOutcome.Finished or StatisticalOutcome.BelowTarget).ToList();
        if (finished.Count == 0)
        {
            outcome = runs.All(r => r.Result.Outcome == StatisticalOutcome.Refused) ? StatisticalOutcome.Refused : StatisticalOutcome.NoneEvaluated;
            refusal = runs.Select(r => r.Result.Refusal).FirstOrDefault(r => r is not null);
        }
        else outcome = finished.Any(r => r.Result.Outcome == StatisticalOutcome.BelowTarget) ? StatisticalOutcome.BelowTarget : StatisticalOutcome.Finished;

        var data = CornerDataSet.Stack([.. runs.Select(r => r.Name)], [.. runs.Select(r => r.Result.Data)]);
        string? written = data is null ? null : Write(data, notesOut);
        var worst = finished.Where(r => !double.IsNaN(r.Result.Yield.Yield)).OrderBy(r => r.Result.Yield.Yield).FirstOrDefault();
        return new CornerResult
        {
            Outcome = outcome, Refusal = refusal, Notes = notesOut, Yields = runs, WorstYield = worst?.Name,
            Data = data, WrittenPath = written, Evaluations = evaluations,
        };
    }

    private string? Write(DataSet data, List<Diagnostic> notes)
    {
        if (_options.ResultPath is not { } path) return null;
        try
        {
            if (File.Exists(path)) File.Delete(path);
            DataSetExporter.Export(data, path, ExportFormat.Npy);
            return Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            notes.Add(StatisticsDiagnostics.RunWriteFailed(path, ex.Message));
            return null;
        }
    }

    /// <summary>A corner's <c>temp</c> as a number of °C, or null when it sets none.</summary>
    public static double? TempOf(CornerDefinition? corner)
        => corner?.Temp is { } t && double.TryParse(t.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
}

/// <summary>What a caller may change about a corner run.</summary>
public sealed record CornerOptions
{
    /// <summary>The setup to run; null runs the one the circuit's netlist carries.</summary>
    public TuningSetup? Setup { get; init; }

    /// <summary><c>--set name=expr</c> overrides; a corner naming one of them is a refusal.</summary>
    public IReadOnlyList<(string Name, string Expression)> Sets { get; init; } = [];

    /// <summary>The corners to run, by name; null for every enabled corner.</summary>
    public IReadOnlyList<string>? Corners { get; init; }

    /// <summary>The goals a corner is scored against — the yield specs by default.</summary>
    public GoalUse Goals { get; init; } = GoalUse.Yield;

    /// <summary>A Monte Carlo or yield run at each corner (R-ya6-3) instead of one evaluation.</summary>
    public bool MonteCarlo { get; init; }

    /// <summary>With <see cref="MonteCarlo"/>, what each run is.</summary>
    public StatisticalMode Mode { get; init; } = StatisticalMode.Yield;

    /// <summary>The result of the run statistical corners came from (<c>&lt;design&gt;.yield.npy</c>), when at hand:
    /// its recorded z-vectors are replayed as they stand (R-ya6-4).</summary>
    public DataSet? Recorded { get; init; }

    public IProgress<CornerProgress>? Progress { get; init; }

    /// <summary>Cancelling abandons the run and writes nothing (exit 130).</summary>
    public CancellationToken Cancellation { get; init; }

    /// <summary>Trials per batch in a Monte Carlo at each corner; null is the evaluator's parallelism.</summary>
    public int? BatchSize { get; init; }

    /// <summary>Where the result is written (<see cref="CornerRun.ResultPathFor"/>, or for a Monte Carlo at each corner
    /// <see cref="StatisticalRun.ResultPathFor"/>); null writes nothing.</summary>
    public string? ResultPath { get; init; }
}

/// <summary>Corners done of the total; <paramref name="Corner"/> the one just finished (a Monte Carlo at each).</summary>
public sealed record CornerProgress(int Done, int Total, string? Corner, TimeSpan Elapsed);

/// <summary>One corner evaluated (R-ya6-2).</summary>
/// <param name="Definition">The corner line; null for the nominal.</param>
/// <param name="Values">The value map it evaluated with.</param>
/// <param name="Notes">What evaluating it noticed — a statistical corner's streams that no longer exist.</param>
public sealed record CornerEvaluation(
    string                              Name,
    CornerDefinition?                   Definition,
    PointStatus                         Status,
    Diagnostic?                         Reason,
    IReadOnlyList<GoalScore>            Goals,
    IReadOnlyDictionary<string, string> Values,
    DataSet?                            Data,
    IReadOnlyList<Diagnostic>           Notes)
{
    public bool Evaluated => Status == PointStatus.Evaluated;

    /// <summary>It evaluated and every goal is met there.</summary>
    public bool Pass => Evaluated && Goals.All(g => g.Met);
}

/// <summary>A goal's worst corner: where its margin is smallest. <see cref="Corner"/> is null when no corner scored it.</summary>
public sealed record CornerWorst(string Goal, string? Corner, double Margin, bool Met);

/// <summary>One corner's Monte Carlo or yield run (R-ya6-3).</summary>
public sealed record CornerYield(string Name, CornerDefinition? Definition, StatisticalRun Run, StatisticalResult Result);

/// <summary>What a corner run produced.</summary>
public sealed class CornerResult
{
    public required StatisticalOutcome Outcome { get; init; }

    /// <summary>Yield overview D13: 0 every goal met at every corner (or every yield at its target) · 3 a goal failed at a
    /// corner (a yield below its target) · 1 refused · 2 nothing evaluated · 130 cancelled.</summary>
    public int ExitCode => Outcome switch
    {
        StatisticalOutcome.Finished      => 0,
        StatisticalOutcome.BelowTarget   => 3,
        StatisticalOutcome.Refused       => 1,
        StatisticalOutcome.NoneEvaluated => 2,
        _                                => 130,
    };

    public Diagnostic? Refusal { get; init; }
    public IReadOnlyList<Diagnostic> Notes { get; init; } = [];

    /// <summary>One evaluation per corner, the nominal first (not a Monte Carlo at each).</summary>
    public IReadOnlyList<CornerEvaluation> Corners { get; init; } = [];

    /// <summary>The goals scored (not a Monte Carlo at each).</summary>
    public IReadOnlyList<OptimizationGoal> Goals { get; init; } = [];

    /// <summary>Per goal, its worst corner.</summary>
    public IReadOnlyList<CornerWorst> Worst { get; init; } = [];

    /// <summary>A Monte Carlo at each corner: one run per corner, the nominal first.</summary>
    public IReadOnlyList<CornerYield> Yields { get; init; } = [];

    /// <summary>A Monte Carlo at each corner: the corner with the lowest yield.</summary>
    public string? WorstYield { get; init; }

    /// <summary>One <c>DataSet</c> with an outer <c>corner</c> axis (yield overview D9); null when refused or cancelled.</summary>
    public DataSet? Data { get; init; }

    public string? WrittenPath { get; init; }

    public long Evaluations { get; init; }

    internal static CornerResult Refused(Diagnostic why, IReadOnlyList<Diagnostic> notes)
        => new() { Outcome = StatisticalOutcome.Refused, Refusal = why, Notes = notes };
}
