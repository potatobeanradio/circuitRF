using System.Collections.Generic;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Commands.Layout;

/// <summary>
/// Several instance replacements as ONE undo entry and ONE change notification.
/// </summary>
/// <remarks>
/// <see cref="ReplaceInstanceCommand"/> notifies per instance, and a composite of thirty of them is
/// thirty <c>Changed</c> events — each of which a watching railRF window answers by re-flattening the
/// whole board. railRF's Turn gesture (field report, 2026-09-22) turns every part the copper reads as
/// placed at 180° in one go, so it takes this instead.
/// </remarks>
internal sealed class ReplaceInstancesCommand(
    LayoutView view,
    IReadOnlyList<(int Index, LayoutInstance Before, LayoutInstance After)> edits,
    string description) : IUiCommand
{
    public string Description => description;

    public void Execute()
    {
        foreach (var (index, _, after) in edits) view.Instances[index] = after;
        view.NotifyChanged(LayoutChangeInfo.InstancesOnly);
    }

    public void Undo()
    {
        for (int i = edits.Count - 1; i >= 0; i--) view.Instances[edits[i].Index] = edits[i].Before;
        view.NotifyChanged(LayoutChangeInfo.InstancesOnly);
    }
}
