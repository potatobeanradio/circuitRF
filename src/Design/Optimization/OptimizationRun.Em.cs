// Framework-free. No Avalonia, no SkiaSharp.
//
// brief-sdl-7 — optimising copper geometry by EM, in the designer's own schematic. Three modes on the optimize line
// (D1): em=off runs every evaluation on circuit models; em=direct makes every evaluation an EM solve (cached by
// geometry, SDL-6 D3) with an algorithm that works in few evaluations (D3); em=correct (the default when EM is in play)
// searches on the circuit model — the same schematic, its analyses run by circuit models — and corrects it with a few EM
// solves, in goal space (D2). A budget caps the uncached solves (D4); a length is never stepped by less than two
// database units (D5); a variable that does not reach the copper is refused (D6); connectivity is held fixed and every
// candidate is checked against it before anything is solved (D9); and each variable's range is scanned first (D10).
//
// THE BEST POINT IS ALWAYS EM-VERIFIED. Under em=correct the circuit model only proposes; what the run returns, and what
// Push / Apply write, is the best point an EM solve has scored.

using System.Diagnostics;
using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Layout.Em.ParametricEm;
using CircuitRF.Design.Layout.SchematicSync;
using CircuitRF.Diagnostics;
using CircuitRF.Engine;
using RfCore.Data;

namespace CircuitRF.Design.Optimization;

/// <summary>One EM evaluation an optimization made (brief-sdl-7 R-sdl7-4): where, which geometry, and how the goals scored
/// on EM there. <paramref name="Cached"/>: the geometry was already in the EM cache, so it cost no solve.</summary>
public sealed record EmSolveRow(
    IReadOnlyDictionary<string, string> Values, string? Key, bool Cached, bool Failed, double Cost,
    IReadOnlyList<GoalReport> Goals)
{
    public bool Met => !Failed && Goals.All(g => g.Met);
}

/// <summary>D10 — the interval around a variable's start value, in its own unit, over which the layout keeps its
/// connectivity with every other variable at its start value.</summary>
public sealed record ConnectedRange(string Key, string Unit, double Lo, double Hi, double Min, double Max)
{
    /// <summary>Whether connectivity holds across the whole range.</summary>
    public bool Whole => Lo <= Min && Hi >= Max;

    /// <summary><c>L90: connected from 3.95 mm to 6.20 mm (range 3.00-8.00 mm).</c></summary>
    public string Sentence => Whole
        ? $"{Key}: connected across its whole range ({Text(Min)} to {Text(Max)})."
        : $"{Key}: connected from {Text(Lo)} to {Text(Hi)} (range {Text(Min)} to {Text(Max)}).";

    private string Text(double v) => TunableValue.Format(v, Unit);
}

/// <summary>How an em=correct or em=direct run stopped short of its own algorithm's finish.</summary>
public enum EmStop
{
    None,
    GoalsMet,
    SameGeometry,
    StepBelowDbu,
    Budget,
    NoBetterPoint,
}

/// <summary>What an inner (circuit model + offset) run of em=correct is told: its box, start, offsets and the
/// connectivity check every candidate passes.</summary>
internal sealed record EmInner(double[] Lo, double[] Hi, double[] Start, Value?[]? Offsets,
                               Func<DecodedPoint, string?>? Feasibility, double MinStep);

/// <summary>
/// Where one set of values puts every EM evaluation, before anything is solved: the run planned, every EM evaluation
/// counted through the provider (so it builds the geometry, keys it and measures its connectivity), nothing solved.
/// Answers are kept by value text.
/// </summary>
internal sealed class EmGeometryProbe(PreparedCircuit circuit, IReadOnlyList<(string Name, string Expression)> sets,
                                      IReadOnlyList<string>? analyses)
{
    public sealed record Answer(IReadOnlyList<EmEvaluationRecord> Records, string? Refusal, EmCellTarget? PathA)
    {
        /// <summary>brief-sdl-10 D5 — the connectivity of the PARENT's layout at these values, with every placed EM component
        /// at its own geometry; null when the schematic is not laid out, or is itself the EM cell (Path A).</summary>
        public ConnectivitySignature? Parent { get; init; }

        /// <summary>The geometry keys, in record order.</summary>
        public IEnumerable<string> Keys => Records.Select(r => r.Key).OfType<string>();

        /// <summary>The keys the cache does not hold.</summary>
        public IEnumerable<string> Uncached => Records.Where(r => r.Key is not null && !r.Cached).Select(r => r.Key!).Distinct();

        public string KeyText => string.Join("+", Keys.Distinct());
    }

    private readonly Dictionary<string, Answer> _seen = new(StringComparer.Ordinal);

    /// <summary>
    /// brief-sdl-10 D5 — the parent schematic laid out at <paramref name="values"/>: its saved layout re-planned in memory
    /// with the values written where they are defined (EmApply's rule), so each placed EM component takes its own geometry
    /// (a variant, D3) and the parent's lines follow its pins (D4). The planner's own report is the signature. Null when the
    /// run's schematic has no layout made from it.
    /// </summary>
    private ConnectivitySignature? ParentSignature(IReadOnlyDictionary<string, string> values)
    {
        if (circuit.SourcePath is not { } csch || !csch.EndsWith(".csch", StringComparison.OrdinalIgnoreCase)) return null;
        if (EmCapability.CellDirOfSchematic(csch) is not { } cellDir
            || CircuitRF.Design.Workspace.WorkspaceRootFinder.WorkspaceDirOf(cellDir) is not { } ws) return null;
        var primary = CircuitRF.Design.Cells.CellFolder.ResolvePrimary(cellDir, CircuitRF.Design.Cells.ViewType.Layout);
        if (primary.State is not (CircuitRF.Design.Cells.PrimaryState.SoleFile or CircuitRF.Design.Cells.PrimaryState.NamedPresent)
            || primary.ResolvedName is null) return null;
        string clay = Path.Combine(CircuitRF.Design.Cells.CellFolder.SubFolderPath(cellDir, CircuitRF.Design.Cells.ViewType.Layout),
                                   primary.ResolvedName);
        try
        {
            var layout = CircuitRF.Design.Layout.LayoutPersistence.LoadFromFile(clay);
            if (layout.GeneratedPlacements.Count == 0) return null;
            var model = CircuitRF.Design.Schematic.SchematicPersistence.LoadFromFile(csch).model;
            model.SchematicDirectory = Path.GetDirectoryName(csch);
            EmApply.WriteValues(model, values, out _);
            var (tech, _) = CircuitRF.Design.Layout.TechnologyResolver.ResolveForDocument(
                layout.TechRef, clay, Path.Combine(ws, ".cws"), new CircuitRF.Design.Layout.TechnologyCache());
            var plan = SchematicLayoutPlanner.Plan(model, layout, model.SchematicDirectory!, ws, Path.GetDirectoryName(clay)!,
                                                   tech.Tech, tech.ResolvedPath, CircuitRF.Design.Schematic.DiskCellResolver.Instance,
                                                   targetLayoutPath: clay, connectParts: true);
            return ConnectivitySignature.Of(plan.Lines);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                       or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
    private readonly Lock _gate = new();

    public long Probes { get; private set; }

    public Answer At(IReadOnlyDictionary<string, string> values)
    {
        string text = OptimizationRun.CacheKeyOf(values);
        lock (_gate) if (_seen.TryGetValue(text, out var hit)) return hit;
        var answer = Probe(values);
        lock (_gate) { _seen[text] = answer; Probes++; }
        return answer;
    }

    /// <summary>The answer at <paramref name="values"/> afresh — the cache's state may have changed since it was asked.</summary>
    public Answer Fresh(IReadOnlyDictionary<string, string> values)
    {
        var answer = Probe(values);
        lock (_gate) _seen[OptimizationRun.CacheKeyOf(values)] = answer;
        return answer;
    }

    private Answer Probe(IReadOnlyDictionary<string, string> values)
    {
        var plan = CircuitEvaluation.Plan(circuit, new CircuitEvaluationRequest { Tunables = values, Sets = sets, Analyses = analyses });
        try
        {
            if (plan.Status != RunStatus.Success) return new Answer([], plan.StatusMessage, null);
            var count = CircuitEvaluation.CountEmSolves(plan, estimate: false);
            var pathA = plan.Analyses.Select(a => a.Em?.PathA).FirstOrDefault(a => a is not null);
            return new Answer(count.Records, count.Refusals.FirstOrDefault(), pathA)
            {
                Parent = pathA is null && count.Records.Count > 0 ? ParentSignature(values) : null,
            };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new Answer([], e.Message, null);
        }
        finally { plan.Nl?.Dispose(); }
    }
}

public sealed partial class OptimizationRun
{
    /// <summary>The default solve budget of em=correct (D4).</summary>
    public const int DefaultCorrectBudget = 12;

    /// <summary>The default solve budget of em=direct (D4).</summary>
    public const int DefaultDirectBudget = 30;

    /// <summary>brief-sdl-8 D8 — em=correct's default budget when the EM view is a 3D setup: a 3D solve takes minutes to hours.</summary>
    public const int DefaultCorrectBudget3D = 6;

    /// <summary>brief-sdl-8 D8 — em=direct's default budget when the EM view is a 3D setup.</summary>
    public const int DefaultDirectBudget3D = 15;

    /// <summary>D2 — the first step around each accepted point, as a fraction of each variable's range.</summary>
    internal const double FirstCorrectionStep = 0.1;

    /// <summary>D10 — points scanned across each variable's range.</summary>
    internal const int ScanPoints = 64;

    // The evaluation model every Simulate runs with (CircuitEvaluationRequest.Em): null as the design says, false
    // circuit models only.
    private bool? _evalEm;
    private Value?[]? _offsets;
    private EmInner? _inner;
    private OptimizationRun? _parent;
    private double[]? _boxLo, _boxHi;

    private EmGeometryProbe? _probe;
    private int _emBudget;
    private readonly HashSet<string> _solvedKeys = new(StringComparer.Ordinal);
    private bool _budgetHit;
    private readonly Dictionary<string, string?> _feasibility = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _infeasibleWhy = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ConnectivitySignature> _startSignatures = new(StringComparer.Ordinal);
    private ConnectivitySignature? _startParent;
    private readonly List<EmSolveRow> _emRows = [];
    private List<ConnectedRange> _ranges = [];
    private double[] _dbuStep = [];
    private EmStop _emStop;
    private string? _bestEmKey;

    /// <summary>brief-sdl-7 D1 — how this run treats analyses simulated by EM, as resolved from the setup and the design.</summary>
    public OptimizerEmMode EmMode { get; private set; } = OptimizerEmMode.Off;

    /// <summary>The solve budget in force; 0 when no EM mode runs.</summary>
    public int EmBudget => EmMode == OptimizerEmMode.Off ? 0 : _emBudget;

    /// <summary>The run whose Stop and Pause this one obeys: the em=correct run an inner run belongs to, else itself.</summary>
    private OptimizationRun Ctl => _parent ?? this;

    /// <summary>The start point the algorithm is handed, in this run's box.</summary>
    private double[] StartPoint => _inner?.Start ?? Variables!.Start;

    /// <summary>A point of this run's box, in the variables' own box.</summary>
    internal double[] Box(double[] u)
    {
        if (_boxLo is null) return u;
        var x = new double[u.Length];
        for (int i = 0; i < u.Length; i++) x[i] = _boxLo[i] + Math.Clamp(u[i], 0, 1) * (_boxHi![i] - _boxLo[i]);
        return x;
    }

    private DecodedPoint DecodeAt(double[] u, bool snapPreferred, bool snapParts) => Variables!.Decode(Box(u), snapPreferred, snapParts);

    // ── Mode (constructor) ───────────────────────────────────────────────────

    /// <summary>
    /// Resolves the EM mode (D1) at construction — refusals that need no evaluation only (D3, corners). Everything that
    /// plans a geometry waits for <see cref="Run"/>, since constructing a run is also what <c>check</c> does, and
    /// <c>check</c> writes nothing.
    /// </summary>
    private Diagnostic? InitEm(TestBench tb, Library lib, OptimizationOptions options)
    {
        if (options.Inner is { } inner)
        {
            _inner  = inner;
            _parent = options.Parent;
            _evalEm = false;
            _offsets = inner.Offsets;
            _boxLo = inner.Lo;
            _boxHi = inner.Hi;
            return null;
        }

        bool involved = EmInvolved(tb, lib);
        var asked = _settings.Em;
        var mode = asked ?? (involved ? OptimizerEmMode.Correct : OptimizerEmMode.Off);

        // brief-sdl-10 D2 — a part with no circuit model leaves em=direct the only mode.
        if (EmModelSwitch.WithoutCircuitModel(tb, lib) is { Count: > 0 } emOnly)
        {
            string parts = string.Join(", ", emOnly);
            if (asked is OptimizerEmMode.Correct or OptimizerEmMode.Off)
                return OptimizationDiagnostics.EmModeNeedsCircuitModel(EmToken(asked.Value), parts);
            if (mode == OptimizerEmMode.Correct)
            {
                mode = OptimizerEmMode.Direct;
                _notes.Add(OptimizationDiagnostics.EmDirectForNoCircuitModel(parts));
            }
            involved = true;
        }
        if (mode != OptimizerEmMode.Off && !involved)
        {
            _notes.Add(OptimizationDiagnostics.EmNotInvolved(EmToken(mode)));
            mode = OptimizerEmMode.Off;
        }
        EmMode = mode;
        _evalEm = mode == OptimizerEmMode.Off && involved ? false : null;
        if (mode == OptimizerEmMode.Off) return null;

        if (_settings.CornerNames is not { Count: 0 })
            return OptimizationDiagnostics.EmWithCorners(EmToken(mode));
        _emBudget = _settings.EmBudget ?? (mode == OptimizerEmMode.Correct ? DefaultCorrectBudget : DefaultDirectBudget);
        if (_emBudget < 1) return TuningDiagnostics.EmBudgetNotPositive(_emBudget.ToString(CultureInfo.InvariantCulture));

        if (mode == OptimizerEmMode.Direct)
        {
            if (AlgorithmId == OptimizerAlgorithms.Auto) AlgorithmId = OptimizerAlgorithms.EmDirectAuto;
            else if (!OptimizerAlgorithms.ForEmDirect.Contains(AlgorithmId))
                return TuningDiagnostics.EmDirectAlgorithm(AlgorithmId, string.Join(", ", OptimizerAlgorithms.ForEmDirect));
        }
        return null;
    }

    /// <summary>Whether a goal reads an analysis run by EM (model=em anywhere in its chain), or an instance is set to EM.</summary>
    private bool EmInvolved(TestBench tb, Library lib)
    {
        if (EmModelSwitch.HasInstancesSetToEm(tb, lib)) return true;
        foreach (var name in _goals.Select(g => g.Analysis).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            var one = tb.Analyses.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
            if (one is null || !one.Enabled || !AnalysisChain.IsChainRunnable(one, tb)) continue;
            if (EmModelSwitch.IsEm(AnalysisChain.PromoteToRunnableTop(one, tb), tb)) return true;
        }
        return false;
    }

    /// <summary>
    /// brief-sdl-7 D8 — what this evaluator would simulate by EM: the analyses it runs whose chain says model=em, and
    /// "parts set to EM" when an instance is. Empty when it runs by circuit models (<see cref="OptimizationOptions.EvaluateEm"/>
    /// false) or nothing here is EM. What a yield or sensitivity run refuses on, before its first trial.
    /// </summary>
    public IReadOnlyList<string> EmAnalyses()
    {
        if (_evalEm == false || _circuit.Tb is not { } tb || _circuit.Lib is not { } lib) return [];
        var names = new List<string>();
        var tops = _analyses is null ? AnalysisChain.RunnableTops(tb)
            : _analyses.Select(n => tb.Analyses.FirstOrDefault(a => string.Equals(a.Name, n, StringComparison.OrdinalIgnoreCase)))
                       .OfType<Analysis>().Where(a => a.Enabled && AnalysisChain.IsChainRunnable(a, tb))
                       .Select(a => AnalysisChain.PromoteToRunnableTop(a, tb));
        foreach (var top in tops)
            if (EmModelSwitch.IsEm(top, tb) && !names.Contains(top.Name)) names.Add(top.Name);
        if (EmModelSwitch.HasInstancesSetToEm(tb, lib)) names.Add("parts set to EM");
        return names;
    }

    internal static string EmToken(OptimizerEmMode mode) => AnalysisDirectiveSchemaTokens.EmMode(mode);

    // ── Preparing an EM run (Run) ────────────────────────────────────────────

    /// <summary>
    /// What an EM run checks before its first solve: the start's geometry can be built and agrees with the schematic
    /// (D9); every optimized value reaches the copper (D6); each variable's connected interval (D10); the two-DBU step
    /// floor (D5). Null when it may go on.
    /// </summary>
    private Diagnostic? PrepareEm(CancellationToken ct)
    {
        _probe = new EmGeometryProbe(_circuit, _options.Sets, _analyses);
        var vars = Variables!;
        var startValues = vars.Decode(vars.Start).Values;
        var start = _probe.Fresh(startValues);
        if (start.Refusal is { } why) return OptimizationDiagnostics.EmStartRefused(why);
        if (!start.Records.Any(r => r.Key is not null)) return OptimizationDiagnostics.EmStartRefused("no EM evaluation is made at the start values.");

        // D6 — through the schematic's variables only (Path A), and never into a part whose value the layout holds.
        var catalog = TunableCatalog.FromNetlist(_circuit.Tb!, _circuit.Lib!, parts: _circuit.PartTunables);
        foreach (var c in vars.Coordinates)
        {
            string name = c.ValueKey;
            if (start.PathA is { } a && catalog.FindValue(c.ValueKey) is { } t && t.Kind != TunableKind.Variable)
                return OptimizationDiagnostics.EmNotThroughVariable(c.Key, a.CellName);
            string leaf = name[(Math.Max(name.LastIndexOf('.'), name.LastIndexOf(':')) + 1)..];
            if (start.Records.SelectMany(r => r.Unreached).FirstOrDefault(u => u.StartsWith(leaf + " does not reach", StringComparison.Ordinal)) is { } unreached)
                return OptimizationDiagnostics.EmUnreached(c.Key, unreached);
        }

        // D9 — the start must agree with the schematic; its signature is then held. A gap the schematic's own lengths make
        // is found by planning them onto an empty layout, since the saved layout's re-plan reads every open joint as one
        // the user opened.
        foreach (var r in start.Records)
        {
            if (r.Connectivity is not { } sig) continue;
            if (sig.Disagreement is { } lines) return OptimizationDiagnostics.EmStartDisagrees(lines);
            _startSignatures[r.InstancePath] = sig;
        }
        // brief-sdl-10 D5 — and the parent's own layout, when it is laid out: held at both levels.
        if (start.Parent is { } parentSig) _startParent = parentSig;
        if (start.PathA is { } own && EmEvaluation.SchematicDisagreements(own, new Dictionary<string, Value>()) is { Count: > 0 } made)
            return OptimizationDiagnostics.EmStartDisagrees(string.Join(" ", made));
        if (_startSignatures.Count == 0) _notes.Add(OptimizationDiagnostics.EmConnectivityUnchecked());

        // brief-sdl-8 D8 — a 3D EM view's budgets, where the setup states none.
        if (_settings.EmBudget is null && start.Records.Any(r => r.ThreeD))
        {
            _emBudget = EmMode == OptimizerEmMode.Correct ? DefaultCorrectBudget3D : DefaultDirectBudget3D;
            _notes.Add(OptimizationDiagnostics.EmThreeDBudget(EmToken(EmMode), _emBudget));
        }

        // D5 — two database units, as a fraction of each length variable's range. brief-sdl-8 D7 — for a 3D EM view, the
        // larger of that and the smallest element the held mesh makes: a step under one element is meshing noise.
        _dbuStep = new double[vars.Coordinates.Count];
        if (start.PathA is { } target && DbuMetres(target) is { } dbu)
        {
            double stepM = 2 * dbu;
            if (start.Records.Select(r => r.SmallestElementM).OfType<double>().DefaultIfEmpty(0).Max() is var mesh && mesh > stepM)
            {
                stepM = mesh;
                _notes.Add(OptimizationDiagnostics.EmMeshStepFloor(mesh));
            }
            for (int i = 0; i < vars.Coordinates.Count; i++)
                _dbuStep[i] = LengthStepFraction(vars.Coordinates[i], stepM / 2, vars.Start[i]);
        }

        // D10 — each variable alone across its range; one that never changes the geometry is refused (D6).
        _ranges = [];
        string startKey = start.KeyText;
        for (int i = 0; i < vars.Coordinates.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var c = vars.Coordinates[i];
            if (c.VendorPart is not null || c.Part is not null) continue;
            var feasible = new bool[ScanPoints];
            bool moves = false;
            for (int j = 0; j < ScanPoints; j++)
            {
                var u = (double[])vars.Start.Clone();
                u[i] = j / (double)(ScanPoints - 1);
                var d = vars.Decode(u);
                var answer = _probe.At(d.Values);
                feasible[j] = !d.Infeasible && ConnectivityProblem(answer) is null;
                if (feasible[j] && answer.KeyText != startKey) moves = true;
            }
            if (!moves && start.PathA is not null) return OptimizationDiagnostics.EmNoEffect(c.Key);
            int at = (int)Math.Round(vars.Start[i] * (ScanPoints - 1));
            if (!feasible[at]) continue;
            int lo = at, hi = at;
            while (lo > 0 && feasible[lo - 1]) lo--;
            while (hi < ScanPoints - 1 && feasible[hi + 1]) hi++;
            double Value(int j) => c.Decode(j / (double)(ScanPoints - 1));
            var range = new ConnectedRange(c.Key, c.Unit, lo == 0 ? c.Min : Value(lo), hi == ScanPoints - 1 ? c.Max : Value(hi), c.Min, c.Max);
            _ranges.Add(range);
            _notes.Add(OptimizationDiagnostics.EmConnectedRange(range.Sentence));
        }
        return null;
    }

    /// <summary>D9 — why a candidate is infeasible: it cannot be built, or its connectivity is not the start's.</summary>
    private string? ConnectivityProblem(EmGeometryProbe.Answer answer)
    {
        if (answer.Refusal is { } refused) return refused;
        foreach (var r in answer.Records)
        {
            if (r.Refusal is { } why) return why;
            if (!_startSignatures.TryGetValue(r.InstancePath, out var held)) continue;
            if (r.Connectivity is not { } sig) return "its connectivity could not be measured";
            if (sig.DifferenceFrom(held) is { } diff) return diff;
        }
        // brief-sdl-10 D5 — a component intact in itself whose port leaves the parent's line is infeasible too.
        if (_startParent is { } parentHeld)
        {
            if (answer.Parent is not { } parentNow) return "the parent's layout could not be planned at these values";
            if (parentNow.DifferenceFrom(parentHeld) is { } diff) return "in the parent's layout, " + diff;
        }
        return null;
    }

    /// <summary>The feasibility check of one decoded point (D9), kept by its values. Null: feasible.</summary>
    private string? Feasible(DecodedPoint d)
    {
        lock (_feasibility)
            if (_feasibility.TryGetValue(d.CacheKey, out var known)) return known;
        string? why = ConnectivityProblem(_probe!.At(d.Values));
        string? said = why is null ? null : $"{Describe(d.Values)}: {why}";
        lock (_feasibility) _feasibility[d.CacheKey] = said;
        return said;
    }

    private static string Describe(IReadOnlyDictionary<string, string> values) =>
        string.Join(", ", values.Select(kv => $"{kv.Key} = {kv.Value}"));

    /// <summary>A database unit in metres, from the cell's saved layout; null when it cannot be read.</summary>
    private static double? DbuMetres(EmCellTarget target)
    {
        try
        {
            var view = CircuitRF.Design.Layout.LayoutPersistence.LoadFromFile(target.ClayPath);
            return view.DbuPerMicron > 0 ? 1e-6 / view.DbuPerMicron : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException
                                       or System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>D5 — two database units as a fraction of <paramref name="c"/>'s box, at <paramref name="u"/>; 0 for a
    /// value with no length unit.</summary>
    private static double LengthStepFraction(OptimizationCoordinate c, double dbuMetres, double u)
    {
        if (c.Unit.Length == 0) return 0;
        string unit = UnitNormalizer.ToEngineUnit(c.Unit);
        if (Units.BaseUnit(unit) != "metre" || Units.Scale(unit) is not { } scale || scale <= 0) return 0;
        double step = 2 * dbuMetres / scale;      // in the coordinate's own unit
        if (c.Scale == TuneScale.Log && c.Min > 0 && c.Max > c.Min)
        {
            double v = c.Decode(u);
            return step / (Math.Max(v, c.Min) * Math.Log(c.Max / c.Min));
        }
        return c.Max > c.Min ? step / (c.Max - c.Min) : 0;
    }

    /// <summary>D5 — the step options of the algorithm about to run, raised to two database units where the setup does
    /// not state them.</summary>
    private void FloorSteps(string algorithmId, double minStep)
    {
        if (minStep <= 0) return;
        string? option = algorithmId switch
        {
            "pattern" => "minpoll",
            "simplex" => "xtol",
            "trust_region" or "minimax" => "minradius",
            _ => null,
        };
        foreach (var name in new[] { option, OptimizerAlgorithms.Find(algorithmId)?.Option("fdstep") is null ? null : "fdstep" })
        {
            if (name is null || _settings.Options?.ContainsKey(name) == true) continue;
            double def = double.TryParse(OptimizerAlgorithms.Find(algorithmId)?.Option(name)?.Default, NumberStyles.Float,
                                         CultureInfo.InvariantCulture, out double v) ? v : 0;
            if (minStep > def)
                (_algOptions ??= new Dictionary<string, string>(StringComparer.Ordinal))[name] =
                    minStep.ToString("R", CultureInfo.InvariantCulture);
        }
    }

    // ── em=direct: the budget, in the batch ──────────────────────────────────

    /// <summary>
    /// D4 under em=direct — which of a batch's feasible points fit the budget: walked in order, each geometry not yet
    /// solved and not in the cache is a solve; the first that would exceed the budget, and every point after it, is left
    /// out and the run ends.
    /// </summary>
    private int BudgetPrefix(IReadOnlyList<DecodedPoint> decoded)
    {
        if (EmMode != OptimizerEmMode.Direct || _probe is null) return decoded.Count;
        for (int k = 0; k < decoded.Count; k++)
        {
            if (decoded[k].Infeasible) continue;
            var fresh = _probe.Fresh(decoded[k].Values);
            var add = fresh.Uncached.Where(key => !_solvedKeys.Contains(key)).ToList();
            if (_solvedKeys.Count + add.Count > _emBudget)
            {
                _budgetHit = true;
                _emStop = EmStop.Budget;
                return k;
            }
            foreach (var key in add) _solvedKeys.Add(key);
        }
        return decoded.Count;
    }

    /// <summary>em=direct — one row per point that was simulated (its EM geometry solved or read from the cache).</summary>
    private void RecordDirectRow(DecodedPoint d, PointOutcome o, bool cachedBefore)
    {
        var answer = _probe!.At(d.Values);
        _emRows.Add(new EmSolveRow(d.Values, answer.KeyText, cachedBefore, o.Failed, o.Failed ? double.NaN : o.Cost,
                                   o.Failed ? [] : Reports(o)));
    }

    private static IReadOnlyList<GoalReport> Reports(PointOutcome o) =>
        [.. o.Scores.Select(s => new GoalReport(s.Name, s.WorstViolation, s.WorstAt, s.Axis, s.WorstValue, s.Met, s.Margin))];

    // ── em=correct (D2) ──────────────────────────────────────────────────────

    /// <summary>
    /// The loop: optimise on the circuit model to p₀; solve EM at pₖ and record each goal's offset EM − circuit; optimise
    /// the circuit model plus that fixed offset within a step of pₖ; a worse EM result halves the step and retries from
    /// pₖ. It stops when EM meets every goal, when a new point is pₖ's geometry, when the step is below two database
    /// units, or at the budget.
    /// </summary>
    private OptimizationResult RunEmCorrect(CancellationToken ct)
    {
        var vars = Variables!;
        int n = vars.Coordinates.Count;
        var emEval = new OptimizationRun(_circuit, _options with { Progress = null }, GoalUse.Opt);
        emEval._evalEm = null;
        var circuitEval = new OptimizationRun(_circuit, _options with { Progress = null }, GoalUse.Opt);
        circuitEval._evalEm = false;
        if (emEval.Refusal is { } r1) return Refused(r1);

        var innerLogs = new List<EvaluationRecord>();
        long innerEvals = 0, innerFailures = 0, innerInfeasible = 0, innerHits = 0;
        int iterations = 0;

        // Step 1: the circuit model alone, from the design's start, over the whole box.
        var first = Inner(vars.Start, [.. Enumerable.Repeat(0.0, n)], [.. Enumerable.Repeat(1.0, n)], null, ct);
        if (first is null) return Finish(OptimizationOutcome.Cancelled, "cancelled", null);
        Absorb(first.Value.Run, first.Value.Result);
        if (first.Value.Result.Outcome == OptimizationOutcome.Refused) return Refused(first.Value.Result.Refusal!);
        if (first.Value.Result.BestPoint is not { } p0)
            return EmFinish("the circuit model found no point to start from", ct);

        double[] pk = p0;
        double step = FirstCorrectionStep;
        (double[] U, string Key, double Cost, Value?[] Offsets)? accepted = null;
        string reason = "";
        int guard = 4 * _emBudget + 16;   // every pass either solves, halves the step or stops; this only bounds a bug
        while (guard-- > 0)
        {
            if (Ctl._stop) { reason = "stopped"; break; }
            ct.ThrowIfCancellationRequested();

            var at = vars.Decode(pk);
            var answer = _probe!.Fresh(at.Values);
            string key = answer.KeyText;
            if (accepted is { } acc && key == acc.Key)
            {
                _emStop = EmStop.SameGeometry;
                reason = "the next point is the same geometry as the last one solved: nothing more can change";
                break;
            }
            var uncached = answer.Uncached.Where(k => !_solvedKeys.Contains(k)).ToList();
            if (_solvedKeys.Count + uncached.Count > _emBudget)
            {
                _emStop = EmStop.Budget;
                reason = $"its EM budget of {_emBudget} solve{(_emBudget == 1 ? "" : "s")} was spent";
                break;
            }

            var em = emEval.EvaluateValues([at.Values], keepData: true, ct)[0];
            foreach (var k in uncached) _solvedKeys.Add(k);
            iterations++;
            var row = new EmSolveRow(at.Values, key, uncached.Count == 0, em.Status != PointStatus.Evaluated,
                                     em.Status == PointStatus.Evaluated ? em.Cost : double.NaN,
                                     em.Status == PointStatus.Evaluated
                                         ? [.. em.Goals.Select(s => new GoalReport(s.Name, s.WorstViolation, s.WorstAt, s.Axis, s.WorstValue, s.Met, s.Margin))]
                                         : []);
            _emRows.Add(row);
            bool better = em.Status == PointStatus.Evaluated && (_bestEm is null || em.Cost < _bestEm.Value.Row.Cost);
            if (better) { _bestEm = (row, pk, em.Data); _bestEmKey = key; }
            EmProgress(iterations, row);

            if (em.Pass)
            {
                _emStop = EmStop.GoalsMet;
                reason = "every enabled goal is met on EM";
                break;
            }

            double[] from;
            Value?[] offsets;
            if (accepted is { } prev && (em.Status != PointStatus.Evaluated || em.Cost >= prev.Cost))
            {
                // Worse than the accepted point: retry from it with half the step.
                step /= 2;
                if (StepBelowDbu(step))
                {
                    _emStop = EmStop.StepBelowDbu;
                    reason = "the step fell below two database units of the layout";
                    break;
                }
                from = prev.U;
                offsets = prev.Offsets;
            }
            else
            {
                if (em.Status != PointStatus.Evaluated)
                {
                    reason = "the EM evaluation at the circuit model's optimum failed: " + (em.Reason?.Render() ?? "");
                    break;
                }
                var circuit = circuitEval.EvaluateValues([at.Values], keepData: false, ct)[0];
                offsets = Offsets(em.GoalValues, circuit.GoalValues);
                accepted = (pk, key, em.Cost, offsets);
                from = pk;
            }

            var lo = new double[n];
            var hi = new double[n];
            var start = new double[n];
            for (int i = 0; i < n; i++)
            {
                lo[i] = Math.Max(0, from[i] - step);
                hi[i] = Math.Min(1, from[i] + step);
                start[i] = hi[i] > lo[i] ? (from[i] - lo[i]) / (hi[i] - lo[i]) : 0;
            }
            var next = Inner(start, lo, hi, offsets, ct);
            if (next is null) return Finish(OptimizationOutcome.Cancelled, "cancelled", null);
            Absorb(next.Value.Run, next.Value.Result);
            if (next.Value.Result.BestPoint is not { } candidate)
            {
                _emStop = EmStop.NoBetterPoint;
                reason = "the corrected circuit model found no feasible point within the step";
                break;
            }
            pk = candidate;
        }
        return EmFinish(reason, ct);

        void Absorb(OptimizationRun run, OptimizationResult result)
        {
            innerLogs.AddRange(result.Log.Select(l => l with { Point = run.Box(l.Point) }));
            innerEvals += result.Evaluations;
            innerFailures += result.Failures;
            innerInfeasible += result.Infeasible;
            innerHits += result.CacheHits;
            _iterations += result.Iterations;
            foreach (var note in result.Notes)
                if (!_notes.Any(x => x.Render() == note.Render())) _notes.Add(note);
            foreach (var kv in run._infeasibleWhy) _infeasibleWhy.TryAdd(kv.Key, kv.Value);
            _innerLog = innerLogs;
            _innerCounts = (innerEvals, innerFailures, innerInfeasible, innerHits);
        }
    }

    private (EmSolveRow Row, double[] U, DataSet? Data)? _bestEm;
    private List<EvaluationRecord>? _innerLog;
    private (long Evals, long Failures, long Infeasible, long Hits) _innerCounts;

    /// <summary>One inner run: the circuit model plus <paramref name="offsets"/>, in the box [lo, hi], from
    /// <paramref name="start"/> (in that box), every candidate checked for connectivity. Null when cancelled.</summary>
    private (OptimizationRun Run, OptimizationResult Result)? Inner(double[] start, double[] lo, double[] hi, Value?[]? offsets,
                                                                   CancellationToken ct)
    {
        double minStep = 0;
        for (int i = 0; i < _dbuStep.Length; i++)
            if (hi[i] > lo[i]) minStep = Math.Max(minStep, _dbuStep[i] / (hi[i] - lo[i]));
        var inner = new EmInner(lo, hi, start, offsets, _startSignatures.Count == 0 ? null : Feasible, minStep);
        int solveNo = _emRows.Count + 1;
        var run = new OptimizationRun(_circuit, _options with
        {
            Inner  = inner,
            Parent = this,
            SnapAndPolish = false,
            Progress = _options.Progress is not { } progress ? null : p => progress(p with
            {
                Stage = $"circuit model{(offsets is null ? "" : " + EM correction")}, before EM solve {solveNo}",
                Iteration = _iterations + p.Iteration,
            }),
        });
        if (run.Refusal is { } refusal)
            return (run, new OptimizationResult { Outcome = OptimizationOutcome.Refused, Refusal = refusal, FinishReason = refusal.Render() });
        var result = run.Run();
        return result.Outcome == OptimizationOutcome.Cancelled ? null : (run, result);
    }

    /// <summary>Each goal's offset EM − circuit, sample by sample; null for a goal whose two values do not line up.</summary>
    private Value?[] Offsets(IReadOnlyList<Value?>? em, IReadOnlyList<Value?>? circuit)
    {
        var d = new Value?[_goals.Count];
        for (int g = 0; g < _goals.Count; g++)
        {
            if (em is null || circuit is null || g >= em.Count || g >= circuit.Count || em[g] is not { } e || circuit[g] is not { } c) continue;
            try { d[g] = Value.Sub(e, c); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _notes.Add(OptimizationDiagnostics.EmOffsetMismatch(_goals[g].Name));
            }
        }
        return d;
    }

    /// <summary>A goal's value with its EM offset added (D2) — applied when scoring, never in the goal's text.</summary>
    private Value? WithOffset(int goal, Value? value)
    {
        if (_offsets is null || goal >= _offsets.Length || _offsets[goal] is not { } d || value is not { } v) return value;
        try { return Value.Add(v, d); }
        catch (Exception e) when (e is not OperationCanceledException) { return value; }
    }

    private bool StepBelowDbu(double step)
    {
        var lengths = _dbuStep.Where(s => s > 0).ToList();
        return lengths.Count > 0 ? lengths.All(s => step < s) : step < 1e-4;
    }

    private void EmProgress(int solve, EmSolveRow row)
    {
        if (_options.Progress is not { } progress || _bestEm is not { } best) return;
        progress(new OptimizationProgress(
            _iterations, Evaluations + _innerCounts.Evals, _innerCounts.Failures, _innerCounts.Infeasible, _innerCounts.Hits,
            _sw.Elapsed, row.Cost, best.Row.Cost, best.Row.Values, best.Row.Goals, Variables!.Railed(best.U), best.Data,
            $"EM solve {solve} of at most {_emBudget}"));
    }

    private OptimizationResult EmFinish(string reason, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Finish(OptimizationOutcome.Cancelled, "cancelled", null);
        var vars = Variables!;
        if (_bestEm is not { } best)
            return new OptimizationResult
            {
                Outcome = OptimizationOutcome.NoConvergence, FinishReason = reason,
                Refusal = OptimizationDiagnostics.NoneConverged(_emRows.Count, reason), Algorithm = AlgorithmId,
                Notes = _notes, EmMode = EmMode, EmBudgetUsed = _solvedKeys.Count, EmSolves = _emRows, ConnectedRanges = _ranges,
                Evaluations = _innerCounts.Evals + _emRows.Count, Failures = _innerCounts.Failures,
                Infeasible = _innerCounts.Infeasible, CacheHits = _innerCounts.Hits, Log = _innerLog ?? [],
            };
        bool met = best.Row.Met;
        if (!met) _notes.Add(OptimizationDiagnostics.EmCorrectionShort(_emStop == EmStop.Budget));
        return new OptimizationResult
        {
            Outcome      = met ? OptimizationOutcome.GoalsMet : OptimizationOutcome.GoalsUnmet,
            FinishReason = reason,
            Algorithm    = AlgorithmId,
            Stages       = [OptimizationStages.EmCorrect],
            Notes        = _notes,
            BestValues   = best.Row.Values,
            BestPoint    = best.U,
            BestCost     = best.Row.Cost,
            Goals        = best.Row.Goals,
            Railed       = vars.Railed(best.U),
            BestData     = best.Data,
            Iterations   = _iterations,
            Evaluations  = _innerCounts.Evals + _emRows.Count,
            Failures     = _innerCounts.Failures,
            Infeasible   = _innerCounts.Infeasible,
            CacheHits    = _innerCounts.Hits,
            Log          = _innerLog ?? [],
            History      = History(_innerLog ?? [], []),
            EmMode       = EmMode,
            EmSolves     = _emRows,
            EmBudgetUsed = _solvedKeys.Count,
            StoppedByBudget = _emStop == EmStop.Budget,
            BestEmKey    = _bestEmKey,
            ConnectedRanges = _ranges,
        };
    }

    /// <summary>The opt group's EM rows (R-sdl7-4): one per EM evaluation, over axis <c>em_solve</c>, whose labels are the
    /// geometry keys.</summary>
    private void AddEmHistory(DataSet ds, string group)
    {
        if (_emRows.Count == 0) return;
        var axis = new Axis("em_solve", [.. Enumerable.Range(1, _emRows.Count).Select(i => (double)i)], "",
                            [.. _emRows.Select(r => r.Key ?? "")]);
        ds.AddToGroup(group, "em_cost", new DataCube([axis], [.. _emRows.Select(r => r.Cost)]));
        ds.AddToGroup(group, "em_cached", new DataCube([axis], [.. _emRows.Select(r => r.Cached ? 1.0 : 0.0)]));
        ds.AddToGroup(group, "em_met", new DataCube([axis], [.. _emRows.Select(r => r.Met ? 1.0 : 0.0)]));
        foreach (var key in Variables!.ValueKeys)
        {
            if (Variables.IsComplex(key)) continue;
            string unit = Variables.UnitOf(key);
            var q = _emRows.Select(r => r.Values.TryGetValue(key, out var text) && TunableValue.TryParse(text, out _, out _, out double si)
                                       ? si : double.NaN).ToArray();
            var cube = new DataCube([axis], q) { Unit = unit.Length == 0 ? "" : Units.BaseUnit(UnitNormalizer.ToEngineUnit(unit)) };
            ds.AddToGroup(group, "em_" + key, cube);
        }
        foreach (var g in _goals)
            ds.AddToGroup(group, "em_margin_" + g.Name,
                new DataCube([axis], [.. _emRows.Select(r => r.Goals.FirstOrDefault(x => x.Name == g.Name)?.Margin ?? double.NaN)]));
    }
}

/// <summary>The <c>em=</c> tokens, spelled as the optimize line writes them.</summary>
internal static class AnalysisDirectiveSchemaTokens
{
    public static string EmMode(OptimizerEmMode mode) => CircuitRF.Core.Netlist.AnalysisDirectiveSchema.EmModeTokens[(int)mode];
}
