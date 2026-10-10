// brief-img-8-schematic-symbols.md R-im8-4 — a structure check settles what a distance cannot.

using System.Linq;
using CircuitRF.Design.Schematic.Recognition;
using Xunit;

namespace CircuitRF.Ui.Tests.Imaging;

public sealed class SymbolStructureTests
{
    [Fact]
    public void TwoParallelBarsWithNoGap_AreNotACapacitor()
    {
        var r = SymbolPictures.Read(SymbolPictures.Picture(SymbolPictures.JoinedBars));

        var s = Assert.Single(r.Symbols);
        Assert.NotEqual(ImageSymbolKind.Capacitor, s.Kind);
        Assert.Contains(s.Demotions, d => d.Failed == StructureCheck.Capacitor);
        Assert.True(r.Report.Demotions[StructureCheck.Capacitor] > 0);
    }
}
