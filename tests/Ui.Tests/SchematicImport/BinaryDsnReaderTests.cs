// brief-schimp-4-binary-dsn-reader.md R-si4-3/5/6 — the binary .DSN reader, against containers built by the tests:
// the refusals, and the symbol cache's graphics.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CircuitRF.Design.Schematic.Import;
using CircuitRF.Design.Schematic.Import.BinaryDsn;
using Xunit;

namespace CircuitRF.Ui.Tests.ImportSchematic;

public sealed class BinaryDsnReaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("crf-schimp4-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Write(string name, IReadOnlyDictionary<string, byte[]> streams)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, CfbTestWriter.Build(streams));
        return path;
    }

    /// <summary>A Library stream stating version <paramref name="major"/>.<paramref name="minor"/>.</summary>
    private static byte[] Library(int major, int minor)
    {
        var b = new byte[64];
        Encoding.ASCII.GetBytes("Windows Design".PadRight(31)).CopyTo(b, 0);
        b[32] = (byte)major;
        b[34] = (byte)minor;
        return b;
    }

    /// <summary>R-si4-3: a version not read is refused by number, with the EDIF route — and classified as the binary
    /// design it is, never as something else.</summary>
    [Fact]
    public void AnUnreadVersion_IsRefusedByNumber_WithTheEdifRoute()
    {
        string path = Write("future.dsn", new Dictionary<string, byte[]>
        {
            ["Library"] = Library(9, 1),
            ["Cache"] = new byte[8],
            ["Views/SCHEMATIC1/Pages/PAGE1"] = new byte[8],
        });
        var read = SchematicImport.Read(path);
        Assert.Equal(SchematicFileKind.BinaryDsn, read.Kind);
        Assert.False(read.Ok);
        Assert.Contains("version (9.1) is not readable", read.Refusal);
        Assert.Contains("EDIF 2.0.0", read.Refusal);
    }

    /// <summary>A compound file that is not a design — a spreadsheet, a symbol library — is said to be one, not misread.</summary>
    [Fact]
    public void ACompoundFileThatIsNotADesign_IsRefusedAsSuch()
    {
        string path = Write("book.dsn", new Dictionary<string, byte[]> { ["Workbook"] = new byte[600] });
        var read = BinaryDsnReader.Read(path);
        Assert.False(read.Ok);
        Assert.Contains("not a schematic design", read.Refusal);
    }

    /// <summary>A version-3 cache holding one symbol named <paramref name="name"/> with <paramref name="graphics"/>, each a
    /// (type, payload) as the cache stores them: the type twice, the payload's size, a zero, the payload.</summary>
    private static byte[] V3Cache(string name, params (byte Type, byte[] Payload)[] graphics)
    {
        var b = new List<byte>();
        void U16(int v) => b.AddRange(BitConverter.GetBytes((ushort)v));
        void U32(int v) => b.AddRange(BitConverter.GetBytes(v));
        void Str(string v) { U16(v.Length); b.AddRange(Encoding.ASCII.GetBytes(v)); b.Add(0); }
        b.AddRange([0xFF, 0xE4, 0x5C, 0x39]);
        U32(0);                                  // the body's counted block, empty
        Str(name);
        Str("parts.olb");
        U32(0);                                  // colour
        U16(graphics.Length);
        foreach (var (type, payload) in graphics)
        {
            b.Add(type);
            b.Add(type);
            U32(payload.Length);
            U32(0);
            b.AddRange(payload);
        }
        U32(0);
        U16(30);
        U16(20);
        U16(0);                                  // no pins
        return [.. b];
    }

    private static byte[] Ints(params int[] values) => [.. values.SelectMany(BitConverter.GetBytes)];

    private static byte[] Points(params (short A, short B)[] stored) =>
        [.. BitConverter.GetBytes((ushort)stored.Length), .. stored.SelectMany(p => BitConverter.GetBytes(p.A).Concat(BitConverter.GetBytes(p.B)))];

    /// <summary>A polyline's and a polygon's points are stored Y first, read as (x, y) a diode's triangle points down out of its
    /// body; a rectangle and a polygon carry their fill (0 solid, 1 none) after style and width.</summary>
    [Fact]
    public void TheCache_ReadsPointsYFirst_AndRectanglesAndPolygonsWithTheirFill()
    {
        var cache = new DsnCache(V3Cache("DIODE.Normal",
            (0x28, Ints(0, 0, 30, 20, 0, 3, 1, 0)),
            (0x2C, [.. Ints(0, 0, 0, 0), .. Points((10, 20), (17, 10), (3, 10), (10, 20))]),
            (0x2D, [.. Ints(0, 0), .. Points((1, 18), (8, 19))])), new EdifText(1252), 3);

        var symbol = cache.Find("diode.normal");

        Assert.NotNull(symbol);
        Assert.Equal(0, symbol!.SkippedGraphics);
        Assert.Equal(new DsnRectangle(0, 0, 30, 20, Filled: false), symbol.Graphics[0]);
        var triangle = Assert.IsType<DsnPolygon>(symbol.Graphics[1]);
        Assert.True(triangle.Filled);
        Assert.Equal([(20, 10), (10, 17), (10, 3), (20, 10)], triangle.Points);
        Assert.Equal([(18, 1), (19, 8)], Assert.IsType<DsnPolyline>(symbol.Graphics[2]).Points);
    }

    private static readonly byte[] Magic = [0xFF, 0xE4, 0x5C, 0x39];

    /// <summary>
    /// A sized prefix <c>0D 2B 00 00 00 00 00 00 00</c> also reads as a final prefix claiming 0x2B pairs, and a pin's own end
    /// marker can sit exactly 43 × 8 bytes on. The header ends at the NEAREST final prefix: the record's one real pair is
    /// read and its body starts after it (a field design's part read past its page's end before this).
    /// </summary>
    [Fact]
    public void ARecordHeader_EndsAtTheNearestFinalPrefix_WhenASizedPrefixAlsoReadsAsOne()
    {
        var b = new List<byte>();
        void U32(int v) => b.AddRange(BitConverter.GetBytes(v));
        b.Add(0x0D); U32(0); U32(0);                       // outer sized prefix; size patched below
        b.Add(0x0D); U32(0x2B); U32(0);                    // a sized prefix that also reads as "43 pairs"
        b.Add(0x0D); b.AddRange([1, 0]); U32(7); U32(8); b.AddRange(Magic);   // the real final prefix: one pair
        int body = b.Count;
        while (b.Count < 9 + 3 + 8 * 0x2B) b.Add(0x10);
        b.AddRange(Magic);                                 // a pin record's marker, where "43 pairs" would end
        b.AddRange(new byte[16]);
        var bytes = b.ToArray();
        BitConverter.GetBytes(bytes.Length - 9).CopyTo(bytes, 1);

        var r = new DsnBytes(bytes, "test", new EdifText(1252));
        var (_, _, pairs) = r.Record(0x0D);

        Assert.Equal([(7, 8)], pairs);
        Assert.Equal(body, r.Pos);
    }

    /// <summary>
    /// A version-3 page with a bus: the list after the nets holds a 0x35 record followed, OUTSIDE its stated size, by a u16
    /// count and that many u32 member net ids, and the wires include a 0x15 bus wire. Skipped by size alone, the member list
    /// was read as a record header and a field design was refused; the page now reads, its one ordinary wire kept and the bus
    /// wire left out and said.
    /// </summary>
    [Fact]
    public void AV3PageWithABus_Reads_TheBusWireLeftOut()
    {
        var b = new List<byte>();
        void U16(int v) => b.AddRange(BitConverter.GetBytes((ushort)v));
        void U32(int v) => b.AddRange(BitConverter.GetBytes(v));
        void Str(string v) { U16(v.Length); b.AddRange(Encoding.ASCII.GetBytes(v)); b.Add(0); }
        void Record(byte type, Action body)
        {
            int at = b.Count;
            b.Add(type); U32(0); U32(0);
            U32(0);                                        // the counted block, empty
            body();
            BitConverter.GetBytes(b.Count - at - 9).CopyTo(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(b)[(at + 1)..]);
        }
        void Wire(byte type, int id, int net) => Record(type, () => { U32(id); U32(net); U32(0); U32(0); U32(0); U32(100); U32(0); b.Add(0); U16(0); });

        Record(0x0A, () =>
        {
            Str("RF"); Str("A3"); b.AddRange(new byte[8]);
            var head = new byte[150];
            BitConverter.GetBytes(1100).CopyTo(head, 16); BitConverter.GetBytes(850).CopyTo(head, 20); BitConverter.GetBytes(100).CopyTo(head, 24);
            b.AddRange(head);
            U16(1); Record(0x34, () => U32(7));            // nets
            U16(1); Record(0x35, () => U32(9));            // buses: one, with two members after it
            U16(2); U32(7); U32(8);
            U16(1); Str("RF_IN"); U32(7);                  // net names
            U16(2); Wire(0x14, 1, 7); Wire(0x15, 2, 9);    // wires: one ordinary, one on the bus
            for (int i = 0; i < 9; i++) U16(0);            // parts, ports, globals, off-page, and the lists after them
        });

        var page = DsnPageV3.Read(new DsnBytes([.. b], "page", new EdifText(1252)), new DsnCache([], new EdifText(1252), 3));

        Assert.Equal(7u, Assert.Single(page.Wires).Net);
        Assert.Contains(page.Skipped, s => s.StartsWith("1 bus wire segment", StringComparison.Ordinal));
    }

    /// <summary>The Hierarchy stream's occurrence records give each placed part the reference the design SHOWS, keyed by
    /// instance id: after the end marker and a counted block (empty, or a part's 37-byte display block), u32, the id, and
    /// the reference 16 bytes on. An empty reference, and an id met twice, give nothing.</summary>
    [Fact]
    public void TheHierarchyStream_GivesEachInstanceTheReferenceTheDesignShows()
    {
        var b = new List<byte>();
        void U32(int v) => b.AddRange(BitConverter.GetBytes(v));
        void Occurrence(int id, string reference, int block = 0)
        {
            b.AddRange([0x42, 0, 0]); b.AddRange(Magic);
            U32(block); b.AddRange(new byte[block]);
            U32(4000);                                     // the record's own id
            U32(id);
            b.Add(0x42); U32(0x7E); U32(0); b.AddRange(new byte[3]);   // nested prefix and 3 bytes
            b.AddRange(BitConverter.GetBytes((ushort)reference.Length)); b.AddRange(Encoding.ASCII.GetBytes(reference)); b.Add(0);
        }
        Occurrence(0x12B684, "Y1");
        Occurrence(0x10B9E5, "TP115", block: 37);
        Occurrence(0x189710, "");                          // keeps its page reference
        Occurrence(0x0C2F89, "C5");
        Occurrence(0x0C2F89, "C6");                        // placed twice: not used

        var shown = DsnOccurrences.Read([.. b], new EdifText(1252));

        Assert.Equal(new Dictionary<uint, string> { [0x12B684] = "Y1", [0x10B9E5] = "TP115" }, shown);
    }
}
