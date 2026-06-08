using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>
/// Places a new wire segment (polyline).
/// Undo removes it; Redo places it again (preserving object identity).
/// </summary>
internal sealed class PlaceWireCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly EditableWire       _wire;

    public string Description => "Place wire";

    public PlaceWireCommand(SchematicEditModel model, EditableWire wire)
    {
        _model = model;
        _wire  = wire;
    }

    public void Execute()
    {
        _model.Wires.Add(_wire);
        _model.NotifyChanged();
    }

    public void Undo()
    {
        _model.Wires.Remove(_wire);
        _model.NotifyChanged();
    }
}

/// <summary>
/// Deletes existing wire segments (created by selecting wires and pressing Delete).
/// Handled by DeleteCommand; this file exists for discoverability.
/// </summary>
// Deletion of wires is handled generically by DeleteCommand.
