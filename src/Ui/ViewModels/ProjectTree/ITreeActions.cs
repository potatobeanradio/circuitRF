namespace CircuitRF.Ui.ViewModels.ProjectTree;

/// <summary>
/// Callbacks provided by WorkspaceViewModel to ProjectTreeNodeViewModel commands.
/// Injected at VM construction via ProjectTreeTool.SetActions.
/// Each method maps to one user gesture (double-click or context-menu item).
/// </summary>
public interface ITreeActions
{
    /// <summary>Double-click open/activate for any node kind.</summary>
    void OpenNode(ProjectTreeNodeViewModel node);

    /// <summary>Make Primary — write .ccell, Refresh tree.</summary>
    void MakePrimary(ProjectTreeNodeViewModel node);

    /// <summary>Reveal in OS file manager (Finder / Explorer / xdg-open).</summary>
    void Reveal(ProjectTreeNodeViewModel node);

    /// <summary>
    /// The same reveal, for a surface that has a path but no tree node — the recent-workspaces list,
    /// which is shown precisely when there is no tree. Routed through the one implementation
    /// <see cref="Reveal"/> uses, because the per-platform argument forms are exactly what a second
    /// copy gets subtly wrong.
    /// </summary>
    void RevealPath(string absolutePath);

    /// <summary>New Cell on workspace/library node — prompts for name, creates folder, Refresh.</summary>
    Task NewCellAsync(ProjectTreeNodeViewModel parentNode);

    /// <summary>New Cell in the workspace root — no parent node; used by File menu and tree-header button.</summary>
    Task NewCellInWorkspaceAsync();

    /// <summary>New Symbol on cell node — prompts for name, creates .csym, opens editor, Refresh.</summary>
    Task NewSymbolAsync(ProjectTreeNodeViewModel cellNode);

    /// <summary>New Schematic on cell node — prompts for name, creates .csch, opens tab, Refresh.</summary>
    Task NewSchematicAsync(ProjectTreeNodeViewModel cellNode);

    /// <summary>New Layout on cell node — prompts for name, creates .clay, opens editor, Refresh.</summary>
    Task NewLayoutAsync(ProjectTreeNodeViewModel cellNode);

    /// <summary>Register a file or directory path as a Known File in the workspace .cws.</summary>
    void AddKnownFile(string path);

    /// <summary>Open a Known File with the OS default handler.</summary>
    void OpenExternal(ProjectTreeNodeViewModel node);

    /// <summary>Copy a Known File into the workspace folder and re-point the .cws reference.</summary>
    void CopyToWorkspace(ProjectTreeNodeViewModel node);

    /// <summary>Remove the Known File reference from .cws (does NOT delete the file on disk).</summary>
    void RemoveKnownFile(ProjectTreeNodeViewModel node);

    /// <summary>Open (or activate) the cell's primary schematic in a Content tab.</summary>
    void OpenCellSchematic(ProjectTreeNodeViewModel cellNode);

    /// <summary>Open (or activate) the cell's primary symbol in a Content tab.</summary>
    void OpenCellSymbol(ProjectTreeNodeViewModel cellNode);

    /// <summary>Open (or activate) the cell's primary layout in a Content tab.</summary>
    void OpenCellLayout(ProjectTreeNodeViewModel cellNode);

    /// <summary>Remove a .cdd Data Display file (moves to Trash). Confirms; no usage check.</summary>
    void RemoveDataDisplay(ProjectTreeNodeViewModel node);

    /// <summary>Remove a removable file/dir (.csch, .csym, results dir/subdir, .npy) — moves to Trash. Confirms.</summary>
    void RemoveFile(ProjectTreeNodeViewModel node);

    /// <summary>Remove a cell folder (moves to Trash). Big warning incl. workspace usage count; no in-app undo.</summary>
    Task RemoveCellAsync(ProjectTreeNodeViewModel cellNode);

    /// <summary>True when this node has unsaved work (drives the "Save" context item's visibility).</summary>
    bool IsNodeDirty(ProjectTreeNodeViewModel node);

    /// <summary>Save this node: a cell saves all its dirty schematics+symbols; a file saves just itself.</summary>
    Task SaveNodeAsync(ProjectTreeNodeViewModel node);

    // ── Recent-workspace access (Item 1) ──────────────────────────────────────

    /// <summary>Returns the recent-workspace list as (Name, Path) pairs (most-recent first).</summary>
    IReadOnlyList<(string Name, string Path)> GetRecentWorkspaces();

    /// <summary>Open the workspace at <paramref name="cwsPath"/> (same as clicking Open Recent).</summary>
    void OpenWorkspacePath(string cwsPath);

    /// <summary>Clear all recent workspaces.</summary>
    void ClearRecentWorkspaces();

    // ── Workspace-level actions on the tree header (owner request, 2026-08-15) ─

    /// <summary>
    /// Close the whole workspace — the tree header's own context item. Deliberately the whole
    /// workspace and never "close this window": the header names the workspace, so that is what the
    /// item on it has to mean (see <c>WorkspaceViewModel.CloseWorkspaceOrWindow</c>'s own note).
    /// </summary>
    Task CloseWorkspaceFromTreeAsync();

    /// <summary>Archive the workspace — the same command the File menu offers.</summary>
    Task ArchiveWorkspaceFromTreeAsync();

    /// <summary>
    /// Open a workspace, through the same picker and the same code File ▸ Open Workspace… uses.
    /// Reachable from the "No workspace open" header, which is exactly when the File menu's own
    /// entry is hardest to think of.
    /// </summary>
    Task OpenWorkspaceFromTreeAsync();

    /// <summary>Unarchive a workspace archive and open it — the same command the File menu offers.</summary>
    Task UnarchiveWorkspaceFromTreeAsync();

    // ── Selection change hook (Item 5) ────────────────────────────────────────

    /// <summary>
    /// Called when the project-tree selection changes. Implementor may update the Properties
    /// pane with file-info for leaf Known File / OtherFile nodes.
    /// </summary>
    void OnTreeSelectionChanged(ProjectTreeNodeViewModel? node);

    // ── Cell operations (Items 6 & 7) ────────────────────────────────────────

    /// <summary>Duplicate the given cell folder to a new name in the same workspace directory.</summary>
    Task DuplicateCellAsync(ProjectTreeNodeViewModel cellNode);

    /// <summary>Rename the given cell folder, rewrite all workspace references, and optionally rename primaries.</summary>
    Task RenameCellAsync(ProjectTreeNodeViewModel cellNode);

    // ── Technology (.ctech) node actions (L0c) ────────────────────────────────

    /// <summary>Writes this .ctech node's workspace-relative path into .cws DefaultTechRef,
    /// invalidates the technology cache for it, and refreshes every open layout's resolution.</summary>
    void SetAsWorkspaceDefault(ProjectTreeNodeViewModel node);

    /// <summary>Invalidates the cached Technology for this .ctech node so a hand-edited file takes
    /// effect without restarting — the live-refresh seam for open layouts using it. Prompts for
    /// confirmation first when a live (unsaved editor) override exists for the path, since reloading
    /// would otherwise silently discard those unsaved changes; cancelling leaves the override intact.</summary>
    Task ReloadTechnologyAsync(ProjectTreeNodeViewModel node);

    /// <summary>True when this .ctech node is the workspace's current default technology — drives
    /// the check/radio affordance on the node.</summary>
    bool IsWorkspaceDefaultTech(ProjectTreeNodeViewModel node);

    // ── wBond (.wBond) node actions (wbond.md §9.2 routes 2 and 3) ────────────

    /// <summary>
    /// Route 2 — place this <c>.wBond</c>'s wires in the ACTIVE schematic as a wBond component,
    /// wired to nothing, with its <c>File</c> already pointing at the design.
    /// </summary>
    Task AddWBondToSchematicAsync(ProjectTreeNodeViewModel node);

    /// <summary>
    /// Route 3 — create a new cell whose LAYOUT view is this <c>.wBond</c>'s embedded geometry and
    /// whose SCHEMATIC view holds the wBond component. A design carrying no embedded geometry is
    /// route 2, not a failure, and is diverted there with a message.
    /// </summary>
    Task AddWBondAsCellAsync(ProjectTreeNodeViewModel node);

    /// <summary>New Technology… — prompts for a name and starting point (PCB / MMIC / Empty),
    /// writes tech/&lt;name&gt;.ctech, optionally sets it as the workspace default, opens it.</summary>
    Task NewTechnologyAsync(ProjectTreeNodeViewModel node);
}
