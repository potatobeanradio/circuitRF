using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gates for brief-housekeeping-tearoff-palette-repo.md §7A.4/R-hk-19a: loading a `.csch` that
/// names a component type this build doesn't recognize (e.g. "FET", after §7A's hard removal of
/// the library FET) must not crash, must not silently drop the component, and must still load the
/// REST of the schematic. The unknown component itself becomes <see cref="SymbolKind.Unknown"/>
/// with the original raw type string preserved for reporting by name.
/// </summary>
public class UnknownComponentTypeTests
{
    private const string HandCraftedCsch = """
        {
          "FormatVersion": 2,
          "CellName": "LegacyFetTest",
          "Components": [
            { "InstanceName": "R1", "Symbol": "Resistor", "X": 0, "Y": 0,
              "Parameters": [ { "Name": "R", "Expression": "50", "Unit": "Ω" } ] },
            { "InstanceName": "X1", "Symbol": "FET", "X": 600, "Y": 200 },
            { "InstanceName": "C1", "Symbol": "Capacitor", "X": 300, "Y": 0,
              "Parameters": [ { "Name": "C", "Expression": "1", "Unit": "pF" } ] }
          ]
        }
        """;

    [Fact]
    public void UnknownSymbol_DoesNotThrow_LoadsSuccessfully()
    {
        var (model, _, cellName) = SchematicPersistence.Deserialize(HandCraftedCsch);
        Assert.Equal("LegacyFetTest", cellName);
        Assert.Equal(3, model.Components.Count); // nothing dropped
    }

    [Fact]
    public void UnknownSymbol_BecomesUnknownKind_WithOriginalNamePreserved()
    {
        var (model, _, _) = SchematicPersistence.Deserialize(HandCraftedCsch);
        var unknown = model.Components.Single(c => c.InstanceName == "X1");

        Assert.Equal(SymbolKind.Unknown, unknown.Symbol);
        Assert.Equal("FET", unknown.UnknownSymbolRawName);
        Assert.Equal(600, unknown.X);
        Assert.Equal(200, unknown.Y);
    }

    [Fact]
    public void UnknownSymbol_SiblingComponents_StillLoadCorrectly()
    {
        var (model, _, _) = SchematicPersistence.Deserialize(HandCraftedCsch);

        var r1 = model.Components.Single(c => c.InstanceName == "R1");
        Assert.Equal(SymbolKind.Resistor, r1.Symbol);
        Assert.Equal("50", r1.Parameters.Single(p => p.Name == "R").Expression);

        var c1 = model.Components.Single(c => c.InstanceName == "C1");
        Assert.Equal(SymbolKind.Capacitor, c1.Symbol);
        Assert.Null(c1.UnknownSymbolRawName);
    }

    [Fact]
    public void UnknownSymbol_RoundTrips_RawNamePreservedAcrossSaveReload()
    {
        var (model, _, cellName) = SchematicPersistence.Deserialize(HandCraftedCsch);
        string json = SchematicPersistence.Serialize(model, cellName);

        var (reloaded, _, _) = SchematicPersistence.Deserialize(json);
        var unknown = reloaded.Components.Single(c => c.InstanceName == "X1");

        Assert.Equal(SymbolKind.Unknown, unknown.Symbol);
        Assert.Equal("FET", unknown.UnknownSymbolRawName);
    }

    [Fact]
    public void UnknownKind_RendersAsGenericPlaceholder_NoThrow()
    {
        var sym = BuiltInSymbols.Primitives(SymbolKind.Unknown);
        Assert.NotNull(sym);
        Assert.NotEmpty(sym.Primitives);
    }

    [Fact]
    public void MalformedComponentElement_NeverThrows_StillProducesAPlaceholder()
    {
        const string malformed = """
            {
              "FormatVersion": 2,
              "CellName": "Malformed",
              "Components": [
                { "InstanceName": "R1", "Symbol": "Resistor", "X": 0, "Y": 0 },
                { "InstanceName": "X1", "Symbol": "FET", "X": "not-a-number", "Y": 200 }
              ]
            }
            """;

        var (model, _, _) = SchematicPersistence.Deserialize(malformed);
        Assert.Equal(2, model.Components.Count);
        var unknown = model.Components.Single(c => c.InstanceName == "X1");
        Assert.Equal(SymbolKind.Unknown, unknown.Symbol);
        Assert.Equal("FET", unknown.UnknownSymbolRawName);
    }
}
