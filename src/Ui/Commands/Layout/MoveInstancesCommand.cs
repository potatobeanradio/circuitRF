using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Commands.Layout;

/// <summary>
/// Translates a set of <see cref="LayoutInstance"/>s by (Dx, Dy) — the instance analogue of
/// <see cref="MoveShapesCommand"/>. An instance is a single (X, Y) origin (unlike a shape's vertex
/// list), so there is no delta-vs-vertex snapping distinction to get wrong here — the caller still
/// snaps the delta before constructing this command, for the same reason every other layout move does
/// (consistency with the rest of the editor, not because an instance origin could otherwise land
/// off-grid in a way that matters).
/// </summary>
internal sealed class MoveInstancesCommand : IUiCommand
{
    private readonly LayoutView _view;
    private readonly List<int> _indices;
    private readonly long _dx, _dy;

    public string Description => "Move Instance";

    public MoveInstancesCommand(LayoutView view, IEnumerable<int> indices, long dx, long dy)
    {
        _view = view;
        _indices = indices.ToList();
        _dx = dx;
        _dy = dy;
    }

    public void Execute()
    {
        foreach (var i in _indices)
        {
            _view.Instances[i].X += _dx;
            _view.Instances[i].Y += _dy;
        }
        _view.NotifyChanged(LayoutChangeInfo.InstancesOnly);
    }

    public void Undo()
    {
        foreach (var i in _indices)
        {
            _view.Instances[i].X -= _dx;
            _view.Instances[i].Y -= _dy;
        }
        _view.NotifyChanged(LayoutChangeInfo.InstancesOnly);
    }
}
