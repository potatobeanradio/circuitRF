// HZN-4 R-hzn4-4 — File ▸ Export ▸ Hertzon Project…: the active 3D document as a Hertzon design. The command only opens the dialog and
// reports; the export is HznExport's, the function `convert x.c3d -o y.hzn` calls too.

using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.ThreeD;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ViewModels;

public partial class WorkspaceViewModel
{
    /// <summary>File ▸ Export ▸ Hertzon Project…'s tooltip: what it writes, then what it needs.</summary>
    public string ThreeDExportHertzonTip
        => "The active 3D document as a Hertzon design (.hzn), in a new project or added to an existing one. A design Hertzon " +
           "cannot state is listed and nothing is written. Requires an active 3D document.";

    private (string Path, C3dDocument Document, CircuitRF.Ui.ThreeD.Hzn.HznDesignSource? Held)? HertzonExportSource()
        => ResolveActiveDocumentForCommands() switch
        {
            C3dEditorDocument e => (e.ViewModel.FilePath, e.ViewModel.Document, e.ViewModel.Source as CircuitRF.Ui.ThreeD.Hzn.HznDesignSource),
            _ => null,
        };

    private bool CanExportHertzon() => HertzonExportSource() is not null;

    [RelayCommand(CanExecute = nameof(CanExportHertzon))]
    private async Task ExportHertzon(Window? owner)
    {
        if (HertzonExportSource() is not { } src) return;
        if (ResolveOwner(owner) is not { } window) return;
        // A snapshot: the editor may keep changing while the dialog plans.
        var snapshot = C3dPersistence.Deserialize(C3dPersistence.Serialize(src.Document));
        // A design held in a project is written with the project's materials, under its own name, as a copy: Save is what writes it back.
        if (src.Held is not null) snapshot.TechRef = null;
        var vm = new HznExportDialogViewModel(snapshot, src.Path, CurrentWorkspacePath, Path.GetDirectoryName(src.Path) ?? ".",
                                              src.Held?.ProjectTechnology, src.Held?.Design);
        bool ok = await new CircuitRF.Ui.Views.ThreeD.HznExportDialog(vm).ShowDialog<bool>(window);
        if (!ok || vm.Outcome is not { Path: { } written } outcome) return;
        foreach (string note in outcome.Notes) Messages.Info(note, src.Path);
        Messages.Success($"Exported Hertzon design '{outcome.Design}'", written);
    }
}
