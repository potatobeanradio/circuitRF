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
    [ObservableProperty] private bool _knownFiles          = true;
    [ObservableProperty] private bool _workspaceFileSystem = true;

    public bool IsAllOn =>
        Cells && Libraries && TestBenches && DataDisplays && ColorThemes && KnownFiles && WorkspaceFileSystem;

    public void SetAll(bool value)
    {
        Cells = Libraries = TestBenches = DataDisplays = ColorThemes = KnownFiles = WorkspaceFileSystem = value;
    }
}
