namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Framework-free executor for the file-IO portion of a <see cref="SavePlan"/>.
/// Creates the workspace folder + .cws, cell folders + .ccell files, saves .csch
/// files, and transitions each scratch document to materialized.
/// The caller (WorkspaceViewModel) handles Dock refresh, _openDocsByPath housekeeping,
/// and message reporting after this returns.
/// </summary>
public static class SavePlanExecutor
{
    /// <summary>
    /// Executes the file-IO steps of <paramref name="plan"/> and returns the full list
    /// of files written (workspace .cws, each .ccell, each .csch).
    /// </summary>
    /// <param name="plan">Confirmed save plan from the plan dialog.</param>
    /// <param name="existingWorkspaceDir">
    /// Absolute path to the already-open workspace folder, or null when the plan
    /// contains a workspace creation step.
    /// </param>
    /// <returns>Absolute paths of every file written, in creation order.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the plan has no workspace step and existingWorkspaceDir is null.
    /// </exception>
    public static IReadOnlyList<string> ExecuteFileOps(
        SavePlan plan,
        string?  existingWorkspaceDir)
    {
        var written = new List<string>();

        // ── Create workspace (if needed) ──────────────────────────────────────
        string workspaceDir;
        if (plan.WorkspaceStep is { } wsStep)
        {
            workspaceDir = Path.Combine(wsStep.ParentDir, wsStep.Name);
            Directory.CreateDirectory(workspaceDir);
            var cwsPath = Path.Combine(workspaceDir, ".cws");
            WorkspacePersistence.SaveToFileAtomic(cwsPath, new CwsFile());
            written.Add(cwsPath);
        }
        else
        {
            workspaceDir = existingWorkspaceDir
                ?? throw new InvalidOperationException(
                    "SavePlan has no workspace step but no existing workspace dir was supplied.");
        }

        // ── Create cells ──────────────────────────────────────────────────────
        foreach (var cellStep in plan.CellSteps)
        {
            CellFolder.CreateCellFolder(workspaceDir, cellStep.Name);
            var cellDir   = Path.Combine(workspaceDir, cellStep.Name);
            var ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
            written.Add(ccellPath);

            if (cellStep.IsTestBench)
            {
                var ccell = CellPersistence.LoadFromFile(ccellPath);
                ccell.IsTestBench = true;
                CellPersistence.SaveToFile(ccellPath, ccell);
            }
        }

        // ── Save schematics + set PrimarySchematic ────────────────────────────
        var primarySetForCell = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var saveStep in plan.SaveSteps)
        {
            var cellDir      = Path.Combine(workspaceDir, saveStep.TargetCellName);
            var schematicDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
            var filePath     = Path.Combine(schematicDir, saveStep.FileName);

            SchematicPersistence.SaveToFile(
                filePath,
                saveStep.Document.ViewModel.EditModel,
                cellName: saveStep.TargetCellName);
            written.Add(filePath);

            // Set PrimarySchematic on the .ccell for the first primary save per cell.
            if (saveStep.IsPrimary && primarySetForCell.Add(saveStep.TargetCellName))
            {
                var ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
                var ccell     = CellPersistence.LoadFromFile(ccellPath);
                ccell.PrimarySchematic = saveStep.FileName;
                CellPersistence.SaveToFile(ccellPath, ccell);
            }

            // ── Scratch → materialized transition ─────────────────────────────
            saveStep.Document.Materialize(filePath);
        }

        return written;
    }
}
