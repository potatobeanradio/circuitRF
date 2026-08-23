using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Commands.Layout;

/// <summary>
/// Adds a new <see cref="LayoutInstance"/> to a <see cref="LayoutView"/> — the instance-placement
/// analogue of <see cref="AddShapeCommand"/> (L3a, docs/sonnet-briefs/brief-L3a-instances-and-arrays.md
/// §6). Restore-at-original-index for the same reason: <c>LayoutView.Instances</c> has no ZOrder of
/// its own, but list order still governs draw order (later instances paint over earlier ones) and
/// undo/redo must not silently reshuffle that.
/// </summary>
internal sealed class AddInstanceCommand : IUiCommand
{
    private readonly LayoutView _view;
    private readonly LayoutInstance _instance;
    private int _index = -1;

    public string Description => "Place Instance";

    public AddInstanceCommand(LayoutView view, LayoutInstance instance)
    {
        _view = view;
        _instance = instance;
    }

    public void Execute()
    {
        lock (_view.RenderLock)   // one step as far as the render thread is concerned — see DeleteShapesCommand
        {
            if (_index < 0) _index = _view.Instances.Count;
            _view.Instances.Insert(_index, _instance);
            _view.NotifyChanged(LayoutChangeInfo.InstancesOnly);
        }
    }

    public void Undo()
    {
        lock (_view.RenderLock)
        {
            _view.Instances.Remove(_instance);
            _view.NotifyChanged(LayoutChangeInfo.InstancesOnly);
        }
    }
}
