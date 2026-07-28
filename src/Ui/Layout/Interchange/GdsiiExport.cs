// GDSII export orchestrator (docs/sonnet-briefs/brief-L4a-gdsii-interchange.md §2.3, R-L4a-3). Walks
// a design's cell hierarchy from a root cell folder into InterchangeStructures, then runs the SAME
// GdsiiWriter.Write path (a dry run into Stream.Null) to produce the pre-flight fidelity plan the
// export dialog shows BEFORE any bytes are written — the preview can never disagree with the real
// write because it IS the real write, just discarded.

using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Layout.Interchange;

public static class GdsiiExport
{
    /// <summary>What the export dialog shows before writing (R-L4a-3): curve/hole/bitmap counts,
    /// the structure-name mapping, any coordinate overflow that must block the write entirely, and
    /// any instance whose <c>CellRef</c> does not resolve within this design (it still exports — as a
    /// dangling reference to a structure name absent from the file, which every GDSII viewer shows as
    /// an unresolved-reference placeholder — but never silently).</summary>
    public sealed record ExportPlan(
        int CurvedShapesFlattened,
        int HolesKeyholed,
        int BitmapsSkipped,
        IReadOnlyList<string> CoordinateOverflowOffenders,
        IReadOnlyList<string> UnresolvedInstanceReferences,
        IReadOnlyDictionary<string, string> StructureNameByCellName,
        IReadOnlyList<InterchangeStructure> Structures,
        GdsiiUnits Units,
        Technology? Tech)
    {
        public bool CanWrite => CoordinateOverflowOffenders.Count == 0;
    }

    /// <summary>Walks <paramref name="rootCellDir"/>'s hierarchy and computes the fidelity plan — no
    /// bytes written yet. <paramref name="dbuPerMicron"/> is the resolution every reachable cell's
    /// coordinates are assumed to already share (this codebase's own per-`.clay` <c>DbuPerMicron</c>
    /// convention; a design mixing resolutions across cells is a stated, narrower scope this brief
    /// does not resolve).</summary>
    public static ExportPlan Analyze(string rootCellDir, Technology? tech, int dbuPerMicron)
    {
        var (structures, nameByCellName, unresolvedRefs) = CollectHierarchy(rootCellDir);
        var units = new GdsiiUnits(1e-6, 1e-6 / dbuPerMicron);

        try
        {
            var summary = GdsiiWriter.Write(Stream.Null, structures, units, tech);
            return new ExportPlan(
                summary.CurvedShapesFlattened, summary.HolesKeyholed, summary.BitmapsSkipped,
                [], unresolvedRefs, nameByCellName, structures, units, tech);
        }
        catch (GdsiiExportException ex)
        {
            return new ExportPlan(0, 0, 0, ex.Offenders, unresolvedRefs, nameByCellName, structures, units, tech);
        }
    }

    /// <summary>Writes a previously-analyzed plan. Throws <see cref="GdsiiExportException"/> (never
    /// writes a partial file) if <see cref="ExportPlan.CanWrite"/> is false — callers should check
    /// that first and block the write in the UI rather than relying on this exception alone.</summary>
    public static void Write(string filePath, ExportPlan plan)
    {
        if (!plan.CanWrite) throw new GdsiiExportException(plan.CoordinateOverflowOffenders);
        using var stream = File.Create(filePath);
        GdsiiWriter.Write(stream, plan.Structures, plan.Units, plan.Tech);
    }

    private static (List<InterchangeStructure> Structures, IReadOnlyDictionary<string, string> NameByCellName,
        IReadOnlyList<string> UnresolvedRefs) CollectHierarchy(string rootCellDir)
    {
        var rootAbs = Path.GetFullPath(rootCellDir);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootAbs };
        var order = new List<string> { rootAbs };
        var viewByDir = new Dictionary<string, LayoutView>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(rootAbs);

        while (queue.Count > 0)
        {
            var cellDir = queue.Dequeue();
            var view = LoadPrimaryLayout(cellDir);
            viewByDir[cellDir] = view;

            var layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
            foreach (var inst in view.Instances)
            {
                string targetDir;
                try { targetDir = Path.GetFullPath(Path.Combine(layoutDir, inst.CellRef)); }
                catch { continue; }
                if (!Directory.Exists(targetDir)) continue; // broken reference — nothing to enqueue
                if (visited.Add(targetDir))
                {
                    order.Add(targetDir);
                    queue.Enqueue(targetDir);
                }
            }
        }

        var cellNames = order.Select(d => Path.GetFileName(d)).ToList();
        var structureNameByCellName = GdsiiStructureNaming.MangleForExport(cellNames);

        var dirToStructureName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in order)
            dirToStructureName[dir] = structureNameByCellName[Path.GetFileName(dir)];

        var unresolvedRefs = new List<string>();
        var structures = new List<InterchangeStructure>(order.Count);
        foreach (var dir in order)
        {
            var view = viewByDir[dir];
            var cellName = Path.GetFileName(dir);
            var layoutDir = CellFolder.SubFolderPath(dir, ViewType.Layout);
            var instances = view.Instances.Select(inst =>
            {
                string targetDir;
                try { targetDir = Path.GetFullPath(Path.Combine(layoutDir, inst.CellRef)); }
                catch { targetDir = ""; }
                if (!dirToStructureName.TryGetValue(targetDir, out var targetStructureName))
                {
                    // Genuinely unresolved within this design — exports as a dangling reference
                    // (every GDSII viewer shows this as an unresolved-reference placeholder), but
                    // reported here so the export dialog can surface it BEFORE writing rather than
                    // leaving the user to discover it as a mysterious placeholder in a third-party tool.
                    targetStructureName = inst.CellRef;
                    unresolvedRefs.Add($"{cellName}: instance referencing \"{inst.CellRef}\" does not resolve.");
                }
                return new LayoutInstance
                {
                    CellRef = targetStructureName,
                    X = inst.X, Y = inst.Y, Rot = inst.Rot, MirrorX = inst.MirrorX, Mag = inst.Mag,
                    Rows = inst.Rows, Cols = inst.Cols, PitchX = inst.PitchX, PitchY = inst.PitchY,
                };
            }).ToList();

            structures.Add(new InterchangeStructure(dirToStructureName[dir], [.. view.Shapes], instances));
        }

        return (structures, structureNameByCellName, unresolvedRefs);
    }

    private static LayoutView LoadPrimaryLayout(string cellDir)
    {
        var res = CellFolder.ResolvePrimary(cellDir, ViewType.Layout);
        if (res.State is not (PrimaryState.SoleFile or PrimaryState.NamedPresent) || res.ResolvedName is null)
            return new LayoutView(); // no resolvable primary — an empty structure, not a crash

        var layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        try
        {
            return LayoutPersistence.LoadFromFile(Path.Combine(layoutDir, res.ResolvedName));
        }
        catch
        {
            return new LayoutView();
        }
    }
}
