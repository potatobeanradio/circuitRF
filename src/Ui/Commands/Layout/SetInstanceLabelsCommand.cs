using System.Collections.Generic;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Commands.Layout;

/// <summary>
/// One undo entry for every edit to a placement's DESIGNATOR — a drag, a Reset to auto, a
/// show/hide (brief-footprint-4b R-fp4b-6a/b/d).
///
/// <para><b>An old-values/new-values snapshot pair over a SET of instances</b>, which is the shape
/// <c>MoveLabelsCommand</c> already uses on the schematic side for the same gesture — deliberately,
/// so a user who has learned one editor's label drag has learned the other's, and so the undo entry
/// is one command like every other instance edit rather than N. The set is what makes a multi-select
/// Reset or a multi-select hide a single entry too.</para>
///
/// <para>Stores whole <see cref="LayoutInstance"/> values rather than the five label fields, exactly
/// as <c>ReplaceInstanceCommand</c> beside it does: a <see cref="LayoutInstance"/> is a small
/// plain-field record, and a field-list command here would be a second place the designator's fields
/// are enumerated — which is how one of them gets forgotten.</para>
/// </summary>
internal sealed class SetInstanceLabelsCommand : IUiCommand
{
    private readonly LayoutView _view;
    private readonly IReadOnlyList<(int Index, LayoutInstance Before, LayoutInstance After)> _edits;

    public string Description { get; }

    public SetInstanceLabelsCommand(
        LayoutView view, IReadOnlyList<(int, LayoutInstance, LayoutInstance)> edits, string description)
    {
        _view = view;
        _edits = edits;
        Description = description;
    }

    public void Execute()
    {
        foreach (var (i, _, after) in _edits) _view.Instances[i] = after;
        _view.NotifyChanged(LayoutChangeInfo.InstancesOnly);
    }

    public void Undo()
    {
        foreach (var (i, before, _) in _edits) _view.Instances[i] = before;
        _view.NotifyChanged(LayoutChangeInfo.InstancesOnly);
    }
}
