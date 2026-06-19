// ================================================================
//  SparamPortIndexingTests.cs  —  Gate tests for brief-sparam-port-indexing
//
//  Rule: i/j axes use 1-based port numbers in bracket indices everywhere.
//  S[:, 2, 1] = S21, S[:, 1, 1] = S11. Internally Index stays 0-based.
//
//  Trace-card (generate/parse/label):
//  1. Generate_S11             — Trace slice (i:Pin0, j:Pin0) → "SP1.S[:, 1, 1]"
//  2. Generate_S21             — Trace slice (i:Pin1, j:Pin0) → "SP1.S[:, 2, 1]"
//  3. Legend_S21               — TraceLabeler quantity contains "(i=2,j=1)"
//  4. Parse_S21                — TryParse "SP1.S[:, 2, 1]" → i.Index==1, j.Index==0
//  5. RoundTrip                — parse → regenerate → identical string
//  6. PortOutOfRange           — port 3 on 2-port → false with "(1..2)"; port 0 → false
//  7. NonPortUnchanged         — HB V cube "harmonic" axis still uses 0-based index
//
//  Measurement evaluator (bracket + S() accessor):
//  8. Bracket_S21              — SP1.S[:, 2, 1] returns S21 over freq
//  9. Bracket_S11              — SP1.S[:, 1, 1] returns S11 over freq
// 10. Accessor_eq_Bracket      — SP1.S(2,1) equals SP1.S[:, 2, 1] element-wise
// 11. Bracket_PortOutOfRange   — SP1.S[:, 5, 1] on 2-port → ExpressionException
// 12. SweptS                   — SP1.S[0, :, 2, 1] pins sweep index 0 (0-based), returns S21
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Expressions;
using CircuitRF.Ui.DataDisplay;
using RfCore;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class SparamPortIndexingTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a [freq, i, j] S cube for a 2-port.
    /// i and j axes have VALUES [1.0, 2.0] (1-based port numbers).
    /// Data: S[f,i,j] = new Complex(10*(i+1) + (j+1), f) so each entry is unique.
    ///   S11[f] = (12, f),  S12[f] = (13, f)
    ///   S21[f] = (22, f),  S22[f] = (23, f)
    /// </summary>
    private static DataCube MakeS2Cube(int nFreq = 3)
    {
        var freqVals = new double[nFreq];
        for (int f = 0; f < nFreq; f++) freqVals[f] = (f + 1) * 1e9;

        // 1-based port values: [1, 2]
        var portVals = new double[] { 1.0, 2.0 };
        var freqAxis = new Axis("freq", freqVals, "Hz");
        var iAxis    = new Axis("i", portVals, "port");
        var jAxis    = new Axis("j", portVals, "port");

        var data = new Complex[nFreq * 2 * 2];
        for (int f = 0; f < nFreq; f++)
        for (int i = 0; i < 2; i++)
        for (int j = 0; j < 2; j++)
            // i and j in 0-based index; port number = idx+1
            data[f * 4 + i * 2 + j] = new Complex(10.0 * (i + 1) + (j + 1), f);

        return new DataCube(new[] { freqAxis, iAxis, jAxis }, data);
    }

    /// <summary>Builds a grouped DataSet with "SP1" group containing the S cube.</summary>
    private static DataSet MakeGroupedDs(int nFreq = 3)
    {
        var ds = new DataSet();
        ds.AddToGroup("SP1", "S", MakeS2Cube(nFreq));
        return ds;
    }

    /// <summary>Builds a plain DataSet (for evaluator tests) with S in the default group.</summary>
    private static DataSet MakeEvalDs(int nFreq = 3)
    {
        var ds = new DataSet();
        ds.Add("S", MakeS2Cube(nFreq));
        return ds;
    }

    private static Trace MakeTrace(string cubeName, AxisSlice[] slice)
    {
        var t = new Trace(new SNP(new double[] { 1e9 }, 2), MatrixType.S, 0, 0, DependentVarFormat.Db);
        t.CubeName  = cubeName;
        t.Slice     = slice;
        t.Transform = CubeTransform.None;
        return t;
    }

    // ── Test 1: Generate_S11 ─────────────────────────────────────────────────

    [Fact]
    public void Generate_S11()
    {
        // Slice: freq→X, i pinned to index 0, j pinned to index 0 → S11 → "SP1.S[:, 1, 1]"
        var t = MakeTrace("SP1.S", new[]
        {
            new AxisSlice("freq", AxisRole.KeepAsX,    0),
            new AxisSlice("i",    AxisRole.PinToIndex, 0),  // port 1 (1-based)
            new AxisSlice("j",    AxisRole.PinToIndex, 0),  // port 1 (1-based)
        });

        Assert.Equal("SP1.S[:, 1, 1]", t.BuildPickerExpression());
    }

    // ── Test 2: Generate_S21 ─────────────────────────────────────────────────

    [Fact]
    public void Generate_S21()
    {
        // i pinned to index 1 (port 2), j pinned to index 0 (port 1) → "SP1.S[:, 2, 1]"
        var t = MakeTrace("SP1.S", new[]
        {
            new AxisSlice("freq", AxisRole.KeepAsX,    0),
            new AxisSlice("i",    AxisRole.PinToIndex, 1),  // port 2 (1-based)
            new AxisSlice("j",    AxisRole.PinToIndex, 0),  // port 1 (1-based)
        });

        Assert.Equal("SP1.S[:, 2, 1]", t.BuildPickerExpression());
    }

    // ── Test 3: Legend_S21 ───────────────────────────────────────────────────

    [Fact]
    public void Legend_S21()
    {
        // TraceLabeler must emit "(i=2,j=1)" for the S21 trace.
        var t = MakeTrace("SP1.S", new[]
        {
            new AxisSlice("freq", AxisRole.KeepAsX,    0),
            new AxisSlice("i",    AxisRole.PinToIndex, 1),  // index 1 → port 2
            new AxisSlice("j",    AxisRole.PinToIndex, 0),  // index 0 → port 1
        });

        var labels = TraceLabeler.ComputeMinimalLabels(new[] { t });
        Assert.Single(labels);
        Assert.Contains("(i=2,j=1)", labels[0]);
    }

    // ── Test 4: Parse_S21 ────────────────────────────────────────────────────

    [Fact]
    public void Parse_S21()
    {
        var ds = MakeGroupedDs();
        bool ok = CubeTraceSpecParser.TryParse(
            "SP1.S[:, 2, 1]", ds,
            out string cubeName, out var slice, out var transform, out string error);

        Assert.True(ok, error);
        Assert.Equal("SP1.S", cubeName);
        Assert.Equal(CubeTransform.None, transform);
        Assert.NotNull(slice);
        Assert.Equal(3, slice!.Length);

        // freq axis → KeepAsX
        Assert.Equal(AxisRole.KeepAsX, slice[0].Role);

        // i axis → pinned at 0-based index 1 (parsed from "2" via 1-based port rule)
        Assert.Equal(AxisRole.PinToIndex, slice[1].Role);
        Assert.Equal(1, slice[1].Index);

        // j axis → pinned at 0-based index 0 (parsed from "1" via 1-based port rule)
        Assert.Equal(AxisRole.PinToIndex, slice[2].Role);
        Assert.Equal(0, slice[2].Index);
    }

    // ── Test 5: RoundTrip ────────────────────────────────────────────────────

    [Fact]
    public void RoundTrip()
    {
        var ds = MakeGroupedDs();
        bool ok = CubeTraceSpecParser.TryParse(
            "SP1.S[:, 2, 1]", ds,
            out string cubeName, out var slice, out var transform, out string error);

        Assert.True(ok, error);

        // Reconstruct a Trace and regenerate the expression.
        var t = MakeTrace(cubeName, slice!);
        t.Transform = transform;

        Assert.Equal("SP1.S[:, 2, 1]", t.BuildPickerExpression());
    }

    // ── Test 6: PortOutOfRange ────────────────────────────────────────────────

    [Fact]
    public void PortOutOfRange()
    {
        var ds = MakeGroupedDs();

        // Port 3 does not exist on a 2-port (valid: 1..2).
        bool ok3 = CubeTraceSpecParser.TryParse(
            "SP1.S[:, 3, 1]", ds,
            out _, out _, out _, out string err3);
        Assert.False(ok3);
        Assert.Contains("(1..2)", err3);

        // Port 0 is not a valid 1-based port number.
        bool ok0 = CubeTraceSpecParser.TryParse(
            "SP1.S[:, 0, 1]", ds,
            out _, out _, out _, out string err0);
        Assert.False(ok0);
        Assert.Contains("(1..2)", err0);
    }

    // ── Test 7: NonPortUnchanged ─────────────────────────────────────────────

    [Fact]
    public void NonPortUnchanged()
    {
        // Build a V cube [sweep(5), node(2), harmonic(4)] — non-port axes.
        var sweepAxis = new Axis("sweep",    new double[] { -20, -15, -10, -5, 0 }, "dBm");
        var nodeAxis  = new Axis("node",     new double[] { 0, 1 }, labels: new[] { "Vout", "Vin" });
        var harmAxis  = new Axis("harmonic", new double[] { 0, 1, 2, 3 }, "Hz");
        var data = new Complex[5 * 2 * 4];
        var ds = new DataSet();
        ds.Add("V", new DataCube(new[] { sweepAxis, nodeAxis, harmAxis }, data));

        // "V[:, \"Vout\", 1]": sweep→X, node pinned by label "Vout", harmonic pinned at index 1 (0-based).
        bool ok = CubeTraceSpecParser.TryParse(
            "V[:, \"Vout\", 1]", ds,
            out _, out var slice, out _, out string error);

        Assert.True(ok, error);
        Assert.NotNull(slice);

        // harmonic axis token "1" is 0-based index (not 1-based port number).
        var harmSlice = slice!.First(s => s.AxisName == "harmonic");
        Assert.Equal(AxisRole.PinToIndex, harmSlice.Role);
        Assert.Equal(1, harmSlice.Index);   // 0-based index 1 = second harmonic

        // node slice is by label.
        var nodeSlice = slice.First(s => s.AxisName == "node");
        Assert.Equal("Vout", nodeSlice.Label);
    }

    // ── Evaluator helpers ─────────────────────────────────────────────────────

    private static Evaluator MakeEvaluator(int nFreq = 3)
    {
        var ds  = MakeEvalDs(nFreq);
        var ctx = new MeasurementContext(new Dictionary<string, DataSet> { ["SP1"] = ds });
        return new Evaluator(ctx);
    }

    private static Scope TestScope() => new Scope("test");

    // ── Test 8: Bracket_S21 ──────────────────────────────────────────────────

    [Fact]
    public void Bracket_S21()
    {
        var nFreq = 3;
        var eval = MakeEvaluator(nFreq);
        var ds   = MakeEvalDs(nFreq);

        var result = eval.Eval("SP1.S[:, 2, 1]", TestScope());
        Assert.Equal(ValueKind.Cube, result.Kind);

        var cube    = result.AsCube();
        var expected = ds.S(2, 1);

        Assert.Equal(1, cube.Rank);
        Assert.Equal(nFreq, cube.Axes[0].Length);

        for (int f = 0; f < nFreq; f++)
            Assert.Equal(expected.ComplexValues[f], cube.ComplexValues[f]);
    }

    // ── Test 9: Bracket_S11 ──────────────────────────────────────────────────

    [Fact]
    public void Bracket_S11()
    {
        var nFreq = 3;
        var eval  = MakeEvaluator(nFreq);
        var ds    = MakeEvalDs(nFreq);

        var result   = eval.Eval("SP1.S[:, 1, 1]", TestScope());
        var expected = ds.S(1, 1);

        Assert.Equal(ValueKind.Cube, result.Kind);
        var cube = result.AsCube();

        for (int f = 0; f < nFreq; f++)
            Assert.Equal(expected.ComplexValues[f], cube.ComplexValues[f]);
    }

    // ── Test 10: Accessor_eq_Bracket ─────────────────────────────────────────

    [Fact]
    public void Accessor_eq_Bracket()
    {
        var nFreq = 3;
        var eval  = MakeEvaluator(nFreq);

        var bracket  = eval.Eval("SP1.S[:, 2, 1]", TestScope());
        var accessor = eval.Eval("SP1.S(2,1)",      TestScope());

        Assert.Equal(ValueKind.Cube, bracket.Kind);
        Assert.Equal(ValueKind.Cube, accessor.Kind);

        var bc = bracket.AsCube();
        var ac = accessor.AsCube();

        Assert.Equal(bc.Axes[0].Length, ac.Axes[0].Length);
        for (int f = 0; f < nFreq; f++)
            Assert.Equal(ac.ComplexValues[f], bc.ComplexValues[f]);
    }

    // ── Test 11: Bracket_PortOutOfRange ───────────────────────────────────────

    [Fact]
    public void Bracket_PortOutOfRange()
    {
        var eval = MakeEvaluator();

        var ex = Assert.Throws<ExpressionException>(
            () => eval.Eval("SP1.S[:, 5, 1]", TestScope()));

        Assert.Contains("5", ex.Message);
        Assert.Contains("port", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Test 12: SweptS ───────────────────────────────────────────────────────

    [Fact]
    public void SweptS()
    {
        // Build a swept S cube [sweep(4), freq(3), i(2), j(2)] with 1-based i/j.
        int nSweep = 4, nFreq = 3;
        var sweepVals = Enumerable.Range(0, nSweep).Select(k => (double)k).ToArray();
        var freqVals  = Enumerable.Range(0, nFreq).Select(k => (k + 1) * 1e9).ToArray();
        var portVals  = new double[] { 1.0, 2.0 };

        var sweepAxis = new Axis("sweep", sweepVals, "");
        var freqAxis  = new Axis("freq",  freqVals,  "Hz");
        var iAxis     = new Axis("i",     portVals,  "port");
        var jAxis     = new Axis("j",     portVals,  "port");

        // Fill with distinguishable values: data[sweep,freq,i,j] = (sweep*1000 + freq*100 + i*10 + j)
        var data = new Complex[nSweep * nFreq * 2 * 2];
        for (int s = 0; s < nSweep; s++)
        for (int f = 0; f < nFreq;  f++)
        for (int i = 0; i < 2;      i++)
        for (int j = 0; j < 2;      j++)
            data[s * nFreq * 4 + f * 4 + i * 2 + j] = new Complex(s * 1000 + f * 100 + i * 10 + j, 0);

        var cube = new DataCube(new[] { sweepAxis, freqAxis, iAxis, jAxis }, data);
        var ds   = new DataSet();
        ds.Add("S", cube);
        var ctx  = new MeasurementContext(new Dictionary<string, DataSet> { ["SP1"] = ds });
        var eval = new Evaluator(ctx);

        // SP1.S[0, :, 2, 1] → sweep=0 (0-based), freq=All, i=2 (1-based→idx 1), j=1 (1-based→idx 0)
        var result = eval.Eval("SP1.S[0, :, 2, 1]", TestScope());
        Assert.Equal(ValueKind.Cube, result.Kind);

        var resultCube = result.AsCube();
        Assert.Equal(1, resultCube.Rank);
        Assert.Equal("freq", resultCube.Axes[0].Name);
        Assert.Equal(nFreq, resultCube.Axes[0].Length);

        // At sweep=0, i=1(idx), j=0(idx): value = 0*1000 + f*100 + 1*10 + 0 = f*100 + 10
        for (int f = 0; f < nFreq; f++)
            Assert.Equal(new Complex(f * 100 + 10, 0), resultCube.ComplexValues[f]);
    }
}
