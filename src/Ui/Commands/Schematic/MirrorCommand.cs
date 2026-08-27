using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>
/// Mirrors the selection as ONE RIGID BODY — about a vertical axis by default (a left↔right flip),
/// about a horizontal one otherwise. Components, canvas objects, the wires between them and their
/// junction dots are all reflected through the same axis, so nothing that was connected comes apart;
/// see <see cref="SchematicGroupTransform"/>.
///
/// <para><b>The orientation bookkeeping is derived, not guessed, and it changed here.</b> A symbol's
/// transform is mirror-then-rotate, so pre-composing a world reflection gives
/// <c>M ∘ Rot(θ) ∘ Mx^m = Rot(−θ) ∘ Mx^(m+1)</c> for a horizontal flip: the rotation NEGATES and the
/// mirror flag toggles. A vertical flip is a half turn on top of that, <c>Rot(180 − θ)</c>. This
/// command used to toggle the flag and leave the rotation alone (horizontal) or advance it by a half
/// turn regardless (vertical), which is only a reflection for an UNROTATED symbol — on a symbol at
/// R90 or R270 it reflected about the other axis instead, silently. The Layout Editor derives the
/// same two rules for its instances.</para>
///
/// <para>Undo reverses the whole gesture atomically.</para>
/// </summary>
internal sealed class MirrorCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly SchematicGroupTransform _transform;
    private readonly bool _horizontal;  // true = mirror H (flip left↔right), false = mirror V

    public string Description => _horizontal ? "Mirror Horizontal" : "Mirror Vertical";

    public MirrorCommand(SchematicEditModel model, IReadOnlyList<string> selectedIds, bool horizontal = true)
    {
        _model      = model;
        _horizontal = horizontal;

        _transform = SchematicGroupTransform.Build(model, selectedIds, new SchematicGroupTransform.Spec(
            MapOffset:      horizontal ? (dx, dy) => (-dx, dy) : (dx, dy) => (dx, -dy),
            MapRotation:    r => MirroredRotation(r, horizontal),
            TogglesMirrorX: true,
            // A canvas object's ANGLE is left alone: text has no mirror of its own, and reversed
            // glyphs would be worse than none. Its anchor moves with everything else; the words stay
            // readable. Stated rather than silently dropped — the Layout Editor makes the same call.
            MapObjectAngleDeg: d => d));
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

    /// <summary>Rot(−θ) for a horizontal flip; Rot(180 − θ) for a vertical one.</summary>
    private static SymbolRotation MirroredRotation(SymbolRotation rot, bool horizontal)
        => horizontal
            ? rot switch
            {
                SymbolRotation.R90  => SymbolRotation.R270,
                SymbolRotation.R270 => SymbolRotation.R90,
                _                   => rot,                    // R0 and R180 are their own negatives
            }
            : rot switch
            {
                SymbolRotation.R0   => SymbolRotation.R180,
                SymbolRotation.R180 => SymbolRotation.R0,
                _                   => rot,                    // 180 − 90 = 90; 180 − 270 = −90 = 270
            };
}
