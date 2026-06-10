using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Cell;

/// <summary>
/// Appends a new <see cref="CcellParameter"/> to the cell's parameter interface.
/// Execute adds; Undo removes.  Both persist to .ccell and fire Changed.
/// </summary>
internal sealed class AddCellParameterCommand : IUiCommand
{
    private readonly CellParameterEditModel _model;
    private readonly CcellParameter         _param;

    public string Description => $"Add parameter {_param.Name}";

    public AddCellParameterCommand(CellParameterEditModel model, CcellParameter param)
    {
        _model = model;
        _param = param;
    }

    public void Execute()
    {
        _model.MutableParameters.Add(_param);
        _model.Save();
        _model.NotifyChanged();
    }

    public void Undo()
    {
        _model.MutableParameters.Remove(_param);
        _model.Save();
        _model.NotifyChanged();
    }
}
