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
    private readonly ITreeActions?          _actions;

    // ── Identity / model data (read from the node — never re-derived here) ────

    public NodeKind  Kind          => _node.Kind;
    public bool      IsPrimary     => _node.IsPrimary;
    public bool      IsTestBench   => _node.IsTestBench;
    public string?   WarningReason => _node.WarningReason;
    public string    RelativePath  => _node.RelativePath;
    public string    AbsolutePath  => _node.AbsolutePath;

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
    /// Path shown in the node tooltip.
    /// Known File directories show the absolute path (the label already shows relative).
    /// All other nodes show the relative path.
    /// </summary>
    public string TooltipPath => (Kind == NodeKind.KnownFile && _node.IsDirectory)
        ? _node.AbsolutePath
        : _node.RelativePath;

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
        (NodeKind.CellViewFolder,  _)     => MaterialIconKind.FolderOutline,
        (NodeKind.ViewFile,        _)     => MaterialIconKind.FileOutline,
        (NodeKind.DataDisplayFile, _)     => MaterialIconKind.ChartLine,
        (NodeKind.ColorThemeFile,  _)     => MaterialIconKind.Palette,
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
                                          or NodeKind.ColorThemeFile
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

    /// <summary>New Symbol on cell nodes.</summary>
    public IAsyncRelayCommand NewSymbolCommand { get; }

    /// <summary>New Schematic on cell nodes.</summary>
    public IAsyncRelayCommand NewSchematicCommand { get; }

    /// <summary>Open Known File with the OS default handler.</summary>
    public IRelayCommand OpenExternalCommand { get; }

    /// <summary>Copy an external Known File into the workspace and re-point the reference.</summary>
    public IRelayCommand CopyToWorkspaceCommand { get; }

    /// <summary>Remove the Known File reference from .cws (does NOT delete the file on disk).</summary>
    public IRelayCommand RemoveKnownFileCommand { get; }

    // ── Tree state ─────────────────────────────────────────────────────────────

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; OnPropertyChanged(); } }
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
        ITreeActions?           actions       = null)
    {
        _node    = node;
        _filter  = filter;
        _actions = actions;

        // Workspace root is always expanded initially; other nodes restore from saved paths.
        _isExpanded = node.Kind == NodeKind.Workspace
            || (expandedPaths?.Contains(node.AbsolutePath) ?? false);

        // Build child VMs recursively; each child applies the same filter state.
        Children = new ObservableCollection<ProjectTreeNodeViewModel>(
            node.Children.Select(c => new ProjectTreeNodeViewModel(c, filter, expandedPaths, actions)));

        // React to filter-toggle changes.
        _filter.PropertyChanged += (_, _) => ApplyFilter();

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
            () => _actions is not null && IsWorkspaceOrLibrary);

        NewSymbolCommand = new AsyncRelayCommand(
            () => _actions?.NewSymbolAsync(this) ?? Task.CompletedTask,
            () => _actions is not null && IsCell);

        NewSchematicCommand = new AsyncRelayCommand(
            () => _actions?.NewSchematicAsync(this) ?? Task.CompletedTask,
            () => _actions is not null && IsCell);

        OpenExternalCommand = new RelayCommand(
            () => _actions?.OpenExternal(this),
            () => _actions is not null && IsKnownFile && !IsWarning);

        CopyToWorkspaceCommand = new RelayCommand(
            () => _actions?.CopyToWorkspace(this),
            () => _actions is not null && IsKnownFile && !IsWarning && !IsInsideWorkspace);

        RemoveKnownFileCommand = new RelayCommand(
            () => _actions?.RemoveKnownFile(this),
            () => _actions is not null && IsKnownFile);
    }

    // ── Filter ─────────────────────────────────────────────────────────────────

    internal void ApplyFilter()
    {
        // Bottom-up: children must be filtered before we check ancestor-preservation.
        foreach (var child in Children)
            child.ApplyFilter();

        FilteredChildren.Clear();
        foreach (var child in Children)
        {
            if (child.IsVisibleUnderFilter())
                FilteredChildren.Add(child);
        }
        OnPropertyChanged(nameof(FilteredChildren));
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
            NodeKind.DataDisplayFile => f.DataDisplays,
            NodeKind.ColorThemeFile  => f.ColorThemes,
            NodeKind.KnownFile       => f.KnownFiles,
            NodeKind.KnownFilesGroup => f.KnownFiles,
            NodeKind.UserFolder      => f.WorkspaceFileSystem,
            NodeKind.OtherFile       => f.WorkspaceFileSystem,
            _                        => true,
        };

        // Ancestor-preservation: visible if own category matches OR any descendant is visible.
        return ownMatch || FilteredChildren.Count > 0;
    }
}
