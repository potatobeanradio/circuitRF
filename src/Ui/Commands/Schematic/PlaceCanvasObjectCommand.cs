using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>Places a canvas object (bitmap/text/primitive); undo removes it.</summary>
internal sealed class PlaceCanvasObjectCommand : IUiCommand
{
    private readonly SchematicEditModel     _model;
    private readonly EditableCanvasObject   _object;

    public string Description => $"Insert {_object.Kind}";

    public PlaceCanvasObjectCommand(SchematicEditModel model, EditableCanvasObject obj)
    {
        _model  = model;
        _object = obj;
    }

    public void Execute() { _model.CanvasObjects.Add(_object);    _model.NotifyChanged(); }
    public void Undo()    { _model.CanvasObjects.Remove(_object); _model.NotifyChanged(); }
}

/// <summary>
/// Resizes a canvas object. Records the before/after dimensions.
/// </summary>
internal sealed class ResizeCanvasObjectCommand : IUiCommand
{
    private readonly SchematicEditModel   _model;
    private readonly EditableCanvasObject _object;
    private readonly double _oldX, _oldY, _oldW, _oldH;
    private readonly double _newX, _newY, _newW, _newH;

    public string Description => "Resize";

    public ResizeCanvasObjectCommand(
        SchematicEditModel model, EditableCanvasObject obj,
        double newX, double newY, double newW, double newH)
    {
        _model = model; _object = obj;
        _oldX = obj.X; _oldY = obj.Y; _oldW = obj.Width; _oldH = obj.Height;
        _newX = newX;  _newY = newY;  _newW = newW;      _newH = newH;
    }

    public void Execute()
    {
        _object.X = _newX; _object.Y = _newY;
        _object.Width = _newW; _object.Height = _newH;
        _model.NotifyChanged();
    }

    public void Undo()
    {
        _object.X = _oldX; _object.Y = _oldY;
        _object.Width = _oldW; _object.Height = _oldH;
        _model.NotifyChanged();
    }
}

/// <summary>
/// Sets a property on a canvas object (e.g. ImagePath, Text, transparency, lock state).
/// Uses pre/post snapshots of the property value.
/// </summary>
internal sealed class SetCanvasObjectPropertyCommand<T> : IUiCommand
{
    private readonly SchematicEditModel   _model;
    private readonly EditableCanvasObject _obj;
    private readonly string               _propName;
    private readonly T                    _oldValue;
    private readonly T                    _newValue;
    private readonly Action<T>            _setter;

    public string Description => $"Set {_propName}";

    public SetCanvasObjectPropertyCommand(
        SchematicEditModel model, EditableCanvasObject obj,
        string propName, T oldValue, T newValue, Action<T> setter)
    {
        _model = model; _obj = obj;
        _propName = propName;
        _oldValue = oldValue; _newValue = newValue;
        _setter = setter;
    }

    public void Execute() { _setter(_newValue); _model.NotifyChanged(); }
    public void Undo()    { _setter(_oldValue); _model.NotifyChanged(); }
}
