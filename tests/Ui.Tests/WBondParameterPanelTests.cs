using System;
using System.Linq;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using CircuitRF.WBond;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The wBond panel in the Parameter Editor (owner, 2026-08-16): the "gibberish" row, the Symbol Pitch
/// combo, the floating reference pin, and the array editor that answers "there's no way to add new
/// arrays".
/// </summary>
public class WBondParameterPanelTests
{
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

    // ── The four rows that should not be rows ─────────────────────────────────

    /// <summary>
    /// <b>None of the four panel-owned parameters appears as a generic text row.</b>
    ///
    /// <para>Two of them are documented HIDDEN (wbond.md §5.0) and were showing anyway: <c>Design</c>
    /// is the base64 of the entire wirebond design — the owner's "gibberish", correctly unreadable
    /// and with nothing in it to type — and <c>Arrays</c> is drift-detection bookkeeping. The other
    /// two have real controls in the panel.</para>
    ///
    /// <para><c>Temp</c> and <c>GroundPlane</c> ARE ordinary engine values and must stay generic
    /// rows; asserting that is what keeps this a filter rather than a blanket suppression.</para>
    /// </summary>
    [Fact]
    public void ThePanelOwnedParameters_AreNotGenericRows()
    {
        var (_, _, editor) = Place();

        Assert.True(editor.IsWBond);

        foreach (string hidden in new[] { "Design", "Arrays", "SymbolPitch", "RefPin" })
            Assert.DoesNotContain(editor.Rows, r => r.Name == hidden);

        Assert.Contains(editor.Rows, r => r.Name == "Temp");
        Assert.Contains(editor.Rows, r => r.Name == "GroundPlane");
    }

    /// <summary>
    /// What replaces the <c>Design</c> row: the same one-line summary the symbol body carries. The
    /// payload has nothing to type in it, but "how much wire is in here" is exactly the question the
    /// unreadable row was failing to answer.
    /// </summary>
    [Fact]
    public void TheDesignRow_IsReplacedByASummary()
    {
        var (_, _, editor) = Place();

        Assert.Equal("", editor.WBondPayloadError);
        Assert.Contains("1 array", editor.WBondSummary);
        Assert.Contains("1 wire", editor.WBondSummary);
    }

    /// <summary>An unreadable payload is REPORTED, never an empty panel that reads as "nothing here".</summary>
    [Fact]
    public void AnUnreadablePayload_IsReported()
    {
        var (vm, comp, editor) = Place();

        var updated = comp.Parameters.Select(p => p.Clone()).ToList();
        updated.First(p => p.Name == "Design").Expression = "not-a-design";
        vm.Execute(new CircuitRF.Ui.Commands.Schematic.SetParametersCommand(vm.EditModel, comp, updated));

        Assert.NotEqual("", editor.WBondPayloadError);
        Assert.Equal("", editor.WBondSummary);
    }

    // ── Symbol Pitch ──────────────────────────────────────────────────────────

    /// <summary>
    /// <b>Named <c>SymbolPitch</c>, not <c>Pitch</c></b> (owner): on a wirebond component "pitch"
    /// reads as the WIRE pitch — the centre-to-centre spacing of the bonds — which this has nothing
    /// to do with. SnP has no such collision and keeps the short name.
    /// </summary>
    [Fact]
    public void ThePitchParameter_IsCalledSymbolPitch()
    {
        var (_, comp, _) = Place();

        Assert.Contains(comp.Parameters, p => p.Name == "SymbolPitch");
        Assert.DoesNotContain(comp.Parameters, p => p.Name == "Pitch");
    }

    /// <summary>The combo writes the parameter, and the parameter re-points the symbol.</summary>
    [Fact]
    public void TheSymbolPitchCombo_WritesTheParameter()
    {
        var (_, comp, editor) = Place();

        Assert.Equal(Array.IndexOf(ParameterEditorViewModel.WBondSymbolPitchOptions, "Loose"),
                     editor.WBondSymbolPitchIndex);

        editor.WBondSymbolPitchIndex =
            Array.IndexOf(ParameterEditorViewModel.WBondSymbolPitchOptions, "Tight");

        Assert.Equal("Tight", comp.Parameters.First(p => p.Name == "SymbolPitch").Expression);
        Assert.Contains("Tight", comp.ExternalSymbolRef);
    }

    // ── The floating reference pin ────────────────────────────────────────────

    /// <summary>
    /// <b>Off by default</b>, as SnP's own <c>RefNode</c> is — so a freshly-placed wBond has 2M pins.
    /// Ticking it adds REF as the LAST pin, which is what makes the flag safe: nothing else
    /// renumbers.
    /// </summary>
    [Fact]
    public void TheReferencePin_IsOffByDefault_AndAddsRefAsTheLastPin()
    {
        var (_, comp, editor) = Place();

        Assert.False(editor.WBondRefPin);

        var without = WBondSymbolProvider.Resolve(comp.ExternalSymbolRef!, null);
        Assert.Equal(2, without.Symbol!.Pins.Count);
        Assert.DoesNotContain(without.Symbol.Pins, p => p.Name == "REF");

        editor.WBondRefPin = true;

        Assert.Equal("true", comp.Parameters.First(p => p.Name == "RefPin").Expression);

        var with = WBondSymbolProvider.Resolve(comp.ExternalSymbolRef!, null);
        Assert.Equal(3, with.Symbol!.Pins.Count);
        Assert.Equal("REF", with.Symbol.Pins[^1].Name);

        // The signal terminals did not move — that is the whole basis for the pin being optional.
        for (int i = 0; i < 2; i++)
            Assert.Equal(without.Symbol.Pins[i].Name, with.Symbol.Pins[i].Name);
    }

    // ── The array editor ──────────────────────────────────────────────────────

    /// <summary>
    /// A freshly-placed wBond shows its one array: its NAME and its wire count, and nothing else.
    ///
    /// <para>The "G1.i / G1.o" pin-name column is deliberately gone (owner, 2026-08-17: "the user does
    /// not care or even needs to know what G1.i or G1.o is") — it was an internal spelling of "this
    /// array's + and − terminals", and the terminals are visible on the symbol where they are wired.</para>
    /// </summary>
    [Fact]
    public void TheArrayEditor_ListsTheArrays()
    {
        var (_, _, editor) = Place();

        var row = Assert.Single(editor.WBondArrays);
        Assert.Equal("G1", row.Name);
        Assert.Equal("1 wire", row.WiresText);
        Assert.Equal(1, row.WireCount);

        // The last array is never removable: a wBond with no arrays has no pins at all.
        Assert.False(editor.CanRemoveWBondArray);
    }

    /// <summary>
    /// <b>Adding an array adds a PORT</b> — two pins on the symbol — and it arrives carrying one
    /// wire, so the component still simulates the moment it is added rather than becoming
    /// unsimulatable until someone visits another editor. An empty array is refused by
    /// <c>WBondDesign.Validate</c>: it makes the array-basis inductance singular.
    /// </summary>
    [Fact]
    public void AddingAnArray_AddsAPortCarryingOneWire()
    {
        var (_, comp, editor) = Place();

        editor.AddWBondArrayCommand.Execute(null);

        Assert.Equal(2, editor.WBondArrays.Count);
        Assert.Equal("G2", editor.WBondArrays[1].Name);
        Assert.True(editor.CanRemoveWBondArray);

        Assert.True(WBondEmbedding.TryDecode(
            comp.Parameters.First(p => p.Name == "Design").Expression, out var design));

        design!.Validate();   // would throw on an empty array
        Assert.Equal(2, design.Arrays.Count);
        Assert.Equal(1, design.Arrays[1].Wires.Count);

        // Four pins now — and the `Arrays` record moved with the design, so a later import compares
        // against what is actually there.
        Assert.Equal(4, WBondSymbolProvider.Resolve(comp.ExternalSymbolRef!, null).Symbol!.Pins.Count);
        Assert.Equal("G1|G2", comp.Parameters.First(p => p.Name == "Arrays").Expression);
    }

    /// <summary>
    /// The added wire is OFFSET from the ones already there. Two wires at the same place have
    /// infinite mutual coupling, so a stack of them is singular the moment it is solved.
    /// </summary>
    [Fact]
    public void AddedArrays_DoNotStackTheirWiresOnTopOfEachOther()
    {
        var (_, comp, editor) = Place();

        editor.AddWBondArrayCommand.Execute(null);
        editor.AddWBondArrayCommand.Execute(null);

        Assert.True(WBondEmbedding.TryDecode(
            comp.Parameters.First(p => p.Name == "Design").Expression, out var design));

        var feet = design!.AllWires().Select(w => (w.Points[0].X, w.Points[0].Y)).ToList();
        Assert.Equal(3, feet.Count);
        Assert.Equal(3, feet.Distinct().Count());
    }

    /// <summary>Renaming an array renames its PINS — the array name IS the pin name.</summary>
    [Fact]
    public void RenamingAnArray_RenamesItsPins()
    {
        var (_, comp, editor) = Place();

        editor.WBondArrays[0].Name = "Drain";
        editor.WBondArrays[0].Commit();

        var symbol = WBondSymbolProvider.Resolve(comp.ExternalSymbolRef!, null).Symbol!;
        Assert.Equal("Drain.i", symbol.Pins[0].Name);
        Assert.Equal("Drain.o", symbol.Pins[1].Name);
        Assert.Equal("Drain", comp.Parameters.First(p => p.Name == "Arrays").Expression);
    }

    /// <summary>
    /// A blank or duplicate name is refused and the row snaps back. Array names are the symbol's pin
    /// names and must be unique (<c>WBondDesign.Validate</c>) — a duplicate would make the payload
    /// unreadable by the very component carrying it.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("G1")]
    public void ABlankOrDuplicateArrayName_IsRefused(string attempt)
    {
        var (_, comp, editor) = Place();
        editor.AddWBondArrayCommand.Execute(null);

        editor.WBondArrays[1].Name = attempt;
        editor.WBondArrays[1].Commit();

        Assert.Equal("G1|G2", comp.Parameters.First(p => p.Name == "Arrays").Expression);
        Assert.Equal("G2", editor.WBondArrays[1].Name);
    }

    /// <summary>Removing an array removes its two pins, and the last one cannot be removed at all.</summary>
    [Fact]
    public void RemovingAnArray_RemovesItsPins_AndTheLastOneIsKept()
    {
        var (_, comp, editor) = Place();
        editor.AddWBondArrayCommand.Execute(null);
        Assert.Equal(4, WBondSymbolProvider.Resolve(comp.ExternalSymbolRef!, null).Symbol!.Pins.Count);

        editor.RemoveWBondArrayCommand.Execute(editor.WBondArrays[1]);

        Assert.Single(editor.WBondArrays);
        Assert.Equal(2, WBondSymbolProvider.Resolve(comp.ExternalSymbolRef!, null).Symbol!.Pins.Count);

        // The last one stays, whatever the command is asked to do.
        editor.RemoveWBondArrayCommand.Execute(editor.WBondArrays[0]);
        Assert.Single(editor.WBondArrays);
    }

    /// <summary>
    /// An array edit is ONE undo entry, and it takes the <c>Arrays</c> record back with the design.
    /// They are two statements of the same fact; a design restored without its record would report
    /// drift against itself on the next import.
    /// </summary>
    [Fact]
    public void AnArrayEdit_IsOneUndoEntry_AndCarriesTheArraysRecordWithIt()
    {
        var (vm, comp, editor) = Place();

        editor.AddWBondArrayCommand.Execute(null);
        Assert.Equal("G1|G2", comp.Parameters.First(p => p.Name == "Arrays").Expression);

        vm.UndoRedo.Undo();

        Assert.Equal("G1", comp.Parameters.First(p => p.Name == "Arrays").Expression);
        Assert.True(WBondEmbedding.TryDecode(
            comp.Parameters.First(p => p.Name == "Design").Expression, out var design));
        Assert.Single(design!.Arrays);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  WB-G — §2.3's panel, and WB45's Source control
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// §2.3 — <b>the per-array rows are generated from the instance's OWN array list</b>, so they name
    /// G1/G2 rather than asking anyone to spell a suffix. That is also the only way this can be offered
    /// at all: the array names are not knowable until the payload is decoded.
    /// </summary>
    [Fact]
    public void ThePerArrayControlRows_AreGeneratedFromTheInstancesOwnArrays()
    {
        var (_, _, editor) = Place();

        Assert.Single(editor.WBondControls);
        Assert.Equal("G1", editor.WBondControls[0].ArrayName);

        editor.AddWBondArrayCommand.Execute(null);

        Assert.Equal(2, editor.WBondControls.Count);
        Assert.Equal(["G1", "G2"], editor.WBondControls.Select(r => r.ArrayName));
    }

    /// <summary>
    /// §2.3 — <b>unset must be visibly distinct from zero.</b> An empty box means "as drawn" and the
    /// parameter is not written at all; <c>0</c> is a mistake the run refuses by name. A row that
    /// blanks its value has its parameter REMOVED rather than left as an empty string, which is what
    /// keeps the panel honest about which arrays actually carry an override.
    /// </summary>
    [Fact]
    public void BlankingAPerArrayOverride_RemovesTheParameterRatherThanLeavingItEmpty()
    {
        var (_, comp, editor) = Place();

        var row = editor.WBondControls[0];
        Assert.DoesNotContain(comp.Parameters, p => p.Name == "LoopHeight_G1");

        row.LoopHeight = "12";
        row.CommitLoopHeight();

        var written = comp.Parameters.First(p => p.Name == "LoopHeight_G1");
        Assert.Equal("12", written.Expression);
        Assert.Equal("mil", written.Unit);   // a wirebond is authored in mils

        row.LoopHeight = "";
        row.CommitLoopHeight();

        Assert.DoesNotContain(comp.Parameters, p => p.Name == "LoopHeight_G1");
    }

    /// <summary>
    /// <c>Material</c> is an ENUMERATION over the design's own metals, so it is a dropdown — and its
    /// first entry is "As drawn", which is what unset means and is deliberately not a metal's name.
    /// </summary>
    [Fact]
    public void TheMaterialDropdown_OffersAsDrawnFirst_AndThenTheDesignsOwnMetals()
    {
        var (_, comp, editor) = Place();

        Assert.Equal(ParameterEditorViewModel.AsDrawn, editor.WBondMaterialOptions[0]);
        Assert.Contains("Gold", editor.WBondMaterialOptions);
        Assert.Contains("Aluminium", editor.WBondMaterialOptions);
        Assert.Equal(0, editor.WBondMaterialIndex);

        editor.WBondMaterialIndex = editor.WBondMaterialOptions.IndexOf("Aluminium");
        Assert.Equal("Aluminium", comp.Parameters.First(p => p.Name == "Material").Expression);

        editor.WBondMaterialIndex = 0;
        Assert.Equal("", comp.Parameters.First(p => p.Name == "Material").Expression);
    }

    /// <summary>
    /// The panel-owned parameters are not ALSO generic text rows — but <c>LoopHeight</c>,
    /// <c>Diameter</c>, <c>Temp</c> and <c>GroundPlane</c> deliberately still are. They are ordinary
    /// expression fields, and that is what makes <c>LoopHeight = loopH</c> typable and therefore
    /// sweepable (WB44 property 4).
    /// </summary>
    [Fact]
    public void TheUnsuffixedLengths_StayGenericExpressionRows()
    {
        var (_, _, editor) = Place();

        foreach (string owned in new[] { "Design", "Arrays", "SymbolPitch", "RefPin", "Source", "File", "Material" })
            Assert.DoesNotContain(editor.Rows, r => r.Name == owned);

        foreach (string generic in new[] { "LoopHeight", "Diameter", "Temp", "GroundPlane" })
            Assert.Contains(editor.Rows, r => r.Name == generic);
    }

    /// <summary>
    /// <b>The summary describes what will SIMULATE, not what was drawn</b> — the owner's Update Layout
    /// report of 2026-08-17 one surface over. Total wire length moves with loop height, so a component
    /// carrying a 45 mil override over 20 mil wires would otherwise report a length no run ever uses.
    /// The line says when an override is in force, because a number that changes as you type in a box
    /// below it should explain itself.
    /// </summary>
    [Fact]
    public void TheSummary_ReportsTheOverriddenGeometry_AndSaysThatItHas()
    {
        var (_, comp, editor) = Place();

        string asDrawn = editor.WBondSummary;
        Assert.DoesNotContain("with overrides", asDrawn);

        var lh = comp.Parameters.First(p => p.Name == "LoopHeight");
        lh.Expression = "45";
        lh.Unit = "mil";
        editor.SetTargetDirect(new SchematicViewModel(new SchematicEditModel()), comp, showClose: false);

        Assert.Contains("with overrides", editor.WBondSummary);
        Assert.NotEqual(asDrawn, editor.WBondSummary);

        // …and the payload is untouched by having been summarised. Applying an override on a path that
        // writes back would bake it in and break WB44 property 1.
        Assert.True(WBondEmbedding.TryDecode(
            comp.Parameters.First(p => p.Name == "Design").Expression, out var design));
        Assert.Equal(WBondUnits.ToNm(20.0, WBondUnit.Mil), design!.Arrays[0].Wires[0].LoopHeightNm);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Label ORDER on the symbol (owner, 2026-08-17)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Owner report.</b> <i>"When I create G1, G2, G3 arrays in the Component Parameters dialog, the
    /// symbol rendering lists them as LoopHeight_G2, LoopHeight_G1, LoopHeight_G3."</i>
    ///
    /// <para>Labels are built by walking <c>Parameters</c> in list order, and a per-array override is
    /// APPENDED when its box is first committed — so the on-symbol order was the order the user's focus
    /// happened to visit the boxes in. This test commits them deliberately out of order (G2, G1, G3, the
    /// reported sequence) and asserts the SYMBOL comes out in array order regardless.</para>
    ///
    /// <para>The oracle is the render model's own <c>Labels</c>, not the parameter list — that is the
    /// thing the user is looking at.</para>
    /// </summary>
    [Fact]
    public void PerArrayLabels_RenderInArrayOrder_WhateverOrderTheyWereTypedIn()
    {
        var model = new SchematicEditModel();
        var comp = WBondPlacement.BuildCarrying(null, "W1");
        model.Components.Add(comp);

        var vm = new SchematicViewModel(model);
        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(vm, comp, showClose: false);

        editor.AddWBondArrayCommand.Execute(null);
        editor.AddWBondArrayCommand.Execute(null);
        Assert.Equal(["G1", "G2", "G3"], editor.WBondControls.Select(r => r.ArrayName));

        // The reported sequence: the middle box committed FIRST. Each array's value is tied to the
        // array rather than to the commit position, so the assertion below distinguishes the two.
        string[] byArray = ["11", "22", "33"];
        foreach (int i in new[] { 1, 0, 2 })
        {
            editor.WBondControls[i].LoopHeight = byArray[i];
            editor.WBondControls[i].CommitLoopHeight();
        }

        var labels = model.BuildRenderModel().Model.Components.Single().Labels
            .Where(l => l.StartsWith("LoopHeight_", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(
            ["LoopHeight_G1 = 11 mil", "LoopHeight_G2 = 22 mil", "LoopHeight_G3 = 33 mil"],
            labels);
    }

    /// <summary>
    /// Renaming an array takes its controlling parameters with it. An override left behind under the
    /// old suffix silently stops applying — with its value still in the dialog and still drawn on the
    /// symbol, which is the worst of both.
    /// </summary>
    [Fact]
    public void RenamingAnArray_RenamesItsControllingParameters()
    {
        var (_, comp, editor) = Place();

        editor.WBondControls[0].LoopHeight = "30";
        editor.WBondControls[0].CommitLoopHeight();
        Assert.Contains(comp.Parameters, p => p.Name == "LoopHeight_G1");

        editor.RenameWBondArray(editor.WBondArrays[0], "D1");

        Assert.DoesNotContain(comp.Parameters, p => p.Name == "LoopHeight_G1");
        Assert.Equal("30", comp.Parameters.First(p => p.Name == "LoopHeight_D1").Expression);
    }

    /// <summary>
    /// Deleting an array drops its controlling parameters. Left behind, they would draw a label for a
    /// pin pair that is no longer on the symbol.
    /// </summary>
    [Fact]
    public void DeletingAnArray_DropsItsControllingParameters()
    {
        var (_, comp, editor) = Place();

        editor.AddWBondArrayCommand.Execute(null);
        editor.WBondControls[1].LoopHeight = "30";
        editor.WBondControls[1].CommitLoopHeight();
        Assert.Contains(comp.Parameters, p => p.Name == "LoopHeight_G2");

        editor.RemoveWBondArrayCommand.Execute(editor.WBondArrays[1]);

        Assert.DoesNotContain(comp.Parameters, p => p.Name == "LoopHeight_G2");
    }

    /// <summary>
    /// WB45 — <b>a freshly placed wBond is Carried, and cannot be set to Linked with nothing to link
    /// to.</b> Linked with no path would be a Not-Found on the next Run with no way back except this
    /// same box; the box snaps back and the note line says why.
    /// </summary>
    [Fact]
    public void TheSourceControl_StartsCarried_AndRefusesLinkedWithNothingToLinkTo()
    {
        var (_, comp, editor) = Place();

        Assert.Equal(0, editor.WBondSourceIndex);
        Assert.Contains("Update Layout from Schematic", editor.WBondSourceNote);

        editor.WBondSourceIndex = 1;

        Assert.Equal(0, editor.WBondSourceIndex);
        Assert.Equal(nameof(WBondPlacement.WireSource.Carried),
            comp.Parameters.First(p => p.Name == "Source").Expression);
    }
}
