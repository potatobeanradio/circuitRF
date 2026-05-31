// ================================================================
//  DataCubeTests.cs  —  Contract tests for DataCube and DataSet
// ================================================================

using System;
using System.Numerics;
using RfCore;
using RfCore.Data;
using Xunit;

namespace RfCore.Tests;

public class DataCubeTests
{
    // ---- Test fixtures -------------------------------------------

    private static readonly Axis FreqAxis = new("freq",
        new[] { 1e9, 2e9, 3e9 }, "Hz");   // 3 points
    private static readonly Axis IAxis = new("i",
        new[] { 1.0, 2.0 }, "port");       // 2 ports
    private static readonly Axis JAxis = new("j",
        new[] { 1.0, 2.0 }, "port");       // 2 ports
    private static readonly Axis PinAxis = new("Pin",
        new[] { 0.0, 1.0, 2.0, 3.0 }, "dBm"); // 4 sweep points
    private static readonly Axis HarmAxis = new("harmonic",
        new[] { 0.0, 1.0, 2.0 }, "");     // DC, fund, 2nd

    // 3-freq × 2-port × 2-port Complex cube (S-parameter shape)
    private static DataCube MakeSCube()
    {
        var data = new Complex[3 * 2 * 2];
        int k = 0;
        for (int fi = 0; fi < 3; fi++)
        for (int i = 0; i < 2; i++)
        for (int j = 0; j < 2; j++)
            data[k++] = new Complex(fi * 10 + i + 1, j + 0.5);
        return new DataCube(new[] { FreqAxis, IAxis, JAxis }, data);
    }

    // 3-harmonic × 4-Pin Real cube (HB result shape)
    private static DataCube MakeRealCube()
    {
        var data = new double[3 * 4];
        for (int h = 0; h < 3; h++)
        for (int p = 0; p < 4; p++)
            data[h * 4 + p] = h * 10.0 + p;
        return new DataCube(new[] { HarmAxis, PinAxis }, data);
    }

    // ================================================================
    //  DataKind and construction
    // ================================================================

    [Fact]
    public void ComplexCube_HasComplexKind()
    {
        Assert.Equal(DataKind.Complex, MakeSCube().DataKind);
    }

    [Fact]
    public void RealCube_HasRealKind()
    {
        Assert.Equal(DataKind.Real, MakeRealCube().DataKind);
    }

    [Fact]
    public void ShapeMismatch_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new DataCube(new[] { FreqAxis, IAxis }, new Complex[3 * 3])); // wrong size
    }

    // ================================================================
    //  Slice semantics — int collapses rank, Range keeps it
    // ================================================================

    [Fact]
    public void Slice_IntInt_ReturnsBareComplex()
    {
        var cube = MakeSCube();
        // Pin all three axes → bare element
        SliceResult r = cube[0, 0, 0];
        Assert.True(r.IsComplex);
        Assert.False(r.IsCube);
        Complex val = (Complex)r;
        Assert.Equal(1.0, val.Real);
        Assert.Equal(0.5, val.Imaginary);
    }

    [Fact]
    public void Slice_RangeIntInt_ReturnsCubeRank1()
    {
        var cube = MakeSCube();
        // Keep freq, pin i=0, j=0
        DataCube result = (DataCube)cube[.., 0, 0];
        Assert.Equal(1, result.Rank);
        Assert.Equal(3, result.Axes[0].Length);
        Assert.Equal("freq", result.Axes[0].Name);
    }

    [Fact]
    public void Slice_RangeIntInt_ValuesCorrect()
    {
        var cube = MakeSCube();
        DataCube trace = (DataCube)cube[.., 0, 0];  // S11 over freq
        var vals = trace.ComplexValues;
        // S[fi, 0, 0] = new Complex(fi*10+1, 0.5)
        for (int fi = 0; fi < 3; fi++)
            Assert.Equal(new Complex(fi * 10 + 1, 0.5), vals[fi]);
    }

    [Fact]
    public void Slice_SubRange_EndExclusive()
    {
        var cube = MakeSCube();
        // freq range 1..3 = indices 1, 2 (not 3 — end exclusive)
        DataCube sub = (DataCube)cube[1..3, 0, 0];
        Assert.Equal(2, sub.Axes[0].Length);
        Assert.Equal(2e9, sub.Axes[0].Values[0]);
        Assert.Equal(3e9, sub.Axes[0].Values[1]);
    }

    [Fact]
    public void Slice_RangeRange_ReturnsCubeRank2()
    {
        var cube = MakeSCube();
        // Keep freq and i, pin j=1
        DataCube result = (DataCube)cube[.., .., 1];
        Assert.Equal(2, result.Rank);
        Assert.Equal(3, result.Axes[0].Length); // freq
        Assert.Equal(2, result.Axes[1].Length); // i
    }

    [Fact]
    public void Slice_AllRange_ReturnsSameShape()
    {
        var cube = MakeSCube();
        DataCube all = (DataCube)cube[.., .., ..];
        Assert.Equal(3, all.Rank);
        Assert.Equal(3 * 2 * 2, all.ComplexValues.Length);
    }

    [Fact]
    public void Slice_OutOfRange_Throws()
    {
        var cube = MakeSCube();
        Assert.Throws<ArgumentOutOfRangeException>(() => { var _ = cube[99, 0, 0]; });
    }

    [Fact]
    public void Slice_WrongArgCount_Throws()
    {
        var cube = MakeSCube();
        Assert.Throws<ArgumentException>(() => { var _ = cube[0, 0]; }); // 2 args for rank-3 cube
    }

    // ================================================================
    //  Axis labels preserved through slicing
    // ================================================================

    [Fact]
    public void Slice_AxisLabelsPreserved()
    {
        var cube = MakeSCube();
        DataCube sub = (DataCube)cube[.., 0, ..];
        Assert.Equal("freq", sub.Axes[0].Name);
        Assert.Equal("j",    sub.Axes[1].Name);
        Assert.Equal("Hz",   sub.Axes[0].Unit);
    }

    // ================================================================
    //  Element-wise transforms
    // ================================================================

    [Fact]
    public void Real_ComplexCube_ReturnsRealCube_SameShape()
    {
        var cube = MakeSCube();
        var r = cube.Real();
        Assert.Equal(DataKind.Real, r.DataKind);
        Assert.Equal(cube.Rank, r.Rank);
        Assert.Equal(cube.Axes[0].Length, r.Axes[0].Length);
    }

    [Fact]
    public void Imag_ComplexCube_ReturnsImaginaryPart()
    {
        var cube = MakeSCube();
        var im = cube.Imag();
        var vals = im.RealValues;
        // All imaginary parts are 0.5 or 1.5
        foreach (var v in vals)
            Assert.True(v == 0.5 || v == 1.5, $"Unexpected imag part {v}");
    }

    [Fact]
    public void Real_OnRealCube_IsNoOp()
    {
        var cube = MakeRealCube();
        var r = cube.Real();
        Assert.Same(cube, r);
    }

    [Fact]
    public void Mag_OnRealCube_IsNoOp()
    {
        var cube = MakeRealCube();
        var m = cube.Mag();
        Assert.Same(cube, m);
    }

    [Fact]
    public void DB20_vs_DB10_Differ_For_NonUnityMagnitude()
    {
        // For a complex value with magnitude 10, dB20=20, dB10=10
        var data = new Complex[] { new Complex(10, 0) };
        var cube = new DataCube(new[] { new Axis("x", new[] { 1.0 }) }, data);
        Assert.Equal(20.0, cube.DB20().RealValues[0], 6);
        Assert.Equal(10.0, cube.DB10().RealValues[0], 6);
        Assert.Equal(10.0, cube.DB().RealValues[0],   6);  // DB() is DB10()
    }

    [Fact]
    public void Phase_DefaultDegrees()
    {
        var data = new Complex[] { new Complex(0, 1) }; // j → 90°
        var cube = new DataCube(new[] { new Axis("x", new[] { 1.0 }) }, data);
        double phase = cube.Phase().RealValues[0];
        Assert.Equal(90.0, phase, 10);
    }

    [Fact]
    public void Conj_ComplexCube_ConjugatesValues()
    {
        var c = new Complex(3, 4);
        var cube = new DataCube(new[] { new Axis("x", new[] { 1.0 }) }, new[] { c });
        Complex back = (Complex)cube.Conj()[0];
        Assert.Equal(Complex.Conjugate(c), back);
    }

    // ================================================================
    //  Reductions
    // ================================================================

    [Fact]
    public void Max_ReducesAlongNamedAxis()
    {
        var cube = MakeRealCube();  // [3 harmonic × 4 Pin]
        var result = cube.Max("Pin");
        Assert.Equal(1, result.Rank);
        Assert.Equal(3, result.Axes[0].Length);
        Assert.Equal("harmonic", result.Axes[0].Name);
        var vals = result.RealValues;
        // Max over Pin for each harmonic: h*10+0 … h*10+3 → max = h*10+3
        for (int h = 0; h < 3; h++)
            Assert.Equal(h * 10.0 + 3, vals[h]);
    }

    [Fact]
    public void Min_ReducesAlongNamedAxis()
    {
        var cube = MakeRealCube();
        var result = cube.Min("harmonic");
        Assert.Equal(1, result.Rank);
        Assert.Equal(4, result.Axes[0].Length);
        var vals = result.RealValues;
        // Min over harmonic for each Pin: 0+p is always h=0 → min = p
        for (int p = 0; p < 4; p++)
            Assert.Equal((double)p, vals[p]);
    }

    [Fact]
    public void Reduce_RequiresRealCube()
    {
        Assert.Throws<InvalidOperationException>(() =>
            MakeSCube().Max("freq"));
    }

    [Fact]
    public void At_PinsNamedAxis()
    {
        var cube = MakeRealCube();  // [harmonic × Pin]
        var atPin2 = cube.At("Pin", 2);  // Pin index 2
        Assert.Equal(1, atPin2.Rank);
        Assert.Equal(3, atPin2.Axes[0].Length); // harmonic axis remains
        var vals = atPin2.RealValues;
        for (int h = 0; h < 3; h++)
            Assert.Equal(h * 10.0 + 2, vals[h]);
    }

    // ================================================================
    //  DataSet  —  S(i,j) convenience accessor
    // ================================================================

    [Fact]
    public void DataSet_S21_ReturnsCorrectTrace()
    {
        var snp = TouchstoneIO.ReadFile(
            System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", "2SC5226A.s2p"));
        var ds = DataSetBuilder.FromSnp(snp);

        Assert.True(ds.Contains("S"));

        // S(2,1) = row 1, col 0 in 0-based matrix (i=2 → index 1, j=1 → index 0)
        DataCube s21 = ds.S(2, 1);
        Assert.Equal(1, s21.Rank);
        Assert.Equal(snp.FrequencyCount, s21.Axes[0].Length);
        Assert.Equal("freq", s21.Axes[0].Name);

        // Spot-check first value against raw SNP matrix
        Complex expected = snp.Matrices[0][1, 0];
        Complex actual   = (Complex)s21[0];
        Assert.Equal(expected.Real,      actual.Real,      10);
        Assert.Equal(expected.Imaginary, actual.Imaginary, 10);
    }

    [Fact]
    public void DataSet_S11_DifferentFrom_S21()
    {
        var snp = TouchstoneIO.ReadFile(
            System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", "2SC5226A.s2p"));
        var ds = DataSetBuilder.FromSnp(snp);

        var s11 = ds.S(1, 1).ComplexValues;
        var s21 = ds.S(2, 1).ComplexValues;
        bool anyDiff = false;
        for (int i = 0; i < s11.Length && !anyDiff; i++)
            anyDiff = s11[i] != s21[i];
        Assert.True(anyDiff, "S11 and S21 should differ for a real transistor file");
    }

    [Fact]
    public void DataSet_PortOutOfRange_Throws()
    {
        var snp = TouchstoneIO.ReadFile(
            System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", "2SC5226A.s2p"));
        var ds = DataSetBuilder.FromSnp(snp);
        Assert.Throws<ArgumentException>(() => ds.S(3, 1)); // 2-port, port 3 doesn't exist
    }

    // ================================================================
    //  DataSet positional indexer
    // ================================================================

    [Fact]
    public void DataSet_PositionalIndexer_ReturnsSameCube()
    {
        var snp = TouchstoneIO.ReadFile(
            System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", "2SC5226A.s2p"));
        var ds = DataSetBuilder.FromSnp(snp);
        var cube = ds["S"];
        Assert.Equal(3, cube.Rank);
    }

    [Fact]
    public void DataSet_MissingKey_Throws()
    {
        var ds = new DataSet();
        Assert.Throws<System.Collections.Generic.KeyNotFoundException>(() => _ = ds["X"]);
    }
}
