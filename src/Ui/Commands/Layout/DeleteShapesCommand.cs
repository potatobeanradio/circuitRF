using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Commands.Layout;

/// <summary>
/// Removes a set of <see cref="LayoutShape"/>s from a <see cref="LayoutView"/> by index — mirrors
/// <c>DeleteSymbolPrimitivesCommand</c> / <c>AddShapeCommand</c>'s restore-at-original-index rule.
///
/// <b>Restoring at original indices (not appending) matters more here than in L1b</b>: deleting a
/// multi-selection spans several z-positions, and undoing it must restore z-order exactly — an
/// Undo that appended would silently reorder every deleted shape relative to its neighbours.
/// </summary>
internal sealed class DeleteShapesCommand : IUiCommand
{
    // Captured at command-build time (before removal), sorted descending by index so Execute can
    // RemoveAt without index shifting.
    private readonly LayoutView _view;
    private readonly List<(int Index, LayoutShape Shape)> _items;

    public string Description => "Delete";

    public DeleteShapesCommand(LayoutView view, IEnumerable<int> indices)
    {
        _view = view;
        _items = indices
            .Where(i => i >= 0 && i < view.Shapes.Count)
            .Distinct()
            .OrderByDescending(i => i)
            .Select(i => (i, view.Shapes[i]))
            .ToList();
    }

    public void Execute()
    {
        foreach (var (idx, _) in _items)
            _view.Shapes.RemoveAt(idx);
        _view.NotifyChanged();
    }

    public void Undo()
    {
        // Re-insert ascending so each index is valid at the moment of its own insertion.
        foreach (var (idx, shape) in Enumerable.Reverse(_items))
            _view.Shapes.Insert(idx, shape);
        _view.NotifyChanged();
    }
}
