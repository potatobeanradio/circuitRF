using CircuitRF.Design.Layout.Interchange;

namespace CircuitRF.Ui.Tests;

/// <summary>R-L4b-2 — mirror is xscale=-1, DIRECT mapping (no reflect-then-rotate-180 trick GDSII's
/// STRANS needs), since DXF's INSERT scale-then-rotate order already matches
/// LayoutInstanceTransform's own "negate local X before rotation" convention.
///
/// <para><b>L3d (R-L3d-8) changed the arbitrary-angle test deliberately</b> — see
/// <see cref="GdsiiTransformCodecTests"/>'s own note for the reasoning. The yscale-mismatch report is
/// unrelated to rotation and is unchanged: a non-uniform INSERT is still something our (Mag, MirrorX)
/// model genuinely cannot represent.</para></summary>
public class DxfTransformCodecTests
{
    [Theory]
    [InlineData(false, 0.0)]
    [InlineData(false, 90.0)]
    [InlineData(false, 180.0)]
    [InlineData(false, 270.0)]
    [InlineData(true, 0.0)]
    [InlineData(true, 90.0)]
    [InlineData(true, 180.0)]
    [InlineData(true, 270.0)]
    public void ToDxf_FromDxf_RoundTrips_AllEightCombinations(bool mirrorX, double rotDeg)
    {
        var (xscale, yscale, outDeg) = DxfTransformCodec.ToDxf(mirrorX, rotDeg, mag: 2.0);
        var (outMirror, outRot, outMag) = DxfTransformCodec.FromDxf(xscale, yscale, outDeg, out var mismatch);

        Assert.Equal(mirrorX, outMirror);
        Assert.Equal(rotDeg, outRot, 9);
        Assert.Equal(2.0, outMag, 9);
        Assert.False(mismatch);
    }

    [Fact]
    public void ToDxf_MirrorX_NegatesOnlyXScale_DirectMapping_NoRotationAdjustment()
    {
        // The trap: naively porting GDSII's reflect-then-rotate-180 correction here would add 180 to
        // the rotation. DXF's own scale-then-rotate order needs NO such adjustment.
        var (xscale, yscale, rotDeg) = DxfTransformCodec.ToDxf(mirrorX: true, 90.0, mag: 1.0);
        Assert.Equal(-1.0, xscale, 9);
        Assert.Equal(1.0, yscale, 9);
        Assert.Equal(90.0, rotDeg, 9); // NOT 270 — no +180 trick
    }

    /// <summary>R-L3d-8: was "88° snaps to R90 and reports Δ=2". Now survives.</summary>
    [Fact]
    public void FromDxf_ArbitraryAngle_SurvivesExactly()
    {
        var (_, rotDeg, _) = DxfTransformCodec.FromDxf(1.0, 1.0, 88.0, out _);
        Assert.Equal(88.0, rotDeg, 9);
    }

    [Fact]
    public void FromDxf_NegativeAngle_NormalizesIntoRange()
    {
        var (_, rotDeg, _) = DxfTransformCodec.FromDxf(1.0, 1.0, -30.0, out _);
        Assert.Equal(330.0, rotDeg, 9);
    }

    [Fact]
    public void FromDxf_NonUniformScale_ReportsMismatch()
    {
        DxfTransformCodec.FromDxf(2.0, 3.0, 0.0, out var mismatch);
        Assert.True(mismatch);
    }
}
