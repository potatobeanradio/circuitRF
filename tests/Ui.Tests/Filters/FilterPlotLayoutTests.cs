using CircuitRF.Ui.Views.Filters;
using Xunit;

namespace CircuitRF.Ui.Tests.Filters;

/// <summary>The Filter Designer's mag/phase pane — one plot fills; both sit side by side.</summary>
public class FilterPlotLayoutTests
{
    [Fact]
    public void BothOn_SitSideBySide()
    {
        var slots = FilterPlotLayout.SlotsFor(magnitude: true, phase: true, width: 400, height: 200);
        Assert.True(slots.Magnitude.Width > 0);
        Assert.True(slots.Phase.Width > 0);
        Assert.Equal(0, slots.Magnitude.X, 3);
        Assert.True(slots.Phase.X > slots.Magnitude.Right);
        Assert.Equal(200, slots.Magnitude.Height, 3);
        Assert.Equal(200, slots.Phase.Height, 3);
    }

    [Fact]
    public void MagnitudeAlone_FillsThePane()
    {
        var slots = FilterPlotLayout.SlotsFor(magnitude: true, phase: false, width: 400, height: 200);
        Assert.True(slots.Magnitude.Width > 0);
        Assert.Equal(0, slots.Phase.Width, 3);
        // Lone rect is centred and aspect-fitted, so it uses most of the pane.
        Assert.True(slots.Magnitude.Width >= 300);
        Assert.True(slots.Magnitude.Height >= 150);
    }

    [Fact]
    public void PhaseAlone_FillsThePane()
    {
        var slots = FilterPlotLayout.SlotsFor(magnitude: false, phase: true, width: 400, height: 200);
        Assert.Equal(0, slots.Magnitude.Width, 3);
        Assert.True(slots.Phase.Width >= 300);
    }

    [Fact]
    public void Neither_LeavesEmptySlots()
    {
        var slots = FilterPlotLayout.SlotsFor(magnitude: false, phase: false, width: 400, height: 200);
        Assert.Equal(0, slots.Magnitude.Width, 3);
        Assert.Equal(0, slots.Phase.Width, 3);
    }
}
