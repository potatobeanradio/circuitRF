using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Engine;
using CircuitRF.Ui.Schematic;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate tests for the <see cref="SymbolKind.SpiceModel"/> component — a SPICE <c>.model</c> card or
/// <c>.subckt</c> definition placed as a component and run from its own file, with no cell folder
/// anywhere.
///
/// <para><b>Two things have to be true at once, and they are the same fact seen twice.</b> The
/// SYMBOL is generated from the file (how many pins, in which order, drawn as what) and the
/// NETLIST is generated from the same file (which engine component, bound to which nets in which
/// order). If those two ever disagree the design is wired to something other than what it draws —
/// which simulates. So every test here that asserts one of them asserts the other against the same
/// fixture, and the extraction path's own pin-count guard is exercised directly.</para>
/// </summary>
public class SpiceModelComponentTests : IDisposable
{
    private readonly string _root;

    public SpiceModelComponentTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-spicemodel-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        SpiceModelPeek.InvalidateAll();
    }

    public void Dispose()
    {
        SpiceModelPeek.InvalidateAll();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Fixtures
    // ─────────────────────────────────────────────────────────────────────────

    private string Write(string fileName, string text)
    {
        string path = Path.Combine(_root, fileName);
        File.WriteAllText(path, text);
        return path;
    }

    private const string DiodeCard = ".model D1N4148 D (IS=2.52n RS=0.568 N=1.752 CJO=4p M=0.4 VJ=0.7)\n";

    private const string LadderSubckt = """
        .subckt lowpass in out gnd
        L1 in mid 10n
        C1 mid gnd 2p
        R1 mid out 50
        .ends
        """;

    /// <summary>A part built from a piece — the shape of every real vendor file.</summary>
    private const string PartOverPiece = """
        .subckt cell_core a b
        R1 a b 25
        .ends
        .subckt package p1 p2
        X1 p1 mid cell_core
        X2 mid p2 cell_core
        .ends
        """;

    private SchematicEditModel ModelWith(string file, string name = "", params (string Name, string Value)[] extra)
    {
        var model = new SchematicEditModel { SchematicDirectory = _root };
        var comp = new EditableComponent { InstanceName = "X1", Symbol = SymbolKind.SpiceModel, X = 0, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.SpiceModel, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });
        Set(comp, SpiceModelSymbolProvider.FileParameter, file);
        Set(comp, SpiceModelSymbolProvider.NameParameter, name);
        foreach (var (n, v) in extra) comp.Parameters.Add(new EditableParameter { Name = n, Expression = v });
        model.Components.Add(comp);
        return model;

        static void Set(EditableComponent c, string n, string v)
            => c.Parameters.First(p => p.Name == n).Expression = v;
    }

    private static EditableComponent Comp(SchematicEditModel m) => m.Components[0];

    private static Symbol ResolveSymbol(SchematicEditModel model)
    {
        var res = CellSymbolResolver.Resolve(Comp(model).ExternalSymbolRef!, model.SchematicDirectory);
        Assert.Equal(CellSymbolState.Resolved, res.State);
        return res.Symbol!;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Unconfigured
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FreshlyPlaced_DrawsAGenericTwoPort_AndIsWirable()
    {
        var model = ModelWith(file: "");

        // Resolved, not NotFound: an instance the user has not pointed anywhere yet is not a broken
        // reference. It has to be placeable and wirable while they decide, and the broken-reference
        // placeholder has no pins at all.
        var symbol = ResolveSymbol(model);
        Assert.Equal(2, symbol.Pins.Count);
        Assert.Equal([-200.0, 200.0], symbol.Pins.OrderBy(p => p.LocalX).Select(p => p.LocalX));
    }

    [Fact]
    public void FreshlyPlaced_RefusesToSimulate_ByName()
    {
        var r = NetExtractor.Extract(ModelWith(file: ""));
        Assert.Empty(r.TestBench.Instances);
        Assert.Contains(r.Conflicts, c => c.Contains("X1") && c.Contains("no SPICE model file is set"));
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  .model card
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ModelCard_DrawsAsTheCircuitRfDeviceThatImplementsIt()
    {
        Write("diode.model", DiodeCard);
        var symbol = ResolveSymbol(ModelWith("diode.model"));

        // The DIODE's own artwork and the diode's own two terminals — not a box. That is the whole
        // of what the dynamic symbol buys: a reader sees which lead is the anode without opening
        // anything.
        var expected = BuiltInSymbols.Primitives(SymbolKind.Diode, 2);
        Assert.Equal(expected.Pins.Select(p => (p.LocalX, p.LocalY, p.Name)),
                     symbol.Pins.Select(p => (p.LocalX, p.LocalY, p.Name)));
    }

    [Fact]
    public void ModelCard_EmitsThePrimitiveWithTheCardsParameters()
    {
        Write("diode.model", DiodeCard);
        var r = NetExtractor.Extract(ModelWith("diode.model"));

        var inst = Assert.Single(r.TestBench.Instances);
        Assert.Equal("Diode", inst.Reference);
        Assert.Equal(2, inst.NetBindings.Count);

        // In BASE SI, as the card states them — the trap ModelCardCellBuilder documents. A row
        // carrying the registry's convenience unit would read 4e-12 as picofarads of picofarads.
        var cj = Assert.Single(inst.Overrides.Where(o => o.Name.Equals("Cj0", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(4e-12, double.Parse(cj.Expression, System.Globalization.CultureInfo.InvariantCulture), 15);
    }

    [Fact]
    public void ModelCard_CircuitRfHasNoModelFor_IsRefusedByName_AndNeverApproximated()
    {
        // A type circuitRF has no model for. The refusal is the deliverable: a plausible wrong
        // device costs the user the measurement they built around it.
        Write("odd.model", ".model SOMETHING NPNX (IS=1e-16)\n");
        var model = ModelWith("odd.model");

        var res = CellSymbolResolver.Resolve(Comp(model).ExternalSymbolRef!, _root);
        Assert.Equal(CellSymbolState.PrimaryMissing, res.State);

        var r = NetExtractor.Extract(model);
        Assert.Empty(r.TestBench.Instances);
        Assert.Contains(r.Conflicts, c => c.Contains("X1"));
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  .subckt
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Subcircuit_DrawsABoxCarryingTheDefinitionsOwnPortNames()
    {
        Write("lp.subckt", LadderSubckt);
        var symbol = ResolveSymbol(ModelWith("lp.subckt"));

        Assert.Equal(3, symbol.Pins.Count);
        Assert.Equal(["in", "out", "gnd"], symbol.Pins.OrderBy(p => p.PortIndex).Select(p => p.Name));
    }

    [Fact]
    public void Subcircuit_EmitsACellInstance_AndTheCellIsTheFilesCircuit()
    {
        Write("lp.subckt", LadderSubckt);
        var r = NetExtractor.Extract(ModelWith("lp.subckt"));

        var inst = Assert.Single(r.TestBench.Instances);
        Assert.Equal("lowpass", inst.Reference);
        Assert.Equal(3, inst.NetBindings.Count);
        Assert.Equal(3, inst.NetBindings.Distinct().Count());

        var cell = r.Library.Find("lowpass");
        Assert.NotNull(cell);
        Assert.Equal(["in", "out", "gnd"], cell!.Ports);
        Assert.Equal(["C1", "L1", "R1"], cell.Instances.Select(i => i.InstanceName).OrderBy(n => n));
        Assert.Equal(["C", "L", "R"], cell.Instances.OrderBy(i => i.InstanceName).Select(i => i.Reference));
    }

    [Fact]
    public void Subcircuit_PinOrderIsPortOrder_SoPinKBindsToPortK()
    {
        Write("lp.subckt", LadderSubckt);
        var model = ModelWith("lp.subckt");
        var symbol = ResolveSymbol(model);
        var cell = NetExtractor.Extract(model).Library.Find("lowpass")!;

        // The one contract that cannot be checked by looking at either side alone: the symbol's
        // pin k and the cell's port k must name the same terminal, or the design is wired to a
        // circuit other than the one it draws — and it still simulates.
        Assert.Equal(cell.Ports, symbol.Pins.OrderBy(p => p.PortIndex).Select(p => p.Name).ToList());
    }

    [Fact]
    public void Subcircuit_NestedCalls_BringTheirDefinitionsWithThem()
    {
        Write("part.sp", PartOverPiece);
        var r = NetExtractor.Extract(ModelWith("part.sp", "package"));

        Assert.Equal("package", Assert.Single(r.TestBench.Instances).Reference);
        Assert.NotNull(r.Library.Find("package"));
        Assert.NotNull(r.Library.Find("cell_core"));
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Choosing which definition
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BlankName_ResolvesToTheHighestLevelDefinition_NotTheFirst()
    {
        // The file states the PIECE first, which is how vendor files are written. Taking the first
        // supported definition would place an internal element where the user asked for the part.
        Write("part.sp", PartOverPiece);
        var file = SpiceModelPeek.Read(Path.Combine(_root, "part.sp"));

        Assert.Equal("package", SpiceModelPeek.Select(file, "")!.Name);
        Assert.Equal("package", Assert.Single(NetExtractor.Extract(ModelWith("part.sp")).TestBench.Instances).Reference);
    }

    [Fact]
    public void BlankName_PrefersASubcircuitOverTheCardsThatSupportIt()
    {
        Write("both.lib", DiodeCard + "\n" + LadderSubckt);
        var file = SpiceModelPeek.Read(Path.Combine(_root, "both.lib"));
        Assert.Equal("lowpass", SpiceModelPeek.Select(file, "")!.Name);
    }

    [Fact]
    public void EveryDefinitionInTheFileIsOffered_SupportedOrNot()
    {
        Write("both.lib", DiodeCard + "\n" + PartOverPiece);
        var file = SpiceModelPeek.Read(Path.Combine(_root, "both.lib"));

        // Everything, including the piece and the card, because the combo has to let a user pick
        // one deliberately — the automatic choice is only the DEFAULT.
        Assert.Equal(["cell_core", "d1n4148", "package"],
                     file.Definitions.Select(d => d.Name.ToLowerInvariant())
                         .OrderBy(n => n, StringComparer.Ordinal));

        Assert.Contains(file.Definitions, d => d.Name == "cell_core" && !d.IsTopLevel);
        Assert.Contains(file.Definitions, d => d.Name == "package"   && d.IsTopLevel);
    }

    [Fact]
    public void ANameTheFileDoesNotDefine_IsReportedByName_AndListsWhatItDoes()
    {
        Write("lp.subckt", LadderSubckt);
        var model = ModelWith("lp.subckt", "highpass");

        Assert.Equal(CellSymbolState.PrimaryMissing,
                     CellSymbolResolver.Resolve(Comp(model).ExternalSymbolRef!, _root).State);

        var r = NetExtractor.Extract(model);
        Assert.Empty(r.TestBench.Instances);
        Assert.Contains(r.Conflicts, c => c.Contains("highpass") && c.Contains("lowpass"));
    }

    [Fact]
    public void AMissingFile_IsReported_AndNothingIsEmitted()
    {
        var r = NetExtractor.Extract(ModelWith("nowhere.subckt"));
        Assert.Empty(r.TestBench.Instances);
        Assert.Contains(r.Conflicts, c => c.Contains("X1") && c.Contains("nowhere.subckt"));
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Subcircuit parameters
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SubcircuitParameters_AreForwardedAsOverrides_AndOnlyOnesItDeclares()
    {
        Write("p.subckt", """
            .subckt res2 a b rval=25
            R1 a b {rval}
            .ends
            """);

        var r = NetExtractor.Extract(ModelWith("p.subckt", "res2",
            ("rval", "37"), ("notdeclared", "9")));

        var inst = Assert.Single(r.TestBench.Instances);
        Assert.Equal("37", Assert.Single(inst.Overrides.Where(o => o.Name == "rval")).Expression);

        // A subcircuit handed a parameter it never declared is an error in the elaborator, so it is
        // dropped here rather than allowed to reach it.
        Assert.DoesNotContain(inst.Overrides, o => o.Name == "notdeclared");
    }

    [Fact]
    public void PanelParameters_NeverReachTheNetlist()
    {
        Write("lp.subckt", LadderSubckt);
        var inst = Assert.Single(NetExtractor.Extract(ModelWith("lp.subckt")).TestBench.Instances);

        foreach (var name in new[] { "File", "Name", "PinConfig", "Pitch" })
            Assert.DoesNotContain(inst.Overrides, o => o.Name == name);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Artwork options
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PinsAndPitch_LayOutASubcircuitsBox_TheWayTheyLayOutAnSnP()
    {
        Write("wide.subckt", """
            .subckt wide a b c d e f
            R1 a b 1
            R2 c d 1
            R3 e f 1
            .ends
            """);

        var loose = ResolveSymbol(ModelWith("wide.subckt"));

        var tight = ModelWith("wide.subckt");
        Comp(tight).Parameters.First(p => p.Name == "Pitch").Expression = "Tight";
        var tightSym = ResolveSymbol(tight);

        Assert.Equal(6, loose.Pins.Count);
        Assert.Equal(6, tightSym.Pins.Count);

        double Span(Symbol s) => s.Pins.Max(p => p.LocalY) - s.Pins.Min(p => p.LocalY);
        Assert.True(Span(tightSym) < Span(loose),
            "Tight pitch must pack the rows closer, exactly as it does on an SnP.");
    }

    [Fact]
    public void ChangingPinsOrPitch_DoesNotChangeWhatIsSimulated()
    {
        Write("wide.subckt", """
            .subckt wide a b c d e f
            R1 a b 1
            R2 c d 1
            R3 e f 1
            .ends
            """);

        var standard = NetExtractor.Extract(ModelWith("wide.subckt"));

        var dual = ModelWith("wide.subckt");
        Comp(dual).Parameters.First(p => p.Name == "PinConfig").Expression = "DualRow";
        var dualR = NetExtractor.Extract(dual);

        Assert.Equal(standard.TestBench.Instances[0].Reference, dualR.TestBench.Instances[0].Reference);
        Assert.Equal(standard.TestBench.Instances[0].NetBindings.Count,
                     dualR.TestBench.Instances[0].NetBindings.Count);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  It actually simulates
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The acceptance test the whole component exists for: a <c>.subckt</c> placed as a component,
    /// wired between two ports, produces the S-parameters of the circuit the file wrote.
    ///
    /// <para><b>The oracle is a hand-authored testbench with no hierarchy in it at all</b> — the
    /// series R and L written directly as top-level instances. That is what makes it an oracle
    /// rather than a second reading of the same machinery: if the cell this component mints were
    /// wired transposed, or its ports bound in the wrong order, or its element values read at the
    /// wrong scale, the flat circuit would disagree and this would fail. Everything up to here
    /// checks the SHAPE of what is emitted; only this checks the numbers.</para>
    /// </summary>
    [Fact]
    public void ASubcircuitPlacedAsAComponent_SimulatesAsTheCircuitTheFileWrote()
    {
        Write("rl.subckt", """
            .subckt rl in out
            R1 in mid 50
            L1 mid out 5n
            .ends
            """);

        var extracted = NetExtractor.Extract(TwoPortAround("rl.subckt"));
        Assert.Empty(extracted.Conflicts);

        var authored = new TestBench("oracle");
        authored.Instances.Add(new Instance("P1", "Port", ["n1", "0"],
            [new ParameterAssignment("Num", "1"), new ParameterAssignment("Z", "50")]));
        authored.Instances.Add(new Instance("R1", "R",  ["n1", "nm"], [new ParameterAssignment("R", "50")]));
        authored.Instances.Add(new Instance("L1", "L",  ["nm", "n2"], [new ParameterAssignment("L", "5e-9")]));
        authored.Instances.Add(new Instance("P2", "Port", ["n2", "0"],
            [new ParameterAssignment("Num", "2"), new ParameterAssignment("Z", "50")]));

        double[] freqs = [1e9, 5e9, 10e9];

        var fromFile = SParameterEngine.Run(
            new Elaborator(extracted.Library).Elaborate(extracted.TestBench), freqs);
        var fromHand = SParameterEngine.Run(new Elaborator().Elaborate(authored), freqs);

        AssertSameSParameters(fromHand, fromFile);
    }

    /// <summary>The same acceptance, for a <c>.model</c> card: a diode's DC curve is the card's.</summary>
    [Fact]
    public void AModelCardPlacedAsAComponent_SimulatesAsTheCardsDevice()
    {
        Write("d.model", DiodeCard);

        var extracted = NetExtractor.Extract(TwoPortAround("d.model"));
        var inst = Assert.Single(extracted.TestBench.Instances.Where(i => i.InstanceName == "X1"));

        var elaborated = new Elaborator(extracted.Library).Elaborate(extracted.TestBench);
        var device = Assert.Single(elaborated.Components.Where(c => c.InstancePath == "X1"));

        // The elaborated device is the DIODE the card names, carrying the card's own saturation
        // current — not a default one, and not a stand-in.
        Assert.Equal("Diode", inst.Reference);
        Assert.Contains("Is", device.Parameters.Keys, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>P1 — X1 — P2, both terms grounded: the smallest thing that can be S-parameterised.</summary>
    private SchematicEditModel TwoPortAround(string file)
    {
        var model = ModelWith(file);
        Comp(model).X = 400;
        Comp(model).Y = 0;

        model.Components.Add(Term("P1", 100, 200, 1));
        model.Components.Add(new EditableComponent { InstanceName = "GP1", Symbol = SymbolKind.Ground, X = 100, Y = 400 });
        model.Components.Add(Term("P2", 800, 200, 2));
        model.Components.Add(new EditableComponent { InstanceName = "GP2", Symbol = SymbolKind.Ground, X = 800, Y = 400 });

        model.Wires.Add(Wire((100, 0), (200, 0)));    // P1 "+" to X1 pin 1
        model.Wires.Add(Wire((600, 0), (800, 0)));    // X1 pin 2 to P2 "+"
        return model;

        static EditableComponent Term(string name, double x, double y, int num)
        {
            var c = new EditableComponent { InstanceName = name, Symbol = SymbolKind.Term, X = x, Y = y };
            c.Parameters.Add(new EditableParameter { Name = "Num", Expression = num.ToString() });
            c.Parameters.Add(new EditableParameter { Name = "Z",   Expression = "50" });
            return c;
        }

        static EditableWire Wire(params (double X, double Y)[] pts)
        {
            var w = new EditableWire();
            w.Points.AddRange(pts);
            return w;
        }
    }

    private static void AssertSameSParameters(DataSet expected, DataSet actual, double tolerance = 1e-9)
    {
        Assert.Equal(expected.Cubes.Keys.OrderBy(k => k), actual.Cubes.Keys.OrderBy(k => k));

        foreach (var key in expected.Cubes.Keys)
        {
            var e = expected.Cubes[key];
            var a = actual.Cubes[key];
            Assert.Equal(e.DataKind, a.DataKind);

            if (e.DataKind == DataKind.Complex)
            {
                Assert.Equal(e.ComplexValues.Length, a.ComplexValues.Length);
                for (int i = 0; i < e.ComplexValues.Length; i++)
                    Assert.True((e.ComplexValues[i] - a.ComplexValues[i]).Magnitude <= tolerance,
                        $"Cube '{key}' index {i}: {e.ComplexValues[i]} vs {a.ComplexValues[i]}");
            }
            else
            {
                Assert.Equal(e.RealValues.Length, a.RealValues.Length);
                for (int i = 0; i < e.RealValues.Length; i++)
                    Assert.True(Math.Abs(e.RealValues[i] - a.RealValues[i]) <= tolerance,
                        $"Cube '{key}' index {i}: {e.RealValues[i]} vs {a.RealValues[i]}");
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Hierarchy
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ThereIsNoPopIn_BecauseThereIsNoSchematicToPushInto()
    {
        Write("lp.subckt", LadderSubckt);
        var model = ModelWith("lp.subckt");

        Assert.False(HierarchyResolverProbe.CanPushInto(Comp(model), model, out _));
    }

    /// <summary>Reaches <c>HierarchyResolver</c>, which is internal to the UI assembly.</summary>
    private static class HierarchyResolverProbe
    {
        public static bool CanPushInto(EditableComponent c, SchematicEditModel m, out string? why)
            => HierarchyResolver.CanPushInto(c, m, out why);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Persistence
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A saved SpiceModel comes back as one, still pointed at the same definition and still drawing
    /// the same pins — the symbol is DERIVED on load rather than stored, so there is no second copy
    /// of the interface to have gone stale in the file.
    /// </summary>
    [Fact]
    public void SurvivesACschRoundTrip_AndRedrawsItsPinsFromTheFile()
    {
        Write("lp.subckt", LadderSubckt);
        var model = ModelWith("lp.subckt", "lowpass");

        string path = Path.Combine(_root, "sheet.csch");
        SchematicPersistence.SaveToFile(path, model);
        var (loaded, _, _) = SchematicPersistence.LoadFromFile(path);
        loaded.SchematicDirectory = _root;

        var comp = Assert.Single(loaded.Components);
        Assert.Equal(SymbolKind.SpiceModel, comp.Symbol);
        Assert.Equal("lowpass", comp.TypeLabelText());

        var res = CellSymbolResolver.Resolve(comp.ExternalSymbolRef!, _root);
        Assert.Equal(CellSymbolState.Resolved, res.State);
        Assert.Equal(["in", "out", "gnd"], res.Symbol!.Pins.OrderBy(p => p.PortIndex).Select(p => p.Name));
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Two instances, two files
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TwoInstancesOfTheSameFile_ShareOneCell()
    {
        Write("lp.subckt", LadderSubckt);
        var model = ModelWith("lp.subckt");
        var second = new EditableComponent { InstanceName = "X2", Symbol = SymbolKind.SpiceModel, X = 1000, Y = 0 };
        foreach (var p in Comp(model).Parameters) second.Parameters.Add(p.Clone());
        model.Components.Add(second);

        var r = NetExtractor.Extract(model);
        Assert.Equal(2, r.TestBench.Instances.Count);
        Assert.Single(r.Library.Cells.Where(c => c.Name == "lowpass"));
    }

    [Fact]
    public void TwoFilesDefiningOneName_IsReported_NotSilentlyBoundToTheFirst()
    {
        Write("a.subckt", LadderSubckt);
        Write("b.subckt", """
            .subckt lowpass in out gnd
            R9 in out 1k
            R8 out gnd 1k
            .ends
            """);

        var model = ModelWith("a.subckt");
        var second = new EditableComponent { InstanceName = "X2", Symbol = SymbolKind.SpiceModel, X = 1000, Y = 0 };
        foreach (var p in Comp(model).Parameters) second.Parameters.Add(p.Clone());
        second.Parameters.First(p => p.Name == "File").Expression = "b.subckt";
        model.Components.Add(second);

        var r = NetExtractor.Extract(model);

        // The first instance is built; the second names the same definition out of a different file
        // and is refused rather than bound to the first one's circuit.
        Assert.Single(r.TestBench.Instances);
        Assert.Contains(r.Conflicts, c => c.Contains("X2") && c.Contains("two different SPICE files"));
    }
}
