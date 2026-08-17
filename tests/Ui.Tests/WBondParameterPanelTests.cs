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
}
