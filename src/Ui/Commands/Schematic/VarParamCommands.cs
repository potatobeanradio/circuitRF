using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>
/// Atomically replaces all parameters on a component.
/// Used by VAR text mode and the add/remove-group path in ParameterEditorViewModel.
/// </summary>
internal sealed class SetParametersCommand : IUiCommand
{
    private readonly SchematicEditModel         _model;
    private readonly EditableComponent          _comp;
    private readonly List<EditableParameter>    _oldParams;
    private readonly List<EditableParameter>    _newParams;

    public string Description => "Edit parameters";

    public SetParametersCommand(
        SchematicEditModel      model,
        EditableComponent       comp,
        IEnumerable<EditableParameter> newParams)
    {
        _model     = model;
        _comp      = comp;
        _oldParams = comp.Parameters.Select(p => p.Clone()).ToList();
        _newParams = newParams.Select(p => p.Clone()).ToList();
    }

    public void Execute() => Apply(_newParams);
    public void Undo()    => Apply(_oldParams);

    private void Apply(List<EditableParameter> src)
    {
        _comp.Parameters.Clear();
        foreach (var p in src)
            _comp.Parameters.Add(p.Clone());
        _model.NotifyChanged();
    }
}

/// <summary>
/// Appends one new empty parameter row to a component.
/// </summary>
internal sealed class AddParameterCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly EditableComponent  _comp;
    private readonly EditableParameter  _param;

    public string Description => "Add parameter";

    public AddParameterCommand(SchematicEditModel model, EditableComponent comp, EditableParameter param)
    {
        _model = model;
        _comp  = comp;
        _param = param;
    }

    public void Execute() { _comp.Parameters.Add(_param); _model.NotifyChanged(); }
    public void Undo()    { _comp.Parameters.Remove(_param); _model.NotifyChanged(); }
}

/// <summary>
/// Removes one parameter row from a component (restoring it on undo).
/// </summary>
internal sealed class RemoveParameterCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly EditableComponent  _comp;
    private readonly EditableParameter  _param;
    private readonly int                _index;

    public string Description => "Remove parameter";

    public RemoveParameterCommand(SchematicEditModel model, EditableComponent comp, EditableParameter param)
    {
        _model = model;
        _comp  = comp;
        _param = param;
        _index = comp.Parameters.IndexOf(param);
    }

    public void Execute() { _comp.Parameters.Remove(_param); _model.NotifyChanged(); }
    public void Undo()
    {
        int idx = Math.Clamp(_index, 0, _comp.Parameters.Count);
        _comp.Parameters.Insert(idx, _param);
        _model.NotifyChanged();
    }
}

/// <summary>
/// Renames a single parameter (changes Name only).
/// </summary>
internal sealed class SetParameterNameCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly EditableParameter  _param;
    private readonly string             _newName;
    private readonly string             _oldName;

    public string Description => $"Rename parameter to {_newName}";

    public SetParameterNameCommand(SchematicEditModel model, EditableParameter param, string newName)
    {
        _model   = model;
        _param   = param;
        _oldName = param.Name;
        _newName = newName;
    }

    public void Execute() { _param.Name = _newName; _model.NotifyChanged(); }
    public void Undo()    { _param.Name = _oldName; _model.NotifyChanged(); }
}
