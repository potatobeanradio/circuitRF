// brief-img-9-text-and-values.md R-im9-3 — every value spelling a schematic writes is read by the bill-of-materials
// reader (BomTablePaste), the one value reader, to one SI number.

using CircuitRF.Design.Cells;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Schematic.Recognition;
using Xunit;

namespace CircuitRF.Ui.Tests.Imaging;

public sealed class ValueGrammarTests
{
    [Theory]
    [InlineData("10p", 10e-12, SymbolKind.Capacitor)]
    [InlineData("10pF", 10e-12, SymbolKind.Capacitor)]
    [InlineData("10 pF", 10e-12, SymbolKind.Capacitor)]
    [InlineData("0.5 pF", 0.5e-12, SymbolKind.Capacitor)]
    [InlineData("2n2", 2.2e-9, SymbolKind.Inductor)]
    [InlineData("2.2nH", 2.2e-9, SymbolKind.Inductor)]
    [InlineData("4R7", 4.7, SymbolKind.Resistor)]
    [InlineData("1k", 1e3, SymbolKind.Resistor)]
    [InlineData("1kΩ", 1e3, SymbolKind.Resistor)]
    public void EverySpelling_IsTheSharedReadersNumber(string text, double si, SymbolKind kind)
    {
        var r = ValueGrammar.Parse(text);
        Assert.Equal(WordClass.Value, r?.Class);
        Assert.Equal(si, r!.Value!.Si, si * 1e-12);

        // The same number the bill-of-materials reader gives the part it belongs to — read once, never a second way.
        Assert.True(BomTablePaste.TryReadValue(text, kind, false, out double bom, out var dim));
        Assert.Equal(bom, r.Value.Si, si * 1e-12);
        Assert.Contains(dim, r.Value.Dimensions);
    }

    [Fact]
    public void NotFittedMarkers_AreValuesOfNoPart()
    {
        foreach (string text in new[] { "DNP", "NC" })
        {
            var r = ValueGrammar.Parse(text);
            Assert.Equal(WordClass.Value, r?.Class);
            Assert.True(r!.Value!.NotFitted);
            Assert.True(r.Value.Fits(UnitDimension.Capacitance));
        }
    }
}
