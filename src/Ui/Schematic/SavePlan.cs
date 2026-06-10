namespace CircuitRF.Ui.Schematic;

// ── Mode ──────────────────────────────────────────────────────────────────────

/// <summary>How scratch schematics are mapped to destination cells in the plan dialog.</summary>
public enum SaveMode
{
    /// <summary>Each scratch schematic gets its own cell, name seeded from the document name.</summary>
    EachOwnCell,
    /// <summary>All scratch schematics land in one shared cell; the first becomes primary.</summary>
    AllInOneCell,
}

// ── Step records ─────────────────────────────────────────────────────────────

/// <summary>A step to create a new workspace folder and .cws file.</summary>
public sealed record WorkspaceStep(string Name, string ParentDir);

/// <summary>A step to create a new cell folder and .ccell file.</summary>
public sealed record CellStep(string Name, bool IsTestBench);

/// <summary>A step to save one schematic document into a cell's schematic sub-folder.</summary>
public sealed record SaveStep(
    SchematicDocument Document,
    string            TargetCellName,
    ViewType          ViewType,
    string            FileName,
    bool              IsPrimary);

// ── Plan ──────────────────────────────────────────────────────────────────────

/// <summary>
/// Computed materialize plan for a set of dirty scratch schematics.
/// Framework-free (no Avalonia). Produced by <see cref="SavePlanBuilder.Build"/>.
/// </summary>
public sealed class SavePlan
{
    /// <summary>Create a workspace (present only when no workspace is currently loaded).</summary>
    public WorkspaceStep? WorkspaceStep { get; init; }

    /// <summary>Cells to create, de-duplicated by name (one per distinct destination).</summary>
    public IReadOnlyList<CellStep> CellSteps { get; init; } = [];

    /// <summary>Documents to save, one per scratch schematic.</summary>
    public IReadOnlyList<SaveStep> SaveSteps { get; init; } = [];
}

// ── Builder ───────────────────────────────────────────────────────────────────

/// <summary>
/// Computes a <see cref="SavePlan"/> from the current workspace state and set of dirty
/// scratch documents. Framework-free. Call <see cref="Build"/> with mode and optional
/// name overrides; the dialog calls it live on every mode toggle or name edit.
/// </summary>
public sealed class SavePlanBuilder
{
    private readonly string?                          _currentWorkspacePath;
    private readonly string                           _workspaceParentDir;
    private readonly IReadOnlyList<SchematicDocument> _scratchDocs;

    public SavePlanBuilder(
        string?                          currentWorkspacePath,
        string                           workspaceParentDir,
        IReadOnlyList<SchematicDocument> scratchDocs)
    {
        _currentWorkspacePath = currentWorkspacePath;
        _workspaceParentDir   = workspaceParentDir;
        _scratchDocs          = scratchDocs;
    }

    private static bool SchematicHasAnalyses(SchematicEditModel model)
        => model.Analyses.Count > 0;

    /// <summary>
    /// Returns the next free Untitled-Workspace-N name for the workspace parent dir.
    /// </summary>
    public string DefaultWorkspaceName()
    {
        for (int n = 1; n <= 9999; n++)
        {
            var candidate = $"Untitled-Workspace-{n}";
            if (!Directory.Exists(Path.Combine(_workspaceParentDir, candidate)))
                return candidate;
        }
        return "Untitled-Workspace";
    }

    /// <summary>
    /// Builds a <see cref="SavePlan"/> for the given mode and optional name overrides.
    /// </summary>
    /// <param name="mode">EachOwnCell (default) or AllInOneCell.</param>
    /// <param name="workspaceNameOverride">Workspace folder name (only when no workspace loaded).</param>
    /// <param name="allInOneCellName">Shared cell name for AllInOneCell mode.</param>
    /// <param name="cellNameOverrides">Maps document Id → cell name in EachOwnCell mode.</param>
    public SavePlan Build(
        SaveMode                              mode                  = SaveMode.EachOwnCell,
        string?                               workspaceNameOverride = null,
        string?                               allInOneCellName      = null,
        IReadOnlyDictionary<string, string>?  cellNameOverrides     = null)
    {
        // ── Workspace step ────────────────────────────────────────────────────
        WorkspaceStep? wsStep = null;
        if (_currentWorkspacePath is null)
        {
            var name = workspaceNameOverride ?? DefaultWorkspaceName();
            wsStep = new WorkspaceStep(name, _workspaceParentDir);
        }

        // ── Cell + Save steps ─────────────────────────────────────────────────
        var cellSteps = new List<CellStep>();
        var saveSteps = new List<SaveStep>();

        if (mode == SaveMode.EachOwnCell)
        {
            foreach (var doc in _scratchDocs)
            {
                var docId    = doc.Id;
                var cellName = cellNameOverrides is not null &&
                               cellNameOverrides.TryGetValue(docId, out var ov)
                    ? ov : docId;

                var hasAnal = SchematicHasAnalyses(doc.ViewModel.EditModel);

                // De-dupe: only add a cell step if this name isn't already in the list.
                if (!cellSteps.Any(cs =>
                    string.Equals(cs.Name, cellName, StringComparison.OrdinalIgnoreCase)))
                {
                    cellSteps.Add(new CellStep(cellName, hasAnal));
                }

                // First save step for a given cell name is primary.
                var isPrimary = !saveSteps.Any(ss =>
                    string.Equals(ss.TargetCellName, cellName, StringComparison.OrdinalIgnoreCase));

                var fileName = $"{cellName}{CellFolder.ViewExtension(ViewType.Schematic)}";
                saveSteps.Add(new SaveStep(doc, cellName, ViewType.Schematic, fileName, isPrimary));
            }
        }
        else // AllInOneCell
        {
            var firstDoc   = _scratchDocs.Count > 0 ? _scratchDocs[0] : null;
            var sharedCell = allInOneCellName
                ?? (firstDoc is not null ? firstDoc.Id : "Untitled-Schematic-1");
            var anyHasAnal = _scratchDocs.Any(d => SchematicHasAnalyses(d.ViewModel.EditModel));

            cellSteps.Add(new CellStep(sharedCell, anyHasAnal));

            for (int i = 0; i < _scratchDocs.Count; i++)
            {
                var doc       = _scratchDocs[i];
                var isPrimary = i == 0;
                // First schematic uses the shared cell name as its filename (it is primary);
                // subsequent schematics use their own doc name to avoid filename collisions.
                var baseName = i == 0 ? sharedCell : doc.Id;
                var fileName = $"{baseName}{CellFolder.ViewExtension(ViewType.Schematic)}";
                saveSteps.Add(new SaveStep(doc, sharedCell, ViewType.Schematic, fileName, isPrimary));
            }
        }

        return new SavePlan
        {
            WorkspaceStep = wsStep,
            CellSteps     = cellSteps,
            SaveSteps     = saveSteps,
        };
    }
}
