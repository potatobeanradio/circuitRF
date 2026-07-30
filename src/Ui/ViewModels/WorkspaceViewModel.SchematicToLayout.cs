using System;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels.Dock;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// docs/sonnet-briefs/brief-L5-schematic-to-layout.md §2.1/§3A/§3B — the two "Design" menu commands:
/// **Update Layout from Schematic** (§2, the schematic→layout generator already wired) and
/// **Update Schematic from Layout** (§3A, its mechanical inverse). Both are symmetric in every way
/// the brief asks for: same file-targeting shape (create-if-absent, open, focus, leave a differently-
/// named primary alone and report it — R-L5-17/21), same change-report shape (capped at 20 instances,
/// overwritten-count always visible, silent when nothing changed — R-L5-13/14/22), one undoable action
/// each (R-L5-12/22), and — the one guardrail that is NOT negotiable — neither ever runs except from
/// an explicit user invocation (R-L5-23): no save hook, no open hook, no document-activation hook
/// anywhere near either method.
/// </summary>
public partial class WorkspaceViewModel
{
    // ── Update Layout from Schematic (§2/§2.1) ───────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(IsSchematicDocumentActive))]
    private void UpdateLayoutFromSchematic()
    {
        if (ResolveActiveDocumentForCommands() is not SchematicDocument doc) return;

        if (doc.IsScratch)
        {
            Messages.Error("Update Layout from Schematic: save the schematic first — a scratch schematic has no cell to write into.");
            return;
        }

        string schematicPath = doc.FilePath!;
        string schematicDir  = Path.GetDirectoryName(schematicPath)!;
        string cellDir       = Path.GetDirectoryName(schematicDir)!;
        string schematicName = Path.GetFileNameWithoutExtension(schematicPath);
        string layoutDir     = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        string targetPath    = Path.Combine(layoutDir, schematicName + CellFolder.ViewExtension(ViewType.Layout));

        // R-L5-17: capture primacy BEFORE touching anything.
        var primaryBefore = CellFolder.ResolvePrimary(cellDir, ViewType.Layout);
        bool hadNoRealPrimary = primaryBefore.State is not (PrimaryState.SoleFile or PrimaryState.NamedPresent);
        string? primaryBeforeName = primaryBefore.ResolvedName;

        bool createdNewFile = !File.Exists(targetPath);
        if (createdNewFile)
        {
            Directory.CreateDirectory(layoutDir);
            var techRes = ResolveTechFor(null, targetPath);
            var seedModel = new LayoutView
            {
                DbuPerMicron = LayoutUnits.DefaultDbuPerMicron,
                DisplayUnit  = techRes.Tech?.DefaultDisplayUnit ?? LayoutUnit.Um,
                SnapDbu      = techRes.Tech?.DefaultSnapDbu ?? 1000,
                AngleMode    = AngleMode.AnyAngle,
            };
            LayoutPersistence.SaveToFile(targetPath, seedModel);
            _factory.ProjectTreeTool?.Refresh();
        }

        // R-L5-16: open it and make it the active document — no prompt, whether created or pre-existing.
        OpenOrActivateLayout(targetPath);
        var layoutVm = GetOrCreateLayoutSession(targetPath);

        if (layoutVm.WorkspaceRootDir is not { Length: > 0 } workspaceRoot)
        {
            Messages.Error("Update Layout from Schematic: no workspace is open — generated PCell cells need a workspace to live in.");
            return;
        }

        var result = SchematicToLayoutGenerator.Run(
            doc.ViewModel.EditModel, layoutVm.Model, schematicDir, workspaceRoot, layoutDir,
            layoutVm.Technology, layoutVm.ResolvedTechPath, this);

        if (result.Command is not null)
            layoutVm.Execute(result.Command);

        ReportGenerationResult(result.Command, result.Lines, result.NoLayoutWarnings,
            result.AddedCount, result.UpdatedCount, result.UnchangedCount, result.RemovedCount,
            result.OverwrittenParameterCount, "Update Layout from Schematic");

        // R-L5-17: primacy — new file with no prior real primary becomes primary automatically
        // (CellFolder's own SoleFile branch handles the common case for free); only the ambiguous
        // multi-file/no-named-primary case needs an explicit .ccell write. A differently-named primary
        // is left untouched and reported.
        if (createdNewFile)
        {
            if (hadNoRealPrimary)
            {
                var afterCreate = CellFolder.ResolvePrimary(cellDir, ViewType.Layout);
                if (afterCreate.State is not (PrimaryState.SoleFile or PrimaryState.NamedPresent))
                    SetPrimaryLayout(cellDir, Path.GetFileName(targetPath));
            }
            else
            {
                Messages.Warning($"'{primaryBeforeName}' remains this cell's primary layout — '{Path.GetFileName(targetPath)}' was written but not made primary.");
            }
        }
    }

    // ── Update Schematic from Layout (§3A) ───────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(IsLayoutDocumentActive))]
    private void UpdateSchematicFromLayout()
    {
        if (ResolveActiveDocumentForCommands() is not LayoutDocument doc) return;

        if (doc.FilePath is not { Length: > 0 } layoutPath)
        {
            Messages.Error("Update Schematic from Layout: save the layout first — a scratch layout has no cell to write into.");
            return;
        }

        string layoutDir     = Path.GetDirectoryName(layoutPath)!;
        string cellDir       = Path.GetDirectoryName(layoutDir)!;
        string layoutName    = Path.GetFileNameWithoutExtension(layoutPath);
        string schematicDir  = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        string targetPath    = Path.Combine(schematicDir, layoutName + CellFolder.ViewExtension(ViewType.Schematic));

        var primaryBefore = CellFolder.ResolvePrimary(cellDir, ViewType.Schematic);
        bool hadNoRealPrimary = primaryBefore.State is not (PrimaryState.SoleFile or PrimaryState.NamedPresent);
        string? primaryBeforeName = primaryBefore.ResolvedName;

        bool createdNewFile = !File.Exists(targetPath);
        if (createdNewFile)
        {
            Directory.CreateDirectory(schematicDir);
            SchematicPersistence.SaveToFile(targetPath, new SchematicEditModel(), cellName: Path.GetFileName(cellDir));
            _factory.ProjectTreeTool?.Refresh();
        }

        OpenOrActivateSchematic(targetPath);
        var schematicVm = GetOrCreateSession(targetPath);

        var layoutVm = GetOrCreateLayoutSession(layoutPath);
        var result = LayoutToSchematicGenerator.Run(layoutVm.Model, schematicVm.EditModel, layoutDir, layoutVm.Technology);

        if (result.Command is not null)
            schematicVm.Execute(result.Command);

        ReportGenerationResult(result.Command, result.Lines, [],
            result.CreatedCount, result.UpdatedCount, result.UnchangedCount, 0,
            result.OverwrittenParameterCount, "Update Schematic from Layout", overwrittenNoun: "schematic edits");

        if (createdNewFile)
        {
            if (hadNoRealPrimary)
            {
                var afterCreate = CellFolder.ResolvePrimary(cellDir, ViewType.Schematic);
                if (afterCreate.State is not (PrimaryState.SoleFile or PrimaryState.NamedPresent))
                    SetPrimarySchematic(cellDir, Path.GetFileName(targetPath));
            }
            else
            {
                Messages.Warning($"'{primaryBeforeName}' remains this cell's primary schematic — '{Path.GetFileName(targetPath)}' was written but not made primary.");
            }
        }
    }

    // ── Shared file-primacy helpers (mirrors MakePrimary's own .ccell write) ────────────────────────

    private void SetPrimaryLayout(string cellDir, string fileName) => SetPrimary(cellDir, ccell => ccell.PrimaryLayout = fileName);
    private void SetPrimarySchematic(string cellDir, string fileName) => SetPrimary(cellDir, ccell => ccell.PrimarySchematic = fileName);

    private void SetPrimary(string cellDir, Action<CcellFile> apply)
    {
        string ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        if (!File.Exists(ccellPath)) return;
        try
        {
            var ccell = CellPersistence.LoadFromFile(ccellPath);
            apply(ccell);
            CellPersistence.SaveToFile(ccellPath, ccell);
            _factory.ProjectTreeTool?.Refresh();
        }
        catch (Exception ex)
        {
            Messages.Error($"Could not set primary view: {ex.Message}");
        }
    }

    // ── Shared change report (R-L5-13/14/22) ─────────────────────────────────────────────────────

    /// <summary>
    /// R-L5-13: one line per INSTANCE (grouped — an instance's several changed parameters share one
    /// group), capped at <paramref name="cap"/> instances, with a trailing summary that keeps the
    /// overwritten-parameter count visible even when details are truncated. R-L5-14: posts NOTHING
    /// (not even a summary) when <paramref name="command"/> is null AND there is nothing else to say.
    /// Shared verbatim by both directions (R-L5-22: "same cap, same silence, same single undoable
    /// action").
    /// </summary>
    private void ReportGenerationResult(
        Commands.IUiCommand? command,
        IReadOnlyList<SchematicToLayoutGenerator.ReportLine> lines,
        IReadOnlyList<string> extraWarnings,
        int addedOrCreated, int updated, int unchanged, int removed, int overwrittenParamCount,
        string commandLabel, int cap = 20, string overwrittenNoun = "layout edits")
    {
        foreach (var w in extraWarnings) Messages.Warning(w);

        if (command is null)
            return; // R-L5-14 — nothing changed, say nothing (the extra warnings above, if any, are a
                     // separate, persistent concern and are not silenced by "nothing changed this run").

        var byInstance = lines
            .GroupBy(l => l.InstanceName)
            .ToList();

        int shown = Math.Min(cap, byInstance.Count);
        for (int i = 0; i < shown; i++)
            foreach (var line in byInstance[i])
                Messages.Post(line.Severity == SchematicToLayoutGenerator.ReportSeverity.Warning ? MessageLevel.Warning : MessageLevel.Info, line.Text);

        if (byInstance.Count > cap)
        {
            int remaining = byInstance.Count - cap;
            Messages.Info($"…and {remaining} more instance(s) updated. {overwrittenParamCount} had {overwrittenNoun} overwritten.");
        }

        string removedSuffix = removed > 0 ? $", {removed} no longer in the schematic" : "";
        Messages.Success($"{commandLabel}: {addedOrCreated} added, {updated} updated, {unchanged} unchanged{removedSuffix}.");
    }
}
