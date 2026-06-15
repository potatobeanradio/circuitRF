// ================================================================
//  SnpLibraryViewModel.cs  —  manages all data-source files loaded in memory
//
//  NAMING DEBT: This class now loads both Touchstone (.sNp) and .npy files.
//  Each entry carries a DataSet; .npy-with-S exposes a ToSnp SNP for the
//  existing picker.  Rename to DataSourceLibraryViewModel in 7.2c when the
//  cube-native trace path + class rename ship.
// ================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Platform.Storage;
using RfCore;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

public partial class SnpLibraryViewModel : ViewModelBase
{
    public ObservableCollection<SnpEntryViewModel> Entries { get; } = new();

    /// <summary>
    /// Fired after any load, reload, remove, or restore operation.
    /// Subscribers (e.g. PlotInspectorViewModel) use this to trigger
    /// path rebuilds when data changes in-place.
    /// </summary>
    public event EventHandler? LibraryChanged;

    // ---- UI callbacks set from code-behind --------------------------------

    /// <summary>
    /// Called with the missing file's full path when the user requests a reload
    /// of a broken entry.  Should show a file picker and return the chosen path,
    /// or null if the user cancelled.
    /// </summary>
    public Func<string, Task<string?>>? FindMissingFileAsync { get; set; }

    /// <summary>Copies text to the system clipboard (set from code-behind).</summary>
    public Func<string, Task>? CopyToClipboardFunc { get; set; }

    /// <summary>
    /// Returns the directory of the currently open config file, or null if
    /// no config has been saved/loaded yet.  Set from MainWindow code-behind
    /// as a live getter over DataDisplay.CurrentConfigPath.
    /// </summary>
    public Func<string?>? GetConfigDirectoryFunc { get; set; }

    /// <summary>
    /// Command bound to the library header's import button.
    /// Set by the document container to Window.OpenFileCommand so the button
    /// opens the file picker without the view needing a Window reference.
    /// </summary>
    public ICommand? ImportCommand { get; internal set; }

    // ---- Public API -------------------------------------------------------

    /// <summary>
    /// Load a Touchstone or .npy file from disk.
    /// Touchstone extensions → TouchstoneIO; .npy → DataSetImporter.
    /// Broken-entry restore and deduplication apply to both formats.
    /// Silently ignores unrecognised extensions or unparseable files.
    /// </summary>
    public async Task LoadFileAsync(string path)
    {
        path = Path.GetFullPath(path);
        string ext = Path.GetExtension(path);

        if (IsNpyExtension(ext))
        {
            await LoadNpyAsync(path);
            return;
        }

        if (!IsSnpExtension(ext)) return;

        // If a broken Touchstone entry matches this path, restore it.
        var broken = Entries.FirstOrDefault(e =>
            e.Kind == SourceKind.Touchstone && e.IsBroken &&
            string.Equals(e.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (broken != null)
        {
            await RestoreBrokenEntry(broken, path);
            return;
        }

        // Skip normal duplicates
        if (Entries.Any(e => !e.IsBroken &&
                string.Equals(e.FilePath, path, StringComparison.OrdinalIgnoreCase)))
            return;

        SNP? snp;
        try { snp = await Task.Run(() => TouchstoneIO.ReadFile(path)); }
        catch { return; }

        snp.FilePath = path;
        Entries.Add(new SnpEntryViewModel(snp, this));
        UpdateDisplayNames();
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Load a Touchstone or .npy file supplied as an <see cref="IStorageFile"/>
    /// (e.g. from a macOS Apple Event / Finder double-click).
    /// Touchstone → reads via <see cref="IStorageFile.OpenReadAsync"/> for
    /// security-scoped URL access.  .npy → uses LocalPath directly.
    /// </summary>
    public async Task LoadFileAsync(IStorageFile file)
    {
        string path = Path.GetFullPath(file.Path.LocalPath);
        string ext  = Path.GetExtension(path);

        if (IsNpyExtension(ext))
        {
            await LoadNpyAsync(path);
            return;
        }

        if (!IsSnpExtension(ext)) return;

        var broken = Entries.FirstOrDefault(e =>
            e.Kind == SourceKind.Touchstone && e.IsBroken &&
            string.Equals(e.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (broken != null) { await RestoreBrokenEntry(broken, path); return; }

        if (Entries.Any(e => !e.IsBroken &&
                string.Equals(e.FilePath, path, StringComparison.OrdinalIgnoreCase)))
            return;

        SNP? snp;
        try
        {
            // Open via the storage API so macOS security-scoped URLs are handled.
            using var stream = await file.OpenReadAsync();
            snp = await Task.Run(() =>
            {
                using var reader = new StreamReader(stream);
                return TouchstoneIO.Read(reader);
            });
        }
        catch { return; }

        snp.FilePath = path;
        Entries.Add(new SnpEntryViewModel(snp, this));
        UpdateDisplayNames();
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Add a placeholder entry for a file that could not be found on disk.
    /// Routes by extension: Touchstone → SNP broken entry; .npy → .npy broken entry.
    /// Does not fire LibraryChanged — the caller is responsible for adding
    /// any associated Trace objects before PlotInspectorViewModels subscribe.
    /// </summary>
    public void AddBrokenEntry(string path)
    {
        if (Entries.Any(e => string.Equals(e.FilePath, path,
                StringComparison.OrdinalIgnoreCase)))
            return;

        SnpEntryViewModel entry;
        if (IsNpyExtension(Path.GetExtension(path)))
        {
            // Broken .npy: use SNP.CreateBroken so IsBroken returns true,
            // but store no DataSet (file is missing).
            entry = new SnpEntryViewModel(path, data: null, snp: SNP.CreateBroken(path), this);
        }
        else
        {
            var snp = SNP.CreateBroken(path);
            entry = new SnpEntryViewModel(snp, this);
        }

        Entries.Add(entry);
        UpdateDisplayNames();
        // No LibraryChanged: broken entries have no usable data to rebuild paths with.
    }

    /// <summary>
    /// Replace a broken entry's data with a freshly-loaded file.
    /// Routes by extension; updates the path if the user chose a different location.
    /// </summary>
    public async Task RestoreBrokenEntry(SnpEntryViewModel entry, string newPath)
    {
        if (entry.Kind == SourceKind.Npy)
        {
            DataSet data;
            try { data = (await Task.Run(() => DataSetImporter.Import(newPath))).DataSet; }
            catch { return; }

            entry.RefreshNpy(data, newPath);
            UpdateDisplayNames();
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            SNP newData;
            try { newData = await Task.Run(() => TouchstoneIO.ReadFile(newPath)); }
            catch { return; }

            entry.RefreshTouchstone(newData, newPath);
            UpdateDisplayNames();
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Reload an entry from disk, or prompt the user to find a missing file
    /// when the entry is broken.
    /// </summary>
    public async Task ReloadAsync(SnpEntryViewModel entry)
    {
        bool filePresent = !string.IsNullOrEmpty(entry.FilePath)
                           && File.Exists(entry.FilePath);

        if (entry.IsBroken || !filePresent)
        {
            // Missing file — ask the user to locate it.
            if (FindMissingFileAsync is null) return;
            string? newPath = await FindMissingFileAsync(entry.FilePath ?? "");
            if (newPath is null) return;
            await RestoreBrokenEntry(entry, newPath);
            return;
        }

        if (entry.Kind == SourceKind.Npy)
        {
            DataSet data;
            try { data = (await Task.Run(() => DataSetImporter.Import(entry.FilePath!))).DataSet; }
            catch { return; }

            entry.RefreshNpy(data, entry.FilePath!);
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            SNP newData;
            try { newData = await Task.Run(() => TouchstoneIO.ReadFile(entry.FilePath!)); }
            catch { return; }

            entry.RefreshTouchstone(newData, entry.FilePath!);
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Reload only the entries whose source file matches one of <paramref name="changedAbsPaths"/>
    /// (used after a run regenerates .npy results). Files that don't exist are skipped (no missing-file prompt).
    /// Fires LibraryChanged per reloaded entry so open inspectors rebuild + redraw the affected traces only.</summary>
    public async Task ReloadChangedAsync(IReadOnlyCollection<string> changedAbsPaths)
    {
        if (changedAbsPaths.Count == 0) return;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in changedAbsPaths) set.Add(Path.GetFullPath(p));

        foreach (var entry in Entries.ToList())   // snapshot: ReloadAsync may mutate state
        {
            if (entry.FilePath is not string fp) continue;
            if (!set.Contains(Path.GetFullPath(fp))) continue;
            if (!File.Exists(fp)) continue;        // never trigger the FindMissingFileAsync prompt during auto-refresh
            await ReloadAsync(entry);              // in-place refresh + LibraryChanged
        }
    }

    /// <summary>Remove an entry from the library (does not delete the file).</summary>
    public void Remove(SnpEntryViewModel entry)
    {
        Entries.Remove(entry);
        UpdateDisplayNames();
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- Display name computation ----------------------------------------

    private void UpdateDisplayNames()
    {
        var groups = Entries
            .GroupBy(e => Path.GetFileName(e.FilePath ?? "").ToLowerInvariant())
            .ToList();

        foreach (var group in groups)
        {
            var items = group.ToList();

            if (items.Count == 1)
            {
                items[0].DisplayName = items[0].FileName ?? items[0].FilePath ?? "";
                continue;
            }

            var segments = items
                .Select(e => (e.FilePath ?? "")
                    .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Reverse()
                    .ToArray())
                .ToList();

            int maxDepth = segments.Min(s => s.Length);
            int depth    = 1;

            while (depth < maxDepth)
            {
                var suffixes = segments
                    .Select(s => string.Join("/", s.Take(depth).Reverse()))
                    .ToList();

                if (suffixes.Distinct(StringComparer.OrdinalIgnoreCase).Count()
                    == suffixes.Count)
                    break;

                depth++;
            }

            for (int i = 0; i < items.Count; i++)
                items[i].DisplayName = string.Join("/",
                    segments[i].Take(depth).Reverse());
        }
    }

    // ---- Private helpers --------------------------------------------------

    private async Task LoadNpyAsync(string path)
    {
        // Restore broken .npy entry if present.
        var broken = Entries.FirstOrDefault(e =>
            e.Kind == SourceKind.Npy && e.IsBroken &&
            string.Equals(e.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (broken != null)
        {
            await RestoreBrokenEntry(broken, path);
            return;
        }

        // Skip normal duplicates.
        if (Entries.Any(e => !e.IsBroken &&
                string.Equals(e.FilePath, path, StringComparison.OrdinalIgnoreCase)))
            return;

        DataSet data;
        try { data = (await Task.Run(() => DataSetImporter.Import(path))).DataSet; }
        catch { return; }

        SNP? snp = null;
        if (data.Contains("S"))
        {
            try
            {
                snp = DataSetBuilder.ToSnp(data);
                snp.FilePath = path;
            }
            catch { snp = null; }
        }

        Entries.Add(new SnpEntryViewModel(path, data, snp, this));
        UpdateDisplayNames();
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- Extension helpers ------------------------------------------------

    private static readonly HashSet<string> _snpExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".s1p", ".s2p", ".s3p", ".s4p", ".s5p", ".s6p", ".s7p", ".s8p", ".s9p", ".s10p",
        ".s11p", ".s12p", ".s13p", ".s14p", ".s15p", ".s16p", ".s17p", ".s18p", ".s19p", ".s20p",
        ".s21p", ".s22p", ".s23p", ".s24p",
        ".snp", ".ts" };

    private static bool IsSnpExtension(string ext) => _snpExtensions.Contains(ext);
    private static bool IsNpyExtension(string ext)  =>
        string.Equals(ext, ".npy", StringComparison.OrdinalIgnoreCase);
}
