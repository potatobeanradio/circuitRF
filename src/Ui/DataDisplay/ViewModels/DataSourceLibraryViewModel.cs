// ================================================================
//  DataSourceLibraryViewModel.cs  —  manages all data-source files loaded in memory
// ================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Platform.Storage;
using RfCore;
using RfCore.Data;
using RfCore.Export;
using RfCore.Loadpull;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

/// <summary>One entry in the datasource combobox.</summary>
public record DataSourceItem(
    string DisplayName,
    string LogicalId,
    string AbsolutePath,
    SourceKind Kind);

public partial class DataSourceLibraryViewModel : ViewModelBase
{
    public ObservableCollection<DataSourceEntryViewModel> Entries { get; } = new();

    /// <summary>
    /// Fired after any load, reload, remove, or restore operation.
    /// Subscribers (e.g. PlotInspectorViewModel) use this to trigger
    /// path rebuilds when data changes in-place.
    /// </summary>
    public event EventHandler? LibraryChanged;

    /// <summary>Fired once per source path when a source with non-uniform or complex Z0 is loaded.
    /// Workspace subscribes and posts a one-time warning to the Messages pane.</summary>
    public event Action<string, Z0Kind, IReadOnlyList<Complex>>? UnusualZ0Detected;

    // Tracks which paths have already triggered the unusual-Z0 warning (per library instance).
    private readonly HashSet<string> _warnedPaths = new(StringComparer.OrdinalIgnoreCase);

    private void MaybeFireUnusualZ0Warning(DataSourceEntryViewModel entry)
    {
        if (!entry.HasUnusualZ0) return;
        if (entry.FilePath is not string path) return;
        if (!_warnedPaths.Add(path)) return;
        UnusualZ0Detected?.Invoke(path, entry.Z0Kind!.Value, entry.Z0PerPort);
    }

    // ---- Single-source selection (brief: single-datasource Data Display) -

    /// <summary>Returns the workspace results directory, e.g. "…/results". Null outside a workspace.</summary>
    public Func<string?>? ResultsRootProvider { get; set; }

    /// <summary>Returns absolute paths of workspace-tracked Touchstone known files.</summary>
    public Func<IReadOnlyList<string>>? KnownTouchstoneProvider { get; set; }

    /// <summary>Returns absolute paths of workspace-tracked loadpull known files (.spl/.lpcwave).</summary>
    public Func<IReadOnlyList<string>>? KnownLoadpullProvider { get; set; }

    /// <summary>Logical id persisted in .cdd (e.g. flat "ampA.npy" under results/, or abs Touchstone path).</summary>
    public string? SelectedDataSourceRef { get; private set; }

    /// <summary>Resolved absolute path for the selected datasource; null when nothing is selected or file missing.</summary>
    public string? SelectedDataSourceAbs { get; private set; }

    /// <summary>The loaded library entry for the selected datasource, or null when lazy-load hasn't happened yet.</summary>
    public DataSourceEntryViewModel? SelectedEntry { get; private set; }

    /// <summary>Available datasources for the toolbar combobox. Rebuilt by RefreshAvailableDataSources.</summary>
    public System.Collections.ObjectModel.ObservableCollection<DataSourceItem> AvailableDataSources { get; } = new();

    /// <summary>Fired after SelectDataSourceAsync completes (selection or load changed).</summary>
    public event EventHandler? SelectedDataSourceChanged;

    /// <summary>Resolves a logical SourceRef to an absolute path.
    /// Null/sentinel → SelectedDataSourceAbs. Rooted → as-is. Relative → under ResultsRootProvider.</summary>
    public string? ResolveAbs(string? sourceRef)
    {
        if (string.IsNullOrEmpty(sourceRef) || sourceRef == DataSourceRef.Selected)
            return SelectedDataSourceAbs;
        if (Path.IsPathRooted(sourceRef)) return sourceRef;
        var root = ResultsRootProvider?.Invoke();
        return root is null ? null : Path.GetFullPath(Path.Combine(root, sourceRef));
    }

    /// <summary>Enumerate available datasources without loading any file. Safe when there's no workspace.
    /// R-res-7 — results/ is a FLAT, shared directory (brief-results-storage-and-data-display.md §1):
    /// every schematic's own results file, plus any user-named baseline, sits directly in it as
    /// "&lt;name&gt;.npy" — there is no per-schematic subdirectory to descend into.</summary>
    public void RefreshAvailableDataSources()
    {
        AvailableDataSources.Clear();

        var root = ResultsRootProvider?.Invoke();
        if (root is not null && Directory.Exists(root))
        {
            var simItems = new List<(string logicalId, string abs, long lastWrite)>();
            foreach (var npy in Directory.EnumerateFiles(root, "*.npy"))
            {
                string name      = Path.GetFileNameWithoutExtension(npy);
                string logicalId = Path.GetFileName(npy);   // "<name>.npy", relative to results root
                long ticks;
                try { ticks = new System.IO.FileInfo(npy).LastWriteTimeUtc.Ticks; }
                catch { ticks = 0; }
                simItems.Add((logicalId, npy, ticks));
            }
            simItems.Sort((a, b) => b.lastWrite.CompareTo(a.lastWrite));
            foreach (var (logicalId, abs, _) in simItems)
            {
                string name = Path.GetFileNameWithoutExtension(logicalId);
                AvailableDataSources.Add(new DataSourceItem(name, logicalId, abs, SourceKind.Npy));
            }
        }

        // Workspace known Touchstone files, sorted by name.
        var touchstone = KnownTouchstoneProvider?.Invoke() ?? Array.Empty<string>();
        foreach (var p in touchstone.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            AvailableDataSources.Add(new DataSourceItem(Path.GetFileName(p), p, p, SourceKind.Touchstone));

        // Workspace known loadpull files (.spl/.lpcwave), sorted by name.
        var loadpull = KnownLoadpullProvider?.Invoke() ?? Array.Empty<string>();
        foreach (var p in loadpull.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var kind = IsSplExtension(Path.GetExtension(p)) ? SourceKind.Spl : SourceKind.Lpcwave;
            AvailableDataSources.Add(new DataSourceItem(Path.GetFileName(p), p, p, kind));
        }
    }

    /// <summary>Returns the LogicalId ("&lt;name&gt;.npy") of the most-recently-written results file
    /// directly under results/, or null.</summary>
    public string? MostRecentRunRef()
    {
        var root = ResultsRootProvider?.Invoke();
        if (root is null || !Directory.Exists(root)) return null;

        string? bestId    = null;
        long    bestTicks = 0;
        foreach (var npy in Directory.EnumerateFiles(root, "*.npy"))
        {
            long ticks;
            try { ticks = new System.IO.FileInfo(npy).LastWriteTimeUtc.Ticks; }
            catch { continue; }
            if (ticks > bestTicks)
            {
                bestTicks = ticks;
                bestId    = Path.GetFileName(npy);
            }
        }
        return bestId;
    }

    /// <summary>Select by logical id: resolve, lazy-load, set SelectedEntry, fire event.</summary>
    public async Task SelectDataSourceAsync(string? logicalId)
    {
        SelectedDataSourceRef = logicalId;
        SelectedDataSourceAbs = ResolveAbs(logicalId);
        SelectedEntry         = null;

        if (SelectedDataSourceAbs is not null)
        {
            if (File.Exists(SelectedDataSourceAbs))
            {
                // Load into cache if not already present.
                await LoadFileAsync(SelectedDataSourceAbs);
                SelectedEntry = Entries.FirstOrDefault(e =>
                    string.Equals(e.FilePath, SelectedDataSourceAbs,
                                  StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                // File missing — leave SelectedEntry null; traces render <invalid>.
                AddBrokenEntry(SelectedDataSourceAbs);
                SelectedEntry = Entries.FirstOrDefault(e =>
                    string.Equals(e.FilePath, SelectedDataSourceAbs,
                                  StringComparison.OrdinalIgnoreCase));
            }
        }

        SelectedDataSourceChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- UI callbacks set from code-behind --------------------------------

    /// <summary>
    /// Called with the missing file's full path when the user requests a reload
    /// of a broken entry.  Should show a file picker and return the chosen path,
    /// or null if the user cancelled.
    /// </summary>
    public Func<string, Task<string?>>? FindMissingFileAsync { get; set; }

    /// <summary>
    /// Called when the Add Trace picker's source selector chooses "Add from file…" (R-dd-2).
    /// Should show a single-file picker and return the chosen path, or null if the user cancelled.
    /// </summary>
    public Func<Task<string?>>? AddSourceFileRequested { get; set; }

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

        if (IsSplExtension(ext))
        {
            await LoadSplAsync(path);
            return;
        }

        if (IsLpcwaveExtension(ext))
        {
            await LoadLpcwaveAsync(path);
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
        var newEntry = new DataSourceEntryViewModel(snp, this);
        Entries.Add(newEntry);
        UpdateDisplayNames();
        MaybeFireUnusualZ0Warning(newEntry);
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

        if (IsSplExtension(ext))
        {
            await LoadSplAsync(path);
            return;
        }

        if (IsLpcwaveExtension(ext))
        {
            await LoadLpcwaveAsync(path);
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
        var newEntry2 = new DataSourceEntryViewModel(snp, this);
        Entries.Add(newEntry2);
        UpdateDisplayNames();
        MaybeFireUnusualZ0Warning(newEntry2);
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

        DataSourceEntryViewModel entry;
        string brokenExt = Path.GetExtension(path);
        if (IsNpyExtension(brokenExt))
        {
            // Broken .npy: use SNP.CreateBroken so IsBroken returns true,
            // but store no DataSet (file is missing).
            entry = new DataSourceEntryViewModel(path, data: null, snp: SNP.CreateBroken(path), this);
        }
        else if (IsSplExtension(brokenExt))
        {
            entry = new DataSourceEntryViewModel(path, data: null, snp: SNP.CreateBroken(path), this,
                                                 SourceKind.Spl);
        }
        else if (IsLpcwaveExtension(brokenExt))
        {
            entry = new DataSourceEntryViewModel(path, data: null, snp: SNP.CreateBroken(path), this,
                                                 SourceKind.Lpcwave);
        }
        else
        {
            var snp = SNP.CreateBroken(path);
            entry = new DataSourceEntryViewModel(snp, this);
        }

        Entries.Add(entry);
        UpdateDisplayNames();
        // No LibraryChanged: broken entries have no usable data to rebuild paths with.
    }

    /// <summary>
    /// Replace a broken entry's data with a freshly-loaded file.
    /// Routes by extension; updates the path if the user chose a different location.
    /// </summary>
    public async Task RestoreBrokenEntry(DataSourceEntryViewModel entry, string newPath)
    {
        if (entry.Kind == SourceKind.Npy)
        {
            DataSet data;
            try { data = (await Task.Run(() => DataSetImporter.Import(newPath))).DataSet; }
            catch { return; }

            entry.RefreshNpy(data, newPath);
            UpdateDisplayNames();
            MaybeFireUnusualZ0Warning(entry);
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (entry.Kind == SourceKind.Spl)
        {
            DataSet data;
            try { data = await Task.Run(() => SplReader.ReadSpl(newPath)); }
            catch { return; }

            entry.RefreshLoadpull(data, newPath);
            UpdateDisplayNames();
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (entry.Kind == SourceKind.Lpcwave)
        {
            DataSet data;
            try { data = await Task.Run(() => LpcwaveReader.ReadLpcwave(newPath)); }
            catch { return; }

            entry.RefreshLoadpull(data, newPath);
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
            MaybeFireUnusualZ0Warning(entry);
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Reload an entry from disk, or prompt the user to find a missing file
    /// when the entry is broken.
    /// </summary>
    public async Task ReloadAsync(DataSourceEntryViewModel entry)
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
        else if (entry.Kind == SourceKind.Spl)
        {
            DataSet data;
            try { data = await Task.Run(() => SplReader.ReadSpl(entry.FilePath!)); }
            catch { return; }

            entry.RefreshLoadpull(data, entry.FilePath!);
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (entry.Kind == SourceKind.Lpcwave)
        {
            DataSet data;
            try { data = await Task.Run(() => LpcwaveReader.ReadLpcwave(entry.FilePath!)); }
            catch { return; }

            entry.RefreshLoadpull(data, entry.FilePath!);
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

    /// <summary>
    /// The one mutator for <see cref="DataSourceEntryViewModel.Alias"/> (R-res-4) — enforces
    /// uniqueness within the library (case-insensitive) so trace labels qualified by alias are
    /// never ambiguous. Blank input falls back to the entry's own file stem. Returns false (leaving
    /// the alias unchanged) when the candidate collides with a DIFFERENT entry's alias.
    /// </summary>
    public bool TrySetAlias(DataSourceEntryViewModel entry, string proposed)
    {
        var candidate = string.IsNullOrWhiteSpace(proposed)
            ? DataSourceEntryViewModel.DefaultAlias(entry.FileName)
            : proposed.Trim();

        bool collides = Entries.Any(e =>
            !ReferenceEquals(e, entry) &&
            string.Equals(e.Alias, candidate, StringComparison.OrdinalIgnoreCase));
        if (collides) return false;

        entry.Alias = candidate;
        LibraryChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Looks up the alias of the entry whose FilePath matches <paramref name="absPath"/>,
    /// or null when there is no such entry (e.g. a trace bound to a not-yet-loaded source).</summary>
    public string? AliasFor(string? absPath)
    {
        if (absPath is null) return null;
        return Entries.FirstOrDefault(e =>
            string.Equals(e.FilePath, absPath, StringComparison.OrdinalIgnoreCase))?.Alias;
    }

    /// <summary>Remove an entry from the library (does not delete the file).</summary>
    public void Remove(DataSourceEntryViewModel entry)
    {
        if (entry.FilePath is string fp) _warnedPaths.Remove(fp);
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

        var npyEntry = new DataSourceEntryViewModel(path, data, snp, this);
        Entries.Add(npyEntry);
        UpdateDisplayNames();
        MaybeFireUnusualZ0Warning(npyEntry);
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task LoadSplAsync(string path)
    {
        var broken = Entries.FirstOrDefault(e =>
            e.Kind == SourceKind.Spl && e.IsBroken &&
            string.Equals(e.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (broken != null)
        {
            await RestoreBrokenEntry(broken, path);
            return;
        }

        if (Entries.Any(e => !e.IsBroken &&
                string.Equals(e.FilePath, path, StringComparison.OrdinalIgnoreCase)))
            return;

        DataSet data;
        try { data = await Task.Run(() => SplReader.ReadSpl(path)); }
        catch { return; }

        var entry = new DataSourceEntryViewModel(path, data, null, this, SourceKind.Spl);
        Entries.Add(entry);
        UpdateDisplayNames();
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task LoadLpcwaveAsync(string path)
    {
        var broken = Entries.FirstOrDefault(e =>
            e.Kind == SourceKind.Lpcwave && e.IsBroken &&
            string.Equals(e.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (broken != null)
        {
            await RestoreBrokenEntry(broken, path);
            return;
        }

        if (Entries.Any(e => !e.IsBroken &&
                string.Equals(e.FilePath, path, StringComparison.OrdinalIgnoreCase)))
            return;

        DataSet data;
        try { data = await Task.Run(() => LpcwaveReader.ReadLpcwave(path)); }
        catch { return; }

        var entry = new DataSourceEntryViewModel(path, data, null, this, SourceKind.Lpcwave);
        Entries.Add(entry);
        UpdateDisplayNames();
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- Test helpers (accessible to CircuitRF.Ui.Tests via InternalsVisibleTo) ----

    internal void FireLibraryChangedForTest() => LibraryChanged?.Invoke(this, EventArgs.Empty);

    // ---- Extension helpers ------------------------------------------------

    private static readonly HashSet<string> _snpExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".s1p", ".s2p", ".s3p", ".s4p", ".s5p", ".s6p", ".s7p", ".s8p", ".s9p", ".s10p",
        ".s11p", ".s12p", ".s13p", ".s14p", ".s15p", ".s16p", ".s17p", ".s18p", ".s19p", ".s20p",
        ".s21p", ".s22p", ".s23p", ".s24p",
        ".snp", ".ts" };

    private static bool IsSnpExtension(string ext)     => _snpExtensions.Contains(ext);
    private static bool IsNpyExtension(string ext)      =>
        string.Equals(ext, ".npy",      StringComparison.OrdinalIgnoreCase);
    private static bool IsSplExtension(string ext)      =>
        string.Equals(ext, ".spl",      StringComparison.OrdinalIgnoreCase);
    private static bool IsLpcwaveExtension(string ext)  =>
        string.Equals(ext, ".lpcwave",  StringComparison.OrdinalIgnoreCase);
}
