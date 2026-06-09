using Dock.Model.Mvvm.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.ViewModels.Dock;

/// <summary>
/// Dock Tool for the Properties region. Hosts the ParameterEditorViewModel, which tracks
/// the active schematic's selection and shows/edits the selected component's parameters.
/// </summary>
public partial class PropertiesTool : Tool
{
    [ObservableProperty]
    private ParameterEditorViewModel _editorVm = new();

    public PropertiesTool()
    {
        Id    = "Properties";
        Title = "Properties";
    }

    /// <summary>Called by WorkspaceViewModel when the active schematic document changes.</summary>
    public void SetActiveSchematic(SchematicViewModel? vm) => EditorVm.SetContext(vm);
}
