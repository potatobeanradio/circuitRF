// ================================================================
//  ContourGridHoleSpanTests.cs — R8A §6's own gate
//
//  D5's original doctrine ("holes are thrown out, never extrapolated into") is REVERSED by owner
//  ruling: the surface model still covers a hole, so an iso-line now SPANS it rather than breaking.
//  See ContourGrid's own class doc comment for the reversal, the reasoning, and why it depends on the
//  hollow hole dot staying drawn. What must NOT change is the optimum search — MXP/MXE and
//  InterpolatedArgmax must never be reported at a Γ nothing converged at.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using RfCore.Loadpull;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Harmonica.Tests;

public sealed class ContourGridHoleSpanTests(ITestOutputHelper output)
{
    /// <summary>Same synthetic fixture <c>ContourGridTests</c>'s own Tier 7 uses: a ring set with one
    /// INTERIOR point removed (an edge hole would be excluded by the convex hull anyway, and would
    /// prove nothing about the disc-exclusion switch this file is about).</summary>
    private static (ContourGrid Grid, Complex HoleGamma) BuildHoleGrid()
    {
        var gammas = ContourGrid.RingGrid(rings: 3, spokes: 12, maxGamma: 0.75);
        var grid = new ContourGrid();
        int holeIndex = 1 + 12 + 3;
        ContourGridTests.SeedSyntheticFor(grid, gammas, holeIndex);
        return (grid, gammas[holeIndex]);
    }

    // ContourGrid.ConvexHull/InsideHull are `internal` in CircuitRF.Harmonica, which grants no
    // InternalsVisibleTo to CircuitRF.Harmonica.Tests (unlike CircuitRF.Ui's own test project) — so
    // they are reached the same way ContourGridTests.SeedSyntheticFor already reaches ContourGrid's
    // private `_points` field: reflection.

    private static IReadOnlyList<Complex> ConvexHull(IReadOnlyList<Complex> pts)
    {
        var m = typeof(ContourGrid).GetMethod("ConvexHull",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (IReadOnlyList<Complex>)m.Invoke(null, [pts])!;
    }

    private static bool InsideHull(IReadOnlyList<Complex> hull, double re, double im)
    {
        var m = typeof(ContourGrid).GetMethod("InsideHull",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (bool)m.Invoke(null, [hull, re, im])!;
    }

    [Fact]
    public void Raster_DefaultExcludeHoleDiscsFalse_HasNoNaNCellInsideTheHull()
    {
        var (grid, holeGamma) = BuildHoleGrid();
        var hull = ConvexHull([.. grid.Points.Where(p => !p.IsHole).Select(p => p.Gamma)]);

        // R8A §6 — the default: the surface still covers the hole.
        var raster = grid.Raster(GridMetric.PoutDbm, resolution: 121);

        int nanInsideHull = 0, insideHullCount = 0;
        for (int yi = 0; yi < raster.YSpace.Length; yi++)
        for (int xi = 0; xi < raster.XSpace.Length; xi++)
        {
            double x = raster.XSpace[xi], y = raster.YSpace[yi];
            if (!InsideHull(hull, x, y)) continue;
            insideHullCount++;
            if (double.IsNaN(raster.Values[yi * raster.XSpace.Length + xi])) nanInsideHull++;
        }

        output.WriteLine($"{insideHullCount} raster cells inside the hull, {nanInsideHull} of them NaN " +
                         $"(hole at Γ={holeGamma:G4})");
        Assert.True(insideHullCount > 0, "the hull check itself found nothing — fixture is broken");
        Assert.Equal(0, nanInsideHull);
    }

    [Fact]
    public void Raster_ExcludeHoleDiscsTrue_StillHasAtLeastOneNaNCell()
    {
        var (grid, _) = BuildHoleGrid();

        // The pre-R8A behaviour, still reachable explicitly — the mechanism InterpolatedArgmax leans
        // on unconditionally must still exist.
        var raster = grid.Raster(GridMetric.PoutDbm, resolution: 121, excludeHoleDiscs: true);

        int nan = raster.Values.Count(double.IsNaN);
        output.WriteLine($"{nan} NaN cells with excludeHoleDiscs: true");
        Assert.True(nan > 0, "excludeHoleDiscs: true must still blank at least the hole's own cell");
    }

    [Fact]
    public void Contours_DefaultSpanning_ReturnsAPolylineStraddlingTheHoleCentre()
    {
        var (grid, holeGamma) = BuildHoleGrid();
        double radius = grid.HoleRadius;

        // R8A §6 — Contours() always builds its raster with excludeHoleDiscs: false now.
        var polylines = grid.Contours(GridMetric.PoutDbm, levels: 12, resolution: 201);
        Assert.True(polylines.Count > 0, "no contours were drawn at all");

        int insideDisc = polylines.SelectMany(p => p.Points).Count(pt =>
        {
            double dr = pt.X - holeGamma.Real, di = pt.Y - holeGamma.Imaginary;
            return dr * dr + di * di < radius * radius;
        });
        output.WriteLine($"{insideDisc} contour vertices sit inside the hole's own {radius:F4}-Γ disc " +
                         $"— under D5's original doctrine this was always 0");
        Assert.True(insideDisc > 0,
            "a spanning contour must actually pass through the hole's disc, or the reversal isn't wired");
    }

    [Fact]
    public void InterpolatedArgmax_FedTheExcludingRaster_StillRefusesTheHole()
    {
        var (grid, _) = BuildHoleGrid();

        // Mirrors HarmonicaSolver.BuildSmith's own R8A §6.3 call: the optimum search's raster keeps
        // excludeHoleDiscs: true regardless of what the DRAWN raster does.
        var raster = grid.Raster(GridMetric.PoutDbm, resolution: 121, excludeHoleDiscs: true);
        var argmax = grid.InterpolatedArgmax(GridMetric.PoutDbm, raster);
        Assert.NotNull(argmax);

        foreach (var p in grid.Points.Where(p => p.IsHole))
        {
            double dist = (argmax!.Value.Gamma - p.Gamma).Magnitude;
            output.WriteLine($"argmax Γ={argmax.Value.Gamma:G4}, hole Γ={p.Gamma:G4}, " +
                             $"distance {dist:F4} (must exceed HoleRadius {grid.HoleRadius:F4})");
            Assert.True(dist > grid.HoleRadius,
                "InterpolatedArgmax must never land inside a hole's excluded disc");
        }
    }
}
