using CircuitRF.Core.Matching;
using CircuitRF.Core.Systems;
using CircuitRF.Ui.Filters;
using CircuitRF.Ui.Matching;
using Xunit;

namespace CircuitRF.Ui.Tests.Filters;

public class FilterLadderDrawingTests
{
    [Fact]
    public void AnEllipticLowpassStubSharesOneColumn()
    {
        var built = FilterLadder.Synthesize(
            FilterResponse.Elliptic, NetworkForm.Lowpass, 3,
            1e9, 0, 0, 0.1, 50, FilterFirstElement.Series, 40);
        Assert.True(built.Ok, built.Refusal?.Message);

        var layout = FilterLadderDrawing.Build(
            built.Realization!.Elements, 50, 50,
            (ind, v) => ind ? $"L={v}" : $"C={v}");

        // Series L, stub (L+C stacked), series L → three columns, four placed symbols.
        Assert.Equal(4, layout.Elements.Count);
        var stub = layout.Elements.Where(e => e.IsShunt).ToList();
        Assert.Equal(2, stub.Count);
        Assert.Equal(stub[0].X, stub[1].X);
        Assert.True(stub[1].Y > stub[0].Y);
    }

    [Fact]
    public void AParallelTankSharesOneSeriesColumnAboveTheSpine()
    {
        var built = FilterLadder.Synthesize(
            FilterResponse.Elliptic, NetworkForm.Lowpass, 5,
            1e9, 0, 0, 0.1, 50, FilterFirstElement.Parallel, 40);
        Assert.True(built.Ok, built.Refusal?.Message);

        var layout = FilterLadderDrawing.Build(
            built.Realization!.Elements, 50, 50,
            (ind, v) => ind ? $"L={v}" : $"C={v}");

        var tanks = layout.Elements.Where(e => !e.IsShunt).GroupBy(e => e.X)
            .Where(g => g.Count() == 2).ToList();
        Assert.NotEmpty(tanks); // elliptic order 5 has two finite-zero series arms
        foreach (var tank in tanks)
        {
            var onSpine = tank.Single(e => e.Y == MatchLadderLayout.SpineY);
            var above = tank.Single(e => e.Y < MatchLadderLayout.SpineY);
            Assert.Equal(ElementType.C, onSpine.Type);
            Assert.Equal(ElementType.L, above.Type);
        }
        Assert.Equal(tanks.Count, layout.ParallelArms.Count);
        Assert.All(layout.ParallelArms, a => Assert.True(a.Y < MatchLadderLayout.SpineY));
    }

    [Fact]
    public void AnEllipticBandpassBridgesSeriesAndParallelResonators()
    {
        var built = FilterLadder.Synthesize(
            FilterResponse.Elliptic, NetworkForm.Bandpass, 3,
            0, 1e9, 2e9, 0.1, 50, FilterFirstElement.Parallel, 40);
        Assert.True(built.Ok, built.Refusal?.Message);

        var layout = FilterLadderDrawing.Build(
            built.Realization!.Elements, 50, 50,
            (ind, v) => ind ? $"L={v}" : $"C={v}");

        // Eight elements: two shunt tanks (4) + series LC chain (2) + parallel LC (2).
        Assert.Equal(8, layout.Elements.Count);
        Assert.NotEmpty(layout.ParallelArms);

        // Parallel tank: both parts share mid-X on stacked rails (true L∥C), not side-by-side
        // on one rail (that drew two series-LC legs).
        var above = layout.Elements.Where(e => !e.IsShunt && e.Y < MatchLadderLayout.SpineY).ToList();
        Assert.Equal(2, above.Count);
        Assert.Equal(above[0].X, above[1].X);
        Assert.NotEqual(above[0].Y, above[1].Y);
        Assert.Contains(above, e => e.Type == ElementType.L);
        Assert.Contains(above, e => e.Type == ElementType.C);

        var spineSeries = layout.Elements
            .Where(e => !e.IsShunt && e.Y == MatchLadderLayout.SpineY).ToList();
        Assert.Equal(2, spineSeries.Count);
        Assert.NotEqual(spineSeries[0].X, spineSeries[1].X);

        // One rail per parallel-tank part, both above the spine.
        Assert.Equal(2, layout.ParallelArms.Count);
        Assert.All(layout.ParallelArms, a => Assert.True(a.Y < MatchLadderLayout.SpineY));

        // Compact: the upper rail sits within ~1.7·Lead of the spine (two 0.8·Lead steps).
        double lead = MatchSchematicGeometry.LeadHalf;
        Assert.All(above, e => Assert.True(MatchLadderLayout.SpineY - e.Y < 1.7 * lead + 1.0));

        // No two series symbols share the same (X, Y) — that was the overlapping drawing.
        Assert.Equal(
            layout.Elements.Count(e => !e.IsShunt),
            layout.Elements.Where(e => !e.IsShunt).Select(e => (e.X, e.Y)).Distinct().Count());
    }
}
