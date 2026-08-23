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
///
/// <b>L2b:</b> that same restore-at-original-index rule is exactly what makes this a safe trailing
/// append/remove for the spatial index's incremental fast path (<see cref="LayoutChangeInfo.Appended"/>/
/// <see cref="LayoutChangeInfo.RemovedTrailing"/>): under a linear (LIFO) undo/redo stack, <see
/// cref="Undo"/> can only ever run once everything pushed after this command has already been undone —
/// so the shape being removed is always, provably, the CURRENT last element of <c>Shapes</c>, regardless
/// of what other command types were interleaved. No other shape's index ever shifts.
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
        lock (_view.RenderLock)   // one step as far as the render thread is concerned — see DeleteShapesCommand
        {
            if (_index < 0) _index = _view.Shapes.Count;
            _view.Shapes.Insert(_index, _shape);
            // L2b: always a trailing append under LIFO undo/redo — see the type doc comment. Safe for the
            // incremental spatial-index fast path.
            _view.NotifyChanged(LayoutChangeInfo.Appended(_index, 1));
        }
    }

    public void Undo()
    {
        lock (_view.RenderLock)
        {
            _view.Shapes.Remove(_shape);
            _view.NotifyChanged(LayoutChangeInfo.RemovedTrailing(_index, 1));
        }
    }
}
