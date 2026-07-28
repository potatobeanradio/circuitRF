using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Commands.Layout;

/// <summary>Removes a set of <see cref="LayoutInstance"/>s from a <see cref="LayoutView"/> by index —
/// the instance analogue of <see cref="DeleteShapesCommand"/>, same restore-at-original-index rule.</summary>
internal sealed class DeleteInstancesCommand : IUiCommand
{
    private readonly LayoutView _view;
    private readonly List<(int Index, LayoutInstance Instance)> _items;

    public string Description => "Delete Instance";

    public DeleteInstancesCommand(LayoutView view, IEnumerable<int> indices)
    {
        _view = view;
        _items = indices
            .Where(i => i >= 0 && i < view.Instances.Count)
            .Distinct()
            .OrderByDescending(i => i)
            .Select(i => (i, view.Instances[i]))
            .ToList();
    }

    public void Execute()
    {
        foreach (var (idx, _) in _items)
            _view.Instances.RemoveAt(idx);
        _view.NotifyChanged(LayoutChangeInfo.InstancesOnly);
    }

    public void Undo()
    {
        foreach (var (idx, instance) in Enumerable.Reverse(_items))
            _view.Instances.Insert(idx, instance);
        _view.NotifyChanged(LayoutChangeInfo.InstancesOnly);
    }
}
