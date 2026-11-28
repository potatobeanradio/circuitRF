// Owner: PCD-14 (brief-pcd-14 R-pcd14-1). Headless, through the view model.

namespace CircuitRF.Ui.Tests.PCellDesigner;

public sealed class Pcd14SelectionTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "crf-pcd14s-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>R-pcd14-1: a click selects the part under it, Shift-click adds and removes, a click on nothing clears, and
    /// the layer picker adds a layer operand.</summary>
    [Fact]
    public async Task ClickAndShiftClick_SelectOperands_AndALayerIsAnOperand()
    {
        var (vm, p) = Pcd6Fixture.Open(_scratch);
        await vm.DesignAsync(0);
        (long, long) Centre(string id)
        {
            var b = Pcd6Fixture.Box(vm.Layout!, p.LayoutDir, id);
            return ((b.MinX + b.MaxX) / 2, (b.MinY + b.MaxY) / 2);
        }

        var (x1, y1) = Centre("TL1");
        vm.SelectAt(x1, y1, additive: false);
        Assert.Equal(["TL1"], vm.Selection);
        var (x2, y2) = Centre("TL2");
        vm.SelectAt(x2, y2, additive: true);
        Assert.Equal(["TL1", "TL2"], vm.Selection);
        vm.SelectAt(x1, y1, additive: true);
        Assert.Equal(["TL2"], vm.Selection);

        string layer = Assert.IsType<string>(vm.LayerNames.First());
        vm.AddLayerOperand(layer);
        Assert.Equal(["TL2", "layer:" + layer], vm.Selection);

        vm.SelectAt(x1 - 100_000_000, y1 - 100_000_000, additive: false);
        Assert.Empty(vm.Selection);
    }
}
