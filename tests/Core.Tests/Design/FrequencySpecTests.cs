using System;
using System.Collections.Generic;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using Xunit;

namespace CircuitRF.Core.Tests.Design;

/// <summary>
/// Layer 1 gate: FrequencySpec — StepSize/PointCount modes, Linear/Log spacing,
/// expression-string fields, Expand() with and without globals.
/// </summary>
public sealed class FrequencySpecTests
{
    // ── StepSize, Linear ─────────────────────────────────────────────────────

    [Fact]
    public void StepSize_Linear_ExprStrings_ExpandsCorrectly()
    {
        var spec = new FrequencySpec("1e9", "3e9", "1e9");
        var pts  = spec.Expand();
        Assert.Equal(3, pts.Length);
        Assert.Equal(1e9, pts[0], 1.0);
        Assert.Equal(2e9, pts[1], 1.0);
        Assert.Equal(3e9, pts[2], 1.0);
    }

    [Fact]
    public void StepSize_Linear_BackcompatDoubles_ExpandsCorrectly()
    {
        // Backward-compat constructor: doubles stored as expression strings.
        var spec = new FrequencySpec(1e9, 10e9, 1e9);
        Assert.Equal(FreqSpecMode.StepSize, spec.Mode);
        Assert.Equal(SweepKind.Linear,      spec.Kind);
        var pts = spec.Expand();
        Assert.Equal(10, pts.Length);
        Assert.Equal(1e9,  pts[0],  1.0);
        Assert.Equal(10e9, pts[^1], 1.0);
    }

    [Fact]
    public void StepSize_Linear_StoresModeAndExprs()
    {
        var spec = new FrequencySpec("1e9", "2*f0", "500e6");
        Assert.Equal(FreqSpecMode.StepSize, spec.Mode);
        Assert.Equal(SweepKind.Linear,      spec.Kind);
        Assert.Equal("1e9",   spec.StartExpr);
        Assert.Equal("2*f0",  spec.StopExpr);
        Assert.Equal("500e6", spec.StepExpr);
        Assert.Null(spec.NumPoints);
    }

    // ── PointCount, Linear ───────────────────────────────────────────────────

    [Fact]
    public void PointCount_Linear_ExpandsToNPoints()
    {
        var spec = new FrequencySpec("1e9", "5e9", 5);
        Assert.Equal(FreqSpecMode.PointCount, spec.Mode);
        Assert.Equal(5, spec.NumPoints);
        var pts = spec.Expand();
        Assert.Equal(5,   pts.Length);
        Assert.Equal(1e9, pts[0], 1.0);
        Assert.Equal(5e9, pts[^1], 1.0);
        Assert.Equal(3e9, pts[2], 1.0);  // midpoint of 1–5 GHz with 5 pts
    }

    [Fact]
    public void PointCount_Linear_N2_HasExactlyEndpoints()
    {
        var spec = new FrequencySpec("1e9", "10e9", 2);
        var pts  = spec.Expand();
        Assert.Equal(2,    pts.Length);
        Assert.Equal(1e9,  pts[0],  1.0);
        Assert.Equal(10e9, pts[1],  1.0);
    }

    [Fact]
    public void PointCount_Linear_N1_ReturnsSingleStartPoint()
    {
        var spec = new FrequencySpec("2e9", "8e9", 1);
        var pts  = spec.Expand();
        Assert.Single(pts);
        Assert.Equal(2e9, pts[0], 1.0);
    }

    [Fact]
    public void PointCount_NumPointsLessThanOne_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FrequencySpec("1e9", "10e9", 0));
    }

    // ── PointCount, Log ──────────────────────────────────────────────────────

    [Fact]
    public void PointCount_Log_ExpandsToNLogSpacedPoints()
    {
        var spec = new FrequencySpec("1e9", "10e9", 11, SweepKind.Log);
        Assert.Equal(FreqSpecMode.PointCount, spec.Mode);
        Assert.Equal(SweepKind.Log,           spec.Kind);
        var pts = spec.Expand();
        Assert.Equal(11, pts.Length);
        Assert.Equal(1e9,  pts[0],  1e3);   // tolerance: 1 kHz
        Assert.Equal(10e9, pts[^1], 1e3);
        // Points should be multiplicatively equal spacing
        double ratio1 = pts[1] / pts[0];
        double ratio2 = pts[2] / pts[1];
        Assert.Equal(ratio1, ratio2, 3);  // 3 decimal places
    }

    [Fact]
    public void PointCount_Log_N2_HasExactEndpoints()
    {
        var spec = new FrequencySpec("1e9", "100e9", 2, SweepKind.Log);
        var pts  = spec.Expand();
        Assert.Equal(2,     pts.Length);
        Assert.Equal(1e9,   pts[0],  1e3);
        Assert.Equal(100e9, pts[1],  1e3);
    }

    // ── Expression-string globals resolution ─────────────────────────────────

    [Fact]
    public void ExpressionFields_ResolvedAgainstGlobals()
    {
        // stop = "2*f0" where f0 = 5e9 → stop = 10e9
        var globals = new Dictionary<string, Value>
        {
            ["f0"] = new Value(5e9),
        };
        var spec = new FrequencySpec("1e9", "2*f0", "1e9");
        var pts  = spec.Expand(globals);
        Assert.Equal(10, pts.Length);       // 1 GHz to 10 GHz in 1 GHz steps
        Assert.Equal(1e9,  pts[0],  1.0);
        Assert.Equal(10e9, pts[^1], 1.0);
    }

    [Fact]
    public void ExpressionFields_StartExpr_UsesGlobal()
    {
        var globals = new Dictionary<string, Value>
        {
            ["fstart"] = new Value(2e9),
        };
        var spec = new FrequencySpec("fstart", "4e9", 3);
        var pts  = spec.Expand(globals);
        Assert.Equal(3,   pts.Length);
        Assert.Equal(2e9, pts[0],  1.0);
        Assert.Equal(4e9, pts[^1], 1.0);
    }

    [Fact]
    public void ExpressionFields_NullGlobals_WorksForLiteralExprs()
    {
        var spec = new FrequencySpec("1e9", "10e9", "1e9");
        var pts  = spec.Expand(null);
        Assert.Equal(10, pts.Length);
    }

    // ── Mode and Kind stored correctly ────────────────────────────────────────

    [Fact]
    public void StepSize_LogKind_StoredCorrectly()
    {
        var spec = new FrequencySpec("1e9", "10e9", "2.0", SweepKind.Log);
        Assert.Equal(FreqSpecMode.StepSize, spec.Mode);
        Assert.Equal(SweepKind.Log,         spec.Kind);
        Assert.Empty(spec.StepExpr.Replace("2.0", ""));  // StepExpr set
        Assert.Null(spec.NumPoints);
    }

    [Fact]
    public void PointCount_LogKind_StoredCorrectly()
    {
        var spec = new FrequencySpec("1e9", "10e9", 101, SweepKind.Log);
        Assert.Equal(FreqSpecMode.PointCount, spec.Mode);
        Assert.Equal(SweepKind.Log,           spec.Kind);
        Assert.Equal(101, spec.NumPoints);
        Assert.Empty(spec.StepExpr);
    }

    // ── StepSize, Log (multiplicative ratio) ─────────────────────────────────

    [Fact]
    public void StepSize_Log_MultiplicativeRatio_ExpandsCorrectly()
    {
        // Ratio 10: 1 GHz → 10 GHz → 100 GHz (3 points)
        var spec = new FrequencySpec("1e9", "100e9", "10.0", SweepKind.Log);
        var pts  = spec.Expand();
        Assert.Equal(3,     pts.Length);
        Assert.Equal(1e9,   pts[0],  1.0);
        Assert.Equal(10e9,  pts[1],  1.0);
        Assert.Equal(100e9, pts[2],  1.0);
    }
}
