using Dock.Model.Mvvm.Controls;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.Yield;

namespace CircuitRF.Ui.ViewModels.Dock;

/// <summary>
/// A dock Tool hosting a <see cref="YieldPanelViewModel"/> in the one <c>YieldToolView</c> (brief-sens-3 R-sens3-1): the
/// Yield panel and the Sensitivity panel are this view over two panels with different mode sets — no second view and
/// no copied XAML. The panel itself knows nothing of Dock.
/// </summary>
public abstract class YieldPanelTool : Tool
{
    protected YieldPanelTool(YieldPanelViewModel panel) => Panel = panel;

    public YieldPanelViewModel Panel { get; }
}

/// <summary>
/// Dock Tool for the Yield panel (brief-yield-10). Follows the focused schematic exactly as Tuning and the Optimizer
/// do, and is tabbed behind the Optimizer by default.
/// </summary>
public sealed class YieldTool : YieldPanelTool
{
    public YieldTool() : base(new YieldPanelViewModel())
    {
        Id    = DockPanelIds.Yield;
        Title = "Yield";
    }
}
