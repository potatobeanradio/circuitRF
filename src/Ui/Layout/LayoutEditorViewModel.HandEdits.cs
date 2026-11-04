using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Commands.Layout;

namespace CircuitRF.Ui.Layout;

// brief-sdl-4 R-sdl4-2: what the layout editor offers for re-runs that respect hand edits — the lock, and the
// selection Re-snap acts on. Nothing here decides a position: Lock is a flag the planner reads, and Re-snap and
// Restore Removed Parts are Update Layout from Schematic with an argument (WorkspaceViewModel.LayoutHandEdits).
public partial class LayoutEditorViewModel
{
    /// <summary>Some selected placement is not locked — Lock Position has something to do.</summary>
    public bool CanLockSelection => SelectedInstanceIndices.Any(i => i < Model.Instances.Count && !Model.Instances[i].Locked);

    /// <summary>Some selected placement is locked — Unlock Position has something to do.</summary>
    public bool CanUnlockSelection => SelectedInstanceIndices.Any(i => i < Model.Instances.Count && Model.Instances[i].Locked);

    /// <summary>The schematic ids of the selected placements Update Layout from Schematic generated — what Re-snap acts
    /// on. Empty when none is linked.</summary>
    public IReadOnlySet<string> SelectedSchematicIds =>
        SelectedInstanceIndices.Where(i => i < Model.Instances.Count)
                               .Select(i => Model.Instances[i].SchematicId)
                               .OfType<string>().Where(s => s.Length > 0)
                               .ToHashSet(System.StringComparer.Ordinal);

    /// <summary>Lock Position / Unlock Position, and the Properties panel's Locked box: the whole selection, one undo
    /// step. A locked part never moves during Update Layout from Schematic and holds its side of a change.</summary>
    public void SetSelectedInstancesLocked(bool locked)
    {
        var edits = new List<(int, LayoutInstance, LayoutInstance)>();
        foreach (int i in SelectedInstanceIndices)
        {
            if (i < 0 || i >= Model.Instances.Count || Model.Instances[i].Locked == locked) continue;
            var after = LayoutGeometry.Clone(Model.Instances[i]);
            after.Locked = locked;
            edits.Add((i, Model.Instances[i], after));
        }
        if (edits.Count == 0) return;
        Execute(new SetInstanceLabelsCommand(Model, edits, locked ? "Lock Position" : "Unlock Position"));
        RebuildOverlay();
    }
}
