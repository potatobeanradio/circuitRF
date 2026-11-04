using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.ViewModels.Dock;

/// <summary>A column the vendor part list sorts by (R-vl2-2a).</summary>
public enum VendorPartSort { Value, Part, Description, Range }

/// <summary>
/// One row of a vendor library's part list. Built once per library index and reused across every
/// filter and sort, so a keystroke allocates a list of references rather than tens of thousands of
/// rows; the text is formatted on first read, which in a virtualized list means for the rows on
/// screen only.
/// </summary>
public sealed partial class VendorPartRowVm : ObservableObject
{
    public string Library { get; }
    public VendorLibraryEntry Entry { get; }

    [ObservableProperty]
    private bool _isArmed;

    public ICommand ArmCommand { get; }

    internal VendorPartRowVm(string library, VendorLibraryEntry entry, Action<VendorPartRowVm> arm)
    {
        Library    = library;
        Entry      = entry;
        ArmCommand = new RelayCommand(() => arm(this));
    }

    public string Part => Entry.Part;

    public string Value => _value ??= Entry.Header.ValueText();
    private string? _value;

    /// <summary>The description fields, or the file stem for a part whose header has none.</summary>
    public string Description => Entry.Header.Description ?? Entry.FileStem;

    public string Range => _range ??= Entry.Header.FrequencyRangeText();
    private string? _range;

    /// <summary>The row's second line: the description, then the frequency range — or, for a part with no build for
    /// this platform, the sentence saying so.</summary>
    public string Detail => _detail ??= Unavailable ?? (Range.Length == 0 ? Description : $"{Description} · {Range}");
    private string? _detail;

    public VendorPartRef Ref => _ref ??= VendorPartRef.For(Library, Entry);
    private VendorPartRef? _ref;

    /// <summary>Why the part cannot be placed on this platform (brief vendorlib-15 D3), or null. Such a row is
    /// listed, greyed, and neither arms nor drags.</summary>
    public string? Unavailable => Entry.Model?.NoBuildSentence();
    public bool IsPlaceable => Unavailable is null;

    /// <summary>The tooltip: everything the row abbreviates, and for a part with no build here, why it is grey.</summary>
    public string ToolTipText => string.Join("\n", new[]
    {
        Part, Value, Entry.Header.Description ?? "", Range, Entry.File, Unavailable ?? "",
    }.Where(t => t.Length > 0));
}

/// <summary>"312 parts in &lt;library&gt; match" — a row above the All grid that selects that library.</summary>
public sealed class VendorLibraryMatchVm(string text, ICommand select)
{
    public string Text { get; } = text;
    public ICommand SelectCommand { get; } = select;
}

/// <summary>
/// The vendor-library half of the palette (brief vendorlib-2): the libraries' Type menus, the
/// virtualized part list, value-aware search and arming by part identity.
///
/// <para><b>Nothing here reads a file.</b> Every list, count and match comes from the in-memory index
/// (<see cref="VendorLibraryIndex.Latest"/>); a library still being indexed says so instead of
/// waiting. The index's own <see cref="VendorLibraryIndex.FileReads"/> counter is what the
/// responsiveness gate asserts on.</para>
/// </summary>
public sealed partial class PaletteTool
{
    private IReadOnlyList<(string Name, string? Root)> _vendorLibs = [];

    /// <summary>
    /// The workspace's vendor libraries — name, and the folder it resolves to (null when it does not).
    /// Starts an index build for any library that has none yet; never waits for one.
    /// </summary>
    public void SetVendorLibraries(IReadOnlyList<(string Name, string? Root)> libraries)
    {
        _vendorLibs = libraries ?? [];
        foreach (var (_, root) in _vendorLibs)
            if (root is not null && VendorLibraryIndex.Latest(root) is null && !VendorLibraryIndex.IsBuilding(root))
                _ = VendorLibraryIndex.StartBuild(root);
        RebuildCategoriesKeepingSelection();
    }

    /// <summary>A library's index finished building: its menu, counts and list are rebuilt from it.</summary>
    public void RefreshVendorIndexes() => RebuildCategoriesKeepingSelection();

    private string? RootOf(string? library)
        => library is null ? null
         : _vendorLibs.FirstOrDefault(l => string.Equals(l.Name, library, StringComparison.OrdinalIgnoreCase)).Root;

    private static VendorLibraryBuild? IndexOf(string? root) => root is null ? null : VendorLibraryIndex.Latest(root);

    private PaletteMenuNode VendorLibraryMenu(string name, string? root)
    {
        if (IndexOf(root) is { IsUsable: true } build) return VendorMenuTree.Build(name, build);

        // No tree to offer yet (or ever): the library stays selectable, and its list says why.
        string state = root is null ? "not found" : VendorLibraryIndex.IsBuilding(root) ? "indexing…" : "unavailable";
        return new PaletteMenuNode
        {
            Header   = name,
            Children = [new PaletteMenuNode { Header = $"All in {name}", Entry = PaletteCategoryEntry.ForVendor(name, "", name) },
                        new PaletteMenuNode { Header = $"({state})", IsEnabled = false }],
        };
    }

    /// <summary>
    /// A vendor folder or file selection survives a rebuild that cannot yet confirm it — its library
    /// still referenced but not yet indexed — rather than falling back to All while the index builds.
    /// </summary>
    private PaletteCategoryEntry? VendorSelectionStillOffered(PaletteCategoryEntry? keep)
    {
        if (keep is not { Kind: PaletteCategoryKind.Vendor, VendorLibrary: { } lib }) return null;
        if (!_vendorLibs.Any(l => string.Equals(l.Name, lib, StringComparison.OrdinalIgnoreCase))) return null;
        return IndexOf(RootOf(lib)) is { IsUsable: true } ? null : keep;
    }

    // ── The view ──────────────────────────────────────────────────────────────

    /// <summary>True when the selection is a vendor library or a folder or file inside one: the tile
    /// grid gives way to the part list.</summary>
    public bool IsVendorSelection => SelectedCategory?.Kind == PaletteCategoryKind.Vendor;

    /// <summary>The selected library's folder, or null — what its Help button looks in for documents.</summary>
    public string? SelectedVendorLibraryRoot => IsVendorSelection ? RootOf(SelectedCategory?.VendorLibrary) : null;

    /// <summary>The selected library's matching parts, sorted (R-vl2-2a).</summary>
    public IReadOnlyList<VendorPartRowVm> VendorRows { get; private set; } = [];

    /// <summary>What the part list shows in place of rows — <i>Indexing &lt;name&gt;…</i>, why a library
    /// cannot be shown, or that nothing matches — or null when there are rows.</summary>
    public string? VendorStatus { get; private set; }

    public bool HasVendorStatus => VendorStatus is not null;

    /// <summary>Under All with a query: one row per library with matches (R-vl2-4c).</summary>
    public IReadOnlyList<VendorLibraryMatchVm> LibraryMatches { get; private set; } = [];

    public bool HasLibraryMatches => LibraryMatches.Count > 0;

    private void RebuildVendorView()
    {
        var rows = (IReadOnlyList<VendorPartRowVm>)[];
        string? status = null;
        var matches = (IReadOnlyList<VendorLibraryMatchVm>)[];

        var query = VendorLibrarySearch.Parse(SearchQuery);
        if (SelectedCategory is { Kind: PaletteCategoryKind.Vendor, VendorLibrary: { } lib } sel)
        {
            string? root = RootOf(lib);
            var build = IndexOf(root);
            if (root is null)
                status = $"The vendor library '{lib}' cannot be found. Repair it in File ▸ Manage Libraries….";
            else if (build is null || (VendorLibraryIndex.IsBuilding(root) && !build.IsUsable))
                status = $"Indexing {lib}…";
            else if (build.Refusal is { } refusal)
                status = refusal;
            else
            {
                rows = FilterRows(RowsOf(lib, build), sel.VendorPath, query);
                if (rows.Count == 0) status = "No matching parts.";
            }
        }
        else if (SelectedCategory?.Kind == PaletteCategoryKind.All && !query.IsEmpty)
        {
            // Library parts never enter the All grid — it would hold tens of thousands of tiles. A
            // count per library stands in, and selecting it keeps the query.
            var list = new List<VendorLibraryMatchVm>();
            foreach (var (name, root) in _vendorLibs)
            {
                if (IndexOf(root) is not { IsUsable: true } build) continue;
                int n = 0;
                foreach (var e in build.Entries)
                    if (VendorLibrarySearch.Match(e, query)) n++;
                if (n == 0) continue;
                string libName = name;
                list.Add(new VendorLibraryMatchVm(
                    n == 1 ? $"1 part in {name} matches" : $"{n:N0} parts in {name} match",
                    new RelayCommand(() => SelectVendorLibrary(libName))));
            }
            matches = list;
        }

        VendorRows     = rows;
        VendorStatus   = status;
        LibraryMatches = matches;
        UpdateArmedVendorRow(_svc?.Pending);
        OnPropertyChanged(nameof(VendorRows));
        OnPropertyChanged(nameof(VendorStatus));
        OnPropertyChanged(nameof(HasVendorStatus));
        OnPropertyChanged(nameof(LibraryMatches));
        OnPropertyChanged(nameof(HasLibraryMatches));
    }

    /// <summary>Selects a library's own entry, the search query kept.</summary>
    private void SelectVendorLibrary(string library)
    {
        var entry = Categories.FirstOrDefault(c => c.Kind == PaletteCategoryKind.Vendor &&
                                                   string.Equals(c.VendorLibrary, library, StringComparison.OrdinalIgnoreCase));
        if (entry is not null) SelectedCategory = entry;
    }

    private static List<VendorPartRowVm> FilterRows(VendorPartRowVm[] sorted, string path, VendorLibrarySearch.Query query)
    {
        var list = new List<VendorPartRowVm>();
        string prefix = path + "/";
        foreach (var row in sorted)
        {
            string file = row.Entry.File;
            if (path.Length > 0 && !string.Equals(file, path, StringComparison.Ordinal)
                                && !file.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (!query.IsEmpty && !VendorLibrarySearch.Match(row.Entry, query)) continue;
            list.Add(row);
        }
        return list;
    }

    // ── Rows, cached per library index ────────────────────────────────────────

    private sealed class LibraryRows(VendorLibraryBuild build, VendorPartRowVm[] rows)
    {
        public VendorLibraryBuild Build { get; } = build;
        public VendorPartRowVm[] Rows { get; } = rows;
        public Dictionary<string, VendorPartRowVm> ByPart { get; } =
            rows.GroupBy(r => r.Part, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        public Dictionary<(VendorPartSort, bool), VendorPartRowVm[]> Sorted { get; } = [];
    }

    private readonly Dictionary<string, LibraryRows> _rowCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The library's rows in the current sort — built once per index, sorted once per order.</summary>
    private VendorPartRowVm[] RowsOf(string library, VendorLibraryBuild build)
    {
        if (!_rowCache.TryGetValue(library, out var cache) || !ReferenceEquals(cache.Build, build))
            _rowCache[library] = cache = new LibraryRows(build,
                [.. build.Entries.Select(e => new VendorPartRowVm(library, e, ArmRow))]);

        var key = (VendorSort, VendorSortDescending);
        if (!cache.Sorted.TryGetValue(key, out var sorted))
            cache.Sorted[key] = sorted = Sort(cache.Rows, VendorSort, VendorSortDescending);
        return sorted;
    }

    private static VendorPartRowVm[] Sort(VendorPartRowVm[] rows, VendorPartSort by, bool descending)
    {
        IOrderedEnumerable<VendorPartRowVm> o = by switch
        {
            VendorPartSort.Part        => rows.OrderBy(r => r.Part, StringComparer.OrdinalIgnoreCase),
            VendorPartSort.Description => rows.OrderBy(r => r.Description, StringComparer.OrdinalIgnoreCase)
                                              .ThenBy(r => r.Entry.Header.Value ?? double.MaxValue),
            VendorPartSort.Range       => rows.OrderBy(r => r.Entry.Header.FrequencyHighHz ?? double.MaxValue)
                                              .ThenBy(r => r.Entry.Header.FrequencyLowHz ?? double.MaxValue),
            _                          => rows.OrderBy(r => r.Entry.Header.Value ?? double.MaxValue),
        };
        var sorted = o.ThenBy(r => r.Part, StringComparer.OrdinalIgnoreCase).ToArray();
        if (descending) Array.Reverse(sorted);
        return sorted;
    }

    // ── Sorting ───────────────────────────────────────────────────────────────

    /// <summary>The column the list is sorted by — Value by default (R-vl2-2a).</summary>
    public VendorPartSort VendorSort { get; private set; } = VendorPartSort.Value;

    public bool VendorSortDescending { get; private set; }

    /// <summary>A column header's click: sort by it, or reverse it when it is already the sort.</summary>
    [RelayCommand]
    private void SortVendorParts(string column)
    {
        if (!Enum.TryParse<VendorPartSort>(column, ignoreCase: true, out var by)) return;
        VendorSortDescending = by == VendorSort && !VendorSortDescending;
        VendorSort = by;
        OnPropertyChanged(nameof(PartHeader));
        OnPropertyChanged(nameof(ValueHeader));
        OnPropertyChanged(nameof(DescriptionHeader));
        OnPropertyChanged(nameof(RangeHeader));
        RebuildVendorView();
    }

    public string PartHeader        => Header("Part",        VendorPartSort.Part);
    public string ValueHeader       => Header("Value",       VendorPartSort.Value);
    public string DescriptionHeader => Header("Description", VendorPartSort.Description);
    public string RangeHeader       => Header("Range",       VendorPartSort.Range);

    private string Header(string text, VendorPartSort column)
        => column != VendorSort ? text : text + (VendorSortDescending ? " ▼" : " ▲");

    // ── Arming ────────────────────────────────────────────────────────────────

    private void ArmRow(VendorPartRowVm row)
    {
        if (!row.IsPlaceable) return;   // greyed with its sentence (brief vendorlib-15 D3); placement would refuse
        _svc?.Toggle(row.Ref);
    }

    /// <summary>
    /// ↑/↓ in a library's part list (from the Search box or the list itself): arms the next or
    /// previous row — the first or last when none is armed — exactly as clicking it would, so the
    /// filtered list is walked from the keyboard. Returns the armed row's index, or -1 for no rows.
    /// </summary>
    public int ArmAdjacentVendorRow(int direction)
    {
        var rows = VendorRows;
        if (rows.Count == 0 || direction == 0) return -1;
        int at = -1;
        for (int i = 0; i < rows.Count; i++) if (rows[i].IsArmed) { at = i; break; }
        // A greyed row (no build for this platform) cannot be armed, so the walk steps over it — stopping on one would
        // leave nothing armed and every later key starting again from the same place.
        int step = Math.Sign(direction);
        int next = at < 0 ? (step > 0 ? 0 : rows.Count - 1) : at + step;
        while (next >= 0 && next < rows.Count && !rows[next].IsPlaceable) next += step;
        if (next < 0 || next >= rows.Count) return at;
        if (next != at) ArmRow(rows[next]);
        return next;
    }

    private VendorPartRowVm? _armedRow;

    /// <summary>
    /// Lights the one row whose part is armed, and only that one. A direct look-up by library + part —
    /// never a sweep of every row, and never by kind: every vendor part is a SpiceModel, so a kind
    /// comparison would light the whole list (R-vl2-2b).
    /// </summary>
    private void UpdateArmedVendorRow(PendingPlacement? p)
    {
        VendorPartRowVm? now = null;
        if (p?.Vendor is { } part && _rowCache.TryGetValue(part.Library, out var cache))
            cache.ByPart.TryGetValue(part.Part, out now);

        if (ReferenceEquals(now, _armedRow)) { if (now is not null) now.IsArmed = true; return; }
        if (_armedRow is not null) _armedRow.IsArmed = false;
        _armedRow = now;
        if (now is not null) now.IsArmed = true;
    }
}
