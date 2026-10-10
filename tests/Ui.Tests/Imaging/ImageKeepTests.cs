// brief-img-2-image-source-and-kind.md §4 — where the picture is kept (R-im2-4, D4).

using System;
using System.IO;
using CircuitRF.Design.Imaging;
using Xunit;

namespace CircuitRF.Ui.Tests.Imaging;

public sealed class ImageKeepTests : IDisposable
{
    private readonly string _cell = Path.Combine(Path.GetTempPath(), "crf-imgkeep-" + Guid.NewGuid().ToString("N")[..12], "amp");

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_cell)!, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void NothingIsWrittenBeforeTheCall_ThenSameHashReused_DifferentHashSuffixed()
    {
        Directory.CreateDirectory(_cell);
        var first = ImageSource.FromBytes(Pictures.Png(Pictures.Schematic()), "clip.png").Source!;
        ImageKind.Classify(first.Raster);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_cell));      // reading and classifying wrote nothing

        var kept = ImageKeep.Into(_cell, "amp", first);
        Assert.Equal("amp.source.png", kept.Ref);
        Assert.False(kept.Reused);
        Assert.Equal(first.OriginalBytes, File.ReadAllBytes(kept.FullPath));
        Assert.Equal("../amp.source.png", kept.RefFrom(Path.Combine(_cell, "schematic")));

        var again = ImageKeep.Into(_cell, "amp", first);
        Assert.True(again.Reused);
        Assert.Equal(kept.FullPath, again.FullPath);

        var other = ImageSource.FromBytes(Pictures.Png(Pictures.Layout())).Source!;
        var second = ImageKeep.Into(_cell, "amp", other);
        Assert.Equal("amp.source-2.png", second.Ref);
        Assert.Equal(first.OriginalBytes, File.ReadAllBytes(kept.FullPath));    // the first copy is untouched
        Assert.Equal(2, Directory.GetFiles(_cell).Length);
    }
}
