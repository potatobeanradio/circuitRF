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

    internal static PaletteCategoryEntry ForAll()          => new("All",           PaletteCategoryKind.All);
    internal static PaletteCategoryEntry ForCommon()       => new("Common",        PaletteCategoryKind.Common);
    internal static PaletteCategoryEntry ForRecentlyUsed() => new("Recently Used", PaletteCategoryKind.RecentlyUsed);

    internal static PaletteCategoryEntry ForReal(ComponentCategory cat) =>
        new(RealDisplayName(cat), PaletteCategoryKind.Real, cat);

    private static string RealDisplayName(ComponentCategory c) => c switch
    {
        ComponentCategory.TransmissionLine => "Transmission Line",
        ComponentCategory.DataFiles        => "Data Files",
        _                                  => c.ToString()
    };
}

internal enum PaletteCategoryKind { All, Common, RecentlyUsed, Real }

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
            vm.IsArmed = p?.Kind == vm.Item.Kind && p?.PortCount == vm.Item.PortCount;
    }

    // ── Arm command ───────────────────────────────────────────────────────────

    private void ArmItem(PaletteItem item) => _svc?.Toggle(item.Kind, item.PortCount);

    // ── MRU — in-memory empty list for step 3; persistence wired in step 4 ───

    private IReadOnlyList<SymbolKind> _mruList = Array.Empty<SymbolKind>();

    /// <summary>Replace the MRU list (called by WorkspaceViewModel on each placement commit).</summary>
    public void SetMru(IReadOnlyList<SymbolKind> mru)
    {
        _mruList = mru;
        RebuildDisplayedItems();
    }

    // ── Category list (virtual + real, stable order) ─────────────────────────

    /// <summary>Ordered category entries for the header ComboBox.</summary>
    public IReadOnlyList<PaletteCategoryEntry> Categories { get; } = BuildCategories();

    private static IReadOnlyList<PaletteCategoryEntry> BuildCategories()
    {
        var list = new List<PaletteCategoryEntry>
        {
            PaletteCategoryEntry.ForAll(),
            PaletteCategoryEntry.ForCommon(),
            PaletteCategoryEntry.ForRecentlyUsed(),
        };
        foreach (var cat in new[]
        {
            ComponentCategory.Lumped,
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
        return list.AsReadOnly();
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
            .Select(item => new PaletteTileVm(item, ArmItem)
            {
                IsArmed = p?.Kind == item.Kind && p?.PortCount == item.PortCount,
            })
            .ToList();
        OnPropertyChanged(nameof(DisplayedItems));
        OnPropertyChanged(nameof(HasNoItems));
    }

    private IReadOnlyList<PaletteItem> ComputeRawItems()
    {
        if (SelectedCategory is null) return LibraryCatalog.AllItems;

        var q = SearchQuery?.Trim() ?? "";

        if (SelectedCategory.Kind == PaletteCategoryKind.Real)
            return LibraryCatalog.Search(q, SelectedCategory.Real);

        if (string.IsNullOrWhiteSpace(q))
        {
            return SelectedCategory.Kind switch
            {
                PaletteCategoryKind.Common       => LibraryCatalog.Common,
                PaletteCategoryKind.RecentlyUsed => LibraryCatalog.RecentlyUsed(_mruList),
                _                                => LibraryCatalog.AllItems,
            };
        }

        return LibraryCatalog.Search(q);
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
