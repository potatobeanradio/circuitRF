using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Tests.Composed;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Modeling;
using CircuitRF.Design.Modeling.Passive;
using CircuitRF.Design.Modeling.VerilogA;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.VendorLibraries.ModelLibrary;
using CircuitRF.Design.VendorLibraries.ModelLibrary.Protect;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine;
using CircuitRF.Engine.Tests.Composed;
using CircuitRF.Ui.ModelComposer;
using CircuitRF.Ui.Tests.ModelComposer.VerilogA;
using Xunit;
using static CircuitRF.Ui.Tests.VendorLibraries.ModelLibraryExportTests;

namespace CircuitRF.Ui.Tests.VendorLibraries;

/// <summary>
/// Brief vendorlib-17 §3: Export to Library from Model Composer. The cell written for a model exports to the same part a
/// hand-drawn cell does; a self-heating model has a fourth pin; each reason the button is greyed is its sentence, and the
/// CLI refuses a stale model with the same one; the written cell never outlives the export; and Compile netlist gives the
/// FET's own module. Every model here is synthetic (TSTFET100).
/// </summary>
[Collection(CellStatGlobalsCollection.Name)]
public sealed class ModelComposerExportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-vl17-" + Guid.NewGuid().ToString("N")[..12]);

    public ModelComposerExportTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    // 1 — the written cell exports, and the library part runs as a hand-drawn cell around the same .cmodel does (the
    // export's own self-check comparison, at DC and over frequency).
    [Fact]
    public void TheWrittenCell_Exports_AndThePartMatchesAHandDrawnCell()
    {
        string cmc = WriteModelDocument(VaTestModels.AnnModel(AnnActivation.Tanh));
        var source = ModelCellFromModel.Open(cmc, out _)!;
        Assert.Equal("TSTFET100", source.PartNumber);

        ExportResult result;
        using (var staged = ModelCellFromModel.Stage(source))
        {
            var options = new ExportOptions { LibraryDir = Path.Combine(_dir, "lib"), PartNumber = source.PartNumber };
            Assert.Equal(ModelLibraryExport.SymbolFet, ModelLibraryExport.Plan(staged.CellDir, options).DefaultSymbol);
            result = ModelLibraryExport.Export(staged.CellDir, options);
        }
        Assert.True(result.Succeeded, result.Refusal);

        var (part, _) = ModelPartManifest.Read(Path.Combine(result.ModelDir!, ModelFolder.PartFileName));
        Assert.Equal(ModelPartManifest.KindActive, part!.Kind);
        Assert.Equal(["g", "d", "s"], part.Pins);

        string hand = WriteHandDrawnCell(source.CmodelPath!);
        Assert.Null(ModelLibraryExport.SelfCheck(hand, result.ModelDir!, part));

        // check elaborates the model's schematic against the model folder, as a run does: its .cmodel is models/x.cmodel
        // there, and against the schematic's own folder it was reported not found.
        var checkedPart = RunCliPublic("check", result.ModelDir!);
        Assert.True(checkedPart.Exit == 0, checkedPart.StdErr);
    }

    // 2 — a self-heating model's cell has t as pin 4, one that does not three pins; and the cell is deterministic.
    [Fact]
    public void ASelfHeatingModel_GetsPinT_AndTheCellIsDeterministic()
    {
        var plain = VaTestModels.AnnModel(AnnActivation.Tanh);
        string cool = ComposedTestModels.WriteTemp(plain, "cool.cmodel");
        string hot = ComposedTestModels.WriteTemp(
            ComposedTestModels.Model(ModelTemperature.Range(0, 200)) with { Thermal = ModelThermal.Single(25, 1e-9) }, "hot.cmodel");

        string a = Path.Combine(_dir, "a", "TSTFET100"), b = Path.Combine(_dir, "b", "TSTFET100"), t = Path.Combine(_dir, "t", "TSTFET100");
        ModelCellFromModel.Write(cool, a);
        ModelCellFromModel.Write(cool, b);
        ModelCellFromModel.Write(hot, t);

        Assert.Equal(["g", "d", "s"], Pins(a));
        Assert.Equal(["g", "d", "s", "t"], Pins(t));
        foreach (string f in new[] { Path.Combine("schematic", "TSTFET100.csch"), CellFolder.CcellFileName })
            Assert.Equal(File.ReadAllBytes(Path.Combine(a, f)), File.ReadAllBytes(Path.Combine(b, f)));
        var ccell = CellPersistence.LoadFromFile(Path.Combine(a, CellFolder.CcellFileName));
        Assert.Equal([("N", plain.BasePeriphery.Fingers.ToString()), ("Temp", "temp")],
                     ccell.Parameters.Select(p => (p.Name, p.DefaultExpression)));
        Assert.Empty(Directory.EnumerateFiles(CellFolder.SubFolderPath(a, ViewType.Symbol)));

        static string[] Pins(string cell)
            => [.. NetExtractor.ExtractCell(cell, out _)!.Value.Library.Find("TSTFET100")!.Ports];
    }

    // 3 — each D6 condition greys the button with its sentence; the CLI refuses a stale model with the same one.
    [Fact]
    public async Task EachReason_GreysTheButton_AndTheCliRefusesAStaleModelWithTheSameSentence()
    {
        Assert.Contains("Save this document first", Button(new ModelComposerViewModel()));

        string cmc = WriteModelDocument(VaTestModels.AnnModel(AnnActivation.Tanh), exported: false);
        var vm = ModelComposerViewModel.OpenFile(cmc);
        Assert.Equal("The model has not been exported to TSTFET100.cmodel yet; run the Export step first.", Button(vm));

        string cmodel = ModelCellFromModel.DefaultCmodelPath(cmc);
        CmodelFile.Write(VaTestModels.AnnModel(AnnActivation.Tanh), cmodel);
        Assert.Null(Button(vm));
        vm.IsDirty = true;
        Assert.Contains("Save this document first", vm.ExportToLibraryBlocked);
        vm.IsDirty = false;

        File.SetLastWriteTimeUtc(cmodel, File.GetLastWriteTimeUtc(cmc).AddMinutes(-5));
        string stale = "TSTFET100.cmodel is older than TSTFET100.cmc; run the Export step again so the library gets the model as it is now.";
        Assert.Equal(stale, Button(vm));
        var cli = RunCliPublic("export", "part", cmc, "--library", Path.Combine(_dir, "lib"));
        Assert.Equal(1, cli.Exit);
        Assert.Contains(stale, cli.StdErr);

        string passive = Path.Combine(_dir, "passive.cmc");
        CmcFile.Write(passive, new CmcDocument { Kind = CmcKind.Passive });
        Assert.Equal("The Export step names no cell and library yet; set them and export the cell first.",
                     Button(ModelComposerViewModel.OpenFile(passive)));
        CmcFile.Write(passive, PassiveStep.WithSettings(CmcFile.Read(passive),
            PassiveStepSettings.Default with { Export = new PassiveExportSettings("TSTCAP10", "lib") }));
        Assert.Equal("The cell TSTCAP10 has not been exported yet; run the Export step first.",
                     Button(ModelComposerViewModel.OpenFile(passive)));
        await Task.CompletedTask;

        static string? Button(ModelComposerViewModel m)
        {
            m.PublishToLibrary = (_, _) => Task.CompletedTask;
            m.RefreshExportToLibrary();
            Assert.Equal(m.ExportToLibraryBlocked is null, m.ExportToLibraryCommand.CanExecute(null));
            return m.ExportToLibraryBlocked;
        }
    }

    // 4 — the written cell is gone after a success, a refusal and a cancel; a crash's leftover goes on the next run.
    [Fact]
    public void TheWrittenCell_IsGoneAfterSuccessRefusalAndCancel()
    {
        string cmc = WriteModelDocument(VaTestModels.AnnModel(AnnActivation.Tanh));
        string docDir = Path.GetDirectoryName(cmc)!;
        string[] Leftovers() => [.. Directory.EnumerateDirectories(docDir, ModelCellFromModel.TemporaryPrefix + "*")];

        var ok = RunCliPublic("export", "part", cmc, "--library", Path.Combine(_dir, "lib"));
        Assert.True(ok.Exit == 0, ok.StdErr);
        Assert.Empty(Leftovers());

        var refused = RunCliPublic("export", "part", cmc, "--library", Path.Combine(_dir, "lib"));   // already there, no --replace
        Assert.Equal(1, refused.Exit);
        Assert.Empty(Leftovers());

        var stop = new RunControl();
        stop.RequestStop();
        using (var staged = ModelCellFromModel.Stage(ModelCellFromModel.Open(cmc, out _)!))
            Assert.False(ModelLibraryExport.Export(staged.CellDir, new ExportOptions { LibraryDir = Path.Combine(_dir, "lib2") }, stop).Succeeded);
        Assert.Empty(Leftovers());

        Directory.CreateDirectory(Path.Combine(docDir, ModelCellFromModel.TemporaryPrefix + "CRASHED", "CRASHED"));
        using (ModelCellFromModel.Stage(ModelCellFromModel.Open(cmc, out _)!))
            Assert.Equal([Path.Combine(docDir, ModelCellFromModel.TemporaryPrefix + "TSTFET100")], Leftovers());
        Assert.Empty(Leftovers());
    }

    // 5 — Compile netlist on the written cell is the FET's own standalone module (brief 16 test 1, through this path).
    [Fact]
    public void CompileNetlist_OnTheWrittenCell_IsTheFetsOwnModule()
    {
        string cmc = WriteModelDocument(VaTestModels.AnnModel(AnnActivation.Tanh));
        var source = ModelCellFromModel.Open(cmc, out _)!;
        using var staged = ModelCellFromModel.Stage(source);

        string text = ModelNetlist.FromCell(staged.CellDir, out string? why) ?? throw new InvalidOperationException(why);
        string? baseDir = SchematicCircuit.ReferenceBaseOfDirectory(CellFolder.SubFolderPath(staged.CellDir, ViewType.Schematic));
        var module = NetlistVerilogA.Write(ProtectPartition.Partition(text, "TSTFET100", baseDir), "TSTFET100");

        Assert.Equal(CmodelVerilogA.Write(CmodelFile.Read(source.CmodelPath!),
                                          new VerilogAOptions { ModuleName = "TSTFET100", SourceName = "TSTFET100.cmodel" }),
                     module.Text);
        Assert.Equal(["g", "d", "s"], module.Nets);
    }

    // R-vl17-3 over MCP: create what=libraryPart takes the .cmc as its cell, and the argv it becomes publishes the part.
    [Fact]
    public void OverMcp_ACmcIsTheCell_AndPublishesThePart()
    {
        string cmc = WriteModelDocument(VaTestModels.AnnModel(AnnActivation.Tanh));
        string lib = Path.Combine(_dir, "lib");
        var call = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(new { what = "libraryPart", cell = cmc, library = lib }))!.AsObject();
        var argv = CircuitRF.Cli.Serve.ToolCatalog.ToArgv("create", call, CircuitRF.Cli.Serve.PathRoot.Open(_dir, out _)!, out var refusal);
        Assert.Null(refusal);
        Assert.Equal(["export", "part", cmc], argv!.Take(3));

        var run = RunCliPublic([.. argv!]);
        Assert.True(run.Exit == 0, run.StdErr);
        Assert.True(File.Exists(Path.Combine(lib, "TSTFET100", ModelFolder.PartFileName)));
    }

    // ── fixtures ────────────────────────────────────────────────────────────────

    /// <summary>A saved active document, <c>TSTFET100.cmc</c>, in a workspace; with its model exported beside it after it
    /// unless <paramref name="exported"/> is false.</summary>
    private string WriteModelDocument(ComposedModel model, bool exported = true)
    {
        string ws = WorkspaceCreate.Create(_dir, "ws-" + Guid.NewGuid().ToString("N")[..6], null).WorkspaceDir;
        string cmc = Path.Combine(ws, "TSTFET100.cmc");
        CmcFile.Write(cmc, new CmcDocument { Kind = CmcKind.Active });
        if (exported)
        {
            string cmodel = ModelCellFromModel.DefaultCmodelPath(cmc);
            CmodelFile.Write(model, cmodel);
            File.SetLastWriteTimeUtc(cmodel, File.GetLastWriteTimeUtc(cmc).AddSeconds(1));
        }
        WorkspaceRootFinder.InvalidateCache();
        return cmc;
    }

    /// <summary>The cell a maker would draw: Pins g, d, s wired to a ComposedFET placing <paramref name="cmodel"/>.</summary>
    private string WriteHandDrawnCell(string cmodel)
    {
        string ws = WorkspaceCreate.Create(_dir, "ws-hand", null).WorkspaceDir;
        var sch = new SchematicEditModel();
        var g = Pin("P1", 1, "g", 0, 0);
        var d = Pin("P2", 2, "d", 6000, -2000);
        var s = Pin("P3", 3, "s", 6000, 4000);
        var q = new EditableComponent { InstanceName = "Q1", Symbol = SymbolKind.ComposedFET, X = 3000, Y = 0 };
        q.Parameters.Add(new EditableParameter { Name = "File", Expression = cmodel });
        foreach (var c in new[] { g, d, s, q }) sch.Components.Add(c);
        Wire(sch, g, 0, q, 0);
        Wire(sch, q, 1, d, 0);
        Wire(sch, q, 2, s, 0);
        return WriteCell(ws, "HAND", sch);
    }
}
