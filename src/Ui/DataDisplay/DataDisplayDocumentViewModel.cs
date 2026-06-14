using CommunityToolkit.Mvvm.ComponentModel;
using CircuitRF.Ui.DataDisplay.ViewModels;

namespace CircuitRF.Ui.DataDisplay;

/// <summary>
/// View model for a Data Display document tab (document shell — not the canvas VM).
/// </summary>
public sealed partial class DataDisplayDocumentViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isDirty;

    /// <summary>
    /// The ported DisplayWindowViewModel — owns tabs, canvas VMs, and commands.
    /// Wrapped here rather than merged so the ported VM stays intact.
    /// </summary>
    public DisplayWindowViewModel Window { get; }

    public DataDisplayDocumentViewModel()
    {
        Window = new DisplayWindowViewModel();
    }
}
