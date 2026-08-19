using System.IO;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.WBond;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Rename Cell renamed only the primary schematic and symbol. The layout was omitted from that
/// list, so a renamed cell kept a <c>.clay</c> named for the old cell — and, worse, the wires:
/// <see cref="WBondCell.Resolve"/> pairs a <c>.wBond</c> to a <c>.clay</c> by SHARED STEM, so once
/// the layout IS renamed the wirebond design has to move with it or the layout reopens with none.
/// </summary>
public class RenameCellLayoutAndWBondTests : IDisposable
{
    private readonly string _ws;

    public RenameCellLayoutAndWBondTests()
    {
        _ws = Path.Combine(Path.GetTempPath(), $"crf_ren_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_ws);
    }

    public void Dispose() { try { Directory.Delete(_ws, recursive: true); } catch { } }

    private string Cell(string name) => CellFolder.CreateCellFolder(_ws, name);

    private static string LayoutDir(string cellDir) => CellFolder.SubFolderPath(cellDir, ViewType.Layout);

    private static string WriteClay(string cellDir, string stem)
    {
        var dir  = LayoutDir(cellDir);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, stem + ".clay");
        LayoutPersistence.SaveToFile(path,
            new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 });
        return path;
    }

    private static string WriteWBond(string cellDir, string stem)
    {
        var dir  = LayoutDir(cellDir);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, stem + WBondCell.FileExtension);
        File.WriteAllText(path, "{}");
        return path;
    }

    /// <summary>Writes a schematic holding one placed wBond linked to <paramref name="link"/>.</summary>
    private static string WriteSchematicLinking(string cellDir, string link)
    {
        var dir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Path.GetFileName(cellDir) + ".csch");

        var model = new SchematicEditModel();
        var comp  = new EditableComponent { InstanceName = "WB1", Symbol = SymbolKind.WBond };
        comp.Parameters.Add(new EditableParameter
        {
            Name = WBondPlacement.FileParameter, Expression = link,
        });
        model.Components.Add(comp);
        SchematicPersistence.SaveToFile(path, model);
        return path;
    }

    private static string LinkIn(string cschPath)
    {
        var (model, _, _) = SchematicPersistence.LoadFromFile(cschPath);
        return model.Components.Single()
            .Parameters.Single(p => p.Name == WBondPlacement.FileParameter).Expression;
    }

    // ── the stem pairing ─────────────────────────────────────────────────────

    [Fact]
    public void RenamingTheArtwork_TakesTheWiresWithIt()
    {
        var cell = Cell("Amp");
        var clay = WriteClay(cell, "Amp");
        WriteWBond(cell, "Amp");

        // Rename the .clay exactly as Rename Cell does, then the wires.
        File.Move(clay, Path.Combine(LayoutDir(cell), "PowerAmp.clay"));
        var (outcome, _) = WBondCell.RenamePairedWires(LayoutDir(cell), "Amp", "PowerAmp");

        Assert.Equal(WBondCell.RenameOutcome.Renamed, outcome);
        // The pairing is what actually matters, so assert through the resolver, not the file list.
        Assert.Equal(
            Path.Combine(LayoutDir(cell), "PowerAmp" + WBondCell.FileExtension),
            WBondCell.FindFor(Path.Combine(LayoutDir(cell), "PowerAmp.clay")));
    }

    [Fact]
    public void ALayoutWithNoWires_IsNotAnError()
    {
        var cell = Cell("Amp");
        WriteClay(cell, "Amp");

        Assert.Equal(WBondCell.RenameOutcome.NothingToRename,
            WBondCell.RenamePairedWires(LayoutDir(cell), "Amp", "PowerAmp").Outcome);
    }

    /// <summary>An existing file at the new stem belongs to a different layout — overwriting it
    /// would destroy someone's wires, so the move is refused and the caller reports it.</summary>
    [Fact]
    public void AnOccupiedTargetStem_IsRefused_NotOverwritten()
    {
        var cell = Cell("Amp");
        WriteWBond(cell, "Amp");
        var occupied = WriteWBond(cell, "PowerAmp");
        File.WriteAllText(occupied, "{\"keep\":1}");

        Assert.Equal(WBondCell.RenameOutcome.Blocked,
            WBondCell.RenamePairedWires(LayoutDir(cell), "Amp", "PowerAmp").Outcome);
        Assert.Equal("{\"keep\":1}", File.ReadAllText(occupied));
        Assert.True(File.Exists(Path.Combine(LayoutDir(cell), "Amp" + WBondCell.FileExtension)));
    }

    /// <summary>A .wBond under a name of its own — an assembly house's bond list — pairs with a
    /// different .clay (or with nothing) and is not this rename's business.</summary>
    [Fact]
    public void AnUnrelatedWBond_IsLeftAlone()
    {
        var cell = Cell("Amp");
        WriteWBond(cell, "vendor_bondlist");

        Assert.Equal(WBondCell.RenameOutcome.NothingToRename,
            WBondCell.RenamePairedWires(LayoutDir(cell), "Amp", "PowerAmp").Outcome);
        Assert.True(File.Exists(Path.Combine(LayoutDir(cell), "vendor_bondlist" + WBondCell.FileExtension)));
    }

    // ── the links ────────────────────────────────────────────────────────────

    [Fact]
    public void TheCellsOwnSchematic_IsRepointed()
    {
        var cell = Cell("PowerAmp");                       // already renamed on disk
        var csch = WriteSchematicLinking(cell, "../layout/Amp.wBond");
        WriteWBond(cell, "Amp");

        var rewritten = CellUsageScanner.RewriteWBondLinks(
            _ws, LayoutDir(cell), "Amp", "PowerAmp", "Amp", "PowerAmp", out var failed);

        Assert.Empty(failed);
        Assert.Equal([csch], rewritten);
        Assert.Equal("../layout/PowerAmp.wBond", LinkIn(csch));
    }

    /// <summary>
    /// A link from ANOTHER cell still spells the old cell folder, and the folder rename already
    /// broke it. Recognising it needs the substitution, so this is the case a name-only or a
    /// resolve-only rule each miss on their own.
    /// </summary>
    [Fact]
    public void ALinkFromAnotherCell_IsRepairedAndRepointed()
    {
        var target = Cell("PowerAmp");                     // already renamed on disk
        WriteWBond(target, "Amp");
        var other = Cell("Board");
        var csch  = WriteSchematicLinking(other, "../../Amp/layout/Amp.wBond");

        CellUsageScanner.RewriteWBondLinks(
            _ws, LayoutDir(target), "Amp", "PowerAmp", "Amp", "PowerAmp", out var failed);

        Assert.Empty(failed);
        Assert.Equal("../../PowerAmp/layout/PowerAmp.wBond", LinkIn(csch));
    }

    /// <summary>
    /// Two cells may each own a <c>layout/top.wBond</c>. The link is matched by where it RESOLVES,
    /// so the one that did not move is not touched — a name-only match would repoint it.
    /// </summary>
    [Fact]
    public void ASameNamedWBondInAnotherCell_IsNotRepointed()
    {
        var moved = Cell("PowerAmp");
        WriteWBond(moved, "top");
        var other = Cell("Board");
        WriteWBond(other, "top");
        var csch = WriteSchematicLinking(other, "../layout/top.wBond");

        CellUsageScanner.RewriteWBondLinks(
            _ws, LayoutDir(moved), "top", "PowerAmp", "Amp", "PowerAmp", out var failed);

        Assert.Empty(failed);
        Assert.Equal("../layout/top.wBond", LinkIn(csch));
    }
}
