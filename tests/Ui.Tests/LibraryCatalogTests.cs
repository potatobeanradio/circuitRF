using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests;

// ── LAYER 1 — registry palette metadata ──────────────────────────────────────
// Verify the registry carries category/search/Common on every built-in and that
// existing behavior (DisplayName, EngineReference, DefaultParameters) is unchanged.

public class RegistryPaletteMetadataTests
{
    [Fact]
    public void AllBuiltIns_HaveSearchTerms()
    {
        foreach (SymbolKind kind in Enum.GetValues<SymbolKind>())
        {
            var info = ComponentTypeRegistry.Get(kind);
            Assert.NotNull(info.SearchTerms);
            Assert.NotEmpty(info.SearchTerms);
        }
    }

    [Theory]
    [InlineData(SymbolKind.Resistor,      ComponentCategory.Lumped)]
    [InlineData(SymbolKind.Inductor,      ComponentCategory.Lumped)]
    [InlineData(SymbolKind.Capacitor,     ComponentCategory.Lumped)]
    [InlineData(SymbolKind.Vdc, ComponentCategory.Sources)]
    [InlineData(SymbolKind.ToneSource,    ComponentCategory.Sources)]
    [InlineData(SymbolKind.Ground,        ComponentCategory.Terminals)]
    [InlineData(SymbolKind.Term,          ComponentCategory.Terminals)]
    [InlineData(SymbolKind.Sdd,           ComponentCategory.Other)]
    [InlineData(SymbolKind.ZPort,         ComponentCategory.Other)]
    [InlineData(SymbolKind.Generic,       ComponentCategory.Other)]
    public void Registry_Category_CorrectForEachKind(SymbolKind kind, ComponentCategory expected)
    {
        Assert.Equal(expected, ComponentTypeRegistry.Get(kind).Category);
    }

    [Theory]
    [InlineData(SymbolKind.Resistor,      true)]
    [InlineData(SymbolKind.Inductor,      true)]
    [InlineData(SymbolKind.Capacitor,     true)]
    [InlineData(SymbolKind.Vdc, true)]
    [InlineData(SymbolKind.ToneSource,    true)]
    [InlineData(SymbolKind.Ground,        true)]
    [InlineData(SymbolKind.Term,          true)]
    [InlineData(SymbolKind.Sdd,           false)]
    [InlineData(SymbolKind.ZPort,         false)]
    [InlineData(SymbolKind.Generic,       false)]
    public void Registry_IsCommon_CorrectForEachKind(SymbolKind kind, bool expected)
    {
        Assert.Equal(expected, ComponentTypeRegistry.Get(kind).IsCommon);
    }

    [Fact]
    public void Registry_SearchTerms_CapAlias_OnCapacitor()
    {
        var terms = ComponentTypeRegistry.Get(SymbolKind.Capacitor).SearchTerms!;
        Assert.Contains("cap", terms, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Registry_SearchTerms_ResAlias_OnResistor()
    {
        var terms = ComponentTypeRegistry.Get(SymbolKind.Resistor).SearchTerms!;
        Assert.Contains("res", terms, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Registry_SearchTerms_IndAlias_OnInductor()
    {
        var terms = ComponentTypeRegistry.Get(SymbolKind.Inductor).SearchTerms!;
        Assert.Contains("ind", terms, StringComparer.OrdinalIgnoreCase);
    }

    // Confirm existing behavior is unchanged by the additive metadata.
    [Fact]
    public void Registry_ExistingBehavior_DisplayName_Unchanged()
    {
        Assert.Equal("R",    ComponentTypeRegistry.DisplayName(SymbolKind.Resistor));
        Assert.Equal("C",    ComponentTypeRegistry.DisplayName(SymbolKind.Capacitor));
        Assert.Equal("VTone",ComponentTypeRegistry.DisplayName(SymbolKind.ToneSource));
        Assert.Equal("GND",  ComponentTypeRegistry.DisplayName(SymbolKind.Ground));
        Assert.Equal("Z2P",  ComponentTypeRegistry.DisplayName(SymbolKind.ZPort, 2));
        Assert.Equal("SDD3", ComponentTypeRegistry.DisplayName(SymbolKind.Sdd,   3));
    }

    [Fact]
    public void Registry_ExistingBehavior_EngineReference_Unchanged()
    {
        Assert.Equal("V_1Tone", ComponentTypeRegistry.EngineReference(SymbolKind.ToneSource));
        Assert.Equal("Z_Port",  ComponentTypeRegistry.EngineReference(SymbolKind.ZPort));
        Assert.Equal("SDD",     ComponentTypeRegistry.EngineReference(SymbolKind.Sdd));
    }

    [Fact]
    public void Registry_ExistingBehavior_Ground_LabelsHiddenByDefault()
    {
        var info = ComponentTypeRegistry.Get(SymbolKind.Ground);
        Assert.False(info.DefaultShowTypeLabel);
        Assert.False(info.DefaultShowInstanceName);
    }

    [Fact]
    public void Registry_Fallback_Unknown_CategoryIsOther()
    {
        // The fallback for an unknown kind must default to Other/null/false.
        // Cast a value outside the defined enum to exercise the fallback path.
        var info = ComponentTypeRegistry.Get((SymbolKind)999);
        Assert.Equal(ComponentCategory.Other, info.Category);
        Assert.False(info.IsCommon);
    }
}

// ── LAYER 2 — LibraryCatalog projection + filter/search ──────────────────────

public class LibraryCatalogTests
{
    // ── AllItems ──────────────────────────────────────────────────────────────

    [Fact]
    public void AllItems_ContainsExactlyAllSymbolKinds()
    {
        // Demonstrates the contribution point: AllItems is derived from SymbolKind
        // enum + registry — adding an entry makes it appear automatically.
        // Generic ("X") and Unknown are the deliberate exceptions (owner report, 2026-07-29 /
        // R-hk-19a): both are internal-machinery-only kinds (Generic is the placeholder base kind
        // for a placed cell-reference instance; Unknown is the load-time-only sentinel for an
        // unrecognized `.csch` component type) — neither is ever something a user picks from the
        // palette to place fresh, so both are excluded from it.
        var expectedKinds = Enum.GetValues<SymbolKind>().ToHashSet();
        expectedKinds.Remove(SymbolKind.Unknown);
        expectedKinds.Remove(SymbolKind.Generic);
        var actualKinds   = LibraryCatalog.AllItems.Select(i => i.Kind).ToHashSet();
        Assert.Equal(expectedKinds, actualKinds);
    }

    [Fact]
    public void AllItems_NeverContainsUnknownOrGeneric()
    {
        Assert.DoesNotContain(LibraryCatalog.AllItems, i => i.Kind == SymbolKind.Unknown);
        Assert.DoesNotContain(LibraryCatalog.AllItems, i => i.Kind == SymbolKind.Generic);
    }

    [Fact]
    public void AllItems_IsDeterministic()
    {
        var first  = LibraryCatalog.AllItems.Select(i => i.Kind).ToList();
        var second = LibraryCatalog.AllItems.Select(i => i.Kind).ToList();
        Assert.Equal(first, second);
    }

    [Fact]
    public void AllItems_OrderedByCategoryThenDisplayName()
    {
        var items = LibraryCatalog.AllItems;
        for (int i = 1; i < items.Count; i++)
        {
            var prev = items[i - 1];
            var curr = items[i];
            if (prev.Category == curr.Category)
                Assert.True(
                    string.Compare(prev.DisplayName, curr.DisplayName, StringComparison.OrdinalIgnoreCase) <= 0,
                    $"Within {curr.Category}: '{prev.DisplayName}' should sort before '{curr.DisplayName}'");
        }
    }

    [Fact]
    public void AllItems_LumpedPrecedesSources()
    {
        var lumpedIdx  = LibraryCatalog.AllItems.Select((i, n) => (i, n)).First(x => x.i.Category == ComponentCategory.Lumped).n;
        var sourcesIdx = LibraryCatalog.AllItems.Select((i, n) => (i, n)).First(x => x.i.Category == ComponentCategory.Sources).n;
        Assert.True(lumpedIdx < sourcesIdx);
    }

    // ── Category correctness ──────────────────────────────────────────────────

    [Fact]
    public void AllItems_RLC_InLumped()
    {
        var lumped = LibraryCatalog.AllItems
            .Where(i => i.Category == ComponentCategory.Lumped)
            .Select(i => i.Kind)
            .ToHashSet();
        Assert.Contains(SymbolKind.Resistor,  lumped);
        Assert.Contains(SymbolKind.Inductor,  lumped);
        Assert.Contains(SymbolKind.Capacitor, lumped);
    }

    [Fact]
    public void AllItems_VAndVTone_InSources()
    {
        var sources = LibraryCatalog.AllItems
            .Where(i => i.Category == ComponentCategory.Sources)
            .Select(i => i.Kind)
            .ToHashSet();
        Assert.Contains(SymbolKind.Vdc,        sources);
        Assert.Contains(SymbolKind.ToneSource, sources);
    }

    [Fact]
    public void AllItems_GroundAndPort_InTerminals()
    {
        var terminals = LibraryCatalog.AllItems
            .Where(i => i.Category == ComponentCategory.Terminals)
            .Select(i => i.Kind)
            .ToHashSet();
        Assert.Contains(SymbolKind.Ground, terminals);
        Assert.Contains(SymbolKind.Term,   terminals);
    }

    // ── ByCategory ────────────────────────────────────────────────────────────

    [Fact]
    public void ByCategory_Lumped_ReturnsLumpedPassives()
    {
        var kinds = LibraryCatalog.ByCategory(ComponentCategory.Lumped).Select(i => i.Kind).ToHashSet();
        Assert.Equal(new HashSet<SymbolKind>
        {
            SymbolKind.Resistor, SymbolKind.Inductor, SymbolKind.Capacitor, SymbolKind.NonlinearC,
            SymbolKind.Mutual,
        }, kinds);
    }

    [Fact]
    public void ByCategory_Sources_ReturnsBothSources()
    {
        var kinds = LibraryCatalog.ByCategory(ComponentCategory.Sources).Select(i => i.Kind).ToHashSet();
        Assert.Contains(SymbolKind.Vdc,        kinds);
        Assert.Contains(SymbolKind.ToneSource, kinds);
    }

    [Fact]
    public void ByCategory_Microstrip_ContainsAllSixBuiltIns()
    {
        // brief-L5a-pcell-contract-and-microstrip.md: MLIN/MBend/MTee/MCross are SymbolKind-
        // registered under the Microstrip category (previously reserved, unpopulated).
        // brief-mtaper-mklopf.md adds MTaper/MKlopf to the same family.
        var kinds = LibraryCatalog.ByCategory(ComponentCategory.Microstrip).Select(i => i.Kind).ToList();
        Assert.Contains(SymbolKind.Mlin, kinds);
        Assert.Contains(SymbolKind.MBend, kinds);
        Assert.Contains(SymbolKind.MTee, kinds);
        Assert.Contains(SymbolKind.MCross, kinds);
        Assert.Contains(SymbolKind.Mtaper, kinds);
        Assert.Contains(SymbolKind.Mklopf, kinds);
        Assert.Equal(6, kinds.Count);
    }

    [Fact]
    public void ByCategory_DataFiles_ContainsSnp()
    {
        // SnP (Touchstone file-backed N-port) lives in the DataFiles category.
        var kinds = LibraryCatalog.ByCategory(ComponentCategory.DataFiles).Select(i => i.Kind);
        Assert.Contains(SymbolKind.Snp, kinds);
    }

    // ── Multi-category (ExtraCategories set-containment) ─────────────────────

    [Fact]
    public void ByCategory_MultiCategory_ItemAppearsInBothCategories()
    {
        // Mlin has ExtraCategories=[TransmissionLine]: it appears under Microstrip AND
        // TransmissionLine (ZPort is NOT multi-category — owner report, 2026-07-29: Z/Z1P/Z2P/Z3P
        // are not transmission lines and must not show under that filter; see the dedicated
        // ZPort_NeverAppearsUnderTransmissionLine test below).
        var microstrip = LibraryCatalog.ByCategory(ComponentCategory.Microstrip).Select(i => i.Kind);
        var tline      = LibraryCatalog.ByCategory(ComponentCategory.TransmissionLine).Select(i => i.Kind);
        Assert.Contains(SymbolKind.Mlin, microstrip);
        Assert.Contains(SymbolKind.Mlin, tline);
    }

    [Fact]
    public void AllItems_MultiCategoryItem_AppearsOnce()
    {
        // AllItems lists each (Kind, PortCount) pair once regardless of how many categories it
        // belongs to.
        var mlinCount = LibraryCatalog.AllItems.Count(i => i.Kind == SymbolKind.Mlin);
        Assert.Equal(1, mlinCount);
    }

    [Fact]
    public void AllItems_MultiCategoryItem_SortsByPrimaryCategory()
    {
        // Mlin's primary is Microstrip — it sorts with the Microstrip group, not TransmissionLine.
        var mlin = LibraryCatalog.AllItems.First(i => i.Kind == SymbolKind.Mlin);
        Assert.Equal(ComponentCategory.Microstrip, mlin.Category);
    }

    [Fact]
    public void ByCategory_TransmissionLine_ContainsMlin()
    {
        var tline = LibraryCatalog.ByCategory(ComponentCategory.TransmissionLine);
        Assert.Contains(tline, i => i.Kind == SymbolKind.Mlin);
    }

    [Fact]
    public void TransmissionLineFilter_NeverContainsZPort_OrAnyOfItsEntryPoints()
    {
        // Owner report, 2026-07-29: Z, Z1P, Z2P, Z3P are impedance-network terminations, not
        // transmission lines, and must not appear under this filter.
        var tline = LibraryCatalog.ByCategory(ComponentCategory.TransmissionLine);
        Assert.DoesNotContain(tline, i => i.Kind == SymbolKind.ZPort);
    }

    [Fact]
    public void SingleCategoryItems_UnchangedByExtraCategoryFeature()
    {
        // Lumped returns exactly R/L/C/NLC/Mutual — no extra-category bleed from other components.
        var kinds = LibraryCatalog.ByCategory(ComponentCategory.Lumped).Select(i => i.Kind).ToHashSet();
        Assert.Equal(
            new HashSet<SymbolKind>
            {
                SymbolKind.Resistor, SymbolKind.Inductor, SymbolKind.Capacitor, SymbolKind.NonlinearC,
                SymbolKind.Mutual,
            },
            kinds);
    }

    // ── Common ────────────────────────────────────────────────────────────────

    [Fact]
    public void Common_ContainsOnlyIsCommonItems()
    {
        Assert.All(LibraryCatalog.Common, i => Assert.True(i.IsCommon));
    }

    [Fact]
    public void Common_ContainsRLC()
    {
        var commonKinds = LibraryCatalog.Common.Select(i => i.Kind).ToHashSet();
        Assert.Contains(SymbolKind.Resistor,  commonKinds);
        Assert.Contains(SymbolKind.Inductor,  commonKinds);
        Assert.Contains(SymbolKind.Capacitor, commonKinds);
    }

    [Fact]
    public void Common_DoesNotContainSddOrGeneric()
    {
        var commonKinds = LibraryCatalog.Common.Select(i => i.Kind).ToHashSet();
        Assert.DoesNotContain(SymbolKind.Sdd,     commonKinds);
        Assert.DoesNotContain(SymbolKind.Generic, commonKinds);
    }

    // ── RecentlyUsed ─────────────────────────────────────────────────────────

    [Fact]
    public void RecentlyUsed_OrderedByMruList()
    {
        var mru = new[] { SymbolKind.Capacitor, SymbolKind.Resistor, SymbolKind.Inductor };
        var result = LibraryCatalog.RecentlyUsed(mru);
        Assert.Equal(3, result.Count);
        Assert.Equal(SymbolKind.Capacitor, result[0].Kind);
        Assert.Equal(SymbolKind.Resistor,  result[1].Kind);
        Assert.Equal(SymbolKind.Inductor,  result[2].Kind);
    }

    [Fact]
    public void RecentlyUsed_UnknownKind_Silently_Skipped()
    {
        var mru = new[] { SymbolKind.Resistor, (SymbolKind)999 };
        var result = LibraryCatalog.RecentlyUsed(mru);
        Assert.Single(result);
        Assert.Equal(SymbolKind.Resistor, result[0].Kind);
    }

    [Fact]
    public void RecentlyUsed_EmptyList_ReturnsEmpty()
    {
        Assert.Empty(LibraryCatalog.RecentlyUsed([]));
    }

    // ── Search ────────────────────────────────────────────────────────────────

    [Fact]
    public void Search_Cap_FindsCapacitor()
    {
        var result = LibraryCatalog.Search("cap");
        Assert.Contains(result, i => i.Kind == SymbolKind.Capacitor);
    }

    [Fact]
    public void Search_Res_FindsResistor()
    {
        var result = LibraryCatalog.Search("res");
        Assert.Contains(result, i => i.Kind == SymbolKind.Resistor);
    }

    [Fact]
    public void Search_Ind_FindsInductor()
    {
        var result = LibraryCatalog.Search("ind");
        Assert.Contains(result, i => i.Kind == SymbolKind.Inductor);
    }

    [Fact]
    public void Search_CaseInsensitive_CapUpperCase_FindsCapacitor()
    {
        var result = LibraryCatalog.Search("CAP");
        Assert.Contains(result, i => i.Kind == SymbolKind.Capacitor);
    }

    [Fact]
    public void Search_VTone_FindsToneSource()
    {
        var result = LibraryCatalog.Search("VTone");
        Assert.Contains(result, i => i.Kind == SymbolKind.ToneSource);
    }

    [Fact]
    public void Search_RF_FindsToneSource()
    {
        var result = LibraryCatalog.Search("rf");
        Assert.Contains(result, i => i.Kind == SymbolKind.ToneSource);
    }

    [Fact]
    public void Search_EmptyQuery_ReturnsAllItems()
    {
        var result = LibraryCatalog.Search("");
        Assert.Equal(LibraryCatalog.AllItems.Count, result.Count);
    }

    [Fact]
    public void Search_WhitespaceQuery_ReturnsAllItems()
    {
        var result = LibraryCatalog.Search("   ");
        Assert.Equal(LibraryCatalog.AllItems.Count, result.Count);
    }

    [Fact]
    public void Search_WithCategory_FiltersWithinCategory()
    {
        // "r" matches Resistor in Lumped, but also other things in other categories
        var allR    = LibraryCatalog.Search("r");
        var lumpedR = LibraryCatalog.Search("r", ComponentCategory.Lumped);

        // lumpedR must only contain Lumped items
        Assert.All(lumpedR, i => Assert.Equal(ComponentCategory.Lumped, i.Category));
        // And lumpedR must be a subset of allR
        Assert.All(lumpedR, i => Assert.Contains(allR, x => x.Kind == i.Kind));
    }

    [Fact]
    public void Search_WithCategory_EmptyQuery_ReturnsWholeCategory()
    {
        var result   = LibraryCatalog.Search("", ComponentCategory.Lumped);
        var expected = LibraryCatalog.ByCategory(ComponentCategory.Lumped);
        Assert.Equal(expected.Select(i => i.Kind), result.Select(i => i.Kind));
    }

    [Fact]
    public void Search_NoMatch_ReturnsEmpty()
    {
        Assert.Empty(LibraryCatalog.Search("xyzzy_no_such_component"));
    }

    // ── PaletteItem shape ─────────────────────────────────────────────────────

    [Fact]
    public void PaletteItem_SearchTerms_NonNull()
    {
        Assert.All(LibraryCatalog.AllItems, i => Assert.NotNull(i.SearchTerms));
    }

    [Fact]
    public void PaletteItem_DisplayName_MatchesRegistry()
    {
        foreach (var item in LibraryCatalog.AllItems)
            Assert.Equal(ComponentTypeRegistry.DisplayName(item.Kind, item.PortCount), item.DisplayName);
    }

    [Fact]
    public void PaletteItem_IsCommon_MatchesRegistry()
    {
        foreach (var item in LibraryCatalog.AllItems)
            Assert.Equal(ComponentTypeRegistry.Get(item.Kind).IsCommon, item.IsCommon);
    }
}
