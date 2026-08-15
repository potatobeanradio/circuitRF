// ================================================================
//  ContourIsoLabelPlacementTests.cs — R8A §4's own gate
//
//  ContourRenderer.ComputeLabelAnchors is the pure-arithmetic half of the extracted label placer
//  (R8A §4.1) — the world-unit arc walk, separable from Skia, so it is tested directly rather than
//  through a rendered bitmap.
// ================================================================

using System;
using System.Collections.Generic;
using CircuitRF.Ui.DataDisplay;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests;

public sealed class ContourIsoLabelPlacementTests(ITestOutputHelper output)
{
    /// <summary>A full circular ring of world radius <paramref name="r"/>, sampled finely enough
    /// (100,000 segments) that even the WORST-case chord deviation — a segment midpoint — sits under
    /// 1e-9 of the true radius: deviation ≈ r·φ²/8 for a subtended angle φ = 2π/n, ≈ 2.96e-10 here.
    /// Closed back to the start so the walked arc length is exactly 2πr.</summary>
    private static List<(double X, double Y)> Ring(double r, int n = 100_000)
    {
        var pts = new List<(double X, double Y)>(n + 1);
        for (int i = 0; i <= n; i++)
        {
            double theta = 2.0 * Math.PI * i / n;
            pts.Add((r * Math.Cos(theta), r * Math.Sin(theta)));
        }
        return pts;
    }

    [Fact]
    public void Spacing035_OnA06RadiusRing_Yields8OrMoreAnchors_AllOnTheRing()
    {
        const double radius = 0.6;   // arc = 2π·0.6 ≈ 3.77 — the Γ-plane rim-scale ring §4.2 describes
        var pts = Ring(radius);

        var anchors = ContourRenderer.ComputeLabelAnchors(pts, spacingWorld: 0.35, ringIndex: 0);

        output.WriteLine($"{anchors.Count} anchors at spacing 0.35 on a ring of arc " +
                         $"{2 * Math.PI * radius:F4}");
        Assert.True(anchors.Count >= 8, $"expected >= 8 label anchors, got {anchors.Count}");

        foreach (var (x, y) in anchors)
        {
            double dist = Math.Sqrt(x * x + y * y);
            Assert.True(Math.Abs(dist - radius) < 1e-9,
                $"anchor ({x:G6},{y:G6}) at distance {dist:G12} deviates from the ring radius " +
                $"{radius} by {Math.Abs(dist - radius):E3}");
        }
    }

    [Fact]
    public void Spacing30_WiderThanTheWholeRing_YieldsExactlyOneAnchor_TheBFallback()
    {
        // R8A §4.2(b) — a spacing wider than the polyline's own total arc length must never silently
        // produce ZERO labels; it produces exactly one, at startFrac × totalArc.
        //
        // This is the EXACT bug: before this fix, ContourRenderer.DrawIsoLines's inline walk started
        // at targetArcW = startFrac × 30 ≈ 4.5–25.5 world units, but this ring's entire arc is only
        // ≈ 3.77 — so `while (targetArcW <= segEnd)` never once fired and the walk drew ZERO labels,
        // for every contour on every Smith/Polar plot (§4.2's own default of 30.0 world units on a
        // ≤2π unit-disc polyline). Not asserted live here — that was the documented OLD behaviour;
        // ComputeLabelAnchors's whole point is that it can no longer happen.
        const double radius = 0.6;
        var pts = Ring(radius);

        var anchors = ContourRenderer.ComputeLabelAnchors(pts, spacingWorld: 30.0, ringIndex: 0);

        var (x, y) = Assert.Single(anchors);
        double dist = Math.Sqrt(x * x + y * y);
        Assert.True(Math.Abs(dist - radius) < 1e-9,
            $"the fallback anchor must still lie on the ring: distance {dist:G12} vs radius {radius}");
    }

    [Fact]
    public void FewerThanTwoPoints_ReturnsNoAnchors()
    {
        Assert.Empty(ContourRenderer.ComputeLabelAnchors([], spacingWorld: 0.35, ringIndex: 0));
        Assert.Empty(ContourRenderer.ComputeLabelAnchors([(0.1, 0.2)], spacingWorld: 0.35, ringIndex: 0));
    }

    [Fact]
    public void ZeroLengthPath_ReturnsNoAnchors()
    {
        // Every point identical: total arc length is zero, so there is nowhere to place a label.
        var pts = new List<(double X, double Y)> { (0.3, 0.4), (0.3, 0.4), (0.3, 0.4) };
        Assert.Empty(ContourRenderer.ComputeLabelAnchors(pts, spacingWorld: 0.35, ringIndex: 0));
    }
}
