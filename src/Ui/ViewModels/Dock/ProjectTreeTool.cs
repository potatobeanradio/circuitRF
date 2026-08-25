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
public partial class ProjectTreeTool : Tool, IActivatableTool
{

    // ── Activation focus (owner, 2026-08-25) ──────────────────────────────────
    //
    //  Clicking this panel's TAB leaves keyboard focus on the tab — Dock's chrome, outside this
    //  panel's view — so the view's key handler is not on the event's route and Page Up/Down never
    //  arrive. The view listens here and focuses its own content instead. Same mechanism document
    //  tabs have had all along (IActivatableDocument); tools were never given it.

    private readonly ActivationFocusRelay _activationFocus = new();

    public event Action? ActivationFocusRequested
    {
        add    => _activationFocus.Requested += value;
        remove => _activationFocus.Requested -= value;
    }

    public void RequestActivationFocus() => _activationFocus.Request();

    public bool ConsumeActivationFocus() => _activationFocus.Consume();

    /// <summary>Dock's own "this tab was chosen" hook.</summary>
    public override void OnSelected()
    {
        base.OnSelected();
        RequestActivationFocus();
    }

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

        /// <summary>
        /// Opens this workspace — the same command the row's own click runs, carried here for the same
        /// reason <see cref="RevealCommand"/> is: the context menu is a separate popup tree and cannot
        /// walk out to the tool view model.
        /// </summary>
        public IRelayCommand<string>? OpenCommand { get; init; }
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
                    OpenCommand   = OpenRecentCommand,
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
        _activationFocus.Follow(this);
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

    [RelayCommand]
    private Task NewFolderInWorkspace() => _actions?.NewFolderInWorkspaceAsync() ?? Task.CompletedTask;

    // ── Name search (owner, 2026-08-25) ───────────────────────────────────────
    //
    //  TWO properties, on purpose. SearchText is what the TextBox is bound to, so the box itself is
    //  never waiting on anything. FilterState.SearchQuery is what the whole tree re-filters on, and
    //  it is written from a COALESCED callback — a burst of keystrokes costs one filter pass, not one
    //  per character.
    //
    //  Coalesced at Background priority rather than debounced on a timer: input is dispatched at a
    //  higher priority, so the keystroke always renders first and the filter runs in whatever gap
    //  follows — no arbitrary delay to tune, and no lag added when the user types a single character
    //  and stops. This is the same idiom ProjectTreeView already uses to coalesce its on-focus
    //  rescan.

    /// <summary>
    /// Whether the search FIELD is showing. Off by default: the magnifier button is the affordance,
    /// and a field that is always present spends the whole session eating the width the workspace
    /// name and the toolbar buttons need, for a control most sessions never touch (owner, 2026-08-25).
    /// </summary>
    [ObservableProperty] private bool _isSearchOpen;

    [RelayCommand]
    private void ToggleSearch() => IsSearchOpen = !IsSearchOpen;

    partial void OnIsSearchOpenChanged(bool value)
    {
        // Closing the field CLEARS the query, always. A filter that is still applied with nothing on
        // screen to say so is the worst state this panel can be in — the tree silently hides cells and
        // the only affordance that would explain it has just been put away.
        if (!value) SearchText = "";
    }

    /// <summary>What the user has typed, updated on every keystroke. Bound directly by the view.</summary>
    [ObservableProperty] private string _searchText = "";

    /// <summary>Drives the clear button's visibility off the TYPED text, so the X appears and
    /// disappears with the caret rather than with the filter pass behind it.</summary>
    public bool HasSearchText => !string.IsNullOrWhiteSpace(SearchText);

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasSearchText));
        ScheduleFilterApply();

        // Emptying the field deliberately does NOT close it (owner, 2026-08-25, revising an earlier
        // round that did): clearing the text and putting the field away are two different intentions,
        // and backspacing to the start of a re-typed query is the common one. There are exactly two
        // ways to collapse the field — Escape, and the X inside it — and both are explicit.
    }

    /// <summary>
    /// How a coalesced filter pass gets scheduled. Overridable so a headless test can run it inline —
    /// there is no dispatcher loop in the test host, and a posted callback would simply never fire.
    /// </summary>
    internal Action<Action> FilterScheduler { get; set; } =
        a => Avalonia.Threading.Dispatcher.UIThread.Post(a, Avalonia.Threading.DispatcherPriority.Background);

    private bool _filterApplyPending;

    private void ScheduleFilterApply()
    {
        if (_filterApplyPending) return;
        _filterApplyPending = true;
        FilterScheduler(() =>
        {
            _filterApplyPending = false;
            // Read SearchText HERE, not when the pass was scheduled — that is what makes several
            // keystrokes collapse into one pass against the latest text.
            FilterState.SearchQuery = SearchText;
        });
    }

    /// <summary>The X inside the field — one of the two ways to collapse it (Escape, handled by the
    /// view, is the other). Clears AND closes: emptying the text on its own leaves the field open, so
    /// both halves have to be spelled out here or the X would only do half its job.</summary>
    [RelayCommand]
    private void ClearSearch()
    {
        SearchText   = "";
        IsSearchOpen = false;
    }

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
        _renderedSignature = SignatureOf(_workspaceModel.RootNode);
        OnPropertyChanged(nameof(HasWorkspace));
    }

    /// <summary>Called when the workspace is closed or reset.</summary>
    public void ClearWorkspace()
    {
        _workspaceModel = null;
        _renderedSignature = 0;
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
        ApplyScan(_workspaceModel.ScanDetached());
    }

    /// <summary>
    /// The same refresh with the SCAN off the UI thread. Used by the on-focus re-entry, which is the
    /// frequent one and the one nothing waits on — an explicit Refresh keeps the synchronous
    /// <see cref="Refresh"/> so its many callers still see a finished tree when it returns.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (_workspaceModel is not { } model) return;
        if (_scanInFlight) return;      // a focus storm must not queue N scans of the same folder

        _scanInFlight = true;
        try
        {
            var scanned = await Task.Run(model.ScanDetached).ConfigureAwait(true);

            // The workspace can be closed or swapped while a scan is in flight; a result for a folder
            // nobody is looking at any more must not be installed over the one that is.
            if (!ReferenceEquals(_workspaceModel, model)) return;

            ApplyScan(scanned);
        }
        catch (Exception)
        {
            // A rescan is best-effort and unattended — a folder that vanished mid-walk is not worth an
            // error box on a window activation. The tree simply keeps what it had.
        }
        finally
        {
            _scanInFlight = false;
        }
    }

    private bool _scanInFlight;

    /// <summary>
    /// Signature of the node tree currently rendered — see <see cref="SignatureOf"/> for why this
    /// exists at all.
    /// </summary>
    private ulong _renderedSignature;

    /// <summary>
    /// Installs a scan result, and — the whole point — <b>does nothing at all when it describes the
    /// same tree that is already on screen.</b>
    ///
    /// <para><b>Owner, 2026-08-25: "when I open a large workspace, I can see the workspace flash a
    /// little bit."</b> <see cref="RebuildVmTree"/> throws every node VM away and assigns a NEW
    /// collection to <see cref="TopLevelItems"/>, so the TreeView tears down and rebuilds every
    /// realized container — and Avalonia's TreeView does not virtualize, so that is every row.
    /// <c>ProjectTreeView</c> runs this on the workspace window's <c>Activated</c>, which fires on
    /// open, on every alt-tab back, and on every dialog close. The overwhelming majority of those
    /// found nothing changed on disk and rebuilt the whole tree anyway.</para>
    ///
    /// <para>Expansion and dirty marks survive a rebuild anyway — <see cref="CollectExpandedPaths"/>
    /// and <see cref="RestoreDirtyFlags"/> exist precisely to reinstate them. The SELECTION does not:
    /// a rebuild replaces every node VM, so the selected one is no longer in the tree and the choice
    /// is dropped. Skipping keeps it.</para>
    /// </summary>
    private void ApplyScan(ProjectTreeNode scanned)
    {
        if (_workspaceModel is null) return;

        ulong signature = SignatureOf(scanned);
        if (signature == _renderedSignature && RootItems.Count > 0)
        {
            // Adopt the fresh nodes anyway so later reads see current objects; nothing rendered
            // differs, so nothing is rebuilt and nothing flashes.
            _workspaceModel.Adopt(scanned);
            return;
        }

        var expandedPaths = CollectExpandedPaths();
        _workspaceModel.Adopt(scanned);
        RebuildVmTree(expandedPaths);
        _renderedSignature = signature;
    }

    /// <summary>
    /// A 64-bit FNV-1a over everything about the scanned tree that can change what is RENDERED —
    /// shape, order, path, kind, name, primacy, test-bench flag and warning text.
    ///
    /// <para>64-bit rather than <c>HashCode</c>'s 32: the cost of a collision here is a real change on
    /// disk that never appears in the tree until an unrelated one comes along, and 32 bits is not a
    /// margin worth taking for that. A full string comparison would be exact and allocates a copy of
    /// the whole tree's text on every window activation, which is the thing being avoided.</para>
    ///
    /// <para>Everything the node VM derives at build time (a cell's openable views) derives from files
    /// that ARE nodes here, so a change to any of it moves this number.</para>
    /// </summary>
    private static ulong SignatureOf(ProjectTreeNode root)
    {
        ulong h = 14695981039346656037UL;   // FNV-1a 64 offset basis
        Fold(root, ref h);
        return h;

        static void Fold(ProjectTreeNode n, ref ulong h)
        {
            Mix(n.RelativePath, ref h);
            Mix(n.Name, ref h);
            Mix(((int)n.Kind).ToString(), ref h);
            Mix(n.IsPrimary ? "P" : "-", ref h);
            Mix(n.IsTestBench ? "T" : "-", ref h);
            Mix(n.IsDirectory ? "D" : "-", ref h);
            Mix(n.WarningReason ?? "", ref h);
            Mix("(", ref h);                 // shape delimiters: two flat lists must not collide
            foreach (var c in n.Children) Fold(c, ref h);
            Mix(")", ref h);
        }

        static void Mix(string s, ref ulong h)
        {
            foreach (char c in s)
            {
                h ^= c;
                h *= 1099511628211UL;        // FNV-1a 64 prime
            }
            h ^= 0xFF;
            h *= 1099511628211UL;            // field separator — "ab"+"c" must not equal "a"+"bc"
        }
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
    /// Sets (or clears) the dirty indicator on the FILE node at <paramref name="fileAbsPath"/> — a
    /// <c>.ctech</c>, <c>.cem</c> or <c>.cdd</c> node, or a cell view file. Called by
    /// WorkspaceViewModel whenever the corresponding open editor's dirty state changes, including the
    /// clear a save performs. No-op when no such node is in the tree (no workspace, a different
    /// workspace, or a file written since the last scan).
    /// </summary>
    /// <remarks>
    /// <b>One method for every file kind, deliberately</b> — this used to be one method PER kind, each
    /// guarding <c>node.Kind</c>, and two owner reports came straight out of that shape:
    ///
    /// <list type="bullet">
    /// <item><description><b>2026-08-21:</b> <i>"a dirty .cem does not show as dirty in the Project
    /// tree /em folder."</i> <c>HookEmSetupDirty</c> called the <c>.ctech</c> setter — a copy of the
    /// tech hook, correct in every respect except that a <c>.cem</c> node is
    /// <see cref="NodeKind.EmSetupFile"/>, so the <see cref="NodeKind.TechFile"/> guard threw the push
    /// away in silence. Nothing errored and nothing logged; the mark simply never appeared.</description></item>
    /// <item><description><b>2026-08-21:</b> <i>"after I saved a .cdd file to my results directory, the
    /// project tree view still indicated it was dirty in the tree."</i> A <c>.cdd</c> had no setter at
    /// all, so its mark was written only by <see cref="RestoreDirtyFlags"/> during a rebuild — and a
    /// save raises no window <c>Activated</c>, so the stale mark stood until an unrelated focus change
    /// rebuilt the tree.</description></item>
    /// </list>
    ///
    /// <para>The path already identifies the node uniquely, so a per-kind parameter added nothing but a
    /// way to pick the wrong one. The kind test that remains is only about what a mark MEANS on a node
    /// — a folder is marked through <see cref="SetCellDirty"/>, which owns the one node kind that is a
    /// directory.</para>
    /// </remarks>
    public void SetFileDirty(string? fileAbsPath, bool isDirty)
    {
        if (RootItems.Count == 0 || string.IsNullOrEmpty(fileAbsPath)) return;
        var node = FindNodeByPath(RootItems[0], Path.GetFullPath(fileAbsPath));
        if (node is not null && IsDirtyableFile(node.Kind))
            node.IsDirty = isDirty;
    }

    /// <summary>File node kinds that can carry a dirty mark — every kind this application can open in
    /// an editor. A kind absent here is one nothing can make dirty yet.</summary>
    private static bool IsDirtyableFile(NodeKind kind) => kind is
        NodeKind.ViewFile or NodeKind.DataDisplayFile or NodeKind.TechFile
        or NodeKind.EmSetupFile or NodeKind.HarmonicaFile or NodeKind.WBondFile;

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
        RestoreDirtyFlags(root);
        // Point the tree at the workspace root's children — the header already names the workspace,
        // so the root row itself is omitted from the rendered tree.
        TopLevelItems = root.FilteredChildren;
    }

    /// <summary>
    /// Re-asks <see cref="ITreeActions.IsNodeDirty"/> for every node of a freshly-built tree.
    /// </summary>
    /// <remarks>
    /// <b>Owner-reported, 2026-08-20:</b> <i>"when I closed my Match Designer, the schematic document
    /// became dirty, but the cell in the project tree indicated that it was not dirty — as soon as I
    /// closed the Match Designer window, the indicator on the cell changed from dirty to not
    /// dirty."</i>
    ///
    /// <para>Nothing about the Match Designer did that; <b>closing any window did</b>. The dirty
    /// indicator is PUSHED onto a node (<see cref="SetCellDirty"/>, <see cref="SetFileDirty"/>) and
    /// therefore lives only on the node object. <see cref="ProjectTreeView"/> re-scans on the
    /// workspace window's <c>Activated</c>, which is exactly what closing a second window raises, and
    /// <see cref="RebuildVmTree"/> throws every node away and builds new ones whose <c>IsDirty</c> is
    /// the field default. Every unsaved cell in the tree therefore went clean on a focus change, with
    /// the document tab still correctly showing its dot — the two disagreeing is the whole
    /// report.</para>
    ///
    /// <para>The fix is here rather than at the push sites because the state was never lost by any of
    /// them: <see cref="ITreeActions.IsNodeDirty"/> already answers the same question for the Save
    /// context menu, from the session registry and the open documents, which SURVIVE the rescan. A
    /// rebuilt tree simply has to ask it. Cheap, too — it is the same walk the build itself just
    /// did.</para>
    /// </remarks>
    private void RestoreDirtyFlags(ProjectTreeNodeViewModel node)
    {
        if (_actions is null) return;
        node.IsDirty = _actions.IsNodeDirty(node);
        foreach (var child in node.Children)
            RestoreDirtyFlags(child);
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
