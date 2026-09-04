using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Devices;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// PM2 P3's gate: the VerilogA box draws the MODEL's own terminal names once the file has been read,
/// and falls back to numbers otherwise.
///
/// <para><b>Why this is worth a symbol change at all.</b> A five-terminal physics model presents as
/// five identical leads numbered 1..5, and on a part whose fifth terminal is thermal those numbers
/// are the largest single source of mis-wiring — while the model has already declared each
/// terminal's own name. Drawing 1..5 over it withholds something circuitRF was told.</para>
///
/// <para><b>Asserted on the symbol's own primitives, not on a screenshot.</b> The text a lead is
/// labelled with is in the geometry; rendering it and looking at pixels would measure the font.</para>
/// </summary>
public sealed class VerilogATerminalLabelTests
{
    private static IReadOnlyList<string> DrawnLabels(Symbol sym)
        => [.. sym.Primitives.OfType<TextPrimitive>().Select(t => t.Content)];

    // ── The fallback: numbers, exactly as before ──────────────────────────────

    [Fact]
    public void WithNoLabelsTheLeadsAreStillNumbered()
    {
        // A component placed before a file has been chosen still has to draw, and this is what it
        // has always drawn.
        var sym = BuiltInSymbols.PrimitivesForVerilogA(5, labels: null);

        Assert.Equal(["1", "2", "3", "4", "5"], DrawnLabels(sym));
    }

    [Fact]
    public void AnEmptyOrAllBlankLabelListFallsBackToNumbers()
    {
        Assert.Equal(["1", "2"], DrawnLabels(BuiltInSymbols.PrimitivesForVerilogA(2, [])));
        Assert.Equal(["1", "2"], DrawnLabels(BuiltInSymbols.PrimitivesForVerilogA(2, ["", "  "])));
    }

    // ── The labelled form ─────────────────────────────────────────────────────

    [Fact]
    public void TheModelsOwnTerminalNamesAreDrawnWhenItHasStatedThem()
    {
        var sym = BuiltInSymbols.PrimitivesForVerilogA(5, ["d", "g", "s", "b", "dt"]);

        Assert.Equal(["d", "g", "s", "b", "dt"], DrawnLabels(sym));
    }

    [Fact]
    public void EachLeadFallsBackIndependently()
    {
        // A model that names three of five terminals draws three names and two numbers — not five
        // numbers, and not three names and two blanks.
        var sym = BuiltInSymbols.PrimitivesForVerilogA(5, ["d", "", "s"]);

        Assert.Equal(["d", "2", "s", "4", "5"], DrawnLabels(sym));
    }

    /// <summary>
    /// <b>The invariant everything downstream rests on.</b> Naming a lead changes the TEXT and
    /// nothing else: same pin coordinates, same port indices, same body. Connectivity is by port
    /// INDEX and geometry is by coordinate, so a labelled symbol that moved a pin would silently
    /// re-wire a placed component — and a schematic drawn before the file was read would connect
    /// differently from the same schematic after it.
    /// </summary>
    [Fact]
    public void LabellingChangesTheTextAndNothingElse()
    {
        var numbered = BuiltInSymbols.PrimitivesForVerilogA(5, labels: null);
        var labelled = BuiltInSymbols.PrimitivesForVerilogA(5, ["d", "g", "s", "b", "dt"]);

        Assert.Equal(numbered.Pins.Count, labelled.Pins.Count);
        for (int i = 0; i < numbered.Pins.Count; i++)
        {
            Assert.Equal(numbered.Pins[i].LocalX,    labelled.Pins[i].LocalX);
            Assert.Equal(numbered.Pins[i].LocalY,    labelled.Pins[i].LocalY);
            Assert.Equal(numbered.Pins[i].PortIndex, labelled.Pins[i].PortIndex);
        }

        // And the same non-text geometry: the leads and the body are untouched.
        Assert.Equal(numbered.Primitives.Count(p => p is not TextPrimitive),
                     labelled.Primitives.Count(p => p is not TextPrimitive));
    }

    [Fact]
    public void ALabelListLongerThanThePinCountDrawsOnlyTheLeadsThatExist()
    {
        // `Pins` deliberately connects a PREFIX of the model's terminals — a four-pin placement of a
        // five-terminal self-heating model is the ordinary configuration, not a mistake.
        var sym = BuiltInSymbols.PrimitivesForVerilogA(4, ["d", "g", "s", "b", "dt"]);

        Assert.Equal(["d", "g", "s", "b"], DrawnLabels(sym));
    }

    // ── The cache the render path is allowed to read ──────────────────────────

    /// <summary>
    /// The symbol may only read labels that are ALREADY known. Its lookup runs on every glyph
    /// rebuild, so describing a file there would put a worker process launch inside a redraw.
    /// </summary>
    [Fact]
    public void LabelsForAFileThatHasNotBeenReadAreSimplyAbsent()
    {
        VerilogAModelIntrospection.ForgetCachedLabels();

        Assert.Null(VerilogAModelIntrospection.CachedTerminalLabels("/nowhere/never-read.osdi", ""));
        Assert.Null(VerilogAModelIntrospection.CachedTerminalLabels("", ""));
        Assert.Null(VerilogAModelIntrospection.CachedTerminalLabels(null, null));
    }

    // ── The placed instance ───────────────────────────────────────────────────

    [Fact]
    public void APlacedComponentWithNoFileDrawsTheNumberedBox()
    {
        VerilogAModelIntrospection.ForgetCachedLabels();

        var comp = new EditableComponent { Symbol = SymbolKind.VerilogA };
        comp.Parameters.Add(new EditableParameter
            { Name = ComponentModelFactory.VerilogAFileParam,  Expression = "" });
        comp.Parameters.Add(new EditableParameter
            { Name = ComponentModelFactory.VerilogAModelParam, Expression = "" });
        comp.Parameters.Add(new EditableParameter
            { Name = ComponentModelFactory.VerilogAPinsParam,  Expression = "5" });

        Assert.Equal(5, comp.PortCount);
        Assert.NotNull(comp.ToRenderComponent());
    }

    // ── The omitted thermal terminal ──────────────────────────────────────────
    //
    // Asserted BY TEXT, per the brief's own gate — never by screenshot.

    /// <summary>The case this exists for: four pins drawn on a five-terminal self-heating model is
    /// the ORDINARY configuration, and without a word here it reads as a symbol drawn with one lead
    /// too few. The user's "fix" would be to add the pin — which floats the thermal node and leaves
    /// the DC solve with no solution at all.</summary>
    [Fact]
    public void AFourPinPlacementOfAFiveTerminalSelfHeatingModelIsExplained()
    {
        var model = new VerilogAModelInfo("m", PinCount: 5, ParameterCount: 100,
            TerminalLabels: ["d", "g", "s", "b", "dt"], ThermalTerminals: [4]);

        string note = ParameterEditorViewModel.OmittedThermalTerminalNote(4, model);

        Assert.Contains("thermal terminal 'dt'", note, System.StringComparison.Ordinal);
        Assert.Contains("ordinary way to place this part", note, System.StringComparison.Ordinal);
    }

    [Fact]
    public void AllFivePinsDrawnSaysNothing()
    {
        var model = new VerilogAModelInfo("m", 5, 100, ["d", "g", "s", "b", "dt"], [4]);

        Assert.Equal("", ParameterEditorViewModel.OmittedThermalTerminalNote(5, model));
    }

    [Fact]
    public void AnOmittedELECTRICALTerminalIsNotReassuredAbout()
    {
        // Saying "that is fine" about a genuine mis-wiring is worse than saying nothing at all.
        var model = new VerilogAModelInfo("m", 4, 100, ["d", "g", "s", "b"], ThermalTerminals: []);

        Assert.Equal("", ParameterEditorViewModel.OmittedThermalTerminalNote(3, model));
    }

    [Fact]
    public void TwoMissingTerminalsAreNotThisCase()
    {
        var model = new VerilogAModelInfo("m", 5, 100, ["d", "g", "s", "b", "dt"], [4]);

        Assert.Equal("", ParameterEditorViewModel.OmittedThermalTerminalNote(3, model));
    }

    [Fact]
    public void AnUnnamedThermalTerminalIsStillExplained()
    {
        // A model that names no terminal still declares the discipline, and the number is what is
        // left to call it by.
        var model = new VerilogAModelInfo("m", 5, 100, TerminalLabels: [], ThermalTerminals: [4]);

        string note = ParameterEditorViewModel.OmittedThermalTerminalNote(4, model);

        Assert.Contains("thermal terminal terminal 5", note, System.StringComparison.Ordinal);
    }
}
