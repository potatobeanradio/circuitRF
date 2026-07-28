using CircuitRF.Ui.Layout.Interchange;

namespace CircuitRF.Ui.Tests;

public class GdsiiStructureNamingTests
{
    [Fact]
    public void MangleForExport_SanitizesIllegalCharacters()
    {
        var map = GdsiiStructureNaming.MangleForExport(["My Cell!", "Amp-v2"]);
        Assert.Equal("My_Cell_", map["My Cell!"]);
        Assert.Equal("Amp_v2", map["Amp-v2"]);
    }

    [Fact]
    public void MangleForExport_CollidingNames_GetDistinctSuffixes()
    {
        // Both "Amp!" and "Amp " sanitize to "Amp_" before dedup ('!' and ' ' are both illegal in
        // GDSII's own charset — unlike '?', which is legal and passes through unchanged).
        var map = GdsiiStructureNaming.MangleForExport(["Amp!", "Amp "]);
        Assert.Equal("Amp_", map["Amp!"]);
        Assert.Equal("Amp__2", map["Amp "]);
        Assert.NotEqual(map["Amp!"], map["Amp "]);
    }

    [Fact]
    public void MangleForExport_TruncatesToMaxLengthAndKeepsUnique()
    {
        var longName = new string('A', 250);
        var map = GdsiiStructureNaming.MangleForExport([longName, longName + "X"]);
        Assert.True(map[longName].Length <= GdsiiStructureNaming.MaxLength);
        Assert.NotEqual(map[longName], map[longName + "X"]);
    }

    [Fact]
    public void NameCellsForImport_SanitizesFilesystemIllegalCharacters()
    {
        var map = GdsiiStructureNaming.NameCellsForImport(["CELL?1"]);
        Assert.Equal("CELL_1", map["CELL?1"]);
    }

    [Fact]
    public void MangleForExport_EmptyName_NeverProducesEmptyResult()
    {
        var map = GdsiiStructureNaming.MangleForExport([""]);
        Assert.Equal("_", map[""]);
    }

    [Theory]
    [InlineData("Amp1")]
    [InlineData("via_cell")]
    public void MangleForExport_AlreadyLegalName_Unchanged(string name)
        => Assert.Equal(name, GdsiiStructureNaming.MangleForExport([name])[name]);
}
