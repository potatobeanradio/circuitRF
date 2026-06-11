using Dock.Model.Mvvm.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.ViewModels.Dock;

/// <summary>
/// Dock Tool for the Properties region. Hosts the ParameterEditorViewModel (schematic) and
/// SymbolPrimitiveInspectorViewModel (symbol editor); switches active context on tab change.
/// </summary>
public partial class PropertiesTool : Tool
{
    [ObservableProperty]
    private ParameterEditorViewModel _editorVm = new();

    [ObservableProperty]
    private SymbolPrimitiveInspectorViewModel _symbolInspectorVm = new();

    /// <summary>
    /// True when a symbol editor document is active; the Properties pane shows the
    /// symbol primitive inspector rather than the schematic parameter editor.
    /// </summary>
    [ObservableProperty]
    private bool _isSymbolEditorActive;

    public PropertiesTool()
    {
        Id    = "Properties";
        Title = "Properties";
    }

    /// <summary>Called by WorkspaceViewModel when the active schematic document changes.</summary>
    public void SetActiveSchematic(SchematicViewModel? vm)
    {
        IsSymbolEditorActive = false;
        EditorVm.SetContext(vm);
        SymbolInspectorVm.SetContext(null);
    }

    /// <summary>Called by WorkspaceViewModel when the active symbol-editor document changes.</summary>
    public void SetActiveSymbolEditor(SymbolEditorViewModel? vm)
    {
        IsSymbolEditorActive = vm is not null;
        EditorVm.SetContext(null);
        SymbolInspectorVm.SetContext(vm);
    }
}
