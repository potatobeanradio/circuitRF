using System;
using CircuitRF.Core.Design;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate tests for <see cref="SweepExpander"/> — the headless sweep-axis expansion helper.
/// Covers StepSize-Lin, StepSize-Log, PointCount-Lin, PointCount-Log, and List modes.
/// </summary>
public sealed class SweepExpanderTests
{
    // ── StepSize, Linear ─────────────────────────────────────────────────────

    [Fact]
    public void ExpandSweep_StepSizeLin_CorrectPoints()
    {
        double[] pts = SweepExpander.ExpandSweep(0, 1, 0.25, SweepAxisMode.StepSize, SweepKind.Linear);

        Assert.Equal(5, pts.Length);
        Assert.Equal(0.00, pts[0], 6);
        Assert.Equal(0.25, pts[1], 6);
        Assert.Equal(0.50, pts[2], 6);
        Assert.Equal(0.75, pts[3], 6);
        Assert.Equal(1.00, pts[4], 6);
    }

    [Fact]
    public void ExpandSweep_StepSizeLin_ZeroStep_Fallback()
    {
        // step = 0 → falls back to (stop-start)/100 = 0.01 → 101 points
        double[] pts = SweepExpander.ExpandSweep(0, 1, 0, SweepAxisMode.StepSize, SweepKind.Linear);
        Assert.True(pts.Length > 1);
        Assert.Equal(0.0, pts[0],  6);
        Assert.Equal(1.0, pts[^1], 6);
    }

    // ── StepSize, Log ─────────────────────────────────────────────────────────

    [Fact]
    public void ExpandSweep_StepSizeLog_CorrectPoints()
    {
        // step = 10 (multiplicative ratio): 1 → 10 → 100
        double[] pts = SweepExpander.ExpandSweep(1, 100, 10, SweepAxisMode.StepSize, SweepKind.Log);

        Assert.Equal(3, pts.Length);
        Assert.Equal(1.0,   pts[0], 6);
        Assert.Equal(10.0,  pts[1], 6);
        Assert.Equal(100.0, pts[2], 6);
    }

    [Fact]
    public void ExpandSweep_StepSizeLog_StepLeOne_Fallback100()
    {
        // step ≤ 1 → falls back to LogSpace(start, stop, 100)
        double[] pts = SweepExpander.ExpandSweep(1, 1000, 0.5, SweepAxisMode.StepSize, SweepKind.Log);
        Assert.Equal(100, pts.Length);
        Assert.Equal(1.0,    pts[0], 3);
        Assert.Equal(1000.0, pts[^1], 3);
    }

    // ── PointCount, Linear ────────────────────────────────────────────────────

    [Fact]
    public void ExpandSweep_PointCountLin_CorrectPoints()
    {
        double[] pts = SweepExpander.ExpandSweep(-10, 10, 5, SweepAxisMode.PointCount, SweepKind.Linear);

        Assert.Equal(5, pts.Length);
        Assert.Equal(-10.0, pts[0], 6);
        Assert.Equal(-5.0,  pts[1], 6);
        Assert.Equal(0.0,   pts[2], 6);
        Assert.Equal(5.0,   pts[3], 6);
        Assert.Equal(10.0,  pts[4], 6);
    }

    [Fact]
    public void ExpandSweep_PointCountLin_OnePoint()
    {
        double[] pts = SweepExpander.ExpandSweep(3.14, 3.14, 1, SweepAxisMode.PointCount, SweepKind.Linear);
        Assert.Single(pts);
        Assert.Equal(3.14, pts[0], 6);
    }

    // ── PointCount, Log ───────────────────────────────────────────────────────

    [Fact]
    public void ExpandSweep_PointCountLog_CorrectPoints()
    {
        // 3 pts log from 1 to 100: 1, 10, 100
        double[] pts = SweepExpander.ExpandSweep(1, 100, 3, SweepAxisMode.PointCount, SweepKind.Log);

        Assert.Equal(3, pts.Length);
        Assert.Equal(1.0,   pts[0], 6);
        Assert.Equal(10.0,  pts[1], 4);
        Assert.Equal(100.0, pts[2], 6);
    }

    // ── List ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ExpandList_ParsesCommaValues()
    {
        double[] pts = SweepExpander.ExpandList("-20, -15, -10, -5, 0");

        Assert.Equal(5, pts.Length);
        Assert.Equal(-20.0, pts[0], 6);
        Assert.Equal(-15.0, pts[1], 6);
        Assert.Equal(-10.0, pts[2], 6);
        Assert.Equal(-5.0,  pts[3], 6);
        Assert.Equal(0.0,   pts[4], 6);
    }

    [Fact]
    public void ExpandList_ScientificNotation()
    {
        double[] pts = SweepExpander.ExpandList("1e9, 2e9, 5e9");

        Assert.Equal(3, pts.Length);
        Assert.Equal(1e9, pts[0], 3);
        Assert.Equal(2e9, pts[1], 3);
        Assert.Equal(5e9, pts[2], 3);
    }

    [Fact]
    public void ExpandList_EmptyString_ReturnsEmpty()
    {
        double[] pts = SweepExpander.ExpandList("");
        Assert.Empty(pts);
    }

    [Fact]
    public void ExpandList_WhitespaceOnly_ReturnsEmpty()
    {
        double[] pts = SweepExpander.ExpandList("   ");
        Assert.Empty(pts);
    }

    [Fact]
    public void ExpandList_InvalidTokensSkipped()
    {
        // Non-parseable tokens are skipped gracefully.
        double[] pts = SweepExpander.ExpandList("1, abc, 3");
        Assert.Equal(2, pts.Length);
        Assert.Equal(1.0, pts[0], 6);
        Assert.Equal(3.0, pts[1], 6);
    }
}
