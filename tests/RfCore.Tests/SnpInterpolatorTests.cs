// ================================================================
//  SnpInterpolatorTests.cs  —  SP-P1 gate
//
//  The refactor's whole claim is that fitting once and evaluating per
//  frequency produces THE SAME DOUBLES as re-fitting per frequency. So
//  these tests compare Assert.Equal on Complex — no tolerance at all.
//  If a number moves, the refactor is wrong.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using NumFlat;
using RfCore;
using Xunit;

namespace RfCore.Tests;

public class SnpInterpolatorTests
{
    private static readonly string TestDataDir =
        Path.Combine(AppContext.BaseDirectory, "testdata");

    public static IEnumerable<object[]> FilesMethodsFormats()
    {
        foreach (string file in new[] { "2SC5226A.s2p", "Test_5Port.s5p" })
        foreach (InterpolationMethod m in new[] { InterpolationMethod.CubicSpline,
                                                  InterpolationMethod.Linear,
                                                  InterpolationMethod.Makima })
        foreach (InterpolationFormat f in new[] { InterpolationFormat.RealImag,
                                                  InterpolationFormat.MagPhase })
            yield return new object[] { file, m, f };
    }

    /// <summary>
    /// Per-frequency Evaluate == the batch Interpolate's matrix at that frequency, exactly,
    /// on a grid that deliberately straddles knots (the interesting case for a spline).
    /// </summary>
    [Theory]
    [MemberData(nameof(FilesMethodsFormats))]
    public void PerFrequency_EqualsBatchInterpolate_BitIdentical(
        string filename, InterpolationMethod method, InterpolationFormat format)
    {
        var snp = TouchstoneIO.ReadFile(Path.Combine(TestDataDir, filename));

        double fMin = snp.Frequencies[0];
        double fMax = snp.Frequencies[snp.FrequencyCount - 1];
        var grid = new double[97];
        for (int i = 0; i < grid.Length; i++)
            grid[i] = fMin + (fMax - fMin) * i / (grid.Length - 1.0);

        var batch = RFNetwork.Interpolate(snp, grid, method, format);
        var interp = new SnpInterpolator(snp, method, format);

        for (int t = 0; t < grid.Length; t++)
        {
            var m = interp.Evaluate(grid[t]);
            for (int r = 0; r < snp.Ports; r++)
            for (int c = 0; c < snp.Ports; c++)
                Assert.Equal(batch.Matrices[t][r, c], m[r, c]);
        }
    }

    /// <summary>The stored points themselves — the interpolating property, exactly.</summary>
    [Theory]
    [MemberData(nameof(FilesMethodsFormats))]
    public void AtStoredPoints_EqualsBatchInterpolate_BitIdentical(
        string filename, InterpolationMethod method, InterpolationFormat format)
    {
        var snp   = TouchstoneIO.ReadFile(Path.Combine(TestDataDir, filename));
        var batch = RFNetwork.Interpolate(snp, snp.Frequencies, method, format);
        var interp = new SnpInterpolator(snp, method, format);

        for (int t = 0; t < snp.FrequencyCount; t++)
        {
            var m = interp.Evaluate(snp.Frequencies[t]);
            for (int r = 0; r < snp.Ports; r++)
            for (int c = 0; c < snp.Ports; c++)
                Assert.Equal(batch.Matrices[t][r, c], m[r, c]);
        }
    }

    /// <summary>Out-of-range on both sides, under both policies, still bit-identical.</summary>
    [Theory]
    [InlineData(OutOfRangePolicy.WarnClamp)]
    [InlineData(OutOfRangePolicy.WarnExtrapolate)]
    public void OutOfRange_EqualsBatchInterpolate_BitIdentical(OutOfRangePolicy policy)
    {
        var snp = TouchstoneIO.ReadFile(Path.Combine(TestDataDir, "2SC5226A.s2p"));
        double fMin = snp.Frequencies[0];
        double fMax = snp.Frequencies[snp.FrequencyCount - 1];
        double span = fMax - fMin;
        var grid = new[] { fMin - span, fMin - 1e9, fMin, (fMin + fMax) / 2, fMax, fMax + 1e9, fMax + span };

        var warnings = new List<string>();
        RFNetwork.OnWarning += warnings.Add;
        try
        {
            var batch  = RFNetwork.Interpolate(snp, grid, outOfRange: policy);
            var interp = new SnpInterpolator(snp, outOfRange: policy);

            for (int t = 0; t < grid.Length; t++)
            {
                var m = interp.Evaluate(grid[t]);
                for (int r = 0; r < snp.Ports; r++)
                for (int c = 0; c < snp.Ports; c++)
                    Assert.Equal(batch.Matrices[t][r, c], m[r, c]);
            }
        }
        finally
        {
            RFNetwork.OnWarning -= warnings.Add;
        }
    }

    /// <summary>
    /// The out-of-range warning is per INTERPOLATOR, not per Evaluate call — which is the whole
    /// point of hoisting the fit out of the per-point path: SnpModel.Stamp calls Evaluate once per
    /// frequency, and the old code warned once per frequency for the engine's drain to dedupe.
    /// </summary>
    [Fact]
    public void OutOfRangeWarning_FiresOncePerInterpolator_NotPerEvaluate()
    {
        var snp = TouchstoneIO.ReadFile(Path.Combine(TestDataDir, "2SC5226A.s2p"));
        double fMax = snp.Frequencies[snp.FrequencyCount - 1];

        var warnings = new List<string>();
        void Sink(string s) => warnings.Add(s);
        RFNetwork.OnWarning += Sink;
        try
        {
            var interp = new SnpInterpolator(snp);
            for (int i = 0; i < 25; i++) interp.Evaluate(fMax + 1e9);
            Assert.Single(warnings);

            // A second interpolator is a second consumer, and warns for itself.
            var interp2 = new SnpInterpolator(snp);
            interp2.Evaluate(fMax + 1e9);
            Assert.Equal(2, warnings.Count);
        }
        finally
        {
            RFNetwork.OnWarning -= Sink;
        }
    }

    /// <summary>An in-range interpolator never warns, however many times it is evaluated.</summary>
    [Fact]
    public void InRange_NeverWarns()
    {
        var snp = TouchstoneIO.ReadFile(Path.Combine(TestDataDir, "2SC5226A.s2p"));
        double fMin = snp.Frequencies[0];
        double fMax = snp.Frequencies[snp.FrequencyCount - 1];

        var warnings = new List<string>();
        void Sink(string s) => warnings.Add(s);
        RFNetwork.OnWarning += Sink;
        try
        {
            var interp = new SnpInterpolator(snp);
            for (int i = 0; i < 50; i++)
                interp.Evaluate(fMin + (fMax - fMin) * i / 49.0);
            Assert.Empty(warnings);
        }
        finally
        {
            RFNetwork.OnWarning -= Sink;
        }
    }

    /// <summary>Interpolating in Z rather than S goes through the same single fitting path.</summary>
    [Fact]
    public void InterpolateInZ_EqualsBatchInterpolate_BitIdentical()
    {
        var snp = TouchstoneIO.ReadFile(Path.Combine(TestDataDir, "2SC5226A.s2p"));
        var grid = new[] { snp.Frequencies[1] * 1.3, snp.Frequencies[2] * 0.9 };

        var batch  = RFNetwork.Interpolate(snp, grid, interpolateIn: MatrixType.Z);
        var interp = new SnpInterpolator(snp, interpolateIn: MatrixType.Z);

        Assert.Equal(MatrixType.Z, batch.Type);
        for (int t = 0; t < grid.Length; t++)
        for (int r = 0; r < snp.Ports; r++)
        for (int c = 0; c < snp.Ports; c++)
            Assert.Equal(batch.Matrices[t][r, c], interp.Evaluate(grid[t])[r, c]);
    }

    /// <summary>The batch overload is still the old Interpolate, byte for byte.</summary>
    [Fact]
    public void BatchEvaluate_IsWhatInterpolateReturns()
    {
        var snp  = TouchstoneIO.ReadFile(Path.Combine(TestDataDir, "Test_5Port.s5p"));
        var grid = new[] { snp.Frequencies[0], snp.Frequencies[1] * 1.1 };

        var a = RFNetwork.Interpolate(snp, grid, InterpolationMethod.Makima);
        var b = new SnpInterpolator(snp, InterpolationMethod.Makima).Evaluate(grid);

        Assert.Equal(a.Type, b.Type);
        Assert.Equal(a.Format, b.Format);
        Assert.Equal(a.Z0, b.Z0);
        Assert.Equal(a.Frequencies, b.Frequencies);
        for (int t = 0; t < grid.Length; t++)
        for (int r = 0; r < snp.Ports; r++)
        for (int c = 0; c < snp.Ports; c++)
            Assert.Equal(a.Matrices[t][r, c], b.Matrices[t][r, c]);
    }

    [Fact]
    public void EmptySource_Refused()
    {
        var broken = SNP.CreateBroken("nowhere.s2p");
        Assert.Throws<ArgumentException>(() => new SnpInterpolator(broken));
    }

    [Fact]
    public void EmptyTargetGrid_Refused()
    {
        var snp = TouchstoneIO.ReadFile(Path.Combine(TestDataDir, "2SC5226A.s2p"));
        Assert.Throws<ArgumentException>(
            () => new SnpInterpolator(snp).Evaluate(Array.Empty<double>()));
    }
}
