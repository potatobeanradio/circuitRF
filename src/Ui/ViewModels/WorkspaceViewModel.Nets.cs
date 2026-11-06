using CircuitRF.Ui.Docking;

namespace CircuitRF.Ui.ViewModels;

public partial class WorkspaceViewModel
{
    /// <summary>
    /// brief-ipx-1. View ▸ Panels ▸ Nets, on the Review panel's terms: where the panel was if anywhere is remembered, otherwise
    /// docked beside a side-column panel, since a list of nets wants height. False where the ordinary route should show it.
    /// </summary>
    private bool TryDockNetsPanel()
    {
        if (_factory.NetsTool is not { } tool) return false;
        if (_factory.IsToolAutoHidden(tool) || _factory.TryFindTool(tool, out _, out _)) return false;
        return RestorePanelToItsHome(DockPanelIds.Nets, tool)
            || DockPanelBesideSibling(tool, _factory.ReviewTool, _factory.ImpedanceTool, _factory.PropertiesTool,
                                      _factory.AnalysesTool, _factory.ProjectTreeTool);
    }
}
