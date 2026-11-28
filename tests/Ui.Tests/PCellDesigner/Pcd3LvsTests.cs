// Owner: PCD-3 (brief-pcd-3 R-pcd3-4).

using CircuitRF.Design.Layout.Lvs;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Tests.Layout.PCells;

namespace CircuitRF.Ui.Tests.PCellDesigner;

[Collection(PCellResolverCollection.Name)]
public sealed class Pcd3LvsTests
{
    /// <summary>R-pcd3-4: drawn metal is a transmission line to LVS, and its generator id names its kind.</summary>
    [Fact]
    public void MShapeIsATransmissionLineAndItsGeneratorNamesIt()
    {
        Assert.Equal(DeviceKind.TransmissionLine, DeviceTypes.Of(SymbolKind.MShape));
        Assert.True(DeviceTypes.TryGetSymbolKind("MSHAPE", out var kind));
        Assert.Equal(SymbolKind.MShape, kind);
    }
}
