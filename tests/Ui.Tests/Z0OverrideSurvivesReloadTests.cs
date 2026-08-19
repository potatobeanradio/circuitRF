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

/// <summary>
/// <b>The Z0 override must survive a workspace close and reopen</b> (owner, 2026-08-18: <i>"the Z0
/// override is not respected when closing and reopening a workspace. I suspect the .cdd file is not
/// persisting the override or its value."</i>).
///
/// <para><b>The <c>.cdd</c> persists it correctly, and always did</b> — <c>TraceConfig.Z0Override</c>
/// and <c>TraceConfig.Z0</c> are both written and both read back. What destroyed it was the RESTORE
/// ORDER: the config is applied first, and then the trace card rebuilds its signal list against the
/// freshly-populated library, and that rebuild called <c>ApplySourceZ0</c> — which unconditionally
/// clears the override checkbox and reseeds the Z0 box from the source. So a correct value was
/// loaded and then thrown away a moment later, which is exactly why it looks like a persistence
/// bug.</para>
/// </summary>
public sealed class Z0OverrideSurvivesReloadTests
{
    private static DataSet MakeTwoPortDataSet(double z0 = 50)
    {
        var freqAxis = new Axis("freq", new[] { 1e9, 2e9 }, "Hz");
        var iAxis    = new Axis("i", new[] { 1.0, 2.0 }, "port");
        var jAxis    = new Axis("j", new[] { 1.0, 2.0 }, "port");

        var s = new Complex[2 * 2 * 2];
        for (int f = 0; f < 2; f++)
        {
            s[f * 4 + 0] = new Complex(0.2, 0.1);
            s[f * 4 + 1] = new Complex(0.7, -0.2);
            s[f * 4 + 2] = new Complex(0.7, -0.2);
            s[f * 4 + 3] = new Complex(0.15, 0.05);
        }

        var ds = new DataSet();
        ds.Add("S", new DataCube([freqAxis, iAxis, jAxis], s));
        ds.Add("Z0", DataSetBuilder.BuildZ0Cube([new Complex(z0, 0), new Complex(z0, 0)]));
        return ds;
    }

    /// <summary>
    /// <b>The reproduction.</b> A library rebuild — which is what a workspace open triggers once the
    /// data sources finish loading — must not clear an override the restore had just put in place.
    /// </summary>
    [Fact]
    public async Task ALibraryRebuild_DoesNotClearTheUsersZ0Override()
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"crf_z0reload_{Guid.NewGuid():N}.npy");
        try
        {
            RfCore.Export.DataSetExporter.Export(MakeTwoPortDataSet(), path,
                                                 RfCore.Export.ExportFormat.Npy);

            var lib = new DataSourceLibraryViewModel();
            await lib.LoadFileAsync(path);
            await lib.SelectDataSourceAsync(path);

            var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
            plot.Traces.Add(new Trace(lib.Entries.Single().Snp!, MatrixType.S, 0, 0, DependentVarFormat.Db));

            var inspector = new PlotInspectorViewModel(plot, () => { }, lib);
            var row = inspector.Traces[0];

            // What restoring a .cdd leaves behind: the override on, at a value that is NOT the
            // source's own reference.
            row.Z0OverrideEnabled = true;
            row.Z0String = "75";

            Assert.True(row.Z0OverrideEnabled, "pre-condition: the override is on");
            Assert.Equal(75.0, row.Trace.Z0.Real, 6);

            // Now the library changes in place — auto-refresh, or the sources finishing their load
            // during a workspace open. This is the moment the override used to vanish.
            await lib.ReloadAsync(lib.Entries.Single());

            Assert.True(row.Z0OverrideEnabled,
                "A library refresh cleared the Z0 override. It is the user's setting, not the " +
                "source's — only an explicit source change may reset it.");
            Assert.Equal(75.0, row.Trace.Z0.Real, 6);
            Assert.Contains("75", row.Z0String);
        }
        finally
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
    }

    /// <summary>
    /// With NO override in force a library refresh must still reseed the box from the source — that
    /// is what the refresh is for, and turning it off entirely would leave a stale reference showing
    /// after a file is re-read at a different Z0.
    /// </summary>
    [Fact]
    public async Task ALibraryRebuild_StillReseedsWhenThereIsNoOverride()
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"crf_z0reseed_{Guid.NewGuid():N}.npy");
        try
        {
            RfCore.Export.DataSetExporter.Export(MakeTwoPortDataSet(50), path,
                                                 RfCore.Export.ExportFormat.Npy);

            var lib = new DataSourceLibraryViewModel();
            await lib.LoadFileAsync(path);
            await lib.SelectDataSourceAsync(path);

            var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
            plot.Traces.Add(new Trace(lib.Entries.Single().Snp!, MatrixType.S, 0, 0, DependentVarFormat.Db));

            var row = new PlotInspectorViewModel(plot, () => { }, lib).Traces[0];
            Assert.False(row.Z0OverrideEnabled, "pre-condition: no override");

            // The file is re-written at a different reference and re-read in place.
            RfCore.Export.DataSetExporter.Export(MakeTwoPortDataSet(75), path,
                                                 RfCore.Export.ExportFormat.Npy);
            await lib.ReloadAsync(lib.Entries.Single());

            Assert.False(row.Z0OverrideEnabled);
            Assert.Contains("75", row.Z0String);
        }
        finally
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
    }

    /// <summary>
    /// The <c>.cdd</c> half, asserted separately so a future regression is attributed to the right
    /// side: both the flag and the value round-trip through the config.
    /// </summary>
    [Fact]
    public void TheConfigRoundTripsBothTheFlagAndTheValue()
    {
        var snp = new SNP([1e9, 2e9], 2, MatrixType.S, MatrixFormat.MA, new Complex(50, 0));
        var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            Z0OverrideEnabled = true,
            Z0 = new Complex(75, -5),
        };

        // The two fields the .cdd carries, written and read exactly as DataDisplayViewModel does.
        string z0Text = ComplexStringHelper.Format(trace.Z0);
        bool overrideFlag = trace.Z0OverrideEnabled;

        Assert.True(overrideFlag);
        Assert.Contains("75", z0Text);

        var restored = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);
        if (ComplexStringHelper.TryParse(z0Text, out var z0)) restored.Z0 = z0;
        restored.Z0OverrideEnabled = overrideFlag;

        Assert.True(restored.Z0OverrideEnabled);
        Assert.Equal(75.0, restored.Z0.Real, 6);
        Assert.Equal(-5.0, restored.Z0.Imaginary, 6);
    }
}
