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

    /// <summary>
    /// SL4 R-sl4-10: the same scan with the REFERENCED sub-trees carried forward from the tree
    /// currently installed rather than re-walked. The workspace's own folders are walked exactly as
    /// always — this is only about not reading someone else's disk on every window activation.
    /// </summary>
    public ProjectTreeNode ScanDetachedReusingReferenced()
        => WorkspaceScanner.Scan(WorkspaceRootDir, ReferencedSubtrees.Reuse, RootNode);

    /// <summary>
    /// The same scan, WITHOUT installing the result — so it can run on a background thread and the
    /// caller can decide, back on the UI thread, whether the result is worth adopting.
    ///
    /// <para><see cref="WorkspaceScanner"/> is framework-free and touches nothing but the filesystem,
    /// which is what makes this safe off-thread. Measured at ~92 ms for a 600-cell workspace, and it
    /// was being run synchronously on the UI thread on every window activation.</para>
    /// </summary>
    public ProjectTreeNode ScanDetached() => WorkspaceScanner.Scan(WorkspaceRootDir);

    /// <summary>Installs a node tree produced by <see cref="ScanDetached"/>. UI thread.</summary>
    public void Adopt(ProjectTreeNode scanned) => RootNode = scanned;
}
