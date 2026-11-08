using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.ViewModels;

// brief-isc-1 — the EM setup panel's "Create now": the cell Create SnP schematic would make after a run, made without one. It reads the
// setup's existing Touchstone, or names the one the run will write and warns. An existing cell asks Replace / New name / Cancel.
public partial class WorkspaceViewModel
{
    /// <summary>Create now on a <c>.cem</c>: its layout, or the 3D view its LayoutRef names (as Simulate resolves it).</summary>
    private Task CreateEmSchematicNowAsync(EmSetupEditorViewModel vm)
    {
        var setup = vm.Working.Clone();
        string resultsRoot = EmResultsRoot();
        string? cemThreeD = EmSetupResolver.ResolveLayoutPath(vm.FilePath, vm.Working.LayoutRef, CurrentWorkspacePath);
        if (C3dSetups.IsThreeDView(cemThreeD) && File.Exists(cemThreeD))
        {
            C3dDocument threeD;
            try { threeD = C3dPersistence.LoadFromFile(cemThreeD!); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                Messages.Error($"The 3D view {cemThreeD} cannot be read: {ex.Message}");
                return Task.CompletedTask;
            }
            string c3dPath = cemThreeD!;
            string? cws = CircuitRF.Design.Workspace.WorkspaceRootFinder.FindAncestorCws(Path.GetDirectoryName(c3dPath));
            return CreateSchematicNowAsync(setup, resultsRoot, CellFolder.SiblingView(c3dPath, ViewType.ThreeD, ViewType.Schematic).CellDir,
                (replace, name) => EmSchematicNow.ForThreeD(setup, threeD, c3dPath, cws, resultsRoot, fromCem: true, replace, name));
        }
        var source = ResolveEmLayout(vm.FilePath, vm.Working.LayoutRef);
        string? cellDir = source is null ? null : CellFolder.SiblingView(source.AbsolutePath, ViewType.Layout, ViewType.Schematic).CellDir;
        return CreateSchematicNowAsync(setup, resultsRoot, cellDir, (replace, name) => EmSchematicNow.ForLayout(setup, source, resultsRoot, replace, name));
    }

    /// <summary>Create now on a 3D view's setup (<paramref name="setupName"/>; null is the active one).</summary>
    internal Task CreateC3dSchematicNowAsync(C3dEditorViewModel c3d, string? setupName)
    {
        if (c3d.IsScratch)
        {
            Messages.Warning($"'{Path.GetFileName(c3d.FilePath)}' has not been saved: save it (File ▸ Save As…) to make its schematic.");
            return Task.CompletedTask;
        }
        var (setup, fromCem, refusal) = c3d.RunSetupFor(setupName);
        if (setup is null) { Messages.Warning(refusal ?? "There is no setup to make a schematic for."); return Task.CompletedTask; }
        var document = c3d.RunDocument();
        string path = c3d.TopFilePath;
        string? cws = CurrentWorkspacePath;
        string resultsRoot = EmResultsRoot();
        return CreateSchematicNowAsync(setup, resultsRoot, CellFolder.SiblingView(path, ViewType.ThreeD, ViewType.Schematic).CellDir,
            (replace, name) => EmSchematicNow.ForThreeD(setup, document, path, cws, resultsRoot, fromCem, replace, name));
    }

    private async Task CreateSchematicNowAsync(EmSetup setup, string resultsRoot, string? emCellDir, Func<bool, string?, EmSnpSchematicResult> make)
    {
        string workspace = EmSchematicNow.WorkspaceOf(resultsRoot);
        string target = EmSchematicNow.TargetCellName(setup, emCellDir);
        string targetDir = Path.Combine(workspace, target);
        bool replace = false;
        string? name = null;
        if (Directory.Exists(targetDir) || File.Exists(targetDir))
        {
            if (ResolveOwner(null) is not { } window) return;
            var (choice, chosen) = await CellExistsDialog.AskAsync(window, workspace, target);
            if (choice == CellExistsChoice.Cancel) return;
            replace = choice == CellExistsChoice.Replace;
            name = choice == CellExistsChoice.NewName ? chosen : null;
            // An open schematic would keep showing (and on its next save write back) what Replace removed.
            if (replace && OpenSchematicIn(targetDir) is { } open)
            {
                Messages.Warning($"{Path.GetFileName(open)} is open, so cell {target} was not replaced. Close it and press Create now again.", open);
                return;
            }
        }
        var owningWorkspace = CurrentWorkspacePath;
        Messages.Info($"Making the SnP schematic for '{setup.Name}'…");
        EmSnpSchematicResult made;
        try { made = await Task.Run(() => make(replace, name)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Messages.Error($"No schematic was created: {ex.Message}");
            return;
        }
        PostSnpSchematic(made, owningWorkspace);
    }

    /// <summary>The path of a schematic document open from <paramref name="cellDir"/>, or null.</summary>
    private string? OpenSchematicIn(string cellDir)
    {
        string schematicDir = Path.GetFullPath(CellFolder.SubFolderPath(cellDir, ViewType.Schematic)) + Path.DirectorySeparatorChar;
        return _openDocsByPath.Keys.FirstOrDefault(p => p.EndsWith(".csch", StringComparison.OrdinalIgnoreCase)
                                                       && Path.GetFullPath(p).StartsWith(schematicDir, StringComparison.OrdinalIgnoreCase));
    }
}
