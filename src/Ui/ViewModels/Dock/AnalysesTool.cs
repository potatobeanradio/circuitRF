using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Mvvm.Controls;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.ViewModels.Dock;

/// <summary>
/// Dock Tool hosting the <see cref="AnalysesListViewModel"/> in the left panel.
/// Tracks the active schematic by mirroring the same wiring as <see cref="PropertiesTool"/>.
/// </summary>
public sealed partial class AnalysesTool : Tool
{
    [ObservableProperty]
    private AnalysesListViewModel _listVm = new();

    public AnalysesTool()
    {
        Id    = "Analyses";
        Title = "Analyses";
    }

    /// <summary>Called by WorkspaceViewModel when the active schematic document changes.</summary>
    public void SetActiveSchematic(SchematicViewModel? vm, string? schematicName = null)
        => ListVm.SetActiveSchematic(vm, schematicName);

    /// <summary>Called by WorkspaceViewModel when the open workspace changes (or is cleared).</summary>
    public void SetWorkspaceDir(string? workspaceDir) => ListVm.SetWorkspaceDir(workspaceDir);
}
