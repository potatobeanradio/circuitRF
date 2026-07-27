using System.Collections.Generic;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Commands.Layout;

/// <summary>
/// ONE undoable command for a technology retarget (docs/sonnet-briefs/brief-L1g-technology-retarget.md
/// §4): rewrites <see cref="LayoutView.TechRef"/>, every affected shape's <see cref="LayerKey"/>, and
/// — only when the caller opted in — <see cref="LayoutView.DisplayUnit"/>/<see cref="LayoutView.SnapDbu"/>.
/// A half-undone retarget is worse than no undo at all, so every one of those fields is captured as a
/// single before/after snapshot and restored together. <see cref="LayoutView.DbuPerMicron"/> is never
/// touched — resolution is a property of the layout, not of the technology.
/// </summary>
internal sealed class RetargetTechnologyCommand : IUiCommand
{
    private readonly LayoutView _view;
    private readonly LayoutEditorViewModel _owner;
    private readonly LayoutEditorViewModel.RetargetState _before;
    private readonly LayoutEditorViewModel.RetargetState _after;
    private readonly IReadOnlyList<(int Index, LayerKey Before, LayerKey After)> _layerChanges;

    public string Description { get; }

    public RetargetTechnologyCommand(
        LayoutEditorViewModel owner,
        LayoutEditorViewModel.RetargetState before,
        LayoutEditorViewModel.RetargetState after,
        IReadOnlyList<(int Index, LayerKey Before, LayerKey After)> layerChanges,
        string description)
    {
        _owner = owner;
        _view = owner.Model;
        _before = before;
        _after = after;
        _layerChanges = layerChanges;
        Description = description;
    }

    public void Execute()
    {
        foreach (var (index, _, after) in _layerChanges)
            _view.Shapes[index].Layer = after;
        _owner.ApplyRetargetState(_after);
        _view.NotifyChanged();
    }

    public void Undo()
    {
        foreach (var (index, before, _) in _layerChanges)
            _view.Shapes[index].Layer = before;
        _owner.ApplyRetargetState(_before);
        _view.NotifyChanged();
    }
}
