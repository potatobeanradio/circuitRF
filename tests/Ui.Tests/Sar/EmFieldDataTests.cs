// ================================================================
//  EmFieldDataTests.cs — brief-cosim-pattern-2: the EM Field Data block. It stamps the same model as an SnP, reads saved data and
//  nothing else, refuses a result it cannot stand behind, and is the only block a Fields analysis takes.
// ================================================================

using CircuitRF.Core.Devices;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests.Sar;

[Collection(FieldCosimulationRunsCollection.Name)]
public sealed class EmFieldDataTests : IDisposable
{
    private const double Hz = 300e6;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-cp2-" + Guid.NewGuid().ToString("N")[..8]);
    private string Record => Path.Combine(_dir, "ems.cemr");
    private string Snp => Path.Combine(_dir, "ems.s2p");
    private string Npy => Path.Combine(_dir, "ems_em.npy");

    public EmFieldDataTests() => new MemoryFieldSet().WriteBeside(_dir, Hz, "stamp-1");

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private const string Around = "Port:P1 in 0 Num=1 Z=50 Ohm\nC:C1 p2 0 C=2e-12\n";
    private string Block => $"EmFieldData:EM1 in p2 File=\"{Record}\"\n";
    private const string Sweep = "analysis SP1 type=sparam start=200 stop=400 npts=11 Unit=MHz\n";
    private const string Fields = "analysis F1 type=fields block=EM1 freq=300 freqUnit=MHz drive=1 average=no\n";

    private RunResult Run(string text) => CircuitEvaluation.Evaluate(PreparedCircuit.FromText(text, _dir, _dir), new CircuitEvaluationRequest());

    [Fact]
    public void OnOneTouchstone_ItAndAnSnpGiveIdenticalS()
    {
        var block = Run(Around + Block + Sweep).Results[0].Data.Cubes["S"].ComplexValues;
        var snp = Run(Around + $"SnP:EM1 in p2 0 NumPorts=2 File=\"{Snp}\"\n" + Sweep).Results[0].Data.Cubes["S"].ComplexValues;
        Assert.Equal(snp.Select(v => (BitConverter.DoubleToInt64Bits(v.Real), BitConverter.DoubleToInt64Bits(v.Imaginary))),
                     block.Select(v => (BitConverter.DoubleToInt64Bits(v.Real), BitConverter.DoubleToInt64Bits(v.Imaginary))));
    }

    [Fact]
    public void ItReadsSavedDataOnly_SoADeletedSetupChangesNothing()
    {
        string setup = Path.Combine(_dir, "ems.cem");
        EmSetupPersistence.SaveToFile(setup, new EmSetup());
        var (record, _) = EmResultRecord.Load(Record);
        record!.Setup = EmSetupPersistence.ToEmbedded(new EmSetup());
        record.SetupSource = new EmResultSetupSource { File = "ems.cem" };
        File.WriteAllText(Record, record.Serialize());
        var withSetup = Run(Around + Block + Fields);
        Assert.Equal(RunStatus.Success, withSetup.Status);

        File.Delete(setup);
        var without = Run(Around + Block + Fields);
        Assert.True(without.Status == RunStatus.Success, without.StatusMessage);
        Assert.Equal(withSetup.Results[0].Data.Cubes[FieldCosimulation.DeliveredCube].RealValues, without.Results[0].Data.Cubes[FieldCosimulation.DeliveredCube].RealValues);
    }

    [Fact]
    public void ASetupThatHasMovedOnAddsOneNote_AndNeverAWarning()
    {
        string setup = Path.Combine(_dir, "ems.cem");
        var (record, _) = EmResultRecord.Load(Record);
        record!.Setup = EmSetupPersistence.ToEmbedded(new EmSetup { Name = "then" });
        record.SetupSource = new EmResultSetupSource { File = "ems.cem" };
        File.WriteAllText(Record, record.Serialize());

        EmSetupPersistence.SaveToFile(setup, new EmSetup { Name = "now" });
        var run = Run(Around + Block + Fields);
        Assert.Equal(RunStatus.Success, run.Status);
        Assert.Single(run.Notes, n => n.Contains("its setup has changed") && n.Contains("EM1"));
        Assert.DoesNotContain(run.Warnings, w => w.Contains("setup has changed"));

        var (settings, whyNot) = EmFieldBlocks.SettingsOf(record, Record);
        Assert.Null(whyNot);
        var name = settings.Single(s => s.Differs && s.Field == "Name");
        Assert.Equal(("then", "now"), (name.Recorded, name.Now));
        Assert.True(settings[0].Differs, "what differs is listed first");

        record.Setup = null;
        Assert.Equal(EmResultRecord.SettingsNotRecorded, EmFieldBlocks.SettingsOf(record, Record).WhyNot);

        // the panel's one line: when, which solver and quality, the sweep, the port count
        record.Solver = new EmResultSolver { Name = "Palace" };
        record.Setup = System.Text.Json.JsonDocument.Parse("""{"Palace":{"Quality":"Draft"}}""").RootElement;
        string summary = record.Summary();
        Assert.Contains("Palace Draft", summary);
        Assert.Contains("180 MHz to 420 MHz, 5 points, 2 ports", summary);
    }

    private void HeldRefusal(string text, params string[] names)
    {
        var plan = Run(Around + text + Sweep);
        Assert.Equal(RunStatus.EngineError, plan.Status);
        Assert.Contains("EM Field Data 'EM1'", plan.StatusMessage);
        foreach (string name in names) Assert.Contains(name, plan.StatusMessage);
    }

    [Fact]
    public void ARecordItCannotStandBehindIsRefused_NamingTheBlockAndTheFile()
    {
        HeldRefusal($"EmFieldData:EM1 in p2 File=\"{Path.Combine(_dir, "nope.cemr")}\"\n", "no result record", "nope.cemr");
        HeldRefusal($"EmFieldData:EM1 in p2 File=\"{Snp}\"\n", "not a result record", "ems.s2p");

        // a port count the line does not wire
        HeldRefusal($"EmFieldData:EM1 in p2 p3 p4 File=\"{Record}\"\n", "4 net(s)", "2 port(s)", "ems.cemr");

        // stamps that disagree, saying which
        string kept = File.ReadAllText(Snp);
        File.WriteAllText(Snp, kept.Replace("stamp-1", "stamp-2"));
        HeldRefusal(Block, "'ems.s2p' is from another run (stamp stamp-2)");
        File.WriteAllText(Snp, kept);

        // truncated: the Touchstone, the .npy and a field file
        File.WriteAllLines(Snp, File.ReadAllLines(Snp).SkipLast(2));
        HeldRefusal(Block, "ems.s2p", "truncated");
        File.WriteAllText(Snp, kept);

        byte[] npy = File.ReadAllBytes(Npy);
        File.WriteAllBytes(Npy, npy[..^16]);
        HeldRefusal(Block, "ems_em.npy", "truncated");
        File.WriteAllBytes(Npy, npy);

        string e = Path.Combine(_dir, "ems.fields", PortFieldSet.FieldFile(1, 1, 'E'));
        byte[] field = File.ReadAllBytes(e);
        File.WriteAllBytes(e, field[..^8]);
        HeldRefusal(Block, PortFieldSet.FieldFile(1, 1, 'E'), "truncated");
        File.WriteAllBytes(e, field);

        // missing
        File.Delete(Npy);
        HeldRefusal(Block, "ems_em.npy", "missing");
        File.WriteAllBytes(Npy, npy);

        // nothing an SnP does not have: says where its S-parameters go and which setting keeps fields
        var (record, _) = EmResultRecord.Load(Record);
        record!.Files.Fields = null;
        record.Frequencies.FieldSetHz = [];
        File.WriteAllText(Record, record.Serialize());
        HeldRefusal(Block, "neither a far field nor a field set", "SnP", "Keep per-port fields");
    }

    [Fact]
    public void AHierarchicalBlocksPathResolves_AndIsListed()
    {
        string text = $"""
            Port:P1 in 0 Num=1 Z=50 Ohm
            COIL:X1 in
            define COIL (a)
              {Block.TrimEnd().Replace(" in p2 ", " a p2 ")}
              C:C1 p2 0 C=2e-12
            end
            analysis F1 type=fields block=X1.EM1 freq=300 freqUnit=MHz drive=1 average=no
            """;
        var (lib, tb) = new CnlReader().Read(text, "t", _dir);
        Assert.Equal(["X1.EM1"], EmFieldBlocks.Paths(lib, tb));
        var run = Run(text);
        Assert.True(run.Status == RunStatus.Success, run.StatusMessage);
        Assert.True(run.Results[0].Data.Cubes[FieldCosimulation.DeliveredCube].RealValues[0] > 0);
    }

    [Fact]
    public void ASchematicRoundTripsThroughTheNetlistAndRuns()
    {
        string text = Around + Block + Fields;
        var (lib, tb) = new CnlReader().Read(text, "t", _dir);
        var drawn = NetlistSchematic.Build(lib, tb, _dir);
        var block = drawn.Schematic.Components.Single(c => c.Symbol == SymbolKind.EmFieldData);
        Assert.Equal(2, block.PortCount);
        drawn.Schematic.SchematicDirectory = _dir;

        string cnl = SchematicCircuit.CnlTextOf(drawn.Schematic, "t");
        string line = cnl.Split('\n').Single(l => l.StartsWith("EmFieldData:EM1", StringComparison.Ordinal));
        Assert.DoesNotContain("NumPorts", line);
        Assert.Contains("ems.cemr", line);

        double direct = Run(text).Results[0].Data.Cubes[FieldCosimulation.DeliveredCube].RealValues[0];
        double through = Run(cnl).Results[0].Data.Cubes[FieldCosimulation.DeliveredCube].RealValues[0];
        Assert.Equal(direct, through);
    }

    [Fact]
    public void APlainSnpIsRefused_AndTheChangeIsOneUndoableClick()
    {
        // a Touchstone with a .npy and no record: what a saved document from before this block holds
        File.Delete(Record);
        string snpLine = $"SnP:EM1 in p2 0 NumPorts=2 File=\"{Snp}\"\n";
        var refused = Run(Around + snpLine + Fields);
        Assert.Equal(RunStatus.EngineError, refused.Status);
        Assert.Contains("is an SnP", refused.StatusMessage);
        Assert.Contains("EM Field Data block", refused.StatusMessage);
        Assert.Contains("ems.cemr", refused.StatusMessage);
        Assert.False(File.Exists(Record), "a refusal writes nothing");
        var plain = Assert.Single(refused.PlainSnpFieldsBlocks);

        var (lib, tb) = new CnlReader().Read(Around + snpLine + Fields, "t", _dir);
        var drawn = NetlistSchematic.Build(lib, tb, _dir);
        drawn.Schematic.SchematicDirectory = _dir;
        var vm = new SchematicViewModel(drawn.Schematic, messageSink: null);
        var offer = ChangeToEmFieldDataCommand.For(vm, plain);
        Assert.NotNull(offer);
        offer!.Apply();

        var comp = drawn.Schematic.Components.Single(c => c.InstanceName == "EM1");
        Assert.Equal(SymbolKind.EmFieldData, comp.Symbol);
        Assert.True(File.Exists(Record));
        var run = Run(SchematicCircuit.CnlTextOf(drawn.Schematic, "t"));
        Assert.True(run.Status == RunStatus.Success, run.StatusMessage);

        vm.UndoRedo.Undo();
        Assert.Equal(SymbolKind.Snp, comp.Symbol);
        Assert.Contains(comp.Parameters, p => p.Name == "File" && p.Expression.Contains(".s2p", StringComparison.Ordinal));

        // no .npy, so nothing to adopt: the refusal stands with no offer
        File.Delete(Record);
        File.Delete(Npy);
        Assert.Null(ChangeToEmFieldDataCommand.For(vm, plain));
    }

    [Fact]
    public async Task ThePickerTakesARecord_AndOffersToAdoptAnOlderResult()
    {
        var editor = new ParameterEditorViewModel();
        Assert.Equal(Record, await editor.RecordFor(Record));
        Assert.Equal(Record, await editor.RecordFor(Snp));                     // a record beside it is that record

        File.Delete(Record);
        editor.ConfirmAdoptAsync = (_, _) => Task.FromResult(false);
        Assert.Null(await editor.RecordFor(Snp));
        Assert.False(File.Exists(Record));

        editor.ConfirmAdoptAsync = (snp, record) => Task.FromResult(snp == "ems.s2p" && record == "ems.cemr");
        Assert.Equal(Record, await editor.RecordFor(Snp));
        Assert.True(File.Exists(Record));

        File.Delete(Record);
        File.Delete(Npy);
        Assert.Null(await editor.RecordFor(Snp));
        Assert.Contains("No .npy matches", editor.EmFieldSummary);
    }

    [Fact]
    public void TheShippedExampleIsMoved()
    {
        string root = Em3d.PalaceBackendTests.RepoRoot();
        var (model, _, _) = SchematicPersistence.LoadFromFile(Path.Combine(root, "examples", "SAR MRI Loop Coil", "Coil Bench", "schematic", "Coil Bench.csch"));
        var block = model.Components.Single(c => c.InstanceName == "EM1");
        Assert.Equal(SymbolKind.EmFieldData, block.Symbol);
        Assert.Equal(4, block.PortCount);
        Assert.Equal("results/Loop Coil CoSim.openems.cemr", block.Parameters.Single(p => p.Name == "File").Expression);
    }
}
