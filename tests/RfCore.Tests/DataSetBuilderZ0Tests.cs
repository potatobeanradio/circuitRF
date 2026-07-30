// ================================================================
//  DataSetBuilderZ0Tests.cs  —  Phase 7.2a gate tests
//
//  Gates:
//  1. FromSnp emits a Z0 cube with axis "port" = [1..n] and all entries = snp.Z0.
//  3. .npy round-trip: S + per-port-complex Z0 survives export → import.
//  4. ToSnp reads Z0 cube: uniform → SNP.Z0; absent → 50 Ω; non-uniform → port-1 + warning.
//  5. ClassifyZ0 correctly identifies UniformReal, UniformComplex, and NonUniform inputs.
// ================================================================

using System;
using System.IO;
using System.Numerics;
using RfCore;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace RfCore.Tests;

public class DataSetBuilderZ0Tests
{
    // ── helpers ─────────────────────────────────────────────────────────────────

    private static SNP MakeUniformSnp(int nPorts, double z0Real = 50.0)
    {
        // 2-frequency, N-port SNP with the identity S-matrix (each frequency identical).
        double[] freqs  = [1e9, 2e9];
        var identity    = new NumFlat.Mat<Complex>(nPorts, nPorts);
        for (int i = 0; i < nPorts; i++) identity[i, i] = Complex.One;
        var mats = new[] { identity, identity };
        return new SNP(freqs, mats, MatrixType.S, MatrixFormat.RI, new Complex(z0Real, 0));
    }

    private static DataSet MakeZ0DataSet(Complex[] z0PerPort)
    {
        // Minimal 2-port S DataSet with a manually built Z0 cube.
        int n = z0PerPort.Length;
        double[] freqs    = [1e9, 2e9];
        var freqAxis      = new Axis("freq", freqs, "Hz");
        var portVals      = new double[n];
        for (int p = 0; p < n; p++) portVals[p] = p + 1;
        var iAxis = new Axis("i", portVals, "port");
        var jAxis = new Axis("j", portVals, "port");
        var data  = new Complex[freqs.Length * n * n];  // all zeros → S = 0 (fine for Z0 tests)
        var ds    = new DataSet();
        ds.Add("S",  new DataCube(new[] { freqAxis, iAxis, jAxis }, data));
        ds.Add("Z0", DataSetBuilder.BuildZ0Cube(z0PerPort));
        return ds;
    }

    // ── Gate 1: FromSnp emits a Z0 cube ─────────────────────────────────────────

    [Fact]
    public void FromSnp_EmitsZ0Cube_UniformPortAxis()
    {
        var snp = MakeUniformSnp(nPorts: 3, z0Real: 75.0);
        var ds  = DataSetBuilder.FromSnp(snp);

        Assert.True(ds.Contains("Z0"), "DataSet must contain a 'Z0' cube.");

        var z0 = ds["Z0"];
        Assert.Equal(DataKind.Complex, z0.DataKind);
        Assert.Equal(1, z0.Rank);
        Assert.Equal("port", z0.Axes[0].Name);
        Assert.Equal(3, z0.Axes[0].Length);

        // Axis values are 1-based port numbers
        for (int p = 0; p < 3; p++)
            Assert.Equal(p + 1, z0.Axes[0].Values[p]);

        // All entries equal snp.Z0
        var vals = z0.ComplexValues;
        Assert.Equal(3, vals.Length);
        foreach (var v in vals)
        {
            Assert.Equal(75.0, v.Real,      precision: 12);
            Assert.Equal(0.0,  v.Imaginary, precision: 12);
        }
    }

    [Fact]
    public void FromSnp_2Port_50Ohm_AllEntriesAre50()
    {
        var snp = MakeUniformSnp(nPorts: 2, z0Real: 50.0);
        var ds  = DataSetBuilder.FromSnp(snp);

        var vals = ds["Z0"].ComplexValues;
        Assert.Equal(2, vals.Length);
        Assert.Equal(new Complex(50, 0), vals[0]);
        Assert.Equal(new Complex(50, 0), vals[1]);
    }

    // ── Gate 3: .npy round-trip ─────────────────────────────────────────────────

    [Fact]
    public void NpyRoundTrip_Z0CubeSurvivesExportImport()
    {
        var z0PerPort = new Complex[] { new(50, 0), new(75, -10) };
        var ds        = MakeZ0DataSet(z0PerPort);

        var path = Path.Combine(Path.GetTempPath(), $"z0rt_{Guid.NewGuid():N}.npy");
        try
        {
            DataSetExporter.Export(ds, path, ExportFormat.Npy);
            var (imported, _) = DataSetImporter.Import(path);

            Assert.True(imported.Contains("Z0"), "Imported DataSet must have a 'Z0' cube.");
            var z0 = imported["Z0"];
            Assert.Equal(DataKind.Complex, z0.DataKind);
            Assert.Equal(1, z0.Rank);
            Assert.Equal("port", z0.Axes[0].Name);
            Assert.Equal(2, z0.Axes[0].Length);

            // Axis values bitwise-equal
            Assert.Equal(1.0, z0.Axes[0].Values[0]);
            Assert.Equal(2.0, z0.Axes[0].Values[1]);

            // Impedance values survive with zero tolerance (IEEE double round-trip via G17)
            var vals = z0.ComplexValues;
            Assert.Equal(50.0,  vals[0].Real,      precision: 15);
            Assert.Equal(0.0,   vals[0].Imaginary, precision: 15);
            Assert.Equal(75.0,  vals[1].Real,      precision: 15);
            Assert.Equal(-10.0, vals[1].Imaginary, precision: 15);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── Gate 4: ToSnp reads Z0 cube ─────────────────────────────────────────────

    [Fact]
    public void ToSnp_UniformZ0Cube_SetsSnpZ0()
    {
        // Uniform real 75 Ω
        var ds  = MakeZ0DataSet([new Complex(75, 0), new Complex(75, 0)]);
        var snp = DataSetBuilder.ToSnp(ds);
        Assert.Equal(75.0, snp.Z0.Real,      precision: 12);
        Assert.Equal(0.0,  snp.Z0.Imaginary, precision: 12);
    }

    [Fact]
    public void ToSnp_UniformComplexZ0Cube_SetsSnpZ0()
    {
        var ds  = MakeZ0DataSet([new Complex(75, -10), new Complex(75, -10)]);
        var snp = DataSetBuilder.ToSnp(ds);
        Assert.Equal(75.0,  snp.Z0.Real,      precision: 12);
        Assert.Equal(-10.0, snp.Z0.Imaginary, precision: 12);
    }

    [Fact]
    public void ToSnp_AbsentZ0Cube_Fallback50Ohm()
    {
        // Build a DataSet with no Z0 cube (legacy path)
        var snpSrc = MakeUniformSnp(nPorts: 2);
        var dsSrc  = DataSetBuilder.FromSnp(snpSrc);
        // Manually remove Z0 to simulate a legacy import — rebuild without it
        var dsNoZ0 = new DataSet();
        dsNoZ0.Add("S", dsSrc["S"]);

        var snp = DataSetBuilder.ToSnp(dsNoZ0);
        Assert.Equal(50.0, snp.Z0.Real,      precision: 12);
        Assert.Equal(0.0,  snp.Z0.Imaginary, precision: 12);
    }

    [Fact]
    public void ToSnp_NonUniformZ0_UsesPort1AndFiresWarning()
    {
        var ds = MakeZ0DataSet([new Complex(50, 0), new Complex(75, -10)]);

        string? warned = null;
        Action<string> handler = w => warned = w;
        RFNetwork.OnWarning += handler;
        try
        {
            var snp = DataSetBuilder.ToSnp(ds);
            // SNP.Z0 = port-1 value
            Assert.Equal(50.0, snp.Z0.Real,      precision: 12);
            Assert.Equal(0.0,  snp.Z0.Imaginary, precision: 12);
            // Warning was fired
            Assert.NotNull(warned);
            Assert.Contains("non-uniform", warned, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            RFNetwork.OnWarning -= handler;
        }
    }

    // ── Gate 5: ClassifyZ0 ───────────────────────────────────────────────────────

    [Fact]
    public void ClassifyZ0_UniformReal_ReturnsUniformReal()
    {
        var cube = DataSetBuilder.BuildZ0Cube([new Complex(50, 0), new Complex(50, 0)]);
        Assert.Equal(Z0Kind.UniformReal, DataSetBuilder.ClassifyZ0(cube));
    }

    [Fact]
    public void ClassifyZ0_UniformComplex_ReturnsUniformComplex()
    {
        var cube = DataSetBuilder.BuildZ0Cube([new Complex(75, -10), new Complex(75, -10)]);
        Assert.Equal(Z0Kind.UniformComplex, DataSetBuilder.ClassifyZ0(cube));
    }

    [Fact]
    public void ClassifyZ0_NonUniform_ReturnsNonUniform()
    {
        var cube = DataSetBuilder.BuildZ0Cube([new Complex(50, 0), new Complex(75, -10)]);
        Assert.Equal(Z0Kind.NonUniform, DataSetBuilder.ClassifyZ0(cube));
    }

    [Fact]
    public void ClassifyZ0_SinglePort_UniformReal()
    {
        var cube = DataSetBuilder.BuildZ0Cube([new Complex(50, 0)]);
        Assert.Equal(Z0Kind.UniformReal, DataSetBuilder.ClassifyZ0(cube));
    }

    [Fact]
    public void ClassifyZ0_NearlyEqualWithinTolerance_TreatedAsUniform()
    {
        // Difference of 1e-12 — well within 1e-9 tolerance
        var cube = DataSetBuilder.BuildZ0Cube([new Complex(50, 0), new Complex(50 + 1e-12, 0)]);
        Assert.Equal(Z0Kind.UniformReal, DataSetBuilder.ClassifyZ0(cube));
    }

    [Fact]
    public void ClassifyZ0_TinyImagPart_BeyondTolerance_UniformComplex()
    {
        // 50 + j·1e-8 — imaginary exceeds 1e-9 tolerance
        var cube = DataSetBuilder.BuildZ0Cube([new Complex(50, 1e-8), new Complex(50, 1e-8)]);
        Assert.Equal(Z0Kind.UniformComplex, DataSetBuilder.ClassifyZ0(cube));
    }
}
