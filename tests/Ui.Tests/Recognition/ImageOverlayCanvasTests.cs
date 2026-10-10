// brief-img-5-dialog.md §4 — the canvas's cross-probe, as the pure mapping it is: selecting a parts-table row selects
// its part on the picture, and clicking a part on the picture selects its row. Nothing is rendered.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.Layout.Recognition.Image;
using CircuitRF.Design.RailRf;
using CircuitRF.Ui.Recognition;
using Xunit;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class ImageOverlayCanvasTests : IDisposable
{
    private readonly string _ws = Directory.CreateTempSubdirectory("crf-im5-overlay-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_ws, true); } catch { /* best effort */ }
    }

    /// <summary>Two parts, 10 DBU to the pixel, read off a 400 × 300 picture.</summary>
    private static ImageRecognitionCircuit TwoParts(ImageRecognitionInput input)
    {
        var frame = PixelFrame.YUp(10, 300);
        PartRow Part(string refdes, double x, double y)
        {
            var (ax, ay) = frame.ToTarget(x - 5, y);
            var (bx, by) = frame.ToTarget(x + 5, y);
            return new PartRow
            {
                Refdes = refdes, Kind = PartKind.C, Value = 1e-12,
                Terminals = [new PartTerminal((long)ax, (long)ay, null, 0, false), new PartTerminal((long)bx, (long)by, null, 0, false)],
            };
        }
        var table = new PartsTable([Part("C1", 100, 100), Part("R1", 300, 200)], RailLengthFormat.Dbu, null);
        var trace = new ImageTraceResult
        {
            Report = new RecognitionReport(),
            Scale = new ImageScaleCandidate(ImageScaleKind.Stated, 10e-9, 1, "stated"),
            Frame = frame,
        };
        var recognition = new RecognitionResult(null, new RecognitionReport(), null) { Parts = table };
        return new ImageRecognitionCircuit(trace, null, recognition, null, new RecognitionReport(), null);
    }

    [Fact]
    public async Task SelectingARow_SelectsItsPartOnThePicture_AndClickingAPart_SelectsItsRow()
    {
        var runner = new RecordingImageRunner(DrawingKind.Layout) { ReadAs = TwoParts };
        var vm = ImageDialog.Open(_ws, runner, ImageDialog.Board(), makeSchematic: true);
        await vm.Recognition;

        vm.SelectedRow = vm.Rows.Single(r => r.Refdes == "R1");
        Assert.Equal("R1", vm.SelectedItem?.PartKey);
        Assert.True(vm.SelectedItem!.Bounds.Contains(300, 200));

        var c1 = vm.Overlay.HitTest(100, 100, 2, vm.Shows);
        Assert.Equal(ImageOverlayClass.Part, c1?.Class);
        vm.SelectedItem = c1;
        Assert.Equal("C1", vm.SelectedRow?.Refdes);

        vm.SelectedRow = null;
        Assert.Null(vm.SelectedItem);
    }
}
