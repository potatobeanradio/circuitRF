using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels.Dock;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Owner request, 2026-08-16: an explicit pin order for the Library Palette's "All" filter, a new
/// "All - Alphabetical" filter (built-ins alphabetical, then PDK parts grouped by kit and
/// alphabetical within each kit — never interleaved across kits), and a new "Nonlinear" filter
/// (NonlinearC, VerilogA, Diode, the FET family, every SDD tile).
/// </summary>
public class PaletteFilterOrderingTests
{
    // ── "All" — the explicit pin order ────────────────────────────────────────

    [Fact]
    public void AllItemsPinnedOrder_StartsWithTheExactRequestedSequence()
    {
        var pinned = LibraryCatalog.AllItemsPinnedOrder();

        string[] expected =
        [
            "R", "GND", "L", "M", "C", "NonlinearC",
            "Term", "TermG", "VAR", "MEAS", "IProbe", "VProbe", "Vdc",
            "P1Tone", "ITone", "VTone",
            "S2P", "WSProbe", "S3P", "SPICE", "TLIN", "MLIN",
            "SourceTuner", "LoadTuner", "Z1P", "wBond",
        ];

        Assert.Equal(expected, pinned.Take(expected.Length).Select(i => i.DisplayName));
    }

    [Fact]
    public void AllItemsPinnedOrder_ContainsEveryBuiltIn_ExactlyOnce_SameSetAsAllItems()
    {
        var pinned = LibraryCatalog.AllItemsPinnedOrder();

        Assert.Equal(LibraryCatalog.AllItems.Count, pinned.Count);
        Assert.Equal(
            LibraryCatalog.AllItems.Select(i => (i.Kind, i.PortCount)).OrderBy(k => k).ToList(),
            pinned.Select(i => (i.Kind, i.PortCount)).OrderBy(k => k).ToList());
    }

    [Fact]
    public void AllItemsPinnedOrder_EverythingAfterThePinnedRows_KeepsAllItemsOwnRelativeOrder()
    {
        // 27 = the length of LibraryCatalog's own AllFilterPinnedOrder. Bump this when a row is
        // pinned or unpinned; Match was the 23rd (2026-08-19), ITone the 24th (2026-08-29),
        // SpiceModel the 25th (2026-09-01), pinned next to SnP because it is the same gesture —
        // placing a file the user already has — VProbe the 26th, pinned next to IProbe for the
        // same reason, and WSProbe the 27th (WSP-4). WSProbe was pinned between IProbe and VProbe
        // until 2026-09-08 and now sits directly after S2P (owner) — the count is unchanged, and the
        // owner's own Vdc-directly-after-VProbe adjacency (2026-09-07) survives the move because the
        // probe run closes up behind it. The sequence asserted above is the authority.
        //
        // The tail is the automatic order EXCEPT for LibraryCatalog's declared positional swaps —
        // Bead <-> SRLC, 2026-09-07 — which the expectation applies here rather than exempting the
        // rows from the check: a swap that silently became a third change would otherwise pass.
        const int PinnedRows = 27;
        var pinned      = LibraryCatalog.AllItemsPinnedOrder();
        var pinnedSet   = pinned.Take(PinnedRows).Select(i => (i.Kind, i.PortCount)).ToHashSet();
        var restActual  = pinned.Skip(PinnedRows).Select(i => (i.Kind, i.PortCount)).ToList();
        var restInAllItems = LibraryCatalog.AllItems
            .Where(i => !pinnedSet.Contains((i.Kind, i.PortCount)))
            .Select(i => (i.Kind, i.PortCount))
            .ToList();

        int iBead = restInAllItems.IndexOf((SymbolKind.Bead, 0));
        int iSrlc = restInAllItems.IndexOf((SymbolKind.Srlc, 0));
        Assert.True(iBead >= 0 && iSrlc >= 0);
        (restInAllItems[iBead], restInAllItems[iSrlc]) = (restInAllItems[iSrlc], restInAllItems[iBead]);

        Assert.Equal(restInAllItems, restActual);
    }

    /// <summary>
    /// Owner request, 2026-09-07: Bead and SRLC trade places in the "All" filter. Asserted as a
    /// SWAP — the two reverse, and the row that sits BETWEEN them stays put — so the test says what
    /// was asked for rather than freezing absolute indices that any new Lumped registry entry shifts.
    /// </summary>
    [Fact]
    public void AllItemsPinnedOrder_BeadAndSrlc_TradePlaces()
    {
        var order = LibraryCatalog.AllItemsPinnedOrder().Select(i => i.Kind).ToList();

        // The unpinned TAIL, in AllItems' own order — the run the swap acts on. Comparing against
        // the whole of AllItems would be the wrong baseline: the curated head hoists R, L, C,
        // Mutual and NonlinearC out of this run, so rows that sit between Bead and SRLC there are
        // not between them here.
        var plain = LibraryCatalog.AllItems
            .Where(i => !order.Take(27).Contains(i.Kind))
            .Select(i => i.Kind)
            .ToList();

        int beadWas = plain.IndexOf(SymbolKind.Bead), srlcWas = plain.IndexOf(SymbolKind.Srlc);
        int beadNow = order.IndexOf(SymbolKind.Bead), srlcNow = order.IndexOf(SymbolKind.Srlc);

        // Category-then-name puts Bead ahead of SRLC (both Lumped); the swap reverses exactly that.
        Assert.True(beadWas < srlcWas, "AllItems' own order should still put Bead ahead of SRLC.");
        Assert.True(srlcNow < beadNow, "SRLC should now sit where Bead used to.");

        // The tail rows BETWEEN them stay put, in their own order — a swap, not a re-sort of the
        // run. Stated as "the between-run is unchanged" rather than as a literal list, because the
        // RLC family grew from two members to nine in 2026-09-20's round and a frozen list would
        // have to be re-typed every time the Lumped category gains a row. (The whole tail is
        // compared against AllItems' own order by the sibling test above; this one only has to say
        // that nothing travelled with the pair.)
        var between = plain.GetRange(beadWas + 1, srlcWas - beadWas - 1);
        Assert.NotEmpty(between);
        Assert.Equal(between, order.GetRange(srlcNow + 1, beadNow - srlcNow - 1));
    }

    /// <summary>Owner request, 2026-09-07: Vdc moves down to sit directly after VProbe.</summary>
    [Fact]
    public void AllItemsPinnedOrder_VdcSitsDirectlyAfterVProbe()
    {
        var names = LibraryCatalog.AllItemsPinnedOrder().Select(i => i.DisplayName).ToList();
        int vProbe = names.IndexOf("VProbe");
        Assert.True(vProbe >= 0);
        Assert.Equal("Vdc", names[vProbe + 1]);
    }

    [Fact]
    public void AllItemsPinnedOrder_IsDeterministic()
    {
        var first  = LibraryCatalog.AllItemsPinnedOrder().Select(i => i.Kind).ToList();
        var second = LibraryCatalog.AllItemsPinnedOrder().Select(i => i.Kind).ToList();
        Assert.Equal(first, second);
    }

    // ── "All - Alphabetical" — built-in half ──────────────────────────────────

    [Fact]
    public void AllItemsAlphabetical_IsStrictlyAscendingByDisplayName()
    {
        var items = LibraryCatalog.AllItemsAlphabetical();
        for (int i = 1; i < items.Count; i++)
            Assert.True(
                string.Compare(items[i - 1].DisplayName, items[i].DisplayName, StringComparison.OrdinalIgnoreCase) <= 0,
                $"'{items[i - 1].DisplayName}' should sort at or before '{items[i].DisplayName}'");
    }

    [Fact]
    public void AllItemsAlphabetical_SameSetAsAllItems_NoCategoryGrouping()
    {
        var alpha = LibraryCatalog.AllItemsAlphabetical();
        Assert.Equal(LibraryCatalog.AllItems.Count, alpha.Count);
        Assert.Equal(
            LibraryCatalog.AllItems.Select(i => (i.Kind, i.PortCount)).OrderBy(k => k).ToList(),
            alpha.Select(i => (i.Kind, i.PortCount)).OrderBy(k => k).ToList());
    }

    // ── "Nonlinear" — the new Real category ───────────────────────────────────

    [Fact]
    public void Nonlinear_ContainsExactlyTheRequestedKinds()
    {
        var nonlinear = LibraryCatalog.ByCategory(ComponentCategory.Nonlinear);

        // NonlinearC, VerilogA, Diode, the 5 n-channel FET laws, the 3 p-channel ones, the 2 JFET
        // channels, the 4 lateral MOS tiles (two levels x two channels), the 2 vertical power MOS
        // channels, the 2 IGBT channels, the 2 BJT polarities, the 2 mixer tiles, every SDD row
        // (plain SDD + SDD1/SDD2/SDD3), and SPICE (owner request 2026-09-01 — a referenced .model
        // or .subckt is normally a transistor or diode, so it must be reachable from here).
        //
        // The ferrite bead is deliberately NOT here: it is a linear impedance and belongs with the
        // lumped elements, which is where a user looking for one goes.
        Assert.Equal(30, nonlinear.Count);
        Assert.Contains(nonlinear, i => i.Kind == SymbolKind.NonlinearC);
        Assert.Contains(nonlinear, i => i.Kind == SymbolKind.VerilogA);
        Assert.Contains(nonlinear, i => i.Kind == SymbolKind.Diode);
        Assert.Contains(nonlinear, i => i.Kind == SymbolKind.FetCurtice);
        Assert.Contains(nonlinear, i => i.Kind == SymbolKind.FetCurticeCubic);
        Assert.Contains(nonlinear, i => i.Kind == SymbolKind.FetStatz);
        Assert.Contains(nonlinear, i => i.Kind == SymbolKind.FetMaterka);
        Assert.Contains(nonlinear, i => i.Kind == SymbolKind.FetAngelov);
        Assert.Contains(nonlinear, i => i.Kind == SymbolKind.PFetCurtice);
        Assert.Contains(nonlinear, i => i.Kind == SymbolKind.PFetStatz);
        Assert.Contains(nonlinear, i => i.Kind == SymbolKind.PFetMaterka);
        Assert.Contains(nonlinear, i => i.Kind == SymbolKind.JfetN);
        Assert.Contains(nonlinear, i => i.Kind == SymbolKind.JfetP);
        Assert.Contains(nonlinear, i => i.Kind == SymbolKind.Mos1N);
        Assert.Contains(nonlinear, i => i.Kind == SymbolKind.Mos3N);
        Assert.Contains(nonlinear, i => i.Kind == SymbolKind.Mos3P);
        Assert.Contains(nonlinear, i => i.Kind == SymbolKind.VdmosN);
        Assert.Contains(nonlinear, i => i.Kind == SymbolKind.VdmosP);
        Assert.Contains(nonlinear, i => i.Kind == SymbolKind.IgbtN);
        Assert.Contains(nonlinear, i => i.Kind == SymbolKind.IgbtP);
        Assert.DoesNotContain(nonlinear, i => i.Kind == SymbolKind.Bead);
        Assert.Contains(nonlinear, i => i.Kind == SymbolKind.Mos1P);
        Assert.Contains(nonlinear, i => i.Kind == SymbolKind.BjtNpn);
        Assert.Contains(nonlinear, i => i.Kind == SymbolKind.BjtPnp);
        // Both mixer tiles: the device really is nonlinear (its law is a product of two port
        // voltages), so a user filtering for nonlinear parts must find it here.
        Assert.Contains(nonlinear, i => i.Kind == SymbolKind.Mixer);
        Assert.Contains(nonlinear, i => i.Kind == SymbolKind.MixerD);
        Assert.Equal(4, nonlinear.Count(i => i.Kind == SymbolKind.Sdd));   // SDD, SDD1, SDD2, SDD3
        Assert.Contains(nonlinear, i => i.Kind == SymbolKind.SpiceModel);

        // Nothing else leaks in — no lumped R/L/C, no terminals.
        Assert.DoesNotContain(nonlinear, i => i.Kind == SymbolKind.Resistor);
        Assert.DoesNotContain(nonlinear, i => i.Kind == SymbolKind.Term);
    }

    [Fact]
    public void Spice_ReachableFromDevicesAndNonlinear_ButKeepsDataFilesAsItsPrimary()
    {
        // Owner request, 2026-09-01. What a SPICE component REFERENCES is usually a transistor or a
        // diode, so somebody shopping for an active part has to find it under Devices and Nonlinear
        // — but it IS a file reference, so DataFiles stays its primary category and the tile still
        // sorts (and appears) exactly once.
        Assert.Contains(LibraryCatalog.ByCategory(ComponentCategory.Devices),   i => i.Kind == SymbolKind.SpiceModel);
        Assert.Contains(LibraryCatalog.ByCategory(ComponentCategory.Nonlinear), i => i.Kind == SymbolKind.SpiceModel);
        Assert.Contains(LibraryCatalog.ByCategory(ComponentCategory.DataFiles), i => i.Kind == SymbolKind.SpiceModel);

        var item = Assert.Single(LibraryCatalog.AllItems, i => i.Kind == SymbolKind.SpiceModel);
        Assert.Equal(ComponentCategory.DataFiles, item.Category);
        Assert.Equal("SPICE", item.DisplayName);
    }

    [Fact]
    public void VerilogA_ReachableFromDataFiles_ButKeepsDevicesAsItsPrimary()
    {
        // Owner request, 2026-09-17 — SpiceModel's arrangement above, read the other way round.
        // Both components place a FILE the user already has, so both have to be findable under Data
        // Files, where the choice between them is which compiled form the model arrived in. Devices
        // stays VerilogA's primary (a user shopping for a transistor still looks there first) and
        // the tile appears exactly once.
        Assert.Contains(LibraryCatalog.ByCategory(ComponentCategory.DataFiles), i => i.Kind == SymbolKind.VerilogA);
        Assert.Contains(LibraryCatalog.ByCategory(ComponentCategory.Devices),   i => i.Kind == SymbolKind.VerilogA);
        Assert.Contains(LibraryCatalog.ByCategory(ComponentCategory.Nonlinear), i => i.Kind == SymbolKind.VerilogA);

        var item = Assert.Single(LibraryCatalog.AllItems, i => i.Kind == SymbolKind.VerilogA);
        Assert.Equal(ComponentCategory.Devices, item.Category);
    }

    [Fact]
    public void Nonlinear_AppearsInThePaletteToolsCategoryList()
    {
        var tool = new PaletteTool();
        Assert.Contains(tool.Categories, c => c.DisplayName == "Nonlinear");
    }

    // ── PaletteTool wiring ─────────────────────────────────────────────────────

    [Fact]
    public void AllAlphabetical_IsListedDirectlyUnderAll()
    {
        var tool = new PaletteTool();
        var names = tool.Categories.Select(c => c.DisplayName).ToList();

        int allIdx = names.IndexOf("All");
        Assert.True(allIdx >= 0);
        Assert.Equal("All - Alphabetical", names[allIdx + 1]);
    }

    [Fact]
    public void SelectingAll_UsesThePinnedOrder()
    {
        var tool = new PaletteTool();
        tool.SelectedCategory = tool.Categories.Single(c => c.DisplayName == "All");

        var expectedStart = LibraryCatalog.AllItemsPinnedOrder().Take(5).Select(i => i.DisplayName);
        Assert.Equal(expectedStart, tool.DisplayedItems.Take(5).Select(i => i.Item.DisplayName));
    }

    [Fact]
    public void SelectingAllAlphabetical_BuiltInsFirst_ThenPdkGroupedByKit_NeverInterleaved()
    {
        var tool = new PaletteTool();
        tool.SetPdkParts([
            Item("ZetaPart", "KitB"),
            Item("AlphaPart", "KitB"),
            Item("BravoPart", "KitA"),
            Item("AlphaPart2", "KitA"),
        ]);
        tool.SelectedCategory = tool.Categories.Single(c => c.DisplayName == "All - Alphabetical");

        var names = tool.DisplayedItems.Select(i => i.Item.DisplayName).ToList();

        // Built-ins come first, in pure alphabetical order.
        var builtInCount = LibraryCatalog.AllItems.Count;
        Assert.Equal(LibraryCatalog.AllItemsAlphabetical().Select(i => i.DisplayName),
                     names.Take(builtInCount));

        // Then the PDK tail: KitA before KitB (alphabetical), and within each kit, alphabetical —
        // never interleaved between kits.
        var pdkTail = names.Skip(builtInCount).ToList();
        Assert.Equal(["AlphaPart2", "BravoPart", "AlphaPart", "ZetaPart"], pdkTail);
    }

    private static PaletteItem Item(string id, string kit) => new(
        Kind:            SymbolKind.Generic,
        PortCount:       0,
        DisplayName:     id,
        Category:        ComponentCategory.Other,
        SearchTerms:     [id, kit],
        IsCommon:        false,
        ExtraCategories: null,
        Pdk:             new PdkPartRef(kit, id, null, PdkKitRegistry.RefFor(kit, id), ""));
}
