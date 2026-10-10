// brief-img-7-schematic-wires-and-regions.md R-im7-8 — a picture of words only has no wires to read.

using CircuitRF.Design.Imaging;
using CircuitRF.Design.Schematic.Recognition;
using Xunit;

namespace CircuitRF.Ui.Tests.Imaging;

public sealed class SchematicImageRefusalTests
{
    [Fact]
    public void APictureOfWordsOnly_RefusesWithTheNoWiresSentence()
    {
        var img = Pictures.Draw(320, 120, c =>
        {
            SchematicPictures.Text(c, "NOTES ON THE DESIGN", 20, 40);
            SchematicPictures.Text(c, "SEE THE BOARD FILE", 20, 80);
        });
        var source = ImageSource.FromBytes(Pictures.Png(img));
        Assert.True(source.Ok, source.Refusal);

        var r = SchematicImageReading.Read(source.Source!);

        Assert.Equal(SchematicImageReport.NoWiresRefusal, r.Refusal);
        Assert.NotEmpty(r.Words);
    }
}
