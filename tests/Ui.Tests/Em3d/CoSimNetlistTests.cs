using System.Globalization;
using System.Text;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Mom;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

// brief-emb-1 R-emb1-7 — the co-simulation netlist: the board's N-port, every gap-port part from its port's node to ground, a
// two-port part file measured in series wired node-and-ground, a connector and the 3D view's own ports as Port lines, a part
// with no model a named placeholder — and the file elaborates.
public sealed class CoSimNetlistTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("emb1-cosim-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void TheNetlist_WiresEachPartFromItsNodeToGround_AndElaborates()
    {
        Touchstone(Path.Combine(_root, "results", "board.s5p"), 5);
        Touchstone(Path.Combine(_root, "part.s2p"), 2);

        var ports = new List<EmCoSimPort>
        {
            new(1, "P1", "50", null),
            new(2, "C1", "50", Component("C1")),
            new(3, "L1", "50", Component("L1")),
            new(4, "J1", "50", Component("J1") with { Connector = true }),
            new(5, "R5", "50", Component("R5")),
        };
        var models = new EmCoSimModels(new Dictionary<string, Instance>(StringComparer.OrdinalIgnoreCase)
        {
            ["C1"] = new("C1", "C", ["a", "b"], [new ParameterAssignment("C", "10", "pF")]),
            // A series-measured part file: two ports, port 1 to port 2 across the part.
            ["L1"] = new("L1", "SnP", ["x", "y"], [new ParameterAssignment("NumPorts", "2"), new ParameterAssignment("File", "part.s2p")]),
        }, null, null);
        var setup = new EmSetup { Name = "board Driven", Frequency = new FrequencySpec("1", "3", 3, SweepKind.Linear, "GHz", "GHz") };

        string text = EmCoSimNetlist.Text(setup, "results/board.s5p", ports, models);
        string[] lines = [.. text.Split('\n').Select(l => System.Text.RegularExpressions.Regex.Replace(l.Trim(), @"\s+", " "))];

        Assert.Contains("SnP:BOARD p1 p2 p3 p4 p5 NumPorts=5 File=\"results/board.s5p\"", lines);
        Assert.Contains("C:C1 p2 0 C=10 pF", lines);
        Assert.Contains(lines, l => l.StartsWith("SnP:L1 p3 0 ", StringComparison.Ordinal));
        Assert.Contains("Port:P1 p1 0 Num=1 Z=50 Ohm", lines);
        Assert.Contains("Port:P2 p4 0 Num=2 Z=50 Ohm", lines);
        Assert.Contains("R:R5 p5 0 R=R5_value", lines);
        Assert.Contains(lines, l => l.StartsWith("; R5: the board's schematic gives no model", StringComparison.Ordinal));
        // Never between two port nodes.
        Assert.DoesNotContain(lines, l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^\w+:\w+ p\d+ p\d+") && !l.StartsWith("SnP:BOARD", StringComparison.Ordinal));

        string cnl = Path.Combine(_root, "board_cosim.cnl");
        File.WriteAllText(cnl, text);
        var (lib, tb) = CnlReader.ReadFile(cnl);
        var netlist = new Elaborator(lib).Elaborate(tb);
        Assert.Equal(1 + 2 + 3, tb.Instances.Count);      // the board, two Port lines, three parts
        Assert.NotNull(netlist);
    }

    private static C3dComponentPort Component(string refdes)
        => new(refdes, "1", "2", new C3dPort { Name = refdes }, 300e-6, 500e-6, true, [], null, false) { PartPins = ["1", "2"] };

    private static void Touchstone(string path, int n)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sb = new StringBuilder("# GHz S RI R 50\n");
        foreach (double f in new[] { 1.0, 2.0, 3.0 })
        {
            sb.Append(f.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < n * n; i++) sb.Append(i % (n + 1) == 0 ? " 0.1 0" : " 0.5 0");
            sb.Append('\n');
        }
        File.WriteAllText(path, sb.ToString());
    }
}
