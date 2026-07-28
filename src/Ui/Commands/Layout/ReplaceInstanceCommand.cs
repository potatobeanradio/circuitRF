using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Commands.Layout;

/// <summary>
/// The single command for every instance PROPERTY edit — rotation, mirror, magnification, array
/// rows/cols/pitch, and cell-reference retarget (L3a §6 "cell reference (with a re-target button),
/// rotation, mirror, magnification, array fields"). Mirrors <c>ReplaceShapeCommand</c>'s "swap the
/// whole value at a fixed index" shape: a <see cref="LayoutInstance"/> is a small plain-field record
/// with no vertex list, so there is no promotion-rule reason to need this over per-field commands the
/// way <c>ReplaceShapeCommand</c> was — but ONE command for every property still means the Properties
/// Inspector's staged-field commit pattern (stage text, commit on LostFocus/Enter, build a full
/// before/after value) needs no per-field command type, matching every other typed-field editor in
/// this codebase.
/// </summary>
internal sealed class ReplaceInstanceCommand : IUiCommand
{
    private readonly LayoutView _view;
    private readonly int _index;
    private readonly LayoutInstance _before;
    private readonly LayoutInstance _after;

    public string Description => "Edit Instance";

    public ReplaceInstanceCommand(LayoutView view, int index, LayoutInstance before, LayoutInstance after)
    {
        _view = view;
        _index = index;
        _before = before;
        _after = after;
    }

    public void Execute()
    {
        _view.Instances[_index] = _after;
        _view.NotifyChanged(LayoutChangeInfo.InstancesOnly);
    }

    public void Undo()
    {
        _view.Instances[_index] = _before;
        _view.NotifyChanged(LayoutChangeInfo.InstancesOnly);
    }
}
