// Owner: PCD-13 (brief-pcd-13 D8). Framework-free.
//
// A designed cell whose operation history changed its copper: merged or cut copper is no longer the parts' bodies, so a
// part-by-part comparison would read it as shorts. Such a cell is compared by which ports each piece of its final copper
// joins, against the same reading of the cell planned without its history (which the planner drew from the schematic).

using CircuitRF.Design.Layout.SchematicSync;
using CircuitRF.Design.PCellDesigner;
using CircuitRF.Design.PCellDesigner.Operations;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Workspace;
using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Layout.Lvs;

internal static class LvsOperatedCell
{
    /// <summary>The comparison of <paramref name="cellDir"/> by port connectivity, or null when it is not a designed cell
    /// whose saved layout holds copper its operations drew, or which places a tool (every other cell is compared part by
    /// part).</summary>
    public static LvsRunResult? Run(string cellDir, SchematicEditModel model, LayoutView view, string clayPath, Technology? tech)
    {
        if (tech is null || !(view.Shapes.Any(s => s.Generated == PCellOperations.GeneratedTag) || model.Components.Any(PCellOperations.IsTool))
            || !PCellDesign.IsDesigned(cellDir)
            || WorkspaceRootFinder.FindAncestorCws(cellDir) is not { } cws) return null;

        var plain = SchematicPersistence.Deserialize(SchematicPersistence.Serialize(model), model.SchematicDirectory).model;
        plain.PCellOperations = null;
        var planned = DesignedCellLayout.At(cellDir, new Dictionary<string, Core.Expressions.Value>(), Path.GetDirectoryName(cws)!,
                                            tech, null, plain);
        if (planned.Layout is null) return null;

        string layoutDir = Path.GetDirectoryName(Path.GetFullPath(clayPath))!;
        string want = PCellOperationsEngine.PortsJoined(planned.Layout, layoutDir, tech);
        string have = PCellOperationsEngine.PortsJoined(view, layoutDir, tech);
        string cell = Path.GetFileName(Path.TrimEndingDirectorySeparator(cellDir));
        var notes = new List<Diagnostic>
        {
            Diagnostic.Create("lvs.scope.pcell-operations", DiagnosticSeverity.Info,
                "'{cell}' is a designed cell whose operations change its copper, so it is compared by which ports each piece of " +
                "copper joins ({joined}), not part by part.", ("cell", cell), ("joined", have.Length > 0 ? have : "one port or none")),
        };
        if (want != have)
            notes.Add(Diagnostic.Create(Groups(have) < Groups(want) ? "lvs.net.short" : "lvs.net.open", DiagnosticSeverity.Error,
                "In '{cell}' the copper joins its ports as {have}; its schematic joins them as {want}.",
                ("cell", cell), ("have", have), ("want", want)));

        var empty = LvsNetlist.Nothing([]);
        var log = new ReductionLog(false, 0, 0, 0, []);
        var comparison = new LvsComparison([], [], [], [], [], 0, 0, 0);
        var findings = LvsReport.Build(comparison, empty, empty, LvsGeometry.None, notes, LvsCounts.Nothing, tech.Name, ReductionMode.On);
        return new LvsRunResult(findings, comparison, empty, empty, LvsGeometry.None, log, log, LvsCounts.Nothing, tech.Name);
    }

    private static int Groups(string joined) => joined.Length == 0 ? 0 : joined.Split(", ").Length;
}
