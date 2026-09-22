using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Mvvm.Controls;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.ViewModels.Dock;

/// <summary>
/// Dock Tool for the LVS results panel — <b><see cref="DrcTool"/>'s shape, not a new one</b>
/// (brief-lvs-12-gui.md R-lvs12-1a): a list, click to zoom to the marker, severity grouping, a run
/// button, a summary strip.
///
/// <para>Follows the active LAYOUT document, exactly as <see cref="DrcTool"/> does —
/// <see cref="SetActiveLayout"/> is called from the same places. The panel holds no result of its
/// own: an LVS result belongs to the cell that was compared, and showing one cell's findings while
/// another is on screen would be worse than showing none.</para>
///
/// <para><b>It binds <c>LvsRunResult</c> and there is no second result type</b> (brief §7). Every
/// row, every count and every marker on the canvas is a projection of the object
/// <c>LvsRun.Run</c> returned — the same object <c>circuitrf lvs</c> prints.</para>
/// </summary>
public partial class LvsTool : Tool
{
    [ObservableProperty] private LayoutEditorViewModel? _editorVm;

    /// <summary>True when a layout document is active — otherwise the panel shows why it is empty.</summary>
    [ObservableProperty] private bool _isLayoutActive;

    public LvsTool()
    {
        Id    = DockPanelIds.Lvs;
        Title = "LVS";
    }

    public void SetActiveLayout(LayoutEditorViewModel? vm)
    {
        EditorVm       = vm;
        IsLayoutActive = vm is not null;
    }
}
