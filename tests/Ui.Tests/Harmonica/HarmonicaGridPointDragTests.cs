// ================================================================
//  HarmonicaGridPointDragTests.cs  —  R-h7-12's gate, brief-harmonicarf-h7
//
//  "Dragging one grid point invalidates exactly one Γ sample — ~8 solves ≈ 8 ms plus a re-fit."
//  Measured against a full rebuild, not asserted.
// ================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using CircuitRF.Ui.Harmonica.Renderers;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaGridPointDragTests(ITestOutputHelper output)
{
    private static (HarmonicaContext Ctx, TerminationSet Terms) Fixture()
    {
        var model = HarmonicaViewModel.DefaultModel();
        var terms = new TerminationSet(model.Settings.HarmonicCount);
        terms.Set(TerminationSide.Source, 1, new Complex(25, 0));
        terms.Set(TerminationSide.Load,   1, new Complex(80, 10));
        return (HarmonicaContext.Create(model), terms);
    }

    // ══ the hit test — grid points are the THIRD pass ═══════════════════════

    [Fact]
    public void AGridPoint_IsGrabbedOnlyWhenNoMarkerOrGlyphIsThere()
    {
        var vm = new HarmonicaViewModel();
        vm.SolveFrame(new HarmonicaSolver.Options { Rings = 3, Spokes = 12, RasterResolution = 32 });

        var layout = vm.Layout;
        var points = vm.Frame.SmithPower.GridPoints;
        Assert.NotEmpty(points);

        var (_, size) = HarmonicaHitTest.ToPanel(layout, HarmonicaPanelId.SmithPower, 0, 0, 1000, 800);
        var panel = layout.PlacementOf(HarmonicaPanelId.SmithPower);

        // Find a grid point that is NOT under a marker, and grab it.
        int index = -1;
        double gx = 0, gy = 0;
        for (int i = 0; i < points.Count; i++)
        {
            var at = HarmonicaPanelRenderer.GammaToCanvas(points[i].Gamma, size);
            double cx = panel.X * 1000 + at.X, cy = panel.Y * 800 + at.Y;
            var probe = HarmonicaHitTest.Resolve(layout, vm.Markers, cx, cy, 1000, 800,
                                                 gridPoints: points);
            if (probe.Kind == HarmonicaGrabKind.GridPoint) { index = i; gx = cx; gy = cy; break; }
        }

        Assert.True(index >= 0, "no grid point was grabbable — every one is under a marker or a glyph");
        var grab = HarmonicaHitTest.Resolve(layout, vm.Markers, gx, gy, 1000, 800, gridPoints: points);
        Assert.Equal(HarmonicaGrabKind.GridPoint, grab.Kind);
        Assert.Equal(index, grab.GridIndex);
        output.WriteLine($"grabbed grid point {index} at Γ = {points[index].Gamma}");

        // Z-ORDER: a marker sitting exactly on a grid point wins, because it is drawn on top.
        var l1 = vm.Markers.Single(m => m is { Side: TerminationSideKind.Load, Band: 1 });
        var mAt = HarmonicaPanelRenderer.MarkerToCanvas(l1.Gamma, size);
        var onMarker = HarmonicaHitTest.Resolve(
            layout, vm.Markers, panel.X * 1000 + mAt.X, panel.Y * 800 + mAt.Y, 1000, 800,
            gridPoints: [.. points, new HarmonicaGridPoint(l1.Gamma, false)]);
        Assert.Equal(HarmonicaGrabKind.ExtrinsicMarker, onMarker.Kind);
    }

    [Fact]
    public void InsideTheUnitCircleTheTwoTransformsCOINCIDE_SoAGridPointCannotBeMissedByEither()
    {
        // Worth writing down rather than assumed either way: the compressed radial scale only ACTS
        // outside the rim (IntrinsicGlyphScale compresses |Γ| > 1 into the annulus and is the
        // identity below it). A grid point is a passive load termination and is always inside, so
        // MarkerToCanvas and GammaToCanvas agree on it exactly — the R-h6-1 offset that misses a
        // marker near the rim has NO analogue here.
        //
        // The hit test still goes through GammaToCanvas, because that is the transform the renderer
        // draws grid points with; matching the renderer is the rule, not the size of today's error.
        var layout = CharmLayout.Default;
        var size = (W: 420.0, H: 420.0);

        foreach (double mag in new[] { 0.0, 0.25, 0.6, 0.8, 0.95, 0.999 })
        {
            var gamma = Complex.FromPolarCoordinates(mag, 0.7);
            var raw        = HarmonicaPanelRenderer.GammaToCanvas(gamma, size);
            var compressed = HarmonicaPanelRenderer.MarkerToCanvas(gamma, size);
            double offset  = Math.Sqrt((raw.X - compressed.X) * (raw.X - compressed.X)
                                     + (raw.Y - compressed.Y) * (raw.Y - compressed.Y));
            output.WriteLine($"  |Γ| = {mag:F3}: offset {offset:E2} px");
            Assert.True(offset < 1e-4, $"the two transforms differ by {offset:E2} px at |Γ| = {mag}");
        }

        // …and OUTSIDE the rim they diverge, which is what the annulus is for. Stated here so the
        // "they coincide" claim above is scoped rather than absolute.
        var outside = new Complex(1.6, 0.0);
        var rawOut  = HarmonicaPanelRenderer.GammaToCanvas(outside, size);
        var compOut = HarmonicaPanelRenderer.MarkerToCanvas(outside, size);
        output.WriteLine($"  |Γ| = 1.600: offset {Math.Abs(rawOut.X - compOut.X):F1} px (the annulus)");
        Assert.True(Math.Abs(rawOut.X - compOut.X) > 10.0);

        // …and a point at |Γ| = 0.95 is genuinely grabbable through the renderer's own transform, on
        // the panel's own coordinates rather than the canvas's.
        var points = new[] { new HarmonicaGridPoint(new Complex(0.95, 0.0), false) };
        double cw = 1000, ch = 800;
        var panel = layout.PlacementOf(HarmonicaPanelId.SmithPower);
        var at = HarmonicaPanelRenderer.GammaToCanvas(points[0].Gamma, (panel.W * cw, panel.H * ch));
        var grab = HarmonicaHitTest.Resolve(layout, [], panel.X * cw + at.X, panel.Y * ch + at.Y,
                                            cw, ch, gridPoints: points);
        Assert.Equal(HarmonicaGrabKind.GridPoint, grab.Kind);
    }

    // ══ R-h7-12 — one moved point is one Γ sample ═══════════════════════════

    [Fact]
    public void MovingOneGridPoint_ReSolvesOnePoint_AndKeepsEveryOther()
    {
        var (ctx, terms) = Fixture();
        var grid = new ContourGrid();
        var scatter = ContourGrid.RingGrid(3, 12).ToArray();

        grid.Build(ctx, terms, scatter, reuseUnchanged: true);
        int fullSolves = grid.SolveCount;
        Assert.Equal(0, grid.ReusedPointCount);

        // Move exactly one point.
        var moved = scatter.ToArray();
        moved[7] = moved[7] * 0.9 + new Complex(0.02, 0.01);

        grid.Build(ctx, terms, moved, reuseUnchanged: true);

        output.WriteLine($"full grid {scatter.Length} points, {fullSolves} HB solves");
        output.WriteLine($"one point moved: {grid.ReusedPointCount} reused, " +
                         $"{grid.SolveCount - grid.Points.Where((_, i) => i != 7).Sum(p => p.Result.Solves)} " +
                         $"solves on the moved point");

        Assert.Equal(scatter.Length - 1, grid.ReusedPointCount);
        int movedSolves = grid.Points[7].Result.Solves;
        Assert.True(movedSolves > 0);
        Assert.True(movedSolves < fullSolves / 4,
            $"the moved point cost {movedSolves} solves against a full rebuild's {fullSolves} — " +
            "that is not one Γ sample");
    }

    [Fact]
    public void TheReuseCache_RefusesWhenAnythingElseChanged()
    {
        var (ctx, terms) = Fixture();
        var grid = new ContourGrid();
        var scatter = ContourGrid.RingGrid(2, 8).ToArray();

        grid.Build(ctx, terms, scatter, reuseUnchanged: true);
        grid.Build(ctx, terms, scatter, reuseUnchanged: true);
        Assert.Equal(scatter.Length, grid.ReusedPointCount);

        // A different SOURCE termination is a different question at every load point, so nothing may
        // be kept. (The band the grid SWEEPS is deliberately excluded from the key — it is
        // overwritten per point and says nothing about what a held point was solved at.)
        var other = terms.Clone();
        other.Set(TerminationSide.Source, 1, new Complex(12, -8));
        grid.Build(ctx, other, scatter, reuseUnchanged: true);
        Assert.Equal(0, grid.ReusedPointCount);

        // A bias change likewise.
        grid.Build(ctx, other, scatter, reuseUnchanged: true);
        Assert.Equal(scatter.Length, grid.ReusedPointCount);
        ctx.SetBias(-3.15, 44.0);
        grid.Build(ctx, other, scatter, reuseUnchanged: true);
        Assert.Equal(0, grid.ReusedPointCount);
    }

    [Fact]
    public void MovingAGridPoint_InvalidatesTheRbfFactorization_BecauseTheNodeSetMoved()
    {
        var (ctx, terms) = Fixture();
        var grid = new ContourGrid();
        var scatter = ContourGrid.RingGrid(2, 8).ToArray();

        grid.Build(ctx, terms, scatter, reuseUnchanged: true);
        grid.Fit(GridMetric.PoutDbm);
        grid.Fit(GridMetric.DrainEfficiency);
        int factorsAfterFirstBuild = grid.FactorizationCount;
        Assert.Equal(1, factorsAfterFirstBuild);      // two metrics, one factor — R-hrf-9's own claim

        var moved = scatter.ToArray();
        moved[3] += new Complex(0.05, -0.03);
        grid.Build(ctx, terms, moved, reuseUnchanged: true);
        grid.Fit(GridMetric.PoutDbm);

        output.WriteLine($"factorizations: {factorsAfterFirstBuild} → {grid.FactorizationCount}");
        Assert.Equal(factorsAfterFirstBuild + 1, grid.FactorizationCount);
    }

    [Fact]
    public void ADragThroughTheDocument_FreezesTheRingSetSoTheMovedPointSurvivesTheNextFrame()
    {
        var vm = new HarmonicaViewModel();
        vm.SolveFrame(new HarmonicaSolver.Options { Rings = 2, Spokes = 8, RasterResolution = 32 });

        Assert.Null(vm.CustomGrid);
        vm.BeginGridPointDrag(3);
        Assert.NotNull(vm.CustomGrid);
        Assert.True(vm.IsGridPointDragging);

        var target = new Complex(0.45, -0.22);
        vm.DragGridPoint(3, target, dragging: true);
        Assert.Equal(target.Real, vm.CustomGrid![3].Real, 9);

        vm.EndGridPointDrag();
        Assert.False(vm.IsGridPointDragging);
    }

    [Fact]
    public void ADraggedGridPointIsClampedInsideTheUnitCircle()
    {
        var vm = new HarmonicaViewModel();
        vm.SolveFrame(new HarmonicaSolver.Options { Rings = 2, Spokes = 8, RasterResolution = 32 });
        vm.BeginGridPointDrag(1);

        vm.DragGridPoint(1, new Complex(2.5, 1.8), dragging: true);

        double mag = vm.CustomGrid![1].Magnitude;
        output.WriteLine($"dragged to |Γ| = 3.08, clamped to {mag:F4}");
        Assert.True(mag < 1.0, "a Γ at or beyond the rim is an open the closure cannot represent");
    }
}
