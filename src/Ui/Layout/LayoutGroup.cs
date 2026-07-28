// Group into Cell (brief-L3c-flatten-and-group.md §4) — framework-free geometry math for turning a
// selection into a new cell's contents. File I/O (creating the cell folder, writing the .clay/.ccell,
// picking the replacement instance's CellRef) is the VM's own concern — this file only computes WHAT
// the new cell's shapes/instances become, never touches a filesystem path.

namespace CircuitRF.Ui.Layout;

public static class LayoutGroup
{
    /// <summary>The new cell's own contents (shapes and instances, already shifted into the new
    /// cell's local frame) plus the origin point (in the PARENT's frame) the replacement instance must
    /// be placed at so nothing visibly moves (R-L3c-5).</summary>
    public sealed record GroupContents(
        IReadOnlyList<LayoutShape> Shapes,
        IReadOnlyList<LayoutInstance> Instances,
        long OriginX, long OriginY);

    /// <summary>
    /// Computes the new cell's contents: the selection's own combined bounding-box MINIMUM becomes the
    /// new cell's local origin (§4's own suggestion — "predictable and easy to reason about"), so every
    /// shape/instance is translated by <c>(-OriginX, -OriginY)</c> into the new cell's frame. Placing a
    /// fresh instance of the resulting cell at <c>(OriginX, OriginY)</c> in the PARENT's frame (R0, no
    /// mirror, Mag 1) then reproduces the exact original geometry — R-L3c-5's pixel-identity invariant,
    /// by construction rather than by a later corrective step.
    /// <br/><br/>
    /// A selected INSTANCE moves into the new cell as an instance, unchanged apart from the translate
    /// (grouping does not flatten — §4's own explicit rule); <paramref name="parentBaseDir"/> is needed
    /// only to compute each selected instance's own bbox (<see cref="CellHierarchy.InstanceBbox"/>) for
    /// the combined origin — it plays no other role, since an instance's <c>CellRef</c> is untouched by
    /// this move (it already resolves correctly from wherever the new cell's own <c>.clay</c> ends up,
    /// because <c>CellRef</c> resolution is baseDir-relative and the VM rebases it exactly like a paste
    /// would if the new cell lives somewhere the old relative path no longer reaches — see the VM's own
    /// caller for that rebasing step).
    /// <br/><br/>
    /// Returns <c>null</c> when the selection is entirely empty (no shapes, no instances) — there is no
    /// meaningful bbox to anchor against.
    /// </summary>
    public static GroupContents? BuildContents(
        IReadOnlyList<LayoutShape> shapes, IReadOnlyList<LayoutInstance> instances, string parentBaseDir)
    {
        if (shapes.Count == 0 && instances.Count == 0) return null;

        var bbox = Bbox.Empty;
        foreach (var s in shapes) bbox = bbox.Union(LayoutGeometry.BboxOf(s));
        foreach (var i in instances) bbox = bbox.Union(CellHierarchy.InstanceBbox(i, parentBaseDir));
        if (bbox.IsEmpty) return null;

        long originX = bbox.MinX, originY = bbox.MinY;
        var translate = new LayoutCoordinateTransform((x, y) => (x - originX, y - originY), m => m);

        var newShapes = new List<LayoutShape>(shapes.Count);
        foreach (var s in shapes)
        {
            var clone = LayoutGeometry.Clone(s);
            LayoutCoordinateWalk.Transform(clone, translate);
            newShapes.Add(clone);
        }

        var newInstances = new List<LayoutInstance>(instances.Count);
        foreach (var i in instances)
        {
            var clone = LayoutGeometry.Clone(i);
            LayoutGeometry.TranslateBy(clone, -originX, -originY);
            newInstances.Add(clone);
        }

        return new GroupContents(newShapes, newInstances, originX, originY);
    }
}
