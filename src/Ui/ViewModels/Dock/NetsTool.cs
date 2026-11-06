using Dock.Model.Mvvm.Controls;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Nets;

namespace CircuitRF.Ui.ViewModels.Dock;

/// <summary>Dock Tool for the Nets panel (brief-ipx-1 R-ipx1-1a), <see cref="ReviewTool"/>'s shape: it follows the active layout.</summary>
public sealed class NetsTool : Tool
{
    public NetsTool()
    {
        Id    = DockPanelIds.Nets;
        Title = "Nets";
    }

    /// <summary>The panel itself: the list, its filter, and what selecting a row does on the canvas.</summary>
    public NetsPanelViewModel Panel { get; } = new();

    public void SetActiveLayout(LayoutEditorViewModel? vm) => Panel.SetEditor(vm);
}
