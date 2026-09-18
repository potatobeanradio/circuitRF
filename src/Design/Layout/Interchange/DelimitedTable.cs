// The text mechanics every delimited companion table shares — the placement file and the bill of
// materials (docs/sonnet-briefs/brief-railrf-2-companion-readers.md R-rail2-14 item 4).
//
// THE GOVERNING RULE THIS FILE SERVES IS BoardNetlistFile's (R-rail2-1): these tables are EVIDENCE
// ABOUT THE ARTWORK AND NEVER GEOMETRY. Nothing here creates a shape. It turns bytes into rows of
// fields, and every reader above it attaches facts to objects the Gerber, Excellon and board readers
// already built.
//
// WHY IT IS A FILE OF ITS OWN: R-rail2-14 names four things a real export plausibly does that the
// obvious implementation forbids, and three of them are text mechanics rather than schema — the
// encoding, the line endings, a quoted field containing the delimiter, a trailing separator, and a
// leading BYTE ORDER MARK. Each is cheap to allow now and annoying to retrofit, and allowing them
// once is cheaper than allowing them twice.
//
// THE NAMING HAZARD, AND IT IS WRITTEN OUT HERE ONCE FOR THE WHOLE FEATURE (R-rail2-14 item 4): in
// railRF, "BOM" is a BILL OF MATERIALS. In every text reader ever written, "BOM" is a BYTE ORDER
// MARK. The two meet in this exact code path. So the byte order mark is SPELLED OUT IN FULL
// everywhere in this file and in BomFile.cs, every time, and the abbreviation is never used for it.
//
// THE DELIMITER IS INFERRED, AND THE INFERENCE IS REPORTED. An export may be comma, semicolon, tab
// or pipe separated and none of them is more standard than the others. The inference is a
// CONSISTENCY test — the candidate that yields the same field count on the most lines wins — not a
// frequency count, because a free-text description column full of commas beats a tab on frequency
// and loses on consistency. A caller that knows better states it.

using System.Text;

namespace CircuitRF.Design.Layout.Interchange;

/// <summary>One physical row of a delimited table: its fields, and the 1-based line the row STARTED
/// on. The start line rather than the end line, because a quoted field may span line endings and the
/// line a reader reports has to be the one a user can find.</summary>
public sealed record DelimitedRow(int Line, IReadOnlyList<string> Fields)
{
    /// <summary>Field <paramref name="index"/>, trimmed, or null where the row is shorter than that
    /// or the cell is blank. A short row is ordinary — a trailing separator and a ragged export both
    /// produce one — and it is never an exception.</summary>
    public string? Field(int index) =>
        index >= 0 && index < Fields.Count && Fields[index].Trim().Length > 0
            ? Fields[index].Trim()
            : null;

    /// <summary>How many fields carry anything at all. Used to tell a data row from a separator or a
    /// decorative rule line.</summary>
    public int Populated => Fields.Count(f => f.Trim().Length > 0);
}

/// <summary>
/// A delimited table, parsed but not yet understood: the delimiter that was used, the lines that came
/// before the table proper, and the rows.
///
/// <para><see cref="Preamble"/> is where a placement file's units and origin records live — the small
/// header §2.2 describes — so it is carried rather than discarded.</para>
/// </summary>
/// <param name="Delimiter">The character that separated the fields.</param>
/// <param name="DelimiterDeclared">True when the caller stated it, false when it was inferred.</param>
/// <param name="Preamble">The comment/header lines above the table, in order, with their comment
/// markers stripped. A file with no such lines has an empty one.</param>
/// <param name="Rows">Every row that held at least one populated field, in file order.</param>
/// <param name="HadByteOrderMark">Whether the text began with a UNICODE BYTE ORDER MARK (U+FEFF),
/// which was removed. Not a bill of materials — see this file's header.</param>
public sealed record DelimitedTable(
    char Delimiter,
    bool DelimiterDeclared,
    IReadOnlyList<string> Preamble,
    IReadOnlyList<DelimitedRow> Rows,
    bool HadByteOrderMark)
{
    public static readonly DelimitedTable Empty = new(',', false, [], [], false);
}

public static class DelimitedTables
{
    /// <summary>The separators in circulation, in the order the inference tries them. Comma first
    /// because it is the commonest, but the order only settles a TIE — the test itself is
    /// consistency, not position.</summary>
    public static readonly char[] Candidates = [',', ';', '\t', '|'];

    /// <summary>How many rows the delimiter inference looks at. Enough to be sure, few enough that a
    /// 50,000-row bill of materials is not parsed four times in full.</summary>
    private const int InferenceRows = 40;

    /// <summary>The characters that begin a comment/preamble line. A <c>#</c> is the commonest, a
    /// <c>;</c> appears in tool-written headers, and <c>//</c> in a few. A <c>;</c> is ALSO a
    /// candidate delimiter, which is why a preamble line is only recognised BEFORE the first data
    /// row — a semicolon-delimited table whose first cell is empty must not be read as a comment.
    /// </summary>
    private static readonly string[] CommentMarkers = ["#", "//", ";", "!"];

    /// <summary>
    /// Parses <paramref name="text"/> into rows.
    ///
    /// <para>Handles, deliberately and up front (R-rail2-14 item 4): a leading UNICODE BYTE ORDER
    /// MARK (U+FEFF — not a bill of materials), all three line endings, a quoted field containing the
    /// delimiter or a line ending, a doubled quote inside a quoted field, and a trailing separator.
    /// </para>
    /// </summary>
    /// <param name="delimiterOverride">The delimiter, where the caller knows it. Null infers it.</param>
    public static DelimitedTable Parse(string text, char? delimiterOverride = null)
    {
        if (text.Length == 0) return DelimitedTable.Empty;

        // The UNICODE BYTE ORDER MARK, spelled out: U+FEFF at the head of a UTF-8 file. Left in
        // place it becomes part of the first header cell's name, so "Ref" stops matching "ref" and
        // the whole header resolution fails on a file that is otherwise perfectly ordinary.
        bool hadByteOrderMark = text[0] == '﻿';
        if (hadByteOrderMark) text = text[1..];

        var (preamble, body) = SplitPreamble(text);

        char delimiter = delimiterOverride ?? Infer(body);
        var rows = ParseRows(body, delimiter);

        return new DelimitedTable(delimiter, delimiterOverride is not null, preamble, rows, hadByteOrderMark);
    }

    /// <summary>Parses a file. A companion table that cannot be read is not an exception out of the
    /// middle of an import — the same contract <see cref="BoardNetlistFile.ReadFile"/> keeps.</summary>
    public static DelimitedTable? ParseFile(string path, char? delimiterOverride = null)
    {
        try
        {
            // Encoding: UTF-8 with detection, which is what every companion export in circulation is
            // or is a subset of. A file that is genuinely another code page decodes to replacement
            // characters in its DESCRIPTION column and to intact ASCII everywhere a reader branches,
            // which is the failure worth having.
            return Parse(File.ReadAllText(path, Encoding.UTF8), delimiterOverride);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    // ── the preamble ──────────────────────────────────────────────────────────

    /// <summary>Peels the leading comment lines off. Stops at the first line that is not one, so a
    /// comment BELOW the header is an ordinary row and is reported as unreadable by the reader above
    /// rather than silently swallowing the rows after it.</summary>
    private static (IReadOnlyList<string> Preamble, string Body) SplitPreamble(string text)
    {
        var preamble = new List<string>();
        int at = 0;

        while (at < text.Length)
        {
            int end = text.IndexOf('\n', at);
            string line = (end < 0 ? text[at..] : text[at..end]).TrimEnd('\r');
            string trimmed = line.TrimStart();

            if (trimmed.Length == 0)
            {
                // A blank line above the table is neither a comment nor a row. Consumed, not kept.
                at = end < 0 ? text.Length : end + 1;
                continue;
            }

            string? marker = CommentMarkers.FirstOrDefault(m => trimmed.StartsWith(m, StringComparison.Ordinal));
            if (marker is null) break;

            preamble.Add(trimmed[marker.Length..].Trim());
            at = end < 0 ? text.Length : end + 1;
        }

        return (preamble, text[at..]);
    }

    // ── the delimiter ─────────────────────────────────────────────────────────

    /// <summary>
    /// Which separator this text uses. The test is CONSISTENCY: parse the head with each candidate
    /// and take the one whose rows agree on a field count, with more fields breaking a tie.
    ///
    /// <para>Not a frequency count, and the difference matters: a bill of materials whose description
    /// column reads <c>MLCC, 10n0, 50V</c> carries more commas than tabs on a tab-separated file, and
    /// a frequency count would choose the comma and shred every row. Consistency chooses the tab,
    /// because the comma count varies per row and the tab count does not.</para>
    /// </summary>
    private static char Infer(string body)
    {
        char best = Candidates[0];
        double bestScore = -1;

        foreach (char candidate in Candidates)
        {
            var rows = ParseRows(body, candidate, InferenceRows);
            if (rows.Count == 0) continue;

            // The modal field count, and how many rows agree with it. A single-column reading —
            // every row one field — is what a WRONG delimiter looks like, and it is perfectly
            // consistent, so it scores zero rather than winning by agreeing with itself.
            var counts = rows.GroupBy(r => r.Fields.Count).OrderByDescending(g => g.Count()).First();
            if (counts.Key < 2) continue;

            double agreement = (double)counts.Count() / rows.Count;
            double score = agreement * 1000 + counts.Key;
            if (score > bestScore) { bestScore = score; best = candidate; }
        }

        return best;
    }

    // ── the rows ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The field parser: one pass over the text, quote-aware.
    ///
    /// <para>A field is quoted where its FIRST non-space character is a double quote. Inside quotes
    /// the delimiter, a carriage return and a line feed are ordinary characters and a doubled quote
    /// is one quote — which is the whole reason this is a state machine over the text rather than a
    /// split per line. A quote that opens and never closes takes the rest of the file, so the text is
    /// re-read unquoted in that case; an export with one stray quote in a description would otherwise
    /// lose every row below it, silently.</para>
    /// </summary>
    private static IReadOnlyList<DelimitedRow> ParseRows(string text, char delimiter, int limit = int.MaxValue)
    {
        var rows = Scan(text, delimiter, quoteAware: true, limit, out bool unterminated);
        return unterminated ? Scan(text, delimiter, quoteAware: false, limit, out _) : rows;
    }

    private static List<DelimitedRow> Scan(
        string text, char delimiter, bool quoteAware, int limit, out bool unterminated)
    {
        var rows = new List<DelimitedRow>();
        var fields = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false, fieldStarted = false, quoted = false;
        int line = 1, rowLine = 1;
        unterminated = false;

        void EndField()
        {
            fields.Add(quoted ? field.ToString() : field.ToString().Trim());
            field.Clear();
            fieldStarted = false;
            quoted = false;
        }

        void EndRow()
        {
            EndField();

            // A trailing separator leaves one empty field on the end. Dropped — a row written
            // "C1,100n,0402," has four columns and a writer's habit, not five columns one of which
            // is always blank.
            if (fields.Count > 1 && fields[^1].Length == 0) fields.RemoveAt(fields.Count - 1);

            if (fields.Any(f => f.Length > 0)) rows.Add(new DelimitedRow(rowLine, [.. fields]));
            fields.Clear();
        }

        for (int i = 0; i < text.Length && rows.Count < limit; i++)
        {
            char c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                    continue;
                }
                if (c == '\n') line++;
                field.Append(c);
                continue;
            }

            if (c == delimiter) { EndField(); continue; }

            if (c == '\r')
            {
                // A lone carriage return is a line ending too. A CR LF pair is one ending, not two.
                if (i + 1 < text.Length && text[i + 1] == '\n') continue;
                EndRow();
                line++;
                rowLine = line;
                continue;
            }

            if (c == '\n')
            {
                EndRow();
                line++;
                rowLine = line;
                continue;
            }

            if (quoteAware && c == '"' && !fieldStarted)
            {
                inQuotes = true;
                quoted = true;
                fieldStarted = true;
                continue;
            }

            if (!fieldStarted && char.IsWhiteSpace(c)) continue;   // leading space before a field
            fieldStarted = true;
            field.Append(c);
        }

        unterminated = inQuotes;
        if (field.Length > 0 || fields.Count > 0) EndRow();
        return rows;
    }

    // ── header names ──────────────────────────────────────────────────────────

    /// <summary>
    /// A header cell reduced to the form aliases are compared in: lower case, no punctuation, single
    /// spaces, and with a trailing parenthesised unit removed — <c>"Pos X (mm)"</c> and
    /// <c>"pos_x"</c> are the same column, and the <c>(mm)</c> is read separately by
    /// <see cref="UnitInHeader"/> because it is evidence about the units and must not be thrown away
    /// to make a name match.
    /// </summary>
    public static string NormalizeHeader(string cell)
    {
        string s = cell.Trim().Trim('"').ToLowerInvariant();

        int open = s.LastIndexOf('(');
        if (open > 0 && s.EndsWith(')')) s = s[..open];

        var sb = new StringBuilder(s.Length);
        bool space = false;
        foreach (char c in s)
        {
            if (char.IsLetterOrDigit(c)) { if (space && sb.Length > 0) sb.Append(' '); space = false; sb.Append(c); }
            else space = true;
        }
        return sb.ToString();
    }

    /// <summary>The index of the first column whose name is one of <paramref name="aliases"/>, or -1.
    /// Exact equality over <see cref="NormalizeHeader"/>, never a substring test: a
    /// <c>contains "x"</c> rule matches <c>footprint</c>, and the column it puts the coordinate in is
    /// the one nothing downstream questions.</summary>
    public static int IndexOf(IReadOnlyList<string> header, params string[] aliases)
    {
        for (int i = 0; i < header.Count; i++)
        {
            string name = NormalizeHeader(header[i]);
            if (name.Length > 0 && aliases.Contains(name, StringComparer.Ordinal)) return i;
        }
        return -1;
    }

    /// <summary>The unit a header cell states in parentheses — <c>"Pos X (mm)"</c>, <c>"X (mil)"</c>
    /// — or null. Read from the COLUMN because a placement export that states its units nowhere else
    /// very often states them here, and a unit read from the file is <c>Declared</c> evidence where
    /// an assumed one is not.</summary>
    public static LayoutUnit? UnitInHeader(string cell)
    {
        string s = cell.Trim().Trim('"').ToLowerInvariant();
        int open = s.LastIndexOf('(');
        if (open < 0 || !s.EndsWith(')')) return null;
        return ParseUnit(s[(open + 1)..^1]);
    }

    /// <summary>A unit word, as a units record or a column heading spells it. Null where the word is
    /// not one — never a default, because the caller's own evidence rule depends on telling "the file
    /// said" from "nothing said".</summary>
    public static LayoutUnit? ParseUnit(string word) => word.Trim().ToLowerInvariant() switch
    {
        "mm" or "millimetre" or "millimeter" or "millimetres" or "millimeters" => LayoutUnit.Mm,
        "um" or "µm" or "μm" or "micron" or "microns" or "micrometre" or "micrometer" => LayoutUnit.Um,
        "nm" or "nanometre" or "nanometer" => LayoutUnit.Nm,
        "mil" or "mils" or "thou" => LayoutUnit.Mil,
        "in" or "inch" or "inches" or "\"" => LayoutUnit.Inch,
        _ => null,
    };
}
