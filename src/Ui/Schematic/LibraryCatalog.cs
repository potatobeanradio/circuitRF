namespace CircuitRF.Ui.Schematic;

// ── Library Palette catalog ───────────────────────────────────────────────────
// Framework-free, Avalonia-free. The single source the Palette VM binds to.
// The component-type registry is the sole contribution point: adding a SymbolKind
// + registry entry makes the new type appear in AllItems automatically.

/// <summary>
/// One item in the Library Palette — the shape the Palette VM binds to.
/// Bind to this, not to SymbolKind directly, so the anticipated v2 re-key of the
/// registry is a catalog-internal change rather than a Palette rewrite.
/// </summary>
public sealed record PaletteItem(
    SymbolKind Kind,
    int PortCount,
    string DisplayName,
    /// <summary>Primary category — drives sort order in AllItems.</summary>
    ComponentCategory Category,
    IReadOnlyList<string> SearchTerms,
    bool IsCommon,
    /// <summary>
    /// Additional categories this item belongs to. ByCategory uses set-containment over
    /// {Category} ∪ ExtraCategories. AllItems lists each item once, by primary Category.
    /// </summary>
    IReadOnlyList<ComponentCategory>? ExtraCategories = null);

/// <summary>
/// Projects <see cref="ComponentTypeRegistry"/> into an ordered, filterable list of
/// <see cref="PaletteItem"/>s. Framework-free and headless — no Avalonia or Skia types.
/// </summary>
public static class LibraryCatalog
{
    private static readonly Lazy<IReadOnlyList<PaletteItem>> _allItems = new(BuildAllItems);

    /// <summary>
    /// All palette items in stable order: by category rank then display name.
    /// Derived from the registry — a new registry entry appears here automatically.
    /// </summary>
    public static IReadOnlyList<PaletteItem> AllItems => _allItems.Value;

    /// <summary>Virtual category Common: items marked <see cref="PaletteItem.IsCommon"/> in the registry.</summary>
    public static IReadOnlyList<PaletteItem> Common
        => AllItems.Where(i => i.IsCommon).ToList();

    /// <summary>
    /// Real-category filter: returns items that belong to <paramref name="category"/> — either as their
    /// primary <see cref="PaletteItem.Category"/> or in their <see cref="PaletteItem.ExtraCategories"/> set.
    /// An item with two categories therefore appears under both category filters.
    /// </summary>
    public static IReadOnlyList<PaletteItem> ByCategory(ComponentCategory category)
        => AllItems.Where(i => i.Category == category ||
                               (i.ExtraCategories?.Contains(category) ?? false)).ToList();

    /// <summary>
    /// Virtual category Recently Used: returns items ordered by the caller-supplied MRU list,
    /// most-recent first. Kinds not present in the catalog are silently skipped.
    /// The catalog provides the projection; the persistent MRU store is wired separately in the UI.
    /// </summary>
    public static IReadOnlyList<PaletteItem> RecentlyUsed(IReadOnlyList<SymbolKind> mru)
    {
        // A dynamic type (SNP/ZPort/SDD) now has several AllItems entries sharing one Kind — the
        // explicit port-count entry points (§2). The MRU list only ever records the Kind that was
        // placed, so the plain (PortCount == 0) tile is the representative shown in Recently Used.
        var byKind = AllItems
            .GroupBy(i => i.Kind)
            .ToDictionary(g => g.Key, g => g.FirstOrDefault(i => i.PortCount == 0) ?? g.First());
        return mru.Where(byKind.ContainsKey).Select(k => byKind[k]).ToList();
    }

    /// <summary>
    /// Case-insensitive substring search over display name, search terms, and category name.
    /// An empty or whitespace query returns the full source set.
    /// Composes with an optional real category filter: pass null to search All.
    /// </summary>
    public static IReadOnlyList<PaletteItem> Search(string query, ComponentCategory? category = null)
    {
        var source = category.HasValue ? ByCategory(category.Value) : AllItems;
        if (string.IsNullOrWhiteSpace(query)) return source;
        var q = query.Trim().ToUpperInvariant();
        return source.Where(i =>
            i.DisplayName.ToUpperInvariant().Contains(q) ||
            i.SearchTerms.Any(t => t.ToUpperInvariant().Contains(q)) ||
            i.Category.ToString().ToUpperInvariant().Contains(q)
        ).ToList();
    }

    private static IReadOnlyList<PaletteItem> BuildAllItems()
        => Array.AsReadOnly(
            Enum.GetValues<SymbolKind>()
                // SymbolKind.Unknown is a load-time-only sentinel (R-hk-19a) — never a real,
                // user-placeable component, so it must never appear in the palette.
                .Where(kind => kind != SymbolKind.Unknown)
                .Select(kind =>
                {
                    var info = ComponentTypeRegistry.Get(kind);
                    return new PaletteItem(
                        kind,
                        0,
                        ComponentTypeRegistry.DisplayName(kind, 0),
                        info.Category,
                        info.SearchTerms ?? [],
                        info.IsCommon,
                        info.ExtraCategories);
                })
                .Concat(BuildPortCountEntryPoints())
                .OrderBy(i => CategorySortKey(i.Category))
                .ThenBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray());

    /// <summary>
    /// Explicit S1P/S2P/S3P/S4P, Z1P/Z2P/Z3P, SDD1/SDD2/SDD3 palette entries
    /// (brief-housekeeping-tearoff-palette-repo.md §2; S4P added on the owner's explicit follow-up
    /// request). Each is the SAME dynamic type (<see cref="SymbolKind.Snp"/>/<see cref="SymbolKind.ZPort"/>/
    /// <see cref="SymbolKind.Sdd"/>) as the plain generic tile — an entry point that presets
    /// <see cref="PaletteItem.PortCount"/> and nothing else, never a parallel component. The
    /// on-schematic label already tracks the PLACED instance's actual port count via
    /// <see cref="ComponentTypeRegistry.DisplayName(SymbolKind,int)"/> regardless of how it was
    /// placed, so these entries add discoverability only. IsCommon is inherited from the dynamic
    /// type's own registry entry (not hardcoded) so every AllItems row stays consistent with
    /// <see cref="ComponentTypeRegistry.Get"/> for its Kind.
    /// </summary>
    private static IEnumerable<PaletteItem> BuildPortCountEntryPoints()
    {
        var sndInfo = ComponentTypeRegistry.Get(SymbolKind.Snp);
        var zInfo   = ComponentTypeRegistry.Get(SymbolKind.ZPort);
        var sddInfo = ComponentTypeRegistry.Get(SymbolKind.Sdd);

        for (int n = 1; n <= 4; n++)
        {
            yield return new PaletteItem(
                SymbolKind.Snp, n, ComponentTypeRegistry.DisplayName(SymbolKind.Snp, n),
                sndInfo.Category, sndInfo.SearchTerms ?? [], sndInfo.IsCommon, sndInfo.ExtraCategories);
        }

        for (int n = 1; n <= 3; n++)
        {
            // R-hk-4: Z1P alone gets a Terminals filter keyword — only Z1P, not Z2P/Z3P.
            var zExtra = n == 1
                ? (zInfo.ExtraCategories ?? []).Concat([ComponentCategory.Terminals]).Distinct().ToArray()
                : zInfo.ExtraCategories;
            yield return new PaletteItem(
                SymbolKind.ZPort, n, ComponentTypeRegistry.DisplayName(SymbolKind.ZPort, n),
                zInfo.Category, zInfo.SearchTerms ?? [], zInfo.IsCommon, zExtra);

            yield return new PaletteItem(
                SymbolKind.Sdd, n, ComponentTypeRegistry.DisplayName(SymbolKind.Sdd, n),
                sddInfo.Category, sddInfo.SearchTerms ?? [], sddInfo.IsCommon, sddInfo.ExtraCategories);
        }
    }

    private static int CategorySortKey(ComponentCategory c) => c switch
    {
        ComponentCategory.Lumped           => 0,
        ComponentCategory.Sources          => 1,
        ComponentCategory.Terminals        => 2,
        ComponentCategory.TransmissionLine => 3,
        ComponentCategory.Microstrip       => 4,
        ComponentCategory.DataFiles        => 5,
        _                                  => 6,
    };
}
