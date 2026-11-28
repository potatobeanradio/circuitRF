// Owner: PCD-13 (brief-pcd-13 §2). Framework-free.
//
// A designed cell's operation history: the records the .csch holds (D1) and the edits, listings and readings the
// designer window (PCD-14), the CLI and the model switch call. The geometry is PCellOperationsEngine's.

using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Layout.SchematicSync;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Workspace;

namespace CircuitRF.Design.PCellDesigner.Operations;

public enum PCellOperationKind { Merge, Subtract, Intersect, Exclusive, Grow, Repeat }

/// <summary>brief-pcd-13 R-pcd13-1: one step of a designed cell's history. Expressions evaluate in the cell's scope; a
/// length is in <see cref="Unit"/> when it carries none of its own.</summary>
public sealed record PCellOperation
{
    public string Id { get; init; } = "";
    public PCellOperationKind Kind { get; init; }
    public List<string> A { get; init; } = [];
    public List<string>? B { get; init; }
    public string? Layer { get; init; }
    public string? Amount { get; init; }
    public string? Onto { get; init; }
    public string? Rows { get; init; }
    public string? Columns { get; init; }
    public string? PitchX { get; init; }
    public string? PitchY { get; init; }
    public string Unit { get; init; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Suppressed { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool KeepModel { get; init; }

    internal bool IsBoolean => Kind is PCellOperationKind.Merge or PCellOperationKind.Subtract
                                    or PCellOperationKind.Intersect or PCellOperationKind.Exclusive;
}

/// <summary>A history edit: the new schematic, or null with the sentence saying why not.</summary>
public sealed record PCellOperationEdit(SchematicEditModel? Schematic, string? Refusal);

/// <summary>What one operation did when the layout was planned, and the shapes it made in the cell's frame.</summary>
public sealed record PCellOperationStep(string Id, bool Applied, string? Refusal, IReadOnlyList<LayoutShape> Result);

/// <summary>A parameter dimension an operation gives (brief-pcd-13 D9), in the plan's DBU.</summary>
public sealed record PCellOperationSpan(string Expression, string Unit, long FromX, long FromY, long ToX, long ToY);

public sealed record PCellOperationTrace(IReadOnlyList<PCellOperationStep> Steps, IReadOnlyList<PCellOperationSpan> Spans);

/// <summary>The layout with the history applied through one operation, and that operation's step.</summary>
public sealed record PCellOperationPreview(LayoutView? Layout, PCellOperationStep? Step,
                                           IReadOnlyList<SchematicLayoutPlanner.ReportLine> Lines, string? Refusal);

/// <summary>brief-pcd-13 §2: the history's edits and readings.</summary>
public static class PCellOperations
{
    public const string LayerPrefix = "layer:";
    public const string GeneratedTag = "pcell-ops";

    public static IReadOnlyList<PCellOperation> Of(SchematicEditModel schematic) => schematic.PCellOperations ?? [];

    public static string NextId(SchematicEditModel schematic)
    {
        var taken = Of(schematic).Select(o => o.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int k = 1;
        while (taken.Contains($"op{k}")) k++;
        return $"op{k}";
    }

    public static PCellOperationEdit Add(SchematicEditModel current, PCellOperation operation, int index = -1)
    {
        var op = operation.Id.Length == 0 ? operation with { Id = NextId(current) } : operation;
        if (Of(current).Any(o => o.Id.Equals(op.Id, StringComparison.OrdinalIgnoreCase)))
            return new(null, $"The history already has an operation {op.Id}.");
        return Changed(current, op, list => list.Insert(index < 0 || index > list.Count ? list.Count : index, op));
    }

    public static PCellOperationEdit Edit(SchematicEditModel current, string id, PCellOperation operation)
    {
        int at = IndexOf(current, id);
        if (at < 0) return Unknown(id);
        var op = operation with { Id = Of(current)[at].Id };
        return Changed(current, op, list => list[at] = op);
    }

    public static PCellOperationEdit Move(SchematicEditModel current, string id, int index)
    {
        int at = IndexOf(current, id);
        if (at < 0) return Unknown(id);
        return Changed(current, null, list =>
        {
            var op = list[at];
            list.RemoveAt(at);
            list.Insert(Math.Clamp(index, 0, list.Count), op);
        });
    }

    public static PCellOperationEdit Suppress(SchematicEditModel current, string id, bool suppressed)
    {
        int at = IndexOf(current, id);
        return at < 0 ? Unknown(id) : Changed(current, null, list => list[at] = list[at] with { Suppressed = suppressed });
    }

    public static PCellOperationEdit Delete(SchematicEditModel current, string id)
    {
        int at = IndexOf(current, id);
        return at < 0 ? Unknown(id) : Changed(current, null, list => list.RemoveAt(at));
    }

    private static int IndexOf(SchematicEditModel m, string id)
    {
        var list = Of(m);
        for (int i = 0; i < list.Count; i++) if (list[i].Id.Equals(id, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static PCellOperationEdit Unknown(string id) => new(null, $"The history has no operation {id}.");

    /// <summary>A copy of <paramref name="current"/> with its history changed by <paramref name="change"/>, after
    /// <paramref name="checkedOp"/> (the operation written, when one is) is found well formed.</summary>
    private static PCellOperationEdit Changed(SchematicEditModel current, PCellOperation? checkedOp, Action<List<PCellOperation>> change)
    {
        if (checkedOp is not null && Malformed(current, checkedOp) is { } why) return new(null, $"{checkedOp.Id}: {why}");
        var copy = SchematicPersistence.Deserialize(SchematicPersistence.Serialize(current), current.SchematicDirectory).model;
        copy.SchematicDirectory = current.SchematicDirectory;
        var list = new List<PCellOperation>(Of(current));
        change(list);
        copy.PCellOperations = list.Count > 0 ? list : null;
        return new(copy, null);
    }

    /// <summary>Why an operation cannot stand in this schematic, or null: the fields its kind needs, operands that name a
    /// part or a layer, and expressions that parse.</summary>
    private static string? Malformed(SchematicEditModel m, PCellOperation op)
    {
        if (op.A.Count == 0) return "it names no operand.";
        if (op.IsBoolean && op.B is not { Count: > 0 }) return $"{op.Kind} needs a second operand (B).";
        if (op.Kind == PCellOperationKind.Grow && string.IsNullOrWhiteSpace(op.Amount)) return "Grow needs an Amount.";
        if (op.Kind == PCellOperationKind.Repeat && (string.IsNullOrWhiteSpace(op.Rows) || string.IsNullOrWhiteSpace(op.Columns)))
            return "Repeat needs Rows and Columns.";
        foreach (string operand in op.A.Concat(op.B ?? []))
            if (!operand.StartsWith(LayerPrefix, StringComparison.OrdinalIgnoreCase)
                && !m.Components.Any(c => c.InstanceName == operand && !PortPlacement.IsPort(c)))
                return $"{operand} is neither a part of the cell nor {LayerPrefix}<name>.";
        foreach (string? e in new[] { op.Amount, op.Rows, op.Columns, op.PitchX, op.PitchY })
        {
            if (string.IsNullOrWhiteSpace(e)) continue;
            try { Parser.Parse(e); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return $"'{e}' is not an expression: {ex.Message}"; }
        }
        return null;
    }

    // ── Readings ─────────────────────────────────────────────────────────────

    /// <summary>One line naming what the operation does.</summary>
    public static string Describe(PCellOperation op)
    {
        string a = string.Join(" + ", op.A), b = string.Join(" + ", op.B ?? []);
        string length(string? e) => string.IsNullOrWhiteSpace(e) ? "0" : e.Trim() + (op.Unit.Length > 0 ? " " + op.Unit : "");
        string what = op.Kind switch
        {
            PCellOperationKind.Merge     => $"{a} + {b}",
            PCellOperationKind.Subtract  => $"{a} - ({b})",
            PCellOperationKind.Intersect => $"{a} & ({b})",
            PCellOperationKind.Exclusive => $"{a} ^ ({b})",
            PCellOperationKind.Grow      => $"{a} by {length(op.Amount)}{(op.Onto is { Length: > 0 } onto ? " onto " + onto : "")}",
            _                            => $"{a} {op.Rows} x {op.Columns} at {length(op.PitchX)}, {length(op.PitchY)}",
        };
        return $"{op.Id}  {op.Kind}  {what}  on {(op.Layer is { Length: > 0 } l ? l : "top conductor")}"
               + (op.Suppressed ? "  (suppressed)" : "") + (op.KeepModel ? "  (keep model)" : "");
    }

    /// <summary>The cell's primary schematic's history as <see cref="Describe"/> lines; empty for none.</summary>
    public static IReadOnlyList<string> Listing(string cellDir)
        => PrimarySchematic(cellDir) is { } m ? [.. Of(m).Select(Describe)] : [];

    /// <summary>An MSHAPE whose Role is "tool": a cutter, never copper (brief-pcd-13 D2).</summary>
    public static bool IsTool(EditableComponent component)
        => DrawnMetal.Is(component)
           && component.Parameters.FirstOrDefault(p => p.Name == DrawnMetal.Role)?.Expression.Trim().Trim('"').Trim()
                  .Equals(DrawnMetal.RoleTool, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>R-pcd13-4 (D7): the operations that make this cell EM-only, as "op2 (Subtract)".</summary>
    public static IReadOnlyList<string> WithoutCircuitModel(SchematicEditModel schematic, Technology? technology)
    {
        var tools = schematic.Components.Where(IsTool).Select(c => c.InstanceName).ToHashSet(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (var op in Of(schematic))
        {
            if (op.Suppressed || op.KeepModel) continue;
            if (op.Kind == PCellOperationKind.Merge && !op.A.Concat(op.B ?? []).Any(tools.Contains)) continue;   // copper ∪ copper adds nothing
            string? written = op.Kind == PCellOperationKind.Grow && op.Onto is { Length: > 0 } onto ? onto : op.Layer;
            if (written is { Length: > 0 } name && technology is not null && !IsConductor(technology, name)) continue;
            list.Add($"{op.Id} ({op.Kind})");
        }
        return list;
    }

    private static bool IsConductor(Technology tech, string layerName)
        => tech.Layers.FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase)) is not { } layer
           || tech.Stackup.Layers.Any(s => s.Kind == StackupKind.Conductor && s.DrawingLayers.Contains(layer.Key));

    /// <summary>
    /// D7: the bench's and every library cell's operations that make them EM-only, as "Cell:op2 (Subtract)", for each
    /// that is a registered designed cell. What <c>EmModelSwitch.WithoutCircuitModel</c> appends.
    /// </summary>
    /// <remarks>ponytail: a name is looked up through the registered resolvers, so a run outside a workspace (no
    /// registration) sees no operation; a key on the cell's define would carry it (brief §5).</remarks>
    internal static IEnumerable<string> WithoutCircuitModelIn(Core.Design.TestBench tb, Core.Design.Library lib)
    {
        foreach (string name in lib.Cells.Select(c => c.Name).Prepend(tb.Name).Distinct(StringComparer.Ordinal))
        {
            if (name is "" or "tb" || DesignedPCellResolver.CellDirOf(PCellDesign.GeneratorIdFor(name)) is not { } dir
                || PrimarySchematic(dir) is not { PCellOperations.Count: > 0 } m) continue;
            foreach (string op in WithoutCircuitModel(m, TechnologyOf(dir))) yield return $"{name}:{op}";
        }
    }

    /// <summary>The technology a designed cell is planned on: its layout's, as <c>pcell render</c> resolves it.</summary>
    internal static Technology? TechnologyOf(string cellDir)
    {
        if (WorkspaceRootFinder.FindAncestorCws(cellDir) is not { } cws) return null;
        string clay = Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout),
                                   CellFolder.ResolvePrimary(cellDir, ViewType.Layout).ResolvedName ?? Path.GetFileName(cellDir) + ".clay");
        try
        {
            string? techRef = File.Exists(clay) ? LayoutPersistence.LoadFromFile(clay).TechRef : null;
            return TechnologyResolver.ResolveForDocument(techRef, clay, cws, new TechnologyCache()).Item1.Tech;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        { return null; }
    }

    private static SchematicEditModel? PrimarySchematic(string cellDir)
    {
        if (CellFolder.ResolvePrimary(cellDir, ViewType.Schematic).ResolvedName is not { } csch) return null;
        try { return SchematicPersistence.LoadFromFile(Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Schematic), csch)).model; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        { return null; }
    }

    // ── What the engine did ──────────────────────────────────────────────────

    private static readonly ConditionalWeakTable<LayoutView, PCellOperationTrace> _traces = new();

    internal static void Record(LayoutView layout, PCellOperationTrace trace) => _traces.AddOrUpdate(layout, trace);

    public static PCellOperationTrace? TraceOf(LayoutView layout) => _traces.TryGetValue(layout, out var t) ? t : null;

    public static PCellOperationPreview Preview(string cellDir, SchematicEditModel schematic, string operationId,
                                                IReadOnlyDictionary<string, Value> values, string workspaceRootDir,
                                                Technology technology, string? techIdentity)
    {
        int at = IndexOf(schematic, operationId);
        if (at < 0) return new(null, null, [], $"The history has no operation {operationId}.");
        var copy = SchematicPersistence.Deserialize(SchematicPersistence.Serialize(schematic), schematic.SchematicDirectory).model;
        copy.SchematicDirectory = schematic.SchematicDirectory;
        copy.PCellOperations = [.. Of(schematic).Take(at + 1)];
        var result = DesignedCellLayout.At(cellDir, values, workspaceRootDir, technology, techIdentity, copy);
        var step = result.Layout is { } layout ? TraceOf(layout)?.Steps.FirstOrDefault(s => s.Id == Of(schematic)[at].Id) : null;
        return new(result.Layout, step, result.Lines, result.Refusal);
    }

}
