// ================================================================
//  Z0OverrideTests.cs  —  Phase 7.2f-2 gate tests
//
//  1. UniformSource_BoxLockedShowsPort1      — ShowZ0Control true, box seeded from port-1
//  2. Override_EnablesEditing               — Override checkbox unlocks box + reverts on uncheck
//  3. UniformComplex_TreatedAsUniform       — complex-but-uniform shows box, IsMultiPortNorm false
//  4. NonUniform_ShowsControlBoxWithBadge   — non-uniform shows box (renorm enabled), badge true
//  5. NonScattering_NoControl               — cube-bound + derived/Z/Y traces hide the Z0 control
// ================================================================

using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class Z0OverrideTests
{
    // ---- Helpers -----------------------------------------------------------

    private static SNP MakeUniformRealSnp(double z0 = 50)
    {
        var snp = new SNP(new[] { 1e9, 2e9 }, 2, MatrixType.S, MatrixFormat.MA, new Complex(z0, 0));
        return snp;
    }

    private static DataSet MakeUniformComplexZ0DataSet()
    {
        // 1-port, uniform-complex Z0 (same value on every port but imaginary part ≠ 0).
        var freqAxis = new Axis("freq", new[] { 1e9 }, "Hz");
        var iAxis    = new Axis("i", new[] { 1.0 }, "port");
        var jAxis    = new Axis("j", new[] { 1.0 }, "port");
        var ds       = new DataSet();
        ds.Add("S", new DataCube(new[] { freqAxis, iAxis, jAxis }, new Complex[] { new(0.1, 0) }));
        ds.Add("Z0", DataSetBuilder.BuildZ0Cube(new[] { new Complex(50, -10) }));
        return ds;
    }

    private static DataSet MakeNonUniformZ0DataSet()
    {
        var freqAxis = new Axis("freq", new[] { 1e9 }, "Hz");
        var iAxis    = new Axis("i", new[] { 1.0, 2.0 }, "port");
        var jAxis    = new Axis("j", new[] { 1.0, 2.0 }, "port");
        var ds       = new DataSet();
        ds.Add("S", new DataCube(new[] { freqAxis, iAxis, jAxis }, new Complex[4]));
        ds.Add("Z0", DataSetBuilder.BuildZ0Cube(new[] { new Complex(50, 0), new Complex(75, -10) }));
        return ds;
    }

    // ---- Test 1: UniformSource_BoxLockedShowsPort1 --------------------------
    // Uniform-real 50 Ω source → ShowZ0Control true, IsMultiPortNormalization false,
    // IsZ0Editable false (Override unchecked), Z0String shows the port-1 value.

    [Fact]
    public void UniformSource_BoxLockedShowsPort1()
    {
        var snp  = MakeUniformRealSnp(50);
        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
        plot.Traces.Add(new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db));
        var insp = new PlotInspectorViewModel(plot, () => { }, null);
        var row  = insp.Traces[0];

        Assert.True(row.ShowZ0Control,           "uniform-real: Z0 control must be visible");
        Assert.False(row.IsMultiPortNormalization, "uniform-real: must NOT be multi-port");
        Assert.False(row.IsZ0Editable,            "uniform-real: box must be locked (Override off)");
        // Z0String should reflect the SNP's port-1 reference (50+j0).
        Assert.Contains("50", row.Z0String);
    }

    // ---- Test 2: Override_EnablesEditing ------------------------------------
    // Checking Override unlocks the box; unchecking reverts Z0String to source port-1.

    [Fact]
    public async Task Override_EnablesEditing()
    {
        var tmpPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"crf_override_{Guid.NewGuid():N}.npy");
        try
        {
            // Use a plain uniform-real DataSet (non-unusual, so ShowZ0Control is true).
            var snp  = MakeUniformRealSnp(50);
            var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
            plot.Traces.Add(new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db));
            var insp = new PlotInspectorViewModel(plot, () => { }, null);
            var row  = insp.Traces[0];

            // Initially locked.
            Assert.False(row.Z0OverrideEnabled, "Override must start unchecked");
            Assert.False(row.IsZ0Editable);

            // Check Override → box becomes editable.
            row.Z0OverrideEnabled = true;
            Assert.True(row.IsZ0Editable, "Override on: box must be editable");

            // Edit Z0String to a custom value (should drive _trace.Z0 via OnZ0StringChanged).
            row.Z0String = "75";
            Assert.Equal("75", row.Z0String);

            // Uncheck Override → Z0String and _trace.Z0 must revert to the source port-1 value.
            row.Z0OverrideEnabled = false;
            Assert.False(row.IsZ0Editable, "Override off: box must relock");
            // Reverted value must contain "50" (the source port-1 Z0).
            Assert.Contains("50", row.Z0String);
        }
        finally
        {
            if (System.IO.File.Exists(tmpPath)) System.IO.File.Delete(tmpPath);
        }
    }

    // ---- Test 3: UniformComplex_TreatedAsUniform ----------------------------
    // A uniform-complex source (same complex value on every port) is NOT multi-port.
    // ShowZ0Control is true; the box shows the complex value; Override is available.

    [Fact]
    public async Task UniformComplex_TreatedAsUniform()
    {
        var tmpPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"crf_ucx_{Guid.NewGuid():N}.npy");
        try
        {
            var ds = MakeUniformComplexZ0DataSet();
            RfCore.Export.DataSetExporter.Export(ds, tmpPath, RfCore.Export.ExportFormat.Npy);

            var lib = new DataSourceLibraryViewModel();
            await lib.LoadFileAsync(tmpPath);
            await lib.SelectDataSourceAsync(tmpPath);
            var entry = lib.Entries.Single();

            Assert.Equal(Z0Kind.UniformComplex, entry.Z0Kind);
            Assert.True(entry.HasUnusualZ0, "pre-condition: UniformComplex IS unusual");

            var snp  = entry.Snp!;
            var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
            plot.Traces.Add(new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db));
            var insp = new PlotInspectorViewModel(plot, () => { }, lib);
            var row  = insp.Traces[0];

            Assert.True(row.ShowZ0Control,
                "UniformComplex: box must be shown (not multi-port)");
            Assert.False(row.IsMultiPortNormalization,
                "UniformComplex: must NOT trigger multi-port label");
            // Override is off by default so box is read-only.
            Assert.False(row.IsZ0Editable);
        }
        finally
        {
            if (System.IO.File.Exists(tmpPath)) System.IO.File.Delete(tmpPath);
        }
    }

    // ---- Test 4: NonUniform_ShowsControlBoxWithBadge ------------------------
    // brief-dd-z0-renormalization.md §2: a non-uniform source no longer hides the Z0 box —
    // IsMultiPortNormalization is now purely a "source was unusual" badge signal;
    // ShowZ0Control/renorm is enabled the same as any other source. Box starts locked
    // (Override unchecked), same as every other case.

    [Fact]
    public async Task NonUniform_ShowsControlBoxWithBadge()
    {
        var tmpPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"crf_nu_{Guid.NewGuid():N}.npy");
        try
        {
            var ds = MakeNonUniformZ0DataSet();
            RfCore.Export.DataSetExporter.Export(ds, tmpPath, RfCore.Export.ExportFormat.Npy);

            var lib = new DataSourceLibraryViewModel();
            await lib.LoadFileAsync(tmpPath);
            await lib.SelectDataSourceAsync(tmpPath);
            var entry = lib.Entries.Single();

            Assert.Equal(Z0Kind.NonUniform, entry.Z0Kind);

            var snp  = entry.Snp!;
            var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
            plot.Traces.Add(new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db));
            var insp = new PlotInspectorViewModel(plot, () => { }, lib);
            var row  = insp.Traces[0];

            Assert.True(row.IsMultiPortNormalization,
                "NonUniform: IsMultiPortNormalization (badge condition) must be true");
            Assert.True(row.ShowZ0Control,
                "NonUniform: Z0 control must now be shown (renorm enabled per §2)");
            Assert.False(row.IsZ0Editable,
                "NonUniform: Z0 box starts locked (Override off by default)");
        }
        finally
        {
            if (System.IO.File.Exists(tmpPath)) System.IO.File.Delete(tmpPath);
        }
    }

    // ---- Test 5: NonScattering_NoControl ------------------------------------
    // Cube-bound traces and Z/Y or derived network traces must hide the Z0 control.

    [Fact]
    public void NonScattering_NoControl()
    {
        var snp = MakeUniformRealSnp(50);

        // Case A: Z-parameter trace (MatrixType.Z) — ShowZ0Control false.
        {
            var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
            plot.Traces.Add(new Trace(snp, MatrixType.Z, 0, 0, DependentVarFormat.Db));
            var insp = new PlotInspectorViewModel(plot, () => { }, null);
            var row  = insp.Traces[0];
            Assert.False(row.ShowZ0Control, "Z-trace must hide Z0 control");
            Assert.False(row.IsMultiPortNormalization, "Z-trace: not multi-port");
        }

        // Case B: derived stability-circle trace — ShowZ0Control false.
        {
            var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
            var t = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db)
                { Derived = DerivedParameters.Mu };
            plot.Traces.Add(t);
            var insp = new PlotInspectorViewModel(plot, () => { }, null);
            var row  = insp.Traces[0];
            Assert.False(row.ShowZ0Control, "derived (Mu) trace must hide Z0 control");
        }
    }
}
