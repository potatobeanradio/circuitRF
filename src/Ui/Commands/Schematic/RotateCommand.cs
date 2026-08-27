using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>
/// Rotates the selection 90° CW or CCW as ONE RIGID BODY — components, canvas objects, the wires
/// between them and their junction dots all turn together about a single pivot, so nothing that was
/// connected comes apart. <see cref="SchematicGroupTransform"/> owns that rule and the reasoning
/// behind it; a single selected component still turns about its own origin and does not move.
///
/// <para>Undo reverses the whole gesture atomically.</para>
/// </summary>
internal sealed class RotateCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly SchematicGroupTransform _transform;
    private readonly bool _clockwise;

    public string Description => _clockwise ? "Rotate CW" : "Rotate CCW";

    public RotateCommand(SchematicEditModel model, IReadOnlyList<string> selectedIds, bool clockwise = false)
    {
        _model     = model;
        _clockwise = clockwise;

        // The offset map and the symbol map are the SAME rotation, written twice: R90 takes a local
        // (x,y) to (-y,x) — which is what SchematicGeometry.LocalToWorld does — and Step advances a
        // symbol by that same quarter turn. They have to agree, or a pin drifts off the geometry it
        // belongs to. `clockwise: false` is the R0 → R90 direction.
        _transform = SchematicGroupTransform.Build(model, selectedIds, new SchematicGroupTransform.Spec(
            MapOffset:         clockwise ? (dx, dy) => (dy, -dx) : (dx, dy) => (-dy, dx),
            MapRotation:       r => Step(r, clockwise),
            TogglesMirrorX:    false,
            MapObjectAngleDeg: d => (d + (clockwise ? -90.0 : 90.0) + 360.0) % 360.0));
    }

    public void Execute()
    {
        _transform.Apply();
        _model.NotifyChanged();
    }

    public void Undo()
    {
        _transform.Revert();
        _model.NotifyChanged();
    }

    private static SymbolRotation Step(SymbolRotation r, bool cw) => cw
        ? r switch { SymbolRotation.R0 => SymbolRotation.R270, SymbolRotation.R270 => SymbolRotation.R180,
                     SymbolRotation.R180 => SymbolRotation.R90, _ => SymbolRotation.R0 }
        : r switch { SymbolRotation.R0 => SymbolRotation.R90,  SymbolRotation.R90 => SymbolRotation.R180,
                     SymbolRotation.R180 => SymbolRotation.R270, _ => SymbolRotation.R0 };
}
