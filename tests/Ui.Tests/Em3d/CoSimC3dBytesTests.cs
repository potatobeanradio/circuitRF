using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Mom;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

// brief-lcp-7 gate 1 — the writer reads ports now (EmCoSimPort, each part an EmCoSimPart), not a C3dDocument and a
// C3dComponentPortBuild. A .c3d's netlist is byte-identical to what the writer made before: the expected text below is the old
// writer's output on these inputs, captured before the change. The inputs cover a 3D view port, a renumbered one, a two-pad part,
// a multi-pad part's two ports, a connector and a refused part.
public sealed class CoSimC3dBytesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("lcp7-c3d-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void TheC3dNetlist_IsByteIdenticalToBefore()
    {
        var setup = new EmSetup { Name = "board Driven", Frequency = new FrequencySpec("1", "3", 3, SweepKind.Linear, "GHz", "GHz") };
        var document = new C3dDocument
        {
            Ports = [new C3dPort { Number = 1, Name = "IN", Z0 = "50" }, new C3dPort { Number = 7, Name = "OUT", Z0 = "75" }],
        };
        static C3dComponentPort Part(string rd, string pin, string reference, int number, string z0, bool connector, string[] pins)
            => new(rd, pin, reference, new C3dPort { Number = number, Name = pins.Length > 2 ? $"{rd}.{pin}" : rd, Z0 = z0 }, 300e-6, 500e-6, true, [], null, connector)
            { PartPins = pins, SchematicId = rd, Nets = ("N" + number, "GND") };
        var build = new C3dComponentPortBuild(
        [
            Part("C1", "1", "2", 2, "50", false, ["1", "2"]),
            Part("U1", "IN", "GND", 3, "50", false, ["IN", "GND", "OUT"]),
            Part("U1", "OUT", "GND", 4, "25", false, ["IN", "GND", "OUT"]),
            Part("J1", "1", "2", 5, "50", true, ["1", "2"]),
            new C3dComponentPort("R9", null, null, null, 0, 0, false, [], "R9 is refused.", false),
        ], null!, [], [], [], null);
        Em3dPort Problem(int number, int? source = null)
            => new(number, "p", "a", "b", default, default, default, new Complex(50, 0), new Em3dReferencePlane(default, default, 0)) { SourceNumber = source };
        var problem = new[] { Problem(1), Problem(2), Problem(3), Problem(4), Problem(5), Problem(6, 7) };
        string snp = Path.Combine(_root, "results", "board.s6p");
        var r = EmCoSimNetlist.Write(_root, setup, snp, null, EmCoSimNetlist.PortsOf(problem, document, build));
        Assert.Equal(EmSnpSchematicOutcome.Created, r.Outcome);
        Assert.Equal(Expected.Replace("\r\n", "\n"), File.ReadAllText(r.Path!).Replace("\r\n", "\n"));
        Assert.Equal("Wrote board Driven_cosim.cnl: the board's S6P with 3 part port(s) closed by their models, ready to simulate.", r.Sentence);
    }

    /// <summary>The netlist's comments land on the cell's schematic as ONE text, a line per comment, not a text per line.</summary>
    [Fact]
    public void TheCellsNotes_AreOneMultiLineText()
    {
        var model = new CircuitRF.Design.Schematic.SchematicEditModel();
        model.Components.Add(new CircuitRF.Design.Schematic.EditableComponent { Symbol = SymbolKind.Resistor, X = 0, Y = 0 });
        EmCoSimNetlist.AddNotes(model, "; first line\n; second line\n;\n; third line\n\nR:R1 a 0 R=50\n");
        var note = Assert.IsType<CircuitRF.Design.Schematic.EditableText>(Assert.Single(model.CanvasObjects));
        Assert.Equal("first line\nsecond line\nthird line", note.Text);
        Assert.True(note.GetBoundingBox().MaxY <= -300 + 1e-9);   // above the drawing, as the lines were
    }

    private const string Expected = """
        ; Co-simulation of 'board Driven': the board's 6-port with each part across its own component port (written by
        ; circuitRF from the run). A gap port measures the board with the part's pads open, so each part stands from its
        ; port's node to ground, never between two port nodes.
        ; BOARD's ports: p1 is IN, p2 is C1, p3 is U1.IN, p4 is U1.OUT, p5 is J1, p6 is OUT.
        ; C1: the board's schematic gives no model for it, so C1_value is a placeholder; set it to the part's value.
        ; U1_IN: the board's schematic gives no model for it, so U1_IN_value is a placeholder; set it to the part's value.
        ; U1_OUT: the board's schematic gives no model for it, so U1_OUT_value is a placeholder; set it to the part's value.

        C1_value = 1 pF
        U1_IN_value = 1e9 Ohm
        U1_OUT_value = 1e9 Ohm

        SnP:BOARD  p1  p2  p3  p4  p5  p6  NumPorts=6  File="results/board.s6p"
        Port:P1  p1  0  Num=1  Z=50 Ohm
        C:C1  p2  0  C=C1_value
        R:U1_IN  p3  0  R=U1_IN_value
        R:U1_OUT  p4  0  R=U1_OUT_value
        Port:P2  p5  0  Num=2  Z=50 Ohm
        Port:P3  p6  0  Num=3  Z=75 Ohm

        analysis SP1 type=sparam start="1" startUnit=GHz stop="3" stopUnit=GHz npts=3

        """;
}
