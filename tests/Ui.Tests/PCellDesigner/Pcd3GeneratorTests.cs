// Owner: PCD-3 (brief-pcd-3 R-pcd3-2).

using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.PCells;

namespace CircuitRF.Ui.Tests.PCellDesigner;

public sealed class Pcd3GeneratorTests
{
    /// <summary>R-pcd3-2: MSHAPE draws its polygon, or its path W wide, on the signal layer with its pins as declared; a
    /// polygon short of three vertices draws nothing and says so.</summary>
    [Fact]
    public void MShapeDrawsItsOutlineOrPathWithItsPins()
    {
        Assert.True(PCellRegistry.TryGet("MSHAPE", out var generate));
        var p = new Dictionary<string, PCellValue>
        {
            ["Form"] = PCellValue.Text("\"polygon\""),
            ["X[1]"] = 0.0, ["Y[1]"] = -50e-6, ["X[2]"] = 100e-6, ["Y[2]"] = -50e-6,
            ["X[3]"] = 100e-6, ["Y[3]"] = 50e-6, ["X[4]"] = 0.0, ["Y[4]"] = 50e-6,
            ["PinX[1]"] = 0.0, ["PinY[1]"] = 0.0, ["PinW[1]"] = 100e-6, ["PinDir[1]"] = 180.0,
            ["PinX[2]"] = 100e-6, ["PinY[2]"] = 0.0, ["PinW[2]"] = 20e-6, ["PinDir[2]"] = 0.0,
        };
        var square = generate(p, null, PCellLayerSelection.Default);
        var poly = Assert.IsType<PolygonShape>(Assert.Single(square.Shapes));
        Assert.Equal(new long[] { 0, -50_000, 100_000, -50_000, 100_000, 50_000, 0, 50_000 }, poly.Xy);
        Assert.Equal(new[] { new PCellPin("1", 0, 0, poly.Layer, 100_000, 180), new PCellPin("2", 100_000, 0, poly.Layer, 20_000, 0) },
                     square.Pins);

        p["Form"] = PCellValue.Text("path");
        p["W"] = 10e-6;
        var path = Assert.IsType<PathShape>(Assert.Single(generate(p, null, PCellLayerSelection.Default).Shapes));
        Assert.Equal(10_000, path.Width);
        Assert.Equal(8, path.Xy.Length);

        p["Form"] = PCellValue.Text("polygon");
        p.Remove("X[3]");
        var refused = generate(p, null, PCellLayerSelection.Default);
        Assert.Empty(refused.Shapes);
        Assert.Contains("three vertices", Assert.Single(refused.Diagnostics!));
    }
}
