// brief-img-5-dialog.md §4 — the colour → layer rows (D7): a row the user re-read survives the re-run an option change
// causes; a preset whose colours match is offered in the status line, and applied only when accepted. The trace is the
// real one, on IM-3's two-layer picture.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout.Recognition.Image;
using CircuitRF.Ui.Recognition;
using CircuitRF.Ui.Tests.Imaging;
using Xunit;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class ImageLayerRowViewModelTests : IDisposable
{
    private readonly string _ws = Directory.CreateTempSubdirectory("crf-im5-layers-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_ws, true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task AnEditedRow_SurvivesTheReRunAnOptionChangeCauses()
    {
        var runner = new RecordingImageRunner(DrawingKind.Layout);
        var vm = ImageDialog.Open(_ws, runner, ImageDialog.Board(), makeSchematic: false);
        await vm.Recognition;
        var bottom = vm.LayerRows.First(r => r.Row.Role == ImageLayerRole.Layer && !r.Row.Overlap);
        int rgb = bottom.Rgb;

        bottom.LayerText = ImageLayerRowViewModel.Ignore;
        await vm.Recognition;
        vm.SimplifyText = "0.5";
        await vm.Recognition;

        var handed = runner.Traces[^1];
        Assert.Equal(0.5, handed.Options.SimplifyPx);
        Assert.True(handed.LayerMap!.Edited);
        var row = vm.LayerRows.Single(r => r.Rgb == rgb);
        Assert.True(row.Edited);
        Assert.Equal(ImageLayerRowViewModel.Ignore, row.LayerText);
        Assert.All(vm.LayerRows.Where(r => r.Rgb != rgb), r => Assert.False(r.Edited));
    }

    [Fact]
    public async Task AMatchingPreset_IsOffered_NotApplied()
    {
        string presets = Path.Combine(_ws, "presets");
        var picture = ImageDialog.Board();
        var traced = ImageTrace.Trace(new ImageTraceInput { Source = picture, Technology = TracePictures.Tech() });
        ImageTracePresets.Save(ImageTracePresets.From("viewer dark", traced.LayerMap!, new ImageTraceOptions()), presets);

        var runner = new RecordingImageRunner(DrawingKind.Layout);
        var vm = ImageDialog.Open(_ws, runner, picture, makeSchematic: false, presets);
        await vm.Recognition;

        Assert.Equal("Colours match preset \"viewer dark\"", vm.OfferText);
        Assert.Null(runner.Traces[^1].LayerMap);
        Assert.All(vm.LayerRows, r => Assert.False(r.Edited));

        vm.AcceptOfferCommand.Execute(null);
        await vm.Recognition;

        Assert.True(runner.Traces[^1].LayerMap!.Edited);
        Assert.False(vm.HasOffer);
    }
}
