// ================================================================
//  TraceExpressionTests.cs  —  Gate tests for TraceExpression
//  (brief-trace-expressions.md, 8 tests)
// ================================================================

using System;
using System.Linq;
using System.Numerics;
using CircuitRF.Ui.DataDisplay;
using RfCore;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class TraceExpressionTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static DataSet MakeDs()
    {
        // Cube "V": axes [freq(3), node(2)]
        // Complex data in row-major order: V[freq, node]
        var freqAxis = new Axis("freq", new[] { 1e9, 2e9, 3e9 }, "Hz");
        var nodeAxis = new Axis("node", new[] { 0.0, 1.0 });
        var data = new Complex[]
        {
            new(1.0, 0.0), new(2.0, 0.0),   // freq=1GHz: node0=1+0j, node1=2+0j
            new(0.5, 0.5), new(1.0, 1.0),   // freq=2GHz
            new(0.1,-0.1), new(0.9, 0.9),   // freq=3GHz
        };
        var cube = new DataCube(new[] { freqAxis, nodeAxis }, data);
        var ds   = new DataSet();
        ds.Add("V", cube);
        return ds;
    }

    private static DataSet MakeDs2Cubes()
    {
        // Two cubes with *different* X lengths for dim-mismatch test.
        var f5  = new Axis("freq", new[] { 1e9, 2e9, 3e9, 4e9, 5e9 }, "Hz");
        var f3  = new Axis("freq", new[] { 1e9, 2e9, 3e9 }, "Hz");
        var n1  = new Axis("node", new[] { 0.0 });

        var c5 = new DataCube(new[] { f5, n1 }, Enumerable.Repeat(Complex.One, 5).ToArray());
        var c3 = new DataCube(new[] { f3, n1 }, Enumerable.Repeat(Complex.One, 3).ToArray());

        var ds = new DataSet();
        ds.Add("A", c5);
        ds.Add("B", c3);
        return ds;
    }

    // ── Test 1: single transform ──────────────────────────────────────────────

    [Fact]
    public void Expr_SingleTransform()
    {
        var ds = MakeDs();
        bool ok = TraceExpression.TryEvaluate(
            "mag(V[:, 0])", ds, PlotType.Rect,
            out var xVals, out var cz, out var rz,
            out _, out _, out _, out var err);

        Assert.True(ok, err);
        Assert.NotNull(rz);
        Assert.Null(cz);
        Assert.Equal(3, rz!.Length);

        // Expected: |V[0,0]|=1, |V[1,0]|=√0.5, |V[2,0]|=√0.02
        Assert.Equal(1.0,             rz[0], 6);
        Assert.Equal(Math.Sqrt(0.5),  rz[1], 6);
        Assert.Equal(Math.Sqrt(0.02), rz[2], 6);
    }

    // ── Test 2: element-wise sum ──────────────────────────────────────────────

    [Fact]
    public void Expr_Sum()
    {
        var ds = MakeDs();
        bool ok = TraceExpression.TryEvaluate(
            "mag(V[:, 0]) + mag(V[:, 1])", ds, PlotType.Rect,
            out _, out _, out var rz, out _, out _, out _, out var err);

        Assert.True(ok, err);
        Assert.NotNull(rz);
        Assert.Equal(3, rz!.Length);

        // freq=1GHz: |1+0j|+|2+0j| = 1+2 = 3
        Assert.Equal(3.0, rz[0], 6);
        // freq=2GHz: |0.5+0.5j|+|1+1j| = √0.5 + √2
        Assert.Equal(Math.Sqrt(0.5) + Math.Sqrt(2.0), rz[1], 6);
    }

    // ── Test 3: dB gain ───────────────────────────────────────────────────────

    [Fact]
    public void Expr_dBGain()
    {
        var ds = MakeDs();
        bool ok = TraceExpression.TryEvaluate(
            "dB20(V[:, 1]) - dB20(V[:, 0])", ds, PlotType.Rect,
            out _, out _, out var rz, out _, out _, out _, out var err);

        Assert.True(ok, err);
        Assert.NotNull(rz);

        // freq=1GHz: dB20(2) - dB20(1) = 20*log10(2) - 0
        double expected0 = 20.0 * Math.Log10(2.0);
        Assert.Equal(expected0, rz![0], 5);
    }

    // ── Test 4: bare ref → complex ────────────────────────────────────────────

    [Fact]
    public void Expr_BareRefComplex()
    {
        var ds = MakeDs();
        bool ok = TraceExpression.TryEvaluate(
            "V[:, 0]", ds, PlotType.Rect,
            out _, out var cz, out var rz, out _, out _, out _, out var err);

        Assert.True(ok, err);
        Assert.NotNull(cz);
        Assert.Null(rz);
        Assert.Equal(3, cz!.Length);

        Assert.Equal(new Complex(1.0, 0.0), cz[0]);
        Assert.Equal(new Complex(0.5, 0.5), cz[1]);
    }

    // ── Test 5: dimension mismatch ────────────────────────────────────────────

    [Fact]
    public void Expr_DimMismatch()
    {
        var ds = MakeDs2Cubes();
        bool ok = TraceExpression.TryEvaluate(
            "mag(A[:, 0]) + mag(B[:, 0])", ds, PlotType.Rect,
            out _, out _, out _, out _, out _, out _, out var err);

        Assert.False(ok);
        // Error message should mention both lengths.
        Assert.Contains("5", err);
        Assert.Contains("3", err);
    }

    // ── Test 6a: parse error ──────────────────────────────────────────────────

    [Fact]
    public void Expr_ParseError()
    {
        var ds = MakeDs();
        bool ok = TraceExpression.TryEvaluate(
            "mag(V[:, 0]) +", ds, PlotType.Rect,
            out _, out _, out _, out _, out _, out _, out var err);

        Assert.False(ok);
        Assert.Contains("parse", err, StringComparison.OrdinalIgnoreCase);
    }

    // ── Test 6b: unknown function ─────────────────────────────────────────────

    [Fact]
    public void Expr_UnknownFunction()
    {
        var ds = MakeDs();
        bool ok = TraceExpression.TryEvaluate(
            "foo(V[:, 0])", ds, PlotType.Rect,
            out _, out _, out _, out _, out _, out _, out var err);

        Assert.False(ok);
        Assert.Contains("foo", err);
    }

    // ── Test 7: function-call shorthand round-trips ───────────────────────────

    [Fact]
    public void Expr_FunctionCallSyntax()
    {
        var ds  = MakeDs();
        var snp = new SNP(new double[] { 1e9 }, 2);

        // Build a picker-authored trace with a mag transform.
        var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            CubeName  = "V",
            Slice     = new[]
            {
                new AxisSlice("freq", AxisRole.KeepAsX, 0),
                new AxisSlice("node", AxisRole.PinToIndex, 0),
            },
            Transform = CubeTransform.Mag,
        };

        // Shorthand must be function-call form: "mag(V[:, 0])"
        string shorthand = trace.BuildPickerExpression();
        Assert.Equal("mag(V[:, 0])", shorthand);

        // And TraceExpression must parse it successfully.
        bool ok = TraceExpression.TryEvaluate(
            shorthand, ds, PlotType.Rect,
            out _, out _, out var rz, out _, out _, out _, out var err);
        Assert.True(ok, err);
        Assert.NotNull(rz);
    }

    // ── Test 8: real expression on Smith → gentle error ───────────────────────

    [Fact]
    public void Expr_RealOnSmith()
    {
        var ds = MakeDs();
        bool ok = TraceExpression.TryEvaluate(
            "mag(V[:, 0])", ds, PlotType.Smith,
            out _, out _, out _, out _, out _, out _, out var err);

        Assert.False(ok);
        Assert.Contains("complex", err, StringComparison.OrdinalIgnoreCase);
    }

    // ── Test 9 (Part 1): V[All, 0] evaluates identically to V[:, 0] ─────────

    [Fact]
    public void Expr_All_AliasForColon()
    {
        var ds = MakeDs();
        bool ok1 = TraceExpression.TryEvaluate("V[:, 0]",   ds, PlotType.Rect,
            out var x1, out var c1, out _, out _, out _, out _, out _);
        bool ok2 = TraceExpression.TryEvaluate("V[All, 0]", ds, PlotType.Rect,
            out var x2, out var c2, out _, out _, out _, out _, out _);

        Assert.True(ok1);
        Assert.True(ok2);
        Assert.Equal(x1, x2);
        Assert.NotNull(c1);
        Assert.NotNull(c2);
        Assert.Equal(c1!.Length, c2!.Length);
        for (int i = 0; i < c1.Length; i++)
            Assert.Equal(c1[i], c2[i]);
    }

    // ── Test 10 (Part 1): two X axes in expression → error ───────────────────

    [Fact]
    public void Expr_TwoXAxes_Error()
    {
        var ds = MakeDs();
        bool ok = TraceExpression.TryEvaluate("V[:, :]", ds, PlotType.Rect,
            out _, out _, out _, out _, out _, out _, out var error);

        Assert.False(ok);
        Assert.Contains("more than one X axis", error, StringComparison.OrdinalIgnoreCase);
    }

    // ── Test 11: real expression on Table is accepted (Fix 2) ─────────────────

    [Fact]
    public void Expr_RealOnTable_Accepted()
    {
        var ds = MakeDs();
        bool ok = TraceExpression.TryEvaluate(
            "mag(V[:, 0])", ds, PlotType.Table,
            out _, out _, out var rz, out _, out _, out _, out var err);

        Assert.True(ok, err);
        Assert.NotNull(rz);
        Assert.Equal(3, rz!.Length);
    }
}
