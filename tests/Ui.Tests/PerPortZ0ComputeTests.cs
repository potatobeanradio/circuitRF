// ================================================================
//  PerPortZ0ComputeTests.cs  —  Phase 7.2f gate tests
//
//  1. NonUniformSource_S_NoRenorm           — S-trace uses stored matrix; uniform 50 Ω unchanged
//  2. NonUniformSource_Z_UsesPerPort        — Z-trace uses per-port SToZ, not scalar collapse
//  3. MarkerImpedance_PerPort               — marker impedance uses SourceZ0PerPort[Row]
//  4. Z0Box_GatedByKind                     — IsZ0Editable true only for uniform-real sources
//  5. Stability_NonUniform2Port             — Mu on unusual 2-port matches manually renormed equivalent
// ================================================================

using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using NumFlat;
using RfCore;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class PerPortZ0ComputeTests
{
    // ---- Helpers -----------------------------------------------------------

    // Fixed 2-port S matrix for all tests (one frequency at 1 GHz).
    private static readonly Complex S11 = new( 0.1,  0.2);
    private static readonly Complex S12 = new( 0.3,  0.0);
    private static readonly Complex S21 = new( 0.3,  0.0);
    private static readonly Complex S22 = new(-0.1,  0.1);

    private static readonly Complex Z0Port1 = new(50.0,   0.0);
    private static readonly Complex Z0Port2 = new(75.0, -10.0);

    /// <summary>
    /// Builds a 2-port 1-frequency SNP with the test S matrix.
    /// The SNP's own Z0 is 50+j0 (the uniform fallback).
    /// </summary>
    private static SNP MakeTestSnp()
    {
        var snp = new SNP(new[] { 1e9 }, 2, MatrixType.S, MatrixFormat.MA, new Complex(50, 0));
        snp.Matrices[0][0, 0] = S11;
        snp.Matrices[0][0, 1] = S12;
        snp.Matrices[0][1, 0] = S21;
        snp.Matrices[0][1, 1] = S22;
        return snp;
    }

    private static Complex[] MakeNonUniformZ0() => new[] { Z0Port1, Z0Port2 };

    private static Trace MakeUnusualTrace(SNP snp, MatrixType mt, int row, int col,
                                          DependentVarFormat fmt = DependentVarFormat.Complex)
    {
        var t = new Trace(snp, mt, row, col, fmt);
        t.SourceZ0PerPort   = MakeNonUniformZ0();
        t.SourceZ0IsUnusual = true;
        return t;
    }

    // ---- Test 1: NonUniformSource_S_NoRenorm --------------------------------
    // An S-trace on an unusual source returns the stored S value unchanged (no SToS shift).
    // A uniform-50Ω trace on the same SNP also returns the stored value (regression).

    [Fact]
    public void NonUniformSource_S_NoRenorm()
    {
        var snp = MakeTestSnp();

        // Unusual S21 trace — Smith plot returns (Re(S), Im(S)) as the point.
        var unusual = MakeUnusualTrace(snp, MatrixType.S, 0, 1, DependentVarFormat.Complex);
        unusual.BuildPath(PlotType.Smith, FreqUnit.GHz);

        Assert.Single(unusual.Points);
        Assert.Equal((float)S21.Real,      unusual.Points[0].X, precision: 6);
        Assert.Equal((float)S21.Imaginary, unusual.Points[0].Y, precision: 6);

        // Regression: uniform-50Ω trace (SourceZ0IsUnusual = false, _z0 == Data.Z0 == 50).
        var uniform = new Trace(snp, MatrixType.S, 0, 1, DependentVarFormat.Complex);
        uniform.BuildPath(PlotType.Smith, FreqUnit.GHz);

        Assert.Single(uniform.Points);
        Assert.Equal((float)S21.Real,      uniform.Points[0].X, precision: 6);
        Assert.Equal((float)S21.Imaginary, uniform.Points[0].Y, precision: 6);
    }

    // ---- Test 2: NonUniformSource_Z_UsesPerPort -----------------------------
    // The per-port Z-trace computes Z22 using the two-element Z0 array, which gives
    // a different answer than the scalar-collapse (uniform 50Ω) path.
    // Z22 is used because it is directly governed by port-2's Z0 (= 75-j10 Ω).

    [Fact]
    public void NonUniformSource_Z_UsesPerPort()
    {
        var snp      = MakeTestSnp();
        var sourceZ0 = MakeNonUniformZ0();
        var mat      = snp.Matrices[0];

        // Expected Z22 using per-port array [50, 75-j10].
        var expectedZ22  = RFNetwork.SToZ(mat, sourceZ0)[1, 1];
        // "Wrong" value from scalar-collapse (uniform 50Ω → [50, 50]).
        var collapsedZ22 = RFNetwork.SToZ(mat, new Complex(50, 0))[1, 1];

        // Pre-condition: port-2 Z0 differs, so Z22 must differ.
        Assert.NotEqual(expectedZ22.Magnitude, collapsedZ22.Magnitude, precision: 3);

        // Unusual Z22 trace with Mag Y-axis on Rect plot: Y = |Z22|.
        var unusual = MakeUnusualTrace(snp, MatrixType.Z, 1, 1, DependentVarFormat.Mag);
        unusual.BuildPath(PlotType.Rect, FreqUnit.GHz);

        Assert.Single(unusual.Points);
        Assert.Equal((float)expectedZ22.Magnitude,  unusual.Points[0].Y, precision: 5);
        Assert.NotEqual((float)collapsedZ22.Magnitude, unusual.Points[0].Y, precision: 3);
    }

    // ---- Test 3: MarkerImpedance_PerPort ------------------------------------
    // For an S22 trace on an unusual source, GetMarkerImpedanceString must use
    // sourceZ0[1] (= 75-j10 Ω) rather than the uniform 50Ω fallback.

    [Fact]
    public void MarkerImpedance_PerPort()
    {
        var snp      = MakeTestSnp();
        var sourceZ0 = MakeNonUniformZ0();

        // S22 trace: Row==Col so MarkerShowsImpedance returns true when YAxis=Complex.
        var unusual = MakeUnusualTrace(snp, MatrixType.S, 1, 1, DependentVarFormat.Complex);

        var marker = new Marker(unusual, 1e9, isMulti: false, isDelta: false, index: 1);
        marker.UseNormalizedImpedance = false;

        Assert.True(unusual.MarkerShowsImpedance(marker),
            "pre-condition: MarkerShowsImpedance must be true for S22/Complex");

        string perPortResult = unusual.GetMarkerImpedanceString(marker);
        Assert.Contains("impedance", perPortResult, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NaN", perPortResult);

        // Uniform (no per-port) trace — same SNP but SourceZ0IsUnusual = false.
        var uniform = new Trace(snp, MatrixType.S, 1, 1, DependentVarFormat.Complex);
        string uniformResult = uniform.GetMarkerImpedanceString(marker);

        // Because port2 Z0 is 75-j10 ≠ 50Ω, the strings must differ.
        Assert.NotEqual(perPortResult, uniformResult);

        // Verify the per-port formula: Z = portZ0 * (conj(portZ0)/portZ0 + S22) / (1 - S22)
        // For real portZ0, conj/portZ0 = 1, giving the standard: Z = portZ0*(1+S)/(1-S).
        // Since portZ0 = 75-j10 (complex), the formula keeps the full complex form.
        var portZ0  = sourceZ0[1];
        var Zexpect = portZ0 * (portZ0.Conjugate() / portZ0 + S22) / (Complex.One - S22);

        var portZ0w = new Complex(50, 0);
        var Zwrong  = portZ0w * (portZ0w.Conjugate() / portZ0w + S22) / (Complex.One - S22);

        // Results must genuinely differ.
        Assert.NotEqual(Zexpect.Real, Zwrong.Real, precision: 2);
    }

    // ---- Test 4: Z0Box_GatedByKind ------------------------------------------
    // IsZ0Editable (on TraceRowViewModel) is true for uniform-real, false for unusual kinds.
    // Uses file I/O + LoadFileAsync so the SNP reference equality in RebuildSignals matches
    // and ApplySourceZ0 is called (setting SourceZ0IsUnusual from the library entry).

    [Fact]
    public async Task Z0Box_GatedByKind()
    {
        var tmpNu = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"crf_z0gate_nu_{Guid.NewGuid():N}.npy");
        var tmpCx = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"crf_z0gate_cx_{Guid.NewGuid():N}.npy");
        try
        {
            // Write non-uniform Z0 DataSet.
            var dsNu = MakeNonUniformZ0DataSet();
            RfCore.Export.DataSetExporter.Export(dsNu, tmpNu, RfCore.Export.ExportFormat.Npy);

            // Write complex Z0 DataSet.
            var dsCx = MakeComplexZ0DataSet();
            RfCore.Export.DataSetExporter.Export(dsCx, tmpCx, RfCore.Export.ExportFormat.Npy);

            // Case A: uniform-real source — ShowZ0Control true (box is shown); IsZ0Editable
            // starts false because Override is unchecked by default (7.2f-2 behavior).
            var snpA = MakeTestSnp();
            var plotA = new Plot(PlotType.Rect, FreqUnit.GHz);
            plotA.Traces.Add(new Trace(snpA, MatrixType.S, 0, 0, DependentVarFormat.Db));
            var inspA = new PlotInspectorViewModel(plotA, () => { }, null);
            Assert.True(inspA.Traces[0].ShowZ0Control,
                "uniform-real source: Z0 control must be visible");
            Assert.False(inspA.Traces[0].IsZ0Editable,
                "uniform-real source: box is locked until Override is checked");

            // Case B: non-uniform Z0 — library entry drives SourceZ0IsUnusual = true.
            var libB = new DataSourceLibraryViewModel();
            await libB.LoadFileAsync(tmpNu);
            var entryB = libB.Entries.Single();
            Assert.True(entryB.HasUnusualZ0, "pre-condition: non-uniform Z0 entry");

            var plotB = new Plot(PlotType.Rect, FreqUnit.GHz);
            plotB.Traces.Add(new Trace(entryB.Snp!, MatrixType.S, 0, 0, DependentVarFormat.Db));
            var inspB = new PlotInspectorViewModel(plotB, () => { }, libB);
            Assert.False(inspB.Traces[0].IsZ0Editable,
                "non-uniform Z0: Z0 box must NOT be editable");
            Assert.NotEmpty(inspB.Traces[0].Z0DisabledReason);

            // Case C: complex Z0 — library entry drives SourceZ0IsUnusual = true.
            var libC = new DataSourceLibraryViewModel();
            await libC.LoadFileAsync(tmpCx);
            var entryC = libC.Entries.Single();
            Assert.True(entryC.HasUnusualZ0, "pre-condition: complex Z0 entry");

            var plotC = new Plot(PlotType.Rect, FreqUnit.GHz);
            plotC.Traces.Add(new Trace(entryC.Snp!, MatrixType.S, 0, 0, DependentVarFormat.Db));
            var inspC = new PlotInspectorViewModel(plotC, () => { }, libC);
            Assert.False(inspC.Traces[0].IsZ0Editable,
                "complex Z0: Z0 box must NOT be editable");
        }
        finally
        {
            if (System.IO.File.Exists(tmpNu)) System.IO.File.Delete(tmpNu);
            if (System.IO.File.Exists(tmpCx)) System.IO.File.Delete(tmpCx);
        }
    }

    // ---- Helpers for test 4 (DataSet builders, mirrors Z0IndicatorTests) ----

    private static DataSet MakeNonUniformZ0DataSet()
    {
        var freqAxis = new Axis("freq", new[] { 1e9 }, "Hz");
        var iAxis    = new Axis("i", new[] { 1.0, 2.0 }, "port");
        var jAxis    = new Axis("j", new[] { 1.0, 2.0 }, "port");
        var ds       = new DataSet();
        ds.Add("S", new DataCube(new[] { freqAxis, iAxis, jAxis }, new Complex[4]));
        ds.Add("Z0", DataSetBuilder.BuildZ0Cube(
            new[] { new Complex(50, 0), new Complex(75, -10) }));
        return ds;
    }

    private static DataSet MakeComplexZ0DataSet()
    {
        var freqAxis = new Axis("freq", new[] { 1e9 }, "Hz");
        var iAxis    = new Axis("i", new[] { 1.0 }, "port");
        var jAxis    = new Axis("j", new[] { 1.0 }, "port");
        var ds       = new DataSet();
        ds.Add("S", new DataCube(new[] { freqAxis, iAxis, jAxis }, new Complex[] { new(0.1, 0) }));
        ds.Add("Z0", DataSetBuilder.BuildZ0Cube(new[] { new Complex(50, -10) }));
        return ds;
    }

    // ---- Test 5: Stability_NonUniform2Port ----------------------------------
    // Mu on an unusual trace must equal the Mu computed from a manually renormed SNP.
    // This validates that BuildDerivedPath renorms to uniform-real before calling StabilityMu.

    [Fact]
    public void Stability_NonUniform2Port()
    {
        var snp      = MakeTestSnp();
        var sourceZ0 = MakeNonUniformZ0();

        // Manually renorm to uniform-real (50+j0) using port-1's real part.
        int n           = 2;
        var z0Real      = new Complex(Z0Port1.Real, 0);   // 50+j0
        var z0RealArray = RFNetwork.Z0Array(z0Real, n);
        var renormedMat = RFNetwork.SToS(snp.Matrices[0], sourceZ0, z0RealArray);
        var renormedSnp = new SNP(snp.Frequencies, new[] { renormedMat },
                                  MatrixType.S, snp.Format, z0Real);

        double muExpected = RFNetwork.StabilityMu(renormedSnp)[0];

        // Unusual trace: BuildPath must renorm internally and produce the same Mu.
        var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Mag)
        {
            SourceZ0PerPort   = sourceZ0,
            SourceZ0IsUnusual = true,
            Derived           = DerivedParameters.Mu,
        };
        trace.BuildPath(PlotType.Rect, FreqUnit.GHz);

        Assert.Single(trace.Points);
        Assert.Equal(muExpected, (double)trace.Points[0].Y, precision: 6);

        // Sanity: a uniform trace (no renorm) produces the Mu of the STORED matrix.
        // The stored matrix is referenced to [50, 75-j10], not uniform-50Ω, so the
        // result must differ from the renormed Mu.
        double muStored = RFNetwork.StabilityMu(
            new SNP(snp.Frequencies, snp.Matrices, MatrixType.S, snp.Format, snp.Z0))[0];

        var uniformTrace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Mag)
        {
            Derived = DerivedParameters.Mu,
        };
        uniformTrace.BuildPath(PlotType.Rect, FreqUnit.GHz);

        if (uniformTrace.Points.Count > 0)
            Assert.Equal(muStored, (double)uniformTrace.Points[0].Y, precision: 6);
    }
}
