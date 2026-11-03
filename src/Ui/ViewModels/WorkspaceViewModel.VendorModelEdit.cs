using System.Threading.Tasks;
using Avalonia.Controls;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// Changing a placed library part (brief vendorlib-12) — the shell around <c>VendorModelAuthoring</c> and
/// <c>ModelLibraryExport.Refresh</c> in src/Design, which hold every rule and every write: Edit Vendor Model… opens the
/// model's schematic as its own tab, the first save of a model per session asks once, and each save refreshes the model;
/// Copy to Workspace… copies it in and asks which parts to point at the copy.
/// </summary>
public partial class WorkspaceViewModel
{
    /// <summary>Model folders whose edit has been confirmed this session (R-vl12-2c: asked once per model).</summary>
    private readonly HashSet<string> _confirmedModelEdits = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public void EditVendorModel(SchematicDocument doc, EditableComponent comp)
    {
        var model = doc.ActiveViewModel.EditModel;
        if (VendorModelAuthoring.EditRefusal(comp, model.SchematicDirectory, model.HostWorkspaceRoot) is { } why)
        { Messages.Warning(why); return; }
        if (VendorModelAuthoring.Resolve(comp, model.SchematicDirectory, model.HostWorkspaceRoot, out _) is { } target)
            OpenOrActivateSchematic(target.Schematic);
    }

    /// <inheritdoc/>
    public void ShowStatus(string text) => Messages.Info(text);

    /// <inheritdoc/>
    public async Task CopyVendorModelToWorkspaceAsync(SchematicDocument doc, EditableComponent comp)
    {
        if (WorkspaceRootDir is not { } root) { Messages.Warning("Copy to Workspace needs an open workspace to copy into."); return; }
        var vm = doc.ActiveViewModel;
        var model = vm.EditModel;
        if (VendorModelAuthoring.Resolve(comp, model.SchematicDirectory, model.HostWorkspaceRoot, out string? why) is not { } target)
        { Messages.Warning(why ?? "This part cannot be copied."); return; }

        var result = await Task.Run(() => VendorModelAuthoring.CopyToWorkspace(target.ModelDir, root, target.Library));
        if (!result.Succeeded) { Messages.Error(result.Message); return; }
        Messages.Success(result.Message, result.CellDir);
        RefreshAfterReferenceChange();

        if (ResolveOwner(null) is not { } owner) return;
        var same = vm.SameLibraryModel(comp);
        var choice = await Views.Dialogs.RepointCopyDialog.AskAsync(owner, target.Manifest.Part, Path.GetFileName(result.CellDir!), same.Count);
        var parts = choice switch
        {
            Views.Dialogs.RepointCopyDialog.Choice.ThisInstance => [comp],
            Views.Dialogs.RepointCopyDialog.Choice.EverySame    => same,
            _                                                   => (IReadOnlyList<EditableComponent>)[],
        };
        if (vm.RepointToCell(parts, result.CellDir!)) RebuildOpenSchematics();
    }

    /// <summary>
    /// R-vl12-2c: before a save writes into a library model, the one confirmation per model per session. False when the
    /// user declined, and nothing is written.
    /// </summary>
    private async Task<bool> ConfirmModelEditsAsync(IEnumerable<string> paths, Window owner)
    {
        foreach (string modelDir in ModelDirsOf(paths))
        {
            if (_confirmedModelEdits.Contains(modelDir)) continue;
            string part = ModelPartManifest.Read(Path.Combine(modelDir, ModelFolder.PartFileName)).Manifest?.Part ?? Path.GetFileName(modelDir);
            int placements = WorkspaceRootDir is { } root ? VendorModelAuthoring.CountPlacements(root, modelDir) : 0;
            bool yes = await Views.Dialogs.TextConfirmDialog.AskAsync(owner, "Edit Vendor Model", $"Save changes to {part}?",
                VendorModelAuthoring.ConfirmSentence(part, VendorModelAuthoring.LibraryNameOf(modelDir), placements), "Save");
            if (!yes) return false;
            _confirmedModelEdits.Add(modelDir);
        }
        return true;
    }

    /// <summary>
    /// R-vl12-2d/2e: after a save into a library model, the model is refreshed — off the UI thread, since converting a
    /// newly added technology part runs the circuit. A refusal leaves the saved schematic as it is and says the part still
    /// runs its previous netlist. A document the refresh rewrote is read again, and every open design re-resolves the
    /// part's symbol and pins (R-vl12-2f).
    /// </summary>
    private async Task RefreshSavedModelsAsync(IEnumerable<string> paths)
    {
        foreach (string modelDir in ModelDirsOf(paths))
        {
            var result = await Task.Run(() => ModelLibraryExport.Refresh(modelDir));
            if (result.Succeeded) Messages.Success(result.Message, modelDir);
            else Messages.Warning(result.Message);
            RefreshAfterReferenceChange();
            if (result.Rewritten.Count > 0)
            {
                var rewritten = result.Rewritten;
                // After the save that triggered it has returned: a save made while closing the tab must finish closing it
                // before the tab is reopened on the rewritten file — or not reopened, when it has gone.
                Avalonia.Threading.Dispatcher.UIThread.Post(async () => await ReloadDocumentsAt(rewritten),
                                                           Avalonia.Threading.DispatcherPriority.Background);
            }
        }
    }

    private static IEnumerable<string> ModelDirsOf(IEnumerable<string> paths)
        => paths.Select(ModelFolder.OwningModelFolder).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
