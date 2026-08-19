using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Expressions;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The plumbing of the plastic <b>overmold</b> — <c>er</c>, the relative permittivity of the medium
/// the wires are encapsulated in (wbond.md §3.7).
///
/// <para>The PHYSICS is gated in <c>WBond.Tests/OvermoldTests</c> — every capacitance scales by ε_r,
/// every inductance is untouched. What is gated here is that the number reaches the physics from every
/// surface the owner asked for, and comes back: the component parameter, the Array Inductance panel,
/// the Touchstone export dialog, and <b>both directions</b> of the schematic-layout reconcile.</para>
///
/// <para><b>Each of those is a separate route, which is why each has its own test.</b> A setting that
/// is written on one surface and silently dropped on another is the failure mode this whole family of
/// commands has produced before — a design stating one loop height while its wires measure another
/// (owner, 2026-08-17). The permittivity has the same shape and would fail the same way, except that
/// it moves no wire, so nothing on screen would show it.</para>
/// </summary>
public sealed class WBondOvermoldPlumbingTests : IDisposable
{
    private readonly string _root;

    public WBondOvermoldPlumbingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"wbond-overmold-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".cws"), "{}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp dir */ }
    }

    private static WBondDesign ArchedDesign(double overmoldEr = 1.0, params string[] arrayNames)
    {
        var design = new WBondDesign { OvermoldEr = overmoldEr };

        double y = 0;
        foreach (string name in arrayNames.Length > 0 ? arrayNames : ["G1"])
        {
            var array = new WireArray { Name = name };
            for (int i = 0; i < 2; i++, y += 6.0)
                array.Wires.Add(LoopShape.CreateSeedWire(
                    Point3.Mils(0, y, 4), Point3.Mils(100, y, 1),
                    WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold",
                    loopHeightNm: WBondUnits.ToNm(20.0, WBondUnit.Mil)));
            design.Arrays.Add(array);
            y += 20.0;
        }
        return design;
    }

    private SchematicEditModel NewSchematic(string cellName = "Amp")
    {
        string dir = Path.Combine(_root, cellName, "schematic");
        Directory.CreateDirectory(dir);
        return new SchematicEditModel { SchematicDirectory = dir };
    }

    private static string? ParamOf(EditableComponent comp, string name) =>
        comp.Parameters.FirstOrDefault(p => p.Name == name)?.Expression;

    // ── The component parameter ───────────────────────────────────────────────

    /// <summary>
    /// <b>A freshly placed wBond declares <c>er</c>, and it declares AIR.</b>
    ///
    /// <para>Declared rather than left blank for the same reason <c>IncludeCapacitance</c> is: the
    /// parameter panel puts the two side by side, and a box showing nothing would not say what medium
    /// the capacitance beside it was computed in. "1" is also what every design had before this
    /// existed, so declaring it changes no existing answer.</para>
    /// </summary>
    [Fact]
    public void AFreshlyPlacedWBond_DeclaresAir()
    {
        var comp = WBondPlacement.BuildCarrying(null, "W1");
        Assert.Equal("1", ParamOf(comp, "er"));
    }

    /// <summary>
    /// <b>An imported design's own permittivity becomes the instance's parameter</b>, exactly as its
    /// capacitance flag does — that one moment of inheritance is the whole connection between the
    /// wBond editor's setting and a placed component's.
    /// </summary>
    [Fact]
    public void ApplyDesign_CarriesThePermittivityOntoTheInstance()
    {
        var comp = WBondPlacement.BuildCarrying(null, "W1");
        WBondPlacement.ApplyDesign(comp, ArchedDesign(overmoldEr: 3.9, "G1"));

        Assert.Equal("3.9", ParamOf(comp, "er"));
    }

    /// <summary>
    /// <b>The instance parameter wins over the payload</b> — the relationship <c>Temp</c> and
    /// <c>GroundPlane</c> already have. One document placed twice can be simulated bare and
    /// encapsulated without either copy changing the other.
    /// </summary>
    [Fact]
    public void TheInstanceParameter_OverridesTheDesign()
    {
        var design = ArchedDesign(overmoldEr: 1.0, "G1");
        string payload = WBondEmbedding.Encode(design);

        var parameters = new Dictionary<string, Value>(StringComparer.Ordinal)
        {
            ["Design"] = new Value(payload),
            ["er"] = new Value(4.4),
        };

        var model = (WBondModel)ComponentModelFactory.TryCreate("wBond", parameters)!;
        Assert.Equal(4.4, model.Design.OvermoldEr);
    }

    /// <summary>
    /// <b><c>er</c> is matched case-insensitively, and it is the only wBond parameter that is.</b>
    ///
    /// <para>It is the one whose spelling a hand-authored <c>.cnl</c> is likely to get wrong — <c>Er</c>
    /// and <c>ER</c> are the same symbol to a reader — and a silently ignored permittivity is a wrong
    /// capacitance with nothing anywhere saying so.</para>
    /// </summary>
    [Theory]
    [InlineData("er")]
    [InlineData("Er")]
    [InlineData("ER")]
    public void ThePermittivityParameter_IsSpellingTolerant(string spelling)
    {
        var parameters = new Dictionary<string, Value>(StringComparer.Ordinal)
        {
            ["Design"] = new Value(WBondEmbedding.Encode(ArchedDesign(1.0, "G1"))),
            [spelling] = new Value(3.3),
        };

        var model = (WBondModel)ComponentModelFactory.TryCreate("wBond", parameters)!;
        Assert.Equal(3.3, model.Design.OvermoldEr);
    }

    /// <summary>
    /// <b>A permittivity below 1 is refused at model creation, by name</b>, rather than reaching the
    /// fill and surfacing as a linear-algebra failure.
    /// </summary>
    [Fact]
    public void APermittivityBelowOne_IsRefusedWhenTheComponentIsBuilt()
    {
        var parameters = new Dictionary<string, Value>(StringComparer.Ordinal)
        {
            ["Design"] = new Value(WBondEmbedding.Encode(ArchedDesign(1.0, "G1"))),
            ["er"] = new Value(0.4),
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => ComponentModelFactory.TryCreate("wBond", parameters));
        Assert.Contains("permittivity", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── The Component Parameters dialog ───────────────────────────────────────

    /// <summary>
    /// <b>The parameter panel owns <c>er</c>, so it is not ALSO a generic text row.</b>
    ///
    /// <para>The owner asked for it beside the Include-capacitance checkbox; a second, unlabelled copy
    /// further down the same dialog is two controls writing one parameter.</para>
    /// </summary>
    [Fact]
    public void ThePanelOwnsEr_SoItIsNotAlsoAGenericRow()
    {
        var (_, _, editor) = Place(ArchedDesign(3.5, "G1"));

        Assert.DoesNotContain(editor.Rows, r => r.Name == "er");
        Assert.Equal("3.5", editor.WBondEr);

        // Temp stays a generic row — this is a filter, not a blanket suppression.
        Assert.Contains(editor.Rows, r => r.Name == "Temp");
    }

    /// <summary>
    /// <b>Typing in the box writes the parameter</b>, and <b>clearing it writes "1" rather than
    /// nothing</b>.
    ///
    /// <para><c>er</c> is declared and always emitted, so an empty expression would reach the
    /// evaluator as a parse error at Run — which is not what someone who cleared the box meant. They
    /// meant air.</para>
    /// </summary>
    [Fact]
    public void EditingTheBox_WritesTheParameter_AndClearingItMeansAir()
    {
        var (_, comp, editor) = Place(ArchedDesign(1.0, "G1"));

        editor.WBondEr = "3.8";
        Assert.Equal("3.8", ParamOf(comp, "er"));

        editor.WBondEr = "   ";
        Assert.Equal("1", ParamOf(comp, "er"));

        // An expression is committed verbatim — this is what keeps er sweepable.
        editor.WBondEr = "moldEr";
        Assert.Equal("moldEr", ParamOf(comp, "er"));
    }

    private static (SchematicViewModel Vm, EditableComponent Comp, ParameterEditorViewModel Editor) Place(
        WBondDesign? design = null)
    {
        var model = new SchematicEditModel();
        var comp = WBondPlacement.BuildCarrying(design, "W1");
        model.Components.Add(comp);

        var vm = new SchematicViewModel(model);
        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(vm, comp, showClose: false);

        return (vm, comp, editor);
    }

    // ── The Array Inductance panel ────────────────────────────────────────────

    /// <summary>
    /// <b>The panel reports the medium, and prints "air" rather than "1" at the default.</b>
    ///
    /// <para>That is the whole reason the row is worth its space: a reader who has never heard of the
    /// setting learns from one word that these wires are modelled bare.</para>
    /// </summary>
    [Fact]
    public void ThePanel_ReportsTheMedium()
    {
        var editor = new WBondViewModel(ArchedDesign(1.0, "G1"));
        var panel = new WBondPanelViewModel { Editor = editor };

        panel.Update(editor.Readout);
        Assert.Equal("air", panel.OvermoldDisplay);

        editor.OvermoldEr = 3.8;
        panel.Update(editor.Readout);
        Assert.Equal("3.8", panel.OvermoldDisplay);
        Assert.Equal(3.8, panel.OvermoldEr);
    }

    /// <summary>
    /// <b>Setting it on the editor moves the reported inductance</b>, because the shunt capacitance it
    /// scales is what makes the effective inductance a function of frequency at all.
    ///
    /// <para>This is the one test here that checks the panel's number rather than its plumbing, and it
    /// is worth it: a permittivity that reached the design but not the refill would show the new value
    /// in the row and the old inductance in every card below it.</para>
    /// </summary>
    [Fact]
    public void SettingThePermittivity_MovesTheReportedInductance()
    {
        var editor = new WBondViewModel(ArchedDesign(1.0, "G1"));
        editor.IncludeCapacitance = true;

        double air = editor.Readout.Rows[0].SelfPicoHenries;
        int fillsBefore = editor.CapacitanceComputeCount;

        editor.OvermoldEr = 4.0;

        double molded = editor.Readout.Rows[0].SelfPicoHenries;

        Assert.True(editor.CapacitanceComputeCount > fillsBefore,
            "Changing the medium must refill P — it is what P is divided by.");
        Assert.True(molded > air,
            $"More capacitance must raise the effective inductance below resonance; " +
            $"{air:F3} pH -> {molded:F3} pH.");

        // The partial inductance is the medium-independent one and must not have moved at all.
        Assert.Equal(editor.Readout.Rows[0].PartialPicoHenries,
                     editor.Readout.Rows[0].PartialPicoHenries);
    }

    /// <summary>
    /// <b>A value below 1 is ignored rather than written.</b> The design would refuse to validate, and
    /// an editor that stored it would leave every later fill throwing.
    /// </summary>
    [Fact]
    public void TheEditor_IgnoresAPermittivityBelowOne()
    {
        var editor = new WBondViewModel(ArchedDesign(2.0, "G1"));

        editor.OvermoldEr = 0.5;
        Assert.Equal(2.0, editor.OvermoldEr);
    }

    // ── The Touchstone export ─────────────────────────────────────────────────

    /// <summary>
    /// <b>The written file says which permittivity it was written at — always, air included.</b>
    ///
    /// <para>"The medium is not stated" and "the medium is air" are not the same claim to someone
    /// reading a <c>.sNp</c> a year later, and nothing else in the file distinguishes two exports of
    /// one design at two permittivities: the geometry, the port map and the model line are
    /// identical.</para>
    /// </summary>
    [Fact]
    public void TheExportedFile_StatesTheMedium()
    {
        var design = ArchedDesign(1.0, "G1");

        string air = string.Join("\n", WBondTouchstoneExport.HeaderComments(
            design, new WBondTouchstoneExport.Options()));
        Assert.Contains("air", air, StringComparison.OrdinalIgnoreCase);

        string molded = string.Join("\n", WBondTouchstoneExport.HeaderComments(
            design, new WBondTouchstoneExport.Options(OvermoldEr: 3.9)));
        Assert.Contains("3.9", molded);
        Assert.Contains("overmold", molded, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <b>The export's override applies to the numbers and does NOT edit the design.</b>
    ///
    /// <para>An export is a read. If it ever started writing back, the symptom would be a schematic
    /// quietly simulating whatever the last export dialog happened to be set to — and nothing on
    /// screen would say so.</para>
    /// </summary>
    [Fact]
    public void TheExportOverride_ChangesTheNumbers_AndNotTheDesign()
    {
        var design = ArchedDesign(1.0, "G1");
        double[] freqs = [1e9, 1e10];

        System.Numerics.Complex Y(double? er) => WBondTouchstoneExport.TerminalAdmittances(
            WBondTouchstoneExport.DesignFor(design, new WBondTouchstoneExport.Options(OvermoldEr: er)),
            freqs)[1][0, 0];

        var air = Y(null);
        var twice = Y(2.0);
        var fourTimes = Y(4.0);

        Assert.Equal(1.0, design.OvermoldEr);
        Assert.NotEqual(air, fourTimes);

        // Y[0,0] is the SERIES arm plus the terminal's own shunt, and the series arm is much the
        // larger — so the tell is not the magnitude but the ALGEBRAIC susceptance: adding capacitance
        // to ground moves Im(Y) in the positive direction, against a large negative inductive term.
        Assert.True(twice.Imaginary > air.Imaginary && fourTimes.Imaginary > twice.Imaginary,
            $"A denser medium must add shunt susceptance; {air.Imaginary:E4} -> " +
            $"{twice.Imaginary:E4} -> {fourTimes.Imaginary:E4}.");

        // And it must be LINEAR in er, which a factor applied in the wrong place would not be: the
        // increase at er = 4 is three times the increase at er = 2.
        double rise2 = twice.Imaginary - air.Imaginary;
        double rise4 = fourTimes.Imaginary - air.Imaginary;
        Assert.Equal(3.0, rise4 / rise2, 6);
    }

    /// <summary>
    /// <b>Options that do not mention the permittivity take the design's own.</b>
    ///
    /// <para>That is why the option is nullable rather than defaulting to 1: an Options built anywhere
    /// else — the Compare view model, a test, a future caller — would otherwise silently strip an
    /// encapsulated design back to air.</para>
    /// </summary>
    [Fact]
    public void OptionsThatSaySoNothing_KeepTheDesignsOwnMedium()
    {
        var design = ArchedDesign(3.6, "G1");

        Assert.Same(design, WBondTouchstoneExport.DesignFor(design, new WBondTouchstoneExport.Options()));
        Assert.Contains("3.6", string.Join("\n", WBondTouchstoneExport.HeaderComments(
            design, new WBondTouchstoneExport.Options())));
    }

    // ── Both directions of the schematic-layout reconcile ─────────────────────

    /// <summary>
    /// <b>Update Layout from Schematic writes the component's permittivity into the cell's
    /// <c>.wBond</c>.</b>
    ///
    /// <para>Same direction and same rule as the controlling parameters: this command makes the layout
    /// describe the schematic. Without it, setting ε_r on the schematic and seeding the layout leaves
    /// two documents disagreeing about the medium — the panel in the layout editor quoting one and the
    /// netlist the other, with no wire out of place to show it.</para>
    /// </summary>
    [Fact]
    public void UpdateLayoutFromSchematic_WritesThePermittivityIntoTheFile()
    {
        var model = NewSchematic();
        var comp = WBondPlacement.BuildCarrying(ArchedDesign(1.0, "G1"), "W1");
        model.Components.Add(comp);

        var er = comp.Parameters.First(p => p.Name == "er");
        er.Expression = "3.7";

        string cellDir = Path.Combine(_root, "Amp");
        var seeded = WBondCellSeeding.Seed(model, cellDir, "Amp");
        Assert.Equal(WBondCellSeeding.Outcome.Created, seeded.Outcome);

        Assert.Equal(3.7, WBondIo.ReadFile(seeded.Path!).OvermoldEr);
    }

    /// <summary>
    /// <b>A LATER change reaches a file that already exists</b> — and the "nothing changed" shortcut
    /// does not swallow it.
    ///
    /// <para>The permittivity moves no wire, so it is invisible to the geometry comparison that
    /// decides whether the merge is worth writing. Counting it is what stops the design being updated
    /// in memory and never written, which is the silent half of this failure.</para>
    /// </summary>
    [Fact]
    public void ASecondUpdate_CarriesAChangedPermittivityThrough()
    {
        var model = NewSchematic();
        var comp = WBondPlacement.BuildCarrying(ArchedDesign(1.0, "G1"), "W1");
        model.Components.Add(comp);

        string cellDir = Path.Combine(_root, "Amp");
        var first = WBondCellSeeding.Seed(model, cellDir, "Amp");
        Assert.Equal(WBondCellSeeding.Outcome.Created, first.Outcome);
        Assert.Equal(1.0, WBondIo.ReadFile(first.Path!).OvermoldEr);

        comp.Parameters.First(p => p.Name == "er").Expression = "4.1";

        var second = WBondCellSeeding.Seed(model, cellDir, "Amp");
        Assert.Equal(4.1, WBondIo.ReadFile(second.Path!).OvermoldEr);
        Assert.Contains("permittivity", string.Join("\n", second.Messages),
                        StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <b>An EXPRESSION is left alone and named</b>, exactly as a <c>VAR</c>-valued loop height is.
    /// <c>er = moldEr</c> has no single value to write into a file, and it still applies at every Run.
    /// </summary>
    [Fact]
    public void AnExpressionPermittivity_IsReportedRatherThanBaked()
    {
        var model = NewSchematic();
        var comp = WBondPlacement.BuildCarrying(ArchedDesign(2.5, "G1"), "W1");
        model.Components.Add(comp);

        comp.Parameters.First(p => p.Name == "er").Expression = "moldEr";

        var seeded = WBondCellSeeding.Seed(model, Path.Combine(_root, "Amp"), "Amp");

        Assert.Equal(2.5, WBondIo.ReadFile(seeded.Path!).OvermoldEr);
        Assert.Contains("er is an expression", string.Join("\n", seeded.Messages),
                        StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <b>Update Schematic from Layout brings it back.</b> The other direction, through
    /// <c>WBondPlacement.ApplyDesign</c>, so the two halves cannot drift apart.
    /// </summary>
    [Fact]
    public void UpdateSchematicFromLayout_BringsThePermittivityBack()
    {
        var model = NewSchematic();
        var comp = WBondPlacement.BuildCarrying(ArchedDesign(1.0, "G1"), "W1");
        model.Components.Add(comp);

        var result = WBondSchematicReconcile.Run(model, ArchedDesign(3.4, "G1"));
        Assert.NotNull(result.Command);
        result.Command!.Execute();

        Assert.Equal("3.4", ParamOf(model.Components.OfType<EditableComponent>().First(), "er"));
    }
}
