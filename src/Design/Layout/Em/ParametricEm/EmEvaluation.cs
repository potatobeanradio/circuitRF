// Framework-free. No Avalonia, no SkiaSharp.
//
// brief-sdl-6 D2 — one EM evaluation of an EM-capable cell at values p. The SAVED schematic is read and p applied to its
// variables and parameters; the planner (the one Update Layout from Schematic runs, SDL-0 S4) re-plans the SAVED layout in
// memory, so every hand edit the user made is in the evaluated geometry (SDL-4); the result is solved through
// EmRunService.Run on that in-memory layout, or read from the cache when the copper has been solved before (D3).
//
// THE USER'S FILES ARE NEVER WRITTEN. The .csch, .clay and .cem are read and nothing else; what is written is the cache
// entry, and the planner's generated PCell cells, which go to the workspace's content-addressed generated-cell store as
// they do on any Update Layout. Writing values back is SDL-7's Apply.

using System.Globalization;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Layout.SchematicSync;
using CircuitRF.Design.Schematic;
using CircuitRF.Engine;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Design.Layout.Em.ParametricEm;

/// <summary>The geometry an evaluation produced, before any solve.</summary>
/// <param name="Source">The evaluated layout, in memory, as the EM run reads it.</param>
/// <param name="Key">Its cache key (<see cref="EmCellCache.KeyOf"/>).</param>
/// <param name="CachedSnp">The cached result for that key, or null when it has not been solved.</param>
/// <param name="Unreached">D4: one sentence per variable that does not reach a part it drives in the schematic, because
/// that part's value was set in the layout.</param>
/// <param name="Notes">What the plan reported that a reader of an EM result should know (gaps it could not close).</param>
/// <param name="Applied">The values that differed from the schematic's own and were applied.</param>
/// <param name="Connectivity">brief-sdl-7 D9 — which nets the copper joins and which it lets touch; null when it could not
/// be measured.</param>
public sealed record EmCellGeometry(
    EmCellTarget Target, EmLayoutSource Source, string Key, string? CachedSnp,
    IReadOnlyList<string> Unreached, IReadOnlyList<string> Notes, IReadOnlyDictionary<string, Value> Applied,
    ConnectivitySignature? Connectivity = null)
{
    /// <summary>brief-sdl-8 — for a cell solved through its 3D view, the point's 3D problem; null for a <c>.cem</c>.</summary>
    public EmThreeDGeometry? ThreeD { get; init; }

    /// <summary>brief-sdl-10 D3 — the schematic at this point's values (the in-memory copy the layout was planned from);
    /// what a variant cell carries as its own schematic. Null for a 3D-parametric cell.</summary>
    public SchematicEditModel? Schematic { get; init; }
}

/// <summary>What one evaluation came to.</summary>
/// <param name="SnpPath">The Touchstone at this geometry; null when refused or only counted.</param>
/// <param name="Cached">True when it came from the cache.</param>
/// <param name="Refusal">The sentence saying why there is no result.</param>
/// <param name="Estimate">For a geometry not yet solved, what solving it would cost (the planar dry-run figures).</param>
public sealed record EmEvaluationResult(
    EmCellGeometry? Geometry, string? SnpPath, bool Cached, string? Refusal, EmSolveEstimate? Estimate = null)
{
    public string? Key => Geometry?.Key;
}

/// <summary>What solving one geometry would cost: its unknowns, its memory, and its factor time over the sweep — the
/// numbers <c>em --dry-run</c> gives for the same setup.</summary>
public sealed record EmSolveEstimate(int Unknowns, long MemoryBytes, double? Seconds, int Points, string Kernel);

public static partial class EmEvaluation
{
    /// <summary>The name of the VAR row an evaluation adds to carry values the schematic has no VAR row for (a cell's
    /// declared parameters). Never saved: it lives in the in-memory copy the planner reads.</summary>
    public const string ValuesRow = "__EMVALUES";

    /// <summary>
    /// A stand-in for the EM solver, for tests (brief-sdl-7 §3's stub provider): given the geometry and the values that
    /// produced it, write the Touchstone to the path it is handed (no extension; the stand-in adds <c>.sNp</c>) and
    /// return that file, or null with nothing written for a failed solve. Null runs the real solver. Flow-local, so a
    /// test that sets it never reaches a real solve another test runs at the same time.
    /// </summary>
    internal static Func<EmCellGeometry, IReadOnlyDictionary<string, Value>, string, string?>? SolverStandIn
    {
        get => _standIn.Value;
        set => _standIn.Value = value;
    }

    private static readonly AsyncLocal<Func<EmCellGeometry, IReadOnlyDictionary<string, Value>, string, string?>?> _standIn = new();

    private static readonly Lock _keysGate = new();
    private static readonly Dictionary<string, object> _keyLocks = new(StringComparer.Ordinal);

    /// <summary>
    /// The geometry <paramref name="target"/> takes at <paramref name="values"/>, built in memory, with its cache key. Null
    /// with <paramref name="refusal"/> when the schematic, layout or technology cannot be read.
    /// </summary>
    public static EmCellGeometry? Geometry(EmCellTarget target, IReadOnlyDictionary<string, Value> values, out string? refusal)
    {
        refusal = null;
        // brief-sdl-10 D1 — a 3D-parametric cell: nothing to plan, its 3D view at the point.
        if (target.ThreeDOnly) return ThreeDOnlyGeometry(target, values, out refusal);
        SchematicEditModel model;
        try
        {
            model = SchematicPersistence.LoadFromFile(target.SchematicPath).model;
            model.SchematicDirectory = Path.GetDirectoryName(target.SchematicPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                       or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            refusal = $"{target.CellName}'s schematic could not be read ({e.Message}).";
            return null;
        }

        // brief-sdl-8 D7 — the design's own values, before this point's are applied: what the held mesh is held at.
        var saved = target.Is3D ? CellVariables.Values(model) : null;
        // D4 follows a part's expression through the VAR rows AS DRAWN: Apply turns each row whose value moved into a literal,
        // which would cut the chain at the first variable and never name the one actually being varied.
        var drawnVars = VarExpressions(model);
        var applied = Apply(model, values);

        LayoutView layout;
        try { layout = LayoutPersistence.LoadFromFile(target.ClayPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                       or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            refusal = $"{target.CellName}'s layout could not be read ({e.Message}).";
            return null;
        }

        var (tech, _) = TechnologyResolver.ResolveForDocument(layout.TechRef, target.ClayPath, target.CwsPath, new TechnologyCache());
        if (tech.Tech is null)
        {
            refusal = $"{target.CellName}'s layout resolves no technology, so it has no stackup to solve: " +
                      string.Join(" ", tech.Diagnostics);
            return null;
        }

        string layoutDir = Path.GetDirectoryName(target.ClayPath)!;
        SchematicLayoutPlan plan;
        try
        {
            // Connect parts ON whatever the GUI preference says: EM solves copper, and copper the planner did not join is
            // not the circuit the schematic draws.
            plan = SchematicLayoutPlanner.Plan(model, layout, model.SchematicDirectory!, target.WorkspaceDir, layoutDir,
                                               tech.Tech, tech.ResolvedPath, DiskCellResolver.Instance,
                                               targetLayoutPath: target.ClayPath, connectParts: true);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            refusal = $"{target.CellName}'s layout could not be planned at these values: {e.Message}";
            return null;
        }
        // R-sdl6-2 — a part the planner could not place (no layout view, a footprint it cannot resolve) is missing from the
        // copper, so the EM result would not be the circuit the schematic draws. A refusal naming each, never an omission.
        // A 3D view's setup is left to its own evaluation, which decides what its parts are.
        if (!target.Is3D && plan.NoLayoutWarnings.Count > 0)
        {
            refusal = $"{target.CellName}'s layout cannot hold every part its schematic places, so its EM result would leave " +
                      $"them out: {string.Join(" ", plan.NoLayoutWarnings)}";
            return null;
        }
        plan.ApplyTo(layout);

        var source = new EmLayoutSource(target.ClayPath, layout, tech.Tech, layout.DbuPerMicron);
        var notes = plan.Lines.Where(l => l.Severity == SchematicLayoutPlanner.ReportSeverity.Warning)
                              .Select(l => l.Text).Concat(plan.NoLayoutWarnings).ToList();
        IReadOnlyList<string>? partGroups = null;
        try { partGroups = ConnectivitySignature.PartGroups(layout, target.ClayPath, tech.Tech); }
        catch (Exception e) when (e is not OperationCanceledException) { /* the joints and overlaps still hold it */ }
        var connectivity = ConnectivitySignature.Of(plan.Lines, partGroups);
        if (target.Is3D)
            return ThreeDGeometry(target, model, saved!, source, applied, notes, Unreached(model, plan, drawnVars), connectivity, out refusal) is { } g3
                ? g3 with { Schematic = model } : null;
        // brief-icp-8 R-icp8-1 — the parts that are not copper become component ports, and a port that cannot be built is a
        // refusal naming the part: left open, it would not be the circuit the schematic draws.
        var setup = target.RunSetup;
        if (setup.ComponentPorts.Count > 0 && EmCellParts.Refusal(target.CellName, EmComponentPorts.Collect(source, setup)) is { } partsRefused)
        {
            refusal = partsRefused;
            return null;
        }
        string key = EmCellCache.KeyOf(source, setup);
        return new EmCellGeometry(target, source, key, EmCellCache.Find(target.WorkspaceDir, target.CellName, key),
                              Unreached(model, plan, drawnVars), notes, applied, connectivity) { Schematic = model };
    }

    /// <summary>
    /// brief-sdl-7 D9 — what the schematic's own lengths do to the copper, with no hand edit in the way: the planner run
    /// at <paramref name="values"/> onto an EMPTY layout, and its warnings (loops that do not close, connections not
    /// joined, overlaps). A re-plan of the saved layout cannot say this: there, every open joint between parts already
    /// placed reads as one the user opened (SDL-4 D6), whatever opened it. A joint the user opened closes here; a gap the
    /// lengths make does not.
    /// </summary>
    public static IReadOnlyList<string> SchematicDisagreements(EmCellTarget target, IReadOnlyDictionary<string, Value> values)
    {
        try
        {
            var model = SchematicPersistence.LoadFromFile(target.SchematicPath).model;
            model.SchematicDirectory = Path.GetDirectoryName(target.SchematicPath);
            Apply(model, values);
            var saved = LayoutPersistence.LoadFromFile(target.ClayPath);
            var (tech, _) = TechnologyResolver.ResolveForDocument(saved.TechRef, target.ClayPath, target.CwsPath, new TechnologyCache());
            if (tech.Tech is null) return [];
            var empty = new LayoutView { DbuPerMicron = saved.DbuPerMicron, TechRef = saved.TechRef, DisplayUnit = saved.DisplayUnit };
            var plan = SchematicLayoutPlanner.Plan(model, empty, model.SchematicDirectory!, target.WorkspaceDir,
                                                   Path.GetDirectoryName(target.ClayPath)!, tech.Tech, tech.ResolvedPath,
                                                   DiskCellResolver.Instance, targetLayoutPath: target.ClayPath, connectParts: true);
            return ConnectivitySignature.Of(plan.Lines).Lines;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                       or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>
    /// The EM result of <paramref name="target"/> at <paramref name="values"/>: from the cache when this geometry has been
    /// solved, else solved now and cached. <paramref name="countOnly"/> stops before solving and returns the estimate.
    /// Never throws for a refusal; a cancelled solve throws <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <param name="estimate">With <paramref name="countOnly"/>, whether an unsolved geometry's cost is estimated (the
    /// planar preflight, which meshes it). A probe that only asks which geometry and whether it connects says no.</param>
    public static EmEvaluationResult Evaluate(EmCellTarget target, IReadOnlyDictionary<string, Value> values,
                                              bool countOnly = false, RunControl? control = null, int? maxCores = null,
                                              bool estimate = true)
    {
        if (Geometry(target, values, out var refusal) is not { } g)
            return new EmEvaluationResult(null, null, false, refusal);
        if (g.CachedSnp is { } hit) return new EmEvaluationResult(g, hit, true, null);
        // brief-sdl-8 D8 — a 3D solve whose estimate is past this machine's memory is refused before any solve, counted or not.
        if (g.ThreeD?.Estimate is { MemoryBytes: > 0 } big && big.MemoryBytes > CircuitRF.Design.Em3d.MachineMemory.PhysicalBytes
            && CircuitRF.Design.Em3d.MachineMemory.PhysicalBytes > 0)
            return new EmEvaluationResult(g, null, false,
                $"the EM solve of '{target.CellName}' needs about {CircuitRF.Design.Em3d.MachineMemory.Format(big.MemoryBytes)} " +
                $"({big.Kernel}'s estimate), more than this machine's {CircuitRF.Design.Em3d.MachineMemory.Format(CircuitRF.Design.Em3d.MachineMemory.PhysicalBytes)}",
                big);
        if (countOnly) return new EmEvaluationResult(g, null, false, null, estimate ? Estimate(g) : null);

        lock (KeyLock(g.Key))
        {
            // Another evaluation of the same copper may have finished while this one waited.
            if (EmCellCache.Find(target.WorkspaceDir, target.CellName, g.Key) is { } now)
                return new EmEvaluationResult(g with { CachedSnp = now }, now, true, null);

            string dir = EmCellCache.CellFolder(target.WorkspaceDir, target.CellName);
            try { Directory.CreateDirectory(dir); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                return new EmEvaluationResult(g, null, false,
                    $"the EM result of '{target.CellName}' has nowhere to be kept: its EM cache folder {dir} cannot be made ({e.Message}).");
            }
            // The setup keeps its own name, so the Touchstone is the one `em` writes for it byte for byte but the write
            // time and the stamp. Only where it goes changes: <key>.sNp in the cache, and the run's other output (the .npy with the
            // diagnostics group) in a <key>/ folder beside it.
            if (SolverStandIn is { } standIn)
            {
                if (standIn(g, values, Path.Combine(dir, g.Key)) is not { } written)
                    return new EmEvaluationResult(g, null, false, $"the EM solve of '{target.CellName}' failed: the stand-in wrote nothing");
                EmCellCache.WriteProvenance(written, target, values);
                return new EmEvaluationResult(g, written, false, null);
            }
            var setup = (g.ThreeD?.Setup ?? target.RunSetup).Clone();
            // Written under a name Find never matches, then renamed onto <key>.sNp: a solve stopped mid-write (a crash, a
            // killed process) leaves a file the cache ignores, never a truncated Touchstone read back as solved.
            // brief-icp-8 — the parts go back on the component ports in the bench (EmCellParts.Close), so no _cosim is written.
            var runTarget = new EmRunTarget { TouchstoneDestination = Path.Combine(dir, g.Key + ".partial-" + Guid.NewGuid().ToString("N")[..8]), KeepRecord = false, WriteCoSim = false };
            setup.CreateSnpSchematic = false;      // a cache entry makes no _sim cell in the user's workspace
            var run = g.ThreeD is { } three
                // brief-sdl-8 R-sdl8-2 — the 3D view's run, on the point's document and its regenerated layout, in memory.
                ? EmRunService.RunThreeDView(setup, three.Document, target.C3dPath!, target.CwsPath, Path.Combine(dir, g.Key),
                                             control?.Token ?? default, control, maxCores, elaborator: three.Elaborator, target: runTarget)
                : EmRunService.Run(setup, g.Source, Path.Combine(dir, g.Key), control?.Token ?? default, control, maxCores, target: runTarget);
            if (run.Status == EmRunStatus.Cancelled)
            {
                TryDelete(run.SnpPath);
                throw new OperationCanceledException(run.Error);
            }
            if (run.Status != EmRunStatus.Ok || run.SnpPath is not { } solvedAt)
            {
                TryDelete(run.SnpPath);
                return new EmEvaluationResult(g, null, false,
                    $"the EM solve of '{target.CellName}' failed: {run.Error ?? "it wrote no Touchstone"}");
            }
            string snp = Path.Combine(dir, g.Key + Path.GetExtension(solvedAt));
            try { File.Move(solvedAt, snp, overwrite: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                TryDelete(solvedAt);
                return new EmEvaluationResult(g, null, false,
                    $"the EM result of '{target.CellName}' was solved but could not be kept in the EM cache ({e.Message}).");
            }
            EmCellCache.WriteProvenance(snp, target, values);
            return new EmEvaluationResult(g, snp, false, null);
        }
    }

    /// <summary>
    /// brief-icp-8 R-icp8-2 — the ports <paramref name="g"/>'s result will have, in its Touchstone's order, found as its run finds them
    /// and with nothing solved: each drawn port by its number, each component port with its part. Null when they cannot be found
    /// before solving (the run would then refuse).
    /// </summary>
    public static IReadOnlyList<EmCoSimPort>? ResultPorts(EmCellGeometry g)
        => g.ThreeD is { } three ? three.ResultPorts : EmRunService.PlannedPorts(g.Target.RunSetup, g.Source)?.Ports;

    /// <summary>What solving <paramref name="g"/> would cost, from the run's own preflight; null when the preflight refuses
    /// (the run would then refuse with the same sentence).</summary>
    public static EmSolveEstimate? Estimate(EmCellGeometry g)
    {
        if (g.ThreeD is { } three) return three.Estimate;
        try
        {
            var pre = EmRunService.Preflight(g.Target.RunSetup, g.Source);
            if (!pre.Ok || pre.PlanarMesh is not { } mesh) return null;
            int points = 0;
            try { points = g.Target.Setup.ExpandFrequencies().Length; } catch (Exception) { }
            long memory = pre.NearField is { } nf ? nf.ResidentBytes : mesh.RunResidentBytes;
            double? perPoint = pre.NearField is { } near ? near.FactorSeconds
                             : g.Target.Setup.AcceleratedSolve ? null
                             : SurfaceMesher.EstimatedFactorSeconds(mesh.UnknownCount);
            return new EmSolveEstimate(mesh.UnknownCount, memory, perPoint * Math.Max(points, 1), points, pre.KernelName);
        }
        catch (Exception e) when (e is not OperationCanceledException) { return null; }
    }

    /// <summary>
    /// <paramref name="values"/> applied to the in-memory schematic: each value that differs from the schematic's own
    /// replaces its VAR row's expression (a base-SI literal, no unit); a value with no VAR row — a cell's declared
    /// parameter, whose default extraction binds as a global — goes on one added row, which extraction prefers to the
    /// default. A value equal to the schematic's own is left alone, so the evaluation at the schematic's values is exactly
    /// the layout Update Layout would make. Only real values reach geometry.
    /// </summary>
    private static Dictionary<string, Value> Apply(SchematicEditModel model, IReadOnlyDictionary<string, Value> values)
    {
        var applied = new Dictionary<string, Value>(StringComparer.Ordinal);
        if (values.Count == 0) return applied;

        var extraction = NetExtractor.Extract(model, "tb", DiskCellResolver.Instance);
        var scope = new Scope("global");
        foreach (var v in extraction.TestBench.GlobalVariables) scope.Bind(v.Name, v.Expression, v.Unit);
        var evaluator = new Evaluator();
        var globals = extraction.TestBench.GlobalVariables.Select(v => v.Name).ToHashSet(StringComparer.Ordinal);
        var units = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var v in extraction.TestBench.GlobalVariables) units.TryAdd(v.Name, v.Unit);
        foreach (var p in model.Components.Where(c => c.Symbol == SymbolKind.Var).SelectMany(c => c.Parameters))
            units[p.Name.Trim()] = p.Unit;   // the row's own spelling, as its editor wrote it

        var rows = model.Components.Where(c => c.Symbol == SymbolKind.Var).SelectMany(c => c.Parameters)
                        .GroupBy(p => p.Name.Trim(), StringComparer.Ordinal)
                        .ToDictionary(gr => gr.Key, gr => gr.ToList(), StringComparer.Ordinal);
        EditableComponent? extra = null;

        foreach (var (name, value) in values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (value.Kind != ValueKind.Real || !globals.Contains(name)) continue;
            double x = value.AsReal();
            try
            {
                var own = evaluator.Eval(name, scope);
                if (own.Kind == ValueKind.Real && Same(own.AsReal(), x)) continue;
            }
            catch (Exception) { /* not evaluable on its own: apply */ }

            // Written IN THE ROW'S OWN UNIT, the unit kept: a bare base-SI literal reads differently wherever a use site
            // applies its own unit to a unit-less variable (var-unit-wins only spares a variable that HAS a unit), and
            // 4.6604 mm written as a bare 0.0046604 made every line it set three orders of magnitude short.
            string? unit = units.GetValueOrDefault(name);
            double scale = unit is { Length: > 0 } u && Units.Scale(UnitNormalizer.ToEngineUnit(u)) is { } sc && sc != 0 ? sc : 1.0;
            string literal = (x / scale).ToString("R", CultureInfo.InvariantCulture);
            if (rows.TryGetValue(name, out var ps))
                foreach (var p in ps) p.Expression = literal;
            else
            {
                extra ??= new EditableComponent { InstanceName = ValuesRow, Symbol = SymbolKind.Var };
                extra.Parameters.Add(new EditableParameter { Name = name, Expression = literal, Unit = unit ?? "" });
            }
            applied[name] = value;
        }
        if (extra is not null) model.Components.Add(extra);
        return applied;
    }

    private static bool Same(double a, double b) => a == b || Math.Abs(a - b) <= 1e-12 * Math.Max(Math.Abs(a), Math.Abs(b));

    /// <summary>
    /// D4 — for every part whose parameter is set in the layout and kept over the schematic's (SDL-4), the variables that
    /// would have driven it: the names its schematic expression reads, followed through the VAR rows they are defined by.
    /// <paramref name="drawnVars"/> is those rows as drawn (<see cref="VarExpressions"/> taken before any value was
    /// applied); null reads them off <paramref name="model"/>.
    /// </summary>
    internal static IReadOnlyList<string> Unreached(SchematicEditModel model, SchematicLayoutPlan plan,
                                                    IReadOnlyDictionary<string, string>? drawnVars = null)
    {
        if (plan.LayoutKept.Count == 0) return [];
        var vars = drawnVars ?? VarExpressions(model);
        var lines = new List<string>();
        foreach (var (id, param) in plan.LayoutKept)
        {
            var comp = model.Components.FirstOrDefault(c => c.InstanceName == id);
            if (comp?.Parameters.FirstOrDefault(p => p.Name == param) is not { } p) continue;
            var reached = new SortedSet<string>(StringComparer.Ordinal);
            var pending = new Queue<string>(Names(p.Expression));
            while (pending.TryDequeue(out var n))
            {
                if (!vars.TryGetValue(n, out var expr) || !reached.Add(n)) continue;
                foreach (var m in Names(expr)) pending.Enqueue(m);
            }
            foreach (var n in reached)
                lines.Add($"{n} does not reach {id}.{param}, which is set in the layout.");
        }
        return lines.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Each VAR row's name and expression as <paramref name="model"/> holds them (the first row of a name wins;
    /// the evaluation's own added row is not one of the schematic's).</summary>
    internal static IReadOnlyDictionary<string, string> VarExpressions(SchematicEditModel model) =>
        model.Components.Where(c => c.Symbol == SymbolKind.Var && c.InstanceName != ValuesRow)
             .SelectMany(c => c.Parameters).Where(p => p.Name.Trim().Length > 0)
             .GroupBy(p => p.Name.Trim(), StringComparer.Ordinal)
             .ToDictionary(g => g.Key, g => g.First().Expression, StringComparer.Ordinal);

    private static IEnumerable<string> Names(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return [];
        try
        {
            return new Tokenizer(expression).Tokenize().Where(t => t.Kind == TokenKind.Identifier).Select(t => t.Text).ToList();
        }
        catch (Exception) { return []; }
    }

    private static void TryDelete(string? path)
    {
        if (path is not { Length: > 0 }) return;
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* a leftover partial is never read */ }
    }

    private static object KeyLock(string key)
    {
        lock (_keysGate)
        {
            if (!_keyLocks.TryGetValue(key, out var o)) _keyLocks[key] = o = new object();
            return o;
        }
    }
}
