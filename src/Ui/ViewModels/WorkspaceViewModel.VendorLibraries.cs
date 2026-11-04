using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CircuitRF.Design.VendorLibraries;
using CircuitRF.Design.VendorLibraries.ModelLibrary;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Schematic;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// Vendor part libraries (brief vendorlib-1): File ▸ Add Vendor Library…, the vendor half of Manage
/// Libraries, and what a workspace open does with the libraries it references.
///
/// <para><b>Every rule lives in <see cref="VendorLibraryRefs"/>.</b> This is the folder picker, the
/// confirm step, the Messages lines and the per-user list — the shell around the one function
/// <c>circuitrf import library</c> also calls.</para>
/// </summary>
public partial class WorkspaceViewModel
{
    /// <summary>
    /// File ▸ Add Vendor Library… — pick the library's folder, index it, confirm the name, and
    /// reference it from this workspace's <c>.cws</c>. Workspace-gated, like Manage Libraries.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasWorkspaceOpen))]
    private async Task AddVendorLibrary(Window? owner)
    {
        var window = ResolveOwner(owner);
        if (window is null || CurrentWorkspacePath is null) return;

        var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title         = "Add Vendor Library — choose the library's folder",
            AllowMultiple = false,
        });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { Length: > 0 } folder) return;

        // Indexed BEFORE the confirm step, so it can say what the folder holds — off the UI thread.
        var build = await Task.Run(() => VendorLibraryIndex.Build(folder));
        if (build.Refusal is { } refusal)
        {
            Messages.Warning($"Add Vendor Library — '{folder}' was not added. {refusal}");
            await Views.Dialogs.TextConfirmDialog.AskAsync(window, "Add Vendor Library",
                "This folder cannot be added", refusal, confirmLabel: null);
            return;
        }

        string cwsPath = CurrentWorkspacePath;
        var cws  = TryLoadCws(cwsPath);
        var refs = cws.VendorLibraries ?? [];
        if (await AddVendorLibraryFromAsync(window, folder, build, refs) is null) return;

        cws.VendorLibraries = refs;
        SaveVendorReferences(cwsPath, refs);
    }

    /// <summary>
    /// The confirm step and the add, into <paramref name="refs"/> — shared by File ▸ Add Vendor
    /// Library… and Manage Libraries' Add, which save the list themselves. Returns the name added
    /// under, or null when the user cancelled or the add was refused.
    /// </summary>
    private async Task<string?> AddVendorLibraryFromAsync(
        Window owner, string folder, VendorLibraryBuild build, List<CwsVendorLibraryRef> refs)
    {
        if (CurrentWorkspacePath is null) return null;
        string wsRoot = Path.GetDirectoryName(CurrentWorkspacePath)!;

        string? name = await Views.Dialogs.AddVendorLibraryDialog.AskAsync(
            owner, folder, VendorLibraryRefs.DefaultName(folder), build.SummaryLines(), build.Signature);
        if (name is null) return null;

        var added = await Task.Run(() => VendorLibraryRefs.AddOrRepair(wsRoot, refs, folder, name, out string? p)
                                         is { } b ? (b, (string?)null) : (null, p));
        if (added.Item1 is not { } done)
        {
            Messages.Warning($"Add Vendor Library — '{name}' was not added. {added.Item2}");
            return null;
        }

        UserVendorLibraries.Remember(name, done.Root);

        // ONE line to Messages, with the unreadable-file sentence when there is one — never a line per
        // file or per part.
        var lines = done.SummaryLines();
        Messages.Info($"Add Vendor Library — added '{name}': {string.Join(" ", lines)}");
        return name;
    }

    /// <summary>Writes the vendor list straight through, as a kit reference is — a library the user
    /// just added must survive closing the workspace without saving a document.</summary>
    private void SaveVendorReferences(string cwsPath, List<CwsVendorLibraryRef> refs)
    {
        try
        {
            var latest = TryLoadCws(cwsPath);
            latest.VendorLibraries = refs.Count == 0 ? null : refs;
            if (!WorkspacePersistence.SaveToFileAtomic(cwsPath, latest))
                Messages.Info(
                    $"'{Path.GetFileName(Path.GetDirectoryName(cwsPath))}' is read-only on this machine, so " +
                    "this library change applies to the open session only and will not be there next time.");
        }
        catch (Exception ex)
        {
            Messages.Warning($"Vendor library — the change could not be recorded: {ex.Message}");
        }
        PublishVendorLibraries();
    }

    /// <summary>
    /// Hands the palette this workspace's vendor libraries — each name with the folder it resolves to
    /// — and the Add Vendor Library… command its Type menu ends with (brief vendorlib-2). Called on
    /// open, after any change to the list, and whenever the palette tool is re-created.
    /// </summary>
    private void PublishVendorLibraries()
    {
        if (_factory.PaletteTool is not { } palette) return;
        EnsureVendorIndexSubscription();

        palette.AddVendorLibraryCommand = AddVendorLibraryCommand;

        var libs = new List<(string Name, string? Root)>();
        if (CurrentWorkspacePath is { } cwsPath && Path.GetDirectoryName(cwsPath) is { } wsRoot)
            foreach (var r in TryLoadCws(cwsPath).VendorLibraries ?? [])
                libs.Add((r.Name, VendorLibraryRefs.ResolveLibraryRoot(wsRoot, r.Name, out _)));
        palette.SetVendorLibraries(libs);
    }

    private bool _vendorIndexSubscribed;

    /// <summary>
    /// A finished index build reaches the palette (its menu, counts and list) and the open schematics
    /// (a placed part's value caption comes from the index). <see cref="VendorLibraryIndex.Built"/> is
    /// raised on a pool thread, hence the post.
    /// </summary>
    private void EnsureVendorIndexSubscription()
    {
        if (_vendorIndexSubscribed) return;
        _vendorIndexSubscribed = true;
        VendorLibraryIndex.Built += _ => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _factory.PaletteTool?.RefreshVendorIndexes();
            RebuildOpenSchematics();
        });
    }

    /// <summary>The File/Name of every vendor part placed in a schematic that is currently OPEN — a SpiceModel's, and a
    /// placed library model's <c>vendormodel://</c> reference with an empty Name (what Validate reads a model as) —
    /// what Remove warns with and Validate checks (the same open-documents scope the kit path uses).</summary>
    private IReadOnlyList<(string File, string Name)> PlacedVendorParts()
    {
        var models = _openDocsByPath.Values.OfType<SchematicDocument>()
            .Concat(_scratchDocs)
            .Select(d => d.ViewModel.EditModel);

        var components = models.SelectMany(m => m.Components).ToList();
        return [.. components
                         .Where(c => c.Symbol == SymbolKind.SpiceModel)
                         .Select(c => (File: Param(c, SpiceModelSymbolProvider.FileParameter),
                                       Name: Param(c, SpiceModelSymbolProvider.NameParameter)))
                         .Where(p => VendorLibraryRefs.IsVendorRef(p.File))
                         .Concat(components.Where(c => c.CellRef is { } r && VendorModelRefs.IsRef(r))
                                           .Select(c => (File: c.CellRef!, Name: "")))
                         .Distinct()];

        static string Param(EditableComponent c, string name)
            => (c.Parameters.FirstOrDefault(p => p.Name == name)?.Expression ?? "").Trim();
    }

    /// <summary>
    /// What a workspace open does with its vendor libraries (R-vl1-2e, R-vl1-4d): every library's
    /// index is STARTED on a background thread and nothing waits for it; a library whose folder is
    /// missing posts ONE line — per library, never per part — with a Repair action when this user's
    /// own list knows that name at a folder that exists.
    /// </summary>
    private void RestoreVendorLibraries(string? wsRoot)
    {
        if (wsRoot is null || CurrentWorkspacePath is null) return;

        foreach (var r in TryLoadCws(CurrentWorkspacePath).VendorLibraries ?? [])
        {
            if (VendorLibraryRefs.ResolveLibraryRoot(wsRoot, r.Name, out string? why) is { } root)
            {
                _ = VendorLibraryIndex.StartBuild(root);
                continue;
            }

            string text = $"Vendor library '{r.Name}': {why} Parts placed from it show as unresolved until " +
                          "it is repaired.";
            if (UserVendorLibraries.ExistingPathFor(r.Name) is { } known)
            {
                string name = r.Name;
                Messages.PostAction(MessageLevel.Warning, text + $" You last had it at '{known}'.", "Repair",
                    () => { RepairVendorLibrary(name, known); return Task.CompletedTask; });
            }
            else
            {
                Messages.Warning(text + " Repair it in File ▸ Manage Libraries….");
            }
        }
        PublishVendorLibraries();
    }

    /// <summary>The Repair action: <see cref="VendorLibraryRefs.AddOrRepair"/> with the folder the
    /// per-user list knows, under the same name — so every placed part resolves again.</summary>
    private void RepairVendorLibrary(string name, string folder)
    {
        if (CurrentWorkspacePath is null) return;
        string cwsPath = CurrentWorkspacePath;
        var refs = TryLoadCws(cwsPath).VendorLibraries ?? [];
        if (VendorLibraryRefs.AddOrRepair(Path.GetDirectoryName(cwsPath), refs, folder, name, out string? problem) is null)
        {
            Messages.Warning($"Vendor library '{name}' could not be repaired: {problem}");
            return;
        }
        SaveVendorReferences(cwsPath, refs);
        Messages.Info($"Vendor library '{name}' repaired: it now resolves to '{folder}'.");
        RefreshAfterReferenceChange();
    }
}
