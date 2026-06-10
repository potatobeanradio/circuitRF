using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Cell;

/// <summary>
/// Sets any combination of Name / DefaultExpression / Unit / Dimension / ShowOnSchematic
/// on a <see cref="CcellParameter"/>.  Old state is captured at construction for undo.
/// Used for rename, default edit, unit/dimension changes, and show-on-schematic toggles.
/// Both Execute and Undo persist to .ccell and fire Changed.
/// </summary>
internal sealed class SetCellParameterCommand : IUiCommand
{
    private readonly CellParameterEditModel _model;
    private readonly CcellParameter         _param;

    // Old state — captured at construction time.
    private readonly string        _oldName;
    private readonly string        _oldDefault;
    private readonly string        _oldUnit;
    private readonly UnitDimension _oldDimension;
    private readonly bool          _oldShow;

    // New state.
    private readonly string        _newName;
    private readonly string        _newDefault;
    private readonly string        _newUnit;
    private readonly UnitDimension _newDimension;
    private readonly bool          _newShow;

    public string Description { get; }

    public SetCellParameterCommand(
        CellParameterEditModel model,
        CcellParameter         param,
        string                 newName,
        string                 newDefault,
        string                 newUnit,
        UnitDimension          newDimension,
        bool                   newShow,
        string                 description)
    {
        _model = model;
        _param = param;

        _oldName      = param.Name;
        _oldDefault   = param.DefaultExpression;
        _oldUnit      = param.Unit;
        _oldDimension = param.Dimension;
        _oldShow      = param.ShowOnSchematic;

        _newName      = newName;
        _newDefault   = newDefault;
        _newUnit      = newUnit;
        _newDimension = newDimension;
        _newShow      = newShow;

        Description = description;
    }

    public void Execute() => Apply(_newName, _newDefault, _newUnit, _newDimension, _newShow);
    public void Undo()    => Apply(_oldName, _oldDefault, _oldUnit, _oldDimension, _oldShow);

    private void Apply(string name, string def, string unit, UnitDimension dim, bool show)
    {
        _param.Name              = name;
        _param.DefaultExpression = def;
        _param.Unit              = unit;
        _param.Dimension         = dim;
        _param.ShowOnSchematic   = show;
        _model.Save();
        _model.NotifyChanged();
    }
}
