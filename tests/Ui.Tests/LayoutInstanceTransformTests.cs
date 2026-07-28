using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  Phase L3a — LayoutInstanceTransform: the ONE canonical (mirror + rotate + scale + translate)
//  definition every consumer (bbox math, hit-test, the renderer's matrix) derives from.
// ──────────────────────────────────────────────────────────────────────────────

public sealed class LayoutInstanceTransformTests
{
    [Theory]
    [InlineData(LayoutRotation.R0,   false, 100, 200,  100,  200)]
    [InlineData(LayoutRotation.R90,  false, 100, 200, -200,  100)]
    [InlineData(LayoutRotation.R180, false, 100, 200, -100, -200)]
    [InlineData(LayoutRotation.R270, false, 100, 200,  200, -100)]
    [InlineData(LayoutRotation.R0,   true,  100, 200, -100,  200)]
    [InlineData(LayoutRotation.R90,  true,  100, 200, -200, -100)]
    [InlineData(LayoutRotation.R180, true,  100, 200,  100, -200)]
    [InlineData(LayoutRotation.R270, true,  100, 200,  200,  100)]
    public void TransformPoint_AllEightRotationMirrorCombos_MatchHandDerivedExpectation(
        LayoutRotation rot, bool mirror, long lx, long ly, long expectedDx, long expectedDy)
    {
        var inst = new LayoutInstance { CellRef = "x", X = 1000, Y = 2000, Rot = rot, MirrorX = mirror, Mag = 1.0 };
        var (x, y) = LayoutInstanceTransform.TransformPoint(lx, ly, inst, 0, 0);
        Assert.Equal(1000 + expectedDx, x);
        Assert.Equal(2000 + expectedDy, y);
    }

    [Fact]
    public void TransformPoint_Magnification_ScalesLocalCoordinates()
    {
        var inst = new LayoutInstance { CellRef = "x", X = 0, Y = 0, Rot = LayoutRotation.R0, Mag = 2.5 };
        var (x, y) = LayoutInstanceTransform.TransformPoint(100, 200, inst, 0, 0);
        Assert.Equal(250, x);
        Assert.Equal(500, y);
    }

    [Theory]
    [InlineData(LayoutRotation.R0, false)]
    [InlineData(LayoutRotation.R90, false)]
    [InlineData(LayoutRotation.R180, false)]
    [InlineData(LayoutRotation.R270, false)]
    [InlineData(LayoutRotation.R0, true)]
    [InlineData(LayoutRotation.R90, true)]
    [InlineData(LayoutRotation.R180, true)]
    [InlineData(LayoutRotation.R270, true)]
    public void InverseTransformPoint_RoundTripsTransformPoint(LayoutRotation rot, bool mirror)
    {
        // Mag = 1.0 deliberately: TransformPoint rounds its output to the nearest DBU (integer
        // storage, §1.1 R1), so a non-unity magnification makes the forward step itself lossy —
        // this test isolates the rotation/mirror/translate algebra exactly; PathSpaceLinearCoefficients_
        // AgreesWithDbuSpaceTransformThroughPathSpace below covers magnification with an appropriate
        // float tolerance instead of asserting bit-exactness through a rounding step.
        var inst = new LayoutInstance { CellRef = "x", X = 5_000, Y = -3_000, Rot = rot, MirrorX = mirror, Mag = 1.0 };
        var (px, py) = LayoutInstanceTransform.TransformPoint(1234, -567, inst, 2, 3);
        var (lx, ly) = LayoutInstanceTransform.InverseTransformPoint(px, py, inst, 2, 3);
        Assert.Equal(1234, lx, 6);
        Assert.Equal(-567, ly, 6);
    }

    [Fact]
    public void ArrayCellOrigin_UnrotatedGridInParentFrame()
    {
        var inst = new LayoutInstance { CellRef = "x", X = 100, Y = 200, PitchX = 50, PitchY = 30, Rows = 4, Cols = 5 };
        Assert.Equal((100, 200), LayoutInstanceTransform.ArrayCellOrigin(inst, 0, 0));
        Assert.Equal((100 + 4 * 50, 200), LayoutInstanceTransform.ArrayCellOrigin(inst, 0, 4));
        Assert.Equal((100, 200 + 3 * 30), LayoutInstanceTransform.ArrayCellOrigin(inst, 3, 0));
    }

    /// <summary>The critical cross-check for R-L3a-3's renderer matrix (docs/sonnet-briefs/
    /// brief-L3a-instances-and-arrays.md §4): <see cref="LayoutInstanceTransform.
    /// PathSpaceLinearCoefficients"/>'s derivation (composing the canonical DBU-space transform with
    /// path space's Y-negation on both ends) must produce EXACTLY the same on-screen point as
    /// computing the transform in DBU space first and THEN converting to path space — for all 8
    /// rotation/mirror combinations and a non-trivial magnification. This is what gate 2's pixel-
    /// identity requirement actually rests on.</summary>
    [Theory]
    [InlineData(LayoutRotation.R0, false)]
    [InlineData(LayoutRotation.R90, false)]
    [InlineData(LayoutRotation.R180, false)]
    [InlineData(LayoutRotation.R270, false)]
    [InlineData(LayoutRotation.R0, true)]
    [InlineData(LayoutRotation.R90, true)]
    [InlineData(LayoutRotation.R180, true)]
    [InlineData(LayoutRotation.R270, true)]
    public void PathSpaceLinearCoefficients_AgreesWithDbuSpaceTransformThroughPathSpace(LayoutRotation rot, bool mirror)
    {
        var inst = new LayoutInstance { CellRef = "x", X = 7_000, Y = -2_500, Rot = rot, MirrorX = mirror, Mag = 1.8 };
        const long lx = 3_300, ly = -1_100;

        // Path A: transform in DBU space (the ground truth — every other consumer, incl.
        // CellHierarchy's bbox math and hit-test's inverse, uses this directly), then convert to
        // parent path space.
        var (wx, wy) = LayoutInstanceTransform.TransformPoint(lx, ly, inst, 0, 0);
        var parentPs = new LayoutRenderer.PathSpace(0, 0, 0.001);
        float expectedPx = parentPs.X(wx), expectedPy = parentPs.Y(wy);

        // Path B: what the renderer actually does — convert the LOCAL point to (sub-cell) path space
        // first, apply the matrix coefficients, then translate by the parent path-space position of
        // the instance's own origin.
        var localPs = new LayoutRenderer.PathSpace(0, 0, 0.001); // same DBU/micron in this test — see note below
        float lpx = localPs.X(lx), lpy = localPs.Y(ly);
        var (a, b, c, d) = LayoutInstanceTransform.PathSpaceLinearCoefficients(inst);
        float tx = parentPs.X(inst.X), ty = parentPs.Y(inst.Y);
        float actualPx = (float)(a * lpx + b * lpy) + tx;
        float actualPy = (float)(c * lpx + d * lpy) + ty;

        Assert.Equal(expectedPx, actualPx, 3);
        Assert.Equal(expectedPy, actualPy, 3);
    }
}
