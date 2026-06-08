using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>Places a net label; undo removes it.</summary>
internal sealed class PlaceNetLabelCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly EditableNetLabel   _label;

    public string Description => $"Place net label \"{_label.Name}\"";

    public PlaceNetLabelCommand(SchematicEditModel model, EditableNetLabel label)
    {
        _model = model;
        _label = label;
    }

    public void Execute() { _model.NetLabels.Add(_label);    _model.NotifyChanged(); }
    public void Undo()    { _model.NetLabels.Remove(_label); _model.NotifyChanged(); }
}

/// <summary>Places a junction dot; undo removes it.</summary>
internal sealed class PlaceDotCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly EditableDot        _dot;

    public string Description => "Place junction";

    public PlaceDotCommand(SchematicEditModel model, EditableDot dot)
    {
        _model = model;
        _dot   = dot;
    }

    public void Execute() { _model.Dots.Add(_dot);    _model.NotifyChanged(); }
    public void Undo()    { _model.Dots.Remove(_dot); _model.NotifyChanged(); }
}
