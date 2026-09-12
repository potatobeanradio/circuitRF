// ================================================================
//  SolvedPointMarkerTests.cs — a marker stands on a SAMPLE, never on an interpolated point.
//
//  Owner report, 2026-09-11: an EM run was STOPPED after six points, and the s-parameter plot —
//  with the trace card's marker shape set to Square — drew about a hundred markers. Nothing was
//  wrong with either half on its own: an adaptively sampled sweep publishes the whole requested
//  grid and models the points it did not solve (R-adf-2), and the renderer marked every point the
//  trace had. Together they claimed ninety-four solves that never happened, in the one form a
//  reader counts samples by.
//
//  The rule this file holds: the LINE runs through every published point, markers only through
//  the solved ones, and a source that says nothing about the question is unchanged — which is
//  every Touchstone file and every circuit analysis.
//
//  The engine half — the published DataSet carries the mask at all — is in
//  tests/Engine.Tests/Mom/SolvedPointCubeTests.cs.
// ================================================================

using System;
using System.IO;
using System.Linq;
using CircuitRF.Design.Results;
using CircuitRF.Render.DataDisplay;
using RfCore;
using RfCore.Data;
using RfCore.Export;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.DataDisplay;

public sealed class SolvedPointMarkerTests
{
    // The shape of the owner's run, shrunk: a requested grid, of which only a few points were
    // solved before the Stop.
    private static readonly double[] Freqs =
        Enumerable.Range(0, 21).Select(i => 1e9 + i * 0.1e9).ToArray();
    private static readonly int[] SolvedAt = { 0, 4, 10, 20 };

    private static DataSet MakeDs(bool withMask)
    {
        var freq = new Axis("freq", (double[])Freqs.Clone(), "Hz");
        var ds   = new DataSet();

        // A smooth response, so the modelled points are entirely plausible — which is the whole
        // problem: nothing about the CURVE says which points were computed.
        var mag = Freqs.Select((f, i) => -0.5 - 0.01 * i).ToArray();
        ds.Add("S21dB", new DataCube([freq], mag));

        if (withMask)
        {
            var solved = SolvedAt.Select(i => Freqs[i]).ToArray();
            ds.AddToGroup("planar", SampleProvenance.SolvedCubeName,
                          SampleProvenance.BuildSolvedCube(
                              new Axis("freq", (double[])Freqs.Clone(), "Hz"), solved));
        }
        return ds;
    }

    private static Trace Resolve(DataSet ds)
    {
        Assert.True(CubeTraceSpecParser.TryParse("S21dB[:]", ds, out string cube, out var slice,
                                                 out var transform, out string error), error);
        var t = new Trace(new SNP([1e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            CubeName = cube, Slice = slice, Transform = transform,
        };
        TraceResolve.SetCubeDataFrom(t, ds, PlotType.Rect, FreqUnit.GHz);
        return t;
    }

    /// <summary>
    /// The curve is still the whole published grid — the modelled points are the run's own answer
    /// and dropping them would be a different lie — but only the solved ones are marked.
    /// </summary>
    [Fact]
    public void EveryPublishedPointIsDrawn_OnlyTheSolvedOnesAreMarked()
    {
        var t = Resolve(MakeDs(withMask: true));

        Assert.Equal(Freqs.Length, t.Points.Count);

        var marked = Enumerable.Range(0, t.Points.Count).Where(t.PointIsSolved).ToArray();
        Assert.Equal(SolvedAt, marked);
    }

    /// <summary>
    /// A source that says nothing about the question is untouched: every point is a sample, which
    /// is what a Touchstone file and a circuit analysis are.
    /// </summary>
    [Fact]
    public void WithNoMask_EveryPointIsASample()
    {
        var t = Resolve(MakeDs(withMask: false));

        Assert.Equal(Freqs.Length, t.Points.Count);
        Assert.All(Enumerable.Range(0, t.Points.Count), i => Assert.True(t.PointIsSolved(i)));
    }

    /// <summary>
    /// Re-binding a trace to a source with no mask CLEARS the previous one. A mask that outlived
    /// its data would hide markers at points that were solved — the same failure the trace's back
    /// branch has already been fixed for, and invisible in exactly the same way.
    /// </summary>
    [Fact]
    public void ReBindingToAnUnmaskedSource_ClearsTheMask()
    {
        var t = Resolve(MakeDs(withMask: true));
        Assert.False(t.PointIsSolved(1));

        var plain = MakeDs(withMask: false);
        Assert.True(CubeTraceSpecParser.TryParse("S21dB[:]", plain, out _, out var slice2,
                                                 out _, out string err2), err2);
        t.Slice = slice2;
        TraceResolve.SetCubeDataFrom(t, plain, PlotType.Rect, FreqUnit.GHz);
        Assert.All(Enumerable.Range(0, t.Points.Count), i => Assert.True(t.PointIsSolved(i)));
    }

    // ── The SNP-bound trace, which is what an s-parameter plot ACTUALLY is ────────────────────
    //
    //  "Add a trace" against any source carrying an S network seeds a NETWORK-bound trace, not a
    //  cube-bound one (PlotInspectorViewModel.AddTrace) — it never goes near SetCubeData. The first
    //  cut of this fix covered only the cube path, so the owner tested it and nothing had changed.
    //  The mask therefore rides the SNP itself, filled in the one place a DataSet becomes one.

    private static DataSet MakeNetworkDs(bool withMask)
    {
        int nf = Freqs.Length;
        var freq = new Axis("freq", (double[])Freqs.Clone(), "Hz");
        var port = new Axis("port", [1.0, 2.0], "");

        var s = new System.Numerics.Complex[nf * 2 * 2];
        for (int i = 0; i < nf; i++)
        {
            s[i * 4 + 0] = new System.Numerics.Complex(0.05, 0.0);                  // S11
            s[i * 4 + 1] = new System.Numerics.Complex(0.9 - 0.001 * i, 0.0);       // S12
            s[i * 4 + 2] = new System.Numerics.Complex(0.9 - 0.001 * i, 0.0);       // S21
            s[i * 4 + 3] = new System.Numerics.Complex(0.05, 0.0);                  // S22
        }

        var ds = new DataSet();
        ds.Add("S", new DataCube([freq, port, port], s));
        if (withMask)
            ds.AddToGroup("planar", SampleProvenance.SolvedCubeName,
                          SampleProvenance.BuildSolvedCube(
                              new Axis("freq", (double[])Freqs.Clone(), "Hz"),
                              SolvedAt.Select(i => Freqs[i]).ToArray()));
        return ds;
    }

    private static Trace NetworkTrace(DataSet ds)
    {
        var t = new Trace(DataSetBuilder.ToSnp(ds), MatrixType.S, 1, 0, DependentVarFormat.Db);
        t.BuildPath(PlotType.Rect, FreqUnit.GHz);
        return t;
    }

    [Fact]
    public void AnSParameterTraceOnTheNetwork_MarksOnlyTheSolvedPoints()
    {
        var t = NetworkTrace(MakeNetworkDs(withMask: true));

        Assert.Equal(Freqs.Length, t.Points.Count);
        Assert.Equal(SolvedAt, Enumerable.Range(0, t.Points.Count).Where(t.PointIsSolved).ToArray());
    }

    [Fact]
    public void ATouchstoneShapedNetwork_CarriesNoMask_AndEveryPointIsASample()
    {
        var snp = DataSetBuilder.ToSnp(MakeNetworkDs(withMask: false));
        Assert.Null(snp.SolvedMask);

        var t = NetworkTrace(MakeNetworkDs(withMask: false));
        Assert.All(Enumerable.Range(0, t.Points.Count), i => Assert.True(t.PointIsSolved(i)));
    }

    // ── And what is actually DRAWN, which is the thing the owner counted ──────────────────────

    private const int W = 420, H = 260;

    private static TransformSet Tf(Trace t)
    {
        float xMin = t.Points.Min(p => p.X), xMax = t.Points.Max(p => p.X);
        float yMin = t.Points.Min(p => p.Y), yMax = t.Points.Max(p => p.Y);
        double xs = (W - 40) / (xMax - xMin), ys = (H - 40) / Math.Max(1e-9, yMax - yMin);
        var map = (XScale: xs, YScale: ys, XOffset: 20 - xMin * xs, YOffset: 20 - yMin * ys);
        return new TransformSet { Primary = map, Secondary = map, CanvasSize = (W, H) };
    }

    /// <summary>Marker ink within a couple of pixels of a point's own position on the canvas.</summary>
    private static bool InkAt(SKBitmap bmp, SKPoint px)
    {
        for (int dy = -2; dy <= 2; dy++)
        for (int dx = -2; dx <= 2; dx++)
        {
            int x = (int)MathF.Round(px.X) + dx, y = (int)MathF.Round(px.Y) + dy;
            if (x < 0 || y < 0 || x >= bmp.Width || y >= bmp.Height) continue;
            if (bmp.GetPixel(x, y) != SKColors.White) return true;
        }
        return false;
    }

    /// <summary>
    /// <b>The distinction has to survive the FILE.</b> Nothing in the window reads the engine's
    /// result object — the EM run writes <c>results/&lt;key&gt;.npy</c> and the Data Display opens
    /// that, so a mask that did not round-trip would be a mask nobody ever sees.
    /// </summary>
    [Fact]
    public void TheMaskSurvivesTheResultsFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-solved-mask-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // The EM's own shape: an S network plus the mask, written the way EmRunService writes it.
            var written = ResultsWriter.WriteRun(dir, "run", MakeNetworkDs(withMask: true));
            Assert.Null(written.Error);
            string npy = Assert.Single(written.Written);

            var (readBack, _) = DataSetImporter.Import(npy);

            // Both routes out of the file, because the window takes the second one: the cube the
            // trace card can pick, and the SNP "add a trace" seeds a network-bound trace from.
            Assert.Equal(SolvedAt.Select(i => Freqs[i]).ToArray(),
                         Enumerable.Range(0, Freqs.Length)
                                   .Where(i => SampleProvenance.SolvedMaskFor(
                                       readBack, readBack["S"].Axes[0])![i])
                                   .Select(i => Freqs[i]).ToArray());

            var t = NetworkTrace(readBack);
            var marked = Enumerable.Range(0, t.Points.Count).Where(t.PointIsSolved).ToArray();
            Assert.Equal(SolvedAt, marked);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Theory]
    [InlineData(false)]   // the cube-bound trace
    [InlineData(true)]    // the network-bound trace an s-parameter plot actually is
    public void TheRendererDrawsAMarkerAtEverySolvedPoint_AndAtNoOther(bool network)
    {
        var t = network ? NetworkTrace(MakeNetworkDs(withMask: true)) : Resolve(MakeDs(withMask: true));
        t.Properties.LineEnabled   = false;   // markers alone, so the ink under a point is a marker
        t.Properties.MarkerEnabled = true;
        t.Properties.MarkerType    = MarkerType.Square;   // the owner's own setting

        var tf  = Tf(t);
        var bmp = new SKBitmap(W, H);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.White);
            TraceRenderer.Draw(canvas, (W, H), t, tf, RenderTheme.Light);
        }

        for (int i = 0; i < t.Points.Count; i++)
        {
            var px = tf.PrimaryToCanvas(t.Points[i].X, t.Points[i].Y);
            bool expected = SolvedAt.Contains(i);
            Assert.Equal(expected, InkAt(bmp, px));
        }
    }
}
