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

    /// <summary>New Folder on a workspace / library / user-folder node — prompts for a name, creates
    /// the directory, Refresh. Cells inside a sub-folder already work everywhere (a CellRef is a
    /// RELATIVE path, and the workspace scanner recurses), so this is the missing way to make one
    /// without leaving the app.</summary>
    Task NewFolderAsync(ProjectTreeNodeViewModel parentNode);

    /// <summary>New Folder in the workspace root — no parent node; the tree-header button.</summary>
    Task NewFolderInWorkspaceAsync();

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

    /// <summary>
    /// Build a NEW cell in the workspace root around a copy of this Known File's view (.csch / .csym
    /// / .clay), prompting for the cell name. Validates the file FIRST and creates nothing when it
    /// does not read back as that view type — the user is told why instead. Distinct from
    /// <see cref="CopyToWorkspace"/>, which copies the file loose and re-points the reference; this
    /// one leaves the Known File reference exactly where it is and produces a cell beside it.
    /// </summary>
    Task CopyKnownFileToWorkspaceAsCellAsync(ProjectTreeNodeViewModel node);

    /// <summary>
    /// Build a NEW cell around one SPICE <c>.model</c> card in this file — the native circuitRF
    /// component carrying the card's parameters, its pins already wired, and an editable copy of
    /// that component's symbol. Reads the file FIRST and creates nothing when it holds no card
    /// circuitRF can build; the user is told which cards it holds and why each was refused.
    /// Distinct from <see cref="CopyKnownFileToWorkspaceAsCellAsync"/>, which adopts a circuitRF
    /// VIEW file as a cell view — a model card is not a view and has to be built into one.
    /// </summary>
    Task CreateCellFromModelCardAsync(ProjectTreeNodeViewModel node);

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

    /// <summary>
    /// The <c>.cws</c> of the workspace this node's files actually live in, when that is a DIFFERENT
    /// workspace from the one open in this window — a cell under a Referenced Workspace sub-tree, a
    /// library cell that happens to sit inside someone else's workspace, or the Referenced Workspace
    /// row itself (whose path IS that other workspace's root). Null for everything that belongs here,
    /// and for a path with no ancestor workspace at all.
    ///
    /// <para>Answered by the walk-up rather than by the node's position in the tree, so it says where
    /// the cell IS rather than how it was reached, and it is memoised
    /// (<c>WorkspaceRootFinder.WorkspaceDirOf</c>) because a context-menu visibility binding asks it
    /// per node.</para>
    /// </summary>
    string? ForeignWorkspaceCwsFor(ProjectTreeNodeViewModel node);

    /// <summary>
    /// Removes one Referenced Workspace entry from this workspace's own <c>.cws</c> — the way OUT of
    /// File ▸ Reference Workspace…, which until now had none: an alias could be created and never
    /// taken away except by hand-editing the file.
    ///
    /// <para>Reference-only, exactly like <c>RemoveKnownFile</c>: nothing is deleted, in either
    /// workspace. What it does break is any cell here that places a cell through the alias, so the
    /// confirmation counts those first.</para>
    /// </summary>
    Task RemoveWorkspaceReferenceAsync(ProjectTreeNodeViewModel referencedWorkspaceNode);

    /// <summary>True when this node has unsaved work (drives the "Save" context item's visibility).</summary>
    bool IsNodeDirty(ProjectTreeNodeViewModel node);

    /// <summary>Save this node: a cell saves all its dirty schematics+symbols; a file saves just itself.</summary>
    Task SaveNodeAsync(ProjectTreeNodeViewModel node);

    // ── Recent-workspace access (Item 1) ──────────────────────────────────────

    /// <summary>Returns the recent-workspace list as (Name, Path) pairs (most-recent first).</summary>
    IReadOnlyList<(string Name, string Path)> GetRecentWorkspaces();

    /// <summary>Open the workspace at <paramref name="cwsPath"/> (same as clicking Open Recent).</summary>
    void OpenWorkspacePath(string cwsPath);

    /// <summary>
    /// Open the workspace at <paramref name="cwsPath"/> in a window of its own, leaving this window
    /// exactly as it is — the same thing File ▸ Open Workspace in New Window… does once its picker
    /// has produced a path, so a workspace already open somewhere simply comes to the front rather
    /// than being opened twice (R-mw1-9).
    /// </summary>
    void OpenWorkspacePathInNewWindow(string cwsPath);

    /// <summary>Clear all recent workspaces.</summary>
    void ClearRecentWorkspaces();

    /// <summary>
    /// Drop one entry from the recent list. The workspace itself is untouched — this forgets the
    /// path, it does not delete anything on disk.
    /// </summary>
    void RemoveRecentWorkspace(string cwsPath);

    // ── Workspace-level actions on the tree header (owner request, 2026-08-15) ─

    /// <summary>
    /// Close the whole workspace — the tree header's own context item. Deliberately the whole
    /// workspace and never "close this window": the header names the workspace, so that is what the
    /// item on it has to mean (see <c>WorkspaceViewModel.CloseWindow</c>'s own note).
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

    // ── Cross-workspace drag-drop (MW3) ──────────────────────────────────────

    /// <summary>
    /// A cell belonging to ANOTHER workspace is being taken into this one — by drag-drop between two
    /// Project Trees, or by File ▸ Add Cell to Workspace…. Prompts for copy-vs-reference (MW3 §1)
    /// and carries out whichever was chosen.
    /// </summary>
    /// <param name="sourceCellDir">The cell folder, absolute — the drag payload already carries one.</param>
    /// <param name="destFolderDir">The folder it was dropped on; null for the workspace root.</param>
    Task AcceptCellFromOtherWorkspaceAsync(string sourceCellDir, string? destFolderDir);

    /// <summary>A loose file dragged from another workspace's tree — copied in, never referenced
    /// (R-mw3-11).</summary>
    Task AcceptDroppedFileAsync(string sourceFile, string? destFolderDir);

    // ── In-workspace move (TM1) ───────────────────────────────────────────────

    /// <summary>
    /// A cell, folder or loose file from THIS workspace's own tree was dropped on a folder in it —
    /// move it there, and repoint every reference the move invalidates in BOTH directions (into the
    /// moved subtree, and out of it).
    ///
    /// <para>There is no in-app undo, exactly as there is none for
    /// <see cref="RemoveCellAsync"/> or <see cref="RenameCellAsync"/>; the success message names the
    /// old and the new location so the move can be reversed by re-dragging it.</para>
    /// </summary>
    /// <param name="sourcePath">The cell folder, user folder or file, absolute.</param>
    /// <param name="destFolderDir">The folder it was dropped on; null for the workspace root.</param>
    Task MoveInsideWorkspaceAsync(string sourcePath, string? destFolderDir);

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
