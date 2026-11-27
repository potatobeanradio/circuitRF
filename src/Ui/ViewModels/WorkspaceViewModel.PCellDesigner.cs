// Owner: PCD-6 (brief-pcd-6 R-pcd6-1, R-pcd6-7, R-pcd6-8). The PCell Designer's entry points on the workspace;
// WorkspaceViewModel.cs is frozen for this series, so they live in this partial.

using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CircuitRF.Design.PCellDesigner;
using CircuitRF.Ui.PCellDesigner;
using CircuitRF.Ui.Schematic;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ViewModels;

public partial class WorkspaceViewModel
{
    /// <summary>
    /// Tools ▸ PCell Designer…: the designer on the active schematic's cell when it is designed, else on a new designed
    /// cell <c>PCell1</c> (<c>PCell2</c>, … when taken) in the workspace (brief-pcd-6 D3).
    /// </summary>
    [RelayCommand]
    private void OpenPCellDesigner()
    {
        if (CurrentWorkspaceRoot is not { } root)
        {
            Messages.Error("PCell Designer: open a workspace first; a designed cell is a cell of one.");
            return;
        }
        string? cell = PCellDesignerViewModel.DesignedCellOf((ResolveActiveDocumentForCommands() as SchematicDocument)?.FilePath);
        if (cell is null)
        {
            try { cell = PCellDesignerViewModel.NewCell(root).CellDir; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Messages.Error($"PCell Designer: {ex.Message}");
                return;
            }
            _factory.ProjectTreeTool?.Refresh();
        }
        OpenPCellDesignerOn(cell);
    }

    /// <summary>Edit in PCell Designer (the schematic and layout context menus): the designer on a designed cell, one
    /// window per cell folder.</summary>
    public void OpenPCellDesignerOn(string cellDir)
    {
        if (CurrentWorkspaceRoot is not { } root) return;
        Views.PCellDesigner.PCellDesignerWindow.Show(cellDir, root, ResolveOwner(null), OpenOptimizerOn);
    }

    /// <summary>R-pcd6-7: the schematic opened and the Optimizer panel shown on it.</summary>
    private void OpenOptimizerOn(string schematicPath)
    {
        OpenOrActivateSchematic(Path.GetFullPath(schematicPath));
        ShowToolPanel("Optimizer");
    }

    /// <summary>File ▸ Import ▸ PCell Package… (R-pcd6-8): a <c>.cpcell</c> into the workspace root.</summary>
    [RelayCommand(CanExecute = nameof(CanImportComponent))]
    private async Task ImportPCellPackage(Window? owner)
    {
        if (CurrentWorkspaceRoot is not { } root || ResolveOwner(owner) is not { } window) return;
        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import PCell Package",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("PCell package") { Patterns = ["*" + PCellDesign.PackageExtension] }],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        var (cellDir, status) = PCellDesignerViewModel.Import(path, root);
        if (cellDir is null) { Messages.Error(status); return; }
        Messages.Success(status, cellDir);
        _factory.ProjectTreeTool?.Refresh();
    }
}
