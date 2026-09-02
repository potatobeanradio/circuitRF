// ================================================================
//  TraceResolveContainmentTests.cs
//  A trace that cannot be resolved says so; it does not end the session.
//
//  Reported twice from the field (Windows, 1.0.0-beta.6 then -beta.7): adding a trace to a second
//  Smith plot after an S-parameter run terminated the application with a bare
//  IndexOutOfRangeException out of DataCube's gather. Every FORESEEN way a resolve can fail —
//  missing source, missing cube, wrong rank, unparseable spec — already ends as "<invalid>" on the
//  trace card, so an unforeseen one has no business being fatal: a curve that cannot be drawn is
//  not worth an unsaved workspace.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Numerics;
using CircuitRF.Ui.Diagnostics;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests;

[Collection(AppDataRootCollection.Name)]
public sealed class TraceResolveContainmentTests
{
    /// <summary>
    /// A DataSet holding a cube whose buffer is shorter than its axes claim. Reflection is the only
    /// way to build one — every constructor validates — which is the point: this reproduces the
    /// state the field crash implies without asserting how it arose.
    /// </summary>
    private static DataSet CorruptedRunDataSet()
    {
        var axes = new[]
        {
            new Axis("freq", new double[] { 1e9, 2e9, 3e9 }, "Hz"),
            new Axis("i",    new double[] { 0 }),
            new Axis("j",    new double[] { 0 }),
        };
        var cube  = new DataCube(axes, new Complex[3]);
        var field = typeof(DataCube).GetField("_complexData",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field!.SetValue(cube, new Complex[1]);

        var ds = new DataSet();
        ds.AddToGroup("SP1", "S", cube);
        ds.AddToGroup("SP1", "Z0", DataSetBuilder.BuildZ0Cube(new[] { new Complex(50, 0) }));
        return ds;
    }

    private static Trace CubeTrace()
    {
        var t = new Trace(new SNP(new double[] { 1e9 }, 1), MatrixType.S, 0, 0,
                          DependentVarFormat.Complex);
        t.CubeName = "SP1.S";
        t.Slice    = new[]
        {
            new AxisSlice("freq", AxisRole.KeepAsX, 0),
            new AxisSlice("i",    AxisRole.PinToIndex, 0),
            new AxisSlice("j",    AxisRole.PinToIndex, 0),
        };
        return t;
    }

    [Fact]
    public void UnresolvableTrace_MarksItselfInvalid_AndDoesNotThrow()
    {
        var t = CubeTrace();

        PlotInspectorViewModel.SetCubeDataFrom(t, CorruptedRunDataSet(),
                                               PlotType.Smith, FreqUnit.GHz);

        Assert.Empty(t.Points);
        Assert.Empty(t.FamilyCurves);
        Assert.Equal("SP1.S", t.InvalidSpecText);
        Assert.NotNull(t.ExpressionError);
    }

    [Fact]
    public void TheFailureIsRecordedInTheCrashTrail_NamingTheCubeAndSlice()
    {
        string root = Path.Combine(Path.GetTempPath(), "crf-trace-trail-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            CrashReporter.ResetForTests();
            AppDataRoot.RedirectTo(root);
            CrashReporter.Install("circuitRF");

            PlotInspectorViewModel.SetCubeDataFrom(CubeTrace(), CorruptedRunDataSet(),
                                                   PlotType.Smith, FreqUnit.GHz);

            string trail = string.Join("\n", Directory
                .GetFiles(Path.Combine(root, CrashReporter.DirName), "session-*.running")
                .Select(f => File.ReadAllText(f)));

            Assert.Contains("trace resolve FAILED", trail);
            Assert.Contains("SP1.S", trail);                 // which cube
            Assert.Contains("freq[3]", trail);               // the shape the source actually holds
            Assert.Contains("freq:KeepAsX", trail);          // and the slice that was asked for

            // The STACK. Every field above was already in range in the reports that followed the
            // first instrumented release, so the throwing line is the only thing left to record and
            // the only thing that can end the hunt.
            Assert.Contains("   at ", trail);
            Assert.Contains("RfCore.Data.DataCube", trail);

            // The branch-selecting state the first note omitted: which transform, whether the Z0
            // override was on, whether this was a versus trace, and what the group actually held.
            Assert.Contains("transform=", trail);
            Assert.Contains("override=", trail);
            Assert.Contains("versus=", trail);
            Assert.Contains("group[SP1]=", trail);
            Assert.Contains("Z0:[port[1]]", trail);

            // What the READ was actually handed, which is not the same claim as any field above.
            // `shape=` is re-read from the DataSet in the catch handler and `slice=` is authored
            // trace state; neither can say whether the gather saw that cube, or with what. This
            // fixture's cube claims three elements and holds one — the axes alone look perfectly
            // healthy, and `buf` vs `expect` is the only pair that shows otherwise.
            Assert.Contains("read=[freq[3]", trail);
            Assert.Contains("buf=1 expect=3", trail);
            Assert.Contains("same=yes", trail);      // still the object `shape=` describes
            Assert.Contains("args=[All, 0, 0]", trail);
        }
        finally
        {
            CrashReporter.ResetForTests();
            AppDataRoot.RedirectTo(null);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AWellFormedTrace_IsUnaffected()
    {
        var axes = new[]
        {
            new Axis("freq", new double[] { 1e9, 2e9, 3e9 }, "Hz"),
            new Axis("i",    new double[] { 0 }),
            new Axis("j",    new double[] { 0 }),
        };
        var ds = new DataSet();
        ds.AddToGroup("SP1", "S", new DataCube(axes,
            new[] { new Complex(0.1, 0.2), new Complex(0.3, 0.4), new Complex(0.5, 0.6) }));
        ds.AddToGroup("SP1", "Z0", DataSetBuilder.BuildZ0Cube(new[] { new Complex(50, 0) }));

        var t = CubeTrace();
        PlotInspectorViewModel.SetCubeDataFrom(t, ds, PlotType.Smith, FreqUnit.GHz);

        Assert.Equal(3, t.Points.Count);
        Assert.Null(t.InvalidSpecText);
        Assert.Null(t.ExpressionError);
    }

    /// <summary>
    /// A trace whose X array and value array disagree in length draws the samples it HAS.
    ///
    /// <para>Every cube read in <c>Trace</c> bounds-checked the index against <c>_cubeXValues</c> and
    /// then indexed <c>_cubeComplexValues</c> — two different arrays — so a torn pair threw a bare
    /// <c>IndexOutOfRangeException</c> from a guard that looks correct. Nothing in the repository
    /// builds a torn pair today; the point is that the guard must not depend on that.</para>
    /// </summary>
    [Fact]
    public void ATornXAndValuePair_DrawsWhatItHas_RatherThanThrowing()
    {
        var t = CubeTrace();
        t.SetCubeData(new double[] { 1e9, 2e9, 3e9 },
                      new[] { new Complex(0.1, 0.2), new Complex(0.3, 0.4), new Complex(0.5, 0.6) },
                      null, "freq", "Hz", PlotType.Smith, FreqUnit.GHz);
        Assert.Equal(3, t.Points.Count);

        // Tear the pair past SetCubeData, which is the only way to reach the guard at all.
        var vf = typeof(Trace).GetField("_cubeComplexValues",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        vf!.SetValue(t, new[] { new Complex(0.1, 0.2) });

        t.BuildPath(PlotType.Smith, FreqUnit.GHz);          // no throw
        Assert.Single(t.Points);                            // the one sample it genuinely has
        Assert.Equal("NaN", t.FormatCubeCell(1, PrecisionFormat.G, 4));
        Assert.Equal("NaN", t.FormatCubeCellForMarker(2, new Marker(t, 2e9, false, false, 1)));
    }
}
