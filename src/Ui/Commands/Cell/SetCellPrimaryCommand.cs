using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Cell;

/// <summary>
/// Undoable command that sets the primary schematic or symbol on a cell's .ccell file.
/// Execute and Undo both call Save() + NotifyChanged() (+ PrimarySymbolChanged for symbol).
/// </summary>
internal sealed class SetCellPrimaryCommand : IUiCommand
{
    private readonly CellParameterEditModel _model;
    private readonly bool                  _isSymbol;
    private readonly string?               _newValue;
    private readonly string?               _oldValue;

    public string Description => _isSymbol
        ? $"Set primary symbol to {_newValue ?? "(none specified)"}"
        : $"Set primary schematic to {_newValue ?? "(none specified)"}";

    public SetCellPrimaryCommand(CellParameterEditModel model, bool isSymbol, string? newValue)
    {
        _model    = model;
        _isSymbol = isSymbol;
        _newValue = newValue;
        _oldValue = isSymbol ? model.PrimarySymbol : model.PrimarySchematic;
    }

    public void Execute()
    {
        if (_isSymbol) _model.SetPrimarySymbol(_newValue);
        else           _model.SetPrimarySchematic(_newValue);
    }

    public void Undo()
    {
        if (_isSymbol) _model.SetPrimarySymbol(_oldValue);
        else           _model.SetPrimarySchematic(_oldValue);
    }
}
