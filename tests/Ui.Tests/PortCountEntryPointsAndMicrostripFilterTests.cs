using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gates for brief-housekeeping-tearoff-palette-repo.md §2 (S1P/S2P/S3P/S4P, Z1P/Z2P/Z3P,
/// SDD1/SDD2/SDD3 palette entry points) and §3 (microstrip components get the Transmission Line
/// filter keyword).
/// </summary>
public class PortCountEntryPointsAndMicrostripFilterTests
{
    private static SchematicViewModel MakeVm() => new(new SchematicEditModel());

    // ── §2: explicit port-count entry points exist, same underlying type ─────────────────────

    [Theory]
    [InlineData(SymbolKind.Snp, 1, "S1P")]
    [InlineData(SymbolKind.Snp, 2, "S2P")]
    [InlineData(SymbolKind.Snp, 3, "S3P")]
    [InlineData(SymbolKind.Snp, 4, "S4P")]
    [InlineData(SymbolKind.ZPort, 1, "Z1P")]
    [InlineData(SymbolKind.ZPort, 2, "Z2P")]
    [InlineData(SymbolKind.ZPort, 3, "Z3P")]
    [InlineData(SymbolKind.Sdd, 1, "SDD1")]
    [InlineData(SymbolKind.Sdd, 2, "SDD2")]
    [InlineData(SymbolKind.Sdd, 3, "SDD3")]
    public void EntryPoint_ExistsWithCorrectPortCountAndDisplayName(SymbolKind kind, int portCount, string displayName)
    {
        var item = LibraryCatalog.AllItems.Single(i => i.Kind == kind && i.PortCount == portCount);
        Assert.Equal(displayName, item.DisplayName);
    }

    [Fact]
    public void Snp_HasNoZ4POrSdd4EntryPoints_OnlyS4PWasRequested()
    {
        // The owner explicitly asked for S4P alongside S1P/S2P/S3P; Z/SDD stayed at N=1..3 per brief.
        Assert.DoesNotContain(LibraryCatalog.AllItems, i => i.Kind == SymbolKind.ZPort && i.PortCount == 4);
        Assert.DoesNotContain(LibraryCatalog.AllItems, i => i.Kind == SymbolKind.Sdd && i.PortCount == 4);
        Assert.Contains(LibraryCatalog.AllItems, i => i.Kind == SymbolKind.Snp && i.PortCount == 4);
    }

    [Fact]
    public void PlainZTile_IsGone_ButZ1PAndZ2PRemain()
    {
        // Owner report, 2026-09-01: the bare "Z" tile placed a 2-port impedance network, which is
        // precisely what the Z2P tile beside it places — one part with two spellings in the palette.
        // The plain tile is suppressed; the port-count entry points are what a user picks.
        Assert.DoesNotContain(LibraryCatalog.AllItems, i => i.Kind == SymbolKind.ZPort && i.PortCount == 0);
        Assert.DoesNotContain(LibraryCatalog.AllItems, i => i.DisplayName == "Z");
        Assert.Contains(LibraryCatalog.AllItems, i => i.Kind == SymbolKind.ZPort && i.PortCount == 1);
        Assert.Contains(LibraryCatalog.AllItems, i => i.Kind == SymbolKind.ZPort && i.PortCount == 2);
    }

    [Fact]
    public void PlainZTileRemoval_LeavesZPortPlaceableAndSearchable()
    {
        // Suppressing the tile must not retire the KIND: Z stays in AllItems (via its entry points),
        // still answers a search for its registry terms, and every other dynamic type keeps its own
        // plain tile.
        Assert.Contains(LibraryCatalog.AllItems, i => i.Kind == SymbolKind.ZPort);
        Assert.Contains(LibraryCatalog.Search("impedance"), i => i.Kind == SymbolKind.ZPort);
        Assert.Contains(LibraryCatalog.AllItems, i => i.Kind == SymbolKind.Snp && i.PortCount == 0);
        Assert.Contains(LibraryCatalog.AllItems, i => i.Kind == SymbolKind.Sdd && i.PortCount == 0);
    }

    [Fact]
    public void Z1P_Alone_HasTerminalsFilterKeyword_Z2PAndZ3PDoNot()
    {
        var z1p = LibraryCatalog.AllItems.Single(i => i.Kind == SymbolKind.ZPort && i.PortCount == 1);
        var z2p = LibraryCatalog.AllItems.Single(i => i.Kind == SymbolKind.ZPort && i.PortCount == 2);
        var z3p = LibraryCatalog.AllItems.Single(i => i.Kind == SymbolKind.ZPort && i.PortCount == 3);

        Assert.Contains(ComponentCategory.Terminals, z1p.ExtraCategories ?? []);
        Assert.DoesNotContain(ComponentCategory.Terminals, z2p.ExtraCategories ?? []);
        Assert.DoesNotContain(ComponentCategory.Terminals, z3p.ExtraCategories ?? []);

        Assert.Contains(LibraryCatalog.ByCategory(ComponentCategory.Terminals), i => i.Kind == SymbolKind.ZPort && i.PortCount == 1);
    }

    [Fact]
    public void EntryPoints_IsCommon_MatchesTheirDynamicTypesRegistryEntry()
    {
        // Snp is IsCommon=true in the registry; ZPort/Sdd are not. Every entry point must agree.
        Assert.All(LibraryCatalog.AllItems.Where(i => i.Kind == SymbolKind.Snp),
            i => Assert.True(i.IsCommon));
        Assert.All(LibraryCatalog.AllItems.Where(i => i.Kind == SymbolKind.ZPort),
            i => Assert.Equal(ComponentTypeRegistry.Get(SymbolKind.ZPort).IsCommon, i.IsCommon));
        Assert.All(LibraryCatalog.AllItems.Where(i => i.Kind == SymbolKind.Sdd),
            i => Assert.Equal(ComponentTypeRegistry.Get(SymbolKind.Sdd).IsCommon, i.IsCommon));
    }

    // ── §2 gate: placed S2P is an SNP with N=2, identical to hand-set SNP+N=2 ─────────────────

    [Fact]
    public void PlacingS2PFromPalette_YieldsSnpInstance_WithNEquals2()
    {
        var vm = MakeVm();
        vm.CommitPlacement(SymbolKind.Snp, portCount: 2, SymbolRotation.R0, 0, 0);
        var comp = vm.EditModel.Components.Single();

        Assert.Equal(SymbolKind.Snp, comp.Symbol);
        Assert.Equal(2, comp.PortCount);
        Assert.Equal("S2P", ComponentTypeRegistry.DisplayName(comp.Symbol, comp.PortCount));
    }

    [Fact]
    public void PlacingS2PFromPalette_NetlistParametersIdentical_ToPlacingSnpAndSettingNByHand()
    {
        var viaPalette = MakeVm();
        viaPalette.CommitPlacement(SymbolKind.Snp, portCount: 2, SymbolRotation.R0, 0, 0);
        var paletteComp = viaPalette.EditModel.Components.Single();

        var viaHand = MakeVm();
        viaHand.CommitPlacement(SymbolKind.Snp, portCount: 0, SymbolRotation.R0, 0, 0); // plain generic tile
        var handComp = viaHand.EditModel.Components.Single();
        var numPorts = handComp.Parameters.Single(p => p.Name == "NumPorts");
        numPorts.Expression = "2"; // the user manually setting N=2 afterward

        // Same PortCount, same parameter set (name/expression/unit) for every parameter — this is
        // what NetExtractor consumes, so the emitted netlist is identical either way.
        Assert.Equal(2, paletteComp.PortCount);
        Assert.Equal(2, handComp.PortCount);
        var paletteParams = paletteComp.Parameters.Select(p => (p.Name, p.Expression, p.Unit)).ToList();
        var handParams    = handComp.Parameters.Select(p => (p.Name, p.Expression, p.Unit)).ToList();
        Assert.Equal(handParams, paletteParams);
    }

    [Fact]
    public void DisplayName_FollowsPortCount_IfChangedAfterPlacement()
    {
        var vm = MakeVm();
        vm.CommitPlacement(SymbolKind.Snp, portCount: 2, SymbolRotation.R0, 0, 0);
        var comp = vm.EditModel.Components.Single();
        Assert.Equal("S2P", ComponentTypeRegistry.DisplayName(comp.Symbol, comp.PortCount));

        comp.Parameters.Single(p => p.Name == "NumPorts").Expression = "3";
        Assert.Equal(3, comp.PortCount); // derived, live getter — no separate rename step
        Assert.Equal("S3P", ComponentTypeRegistry.DisplayName(comp.Symbol, comp.PortCount));
    }

    // ── §3: every microstrip component carries the Transmission Line filter keyword ──────────

    public static readonly SymbolKind[] MicrostripKinds =
        [SymbolKind.Mlin, SymbolKind.MBend, SymbolKind.MTee, SymbolKind.MCross, SymbolKind.Mtaper, SymbolKind.Mklopf];

    [Theory]
    [MemberData(nameof(MicrostripKindsData))]
    public void MicrostripComponent_AppearsUnder_TransmissionLineFilter(SymbolKind kind)
    {
        Assert.Contains(LibraryCatalog.ByCategory(ComponentCategory.TransmissionLine), i => i.Kind == kind);
    }

    public static IEnumerable<object[]> MicrostripKindsData() => MicrostripKinds.Select(k => new object[] { k });

    [Fact]
    public void MicrostripTransmissionLineMembership_UsesTheIdenticalCategoryTlinItself_Uses()
    {
        // TLIN's own PRIMARY category IS ComponentCategory.TransmissionLine — not a hand-typed
        // string. Every microstrip component declares that SAME enum value as an extra category,
        // so there is no keyword string to drift from TLIN's own spelling (R-hk-5).
        var tlin = ComponentTypeRegistry.Get(SymbolKind.Tline);
        Assert.Equal(ComponentCategory.TransmissionLine, tlin.Category);

        foreach (var kind in MicrostripKinds)
        {
            var info = ComponentTypeRegistry.Get(kind);
            Assert.Contains(ComponentCategory.TransmissionLine, info.ExtraCategories ?? []);
        }
    }
}
