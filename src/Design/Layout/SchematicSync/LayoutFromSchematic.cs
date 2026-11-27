// Framework-free. No Avalonia, no SkiaSharp.
//
// Design ▸ Update Layout from Schematic with no window: the cell's layout made (when it has none yet) and brought up to
// date with its schematic by SchematicLayoutPlanner — the planner the menu item runs — then saved. It is what `circuitrf
// new layout` and the MCP `create what=layout` call. The GUI's command wraps the same plan in undoable commands on an
// open document; this applies it to the file (SchematicLayoutPlan.ApplyTo, the gate's own headless applier).

using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Workspace;

namespace CircuitRF.Design.Layout.SchematicSync;

/// <summary>Update Layout from Schematic, headless.</summary>
public static class LayoutFromSchematic
{
    /// <summary>What a run did.</summary>
    /// <param name="LayoutPath">The layout written.</param>
    /// <param name="Created">True when the layout did not exist and was made.</param>
    /// <param name="Lines">The planner's report and the ground artwork's, as the Messages panel shows them.</param>
    /// <param name="EmSetups">EM setups written or updated (with <c>emSetup</c>).</param>
    /// <param name="Refusal">Why nothing was written; null on success.</param>
    public sealed record Outcome(
        string LayoutPath, bool Created, IReadOnlyList<SchematicLayoutPlanner.ReportLine> Lines,
        int Added, int Updated, int Unchanged, int Removed, IReadOnlyList<string> EmSetups, string? Refusal);

    /// <summary>
    /// Runs Update Layout from Schematic on the schematic at <paramref name="schematicPath"/>, writing its cell's layout
    /// (or, for a schematic outside a cell's <c>schematic/</c> folder, the <c>.clay</c> beside it).
    /// <paramref name="connectParts"/> is Settings ▸ Connect parts on Update Layout; <paramref name="emSetup"/> is Settings ▸
    /// Create or update EM setup.
    /// </summary>
    public static Outcome Run(string schematicPath, bool connectParts = true, bool emSetup = false)
    {
        string full = Path.GetFullPath(schematicPath);
        var sibling = CellFolder.SiblingView(full, ViewType.Schematic, ViewType.Layout);
        string target = sibling.TargetPath;
        Outcome Refused(string why) => new(target, false, [], 0, 0, 0, 0, [], why);

        string? cws = WorkspaceRootFinder.FindAncestorCws(Path.GetDirectoryName(full)!);
        if (cws is null) return Refused("the schematic is in no workspace — generated cells need a workspace to live in.");
        string workspaceDir = Path.GetDirectoryName(Path.GetFullPath(cws))!;

        var (model, _, _) = SchematicPersistence.LoadFromFile(full);
        string schematicDir = Path.GetDirectoryName(full)!;
        model.SchematicDirectory = schematicDir;

        var cache = new TechnologyCache();
        bool created = !File.Exists(target);
        if (created)
        {
            Directory.CreateDirectory(sibling.TargetDir);
            // As the menu item: a schematic that names its own technology gets its layout on that one, not the default.
            string? techRef = model.TechRef is { Length: > 0 }
                              && SchematicTechnology.Resolve(model) is { Error: null, Source: SchematicTechSource.DocumentRef, Path: { } schTech }
                ? SchematicTechnology.StoredRef(schTech, sibling.TargetDir) : null;
            var seedTech = TechnologyResolver.ResolveForDocument(techRef, target, cws, cache).Item1.Tech;
            LayoutPersistence.SaveToFile(target, new LayoutView
            {
                TechRef      = techRef,
                DbuPerMicron = LayoutUnits.DefaultDbuPerMicron,
                DisplayUnit  = seedTech?.DefaultDisplayUnit ?? LayoutUnit.Um,
                SnapDbu      = seedTech?.DefaultSnapDbu ?? 1000,
                AngleMode    = AngleMode.AnyAngle,
            });
        }

        var layout = LayoutPersistence.LoadFromFile(target);
        var (tech, _) = TechnologyResolver.ResolveForDocument(layout.TechRef, target, cws, cache);
        if (tech.Tech is null)
            return Refused($"the layout resolves no technology: {string.Join(" ", tech.Diagnostics)}");

        double margin = GroundPourPlanner.DefaultMarginHeights;
        try
        {
            if (WorkspacePersistence.LoadFromFile(cws).GroundPourMarginHeights is { } h && h >= 0 && double.IsFinite(h)) margin = h;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }

        using var designed = CircuitRF.Design.PCellDesigner.DesignedPCellResolver.Register(workspaceDir); // brief-pcd-0 §5 seam 12
        var plan = SchematicLayoutPlanner.Plan(model, layout, schematicDir, workspaceDir, sibling.TargetDir, tech.Tech,
                                               tech.ResolvedPath, DiskCellResolver.Instance, targetLayoutPath: target,
                                               pourMarginHeights: margin, connectParts: connectParts);
        plan.ApplyTo(layout);
        LayoutPersistence.SaveToFile(target, layout);

        var lines = new List<SchematicLayoutPlanner.ReportLine>(plan.Lines);
        lines.AddRange(plan.NoLayoutWarnings.Select(w => new SchematicLayoutPlanner.ReportLine("", w, SchematicLayoutPlanner.ReportSeverity.Warning)));
        lines.AddRange(SchematicLayoutPlanner.GroundArtworkReport(plan.Ground, layout, layout.DisplayUnit));

        var setups = new List<string>();
        if (emSetup)
        {
            string cellName = sibling.CellDir is not null ? Path.GetFileName(sibling.CellDir) : Path.GetFileNameWithoutExtension(full);
            var em = EmSetupFromSchematic.Run(model, cws, target, cellName);
            setups.AddRange(em.Written);
            lines.AddRange(em.Lines);
        }

        return new Outcome(target, created, lines, plan.AddedCount, plan.UpdatedCount, plan.UnchangedCount,
                           plan.RemovedCount, setups, null);
    }
}
