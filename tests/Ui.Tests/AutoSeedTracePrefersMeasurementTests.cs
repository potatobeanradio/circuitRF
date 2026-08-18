using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// <b>The trace a run seeds for you should be one you can read</b> (owner, 2026-08-18): an HB run's
/// first cube is <c>V</c>, indexed by node AND harmonic, so an auto-created Data Display opened on
/// "the voltage at some node at some harmonic" — for a schematic whose whole point is a page of
/// measurement expressions.
///
/// <para><b>The discriminator is the GROUP, and that is the finding worth keeping.</b> A run's
/// measurement cubes are filed under <see cref="DataSet.MeasurementsGroup"/> by
/// <c>SchematicRunService</c> and survive the <c>.npy</c> there — verified against a real exported run
/// (`Cli hb` over a parametric sweep), not assumed from the code. Nothing has to be inferred from cube
/// names or axis shapes, which is what a heuristic here would otherwise have had to do.</para>
///
/// <para>These build the dataset shape that run produced — <c>V [Pin × node × harmonic]</c> plus a
/// measurements group — export it through the real writer, and seed a plot exactly as
/// <c>WorkspaceViewModel.AutoOpenOrCreateDataDisplayAsync</c> does.</para>
/// </summary>
public sealed class AutoSeedTracePrefersMeasurementTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    private string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"crf_seedtrace_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
            try { Directory.Delete(dir, recursive: true); } catch { }
    }

    /// <summary>The swept-HB shape a real run writes: raw cubes in the default group, measurements in
    /// theirs. Axis names and rank match the exported `.npy` of a `parametric_sweep` over Pin.</summary>
    private static DataSet SweptHbDataSet(bool withRealMeasurement, bool withComplexMeasurement)
    {
        var pin      = new Axis("Pin",      new[] { -10.0, -8, -6, -4, -2, 0 }, "dBm");
        var node     = new Axis("node",     new[] { 0.0, 1, 2 }, "", new[] { "Vin", "Vout", "n1" });
        var harmonic = new Axis("harmonic", new[] { 0.0, 1, 2 }, "");

        var v = new Complex[6 * 3 * 3];
        for (int i = 0; i < v.Length; i++) v[i] = new Complex(0.01 * i, 0);

        var ds = new DataSet();
        ds.Add("V", new DataCube(new[] { pin, node, harmonic }, v));
        ds.Add("Converged", new DataCube(new[] { pin }, Enumerable.Repeat(1.0, 6).ToArray()));

        // Declaration order matters: Pin_avail_dBm is the designer's FIRST measurement, and the first
        // real one is what the seed picks.
        if (withComplexMeasurement)
            ds.AddToGroup(DataSet.MeasurementsGroup, "Zin",
                          new DataCube(new[] { pin }, Enumerable.Range(0, 6).Select(i => new Complex(50 + i, 5)).ToArray()));
        if (withRealMeasurement)
            ds.AddToGroup(DataSet.MeasurementsGroup, "Pin_avail_dBm",
                          new DataCube(new[] { pin }, new[] { -10.0, -8, -6, -4, -2, 0 }));

        return ds;
    }

    private static string Export(string dir, string fileName, DataSet ds)
    {
        var path = Path.Combine(dir, fileName);
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        return path;
    }

    /// <summary>Seeds a plot the way the auto-create flow does, and returns its single trace.</summary>
    private static Trace? SeedOneTrace(string npyPath)
    {
        var vm  = new DataDisplayDocumentViewModel();
        var lib = vm.Window.DataSourceLibrary;
        lib.ResultsRootProvider = () => Path.GetDirectoryName(npyPath)!;
        lib.RefreshAvailableDataSources();
        lib.SelectDataSourceAsync(Path.GetFileName(npyPath)).GetAwaiter().GetResult();

        var container = vm.Window.DataDisplay!.Plots.First();
        container.Inspector.PlotType = PlotType.Rect;
        if (!container.Inspector.AddTraceCommand.CanExecute(null)) return null;
        container.Inspector.AddTraceCommand.Execute(null);

        return container.Inspector.Traces.FirstOrDefault()?.Trace;
    }

    /// <summary>
    /// The measurement wins over the raw <c>V</c> cube that precedes it — and it renders: six points,
    /// X running over the swept drive. A seed that named the right cube but resolved to nothing would
    /// be worse than the old behaviour, so the points are part of the assertion.
    /// </summary>
    [Fact]
    public void ASweptHbRun_SeedsItsFirstRealMeasurement_NotTheNodeVoltage()
    {
        var dir  = MakeTempDir();
        var path = Export(dir, "FetHbSweep.npy", SweptHbDataSet(withRealMeasurement: true, withComplexMeasurement: true));

        var trace = SeedOneTrace(path);

        Assert.NotNull(trace);
        // BARE, not "measurements.Pin_avail_dBm" — a measurements-group cube bare-resolves, and the
        // picker and the expression parser already emit these bare, so a seeded trace has to read the
        // same as a typed or picked one (owner: the prefix is noise the user never needs to type).
        Assert.Equal("Pin_avail_dBm", trace!.CubeName);
        Assert.Equal("Pin_avail_dBm", trace.Expression);
        Assert.Equal(6, trace.Points.Count);
        Assert.Equal(-10.0, trace.Points[0].X, 6);
        Assert.Equal(0.0,   trace.Points[^1].X, 6);
    }

    /// <summary>
    /// With only a COMPLEX measurement, that is still better than a node voltage — but it is second
    /// choice, because a complex cube needs a transform picked before it renders as anything.
    /// </summary>
    [Fact]
    public void AComplexMeasurement_IsStillPreferredOverTheRawCube()
    {
        var dir  = MakeTempDir();
        var path = Export(dir, "ComplexOnly.npy", SweptHbDataSet(withRealMeasurement: false, withComplexMeasurement: true));

        var trace = SeedOneTrace(path);

        Assert.NotNull(trace);
        Assert.Equal("Zin", trace!.CubeName);
    }

    /// <summary>
    /// A run with NO measurements is untouched — it still seeds the first plottable cube, which is what
    /// every DC run, bare HB and imported dataset has always done. The preference adds a case; it does
    /// not replace the rule.
    /// </summary>
    [Fact]
    public void ARunWithNoMeasurements_SeedsWhatItAlwaysDid()
    {
        var dir  = MakeTempDir();
        var path = Export(dir, "BareHb.npy", SweptHbDataSet(withRealMeasurement: false, withComplexMeasurement: false));

        var trace = SeedOneTrace(path);

        Assert.NotNull(trace);
        Assert.Equal("V", trace!.CubeName);
    }
}
