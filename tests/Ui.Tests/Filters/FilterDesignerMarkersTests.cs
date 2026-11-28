using CircuitRF.Core.Synthesis.Filters;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.Filters;
using Xunit;

namespace CircuitRF.Ui.Tests.Filters;

/// <summary>The Filter Designer's response plot keeps its markers when a spec edit rebuilds its traces.</summary>
public sealed class FilterDesignerMarkersTests
{
    /// <summary>A marker on the ideal S21 is on the rebuilt ideal S21 after the band is edited, at the same x.</summary>
    [Fact]
    public async Task Markers_SurviveARerun()
    {
        var vm = new FilterDesignerViewModel(null, realisers: [new TappedHairpinRealiser()],
            technology: CircuitRF.Design.Layout.ShippedTechnologies.Load(CircuitRF.Design.Layout.ShippedTechnologies.DefaultId));
        await vm.Realising;
        var s21 = vm.Plot.Traces.Single(t => t.Label == "ideal S21");
        var marker = new Marker(s21, 2.4e9, isMulti: false, isDelta: false, index: vm.ResponseContainer.GetNextMarkerIndex());
        s21.Markers.Add(marker);

        vm.F2 = "2.7 GHz";
        await vm.Realising;
        var rebuilt = vm.Plot.Traces.Single(t => t.Label == "ideal S21");
        Assert.NotSame(s21, rebuilt);
        Assert.Same(marker, Assert.Single(rebuilt.Markers));
        Assert.Equal(2.4e9, marker.Freq);
    }
}
