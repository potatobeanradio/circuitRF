// brief-img-8-schematic-symbols.md R-im8-3 — each convention read with its kind and its orientation, and a diode's pins
// in the order its drawing gives them.

using System;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Schematic.Recognition;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.Imaging;

public sealed class SymbolMatchTests
{
    private static Action<SKCanvas, SKPaint> Drawing(string name) => name switch
    {
        "us-resistor" => SymbolPictures.UsResistor,
        "iec-resistor" => SymbolPictures.IecResistor,
        "coil" => SymbolPictures.Coil,
        "iec-inductor" => SymbolPictures.IecInductor,
        "flat-capacitor" => SymbolPictures.FlatCapacitor,
        "curved-capacitor" => SymbolPictures.CurvedCapacitor,
        "ground" => SymbolPictures.Ground,
        "terminal" => SymbolPictures.Terminal,
        "box-line" => SymbolPictures.BoxLine,
        "diode" => SymbolPictures.Diode,
        _ => throw new ArgumentException(name),
    };

    [Theory]
    [InlineData("us-resistor", ImageSymbolKind.Resistor)]
    [InlineData("iec-resistor", ImageSymbolKind.Resistor)]
    [InlineData("coil", ImageSymbolKind.Inductor)]
    [InlineData("iec-inductor", ImageSymbolKind.Inductor)]
    [InlineData("flat-capacitor", ImageSymbolKind.Capacitor)]
    [InlineData("curved-capacitor", ImageSymbolKind.Capacitor)]
    [InlineData("ground", ImageSymbolKind.Ground)]
    [InlineData("terminal", ImageSymbolKind.Port)]
    [InlineData("box-line", ImageSymbolKind.TransmissionLine)]
    [InlineData("diode", ImageSymbolKind.Diode)]
    public void EachConvention_ReadsItsKindAndOrientation_At0And90Degrees(string drawing, ImageSymbolKind kind)
    {
        foreach (var (deg, rotation) in new[] { (0f, SymbolRotation.R0), (90f, SymbolRotation.R90) })
        {
            var r = SymbolPictures.Read(SymbolPictures.Picture(Drawing(drawing), deg));

            var s = Assert.Single(r.Symbols);
            Assert.Equal(SymbolDisposition.Recognised, s.Disposition);
            Assert.Equal(kind, s.Kind);
            Assert.Equal(new SymbolOrientation(rotation, false), s.Orientation);
        }
    }

    [Fact]
    public void AMirroredDiode_ReadsItsPinsInTheMirroredOrder()
    {
        // At 90° the anode is on the right; mirrored left to right, on the left.
        var plain = Assert.Single(SymbolPictures.Read(SymbolPictures.Picture(SymbolPictures.Diode, 90)).Symbols);
        var mirrored = Assert.Single(SymbolPictures.Read(SymbolPictures.Picture(SymbolPictures.Diode, 90, mirror: true)).Symbols);

        Assert.Equal(ImageSymbolKind.Diode, mirrored.Kind);
        Assert.True(plain.Pins[0].X > plain.Pins[1].X);
        Assert.True(mirrored.Pins[0].X < mirrored.Pins[1].X);
        Assert.Equal(plain.Pins[0].Name, mirrored.Pins[0].Name);
    }
}
