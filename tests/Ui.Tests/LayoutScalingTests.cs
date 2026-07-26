using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

public class LayoutScalingTests
{
    private static LayoutView BuildMultiplesOfTenFixture()
    {
        var view = new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 };
        view.Shapes.Add(new RectShape { X1 = 0, Y1 = 0, X2 = 1000, Y2 = 500 });
        view.Shapes.Add(new PolygonShape { Xy = [0, 0, 100, 0, 50, 80] });
        view.Shapes.Add(new PathShape
        {
            Xy = [0, 0, 1000, 0],
            Width = 200,
            Edges = [new LayoutEdge { Kind = EdgeKind.Cubic, C1X = 10, C1Y = 20, C2X = 30, C2Y = 40 }],
        });
        view.Instances.Add(new LayoutInstance { X = 100, Y = 200, PitchX = 50, PitchY = 60 });
        return view;
    }

    // ── Gate 8 ──────────────────────────────────────────────────────────────

    [Fact]
    public void Refine_Times10_IsLosslessAndReversible()
    {
        var original = BuildMultiplesOfTenFixture();
        var originalJson = LayoutPersistence.Serialize(original);

        var view = LayoutPersistence.Deserialize(originalJson);
        Assert.True(LayoutScaling.TryChangeResolution(view, view.DbuPerMicron * 10, out var offenders));
        Assert.Empty(offenders);
        Assert.Equal(10_000, view.DbuPerMicron);

        Assert.True(LayoutScaling.TryChangeResolution(view, view.DbuPerMicron / 10, out var offenders2));
        Assert.Empty(offenders2);

        Assert.Equal(originalJson, LayoutPersistence.Serialize(view));
    }

    [Fact]
    public void Coarsen_IndivisibleCoordinate_FailsNamesOffender_LeavesUnmutated()
    {
        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Shapes.Add(new RectShape { X1 = 0, Y1 = 0, X2 = 1_234_567, Y2 = 100 });
        var before = LayoutPersistence.Serialize(view);

        bool ok = LayoutScaling.TryChangeResolution(view, 100, out var offenders); // ratio 10

        Assert.False(ok);
        Assert.NotEmpty(offenders);
        Assert.Contains(offenders, s => s.Contains("RectShape") && s.Contains("1234567"));
        Assert.Equal(before, LayoutPersistence.Serialize(view));
    }

    [Fact]
    public void Coarsen_AllDivisible_Succeeds()
    {
        var view = BuildMultiplesOfTenFixture();
        Assert.True(LayoutScaling.TryChangeResolution(view, view.DbuPerMicron / 10, out var offenders));
        Assert.Empty(offenders);
        Assert.Equal(100, view.DbuPerMicron);
    }

    [Fact]
    public void NonIntegerRatio_Rejected()
    {
        var view = new LayoutView { DbuPerMicron = 1000 };
        Assert.False(LayoutScaling.TryChangeResolution(view, 333, out var offenders));
        Assert.NotEmpty(offenders);
    }

    [Fact]
    public void SameResolution_IsNoOpSuccess()
    {
        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Shapes.Add(new RectShape { X1 = 1, Y1 = 2, X2 = 3, Y2 = 4 });
        var before = LayoutPersistence.Serialize(view);

        Assert.True(LayoutScaling.TryChangeResolution(view, 1000, out var offenders));
        Assert.Empty(offenders);
        Assert.Equal(before, LayoutPersistence.Serialize(view));
    }

    [Fact]
    public void Refine_ScalesCubicControlPoints_ButNeverScalesBulge()
    {
        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Shapes.Add(new CurveShape
        {
            Xy = [0, 0, 100, 0, 100, 100],
            Edges =
            [
                new LayoutEdge { Kind = EdgeKind.Cubic, C1X = 10, C1Y = 20, C2X = 30, C2Y = 40 },
                new LayoutEdge { Kind = EdgeKind.Arc, Bulge = 0.5 },
                new LayoutEdge { Kind = EdgeKind.Line },
            ],
        });

        Assert.True(LayoutScaling.TryChangeResolution(view, 2000, out _));

        var curve = (CurveShape)view.Shapes[0];
        Assert.Equal(20, curve.Edges![0].C1X);
        Assert.Equal(40, curve.Edges[0].C1Y);
        Assert.Equal(60, curve.Edges[0].C2X);
        Assert.Equal(80, curve.Edges[0].C2Y);
        Assert.Equal(0.5, curve.Edges[1].Bulge);
    }

    [Fact]
    public void Refine_ScalesSnapDbuAndInstancePitch()
    {
        var view = BuildMultiplesOfTenFixture();
        Assert.True(LayoutScaling.TryChangeResolution(view, 10_000, out _));

        Assert.Equal(10_000, view.SnapDbu);
        var inst = view.Instances[0];
        Assert.Equal(1000, inst.X);
        Assert.Equal(2000, inst.Y);
        Assert.Equal(500, inst.PitchX);
        Assert.Equal(600, inst.PitchY);
    }
}
