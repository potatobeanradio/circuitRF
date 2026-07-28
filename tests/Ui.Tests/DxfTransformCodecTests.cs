using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Interchange;

namespace CircuitRF.Ui.Tests;

/// <summary>R-L4b-2 — mirror is xscale=-1, DIRECT mapping (no reflect-then-rotate-180 trick GDSII's
/// STRANS needs), since DXF's INSERT scale-then-rotate order already matches
/// LayoutInstanceTransform's own "negate local X before rotation" convention.</summary>
public class DxfTransformCodecTests
{
    [Theory]
    [InlineData(false, LayoutRotation.R0)]
    [InlineData(false, LayoutRotation.R90)]
    [InlineData(false, LayoutRotation.R180)]
    [InlineData(false, LayoutRotation.R270)]
    [InlineData(true, LayoutRotation.R0)]
    [InlineData(true, LayoutRotation.R90)]
    [InlineData(true, LayoutRotation.R180)]
    [InlineData(true, LayoutRotation.R270)]
    public void ToDxf_FromDxf_RoundTrips_AllEightCombinations(bool mirrorX, LayoutRotation rot)
    {
        var (xscale, yscale, rotDeg) = DxfTransformCodec.ToDxf(mirrorX, rot, mag: 2.0);
        var (outMirror, outRot, outMag) = DxfTransformCodec.FromDxf(xscale, yscale, rotDeg, out var delta, out var mismatch);

        Assert.Equal(mirrorX, outMirror);
        Assert.Equal(rot, outRot);
        Assert.Equal(2.0, outMag, 9);
        Assert.Equal(0.0, delta, 9);
        Assert.False(mismatch);
    }

    [Fact]
    public void ToDxf_MirrorX_NegatesOnlyXScale_DirectMapping_NoRotationAdjustment()
    {
        // The trap: naively porting GDSII's reflect-then-rotate-180 correction here would add 180 to
        // the rotation. DXF's own scale-then-rotate order needs NO such adjustment.
        var (xscale, yscale, rotDeg) = DxfTransformCodec.ToDxf(mirrorX: true, LayoutRotation.R90, mag: 1.0);
        Assert.Equal(-1.0, xscale, 9);
        Assert.Equal(1.0, yscale, 9);
        Assert.Equal(90.0, rotDeg, 9); // NOT 270 — no +180 trick
    }

    [Fact]
    public void FromDxf_ArbitraryAngle_SnapsToNearestQuadrant_ReportsDelta()
    {
        var (_, rot, _) = DxfTransformCodec.FromDxf(1.0, 1.0, 88.0, out var delta, out _);
        Assert.Equal(LayoutRotation.R90, rot);
        Assert.Equal(2.0, delta, 6);
    }

    [Fact]
    public void FromDxf_NonUniformScale_ReportsMismatch()
    {
        DxfTransformCodec.FromDxf(2.0, 3.0, 0.0, out _, out var mismatch);
        Assert.True(mismatch);
    }
}
