using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>
/// Sets (or clears) the corner selected on one axis, on the testbench that owns it.
///
/// <para>Undoable and dirtying, exactly like every other schematic-level setting — a corner changes
/// every number the design produces, so it belongs in the same history as the design itself rather
/// than being a view preference that quietly persists on its own.</para>
///
/// <para>A null or blank <c>newValue</c> REMOVES the entry rather than storing an empty string, so
/// "no corner chosen on this axis" has exactly one representation and a design that has been set
/// back to defaults writes no corner block at all.</para>
/// </summary>
internal sealed class SetCornerSelectionCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly string             _axisKey;
    private readonly string?            _oldValue;
    private readonly string?            _newValue;

    public string Description => "Set corner";

    public SetCornerSelectionCommand(SchematicEditModel model, string axisKey, string? newValue)
    {
        _model    = model;
        _axisKey  = axisKey;
        _oldValue = model.CornerSelections.TryGetValue(axisKey, out var v) ? v : null;
        _newValue = string.IsNullOrWhiteSpace(newValue) ? null : newValue;
    }

    public void Execute() => Apply(_newValue);
    public void Undo()    => Apply(_oldValue);

    private void Apply(string? value)
    {
        if (value is null) _model.CornerSelections.Remove(_axisKey);
        else               _model.CornerSelections[_axisKey] = value;
        _model.NotifyChanged();
    }
}
