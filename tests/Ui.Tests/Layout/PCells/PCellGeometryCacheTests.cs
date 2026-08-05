using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout.PCells;

/// <summary>Gate 5: "a 50x50 array of one PCell evaluates the generator once; assert the call
/// count, not the timing" (R-pc-5).</summary>
public class PCellGeometryCacheTests
{
    private static readonly Technology Pcb = StarterTechnologies.Pcb2Layer();

    [Fact]
    public void RepeatedIdenticalParameters_InvokesGeneratorExactlyOnce()
    {
        var cache = new PCellGeometryCache();
        var parameters = new Dictionary<string, PCellValue> { ["W"] = 0.0029, ["L"] = 0.01 };

        for (int i = 0; i < 2500; i++) // the 50x50-array gate
            cache.GetOrGenerate(MlinPCell.GeneratorId, MlinPCell.Generate, parameters, Pcb, PCellLayerSelection.Default);

        Assert.Equal(1, cache.GeneratorCallCount);
    }

    [Fact]
    public void DifferentParameters_InvokesGeneratorOncePerUniqueSet()
    {
        var cache = new PCellGeometryCache();
        for (int i = 0; i < 10; i++)
        {
            var parameters = new Dictionary<string, PCellValue> { ["W"] = 0.0029, ["L"] = 0.001 * (i % 3) };
            cache.GetOrGenerate(MlinPCell.GeneratorId, MlinPCell.Generate, parameters, Pcb, PCellLayerSelection.Default);
        }
        Assert.Equal(3, cache.GeneratorCallCount); // only 3 distinct L values among the 10 calls
    }

    [Fact]
    public void DifferentTechnology_InvokesGeneratorAgain()
    {
        var cache = new PCellGeometryCache();
        var parameters = new Dictionary<string, PCellValue> { ["W"] = 0.0029, ["L"] = 0.01 };
        var mmic = StarterTechnologies.MmicGaAs();

        cache.GetOrGenerate(MlinPCell.GeneratorId, MlinPCell.Generate, parameters, Pcb, PCellLayerSelection.Default);
        cache.GetOrGenerate(MlinPCell.GeneratorId, MlinPCell.Generate, parameters, mmic, PCellLayerSelection.Default);

        Assert.Equal(2, cache.GeneratorCallCount);
    }

    [Fact]
    public void CachedResult_IsReturnedUnchanged()
    {
        var cache = new PCellGeometryCache();
        var parameters = new Dictionary<string, PCellValue> { ["W"] = 0.0029, ["L"] = 0.01 };

        var first = cache.GetOrGenerate(MlinPCell.GeneratorId, MlinPCell.Generate, parameters, Pcb, PCellLayerSelection.Default);
        var second = cache.GetOrGenerate(MlinPCell.GeneratorId, MlinPCell.Generate, parameters, Pcb, PCellLayerSelection.Default);

        Assert.Same(first, second); // the cache returns the SAME PCellResult instance on a hit
        Assert.Equal(1, cache.GeneratorCallCount);
    }
}
