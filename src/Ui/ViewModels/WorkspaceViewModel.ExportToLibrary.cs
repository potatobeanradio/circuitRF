using Avalonia.Controls;
using CircuitRF.Design.VendorLibraries;
using CircuitRF.Design.VendorLibraries.ModelLibrary;
using CircuitRF.Ui.Schematic;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// File ▸ Export to Library… and the schematic toolbar's button (brief vendorlib-10 R-vl10-1). The shell around
/// <see cref="ExportToLibraryViewModel"/>: which document it is offered on and why not, the dialog, the one Messages
/// line, and adding the library to the workspace when asked (R-vl10-3f). The export itself is
/// <c>ModelLibraryExport</c>'s, in src/Design.
/// </summary>
public partial class WorkspaceViewModel
{
    /// <summary>
    /// Why Export to Library is not offered on <paramref name="doc"/>, or null when it is (R-vl10-1a): a saved cell's
    /// primary schematic with at least one Pin. The sentence is the toolbar's and the menu's tooltip.
    /// </summary>
    public static string? ExportToLibraryBlockedReason(object? doc)
    {
        if (doc is not SchematicDocument sd) return "Export to Library packages a cell's schematic. Open one.";
        if (sd.CanPopOut) return "Pop back to the top of this schematic to export it.";
        if (sd.IsScratch || sd.FilePath is not { Length: > 0 } path)
            return "Save this schematic into a cell first; Export to Library packages a saved cell.";
        if (sd.IsDirty) return "Save this schematic first; Export to Library packages what is saved.";

        string? schDir = Path.GetDirectoryName(Path.GetFullPath(path));
        string? cellDir = schDir is null ? null : Path.GetDirectoryName(schDir);
        if (cellDir is null || !string.Equals(Path.GetFileName(schDir), CellFolder.SchematicSubFolder, StringComparison.OrdinalIgnoreCase))
            return "This schematic is not in a cell folder; Export to Library packages a cell.";
        try
        {
            var primary = CellFolder.ResolvePrimary(cellDir, ViewType.Schematic);
            if (!string.Equals(primary.ResolvedName, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase))
                return "This is not its cell's primary schematic; Export to Library packages the primary one.";
            string ccell = Path.Combine(cellDir, CellFolder.CcellFileName);
            if (File.Exists(ccell) && CellPersistence.LoadFromFile(ccell).IsTestBench)
                return "This cell is a test bench; a model is a cell with pins that another design places.";
        }
        catch (Exception ex) { return $"This cell could not be read ({ex.Message})."; }

        if (!sd.ViewModel.EditModel.Components.Any(c => c.Symbol == SymbolKind.Pin))
            return "This schematic has no Pin; a model connects to the design that places it through its Pins.";
        return null;
    }

    /// <summary>The menu item's tooltip: what the command does, or why it is greyed.</summary>
    public string ExportToLibraryTip => ExportToLibraryBlockedReason(ResolveActiveDocumentForCommands())
        ?? "Package this cell as a self-contained model in a model library, for others to place.";

    private bool CanExportToLibrary() => ExportToLibraryBlockedReason(ResolveActiveDocumentForCommands()) is null;

    /// <summary>File ▸ Export to Library… on the active schematic.</summary>
    [RelayCommand(CanExecute = nameof(CanExportToLibrary))]
    private Task ExportToLibrary(Window? owner)
        => ResolveActiveDocumentForCommands() is SchematicDocument sd ? ExportToLibraryAsync(sd, owner) : Task.CompletedTask;

    /// <summary>The schematic toolbar's button (<see cref="IHierarchyHost"/>).</summary>
    public Task ExportToLibraryAsync(SchematicDocument doc) => ExportToLibraryAsync(doc, null);

    private async Task ExportToLibraryAsync(SchematicDocument doc, Window? owner)
    {
        var window = ResolveOwner(owner);
        if (window is null || ExportToLibraryBlockedReason(doc) is not null || doc.FilePath is not { } path) return;
        string cellDir = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(path))!)!;
        await ExportCellToLibraryAsync(cellDir, window, temporary: false, null);
    }

    /// <summary>
    /// Model Composer's Export to Library… (brief vendorlib-17 R-vl17-2): the model's cell written by
    /// <see cref="ModelCellFromModel"/>, then the same dialog and the same Messages lines as a schematic's export. The
    /// written cell is deleted when the dialog closes, however it closed (R-vl17-4).
    /// </summary>
    public async Task ExportModelToLibraryAsync(string documentPath, string? cmodelPath, Window owner)
    {
        StagedModelCell? staged;
        ModelCellSource? source;
        string? refusal;
        try
        {
            (source, staged, refusal) = await Task.Run(() =>
            {
                var s = ModelCellFromModel.Open(documentPath, out string? why, cmodelPath);
                return (s, s is null ? null : ModelCellFromModel.Stage(s), why);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CircuitRF.Core.Devices.Composed.CmodelFormatException)
        {
            (source, staged, refusal) = (null, null, ex.Message);
        }
        if (source is null || staged is null)
        {
            Messages.Warning($"Export to Library — {refusal}");
            return;
        }
        using (staged)
            await ExportCellToLibraryAsync(staged.CellDir, owner, temporary: true, vm =>
            {
                vm.PartNumber = source.PartNumber;
                if (source.FrequencyLow.Length > 0) vm.FrequencyLow = source.FrequencyLow;
                if (source.FrequencyHigh.Length > 0) vm.FrequencyHigh = source.FrequencyHigh;
            });
    }

    /// <summary>The dialog on <paramref name="cellDir"/> and what follows a success — the one function both the schematic
    /// editor's export and Model Composer's post their Messages lines through.</summary>
    private async Task ExportCellToLibraryAsync(string cellDir, Window window, bool temporary, Action<ExportToLibraryViewModel>? defaults)
    {
        var known = new List<ExportLibraryChoice>();
        string? wsRoot = CurrentWorkspacePath is { } cws ? Path.GetDirectoryName(cws) : null;
        if (wsRoot is not null)
            foreach (var r in TryLoadCws(CurrentWorkspacePath!).VendorLibraries ?? [])
                if (VendorLibraryRefs.ResolveLibraryRoot(wsRoot, r.Name, out _) is { } root)
                    known.Add(new ExportLibraryChoice(r.Name, root));

        var vm = new ExportToLibraryViewModel(cellDir, known, null) { CellIsTemporary = temporary };
        defaults?.Invoke(vm);
        var result = await Views.Dialogs.ExportToLibraryDialog.AskAsync(window, vm);
        if (result is not { Succeeded: true }) return;

        Messages.Info(result.Message ?? "");
        foreach (string w in result.Warnings) Messages.Warning($"Export to Library — {w}");
        foreach (var b in result.Builds) Messages.Info($"Export to Library — build {b.Describe()}");

        string libraryRoot = vm.LibraryPath.Trim();
        if (vm.AddToWorkspace && !vm.IsReferenced(libraryRoot) && CurrentWorkspacePath is { } cwsPath && wsRoot is not null)
        {
            var file = TryLoadCws(cwsPath);
            var refs = file.VendorLibraries ?? [];
            // AddOrRepair replaces a reference of the same name, and the workspace may already reference ANOTHER
            // folder of this folder's name — whose placed parts would then resolve against this library. Nobody is
            // asked here (the checkbox was the question), so the new library takes a name no reference has.
            string name = VendorLibraryRefs.UnusedName(refs, VendorLibraryRefs.DefaultName(libraryRoot));
            var added = await Task.Run(() => VendorLibraryRefs.AddOrRepair(wsRoot, refs, libraryRoot, name, out string? p) is { } b ? (b, (string?)null) : (null, p));
            if (added.Item1 is null)
                Messages.Warning($"Export to Library — the library was not added to the workspace. {added.Item2}");
            else
            {
                UserVendorLibraries.Remember(refs[^1].Name, added.Item1.Root);
                SaveVendorReferences(cwsPath, refs);
            }
        }
        else if (vm.IsReferenced(libraryRoot))
        {
            // The library is already in the palette: index it again so the new part is listed now.
            _ = Task.Run(() => VendorLibraryIndex.StartBuild(libraryRoot));
        }
    }
}
