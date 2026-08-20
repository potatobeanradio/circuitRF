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
    IReadOnlyList<ComponentCategory>? ExtraCategories = null,
    /// <summary>
    /// Non-null when this entry came from an imported kit rather than the built-in library.
    /// Built-in entries leave it null and are completely unaffected by it.
    /// </summary>
    PdkPartRef? Pdk = null,
    /// <summary>
    /// Non-null when this tile places a PARAMETRIC CELL by generator id rather than a component by
    /// <see cref="SymbolKind"/>. Every cell a kit contributes is discovered at run time and has no
    /// enum member of its own, so the id is what identifies it — see
    /// <c>LayoutEditorViewModel.PlacePCell</c>.
    /// </summary>
    string? PCellGeneratorId = null);

/// <summary>
/// Identifies one part contributed by an imported kit, and points at whatever artwork that kit
/// shipped for it.
///
/// <para>The two artwork paths are deliberately separate because they are used for different
/// things: <see cref="IconPath"/> is the kit's own small raster browser icon, which is exactly what
/// a palette tile wants, while <see cref="CellDir"/> points at a cell whose symbol was built from
/// the kit's vector symbol description — the right thing to draw on a schematic, where it has to
/// scale, carry pins, and follow the colour theme. Using each for what it was drawn for beats
/// stretching one to cover both.</para>
/// </summary>
/// <param name="KitName">Display name of the kit this part came from; also its palette category.</param>
/// <param name="PartId">Identifier, unique within the kit.</param>
/// <param name="IconPath">Absolute path to the kit's palette icon, when it shipped one.</param>
/// <param name="CellDir">
/// Where the part's symbol comes from: an absolute cell folder, or a virtual reference for a part
/// held in memory. Never split this by hand — ask <c>CellSymbolResolver.ResolveCellDirOrRef</c>,
/// which is the one place that knows which of the two forms it is looking at.
/// </param>
/// <param name="Category">
/// The kit's OWN word for what kind of part this is, or empty when it states none.
///
/// <para>Kept verbatim rather than mapped onto a <see cref="ComponentCategory"/>: a kit groups its
/// parts the way its own documentation does, and translating that into circuitRF's built-in
/// vocabulary would be guessing at a grouping the kit already stated. It is what the palette offers
/// as a sub-category under the kit — the difference between browsing a hundred parts and browsing
/// the eight that are diodes.</para>
/// </param>
/// <param name="ModelName">
/// The device model this part declares, verbatim from the kit, or empty when it declares none.
///
/// <para>Carried because it is the ONE identity a kit's schematic part and its layout cell reliably
/// share. A kit routinely names the two differently — its schematic symbol is one file, its
/// parametric layout cell is a class in a package, and neither is obliged to match the other — but
/// both name the model, because that is what a netlist has to say. Matching on it is reading the
/// kit's own statement; matching by chopping prefixes off a name would be guessing, and a guess here
/// puts the wrong artwork on the design while looking perfectly correct.</para>
/// </param>
/// <param name="ParameterNames">
/// The parameters this part declares, as the kit names them — circuitRF's own rows excluded, since a
/// kit has never heard of those.
///
/// <para>Carried for the case <paramref name="ModelName"/> alone cannot settle: several parts and
/// several layout cells all naming ONE model. That is the ordinary shape of a device offered in an
/// RF and a non-RF form, and the kit does state which is which — in what each accepts. A cell that
/// does not declare a parameter the part has cannot be that part's layout view, because the artwork
/// could never follow the schematic. See <see cref="KitPaletteMerge"/>.</para>
/// </param>
public sealed record PdkPartRef(
    string  KitName,
    string  PartId,
    string? IconPath  = null,
    string? CellDir   = null,
    string  Category  = "",
    string  ModelName = "",
    IReadOnlyList<string>? ParameterNames = null);

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

    /// <summary>
    /// The Library Palette's "All" filter order (owner request, 2026-08-16): a curated pin list of
    /// the components scanned most, in the exact sequence requested, followed by every remaining
    /// built-in in the ordinary category-then-name order (<see cref="AllItems"/>'s own order, with
    /// the pinned rows removed). Identified by (Kind, PortCount) rather than DisplayName, because a
    /// dynamic type (Snp/ZPort/Sdd) has several <see cref="AllItems"/> rows sharing one Kind — the
    /// explicit port-count entry points (S2P/S3P, Z1P here).
    /// </summary>
    private static readonly (SymbolKind Kind, int PortCount)[] AllFilterPinnedOrder =
    [
        (SymbolKind.Resistor,      0),
        (SymbolKind.Ground,    0),
        (SymbolKind.Inductor,    0),
        (SymbolKind.Mutual,      0),
        (SymbolKind.Capacitor,   0),
        (SymbolKind.NonlinearC,  0),
        (SymbolKind.Term,        0),
        (SymbolKind.TermG,       0),
        (SymbolKind.Var,         0),
        (SymbolKind.Meas,        0),
        (SymbolKind.Vdc,         0),
        (SymbolKind.IProbe,      0),
        (SymbolKind.P1Tone,      0),
        (SymbolKind.ToneSource,  0),   // "V1Tone" — the single-tone voltage source, displayed as VTone.
        (SymbolKind.Snp,         2),   // S2P
        (SymbolKind.Snp,         3),   // S3P
        (SymbolKind.Tline,       0),   // TLIN
        (SymbolKind.Mlin,        0),   // MLIN
        (SymbolKind.SourceTuner, 0),
        (SymbolKind.LoadTuner,   0),
        (SymbolKind.ZPort,       1),   // Z1P
        (SymbolKind.WBond,       0),
        (SymbolKind.Match,       0),
    ];

    /// <summary>Built-ins in <see cref="AllFilterPinnedOrder"/> order, then everything else in the
    /// ordinary <see cref="AllItems"/> order. Never throws on a pinned row that does not (or no
    /// longer) resolve — it is just skipped, so a future SymbolKind rename degrades gracefully rather
    /// than crashing the palette.</summary>
    public static IReadOnlyList<PaletteItem> AllItemsPinnedOrder()
    {
        var byKey = AllItems.ToDictionary(i => (i.Kind, i.PortCount));
        var pinned = new List<PaletteItem>(AllFilterPinnedOrder.Length);
        foreach (var key in AllFilterPinnedOrder)
            if (byKey.TryGetValue(key, out var item))
                pinned.Add(item);

        var pinnedSet = new HashSet<PaletteItem>(pinned);
        return [.. pinned, .. AllItems.Where(i => !pinnedSet.Contains(i))];
    }

    /// <summary>Built-ins sorted purely alphabetically by <see cref="PaletteItem.DisplayName"/>, with
    /// no category grouping — the built-in half of the "All - Alphabetical" filter.</summary>
    public static IReadOnlyList<PaletteItem> AllItemsAlphabetical()
        => AllItems.OrderBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

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

    /// <summary>
    /// Kinds that exist for internal machinery only and must never be user-selectable in the
    /// palette (owner report, 2026-07-29 — "X" and "Unknown" showing up in the parts list with no
    /// obvious purpose). Both stay fully functional under the hood:
    /// <see cref="SymbolKind.Generic"/> ("X") is the placeholder base kind a placed CELL-REFERENCE
    /// instance carries (its real glyph comes from the resolved cell, not this kind's own — see
    /// <c>SchematicViewModel.CommitCellPlacementAsync</c>); <see cref="SymbolKind.Unknown"/> is the
    /// load-time-only sentinel for an unrecognized `.csch` component type (R-hk-19a). Neither is
    /// ever something a user picks from the palette to place fresh.
    /// </summary>
    private static readonly HashSet<SymbolKind> InternalOnlyKinds = [SymbolKind.Generic, SymbolKind.Unknown];

    private static IReadOnlyList<PaletteItem> BuildAllItems()
        => Array.AsReadOnly(
            Enum.GetValues<SymbolKind>()
                .Where(kind => !InternalOnlyKinds.Contains(kind))
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

            // Owner request 2026-08-02, following R-hk-4's precedent exactly: SDD1 and SDD2 — and
            // only those two — also list under Devices. An SDD carrying device equations is how a
            // user hand-builds a 1- or 2-port nonlinear device, so it belongs beside the built-in
            // diode and FETs. SDD3 and the plain SDD tile are unchanged, and this adds a FILTER
            // keyword only: same kind, same glyph, same engine component, still one AllItems row.
            var sddExtra = n <= 2
                ? (sddInfo.ExtraCategories ?? []).Concat([ComponentCategory.Devices]).Distinct().ToArray()
                : sddInfo.ExtraCategories;

            yield return new PaletteItem(
                SymbolKind.Sdd, n, ComponentTypeRegistry.DisplayName(SymbolKind.Sdd, n),
                sddInfo.Category, sddInfo.SearchTerms ?? [], sddInfo.IsCommon, sddExtra);
        }
    }

    private static int CategorySortKey(ComponentCategory c) => c switch
    {
        ComponentCategory.Lumped           => 0,
        ComponentCategory.Devices          => 1,
        ComponentCategory.Sources          => 2,
        ComponentCategory.Terminals        => 3,
        ComponentCategory.TransmissionLine => 4,
        ComponentCategory.Microstrip       => 5,
        ComponentCategory.Matching         => 6,
        ComponentCategory.DataFiles        => 7,
        _                                  => 8,
    };
}
