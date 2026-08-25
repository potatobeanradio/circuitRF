using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// brief-L3d-arbitrary-angle-instances.md gates 2, 5, 6, 7 and 10 — the parts provable without a
/// renderer or a cell folder on disk: the cardinal-identity proof, the transform's own inverse,
/// composition, shape promotion, and the single-accessor rule.
/// </summary>
public class L3dArbitraryAngleTests
{
    private static LayoutInstance At(double deg, bool mirror = false, double mag = 1.0) =>
        new() { CellRef = "x", X = 0, Y = 0, RotationDegrees = deg, MirrorX = mirror, Mag = mag };

    // ── Gate 2: cardinal identity, against WRITTEN-OUT values, never against the old code ────────

    /// <summary>
    /// The one test that proves every existing design is untouched. The expected values are the
    /// pre-L3d rotation table transcribed by hand — <c>R90 => (-my, mx)</c> and so on — NOT captured
    /// from the implementation, so a generalization that quietly changed a cardinal result would fail
    /// here rather than agreeing with itself.
    /// </summary>
    [Theory]
    // deg,  lx,  ly,  expected x, expected y     (Mag 1, unmirrored: mx = lx, my = ly)
    [InlineData(0.0, 300, 100, 300, 100)]     // R0:   ( mx,  my)
    [InlineData(90.0, 300, 100, -100, 300)]    // R90:  (-my,  mx)
    [InlineData(180.0, 300, 100, -300, -100)]   // R180: (-mx, -my)
    [InlineData(270.0, 300, 100, 100, -300)]   // R270: ( my, -mx)
    public void TransformPoint_AtEveryCardinal_MatchesThePreL3dTableExactly(
        double deg, long lx, long ly, long expectedX, long expectedY)
    {
        var (x, y) = LayoutInstanceTransform.TransformPoint(lx, ly, At(deg), 0, 0);
        Assert.Equal(expectedX, x);
        Assert.Equal(expectedY, y);
    }

    /// <summary>Same proof for the renderer's matrix. Pre-L3d these four were literal tuples in a
    /// switch; they are now <c>(sx·c, sy·s, -sx·s, sy·c)</c> and must still land on them exactly —
    /// which is only true because <see cref="LayoutAngle.CosSin"/> returns exact literals at the
    /// cardinals rather than <c>Math.Cos(PI/2)</c>'s 6.1e-17.</summary>
    [Theory]
    [InlineData(0.0, 1.0, 0.0, 0.0, 1.0)]     // (sx, 0, 0, sy)
    [InlineData(90.0, 0.0, 1.0, -1.0, 0.0)]    // (0, sy, -sx, 0)
    [InlineData(180.0, -1.0, 0.0, 0.0, -1.0)]   // (-sx, 0, 0, -sy)
    [InlineData(270.0, 0.0, -1.0, 1.0, 0.0)]    // (0, -sy, sx, 0)
    public void PathSpaceCoefficients_AtEveryCardinal_MatchThePreL3dTableExactly(
        double deg, double a, double b, double c, double d)
    {
        var (ga, gb, gc, gd) = LayoutInstanceTransform.PathSpaceLinearCoefficients(At(deg));
        Assert.Equal(a, ga);
        Assert.Equal(b, gb);
        Assert.Equal(c, gc);
        Assert.Equal(d, gd);
    }

    /// <summary>Math.Cos(PI/2) is 6.123e-17. If CosSin ever stops special-casing the cardinals, this
    /// is the test that says so — and gate 2 above is what it would otherwise silently break.</summary>
    [Fact]
    public void CosSin_AtCardinals_IsExactlyZeroAndOne_NotMerelyClose()
    {
        Assert.Equal((1.0, 0.0), LayoutAngle.CosSin(0));
        Assert.Equal((0.0, 1.0), LayoutAngle.CosSin(90));
        Assert.Equal((-1.0, 0.0), LayoutAngle.CosSin(180));
        Assert.Equal((0.0, -1.0), LayoutAngle.CosSin(270));
        Assert.Equal((0.0, 1.0), LayoutAngle.CosSin(450));   // normalizes first
        Assert.Equal((0.0, -1.0), LayoutAngle.CosSin(-90));
    }

    // ── Gate 5: the inverse is an inverse, at angles that are not representable in DBU ───────────

    [Theory]
    [InlineData(30.0, false, 1.0)]
    [InlineData(45.0, false, 1.0)]
    [InlineData(137.5, false, 1.0)]
    [InlineData(30.0, true, 1.0)]
    [InlineData(137.5, true, 2.0)]
    [InlineData(45.0, false, 2.0)]
    public void InverseTransformPoint_UndoesTransformPoint_WithinOneDbu(double deg, bool mirror, double mag)
    {
        var inst = At(deg, mirror, mag);
        foreach (var (lx, ly) in new[] { (0L, 0L), (300L, 100L), (-1_234L, 5_678L), (999_999L, -1L) })
        {
            var (wx, wy) = LayoutInstanceTransform.TransformPoint(lx, ly, inst, 0, 0);
            var (bx, by) = LayoutInstanceTransform.InverseTransformPoint(wx, wy, inst, 0, 0);
            Assert.True(Math.Abs(bx - lx) <= 1.0, $"x {bx} vs {lx} at {deg}°");
            Assert.True(Math.Abs(by - ly) <= 1.0, $"y {by} vs {ly} at {deg}°");
        }
    }

    // ── Gate 6: composition ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void ComposeInstances_AddsAngles_AndSubtractsWhenTheOuterIsMirrored()
    {
        var inner = At(20.0);
        Assert.Equal(50.0, LayoutInstanceTransform.ComposeInstances(At(30.0), 0, 0, inner).RotationDegrees, 9);
        Assert.Equal(10.0, LayoutInstanceTransform.ComposeInstances(At(30.0, mirror: true), 0, 0, inner).RotationDegrees, 9);
    }

    /// <summary>R-L3d-3: composition composes ANGLES and rounds coordinates once. Composing a chain
    /// and transforming a point must equal transforming that point through each level in turn — if a
    /// level rounded on its own, a deep hierarchy would drift.</summary>
    [Fact]
    public void ComposeInstances_ThenTransform_MatchesTransformingThroughEachLevel()
    {
        var outer = new LayoutInstance { CellRef = "o", X = 7_000, Y = -3_000, RotationDegrees = 37.5, Mag = 1.0 };
        var inner = new LayoutInstance { CellRef = "i", X = 1_500, Y = 800, RotationDegrees = 12.25, Mag = 1.0 };

        var composed = LayoutInstanceTransform.ComposeInstances(outer, 0, 0, inner);
        var (cx, cy) = LayoutInstanceTransform.TransformPoint(400, -250, composed, 0, 0);

        var (ix, iy) = LayoutInstanceTransform.TransformPoint(400, -250, inner, 0, 0);
        var (ox, oy) = LayoutInstanceTransform.TransformPoint(ix, iy, outer, 0, 0);

        Assert.True(Math.Abs(cx - ox) <= 1, $"{cx} vs {ox}");
        Assert.True(Math.Abs(cy - oy) <= 1, $"{cy} vs {oy}");
    }

    [Fact]
    public void ComposeInstances_OfTwoCardinals_StaysCardinalAndWritesNoAngleField()
    {
        var composed = LayoutInstanceTransform.ComposeInstances(At(270.0), 0, 0, At(180.0));
        Assert.Equal(90.0, composed.RotationDegrees, 9);
        Assert.Equal(LayoutRotation.R90, composed.Rot);
        Assert.Null(composed.RotDeg);
    }

    // ── R-L3d-4/5: the accessor keeps the two serialized fields consistent ───────────────────────

    [Theory]
    [InlineData(0.0, LayoutRotation.R0)]
    [InlineData(90.0, LayoutRotation.R90)]
    [InlineData(180.0, LayoutRotation.R180)]
    [InlineData(270.0, LayoutRotation.R270)]
    [InlineData(360.0, LayoutRotation.R0)]
    [InlineData(-90.0, LayoutRotation.R270)]
    public void RotationDegrees_AtACardinal_WritesTheEnumAndLeavesTheAngleFieldNull(double set, LayoutRotation expected)
    {
        var inst = At(set);
        Assert.Equal(expected, inst.Rot);
        Assert.Null(inst.RotDeg);
    }

    [Fact]
    public void RotationDegrees_OffCardinal_KeepsTheAngleAndDegradesTheEnumToTheNearest()
    {
        var inst = At(100.0);
        Assert.Equal(100.0, inst.RotationDegrees, 9);
        Assert.Equal(100.0, inst.RotDeg!.Value, 9);
        Assert.Equal(LayoutRotation.R90, inst.Rot);
    }

    [Fact]
    public void RotationDegrees_NormalizesAndRefusesNonFinite()
    {
        Assert.Equal(30.0, At(390.0).RotationDegrees, 9);
        Assert.Equal(330.0, At(-30.0).RotationDegrees, 9);
        Assert.Equal(0.0, At(double.NaN).RotationDegrees);
        Assert.Equal(0.0, At(double.PositiveInfinity).RotationDegrees);
    }

    [Fact]
    public void Clone_CarriesTheAngle_NotJustTheEnum()
    {
        var clone = LayoutGeometry.Clone(At(137.5, mirror: true, mag: 2.0));
        Assert.Equal(137.5, clone.RotationDegrees, 9);
        Assert.True(clone.MirrorX);
        Assert.Equal(2.0, clone.Mag, 9);
    }

    // ── Gate 7: promotion ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The bug this whole promotion exists to prevent: mapping a rect's two corners through 45° and
    /// re-normalizing yields its axis-aligned BOUNDING BOX — four corners, right layer, plausible
    /// picture, wrong copper. A promoted rect keeps its area (a rotation is area-preserving); a
    /// bounding box would be twice it.
    /// </summary>
    [Fact]
    public void RectPromotedAndRotated45_KeepsItsAreaAndItsFourDistinctCorners()
    {
        var rect = new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 4_000, Y2 = 2_000 };
        var inst = At(45.0);

        var promoted = Assert.IsType<PolygonShape>(LayoutRotationPromotion.Promote(LayoutGeometry.Clone(rect)));
        LayoutCoordinateWalk.Transform(promoted, new LayoutCoordinateTransform(
            (x, y) => LayoutInstanceTransform.TransformPoint(x, y, inst, 0, 0), m => m, RotatesAxes: true));

        Assert.Equal(4, promoted.Xy.Length / 2);
        Assert.Equal(4, DistinctPoints(promoted.Xy));

        double area = ShoelaceArea(promoted.Xy);
        const double exact = 4_000.0 * 2_000.0;
        Assert.True(Math.Abs(area - exact) < 1_000, $"area {area} vs {exact}");   // preserved, to DBU rounding

        // …and is NOT the bounding box's area, which is what the un-promoted walk would have produced:
        // a 4000x2000 rect at 45 degrees has a bbox of 3x the rect's own area.
        Assert.True(BboxArea(promoted.Xy) > area * 1.5, "promoted shape collapsed to its bounding box");
    }

    [Fact]
    public void RoundedRectPromotes_ToACurveOfFourLinesAndFourQuarterArcs()
    {
        var rr = new RoundedRectShape
        {
            Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 4_000, Y2 = 2_000,
            CornerRadius = 500, FlattenTolDbu = 25,
        };
        var curve = Assert.IsType<CurveShape>(LayoutRotationPromotion.Promote(rr));

        Assert.Equal(8, curve.Xy.Length / 2);
        Assert.Equal(4, curve.Edges!.Count(e => e.Kind == EdgeKind.Line));
        Assert.Equal(4, curve.Edges!.Count(e => e.Kind == EdgeKind.Arc));
        Assert.All(curve.Edges!.Where(e => e.Kind == EdgeKind.Arc),
                   e => Assert.Equal(Math.Tan(Math.PI / 8), e.Bulge, 9));   // tan(90°/4), counter-clockwise
        Assert.Equal(25, curve.FlattenTolDbu);
    }

    [Fact]
    public void RoundedRectWithZeroRadius_PromotesToAPlainPolygon()
    {
        var rr = new RoundedRectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 100, Y2 = 100, CornerRadius = 0 };
        Assert.IsType<PolygonShape>(LayoutRotationPromotion.Promote(rr));
    }

    [Fact]
    public void ShapesThatSurviveARotationUnchanged_AreReturnedAsTheSameInstance()
    {
        foreach (LayoutShape shape in new LayoutShape[]
                 {
                     new CircleShape { Cx = 1, Cy = 2, R = 3 },
                     new ViaShape { X = 1, Y = 2, PadSize = 40, DrillSize = 20 },
                     new PolygonShape { Xy = [0, 0, 10, 0, 10, 10] },
                     new PathShape { Xy = [0, 0, 10, 10], Width = 5 },
                 })
            Assert.Same(shape, LayoutRotationPromotion.Promote(shape));
    }

    /// <summary>R-L3d-7: the walk REFUSES rather than emitting a bounding box. This is what makes the
    /// wrong output unrepresentable instead of merely avoided by convention at the call site.</summary>
    [Theory]
    [InlineData(typeof(RectShape))]
    [InlineData(typeof(RoundedRectShape))]
    [InlineData(typeof(BitmapShape))]
    public void Walk_UnderARotatingTransform_RefusesAnAxisAlignedShape(Type shapeType)
    {
        LayoutShape shape = shapeType == typeof(RectShape) ? new RectShape { X2 = 10, Y2 = 10 }
            : shapeType == typeof(RoundedRectShape) ? new RoundedRectShape { X2 = 10, Y2 = 10, CornerRadius = 2 }
            : new BitmapShape { X = 0, Y = 0, W = 10, H = 10 };

        var rotating = new LayoutCoordinateTransform((x, y) => (y, x), m => m, RotatesAxes: true);
        var ex = Assert.Throws<InvalidOperationException>(() => LayoutCoordinateWalk.Transform(shape, rotating));
        Assert.Contains("LayoutRotationPromotion", ex.Message);
    }

    /// <summary>…and every axis-preserving caller is untouched by that guard: DBU resolution change,
    /// paste rescale and Scale all construct their transform without it.</summary>
    [Fact]
    public void Walk_UnderAnAxisPreservingTransform_StillWalksARectangle()
    {
        var rect = new RectShape { X1 = 1, Y1 = 2, X2 = 3, Y2 = 4 };
        LayoutCoordinateWalk.Transform(rect, LayoutCoordinateTransform.Uniform(v => v * 10));
        Assert.Equal(10, rect.X1);
        Assert.Equal(40, rect.Y2);
        Assert.False(LayoutCoordinateTransform.Uniform(v => v).RotatesAxes);
        Assert.False(LayoutCoordinateTransform.AxisIndependent(v => v, v => v, v => v).RotatesAxes);
    }

    private static int DistinctPoints(long[] xy)
    {
        var seen = new HashSet<(long, long)>();
        for (int i = 0; i + 1 < xy.Length; i += 2) seen.Add((xy[i], xy[i + 1]));
        return seen.Count;
    }

    private static double ShoelaceArea(long[] xy)
    {
        double sum = 0;
        int n = xy.Length / 2;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            sum += (double)xy[2 * i] * xy[2 * j + 1] - (double)xy[2 * j] * xy[2 * i + 1];
        }
        return Math.Abs(sum) / 2.0;
    }

    private static double BboxArea(long[] xy)
    {
        long minX = long.MaxValue, maxX = long.MinValue, minY = long.MaxValue, maxY = long.MinValue;
        for (int i = 0; i + 1 < xy.Length; i += 2)
        {
            minX = Math.Min(minX, xy[i]); maxX = Math.Max(maxX, xy[i]);
            minY = Math.Min(minY, xy[i + 1]); maxY = Math.Max(maxY, xy[i + 1]);
        }
        return (double)(maxX - minX) * (maxY - minY);
    }
}
