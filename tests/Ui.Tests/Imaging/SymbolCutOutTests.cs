// brief-img-8-schematic-symbols.md R-im8-5 — a multi-pin device is cut out; a two-pin region nothing matches is `?`.

using CircuitRF.Design.Schematic.Recognition;
using Xunit;

namespace CircuitRF.Ui.Tests.Imaging;

public sealed class SymbolCutOutTests
{
    [Fact]
    public void AFetWithThreeWires_IsCutOutWithThreePins()
    {
        var r = SymbolPictures.Read(SymbolPictures.Picture(SymbolPictures.Fet));

        var s = Assert.Single(r.Symbols);
        Assert.Equal(SymbolDisposition.CutOut, s.Disposition);
        Assert.Equal(3, s.Pins.Count);
        Assert.Equal(3, Assert.Single(r.Report.CutOut).Pins);
    }

    [Fact]
    public void AnUnknownBlobBetweenTwoWires_IsQuestionMarkWithTwoPins()
    {
        var r = SymbolPictures.Read(SymbolPictures.Picture(SymbolPictures.Blob));

        var s = Assert.Single(r.Symbols);
        Assert.Equal(SymbolDisposition.Unknown, s.Disposition);
        Assert.Equal("?", s.KindLabel);
        Assert.Equal(2, s.Pins.Count);
        Assert.Equal(1, r.Report.Unknown);
    }
}
