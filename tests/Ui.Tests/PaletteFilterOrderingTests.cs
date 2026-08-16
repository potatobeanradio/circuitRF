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
            "Term", "TermG", "VAR", "MEAS", "Vdc", "IProbe",
            "P1Tone", "VTone",
            "S2P", "S3P", "TLIN", "MLIN",
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
        var pinned      = LibraryCatalog.AllItemsPinnedOrder();
        var pinnedSet   = pinned.Take(22).Select(i => (i.Kind, i.PortCount)).ToHashSet();
        var restActual  = pinned.Skip(22).Select(i => (i.Kind, i.PortCount)).ToList();
        var restInAllItems = LibraryCatalog.AllItems
            .Where(i => !pinnedSet.Contains((i.Kind, i.PortCount)))
            .Select(i => (i.Kind, i.PortCount))
            .ToList();

        Assert.Equal(restInAllItems, restActual);
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

        // NonlinearC, VerilogA, Diode, the 5 FETs, and every SDD row (plain SDD + SDD1/SDD2/SDD3).
        Assert.Equal(12, nonlinear.Count);
        Assert.Contains(nonlinear, i => i.Kind == SymbolKind.NonlinearC);
        Assert.Contains(nonlinear, i => i.Kind == SymbolKind.VerilogA);
        Assert.Contains(nonlinear, i => i.Kind == SymbolKind.Diode);
        Assert.Contains(nonlinear, i => i.Kind == SymbolKind.FetCurtice);
        Assert.Contains(nonlinear, i => i.Kind == SymbolKind.FetCurticeCubic);
        Assert.Contains(nonlinear, i => i.Kind == SymbolKind.FetStatz);
        Assert.Contains(nonlinear, i => i.Kind == SymbolKind.FetMaterka);
        Assert.Contains(nonlinear, i => i.Kind == SymbolKind.FetAngelov);
        Assert.Equal(4, nonlinear.Count(i => i.Kind == SymbolKind.Sdd));   // SDD, SDD1, SDD2, SDD3

        // Nothing else leaks in — no lumped R/L/C, no terminals.
        Assert.DoesNotContain(nonlinear, i => i.Kind == SymbolKind.Resistor);
        Assert.DoesNotContain(nonlinear, i => i.Kind == SymbolKind.Term);
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
