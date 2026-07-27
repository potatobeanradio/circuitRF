using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Commands.Layout;

/// <summary>
/// Adds a new <see cref="LayoutShape"/> to a <see cref="LayoutView"/>. The one command Phase L1b
/// needs, but built as the pattern L1c/L1d's dozen more layout commands follow.
///
/// <b>Restore-at-original-index, not append.</b> Z-order within a layer is list order
/// (docs/design/layout-view.md §2.3), so an Undo that quietly re-appends the shape at the end would
/// be a rendering-order bug that only surfaces much later. The index is captured once, on the first
/// <see cref="Execute"/>, and every subsequent Undo/Redo re-inserts at that exact position — this is
/// what makes "draw A, B, C; undo C; undo B; redo B" put B back at index 1, not index 2.
/// </summary>
internal sealed class AddShapeCommand : IUiCommand
{
    private readonly LayoutView _view;
    private readonly LayoutShape _shape;
    private int _index = -1;

    public string Description => "Draw";

    public AddShapeCommand(LayoutView view, LayoutShape shape)
    {
        _view = view;
        _shape = shape;
    }

    public void Execute()
    {
        if (_index < 0) _index = _view.Shapes.Count;
        _view.Shapes.Insert(_index, _shape);
        _view.NotifyChanged();
    }

    public void Undo()
    {
        _view.Shapes.Remove(_shape);
        _view.NotifyChanged();
    }
}
