// Overlapping copper on one level reaches the kernel as ONE conductor (round-7 field report): a placed
// part's footprint pad over an imported board's own pad was two polygons, the feed-lead decision read
// one of them, grew no lead, and the published S11 was an open circuit.

using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Ui.Layout;
using Xunit;

namespace CircuitRF.Ui.Tests.Em;

public sealed class OverlappingCopperMergeTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey TopCopper = new(1, 0);
    private static long Mm(double mm) => (long)Math.Round(mm * 1000 * Dbu);

    [Fact]
    public void OverlappingShapesAreMerged_AndASeparateOneIsLeftAlone()
    {
        var separate = new RectShape { Layer = TopCopper, X1 = Mm(30), Y1 = 0, X2 = Mm(32), Y2 = Mm(2.9) };
        var x = PlanarExtractor.Extract(
        [
            new RectShape { Layer = TopCopper, X1 = 0,        Y1 = 0,         X2 = Mm(20),   Y2 = Mm(2.9) },  // the board's line
            new RectShape { Layer = TopCopper, X1 = Mm(19),   Y1 = Mm(-0.2),  X2 = Mm(20.1), Y2 = Mm(2.6) },  // a footprint pad over its end
            separate,
        ], StarterTechnologies.Pcb2Layer(), Dbu, 5e9);

        Assert.True(x.Ok, x.Refusal);
        Assert.Equal(2, Assert.Single(x.Problem!.Layers).Polygons.Count);
        Assert.Contains(x.Notes, n => n.StartsWith("2 overlapping conductor shape(s) were merged into 1", StringComparison.Ordinal));
    }

    [Fact]
    public void ShapesThatOnlyTouchOrStandApart_AreNotMerged()
    {
        var x = PlanarExtractor.Extract(
        [
            new RectShape { Layer = TopCopper, X1 = 0,      Y1 = 0, X2 = Mm(20), Y2 = Mm(2.9) },
            new RectShape { Layer = TopCopper, X1 = Mm(20), Y1 = 0, X2 = Mm(25), Y2 = Mm(2.9) },   // abuts, no overlap
        ], StarterTechnologies.Pcb2Layer(), Dbu, 5e9);

        Assert.True(x.Ok, x.Refusal);
        Assert.Equal(2, Assert.Single(x.Problem!.Layers).Polygons.Count);
        Assert.DoesNotContain(x.Notes, n => n.Contains("were merged", StringComparison.Ordinal));
    }
}
