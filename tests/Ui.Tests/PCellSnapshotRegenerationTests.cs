using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// docs/sonnet-briefs/brief-L5-followups-2.md §4/R-L5g-6, gate 6 — "the gate the deletion policy
/// depends on": delete the ENTIRE <c>.generated-cells</c> folder with every referencing layout closed
/// (i.e. resolved only from disk, never a live in-memory override), then reopen — every PCell
/// instance regenerates identically, INCLUDING palette-dropped and layout-authored ones with no
/// <c>SchematicId</c> — not just the schematic-linked case <c>SchematicPCellSnapshots</c> already
/// covered before this brief. Also covers gate 7 (the folder is genuinely empty after "close," i.e.
/// after <see cref="GeneratedCellsLifecycle.DeleteGeneratedCellsFolder"/>, and layouts still resolve
/// correctly after "open," i.e. after <see cref="GeneratedCellsLifecycle.RegenerateAll"/>).
/// </summary>
public sealed class PCellSnapshotRegenerationTests : IDisposable
{
    private readonly string _root;

    public PCellSnapshotRegenerationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-pcell-snapshot-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".cws"), "{}");
        CellLayoutResolver.InvalidateUnder(_root);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_root);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private LayoutEditorViewModel MakeVmAt(string cellName)
    {
        string clayPath = Path.Combine(_root, cellName, "layout", "main.clay");
        return new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 }, clayPath);
    }

    private static EditableComponent MakeMlin(string instanceName, double wMm)
    {
        var comp = new EditableComponent { InstanceName = instanceName, Symbol = SymbolKind.Mlin, X = 0, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Mlin, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name, Expression = dp.Name == "W" ? wMm.ToString() : dp.Expression,
                Unit = dp.Unit, ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });
        return comp;
    }

    [Fact]
    public void EveryPCellSnapshot_RecordsEnoughToRebuildItsCell_RegardlessOfOrigin()
    {
        // ── Origin 1: schematic-linked (SchematicPCellSnapshots' original, narrower scope) ──────────
        string schCellDir = CellFolder.CreateCellFolder(_root, "AmpSch");
        string schematicDir = CellFolder.SubFolderPath(schCellDir, ViewType.Schematic);
        string schLayoutDir = CellFolder.SubFolderPath(schCellDir, ViewType.Layout);
        var schModel = new SchematicEditModel { SchematicDirectory = schematicDir };
        schModel.Components.Add(MakeMlin("MLIN1", wMm: 7));
        var schTarget = new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 };
        var runResult = SchematicToLayoutGenerator.Run(schModel, schTarget, schematicDir, _root, schLayoutDir, null, null, null);
        runResult.Command!.Execute();
        string schCellRef = schTarget.Instances[0].CellRef;
        string schClayPath = Path.Combine(schLayoutDir, "AmpSch.clay");
        LayoutPersistence.SaveToFile(schClayPath, schTarget);

        // ── Origin 2: palette-dropped (no SchematicId at all) ────────────────────────────────────────
        var paletteVm = MakeVmAt("Palette");
        Assert.True(paletteVm.CommitPaletteDrop(SymbolKind.Mlin, 0, 0, 0));
        string paletteCellRef = paletteVm.Model.Instances[0].CellRef;
        Assert.Null(paletteVm.Model.Instances[0].SchematicId);
        string paletteClayPath = Path.Combine(_root, "Palette", "layout", "main.clay");
        Directory.CreateDirectory(Path.GetDirectoryName(paletteClayPath)!);
        LayoutPersistence.SaveToFile(paletteClayPath, paletteVm.Model);

        // ── Origin 3: layout-authored copy-on-write (EditInstancePCellParameters forks a NEW cell) ──
        var editVm = MakeVmAt("Edited");
        Assert.True(editVm.CommitPaletteDrop(SymbolKind.Mlin, 0, 0, 0));
        var defaults = SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.Mlin, 0);
        double newW = defaults["W"] * 3;
        Assert.True(editVm.EditInstancePCellParameters(0, new Dictionary<string, double> { ["W"] = newW }));
        string editedCellRef = editVm.Model.Instances[0].CellRef;
        Assert.NotEqual(paletteCellRef, editedCellRef); // genuinely forked to a different cell
        string editClayPath = Path.Combine(_root, "Edited", "layout", "main.clay");
        Directory.CreateDirectory(Path.GetDirectoryName(editClayPath)!);
        LayoutPersistence.SaveToFile(editClayPath, editVm.Model);

        // Every one of the three layouts recorded a snapshot for the cell it actually references.
        Assert.True(schTarget.PCellSnapshots.ContainsKey(Path.GetFileName(schCellRef)));
        Assert.True(paletteVm.Model.PCellSnapshots.ContainsKey(Path.GetFileName(paletteCellRef)));
        Assert.True(editVm.Model.PCellSnapshots.ContainsKey(Path.GetFileName(editedCellRef)));

        // ── "Close": delete the ENTIRE .generated-cells folder — gate 7's first half ────────────────
        string genRoot = Path.Combine(_root, GeneratedCellStore.ReservedFolderName);
        Assert.True(Directory.Exists(genRoot));
        GeneratedCellsLifecycle.DeleteGeneratedCellsFolder(_root);
        Assert.False(Directory.Exists(genRoot));

        // ── "Open": reload each layout FROM DISK (no live override survives a real close) and
        //    regenerate — gate 6/7's second half ────────────────────────────────────────────────────
        CellLayoutResolver.InvalidateUnder(_root);
        GeneratedCellsLifecycle.RegenerateAll(_root, _ => null);

        Assert.True(Directory.Exists(genRoot));

        // Every instance's ORIGINAL CellRef resolves again, with the exact parameters it had before —
        // proving the regenerated cell is byte-identical to what was deleted, for all three origins.
        AssertResolvesWithW(schCellRef, schLayoutDir, 7 * 1e-3);
        AssertResolvesWithW(paletteCellRef, Path.GetDirectoryName(paletteClayPath)!, defaults["W"]);
        AssertResolvesWithW(editedCellRef, Path.GetDirectoryName(editClayPath)!, newW);
    }

    private static void AssertResolvesWithW(string cellRef, string baseDir, double expectedWMeters)
    {
        var res = CellLayoutResolver.Resolve(cellRef, baseDir);
        Assert.Equal(CellLayoutState.Resolved, res.State);
        Assert.NotNull(res.View!.PCellOrigin);
        Assert.Equal(expectedWMeters, res.View.PCellOrigin!.Parameters["W"], 9);
    }
}
