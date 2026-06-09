namespace CircuitRF.Ui.Schematic;

// ──────────────────────────────────────────────────────────────────────────────
//  WorkspaceModel — thin wrapper so the step-3 view binds to one object.
//  Phase 6g Step 2.  Framework-free.
//
//  v1 refresh is manual + on-focus: the view calls Rescan() when it regains
//  focus or when the user triggers a Refresh command.  No FileSystemWatcher
//  (deferred, workspace-and-project-tree.md §9).
// ──────────────────────────────────────────────────────────────────────────────

public sealed class WorkspaceModel
{
    public string           WorkspaceRootDir { get; }
    public ProjectTreeNode  RootNode         { get; private set; }

    public WorkspaceModel(string workspaceRootDir)
    {
        WorkspaceRootDir = Path.GetFullPath(workspaceRootDir);
        RootNode         = WorkspaceScanner.Scan(WorkspaceRootDir);
    }

    /// <summary>
    /// Re-runs the scan and returns the fresh root node.
    /// The view calls this on focus regain or on an explicit Refresh command.
    /// </summary>
    public ProjectTreeNode Rescan()
    {
        RootNode = WorkspaceScanner.Scan(WorkspaceRootDir);
        return RootNode;
    }
}
