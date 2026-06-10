using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Cell;

/// <summary>
/// Removes a <see cref="CcellParameter"/> from the cell's parameter interface.
/// Execute removes (saving the insertion index); Undo re-inserts at the saved index.
/// Both persist to .ccell and fire Changed.
/// </summary>
internal sealed class RemoveCellParameterCommand : IUiCommand
{
    private readonly CellParameterEditModel _model;
    private readonly CcellParameter         _param;
    private int _savedIndex = -1;

    public string Description => $"Remove {_param.Name}";

    public RemoveCellParameterCommand(CellParameterEditModel model, CcellParameter param)
    {
        _model = model;
        _param = param;
    }

    public void Execute()
    {
        _savedIndex = _model.MutableParameters.IndexOf(_param);
        if (_savedIndex < 0) return;
        _model.MutableParameters.RemoveAt(_savedIndex);
        _model.Save();
        _model.NotifyChanged();
    }

    public void Undo()
    {
        if (_savedIndex < 0) return;
        int insertAt = Math.Clamp(_savedIndex, 0, _model.MutableParameters.Count);
        _model.MutableParameters.Insert(insertAt, _param);
        _model.Save();
        _model.NotifyChanged();
    }
}
