using CircuitRF.Design.Layout;
using Xunit;

namespace CircuitRF.Ui.Tests.SchematicSync;

/// <summary>
/// brief-sdl-9 R-sdl9-4: the C8 gap-coupled filter, placed. <see cref="SdlDesignSetTests"/> holds it LVS clean; this
/// holds what the manifest cannot say — each pair of resonators placed by the chain ends exactly the MGAP's
/// <c>S</c> (0.2 mm) apart, with no special case in the placer.
/// </summary>
public sealed class GapCoupledFilterTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "crf-sdl9-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void EachGap_IsExactlyS_InTheLayout()
    {
        var p = SdlChecker.Prepare("C8-gap-coupled-filter", _scratch);
        var layout = p.Applied(p.Plan());

        Bbox Box(string id) => CellHierarchy.InstanceBbox(layout.Instances.Single(i => i.SchematicId == id), p.LayoutDir);
        long Gap(Bbox a, Bbox b) => Math.Max(Math.Max(b.MinX - a.MaxX, a.MinX - b.MaxX), Math.Max(b.MinY - a.MaxY, a.MinY - b.MaxY));

        long s = 200 * layout.DbuPerMicron;
        Assert.Equal(s, Gap(Box("TL1"), Box("TL2")));
        Assert.Equal(s, Gap(Box("TL2"), Box("TL3")));
    }
}
