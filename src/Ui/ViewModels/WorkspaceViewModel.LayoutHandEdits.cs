using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// brief-sdl-4 R-sdl4-2: Re-snap and Restore Removed Parts, run from a LAYOUT. Both are Update Layout from Schematic
/// with one more planner argument — the parts to re-join, or the parts to put back — so they place nothing
/// themselves: the planner decides every position, exactly as it does for the ordinary run, and the run is one undo
/// step. What is here is finding the schematic the layout was generated from.
/// </summary>
public partial class WorkspaceViewModel
{
    /// <summary>Design ▸ Restore Removed Parts: the linked parts deleted from the active layout, put back and joined to
    /// their neighbours.</summary>
    [RelayCommand(CanExecute = nameof(IsLayoutDocumentActive))]
    private void RestoreRemovedParts()
    {
        if (ResolveActiveDocumentForCommands() is LayoutDocument doc)
            RunLayoutUpdateFromLayout(doc, resnap: null, restoreRemoved: true, "Restore Removed Parts");
    }

    /// <summary>The context menu's Re-snap, on the selected linked parts.</summary>
    void ILayoutHierarchyHost.Resnap(LayoutDocument doc, IReadOnlySet<string> schematicIds) =>
        RunLayoutUpdateFromLayout(doc, schematicIds, restoreRemoved: false, "Re-snap");

    private void RunLayoutUpdateFromLayout(LayoutDocument doc, IReadOnlySet<string>? resnap, bool restoreRemoved, string command)
    {
        if (doc.FilePath is not { Length: > 0 } layoutPath)
        {
            Messages.Error($"{command}: save the layout first — it runs Update Layout from Schematic, which needs the cell's schematic.");
            return;
        }
        var sibling = CellFolder.SiblingView(layoutPath, ViewType.Layout, ViewType.Schematic);
        string schematicPath = Path.GetFullPath(sibling.TargetPath);
        if (!File.Exists(schematicPath))
        {
            Messages.Error($"{command}: there is no schematic '{Path.GetFileName(schematicPath)}' beside this layout to update it from.");
            return;
        }

        OpenOrActivateSchematic(schematicPath);
        if (_openDocsByPath.GetValueOrDefault(schematicPath) is not SchematicDocument schematic) return;
        RunLayoutUpdate(schematic, onlyWBond: null, resnap, restoreRemoved);
    }
}
