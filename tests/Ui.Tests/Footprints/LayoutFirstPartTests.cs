using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CircuitRF.Core.Design;
using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests.Footprints;

/// <summary>
/// brief-footprint-6-layout-first-parts.md §6 — <b>a part dropped into a layout is a part, not a
/// piece of copper</b>.
///
/// <para>Three things, each useful on its own: the reverse direction SAYS what it skipped and still
/// creates nothing for a bare land pattern (R-fp6-1); the palette drop places a PART — shared
/// land-pattern artwork plus an identity on the placement (R-fp6-2); Update Schematic from Layout
/// creates the component at the name the board already draws (R-fp6-3). And under all three, one
/// designator pool across the cell's two primary views (R-fp6-4), without which back-annotation can
/// be forced to rename a part that is already silkscreened onto copper.</para>
/// </summary>
public sealed class LayoutFirstPartTests : IDisposable
{
    private readonly string _root;

    public LayoutFirstPartTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-fp6-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ } }

    // ══ 1. The cursor says no at HEAD and yes after ═════════════════════════════════════════════

    [Fact]
    public void ADiscreteRlcIsDroppableOnABoardAndRefusedEverywhereElse()
    {
        var cell = Cell("Board1");

        // HEAD's own predicate — the reason the cursor used to say no. Stated rather than described,
        // because "false before" is otherwise unassertable once the change is in (R-fp6-2a).
        Assert.False(SchematicToLayoutGenerator.HasPCellGenerator(SymbolKind.Resistor, 2, out _));

        Assert.True(cell.Vm.CanDropPaletteComponent(SymbolKind.Resistor, 2));
        Assert.True(cell.Vm.CanDropPaletteComponent(SymbolKind.Capacitor, 2));

        // Still the allow-list's own set, called and not restated: a three-element branch is a network
        // someone builds, not a part someone buys.
        Assert.False(cell.Vm.CanDropPaletteComponent(SymbolKind.Srlc, 2));
        Assert.False(cell.Vm.CanDropPaletteComponent(SymbolKind.Term, 1));

        // R-fp6-2b: null from FootprintDefaults.For is a REFUSAL, not a fallback — an MMIC die design
        // does not silently sprout chip resistors.
        var die = Cell("Die1", "mmic-GaAs_2LM_100um");
        Assert.False(LandPatternLayers.IsBoardTechnology(die.Tech));
        Assert.False(die.Vm.CanDropPaletteComponent(SymbolKind.Resistor, 2));

        // And a microstrip, which was always droppable, still is — through its own generator.
        Assert.True(cell.Vm.CanDropPaletteComponent(SymbolKind.Mlin, 2));
    }

    // ══ 2. A dropped resistor is named, and says what it is ═════════════════════════════════════

    [Fact]
    public void DroppedPartsAreNamedFromTheRegistryPrefixAndRecordTheirKind()
    {
        var cell = Cell("Board2");
        DropThree(cell);

        Assert.Equal(["R1", "R2", "C1"], cell.Vm.Model.Instances.Select(i => i.DisplayRefDes));

        // R-fp6-2d/2f: the identity is on the PLACEMENT, and the placement corresponds to no schematic
        // component yet.
        Assert.Equal(["Resistor", "Resistor", "Capacitor"],
                     cell.Vm.Model.Instances.Select(i => i.PartKind));
        Assert.All(cell.Vm.Model.Instances, i => Assert.Null(i.SchematicId));
        Assert.All(cell.Vm.Model.Instances, i => Assert.Equal(i.RefDes, i.DisplayRefDes));
    }

    // ══ 3. One cell per CASE SIZE, not one per component ════════════════════════════════════════

    [Fact]
    public void TwoResistorsAndACapacitorShareOneLandPatternCell()
    {
        var cell = Cell("Board3");
        DropThree(cell);

        // R-fp6-2c: nothing here mints a per-component cell. The artwork of an 0201 resistor and an
        // 0201 capacitor is identical, and the store is content-addressed on generator + parameters +
        // technology — which is what keeps re-pointing, DRC, flatten and every export working with no
        // changes at all.
        var folders = cell.Vm.Model.Instances
            .Select(i => CellLayoutResolver.Resolve(i.CellRef, cell.LayoutDir).ResolvedCellDir)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.Single(folders);

        // And it is the SAME folder a schematic-driven Update Layout produces for the same case — the
        // identical GeneratedCellStore call SchematicToLayoutGenerator makes (R-fp6-2c).
        string fromSchematic = GeneratedCellStore.GetOrCreate(
            _root, FootprintDefaults.For(SymbolKind.Resistor, cell.Tech)!, new Dictionary<string, PCellValue>(),
            cell.Tech, cell.TechPath, PCellLayerSelection.Default);
        Assert.Equal(Path.GetFullPath(fromSchematic), Path.GetFullPath(folders[0]!), ignoreCase: true);

        // Two densities of one case are two cells, so "per case size" is not "per anything else".
        Assert.NotEqual(
            fromSchematic,
            GeneratedCellStore.GetOrCreate(_root, "smt:0805@N", new Dictionary<string, PCellValue>(),
                                           cell.Tech, cell.TechPath, PCellLayerSelection.Default));
    }

    // ══ 4. The Footprint tool is unchanged ══════════════════════════════════════════════════════

    [Fact]
    public void ALandPatternPlacedByHandStillGetsNoDesignatorAndNoPartKind()
    {
        var cell = Cell("Board4");
        Assert.True(cell.Vm.BeginFootprintPlacement(FootprintRef.For(SmtCaseTable.Find("0402")!)));
        Assert.True(cell.Vm.PlacePCell("smt:0402@N", new Dictionary<string, PCellValue>(), 0, 0));

        // R-fp6-1a, guarding §1d: an instance corresponding to no schematic component must not be
        // given a fabricated identity — and a generated land-pattern cell declares no Reference prefix,
        // so there is nothing to seed from and nothing is invented.
        var placed = Assert.Single(cell.Vm.Model.Instances);
        Assert.Null(placed.RefDes);
        Assert.Null(placed.PartKind);
        Assert.Null(placed.DisplayRefDes);
    }

    // ══ 5. Skipped, and SAID ════════════════════════════════════════════════════════════════════

    [Fact]
    public void ThreeHandPlacedLandPatternsCreateNothingAndAreReportedAsOneInfoLine()
    {
        var cell = Cell("Board5");
        for (int i = 0; i < 3; i++)
            Assert.True(cell.Vm.PlacePCell("smt:0402@N", new Dictionary<string, PCellValue>(), i * 2_000_000, 0));

        var result = Run(cell);

        Assert.Equal(0, result.CreatedCount);
        Assert.True(result.NothingChanged);

        // R-fp6-1b/1c: ONE aggregate line, at Info, naming the count and the remedy. Aggregate because
        // a hand-authored board can hold a hundred of these; Info because nothing is wrong — the user
        // placed artwork and got artwork.
        var line = Assert.Single(result.Lines);
        Assert.Equal(SchematicToLayoutGenerator.ReportSeverity.Info, line.Severity);
        Assert.Contains("3 placements", line.Text, StringComparison.Ordinal);
        Assert.Contains("Library palette", line.Text, StringComparison.Ordinal);

        // And it is RUN-level — the empty instance name is what gets it past the caller's
        // "nothing changed, say nothing" early return. Without this the line is assembled here and
        // then dropped on the floor in exactly the case it exists for.
        Assert.Equal("", line.InstanceName);
    }

    // ══ 6. Created at its own name, with its own artwork ════════════════════════════════════════

    [Fact]
    public void DroppedPartsBackAnnotateToComponentsAtTheNameTheBoardAlreadyDraws()
    {
        var cell = Cell("Board6");
        DropThree(cell);

        var result = Run(cell);
        Assert.Equal(3, result.CreatedCount);
        result.Command!.Execute();

        Assert.Equal(["R1", "R2", "C1"], cell.Schematic.Components.Select(c => c.InstanceName));
        Assert.Equal([SymbolKind.Resistor, SymbolKind.Resistor, SymbolKind.Capacitor],
                     cell.Schematic.Components.Select(c => c.Symbol));

        // R-fp6-3b: the registry default, and the user sets the value in the schematic.
        Assert.All(cell.Schematic.Components, c => Assert.Contains(c.Parameters, p => p.Name == "R" || p.Name == "C"));

        // R-fp6-3c: it is given the artwork it already has. Without this the very next Update Layout
        // from Schematic would report the brand-new component as having no artwork — while its artwork
        // sits on the board.
        Assert.All(cell.Schematic.Components, c =>
            Assert.Equal(FootprintDefaults.For(SymbolKind.Resistor, cell.Tech),
                         c.Parameters.Single(p => p.Name == ArtworkParameters.FootprintName).Expression));
    }

    // ══ 7. Linking TRANSFERS the name; it does not copy it ══════════════════════════════════════

    [Fact]
    public void AfterLinkingThePlacementStoresNoDesignatorOfItsOwnAndDrawsTheSameString()
    {
        var cell = Cell("Board7");
        DropThree(cell);
        var before = cell.Vm.Model.Instances.Select(i => i.DisplayRefDes).ToList();

        Run(cell).Command!.Execute();

        // R-fp6-3d: two fields with one meaning drift, so the instance keeps neither. DisplayRefDes
        // then reads the schematic side, and nothing on the board changes appearance.
        Assert.All(cell.Vm.Model.Instances, i =>
        {
            Assert.NotNull(i.SchematicId);
            Assert.Null(i.RefDes);
            Assert.Null(i.PartKind);
        });
        Assert.Equal(before, cell.Vm.Model.Instances.Select(i => i.DisplayRefDes));
    }

    // ══ 8. Idempotent ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void RunningItASecondTimeCreatesNothing()
    {
        var cell = Cell("Board8");
        DropThree(cell);
        Run(cell).Command!.Execute();

        var again = Run(cell);
        Assert.True(again.NothingChanged);
        Assert.Equal(0, again.CreatedCount);
        Assert.Equal(3, again.UnchangedCount);
        Assert.Equal(3, cell.Schematic.Components.Count);
    }

    // ══ 9. No wires, and it says so ═════════════════════════════════════════════════════════════

    [Fact]
    public void TheGeneratedSchematicHasNoWiresAndTheClosingLineStatesIt()
    {
        var cell = Cell("Board9");
        DropThree(cell);

        var result = Run(cell);
        result.Command!.Execute();

        Assert.Empty(cell.Schematic.Wires);

        // R-fp6-3f: a BOM round trip, not yet a design flow — and the user is told rather than left to
        // discover it. Deriving nets from copper belongs to brief-authored-board-2.
        var closing = result.Lines[^1];
        Assert.Equal(SchematicToLayoutGenerator.ReportSeverity.Info, closing.Severity);
        Assert.Equal("", closing.InstanceName);
        Assert.Contains("3 components", closing.Text, StringComparison.Ordinal);
        Assert.Contains("No nets were derived", closing.Text, StringComparison.Ordinal);
    }

    // ══ 10. Cross-view collision, both directions ═══════════════════════════════════════════════

    [Fact]
    public void ASchematicComponentTakesTheNameOutOfTheLayoutsReach()
    {
        var cell = Cell("Board10a");
        var onDisk = new SchematicEditModel();
        onDisk.Components.Add(new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor });
        SaveSchematic(cell, onDisk);

        Assert.True(cell.Vm.CommitPaletteDrop(SymbolKind.Resistor, 2, 0, 0));

        // Without R-fp6-4a this is R1, and Update Layout from Schematic later places the real R1
        // beside it — two parts, one designator, on a board that no longer matches its BOM.
        Assert.Equal("R2", Assert.Single(cell.Vm.Model.Instances).DisplayRefDes);
    }

    [Fact]
    public void AHandPlacedBoardPartTakesTheNameOutOfTheSchematicsReach()
    {
        var cell = Cell("Board10b");
        Assert.True(cell.Vm.CommitPaletteDrop(SymbolKind.Resistor, 2, 0, 0));
        SaveLayout(cell);

        var schematicVm = SchematicVm(cell);
        schematicVm.CommitPlacement(SymbolKind.Resistor, 2, SymbolRotation.R0, 0, 0);

        // The mirror image of the case above: scanning only schematic.Components is how the schematic
        // took a name the board was already silkscreened with.
        Assert.Equal("R2", Assert.Single(schematicVm.EditModel.Components).InstanceName);
    }

    // ══ 11. A non-primary view does not contribute ══════════════════════════════════════════════

    [Fact]
    public void ANonPrimaryLayoutViewIsNotOnTheBoardAndIsNotInThePool()
    {
        var cell = Cell("Board11");

        // The PRIMARY layout holds R1 and nothing else; a second, NON-primary one is full of R2…R9.
        // A variant land pattern is not on the board (R-fp6-4e), and an instance draws its cell's
        // primary view regardless.
        cell.Vm.Model.Instances.Add(new LayoutInstance { CellRef = "x", Mag = 1.0, RefDes = "R1" });
        SaveLayout(cell);

        var variant = new LayoutView { DbuPerMicron = LayoutUnits.DefaultDbuPerMicron, SnapDbu = 1000 };
        for (int i = 2; i <= 9; i++)
            variant.Instances.Add(new LayoutInstance { CellRef = "x", Mag = 1.0, RefDes = "R" + i });
        LayoutPersistence.SaveToFile(Path.Combine(cell.LayoutDir, "variant.clay"), variant);
        SetPrimary(cell, layout: Path.GetFileName(cell.ClayPath));

        var schematicVm = SchematicVm(cell);
        schematicVm.CommitPlacement(SymbolKind.Resistor, 2, SymbolRotation.R0, 0, 0);

        // R2, not R1 — so the PRIMARY was read; and not R10 — so the variant was not. Asserting only
        // the second half would pass just as well if nothing were read at all.
        Assert.Equal("R2", Assert.Single(schematicVm.EditModel.Components).InstanceName);
    }

    // ══ 12. One-view cells are untouched ════════════════════════════════════════════════════════

    [Fact]
    public void ACellWithNoLayoutProducesExactlyTheNameSequenceItAlwaysDid()
    {
        var cell = Cell("Board12");
        Directory.Delete(cell.LayoutDir, recursive: true);

        var schematicVm = SchematicVm(cell);
        schematicVm.CommitPlacement(SymbolKind.Resistor,  2, SymbolRotation.R0, 0, 0);
        schematicVm.CommitPlacement(SymbolKind.Resistor,  2, SymbolRotation.R0, 200, 0);
        schematicVm.CommitPlacement(SymbolKind.Capacitor, 2, SymbolRotation.R0, 400, 0);

        // R-fp6-4f: the absent side contributes an empty set, which is what keeps this from being a
        // change to every schematic that has no layout.
        Assert.Equal(["R1", "R2", "C1"], schematicVm.EditModel.Components.Select(c => c.InstanceName));
    }

    // ══ 13. Additive — a .clay written before this brief re-saves byte for byte ═════════════════

    [Fact]
    public void AClayWithNoPartKindLoadsAndReSavesByteForByte()
    {
        var cell = Cell("Board13");
        Assert.True(cell.Vm.PlacePCell("smt:0402@N", new Dictionary<string, PCellValue>(), 0, 0));
        string path = Path.Combine(cell.LayoutDir, "Board13.clay");
        LayoutPersistence.SaveToFile(path, cell.Vm.Model);

        byte[] first = File.ReadAllBytes(path);
        LayoutPersistence.SaveToFile(path, LayoutPersistence.LoadFromFile(path));

        Assert.Equal(first, File.ReadAllBytes(path));

        string without = Encoding.UTF8.GetString(first);
        Assert.DoesNotContain("PartKind", without, StringComparison.Ordinal);

        // And a part DOES write it — so the absence above is a property of the instance, not of the
        // writer — at the SAME FormatVersion (R-fp6-2d: additive, no bump).
        Assert.True(cell.Vm.CommitPaletteDrop(SymbolKind.Resistor, 2, 4_000_000, 0));
        LayoutPersistence.SaveToFile(path, cell.Vm.Model);
        string with = File.ReadAllText(path);

        Assert.Contains("\"PartKind\": \"Resistor\"", with, StringComparison.Ordinal);
        Assert.Equal(FormatVersionOf(without), FormatVersionOf(with));
        Assert.Equal(LayoutPersistence.CurrentFormatVersion, FormatVersionOf(with));
    }

    // ══ 14. The sibling is read ONCE ════════════════════════════════════════════════════════════

    [Fact]
    public void PlacingTwentyPartsReadsTheClosedSiblingExactlyOnce()
    {
        var cell = Cell("Board14");
        SaveSchematic(cell, new SchematicEditModel());

        for (int i = 0; i < 20; i++)
            Assert.True(cell.Vm.CommitPaletteDrop(SymbolKind.Resistor, 2, i * 2_000_000, 0));

        Assert.Equal(20, cell.Vm.Model.Instances.Count);

        // R-fp6-4c: placing twenty parts must not pay for the sibling twenty times. A COUNTER, not a
        // clock — the standing rule against timing tests.
        Assert.Equal(1, cell.Vm.SiblingDesignatorReads);
    }

    // ══ 15. Re-pointing a dropped part keeps it a PART ══════════════════════════════════════════

    [Fact]
    public void RePointingADroppedPartToAnotherCaseKeepsItsIdentityAndItStillBackAnnotates()
    {
        var cell = Cell("Board15");
        Assert.True(cell.Vm.CommitPaletteDrop(SymbolKind.Inductor, 2, 0, 0));
        Assert.Equal("L1", Assert.Single(cell.Vm.Model.Instances).DisplayRefDes);

        // The only route to "an inductor at the case size I actually want": drop the part, then
        // re-point it with the Footprint picker. That runs through LayoutGeometry.Clone — as does
        // every properties edit, every move drag and every paste — so a clone that dropped PartKind
        // would turn the part back into bare copper on an ordinary edit, and the ONLY trace would be
        // a designator with nothing behind it (reported from the field, 2026-09-21).
        Assert.True(cell.Vm.RetargetSelectedInstanceToFootprint(FootprintRef.For(SmtCaseTable.Find("0603")!)));

        var inst = Assert.Single(cell.Vm.Model.Instances);
        Assert.Equal("Inductor", inst.PartKind);
        Assert.Equal("L1", inst.DisplayRefDes);
        Assert.Equal("0603", cell.Vm.SelectedInstanceFootprint?.Case.Code);

        // And it still becomes the component it always was — at the case the user chose.
        var result = Run(cell);
        Assert.Equal(1, result.CreatedCount);
        result.Command!.Execute();

        var comp = Assert.Single(cell.Schematic.Components);
        Assert.Equal("L1", comp.InstanceName);
        Assert.Equal(SymbolKind.Inductor, comp.Symbol);
        Assert.Equal("smt:0603@N",
                     comp.Parameters.Single(p => p.Name == ArtworkParameters.FootprintName).Expression);
    }

    // ══ 16. The drop lands on the case this board is being built in ═════════════════════════════

    [Fact]
    public void ChoosingACaseOnceCarriesToThePartsDroppedAfterIt()
    {
        var cell = Cell("Board16");

        // Until a case is chosen, the fixed default — unchanged.
        Assert.True(cell.Vm.CommitPaletteDrop(SymbolKind.Inductor, 2, 0, 0));
        Assert.Equal("smt:0201@N", GeneratorOf(cell, 0));

        // Either Footprint-picker gesture is a choice of case. Re-pointing the part just dropped is
        // the one the field report arrived as — drop, then immediately re-point, every time.
        Assert.True(cell.Vm.RetargetSelectedInstanceToFootprint(FootprintRef.For(SmtCaseTable.Find("0603")!)));

        Assert.True(cell.Vm.CommitPaletteDrop(SymbolKind.Inductor,  2, 2_000_000, 0));
        Assert.True(cell.Vm.CommitPaletteDrop(SymbolKind.Capacitor, 2, 4_000_000, 0));
        Assert.Equal("smt:0603@N", GeneratorOf(cell, 1));
        Assert.Equal("smt:0603@N", GeneratorOf(cell, 2));

        // Still parts, still named from the registry prefix — the case is the only thing that moved.
        Assert.Equal(["L1", "L2", "C1"], cell.Vm.Model.Instances.Select(i => i.DisplayRefDes));
        Assert.Equal(["Inductor", "Inductor", "Capacitor"], cell.Vm.Model.Instances.Select(i => i.PartKind));

        // And it narrows nothing: the allow-list and the board-technology refusal are still
        // FootprintDefaults.For's answers, asked first (R-fp6-2a/2b).
        var die = Cell("Die16", "mmic-GaAs_2LM_100um");
        Assert.True(die.Vm.BeginFootprintPlacement(FootprintRef.For(SmtCaseTable.Find("0603")!)));
        Assert.False(die.Vm.CanDropPaletteComponent(SymbolKind.Inductor, 2));
        Assert.False(cell.Vm.CanDropPaletteComponent(SymbolKind.Srlc, 2));
    }

    // ── Fixture ─────────────────────────────────────────────────────────────────────────────────

    private sealed record CellFixture(
        LayoutEditorViewModel Vm, SchematicEditModel Schematic, Technology Tech, string TechPath,
        string CellDir, string LayoutDir, string SchematicDir, string ClayPath);

    /// <summary>A workspace, a technology, a cell folder with both view sub-folders, and a layout
    /// session on a board technology — the state a user dragging a resistor onto a board is in.</summary>
    private CellFixture Cell(string cellName, string techName = "pcb-2layer_FR-4_70mil_1oz")
    {
        var tech = ShippedTechnologies.Load(techName);
        Directory.CreateDirectory(Path.Combine(_root, "tech"));
        string techPath = Path.Combine(_root, "tech", techName + ".ctech");
        TechPersistence.SaveToFile(techPath, tech);
        WorkspacePersistence.SaveToFile(Path.Combine(_root, ".cws"),
            new CwsFile { DefaultTechRef = "tech/" + techName + ".ctech" });

        string cellDir      = CellFolder.CreateCellFolder(_root, cellName);
        string layoutDir    = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        string schematicDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        Directory.CreateDirectory(layoutDir);
        Directory.CreateDirectory(schematicDir);

        string clay = Path.Combine(layoutDir, cellName + ".clay");
        var model = new LayoutView { DbuPerMicron = LayoutUnits.DefaultDbuPerMicron, SnapDbu = 1000 };
        var vm = new LayoutEditorViewModel(model, clay);
        vm.ApplyTechResolution(new TechResolution(tech, techPath, TechResolutionSource.WorkspaceDefault, []));

        var schematic = new SchematicEditModel { SchematicDirectory = schematicDir };
        return new CellFixture(vm, schematic, tech, techPath, cellDir, layoutDir, schematicDir, clay);
    }

    /// <summary>Two resistors and a capacitor, dropped from the Library palette onto the board.</summary>
    private static void DropThree(CellFixture cell)
    {
        Assert.True(cell.Vm.CommitPaletteDrop(SymbolKind.Resistor,  2, 0, 0));
        Assert.True(cell.Vm.CommitPaletteDrop(SymbolKind.Resistor,  2, 2_000_000, 0));
        Assert.True(cell.Vm.CommitPaletteDrop(SymbolKind.Capacitor, 2, 4_000_000, 0));
    }

    private static LayoutToSchematicGenerator.GenerationResult Run(CellFixture cell)
        => LayoutToSchematicGenerator.Run(cell.Vm.Model, cell.Schematic, cell.LayoutDir, cell.Vm.Technology);

    /// <summary>The generator id behind instance <paramref name="index"/>'s artwork.</summary>
    private static string GeneratorOf(CellFixture cell, int index)
        => CellLayoutResolver.Resolve(cell.Vm.Model.Instances[index].CellRef, cell.LayoutDir)
               .View!.PCellOrigin!.GeneratorId;

    private static void SaveLayout(CellFixture cell)
        => LayoutPersistence.SaveToFile(cell.ClayPath, cell.Vm.Model);

    private static void SaveSchematic(CellFixture cell, SchematicEditModel model)
        => SchematicPersistence.SaveToFile(
            Path.Combine(cell.SchematicDir, Path.GetFileNameWithoutExtension(cell.ClayPath) + ".csch"),
            model, cellName: Path.GetFileName(cell.CellDir));

    /// <summary>A fresh schematic session on the cell's own schematic folder — so its name chooser can
    /// reach the cell's primary layout.</summary>
    private static SchematicViewModel SchematicVm(CellFixture cell)
        => new(new SchematicEditModel { SchematicDirectory = cell.SchematicDir });

    private static void SetPrimary(CellFixture cell, string layout)
    {
        string path = Path.Combine(cell.CellDir, CellFolder.CcellFileName);
        var ccell = CellPersistence.LoadFromFile(path);
        ccell.PrimaryLayout = layout;
        CellPersistence.SaveToFile(path, ccell);
    }

    private static int FormatVersionOf(string json)
    {
        var m = System.Text.RegularExpressions.Regex.Match(json, "\"FormatVersion\"\\s*:\\s*(\\d+)");
        Assert.True(m.Success, "a .clay states its FormatVersion");
        return int.Parse(m.Groups[1].Value);
    }
}
