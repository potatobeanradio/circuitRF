using System.Collections.Generic;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

// ── Phase L1d gate 3: R-L1d-2 hit priority (CubicControl > Bulge > Vertex/Radius/CornerRadius > EdgeMidpoint) ──

public class LayoutHandleHitTestTests
{
    [Fact]
    public void OverlappingHandles_CubicControlBeatsBulgeBeatsVertexBeatsEdgeMidpoint()
    {
        var handles = new List<LayoutHandle>
        {
            new(LayoutHandleKind.EdgeMidpoint, 100, 100, 0),
            new(LayoutHandleKind.Vertex,       101, 100, 1),
            new(LayoutHandleKind.Bulge,        100, 101, 2),
            new(LayoutHandleKind.CubicControl, 101, 101, 3, 0),
        };

        var hit = LayoutHandleHitTest.HitTest(handles, 100, 100, tolDbu: 10);

        Assert.NotNull(hit);
        Assert.Equal(LayoutHandleKind.CubicControl, hit!.Value.Kind);
    }

    [Fact]
    public void WithoutCubicControl_BulgeBeatsVertex()
    {
        var handles = new List<LayoutHandle>
        {
            new(LayoutHandleKind.EdgeMidpoint, 100, 100, 0),
            new(LayoutHandleKind.Vertex,       101, 100, 1),
            new(LayoutHandleKind.Bulge,        100, 101, 2),
        };

        var hit = LayoutHandleHitTest.HitTest(handles, 100, 100, tolDbu: 10);

        Assert.Equal(LayoutHandleKind.Bulge, hit!.Value.Kind);
    }

    [Fact]
    public void WithoutBulgeOrCubicControl_VertexBeatsEdgeMidpoint()
    {
        var handles = new List<LayoutHandle>
        {
            new(LayoutHandleKind.EdgeMidpoint, 100, 100, 0),
            new(LayoutHandleKind.Vertex,       101, 100, 1),
        };

        var hit = LayoutHandleHitTest.HitTest(handles, 100, 100, tolDbu: 10);

        Assert.Equal(LayoutHandleKind.Vertex, hit!.Value.Kind);
    }

    [Fact]
    public void RadiusAndCornerRadius_ShareVertexPriorityTier()
    {
        var radiusHandles = new List<LayoutHandle> { new(LayoutHandleKind.Radius, 100, 100, 0) };
        var cornerHandles = new List<LayoutHandle> { new(LayoutHandleKind.CornerRadius, 100, 100, 0) };

        Assert.Equal(LayoutHandleKind.Radius, LayoutHandleHitTest.HitTest(radiusHandles, 100, 100, 10)!.Value.Kind);
        Assert.Equal(LayoutHandleKind.CornerRadius, LayoutHandleHitTest.HitTest(cornerHandles, 100, 100, 10)!.Value.Kind);
    }

    [Fact]
    public void NearestWithinTier_WinsOverFartherSamePriorityHandle()
    {
        var handles = new List<LayoutHandle>
        {
            new(LayoutHandleKind.Vertex, 105, 100, 0),
            new(LayoutHandleKind.Vertex, 101, 100, 1),
        };

        var hit = LayoutHandleHitTest.HitTest(handles, 100, 100, tolDbu: 10);

        Assert.Equal(1, hit!.Value.Index);
    }

    [Fact]
    public void OutsideTolerance_ReturnsNull()
    {
        var handles = new List<LayoutHandle> { new(LayoutHandleKind.Vertex, 1000, 1000, 0) };

        Assert.Null(LayoutHandleHitTest.HitTest(handles, 0, 0, tolDbu: 10));
    }

    [Fact]
    public void EmptyHandleList_ReturnsNull()
    {
        Assert.Null(LayoutHandleHitTest.HitTest(new List<LayoutHandle>(), 0, 0, tolDbu: 10));
    }
}
