// Low-level DXF ASCII group-code I/O (docs/sonnet-briefs/brief-L4b-dxf-interchange.md). DXF is a
// plain-text, line-oriented format: every record is a group code line followed by a value line.
// Streams one group at a time — mirrors GdsiiRecordIo's "never materialize the whole file" discipline,
// even though DXF's own text nature makes buffering far cheaper than GDSII's binary records would be.
// Written from the public ASCII DXF group-code specification — no DXF library dependency.

using System.Globalization;

namespace CircuitRF.Ui.Layout.Interchange;

/// <summary>One (group code, raw string value) pair — the unit DXF's ASCII format is built from.</summary>
public readonly struct DxfGroup(int code, string value)
{
    public int Code { get; } = code;
    public string Value { get; } = value;

    public double AsDouble() => double.Parse(Value, NumberStyles.Float, CultureInfo.InvariantCulture);
    public int AsInt() => int.Parse(Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
    public long AsLong() => long.Parse(Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
}

/// <summary>Reads DXF groups two lines at a time from a <see cref="TextReader"/> — never materializes
/// the whole file. Tolerates trailing whitespace/CRLF on either line (files roam across platforms).</summary>
public sealed class DxfGroupReader(TextReader reader)
{
    public bool TryReadNext(out DxfGroup group)
    {
        group = default;
        string? codeLine = reader.ReadLine();
        if (codeLine is null) return false;

        string? valueLine = reader.ReadLine();
        if (valueLine is null) return false; // truncated file — the last group is dropped, not thrown

        if (!int.TryParse(codeLine.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
            return false;

        group = new DxfGroup(code, valueLine.Trim('\r'));
        return true;
    }
}

/// <summary>Writes DXF groups to a <see cref="TextWriter"/> as they are built. Numeric formatting uses
/// round-trippable invariant-culture text (§the excess-64-GDSII-real class of bug this brief has no
/// analogue of — DXF numbers are plain ASCII decimal, so there is no custom binary encoding to get
/// wrong, only enough precision to round-trip exactly).</summary>
public sealed class DxfGroupWriter(TextWriter writer)
{
    /// <summary>
    /// Count of <b>USER-AUTHORED</b> text values that contained non-ASCII characters and were therefore
    /// `\U+XXXX`-escaped (docs/sonnet-briefs/brief-dxf-version-support.md R-dxf-2) — read by
    /// <c>DxfWriter.Write</c> at the end to populate <see cref="DxfExportSummary"/>, so escaping of
    /// THEIR data is reported rather than silent.
    ///
    /// <para><b>Text this writer generates itself does not count</b> — see
    /// <see cref="WriteGeneratedString"/>. Owner report, 2026-08-27: exporting a layout whose only
    /// content was one ruler produced "1 non-ASCII text value(s) … will be escaped", on a file
    /// containing no non-ASCII character at all. The Δ in our own <c>Δx / Δy</c> readout was being
    /// reported back to the user as a fidelity caveat about their drawing. It is a caveat about a
    /// choice this writer made, the user cannot act on it, and nothing of theirs was affected.</para>
    /// </summary>
    public int EscapedTextCount { get; private set; }

    public void WriteString(int code, string value)
    {
        writer.Write(code);
        writer.Write('\n');
        writer.Write(value);
        writer.Write('\n');
    }

    /// <summary>For genuinely free-form user text (layer/block names, TEXT content) — R2000 output has
    /// no native Unicode text (that arrived in R2007/AC1021 with UTF-8), so any character outside ASCII
    /// is escaped as `\U+XXXX`, AutoCAD's own established convention for this exact situation, rather
    /// than written as a raw code-page byte that would only round-trip correctly for readers sharing
    /// the exact same code page. Never call this for a fixed keyword string (entity types, subclass
    /// markers, "CONTINUOUS", …) — <see cref="WriteString"/> is fine (and marginally cheaper) for those,
    /// since escaping is a no-op for pure ASCII anyway but the call is the documentation of intent.</summary>
    public void WriteEscapedString(int code, string value)
    {
        string encoded = DxfEncoding.EscapeNonAscii(value, out bool anyEscaped);
        if (anyEscaped) EscapedTextCount++;
        WriteString(code, encoded);
    }

    /// <summary>
    /// Escapes exactly like <see cref="WriteEscapedString"/> — so the bytes on disk are identical and
    /// just as conformant — but does <b>not</b> count toward <see cref="EscapedTextCount"/>.
    ///
    /// <para>For text THIS WRITER composes (a dimension's own readout, its <c>Δx / Δy</c> line, a
    /// <c>µm</c> unit suffix), as opposed to a string the user typed. The escaping still has to happen
    /// and still has to be correct: <c>\U+0394</c> is AutoCAD's own convention and a real reader
    /// renders it as Δ. What must not happen is reporting our own spelling to the user as though their
    /// drawing contained something that needed handling.</para>
    /// </summary>
    public void WriteGeneratedString(int code, string value) =>
        WriteString(code, DxfEncoding.EscapeNonAscii(value, out _));

    public void WriteInt(int code, int value) => WriteString(code, value.ToString(CultureInfo.InvariantCulture));

    public void WriteDouble(int code, double value) =>
        WriteString(code, value.ToString("G17", CultureInfo.InvariantCulture));

    /// <summary>DBU (long) coordinate written as a DXF real, scaled to drawing units by
    /// <paramref name="dbuToDrawingUnit"/> — the ONE place a coordinate crosses from our integer DBU
    /// space into DXF's floating drawing-unit space.</summary>
    public void WriteCoord(int code, long dbu, double dbuToDrawingUnit) =>
        WriteDouble(code, dbu * dbuToDrawingUnit);
}
