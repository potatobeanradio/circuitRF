// brief-img-3-trace-layout-image.md §4 — where a traced layout goes (R-im3-6, R-im3-7, D5, D14).

using System;
using System.IO;
using System.Linq;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Recognition.Image;
using CircuitRF.Ui.Recognition;
using Xunit;

namespace CircuitRF.Ui.Tests.Imaging;

public sealed class ImageTraceTargetTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-imgtrace-" + Guid.NewGuid().ToString("N")[..12]);

    public ImageTraceTargetTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static string[] Snapshot(string dir) =>
        [.. Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Order().Select(f => $"{f}|{new FileInfo(f).Length}|{File.GetLastWriteTimeUtc(f).Ticks}")];

    [Fact]
    public void IntoTheLayout_ReturnsOneEdit_AndWritesNoFile()
    {
        var raster = TracePictures.TwoLayerBoard();
        File.WriteAllBytes(Path.Combine(_dir, "board.png"), Pictures.Png(raster));
        var bitmap = new BitmapShape { ImagePathRef = "board.png", X = 1_000_000, Y = -500_000, W = 4_000_000, H = 3_000_000, Layer = new LayerKey(9, 0) };
        var view = new LayoutView();
        view.Shapes.Add(bitmap);
        string clay = Path.Combine(_dir, "amp.clay");
        LayoutPersistence.SaveToFile(clay, view);
        var before = Snapshot(_dir);

        var run = ImageTrace.Run(new ImageTraceInput
        {
            Source = ImageSource.FromLayoutBitmap(clay, bitmap).Source!, Technology = TracePictures.Tech(),
        }, ImageTraceTarget.IntoLayout);

        Assert.True(run.Ok, run.Refusal);
        Assert.Null(run.LayoutPath);
        Assert.Equal(ImageScaleKind.Placement, run.Result.Scale!.Kind);
        Assert.Null(run.Result.Underlay);                  // the bitmap is already there
        Assert.Equal(before, Snapshot(_dir));

        // On the bitmap: every traced point is inside its rect.
        foreach (var r in run.Edit!.Shapes.OfType<RectShape>())
            Assert.True(r.X1 >= bitmap.X && r.X2 <= bitmap.X + bitmap.W && r.Y1 >= bitmap.Y && r.Y2 <= bitmap.Y + bitmap.H);

        // One undo step.
        var cmd = ImageTraceEditCommand.For(view, run.Edit)!;
        cmd.Execute();
        Assert.Equal(1 + run.Edit.Shapes.Count, view.Shapes.Count);
        cmd.Undo();
        Assert.Same(bitmap, Assert.Single(view.Shapes));
    }

    [Fact]
    public void ANewCell_CarriesTheUnderlay_OnTheTracedFrame()
    {
        var raster = TracePictures.TwoLayerBoard();
        var run = ImageTrace.Run(new ImageTraceInput
        {
            Source = TracePictures.Source(raster), Technology = TracePictures.Tech(), Scale = ImageScaleStatement.Stated(10e-6),
        }, ImageTraceTarget.NewCell(_dir, "board"));

        Assert.True(run.Ok, run.Refusal);
        var view = LayoutPersistence.LoadFromFile(run.LayoutPath!);
        var u = Assert.Single(view.Shapes.OfType<BitmapShape>());
        Assert.Equal((0L, -3_000_000L, 4_000_000L, 3_000_000L), (u.X, u.Y, u.W, u.H));
        Assert.Equal(ImageTrace.UnderlayOpacity, u.Opacity);
        Assert.True(u.Locked);
        Assert.True(File.Exists(Path.Combine(_dir, "board", "board.source.png")));
        Assert.Equal(Path.GetFullPath(Path.Combine(_dir, "board", "board.source.png")), Path.GetFullPath(u.ImagePathRef));
        Assert.Equal("10 µm/px", view.ImageSource!.Scale);
        Assert.Contains(view.Shapes, s => s is RectShape);
    }
}
