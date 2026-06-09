using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>
/// Toggles ShowOnSchematic on a single component parameter.
/// Undoable; fires NotifyChanged so the render snapshot refreshes.
/// </summary>
internal sealed class SetParameterVisibilityCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly EditableParameter  _param;
    private readonly bool               _newValue;
    private readonly bool               _oldValue;

    public string Description =>
        $"{(_newValue ? "Show" : "Hide")} {_param.Name} on schematic";

    public SetParameterVisibilityCommand(
        SchematicEditModel model,
        EditableParameter  param,
        bool               newValue)
    {
        _model    = model;
        _param    = param;
        _newValue = newValue;
        _oldValue = param.ShowOnSchematic;
    }

    public void Execute() { _param.ShowOnSchematic = _newValue; _model.NotifyChanged(); }
    public void Undo()    { _param.ShowOnSchematic = _oldValue; _model.NotifyChanged(); }
}
