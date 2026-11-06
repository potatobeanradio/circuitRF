// ================================================================
//  FarFieldCosimTests.Cell.cs — brief-cosim-pattern-6's routine gates, on the same small result: the netlist's block, the <setup>_cosim
//  cell against the same circuit written by hand, an edited cell left alone, the check on a part between two gap ports, the analysis
//  editor's frequencies, and the source scan that keeps one drawing.
// ================================================================

using System.Text.Json;
using System.Text.RegularExpressions;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.ThreeD;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.ViewModels;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests.Sar;

public sealed partial class FarFieldCosimTests
{
    private static C3dComponentPort Part(string refdes) => new(refdes, "1", "2", new C3dPort { Name = refdes }, 300e-6, 500e-6, true, [], null, false) { PartPins = ["1", "2"] };

    private static IReadOnlyList<EmCoSimPort> BoardPorts =>
        [new(1, "FEED", "50", null), new(2, "L1", "50", Part("L1")), new(3, "C1", "50", Part("C1"))];

    private static EmCoSimModels BoardModels => new(new Dictionary<string, Instance>(StringComparer.OrdinalIgnoreCase)
    {
        ["L1"] = new("L1", "L", ["a", "b"], [new ParameterAssignment("L", "5", "nH")]),
        ["C1"] = new("C1", "C", ["a", "b"], [new ParameterAssignment("C", "1", "pF")]),
    }, null, null);

    private static EmSetup BoardSetup => new()
    {
        Name = "ems", SnpOutputPathOverride = "ems", CreateSnpSchematic = true,
        Frequency = new FrequencySpec("1", "2", 3, SweepKind.Linear, "GHz", "GHz"),
    };

    private string BoardText()
    {
        File.WriteAllText(Path.Combine(_dir, ".cws"), "{}");
        return EmCoSimNetlist.Text(BoardSetup, "ems.s3p", BoardPorts, BoardModels, EmCoSimNetlist.RecordFor(Record, _dir));
    }

    [Fact]
    public void TheNetlist_IsAnEmFieldDataBlockWhenTheRunKeptFields_WithF1AtTheStoredFrequencyNearestTheCentre_AndAnSnPOtherwise()
    {
        WriteResult();
        string text = BoardText();
        Assert.Matches(@"EmFieldData:BOARD\s+p1\s+p2\s+p3\s+File=""ems.cemr""", text);
        Assert.Contains("p2 is L1", text, StringComparison.Ordinal);
        Assert.Contains("analysis F1 type=fields block=BOARD freq=\"1\" freqUnit=GHz drive=1", text, StringComparison.Ordinal);
        Assert.Contains("1 GHz, 2 GHz", text, StringComparison.Ordinal);

        WriteResult(farField: false);
        Assert.Null(EmCoSimNetlist.RecordFor(Record, _dir));
        Assert.Contains("SnP:BOARD", EmCoSimNetlist.Text(BoardSetup, "ems.s3p", BoardPorts, BoardModels), StringComparison.Ordinal);
    }

    [Fact]
    public void TheCell_GivesTheCubesOfTheSameCircuitByHand_AndItsDisplayReadsThem_AndAnEditedCellIsLeftAlone()
    {
        WriteResult();
        string text = BoardText();
        var made = EmCoSimNetlist.CreateCell(_dir, BoardSetup, text, Path.Combine(_dir, "ems.s3p"), Record);
        Assert.True(made.Outcome == EmSnpSchematicOutcome.Created, made.Sentence);

        var run = CircuitEvaluation.Evaluate(PreparedCircuit.FromSchematic(made.SchematicPath!), new CircuitEvaluationRequest { Measurements = false });
        Assert.True(run.Status == RunStatus.Success, run.StatusMessage);
        var hand = Run("Port:P1 p1 0 Num=1 Z=50 Ohm\nL:L1 p2 0 L=5 nH\nC:C1 p3 0 C=1 pF\n" + Block("p1", "p2", "p3") +
                       "analysis SP1 type=sparam start=1 stop=2 npts=3 Unit=GHz\nanalysis F1 type=fields block=EM1 freq=1 freqUnit=GHz drive=1\n");
        Assert.Equal(hand.GroupedResults!["F1.farfield.U"].RealValues, run.GroupedResults!["F1.farfield.U"].RealValues);
        Assert.Equal(hand.GroupedResults!["SP1.S"].ComplexValues, run.GroupedResults!["SP1.S"].ComplexValues);

        string display = Directory.EnumerateFiles(_dir, "*.cdd", SearchOption.AllDirectories).Single();
        var plots = JsonSerializer.Deserialize<DataDisplayConfig>(File.ReadAllText(display))!.Tabs[0].Plots;
        Assert.Equal([PlotType.Rect, PlotType.Polar, PlotType.Polar, PlotType.Table], plots.Select(p => p.PlotType));
        foreach (var trace in plots.SelectMany(p => p.Traces))
            Assert.Equal(run.GroupedResults![trace.CubeName!].Axes.Select(a => a.Name), trace.CubeSlice.Select(a => a.AxisName));

        File.AppendAllText(made.SchematicPath!, " ");
        string edited = File.ReadAllText(made.SchematicPath!);
        var again = EmCoSimNetlist.CreateCell(_dir, BoardSetup, text, Path.Combine(_dir, "ems.s3p"), Record);
        Assert.Equal(EmSnpSchematicOutcome.Current, again.Outcome);
        Assert.Equal(edited, File.ReadAllText(made.SchematicPath!));
    }

    private IReadOnlyList<GapPortBridge> Bridges(string circuit, params string?[] kinds)
    {
        var (record, _) = EmResultRecord.Load(Record);
        for (int k = 0; k < kinds.Length; k++) record!.Ports[k].Kind = kinds[k];
        File.WriteAllText(Record, record!.Serialize());
        var (lib, tb) = new CnlReader().Read(circuit + Block("p1", "p2", "p3"), "tb", _dir);
        using var nl = new Elaborator(lib) { BaseDirectory = _dir }.Elaborate(tb);
        return EmFieldBlocks.GapPortBridges(nl);
    }

    [Fact]
    public void Check_FlagsAPartBetweenTwoGapPorts_NotOneOnARealReturn_NorOnARecordWithNoKinds()
    {
        WriteResult();
        const string Between = "Port:P1 p1 0 Num=1 Z=50 Ohm\nL:L1 p2 p3 L=5 nH\n";
        var found = Assert.Single(Bridges(Between, EmResultPort.Plane, EmResultPort.Gap, EmResultPort.Gap));
        Assert.Equal(("L1", "EM1", "P2", "P3"), (found.Part, found.Block, found.PortA, found.PortB));
        Assert.Empty(Bridges("Port:P1 p1 0 Num=1 Z=50 Ohm\nL:L1 p1 p2 L=5 nH\nC:C1 p3 0 C=1 pF\n", EmResultPort.Plane, EmResultPort.Gap, EmResultPort.Gap));
        Assert.Empty(Bridges(Between, null, null, null));
    }

    [Fact]
    public void TheEditor_OffersTheBlocksStoredFrequenciesAndAll_AndGreysNormAndAverageWithNoFieldSet()
    {
        var vm = new FieldsBodyViewModel { FreqUnit = "GHz", DataSource = () =>
            [new EmFieldBlockData("EM1", [1e9, 2.45e9], HasFarField: true, HasFieldSet: false),
             new EmFieldBlockData("EM2", [298e6], HasFarField: false, HasFieldSet: true)] };
        Assert.True(vm.HasSarFields);       // no block named: nothing is known to grey for
        vm.Block = "EM1";
        Assert.Equal(["1", "2.45", "All"], vm.Frequencies);
        Assert.False(vm.HasSarFields);
        vm.FreqUnit = "MHz";
        Assert.Equal(["1000", "2450", "All"], vm.Frequencies);
        vm.Block = "EM2";
        Assert.Equal(["298"], vm.Frequencies);
        Assert.True(vm.HasSarFields);

        vm.Freq = "All";
        Assert.True(vm.BuildAnalysis("F1", true).AtEveryFrequency);
    }

    [Fact]
    public void OneDrawing_TheVerbAndTheRunBothCallNetlistSchematic()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "circuitRF.slnx"))) root = Path.GetDirectoryName(root)!;
        foreach (string file in new[] { "src/Cli/NetlistToSchematic.cs", "src/Design/Layout/Em/EmCoSimNetlist.cs" })
        {
            string code = Regex.Replace(Regex.Replace(File.ReadAllText(Path.Combine(root, file)), @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");
            Assert.Contains("NetlistSchematic.Build(", code, StringComparison.Ordinal);
            Assert.DoesNotContain("SchematicAutoRouter", code, StringComparison.Ordinal);
            Assert.DoesNotContain("Wires.Add", code, StringComparison.Ordinal);
            Assert.DoesNotContain("Components.Add", code, StringComparison.Ordinal);
        }
    }
}
