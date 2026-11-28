using System.Text.RegularExpressions;
using CircuitRF.Design.Optimization;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>
/// Parameters to Variables (the schematic context menu): every plain-number parameter of the chosen parts becomes a row
/// <c>&lt;Designator&gt;_&lt;Param&gt;</c> of ONE new VAR block, holding the value and unit the part had, and the part's
/// parameter then reads that name. A placed cell (a kit PCell, a designed cell) also converts every parameter its
/// <c>.ccell</c> declares that the instance does not override: the row takes the declared default and unit, and the
/// instance gains the override. Words, choices, files and values already written as expressions are left as they are.
/// </summary>
internal static class ParametersToVariables
{
    /// <summary>Numbers that identify rather than size a part (a port's number, a block's port count).</summary>
    private static readonly HashSet<string> Identities = new(StringComparer.Ordinal) { "Num", "NumPorts", "NumFreqs" };

    /// <summary>The parts the command acts on from <paramref name="clickedId"/>: every selected component when the
    /// clicked one is selected, else the clicked one alone.</summary>
    public static IReadOnlyList<EditableComponent> Targets(SchematicEditModel model, IReadOnlySet<string> selectedIds, string? clickedId)
    {
        if (clickedId is null || model.FindComponent(clickedId) is not { } clicked) return [];
        return selectedIds.Contains(clicked.Id) ? [.. model.Components.Where(c => selectedIds.Contains(c.Id))] : [clicked];
    }

    /// <summary>True when at least one of <paramref name="parts"/> has a parameter to convert.</summary>
    public static bool Offers(SchematicEditModel model, IReadOnlyList<EditableComponent> parts)
        => parts.Any(part => Convertible(model, part).Any());

    /// <summary>The VAR block and the parameter edits as ONE undo step, or null when there is nothing to convert. The
    /// block is placed beside the first part, clear of every component and wire.</summary>
    public static IUiCommand? Command(SchematicEditModel model, IReadOnlyList<EditableComponent> parts)
    {
        var taken = model.Components.Where(c => c.Symbol == SymbolKind.Var)
                         .SelectMany(c => c.Parameters).Select(p => p.Name.Trim()).ToHashSet(StringComparer.Ordinal);
        var block = new EditableComponent
        {
            Symbol = SymbolKind.Var, InstanceName = SchematicEditModel.NextAvailableName(model.Components, SymbolKind.Var),
        };
        var edits = new List<IUiCommand>();
        foreach (var part in parts)
            foreach (var p in Convertible(model, part))
            {
                string stem = Regex.Replace($"{part.InstanceName}_{p.Name}", @"\W", "_"), name = stem;
                for (int k = 2; !taken.Add(name); k++) name = $"{stem}_{k}";
                block.Parameters.Add(new EditableParameter { Name = name, Expression = p.Expression.Trim(), Unit = p.Unit, Dimension = p.Dimension });
                edits.Add(part.Parameters.Contains(p)
                    ? new EditParameterCommand(model, p, name, p.Unit)
                    : new AddParameterCommand(model, part, new EditableParameter
                        { Name = p.Name, Expression = name, Unit = p.Unit, Dimension = p.Dimension, ShowOnSchematic = p.ShowOnSchematic }));
            }
        if (edits.Count == 0) return null;
        Place(model, block, parts[0]);
        return new CommandBatch($"Parameters to Variables ({edits.Count})", [new PlaceComponentCommand(model, block), .. edits]);
    }

    /// <summary>The part's own parameters to convert, then (a placed cell) a stand-in for each declared one it does not
    /// override, carrying the <c>.ccell</c> default; a stand-in is not on the part, which is how Command tells them apart.</summary>
    private static IEnumerable<EditableParameter> Convertible(SchematicEditModel model, EditableComponent part)
    {
        if (part.Symbol is SymbolKind.Var or SymbolKind.Meas) return [];
        var declared = part.CellRef is { Length: > 0 } cellRef
            ? CellSymbolResolver.ResolveCcell(cellRef, model.SchematicDirectory ?? "", model.HostWorkspaceRoot)?.Parameters
            : null;
        var inherited = (declared ?? []).Where(d => part.Parameters.All(p => p.Name != d.Name))
            .Select(d => new EditableParameter
                { Name = d.Name, Expression = d.DefaultExpression, Unit = d.Unit, Dimension = d.Dimension, ShowOnSchematic = d.ShowOnSchematic });
        return part.Parameters.Concat(inherited).Where(p =>
            !p.Name.StartsWith("__", StringComparison.Ordinal) && !Identities.Contains(p.Name)
            && !ComponentTypeRegistry.IsFilePathParameter(part.Symbol, p.Name)
            && declared?.FirstOrDefault(d => d.Name == p.Name) is not ({ Choices: not null } or { IsFilePath: true })
            && TunableValue.TryParse(p.Expression, out _, out _, out _));
    }

    /// <summary>Right of <paramref name="beside"/>, tops aligned, stepped right a grid square at a time until the block and
    /// a square of clearance touch no component and no wire.</summary>
    private static void Place(SchematicEditModel model, EditableComponent block, EditableComponent beside)
    {
        var probe = new SchematicEditModel();
        probe.Components.Add(block);
        var own = probe.BuildRenderModel().Model.Components[0];
        var render = model.BuildRenderModel().Model;
        var boxes = render.Components.Select(c => (c.FullBbMinX, c.FullBbMinY, c.FullBbMaxX, c.FullBbMaxY))
                          .Concat(render.Wires.Select(w => (w.BbMinX, w.BbMinY, w.BbMaxX, w.BbMaxY))).ToList();
        var at = render.Components.FirstOrDefault(c => c.Id == beside.Id);
        double g = model.GridSize > 0 ? model.GridSize : 100;
        double x = model.SnapToGrid((at?.FullBbMaxX ?? beside.X) + 2 * g - own.FullBbMinX);
        double y = model.SnapToGrid((at?.FullBbMinY ?? beside.Y) - own.FullBbMinY);
        while (boxes.Any(b => x + own.FullBbMinX - g < b.Item3 && b.Item1 < x + own.FullBbMaxX + g
                           && y + own.FullBbMinY - g < b.Item4 && b.Item2 < y + own.FullBbMaxY + g))
            x += g;
        block.X = x;
        block.Y = y;
    }
}
