using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>
/// Toggles ShowTypeLabel or ShowInstanceName on a component.
/// Undoable; fires NotifyChanged so the render snapshot refreshes.
/// </summary>
internal sealed class SetLabelVisibilityCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly EditableComponent  _comp;
    private readonly bool               _isTypeLabel;
    private readonly bool               _newValue;
    private readonly bool               _oldValue;

    public string Description =>
        $"{(_newValue ? "Show" : "Hide")} {(_isTypeLabel ? "type label" : "instance name")} on {_comp.InstanceName}";

    public SetLabelVisibilityCommand(
        SchematicEditModel model,
        EditableComponent  comp,
        bool               isTypeLabel,
        bool               newValue)
    {
        _model       = model;
        _comp        = comp;
        _isTypeLabel = isTypeLabel;
        _newValue    = newValue;
        _oldValue    = isTypeLabel ? comp.ShowTypeLabel : comp.ShowInstanceName;
    }

    public void Execute() => Apply(_newValue);
    public void Undo()    => Apply(_oldValue);

    private void Apply(bool value)
    {
        if (_isTypeLabel) _comp.ShowTypeLabel    = value;
        else              _comp.ShowInstanceName  = value;
        _model.NotifyChanged();
    }
}
