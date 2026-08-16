using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.ViewModels.Dock;

// ── Per-tile ViewModel ────────────────────────────────────────────────────────

/// <summary>
/// Observable wrapper around a <see cref="PaletteItem"/> for one tile in the Library Palette.
/// <see cref="IsArmed"/> is updated by <see cref="PaletteTool"/> when the armed state changes.
/// <see cref="ArmCommand"/> toggles the armed state via <see cref="PlacementService"/>.
/// </summary>
public sealed partial class PaletteTileVm : ObservableObject
{
    public PaletteItem Item { get; }

    [ObservableProperty]
    private bool _isArmed;

    public ICommand ArmCommand { get; }

    /// <summary>
    /// Second tooltip line. For a kit part this is the KIT it came from — the useful fact, and the
    /// one the user is actually asking when they hover. Its <see cref="ComponentCategory"/> is the
    /// catch-all bucket every kit part shares and says nothing.
    /// </summary>
    public string CategoryLabel => Item.Pdk?.KitName ?? Item.Category.ToString();

    internal PaletteTileVm(PaletteItem item, Action<PaletteItem> arm)
    {
        Item       = item;
        ArmCommand = new RelayCommand(() => arm(item));
    }
}

// ── Category selector entry ───────────────────────────────────────────────────

/// <summary>
/// Category selector entry for the Library Palette header ComboBox.
/// Covers virtual (All / Common / Recently Used) and real <see cref="ComponentCategory"/> values.
/// </summary>
public sealed class PaletteCategoryEntry
{
    public string DisplayName { get; }
    internal PaletteCategoryKind Kind { get; }
    internal ComponentCategory? Real { get; }

    private PaletteCategoryEntry(string name, PaletteCategoryKind kind, ComponentCategory? real = null)
    {
        DisplayName = name;
        Kind        = kind;
        Real        = real;
    }

    /// <summary>Kit name this entry filters to; null for every non-kit entry.</summary>
    internal string? KitName { get; private init; }

    /// <summary>
    /// The kit's own sub-category this entry narrows to, or null for the kit's own entry — which
    /// shows everything the kit offers.
    /// </summary>
    internal string? KitCategory { get; private init; }

    internal static PaletteCategoryEntry ForAll()             => new("All",               PaletteCategoryKind.All);
    internal static PaletteCategoryEntry ForAllAlphabetical() => new("All - Alphabetical", PaletteCategoryKind.AllAlphabetical);
    internal static PaletteCategoryEntry ForCommon()          => new("Common",             PaletteCategoryKind.Common);
    internal static PaletteCategoryEntry ForRecentlyUsed()    => new("Recently Used",       PaletteCategoryKind.RecentlyUsed);

    internal static PaletteCategoryEntry ForReal(ComponentCategory cat) =>
        new(RealDisplayName(cat), PaletteCategoryKind.Real, cat);

    /// <summary>One imported kit, listed under its own name. Shows every part the kit offers.</summary>
    internal static PaletteCategoryEntry ForKit(string kitName) =>
        new(kitName, PaletteCategoryKind.Kit) { KitName = kitName };

    /// <summary>
    /// One of a kit's OWN groupings, listed directly beneath that kit and indented so the nesting
    /// reads at a glance — a flat combo cannot show a tree, and a kit with a hundred parts is
    /// unbrowsable without one.
    /// </summary>
    internal static PaletteCategoryEntry ForKitCategory(string kitName, string category) =>
        new(SubEntryIndent + category, PaletteCategoryKind.Kit)
        { KitName = kitName, KitCategory = category };

    /// <summary>Leading spaces that make a sub-entry read as nested under the kit above it.</summary>
    private const string SubEntryIndent = "    ";

    private static string RealDisplayName(ComponentCategory c) => c switch
    {
        ComponentCategory.TransmissionLine => "Transmission Line",
        ComponentCategory.DataFiles        => "Data Files",
        _                                  => c.ToString()
    };
}

internal enum PaletteCategoryKind { All, AllAlphabetical, Common, RecentlyUsed, Real, Kit }

// ── PaletteTool ───────────────────────────────────────────────────────────────

/// <summary>
/// Dock Tool hosting the Library Palette.
/// Carries category-filter and search-query state; exposes <see cref="DisplayedItems"/> computed
/// via <see cref="LibraryCatalog"/>.  Placement arming lives here (steps 4+).
/// </summary>
public sealed partial class PaletteTool : Tool
{
    // ── Placement service (injected by WorkspaceViewModel) ────────────────────

    private PlacementService? _svc;

    /// <summary>Inject the app-level placement service. Re-call after layout reset.</summary>
    public void SetPlacementService(PlacementService svc)
    {
        if (_svc is not null) _svc.PropertyChanged -= OnSvcPropertyChanged;
        _svc = svc;
        _svc.PropertyChanged += OnSvcPropertyChanged;
        UpdateArmedState();
    }

    private void OnSvcPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlacementService.Pending))
            UpdateArmedState();
    }

    private void UpdateArmedState()
    {
        var p = _svc?.Pending;
        foreach (var vm in _currentItems)
            vm.IsArmed = ArmedFor(p, vm.Item);
    }

    /// <summary>
    /// Whether <paramref name="p"/> is this exact entry. A kit part is identified by its kit+part
    /// id, never by SymbolKind — every kit part shares one kind, so comparing kinds would light up
    /// every kit tile at once.
    /// </summary>
    private static bool ArmedFor(PendingPlacement? p, PaletteItem item)
    {
        if (p is null) return false;

        if (item.Pdk is { } want)
            return p.Pdk is { } have &&
                   string.Equals(have.KitName, want.KitName, StringComparison.Ordinal) &&
                   string.Equals(have.PartId,  want.PartId,  StringComparison.Ordinal);

        return p.Pdk is null && p.Kind == item.Kind && p.PortCount == item.PortCount;
    }

    // ── Arm command ───────────────────────────────────────────────────────────

    private void ArmItem(PaletteItem item) => _svc?.Toggle(item);

    // ── MRU — in-memory empty list for step 3; persistence wired in step 4 ───

    private IReadOnlyList<SymbolKind> _mruList = Array.Empty<SymbolKind>();

    /// <summary>Replace the MRU list (called by WorkspaceViewModel on each placement commit).</summary>
    public void SetMru(IReadOnlyList<SymbolKind> mru)
    {
        _mruList = mru;
        RebuildDisplayedItems();
    }

    // ── Category list (virtual + real, stable order) ─────────────────────────

    /// <summary>
    /// Ordered category entries for the header ComboBox: the virtual entries, then the built-in
    /// categories, then one entry per imported kit. Rebuilt when the imported-kit set changes.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<PaletteCategoryEntry> _categories = BuildCategories([]);

    private static IReadOnlyList<PaletteCategoryEntry> BuildCategories(IReadOnlyList<PaletteItem> pdkItems)
    {
        var list = new List<PaletteCategoryEntry>
        {
            PaletteCategoryEntry.ForAll(),
            PaletteCategoryEntry.ForAllAlphabetical(),
            PaletteCategoryEntry.ForCommon(),
            PaletteCategoryEntry.ForRecentlyUsed(),
        };
        foreach (var cat in new[]
        {
            ComponentCategory.Lumped,
            ComponentCategory.Devices,
            ComponentCategory.Nonlinear,
            ComponentCategory.Sources,
            ComponentCategory.Terminals,
            ComponentCategory.TransmissionLine,
            ComponentCategory.Microstrip,
            ComponentCategory.DataFiles,
        })
        {
            if (LibraryCatalog.ByCategory(cat).Count > 0)
                list.Add(PaletteCategoryEntry.ForReal(cat));
        }

        foreach (var kit in pdkItems
                     .Select(i => i.Pdk?.KitName)
                     .OfType<string>()
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(PaletteCategoryEntry.ForKit(kit));

            // The kit's own groupings, immediately beneath it. Only where the kit states more than
            // one: a single sub-category would just repeat the kit entry above it in different
            // words, and every part of that kit is already reachable from the kit itself.
            var subs = pdkItems
                .Where(i => string.Equals(i.Pdk?.KitName, kit, StringComparison.Ordinal))
                .Select(i => i.Pdk!.Category)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (subs.Count > 1)
                foreach (var sub in subs)
                    list.Add(PaletteCategoryEntry.ForKitCategory(kit, sub));
        }

        return list.AsReadOnly();
    }

    // ── Imported kits ─────────────────────────────────────────────────────────

    private IReadOnlyList<PaletteItem> _pdkItems = [];

    /// <summary>
    /// Replace the set of parts contributed by imported kits. Each kit gains its own category, and
    /// its parts also appear under All and in search results alongside the built-ins.
    ///
    /// <para>The current category selection survives when it still exists after the rebuild — so
    /// re-importing a kit while browsing it does not throw the user back to All.</para>
    /// </summary>
    public void SetPdkParts(IReadOnlyList<PaletteItem> items)
    {
        _pdkItems = items ?? [];

        string? keepKit  = SelectedCategory?.KitName;
        string? keepSub  = SelectedCategory?.KitCategory;
        var     keepKind = SelectedCategory?.Kind;
        var     keepReal = SelectedCategory?.Real;

        Categories = BuildCategories(_pdkItems);

        var restored = Categories.FirstOrDefault(c =>
            c.Kind == keepKind &&
            c.Real == keepReal &&
            string.Equals(c.KitName, keepKit, StringComparison.Ordinal) &&
            string.Equals(c.KitCategory, keepSub, StringComparison.Ordinal));

        // Assigning SelectedCategory rebuilds the tiles via its partial callback; when the selection
        // is unchanged that callback does not fire, so rebuild explicitly in that case.
        if (restored is not null && !ReferenceEquals(restored, SelectedCategory))
            SelectedCategory = restored;
        else if (restored is null)
            SelectedCategory = Categories[0];
        else
            RebuildDisplayedItems();
    }

    // ── Filter state ──────────────────────────────────────────────────────────

    [ObservableProperty]
    private PaletteCategoryEntry _selectedCategory = null!;   // set in ctor

    [ObservableProperty]
    private string _searchQuery = "";

    /// <summary>True when the search TextBox has text — drives the clear-button visibility.</summary>
    public bool HasSearchQuery => !string.IsNullOrEmpty(SearchQuery);

    // ── Displayed items (PaletteTileVm, with IsArmed maintained live) ─────────

    private List<PaletteTileVm> _currentItems = [];

    /// <summary>
    /// Filtered + searched tile ViewModels the tile grid binds to.
    /// Rebuilt when <see cref="SelectedCategory"/> or <see cref="SearchQuery"/> changes.
    /// Each tile's <see cref="PaletteTileVm.IsArmed"/> is updated live as the armed state changes.
    /// </summary>
    public IReadOnlyList<PaletteTileVm> DisplayedItems => _currentItems;

    /// <summary>True when <see cref="DisplayedItems"/> is empty — drives empty-result text.</summary>
    public bool HasNoItems => _currentItems.Count == 0;

    private void RebuildDisplayedItems()
    {
        var raw = ComputeRawItems();
        var p   = _svc?.Pending;
        _currentItems = raw
            .Select(item => new PaletteTileVm(item, ArmItem) { IsArmed = ArmedFor(p, item) })
            .ToList();
        OnPropertyChanged(nameof(DisplayedItems));
        OnPropertyChanged(nameof(HasNoItems));
    }

    private IReadOnlyList<PaletteItem> ComputeRawItems()
    {
        if (SelectedCategory is null) return WithPdk(LibraryCatalog.AllItemsPinnedOrder());

        var q = SearchQuery?.Trim() ?? "";

        // A kit category shows that kit's parts only — never the built-ins. A sub-category narrows
        // further to the kit's own grouping; the kit's own entry carries none and shows all of them.
        if (SelectedCategory.Kind == PaletteCategoryKind.Kit)
        {
            var mine = _pdkItems
                .Where(i => string.Equals(i.Pdk?.KitName, SelectedCategory.KitName, StringComparison.Ordinal))
                .Where(i => SelectedCategory.KitCategory is not { } sub ||
                            string.Equals(i.Pdk!.Category, sub, StringComparison.OrdinalIgnoreCase))
                .ToList();
            return string.IsNullOrWhiteSpace(q) ? mine : FilterBySearch(mine, q);
        }

        if (SelectedCategory.Kind == PaletteCategoryKind.Real)
            return LibraryCatalog.Search(q, SelectedCategory.Real);

        if (string.IsNullOrWhiteSpace(q))
        {
            return SelectedCategory.Kind switch
            {
                // Common and Recently Used stay built-in-only; both are curated over the built-in
                // library, and neither has a defined meaning for a kit part yet.
                PaletteCategoryKind.Common          => LibraryCatalog.Common,
                PaletteCategoryKind.RecentlyUsed    => LibraryCatalog.RecentlyUsed(_mruList),
                PaletteCategoryKind.AllAlphabetical => WithPdkAlphabeticalByKit(LibraryCatalog.AllItemsAlphabetical()),
                _                                   => WithPdk(LibraryCatalog.AllItemsPinnedOrder()),
            };
        }

        return [.. LibraryCatalog.Search(q), .. FilterBySearch(_pdkItems, q)];
    }

    private IReadOnlyList<PaletteItem> WithPdk(IReadOnlyList<PaletteItem> builtIns) =>
        _pdkItems.Count == 0 ? builtIns : [.. builtIns, .. _pdkItems];

    /// <summary>
    /// "All - Alphabetical"'s PDK half: kit parts grouped by kit (kits themselves in alphabetical
    /// order, matching <see cref="BuildCategories"/>'s own kit ordering) and sorted alphabetically by
    /// <see cref="PaletteItem.DisplayName"/> WITHIN each kit — never interleaved across kits, per the
    /// owner's own spec (2026-08-16).
    /// </summary>
    private IReadOnlyList<PaletteItem> WithPdkAlphabeticalByKit(IReadOnlyList<PaletteItem> builtIns)
    {
        if (_pdkItems.Count == 0) return builtIns;

        var pdkOrdered = _pdkItems
            .GroupBy(i => i.Pdk?.KitName ?? "", StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .SelectMany(g => g.OrderBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase));

        return [.. builtIns, .. pdkOrdered];
    }

    /// <summary>Same case-insensitive substring rule <see cref="LibraryCatalog.Search"/> applies.</summary>
    private static IReadOnlyList<PaletteItem> FilterBySearch(IReadOnlyList<PaletteItem> items, string query)
    {
        var q = query.Trim().ToUpperInvariant();
        return items.Where(i =>
            i.DisplayName.ToUpperInvariant().Contains(q) ||
            i.SearchTerms.Any(t => t.ToUpperInvariant().Contains(q)) ||
            (i.Pdk?.KitName.ToUpperInvariant().Contains(q) ?? false)).ToList();
    }

    // ── Partial callbacks — recompute derived properties on state change ───────

    partial void OnSelectedCategoryChanged(PaletteCategoryEntry value) => RebuildDisplayedItems();

    partial void OnSearchQueryChanged(string value)
    {
        RebuildDisplayedItems();
        OnPropertyChanged(nameof(HasSearchQuery));
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private void ClearSearch() => SearchQuery = "";

    // ── Construction ──────────────────────────────────────────────────────────

    public PaletteTool()
    {
        Id    = "Palette";
        Title = "Library";
        SelectedCategory = Categories[0];   // triggers RebuildDisplayedItems via partial callback
    }
}
