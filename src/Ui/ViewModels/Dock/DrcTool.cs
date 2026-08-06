using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Mvvm.Controls;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.ViewModels.Dock;

/// <summary>
/// Dock Tool for the DRC violations panel (docs/design/layout-view.md §9A.1's "results surface":
/// a list of each hit, click-to-zoom, with markers drawn over the geometry).
///
/// <para>Follows the active LAYOUT document, exactly like <see cref="PropertiesTool"/> follows the
/// active editor — <see cref="SetActiveLayout"/> is called from
/// <c>WorkspaceViewModel.OnDocumentDockPropertyChanged</c>. The panel holds no result of its own: a
/// DRC result belongs to the layout that was checked, and showing one layout's violations while
/// another is on screen would be worse than showing none.</para>
/// </summary>
public partial class DrcTool : Tool
{
    [ObservableProperty] private LayoutEditorViewModel? _editorVm;

    /// <summary>True when a layout document is active — otherwise the panel shows why it is empty.</summary>
    [ObservableProperty] private bool _isLayoutActive;

    public DrcTool()
    {
        Id    = DockPanelIds.Drc;
        Title = "DRC";
    }

    public void SetActiveLayout(LayoutEditorViewModel? vm)
    {
        EditorVm       = vm;
        IsLayoutActive = vm is not null;
    }
}
