using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Cell;

/// <summary>
/// Undoable replacement of a cell's whole terminal map (<c>brief-lvs-1-terminal-map.md</c> R-lvs1-4c).
///
/// <para><b>The whole block, not one row.</b> The map's defects are relational — a duplicate port, one
/// layout pin claimed twice — so a half-applied edit is a state the validator would report as broken
/// while the user was still typing the other half of the fix. Replacing it wholesale also makes undo
/// exact: the previous block is a deep copy, so undoing a "use the derived map" gesture puts the cell
/// back to declaring nothing, which is a different state from declaring an empty list.</para>
/// </summary>
internal sealed class SetCellTerminalsCommand : IUiCommand
{
    private readonly CellParameterEditModel  _model;
    private readonly List<CcellTerminal>?    _newValue;
    private readonly List<CcellTerminal>?    _oldValue;

    public string Description => _newValue is null ? "Clear terminal map" : "Set terminal map";

    public SetCellTerminalsCommand(CellParameterEditModel model, List<CcellTerminal>? newValue)
    {
        _model    = model;
        _newValue = Copy(newValue);
        _oldValue = Copy(model.Terminals);
    }

    public void Execute() => _model.SetTerminals(Copy(_newValue));
    public void Undo()    => _model.SetTerminals(Copy(_oldValue));

    /// <summary>A deep copy in BOTH directions: the command's own snapshots must not alias the list the
    /// model is mutating, or a later edit would rewrite this command's idea of the past.</summary>
    private static List<CcellTerminal>? Copy(IReadOnlyList<CcellTerminal>? rows)
        => rows is null ? null : [.. rows.Select(r => r.Clone())];
}
