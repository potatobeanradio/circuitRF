using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>
/// Toggles <see cref="EditableComponent.ShowFootprintLabel"/> — brief-footprint-2 R-fp2-5a.
/// Undoable; fires NotifyChanged so the render snapshot refreshes.
///
/// <para>Its own command rather than a third leg on <see cref="SetLabelVisibilityCommand"/>'s
/// boolean: that one already branches on <c>isTypeLabel</c>, and a second bool to choose between
/// three things is how a call site comes to mean the wrong one.</para>
/// </summary>
internal sealed class SetFootprintLabelVisibilityCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly EditableComponent  _comp;
    private readonly bool               _newValue;
    private readonly bool               _oldValue;

    public string Description => $"{(_newValue ? "Show" : "Hide")} footprint label on {_comp.InstanceName}";

    public SetFootprintLabelVisibilityCommand(
        SchematicEditModel model, EditableComponent comp, bool newValue)
    {
        _model    = model;
        _comp     = comp;
        _newValue = newValue;
        _oldValue = comp.ShowFootprintLabel;
    }

    public void Execute() => Apply(_newValue);
    public void Undo()    => Apply(_oldValue);

    private void Apply(bool value)
    {
        _comp.ShowFootprintLabel = value;
        _model.NotifyChanged();
    }
}
