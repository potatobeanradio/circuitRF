using CircuitRF.Ui.Layout;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  Phase L3c (brief-L3c-flatten-and-group.md §2) — LayoutInstanceTransform.ComposeInstances: composing
//  an outer instance's transform with one of its sub-cell's own (inner) instances must produce a
//  SINGLE equivalent LayoutInstance whose own TransformPoint reproduces exactly
//  outer.TransformPoint(inner.TransformPoint(local)) for every local point — this is what makes
//  Flatten Hierarchy pixel-identical when the sub-cell being flattened itself contains an instance.
// ──────────────────────────────────────────────────────────────────────────────

public sealed class LayoutInstanceTransformComposeTests
{
    private static readonly LayoutRotation[] AllRotations =
        [LayoutRotation.R0, LayoutRotation.R90, LayoutRotation.R180, LayoutRotation.R270];

    public static IEnumerable<object[]> AllCombos()
    {
        foreach (var rOuter in AllRotations)
        foreach (var mOuter in new[] { false, true })
        foreach (var rInner in AllRotations)
        foreach (var mInner in new[] { false, true })
        foreach (var magOuter in new[] { 1.0, 2.0 })
        foreach (var magInner in new[] { 1.0, 3.0 })
            yield return [rOuter, mOuter, rInner, mInner, magOuter, magInner];
    }

    [Theory]
    [MemberData(nameof(AllCombos))]
    public void ComposeInstances_MatchesNestedTransformPoint_ForEveryLocalPoint(
        LayoutRotation rOuter, bool mOuter, LayoutRotation rInner, bool mInner, double magOuter, double magInner)
    {
        var outer = new LayoutInstance { CellRef = "Outer", X = 1000, Y = -500, Rot = rOuter, MirrorX = mOuter, Mag = magOuter };
        var inner = new LayoutInstance { CellRef = "Inner", X = 300, Y = 700, Rot = rInner, MirrorX = mInner, Mag = magInner };

        var composed = LayoutInstanceTransform.ComposeInstances(outer, row: 0, col: 0, inner);

        // Several representative local points, including off-axis ones (rotation/mirror bugs often
        // only show up when both coordinates are non-zero and unequal).
        (long X, long Y)[] localPoints = [(0, 0), (100, 0), (0, 100), (137, -241), (-59, 83)];

        foreach (var (lx, ly) in localPoints)
        {
            var (sx, sy) = LayoutInstanceTransform.TransformPoint(lx, ly, inner, 0, 0);
            var expected = LayoutInstanceTransform.TransformPoint(sx, sy, outer, 0, 0);
            var actual = LayoutInstanceTransform.TransformPoint(lx, ly, composed, 0, 0);

            // Integer DBU rounding on each of the two separate transform steps vs. the one combined
            // step can legitimately differ by a rounding ULP — assert within 1 DBU, not bit-exact.
            Assert.True(Math.Abs(expected.X - actual.X) <= 1,
                $"X mismatch: expected {expected.X}, got {actual.X} (outer R{rOuter}/M{mOuter}/x{magOuter}, inner R{rInner}/M{mInner}/x{magInner}, local ({lx},{ly}))");
            Assert.True(Math.Abs(expected.Y - actual.Y) <= 1,
                $"Y mismatch: expected {expected.Y}, got {actual.Y} (outer R{rOuter}/M{mOuter}/x{magOuter}, inner R{rInner}/M{mInner}/x{magInner}, local ({lx},{ly}))");
        }
    }

    [Fact]
    public void ComposeInstances_MirrorXor_MagProduct()
    {
        var outer = new LayoutInstance { CellRef = "O", Rot = LayoutRotation.R0, MirrorX = true, Mag = 2.0 };
        var inner = new LayoutInstance { CellRef = "I", Rot = LayoutRotation.R0, MirrorX = true, Mag = 3.0 };
        var composed = LayoutInstanceTransform.ComposeInstances(outer, 0, 0, inner);

        Assert.False(composed.MirrorX);   // true XOR true = false
        Assert.Equal(6.0, composed.Mag, precision: 9);
    }

    [Fact]
    public void ComposeInstances_PreservesInnerArrayShape_ScalesPitchByOuterMagOnly()
    {
        var outer = new LayoutInstance { CellRef = "O", Rot = LayoutRotation.R90, Mag = 2.0 };
        var inner = new LayoutInstance { CellRef = "I", Rows = 5, Cols = 3, PitchX = 1000, PitchY = 2000 };
        var composed = LayoutInstanceTransform.ComposeInstances(outer, 0, 0, inner);

        Assert.Equal(5, composed.Rows);
        Assert.Equal(3, composed.Cols);
        Assert.Equal(2000, composed.PitchX);   // 1000 * outer.Mag(2.0), NOT rotated
        Assert.Equal(4000, composed.PitchY);
    }

    [Fact]
    public void ComposeInstances_DoesNotSetCellRef_CallerMustRebase()
    {
        var outer = new LayoutInstance { CellRef = "Outer" };
        var inner = new LayoutInstance { CellRef = "../Sibling" };
        var composed = LayoutInstanceTransform.ComposeInstances(outer, 0, 0, inner);
        Assert.Equal("../Sibling", composed.CellRef);   // verbatim — caller rebases relative to the new parent
    }
}
