namespace CircuitRF.Ui.Schematic;

// ──────────────────────────────────────────────────────────────────────────────
//  Project Tree — node model (framework-free).
//  Phase 6g Step 2.  See workspace-and-project-tree.md §1/§2/§3.1/§3.2.
//  The tree is rebuilt from scratch by WorkspaceScanner.Scan on every refresh;
//  nodes are transient — Id is never persisted, and nothing here is serialized.
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Discriminates what a <see cref="ProjectTreeNode"/> represents.
/// Carries enough category information for the §3.3 filter toggles in step 3.
/// </summary>
public enum NodeKind
{
    /// <summary>The workspace root folder.</summary>
    Workspace,

    /// <summary>A cell folder (has a .ccell at its root).</summary>
    Cell,

    /// <summary>A referenced external library folder.</summary>
    Library,

    /// <summary>Synthetic group node that contains all Library children.</summary>
    LibrariesGroup,

    /// <summary>One of the schematic / symbol / layout sub-folders inside a cell.</summary>
    CellViewFolder,

    /// <summary>A view file (.csch / .csym / .clay) inside a CellViewFolder.</summary>
    ViewFile,

    /// <summary>An arbitrary workspace sub-folder that is not a cell.</summary>
    UserFolder,

    /// <summary>A .cdd data-display file.</summary>
    DataDisplayFile,

    /// <summary>A .ccolor color-theme file.</summary>
    ColorThemeFile,

    /// <summary>A .ctech technology file.</summary>
    TechFile,

    /// <summary>A .cem EM setup file (brief-L6-L7-em-ui.md D1/R-em-9). Workspace-scoped and never
    /// scratch, exactly like <see cref="TechFile"/>.</summary>
    EmSetupFile,

    /// <summary>Any other file not covered by the above kinds.</summary>
    OtherFile,

    /// <summary>A path bookmarked in the .cws Known Files list.</summary>
    KnownFile,

    /// <summary>Synthetic group node that contains all KnownFile children.</summary>
    KnownFilesGroup,
}

/// <summary>
/// One node in the in-memory project tree produced by <see cref="WorkspaceScanner.Scan"/>.
/// Framework-free; headless-testable; rebuilt on every <see cref="WorkspaceModel.Rescan"/>.
/// </summary>
public sealed class ProjectTreeNode
{
    private readonly List<ProjectTreeNode> _children = [];

    // ── Identity ──────────────────────────────────────────────────────────────

    public NodeKind Kind         { get; }
    public string   Name         { get; }
    /// <summary>Absolute filesystem path.  For synthetic group nodes, the workspace root dir.</summary>
    public string   AbsolutePath { get; }
    /// <summary>Path relative to the workspace root.  Empty for the root itself and group nodes.</summary>
    public string   RelativePath { get; }

    // ── Children ──────────────────────────────────────────────────────────────

    public IReadOnlyList<ProjectTreeNode> Children => _children;

    // ── Per-node state flags (DATA, not rendering) ────────────────────────────

    /// <summary>
    /// True for a <see cref="NodeKind.ViewFile"/> whose primacy resolved to
    /// <see cref="PrimaryState.SoleFile"/> or <see cref="PrimaryState.NamedPresent"/>.
    /// </summary>
    public bool    IsPrimary     { get; }

    /// <summary>
    /// True for a <see cref="NodeKind.Cell"/> whose .ccell sets IsTestBench.
    /// </summary>
    public bool    IsTestBench   { get; }

    /// <summary>
    /// Non-null when the node has a warning.  The step-3 view renders System.Warning
    /// color + italics and uses this string as the tooltip reason.
    /// Possible causes:
    ///   • Library path unresolved
    ///   • Known File path not found
    ///   • Cell's .ccell names a primary view that is absent (MissingNamedPrimary)
    /// </summary>
    public string? WarningReason { get; }

    // ── Construction ──────────────────────────────────────────────────────────

    /// <summary>
    /// True for a <see cref="NodeKind.KnownFile"/> whose path is a directory (not a file).
    /// Drives the folder-vs-file icon and the label/tooltip display in the tree.
    /// </summary>
    public bool IsDirectory { get; }

    public ProjectTreeNode(
        NodeKind kind,
        string   name,
        string   absolutePath,
        string   relativePath,
        bool     isPrimary     = false,
        bool     isTestBench   = false,
        string?  warningReason = null,
        bool     isDirectory   = false)
    {
        Kind          = kind;
        Name          = name;
        AbsolutePath  = absolutePath;
        RelativePath  = relativePath;
        IsPrimary     = isPrimary;
        IsTestBench   = isTestBench;
        WarningReason = warningReason;
        IsDirectory   = isDirectory;
    }

    /// <summary>Appends a child.  Called only by <see cref="WorkspaceScanner"/>.</summary>
    internal void AddChild(ProjectTreeNode child) => _children.Add(child);
}
