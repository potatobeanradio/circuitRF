using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Pdk;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels.Dock;
using Xunit;

namespace CircuitRF.Ui.Tests;

// Synthetic kits throughout — a temp folder holding a hand-written symbol description and a
// stand-in icon file. Nothing here names or reads any kit.

[Collection(PdkToolsDirectoryCollection.Name)]
public sealed class PdkPartInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-pdk-" + Guid.NewGuid().ToString("N")[..8]);

    private string KitDir       => Path.Combine(_root, "kit");
    private string WorkspaceDir => Path.Combine(_root, "ws");

    public PdkPartInstallerTests()
    {
        Directory.CreateDirectory(KitDir);
        Directory.CreateDirectory(WorkspaceDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private const string SymbolFile = """
        1     7.707    0 0
        10    1    "PART_SYM"    2    1    0    0    341    0
        20    0    ""    0 0 0 0 0    2 -3 1    1    0    "schematic.prf" "schematic.lay"
        44    0    -600    600    600    1    0    0
        50    2    0 0 500 0 1    0    0    0    0    0    0    0    0
        60    4    0    2    0 0 500 0 1    0    0    0    0
        70    0 0    500 0
        42    1    2    "gate"     1    2    0    0 0 180000    0    0   ""
        42    2    2    "drain"    2    1    0    500 500 90000    0    0   ""
        42    3    2    "source"   3    0    0    500 -500 -90000    0    0   ""
        21
        """;

    private void WriteSymbol(string relative)
    {
        string abs = Path.Combine(KitDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, SymbolFile);
    }

    private void WriteIcon(string relative)
    {
        string abs = Path.Combine(KitDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllBytes(abs, [0x42, 0x4D, 0, 0, 0, 0]);   // stand-in; never decoded in these tests
    }

    private PdkImportReport ReportWith(params PdkPart[] parts)
    {
        var r = new PdkImportReport { RootPath = KitDir, KitName = "SampleKit" };
        r.Parts.AddRange(parts);
        return r;
    }

    private static PdkAsset SymbolAsset(string rel) =>
        new(rel, PdkAssetKind.SymbolArtwork, PdkAssetSupport.Supported, "symbol description (.dsn)");

    // ── Installation ──────────────────────────────────────────────────────────

    [Fact]
    public void ReadableSymbol_IsInstalledAsACell_WithItsPinsAndAPrimarySymbol()
    {
        WriteSymbol("symbols/part.dsn");

        var outcome = PdkPartInstaller.Install(
            ReportWith(new PdkPart("PART_A", "Part A", SymbolArtwork: SymbolAsset("symbols/part.dsn"))),
            WorkspaceDir);

        var item = Assert.Single(outcome.Items);
        Assert.Equal(1, outcome.SymbolsInstalled);

        string cellDir = item.Pdk!.CellDir!;
        Assert.True(Directory.Exists(cellDir));

        // Installed under the workspace's own kit folder, never beside the kit itself.
        Assert.StartsWith(Path.Combine(WorkspaceDir, PdkPartInstaller.InstallFolderName), cellDir);

        string ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        var ccell = CellPersistence.LoadFromFile(ccellPath);
        Assert.Equal("PART_A.csym", ccell.PrimarySymbol);
        Assert.Equal(3, ccell.NumPorts);

        // The written symbol must resolve exactly like any hand-authored cell's would.
        var resolved = CellSymbolResolver.Resolve(
            Path.GetRelativePath(WorkspaceDir, cellDir), WorkspaceDir);
        Assert.Equal(CellSymbolState.Resolved, resolved.State);
        Assert.Equal(3, resolved.Symbol!.Pins.Count);
        Assert.Equal(["gate", "drain", "source"], resolved.Symbol.Pins.Select(p => p.Name));
    }

    [Fact]
    public void PaletteIcon_IsResolvedToAnAbsolutePath_WhenTheKitShipsOne()
    {
        WriteSymbol("symbols/part.dsn");
        WriteIcon("bitmaps/part.bmp");

        var outcome = PdkPartInstaller.Install(
            ReportWith(new PdkPart("PART_A", "Part A",
                                   IconRelativePath: "bitmaps/part.bmp",
                                   SymbolArtwork:    SymbolAsset("symbols/part.dsn"))),
            WorkspaceDir);

        var item = Assert.Single(outcome.Items);
        Assert.Equal(1, outcome.IconsFound);
        Assert.True(File.Exists(item.Pdk!.IconPath));
    }

    [Fact]
    public void MissingIconFile_LeavesNoIconPath_AndTheEntryStillAppears()
    {
        WriteSymbol("symbols/part.dsn");

        var outcome = PdkPartInstaller.Install(
            ReportWith(new PdkPart("PART_A", "Part A",
                                   IconRelativePath: "bitmaps/absent.bmp",
                                   SymbolArtwork:    SymbolAsset("symbols/part.dsn"))),
            WorkspaceDir);

        var item = Assert.Single(outcome.Items);
        Assert.Null(item.Pdk!.IconPath);
        Assert.Equal(0, outcome.IconsFound);
        Assert.NotNull(item.Pdk.CellDir);   // a missing icon never costs a part its placeability
    }

    [Fact]
    public void PartWithNoReadableSymbol_IsKeptOutOfThePalette_AndCounted()
    {
        // A kit's internal building blocks have no symbol; a tile that cannot place anything is
        // worse than no tile. They are counted, not hidden — the import report still lists them.
        var outcome = PdkPartInstaller.Install(
            ReportWith(new PdkPart("PART_B", "Part B")), WorkspaceDir);

        Assert.Empty(outcome.Items);
        Assert.Equal(1, outcome.OmittedNotPlaceable);
        Assert.Equal(0, outcome.SymbolsInstalled);
    }

    [Fact]
    public void OnlyPlaceablePartsReachThePalette_WhenAKitMixesBothKinds()
    {
        WriteSymbol("symbols/part.dsn");

        var outcome = PdkPartInstaller.Install(
            ReportWith(
                new PdkPart("PART_A", "Part A", SymbolArtwork: SymbolAsset("symbols/part.dsn")),
                new PdkPart("HELPER_1", "Helper 1"),
                new PdkPart("HELPER_2", "Helper 2")),
            WorkspaceDir);

        Assert.Equal(["Part A"], outcome.Items.Select(i => i.DisplayName));
        Assert.Equal(2, outcome.OmittedNotPlaceable);
    }

    [Fact]
    public void BinaryCellView_IsNotTreatedAsASymbolDescription()
    {
        string abs = Path.Combine(KitDir, "cells", "part", "symbol", "symbol.oa");
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllBytes(abs, [0, 1, 2, 3]);

        var outcome = PdkPartInstaller.Install(
            ReportWith(new PdkPart("PART_C", "Part C",
                SymbolArtwork: new PdkAsset("cells/part/symbol/symbol.oa", PdkAssetKind.SymbolArtwork,
                                            PdkAssetSupport.RecognizedNotSupported, "binary cell view (symbol)"))),
            WorkspaceDir);

        Assert.Equal(0, outcome.SymbolsInstalled);
        Assert.Empty(outcome.Items);
        Assert.Equal(1, outcome.OmittedNotPlaceable);
    }

    [Fact]
    public void UnreadableSymbol_IsReportedByName_NeverSilentlySkipped()
    {
        string abs = Path.Combine(KitDir, "symbols", "junk.dsn");
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, "this is not a symbol description");

        var outcome = PdkPartInstaller.Install(
            ReportWith(new PdkPart("PART_D", "Part D", SymbolArtwork: SymbolAsset("symbols/junk.dsn"))),
            WorkspaceDir);

        Assert.Equal(0, outcome.SymbolsInstalled);
        Assert.Contains(outcome.Diagnostics, d => d.Contains("Part D", StringComparison.Ordinal));
    }

    [Fact]
    public void NoWorkspaceOpen_InstallsNothing_AndContributesNothingPlaceable()
    {
        WriteSymbol("symbols/part.dsn");
        WriteIcon("bitmaps/part.bmp");

        var outcome = PdkPartInstaller.Install(
            ReportWith(new PdkPart("PART_A", "Part A",
                                   IconRelativePath: "bitmaps/part.bmp",
                                   SymbolArtwork:    SymbolAsset("symbols/part.dsn"))),
            workspaceRootDir: null);

        // Nothing can be installed without a workspace, so nothing is placeable and the palette
        // gets nothing. The caller warns; it does not pretend the parts are usable.
        Assert.Empty(outcome.Items);
        Assert.Equal(1, outcome.OmittedNotPlaceable);
        Assert.Equal(0, outcome.SymbolsInstalled);
        Assert.False(Directory.Exists(Path.Combine(WorkspaceDir, PdkPartInstaller.InstallFolderName)));
    }

    [Fact]
    public void ArchiveImport_ReportsThatArtworkCouldNotBeReached_RatherThanFailingSilently()
    {
        var report = new PdkImportReport
        {
            RootPath = Path.Combine(_root, "kit.zip"),   // a file, not a directory
            KitName  = "ZippedKit",
        };
        report.Parts.Add(new PdkPart("PART_A", "Part A"));

        var outcome = PdkPartInstaller.Install(report, WorkspaceDir);

        Assert.Empty(outcome.Items);
        Assert.Contains(outcome.Diagnostics, d => d.Contains("archive", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReinstallingTheSameKit_ReusesItsCellFolder_RatherThanCreatingASecond()
    {
        WriteSymbol("symbols/part.dsn");
        var report = ReportWith(new PdkPart("PART_A", "Part A", SymbolArtwork: SymbolAsset("symbols/part.dsn")));

        var first  = PdkPartInstaller.Install(report, WorkspaceDir);
        var second = PdkPartInstaller.Install(report, WorkspaceDir);

        Assert.Equal(first.Items[0].Pdk!.CellDir, second.Items[0].Pdk!.CellDir);

        string kitDir = Path.Combine(WorkspaceDir, PdkPartInstaller.InstallFolderName, "SampleKit");
        Assert.Single(Directory.GetDirectories(kitDir));
    }

    // ── Name safety ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("a/b", "a_b")]
    [InlineData("a\\b", "a_b")]
    [InlineData("../escape", "_escape")]      // leading dots stripped too — no ".." and no dotfile
    [InlineData("..", "part")]
    [InlineData("", "part")]
    public void PartAndKitNames_AreMadeSafeAsFolderNames_OnEveryPlatform(string raw, string expected)
    {
        Assert.Equal(expected, PdkPartInstaller.SanitizeFolderName(raw));
    }

    [Fact]
    public void APartNameContainingASeparator_CannotEscapeTheInstallFolder()
    {
        WriteSymbol("symbols/part.dsn");

        var outcome = PdkPartInstaller.Install(
            ReportWith(new PdkPart("../../evil", "Evil", SymbolArtwork: SymbolAsset("symbols/part.dsn"))),
            WorkspaceDir);

        string cellDir = Path.GetFullPath(outcome.Items[0].Pdk!.CellDir!);
        string kitRoot = Path.GetFullPath(Path.Combine(WorkspaceDir, PdkPartInstaller.InstallFolderName));

        Assert.StartsWith(kitRoot, cellDir, StringComparison.Ordinal);
    }
}

// ── Palette ───────────────────────────────────────────────────────────────────

public sealed class PdkPaletteToolTests
{
    private static PaletteItem KitItem(string kit, string id, string? cellDir = null) =>
        new(SymbolKind.Generic, 0, id, ComponentCategory.Other, [id, kit], false, null,
            new PdkPartRef(kit, id, IconPath: null, CellDir: cellDir));

    [Fact]
    public void ImportedKit_GetsItsOwnCategory_ListedAfterTheBuiltInOnes()
    {
        var tool = new PaletteTool();
        int builtInCount = tool.Categories.Count;

        tool.SetPdkParts([KitItem("KitOne", "A"), KitItem("KitOne", "B"), KitItem("KitTwo", "C")]);

        Assert.Equal(builtInCount + 2, tool.Categories.Count);
        Assert.Equal(["KitOne", "KitTwo"], tool.Categories.TakeLast(2).Select(c => c.DisplayName));
    }

    [Fact]
    public void SelectingAKitCategory_ShowsOnlyThatKitsParts()
    {
        var tool = new PaletteTool();
        tool.SetPdkParts([KitItem("KitOne", "A"), KitItem("KitOne", "B"), KitItem("KitTwo", "C")]);

        tool.SelectedCategory = tool.Categories.First(c => c.DisplayName == "KitOne");

        Assert.Equal(["A", "B"], tool.DisplayedItems.Select(i => i.Item.DisplayName));
    }

    [Fact]
    public void AllCategory_ShowsKitPartsAlongsideTheBuiltIns()
    {
        var tool = new PaletteTool();
        int builtIns = tool.DisplayedItems.Count;

        tool.SetPdkParts([KitItem("KitOne", "A")]);

        Assert.Equal(builtIns + 1, tool.DisplayedItems.Count);
        Assert.Contains(tool.DisplayedItems, i => i.Item.DisplayName == "A");
    }

    [Fact]
    public void CommonAndRecentlyUsed_StayBuiltInOnly()
    {
        var tool = new PaletteTool();
        tool.SetPdkParts([KitItem("KitOne", "A")]);

        tool.SelectedCategory = tool.Categories.First(c => c.DisplayName == "Common");

        Assert.DoesNotContain(tool.DisplayedItems, i => i.Item.Pdk is not null);
    }

    [Fact]
    public void Search_FindsAKitPartByItsOwnNameAndByItsKitName()
    {
        var tool = new PaletteTool();
        tool.SetPdkParts([KitItem("KitOne", "WidgetX")]);

        tool.SearchQuery = "widgetx";
        Assert.Contains(tool.DisplayedItems, i => i.Item.DisplayName == "WidgetX");

        tool.SearchQuery = "kitone";
        Assert.Contains(tool.DisplayedItems, i => i.Item.DisplayName == "WidgetX");
    }

    [Fact]
    public void SelectedKitCategory_SurvivesAReimportOfThatKit()
    {
        var tool = new PaletteTool();
        tool.SetPdkParts([KitItem("KitOne", "A")]);
        tool.SelectedCategory = tool.Categories.First(c => c.DisplayName == "KitOne");

        tool.SetPdkParts([KitItem("KitOne", "A"), KitItem("KitOne", "B")]);

        Assert.Equal("KitOne", tool.SelectedCategory.DisplayName);
        Assert.Equal(2, tool.DisplayedItems.Count);
    }

    [Fact]
    public void RemovingAKitWhileItIsSelected_FallsBackToAll_RatherThanShowingNothing()
    {
        var tool = new PaletteTool();
        tool.SetPdkParts([KitItem("KitOne", "A")]);
        tool.SelectedCategory = tool.Categories.First(c => c.DisplayName == "KitOne");

        tool.SetPdkParts([]);

        Assert.Equal("All", tool.SelectedCategory.DisplayName);
        Assert.NotEmpty(tool.DisplayedItems);
    }

    // ── Arming ────────────────────────────────────────────────────────────────

    [Fact]
    public void ArmingOneKitPart_HighlightsOnlyThatTile_NotEveryKitPart()
    {
        // Every kit part shares one SymbolKind, so an identity check on kind alone would light
        // them all up at once. This is the test that would catch that.
        var svc  = new PlacementService();
        var tool = new PaletteTool();
        tool.SetPlacementService(svc);
        tool.SetPdkParts([KitItem("KitOne", "A"), KitItem("KitOne", "B")]);
        tool.SelectedCategory = tool.Categories.First(c => c.DisplayName == "KitOne");

        tool.DisplayedItems.First(i => i.Item.DisplayName == "A").ArmCommand.Execute(null);

        Assert.True(tool.DisplayedItems.First(i => i.Item.DisplayName == "A").IsArmed);
        Assert.False(tool.DisplayedItems.First(i => i.Item.DisplayName == "B").IsArmed);
    }

    [Fact]
    public void ArmingABuiltIn_IsUnaffectedByKitPartsBeingPresent()
    {
        var svc  = new PlacementService();
        var tool = new PaletteTool();
        tool.SetPlacementService(svc);
        tool.SetPdkParts([KitItem("KitOne", "A")]);

        svc.Toggle(SymbolKind.Resistor, 0);

        Assert.Equal(SymbolKind.Resistor, svc.Pending!.Kind);
        Assert.Null(svc.Pending.Pdk);
        Assert.DoesNotContain(tool.DisplayedItems, i => i.Item.Pdk is not null && i.IsArmed);
    }
}

public sealed class PdkPlacementServiceTests
{
    private static PaletteItem KitItem(string kit, string id) =>
        new(SymbolKind.Generic, 0, id, ComponentCategory.Other, [id], false, null, new PdkPartRef(kit, id));

    [Fact]
    public void TogglingTheSameKitPartTwice_Disarms()
    {
        var svc = new PlacementService();
        var a   = KitItem("K", "A");

        svc.Toggle(a);
        Assert.NotNull(svc.Pending?.Pdk);

        svc.Toggle(a);
        Assert.Null(svc.Pending);
    }

    [Fact]
    public void TogglingADifferentKitPart_Switches_RatherThanDisarming()
    {
        var svc = new PlacementService();

        svc.Toggle(KitItem("K", "A"));
        svc.Toggle(KitItem("K", "B"));

        Assert.Equal("B", svc.Pending!.Pdk!.PartId);
    }

    [Fact]
    public void PartsWithTheSameIdInDifferentKits_AreDistinctPlacements()
    {
        var svc = new PlacementService();

        svc.Toggle(KitItem("KitOne", "A"));
        svc.Toggle(KitItem("KitTwo", "A"));

        Assert.Equal("KitTwo", svc.Pending!.Pdk!.KitName);
    }

    [Fact]
    public void TogglingABuiltInItem_TakesTheOrdinaryPath_AndCarriesNoPartRef()
    {
        var svc = new PlacementService();

        svc.Toggle(new PaletteItem(SymbolKind.Capacitor, 0, "C", ComponentCategory.Lumped, ["C"], true));

        Assert.Equal(SymbolKind.Capacitor, svc.Pending!.Kind);
        Assert.Null(svc.Pending.Pdk);
    }

    [Fact]
    public void RotatingAnArmedKitPart_KeepsItsPartRef()
    {
        var svc = new PlacementService();
        svc.Toggle(KitItem("K", "A"));

        svc.Rotate(clockwise: false);

        Assert.Equal(SymbolRotation.R90, svc.Pending!.Rotation);
        Assert.Equal("A", svc.Pending.Pdk!.PartId);
    }
}

// ── Drag-and-drop carries the same identity as clicking ───────────────────────

public sealed class PdkDragPayloadTests
{
    [Fact]
    public void AKitPartsPayload_CarriesItsCellFolder_SoADropPlacesWhatAClickWould()
    {
        // Without this the drop sees only the placeholder SymbolKind every kit part shares and
        // places a generic component — the two entry points silently disagreeing about one tile.
        var wire = new PaletteDragPayload(SymbolKind.Generic, 0, "/tmp/ws/pdk/Kit/PART").Serialize();

        Assert.True(PaletteDragPayload.TryParse(wire, out var back));
        Assert.Equal("/tmp/ws/pdk/Kit/PART", back.CellDir);
    }

    [Fact]
    public void ABuiltInPayload_CarriesNoCellFolder_AndRoundTripsUnchanged()
    {
        var wire = new PaletteDragPayload(SymbolKind.Resistor, 0).Serialize();

        Assert.True(PaletteDragPayload.TryParse(wire, out var back));
        Assert.Equal(SymbolKind.Resistor, back.Kind);
        Assert.Null(back.CellDir);
    }

    [Fact]
    public void APathContainingASeparator_SurvivesTheRoundTrip()
    {
        // Windows paths carry a drive colon; everything after the port count is the path.
        var wire = new PaletteDragPayload(SymbolKind.Generic, 0, @"C:\ws\pdk\Kit\PART").Serialize();

        Assert.True(PaletteDragPayload.TryParse(wire, out var back));
        Assert.Equal(@"C:\ws\pdk\Kit\PART", back.CellDir);
    }

    [Fact]
    public void ForeignText_IsStillRefused()
    {
        Assert.False(PaletteDragPayload.TryParse("some other app's drag", out _));
        Assert.False(PaletteDragPayload.TryParse(null, out _));
    }
}

public sealed class PdkTooltipTests
{
    [Fact]
    public void AKitPartsTooltip_NamesTheKit_NotItsCatchAllCategory()
    {
        var tool = new PaletteTool();
        tool.SetPdkParts([new PaletteItem(SymbolKind.Generic, 0, "A", ComponentCategory.Other,
                                          ["A"], false, null, new PdkPartRef("MyKit", "A"))]);
        tool.SelectedCategory = tool.Categories.First(c => c.DisplayName == "MyKit");

        Assert.Equal("MyKit", Assert.Single(tool.DisplayedItems).CategoryLabel);
    }

    [Fact]
    public void ABuiltInTooltip_StillShowsItsCategory()
    {
        var tool = new PaletteTool();

        Assert.Contains(tool.DisplayedItems, i => i.CategoryLabel == nameof(ComponentCategory.Lumped));
    }
}
