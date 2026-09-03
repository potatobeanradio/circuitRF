using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Ui.Schematic;
using Material.Icons;

namespace CircuitRF.Ui.ViewModels.ProjectTree;

// ProjectTreeItemKind (4-kind 6b stub) is intentionally deleted — NodeKind (from
// ProjectTreeNode) is the canonical discriminator.  ProjectTreeItemViewModel (6b stub)
// is replaced by ProjectTreeNodeViewModel below.

/// <summary>
/// View-model wrapper for a <see cref="ProjectTreeNode"/> produced by
/// <see cref="WorkspaceScanner.Scan"/>.  Exposes display-ready properties (bold, italic,
/// warning, icon) and a <see cref="FilteredChildren"/> collection that re-computes whenever
/// the injected <see cref="ProjectTreeFilterState"/> changes.
///
/// Commands (Activate, MakePrimary, Reveal, New*) delegate to the injected
/// <see cref="ITreeActions"/> — provided by WorkspaceViewModel.  Null actions = no-op
/// (occurs before a workspace is set).
/// </summary>
public sealed class ProjectTreeNodeViewModel : ObservableObject
{
    private readonly ProjectTreeNode        _node;
    private readonly ProjectTreeFilterState _filter;

    /// <summary>The node that built this one; null on the workspace root. Only the root subscribes to
    /// filter changes — see the constructor for why.</summary>
    private readonly ProjectTreeNodeViewModel? _parent;
    private readonly ITreeActions?          _actions;

    // ── Identity / model data (read from the node — never re-derived here) ────

    public NodeKind  Kind          => _node.Kind;
    public bool      IsPrimary     => _node.IsPrimary;
    public bool      IsTestBench   => _node.IsTestBench;
    public string?   WarningReason => _node.WarningReason;
    public string    RelativePath  => _node.RelativePath;
    public string    AbsolutePath  => _node.AbsolutePath;

    /// <summary>True when this node's path is a DIRECTORY rather than a file. Only a Known File can
    /// be either, which is why the flag lives on the node at all — every other kind is settled by
    /// its <see cref="Kind"/>.</summary>
    public bool      IsDirectory   => _node.IsDirectory;

    /// <summary>
    /// Display name shown in the tree label.
    /// Known File directories show their relative path so the user can tell
    /// them apart when two dirs share the same folder name.
    /// All other nodes show the raw scanner name.
    /// </summary>
    public string Name => (Kind == NodeKind.KnownFile && _node.IsDirectory)
        ? _node.RelativePath
        : _node.Name;

    /// <summary>
    /// Text shown in the node tooltip.
    /// The Known Files GROUP is a synthetic node built by the scanner with an empty relative path,
    /// so the shared tooltip rendered blank for it — it describes what the folder holds instead.
    /// Known File directories show the absolute path (the label already shows relative).
    /// All other nodes show the relative path.
    /// </summary>
    public string TooltipPath => Kind switch
    {
        NodeKind.KnownFilesGroup                  => "External files that are known to this workspace are listed here.",
        NodeKind.KnownFile when _node.IsDirectory => _node.AbsolutePath,
        _                                         => _node.RelativePath,
    };

    // ── Computed display properties (bind → render; never re-derive flags) ────

    public bool IsWarning => WarningReason is not null;
    public bool IsBold    => IsPrimary;
    public bool IsItalic  => IsWarning;

    /// <summary>Material icon glyph — combines Kind + IsTestBench + directory flag.</summary>
    public MaterialIconKind IconKind => (Kind, IsTestBench) switch
    {
        (NodeKind.Workspace,       _)     => MaterialIconKind.Folder,
        (NodeKind.Cell,            true)  => MaterialIconKind.TestTube,
        (NodeKind.Cell,            false) => MaterialIconKind.IntegratedCircuitChip,
        (NodeKind.Library,         _)     => MaterialIconKind.BookOpenPageVariant,
        (NodeKind.LibrariesGroup,  _)     => MaterialIconKind.BookOpenPageVariant,
        // MW2: a referenced WORKSPACE is not a library — it brings a technology, a kit set and a
        // .cws of its own — so it gets its own glyph rather than borrowing the book.
        (NodeKind.ReferencedWorkspace,      _) => MaterialIconKind.FolderNetworkOutline,
        (NodeKind.ReferencedWorkspacesGroup,_) => MaterialIconKind.FolderNetworkOutline,
        (NodeKind.CellViewFolder,  _)     => MaterialIconKind.FolderOutline,
        (NodeKind.ViewFile,        _)     => MaterialIconKind.FileOutline,
        (NodeKind.DataDisplayFile, _)     => MaterialIconKind.ChartLine,
        (NodeKind.HarmonicaFile, _)      => MaterialIconKind.ChartBellCurve,
        (NodeKind.WBondFile, _)          => MaterialIconKind.VectorPolyline,
        (NodeKind.ColorThemeFile,  _)     => MaterialIconKind.Palette,
        (NodeKind.TechFile,        _)     => MaterialIconKind.LayersOutline,
        (NodeKind.EmSetupFile,     _)     => MaterialIconKind.SineWave,
        (NodeKind.KnownFile,       _)     => _node.IsDirectory
                                                ? MaterialIconKind.FolderOutline
                                                : MaterialIconKind.FileOutline,
        (NodeKind.KnownFilesGroup, _)     => MaterialIconKind.FolderOutline,
        (NodeKind.UserFolder,      _)     => MaterialIconKind.Folder,
        (NodeKind.OtherFile,       _)     => MaterialIconKind.FileOutline,
        _                                 => MaterialIconKind.FileOutline,
    };

    // ── Context-menu visibility helpers (bind IsVisible on MenuItem) ──────────

    public bool IsViewFile           => Kind == NodeKind.ViewFile;
    public bool IsCell               => Kind == NodeKind.Cell;
    public bool IsKnownFile          => Kind == NodeKind.KnownFile;
    public bool IsWorkspaceOrLibrary => Kind is NodeKind.Workspace or NodeKind.Library;

    /// <summary>
    /// Where a new cell, technology or folder may be created. A USER FOLDER belongs here and was
    /// simply never added: the workspace scanner has always recursed into one and a CellRef is a
    /// relative path, so a cell inside a folder already worked in every consumer — there was just no
    /// way to put one there from the tree, which made the folder look unsupported when it was not.
    /// </summary>
    public bool CanCreateInside => Kind is NodeKind.Workspace or NodeKind.Library or NodeKind.UserFolder;

    /// <summary>True for .cdd Data Display files — drives "Remove Data Display" context item.</summary>
    public bool IsDataDisplayFile => Kind == NodeKind.DataDisplayFile;

    /// <summary>True for .ctech technology files — drives the "Set as Workspace Default" /
    /// "Reload Technology" context items. No editor yet (L0d); double-click stays a no-op.</summary>
    public bool IsTechFile => Kind == NodeKind.TechFile;

    /// <summary>brief-L6-L7-em-ui.md R-em-9 — a .cem EM setup document.</summary>
    public bool IsEmSetupFile => Kind == NodeKind.EmSetupFile;

    /// <summary>True for .wBond wirebond designs — drives the two §9.2 routes that bring one into
    /// a design ("Add to Schematic", "Add as Cell…"). Double-click still opens the wBond editor
    /// (route 1), which is what a user reaching for the file itself means.</summary>
    public bool IsWBondFile => Kind == NodeKind.WBondFile;

    /// <summary>True when this .ctech node is the workspace's current default technology.
    /// Resolved through the host so it reflects the live .cws state when the menu opens.</summary>
    public bool IsWorkspaceDefaultTech => _actions?.IsWorkspaceDefaultTech(this) ?? false;

    /// <summary>
    /// True for nodes that can be removed via Trash: .csch/.csym view files, results directories
    /// (UserFolder), and .npy/other files under results dirs (OtherFile).
    /// NOTE: results dirs and .npy files are classified as UserFolder/OtherFile (no dedicated NodeKind).
    /// </summary>
    public bool IsRemovableFile =>
        (Kind == NodeKind.ViewFile &&
            Path.GetExtension(AbsolutePath).ToLowerInvariant() is ".csch" or ".csym" or ".clay")
        || Kind == NodeKind.OtherFile
        || Kind == NodeKind.UserFolder;

    /// <summary>True for openable leaf files (.csch / .csym / .clay / .cdd / .charm / .ctech) — drives
    /// the "Open" context item.</summary>
    public bool IsOpenableFile =>
        Kind is NodeKind.DataDisplayFile or NodeKind.HarmonicaFile or NodeKind.TechFile
             or NodeKind.EmSetupFile or NodeKind.WBondFile
        || (Kind == NodeKind.ViewFile && Path.GetExtension(AbsolutePath).ToLowerInvariant() is ".csch" or ".csym" or ".clay")
        || IsOpenableKnownFile;

    /// <summary>
    /// True for a Known File that is a circuitRF DOCUMENT — one the app has an editor for. Such a
    /// file opens in a tab like any other, foreign or not: it is bookmarked from outside the
    /// workspace, so what it opens as is an orphan document (brief-foreign-documents.md — foreignness
    /// is decided by the file's own path, never by how it was opened).
    ///
    /// <para>Excludes a directory (a folder called <c>x.clay</c> is still a folder), a broken
    /// reference (nothing to open), and every non-document extension — a bookmarked <c>.snp</c> or
    /// <c>.pdf</c> still has only "Open External…", which is why that item stays.</para>
    /// </summary>
    public bool IsOpenableKnownFile =>
        Kind == NodeKind.KnownFile
        && !IsDirectory
        && !IsWarning
        && WorkspaceScanner.ClassifyFile(AbsolutePath) is not NodeKind.OtherFile and not NodeKind.ColorThemeFile;

    /// <summary>
    /// True for a Known File that could become a cell — a <c>.csch</c>, <c>.csym</c> or <c>.clay</c>,
    /// the three views a cell folder has a home for. Drives "Copy to Workspace as Cell…"; the file
    /// itself is only VALIDATED when that command runs, since reading every bookmarked file to
    /// decide whether to show a menu item would make opening the menu do the work.
    /// </summary>
    public bool IsKnownFileCopyableAsCell =>
        Kind == NodeKind.KnownFile
        && !IsDirectory
        && !IsWarning
        && CellViewFileValidator.ViewTypeFor(AbsolutePath) is not null;

    /// <summary>
    /// True for a Known File that is a SPICE <c>.model</c> card file — drives "Copy to Workspace as
    /// Cell…" for model cards.
    ///
    /// <para>Extension only, exactly as <see cref="IsKnownFileCopyableAsCell"/> is: the item appears
    /// on a bookmarked path with nothing having read it, and parsing every bookmarked file to decide
    /// whether to show a menu item would make opening the menu do the work. What the file actually
    /// holds is settled when the command runs, which is also where the user can be told about it.</para>
    /// </summary>
    public bool IsModelCardFile =>
        Kind == NodeKind.KnownFile
        && !IsDirectory
        && !IsWarning
        && ModelCardCellBuilder.IsSpiceCellFile(AbsolutePath);

    /// <summary>True when this node has unsaved work — drives the "Save" context item.
    /// Resolved through the host so it reflects live dirty state when the menu opens.</summary>
    public bool IsSaveable => _actions?.IsNodeDirty(this) ?? false;

    /// <summary>Context-menu label for the Save action — varies by node kind.</summary>
    public string SaveHeader => Kind switch
    {
        NodeKind.Cell            => "Save Cell",
        NodeKind.DataDisplayFile => "Save Data Display",
        NodeKind.TechFile        => "Save Technology",
        NodeKind.EmSetupFile     => "Save EM Setup",
        NodeKind.ViewFile when Path.GetExtension(AbsolutePath).ToLowerInvariant() == ".csch" => "Save Schematic",
        NodeKind.ViewFile when Path.GetExtension(AbsolutePath).ToLowerInvariant() == ".csym" => "Save Symbol",
        NodeKind.ViewFile when Path.GetExtension(AbsolutePath).ToLowerInvariant() == ".clay" => "Save Layout",
        _                        => "Save",
    };

    /// <summary>
    /// True when this Known File's path is inside the workspace root.
    /// A relative path that doesn't start with ".." and isn't rooted is inside.
    /// Used to disable Copy-to-Workspace for already-in-workspace files.
    /// </summary>
    public bool IsInsideWorkspace =>
        !RelativePath.StartsWith("..", StringComparison.Ordinal)
        && !Path.IsPathRooted(RelativePath);

    public bool CanReveal            => Kind is NodeKind.ViewFile
                                          or NodeKind.CellViewFolder
                                          or NodeKind.Cell
                                          or NodeKind.Workspace
                                          or NodeKind.Library
                                          or NodeKind.UserFolder
                                          or NodeKind.DataDisplayFile
                                          or NodeKind.HarmonicaFile
                                          or NodeKind.WBondFile
                                          or NodeKind.ColorThemeFile
                                          or NodeKind.TechFile
                                          or NodeKind.EmSetupFile
                                          or NodeKind.KnownFile
                                          or NodeKind.OtherFile;

    /// <summary>Platform-correct label for the Reveal menu item.</summary>
    public string RevealLabel =>
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX)     ? "Reveal in Finder"
        : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Reveal in Explorer"
        : "Reveal in File Manager";

    // ── Commands (wired to ITreeActions) ──────────────────────────────────────

    /// <summary>Activate / open this node (double-click action).</summary>
    public IRelayCommand ActivateCommand { get; }

    /// <summary>Make Primary — write .ccell, refresh tree.</summary>
    public IRelayCommand MakePrimaryCommand { get; }

    /// <summary>Reveal in OS file manager.</summary>
    public IRelayCommand RevealCommand { get; }

    /// <summary>New Cell on workspace/library nodes.</summary>
    public IAsyncRelayCommand NewCellCommand { get; }

    /// <summary>New Folder — offered on anything a cell can be created in.</summary>
    public IAsyncRelayCommand NewFolderCommand { get; }

    /// <summary>New Technology… on workspace/library nodes.</summary>
    public IAsyncRelayCommand NewTechnologyCommand { get; }

    /// <summary>New Symbol on cell nodes.</summary>
    public IAsyncRelayCommand NewSymbolCommand { get; }

    /// <summary>New Schematic on cell nodes.</summary>
    public IAsyncRelayCommand NewSchematicCommand { get; }

    /// <summary>Open Known File with the OS default handler.</summary>
    public IRelayCommand OpenExternalCommand { get; }

    /// <summary>Copy an external Known File into the workspace and re-point the reference.</summary>
    public IRelayCommand CopyToWorkspaceCommand { get; }

    /// <summary>Copy a Known File view into a NEW cell in the workspace (validated first).</summary>
    public IAsyncRelayCommand CopyToWorkspaceAsCellCommand { get; }
    /// <summary>Builds a cell from a SPICE <c>.model</c> card in this file (Known File nodes only).</summary>
    public IAsyncRelayCommand CreateCellFromModelCardCommand { get; }

    /// <summary>Remove the Known File reference from .cws (does NOT delete the file on disk).</summary>
    public IRelayCommand RemoveKnownFileCommand { get; }

    /// <summary>Open this cell's primary schematic in a Content tab.</summary>
    public IRelayCommand OpenSchematicCommand { get; }

    /// <summary>Open this cell's primary symbol in a Content tab.</summary>
    public IRelayCommand OpenSymbolCommand { get; }

    /// <summary>Open this cell's primary layout in a Content tab.</summary>
    public IRelayCommand OpenLayoutCommand { get; }

    /// <summary>New Layout — prompts for a name, creates .clay in this cell's layout/ folder, opens it.</summary>
    public IAsyncRelayCommand NewLayoutCommand { get; }

    /// <summary>Remove this .cdd Data Display file (moves to Trash). Visible only for DataDisplayFile nodes.</summary>
    public IRelayCommand RemoveDataDisplayCommand { get; }

    /// <summary>Remove this file or directory (moves to Trash). Visible only for removable file/dir nodes.</summary>
    public IRelayCommand RemoveFileCommand { get; }

    /// <summary>Remove this cell folder (moves to Trash). Visible only for Cell nodes.</summary>
    public IAsyncRelayCommand RemoveCellCommand { get; }

    /// <summary>Duplicate this cell folder to a new name. Visible only for Cell nodes.</summary>
    public IAsyncRelayCommand DuplicateCellCommand { get; }

    /// <summary>Rename this cell folder. Visible only for Cell nodes.</summary>
    public IAsyncRelayCommand RenameCellCommand { get; }

    /// <summary>Save this node: cell saves all dirty views; file saves only itself.</summary>
    public IAsyncRelayCommand SaveCommand { get; }

    /// <summary>Write this .ctech node into .cws as the workspace default technology.</summary>
    public IRelayCommand SetAsWorkspaceDefaultCommand { get; }

    /// <summary>Invalidate the cached Technology for this .ctech node (prompts first if a live,
    /// unsaved editor override exists for it).</summary>
    public IAsyncRelayCommand ReloadTechnologyCommand { get; }

    /// <summary>wbond.md §9.2 route 2 — add this .wBond's wires to the active schematic as a component.</summary>
    public IAsyncRelayCommand AddWBondToSchematicCommand { get; }

    /// <summary>wbond.md §9.2 route 3 — add this .wBond's wires AND its embedded geometry as a new cell.</summary>
    public IAsyncRelayCommand AddWBondAsCellCommand { get; }

    // ── Primary-view availability (computed once at construction for cell nodes) ──

    public bool CanOpenSchematic { get; }
    public bool CanOpenSymbol    { get; }
    public bool CanOpenLayout    { get; }

    // ── Tree state ─────────────────────────────────────────────────────────────

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; OnPropertyChanged(); } }
    }

    // True when the cell has a dirty (unsaved) editing session.
    // Set by WorkspaceViewModel when any session for this cell's .csch changes dirty state.
    private bool _isDirty;
    public bool IsDirty
    {
        get => _isDirty;
        set { if (_isDirty != value) { _isDirty = value; OnPropertyChanged(); } }
    }

    // All children (unfiltered) — used for expand-state collection before refresh.
    public ObservableCollection<ProjectTreeNodeViewModel> Children { get; }

    // Filtered children — the TreeDataTemplate's ItemsSource.  Cleared and rebuilt on
    // every filter-state change.  An empty collection means no expander arrow is shown.
    public ObservableCollection<ProjectTreeNodeViewModel> FilteredChildren { get; } = new();

    // ── Construction ───────────────────────────────────────────────────────────

    /// <param name="expandedPaths">
    /// Absolute paths that were expanded before a refresh; passed down recursively
    /// so restored expansion is set on initial construction (no second pass needed).
    /// </param>
    /// <param name="actions">
    /// Callback interface provided by WorkspaceViewModel; null when no workspace is set.
    /// </param>
    public ProjectTreeNodeViewModel(
        ProjectTreeNode         node,
        ProjectTreeFilterState  filter,
        HashSet<string>?        expandedPaths = null,
        ITreeActions?           actions       = null,
        ProjectTreeNodeViewModel? parent      = null)
    {
        _node    = node;
        _filter  = filter;
        _actions = actions;
        _parent  = parent;

        // Workspace root is always expanded initially; other nodes restore from saved paths.
        _isExpanded = node.Kind == NodeKind.Workspace
            || (expandedPaths?.Contains(node.AbsolutePath) ?? false);

        // Resolve primary-view availability for cell nodes (a couple of Directory.GetFiles calls).
        if (node.Kind == NodeKind.Cell)
        {
            CanOpenSchematic = CellFolder.ResolvePrimary(node.AbsolutePath, ViewType.Schematic).State
                is PrimaryState.SoleFile or PrimaryState.NamedPresent;
            CanOpenSymbol = CellFolder.ResolvePrimary(node.AbsolutePath, ViewType.Symbol).State
                is PrimaryState.SoleFile or PrimaryState.NamedPresent;
            CanOpenLayout = CellFolder.ResolvePrimary(node.AbsolutePath, ViewType.Layout).State
                is PrimaryState.SoleFile or PrimaryState.NamedPresent;
        }

        // Build child VMs recursively; each child applies the same filter state.
        Children = new ObservableCollection<ProjectTreeNodeViewModel>(
            node.Children.Select(c => new ProjectTreeNodeViewModel(c, filter, expandedPaths, actions, parent: this)));

        // React to filter-state changes — ONLY at the root. ApplyFilter is already a full recursive
        // pass, and it has to be: the text filter (§3.3a) hands each node whether an ANCESTOR matched,
        // which a node re-filtering itself in isolation cannot know. Every node subscribing would run
        // n redundant passes and leave correctness resting on handler ORDER (children subscribe during
        // their own construction, so the root happens to run last and overwrite them) — true today,
        // and silently broken by any future change to when children are built.
        if (_parent is null)
            _filter.PropertyChanged += (_, e) =>
            {
                // HasSearchQuery is DERIVED from SearchQuery and is raised alongside it, so reacting
                // to both ran the entire filter TWICE for every keystroke — measured at 2 x 4,221
                // node passes and 16,882 collection notifications on a 600-cell workspace. Nothing
                // derived belongs in this set; only a real filter input does.
                if (e.PropertyName is nameof(ProjectTreeFilterState.HasSearchQuery)
                                   or nameof(ProjectTreeFilterState.IsSearching)
                                   or nameof(ProjectTreeFilterState.SearchTerm)) return;
                ApplyFilter();
            };

        ApplyFilter();

        // ── Wire commands ──────────────────────────────────────────────────────

        ActivateCommand = new RelayCommand(() => _actions?.OpenNode(this));

        MakePrimaryCommand = new RelayCommand(
            () => _actions?.MakePrimary(this),
            () => _actions is not null && IsViewFile);

        RevealCommand = new RelayCommand(
            () => _actions?.Reveal(this),
            () => _actions is not null && CanReveal);

        NewCellCommand = new AsyncRelayCommand(
            () => _actions?.NewCellAsync(this) ?? Task.CompletedTask,
            () => _actions is not null && CanCreateInside);

        NewFolderCommand = new AsyncRelayCommand(
            () => _actions?.NewFolderAsync(this) ?? Task.CompletedTask,
            () => _actions is not null && CanCreateInside);

        NewTechnologyCommand = new AsyncRelayCommand(
            () => _actions?.NewTechnologyAsync(this) ?? Task.CompletedTask,
            () => _actions is not null && CanCreateInside);

        NewSymbolCommand = new AsyncRelayCommand(
            () => _actions?.NewSymbolAsync(this) ?? Task.CompletedTask,
            () => _actions is not null && IsCell);

        NewSchematicCommand = new AsyncRelayCommand(
            () => _actions?.NewSchematicAsync(this) ?? Task.CompletedTask,
            () => _actions is not null && IsCell);

        NewLayoutCommand = new AsyncRelayCommand(
            () => _actions?.NewLayoutAsync(this) ?? Task.CompletedTask,
            () => _actions is not null && IsCell);

        OpenExternalCommand = new RelayCommand(
            () => _actions?.OpenExternal(this),
            () => _actions is not null && IsKnownFile && !IsWarning);

        CopyToWorkspaceCommand = new RelayCommand(
            () => _actions?.CopyToWorkspace(this),
            () => _actions is not null && IsKnownFile && !IsWarning && !IsInsideWorkspace);

        CopyToWorkspaceAsCellCommand = new AsyncRelayCommand(
            () => _actions?.CopyKnownFileToWorkspaceAsCellAsync(this) ?? Task.CompletedTask,
            () => _actions is not null && IsKnownFileCopyableAsCell);

        CreateCellFromModelCardCommand = new AsyncRelayCommand(
            () => _actions?.CreateCellFromModelCardAsync(this) ?? Task.CompletedTask,
            () => _actions is not null && IsModelCardFile);

        RemoveKnownFileCommand = new RelayCommand(
            () => _actions?.RemoveKnownFile(this),
            () => _actions is not null && IsKnownFile);

        OpenSchematicCommand = new RelayCommand(
            () => _actions?.OpenCellSchematic(this),
            () => _actions is not null && IsCell && CanOpenSchematic);

        OpenSymbolCommand = new RelayCommand(
            () => _actions?.OpenCellSymbol(this),
            () => _actions is not null && IsCell && CanOpenSymbol);

        OpenLayoutCommand = new RelayCommand(
            () => _actions?.OpenCellLayout(this),
            () => _actions is not null && IsCell && CanOpenLayout);

        RemoveDataDisplayCommand = new RelayCommand(
            () => _actions?.RemoveDataDisplay(this),
            () => _actions is not null && IsDataDisplayFile);

        RemoveFileCommand = new RelayCommand(
            () => _actions?.RemoveFile(this),
            () => _actions is not null && IsRemovableFile);

        RemoveCellCommand = new AsyncRelayCommand(
            () => _actions?.RemoveCellAsync(this) ?? Task.CompletedTask,
            () => _actions is not null && IsCell);

        DuplicateCellCommand = new AsyncRelayCommand(
            () => _actions?.DuplicateCellAsync(this) ?? Task.CompletedTask,
            () => _actions is not null && IsCell);

        RenameCellCommand = new AsyncRelayCommand(
            () => _actions?.RenameCellAsync(this) ?? Task.CompletedTask,
            () => _actions is not null && IsCell);

        SaveCommand = new AsyncRelayCommand(
            () => _actions?.SaveNodeAsync(this) ?? Task.CompletedTask);

        SetAsWorkspaceDefaultCommand = new RelayCommand(
            () => _actions?.SetAsWorkspaceDefault(this),
            () => _actions is not null && IsTechFile);

        ReloadTechnologyCommand = new AsyncRelayCommand(
            () => _actions?.ReloadTechnologyAsync(this) ?? Task.CompletedTask,
            () => _actions is not null && IsTechFile);

        AddWBondToSchematicCommand = new AsyncRelayCommand(
            () => _actions?.AddWBondToSchematicAsync(this) ?? Task.CompletedTask,
            () => _actions is not null && IsWBondFile);

        AddWBondAsCellCommand = new AsyncRelayCommand(
            () => _actions?.AddWBondAsCellAsync(this) ?? Task.CompletedTask,
            () => _actions is not null && IsWBondFile);
    }

    /// <summary>
    /// Fires INPC for properties that depend on live state (dirty flag, dirty-header) so the
    /// context menu shows current values when it opens.  Called by the Opening handler in the view.
    /// </summary>
    public void RefreshDynamicMenuState()
    {
        OnPropertyChanged(nameof(IsSaveable));
        OnPropertyChanged(nameof(SaveHeader));
        OnPropertyChanged(nameof(IsWorkspaceDefaultTech));
    }

    // ── Filter ─────────────────────────────────────────────────────────────────

    /// <param name="ancestorMatchedSearch">True when this node's own name did not have to match
    /// because an ANCESTOR's did. Without this, searching for a cell would show the cell row and
    /// then hide everything inside it (its schematic/symbol/layout are named for their views, not
    /// for the cell), so the one row you were looking for could not be opened into.</param>
    internal void ApplyFilter(bool ancestorMatchedSearch = false)
    {
        _ownNameMatched  = _filter.IsSearching && MatchesSearchText();
        _searchSatisfied = ancestorMatchedSearch || MatchesSearchText();

        // Bottom-up: children must be filtered before we check ancestor-preservation.
        foreach (var child in Children)
            child.ApplyFilter(_searchSatisfied);

        // Is there a real match somewhere below? This is what the auto-expand reads — NOT "do I have
        // visible children", which is true of everything a match passes through.
        _matchBelow = false;
        foreach (var child in Children)
            if (child._ownNameMatched || child._matchBelow) { _matchBelow = true; break; }

        if (SyncFilteredChildren())
            OnPropertyChanged(nameof(FilteredChildren));

        ApplySearchExpansion();
    }

    /// <summary>
    /// Brings <see cref="FilteredChildren"/> in line with what the filters now allow, touching it ONLY
    /// where it actually differs. Returns true when anything changed.
    ///
    /// <para><b>This used to be <c>Clear()</c> followed by re-adding every visible child</b>, and that
    /// is what made typing in the search box feel slow (owner, 2026-08-25). The matching itself is
    /// cheap — 1.5 ms for a 4,221-node tree — but each of those notifications is work Avalonia must do
    /// on the UI thread: a Reset tears down every realized container for that node, and each Add
    /// builds one back. <c>ObservableCollection.Clear()</c> raises its Reset <b>even when the
    /// collection is empty</b>, so every LEAF in the tree announced a teardown on every keystroke while
    /// its (zero) children could not possibly have changed. Measured: ~16,882 notifications per
    /// keystroke, against a few dozen that describe a real change.</para>
    ///
    /// <para>Both lists are subsequences of <see cref="Children"/> in the same order, which is what
    /// makes the walk below a single pass rather than a general diff.</para>
    /// </summary>
    private bool SyncFilteredChildren()
    {
        _desired.Clear();
        foreach (var child in Children)
            if (child.IsVisibleUnderFilter())
                _desired.Add(child);

        // The overwhelmingly common case on a keystroke: this node's visible set is exactly what it
        // already was. Say nothing at all.
        if (_desired.Count == FilteredChildren.Count)
        {
            bool same = true;
            for (int i = 0; i < _desired.Count; i++)
                if (!ReferenceEquals(_desired[i], FilteredChildren[i])) { same = false; break; }
            if (same) return false;
        }

        int at = 0;
        foreach (var want in _desired)
        {
            if (at < FilteredChildren.Count && ReferenceEquals(FilteredChildren[at], want)) { at++; continue; }

            // Still present further along? Then everything between was filtered out — remove exactly
            // those, rather than removing `want` too and adding it straight back.
            int found = -1;
            for (int i = at + 1; i < FilteredChildren.Count; i++)
                if (ReferenceEquals(FilteredChildren[i], want)) { found = i; break; }

            if (found >= 0)
                for (int i = found - 1; i >= at; i--) FilteredChildren.RemoveAt(i);
            else
                FilteredChildren.Insert(at, want);   // newly visible

            at++;
        }

        while (FilteredChildren.Count > at) FilteredChildren.RemoveAt(FilteredChildren.Count - 1);
        return true;
    }

    /// <summary>Scratch buffer for <see cref="SyncFilteredChildren"/> — per node rather than per call,
    /// so a full-tree pass allocates nothing.</summary>
    private readonly List<ProjectTreeNodeViewModel> _desired = [];

    /// <summary>True when this node is on a path the text filter kept — either its own name matched
    /// or an ancestor's did. Always true when no text filter is active.</summary>
    private bool _searchSatisfied = true;

    /// <summary>True when THIS node's own name matched the live query — not "was kept by the filter",
    /// which is also true of everything a matched ancestor passes through.</summary>
    private bool _ownNameMatched;

    /// <summary>True when some descendant's own name matched. Together with
    /// <see cref="_ownNameMatched"/> this is the auto-expand rule — see
    /// <see cref="ApplySearchExpansion"/>.</summary>
    private bool _matchBelow;

    /// <summary>This node's expansion state before the current search forced it open, or null when
    /// no search has forced it. Restored the moment the search box is cleared, so browsing a large
    /// workspace does not leave every folder hanging open behind the user.</summary>
    private bool? _expandedBeforeSearch;

    private bool MatchesSearchText()
    {
        if (!_filter.HasSearchQuery) return true;

        // The workspace ROOT never matches on its own name (owner, 2026-08-25: "if I enter the
        // project name into the filter search, all the files show up"). Its Name IS the workspace
        // name, and a node whose name matches passes its ENTIRE subtree through — which is the right
        // rule for a folder the user can see and clicked toward, and catastrophic here, because the
        // root is the ancestor of everything. Typing the workspace's own name therefore granted the
        // whole tree a free pass and the filter appeared to do nothing.
        //
        // It is also the one node that is never RENDERED: the panel header already names the
        // workspace and the TreeView binds to this node's children, so there is not even a matched
        // row on screen to explain the result. Excluding it costs nothing — no visible row is lost.
        if (Kind == NodeKind.Workspace) return false;

        return Name.Contains(_filter.SearchTerm, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A match six levels down is invisible behind a collapsed folder, so a live search opens the
    /// path to it — and puts it back the way it was when the box is cleared.
    ///
    /// <para><b>It opens the path to a match and the match itself, and nothing else.</b> The rule was
    /// originally "expand anything with visible children", which is true of every node a match passes
    /// its subtree through — so typing a single character expanded the ENTIRE tree and the TreeView
    /// had to realize a container for all 4,220 nodes of a 600-cell workspace (measured). That is a
    /// search REVEALING its matches vs. a search flinging every folder open, and the difference is
    /// most of what "search feels slow" was: the deepest and most numerous level — a matched cell's
    /// <c>schematic</c>/<c>symbol</c>/<c>layout</c> folders and their files — is exactly the part
    /// nobody searched for.</para>
    ///
    /// <para>A matched node is still expanded itself, so typing an import folder's name does show what
    /// is inside it (§1.1a) — one level, not its whole subtree.</para>
    /// </summary>
    private void ApplySearchExpansion()
    {
        if (_filter.IsSearching)
        {
            _expandedBeforeSearch ??= IsExpanded;
            if (_matchBelow || _ownNameMatched) IsExpanded = true;
        }
        else if (_expandedBeforeSearch is { } saved)
        {
            IsExpanded = saved;
            _expandedBeforeSearch = null;
        }
    }

    private bool IsVisibleUnderFilter()
    {
        var f = _filter;
        bool ownMatch = Kind switch
        {
            NodeKind.Workspace       => true,
            // A cell satisfies Cells; a TestBench cell ALSO satisfies TestBenches.
            NodeKind.Cell            => f.Cells || (f.TestBenches && IsTestBench),
            NodeKind.CellViewFolder  => f.Cells,
            NodeKind.ViewFile        => f.Cells,
            NodeKind.Library         => f.Libraries,
            NodeKind.LibrariesGroup  => f.Libraries,
            // A referenced workspace's cells are cells; the branch itself rides the Libraries
            // toggle, which is the "things this workspace REACHES rather than contains" filter.
            NodeKind.ReferencedWorkspace       => f.Libraries,
            NodeKind.ReferencedWorkspacesGroup => f.Libraries,
            NodeKind.DataDisplayFile => f.DataDisplays,
            // A .charm is a results-facing document beside a .cdd — same toggle, no seventh checkbox.
            NodeKind.HarmonicaFile   => f.DataDisplays,
            // A .wBond is a design document; it rides the Cells toggle rather than earning its own.
            NodeKind.WBondFile       => f.Cells,
            NodeKind.ColorThemeFile  => f.ColorThemes,
            NodeKind.TechFile        => f.TechFiles,
            // An EM setup is process/analysis configuration alongside the technology it reads, so it
            // rides the same filter toggle rather than earning a seventh checkbox of its own.
            NodeKind.EmSetupFile     => f.TechFiles,
            NodeKind.KnownFile       => f.KnownFiles,
            NodeKind.KnownFilesGroup => f.KnownFiles,
            NodeKind.UserFolder      => f.WorkspaceFileSystem,
            NodeKind.OtherFile       => f.WorkspaceFileSystem,
            _                        => true,
        };

        // Ancestor-preservation: visible if this node survives BOTH filters on its own merits, OR
        // any descendant is visible. The two filters are ANDed — a category toggle that hides a kind
        // must go on hiding it while a search is running, or clearing a checkbox would appear to do
        // nothing the moment the user typed anything.
        return (ownMatch && _searchSatisfied) || FilteredChildren.Count > 0;
    }
}
