using CircuitRF.Design.Layout.PCells;
using static CircuitRF.Design.Layout.SchematicSync.SchematicLayoutPlanner;

namespace CircuitRF.Design.Layout.SchematicSync;

/// <summary>
/// One change a <see cref="SchematicLayoutPlan"/> makes to a layout's instances or shapes, in the order the plan
/// applies them. Indices are into the target's lists AS THEY ARE WHEN THE PLAN IS MADE: adds append and
/// replacements edit in place, so they stay valid until the one deletion, which comes after both.
/// </summary>
public abstract record SchematicLayoutEdit
{
    /// <summary>A new placement, appended, and the placement record that describes it.</summary>
    public sealed record AddInstance(LayoutInstance Instance, GeneratedPlacement Placement) : SchematicLayoutEdit;

    /// <summary>The instance at <paramref name="Index"/> replaced. <paramref name="Placement"/> is the record to
    /// write under <c>After.SchematicId</c> when the transform changed or the part was renamed, else null;
    /// <paramref name="RenamedFrom"/> is the old name whose record goes.</summary>
    public sealed record ReplaceInstance(int Index, LayoutInstance Before, LayoutInstance After,
                                         GeneratedPlacement? Placement, string? RenamedFrom) : SchematicLayoutEdit;

    /// <summary>The instances at these indices removed (a footprint set back to None, R-fp3-4b).</summary>
    public sealed record DeleteInstances(IReadOnlyList<int> Indices) : SchematicLayoutEdit;

    /// <summary>A shape appended — a ground via's tie to its pad.</summary>
    public sealed record AddShape(LayoutShape Shape) : SchematicLayoutEdit;
}

/// <summary>A write to <see cref="LayoutView.SchematicPCellSnapshots"/>: <paramref name="Parameters"/> null
/// removes the key.</summary>
public sealed record SchematicSnapshotChange(string SchematicId, Dictionary<string, PCellValue>? Parameters);

/// <summary>
/// What one Update Layout from Schematic run changes (brief-sdl-0 R-sdl0-1a) — returned by
/// <see cref="SchematicLayoutPlanner.Plan"/>, which mutates nothing it was handed.
///
/// <para><b>Two kinds of change, applied two ways, because they always have been.</b>
/// <see cref="Edits"/>, <see cref="Ground"/> and the placement records are the run's GEOMETRY and are undoable:
/// the GUI turns them into one command. The snapshot tables and the orientation links of parts that did not
/// otherwise change are BOOKKEEPING, written directly and not undone (R-L5-11's side table, and
/// R-L5-13/14's "what is reported is what happened") — <see cref="ApplyBookkeeping"/>.</para>
/// </summary>
public sealed class SchematicLayoutPlan
{
    public required IReadOnlyList<SchematicLayoutEdit> Edits { get; init; }
    public required GroundArtworkPlan Ground { get; init; }

    /// <summary>Where <see cref="GroundArtworkPlan.Remove"/>'s shapes are in the target, so the plan applies to a
    /// copy of it as well as to the target itself.</summary>
    public required IReadOnlyList<int> GroundShapeRemovals { get; init; }

    public required IReadOnlyList<SchematicSnapshotChange> SchematicSnapshotChanges { get; init; }
    public required IReadOnlyList<KeyValuePair<string, PCellSnapshot>> PCellSnapshotWrites { get; init; }

    /// <summary>A new baseline for an instance the run did not otherwise change, by its index.</summary>
    public required IReadOnlyList<KeyValuePair<int, OrientationLink>> OrientationLinkUpdates { get; init; }

    public required IReadOnlyList<ReportLine> Lines { get; init; }
    public required IReadOnlyList<string> NoLayoutWarnings { get; init; }
    public int AddedCount { get; init; }
    public int UpdatedCount { get; init; }
    public int UnchangedCount { get; init; }
    public int RemovedCount { get; init; }
    public int OverwrittenParameterCount { get; init; }
    public Bbox AddedRegion { get; init; }
    public int DeletedCount { get; init; }
    public bool OrientationLinksRecorded { get; init; }

    /// <summary>True when there is nothing undoable to apply — the GUI then has no command to run.</summary>
    public bool ChangesNothing => Edits.Count == 0 && Ground.IsEmpty;

    /// <summary>Writes the bookkeeping into <paramref name="view"/> — the target, or a copy of it.</summary>
    public void ApplyBookkeeping(LayoutView view)
    {
        foreach (var (cell, snapshot) in PCellSnapshotWrites) view.PCellSnapshots[cell] = snapshot;
        foreach (var change in SchematicSnapshotChanges)
        {
            if (change.Parameters is null) view.SchematicPCellSnapshots.Remove(change.SchematicId);
            else view.SchematicPCellSnapshots[change.SchematicId] = change.Parameters;
        }
        foreach (var (index, link) in OrientationLinkUpdates) view.Instances[index].OrientationLink = link;
    }

    /// <summary>
    /// The whole plan applied to <paramref name="view"/> in one go, with no undo — for a headless caller, and for
    /// the gate that proves the GUI's command chain applies exactly this. <paramref name="view"/> is the target
    /// the plan was made from or a copy of it; instances and shapes are cloned on the way in, so a plan applied
    /// to a copy leaves the copy sharing nothing with the target.
    /// </summary>
    public void ApplyTo(LayoutView view)
    {
        ApplyBookkeeping(view);
        foreach (var edit in Edits)
        {
            switch (edit)
            {
                case SchematicLayoutEdit.AddInstance add:
                    view.Instances.Add(LayoutGeometry.Clone(add.Instance));
                    RecordPlacement(view, add.Instance.SchematicId, add.Placement, null);
                    break;
                case SchematicLayoutEdit.ReplaceInstance rep:
                    view.Instances[rep.Index] = LayoutGeometry.Clone(rep.After);
                    if (rep.Placement is not null) RecordPlacement(view, rep.After.SchematicId, rep.Placement, rep.RenamedFrom);
                    break;
                case SchematicLayoutEdit.DeleteInstances del:
                    foreach (int i in del.Indices.Where(i => i >= 0 && i < view.Instances.Count).Distinct().OrderDescending())
                        view.Instances.RemoveAt(i);
                    break;
                case SchematicLayoutEdit.AddShape shape:
                    view.Shapes.Add(LayoutGeometry.Clone(shape.Shape));
                    break;
            }
        }

        if (Ground.IsEmpty) return;
        foreach (int i in GroundShapeRemovals.Distinct().OrderDescending()) view.Shapes.RemoveAt(i);
        foreach (var shape in Ground.Add) view.Shapes.Add(LayoutGeometry.Clone(shape));
        foreach (string k in Ground.KeysAdded) view.GroundViaKeys.Add(k);
        foreach (string k in Ground.KeysRemoved) view.GroundViaKeys.Remove(k);
    }

    /// <summary>The one way a placement record is written — shared with the GUI's undoable command.</summary>
    public static void RecordPlacement(LayoutView view, string? schematicId, GeneratedPlacement placement, string? renamedFrom)
    {
        if (renamedFrom is not null) view.GeneratedPlacements.Remove(renamedFrom);
        if (schematicId is { Length: > 0 }) view.GeneratedPlacements[schematicId] = placement;
    }
}
