using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// One palette tile per PART, carrying every view that part can be placed as.
///
/// <para><b>The rule these pin down:</b> a part is not two things because it has two views. Which
/// view gets placed is decided by what the tile was dropped ON — a schematic places the symbol, a
/// layout places the cell — exactly as a built-in has always behaved.</para>
/// </summary>
public sealed class KitPaletteMergeTests
{
    private static PaletteItem Part(string kit, string id) => new(
        Kind: SymbolKind.Generic, PortCount: 0, DisplayName: id,
        Category: ComponentCategory.Other, SearchTerms: [id], IsCommon: false,
        ExtraCategories: null, Pdk: new PdkPartRef(kit, id, CellDir: $"/kits/{kit}/{id}"));

    /// <summary>The headline: a part with both views is ONE tile carrying both routes.</summary>
    [Fact]
    public void APartWithBothViews_IsOneTile_CarryingBothRoutes()
    {
        var merged = KitPaletteMerge.Compose(
            [Part("ProcessKit", "nmos"), Part("ProcessKit", "cmim")],
            new Dictionary<string, string> { ["nmos"] = "ProcessKit" });

        Assert.Equal(2, merged.Count);   // not three — the generator did not add a tile of its own

        var nmos = Assert.Single(merged, i => i.DisplayName == "nmos");
        Assert.Equal("nmos", nmos.PCellGeneratorId);              // droppable on a layout
        Assert.Equal("/kits/ProcessKit/nmos", nmos.Pdk!.CellDir);      // and still on a schematic

        // A part with no layout generator keeps its schematic route and gains nothing.
        var cmim = Assert.Single(merged, i => i.DisplayName == "cmim");
        Assert.Null(cmim.PCellGeneratorId);
        Assert.Equal("/kits/ProcessKit/cmim", cmim.Pdk!.CellDir);
    }

    /// <summary>A generator with no schematic part is still placeable — layout-only, but real.</summary>
    [Fact]
    public void AGeneratorWithNoSchematicPart_GetsItsOwnTile_UnderItsKit()
    {
        var merged = KitPaletteMerge.Compose(
            [Part("ProcessKit", "nmos")],
            new Dictionary<string, string> { ["sealring"] = "ProcessKit" });

        var sealring = Assert.Single(merged, i => i.DisplayName == "sealring");
        Assert.Equal("sealring", sealring.PCellGeneratorId);
        Assert.Equal("ProcessKit", sealring.Pdk!.KitName);   // the kit's own heading, beside its parts
        Assert.Null(sealring.Pdk.CellDir);               // no symbol — dropping on a schematic does nothing
    }

    /// <summary>
    /// A supplier whose schematic kit and PCell kit carry different names still merges, because the
    /// part name is unambiguous. This is the case a kit actually hits.
    /// </summary>
    [Fact]
    public void ANameMatchAcrossDifferentlyNamedKits_StillMerges_WhenUnambiguous()
    {
        var merged = KitPaletteMerge.Compose(
            [Part("ProcessKit", "nmos")],
            new Dictionary<string, string> { ["nmos"] = "ProcessKit-pcells" });

        var one = Assert.Single(merged);
        Assert.Equal("nmos", one.PCellGeneratorId);
        Assert.Equal("ProcessKit", one.Pdk!.KitName);   // stays under the part's own kit, not the generator's
    }

    /// <summary>
    /// Two kits with a same-named part do NOT merge across kits — that would give one kit's tile the
    /// other's layout, which draws perfectly and is wrong. The generator lands on its own kit's part,
    /// and the other kit's part is untouched.
    /// </summary>
    [Fact]
    public void ASameNamedPartInTwoKits_NeverMergesAcrossThem()
    {
        var merged = KitPaletteMerge.Compose(
            [Part("KitA", "nmos"), Part("KitB", "nmos")],
            new Dictionary<string, string> { ["nmos"] = "KitA" });

        Assert.Equal("nmos", Assert.Single(merged, i => i.Pdk!.KitName == "KitA").PCellGeneratorId);
        Assert.Null(Assert.Single(merged, i => i.Pdk!.KitName == "KitB").PCellGeneratorId);
    }

    /// <summary>
    /// An ambiguous cross-kit name is not guessed at: with two same-named parts and neither in the
    /// generator's own kit, the generator gets its own tile rather than attaching to a coin flip.
    /// </summary>
    [Fact]
    public void AnAmbiguousCrossKitName_IsNotGuessedAt()
    {
        var merged = KitPaletteMerge.Compose(
            [Part("KitA", "nmos"), Part("KitB", "nmos")],
            new Dictionary<string, string> { ["nmos"] = "KitC" });

        Assert.All(merged.Where(i => i.Pdk!.CellDir is not null), i => Assert.Null(i.PCellGeneratorId));
        Assert.Single(merged, i => i.PCellGeneratorId == "nmos" && i.Pdk!.KitName == "KitC");
    }

    /// <summary>No kits, no generators: nothing, and no exception.</summary>
    [Fact]
    public void NothingIn_NothingOut()
        => Assert.Empty(KitPaletteMerge.Compose([], new Dictionary<string, string>()));
}
