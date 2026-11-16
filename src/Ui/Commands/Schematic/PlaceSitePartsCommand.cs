using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>
/// Place site parts: the parts and wires <see cref="PassiveSites.PlaceParts"/> worked out for a planned passive's empty
/// sites, added as one step. Undo removes them; Redo adds the same objects again.
/// </summary>
internal sealed class PlaceSitePartsCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly PassiveSites.Placement _placement;

    public string Description => $"Place site parts ({_placement.Parts.Count})";

    public PlaceSitePartsCommand(SchematicEditModel model, PassiveSites.Placement placement)
    {
        _model = model;
        _placement = placement;
    }

    public void Execute()
    {
        _model.Components.AddRange(_placement.Parts);
        _model.Wires.AddRange(_placement.Wires);
        _model.NotifyChanged();
    }

    public void Undo()
    {
        foreach (var part in _placement.Parts) _model.Components.Remove(part);
        foreach (var wire in _placement.Wires) _model.Wires.Remove(wire);
        _model.NotifyChanged();
    }
}
