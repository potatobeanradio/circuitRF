using Dock.Model.Mvvm.Controls;

namespace CircuitRF.Ui.ViewModels.Dock;

/// <summary>
/// Dock Tool for the Properties region (left, below the Project Tree). Hosts the Component
/// Palette in 6c/6d; in 6b this is an empty placeholder region.
/// </summary>
public class PropertiesTool : Tool
{
    public PropertiesTool()
    {
        Id    = "Properties";
        Title = "Properties";
    }
}
