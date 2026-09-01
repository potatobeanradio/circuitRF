using System;
using System.Linq;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Matching;
using CircuitRF.Core.Systems;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The enum-NAMED parameters of the system blocks are PICKERS in the Parameters dialog, not free
/// text (owner request, 2026-08-31).
///
/// <para><b>Why it matters more than tidiness.</b> A misspelled enum name is not an error anywhere:
/// <c>ComponentModelFactory.EnumNamed</c> reads an unrecognised spelling as the type's DEFAULT and
/// says nothing, so <c>Response=Butterwoth</c> is a Chebyshev filter with no message. A closed
/// picker removes the only way to type one.</para>
///
/// <para>The other half is the round trip: every option a picker offers must be a name the factory
/// can actually parse back. That is gated by ELABORATING each one rather than by comparing the list
/// with itself — a hand-typed option that no longer matches its enum member would pass any
/// string comparison and still silently commit a value nothing reads.</para>
/// </summary>
public class SystemBlockParameterPickerTests
{
    private static (SchematicViewModel Vm, EditableComponent Comp) Place(SymbolKind kind)
    {
        var model = new SchematicEditModel();
        var comp  = new EditableComponent { InstanceName = "X1", Symbol = kind, X = 0, Y = 0 };
        foreach (var d in ComponentTypeRegistry.DefaultParameters(kind, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = d.Name, Expression = d.Expression, Unit = d.Unit,
                Dimension = d.Dimension, ShowOnSchematic = d.ShowOnSchematic,
            });
        model.Components.Add(comp);
        return (new SchematicViewModel(model), comp);
    }

    private static ParameterRowViewModel Row(SymbolKind kind, string name)
    {
        var (vm, comp) = Place(kind);
        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(vm, comp);
        return editor.Rows.Single(r => r.Name == name);
    }

    [Theory]
    [InlineData(SymbolKind.Filter,     "Response")]
    [InlineData(SymbolKind.Filter,     "Form")]
    [InlineData(SymbolKind.Duplexer,   "TxResponse")]
    [InlineData(SymbolKind.Duplexer,   "TxForm")]
    [InlineData(SymbolKind.Duplexer,   "RxResponse")]
    [InlineData(SymbolKind.Duplexer,   "RxForm")]
    [InlineData(SymbolKind.Circulator, "Direction")]
    [InlineData(SymbolKind.Switch,     "OffState")]
    [InlineData(SymbolKind.SwitchD,    "OffState")]
    [InlineData(SymbolKind.Amp,        "IP3Ref")]
    public void AnEnumNamedParameterIsAPickerAndNotATextBox(SymbolKind kind, string name)
    {
        var row = Row(kind, name);

        Assert.True(row.IsChoiceParam, $"{kind}.{name} is still a free-text row");
        Assert.False(row.ShowExpressionTextBox);
        Assert.True(row.IsRegistryChoiceParam);

        // The tile's own default is one of the offered values, and is what the picker shows.
        Assert.Contains(row.SelectedChoice, row.ChoiceOptions);
    }

    /// <summary>
    /// Choosing an option commits that NAME verbatim — the value the factory parses — rather than an
    /// index. This is the difference from <c>EnumParamOptions</c> (MBend's <c>Miter</c>), whose
    /// stored value really is a number.
    /// </summary>
    [Fact]
    public void PickingAnOptionCommitsTheNameItself()
    {
        var (vm, comp) = Place(SymbolKind.Filter);
        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(vm, comp);

        var response = editor.Rows.Single(r => r.Name == "Response");
        response.SelectedChoice = nameof(FilterResponse.Bessel);

        Assert.Equal(nameof(FilterResponse.Bessel),
                     comp.Parameters.Single(p => p.Name == "Response").Expression);
    }

    /// <summary>
    /// Every option the picker offers elaborates into the thing it names — measured through the
    /// FACTORY, because the failure this guards against is an option string that no longer matches
    /// its enum member and is therefore silently read as the default.
    /// </summary>
    [Fact]
    public void EveryFilterOptionOfferedIsOneTheFactoryCanActuallyParse()
    {
        foreach (string response in ComponentTypeRegistry.NamedParamOptions(SymbolKind.Filter, "Response")!)
        foreach (string form     in ComponentTypeRegistry.NamedParamOptions(SymbolKind.Filter, "Form")!)
        {
            var model = new SchematicEditModel();
            var comp  = new EditableComponent { InstanceName = "X1", Symbol = SymbolKind.Filter, X = 0, Y = 0 };
            foreach (var d in ComponentTypeRegistry.DefaultParameters(SymbolKind.Filter, 0))
                comp.Parameters.Add(new EditableParameter
                {
                    Name = d.Name, Expression = d.Expression, Unit = d.Unit,
                    Dimension = d.Dimension, ShowOnSchematic = d.ShowOnSchematic,
                });
            model.Components.Add(comp);
            comp.Parameters.Single(p => p.Name == "Response").Expression = response;
            comp.Parameters.Single(p => p.Name == "Form").Expression     = form;

            var extracted = NetExtractor.Extract(model);
            var netlist   = new Elaborator(extracted.Library).Elaborate(extracted.TestBench);
            var m = Assert.IsType<FilterModel>(Assert.Single(netlist.Components).Model);

            Assert.Equal(Enum.Parse<FilterResponse>(response), m.Network.Prototype.Response);
            Assert.Equal(Enum.Parse<NetworkForm>(form),        m.Network.Form);
        }
    }

    [Theory]
    [InlineData(SymbolKind.Circulator, "Direction", "CW",         "CCW")]
    [InlineData(SymbolKind.Switch,     "OffState",  "Reflective", "Absorptive")]
    [InlineData(SymbolKind.Amp,        "IP3Ref",    "Output",     "Input")]
    public void EveryOtherPickerOffersExactlyItsEnumsMembers(
        SymbolKind kind, string name, string first, string second)
    {
        var options = ComponentTypeRegistry.NamedParamOptions(kind, name)!;
        Assert.Contains(first,  options);
        Assert.Contains(second, options);
        Assert.Equal(2, options.Count);
    }

    /// <summary>
    /// A value the picker does not offer is still LISTED rather than dropped: a ComboBox whose
    /// SelectedItem is absent from its ItemsSource renders blank, which reads as the value having
    /// been lost. The same rule the layer and cell-declared pickers keep.
    /// </summary>
    [Fact]
    public void AnUnrecognisedSpellingIsStillShownRatherThanRenderingBlank()
    {
        var (vm, comp) = Place(SymbolKind.Filter);
        comp.Parameters.Single(p => p.Name == "Response").Expression = "Butterwoth";

        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(vm, comp);
        var row = editor.Rows.Single(r => r.Name == "Response");

        Assert.Contains("Butterwoth", row.ChoiceOptions);
        Assert.Equal("Butterwoth", row.SelectedChoice);
    }

    /// <summary>
    /// A registry picker does NOT float to the top of the dialog. The editor promotes choice rows
    /// for a kit part — which file, then which formulation, then the values — and a built-in's
    /// parameters already arrive in the order they read in, so <c>IP3Ref</c> above <c>Gain</c> would
    /// just be a shuffled dialog.
    /// </summary>
    [Fact]
    public void ARegistryPickerKeepsItsPlaceInTheParameterOrder()
    {
        var (vm, comp) = Place(SymbolKind.Amp);
        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(vm, comp);

        var names = editor.Rows.Select(r => r.Name).ToList();
        Assert.Equal(
            ComponentTypeRegistry.DefaultParameters(SymbolKind.Amp, 0).Select(d => d.Name).ToList(),
            names);
    }

    /// <summary>
    /// The circulator's per-port detune rows are present on a freshly placed tile and are ordinary
    /// numeric rows — a VSWR is a number a user sweeps, not a closed set.
    /// </summary>
    [Fact]
    public void TheCirculatorsDetuneRowsArePresentAndAreOrdinaryNumbers()
    {
        var (vm, comp) = Place(SymbolKind.Circulator);
        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(vm, comp);

        foreach (string n in (string[])["VSWR1", "VSWR2", "VSWR3", "Ang1", "Ang2", "Ang3"])
        {
            var row = editor.Rows.Single(r => r.Name == n);
            Assert.True(row.ShowExpressionTextBox);
            Assert.False(row.IsChoiceParam);
        }

        // The angles carry their `deg` unit from the moment the tile is placed: an angle reaches the
        // model in RADIANS, so a row left unitless would silently mean radians.
        foreach (string n in (string[])["Ang1", "Ang2", "Ang3"])
            Assert.Equal("deg", editor.Rows.Single(r => r.Name == n).StagedUnit);
    }
}
