using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>
/// Sets one component's rotation and mirror outright, about its own origin — what Update Schematic
/// from Layout does when the layout placement was turned. <see cref="RotateCommand"/> and
/// <see cref="MirrorCommand"/> are relative gestures over a selection; a sync knows the orientation it
/// wants, not the steps to it.
/// </summary>
internal sealed class SetOrientationCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly EditableComponent _comp;
    private readonly SymbolRotation _rot, _oldRot;
    private readonly bool _mirror, _oldMirror;

    public string Description => "Set Orientation";

    public SetOrientationCommand(SchematicEditModel model, EditableComponent comp, SymbolRotation rot, bool mirror)
    {
        _model = model;
        _comp = comp;
        _rot = rot;
        _mirror = mirror;
        _oldRot = comp.Rotation;
        _oldMirror = comp.MirrorX;
    }

    public void Execute()
    {
        _comp.Rotation = _rot;
        _comp.MirrorX = _mirror;
        _model.NotifyChanged();
    }

    public void Undo()
    {
        _comp.Rotation = _oldRot;
        _comp.MirrorX = _oldMirror;
        _model.NotifyChanged();
    }
}
