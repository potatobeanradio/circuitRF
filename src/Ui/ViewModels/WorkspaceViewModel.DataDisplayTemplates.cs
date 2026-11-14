// ================================================================
//  WorkspaceViewModel.DataDisplayTemplates.cs  —  `.cddt` templates
//
//  File ▸ New ▸ Data Display from Template lists the templates circuitRF
//  ships, then the open workspace's `templates/` folder, then the user's
//  own templates folder (the `.canl` analysis templates' folders, so one
//  place holds a user's templates of every kind), then Browse…. Choosing
//  one — or opening any `.cddt` — makes a NEW, UNSAVED Data Display from
//  it; the template file is never the document being edited.
//  File ▸ Save Data Display As Template… writes the active display as one.
// ================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Schematic;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ViewModels;

/// <summary>One row of File ▸ New ▸ Data Display from Template.</summary>
/// <param name="Name">What the menu shows: the file's stem with its underscores as spaces.</param>
/// <param name="Key">What the command is handed: <c>builtin:</c> and a shipped template's stem, or a file's path.</param>
public sealed record DataDisplayTemplateEntry(string Name, string Key);

public partial class WorkspaceViewModel
{
    private const string BuiltInTemplateKey = "builtin:";
    private const string BrowseTemplateKey  = "browse";

    /// <summary>The templates the menu offers, in its order: shipped, this workspace's, the user's. A template in a
    /// later folder with the same name as an earlier one is not listed twice — the earlier one wins.</summary>
    public IReadOnlyList<DataDisplayTemplateEntry> DataDisplayTemplates
    {
        get
        {
            var list = new List<DataDisplayTemplateEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var stem in ShippedDataDisplayTemplates.Stems)
                if (seen.Add(TemplateTitle(stem))) list.Add(new(TemplateTitle(stem), BuiltInTemplateKey + stem));
            foreach (var dir in new[] { TemplateManager.WorkspaceTemplatesDir(CurrentWorkspaceRoot), TemplateManager.UserTemplatesDir })
            {
                if (dir is null || !Directory.Exists(dir)) continue;
                string[] files;
                try { files = Directory.GetFiles(dir, "*" + DataDisplayTemplate.Extension); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                foreach (var f in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    string name = TemplateTitle(Path.GetFileNameWithoutExtension(f));
                    if (seen.Add(name)) list.Add(new(name, f));
                }
            }
            return list;
        }
    }

    private static string TemplateTitle(string stem) => stem.Replace('_', ' ');

    /// <summary>The in-window rows, rebuilt each time the submenu opens so a template saved since is listed.
    /// Never empty — Browse… is always there — so the item never becomes a leaf that cannot open.</summary>
    public ObservableCollection<Control> DataDisplayTemplateMenuItems
    {
        get
        {
            if (_dataDisplayTemplateMenuItems is null)
            {
                _dataDisplayTemplateMenuItems = new();
                RebuildDataDisplayTemplateMenuItems();
            }
            return _dataDisplayTemplateMenuItems;
        }
    }

    private ObservableCollection<Control>? _dataDisplayTemplateMenuItems;

    /// <summary>Refills <see cref="DataDisplayTemplateMenuItems"/> from <see cref="DataDisplayTemplates"/>.</summary>
    public void RebuildDataDisplayTemplateMenuItems()
    {
        var items = _dataDisplayTemplateMenuItems ??= new();
        items.Clear();
        foreach (var t in DataDisplayTemplates)
        {
            var item = new MenuItem { Header = t.Name, Command = NewDataDisplayFromTemplateCommand, CommandParameter = t.Key };
            if (!t.Key.StartsWith(BuiltInTemplateKey, StringComparison.Ordinal)) ToolTip.SetTip(item, t.Key);
            items.Add(item);
        }
        items.Add(new Separator());
        items.Add(new MenuItem
        {
            Header = "Browse…", Command = NewDataDisplayFromTemplateCommand, CommandParameter = BrowseTemplateKey,
        });
    }

    /// <summary>
    /// A new, unsaved Data Display from a template: <c>builtin:</c> and a shipped template's stem, a <c>.cddt</c>
    /// path, or <c>browse</c> to pick one. Its traces read the source the new display selects — the most recent
    /// run, as New Data Display selects.
    /// </summary>
    [RelayCommand]
    private async Task NewDataDisplayFromTemplate(string? key)
    {
        if (key is null) return;
        if (key == BrowseTemplateKey)
        {
            if (await PickDataDisplayTemplateAsync() is { } picked) await OpenDataDisplayTemplateAsync(picked);
            return;
        }
        await OpenDataDisplayTemplateAsync(key);
    }

    /// <summary>Opens a `.cddt` — a shipped one by <c>builtin:</c> key, or a file — as a new, unsaved display. A
    /// template that will not read is reported, and no display is left behind.</summary>
    internal async Task OpenDataDisplayTemplateAsync(string key)
    {
        string name, json;
        try
        {
            if (key.StartsWith(BuiltInTemplateKey, StringComparison.Ordinal))
            {
                string stem = key[BuiltInTemplateKey.Length..];
                name = TemplateTitle(stem);
                json = ShippedDataDisplayTemplates.LoadJson(stem);
            }
            else
            {
                name = TemplateTitle(Path.GetFileNameWithoutExtension(key));
                json = await File.ReadAllTextAsync(key);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Messages.Error($"Cannot open the template '{Path.GetFileName(key)}': {ex.Message}");
            return;
        }

        var title = NextDataDisplayTitle();
        var vm    = new DataDisplayDocumentViewModel();
        var doc   = new DataDisplayDocument(title, vm);
        vm.Window.SetOpenFileAsNewDisplayAction(OpenDataDisplayFromFileAsync);
        vm.Window.GetResultsRootAction = GetResultsRoot;
        vm.Window.RefreshAvailableDataSources();
        // The source first, so every trace resolves against it as it loads.
        await vm.Window.DataSourceLibrary.SelectDataSourceAsync(vm.Window.DataSourceLibrary.MostRecentRunRef());
        try
        {
            await vm.Window.LoadTemplateAsync(json, name + DataDisplayTemplate.Extension);
        }
        catch (InvalidDataException ex)
        {
            Messages.Error($"Cannot open the template '{name}': {ex.Message}");
            return;
        }

        _scratchDataDisplays.Add(doc);
        WireDataDisplayLibraryEvents(vm);
        WireDataDisplayTreeDirty(doc);
        _factory.OpenDocument(doc);
    }

    private async Task<string?> PickDataDisplayTemplateAsync()
    {
        if (ResolveOwner(null) is not { } window) return null;
        var start = await TemplatesFolderAsync(window);
        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title                  = "New Data Display from Template",
            AllowMultiple          = false,
            SuggestedStartLocation = start,
            FileTypeFilter =
            [
                new FilePickerFileType("circuitRF Data Display Template") { Patterns = ["*" + DataDisplayTemplate.Extension] },
                new FilePickerFileType("All Files")                       { Patterns = ["*.*"] },
            ],
        });
        return files.Count == 1 ? files[0].TryGetLocalPath() : null;
    }

    private static async Task<IStorageFolder?> FolderAsync(Window window, string? dir)
        => await window.StorageProvider.TryGetReachableFolderAsync(dir);

    /// <summary>Where a template is looked for and saved first: the workspace's <c>templates/</c> folder when a
    /// workspace is open and has one, otherwise the user's own templates folder (created when saving).</summary>
    private async Task<IStorageFolder?> TemplatesFolderAsync(Window window, bool create = false)
    {
        if (await FolderAsync(window, TemplateManager.WorkspaceTemplatesDir(CurrentWorkspaceRoot)) is { } ws) return ws;
        if (create)
        {
            try { Directory.CreateDirectory(TemplateManager.UserTemplatesDir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return await FolderAsync(window, TemplateManager.UserTemplatesDir);
    }

    private bool IsDataDisplayDocumentActive() => ResolveActiveDocumentForCommands() is DataDisplayDocument;

    /// <summary>
    /// File ▸ Save Data Display As Template…: the active display written as a `.cddt`, every trace reading whatever
    /// source the display made from it selects. The display itself is unchanged — its own file, sources and unsaved
    /// changes are as they were.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsDataDisplayDocumentActive))]
    private async Task SaveDataDisplayAsTemplate(Window? owner)
    {
        if (ResolveActiveDocumentForCommands() is not DataDisplayDocument doc) return;
        if (ResolveOwner(owner) is not { } window) return;

        string suggested = doc.FilePath is { } fp ? Path.GetFileNameWithoutExtension(fp) : doc.Title;
        var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title                  = "Save Data Display As Template",
            SuggestedFileName      = suggested + DataDisplayTemplate.Extension,
            SuggestedStartLocation = await TemplatesFolderAsync(window, create: true),
            DefaultExtension       = DataDisplayTemplate.Extension.TrimStart('.'),
            FileTypeChoices        =
            [
                new FilePickerFileType("circuitRF Data Display Template") { Patterns = ["*" + DataDisplayTemplate.Extension] },
            ],
        });
        if (file?.TryGetLocalPath() is not { Length: > 0 } path) return;

        try
        {
            await doc.ViewModel.Window.SaveAsTemplateAsync(path);
            Messages.Success("Saved Data Display template", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Messages.Error($"Save Data Display As Template: {ex.Message}");
        }
        RebuildDataDisplayTemplateMenuItems();
    }
}
