// brief-paa-9 R-paa9-1 — the Phased Array tool's two entries: Tools ▸ Phased Array, a view of a PhasedArray instance in the active schematic,
// and File ▸ New ▸ Phased Array, a cell CellCreate.PhasedArray writes, opened with the tool on it.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Views.Dialogs;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ViewModels;

public partial class WorkspaceViewModel
{
    /// <summary>The bench a schematic session runs as, prepared once — the Tuning panel's own preparation, so the tool evaluates exactly
    /// what Simulate would.</summary>
    private PreparedCircuit? PreparePhasedArrayCircuit(SchematicViewModel schematic)
    {
        if (PrepareTunedCircuit(schematic) is not { } p) return null;
        return p.Circuit;
    }

    /// <summary>
    /// Tools ▸ Phased Array: the tool on the selected Phased Antenna Array of the active schematic — the one selected, else the only one in
    /// the schematic, else the user picks which.
    /// <para>Greyed unless a schematic is the active document, as the Design menu's schematic items are; enablement is raised in both of
    /// the workspace's fan-outs (the shell's own tab changes, and a torn-off window taking focus).</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsSchematicDocumentActive))]
    private async Task OpenPhasedArrayTool()
    {
        var owner = ResolveOwner(null);
        if (ResolveActiveDocumentForCommands() is not SchematicDocument doc)
        {
            Messages.Warning("Phased Array: open the schematic that holds the Phased Antenna Array first.");
            return;
        }
        var vm = doc.ViewModel;
        var arrays = vm.EditModel.Components.Where(c => c.Symbol == SymbolKind.PhasedArray).ToList();
        if (arrays.Count == 0)
        {
            Messages.Warning($"Phased Array: '{doc.Id}' holds no Phased Antenna Array. Place one from the System category, or use File ▸ New ▸ Phased Array.");
            return;
        }
        var selected = arrays.Where(c => vm.Selection.IsSelected(c.Id)).ToList();
        var instance = selected.Count == 1 ? selected[0] : arrays.Count == 1 ? arrays[0] : null;
        if (instance is null && owner is not null)
        {
            var names = arrays.Select(a => a.InstanceName).ToList();
            var dialog = new Views.PhasedArray.ArrayPickerDialog(names);
            if (await dialog.ShowDialog<string?>(owner) is { } chosen) instance = arrays.FirstOrDefault(a => a.InstanceName == chosen);
        }
        if (instance is null) return;
        Views.PhasedArray.PhasedArrayWindow.ShowFor(vm, instance, PreparePhasedArrayCircuit, owner);
    }

    /// <summary>
    /// File ▸ New ▸ Phased Array: a cell whose schematic is a Port, a Feed-mode array on a chosen EM result (8 × 8 at half a wavelength of its
    /// centre frequency) and a fields analysis — written by <see cref="CellCreate.PhasedArray"/>, opened, with the tool on it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanNewThreeDDesign))]
    private async Task NewPhasedArray()
    {
        var owner = ResolveOwner(null);
        if (owner is null || CurrentWorkspacePath is null) return;
        var workspaceDir = Path.GetDirectoryName(CurrentWorkspacePath)!;
        var name = await new InputNameDialog("New Phased Array", "Cell name:").ShowDialog<string?>(owner);
        if (name is null) return;
        string results = Path.Combine(workspaceDir, "results");
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "The EM result of one element or a sub-array",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("EM result (*.cemr)") { Patterns = ["*.cemr"] }],
            SuggestedStartLocation = await owner.StorageProvider.TryGetFolderFromPathAsync(Directory.Exists(results) ? results : workspaceDir),
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } record) return;

        CellCreateResult cell;
        try { cell = CellCreate.PhasedArray(workspaceDir, name, record); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Messages.Error($"New Phased Array: {ex.Message}");
            return;
        }
        _factory.ProjectTreeTool?.Refresh();
        Messages.Success("Created", cell.SchematicPath!);
        OpenOrActivateSchematic(cell.SchematicPath!);
        if (_openDocsByPath.TryGetValue(Path.GetFullPath(cell.SchematicPath!), out var opened) && opened is SchematicDocument sd
            && sd.ViewModel.EditModel.Components.FirstOrDefault(c => c.Symbol == SymbolKind.PhasedArray) is { } pa)
            Views.PhasedArray.PhasedArrayWindow.ShowFor(sd.ViewModel, pa, PreparePhasedArrayCircuit, owner);
    }
}
