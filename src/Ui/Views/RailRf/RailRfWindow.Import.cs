// §2.3 step 1 — getting a board into a railRF document (R-rail7-6, R-rail7-7).
//
// ── NOTHING HERE IMPORTS ANYTHING ────────────────────────────────────────────────────────────────
//
// The artwork goes through GerberImportEntry.Run — the same funnel File ▸ Import ▸ Gerber goes
// through, including its enclosing-folder prompt, its layer-mapping dialog and its drill-format
// prompt. The three companion files go through brief 2's readers. The workspace, where one has to be
// made, goes through WorkspaceCreate.Create — the same function the GUI's own New Workspace command
// calls. What is HERE is the dialog, the ordering, and the reporting: `Authoring.cs`' rule, on the
// window side of the firewall. An operation that exists twice diverges silently.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.RailRf;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine;
using CircuitRF.Ui.RailRf;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.Views.RailRf;

public partial class RailRfWindow
{
    private void WireImportButton() => ImportButton.Click += async (_, _) => await ImportBoardAsync();

    /// <summary>
    /// Picks a board, asks the two things that must not be guessed, imports it, and points the
    /// document at the cell it landed in.
    /// </summary>
    private async Task ImportBoardAsync()
    {
        if (Vm is not { } vm) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "railRF — Import Board",
            AllowMultiple = false,
            FileTypeFilter =
            [
                // A CONVENIENCE, never a decision: what a file IS is settled by CONTENT through the
                // import's own classifier, so nothing here can admit or exclude a file. The list is
                // the Gerber import's own, because it is the same import.
                new FilePickerFileType("Board artwork")
                {
                    Patterns = ["*.gbr", "*.gbrjob", "*.gdo", "*.gtl", "*.gbl", "*.gts", "*.gbs",
                                "*.gto", "*.gbo", "*.gtp", "*.gbp", "*.gko", "*.gm1",
                                "*.drl", "*.ncd", "*.xln", "*.txt", "*.kicad_pcb"],
                },
                new FilePickerFileType("All Files") { Patterns = ["*.*"] },
            ],
        });
        if (files.Count == 0) return;

        string chosen = files[0].Path.LocalPath;
        var workspace = WorkspaceLocator.Any();
        string? workspaceDir = workspace?.CurrentWorkspacePath is { } cws
            ? Path.GetDirectoryName(Path.GetFullPath(cws))
            : null;

        if (await new RailImportDialog(chosen, workspaceDir).ShowDialog<RailImportOptions?>(this)
            is not { } options)
            return;   // Cancel aborts the whole import and leaves nothing behind.

        // ── R-rail7-6: with no workspace open, railRF OFFERS to create one ────────────────────
        //
        // Rather than silently falling back to the throwaway path — which is the failure the default
        // exists to prevent, and which would be invisible: the artwork would import, the window would
        // work, and the next session would find nothing.
        if (options.LandInWorkspace && options.WorkspaceDir is null)
        {
            string? made = await OfferToCreateWorkspaceAsync();
            if (made is null) return;
            options = options with { WorkspaceDir = made };
        }

        string parentDir = options.LandInWorkspace && options.WorkspaceDir is { } dir
            ? dir
            : Path.Combine(Path.GetTempPath(), "circuitrf-rail-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(parentDir);

        // ── destTech is NULL, and that is a decision rather than an omission ──────────────────
        //
        // Gerber import MINTS its own .ctech from the set's own layers and points the .clay at it
        // (R-L4g-8). The destTech argument is the reconciliation target for grafting an import's
        // layers onto an EXISTING technology — which is right when the artwork is a footprint joining
        // a process, and wrong here: a board's copper layers and a workspace's process layers are not
        // the same layers, and reconciling them would silently reinterpret the stackup railRF is
        // about to read every trace thickness and every dielectric out of.
        GerberImport.ImportResult result;
        try
        {
            result = await Task.Run(() => GerberImportEntry.Run(
                chosen, parentDir, null, LayoutUnits.DefaultDbuPerMicron,
                promptForScope: survey => Dispatcher.UIThread
                    .InvokeAsync(() => new GerberImportScopeDialog(survey)
                        .ShowDialog<GerberImportScope?>(this))
                    .GetAwaiter().GetResult(),
                pickFolder: () => Dispatcher.UIThread
                    .InvokeAsync(async () =>
                    {
                        var folders = await StorageProvider.OpenFolderPickerAsync(
                            new FolderPickerOpenOptions { Title = "railRF — Board Folder", AllowMultiple = false });
                        return folders.Count == 0 ? null : folders[0].Path.LocalPath;
                    })
                    .GetAwaiter().GetResult(),
                // R-rail7-7's second half. The EXISTING dialog, not a second sentence for the same
                // refusal: leading vs trailing suppression differ by four orders of magnitude on
                // identical text, `convert` already refuses on it, and two surfaces wording one
                // refusal two ways is a defect that only shows up when someone compares them.
                resolveDrillFormat: (fileName, inferred, crossCheck, remaining) => Dispatcher.UIThread
                    .InvokeAsync(() => new GerberDrillFormatPromptDialog(fileName, inferred, crossCheck, remaining)
                        .ShowDialog<GerberImport.DrillFormatChoice?>(this))
                    .GetAwaiter().GetResult()));
        }
        catch (Exception ex)
        {
            vm.PendingImportRefusal = new RailRefusal(
                $"The board was not imported: {ex.Message}", RailRefusalControl.None);
            return;
        }

        if (result.Cancelled || result.CellDir is null)
        {
            vm.PendingImportRefusal = new RailRefusal(
                result.Messages.Count > 0
                    ? string.Join(" ", result.Messages)
                    : "The board was not imported and nothing was created.",
                RailRefusalControl.None);
            return;
        }

        if (LoadArtwork(result, options) is not { } board)
        {
            vm.PendingImportRefusal = new RailRefusal(
                $"The import created {Path.GetFileName(result.CellDir)} but it holds no layout view "
              + "to read. Nothing was taken from it.", RailRefusalControl.None);
            return;
        }

        vm.ApplyImport(
            options, board,
            placement: ReadPlacement(options, board.DbuPerMicron),
            bom:       ReadBom(options),
            netlist:   ReadNetlist(options, board.DbuPerMicron),
            library:   ReadPartLibrary(options));
    }

    /// <summary>
    /// The created cell's layout view, as shapes plus its technology.
    /// </summary>
    /// <remarks>
    /// The <c>.crail</c> holds a REFERENCE to the cell rather than a copy of the geometry — so what is
    /// stored is a path, relative to the document where there is one, and what is handed to the
    /// extractor is what was read from it now.
    /// </remarks>
    private static RailBoardInputs? LoadArtwork(GerberImport.ImportResult result, RailImportOptions options)
    {
        string? clay = Directory
            .EnumerateFiles(result.CellDir!, "*.clay", SearchOption.AllDirectories)
            .FirstOrDefault();
        if (clay is null) return null;

        var view = LayoutPersistence.LoadFromFile(clay);
        if (result.Technology is not { } tech) return null;

        return new RailBoardInputs
        {
            Shapes         = view.Shapes,
            Technology     = tech,
            DbuPerMicron   = view.DbuPerMicron,
            ArtworkCellRef = options.LandInWorkspace ? clay : null,
        };
    }

    private static PlacementTable? ReadPlacement(RailImportOptions options, int dbuPerMicron) =>
        options.PlacementPath is { Length: > 0 } p
            ? PlacementFile.ReadFile(p, dbuPerMicron, options.PlacementOrigin)
            : null;

    private static BomTable? ReadBom(RailImportOptions options) =>
        options.BomPath is { Length: > 0 } p ? BomFile.ReadFile(p) : null;

    private static BoardNetlist? ReadNetlist(RailImportOptions options, int dbuPerMicron) =>
        options.BoardNetlistPath is { Length: > 0 } p
            ? BoardNetlistFile.ReadFile(p, dbuPerMicron)
            : null;

    private static PartLibrary? ReadPartLibrary(RailImportOptions options) =>
        options.PartLibraryPath is { Length: > 0 } p ? PartLibraryIo.LoadFromFile(p) : null;

    /// <summary>
    /// Offers to make a workspace for the artwork to land in, and makes it through
    /// <see cref="WorkspaceCreate.Create"/> — the same function the GUI's own New Workspace command
    /// calls, so a workspace made here is one the application made.
    /// </summary>
    private async Task<string?> OfferToCreateWorkspaceAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "railRF — where should the new workspace go?",
            AllowMultiple = false,
        });
        if (folders.Count == 0) return null;

        string parent = folders[0].Path.LocalPath;
        if (WorkspaceCreate.UnwritableParentRefusal(parent, "The workspace was not created") is { } refusal)
        {
            if (Vm is { } vm)
                vm.PendingImportRefusal = new RailRefusal(refusal, RailRefusalControl.None);
            return null;
        }

        var made = WorkspaceCreate.Create(parent, "railrf", WorkspaceCreate.DefaultTechnologyId);
        return made.WorkspaceDir;
    }
}
