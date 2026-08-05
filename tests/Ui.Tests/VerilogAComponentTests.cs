using System.Linq;
using CircuitRF.Core.Devices;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The generic Verilog-A component: a compiled compact model a user places and points at their own
/// file, with no kit installed and nothing to configure.
///
/// <para><b>What these pin down is the WIRING, not the physics.</b> Whether the model computes the
/// right currents is settled elsewhere, against a real compiled model. What can break here is
/// quieter: a tile that is not in the palette, a parameter name the factory does not read, a pin
/// count the symbol draws but the netlist does not, or a file field with no Browse button. Every one
/// of those leaves a component that looks placed and does not work.</para>
/// </summary>
public sealed class VerilogAComponentTests
{
    // ── W1 — it is in the palette, under Devices ──────────────────────────────

    [Fact]
    public void W1_ItIsAPlaceablePaletteItemUnderDevices()
    {
        var item = LibraryCatalog.AllItems.FirstOrDefault(i => i.Kind == SymbolKind.VerilogA);

        Assert.True(item is not null, "VerilogA is missing from the palette catalog");
        Assert.Equal(ComponentCategory.Devices, item!.Category);
        Assert.Contains(SymbolKind.VerilogA,
                        LibraryCatalog.ByCategory(ComponentCategory.Devices).Select(i => i.Kind));
    }

    /// <summary>
    /// Findable by what a user would actually type. Somebody with a compiled model in hand searches
    /// for the language, the file format, or the model family they compiled — rarely for the word
    /// circuitRF happens to print on the tile.
    /// </summary>
    [Theory]
    [InlineData("VerilogA")]
    [InlineData("Verilog-A")]
    [InlineData("OSDI")]
    [InlineData("compact model")]
    [InlineData("custom")]
    [InlineData("PSP")]
    [InlineData("BSIM")]
    public void W2_ItIsFoundByTheWordsAUserWouldType(string query)
        => Assert.Contains(LibraryCatalog.Search(query), i => i.Kind == SymbolKind.VerilogA);

    // ── W3 — the parameter names are the ones the factory reads ───────────────

    /// <summary>
    /// The registry's names and the factory's constants are the same strings, checked against each
    /// other rather than by eye. A typo here is a parameter that silently takes its default instead
    /// of the user's value — and for the model file, that means "no model file" on a component the
    /// user just pointed at one.
    /// </summary>
    [Fact]
    public void W3_TheDefaultParametersAreTheOnesTheFactoryReads()
    {
        var names = ComponentTypeRegistry.DefaultParameters(SymbolKind.VerilogA, portCount: 2)
                                         .Select(p => p.Name).ToArray();

        Assert.Contains(ComponentModelFactory.VerilogAFileParam,  names);
        Assert.Contains(ComponentModelFactory.VerilogAModelParam, names);
        Assert.Contains(ComponentModelFactory.VerilogAPinsParam,  names);

        // The engine name a placed instance carries, which the factory dispatches on.
        Assert.Equal("VerilogA", ComponentTypeRegistry.EngineReference(SymbolKind.VerilogA));
    }

    /// <summary>
    /// The model file gets a Browse… picker. A path is exactly the kind of value nobody should be
    /// asked to type, and a mistyped one fails much later with a worse message.
    /// </summary>
    [Fact]
    public void W4_TheModelFileParameterOffersAFilePicker()
    {
        Assert.True(ComponentTypeRegistry.IsFilePathParameter(
            SymbolKind.VerilogA, ComponentModelFactory.VerilogAFileParam));

        // …and the others do not, or every field would grow a button that opens a file dialog.
        Assert.False(ComponentTypeRegistry.IsFilePathParameter(
            SymbolKind.VerilogA, ComponentModelFactory.VerilogAModelParam));
        Assert.False(ComponentTypeRegistry.IsFilePathParameter(SymbolKind.Diode, "Is"));
    }

    // ── W5 — the terminal count follows the model, not the symbol ─────────────

    /// <summary>
    /// A compact model has whatever terminals it has — two for a diode, four for a MOSFET, five for
    /// an HBT with a thermal node. A fixed-pin symbol would make most models unplaceable, so `Pins`
    /// drives both the glyph and the netlist's own idea of how many nets the instance takes.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(8)]
    public void W5_ThePinCountFollowsThePinsParameter(int pins)
    {
        var comp = new EditableComponent { Symbol = SymbolKind.VerilogA };
        comp.Parameters.Clear();
        foreach (var p in ComponentTypeRegistry.DefaultParameters(SymbolKind.VerilogA, portCount: 2))
            comp.Parameters.Add(new EditableParameter
            {
                Name = p.Name,
                Expression = p.Name == ComponentModelFactory.VerilogAPinsParam ? pins.ToString() : p.Expression,
                Unit = p.Unit,
                ShowOnSchematic = p.ShowOnSchematic,
            });

        Assert.Equal(pins, comp.PortCount);

        // The glyph draws exactly that many, and every one lands on the connection grid — a pin off
        // the grid cannot be wired to, which is indistinguishable from a pin that is not there.
        var symbol = BuiltInSymbols.Primitives(SymbolKind.VerilogA, pins);
        Assert.Equal(pins, symbol.Pins.Count);
        Assert.All(symbol.Pins, p =>
        {
            Assert.Equal(0.0, p.LocalX % 100.0, 6);
            Assert.Equal(0.0, p.LocalY % 100.0, 6);
        });

        // Distinct positions: two pins on one point look like one pin and silently short two nets.
        Assert.Equal(pins, symbol.Pins.Select(p => (p.LocalX, p.LocalY)).Distinct().Count());
    }

    /// <summary>A freshly placed component defaults to something placeable rather than to nothing.</summary>
    [Fact]
    public void W6_AFreshlyPlacedComponentHasPins()
    {
        var comp = new EditableComponent { Symbol = SymbolKind.VerilogA };
        Assert.True(comp.PortCount >= 2, $"a placed VerilogA has {comp.PortCount} pins");
    }
}
