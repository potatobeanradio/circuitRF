// ================================================================
//  LoadpullPostProcessorTests.cs — derived-metric enrichment of a
//  simulated loadpull DataSet (docs/design/loadpull-postprocessor.md).
//
//  Builds a synthetic engine-shaped DataSet (V/INl spectra + node-identity
//  provenance + Pout) and asserts LoadpullPostProcessor.Enrich adds
//  Pout_dBm, Zin_real/Zin_imag, IRL, AMPM with the expected values, is
//  idempotent, and is a no-op when inputs are absent.
// ================================================================

using System;
using System.Numerics;
using RfCore.Data;
using RfCore.Loadpull;
using Xunit;

namespace RfCore.Tests;

public sealed class LoadpullPostProcessorTests
{
    private const int NG = 2, NP = 3, NN = 2, NH = 2;   // 2 grid × 3 pin × {src,load} × {DC,fund}
    private const int SrcIdx = 0, LoadIdx = 1;

    // Build a synthetic LoadpullEngine-shaped DataSet (flat, top level).
    // Per (gi,pi): Zin = (80 + 5·idx) + j0 Ω (purely real → no input phase);
    //              load fundamental phase = 10·pi degrees (→ AM/PM = -10·pi after drive-up subtraction).
    private static DataSet BuildEngineDataSet(out double[] expectZinReal)
    {
        var grid = new Axis("gridPoint", new[] { 0.0, 1 });
        var pin  = new Axis("pinStep",   new[] { 0.0, 1, 2 });
        var node = new Axis("node",      new[] { 0.0, 1 }, labels: new[] { "n_gate", "n_drain" });
        var harm = new Axis("harmonic",  new[] { 0.0, 1 });

        var v   = new Complex[NG * NP * NN * NH];
        var inl = new Complex[NG * NP * NN * NH];
        var pout = new double[NG * NP];
        expectZinReal = new double[NG * NP];

        int Idx(int gi, int pi, int ni, int hi) => ((gi * NP + pi) * NN + ni) * NH + hi;

        const double iSrc = 0.01;   // constant source current
        for (int gi = 0; gi < NG; gi++)
        for (int pi = 0; pi < NP; pi++)
        {
            int fom = gi * NP + pi;
            double zin = 80.0 + 5.0 * fom;          // real input impedance
            expectZinReal[fom] = zin;

            // Source node fundamental: Isrc real, Vsrc = Zin·Isrc (real, phase 0).
            v[Idx(gi, pi, SrcIdx, 1)]   = new Complex(zin * iSrc, 0);
            inl[Idx(gi, pi, SrcIdx, 1)] = new Complex(iSrc, 0);

            // Load node fundamental: unit magnitude at phase 10·pi degrees (drive-dependent → AM/PM).
            double thetaDeg = 10.0 * pi;
            double th = thetaDeg * Math.PI / 180.0;
            v[Idx(gi, pi, LoadIdx, 1)] = new Complex(Math.Cos(th), Math.Sin(th));

            pout[fom] = 0.1 * (fom + 1);            // 0.1, 0.2, … W
        }

        // Engine-convention bias/efficiency: BiasILoad negative (passive sign), DE/PAE as fractions.
        var biasILoad = new double[NG * NP];
        var de        = new double[NG * NP];
        var pae       = new double[NG * NP];
        for (int i = 0; i < biasILoad.Length; i++) { biasILoad[i] = -0.05; de[i] = 0.6; pae[i] = 0.55; }

        var ds = new DataSet();
        ds.Add("Pout", new DataCube(new[] { grid, pin }, pout));
        ds.Add("V",    new DataCube(new[] { grid, pin, node, harm }, v));
        ds.Add("INl",  new DataCube(new[] { grid, pin, node, harm }, inl));
        ds.Add("BiasILoad", new DataCube(new[] { grid, pin }, biasILoad));
        ds.Add("BiasISrc",  new DataCube(new[] { grid, pin }, (double[])biasILoad.Clone()));
        ds.Add("DE",  new DataCube(new[] { grid, pin }, de));
        ds.Add("PAE", new DataCube(new[] { grid, pin }, pae));
        ds.Add("__SrcNodeIdx",  new DataCube(Array.Empty<Axis>(), new[] { (double)SrcIdx }));
        ds.Add("__LoadNodeIdx", new DataCube(Array.Empty<Axis>(), new[] { (double)LoadIdx }));
        return ds;
    }

    [Fact]
    public void Enrich_AddsDerivedMetrics_WithExpectedValues()
    {
        var ds = BuildEngineDataSet(out var expectZinReal);
        LoadpullPostProcessor.Enrich(ds);

        // Pout_dBm = 10·log10(Pw) + 30.
        var pw  = ds["Pout"].RealValues;
        var dbm = ds["Pout_dBm"].RealValues;
        for (int i = 0; i < pw.Length; i++)
            Assert.Equal(10.0 * Math.Log10(pw[i]) + 30.0, dbm[i], precision: 9);

        // Zin_real ≈ the impedance used to build Vsrc/Isrc; Zin_imag ≈ 0.
        var zinRe = ds["Zin_real"].RealValues;
        var zinIm = ds["Zin_imag"].RealValues;
        for (int i = 0; i < expectZinReal.Length; i++)
        {
            Assert.Equal(expectZinReal[i], zinRe[i], precision: 6);
            Assert.Equal(0.0, zinIm[i], precision: 6);
        }

        // IRL = -20·log10|Γin|, Γin = (Zin-50)/(Zin+50) (real). E.g. Zin=80 → Γ=30/130 → IRL≈12.74 dB.
        var irl = ds["IRL"].RealValues;
        for (int i = 0; i < expectZinReal.Length; i++)
        {
            double z = expectZinReal[i];
            double gmag = Math.Abs((z - 50.0) / (z + 50.0));
            Assert.Equal(-20.0 * Math.Log10(gmag), irl[i], precision: 6);
        }

        // AMPM per grid: trans_phase = 10·pi deg; AM/PM = trans[0]-trans[pi] = -10·pi (no wrap).
        var ampm = ds["AMPM"].RealValues;
        for (int gi = 0; gi < NG; gi++)
        for (int pi = 0; pi < NP; pi++)
            Assert.Equal(-10.0 * pi, ampm[gi * NP + pi], precision: 4);
    }

    [Fact]
    public void Enrich_DisplayConventionFixes_SignAndScale()
    {
        var ds = BuildEngineDataSet(out _);
        LoadpullPostProcessor.Enrich(ds);

        // BiasILoad/BiasISrc negated → positive Idq for display (engine stores passive sign).
        Assert.All(ds["BiasILoad"].RealValues, v => Assert.Equal(0.05, v, precision: 9));
        Assert.All(ds["BiasISrc"].RealValues,  v => Assert.Equal(0.05, v, precision: 9));
        // DE/PAE fraction → %.
        Assert.All(ds["DE"].RealValues,  v => Assert.Equal(60.0, v, precision: 9));
        Assert.All(ds["PAE"].RealValues, v => Assert.Equal(55.0, v, precision: 9));
    }

    [Fact]
    public void Enrich_IsIdempotent()
    {
        var ds = BuildEngineDataSet(out _);
        LoadpullPostProcessor.Enrich(ds);
        var firstDbm = (double[])ds["Pout_dBm"].RealValues.Clone();

        LoadpullPostProcessor.Enrich(ds);   // second pass — must not change, double-scale, or duplicate.
        Assert.Equal(firstDbm, ds["Pout_dBm"].RealValues);
        Assert.All(ds["DE"].RealValues, v => Assert.Equal(60.0, v, precision: 9));   // NOT 6000
        Assert.All(ds["BiasILoad"].RealValues, v => Assert.Equal(0.05, v, precision: 9)); // NOT back to -0.05
    }

    [Fact]
    public void Enrich_Measured_NoSrcNodeIdx_LeavesBiasAndEfficiencyUntouched()
    {
        // A measured-style DataSet (no __SrcNodeIdx) already carries +Idq and %-efficiency — the
        // display-convention fixes must NOT touch it (no double-scale / no sign flip).
        var grid = new Axis("gridPoint", new[] { 0.0, 1 });
        var pin  = new Axis("pinStep",   new[] { 0.0, 1 });
        var ds = new DataSet();
        ds.Add("Pout", new DataCube(new[] { grid, pin }, new[] { 0.1, 0.2, 0.3, 0.4 }));
        ds.Add("BiasILoad", new DataCube(new[] { grid, pin }, new[] { 0.05, 0.05, 0.05, 0.05 }));
        ds.Add("DE", new DataCube(new[] { grid, pin }, new[] { 60.0, 60, 60, 60 }));

        LoadpullPostProcessor.Enrich(ds);

        Assert.All(ds["BiasILoad"].RealValues, v => Assert.Equal(0.05, v, precision: 9));
        Assert.All(ds["DE"].RealValues, v => Assert.Equal(60.0, v, precision: 9));
    }

    [Fact]
    public void Enrich_NoSpectra_AddsOnlyPoutDbm()
    {
        var grid = new Axis("gridPoint", new[] { 0.0, 1 });
        var pin  = new Axis("pinStep",   new[] { 0.0, 1 });
        var ds = new DataSet();
        ds.Add("Pout", new DataCube(new[] { grid, pin }, new[] { 0.1, 0.2, 0.3, 0.4 }));

        LoadpullPostProcessor.Enrich(ds);

        Assert.True(ds.Contains("Pout_dBm"));        // Pout present → dBm derived
        Assert.False(ds.Contains("Zin_real"));       // no spectra → no Zin/IRL/AMPM
        Assert.False(ds.Contains("IRL"));
        Assert.False(ds.Contains("AMPM"));
    }

    [Fact]
    public void Enrich_Grouped_WritesIntoGroup()
    {
        var flat = BuildEngineDataSet(out _);
        // Re-key under a group to mimic a simulated LP run.npy.
        var grouped = new DataSet();
        foreach (var g in flat.Groups)
            foreach (var kv in flat.CubesIn(g))
                grouped.AddToGroup("LP1", kv.Key, kv.Value);

        LoadpullPostProcessor.Enrich(grouped, "LP1");

        Assert.True(grouped.Contains("LP1.Pout_dBm"));
        Assert.True(grouped.Contains("LP1.Zin_real"));
        Assert.True(grouped.Contains("LP1.AMPM"));
        Assert.True(grouped.Contains("LP1.IRL"));
    }
}
