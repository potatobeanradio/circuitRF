using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Cell;

/// <summary>
/// Undoable command that sets the number of ports declared on a cell's .ccell file.
/// Execute and Undo both call Save() + NotifyChanged() + PortCountChanged.
/// </summary>
internal sealed class SetCellPortCountCommand : IUiCommand
{
    private readonly CellParameterEditModel _model;
    private readonly int                    _newValue;
    private readonly int                    _oldValue;

    public string Description => $"Set number of ports to {_newValue}";

    public SetCellPortCountCommand(CellParameterEditModel model, int newValue)
    {
        _model    = model;
        _newValue = newValue;
        _oldValue = model.NumPorts;
    }

    public void Execute() => _model.SetNumPorts(_newValue);
    public void Undo()    => _model.SetNumPorts(_oldValue);
}
