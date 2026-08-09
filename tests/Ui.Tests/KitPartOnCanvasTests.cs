using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.ViewModels.Dock;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Getting an imported kit's part from the palette onto a schematic — the path a user takes and the
/// two places along it where a VIRTUAL reference used to be mistaken for a path.
///
/// <para>Fixtures name no vendor and no part: a kit name and a part id are strings that arrived at
/// run time, so a synthetic one exercises exactly the code a kit does.</para>
/// </summary>
[Collection(PdkToolsDirectoryCollection.Name)]
public sealed class KitPartOnCanvasTests : IDisposable
{
    private const string Kit  = "SampleKit";
    private const string Part = "PART_A";

    public KitPartOnCanvasTests()
    {
        PdkKitRegistry.Clear();
        PdkKitRegistry.SetKit(Kit, [MakePart(Part)]);
    }

    public void Dispose() => PdkKitRegistry.Clear();

    /// <summary>A part with artwork of its own, so a resolved symbol is distinguishable from none.</summary>
    private static PdkKitPart MakePart(string id)
    {
        var sym = new Symbol(
            primitives: [new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal, -100, 0, 100, 0)],
            pins:       [new SymbolPin(-200, 0, 1, "a"), new SymbolPin(200, 0, 2, "b")],
            portCount:  2);
        return new PdkKitPart(id, sym, new CcellFile { NumPorts = 2 }, IconPath: null);
    }

    private static string Ref => PdkKitRegistry.RefFor(Kit, Part);

    // ── the resolver's own question ───────────────────────────────────────────

    /// <summary>
    /// A kit reference resolves on its own; an ordinary cell reference is a path and needs a base
    /// directory. Asking the resolver rather than re-deriving the list at each call site is the
    /// whole point — the exemption used to be spelled out twice and a kit part fell through both.
    /// </summary>
    [Fact]
    public void C1_AKitReferenceNeedsNoBaseDirectory_AnOrdinaryOneDoes()
    {
        Assert.True(CellSymbolResolver.NeedsNoBaseDirectory(Ref));
        Assert.False(CellSymbolResolver.NeedsNoBaseDirectory("../../cells/SomeCell"));
    }

    /// <summary>
    /// The palette tile and the drag ghost both carry one field that is EITHER a virtual reference
    /// or an absolute cell folder. Splitting it into directory + name is right for a folder and
    /// destroys a virtual reference — which is why every part of an imported kit used to fall back
    /// to the generic placeholder glyph in the palette.
    /// </summary>
    [Fact]
    public void C2_AVirtualReferenceSurvivesTheGlyphAndGhostResolver()
    {
        var res = CellSymbolResolver.ResolveCellDirOrRef(Ref);

        Assert.Equal(CellSymbolState.Resolved, res.State);
        Assert.NotEmpty(res.Symbol!.Primitives);
        Assert.Equal(2, res.Symbol.Pins.Count);
    }

    [Fact]
    public void C3_AnUnloadedKitIsNotFound_RatherThanReportedAsABadPath()
    {
        PdkKitRegistry.Clear();

        Assert.Equal(CellSymbolState.NotFound,
                     CellSymbolResolver.ResolveCellDirOrRef(Ref).State);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void C4_NothingToResolveIsNotFound_NotAThrow(string? cellDir)
        => Assert.Equal(CellSymbolState.NotFound,
                        CellSymbolResolver.ResolveCellDirOrRef(cellDir).State);

    // ── the canvas ────────────────────────────────────────────────────────────

    /// <summary>
    /// The headline: a kit part dropped on an UNSAVED schematic draws its real pins.
    ///
    /// <para>Resolution used to be skipped whenever the schematic had no directory — correct for a
    /// path, wrong for a reference that needs none. The part placed, and then rendered as a pin-less
    /// placeholder: from the user's side, dragging one out of the palette did nothing at all.</para>
    /// </summary>
    [Fact]
    public async Task C5_AKitPartDroppedOnAnUnsavedSchematic_DrawsItsRealPins()
    {
        var model = new SchematicEditModel();
        Assert.Null(model.SchematicDirectory);   // never saved — the case that was broken

        var vm = new SchematicViewModel(model);
        await vm.CommitCellPlacementAsync(Ref, 0, 0, SymbolRotation.R0);

        var placed = Assert.Single(model.Components);
        Assert.Equal(Ref, placed.CellRef);

        var rendered = Assert.Single(vm.RenderModel!.Components);
        Assert.Equal(CellSymbolState.Resolved, rendered.CellRefState);
        Assert.NotNull(rendered.CellRefPrimitives);
        Assert.NotEmpty(rendered.CellRefPrimitives!);
    }

    /// <summary>
    /// Clicking a tile and dragging one must place the same thing. The drag carries the reference
    /// over the platform pasteboard as text, so the round trip is asserted rather than assumed.
    /// </summary>
    [Fact]
    public void C6_TheDragPayloadCarriesTheKitReferenceIntact()
    {
        var payload = new PaletteDragPayload(SymbolKind.Generic, 0, Ref, PCellGeneratorId: null);

        Assert.True(PaletteDragPayload.TryParse(payload.Serialize(), out var back));
        Assert.Equal(Ref, back.CellDir);
        Assert.True(PdkKitRegistry.IsKitRef(back.CellDir));
    }

    // ── the palette's own grouping ────────────────────────────────────────────

    /// <summary>
    /// A kit with a hundred parts is unbrowsable as one flat list. Its OWN groupings are listed
    /// directly beneath it, and the kit's own entry still shows everything.
    /// </summary>
    [Fact]
    public void C7_AKitsOwnGroupingsAreOfferedBeneathIt()
    {
        var tool = new PaletteTool();
        tool.SetPdkParts([
            Item("D1", "diode"),
            Item("D2", "diode"),
            Item("R1", "res"),
        ]);

        var kitEntry = tool.Categories.Single(c => c.DisplayName == Kit);
        int kitAt    = tool.Categories.ToList().IndexOf(kitEntry);

        var subs = tool.Categories.Skip(kitAt + 1).Select(c => c.DisplayName.Trim()).ToList();
        Assert.Equal(["diode", "res"], subs);

        // The kit itself shows every part it offers.
        tool.SelectedCategory = kitEntry;
        Assert.Equal(3, tool.DisplayedItems.Count);

        // One of its groupings narrows to that grouping alone.
        tool.SelectedCategory = tool.Categories.First(c => c.DisplayName.Trim() == "diode");
        Assert.Equal(["D1", "D2"], tool.DisplayedItems.Select(i => i.Item.DisplayName));
    }

    /// <summary>
    /// A single grouping would just repeat the kit entry above it in different words, and every
    /// part is already reachable from the kit itself.
    /// </summary>
    [Fact]
    public void C8_ASingleGroupingIsNotOfferedAsItsOwnEntry()
    {
        var tool = new PaletteTool();
        tool.SetPdkParts([Item("D1", "diode"), Item("D2", "diode")]);

        Assert.Single(tool.Categories, c => c.DisplayName.Trim() == Kit);
        Assert.DoesNotContain(tool.Categories, c => c.DisplayName.Trim() == "diode");
    }

    private static PaletteItem Item(string id, string category) => new(
        Kind:            SymbolKind.Generic,
        PortCount:       0,
        DisplayName:     id,
        Category:        ComponentCategory.Other,
        SearchTerms:     [id, Kit, category],
        IsCommon:        false,
        ExtraCategories: null,
        Pdk:             new PdkPartRef(Kit, id, null, PdkKitRegistry.RefFor(Kit, id), category));
}
