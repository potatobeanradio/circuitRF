using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using CircuitRF.Cli;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using CircuitRF.Ui.Schematic;
using RfCore.Data;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Tests.Footprints;

/// <summary>
/// brief-footprint-2-instance-parameter.md §7 — the gate for the <c>Footprint</c> instance
/// parameter, the combobox, the third label, and the elaborator's blindness to it.
///
/// <para><b>Where this file lives</b>, and why it is not <c>tests/Design.Tests/</c>: the same reason
/// <see cref="ChipLandPatternTests"/> gives. There is no such project, every <c>src/Design</c>
/// feature is tested from here, and half of this brief is in <c>src/Ui</c> anyway.</para>
/// </summary>
public sealed class FootprintParameterTests : IDisposable
{
    private readonly string _root;

    public FootprintParameterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-footprint2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    // ══ 1. Round trip ═══════════════════════════════════════════════════════════════════════════
    //
    // R-fp2-1: an ordinary entry in Parameters, so it saves and loads with everything else — and
    // R-fp2-1b: absent means None, with no default written on load.

    [Fact]
    public void AFootprintRoundTripsThroughTheCschAndAnAbsentOneStaysAbsent()
    {
        var model = new SchematicEditModel();
        model.Components.Add(WithFootprint(Comp("R1",  SymbolKind.Resistor), "smt:0402@N"));
        model.Components.Add(WithFootprint(Comp("S2P1", SymbolKind.Snp),     "smt:0603@L"));
        model.Components.Add(WithFootprint(Comp("X1",  SymbolKind.Srlc),     "footprints/my-part.clay"));
        model.Components.Add(Comp("R2", SymbolKind.Resistor));   // no footprint at all
        model.Components[0].ShowFootprintLabel = true;

        var (restored, _, _) = SchematicPersistence.Deserialize(
            SchematicPersistence.Serialize(model, "Cell", 0, 0, 1.0));

        Assert.Equal("smt:0402@N",               restored.Components[0].Footprint);
        Assert.Equal("smt:0603@L",               restored.Components[1].Footprint);
        Assert.Equal("footprints/my-part.clay",  restored.Components[2].Footprint);
        Assert.True(restored.Components[0].ShowFootprintLabel);

        // R-fp2-1b: nothing was written for the component that had none, and nothing was invented
        // on the way back in.
        Assert.Null(restored.Components[3].Footprint);
        Assert.False(restored.Components[3].ShowFootprintLabel);
        Assert.DoesNotContain("Footprint", ParametersJsonOf(model, "R2"), StringComparison.Ordinal);
        // R-fp2-5a: the flag is written only when it was turned on, so an untouched .csch gains
        // nothing at all.
        Assert.DoesNotContain("ShowFootprintLabel",
            SchematicPersistence.Serialize(OneResistor(), "Cell", 0, 0, 1.0), StringComparison.Ordinal);
    }

    // ══ 2. The default is applied where it should be, and nowhere else ══════════════════════════
    //
    // R-fp2-3. One table over the one function that decides (R-fp2-3e). A component with a CellRef —
    // a placed cell or a kit part — carries the placeholder kind Generic, which is why the cell and
    // kit rows are spelled that way rather than needing files on disk to exist.

    public static TheoryData<SymbolKind, bool> DefaultTable => new()
    {
        { SymbolKind.Resistor,  true  },   // the discrete RLC family, on a board
        { SymbolKind.Capacitor, true  },
        { SymbolKind.Inductor,  true  },
        { SymbolKind.Src,       true  },   // one of the six two-element RLC parts
        { SymbolKind.Mlin,      false },   // a registered PCell generator owns its artwork
        { SymbolKind.Mklopf,    false },
        { SymbolKind.Snp,       false },   // realisation not knowable from the schematic
        { SymbolKind.SpiceModel,false },
        { SymbolKind.Sdd,       false },
        { SymbolKind.Generic,   false },   // a cell reference, and a kit part
        { SymbolKind.Term,      false },
        { SymbolKind.Srlc,      false },   // a three-element branch is a network, not a part
    };

    [Theory]
    [MemberData(nameof(DefaultTable))]
    public void TheDefaultIs0201ForTheDiscreteRlcFamilyOnABoardAndNoneEverywhereElse(
        SymbolKind kind, bool expectsDefault)
    {
        var pcb  = ShippedTechnologies.Load("pcb-2layer_FR-4_70mil_1oz");
        var mmic = ShippedTechnologies.Load("mmic-GaAs_2LM_100um");

        Assert.Equal(expectsDefault ? "smt:0201@N" : null, FootprintDefaults.For(kind, pcb));

        // R-fp2-3b/3c: None on anything that is not a board, for every kind alike — and None with
        // no technology at all, which is what a loose .csch has.
        Assert.Null(FootprintDefaults.For(kind, mmic));
        Assert.Null(FootprintDefaults.For(kind, null));
    }

    [Fact]
    public void PlacingAPartOnABoardWorkspaceActuallyWritesTheDefault()
    {
        // The table above tests the decision; this tests that the placement path asks it. A default
        // that is correct and never called is the defect R-fp2-3e's "one function" is about.
        var vm = VmInWorkspace("pcb-2layer_FR-4_70mil_1oz");
        vm.CommitPlacement(SymbolKind.Capacitor, 0, SymbolRotation.R0, 0, 0);
        vm.CommitPlacement(SymbolKind.Mlin,      0, SymbolRotation.R0, 1000, 0);

        Assert.Equal("smt:0201@N", vm.EditModel.Components[0].Footprint);
        Assert.Null(vm.EditModel.Components[1].Footprint);

        // A parameter, not a field: it lives in Parameters like PinConfig and Form, and it is not
        // drawn as a parameter row — the footprint has its own label (R-fp2-5).
        var p = vm.EditModel.Components[0].Parameters
            .Single(q => q.Name.Equals("Footprint", StringComparison.OrdinalIgnoreCase));
        Assert.False(p.ShowOnSchematic);
    }

    // ══ 3. The default is never re-applied ══════════════════════════════════════════════════════

    [Fact]
    public void AnExplicitNoneSurvivesAReopenAndAChangeOfTechnology()
    {
        // R-fp2-3d. A design that changes when you open it somewhere else is the failure this is
        // about: None is stored as ABSENT (which is what None means, R-fp2-1b), so the only way it
        // could come back as 0201 is something re-deriving the default on load or on a tech change.
        var vm = VmInWorkspace("mmic-GaAs_2LM_100um");
        vm.CommitPlacement(SymbolKind.Resistor, 0, SymbolRotation.R0, 0, 0);
        Assert.Null(vm.EditModel.Components[0].Footprint);

        string json = SchematicPersistence.Serialize(vm.EditModel, "Cell", 0, 0, 1.0);

        // Same document, reopened in a workspace whose technology IS a board.
        string boardDir = MakeWorkspace("pcb-2layer_FR-4_70mil_1oz");
        var (reopened, _, _) = SchematicPersistence.Deserialize(json);
        reopened.SchematicDirectory = boardDir;
        _ = new SchematicViewModel(reopened);

        Assert.Null(reopened.Components[0].Footprint);
    }

    // ══ 4. Nothing electrical ever sees it ══════════════════════════════════════════════════════

    [Fact]
    public void TheSameDesignRunsToABitIdenticalDataSetWithAndWithoutFootprints()
    {
        // R-fp2-6b: the gate is a SIMULATION, not an inspection. An assertion about a dictionary's
        // contents proves the dictionary; this proves the invariant.
        var bare  = SeriesResistorBench(withFootprints: false);
        var dress = SeriesResistorBench(withFootprints: true);

        // The netlist a run consumes really does carry it (R-fp2-6d) — otherwise the comparison
        // below would be between two identical inputs and would prove nothing.
        Assert.Contains("Footprint=smt:0402@N", dress.Cnl, StringComparison.Ordinal);
        Assert.DoesNotContain("Footprint", bare.Cnl, StringComparison.Ordinal);
        Assert.Equal("smt:0402@N", FootprintOverrideOf(dress.Tb, "R1"));

        double[] freqs = [1e9, 2e9, 5e9];
        var a = SParameterEngine.Run(dress.Netlist, freqs);
        var b = SParameterEngine.Run(bare.Netlist,  freqs);

        var namesA = a.Cubes.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.Equal(namesA, b.Cubes.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList());
        foreach (string name in namesA)
        {
            var ca = a.Cubes[name];
            var cb = b.Cubes[name];
            Assert.Equal(ca.DataKind, cb.DataKind);
            if (ca.DataKind == DataKind.Complex)
                Assert.Equal<IEnumerable<Complex>>(ca.ComplexValues, cb.ComplexValues);
            else
                Assert.Equal<IEnumerable<double>>(ca.RealValues, cb.RealValues);
        }

        // And the elaborated netlist never held it either — the drop is before resolution, not a
        // value that happened to resolve to something harmless.
        var r1 = dress.Netlist.Components.Single(c => c.InstancePath == "R1");
        Assert.DoesNotContain(r1.Parameters.Keys,
            k => k.Equals("Footprint", StringComparison.OrdinalIgnoreCase));
    }

    // ══ 5. The combobox shows a selection ═══════════════════════════════════════════════════════

    [Fact]
    public void ThePanelOpensOnTheStoredCaseWithNominalDensity()
    {
        // R-fp2-4d — the wBond round-6 defect is exactly this control shape, so the assertion is
        // "non-blank immediately after the panel is built", not "eventually correct".
        var (vm, comp) = PanelOver("smt:0603@N");

        Assert.True(vm.FootprintIndex > 0);
        Assert.Contains("0603", vm.FootprintOptions[vm.FootprintIndex], StringComparison.Ordinal);
        // R-fp2-4b: the row reads its metric twin and its millimetres. A row reading only "0603" is
        // the defect the 2.4x imperial/metric collision is about.
        Assert.Equal("0603 (metric 1608)   1.60 x 0.80 mm", vm.FootprintOptions[vm.FootprintIndex]);
        Assert.Equal(0, vm.FootprintDensityIndex);            // Nominal, pre-selected
        Assert.True(vm.IsFootprintDensityEnabled);

        // R-fp2-4a: None first, Custom… last.
        Assert.Equal(ParameterEditorViewModel.FootprintNoneRow,   vm.FootprintOptions[0]);
        Assert.Equal(ParameterEditorViewModel.FootprintCustomRow, vm.FootprintOptions[^1]);

        // One editor, not two: the generic rows do not also offer a `Footprint` text box, which
        // would be a second write path and a place to type a reference nothing can resolve.
        Assert.DoesNotContain(vm.Rows, r => r.Name.Equals("Footprint", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(vm.Rows, r => r.Name == "R");

        // R-fp2-4c: the density writes the '@' suffix of the SAME parameter — there is no second one.
        vm.FootprintDensityIndex = 2;
        Assert.Equal("smt:0603@L", comp.Footprint);
        Assert.Single(comp.Parameters, p => p.Name.Equals("Footprint", StringComparison.OrdinalIgnoreCase));
    }

    // ══ 6. An unresolvable stored value survives ════════════════════════════════════════════════

    [Fact]
    public void AStoredValueTheListCannotShowIsKeptAsAnExtraRowAndWrittenBackUnchanged()
    {
        // R-fp2-4e: a picker that erases what it cannot display is a picker that loses work.
        var (vm, comp) = PanelOver("smt:9999");

        Assert.Contains("smt:9999", vm.FootprintOptions[vm.FootprintIndex], StringComparison.Ordinal);
        Assert.Contains("unresolved", vm.FootprintOptions[vm.FootprintIndex], StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.IsFootprintDensityEnabled);   // no case, so no density to choose

        // Saving without touching the combobox writes it back exactly as it was.
        var (restored, _, _) = SchematicPersistence.Deserialize(
            SchematicPersistence.Serialize(vm.SchematicVm!.EditModel, "Cell", 0, 0, 1.0));
        Assert.Equal("smt:9999", restored.Components[0].Footprint);
        Assert.Equal("smt:9999", comp.Footprint);
    }

    // ══ 7. Label offsets do not shift ═══════════════════════════════════════════════════════════

    [Fact]
    public void TheFootprintLabelIsAppendedSoEveryStoredLabelOffsetStillMeansWhatItDid()
    {
        // R-fp2-5c. Inserting at index 2 would shift every stored parameter-label offset in every
        // .csch ever written — a hundred silent one-label-out-of-place bugs. This loads a document
        // written before footprints existed, hand-dragged labels and all, and asserts nothing moved.
        var before = new SchematicEditModel();
        var r = Comp("R1", SymbolKind.Resistor);
        r.Parameters.Add(new EditableParameter { Name = "R", Expression = "50", Unit = "Ohm", ShowOnSchematic = true });
        r.LabelOffsets.AddRange([(10, 20), (30, 40), (50, 60)]);   // type, name, R — all hand-placed
        before.Components.Add(r);

        var (reloaded, _, _) = SchematicPersistence.Deserialize(
            SchematicPersistence.Serialize(before, "Cell", 0, 0, 1.0));
        var plain = reloaded.Components[0].ToRenderComponent();
        Assert.Equal(["R", "R1", "R = 50 Ohm"], plain.Labels);
        Assert.Equal([(10.0, 20.0), (30.0, 40.0), (50.0, 60.0)], plain.LabelOffsets);

        // Now give that very component a footprint and turn the label on. The three existing rows
        // keep their index, their text and their offset; the new row goes on the END.
        var withFp = reloaded.Components[0];
        withFp.Parameters.Add(new EditableParameter { Name = "Footprint", Expression = "smt:0402@N" });
        withFp.ShowFootprintLabel = true;

        var drawn = withFp.ToRenderComponent();
        Assert.Equal(["R", "R1", "R = 50 Ohm", "0402"], drawn.Labels);
        Assert.Equal([(10.0, 20.0), (30.0, 40.0), (50.0, 60.0)], drawn.LabelOffsets);

        // R-fp2-5b: the case code ALONE. "smt:0402@N" is machine spelling on a human drawing.
        Assert.DoesNotContain("smt:", drawn.Labels[3], StringComparison.Ordinal);
        // R-fp2-5d: nothing is drawn when the footprint is None, whatever the toggle says.
        withFp.Parameters.RemoveAll(p => p.Name == "Footprint");
        Assert.Equal(3, withFp.ToRenderComponent().Labels.Count);
        // A Custom .clay draws its file name without the extension; a workspace cell its cell name.
        Assert.Equal("my-part",  EditableComponent.FootprintDisplayName("footprints/my-part.clay"));
        Assert.Equal("chip-0402", EditableComponent.FootprintDisplayName("cells/chip-0402"));
    }

    // ══ 8. The .cnl round trip ══════════════════════════════════════════════════════════════════

    [Fact]
    public void TheNetlistCarriesTheFootprintThroughAWriteAndAReadUnchanged()
    {
        // R-fp2-6d: `circuitrf netlist` writes the .cnl a run consumes, and a footprint dropped
        // there is a footprint a headless Update Layout could not see. This is the CLI's own
        // extraction (CircuitSource.CnlTextOf — the one function the verb calls), not a second one.
        var model = new SchematicEditModel { SchematicDirectory = _root };
        BuildSeriesResistor(model, 25.0);
        Apply(model, "R1", "smt:0402@N");
        // A path with a space in it: a .cnl is whitespace-delimited, so this is the value that
        // would otherwise make the reader treat "parts/Pattern.clay" as a unit and refuse the line.
        Apply(model, "P1", "my parts/Pattern.clay");

        string cnl = CircuitSource.CnlTextOf(model, "tb");
        var (_, tb) = new CnlReader().Read(cnl, "tb", _root);

        Assert.Equal("smt:0402@N",             FootprintOverrideOf(tb, "R1"));
        Assert.Equal("my parts/Pattern.clay",  FootprintOverrideOf(tb, "P1"));
    }

    // ── fixtures ──────────────────────────────────────────────────────────────

    private static EditableComponent Comp(string name, SymbolKind kind)
        => new() { InstanceName = name, Symbol = kind };

    private static EditableComponent WithFootprint(EditableComponent c, string value)
    {
        c.Parameters.Add(new EditableParameter { Name = "Footprint", Expression = value });
        return c;
    }

    private static SchematicEditModel OneResistor()
    {
        var m = new SchematicEditModel();
        m.Components.Add(Comp("R1", SymbolKind.Resistor));
        return m;
    }

    private static string ParametersJsonOf(SchematicEditModel model, string instanceName)
    {
        var one = new SchematicEditModel();
        one.Components.Add(model.Components.Single(c => c.InstanceName == instanceName));
        return SchematicPersistence.Serialize(one, "Cell", 0, 0, 1.0);
    }

    private static void Apply(SchematicEditModel model, string instanceName, string value)
        => model.Components.Single(c => c.InstanceName == instanceName)
                .Parameters.Add(new EditableParameter { Name = "Footprint", Expression = value });

    private static string? FootprintOverrideOf(TestBench tb, string instanceName)
        => tb.Instances.Single(i => i.InstanceName == instanceName)
             .Overrides.FirstOrDefault(o => o.Name.Equals("Footprint", StringComparison.OrdinalIgnoreCase))
             ?.Expression;

    private sealed record Bench(string Cnl, TestBench Tb, ElaboratedNetlist Netlist);

    private Bench SeriesResistorBench(bool withFootprints)
    {
        var model = new SchematicEditModel { SchematicDirectory = _root };
        BuildSeriesResistor(model, 25.0);
        if (withFootprints)
            // "on every component": the Terms and the Grounds too, which is the point — the drop is
            // by NAME and for every kind, not per family.
            foreach (var c in model.Components)
                c.Parameters.Add(new EditableParameter { Name = "Footprint", Expression = "smt:0402@N" });

        string cnl = CircuitSource.CnlTextOf(model, "tb");
        var (lib, tb) = new CnlReader().Read(cnl, "tb", _root);
        return new Bench(cnl, tb, new Elaborator(lib).Elaborate(tb));
    }

    /// <summary>Port 1 — R — port 2, wired on the schematic grid. A Term at (x, y) puts "+" at
    /// (x, y-200) and "−" at (x, y+200); an unrotated resistor puts its two pins at the same
    /// offsets, so every wire below is one segment.</summary>
    private static void BuildSeriesResistor(SchematicEditModel model, double ohms)
    {
        model.Components.Add(Term("P1", 100, 200, 1));
        model.Components.Add(Gnd("GP1", 100, 400));
        var r = Comp("R1", SymbolKind.Resistor);
        r.X = 300; r.Y = 200;
        r.Parameters.Add(new EditableParameter
            { Name = "R", Expression = ohms.ToString(CultureInfo.InvariantCulture) });
        model.Components.Add(r);
        model.Components.Add(Term("P2", 500, 600, 2));
        model.Components.Add(Gnd("GP2", 500, 800));

        model.Wires.Add(Wire((100, 0), (300, 0)));
        model.Wires.Add(Wire((300, 400), (500, 400)));

        static EditableComponent Term(string name, double x, double y, int num)
        {
            var c = new EditableComponent { InstanceName = name, Symbol = SymbolKind.Term, X = x, Y = y };
            c.Parameters.Add(new EditableParameter { Name = "Num", Expression = num.ToString(CultureInfo.InvariantCulture) });
            c.Parameters.Add(new EditableParameter { Name = "Z",   Expression = "50" });
            return c;
        }

        static EditableComponent Gnd(string name, double x, double y)
            => new() { InstanceName = name, Symbol = SymbolKind.Ground, X = x, Y = y };

        static EditableWire Wire(params (double X, double Y)[] pts)
        {
            var w = new EditableWire();
            w.Points.AddRange(pts);
            return w;
        }
    }

    private string MakeWorkspace(string technologyId)
        => WorkspaceCreate.Create(_root, "ws-" + technologyId, technologyId).WorkspaceDir;

    private SchematicViewModel VmInWorkspace(string technologyId)
        => new(new SchematicEditModel { SchematicDirectory = MakeWorkspace(technologyId) });

    /// <summary>A parameter editor bound to one component carrying <paramref name="stored"/>, built
    /// the way the Properties inspector builds it — so the assertions are about the panel as it
    /// first appears, which is what R-fp2-4d is about.</summary>
    private (ParameterEditorViewModel Vm, EditableComponent Comp) PanelOver(string stored)
    {
        var model = new SchematicEditModel();
        var comp  = WithFootprint(Comp("R1", SymbolKind.Resistor), stored);
        comp.Parameters.Add(new EditableParameter { Name = "R", Expression = "50", Unit = "Ohm" });
        model.Components.Add(comp);

        var vm = new ParameterEditorViewModel();
        vm.SetTargetDirect(new SchematicViewModel(model), comp);
        return (vm, comp);
    }
}
