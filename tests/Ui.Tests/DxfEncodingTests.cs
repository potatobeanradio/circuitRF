using System.Text;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Interchange;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Pure-logic gates for docs/sonnet-briefs/brief-dxf-version-support.md §2/R-dxf-2 — the version-driven
/// encoding policy, independent of any real file. DxfVersionToleranceTests.cs covers the same policy
/// end-to-end against real files from another tool (R-dxf-3).
/// </summary>
public class DxfEncodingTests
{
    private static Stream MinimalHeaderStream(string? acadVer, string? dwgCodePage)
    {
        using var sw = new StringWriter();
        var w = new DxfGroupWriter(sw);
        w.WriteString(0, "SECTION");
        w.WriteString(2, "HEADER");
        if (acadVer is not null)
        {
            w.WriteString(9, "$ACADVER");
            w.WriteString(1, acadVer);
        }
        if (dwgCodePage is not null)
        {
            w.WriteString(9, "$DWGCODEPAGE");
            w.WriteString(3, dwgCodePage);
        }
        w.WriteString(0, "ENDSEC");
        w.WriteString(0, "SECTION");
        w.WriteString(2, "ENTITIES");
        w.WriteString(0, "ENDSEC");
        w.WriteString(0, "EOF");
        return new MemoryStream(Encoding.UTF8.GetBytes(sw.ToString()));
    }

    [Theory]
    [InlineData("AC1021")] // R2007 — the first UTF-8 generation
    [InlineData("AC1024")] // R2010
    [InlineData("AC1032")] // R2018
    public void Resolve_R2007OrLater_IsUtf8(string acadVer)
    {
        using var stream = MinimalHeaderStream(acadVer, null);
        var res = DxfEncoding.Resolve(stream);
        Assert.True(res.IsUtf8);
        Assert.Same(Encoding.UTF8, res.Encoding);
        Assert.Contains("UTF-8", res.Report);
    }

    [Theory]
    [InlineData("AC1009")] // R12
    [InlineData("AC1015")] // R2000
    [InlineData("AC1018")] // R2004
    public void Resolve_R2006OrEarlier_IsLegacyCodePage(string acadVer)
    {
        using var stream = MinimalHeaderStream(acadVer, "ANSI_1252");
        var res = DxfEncoding.Resolve(stream);
        Assert.False(res.IsUtf8);
        Assert.Same(DxfEncoding.Windows1252, res.Encoding);
        Assert.False(res.CodePageWasAbsent);
        Assert.False(res.CodePageWasUnsupported);
        Assert.Contains("ANSI_1252", res.Report);
    }

    [Fact]
    public void Resolve_CodePageAbsent_FallsBackToDocumentedDefault_Reports()
    {
        using var stream = MinimalHeaderStream("AC1015", null);
        var res = DxfEncoding.Resolve(stream);
        Assert.True(res.CodePageWasAbsent);
        Assert.False(res.CodePageWasUnsupported);
        Assert.Contains("absent", res.Report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(DxfEncoding.DefaultCodePageName, res.Report);
    }

    [Fact]
    public void Resolve_UnrecognizedCodePageName_FallsBackAndReportsWhichNameWasFound()
    {
        using var stream = MinimalHeaderStream("AC1015", "ANSI_1251"); // Cyrillic — no dedicated table
        var res = DxfEncoding.Resolve(stream);
        Assert.True(res.CodePageWasUnsupported);
        Assert.Same(DxfEncoding.Windows1252, res.Encoding); // still decodes via the fallback table
        Assert.Contains("ANSI_1251", res.Report);
        Assert.Contains(DxfEncoding.DefaultCodePageName, res.Report);
    }

    /// <summary>R-dxf-4: "the import path reports the $ACADVER it found" — the raw code, not only a
    /// friendly display name, so a user (or a future maintainer) can always trace a report back to the
    /// literal header value the file actually declared.</summary>
    [Fact]
    public void Resolve_Report_AlwaysNamesTheRawAcadVerCodeFound()
    {
        using var utf8Stream = MinimalHeaderStream("AC1032", null);
        Assert.Contains("$ACADVER=AC1032", DxfEncoding.Resolve(utf8Stream).Report);

        using var legacyStream = MinimalHeaderStream("AC1009", "ANSI_1252");
        Assert.Contains("$ACADVER=AC1009", DxfEncoding.Resolve(legacyStream).Report);

        using var noVersionStream = MinimalHeaderStream(null, null);
        Assert.Contains("$ACADVER not found", DxfEncoding.Resolve(noVersionStream).Report);
    }

    [Fact]
    public void Resolve_MissingAcadVer_DefaultsToLegacyCodePage_NeverThrows()
    {
        using var stream = MinimalHeaderStream(null, null);
        var res = DxfEncoding.Resolve(stream);
        Assert.False(res.IsUtf8);
        Assert.Contains("unknown", res.VersionDisplayName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_NonSeekableStream_DefaultsToUtf8_NeverThrows()
    {
        using var inner = MinimalHeaderStream("AC1009", "ANSI_1252");
        using var nonSeekable = new NonSeekableStream(inner);
        var res = DxfEncoding.Resolve(nonSeekable);
        Assert.True(res.IsUtf8);
        Assert.Contains("rewound", res.Report, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void EscapeNonAscii_PureAscii_NoOp()
    {
        string escaped = DxfEncoding.EscapeNonAscii("Top Copper", out bool any);
        Assert.False(any);
        Assert.Equal("Top Copper", escaped);
    }

    [Fact]
    public void EscapeNonAscii_NonAscii_EscapesEveryCharOutsideAscii()
    {
        string escaped = DxfEncoding.EscapeNonAscii("Défense Ω", out bool any);
        Assert.True(any);
        Assert.Equal("D\\U+00E9fense \\U+03A9", escaped);
    }

    [Fact]
    public void Unescape_RoundTripsExactly_CaseInsensitiveHex()
    {
        Assert.Equal("Défense Ω", DxfEncoding.Unescape("D\\U+00E9fense \\U+03A9"));
        Assert.Equal("Défense Ω", DxfEncoding.Unescape("D\\u+00e9fense \\u+03a9")); // lowercase U and hex
    }

    [Fact]
    public void Unescape_PlainAsciiText_Unchanged()
    {
        Assert.Equal("Top Copper", DxfEncoding.Unescape("Top Copper"));
    }

    [Fact]
    public void EscapeThenUnescape_IsIdentity_ForAWideRangeOfText()
    {
        string[] samples = ["Défense", "Résistance Ω", "日本語レイヤー", "plain ascii", ""];
        foreach (var s in samples)
        {
            string escaped = DxfEncoding.EscapeNonAscii(s, out _);
            Assert.Equal(s, DxfEncoding.Unescape(escaped));
        }
    }

    [Theory]
    [InlineData(0xE9, "é")]  // Windows-1252 == Latin-1 in this range
    [InlineData(0x41, "A")]
    [InlineData(0x80, "€")]  // the ONE range Windows-1252 differs from Latin-1
    [InlineData(0x93, "“")] // left double quotation mark
    public void Windows1252_DecodesKnownBytes_Correctly(byte b, string expectedChar)
    {
        string decoded = DxfEncoding.Windows1252.GetString([b]);
        Assert.Equal(expectedChar, decoded);
    }

    [Fact]
    public void Windows1252_UndefinedBytes_RoundTripToOwnCodePoint_NeverThrows()
    {
        // 0x81, 0x8D, 0x8F, 0x90, 0x9D are genuinely unassigned in real Windows-1252.
        foreach (byte b in new byte[] { 0x81, 0x8D, 0x8F, 0x90, 0x9D })
        {
            string decoded = DxfEncoding.Windows1252.GetString([b]);
            Assert.Equal((char)b, decoded[0]);
        }
    }

    [Fact]
    public void Windows1252_FullByteRange_NeverThrows_RoundTripsThroughAsciiAndLatin1Range()
    {
        for (int b = 0; b <= 0xFF; b++)
        {
            string decoded = DxfEncoding.Windows1252.GetString([(byte)b]);
            Assert.Single(decoded);
            if (b <= 0x7F || b >= 0xA0) Assert.Equal((char)b, decoded[0]); // identical to ASCII/Latin-1 here
        }
    }

    // ── Gate 2's full round trip: OUR OWN writer -> OUR OWN reader, non-ASCII layer AND label ─────────
    // (DxfVersionToleranceTests.cs separately covers importing non-ASCII text from a THIRD-PARTY writer;
    // this is the other direction the gate names — export -> import through this codebase's own pair.)

    [Fact]
    public void OwnWriterThenOwnReader_NonAsciiLayerNameAndLabelText_SurviveExportImportIntact()
    {
        var layerKey = new LayerKey(1, 0);
        var tech = new Technology
        {
            Layers = { new LayerDef { Key = layerKey, Name = "Défense Ω" } },
        };
        var label = new LabelShape { Layer = layerKey, X = 0, Y = 0, Height = 1000, Text = "Résistance Ω/µF" };

        var structures = new List<InterchangeStructure> { new("TOP", [label], []) };
        using var sw = new StringWriter();
        var summary = DxfWriter.Write(sw, structures, "TOP", tech, 1000, new DxfExportOptions());
        string text = sw.ToString();

        // The LAYER table's own name (from tech), the label entity's own layer (group 8) reference, and
        // the TEXT content itself all carry non-ASCII -> all three escape.
        Assert.True(summary.NonAsciiTextEscaped >= 3);
        Assert.Contains("\\U+00E9", text); // 'é' — shared by "Défense" and "Résistance"
        Assert.Contains("\\U+03A9", text); // 'Ω' — shared by both too
        Assert.Contains("\\U+00B5", text); // 'µ' — MICRO SIGN, label-only

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        using var textReader = new StreamReader(stream);
        var reader = DxfReader.Read(textReader);
        var importedLabel = Assert.IsType<LabelShape>(
            Assert.Single(reader.Structures.Single(s => s.Name == "TOP").Shapes).Shape);
        Assert.Equal("Résistance Ω/µF", importedLabel.Text);
    }
}
