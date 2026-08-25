using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Interchange;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// §2.1 item 4 — STRANS reflect-before-rotate order. GDSII reflects about the X-axis (negates Y);
/// our own model's MirrorX negates X. Every one of the 8 rotation×mirror combinations round-trips
/// through <see cref="GdsiiTransformCodec"/> exactly, and a hand-derived (not code-derived) worked
/// example pins the axis conversion itself, not just self-consistency.
///
/// <para><b>L3d (R-L3d-8) changed what the last two tests here assert, deliberately.</b> They used to
/// pin that a third-party arbitrary ANGLE was SNAPPED to a multiple of 90° with the discarded
/// remainder reported. GDSII always carried the angle; it was our own model that could not, and an
/// instance now can — so the same inputs must survive exactly. The tests were rewritten to that new
/// contract rather than deleted, because "a non-cardinal third-party angle arrives intact" is exactly
/// the property that used to be violated and is now the point.</para>
/// </summary>
public class GdsiiTransformCodecTests
{
    public static readonly TheoryData<bool, double> AllCombinations = new()
    {
        { false, 0.0 }, { false, 90.0 }, { false, 180.0 }, { false, 270.0 },
        { true, 0.0 }, { true, 90.0 }, { true, 180.0 }, { true, 270.0 },
    };

    [Theory]
    [MemberData(nameof(AllCombinations))]
    public void RoundTrips_AllEightCombinations_Exactly(bool mirrorX, double rotDeg)
    {
        var (reflect, angle) = GdsiiTransformCodec.ToGdsii(mirrorX, rotDeg);
        var (backMirror, backRot) = GdsiiTransformCodec.FromGdsii(reflect, angle);

        Assert.Equal(mirrorX, backMirror);
        Assert.Equal(rotDeg, backRot, 9);
    }

    // Hand-derived: reflect Y (x,y)->(x,-y), then rotate 90° CCW: (x,-y) -> (-(-y), x) = (y, x).
    // GDSII reflect=true, angle=90 must decode to our MirrorX=true, rotation=270 — verified against
    // LayoutInstanceTransform.TransformPoint's own 270° result (my, -mx) by hand before writing this.
    [Fact]
    public void FromGdsii_ReflectTrueAngle90_MapsToMirrorTrue270()
    {
        var (mirror, rotDeg) = GdsiiTransformCodec.FromGdsii(true, 90.0);
        Assert.True(mirror);
        Assert.Equal(270.0, rotDeg, 9);
    }

    [Fact]
    public void FromGdsii_ReflectTrueAngle0_MapsToMirrorTrue180()
    {
        var (mirror, rotDeg) = GdsiiTransformCodec.FromGdsii(true, 0.0);
        Assert.True(mirror);
        Assert.Equal(180.0, rotDeg, 9);
    }

    [Fact]
    public void FromGdsii_NoReflect_MapsAngleDirectly()
    {
        var (mirror, rotDeg) = GdsiiTransformCodec.FromGdsii(false, 90.0);
        Assert.False(mirror);
        Assert.Equal(90.0, rotDeg, 9);
    }

    /// <summary>R-L3d-8: was "snaps to nearest quadrant and reports Δ=33". Now survives.</summary>
    [Fact]
    public void FromGdsii_ArbitraryAngle_SurvivesExactly()
    {
        var (_, rotDeg) = GdsiiTransformCodec.FromGdsii(false, 33.0);
        Assert.Equal(33.0, rotDeg, 9);
    }

    /// <summary>R-L3d-8: was "265° snaps to R270 reporting Δ=5". Now survives — and, mirrored, still
    /// composes through the +180 correction rather than losing it along with the snap.</summary>
    [Fact]
    public void FromGdsii_ArbitraryAngleNear270_SurvivesExactly_MirroredAndNot()
    {
        var (_, plain) = GdsiiTransformCodec.FromGdsii(false, 265.0);
        Assert.Equal(265.0, plain, 9);

        var (mirror, reflected) = GdsiiTransformCodec.FromGdsii(true, 265.0);
        Assert.True(mirror);
        Assert.Equal(85.0, reflected, 9);   // 265 + 180, normalized
    }
}
