using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels.ProjectTree;

namespace CircuitRF.Ui.ViewModels.Dock;

/// <summary>
/// Dock Tool for the Project Tree region.  Owns the scanned VM tree and the
/// category-toggle filter (§3.3).  Refresh = re-scan; no FileSystemWatcher (deferred §9).
/// </summary>
public partial class ProjectTreeTool : Tool
{
    private WorkspaceModel? _workspaceModel;
    private ITreeActions?   _actions;

    // ── Filter (§3.3) ─────────────────────────────────────────────────────────

    public ProjectTreeFilterState FilterState { get; } = new();

    // ── Tree data ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Full VM tree (one root = the workspace node). Kept for expand-state collection
    /// and refresh; not bound as the TreeView's ItemsSource — use TopLevelItems instead.
    /// </summary>
    public ObservableCollection<ProjectTreeNodeViewModel> RootItems { get; } = new();

    /// <summary>
    /// The workspace root's filtered children, exposed as the TreeView's ItemsSource so
    /// the root "Workspace" row is omitted (the header already names the workspace).
    /// Null when no workspace is loaded.
    /// </summary>
    [ObservableProperty] private ObservableCollection<ProjectTreeNodeViewModel>? _topLevelItems;

    [ObservableProperty] private ProjectTreeNodeViewModel? _selectedItem;

    /// <summary>
    /// Workspace folder name shown in the in-view header; resets to "No workspace open" when
    /// no workspace is open.  A separate [ObservableProperty] because Tool.Title (Dock base)
    /// fires its own PropertyChanged which Avalonia compiled bindings don't reliably pick up.
    /// </summary>
    [ObservableProperty] private string _workspaceName = "No workspace open";

    /// <summary>True when a workspace is loaded; drives placeholder visibility in the view.</summary>
    public bool HasWorkspace => _workspaceModel is not null;

    // ── Recent workspaces (Item 1) ────────────────────────────────────────────

    /// <summary>
    /// Name + path pair for the no-workspace recent list.
    ///
    /// <para><b>It carries its own reveal label and command, and that is a binding constraint rather
    /// than a design preference.</b> A <c>ContextMenu</c> lives in its own popup visual tree, so an
    /// <c>$parent[ItemsControl]</c> walk out to the tool VM — which is how the row's own Open button
    /// reaches <see cref="OpenRecentCommand"/> — resolves to nothing from inside a menu item. What
    /// the menu item CAN see is the entry it was opened on, so the entry is what carries them.</para>
    /// </summary>
    public sealed record RecentEntry(string Name, string Path)
    {
        /// <summary>Platform-correct "Reveal in …", supplied by the tool that built this entry so
        /// there is one spelling of it per surface rather than one per row.</summary>
        public string RevealLabel { get; init; } = "";

        public IRelayCommand<string>? RevealCommand { get; init; }
    }

    public ObservableCollection<RecentEntry> RecentWorkspaces { get; } = new();
    public bool HasRecentWorkspaces => RecentWorkspaces.Count > 0;

    private void RefreshRecent()
    {
        RecentWorkspaces.Clear();
        if (_actions is not null)
            foreach (var (n, p) in _actions.GetRecentWorkspaces())
                RecentWorkspaces.Add(new RecentEntry(n, p)
                {
                    RevealLabel   = RevealLabel,
                    RevealCommand = RevealRecentCommand,
                });
        OnPropertyChanged(nameof(HasRecentWorkspaces));
    }

    [RelayCommand]
    private void OpenRecent(string path) => _actions?.OpenWorkspacePath(path);

    /// <summary>
    /// Shows a recent workspace in the platform's file manager without opening it.
    ///
    /// <para>The FOLDER is revealed, not the <c>.cws</c> inside it: a workspace IS its folder, and
    /// the file that marks one is a dotfile the file manager may well be configured not to show at
    /// all — revealing it would open a window with nothing selected in it.</para>
    /// </summary>
    [RelayCommand]
    private void RevealRecent(string? cwsPath)
    {
        if (string.IsNullOrWhiteSpace(cwsPath)) return;
        string dir = Path.GetDirectoryName(cwsPath) ?? cwsPath;
        _actions?.RevealPath(dir);
    }

    [RelayCommand]
    private void ClearRecent()
    {
        _actions?.ClearRecentWorkspaces();
        RefreshRecent();
    }

    // ── Workspace reveal (Item 3) ─────────────────────────────────────────────

    /// <summary>Platform-correct label for "Reveal in …" on the workspace title.</summary>
    public string RevealLabel =>
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX)      ? "Reveal in Finder"
        : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Reveal in Explorer"
        : "Reveal in File Manager";

    [RelayCommand]
    private void RevealWorkspace()
    {
        if (RootItems.Count > 0) RootItems[0].RevealCommand.Execute(null);
    }

    // ── Workspace-level header items (owner request, 2026-08-15) ──────────────
    //  On the workspace NAME when one is open, and on the "No workspace open" text when none is —
    //  the header is where a user looks for something that acts on the workspace as a whole, and
    //  when nothing is open it is the only thing on the panel to right-click at all.

    [RelayCommand]
    private Task CloseWorkspace() => _actions?.CloseWorkspaceFromTreeAsync() ?? Task.CompletedTask;

    [RelayCommand]
    private Task ArchiveWorkspace() => _actions?.ArchiveWorkspaceFromTreeAsync() ?? Task.CompletedTask;

    [RelayCommand]
    private Task OpenWorkspace() => _actions?.OpenWorkspaceFromTreeAsync() ?? Task.CompletedTask;

    [RelayCommand]
    private Task UnarchiveWorkspace() => _actions?.UnarchiveWorkspaceFromTreeAsync() ?? Task.CompletedTask;

    // ── Construction ──────────────────────────────────────────────────────────

    public ProjectTreeTool()
    {
        Id    = "ProjectTree";
        Title = "Project";   // static — never updated per workspace
    }

    // ── Actions wiring ────────────────────────────────────────────────────────

    /// <summary>
    /// Called by WorkspaceViewModel to inject the open/create/reveal callback interface.
    /// Must be called before SetWorkspace so actions are available during the first scan.
    /// </summary>
    public void SetActions(ITreeActions actions)
    {
        _actions = actions;
        RefreshRecent();
    }

    /// <summary>New Cell in the workspace root — bound to the tree-header button.</summary>
    [RelayCommand]
    private Task NewCellInWorkspace() => _actions?.NewCellInWorkspaceAsync() ?? Task.CompletedTask;

    /// <summary>
    /// Register a file or directory path as a Known File in the workspace .cws.
    /// Called by the tree drag-drop handler in ProjectTreeView.axaml.cs.
    /// </summary>
    public void AddKnownFile(string path) => _actions?.AddKnownFile(path);

    // ── Workspace wiring ──────────────────────────────────────────────────────

    /// <summary>
    /// Called by WorkspaceViewModel when a workspace file is opened.
    /// The workspace root dir is the folder containing the .cws file.
    /// </summary>
    public void SetWorkspace(string rootDir)
    {
        _workspaceModel = new WorkspaceModel(rootDir);
        var name = Path.GetFileName(rootDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        WorkspaceName = name;   // header shows the workspace name; Title stays "Project"
        RebuildVmTree(expandedPaths: []);
        OnPropertyChanged(nameof(HasWorkspace));
    }

    /// <summary>Called when the workspace is closed or reset.</summary>
    public void ClearWorkspace()
    {
        _workspaceModel = null;
        WorkspaceName = "No workspace open";
        TopLevelItems = null;
        RootItems.Clear();
        OnPropertyChanged(nameof(HasWorkspace));
        RefreshRecent();
    }

    // Notifies ITreeActions of selection changes (Item 5 — file info inspector routing).
    partial void OnSelectedItemChanged(ProjectTreeNodeViewModel? value)
        => _actions?.OnTreeSelectionChanged(value);

    // ── Refresh ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Re-scans the workspace folder and rebuilds the VM tree, preserving expanded paths.
    /// Called by the Refresh button and on-focus re-entry in the view.
    /// No FileSystemWatcher — manual + on-focus only (FileSystemWatcher deferred per §9).
    /// </summary>
    [RelayCommand]
    public void Refresh()
    {
        if (_workspaceModel is null) return;
        var expandedPaths = CollectExpandedPaths();
        _workspaceModel.Rescan();
        RebuildVmTree(expandedPaths);
    }

    // ── Dirty-cell notifications ──────────────────────────────────────────────

    /// <summary>
    /// Sets (or clears) the dirty indicator on the cell node that owns <paramref name="cellAbsDir"/>.
    /// Called by WorkspaceViewModel when a session for that cell's .csch changes dirty state.
    /// No-op when the cell node is not present in the tree (workspace not loaded or different workspace).
    /// </summary>
    public void SetCellDirty(string cellAbsDir, bool isDirty)
    {
        if (RootItems.Count == 0) return;
        var node = FindNodeByPath(RootItems[0], cellAbsDir);
        if (node is { Kind: NodeKind.Cell })
            node.IsDirty = isDirty;
    }

    /// <summary>
    /// Sets (or clears) the dirty indicator on a .ctech node. Called by WorkspaceViewModel when an
    /// open TechEditorViewModel's dirty state changes. Mirrors <see cref="SetCellDirty"/>.
    /// </summary>
    public void SetTechFileDirty(string techAbsPath, bool isDirty)
    {
        if (RootItems.Count == 0) return;
        var node = FindNodeByPath(RootItems[0], techAbsPath);
        if (node is { Kind: NodeKind.TechFile })
            node.IsDirty = isDirty;
    }

    private static ProjectTreeNodeViewModel? FindNodeByPath(
        ProjectTreeNodeViewModel root, string absPath)
    {
        if (string.Equals(root.AbsolutePath, absPath, StringComparison.OrdinalIgnoreCase))
            return root;
        foreach (var child in root.Children)
        {
            var found = FindNodeByPath(child, absPath);
            if (found is not null) return found;
        }
        return null;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void RebuildVmTree(HashSet<string> expandedPaths)
    {
        if (_workspaceModel is null) return;
        RootItems.Clear();
        var root = new ProjectTreeNodeViewModel(_workspaceModel.RootNode, FilterState, expandedPaths, _actions);
        RootItems.Add(root);
        // Point the tree at the workspace root's children — the header already names the workspace,
        // so the root row itself is omitted from the rendered tree.
        TopLevelItems = root.FilteredChildren;
    }

    private HashSet<string> CollectExpandedPaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in RootItems)
            CollectExpandedPathsRecursive(root, paths);
        return paths;
    }

    private static void CollectExpandedPathsRecursive(ProjectTreeNodeViewModel vm, HashSet<string> paths)
    {
        if (vm.IsExpanded) paths.Add(vm.AbsolutePath);
        foreach (var child in vm.Children)
            CollectExpandedPathsRecursive(child, paths);
    }
}
