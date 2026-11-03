using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests.VendorLibraries;

/// <summary>
/// A Verilog-A model's <c>Symbol</c> (brief-vendorlib-7): the FET glyph bound to the model's own terminal names. The
/// models here are synthetic — what is under test is the drawing and the extraction, both of which read only the
/// instance's stored <c>Pins</c>, <c>Terminals</c> and <c>Symbol</c>, so no model file is needed.
/// </summary>
public sealed class VerilogASymbolTests
{
    private static EditableComponent Model(string terminals, int pins, string? symbol)
    {
        var comp = new EditableComponent { InstanceName = "X1", Symbol = SymbolKind.VerilogA, X = 2000, Y = 2000 };
        comp.Parameters.Add(new EditableParameter { Name = "File", Expression = "/models/dev.osdi" });
        comp.Parameters.Add(new EditableParameter { Name = "Model", Expression = "dev" });
        comp.Parameters.Add(new EditableParameter { Name = "Pins", Expression = pins.ToString() });
        comp.Parameters.Add(new EditableParameter { Name = "Terminals", Expression = terminals, ShowOnSchematic = false });
        if (symbol is not null)
            comp.Parameters.Add(new EditableParameter { Name = "Symbol", Expression = symbol, ShowOnSchematic = false });
        return comp;
    }

    /// <summary>The glyph the instance draws: its pins in local coordinates, each with the model terminal it stands for.</summary>
    private static IReadOnlyList<(double X, double Y, int Terminal, string? Name)> Pins(EditableComponent comp)
    {
        if (comp.ExternalSymbolRef is { } r)
            return [.. CellSymbolResolver.Resolve(r, null).Symbol!.Pins.Select(p => (p.LocalX, p.LocalY, p.PortIndex, p.Name))];
        return [.. SymbolPortDefs.For(SymbolKind.VerilogA, comp.PortCount)
                    .Select((d, i) => ((double)d.LocalX, (double)d.LocalY, i, (string?)null))];
    }

    /// <summary>
    /// Wires each pin outward to a probe named after the MODEL TERMINAL that pin stands for, and returns the
    /// extracted netlist line of X1 — so two symbols wired "the same way" means the same net on the same terminal.
    /// </summary>
    private static (string Line, IReadOnlyList<string> Conflicts) Extract(EditableComponent comp, string[] netOf)
    {
        var m = new SchematicEditModel();
        m.Components.Add(comp);
        foreach (var (x, y, terminal, _) in Pins(comp))
        {
            var (wx, wy) = SchematicGeometry.LocalToWorld((float)x, (float)y, comp.X, comp.Y, comp.Rotation, comp.MirrorX);
            (double dx, double dy) = x < 0 ? (-600, 0) : x > 0 ? (600, 0) : (0, y < 0 ? -600 : 600);
            var w = new EditableWire();
            w.Points.AddRange([(wx, wy), (wx + dx, wy + dy)]);
            m.Wires.Add(w);
            // A probe's pin sits at (-100, +100) from its body origin.
            m.Components.Add(new EditableComponent
                { InstanceName = netOf[terminal], Symbol = SymbolKind.VProbe, X = wx + dx + 100, Y = wy + dy - 100 });
        }

        var result = NetExtractor.Extract(m);
        string line = CnlWriter.Write(result.TestBench)
                               .Split('\n')
                               .Single(l => l.TrimStart().StartsWith("VerilogA:X1", StringComparison.Ordinal));
        return (line, result.Conflicts);
    }

    // ── 1. The gate: the same netlist through either symbol ──────────────────

    [Fact]
    public void ABoxAndAFetGlyph_WiredToTheSameTerminals_ExtractTheSameLine()
    {
        string[] nets = ["n_drain", "n_gate", "n_source"];   // the model declares d g s

        var box = Model("d g s", 3, "VerilogA");
        var fet = Model("d g s", 3, "");
        Assert.Null(box.ExternalSymbolRef);
        Assert.NotNull(fet.ExternalSymbolRef);

        var (boxLine, _) = Extract(box, nets);
        var (fetLine, _) = Extract(fet, nets);

        Assert.Equal(boxLine, fetLine);
        Assert.Contains("X1  n_drain  n_gate  n_source ", fetLine);
        // Artwork never reaches the netlist line.
        Assert.DoesNotContain("Symbol", fetLine);
        Assert.DoesNotContain("Terminals", fetLine);
    }

    /// <summary>The glyph's gate is where the gate is drawn: the FET's left lead, whatever the model's order.</summary>
    [Fact]
    public void TheFetGlyphsGateLead_IsTheModelsGateTerminal()
    {
        var pins = Pins(Model("d g s", 3, ""));
        Assert.Equal(1, pins.Single(p => p.X < 0).Terminal);          // gate, LEFT → model terminal 1
        Assert.Equal(0, pins.Single(p => p.Y < 0).Terminal);          // drain, TOP → terminal 0
        Assert.Equal(2, pins.Single(p => p.Y > 0 && p.X == 0).Terminal);
        Assert.All(pins, p => Assert.Null(p.Name));                    // bound by name: no labels
    }

    // ── 2. FetPinBinding ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("d g s",               1, 0, 2, null)]
    [InlineData("gate drain source",   0, 1, 2, null)]
    [InlineData("g d s dt",            0, 1, 2, FetPinRole.Thermal)]
    [InlineData("d g s b",             1, 0, 2, FetPinRole.Bulk)]
    public void NamesThatSayWhichIsTheGate_Bind(string names, int gate, int drain, int source, FetPinRole? fourth)
    {
        var b = FetPinBinding.For(names.Split(' '), []);
        Assert.NotNull(b);
        Assert.Equal((gate, drain, source, fourth), (b!.Gate, b.Drain, b.Source, b.FourthRole));
    }

    [Fact]
    public void NamesThatDoNot_DoNotBind()
    {
        Assert.Null(FetPinBinding.For(["a", "b", "c"], []));
        // A fourth terminal the model declares thermal binds whatever it is called.
        Assert.Equal(FetPinRole.Thermal, FetPinBinding.For(["d", "g", "s", "th"], [3])!.FourthRole);
    }

    // ── 3. The default ───────────────────────────────────────────────────────

    [Fact]
    public void TheDefault_IsTheFetGlyphOnlyWhenTheNamesBind()
    {
        Assert.Equal(SymbolKind.FetCurtice, VerilogASymbolProvider.DefaultKind(3, "g d s"));
        Assert.NotNull(Model("g d s", 3, "").ExternalSymbolRef);

        Assert.Null(VerilogASymbolProvider.DefaultKind(3, "a b c"));
        Assert.Null(Model("a b c", 3, "").ExternalSymbolRef);              // the box

        Assert.Null(VerilogASymbolProvider.DefaultKind(5, "d g s b dt:thermal"));
        Assert.Null(Model("d g s b dt:thermal", 5, "").ExternalSymbolRef);

        // An instance saved before Symbol existed keeps the box its wires were drawn on.
        Assert.Null(Model("g d s", 3, null).ExternalSymbolRef);
    }

    // ── 5. An explicit FET on names that do not bind ─────────────────────────

    [Fact]
    public void AnExplicitFetOnUnboundNames_TakesThemInOrder_LabelsThem_AndWarnsOnce()
    {
        var comp = Model("a b c", 3, "FetCurtice");
        var pins = Pins(comp).OrderBy(p => p.Terminal).ToList();
        Assert.Equal(["a", "b", "c"], pins.Select(p => p.Name));

        var (_, conflicts) = Extract(comp, ["n_a", "n_b", "n_c"]);
        var said = Assert.Single(conflicts, c => c.Contains("do not name gate, drain and source"));
        Assert.Contains("(a, b, c)", said);
    }
}
