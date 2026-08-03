using CircuitRF.Core.Pdk;
using Xunit;

namespace CircuitRF.Core.Tests.Pdk;

/// <summary>
/// A symbol LIBRARY holds several named symbols in one file, and parts name the one they want. A
/// handful of templates can serve a whole kit, so reading this one file is what makes its parts
/// placeable — and the same templates are what the palette shows.
///
/// <para>Fixtures are built here, byte by byte. The tags are the format's own structure names; no
/// supplier, kit or part is named anywhere in this file.</para>
/// </summary>
public class KitSymbolLibraryReaderTests
{
    /// <summary>Builds a library the way the format lays one out.</summary>
    private static byte[] Library(params (string Name, (string Pin, int X, int Y)[] Pins)[] symbols)
    {
        var bytes = new List<byte>();
        foreach (var (name, pins) in symbols)
        {
            bytes.AddRange("KDefaultSymb_2"u8.ToArray());
            bytes.AddRange([0x00, 0x00, 0x20, 0x20]);                    // padding before the name
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(name + "@Sample.lib"));
            bytes.AddRange([0x00, 0x01, 0x02]);
            foreach (var (pin, x, y) in pins)
            {
                bytes.AddRange("KNodePos"u8.ToArray());
                void I32(int v) => bytes.AddRange(BitConverter.GetBytes(v));
                I32(3); I32(x); I32(y); I32(-100); I32(-50); I32(0); I32(1);
                bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(pin));
                bytes.Add(0x00);
            }
        }
        return [.. bytes];
    }

    [Fact]
    public void EverySymbolAndEveryPinIsRead()
    {
        var lib = KitSymbolLibraryReader.Read(Library(
            ("ThreeTerm", [("1", 1000, 0), ("2", 500, 500), ("3", 0, 0)]),
            ("TwoTerm",   [("1", 0, 0), ("2", 1000, 0)])));

        Assert.Equal(["ThreeTerm", "TwoTerm"], lib.Select(s => s.Name));
        Assert.Equal(3, lib[0].Pins.Count);
        Assert.Equal(2, lib[1].Pins.Count);
    }

    [Fact]
    public void PinPositionsAndNamesSurvive()
    {
        var s = Assert.Single(KitSymbolLibraryReader.Read(Library(
            ("Part", [("A", 1000, 0), ("B", 500, -500)]))));

        Assert.Equal(new KitSymbolPin("A", 1000, 0),   s.Pins[0]);
        Assert.Equal(new KitSymbolPin("B", 500, -500), s.Pins[1]);
    }

    [Fact]
    public void NegativeCoordinatesAreSigned_NotHuge()
    {
        // Read as unsigned this comes back as ~4.29 billion, and the symbol lands off-canvas rather
        // than to the left of the origin — a wrong drawing rather than an error.
        var s = Assert.Single(KitSymbolLibraryReader.Read(Library(
            ("Part", [("1", -1200, -1100)]))));

        Assert.Equal(-1200, s.Pins[0].X);
        Assert.Equal(-1100, s.Pins[0].Y);
    }

    [Fact]
    public void ASymbolNameIsTheRunQualifiedByAt_NotWhateverPaddingCameFirst()
    {
        var s = Assert.Single(KitSymbolLibraryReader.Read(Library(("IQ Mixer", [("1", 0, 0)]))));
        Assert.Equal("IQ Mixer", s.Name);
    }

    [Fact]
    public void ARecordDelimiterIsNotPartOfThePinName()
    {
        // Records are bracket-framed, so a pin name sits hard against the terminator of its own
        // record and the opener of the next. Taking every printable byte reads a pin called "1" as
        // "1][" — no crash, nothing obviously wrong in a dump, every pin on every symbol renamed.
        // Found against a real library, not by inspection.
        var bytes = new List<byte>();
        bytes.AddRange("KDefaultSymb_2"u8.ToArray());
        bytes.AddRange(System.Text.Encoding.ASCII.GetBytes("Part@Sample.lib"));
        bytes.AddRange("KNodePos"u8.ToArray());
        foreach (int v in new[] { 3, 1000, 0, -100, -50, 0, 1 })
            bytes.AddRange(BitConverter.GetBytes(v));
        bytes.AddRange(System.Text.Encoding.ASCII.GetBytes("1]["));   // name, close, next open

        var s = Assert.Single(KitSymbolLibraryReader.Read([.. bytes]));
        Assert.Equal("1", Assert.Single(s.Pins).Name);
    }

    [Fact]
    public void APinWithAnUnREADABLEName_StillCounts()
    {
        // Losing it would change the terminal COUNT, which is the one thing everything downstream
        // relies on — and a part with the wrong number of pins wires up wrongly and still runs.
        var bytes = new List<byte>();
        bytes.AddRange("KDefaultSymb_2"u8.ToArray());
        bytes.AddRange(System.Text.Encoding.ASCII.GetBytes("Part@Sample.lib"));
        bytes.AddRange("KNodePos"u8.ToArray());
        foreach (int v in new[] { 3, 10, 20, -100, -50, 0, 1 })
            bytes.AddRange(BitConverter.GetBytes(v));
        bytes.AddRange([0x00, 0x00, 0x00, 0x00]);          // no readable name at all
        bytes.AddRange(new byte[16]);

        var s = Assert.Single(KitSymbolLibraryReader.Read([.. bytes]));
        var pin = Assert.Single(s.Pins);
        Assert.Equal(10, pin.X);
        Assert.Equal("1", pin.Name);                        // positional fallback, not dropped
    }

    [Fact]
    public void ASymbolWithNoPinsIsNotOffered()
    {
        // A template nothing can be wired to is not a symbol; offering it would put an unusable
        // entry in the palette.
        var bytes = new List<byte>();
        bytes.AddRange("KDefaultSymb_2"u8.ToArray());
        bytes.AddRange(System.Text.Encoding.ASCII.GetBytes("Empty@Sample.lib"));
        bytes.AddRange(new byte[8]);

        Assert.Empty(KitSymbolLibraryReader.Read([.. bytes]));
    }

    [Fact]
    public void BytesThatAreNotALibraryReadAsNothing()
    {
        Assert.Empty(KitSymbolLibraryReader.Read("just some text, no records here"u8));
        Assert.Empty(KitSymbolLibraryReader.Read([]));
    }

    [Fact]
    public void ATruncatedPinRecordDoesNotReadPastTheEnd()
    {
        var bytes = new List<byte>();
        bytes.AddRange("KDefaultSymb_2"u8.ToArray());
        bytes.AddRange(System.Text.Encoding.ASCII.GetBytes("Part@Sample.lib"));
        bytes.AddRange("KNodePos"u8.ToArray());
        bytes.AddRange(BitConverter.GetBytes(3));            // record cut off mid-way

        var lib = KitSymbolLibraryReader.Read([.. bytes]);   // must not throw
        Assert.Empty(lib);
    }

    [Fact]
    public void AMissingFileReadsAsNothing() =>
        Assert.Empty(KitSymbolLibraryReader.TryReadFile(
            Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N"))));
}
