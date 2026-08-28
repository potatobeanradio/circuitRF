using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Commands.Layout;

/// <summary>
/// Removes a set of <see cref="RulerAnnotation"/>s by index — mirrors
/// <see cref="DeleteShapesCommand"/>, including its restore-at-original-index discipline. The indices
/// are what the third selection channel holds, so restoring them out of order would leave a selection
/// pointing at a different ruler than the one that was deleted.
/// </summary>
internal sealed class DeleteRulersCommand : IUiCommand
{
    private readonly LayoutView _view;
    private readonly List<(int Index, RulerAnnotation Ruler)> _items;

    public string Description => "Delete Ruler";

    public DeleteRulersCommand(LayoutView view, IEnumerable<int> indices)
    {
        _view = view;
        _items = indices
            .Where(i => i >= 0 && i < view.Rulers.Count)
            .Distinct()
            .OrderByDescending(i => i)
            .Select(i => (i, view.Rulers[i]))
            .ToList();
    }

    public void Execute()
    {
        lock (_view.RenderLock)
        {
            foreach (var (idx, _) in _items) _view.Rulers.RemoveAt(idx);
            _view.NotifyChanged();
        }
    }

    public void Undo()
    {
        lock (_view.RenderLock)
        {
            foreach (var (idx, ruler) in Enumerable.Reverse(_items))
                _view.Rulers.Insert(idx, ruler);
            _view.NotifyChanged();
        }
    }
}
