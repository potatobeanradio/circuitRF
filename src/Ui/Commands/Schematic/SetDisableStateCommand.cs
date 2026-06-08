using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>
/// Sets the disable state (None / Open / Short) on selected components.
/// The renderer shows the disabled glyph; extraction (6e) honors it.
/// </summary>
internal sealed class SetDisableStateCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly DisableState _newState;
    private readonly List<(EditableComponent Comp, DisableState OldState)> _snaps = [];

    public string Description => _newState switch
    {
        DisableState.Open  => "Disable to Open",
        DisableState.Short => "Disable to Short",
        _                  => "Enable component",
    };

    public SetDisableStateCommand(SchematicEditModel model, IReadOnlyList<string> ids, DisableState state)
    {
        _model    = model;
        _newState = state;

        foreach (var id in ids)
        {
            var comp = model.FindComponent(id);
            if (comp is not null) _snaps.Add((comp, comp.Disable));
        }
    }

    public void Execute()
    {
        foreach (var (comp, _) in _snaps) comp.Disable = _newState;
        _model.NotifyChanged();
    }

    public void Undo()
    {
        foreach (var (comp, old) in _snaps) comp.Disable = old;
        _model.NotifyChanged();
    }
}
