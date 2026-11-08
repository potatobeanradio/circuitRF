// Gates for brief-de-3: a Deembed placed in the schematic editor extracts as the netlist type, draws back from a .cnl, shows its two
// sides facing each other, and refuses a file it cannot use.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Netlist;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class DeembedSchematicTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("deembed-sch-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private sealed class Sink : IMessageSink
    {
        public List<(MessageLevel Level, string Text)> Posted { get; } = [];
        public void Post(MessageLevel level, string text, string? filePath = null) => Posted.Add((level, text));
        public void Clear() => Posted.Clear();
    }

    /// <summary>A Deembed as the palette places it, pointed at <paramref name="file"/>.</summary>
    private static EditableComponent Placed(int ports, string file)
    {
        var comp = new EditableComponent { InstanceName = "DE1", Symbol = SymbolKind.Deembed };
        foreach (var d in ComponentTypeRegistry.DefaultParameters(SymbolKind.Deembed, ports))
            comp.Parameters.Add(new EditableParameter { Name = d.Name, Expression = d.Expression, ShowOnSchematic = d.ShowOnSchematic });
        comp.Parameters.Single(p => p.Name == "File").Expression = file;
        return comp;
    }

    private static EditableWire Stub(double x, double y)
    {
        var w = new EditableWire();
        w.Points.AddRange([(x, y), (x < 0 ? x - 200 : x + 200, y)]);
        return w;
    }

    [Fact]
    public void ItsTwoSidesFaceEachOther_PortKAcrossFromPortHalfPlusK()
    {
        var pins = Placed(6, "x.s6p").GetEffectiveSnpPortDefs();
        for (int k = 1; k <= 3; k++)
        {
            var left  = pins.Single(p => p.Name == $"{k}");
            var right = pins.Single(p => p.Name == $"{k + 3}");
            Assert.True(left.LocalX < 0 && right.LocalX > 0, $"port {k} left, port {k + 3} right");
            Assert.Equal(left.LocalY, right.LocalY);
        }
    }

    [Fact]
    public void APlacedDeembed_ExtractsAsTheNetlistType_AndDrawsBackFromIt()
    {
        var model = new SchematicEditModel();
        var comp = Placed(4, "fixtures/ext.s4p");
        model.Components.Add(comp);
        foreach (var (_, x, y) in comp.GetEffectiveSnpPortDefs()) model.Wires.Add(Stub(x, y));

        string cnl = SchematicCircuit.CnlTextOf(model, "tb");
        string line = cnl.Split('\n').Single(l => l.StartsWith("Deembed:DE1", StringComparison.Ordinal));
        Assert.Matches(@"^Deembed:DE1(\s+n\d+){4}\s", line.Trim());
        Assert.Contains("File=\"fixtures/ext.s4p\"", line);
        Assert.Contains("NumPorts=4", line);
        Assert.DoesNotContain("SdB", line);

        var (lib, tb) = new CnlReader().Read(cnl, "tb", _dir);
        var drawn = NetlistSchematic.Build(lib, tb, _dir).Schematic!.Components.Single(c => c.InstanceName == "DE1");
        Assert.Equal(SymbolKind.Deembed, drawn.Symbol);
        Assert.Equal(4, drawn.PortCount);
    }

    [Fact]
    public void AFileWithAnOddPortCount_IsRefused_AndTheSymbolKeepsItsPins()
    {
        string s3p = Path.Combine(_dir, "three.s3p");
        File.WriteAllText(s3p, "# GHz S RI R 50\n1 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
        var sink = new Sink();
        var model = new SchematicEditModel();
        var comp = Placed(2, "");
        model.Components.Add(comp);
        var vm = new SchematicViewModel(model, sink);

        string? said = ParameterEditorViewModel.SetSnpFile(vm, comp, s3p);

        Assert.Contains("even number of ports", said);
        Assert.Equal(2, comp.PortCount);
        Assert.Equal("", comp.Parameters.Single(p => p.Name == "File").Expression);
        Assert.Contains(sink.Posted, m => m.Level == MessageLevel.Warning && m.Text.StartsWith("DE1:", StringComparison.Ordinal));
    }
}
