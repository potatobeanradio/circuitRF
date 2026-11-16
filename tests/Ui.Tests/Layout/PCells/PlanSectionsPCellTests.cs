using System.Text.Json;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.PCells;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout.PCells;

/// <summary>
/// The gap, taper and open-end plan sections draw through their own parts' generators: a taper is MTAPER's trapezoid,
/// a gap is MGAP's two ends and no copper, so the lines either side end S apart, and an open end draws nothing.
/// </summary>
public class PlanSectionsPCellTests
{
    private static readonly PCellLayerSelection Layers = PCellLayerSelection.Default;

    private static string Art(PCellResult art)
        => string.Join("\n", art.Shapes.Select(s => s.GetType().Name + JsonSerializer.Serialize(s, s.GetType())))
         + "\n" + string.Join("\n", art.Pins.Select(p => $"{p.Name} {p.X} {p.Y} {p.Layer} {p.WidthDbu} {p.OutwardDirectionDeg}"));

    private static PCellResult Draw(params PlanSection[] sections)
        => PassivePlanPCells.Draw(new PassivePlan(["1", "2"], sections, 4), null, Layers);

    [Fact]
    public void ATaper_IsMtapersOwnArtwork()
        => Assert.Equal(
            Art(MTaperPCell.Generate(new Dictionary<string, PCellValue> { ["W1"] = 1.1e-3, ["W2"] = 0.4e-3, ["L"] = 6e-3 }, null, Layers)),
            Art(Draw(new PlanTaper([1, 2], 0.0, 0.0, 0.0, 1.1e-3, 0.4e-3, 6e-3))));

    [Fact]
    public void AGap_IsMgapsOwnEnds_AndItsLinesEndSApart()
    {
        Assert.Equal(Art(MGapPCell.Generate(new Dictionary<string, PCellValue> { ["W"] = 1e-3, ["S"] = 0.2e-3 }, null, Layers)),
            Art(Draw(new PlanGap([1, 2], 0.0, 0.0, 0.0, 1e-3, 0.2e-3))));

        var art = Draw(
            new PlanLine([1, 3], 0.0, 0.0, 0.0, 1e-3, 5e-3),
            new PlanGap([3, 4], 5e-3, 0.0, 0.0, 1e-3, 0.2e-3),
            new PlanLine([4, 2], 5.2e-3, 0.0, 0.0, 1e-3, 5e-3));
        var lines = art.Shapes.Cast<RectShape>().ToArray();
        Assert.Equal(2, lines.Length);
        Assert.Equal(200_000L, lines[1].X1 - lines[0].X2);
    }

    [Fact]
    public void AnOpenEnd_DrawsNothing()
        => Assert.Equal(
            Art(MlinPCell.Generate(new Dictionary<string, PCellValue> { ["W"] = 1e-3, ["L"] = 5e-3 }, null, Layers)),
            Art(Draw(new PlanLine([1, 2], 0.0, 0.0, 0.0, 1e-3, 5e-3), new PlanOpen([2], 5e-3, 0.0, 0.0, 1e-3))));
}
