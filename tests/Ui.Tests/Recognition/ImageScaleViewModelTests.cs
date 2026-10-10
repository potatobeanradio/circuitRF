// brief-img-5-dialog.md §4 — the scale row (D6): with only the file's resolution as evidence Create waits; Two points
// with "1.6 mm" states the scale, with a bare "1.6" is refused in place; and what was chosen is what the trace is handed.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.Layout.Recognition.Image;
using CircuitRF.Ui.Recognition;
using Xunit;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class ImageScaleViewModelTests : IDisposable
{
    private readonly string _ws = Directory.CreateTempSubdirectory("crf-im5-scale-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_ws, true); } catch { /* best effort */ }
    }

    /// <summary>A trace whose only evidence is the file's 600 dpi — unless the input states two points.</summary>
    private static ImageTraceResult ResolutionOnly(ImageTraceInput input)
    {
        var resolution = new ImageScaleCandidate(ImageScaleKind.Resolution, 0.0254 / 600, 0, "the file states 600 dpi (pHYs) — offered, not used");
        var stated = input.Scale.Kind == ImageScaleStatement.StatementKind.TwoPoints
            ? ImageScale.FromTwoPoints(input.Scale.A, input.Scale.B, input.Scale.Distance, out _) : null;
        return new ImageTraceResult
        {
            Report = new RecognitionReport(),
            ScaleCandidates = stated is null ? [resolution] : [stated, resolution],
            Scale = stated,
        };
    }

    private async Task<(ImageSourceViewModel Vm, RecordingImageRunner Runner)> Open()
    {
        var runner = new RecordingImageRunner(DrawingKind.Layout) { TraceAs = ResolutionOnly };
        var vm = ImageDialog.Open(_ws, runner, ImageDialog.Board(), makeSchematic: false);
        await vm.Recognition;
        return (vm, runner);
    }

    [Fact]
    public async Task WithOnlyFileResolutionEvidence_CreateWaits()
    {
        var (vm, _) = await Open();

        Assert.True(vm.Scale.NeedsScale);
        Assert.Contains(vm.Scale.Choices, c => c.Label == "File resolution 600 dpi");
        Assert.Null(vm.Scale.SelectedChoice);
        Assert.False(vm.CreateCommand.CanExecute(null));
        Assert.Equal(ImageScaleViewModel.SetScalePrompt, vm.CreateTip);
        Assert.Equal(ImageScaleViewModel.SetScalePrompt, vm.CanvasPrompt);
    }

    [Fact]
    public async Task TwoPoints_RefusesABareNumberInPlace_StatesOneWithAUnit_AndTheTraceIsHandedIt()
    {
        var (vm, runner) = await Open();
        var scale = vm.Scale;
        scale.SelectedChoice = scale.Choices.Single(c => c.Kind == ImageScaleChoiceKind.TwoPoints);
        scale.Click(new PixelPoint(10, 20));
        scale.Click(new PixelPoint(110, 20));
        Assert.True(scale.AskingDistance);

        scale.DistanceText = "1.6";
        scale.ApplyDistanceCommand.Execute(null);

        Assert.Contains("no unit", scale.DistanceError);
        Assert.True(scale.AskingDistance);
        Assert.Equal(ImageScaleStatement.StatementKind.Auto, scale.Statement.Kind);
        Assert.False(vm.CreateCommand.CanExecute(null));

        scale.DistanceText = "1.6 mm";
        scale.ApplyDistanceCommand.Execute(null);
        await vm.Recognition;

        var handed = runner.Traces[^1].Scale;
        Assert.Equal((ImageScaleStatement.StatementKind.TwoPoints, new PixelPoint(10, 20), new PixelPoint(110, 20), "1.6 mm"),
                     (handed.Kind, handed.A, handed.B, handed.Distance));
        Assert.False(scale.NeedsScale);
        Assert.Equal("16 µm/px", scale.ValueText);
        Assert.True(vm.CreateCommand.CanExecute(null));
    }
}
