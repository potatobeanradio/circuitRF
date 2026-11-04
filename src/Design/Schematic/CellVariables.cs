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
}
