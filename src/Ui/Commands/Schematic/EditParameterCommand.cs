using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>
/// Changes the expression and unit of a single named parameter on a component.
/// The parameter name stays fixed — only value and units are edited via the inline box.
/// </summary>
internal sealed class EditParameterCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly EditableParameter _param;
    private readonly string _newExpression;
    private readonly string _oldExpression;
    private readonly string _newUnit;
    private readonly string _oldUnit;

    public string Description => $"Edit {_param.Name}";

    public EditParameterCommand(SchematicEditModel model, EditableParameter param, string newExpression, string newUnit = "")
    {
        _model         = model;
        _param         = param;
        _oldExpression = param.Expression;
        _oldUnit       = param.Unit;
        _newExpression = newExpression;
        _newUnit       = newUnit;
    }

    public void Execute() { _param.Expression = _newExpression; _param.Unit = _newUnit; _model.NotifyChanged(); }
    public void Undo()    { _param.Expression = _oldExpression; _param.Unit = _oldUnit; _model.NotifyChanged(); }
}

/// <summary>
/// Changes a component's InstanceName.
/// </summary>
internal sealed class RenameComponentCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly EditableComponent _comp;
    private readonly string _newName;
    private readonly string _oldName;

    public string Description => "Rename component";

    public RenameComponentCommand(SchematicEditModel model, EditableComponent comp, string newName)
    {
        _model   = model;
        _comp    = comp;
        _oldName = comp.InstanceName;
        _newName = newName;
    }

    public void Execute() { _comp.InstanceName = _newName; _model.NotifyChanged(); }
    public void Undo()    { _comp.InstanceName = _oldName; _model.NotifyChanged(); }
}

/// <summary>
/// Renames an existing net label.
/// </summary>
internal sealed class RenameNetLabelCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly EditableNetLabel _label;
    private readonly string _newName;
    private readonly string _oldName;

    public string Description => $"Rename net label to {_newName}";

    public RenameNetLabelCommand(SchematicEditModel model, EditableNetLabel label, string newName)
    {
        _model   = model;
        _label   = label;
        _oldName = label.Name;
        _newName = newName;
    }

    public void Execute() { _label.Name = _newName; _model.NotifyChanged(); }
    public void Undo()    { _label.Name = _oldName; _model.NotifyChanged(); }
}
