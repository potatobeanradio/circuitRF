namespace CircuitRF.Core.Design;

/// <summary>
/// A named set of a cell's own parameter values — a published part's stated limits (min / typ / max), the
/// <c>corner</c> line inside a <c>define</c> (brief vendorlib-13 R-vl13-2a). A corner binds only parameters the
/// cell declares; a parameter it does not bind keeps its nominal default.
///
/// <para><b>A corner is an alternative set of DEFAULTS</b>, evaluated where a default is — in the cell's own
/// scope — so an instance that also overrides a parameter still wins, exactly as it wins over the default.</para>
/// </summary>
public sealed class CellCorner(string name)
{
    /// <summary>The name an instance selects, the reserved <see cref="Nominal"/> aside.</summary>
    public string Name { get; } = name;

    /// <summary>Parameter → value, in the order written; a parameter absent here is nominal.</summary>
    public List<ParameterAssignment> Bindings { get; } = [];

    /// <summary>The corner every cell has and none declares: its own defaults.</summary>
    public const string Nominal = "nominal";

    /// <summary>The instance parameter that selects a corner. A selector, never a parameter of the cell: a cell that
    /// declares one of its own under this name cannot be published as a library part.</summary>
    public const string SelectorParameter = "Corner";

    /// <summary>The part prefix a bench <c>corner</c> line names a library part's corner by: <c>part:TSTFET100=min</c>.</summary>
    public const string PartPrefix = "part:";

    /// <summary>The corner an instance's <see cref="SelectorParameter"/> override names, or null when it sets none
    /// (or names <see cref="Nominal"/>). The value may be written bare or quoted.</summary>
    public static string? SelectedBy(IEnumerable<ParameterAssignment> overrides)
    {
        var sel = overrides.FirstOrDefault(o => o.Name == SelectorParameter);
        if (sel is null) return null;
        string v = sel.Expression.Trim();
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') v = v[1..^1].Trim();
        return v.Length == 0 || v.Equals(Nominal, StringComparison.OrdinalIgnoreCase) ? null : v;
    }

    /// <summary>
    /// The cell an instance descends into and the overrides it binds, once its corner selection is applied: the
    /// selected corner's values replace the cell's defaults, and the selector itself is dropped. A cell with no
    /// selection comes back as it is. Throws, naming the instance, the cell and its corners, for a corner the cell
    /// does not declare.
    /// </summary>
    public static (Cell Cell, IReadOnlyList<ParameterAssignment> Overrides) Apply(
        Cell cell, IReadOnlyList<ParameterAssignment> overrides, string instancePath)
    {
        if (!overrides.Any(o => o.Name == SelectorParameter)) return (cell, overrides);
        var rest = overrides.Where(o => o.Name != SelectorParameter).ToList();
        if (SelectedBy(overrides) is not { } name) return (cell, rest);

        var corner = cell.Corners.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"'{instancePath}' selects the corner '{name}', which cell '{cell.Name}' does not declare; it offers " +
                (cell.Corners.Count == 0 ? "only nominal." : $"nominal, {string.Join(", ", cell.Corners.Select(c => c.Name))}."));

        var variant = cell.CopyWith(corner);
        return (variant, rest);
    }
}
