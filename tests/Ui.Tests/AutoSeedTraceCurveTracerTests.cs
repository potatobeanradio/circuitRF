using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// <b>A curve tracer auto-opens as a curve tracer</b> (owner, 2026-08-18): a DC analysis swept over VDS
/// and then VGS, with a placed current probe, should seed <c>I[~, :, "IDS"]</c> — the family of drain
/// curves — not one arbitrary node voltage at one arbitrary bias.
///
/// <para><b>The dataset shape below is transcribed from a real run</b>, not invented: the shipped
/// <c>FET_Curve_Tracer</c> nesting (DC1 ← sweep VDS ← sweep VGS) driven through
/// <c>ParametricSweepEngine</c> produces
/// <c>I [VGS:5 × VDS:6 × branch:1(IDS)]</c>, <c>V [VGS × VDS × node(…)]</c> and
/// <c>__ProbeBranches [probe:1(IDS)]</c>. Each <c>parametric_sweep</c> nesting level PREPENDS its axis,
/// which is why the OUTERMOST sweep is axis 0 and the innermost is last — the fact the whole slice rule
/// turns on.</para>
/// </summary>
public sealed class AutoSeedTraceCurveTracerTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    private string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"crf_curve_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
            try { Directory.Delete(dir, recursive: true); } catch { }
    }

    /// <param name="gateSteps">Length of the OUTER (VGS) sweep — the family axis.</param>
    /// <param name="withProbe">Whether the run recorded placed probes (<c>__ProbeBranches</c>).</param>
    private static DataSet CurveTracerDataSet(int gateSteps = 5, bool withProbe = true)
    {
        var vgs  = new Axis("VGS", Enumerable.Range(0, gateSteps).Select(i => -4.0 + i).ToArray(), "V");
        var vds  = new Axis("VDS", new[] { 0.0, 2, 4, 6, 8, 10 }, "V");
        var node = new Axis("node", new[] { 0.0, 1, 2 }, "V", new[] { "n_g", "n_dd", "n_d" });
        // A DC run's branch axis IS the probe list — that is what distinguishes it from an HB run's,
        // which enumerates every device branch.
        var branch = new Axis("branch", new[] { 0.0 }, "A", new[] { "IDS" });

        var ds = new DataSet();
        ds.Add("V", new DataCube(new[] { vgs, vds, node }, new double[gateSteps * 6 * 3]));
        ds.Add("Converged", new DataCube(new[] { vgs, vds }, Enumerable.Repeat(1.0, gateSteps * 6).ToArray()));

        var ids = new double[gateSteps * 6];
        for (int g = 0; g < gateSteps; g++)
        for (int d = 0; d < 6; d++)
            ids[g * 6 + d] = 0.01 * (g + 1) * (d + 1);
        ds.Add("I", new DataCube(new[] { vgs, vds, branch }, ids));

        if (withProbe)
            ds.Add("__ProbeBranches", new DataCube(
                new[] { new Axis("probe", new[] { 0.0 }, "", new[] { "IDS" }) }, new double[1]));

        return ds;
    }

    private static Trace? SeedOneTrace(string dir, string fileName, DataSet ds)
    {
        var path = Path.Combine(dir, fileName);
        DataSetExporter.Export(ds, path, ExportFormat.Npy);

        var vm  = new DataDisplayDocumentViewModel();
        var lib = vm.Window.DataSourceLibrary;
        lib.ResultsRootProvider = () => dir;
        lib.RefreshAvailableDataSources();
        lib.SelectDataSourceAsync(fileName).GetAwaiter().GetResult();

        var container = vm.Window.DataDisplay!.Plots.First();
        container.Inspector.PlotType = PlotType.Rect;
        if (!container.Inspector.AddTraceCommand.CanExecute(null)) return null;
        container.Inspector.AddTraceCommand.Execute(null);

        return container.Inspector.Traces.FirstOrDefault()?.Trace;
    }

    /// <summary>
    /// The whole request in one assertion: the probe current, against the INNER sweep, as a family over
    /// the outer one — and it renders (five curves of six points, not an empty plot).
    /// </summary>
    [Fact]
    public void ATwoDimensionalDcSweep_SeedsTheProbeCurrentAsAFamily()
    {
        var trace = SeedOneTrace(MakeTempDir(), "CurveTracer.npy", CurveTracerDataSet());

        Assert.NotNull(trace);
        Assert.Equal("I", trace!.CubeName);
        Assert.Equal("I[~, :, \"IDS\"]", trace.Expression);

        Assert.True(trace.IsFamily);
        Assert.Equal("VGS", trace.FamilyAxisName);
        Assert.Equal(5, trace.FamilyCurves.Count);                  // one per gate step
        Assert.All(trace.FamilyCurves, c => Assert.Equal(6, c.Points.Count));   // the VDS sweep
        Assert.Equal(-4.0, trace.FamilyCurves[0].AxisValue, 6);

        // X is the INNER sweep and the probe is pinned by NAME, which is what puts "IDS" in the
        // expression instead of a bare index.
        Assert.Equal(AxisRole.KeepAsX,        trace.Slice![1].Role);
        Assert.Equal(AxisRole.FamilyIterate,  trace.Slice![0].Role);
        Assert.Equal("IDS",                   trace.Slice![2].Label);
    }

    /// <summary>
    /// No placed probes, so nothing says the branch axis is anything the designer asked for — the seed
    /// falls back to the first plottable cube exactly as before. This is what keeps the preference from
    /// re-pointing every HB run, whose branch axis enumerates DEVICE branches (<c>M1:g</c>, <c>M1:d</c>)
    /// rather than probes.
    /// </summary>
    [Fact]
    public void WithoutPlacedProbes_TheSeedIsUnchanged()
    {
        var trace = SeedOneTrace(MakeTempDir(), "NoProbe.npy", CurveTracerDataSet(withProbe: false));

        Assert.NotNull(trace);
        Assert.Equal("V", trace!.CubeName);
    }

    /// <summary>
    /// …but the X-axis correction is not conditional on the probe: even seeded on <c>V</c>, a cube with
    /// two swept axes plots against the INNER sweep with the outer as the family. Plotting a 2-D sweep
    /// against its outer variable with the inner pinned at one value is wrong whatever the quantity is.
    /// </summary>
    [Fact]
    public void TheInnerSweepIsTheXAxis_WhateverTheCube()
    {
        var trace = SeedOneTrace(MakeTempDir(), "NoProbe2.npy", CurveTracerDataSet(withProbe: false));

        Assert.NotNull(trace);
        Assert.Equal(AxisRole.FamilyIterate, trace!.Slice![0].Role);   // VGS
        Assert.Equal(AxisRole.KeepAsX,       trace.Slice![1].Role);    // VDS
        Assert.Equal("VGS", trace.FamilyAxisName);
    }

    /// <summary>
    /// A family is capped at <c>Trace.MaxFamilyCurves</c> — the renderer's own guardrail, reused rather
    /// than a second number. The renderer clamps and says so, but a SEEDED trace showing the first 101 of
    /// a 200-point sweep would be claiming to be the whole picture, so past the cap the axis is PINNED
    /// instead — and the corrected X survives, because IDS against VDS at one gate voltage is still the
    /// right pair of axes. The family is one click away in the axis-role editor.
    /// </summary>
    [Fact]
    public void AnOversizedFamilyIsPinnedInstead_ButTheXAxisStaysCorrect()
    {
        var trace = SeedOneTrace(MakeTempDir(), "HugeSweep.npy", CurveTracerDataSet(gateSteps: Trace.MaxFamilyCurves + 1));

        Assert.NotNull(trace);
        Assert.Equal("I", trace!.CubeName);
        Assert.False(trace.IsFamily);
        Assert.Equal(AxisRole.PinToIndex, trace.Slice![0].Role);   // VGS pinned
        Assert.Equal(AxisRole.KeepAsX,    trace.Slice![1].Role);   // still VDS
    }
}
