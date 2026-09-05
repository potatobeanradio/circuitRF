using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.ViewModels.ProjectTree;

/// <summary>
/// Independently-toggleable category filter for the Project Tree (§3.3).
/// All categories on = full tree; any subset = hide nodes whose primary category is off
/// (ancestors of visible nodes remain visible).
/// </summary>
public partial class ProjectTreeFilterState : ObservableObject
{
    [ObservableProperty] private bool _cells               = true;
    [ObservableProperty] private bool _libraries           = true;
    [ObservableProperty] private bool _testBenches         = true;
    [ObservableProperty] private bool _dataDisplays        = true;
    [ObservableProperty] private bool _colorThemes         = true;
    [ObservableProperty] private bool _techFiles            = true;
    [ObservableProperty] private bool _knownFiles          = true;
    [ObservableProperty] private bool _workspaceFileSystem = true;

    /// <summary>
    /// The root-level rows for cells referenced one at a time out of another workspace, and
    /// everything under them. Separate from <see cref="Libraries"/> and from
    /// <see cref="ReferencedWorkspaces"/> because they are three different acts: a library is a
    /// folder of parts, a referenced workspace is someone else's whole project, and a referenced cell
    /// is exactly one design taken from it.
    /// </summary>
    [ObservableProperty] private bool _referencedCells      = true;

    /// <summary>The root-level rows for whole referenced workspaces, and everything under them.</summary>
    [ObservableProperty] private bool _referencedWorkspaces = true;

    /// <summary>
    /// Free-text name filter (owner, 2026-08-25: a board import can add dozens of cells at once,
    /// which makes the user's own cells hard to find). Lives HERE, beside the category toggles,
    /// rather than on the tool: every node VM already subscribes to this object's PropertyChanged
    /// and re-runs <c>ApplyFilter</c>, so the text filter reaches the whole tree through the exact
    /// mechanism the checkboxes use, with no second notification path.
    ///
    /// <para>Empty/whitespace means "no text filter" — NOT "match nothing".</para>
    /// </summary>
    [ObservableProperty] private string _searchQuery = "";

    partial void OnSearchQueryChanged(string value)
    {
        // Normalized ONCE per query, not once per node per pass: the filter is a full tree walk, and
        // Trim() inside the per-node match allocated a string for every node on every keystroke.
        SearchTerm = value?.Trim() ?? "";
        OnPropertyChanged(nameof(HasSearchQuery));
    }

    /// <summary>The trimmed query the node VMs actually match against. A plain property, deliberately:
    /// it is derived from <see cref="SearchQuery"/> and raising a notification for it would re-trigger
    /// every subscriber a second time — which is exactly the bug <see cref="HasSearchQuery"/> caused
    /// (see <c>ProjectTreeNodeViewModel</c>'s subscription).</summary>
    public string SearchTerm { get; private set; } = "";

    public bool HasSearchQuery => SearchTerm.Length > 0;

    /// <summary>True when a name filter is active — the node VMs read this to decide whether to
    /// force-expand the paths down to a match (and to restore expansion when it clears).</summary>
    public bool IsSearching => HasSearchQuery;

    public bool IsAllOn =>
        Cells && Libraries && TestBenches && DataDisplays && ColorThemes && TechFiles && KnownFiles
        && WorkspaceFileSystem && ReferencedCells && ReferencedWorkspaces;

    public void SetAll(bool value)
    {
        Cells = Libraries = TestBenches = DataDisplays = ColorThemes = TechFiles = KnownFiles
              = WorkspaceFileSystem = ReferencedCells = ReferencedWorkspaces = value;
    }
}
