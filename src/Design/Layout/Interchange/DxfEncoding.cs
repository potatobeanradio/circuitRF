// DXF text encoding policy (docs/sonnet-briefs/brief-dxf-version-support.md §2, R-dxf-2). DXF's own
// encoding is genuinely version-dependent, and until this brief it was only IMPLICIT: `DxfImport` opened
// every file with a plain `StreamReader(Stream)`, which defaults to UTF-8 (with BOM detection) regardless
// of what the file's own header actually declares. That is a latent bug in both directions:
//
//   - R2007 (AC1021) and later really are UTF-8.
//   - R2006 (AC1018) and earlier use the DRAWING'S OWN code page, named in `$DWGCODEPAGE`, with
//     `\U+XXXX` escapes (AutoCAD's own established convention) for any character outside it.
//
// Reading an R2000 code-page file as UTF-8 (the old default) — or an R2018 UTF-8 file as a code page —
// never throws; it silently produces mojibake. Fixed by sniffing `$ACADVER`/`$DWGCODEPAGE` from the
// HEADER section BEFORE deciding how to decode the rest of the file (a real two-pass read over the
// stream — confirmed compatible with the streaming discipline precisely BECAUSE both header variables
// are always plain ASCII identifiers, so a byte-transparent first pass can read them correctly
// regardless of the file's real encoding, then the stream is rewound and re-read with the resolved
// encoding for the genuine parse).
//
// No new dependency (the brief's own guardrail): a correct .NET decode of an arbitrary named Windows code
// page needs `System.Text.Encoding.CodePages` (`.NET Core` ships only Unicode transforms + Latin-1 out of
// the box). Rather than add that package, `Windows1252` below is a small, hand-written, from-the-public-
// spec single-byte codec — exact for Windows-1252 (`ANSI_1252`), the code page named by the overwhelming
// majority of Western-European/US AutoCAD installs and this reader's own documented default. Windows-1252
// differs from the built-in `Encoding.Latin1` ONLY in the 0x80-0x9F byte range (smart quotes, dashes, the
// euro sign, ...); every other named code page still decodes through this SAME table, since 0x00-0x7F and
// 0xA0-0xFF match virtually every single-byte Windows code page identically — correct for the ASCII-heavy
// overwhelming majority of real layer/label text, with the substitution always reported, never silent.

using System.Text;

namespace CircuitRF.Design.Layout.Interchange;

/// <summary>What <see cref="DxfEncoding.Resolve"/> decided, and why — carried into <c>DxfImport</c>'s own
/// message list so a user learns the encoding assumption the same way they learn the units assumption.</summary>
public readonly record struct DxfEncodingResolution(
    Encoding Encoding,
    string? AcadVersionRaw,
    string VersionDisplayName,
    bool IsUtf8,
    string? CodePageNameFound,
    bool CodePageWasAbsent,
    bool CodePageWasUnsupported,
    string Report);

public static class DxfEncoding
{
    /// <summary>AC1021 = R2007, the first DXF generation whose ASCII text is real UTF-8.</summary>
    public const int Utf8VersionThreshold = 1021;

    public const string DefaultCodePageName = "ANSI_1252";

    /// <summary>Windows-1252 code points for bytes 0x80-0x9F, in order — the ONLY range where it differs
    /// from ISO-8859-1/Latin-1. Built from explicit numeric code points (never literal punctuation in
    /// source) so this file — of all files — never depends on being read back with a particular encoding
    /// itself. The five genuinely unassigned positions (0x81, 0x8D, 0x8F, 0x90, 0x9D) map to their own
    /// C1-control code point (i.e. behave like Latin-1 at exactly those five bytes) — round-trippable,
    /// never throwing — matching how real-world Windows-1252 codecs commonly handle the undefined range.</summary>
    private static readonly char[] Cp1252HighTable = BuildCp1252HighTable();

    private static char[] BuildCp1252HighTable()
    {
        // 0x80..0x9F, left to right. Each entry is the real Unicode code point Windows-1252 maps that
        // byte to; an entry equal to its own 0x80+index position marks one of the five undefined bytes.
        int[] codePoints =
        [
            0x20AC, 0x0081, 0x201A, 0x0192, 0x201E, 0x2026, 0x2020, 0x2021,
            0x02C6, 0x2030, 0x0160, 0x2039, 0x0152, 0x008D, 0x017D, 0x008F,
            0x0090, 0x2018, 0x2019, 0x201C, 0x201D, 0x2022, 0x2013, 0x2014,
            0x02DC, 0x2122, 0x0161, 0x203A, 0x0153, 0x009D, 0x017E, 0x0178,
        ];
        var table = new char[32];
        for (int i = 0; i < table.Length; i++) table[i] = (char)codePoints[i];
        return table;
    }

    /// <summary>The one legacy single-byte encoding this codebase implements from scratch — exact for
    /// Windows-1252, and a safe (ASCII/Latin-1-correct) stand-in for any OTHER named single-byte code
    /// page, since only the 0x80-0x9F range can differ.</summary>
    public static Encoding Windows1252 { get; } = new Windows1252Encoding();

    private static readonly Dictionary<string, string> VersionDisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AC1004"] = "R9", ["AC1006"] = "R10", ["AC1009"] = "R11/R12",
        ["AC1012"] = "R13", ["AC1014"] = "R14", ["AC1015"] = "R2000",
        ["AC1018"] = "R2004", ["AC1021"] = "R2007", ["AC1024"] = "R2010",
        ["AC1027"] = "R2013", ["AC1032"] = "R2018",
    };

    /// <summary>Sniffs `$ACADVER`/`$DWGCODEPAGE` from the HEADER section, then rewinds
    /// <paramref name="stream"/> so the real parse can re-read from the start — a genuine two-pass read,
    /// done because both header variables are always plain ASCII and can therefore be read correctly
    /// via a byte-transparent pass regardless of what the rest of the file's real encoding turns out to
    /// be. Requires a seekable stream (every real call site — a picked file, a test <see
    /// cref="MemoryStream"/> — is seekable); a non-seekable stream defaults to UTF-8, matching this
    /// reader's prior (implicit) behavior, and is reported as such.</summary>
    public static DxfEncodingResolution Resolve(Stream stream)
    {
        string? acadVer = null;
        string? codePage = null;

        if (stream.CanSeek)
        {
            long start = stream.Position;
            using (var probe = new StreamReader(stream, Windows1252, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true))
            {
                var groupReader = new DxfGroupReader(probe);
                bool inHeader = false;
                string? pendingVarName = null;
                int guard = 0;
                while (groupReader.TryReadNext(out var g) && guard++ < 20_000)
                {
                    if (g.Code == 0 && g.Value == "ENDSEC" && inHeader) break;
                    if (g.Code == 2 && g.Value == "HEADER") { inHeader = true; continue; }
                    if (!inHeader) continue;

                    if (g.Code == 9) { pendingVarName = g.Value; continue; }
                    if (pendingVarName == "$ACADVER") acadVer ??= g.Value;
                    else if (pendingVarName == "$DWGCODEPAGE") codePage ??= g.Value;
                    pendingVarName = null;
                }
            }
            stream.Position = start;
            return ResolveFrom(acadVer, codePage);
        }

        return new DxfEncodingResolution(
            Encoding.UTF8, null, "unknown (non-seekable stream)", true, null, false, false,
            "Text encoding: UTF-8 (assumed — the source stream could not be rewound to check $ACADVER).");
    }

    private static DxfEncodingResolution ResolveFrom(string? acadVer, string? codePage)
    {
        int verNum = 0;
        if (acadVer is { Length: > 2 } && acadVer.StartsWith("AC", StringComparison.OrdinalIgnoreCase))
            int.TryParse(acadVer.AsSpan(2), out verNum);
        bool isUtf8 = verNum >= Utf8VersionThreshold;

        string versionName = acadVer is not null && VersionDisplayNames.TryGetValue(acadVer, out var known)
            ? known
            : acadVer is null ? "unknown" : "unrecognized";

        // R-dxf-4: the import report states the RAW $ACADVER value found, not only the friendly name —
        // "$ACADVER=AC1032 (R2018)", or "$ACADVER not found" when the file never declared one at all.
        string versionLabel = acadVer is null ? "$ACADVER not found" : $"$ACADVER={acadVer} ({versionName})";

        if (isUtf8)
        {
            return new DxfEncodingResolution(
                Encoding.UTF8, acadVer, versionName, true, codePage, false, false,
                $"Text encoding: UTF-8 ({versionLabel} is R2007 or later).");
        }

        bool absent = string.IsNullOrEmpty(codePage);
        bool unsupported = !absent && !string.Equals(codePage, DefaultCodePageName, StringComparison.OrdinalIgnoreCase);
        string report = (absent, unsupported) switch
        {
            (true, _) => $"Text encoding: legacy code page ({versionLabel}) — $DWGCODEPAGE is absent; decoded as " +
                          $"{DefaultCodePageName} (this reader's documented default). Report if any text looks wrong.",
            (false, true) => $"Text encoding: legacy code page ({versionLabel}) — $DWGCODEPAGE names \"{codePage}\", " +
                              $"which this reader has no dedicated table for; decoded as {DefaultCodePageName}. " +
                              "Report if any text looks wrong.",
            _ => $"Text encoding: legacy code page ({DefaultCodePageName}, {versionLabel}, from the file's own $DWGCODEPAGE).",
        };

        return new DxfEncodingResolution(Windows1252, acadVer, versionName, false, codePage, absent, unsupported, report);
    }

    /// <summary>Replaces every AutoCAD `\U+XXXX` escape with its real character — the read-side half of
    /// R-dxf-2. Applied uniformly at <c>DxfReader</c>'s single string-value funnel (harmless/no-op for any
    /// value that never contains the literal sequence, which covers every non-text group this reader
    /// also pulls through the same funnel — handles, keywords, section names).</summary>
    public static string Unescape(string raw)
    {
        if (raw.IndexOf("\\U+", StringComparison.OrdinalIgnoreCase) < 0) return raw;

        var sb = new StringBuilder(raw.Length);
        int i = 0;
        while (i < raw.Length)
        {
            if (i + 7 <= raw.Length && raw[i] == '\\' && (raw[i + 1] == 'U' || raw[i + 1] == 'u') && raw[i + 2] == '+'
                && IsHex(raw[i + 3]) && IsHex(raw[i + 4]) && IsHex(raw[i + 5]) && IsHex(raw[i + 6]))
            {
                int code = Convert.ToInt32(raw.Substring(i + 3, 4), 16);
                sb.Append((char)code);
                i += 7;
            }
            else
            {
                sb.Append(raw[i]);
                i++;
            }
        }
        return sb.ToString();
    }

    private static bool IsHex(char c) => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');

    /// <summary>The write-side half of R-dxf-2: R2000 output has no native Unicode text, so any character
    /// outside ASCII is escaped as `\U+XXXX` rather than written as a raw code-page byte that would only
    /// round-trip correctly for a reader sharing the exact same code page.</summary>
    public static string EscapeNonAscii(string value, out bool anyEscaped)
    {
        anyEscaped = false;
        foreach (char c in value)
        {
            if (c > 0x7F) { anyEscaped = true; break; }
        }
        if (!anyEscaped) return value;

        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (c <= 0x7F) sb.Append(c);
            else sb.Append("\\U+").Append(((int)c).ToString("X4"));
        }
        return sb.ToString();
    }

    private sealed class Windows1252Encoding : Encoding
    {
        public override int GetByteCount(char[] chars, int index, int count) => count;

        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
        {
            for (int i = 0; i < charCount; i++)
            {
                char c = chars[charIndex + i];
                int idx = Array.IndexOf(Cp1252HighTable, c);
                bytes[byteIndex + i] = idx >= 0 ? (byte)(0x80 + idx) : c <= 0xFF ? (byte)c : (byte)'?';
            }
            return charCount;
        }

        public override int GetCharCount(byte[] bytes, int index, int count) => count;

        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
        {
            for (int i = 0; i < byteCount; i++)
            {
                byte b = bytes[byteIndex + i];
                chars[charIndex + i] = b is >= 0x80 and <= 0x9F ? Cp1252HighTable[b - 0x80] : (char)b;
            }
            return byteCount;
        }

        public override int GetMaxByteCount(int charCount) => charCount;
        public override int GetMaxCharCount(int byteCount) => byteCount;
    }
}
