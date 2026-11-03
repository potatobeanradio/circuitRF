using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Symbol;
using CircuitRF.Design.VendorLibraries;
using CircuitRF.Design.VendorLibraries.ModelLibrary;
using CircuitRF.Design.Workspace;
using CircuitRF.Diagnostics;
using CircuitRF.Ui.ViewModels;
using Xunit;
using static CircuitRF.Ui.Tests.VendorLibraries.ModelLibraryExportTests;

namespace CircuitRF.Ui.Tests.VendorLibraries;

/// <summary>
/// Brief vendorlib-12 §5: a placed library part is never pushed into; Edit Vendor Model is greyed for each of its three
/// reasons; an edited model's save refreshes it without touching PublishedHash; a refusal leaves the netlist as it was;
/// Copy to Workspace runs as the library part does and re-pointing is one undo; and the refresh lives once. Every part
/// and library here is synthetic.
/// </summary>
[Collection(CellStatGlobalsCollection.Name)]
public sealed class VendorModelEditTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-vl12-" + Guid.NewGuid().ToString("N")[..12]);
    private static readonly double[] Freqs = [1e7, 1e8, 1e9, 5e9];

    public VendorModelEditTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        WorkspaceWritability.WritabilityProbe = null;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // 1 — Push In is not offered on a library part, and says which two commands are.
    [Fact]
    public void PushIn_IsNotAvailableOnALibraryPart_WithTheReason()
    {
        var f = Fixture();
        var comp = new EditableComponent { InstanceName = "X1", Symbol = SymbolKind.Generic, CellRef = f.Ref };
        Assert.False(HierarchyResolver.CanPushInto(comp, new SchematicEditModel { SchematicDirectory = f.Bench }, out string? reason));
        Assert.Equal(VendorModelAuthoring.PushInRefusal, reason);
    }

    // 2 — Edit Vendor Model is greyed for a SPICE-text part, a ReadOnly library and an unwritable folder, each saying so.
    [Theory]
    [InlineData("spice")]
    [InlineData("readonly")]
    [InlineData("unwritable")]
    public void EditVendorModel_IsGreyed_WithItsOwnReason(string which)
    {
        var f = Fixture();
        var comp = new EditableComponent { InstanceName = "X1", Symbol = SymbolKind.Generic, CellRef = f.Ref };
        Assert.Null(VendorModelAuthoring.EditRefusal(comp, f.Bench, null));

        string expected;
        switch (which)
        {
            case "spice":
                comp = new EditableComponent { InstanceName = "C1", Symbol = SymbolKind.SpiceModel };
                comp.Parameters.Add(new EditableParameter { Name = SpiceModelSymbolProvider.FileParameter, Expression = "vendorlib://Caps/series.lib" });
                expected = VendorModelAuthoring.SpiceTextReason;
                Assert.Equal(expected, VendorModelAuthoring.CopyRefusal(comp, f.Bench, null));
                break;
            case "readonly":
                string clib = Path.Combine(f.Library, ModelFolder.LibraryFileName);
                ModelLibraryManifest.Write(clib, ModelLibraryManifest.Read(clib).Manifest! with { ReadOnly = true });
                expected = VendorModelAuthoring.ReadOnlyReason;
                break;
            default:
                string key = WorkspaceRootFinder.Normalize(f.Model);
                WorkspaceWritability.WritabilityProbe = d => !string.Equals(d, key, StringComparison.OrdinalIgnoreCase);
                expected = VendorModelAuthoring.UnwritableReason(f.Model);
                break;
        }
        Assert.Equal(expected, VendorModelAuthoring.EditRefusal(comp, f.Bench, null));
    }

    // 3 — an edit saved: the netlist changes, PublishedHash does not, check says "edited since exported", a newly
    // referenced file is copied in, and a design placing the part runs the new netlist.
    [Fact]
    public void EditThenSave_RefreshesTheNetlist_NotThePublishedHash_AndPlacedPartsRunIt()
    {
        var f = Fixture();
        string cnlBefore = File.ReadAllText(ModelNetlist.PathOf(f.Model));
        var before = ModelPartManifest.Read(Path.Combine(f.Model, ModelFolder.PartFileName)).Manifest!;
        var sBefore = RunPlaced(f.Bench, f.Ref, Freqs).Cubes["S"].ComplexValues;

        // The edit, written as the editor's save writes it: R1 changed, and an (open) part naming a file outside the model.
        string other = Path.Combine(_dir, "ext", "other.s2p");
        File.Copy(Path.Combine(_dir, "ext", "line.s2p"), other);
        string sch = Path.Combine(f.Model, "schematic", "TSTRL.csch");
        EditModelSchematic(sch, m =>
        {
            m.Components.First(c => c.InstanceName == "R1").Parameters[0].Expression = "70";
            var open = Snp("SNP9", other, 30000, 30000);
            open.Disable = DisableState.Open;
            m.Components.Add(open);
        });
        EditModelSchematic(Path.Combine(f.Cell, "schematic", "TSTRL.csch"),
                           m => m.Components.First(c => c.InstanceName == "R1").Parameters[0].Expression = "70");

        var r = ModelLibraryExport.Refresh(f.Model);
        Assert.True(r.Succeeded, r.Message);
        Assert.True(r.NetlistChanged);
        Assert.NotEqual(cnlBefore, File.ReadAllText(ModelNetlist.PathOf(f.Model)));
        Assert.True(File.Exists(Path.Combine(f.Model, "models", "other.s2p")));
        Assert.Equal("models/other.s2p", Param(JsonNode.Parse(File.ReadAllText(sch))!, "SNP9", "File"));
        Assert.Contains(sch, r.Rewritten);

        var after = ModelPartManifest.Read(Path.Combine(f.Model, ModelFolder.PartFileName)).Manifest!;
        Assert.Equal(before.PublishedHash, after.PublishedHash);
        Assert.Equal(before.Revision, after.Revision);
        var findings = ModelFolderCheck.Validate(f.Model);
        Assert.Contains(findings, d => d.Id == "check.model.edited-since-exported");
        Assert.DoesNotContain(findings, d => d.Severity == DiagnosticSeverity.Error);

        var sLibrary = RunPlaced(f.Bench, f.Ref, Freqs).Cubes["S"].ComplexValues;
        var sCell = RunPlaced(f.Bench, Path.GetRelativePath(f.Bench, f.Cell).Replace('\\', '/'), Freqs).Cubes["S"].ComplexValues;
        Assert.True((sLibrary[0] - sBefore[0]).Magnitude > 1e-3);
        for (int i = 0; i < sLibrary.Length; i++) Assert.True((sLibrary[i] - sCell[i]).Magnitude <= 1e-12, $"S[{i}]");
    }

    // 4 — an edit adding a kit part: the schematic stays saved, the netlist is left as it was, check reports the mismatch.
    [Fact]
    public void AnEditAddingAKitPart_KeepsTheSchematic_LeavesTheNetlist_AndCheckErrors()
    {
        var f = Fixture();
        string cnl = ModelNetlist.PathOf(f.Model);
        byte[] cnlBefore = File.ReadAllBytes(cnl);
        string sch = Path.Combine(f.Model, "schematic", "TSTRL.csch");
        EditModelSchematic(sch, m => m.Components.Add(new EditableComponent
            { InstanceName = "K1", Symbol = SymbolKind.Generic, CellRef = "pdk://kit/nmos", X = 30000 }));
        string saved = File.ReadAllText(sch);

        var r = ModelLibraryExport.Refresh(f.Model);
        Assert.False(r.Succeeded);
        Assert.StartsWith("TSTRL still runs its previous netlist: ", r.Message);
        Assert.Contains("K1", r.Message);
        Assert.Equal(saved, File.ReadAllText(sch));
        Assert.Equal(cnlBefore, File.ReadAllBytes(cnl));
        Assert.Contains(ModelFolderCheck.Validate(f.Model),
                        d => d.Id == "check.model.netlist-not-schematic" && d.Severity == DiagnosticSeverity.Error);
    }

    // 5 — Copy to Workspace runs as the library part does; pointing every instance at the copy is one undo.
    [Fact]
    public void CopyToWorkspace_RunsAsTheLibraryPart_AndRepointingIsOneUndo()
    {
        var f = Fixture();
        var manifest = ModelPartManifest.Read(Path.Combine(f.Model, ModelFolder.PartFileName)).Manifest!;
        var copy = VendorModelAuthoring.CopyToWorkspace(f.Model, f.Workspace, "Lib");
        Assert.True(copy.Succeeded, copy.Message);
        Assert.Equal(Path.Combine(f.Workspace, "TSTRL_2"), copy.CellDir);   // the source cell TSTRL is already there
        Assert.False(File.Exists(Path.Combine(copy.CellDir!, ModelFolder.PartFileName)));
        Assert.False(File.Exists(Path.Combine(copy.CellDir!, "TSTRL.cnl")));
        Assert.True(File.Exists(Path.Combine(copy.CellDir!, "models", "line.s2p")));
        var from = CellPersistence.LoadFromFile(Path.Combine(copy.CellDir!, CellFolder.CcellFileName)).CopiedFrom!;
        Assert.Equal(("Lib", "TSTRL", 1, manifest.PublishedHash), (from.Library, from.Part, from.Revision, from.PublishedHash));

        var sLibrary = RunPlaced(f.Bench, f.Ref, Freqs).Cubes["S"].ComplexValues;
        var sCopy = RunPlaced(f.Bench, Path.GetRelativePath(f.Bench, copy.CellDir!).Replace('\\', '/'), Freqs).Cubes["S"].ComplexValues;
        Assert.Equal(sLibrary.Length, sCopy.Length);
        for (int i = 0; i < sCopy.Length; i++) Assert.True((sCopy[i] - sLibrary[i]).Magnitude <= 1e-12, $"S[{i}]: {sCopy[i]} vs {sLibrary[i]}");

        // Two placed instances, wired, one with a value of its own.
        var bench = new SchematicEditModel { SchematicDirectory = f.Bench };
        var x1 = Placed("X1", f.Ref, 0);
        var x2 = Placed("X2", f.Ref, 10000);
        x2.Parameters.Add(new EditableParameter { Name = "Note", Expression = "7" });
        bench.Components.Add(x1);
        bench.Components.Add(x2);
        bench.Wires.Add(new EditableWire { Points = { (0, 0), (10000, 0) } });
        var vm = new SchematicViewModel(bench);
        var paramsBefore = Rows(x2);

        Assert.True(vm.RepointToCell(vm.SameLibraryModel(x1), copy.CellDir!));
        Assert.All(new[] { x1, x2 }, x => Assert.Equal("../../TSTRL_2", x.CellRef));
        Assert.Equal(paramsBefore.Where(p => p.Name != VendorModelRefs.GlyphParameter), Rows(x2));
        Assert.Single(bench.Wires);
        Assert.Equal((10000.0, 0.0), (x2.X, x2.Y));

        vm.UndoRedo.Undo();
        Assert.All(new[] { x1, x2 }, x => Assert.Equal(f.Ref, x.CellRef));
        Assert.Equal(paramsBefore, Rows(x2));
    }

    // 6 — the refresh and the copy exist once: neither shell holds any of their steps.
    [Theory]
    [InlineData("src/Ui/ViewModels/WorkspaceViewModel.VendorModelEdit.cs", "ModelLibraryExport.Refresh(")]
    [InlineData("src/Ui/ViewModels/SchematicViewModel.VendorModel.cs", null)]
    [InlineData("src/Cli/ExportPart.cs", "ModelLibraryExport.Refresh(")]
    [InlineData("src/Cli/ImportModel.cs", "VendorModelAuthoring.CopyToWorkspace(")]
    public void NeitherShell_HoldsTheRefreshOrTheCopy(string file, string? calls)
    {
        string code = StripComments(File.ReadAllText(Path.Combine(RepoRoot(), file)));
        if (calls is not null) Assert.Contains(calls, code, StringComparison.Ordinal);
        foreach (string inner in new[] { "ModelNetlist.FromCell(", "CnlWriter.Write(", "DocumentFileRefs.", "ModelPartManifest.Write(",
                                         "CrossWorkspaceCellCopy.", "File.Copy(", "File.Delete(", "NetExtractor." })
            Assert.DoesNotContain(inner, code, StringComparison.Ordinal);
    }

    // ── fixture ───────────────────────────────────────────────────────────────

    private sealed record Fix(string Workspace, string Cell, string Library, string Model, string Bench, string Ref);

    /// <summary>
    /// <c>TSTRL</c>: in — R1 (20 Ω) — SNP1 (a Touchstone outside the workspace) — out, exported to a library outside the
    /// workspace as <c>Parts/TSTRL</c>, the library referenced from the workspace as <c>Lib</c>.
    /// </summary>
    private Fix Fixture()
    {
        string ws = WorkspaceCreate.Create(_dir, "ws", null).WorkspaceDir;
        Directory.CreateDirectory(Path.Combine(_dir, "ext"));
        File.WriteAllText(Path.Combine(_dir, "ext", "line.s2p"),
            "! synthetic line\n# GHz S RI R 50\n0.001 0.1 0 0.9 0 0.9 0 0.1 0\n10 0.2 0.1 0.7 -0.3 0.7 -0.3 0.2 0.1\n");

        var sch = new SchematicEditModel();
        var p1 = Pin("P1", 1, "in", 0, 0);
        var r1 = Part("R1", SymbolKind.Resistor, 2000, 2000, "R", "20", "Ohm");
        var s1 = Snp("SNP1", "../ext/line.s2p", 8000, 2000);
        var p2 = Pin("P2", 2, "out", 14000, 0);
        foreach (var c in new[] { p1, r1, s1, p2 }) sch.Components.Add(c);
        Wire(sch, p1, 0, r1, 0);
        Wire(sch, r1, 1, s1, 0);
        Wire(sch, s1, 1, p2, 0);
        string cell = WriteCell(ws, "TSTRL", sch);
        SymbolPersistence.SaveToFile(Path.Combine(CellFolder.SubFolderPath(cell, ViewType.Symbol), "TSTRL.csym"),
                                     AutoSymbolGenerator.Generate("TSTRL", 2));

        string lib = Path.Combine(_dir, "lib");
        var export = ModelLibraryExport.Export(cell, new ExportOptions { LibraryDir = lib, Category = "Parts" });
        Assert.True(export.Succeeded, export.Refusal);

        var refs = new List<CwsVendorLibraryRef>();
        Assert.NotNull(VendorLibraryRefs.AddOrRepair(ws, refs, lib, "Lib", out _));
        var cws = WorkspacePersistence.LoadFromFile(Path.Combine(ws, ".cws"));
        cws.VendorLibraries = refs;
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), cws);
        WorkspaceRootFinder.InvalidateCache();

        string bench = Path.Combine(ws, "bench", "schematic");
        Directory.CreateDirectory(bench);
        return new Fix(ws, cell, lib, export.ModelDir!, bench, VendorModelRefs.RefFor("Lib", "Parts/TSTRL"));
    }

    private static void EditModelSchematic(string path, Action<SchematicEditModel> edit)
    {
        var (model, _, _) = SchematicPersistence.LoadFromFile(path);
        edit(model);
        SchematicPersistence.SaveToFile(path, model);
        WorkspaceRootFinder.InvalidateCache();
    }

    private static EditableComponent Placed(string name, string cellRef, double x)
    {
        var c = new EditableComponent { InstanceName = name, Symbol = SymbolKind.Generic, CellRef = cellRef, X = x };
        c.Parameters.Add(new EditableParameter { Name = VendorModelRefs.GlyphParameter, Expression = "", ShowOnSchematic = false });
        return c;
    }

    private static List<(string Name, string Expression)> Rows(EditableComponent c)
        => [.. c.Parameters.Select(p => (p.Name, p.Expression))];
}
