// ================================================================
//  TouchstoneCacheTests.cs  —  SP-P1 gate
//
//  The cache exists so a parametric sweep does not re-parse and re-fit
//  its Touchstone files at every point. Its two risks are a stale read
//  after the user re-saves a file, and two consumers sharing one fit.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using RfCore;
using Xunit;

namespace RfCore.Tests;

public class TouchstoneCacheTests
{
    private static readonly string TestDataDir =
        Path.Combine(AppContext.BaseDirectory, "testdata");

    /// <summary>Repeated reads of one unchanged file parse it exactly once.</summary>
    [Fact]
    public void RepeatedGet_ParsesOnce_AndReturnsTheSameInstance()
    {
        string path = Path.Combine(TestDataDir, "2SC5226A.s2p");

        TouchstoneCache.ResetForTesting();
        var a = TouchstoneCache.Get(path);
        for (int i = 0; i < 50; i++) TouchstoneCache.Get(path);
        var b = TouchstoneCache.Get(path);

        Assert.Same(a, b);
        Assert.Equal(1, TouchstoneCache.ParseCount);
    }

    /// <summary>
    /// One fit per (file, method, format, domain) — the point of the cache under a sweep, where
    /// every point news a fresh SnpModel over the same file.
    /// </summary>
    [Fact]
    public void RepeatedGetInterpolator_FitsOncePerSettingsTuple()
    {
        string path = Path.Combine(TestDataDir, "2SC5226A.s2p");

        TouchstoneCache.ResetForTesting();
        for (int i = 0; i < 20; i++)
            TouchstoneCache.GetInterpolator(path);
        Assert.Equal(1, TouchstoneCache.FitCount);

        // A different method is a different fit; a different POLICY is not (the policy is an
        // evaluation-time choice, so it shares the coefficients).
        TouchstoneCache.GetInterpolator(path, InterpolationMethod.Makima);
        Assert.Equal(2, TouchstoneCache.FitCount);

        TouchstoneCache.GetInterpolator(path, outOfRange: OutOfRangePolicy.WarnExtrapolate);
        Assert.Equal(2, TouchstoneCache.FitCount);

        TouchstoneCache.GetInterpolator(path, format: InterpolationFormat.MagPhase);
        Assert.Equal(3, TouchstoneCache.FitCount);

        Assert.Equal(1, TouchstoneCache.ParseCount);
    }

    /// <summary>
    /// Each caller gets its OWN interpolator wrapper over the shared fit, so the out-of-range
    /// warning is per consumer. Sharing the wrapper would mean a second run of the same design
    /// silently said nothing about a file the user is extrapolating past the end of.
    /// </summary>
    [Fact]
    public void EachInterpolator_WarnsForItself()
    {
        string path = Path.Combine(TestDataDir, "2SC5226A.s2p");
        var snp  = TouchstoneIO.ReadFile(path);
        double fMax = snp.Frequencies[snp.FrequencyCount - 1];

        TouchstoneCache.ResetForTesting();
        var warnings = new List<string>();
        void Sink(string s) => warnings.Add(s);
        RFNetwork.OnWarning += Sink;
        try
        {
            for (int run = 0; run < 3; run++)
            {
                var interp = TouchstoneCache.GetInterpolator(path);
                interp.Evaluate(fMax + 1e9);
                interp.Evaluate(fMax + 2e9);   // still the same consumer — no second warning
            }
        }
        finally
        {
            RFNetwork.OnWarning -= Sink;
        }

        Assert.Equal(3, warnings.Count);
        Assert.Equal(1, TouchstoneCache.FitCount);
    }

    /// <summary>A file re-saved between runs is re-read; an untouched one is not.</summary>
    [Fact]
    public void ChangedFile_IsReParsed()
    {
        string dir = Path.Combine(Path.GetTempPath(),
                                  "crf-tscache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "dut.s2p");
            File.WriteAllText(path, Through(0.5));

            TouchstoneCache.ResetForTesting();
            var first = TouchstoneCache.Get(path);
            Assert.Equal(0.5, first.Matrices[0][1, 0].Real, 12);
            Assert.Equal(1, TouchstoneCache.ParseCount);

            File.WriteAllText(path, Through(0.25));
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(2));

            var second = TouchstoneCache.Get(path);
            Assert.Equal(0.25, second.Matrices[0][1, 0].Real, 12);
            Assert.Equal(2, TouchstoneCache.ParseCount);

            // And the fit that went with the old contents does not survive the re-read.
            var interp = TouchstoneCache.GetInterpolator(path);
            Assert.Equal(0.25, interp.Evaluate(1.5e9)[1, 0].Real, 12);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>A missing file still throws from the file system, not from the cache.</summary>
    [Fact]
    public void MissingFile_Throws()
    {
        string path = Path.Combine(TestDataDir, "no-such-file-" + Guid.NewGuid().ToString("N") + ".s2p");
        Assert.ThrowsAny<IOException>(() => TouchstoneCache.Get(path));
    }

    private static string Through(double s21)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("! generated by TouchstoneCacheTests");
        sb.AppendLine("# GHz S RI R 50");
        foreach (double fGHz in new[] { 1.0, 1.5, 2.0, 2.5 })
            sb.AppendLine($"{fGHz:0.0000} 0 0 {s21:0.0000} 0 {s21:0.0000} 0 0 0");
        return sb.ToString();
    }
}
