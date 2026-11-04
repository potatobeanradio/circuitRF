using CircuitRF.Core.Expressions;
using CircuitRF.Design.Cells;

namespace CircuitRF.Design.Schematic;

/// <summary>One VAR row of a cell's schematic: what an instance may override (brief-sdl-6 D7).</summary>
public sealed record CellVariable(string Name, string Expression, string Unit);

/// <summary>
/// brief-sdl-6 D7 — a cell's own variables, read from its primary schematic's VAR rows: what an instance's properties list
/// in their Variables group, and what the cell's interface hash names. Read once per save of the schematic.
/// </summary>
public static class CellVariables
{
    private static readonly Dictionary<string, (DateTime Stamp, IReadOnlyList<CellVariable> Vars)> _memo = new(StringComparer.Ordinal);
    private static readonly Lock _gate = new();

    /// <summary>The VAR rows of <paramref name="cellDir"/>'s primary schematic, in the order they are drawn; empty when the
    /// cell has no schematic or it does not read. A disabled VAR row is not a variable.</summary>
    public static IReadOnlyList<CellVariable> Of(string cellDir)
    {
        try
        {
            var primary = CellFolder.ResolvePrimary(cellDir, ViewType.Schematic, useStatCache: true);
            if (primary.State is not (PrimaryState.SoleFile or PrimaryState.NamedPresent) || primary.ResolvedName is null) return [];
            string path = Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Schematic), primary.ResolvedName);
            var stamp = File.GetLastWriteTimeUtc(path);
            lock (_gate)
                if (_memo.TryGetValue(path, out var hit) && hit.Stamp == stamp) return hit.Vars;
            var model = SchematicPersistence.LoadFromFile(path).model;
            IReadOnlyList<CellVariable> vars = model.Components
                .Where(c => c.Symbol == SymbolKind.Var && c.Disable is not (DisableState.Open or DisableState.Short))
                .SelectMany(c => c.Parameters)
                .Where(p => p.Name.Trim().Length > 0)
                .Select(p => new CellVariable(p.Name.Trim(), p.Expression, p.Unit))
                .ToList();
            lock (_gate) _memo[path] = (stamp, vars);
            return vars;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                       or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            return [];
        }
    }
    /// <summary>
    /// brief-sdl-8 D3/D4 — every global of <paramref name="model"/> that a VAR row or a declared cell parameter defines,
    /// evaluated in the schematic's own scope (what extraction binds): its value in base SI and the unit its row states.
    /// A name that does not evaluate to a real number is left out. Null scope (the schematic does not extract) gives none.
    /// </summary>
    public static IReadOnlyDictionary<string, (double Value, string Unit)> Values(SchematicEditModel model)
    {
        var result = new Dictionary<string, (double, string)>(StringComparer.Ordinal);
        NetExtractor.ExtractionResult extraction;
        try { extraction = NetExtractor.Extract(model, "tb", DiskCellResolver.Instance); }
        catch (Exception e) when (e is not OperationCanceledException) { return result; }
        var scope = new Scope("global");
        foreach (var v in extraction.TestBench.GlobalVariables) scope.Bind(v.Name, v.Expression, v.Unit);
        var units = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var v in extraction.TestBench.GlobalVariables) units.TryAdd(v.Name, v.Unit ?? "");
        foreach (var p in model.Components.Where(c => c.Symbol == SymbolKind.Var).SelectMany(c => c.Parameters))
            if (p.Name.Trim().Length > 0) units[p.Name.Trim()] = p.Unit;
        var evaluator = new Evaluator();
        foreach (var v in extraction.TestBench.GlobalVariables)
        {
            if (result.ContainsKey(v.Name)) continue;
            try
            {
                var value = evaluator.Eval(v.Name, scope);
                if (value.Kind == ValueKind.Real) result[v.Name] = (value.AsReal(), units.GetValueOrDefault(v.Name) ?? "");
            }
            catch (Exception e) when (e is ExpressionException or InvalidCastException or InvalidOperationException) { }
        }
        return result;
    }
}
