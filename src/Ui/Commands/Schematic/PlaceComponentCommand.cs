using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>
/// Places a new component on the schematic.
/// Undo removes it; Redo places it again (same Id — object identity preserved).
/// </summary>
internal sealed class PlaceComponentCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly EditableComponent  _component;
    private readonly Action?            _onChanged;

    public string Description => $"Place {_component.Symbol} \"{_component.InstanceName}\"";

    public PlaceComponentCommand(
        SchematicEditModel model, EditableComponent component, Action? onChanged = null)
    {
        _model     = model;
        _component = component;
        _onChanged = onChanged;
    }

    public void Execute()
    {
        _model.Components.Add(_component);
        _model.NotifyChanged();
        _onChanged?.Invoke();
    }

    public void Undo()
    {
        _model.Components.Remove(_component);
        _model.NotifyChanged();
        _onChanged?.Invoke();
    }
}
