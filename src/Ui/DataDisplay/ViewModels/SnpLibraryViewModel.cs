// ================================================================
//  SnpLibraryViewModel.cs  —  manages all SNP files loaded in memory
// ================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using RfCore;

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

    // ---- Public API -------------------------------------------------------

    /// <summary>
    /// Load a Touchstone file from disk.  If a broken entry with the same path
    /// already exists it is restored instead of loading a duplicate.
    /// Silently ignores unrecognised extensions or unparseable files.
    /// </summary>
    public async Task LoadFileAsync(string path)
    {
        path = Path.GetFullPath(path);
        if (!IsSnpExtension(Path.GetExtension(path))) return;

        // If a broken entry matches this path, restore it instead of adding a duplicate.
        var broken = Entries.FirstOrDefault(e =>
            e.Snp.IsEmpty &&
            string.Equals(e.Snp.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (broken != null)
        {
            await RestoreBrokenEntry(broken, path);
            return;
        }

        // Skip normal duplicates
        if (Entries.Any(e => !e.Snp.IsEmpty &&
                string.Equals(e.Snp.FilePath, path, StringComparison.OrdinalIgnoreCase)))
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
    /// Load a Touchstone file supplied as an <see cref="IStorageFile"/> (e.g. from a
    /// macOS Apple Event / Finder double-click).  Reads content via
    /// <see cref="IStorageFile.OpenReadAsync"/> so that security-scoped URL access
    /// is honoured instead of relying on bare-path I/O.
    /// </summary>
    public async Task LoadFileAsync(IStorageFile file)
    {
        string path = Path.GetFullPath(file.Path.LocalPath);
        if (!IsSnpExtension(Path.GetExtension(path))) return;

        var broken = Entries.FirstOrDefault(e =>
            e.Snp.IsEmpty &&
            string.Equals(e.Snp.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (broken != null) { await RestoreBrokenEntry(broken, path); return; }

        if (Entries.Any(e => !e.Snp.IsEmpty &&
                string.Equals(e.Snp.FilePath, path, StringComparison.OrdinalIgnoreCase)))
            return;

        SNP? snp;
        try
        {
            // Open via the storage API so macOS security-scoped URLs are handled.
            // TouchstoneIO.Read(TextReader) infers the port count from the file content
            // when no extension hint is provided.
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
    /// Does not fire LibraryChanged — the caller is responsible for adding
    /// any associated Trace objects before PlotInspectorViewModels subscribe.
    /// </summary>
    public void AddBrokenEntry(string path)
    {
        if (Entries.Any(e => string.Equals(e.Snp.FilePath, path,
                StringComparison.OrdinalIgnoreCase)))
            return;

        var snp = SNP.CreateBroken(path);
        Entries.Add(new SnpEntryViewModel(snp, this));
        UpdateDisplayNames();
        // No LibraryChanged: broken entries have no usable data to rebuild paths with.
    }

    /// <summary>
    /// Replace a broken entry's data with a freshly-loaded file.
    /// Updates the FilePath if the user chose a different location.
    /// </summary>
    public async Task RestoreBrokenEntry(SnpEntryViewModel entry, string newPath)
    {
        SNP newData;
        try { newData = await Task.Run(() => TouchstoneIO.ReadFile(newPath)); }
        catch { return; }

        entry.Snp.FilePath = newPath;
        entry.Snp.RefreshFrom(newData);
        entry.NotifyBrokenStateChanged();
        UpdateDisplayNames();
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Reload an entry from disk, or prompt the user to find a missing file
    /// when the entry is broken.
    /// </summary>
    public async Task ReloadAsync(SnpEntryViewModel entry)
    {
        bool filePresent = !string.IsNullOrEmpty(entry.Snp.FilePath)
                           && File.Exists(entry.Snp.FilePath);

        if (entry.Snp.IsEmpty || !filePresent)
        {
            // Missing file — ask the user to locate it.
            if (FindMissingFileAsync is null) return;
            string? newPath = await FindMissingFileAsync(entry.Snp.FilePath ?? "");
            if (newPath is null) return;
            await RestoreBrokenEntry(entry, newPath);
            return;
        }

        SNP newData;
        try { newData = await Task.Run(() => TouchstoneIO.ReadFile(entry.Snp.FilePath!)); }
        catch { return; }

        entry.Snp.RefreshFrom(newData);
        entry.NotifyBrokenStateChanged();
        LibraryChanged?.Invoke(this, EventArgs.Empty);
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
            .GroupBy(e => Path.GetFileName(e.Snp.FilePath ?? "").ToLowerInvariant())
            .ToList();

        foreach (var group in groups)
        {
            var items = group.ToList();

            if (items.Count == 1)
            {
                items[0].DisplayName = items[0].Snp.FileName;
                continue;
            }

            var segments = items
                .Select(e => (e.Snp.FilePath ?? "")
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

    // ---- Helpers ----------------------------------------------------------

    private static readonly HashSet<string> _snpExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".s1p", ".s2p", ".s3p", ".s4p", ".s5p", ".s6p", ".s7p", ".s8p", ".s9p", ".s10p",
        ".s11p", ".s12p", ".s13p", ".s14p", ".s15p", ".s16p", ".s17p", ".s18p", ".s19p", ".s20p",
        ".s21p", ".s22p", ".s23p", ".s24p",
        ".snp", ".ts" };

    private static bool IsSnpExtension(string ext) =>
        _snpExtensions.Contains(ext);
}
