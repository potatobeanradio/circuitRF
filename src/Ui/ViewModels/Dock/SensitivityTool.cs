using CircuitRF.Ui.Docking;
using CircuitRF.Ui.Yield;

namespace CircuitRF.Ui.ViewModels.Dock;

/// <summary>
/// Dock Tool for the Sensitivity panel (brief-sens-3 R-sens3-1): its own <see cref="YieldPanelViewModel"/> whose only
/// mode is <see cref="YieldMode.Sensitivity"/>, in the Yield panel's view. Tabbed between Tuning and the Optimizer by
/// default; it edits the same schematic's one tuning block as the Yield panel does.
/// </summary>
public sealed class SensitivityTool : YieldPanelTool
{
    public SensitivityTool() : base(new YieldPanelViewModel { AvailableModes = YieldPanelViewModel.SensitivityModes })
    {
        Id    = DockPanelIds.Sensitivity;
        Title = "Sensitivity";
    }
}
