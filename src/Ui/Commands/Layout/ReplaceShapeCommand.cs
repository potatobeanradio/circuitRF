using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Commands.Layout;

/// <summary>
/// R-L1d-1 — the SINGLE command for every geometry-reshape edit (vertex move, edge move, insert,
/// remove, bulge/control-point change, radius/corner-radius resize, Rect corner resize, and edge-kind
/// conversion/promotion). Not a family of per-operation commands: the promotion rule (§4 of the L1d
/// brief) converting a <see cref="PolygonShape"/> edge to an arc CHANGES THE RUNTIME TYPE to
/// <see cref="CurveShape"/> — a command that mutates a shape in place cannot express that, while one
/// that swaps the instance at a fixed index expresses every edit uniformly. Undo is trivially the
/// reverse swap at the same index, satisfying L1b's restore-at-original-index rule by construction.
///
/// Geometry edits are IMMUTABLE-STYLE per this rule: <see cref="LayoutShapeEditing"/>'s builders never
/// mutate the shape the renderer may currently be reading — they build a new one, and this command is
/// the only place that ever swaps it into <see cref="LayoutView.Shapes"/>.
/// </summary>
internal sealed class ReplaceShapeCommand : IUiCommand
{
    private readonly LayoutView _view;
    private readonly int _index;
    private readonly LayoutShape _before;
    private readonly LayoutShape _after;

    public string Description => "Edit Shape";

    public ReplaceShapeCommand(LayoutView view, int index, LayoutShape before, LayoutShape after)
    {
        _view = view;
        _index = index;
        _before = before;
        _after = after;
    }

    public void Execute()
    {
        _view.Shapes[_index] = _after;
        // L2b: a straight swap at a fixed index — Shapes.Count and every OTHER index are untouched,
        // so this is always a safe Updated for the spatial index's incremental fast path.
        _view.NotifyChanged(LayoutChangeInfo.Updated([_index]));
    }

    public void Undo()
    {
        _view.Shapes[_index] = _before;
        _view.NotifyChanged(LayoutChangeInfo.Updated([_index]));
    }
}
