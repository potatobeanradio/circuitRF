// brief-img-5-dialog.md §4 — the picture source: a dropped file that is not a picture is refused and the drop zone
// stays; a picture with no drawing disables Create with its sentence, and the override sets the kind; Make Layout is
// disabled for a schematic picture, and comes back when the picture is read as a layout.

using System;
using System.IO;
using System.Threading.Tasks;
using CircuitRF.Design.Imaging;
using CircuitRF.Ui.Recognition;
using Xunit;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class ImageSourceViewModelTests : IDisposable
{
    private readonly string _ws = Directory.CreateTempSubdirectory("crf-im5-src-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_ws, true); } catch { /* best effort */ }
    }

    [Fact]
    public void ADroppedNonPicture_IsRefused_AndTheDropZoneStays()
    {
        string text = Path.Combine(_ws, "notes.png");
        File.WriteAllText(text, "not a picture");
        var vm = ImageDialog.Open(_ws, new RecordingImageRunner(DrawingKind.Layout), null, makeSchematic: true);

        Assert.False(vm.LoadFile(text));

        Assert.True(vm.IsEmpty);
        Assert.Equal(ImageSource.FromFile(text).Refusal, vm.Status);
        Assert.False(vm.CreateCommand.CanExecute(null));
    }

    [Fact]
    public async Task APictureWithNoDrawing_DisablesCreateWithItsSentence_AndTheOverrideSetsTheKind()
    {
        var vm = ImageDialog.Open(_ws, new RecordingImageRunner(DrawingKind.None), ImageDialog.Board(), makeSchematic: false);
        await vm.Recognition;

        Assert.StartsWith("No drawing found: a photograph-like picture", vm.FailureText);
        Assert.True(vm.FailureOffersKind);
        Assert.False(vm.CreateCommand.CanExecute(null));
        Assert.Equal(vm.FailureText, vm.CreateTip);

        vm.ReadAsLayoutCommand.Execute(null);
        await vm.Recognition;

        Assert.Equal(DrawingKind.Layout, vm.EffectiveKind);
        Assert.False(vm.HasFailure);
    }

    [Fact]
    public async Task MakeLayout_IsDisabledForASchematicPicture_AndComesBackWhenItIsReadAsALayout()
    {
        var vm = ImageDialog.Open(_ws, new RecordingImageRunner(DrawingKind.Schematic), ImageDialog.Board(), makeSchematic: false);
        await vm.Recognition;

        Assert.False(vm.MakeLayoutEnabled);
        Assert.Equal(ImageSourceViewModel.MakeLayoutFromSchematicTip, vm.MakeLayoutTip);
        Assert.True(vm.MakeSchematic);

        vm.KindIndex = 2;
        await vm.Recognition;

        Assert.True(vm.MakeLayoutEnabled);
        Assert.True(vm.MakeLayout);   // what was asked for, not what the misreading forced
    }
}
