// ================================================================
//  LoadpullWriterTests.cs — Phase-2 .spl/.lpcwave writer round-trips
//  (docs/design/loadpull-postprocessor.md §6).
//
//  Strategy: a canonical loadpull DataSet (the SplWriter/LpcwaveWriter input
//  contract) → write → read back via SplReader/LpcwaveReader → assert cube
//  parity. Covers single-freq, multi-freq, ZSource, and the cross-format
//  round-trip (.spl → .lpcwave and back) that answers "can we convert between
//  the two?" — yes, because both go through the one canonical DataSet.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Numerics;
using RfCore.Data;
using RfCore.Loadpull;
using Xunit;

namespace RfCore.Tests;

public sealed class LoadpullWriterTests
{
    // ── Synthetic canonical loadpull DataSet builders ─────────────────────────

    private const int NG = 4, NP = 3;

    // Single-frequency: FOMs over {gridPoint, pinStep}; GammaLoad over {gridPoint};
    // ZSource + __Freq rank-1 {freq}.
    private static DataSet BuildSingleFreq(double freqHz)
    {
        var grid = new Axis("gridPoint", Enumerable.Range(0, NG).Select(i => (double)i).ToArray());
        var pin  = new Axis("pinStep",   new[] { -10.0, -5.0, 0.0 });   // Pavl dBm

        var gamma = new Complex[NG];
        for (int gi = 0; gi < NG; gi++)
            gamma[gi] = Complex.FromPolarCoordinates(0.2 + 0.15 * gi, gi * 0.5);

        DataCube Fom(Func<int, int, double> f)
        {
            var buf = new double[NG * NP];
            for (int gi = 0; gi < NG; gi++)
                for (int pi = 0; pi < NP; pi++)
                    buf[gi * NP + pi] = f(gi, pi);
            return new DataCube(new[] { grid, pin }, buf);
        }

        var ds = new DataSet();
        ds.Add("GammaLoad", new DataCube(new[] { grid }, gamma));
        ds.Add("Pout_dBm",  Fom((gi, pi) => 30.0 + gi + pi));
        ds.Add("Gt_dB",     Fom((gi, pi) => 12.0 + 0.1 * gi - 0.2 * pi));
        ds.Add("Gp_dB",     Fom((gi, pi) => 14.0 + 0.1 * gi));
        ds.Add("Efficiency",Fom((gi, pi) => 40.0 + 2.0 * gi + pi));      // %
        ds.Add("PAE",       Fom((gi, pi) => 35.0 + 2.0 * gi + pi));      // %
        ds.Add("BiasILoad", Fom((gi, pi) => 0.05 + 0.001 * gi));        // A (canonical, positive)
        ds.Add("Zin_real",  Fom((gi, pi) => 60.0 + gi));
        ds.Add("Zin_imag",  Fom((gi, pi) => -5.0 + pi));
        ds.Add("IRL_dB",    Fom((gi, pi) => -12.0 - gi));
        ds.Add("AMPM_deg",  Fom((gi, pi) => 2.0 * pi));
        ds.Add("ZSource",   new DataCube(new[] { new Axis("freq", new[] { freqHz }, "Hz") },
                                         new[] { new Complex(40.0, 10.0) }));
        ds.Add("__Freq",    new DataCube(new[] { new Axis("freq", new[] { freqHz }, "Hz") }, new[] { freqHz }));
        return ds;
    }

    // Multi-frequency: FOMs over {freq, gridPoint, pinStep}; GammaLoad over {freq, gridPoint}.
    private static DataSet BuildMultiFreq(double[] freqHz)
    {
        int nF = freqHz.Length;
        var fAxis = new Axis("freq", freqHz, "Hz");
        var grid  = new Axis("gridPoint", Enumerable.Range(0, NG).Select(i => (double)i).ToArray());
        var pin   = new Axis("pinStep",   new[] { -10.0, -5.0, 0.0 });

        var gamma = new Complex[nF * NG];
        for (int fi = 0; fi < nF; fi++)
            for (int gi = 0; gi < NG; gi++)
                gamma[fi * NG + gi] = Complex.FromPolarCoordinates(0.2 + 0.1 * gi, fi + gi * 0.3);

        DataCube Fom(Func<int, int, int, double> f)
        {
            var buf = new double[nF * NG * NP];
            for (int fi = 0; fi < nF; fi++)
                for (int gi = 0; gi < NG; gi++)
                    for (int pi = 0; pi < NP; pi++)
                        buf[(fi * NG + gi) * NP + pi] = f(fi, gi, pi);
            return new DataCube(new[] { fAxis, grid, pin }, buf);
        }

        var ds = new DataSet();
        ds.Add("GammaLoad", new DataCube(new[] { fAxis, grid }, gamma));
        ds.Add("Pout_dBm",  Fom((fi, gi, pi) => 30.0 + 10 * fi + gi + pi));
        ds.Add("Gt_dB",     Fom((fi, gi, pi) => 12.0 + fi - 0.2 * pi));
        ds.Add("Efficiency",Fom((fi, gi, pi) => 40.0 + fi + gi + pi));
        ds.Add("PAE",       Fom((fi, gi, pi) => 35.0 + fi + gi));
        return ds;
    }

    // ── Round-trip assertion helper ───────────────────────────────────────────

    private static void AssertNear(double a, double b, double tol, string what)
    {
        if (double.IsNaN(a) && double.IsNaN(b)) return;
        Assert.True(Math.Abs(a - b) <= tol, $"{what}: expected {a}, got {b}");
    }

    // ── Single-freq .spl round-trip ───────────────────────────────────────────

    [Fact]
    public void Spl_SingleFreq_RoundTrip()
    {
        var src = BuildSingleFreq(2.4e9);
        var sw  = new StringWriter();
        SplWriter.WriteSpl(src, sw);
        var read = SplReader.ReadSpl(new StringReader(sw.ToString()));

        // Frequency carrier preserved.
        AssertNear(2.4e9, read["__Freq"].RealValues[0], 1.0, "freq");

        // GammaLoad per grid point.
        var gSrc = src["GammaLoad"].ComplexValues;
        var gRd  = read["GammaLoad"].ComplexValues;
        for (int gi = 0; gi < NG; gi++)
        {
            AssertNear(gSrc[gi].Real, gRd[gi].Real, 1e-5, $"GammaLoad[{gi}].re");
            AssertNear(gSrc[gi].Imaginary, gRd[gi].Imaginary, 1e-5, $"GammaLoad[{gi}].im");
        }

        // Every FOM round-trips per (gi,pi).
        foreach (var name in new[] { "Pout_dBm", "Gt_dB", "Gp_dB", "Efficiency", "PAE",
                                     "BiasILoad", "Zin_real", "Zin_imag", "IRL_dB", "AMPM_deg" })
        {
            var a = src[name].RealValues;
            var b = read[name].RealValues;
            Assert.Equal(a.Length, b.Length);
            for (int i = 0; i < a.Length; i++) AssertNear(a[i], b[i], 1e-4, $"{name}[{i}]");
        }

        // Pin axis (Pavl) preserved on the pinStep axis.
        var pinSrc = src["Pout_dBm"].Axes.First(ax => ax.Name == "pinStep").Values;
        var pinRd  = read["Pout_dBm"].Axes.First(ax => ax.Name == "pinStep").Values;
        for (int i = 0; i < pinSrc.Length; i++) AssertNear(pinSrc[i], pinRd[i], 1e-5, $"pinStep[{i}]");

        // ZSource (source termination) round-trips through gamma_src1.
        var zsA = src["ZSource"].ComplexValues[0];
        var zsB = read["ZSource"].ComplexValues[0];
        AssertNear(zsA.Real, zsB.Real, 1e-3, "ZSource.re");
        AssertNear(zsA.Imaginary, zsB.Imaginary, 1e-3, "ZSource.im");
    }

    // ── Single-freq .lpcwave round-trip ───────────────────────────────────────

    [Fact]
    public void Lpcwave_SingleFreq_RoundTrip()
    {
        var src = BuildSingleFreq(3.5e9);
        var sw  = new StringWriter();
        LpcwaveWriter.WriteLpcwave(src, sw);
        var read = LpcwaveReader.ReadLpcwave(new StringReader(sw.ToString()));

        var gSrc = src["GammaLoad"].ComplexValues;
        var gRd  = read["GammaLoad"].ComplexValues;
        for (int gi = 0; gi < NG; gi++)
        {
            AssertNear(gSrc[gi].Real, gRd[gi].Real, 1e-4, $"GammaLoad[{gi}].re");
            AssertNear(gSrc[gi].Imaginary, gRd[gi].Imaginary, 1e-4, $"GammaLoad[{gi}].im");
        }

        foreach (var name in new[] { "Pout_dBm", "Gt_dB", "Efficiency", "PAE",
                                     "Zin_real", "Zin_imag", "AMPM_deg" })
        {
            var a = src[name].RealValues;
            var b = read[name].RealValues;
            for (int i = 0; i < a.Length; i++) AssertNear(a[i], b[i], 1e-4, $"{name}[{i}]");
        }

        var zsA = src["ZSource"].ComplexValues[0];
        var zsB = read["ZSource"].ComplexValues[0];
        AssertNear(zsA.Real, zsB.Real, 1e-2, "ZSource.re");
        AssertNear(zsA.Imaginary, zsB.Imaginary, 1e-2, "ZSource.im");
    }

    // ── Multi-freq .spl round-trip (the multi-frequency requirement) ──────────

    [Fact]
    public void Spl_MultiFreq_RoundTrip()
    {
        var freqs = new[] { 1.8e9, 2.0e9, 2.2e9 };
        var src   = BuildMultiFreq(freqs);
        var sw    = new StringWriter();
        SplWriter.WriteSpl(src, sw);
        var read  = SplReader.ReadSpl(new StringReader(sw.ToString()));

        // Read-back is multi-freq: cubes carry a leading freq axis.
        var poutRd = read["Pout_dBm"];
        var fAxis  = poutRd.Axes.First(ax => ax.Name == "freq");
        Assert.Equal(freqs.Length, fAxis.Length);
        for (int fi = 0; fi < freqs.Length; fi++)
            AssertNear(freqs[fi], fAxis.Values[fi], 1e6, $"freq[{fi}]");

        // FOM parity across the full {freq, gridPoint, pinStep} cube.
        foreach (var name in new[] { "Pout_dBm", "Gt_dB", "Efficiency", "PAE" })
        {
            var a = src[name].RealValues;
            var b = read[name].RealValues;
            Assert.Equal(a.Length, b.Length);
            for (int i = 0; i < a.Length; i++) AssertNear(a[i], b[i], 1e-4, $"{name}[{i}]");
        }

        // GammaLoad over {freq, gridPoint}.
        var gA = src["GammaLoad"].ComplexValues;
        var gB = read["GammaLoad"].ComplexValues;
        Assert.Equal(gA.Length, gB.Length);
        for (int i = 0; i < gA.Length; i++)
        {
            AssertNear(gA[i].Real, gB[i].Real, 1e-4, $"GammaLoad[{i}].re");
            AssertNear(gA[i].Imaginary, gB[i].Imaginary, 1e-4, $"GammaLoad[{i}].im");
        }
    }

    // ── Multi-freq .lpcwave round-trip ────────────────────────────────────────

    [Fact]
    public void Lpcwave_MultiFreq_RoundTrip()
    {
        var freqs = new[] { 2.0e9, 4.0e9 };
        var src   = BuildMultiFreq(freqs);
        var sw    = new StringWriter();
        LpcwaveWriter.WriteLpcwave(src, sw);
        var read  = LpcwaveReader.ReadLpcwave(new StringReader(sw.ToString()));

        var fAxis = read["Pout_dBm"].Axes.First(ax => ax.Name == "freq");
        Assert.Equal(freqs.Length, fAxis.Length);

        var a = src["Pout_dBm"].RealValues;
        var b = read["Pout_dBm"].RealValues;
        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++) AssertNear(a[i], b[i], 1e-4, $"Pout_dBm[{i}]");
    }

    // ── Cross-format: .spl → DataSet → .lpcwave → DataSet (and back) ──────────
    // Answers the user's question: yes — both formats share the canonical DataSet,
    // so either writer can serialize a DataSet read from the other format.

    [Fact]
    public void CrossFormat_SplToLpcwaveToSpl_PreservesFoms()
    {
        var src = BuildSingleFreq(1.8e9);

        // canonical → .spl → DataSet
        var splText = new StringWriter();
        SplWriter.WriteSpl(src, splText);
        var fromSpl = SplReader.ReadSpl(new StringReader(splText.ToString()));

        // DataSet (from .spl) → .lpcwave → DataSet
        var lpcText = new StringWriter();
        LpcwaveWriter.WriteLpcwave(fromSpl, lpcText);
        var fromLpc = LpcwaveReader.ReadLpcwave(new StringReader(lpcText.ToString()));

        foreach (var name in new[] { "Pout_dBm", "Gt_dB", "Efficiency", "PAE", "Zin_real", "AMPM_deg" })
        {
            var a = fromSpl[name].RealValues;
            var b = fromLpc[name].RealValues;
            Assert.Equal(a.Length, b.Length);
            for (int i = 0; i < a.Length; i++) AssertNear(a[i], b[i], 1e-3, $"{name}[{i}]");
        }

        // GammaLoad survives the cross-format hop.
        var gA = fromSpl["GammaLoad"].ComplexValues;
        var gB = fromLpc["GammaLoad"].ComplexValues;
        for (int gi = 0; gi < NG; gi++)
            AssertNear(gA[gi].Magnitude, gB[gi].Magnitude, 1e-3, $"GammaLoad[{gi}].mag");
    }

    // ── Real measured file → write the other format → read back ───────────────

    private static string TestDataDir(string sub)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var c1 = Path.Combine(dir, "testdata", sub);
            if (Directory.Exists(c1)) return c1;
            var c2 = Path.Combine(dir, "circuitRF", "testdata", sub);
            if (Directory.Exists(c2)) return c2;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException($"testdata/{sub} not found");
    }

    [Fact]
    public void RealSpl_WrittenAsLpcwave_RoundTripsKeyFoms()
    {
        string spl = Path.Combine(TestDataDir("spl_test_data"), "Ideal_GaN_FET_1p6_mm_1p8_GHz.spl");
        var measured = SplReader.ReadSpl(spl);

        var sw = new StringWriter();
        LpcwaveWriter.WriteLpcwave(measured, sw);
        var read = LpcwaveReader.ReadLpcwave(new StringReader(sw.ToString()));

        // Pout_dBm parity over the full grid (NaN-aware).
        var a = measured["Pout_dBm"].RealValues;
        var b = read["Pout_dBm"].RealValues;
        Assert.Equal(a.Length, b.Length);
        int compared = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (double.IsNaN(a[i])) { Assert.True(double.IsNaN(b[i]), $"Pout_dBm[{i}] NaN mismatch"); continue; }
            AssertNear(a[i], b[i], 1e-3, $"Pout_dBm[{i}]");
            compared++;
        }
        Assert.True(compared > 0, "expected at least one valid Pout_dBm sample");
    }
}
