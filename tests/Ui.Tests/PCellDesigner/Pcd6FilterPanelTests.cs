// Owner: PCD-6 (the window's part of brief-pcd-12 R-pcd12-3). Headless, through the view model.

using CircuitRF.Core.Synthesis.Filters;
using CircuitRF.Core.Systems;
using CircuitRF.Design.Filters;
using CircuitRF.Design.PCellDesigner;
using CircuitRF.Ui.PCellDesigner;

namespace CircuitRF.Ui.Tests.PCellDesigner;

public sealed class Pcd6FilterPanelTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "crf-pcd6f-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>A cell made by Make PCell… shows its filter metrics, read off each circuit response; any other cell does not.</summary>
    [Fact]
    public async Task AFilterCell_ShowsItsMetricsFromTheResponse_AndOtherCellsDoNot()
    {
        using var ws = new Pcd5Workspace();
        var spec = new FilterSpec(FilterResponse.Chebyshev, FilterBand.Bandpass, 3, 2.2e9, 2.6e9, 0.1);
        var realiser = new TappedHairpinRealiser();
        var made = PCellFilter.Make(ws.Root, "Panel", spec, realiser.Realise(spec, FilterSynthesisService.Substrate(ws.Tech, null, null, out _)),
                                    realiser.Name);
        Assert.NotNull(made.CellDir);

        var vm = new PCellDesignerViewModel(made.CellDir!, ws.Root);
        Assert.True(vm.IsFilterCell);
        await vm.DesignAsync(0);
        Assert.True(vm.FilterMetrics is { Filter: not null, Source: "Circuit response" }, vm.FilterMetrics?.Refusal ?? vm.Status);

        var (other, _) = Pcd6Fixture.Open(_scratch);
        Assert.False(other.IsFilterCell);
        await other.DesignAsync(0);
        Assert.Null(other.FilterMetrics);
    }
}
