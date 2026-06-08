using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>
/// Mirrors selected components horizontally (MirrorX) or vertically (via 180° rotation + MirrorX).
/// Undo reverses the mirror.
/// </summary>
internal sealed class MirrorCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly bool _horizontal;  // true = mirror H, false = mirror V

    private readonly List<(EditableComponent Comp, bool OldMirrorX, SymbolRotation OldRot)> _snaps = [];

    public string Description => _horizontal ? "Mirror Horizontal" : "Mirror Vertical";

    public MirrorCommand(SchematicEditModel model, IReadOnlyList<string> selectedIds, bool horizontal = true)
    {
        _model      = model;
        _horizontal = horizontal;

        foreach (var id in selectedIds)
        {
            var comp = model.FindComponent(id);
            if (comp is not null)
                _snaps.Add((comp, comp.MirrorX, comp.Rotation));
        }
    }

    public void Execute()
    {
        foreach (var (comp, _, _) in _snaps)
        {
            if (_horizontal)
                comp.MirrorX = !comp.MirrorX;
            else
            {
                // Vertical mirror = flip MirrorX then rotate 180°
                comp.MirrorX = !comp.MirrorX;
                comp.Rotation = comp.Rotation switch
                {
                    SymbolRotation.R0   => SymbolRotation.R180,
                    SymbolRotation.R180 => SymbolRotation.R0,
                    SymbolRotation.R90  => SymbolRotation.R270,
                    _                   => SymbolRotation.R90,
                };
            }
        }
        _model.NotifyChanged();
    }

    public void Undo()
    {
        foreach (var (comp, oldMirror, oldRot) in _snaps)
        {
            comp.MirrorX  = oldMirror;
            comp.Rotation = oldRot;
        }
        _model.NotifyChanged();
    }
}
