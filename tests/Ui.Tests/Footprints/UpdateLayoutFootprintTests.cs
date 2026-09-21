using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests.Footprints;

/// <summary>
/// brief-footprint-3-update-layout.md §7 — the gate for the one branch in
/// <c>ResolveComponentLayout</c>, the pad-count contract, and the layout editor's own two footprint
/// gestures.
///
/// <para>The thing the designer actually asked for is test 1: <b>Update Layout from Schematic writes
/// artwork for an S2P, an SRLC and an ordinary capacitor</b>, because each of them now states a
/// footprint. At HEAD before this brief that test produced three <c>NoLayoutWarnings</c> and zero
/// instances.</para>
/// </summary>
public sealed class UpdateLayoutFootprintTests : IDisposable
{
    private readonly string _root;

    public UpdateLayoutFootprintTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-footprint3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ } }

    // ══ 1. The three the designer asked for ═════════════════════════════════════════════════════

    [Fact]
    public void AnS2pAnSrlcAndACapacitorAllPlaceTheirStatedLandPattern()
    {
        var (schematicDir, layoutDir, tech) = MakeCell("Board1");

        var model = new SchematicEditModel { SchematicDirectory = schematicDir };
        model.Components.Add(WithFootprint(Snp("S2P1", ports: 2), "smt:0402@N"));
        model.Components.Add(WithFootprint(Comp("X1", SymbolKind.Srlc), "smt:0603@N"));
        model.Components.Add(WithFootprint(Comp("C1", SymbolKind.Capacitor),
                                           FootprintDefaults.For(SymbolKind.Capacitor, tech)!));

        var target = new LayoutView();
        var result = Run(model, target, schematicDir, layoutDir, tech);

        Assert.Empty(result.NoLayoutWarnings);
        Assert.NotNull(result.Command);
        result.Command!.Execute();

        Assert.Equal(3, target.Instances.Count);
        Assert.Equal(3, result.AddedCount);

        // Two pads each, and the copper is the technology's TOP COPPER resolved by role — not a key
        // a shipped .clay guessed at (the series overview's §1b).
        var topCopper = LandPatternLayers.Resolve(tech, PCellLayerSelection.Default, new List<string>()).Copper;
        Assert.NotNull(topCopper);
        foreach (var inst in target.Instances)
        {
            var view = ResolvedView(inst, layoutDir);
            Assert.Equal(2, view.Pins.Count);
            Assert.All(view.Pins, p => Assert.Equal(topCopper!.Value, p.Layer));
            Assert.Contains(view.Shapes, s => s.Layer == topCopper!.Value);
        }

        // R-fp1-2b: one generated cell per (case, density) — the 0402 and the 0603 are two cells, and
        // the capacitor's 0201 default is a third.
        Assert.Equal(3, target.Instances.Select(i => i.CellRef).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // ══ 2. The pad-count contract, both rows ════════════════════════════════════════════════════

    [Fact]
    public void AnS4pOnATwoPadLandIsReportedWithBothCountsAndNotPlaced()
    {
        var (schematicDir, layoutDir, tech) = MakeCell("Board2");
        var model = new SchematicEditModel { SchematicDirectory = schematicDir };
        model.Components.Add(WithFootprint(Snp("S4P1", ports: 4), "smt:0402@N"));

        var target = new LayoutView();
        var result = Run(model, target, schematicDir, layoutDir, tech);

        Assert.Empty(target.Instances);
        Assert.Null(result.Command);

        string refusal = Assert.Single(result.NoLayoutWarnings);
        Assert.Contains("S4P1", refusal);
        Assert.Contains("0402", refusal);
        Assert.Contains("2 pads", refusal);
        Assert.Contains("4 ports", refusal);
        Assert.Contains("Nothing was placed", refusal);
    }

    /// <summary>
    /// R-fp3-5a — the one that will bite. <c>RefNode</c> generates a reference pin of its own, so a
    /// 2-port S2P is THREE terminals and does not fit a two-pad chip land. Reading
    /// <c>PortCount</c> here (which is 2) would place it and produce artwork one pad short, silently.
    /// </summary>
    [Fact]
    public void AnS2pWithRefNodeIsThreePortsAndDoesNotFitATwoPadLand()
    {
        var (schematicDir, layoutDir, tech) = MakeCell("Board3");
        var comp = WithFootprint(Snp("S2P1", ports: 2), "smt:0402@N");
        comp.Parameters.Add(new EditableParameter { Name = "RefNode", Expression = "true" });
        Assert.Equal(2, comp.PortCount);            // the file's own port count is unchanged…
        Assert.Equal(3, comp.EffectivePortCount);   // …and is not the number that matters here

        var model = new SchematicEditModel { SchematicDirectory = schematicDir };
        model.Components.Add(comp);

        var target = new LayoutView();
        var result = Run(model, target, schematicDir, layoutDir, tech);

        Assert.Empty(target.Instances);
        string refusal = Assert.Single(result.NoLayoutWarnings);
        Assert.Contains("2 pads", refusal);
        Assert.Contains("3 ports", refusal);
    }

    // ══ 3. Re-pointing and removing ═════════════════════════════════════════════════════════════

    [Fact]
    public void ChangingTheFootprintMovesTheExistingInstanceRatherThanAddingASecond()
    {
        var (schematicDir, layoutDir, tech) = MakeCell("Board4");
        var comp = WithFootprint(Comp("C1", SymbolKind.Capacitor), "smt:0402@N");
        var model = new SchematicEditModel { SchematicDirectory = schematicDir };
        model.Components.Add(comp);

        var target = new LayoutView();
        Run(model, target, schematicDir, layoutDir, tech).Command!.Execute();
        string firstCellRef = target.Instances[0].CellRef;

        comp.Parameters.First(p => p.Name == "Footprint").Expression = "smt:0603@N";
        var second = Run(model, target, schematicDir, layoutDir, tech);
        Assert.NotNull(second.Command);
        second.Command!.Execute();

        var instance = Assert.Single(target.Instances);
        Assert.NotEqual(firstCellRef, instance.CellRef);
        Assert.Equal("C1", instance.SchematicId);
        Assert.Equal(1, second.UpdatedCount);
        Assert.Equal(0, second.AddedCount);
        Assert.Contains(second.Lines, l => l.InstanceName == "C1" && l.Text.Contains("updated"));
    }

    /// <summary>
    /// R-fp3-4b. Leaving the artwork behind would mean the layout says a part is there and the
    /// schematic says it is not — the divergence this whole series exists to close.
    /// </summary>
    [Fact]
    public void SettingTheFootprintToNoneRemovesTheInstanceAndSaysSo()
    {
        var (schematicDir, layoutDir, tech) = MakeCell("Board5");
        var comp = WithFootprint(Comp("C1", SymbolKind.Capacitor), "smt:0402@N");
        var model = new SchematicEditModel { SchematicDirectory = schematicDir };
        model.Components.Add(comp);

        var target = new LayoutView();
        Run(model, target, schematicDir, layoutDir, tech).Command!.Execute();
        Assert.Single(target.Instances);

        comp.Parameters.RemoveAll(p => p.Name == "Footprint");   // None IS absence (R-fp2-1b)
        var second = Run(model, target, schematicDir, layoutDir, tech);
        Assert.NotNull(second.Command);
        second.Command!.Execute();

        Assert.Empty(target.Instances);
        Assert.Equal(1, second.DeletedCount);
        Assert.Contains(second.Lines, l => l.InstanceName == "C1" && l.Text.Contains("removed"));

        // One undoable action, and the artwork comes back with it (R-L5-12).
        second.Command!.Undo();
        Assert.Single(target.Instances);
    }

    // ══ 4. Placement is MEASURED, and SchematicId survives ══════════════════════════════════════

    /// <summary>
    /// R-fp3-3a. A generated land pattern HAS geometry, so <c>PlaceNewInstances</c> measures it and
    /// spaces by half the largest — thirteen 0402s land inside a couple of millimetres, not across
    /// 130 mm of board at the 10 mm fallback pitch.
    /// </summary>
    [Fact]
    public void ThirteenChipsLandOnAPitchProportionalToThePatternAndAllKeepTheirSchematicId()
    {
        var (schematicDir, layoutDir, tech) = MakeCell("Board6");
        var model = new SchematicEditModel { SchematicDirectory = schematicDir };
        for (int i = 1; i <= 13; i++)
            model.Components.Add(WithFootprint(Comp($"C{i}", SymbolKind.Capacitor), "smt:0402@N"));

        var target = new LayoutView();
        var result = Run(model, target, schematicDir, layoutDir, tech);
        result.Command!.Execute();

        Assert.Equal(13, target.Instances.Count);
        Assert.All(target.Instances, i => Assert.False(string.IsNullOrEmpty(i.SchematicId)));
        Assert.Equal(13, target.Instances.Select(i => i.SchematicId).Distinct(StringComparer.Ordinal).Count());

        var patternBox = CellHierarchy.InstanceBbox(target.Instances[0], layoutDir);
        long patternWidth = patternBox.MaxX - patternBox.MinX;
        Assert.True(patternWidth > 0);

        long spanX = target.Instances.Max(i => i.X) - target.Instances.Min(i => i.X);
        long spanY = target.Instances.Max(i => i.Y) - target.Instances.Min(i => i.Y);

        // Eight columns at a pitch of at most twice the pattern, and two rows of the same — bounded
        // by the pattern's own size rather than by a constant. The 10 mm fallback would put spanX at
        // 70 mm on its own, which is two orders of magnitude past this.
        Assert.True(spanX <= 8 * 2 * patternWidth, $"spanX {spanX} against a {patternWidth} pattern.");
        Assert.True(spanY <= 2 * 2 * patternWidth, $"spanY {spanY} against a {patternWidth} pattern.");
    }

    // ══ 5. The divergence report's second trigger ═══════════════════════════════════════════════

    /// <summary>
    /// R-fp3-2b. A schematic of capacitors on one technology and a layout on another was silent: the
    /// existing check fires only when the schematic holds a microstrip component, and thirteen
    /// capacitors hold none.
    /// </summary>
    [Fact]
    public void ASchematicOfCapacitorsOnOneTechnologyAndALayoutOnAnotherIsReported()
    {
        var model = new SchematicEditModel();
        model.Components.Add(WithFootprint(Comp("C1", SymbolKind.Capacitor), "smt:0402@N"));

        string twoLayer  = SaveTech("two",  ShippedTechnologies.Load("pcb-2layer_FR-4_70mil_1oz"));
        string fourLayer = SaveTech("four", ShippedTechnologies.Load("pcb-4layer_FR-4_62mil_1oz"));

        string warning = Assert.IsType<string>(TechnologyDivergenceReport.Describe(model, twoLayer, fourLayer));
        Assert.Contains("footprints", warning);
        Assert.Contains("two", warning);
        Assert.Contains("four", warning);

        // The same technology on both halves has nothing to say, and neither does a schematic with
        // no footprint and no microstrip in it.
        Assert.Null(TechnologyDivergenceReport.Describe(model, twoLayer, twoLayer));
        var bare = new SchematicEditModel();
        bare.Components.Add(Comp("C1", SymbolKind.Capacitor));
        Assert.Null(TechnologyDivergenceReport.Describe(bare, twoLayer, fourLayer));
    }

    // ══ 6. The layout editor's own two gestures ═════════════════════════════════════════════════

    /// <summary>
    /// R-fp3-6a (one undo, through the same <c>ReplaceSelectedInstance</c> path every other instance
    /// edit uses — this is what the Properties Inspector's Footprint combobox calls) and R-fp3-6c
    /// (a hand-placed instance carries no <c>SchematicId</c>, because it corresponds to no schematic
    /// component and v2's LVS should report it as unmatched, which is the truth).
    /// </summary>
    [Fact]
    public void PlacingAFootprintByHandCarriesNoSchematicIdAndRePointingItIsOneUndo()
    {
        var (_, layoutDir, _) = MakeCell("Board7");
        string clay = Path.Combine(layoutDir, "Board7.clay");
        LayoutPersistence.SaveToFile(clay, new LayoutView { DbuPerMicron = LayoutUnits.DefaultDbuPerMicron, SnapDbu = 1000 });

        var vm = new LayoutEditorViewModel(
            new LayoutView { DbuPerMicron = LayoutUnits.DefaultDbuPerMicron, SnapDbu = 1000 }, clay);

        var zero402 = FootprintRef.For(SmtCaseTable.Find("0402")!);
        Assert.True(vm.PlacePCell(zero402.ToString(), new Dictionary<string, PCellValue>(), 0, 0));

        var placed = Assert.Single(vm.Model.Instances);
        Assert.Null(placed.SchematicId);
        string before = placed.CellRef;
        Assert.Equal("0402", vm.SelectedInstanceFootprint?.Case.Code);

        Assert.True(vm.RetargetSelectedInstanceToFootprint(FootprintRef.For(SmtCaseTable.Find("0603")!)));
        Assert.Equal("0603", vm.SelectedInstanceFootprint?.Case.Code);
        Assert.Null(vm.Model.Instances[0].SchematicId);   // R-fp3-6d's half: nothing invents one

        // ONE undo takes the re-point back, and the placement is still there behind it.
        vm.UndoRedo.Undo();
        Assert.Equal(before, vm.Model.Instances[0].CellRef);
        Assert.Single(vm.Model.Instances);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private (string SchematicDir, string LayoutDir, Technology Tech) MakeCell(string cellName)
    {
        var tech = ShippedTechnologies.Load("pcb-2layer_FR-4_70mil_1oz");
        SaveTech("t", tech);
        WorkspacePersistence.SaveToFile(Path.Combine(_root, ".cws"), new CwsFile { DefaultTechRef = "tech/t.ctech" });

        string cellDir = Path.Combine(_root, cellName);
        CellFolder.CreateCellFolder(_root, cellName);
        return (CellFolder.SubFolderPath(cellDir, ViewType.Schematic),
                CellFolder.SubFolderPath(cellDir, ViewType.Layout),
                tech);
    }

    private string SaveTech(string name, Technology tech)
    {
        Directory.CreateDirectory(Path.Combine(_root, "tech"));
        string path = Path.Combine(_root, "tech", name + ".ctech");
        TechPersistence.SaveToFile(path, tech);
        return path;
    }

    private SchematicToLayoutGenerator.GenerationResult Run(
        SchematicEditModel model, LayoutView target, string schematicDir, string layoutDir, Technology tech)
        => SchematicToLayoutGenerator.Run(
            model, target, schematicDir, _root, layoutDir, tech,
            Path.Combine(_root, "tech", "t.ctech"), cellResolver: null);

    private static LayoutView ResolvedView(LayoutInstance inst, string layoutDir)
    {
        var res = CellLayoutResolver.Resolve(inst.CellRef, layoutDir);
        Assert.Equal(CellLayoutState.Resolved, res.State);
        return res.View!;
    }

    private static EditableComponent Comp(string name, SymbolKind kind)
        => new() { InstanceName = name, Symbol = kind };

    private static EditableComponent Snp(string name, int ports)
    {
        var c = Comp(name, SymbolKind.Snp);
        c.Parameters.Add(new EditableParameter { Name = "NumPorts", Expression = ports.ToString() });
        return c;
    }

    private static EditableComponent WithFootprint(EditableComponent c, string value)
    {
        c.Parameters.Add(new EditableParameter { Name = "Footprint", Expression = value });
        return c;
    }
}
