using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Interchange;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// §2.1 item 4 — STRANS reflect-before-rotate order. GDSII reflects about the X-axis (negates Y);
/// our own model's MirrorX negates X. Every one of the 8 rotation×mirror combinations round-trips
/// through <see cref="GdsiiTransformCodec"/> exactly, and a hand-derived (not code-derived) worked
/// example pins the axis conversion itself, not just self-consistency.
/// </summary>
public class GdsiiTransformCodecTests
{
    public static readonly TheoryData<bool, LayoutRotation> AllCombinations = new()
    {
        { false, LayoutRotation.R0 }, { false, LayoutRotation.R90 },
        { false, LayoutRotation.R180 }, { false, LayoutRotation.R270 },
        { true, LayoutRotation.R0 }, { true, LayoutRotation.R90 },
        { true, LayoutRotation.R180 }, { true, LayoutRotation.R270 },
    };

    [Theory]
    [MemberData(nameof(AllCombinations))]
    public void RoundTrips_AllEightCombinations_ExactlyNoSnapDelta(bool mirrorX, LayoutRotation rot)
    {
        var (reflect, angle) = GdsiiTransformCodec.ToGdsii(mirrorX, rot);
        var (backMirror, backRot) = GdsiiTransformCodec.FromGdsii(reflect, angle, out var delta);

        Assert.Equal(mirrorX, backMirror);
        Assert.Equal(rot, backRot);
        Assert.Equal(0.0, delta, 9);
    }

    // Hand-derived: reflect Y (x,y)->(x,-y), then rotate 90° CCW: (x,-y) -> (-(-y), x) = (y, x).
    // GDSII reflect=true, angle=90 must decode to our MirrorX=true, Rot=270 — verified against
    // LayoutInstanceTransform.TransformPoint's own R270 formula (my, -mx) by hand before writing this.
    [Fact]
    public void FromGdsii_ReflectTrueAngle90_MapsToMirrorTrueRot270()
    {
        var (mirror, rot) = GdsiiTransformCodec.FromGdsii(true, 90.0, out var delta);
        Assert.True(mirror);
        Assert.Equal(LayoutRotation.R270, rot);
        Assert.Equal(0.0, delta, 9);
    }

    [Fact]
    public void FromGdsii_ReflectTrueAngle0_MapsToMirrorTrueRot180()
    {
        var (mirror, rot) = GdsiiTransformCodec.FromGdsii(true, 0.0, out _);
        Assert.True(mirror);
        Assert.Equal(LayoutRotation.R180, rot);
    }

    [Fact]
    public void FromGdsii_NoReflect_MapsAngleDirectly()
    {
        var (mirror, rot) = GdsiiTransformCodec.FromGdsii(false, 90.0, out var delta);
        Assert.False(mirror);
        Assert.Equal(LayoutRotation.R90, rot);
        Assert.Equal(0.0, delta, 9);
    }

    [Fact]
    public void FromGdsii_ArbitraryAngle_SnapsToNearestQuadrant_ReportsDelta()
    {
        var (_, rot) = GdsiiTransformCodec.FromGdsii(false, 33.0, out var delta);
        Assert.Equal(LayoutRotation.R0, rot);
        Assert.Equal(33.0, delta, 9);
    }

    [Fact]
    public void FromGdsii_ArbitraryAngleNear270_SnapsCorrectly()
    {
        var (_, rot) = GdsiiTransformCodec.FromGdsii(false, 265.0, out var delta);
        Assert.Equal(LayoutRotation.R270, rot);
        Assert.Equal(5.0, delta, 9);
    }
}
