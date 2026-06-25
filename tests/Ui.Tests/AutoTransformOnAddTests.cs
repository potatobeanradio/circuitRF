// ================================================================
//  AutoTransformOnAddTests.cs — auto-transform only for complex data
//
//  Adding/selecting a trace on a Rect plot auto-applies a transform ONLY
//  when the source data is complex (dB20 for S/Y/Z, mag otherwise), so the
//  user doesn't see an annoying "mag" on already-real data. Shared by the
//  seed (BuildSeedCubeTrace) and the signal-switch (OnSelectedSignalChanged).
// ================================================================

using System.Numerics;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class AutoTransformOnAddTests
{
    private static DataCube RealCube()
        => new(new[] { new Axis("Pin", new[] { 0.0, 5.0 }) }, new double[] { 1, 2 });

    private static DataCube ComplexCube()
        => new(new[] { new Axis("Pin", new[] { 0.0, 5.0 }) }, new Complex[] { new(1, 1), new(2, 2) });

    private static DataCube ParameterCube()   // S-parameter cube: freq, i, j
        => new(new[]
        {
            new Axis("freq", new[] { 1e9, 2e9 }, "Hz"),
            new Axis("i",    new[] { 1.0, 2.0 }),
            new Axis("j",    new[] { 1.0, 2.0 }),
        }, new Complex[2 * 2 * 2]);

    [Fact]
    public void RealCube_OnRect_GetsNoTransform()
        => Assert.Equal(CubeTransform.None,
            TraceRowViewModel.DefaultTransformFor(RealCube(), PlotType.Rect));

    [Fact]
    public void ComplexCube_OnRect_GetsMag()
        => Assert.Equal(CubeTransform.Mag,
            TraceRowViewModel.DefaultTransformFor(ComplexCube(), PlotType.Rect));

    [Fact]
    public void ParameterCube_OnRect_GetsDb20()
        => Assert.Equal(CubeTransform.dB20,
            TraceRowViewModel.DefaultTransformFor(ParameterCube(), PlotType.Rect));

    [Fact]
    public void ComplexCube_OnSmith_GetsNoTransform()   // non-Rect → no auto-transform
        => Assert.Equal(CubeTransform.None,
            TraceRowViewModel.DefaultTransformFor(ComplexCube(), PlotType.Smith));

    [Fact]
    public void RealCube_OnTable_GetsNoTransform()
        => Assert.Equal(CubeTransform.None,
            TraceRowViewModel.DefaultTransformFor(RealCube(), PlotType.Table));
}
