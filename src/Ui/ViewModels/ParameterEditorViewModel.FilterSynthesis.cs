using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// "Synthesise…" on the filter families (docs/design/filter-synthesis.md §6): opens the Filter Designer with the selected
/// part's family preselected. The view opens the window, as it does the Match Designer's, so this class stays free of
/// Avalonia.
/// </summary>
public partial class ParameterEditorViewModel
{
    /// <summary>MLAD, MCRC or MCLA: a family a realiser fills.</summary>
    public bool IsFilterSynthesisTarget => _target?.Symbol is SymbolKind.Mlad or SymbolKind.Mcrc or SymbolKind.Mcla;

    /// <summary>Raised by <see cref="OpenFilterDesignerCommand"/>; the view opens the window.</summary>
    public event Action<EditableComponent>? OpenFilterDesignerRequested;

    [RelayCommand]
    private void OpenFilterDesigner()
    {
        if (IsFilterSynthesisTarget) OpenFilterDesignerRequested?.Invoke(_target!);
    }
}
