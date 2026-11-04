using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Modeling.VerilogA;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Symbol;
using CircuitRF.Design.VendorLibraries.ModelLibrary;
using CircuitRF.Design.Workspace;
using Xunit;
using static CircuitRF.Ui.Tests.VendorLibraries.ModelLibraryExportTests;

namespace CircuitRF.Ui.Tests.VendorLibraries;

/// <summary>
/// The defects a review of the model-library series found, one claim each: a data file inside a cell's own folder
/// travels with an export; Copy to Workspace points a model's sub-cells at the copy; the signature covers
/// <c>part.cpart</c> and survives a CR LF checkout of <c>library.sums</c>; a null in a manifest is a refusal; an unbound
/// terminal's <c>$port_connected</c> is a constant; a Windows drive path leaves the folder; a corner value the reader
/// cannot take is refused before it is stored. Every part and library here is synthetic.
/// </summary>
[Collection(CellStatGlobalsCollection.Name)]
public sealed class VendorLibraryReviewFixTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-vlfix-" + Guid.NewGuid().ToString("N")[..12]);

    public VendorLibraryReviewFixTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private const string Line = "! synthetic line\n# GHz S RI R 50\n0.001 0.1 0 0.9 0 0.9 0 0.1 0\n10 0.2 0.1 0.7 -0.3 0.7 -0.3 0.2 0.1\n";

    /// <summary>in — SNP1 (a Touchstone kept in the cell's own data/ folder) — out.</summary>
    private string WriteCellWithOwnData(string ws, string name)
    {
        var sch = new SchematicEditModel();
        var p1 = Pin("P1", 1, "in", 0, 0);
        var s1 = Snp("SNP1", $"{name}/data/meas.s2p", 4000, 0);
        var p2 = Pin("P2", 2, "out", 10000, 0);
        foreach (var c in new[] { p1, s1, p2 }) sch.Components.Add(c);
        Wire(sch, p1, 0, s1, 0);
        Wire(sch, s1, 1, p2, 0);
        string cell = WriteCell(ws, name, sch);
        Directory.CreateDirectory(Path.Combine(cell, "data"));
        File.WriteAllText(Path.Combine(cell, "data", "meas.s2p"), Line);
        SymbolPersistence.SaveToFile(Path.Combine(CellFolder.SubFolderPath(cell, ViewType.Symbol), name + ".csym"),
                                     AutoSymbolGenerator.Generate(name, 2));
        return cell;
    }

    // 1 — a file inside the cell's folder was re-pointed into the copy and never written there.
    [Fact]
    public void Export_CarriesAFileThatLivesInsideTheCellsOwnFolder()
    {
        string ws = WorkspaceCreate.Create(_dir, "ws", null).WorkspaceDir;
        string cell = WriteCellWithOwnData(ws, "TSTDATA");

        var result = ModelLibraryExport.Export(cell, new ExportOptions { LibraryDir = Path.Combine(_dir, "lib") });
        Assert.True(result.Succeeded, result.Refusal);
        var sch = JsonNode.Parse(File.ReadAllText(Path.Combine(result.ModelDir!, "schematic", "TSTDATA.csch")))!;
        string file = Param(sch, "SNP1", "File")!;
        Assert.True(File.Exists(Path.Combine(result.ModelDir!, file)), file);
    }

    // 2 — a library holds no workspace, so the sub-cell walk never ran and the copy's sub-cells pointed into the library.
    [Fact]
    public void CopyToWorkspace_PointsTheModelsSubCellsAtTheCopy()
    {
        string ws = WorkspaceCreate.Create(_dir, "ws", null).WorkspaceDir;
        var sub = new SchematicEditModel();
        var a = Pin("P1", 1, "a", 0, 0);
        var l = Part("L1", SymbolKind.Inductor, 2000, 2000, "L", "3", "nH");
        var b = Pin("P2", 2, "b", 4000, 0);
        foreach (var c in new[] { a, l, b }) sub.Components.Add(c);
        Wire(sub, a, 0, l, 0);
        Wire(sub, l, 1, b, 0);
        string subCell = WriteCell(ws, "TSTSUB", sub);
        SymbolPersistence.SaveToFile(Path.Combine(CellFolder.SubFolderPath(subCell, ViewType.Symbol), "TSTSUB.csym"),
                                     AutoSymbolGenerator.Generate("TSTSUB", 2));
        string topSchDir = Path.Combine(ws, "TSTTOP", "schematic");
        Directory.CreateDirectory(topSchDir);
        var top = new SchematicEditModel { SchematicDirectory = topSchDir };
        var p1 = Pin("P1", 1, "in", 0, 0);
        var x1 = new EditableComponent { InstanceName = "X1", Symbol = SymbolKind.Generic, CellRef = "../../TSTSUB", X = 4000, Y = 2000 };
        var p2 = Pin("P2", 2, "out", 10000, 0);
        foreach (var c in new[] { p1, x1, p2 }) top.Components.Add(c);
        Wire(top, p1, 0, x1, 0);
        Wire(top, x1, 1, p2, 0);
        string cell = WriteCell(ws, "TSTTOP", top);
        SymbolPersistence.SaveToFile(Path.Combine(CellFolder.SubFolderPath(cell, ViewType.Symbol), "TSTTOP.csym"),
                                     AutoSymbolGenerator.Generate("TSTTOP", 2));
        var exported = ModelLibraryExport.Export(cell, new ExportOptions { LibraryDir = Path.Combine(_dir, "lib") });
        Assert.True(exported.Succeeded, exported.Refusal);

        string dest = WorkspaceCreate.Create(_dir, "dest", null).WorkspaceDir;
        var copy = VendorModelAuthoring.CopyToWorkspace(exported.ModelDir!, dest, "Lib");
        Assert.True(copy.Succeeded, copy.Message);
        string schDir = Path.Combine(copy.CellDir!, "schematic");
        var sch = JsonNode.Parse(File.ReadAllText(Path.Combine(schDir, "TSTTOP.csch")))!;
        string target = Path.GetFullPath(Path.Combine(schDir, Comp(sch, "X1")["CellRef"]!.GetValue<string>()));
        Assert.StartsWith(Path.GetFullPath(copy.CellDir!) + Path.DirectorySeparatorChar, target);
    }

    // 3 — part.cpart is outside its folder's PublishedHash; signed on its own line, an edit to it is a change. A
    // library.sums a checkout rewrote with CR LF still verifies.
    [Fact]
    public void TheSignature_CoversPartCpart_AndACrLfSumsStillVerifies()
    {
        string ws = WorkspaceCreate.Create(_dir, "ws", null).WorkspaceDir;
        string lib = Path.Combine(_dir, "lib");
        var exported = ModelLibraryExport.Export(WriteCellWithOwnData(ws, "TSTSIG"),
                                                 new ExportOptions { LibraryDir = lib, Publisher = "TST Devices" });
        Assert.True(exported.Succeeded, exported.Refusal);
        string key = Path.Combine(_dir, "key.pem");
        Assert.NotNull(LibrarySignature.NewKey(key, out _));
        Assert.True(LibrarySignature.Sign(lib, key).Succeeded);

        string sums = Path.Combine(lib, LibrarySignature.SumsFileName);
        File.WriteAllText(sums, File.ReadAllText(sums).Replace("\n", "\r\n"));
        Assert.Equal(LibrarySignatureCheck.Intact, LibrarySignature.Inspect(lib).Check);

        string cpart = Path.Combine(exported.ModelDir!, ModelFolder.PartFileName);
        File.WriteAllText(cpart, File.ReadAllText(cpart).Replace("\"Description\": \"\"", "\"Description\": \"edited\""));
        var after = LibrarySignature.Inspect(lib);
        Assert.Equal(LibrarySignatureCheck.Changed, after.Check);
        Assert.Equal(["TSTSIG"], after.ChangedParts);
    }

    // 4 — a null in a hand-written manifest is that manifest's refusal, not a null string thrown on later.
    [Fact]
    public void ANullInPartCpart_IsARefusal()
    {
        string path = Path.Combine(_dir, ModelFolder.PartFileName);
        File.WriteAllText(path, "{ \"Format\": \"circuitrf-model\", \"FormatVersion\": 1, \"Part\": \"TST1\", \"Cell\": \"TST1\", \"Kind\": \"active\", \"Pins\": [\"a\"], \"Description\": null }");
        var (manifest, refusal) = ModelPartManifest.Read(path);
        Assert.Null(manifest);
        Assert.Contains("Description", refusal);
    }

    // 5 — the compiler takes $port_connected on a port only; an unbound terminal is an internal node, not connected.
    [Fact]
    public void AnUnboundTerminalsPortConnected_IsAConstant()
    {
        const string module = """
            module m(a, t);
              inout a, t;
              electrical a;
              thermal t, tn;
              analog begin
                if ($port_connected(t)) Temp(t, tn) <+ 0.0;
                else Temp(t) <+ 0.0;
              end
            endmodule
            """;
        var f = VaFragment.From(module, "u1_", new Dictionary<string, string> { ["a"] = "x" });
        Assert.DoesNotContain("$port_connected", f.Body);
        Assert.Contains("if (0)", f.Body);
    }

    // 6 — a drive path written on Windows is not rooted to macOS or Linux and combined under the folder.
    [Fact]
    public void AWindowsDrivePath_LeavesTheFolderOnEveryOperatingSystem()
    {
        Assert.True(ModelFolderCheck.LeavesFolder(_dir, @"C:\models\x.s2p"));
        Assert.True(ModelFolderCheck.LeavesFolder(_dir, @"\\server\share\x.s2p"));
        Assert.False(ModelFolderCheck.LeavesFolder(_dir, "models/x.s2p"));
    }

    // 7 — a corner value the reader cannot take is refused before it is stored, not met by every later extraction.
    [Fact]
    public void ACornerValueTheReaderCannotTake_IsRefusedBeforeItIsStored()
    {
        var bad = new CcellCorner { Name = "min", Values = new() { ["Cg"] = "Cnom * 0.9" } };
        Assert.NotNull(ModelCellEdits.CornerProblem([bad], ["Cg", "Cnom"]));
        var good = new CcellCorner { Name = "min", Values = new() { ["Cg"] = "0.9 pF" } };
        Assert.Null(ModelCellEdits.CornerProblem([good], ["Cg", "Cnom"]));
    }
}
