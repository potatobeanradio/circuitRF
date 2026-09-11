// ================================================================
//  Pattern3DDrawPathTests.cs — the 3D surface has TWO ways of putting
//  its triangles on a canvas, and both of them have a silent failure.
//
//  A live frame draws the whole mesh in one DrawVertices call, which is
//  ~9x faster than a filled path per triangle and is what makes a Data
//  Display carrying a pattern pan at frame rate. A document draws the
//  paths, because a vector device records drawVertices as NOTHING — an
//  SVG with an empty <svg> element, a PDF with a blank page.
//
//  Both failure modes look like a picture: an empty export is a valid
//  file, and a mesh drawn with the default black paint under Modulate
//  is a solid black lobe with a correct colour bar beside it. So the
//  gates here are (a) a document still contains the surface, and (b)
//  the two paths draw the SAME surface — same silhouette, same colours
//  — rather than two plausible ones.
//
//  No timing is asserted anywhere in this file. The speed is why the
//  mesh exists; what a test can hold is that it draws the right thing
//  and that the frame-to-frame cache lets go when it should.
// ================================================================

using System;
using System.Linq;
using CircuitRF.Render.DataDisplay;
using RfCore;
using RfCore.Data;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.DataDisplay;

public sealed class Pattern3DDrawPathTests(ITestOutputHelper output)
{
    private const int W = 520, H = 321;

    // ══ fixture ══════════════════════════════════════════════════════════════

    /// <summary>
    /// A broadside-ish lobe over a fine grid — fine enough that the two draw paths have thousands of
    /// facets to disagree about, and asymmetric in φ so a transposed or mirrored surface cannot match
    /// the other one by accident.
    /// </summary>
    private static Plot Surface(double floorDb = -30, PatternCamera? cam = null)
    {
        double[] theta = Enumerable.Range(0, 46).Select(i => i * 2.0).ToArray();
        double[] phi   = Enumerable.Range(0, 72).Select(i => i * 5.0).ToArray();

        var v = new double[theta.Length * phi.Length];
        for (int t = 0; t < theta.Length; t++)
            for (int p = 0; p < phi.Length; p++)
            {
                double ct = Math.Cos(theta[t] * Math.PI / 180.0);
                double cp = Math.Cos(phi[p] * Math.PI / 180.0);
                v[t * phi.Length + p] = Math.Max(1e-4, ct * ct * (1.0 + 0.4 * cp));
            }

        var ds = new DataSet();
        ds.AddToGroup("farfield", "U", new DataCube(
            [new Axis("theta", theta, "deg"), new Axis("phi", phi, "deg")], v));

        Assert.True(CubeTraceSpecParser.TryParse("db10(farfield.U[:, :])", ds,
                                                 out string c, out var s, out var tr, out string e), e);
        var trace = new Trace(new SNP([5e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db)
        { CubeName = c, Slice = s, Transform = tr };

        var plot = new Plot(PlotType.Surface3D, FreqUnit.GHz) { PolarDbFloor = floorDb };
        if (cam is { } k) plot.SurfaceCamera = k;
        plot.Traces.Add(trace);
        TraceResolve.SetCubeDataFrom(trace, ds, PlotType.Surface3D, FreqUnit.GHz);
        plot.Autoscale(force: true);
        return plot;
    }

    private static uint[] Raster(Plot plot, bool asDocument, int scale = 1)
    {
        using var surface = SKSurface.Create(new SKImageInfo(W * scale, H * scale));
        surface.Canvas.Clear(SKColors.White);
        surface.Canvas.Scale(scale, scale);

        if (asDocument)
            using (PlotDocumentScope.Enter())
                PlotRenderer.Draw(surface.Canvas, (W, H), plot, PlotDetail.Full, RenderTheme.Light, false);
        else
            PlotRenderer.Draw(surface.Canvas, (W, H), plot, PlotDetail.Full, RenderTheme.Light, false);

        using var image = surface.Snapshot();
        using var bmp   = SKBitmap.FromImage(image);
        var px = new uint[bmp.Width * bmp.Height];
        for (int y = 0, i = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++, i++)
            {
                var c = bmp.GetPixel(x, y);
                px[i] = ((uint)c.Red << 16) | ((uint)c.Green << 8) | c.Blue;
            }
        return px;
    }

    // ══ the document ═════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A 3D surface in an SVG is thousands of filled paths, not an empty page.</b>
    ///
    /// <para>This is the gate on the whole reason <see cref="PlotDocumentScope"/> exists. Skia's SVG
    /// device silently ignores <c>drawVertices</c>: the same mesh that rasterises correctly writes a
    /// 150-byte document with nothing in it, which opens, prints and exports without complaint. The
    /// path COUNT is asserted rather than the file size, because a surface replaced by its ground
    /// disc and axes alone is still several kilobytes of text.</para>
    /// </summary>
    [Fact]
    public void SurfaceExportsAsPaths_NotAnEmptyVectorPage()
    {
        string svg = PlotDocumentWriter.BuildSvgString(
            canvas => PlotRenderer.Draw(canvas, (W, H), Surface(), PlotDetail.Full,
                                        RenderTheme.Light, false),
            PagePlacement.Letter);

        int paths = svg.Split("<path").Length - 1;
        output.WriteLine($"svg: {svg.Length} chars, {paths} <path> elements");

        // 45 x 72 samples is 44 x 72 cells, two triangles each — over 6,000 facets, minus those the
        // mesh drops at the pole. An order of magnitude under that is not decimation, it is a
        // surface that did not draw.
        Assert.True(paths > 4000, $"the surface is not in the SVG — only {paths} paths were written");
    }

    /// <summary>The same, in PDF: a page with a surface on it is not a page with a colour bar on it.</summary>
    [Fact]
    public void SurfaceExportsIntoPdf()
    {
        byte[] withSurface = PlotDocumentWriter.BuildPdfBytes(
            canvas => PlotRenderer.Draw(canvas, (W, H), Surface(), PlotDetail.Full,
                                        RenderTheme.Light, false),
            PagePlacement.Letter);

        var bare = Surface();
        bare.Traces.Clear();
        byte[] withoutSurface = PlotDocumentWriter.BuildPdfBytes(
            canvas => PlotRenderer.Draw(canvas, (W, H), bare, PlotDetail.Full,
                                        RenderTheme.Light, false),
            PagePlacement.Letter);

        output.WriteLine($"pdf with surface: {withSurface.Length} B, without: {withoutSurface.Length} B");
        // An absolute delta, not a ratio: an empty page still carries the embedded fonts of the
        // title, the captions and the colour bar, which is most of a hundred kilobytes on its own.
        Assert.True(withSurface.Length - withoutSurface.Length > 100_000,
                    "the PDF is no bigger with a surface on it than without one");
    }

    // ══ the two paths draw the same picture ══════════════════════════════════

    /// <summary>
    /// <b>The frame's mesh and the document's paths are the same surface.</b>
    ///
    /// <para>Compared as pixels, which §6 normally forbids — but the question here is exactly a
    /// pixel one, and the reference side is the path fill that ANT-10 shipped and that every export
    /// still uses. The two differ legitimately at the silhouette, where one is antialiased and the
    /// other is not, so the gate is on how MUCH may differ and by how much: a mesh drawn black under
    /// the wrong blend mode, drawn with Gouraud-interpolated colours, or not drawn at all fails every
    /// one of these three assertions rather than squeaking past a single loose tolerance.</para>
    /// </summary>
    [Fact]
    public void FrameMeshAndDocumentPathsDrawTheSameSurface()
    {
        var plot = Surface();
        uint[] mesh  = Raster(plot, asDocument: false);
        uint[] paths = Raster(plot, asDocument: true);

        int filledMesh = 0, filledPaths = 0, wayOff = 0;
        long absDiff = 0;
        for (int i = 0; i < mesh.Length; i++)
        {
            if (mesh[i]  != 0xFFFFFF) filledMesh++;
            if (paths[i] != 0xFFFFFF) filledPaths++;

            int d = Math.Abs((int)((mesh[i] >> 16) & 0xFF) - (int)((paths[i] >> 16) & 0xFF))
                  + Math.Abs((int)((mesh[i] >>  8) & 0xFF) - (int)((paths[i] >>  8) & 0xFF))
                  + Math.Abs((int) (mesh[i]        & 0xFF) - (int) (paths[i]        & 0xFF));
            absDiff += d;
            if (d > 60) wayOff++;
        }

        double meanDiff = (double)absDiff / mesh.Length;
        output.WriteLine($"ink: mesh {filledMesh}, paths {filledPaths}; " +
                         $"mean |diff| {meanDiff:F3}/765; pixels >60 apart: {wayOff} of {mesh.Length}");

        Assert.True(filledMesh > mesh.Length / 50, "the mesh drew almost nothing");
        // Same silhouette: the drawn area may differ only by the antialiased rim.
        Assert.InRange(filledMesh / (double)filledPaths, 0.98, 1.02);
        // Same colours: a black or a Gouraud-blended surface moves this by two orders of magnitude.
        Assert.True(meanDiff < 2.0, $"the two surfaces are different colours (mean |diff| {meanDiff:F2})");
        // And the disagreement is a rim, not a region.
        Assert.True(wayOff < mesh.Length / 100,
                    $"{wayOff} pixels differ by more than a rim's worth of antialiasing");
    }

    // ══ the frame-to-frame mesh cache ════════════════════════════════════════

    /// <summary>
    /// <b>A repeated frame reuses the mesh, and a changed one does not.</b>
    ///
    /// <para>Panning a Data Display redraws every plot on it with nothing about the pattern changed,
    /// and re-projecting and re-sorting the facets to get the identical answer is the larger half of
    /// what such a frame costs. The cache is keyed on everything the build reads; each assertion
    /// below moves exactly one of those keys, because a cache that ignored one would hand back the
    /// PREVIOUS camera's surface — a picture that is entirely plausible and simply does not turn.</para>
    /// </summary>
    [Fact]
    public void MeshIsReusedAcrossIdenticalFramesAndRebuiltWhenTheViewChanges()
    {
        var plot = Surface(cam: PatternCamera.New(35, 25));

        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        SurfaceRenderer.Frame Draw(Plot p, PlotDetail detail = PlotDetail.Full)
        {
            surface.Canvas.Clear(SKColors.White);
            return SurfaceRenderer.Draw(surface.Canvas, (W, H), p, detail, RenderTheme.Light);
        }

        var first  = Draw(plot);
        var second = Draw(plot);
        Assert.True(first.Facets.Count > 1000, "the fixture is too coarse to be worth caching");
        Assert.Same(first.Facets, second.Facets);

        // A render snapshot is a different Plot object over the SAME resolved grid — which is what a
        // frame actually draws, so the cache has to survive it.
        Assert.Same(first.Facets, Draw(plot.RenderSnapshot()).Facets);

        plot.SurfaceCamera = plot.SurfaceCamera.RotatedBy(20, 0);
        var turned = Draw(plot);
        Assert.NotSame(first.Facets, turned.Facets);
        // Not a probe of one facet: after the depth sort, facet 0 is the farthest, which at this
        // camera is a degenerate sliver at the pole and sits at the origin whatever the azimuth.
        Assert.True(first.Facets.Count != turned.Facets.Count
                    || Enumerable.Range(0, first.Facets.Count)
                                 .Any(i => Math.Abs(first.Facets[i].X0 - turned.Facets[i].X0) > 1e-6),
                    "the camera turned and the mesh did not");

        // The decimated mesh an interacting frame draws is a different mesh, not the same one.
        var quick = Draw(plot, PlotDetail.Quick);
        Assert.NotSame(turned.Facets, quick.Facets);
        Assert.True(quick.Facets.Count < turned.Facets.Count);
        Assert.Same(quick.Facets, Draw(plot, PlotDetail.Quick).Facets);

        // And the radial scale is a key too: the same camera over a deeper floor is a different shape.
        var deeper = Surface(floorDb: -60, cam: plot.SurfaceCamera);
        Assert.NotSame(turned.Facets, Draw(deeper).Facets);
    }
}
