using CircuitRF.Ui.Schematic;
using CircuitRF.WBond;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Tier 6 of brief-wbond-wbb §4 — the dynamic wBond symbol (R-wbb-5 / D3).
/// </summary>
public class WBondSymbolGeneratorTests
{
    private static WBondDesign Design(params string[] arrayNames)
    {
        var design = new WBondDesign();
        foreach (string name in arrayNames)
        {
            design.Arrays.Add(new WireArray
            {
                Name = name,
                Wires =
                {
                    new Wire { Points = { Point3.Mils(0, 0, 20), Point3.Mils(100, 0, 20) } },
                },
            });
        }
        return design;
    }

    /// <summary>TIER 6 — two pins per array plus REF, in array order, input left and output right.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(12)]
    public void Tier6_PinCountAndOrder_FollowTheArrayList(int arrays)
    {
        var names = Enumerable.Range(1, arrays).Select(i => $"G{i}").ToArray();
        var symbol = WBondSymbolGenerator.Build(Design(names));

        Assert.NotNull(symbol);
        Assert.Equal(2 * arrays + 1, symbol!.Pins.Count);

        for (int k = 0; k < arrays; k++)
        {
            var input = symbol.Pins[2 * k];
            var output = symbol.Pins[2 * k + 1];

            Assert.Equal($"{names[k]}.i", input.Name);
            Assert.Equal($"{names[k]}.o", output.Name);

            // Pin NUMBERS are the wiring: the stamp reads Nodes[2k] and Nodes[2k+1].
            Assert.Equal(2 * k + 1, input.PortIndex);
            Assert.Equal(2 * k + 2, output.PortIndex);

            Assert.True(input.LocalX < 0, $"{input.Name} must be on the left; it is at x={input.LocalX}.");
            Assert.True(output.LocalX > 0, $"{output.Name} must be on the right; it is at x={output.LocalX}.");
            Assert.Equal(input.LocalY, output.LocalY, 1e-9);
        }

        var reference = symbol.Pins[^1];
        Assert.Equal("REF", reference.Name);
        Assert.Equal(2 * arrays + 1, reference.PortIndex);
    }

    /// <summary>
    /// TIER 6 — the pin names match the model's <c>TerminalNames</c> exactly.
    ///
    /// <para>Two independent statements of the same ordering would drift, and the symptom would be a
    /// correctly-labelled pin wired to the wrong net. This test is what ties them together.</para>
    /// </summary>
    [Fact]
    public void Tier6_SymbolPinNames_MatchTheModelsTerminalNames()
    {
        var design = Design("G1", "G2", "D1", "MT");
        var symbol = WBondSymbolGenerator.Build(design);
        var model = new CircuitRF.Core.Devices.WBondModel(design);

        Assert.NotNull(symbol);
        Assert.Equal(model.PortCount, symbol!.Pins.Count);

        var terminals = model.TerminalNames;
        for (int i = 0; i < terminals.Length; i++)
            Assert.Equal(terminals[i], symbol.Pins[i].Name);
    }

    /// <summary>
    /// TIER 6 — <b>reordering arrays changes the content key</b>, so a cached symbol is not reused.
    ///
    /// <para>This is the MTee failure guarded against: same pin names, different order, wired to the
    /// wrong nets, and silent. The key must distinguish them.</para>
    /// </summary>
    [Fact]
    public void Tier6_ReorderingArrays_ChangesTheContentKey()
    {
        string a = WBondSymbolGenerator.ContentKey(Design("G1", "G2"));
        string b = WBondSymbolGenerator.ContentKey(Design("G2", "G1"));
        string same = WBondSymbolGenerator.ContentKey(Design("G1", "G2"));

        Assert.NotEqual(a, b);
        Assert.Equal(a, same);
    }

    /// <summary>Renaming or adding an array changes the key too.</summary>
    [Fact]
    public void Tier6_RenamingOrAddingAnArray_ChangesTheContentKey()
    {
        string baseline = WBondSymbolGenerator.ContentKey(Design("G1", "G2"));

        Assert.NotEqual(baseline, WBondSymbolGenerator.ContentKey(Design("G1", "D1")));
        Assert.NotEqual(baseline, WBondSymbolGenerator.ContentKey(Design("G1", "G2", "G3")));
    }

    /// <summary>
    /// The content key carries the generator's version, so bumping it invalidates every cached
    /// symbol — the mechanism that makes a generator fix actually take effect.
    /// </summary>
    [Fact]
    public void ContentKey_CarriesTheGeneratorVersion()
    {
        Assert.Contains($"v{WBondSymbolGenerator.ContentVersion}",
                        WBondSymbolGenerator.ContentKey(Design("G1")), System.StringComparison.Ordinal);
    }

    /// <summary>Every pin lands on the connection grid, or a wire cannot attach to it.</summary>
    [Fact]
    public void EveryPin_LandsOnTheConnectionGrid()
    {
        var symbol = WBondSymbolGenerator.Build(Design("G1", "G2", "G3"));
        Assert.NotNull(symbol);

        foreach (var pin in symbol!.Pins)
        {
            Assert.Equal(pin.LocalX, DsnSymbolReader.SnapToPinGrid(pin.LocalX), 1e-9);
            Assert.Equal(pin.LocalY, DsnSymbolReader.SnapToPinGrid(pin.LocalY), 1e-9);
        }
    }

    /// <summary>Pins are at distinct positions — two pins on one point cannot be wired separately.</summary>
    [Fact]
    public void EveryPin_IsAtADistinctPosition()
    {
        var symbol = WBondSymbolGenerator.Build(Design("G1", "G2", "G3", "G4", "G5"));
        Assert.NotNull(symbol);

        var positions = symbol!.Pins.Select(p => (p.LocalX, p.LocalY)).ToHashSet();
        Assert.Equal(symbol.Pins.Count, positions.Count);
    }

    /// <summary>A design with no arrays has no pins, so there is nothing placeable.</summary>
    [Fact]
    public void ADesignWithNoArrays_ProducesNoSymbol()
    {
        Assert.Null(WBondSymbolGenerator.Build(new WBondDesign()));
    }

    /// <summary>The body annotation says what the component is without opening it.</summary>
    [Fact]
    public void Describe_NamesArraysWiresAndTotalLength()
    {
        var design = Design("G1", "G2");
        string text = WBondSymbolGenerator.Describe(design);

        Assert.Contains("2 arrays", text, System.StringComparison.Ordinal);
        Assert.Contains("2 wires", text, System.StringComparison.Ordinal);
        Assert.Contains("mm", text, System.StringComparison.Ordinal);
    }

    /// <summary>Singular forms, because "1 arrays · 1 wires" reads as a bug.</summary>
    [Fact]
    public void Describe_UsesSingularFormsForOne()
    {
        string text = WBondSymbolGenerator.Describe(Design("G1"));

        Assert.Contains("1 array ", text, System.StringComparison.Ordinal);
        Assert.Contains("1 wire ", text, System.StringComparison.Ordinal);
    }
}
