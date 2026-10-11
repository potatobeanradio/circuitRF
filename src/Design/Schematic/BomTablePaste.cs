// A parts table pasted (or a .csv dropped) onto a schematic, read into the R/L/C parts it lists.
//
// A BEST TRY, AND SAYS SO. A table reaches a schematic from three places — a spreadsheet (tab
// separated), a .csv file, and text copied out of a PDF datasheet or application note — and only the
// first two have columns. PDF text arrives as whitespace-separated words, with a cell that wrapped in
// the PDF broken across a space ("Capacito r") or across a line ("M11,M12,M1" then "6"). So this
// reader does not trust column POSITION where it cannot: each field is recognised by what it LOOKS
// like (a reference designator, a type word, a number with a unit, a case code) and a header, where
// there is one, only says which cell to try first. Every row it could not read is REPORTED with the
// reason, never dropped in silence, and every reading a person might disagree with (a bare "0402",
// which is a real case in both the imperial and the metric scheme) is stated.
//
// WHAT IT PRODUCES IS DATA. Nothing here creates an instance: the schematic places each part through
// its own placement path, so a pasted capacitor is the same object a palette-dropped one is.
//
// WHY NOT BomFile. BomFile is railRF's companion reader, and its rule is the opposite one — a bill of
// materials there is EVIDENCE about artwork that already exists and never creates anything. It also
// refuses a header it cannot place and requires a part-number/quantity/description column, which a
// table typed for a schematic rarely has. The pieces that are the same are shared: the delimiter
// inference (DelimitedTables) and the grouped reference cell (RefdesCell).

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Design.Layout.Interchange;

namespace CircuitRF.Design.Schematic;

/// <summary>One part a pasted table asked for.</summary>
/// <param name="Refdes">The reference designator as the table wrote it — the instance name it asks
/// for (a collision with an existing name is resolved by the paste, as any paste resolves one).</param>
/// <param name="Kind">Resistor, capacitor or inductor.</param>
/// <param name="Value">The number, as written (<c>100</c>, <c>0.1</c>), or null where the row gave
/// none the reader could use — the part is then placed with its default value.</param>
/// <param name="Unit">The unit, spelled as the parameter editor's unit list spells it
/// (<c>pF</c>, <c>µF</c>, <c>kΩ</c>), or null with <paramref name="Value"/>.</param>
/// <param name="Case">The case size the row named, or null where it named none this reader
/// recognised — the part then keeps the footprint placement gives it.</param>
/// <param name="Line">The 1-based line of the pasted text the row started on.</param>
public sealed record BomPastePart(
    string Refdes, SymbolKind Kind, string? Value, string? Unit, SmtCase? Case, int Line);

/// <summary>A row, or one reference of a row, that produced no part — and why.</summary>
public sealed record BomPasteSkip(string What, string Reason, int Line);

/// <summary>Everything a pasted table turned out to hold.</summary>
/// <param name="Notes">Readings a person might disagree with, stated once each.</param>
public sealed record BomPasteResult(
    IReadOnlyList<BomPastePart> Parts,
    IReadOnlyList<BomPasteSkip> Skipped,
    IReadOnlyList<string> Notes,
    bool HeaderFound);

/// <summary>Reads a pasted parts table. See the file header for what it will and will not do.</summary>
public static class BomTablePaste
{
    private enum Role { Refs, Type, Value, Unit, Case, Known }

    // Header words, compared with every non-letter removed — so "Valu e", "Packag e" and
    // "Case size" (a wrapped PDF cell, a spreadsheet's own spacing) read as the words they are.
    private static readonly (string Word, Role Role)[] HeaderWords =
    [
        ("refdes", Role.Refs), ("ref", Role.Refs), ("reference", Role.Refs), ("references", Role.Refs),
        ("designator", Role.Refs), ("designators", Role.Refs), ("component", Role.Refs),
        ("components", Role.Refs), ("componentid", Role.Refs), ("comp", Role.Refs),
        ("partreference", Role.Refs), ("referencedesignator", Role.Refs), ("refdesignator", Role.Refs),
        ("item", Role.Refs), ("part", Role.Refs), ("parts", Role.Refs), ("id", Role.Refs),

        ("type", Role.Type), ("description", Role.Type), ("desc", Role.Type), ("kind", Role.Type),
        ("parttype", Role.Type), ("componenttype", Role.Type), ("category", Role.Type),

        ("value", Role.Value), ("val", Role.Value), ("componentvalue", Role.Value),
        ("nominalvalue", Role.Value),

        ("unit", Role.Unit), ("units", Role.Unit),

        ("size", Role.Case), ("casesize", Role.Case), ("case", Role.Case), ("package", Role.Case),
        ("footprint", Role.Case), ("pkg", Role.Case), ("landpattern", Role.Case),
        ("packagesize", Role.Case), ("casecode", Role.Case),

        // Columns a parts table routinely carries and this reader has no use for. They still count
        // towards recognising the row as a header — "Manufacturer" is as sure a header word as
        // "Value" — and they are otherwise ignored.
        ("manufacturer", Role.Known), ("mfr", Role.Known), ("mfg", Role.Known), ("vendor", Role.Known),
        ("family", Role.Known), ("series", Role.Known), ("partcode", Role.Known),
        ("partnumber", Role.Known), ("mpn", Role.Known), ("substitution", Role.Known),
        ("qty", Role.Known), ("quantity", Role.Known), ("notes", Role.Known), ("comment", Role.Known),
        ("tolerance", Role.Known), ("voltage", Role.Known), ("dielectric", Role.Known),
    ];

    private static readonly Regex OneRef = new(@"^[A-Za-z]{1,4}\d{1,5}$", RegexOptions.CultureInvariant);
    private static readonly Regex RangeRef =
        new(@"^[A-Za-z]{1,4}\d{1,5}[-–]([A-Za-z]{1,4})?\d{1,5}$", RegexOptions.CultureInvariant);
    private static readonly Regex Number = new(@"^[+]?(\d+(\.\d*)?|\.\d+)$", RegexOptions.CultureInvariant);
    private static readonly Regex NumberWithTail =
        new(@"^([+]?(?:\d+(?:\.\d*)?|\.\d+))(\S+)$", RegexOptions.CultureInvariant);
    // "4k7", "2R2", "4n7": the letter IS the decimal point and the multiplier. A spreadsheet BOM
    // writes the capacitor and inductor ones upper-case as often as not — "2P3", "4U7", "2N5".
    private static readonly Regex LetterAsPoint =
        new(@"^(\d+)([pnuµμmkKMGRPNU])(\d+)$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads <paramref name="text"/> as a parts table, or returns null where it is not one — so a
    /// caller can hand any other text to whatever it would otherwise have done with it.
    /// </summary>
    /// <remarks>It is a table when it yields at least one part AND either a header row was
    /// recognised or at least two parts were read. One stray line such as "C1 10 nF" is not a
    /// table a paste should act on.</remarks>
    public static BomPasteResult? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Replace("﻿", "").Replace("\r\n", "\n").Replace('\r', '\n');

        // Anything that opens like JSON or markup is some other paste's content, never a table.
        string head = text.TrimStart();
        if (head.StartsWith('{') || head.StartsWith('[') || head.StartsWith('<')) return null;

        var records = ReadRecords(text, out var columns, out bool headerFound);
        if (records.Count == 0) return null;

        var parts = new List<BomPastePart>();
        var skipped = new List<BomPasteSkip>();
        var notes = new List<string>();
        var imperialReadings = new SortedSet<string>(StringComparer.Ordinal);
        var unknownCases = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var noValue = new List<string>();
        var bareOhms = new List<string>();

        foreach (var rec in records)
            ReadRecord(rec, columns, parts, skipped, imperialReadings, unknownCases, noValue, bareOhms);

        if (parts.Count == 0 || (!headerFound && parts.Count < 2)) return null;

        foreach (string code in imperialReadings)
            if (SmtCaseTable.Find(code) is { } c)
                notes.Add($"Case '{code}' was read as the imperial chip code ({c.Display.Trim()}); " +
                          "the same digits are also a metric code for a smaller part.");
        if (unknownCases.Count > 0)
            notes.Add($"Case size(s) {string.Join(", ", unknownCases.Select(u => $"'{u}'"))} are not " +
                      "ones circuitRF generates a land pattern for, so those parts keep the default " +
                      "footprint.");
        if (noValue.Count > 0)
            notes.Add($"No value with a unit was found for {string.Join(", ", noValue)}; placed with " +
                      "the default value.");
        if (bareOhms.Count > 0)
            notes.Add($"{string.Join(", ", bareOhms)}: a number with no unit, straight after the " +
                      "part's type where a value sits, was read as ohms.");

        return new BomPasteResult(parts, skipped, notes, headerFound);
    }

    // ── records ───────────────────────────────────────────────────────────────

    /// <summary>One source row: its cells (delimited) or its words (PDF text), with the line it
    /// started on. <see cref="Delimited"/> says which.</summary>
    private sealed record Record(int Line, List<string> Cells, bool Delimited)
    {
        /// <summary>Words from lines that continued this row — a wrapped PDF cell.</summary>
        public List<string> Continuation { get; } = [];

        /// <summary>One of a run of reference-only lines: the PDF copy delivered this block of rows
        /// column by column (every reference, then every type, then every value), and a cell that
        /// was empty in the table left no line at all — so nothing below can be matched back to this
        /// row. See <see cref="MarkColumnBlocks"/>.</summary>
        public bool ColumnBlock { get; set; }
    }

    private static List<Record> ReadRecords(string text, out Dictionary<Role, List<int>> columns,
                                            out bool headerFound)
    {
        columns = [];
        headerFound = false;

        var table = DelimitedTables.Parse(text);
        if (IsDelimited(table))
        {
            var rows = table.Rows;
            int start = 0;
            for (int i = 0; i < Math.Min(rows.Count, 10); i++)
            {
                if (HeaderRoles(rows[i].Fields) is { } roles)
                {
                    columns = roles;
                    headerFound = true;
                    start = i + 1;
                    break;
                }
            }
            return [.. rows.Skip(start).Select(r => new Record(r.Line, [.. r.Fields.Select(f => f.Trim())], true))];
        }

        // Whitespace-separated words: text copied out of a PDF. A row starts on a line whose first
        // word is a reference designator; any other line after it is a continuation of that row.
        var records = new List<Record>();
        var lines = text.Split('\n');
        bool pastHeader = false;
        for (int i = 0; i < lines.Length; i++)
        {
            var words = lines[i].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (words.Count == 0) continue;

            if (!pastHeader && !IsRefsWord(words[0]) && HeaderRoles(JoinBrokenHeaderWords(words)) is not null)
            {
                pastHeader = true;
                headerFound = true;
                records.Clear();         // anything above the header was a title, not a row
                continue;
            }

            if (IsRefsWord(words[0]))
                records.Add(new Record(i + 1, words, false));
            else if (records.Count > 0)
                records[^1].Continuation.AddRange(words);
        }
        MarkColumnBlocks(records);
        return records;
    }

    /// <summary>
    /// Marks each run of three or more rows that arrived as a bare reference on its own line, back to
    /// back. A PDF copy that reads a table one cell per line usually goes row by row, but a block the
    /// PDF laid out as columns comes back column by column: "R2", "R3", "R1", then "Potentiometer",
    /// "Resistor", "Resistor", then "10 K", "1K", "15" — with an EMPTY cell leaving no line, so the
    /// columns do not even line up. Read row-wise, every one of those rows had no value and was
    /// placed at the default, which is a wrong part rather than a missing one. The last row of the
    /// run carries the transposed columns as its continuation and is marked too.
    /// </summary>
    private static void MarkColumnBlocks(List<Record> records)
    {
        int i = 0;
        while (i < records.Count)
        {
            int j = i;
            while (j + 1 < records.Count && records[j].Cells.Count == 1 && records[j].Continuation.Count == 0 &&
                   records[j + 1].Cells.Count == 1)
                j++;
            // Three or more: in a one-cell-per-line copy every row's own line holds one word, and a
            // case code shaped like a reference ("SOD110") ahead of a real row makes a false pair.
            if (j - i >= 2)
                for (int k = i; k <= j; k++) records[k].ColumnBlock = true;
            i = j + 1;
        }
    }

    /// <summary>A delimited table is one whose rows mostly agree on three or more cells. Text
    /// copied from a PDF has commas inside reference cells and nowhere else, so its comma
    /// "columns" disagree row to row and it fails this — which is the point. A TAB is never inside
    /// a PDF's words, and a spreadsheet exported with its trailing empty cells trimmed gives rows of
    /// every length, so a tab table needs only most rows to have three cells.</summary>
    private static bool IsDelimited(DelimitedTable table)
    {
        if (table.Rows.Count == 0) return false;
        if (table.Delimiter == '\t')
            return table.Rows.Count(r => r.Fields.Count >= 3) >= Math.Max(1, (int)Math.Ceiling(table.Rows.Count * 0.6));
        var modal = table.Rows.GroupBy(r => r.Fields.Count).OrderByDescending(g => g.Count()).First();
        return modal.Key >= 3 && modal.Count() >= Math.Max(1, (int)Math.Ceiling(table.Rows.Count * 0.6));
    }

    /// <summary>The role of each header cell, or null when fewer than two cells are header
    /// words.</summary>
    private static Dictionary<Role, List<int>>? HeaderRoles(IReadOnlyList<string> cells)
    {
        var roles = new Dictionary<Role, List<int>>();
        int known = 0;
        for (int i = 0; i < cells.Count; i++)
        {
            if (HeaderRole(cells[i]) is not { } role) continue;
            known++;
            if (!roles.TryGetValue(role, out var list)) roles[role] = list = [];
            list.Add(i);
        }
        return known >= 2 ? roles : null;
    }

    private static Role? HeaderRole(string cell)
    {
        string n = LettersOnly(cell);
        if (n.Length == 0) return null;
        foreach (var (word, role) in HeaderWords)
            if (n == word) return role;
        return null;
    }

    /// <summary>"Valu e" → "Value": a header word a PDF wrapped arrives as two words. Two adjacent
    /// words are joined when neither is a header word alone and together they are.</summary>
    private static List<string> JoinBrokenHeaderWords(List<string> words)
    {
        var joined = new List<string>();
        for (int i = 0; i < words.Count; i++)
        {
            if (i + 1 < words.Count && HeaderRole(words[i]) is null && HeaderRole(words[i + 1]) is null &&
                HeaderRole(words[i] + words[i + 1]) is not null)
            {
                joined.Add(words[i] + words[i + 1]);
                i++;
            }
            else joined.Add(words[i]);
        }
        return joined;
    }

    // ── one row ───────────────────────────────────────────────────────────────

    private static void ReadRecord(
        Record rec, Dictionary<Role, List<int>> columns,
        List<BomPastePart> parts, List<BomPasteSkip> skipped,
        SortedSet<string> imperialReadings, SortedSet<string> unknownCases, List<string> noValue,
        List<string> bareOhms)
    {
        var cells = rec.Cells;
        var used = new HashSet<int>();

        // The reference cell.
        string? refsText = null;
        if (rec.Delimited)
        {
            int at = FirstColumn(columns, Role.Refs, cells, c => IsRefsCell(c));
            if (at < 0) at = cells.FindIndex(IsRefsCell);
            if (at < 0) return;                       // a title, a total, a rule line: not a row
            refsText = cells[at];
            used.Add(at);
        }
        else
        {
            // Leading words that continue a reference list: "M1," "M23", or "M1" "," "M23".
            var sb = new StringBuilder(cells[0]);
            used.Add(0);
            int k = 1;
            while (k < cells.Count &&
                   (sb[^1] == ',' || cells[k].StartsWith(',') ||
                    (cells[k] == ",")))
            {
                sb.Append(cells[k]);
                used.Add(k);
                k++;
            }
            // "M11,M12,M1 6": the tail of a wrapped reference list, separated by a space. Only for
            // a LIST (one reference is never wrapped), and never when the digits are a value.
            if (sb.ToString().Contains(',') && char.IsDigit(sb[^1]) && k < cells.Count &&
                Regex.IsMatch(cells[k], @"^\d{1,2}$") &&
                !(k + 1 < cells.Count && ParseUnit(cells[k + 1]) is not null))
            {
                sb.Append(cells[k]);
                used.Add(k);
            }
            // …or on the next line, when the PDF put the wrapped tail below.
            else if (sb.ToString().Contains(',') && char.IsDigit(sb[^1]) && rec.Continuation.Count > 0 &&
                     Regex.IsMatch(rec.Continuation[0], @"^\d{1,2}$"))
            {
                sb.Append(rec.Continuation[0]);
                rec.Continuation.RemoveAt(0);
            }
            refsText = sb.ToString();
            cells = [.. cells, .. rec.Continuation];
        }

        var refs = RefdesCell.Parse(refsText).Refdes;
        if (refs.Count == 0) return;
        string refsLabel = string.Join(", ", refs);

        if (rec.ColumnBlock)
        {
            skipped.Add(new BomPasteSkip(refsLabel,
                "only its reference arrived on its line — the copy delivered this block of rows column " +
                "by column, so its type and value cannot be matched back to it; paste these rows from a " +
                "spreadsheet, or copy them one row at a time", rec.Line));
            return;
        }

        // The type.
        SymbolKind? kind = null;
        bool dnp = false;
        string? unknownType = null;
        int typeAt = rec.Delimited ? FirstColumn(columns, Role.Type, cells, _ => true) : -1;
        int typeWordAt = -1;
        if (typeAt >= 0 && !string.IsNullOrWhiteSpace(cells[typeAt]))
        {
            used.Add(typeAt);
            (kind, dnp) = ClassifyType(cells[typeAt], allowSingleLetter: true, typeColumn: true);
            if (kind is null && !dnp) unknownType = cells[typeAt].Trim();
        }
        else
        {
            for (int i = 0; i < cells.Count; i++)
            {
                if (used.Contains(i)) continue;
                var (k2, d2) = ClassifyType(cells[i], allowSingleLetter: rec.Delimited);
                if (k2 is null && !d2) continue;
                (kind, dnp) = (k2, d2);
                used.Add(i);
                typeWordAt = i;
                break;
            }
        }

        // Do-not-populate: stated as a type, or as a value of "--"/"DNP"/"Not assembled" — in the
        // Value column or in any type/description column ("Not assembled" is often written there).
        if (!dnp && rec.Delimited)
            dnp = new[] { Role.Value, Role.Type }.Any(role =>
                columns.TryGetValue(role, out var cols) &&
                cols.Any(v => v < cells.Count && IsDnpWord(cells[v])));
        if (dnp)
        {
            skipped.Add(new BomPasteSkip(refsLabel, "marked do-not-populate", rec.Line));
            return;
        }

        int valueAt = rec.Delimited ? FirstColumn(columns, Role.Value, cells, _ => true) : -1;

        // A type word IN THE VALUE CELL names the part actually fitted, and wins over both the
        // type column and the reference prefix: "0R0 (resistor)" on L1 is a zero-ohm link placed
        // where the layout allowed an inductor (round-9 field report). The parenthetical is then
        // dropped so the value reads on its own.
        string? valueCell = valueAt >= 0 ? cells[valueAt] : null;
        if (valueCell is not null)
        {
            foreach (string w in Regex.Split(valueCell, @"[^\p{L}]+"))
            {
                if (w.Length < 3) continue;
                if (ClassifyType(w, allowSingleLetter: false) is { Kind: { } vk })
                {
                    kind = vk;
                    unknownType = null;
                    break;
                }
            }
            valueCell = Regex.Replace(valueCell, @"\([^)]*\)", " ").Trim();
        }

        // The kind a value is read against: the type word, else the reference prefix. Known BEFORE
        // the value is parsed, so "100k" on R1, "100R", "10U" and "100N" on a C read with the
        // multiplier the prefix implies (round-9 field report: every one of them fell to default).
        SymbolKind? parseKind = kind ?? KindFromRefdes(refs[0]);

        // The value.
        string? value = null, unit = null;
        UnitDimension valueDim = UnitDimension.None;
        int valueEnd = -1;
        if (valueAt >= 0)
        {
            used.Add(valueAt);
            if (TryParseValue(valueCell!, parseKind, out value, out unit, out valueDim))
                valueEnd = valueAt;
            else if (Number.IsMatch(valueCell!))
            {
                // The unit in a cell of its own: a stated Unit column, else the cell to the right —
                // a spreadsheet that split "100 pF" into two cells under one header.
                string number = valueCell!;
                int unitAt = FirstColumn(columns, Role.Unit, cells, c => ParseUnit(c) is not null);
                if (unitAt < 0 && valueAt + 1 < cells.Count && ParseUnit(cells[valueAt + 1]) is not null)
                    unitAt = valueAt + 1;
                if (unitAt >= 0 && ParseUnit(cells[unitAt]) is { } u)
                {
                    (value, unit, valueDim) = (number, u.Unit, u.Dim);
                    used.Add(unitAt);
                    valueEnd = unitAt;
                }
                else if (parseKind == SymbolKind.Resistor)
                {
                    (value, unit, valueDim) = (number, "Ω", UnitDimension.Resistance);
                    valueEnd = valueAt;
                }
            }
        }
        else
        {
            for (int i = 0; i < cells.Count && value is null; i++)
            {
                if (used.Contains(i)) continue;
                if (TryParseValue(cells[i], parseKind, out value, out unit, out valueDim))
                {
                    used.Add(i);
                    valueEnd = i;
                }
                else if (Number.IsMatch(cells[i]) && i + 1 < cells.Count &&
                         (ParseUnit(cells[i + 1]) ??
                          (parseKind == SymbolKind.Resistor ? ResistorMultiplier(cells[i + 1]) : null)) is { } u)
                {
                    // "100 pF", and on a resistor "10 K" — the multiplier a word of its own.
                    (value, unit, valueDim) = (cells[i], u.Unit, u.Dim);
                    used.Add(i);
                    used.Add(i + 1);
                    valueEnd = i + 1;
                }
            }

            // "R1 Resistor 15 1206": a bare number straight after the type word is the Value
            // column's cell with its ohm left off — what the delimited path already reads from a
            // Value column. Only there (a quantity or a part code elsewhere in the row is also a bare
            // number), only on a resistor (a capacitor has no unit to assume), never a case code.
            int at = typeWordAt + 1;
            if (value is null && parseKind == SymbolKind.Resistor && typeWordAt >= 0 && at < cells.Count &&
                !used.Contains(at) && Number.IsMatch(cells[at]) && !TryParseCase(cells[at], out _, out _))
            {
                (value, unit, valueDim) = (cells[at], "Ω", UnitDimension.Resistance);
                used.Add(at);
                valueEnd = at;
                bareOhms.Add(refsLabel);
            }

            // No Value column and no value cell: a description often carries it — "Resistor,
            // 100 ohms, 0402, ±1%", "Inductor, 27n, 0402". Its comma-separated pieces are tried,
            // then their words; only against a known kind, so a bare "27n" has a unit to take.
            if (value is null && typeAt >= 0 && parseKind is not null &&
                DescriptionValue(cells[typeAt], parseKind) is { } dv)
            {
                (value, unit, valueDim) = dv;
                valueEnd = typeAt;
            }
        }

        // The kind, when no type word said it: the reference prefix, then the value's unit.
        kind ??= KindFromRefdes(refs[0]) ?? KindFromDimension(valueDim);
        if (kind is null)
        {
            skipped.Add(new BomPasteSkip(refsLabel,
                unknownType is not null
                    ? $"type '{unknownType}' is not a resistor, capacitor or inductor"
                    : "no type could be read (not a resistor, capacitor or inductor)", rec.Line));
            return;
        }
        if (unknownType is not null && KindFromRefdes(refs[0]) is null)
        {
            skipped.Add(new BomPasteSkip(refsLabel,
                $"type '{unknownType}' is not a resistor, capacitor or inductor", rec.Line));
            return;
        }
        if (value is not null && valueDim != DimensionOf(kind.Value))
        {
            skipped.Add(new BomPasteSkip(refsLabel,
                $"value '{value} {unit}' is not {NounOf(kind.Value)}", rec.Line));
            return;
        }

        // The case size: the case columns first, then any cell not already read — after the value
        // first, since a table puts the size after the value far more often than before it.
        SmtCase? smtCase = null;
        string? caseToken = null;
        if (rec.Delimited && columns.TryGetValue(Role.Case, out var ccols))
        {
            foreach (int c in ccols)
            {
                if (c >= cells.Count || used.Contains(c) || string.IsNullOrWhiteSpace(cells[c])) continue;
                if (TryParseCase(cells[c], out smtCase, out bool imperial))
                {
                    used.Add(c);
                    if (imperial) imperialReadings.Add(smtCase!.Code);
                    break;
                }
                caseToken ??= cells[c].Trim();
            }
        }
        if (smtCase is null)
        {
            var order = Enumerable.Range(valueEnd + 1, Math.Max(0, cells.Count - valueEnd - 1))
                                  .Concat(Enumerable.Range(0, Math.Max(0, valueEnd + 1)));
            foreach (int c in order)
            {
                if (used.Contains(c)) continue;
                if (TryParseCase(cells[c], out smtCase, out bool imperial))
                {
                    if (imperial) imperialReadings.Add(smtCase!.Code);
                    caseToken = null;
                    break;
                }
            }
        }
        if (smtCase is null && typeAt >= 0)
        {
            // A description's own case — "Resistor, 100 ohms, 0402, ±1%" — when no other cell had one.
            foreach (string piece in cells[typeAt].Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Regex.IsMatch(piece, @"^\d{4,5}$") || !TryParseCase(piece, out smtCase, out bool imperial)) continue;
                if (imperial) imperialReadings.Add(smtCase!.Code);
                caseToken = null;
                break;
            }
        }
        if (smtCase is null && caseToken is not null && !IsDnpWord(caseToken))
            unknownCases.Add(caseToken);

        if (value is null) noValue.Add(refsLabel);
        foreach (string r in refs)
            parts.Add(new BomPastePart(r, kind.Value, value, unit, smtCase, rec.Line));
    }

    /// <summary>The first cell of a role's columns that <paramref name="accept"/> takes, or -1.</summary>
    private static int FirstColumn(Dictionary<Role, List<int>> columns, Role role, List<string> cells,
                                   Func<string, bool> accept)
    {
        if (!columns.TryGetValue(role, out var list)) return -1;
        foreach (int c in list)
            if (c < cells.Count && accept(cells[c])) return c;
        return -1;
    }

    // ── shared with artwork recognition (brief-artsch-4-parts-and-parts-table.md R-as4-2/4) ──
    //
    // A bill of materials read beside the artwork names its parts in the same words a pasted table
    // does, so it is read by the same recognisers — these four entry points, not a second copy.

    /// <summary>A type word (or a free-text description's first word) → its kind, or the
    /// do-not-populate marker. A single letter is read only when it is the WHOLE cell — circuitRF's own
    /// bill of materials writes "C", "L" and "R" there — never as one word of a sentence, where it is noise.</summary>
    internal static (SymbolKind? Kind, bool Dnp) ReadTypeWord(string? cell) =>
        cell is { Length: > 0 } c ? ClassifyType(c, allowSingleLetter: true) : (null, false);

    /// <summary>Whether the whole cell is a do-not-populate marker (DNP, DNF, "not fitted", …).</summary>
    internal static bool IsNotFittedMarker(string? cell) => cell is { Length: > 0 } c && IsDnpWord(c);

    /// <summary>
    /// A value with its unit — "100 pF", "4R7", "10n0", "49.9Ω" — in base SI, with the dimension the
    /// unit states. <paramref name="description"/> reads the cell piece by piece as a description is
    /// read; otherwise the whole cell must be the value. The dimension is RETURNED, not checked: a
    /// "10nH" read for a capacitor comes back as an inductance so the caller can say so.
    /// </summary>
    internal static bool TryReadValue(string? cell, SymbolKind? kind, bool description,
                                      out double si, out UnitDimension dim)
    {
        si = 0;
        dim = UnitDimension.None;
        if (cell is not { Length: > 0 }) return false;

        string? value, unit;
        if (description)
        {
            if (DescriptionValue(cell, kind) is not { } found) return false;
            (value, unit, dim) = found;
        }
        else if (!TryParseValue(cell, kind, out value, out unit, out dim)) return false;

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) return false;
        si = n * PrefixScale(unit![..^1]);
        return double.IsFinite(si);
    }

    /// <summary>
    /// A value read before anyone knows what part it belongs to — a word on a schematic picture
    /// (brief-img-9-text-and-values.md R-im9-3). Where the cell states its unit (<c>10pF</c>, <c>4R7</c>,
    /// <c>1kΩ</c>) that is its one dimension. Where it leaves it off (<c>10p</c>, <c>2n2</c>, <c>1k</c>) it is read as
    /// <see cref="TryReadValue"/> reads it for each kind that could supply the unit, and <paramref name="dims"/> lists
    /// every dimension one did — the same number each time, since only the unit's letter differs.
    /// </summary>
    internal static bool TryReadValueOfAnyKind(string? cell, out double si, out IReadOnlyList<UnitDimension> dims)
    {
        dims = [];
        if (TryReadValue(cell, null, false, out si, out var dim))
        {
            dims = [dim];
            return true;
        }
        var found = new List<UnitDimension>();
        double first = 0;
        foreach (var kind in new[] { SymbolKind.Resistor, SymbolKind.Capacitor, SymbolKind.Inductor })
            if (TryReadValue(cell, kind, false, out double v, out var d))
            {
                if (found.Count == 0) first = v;
                found.Add(d);
            }
        si = first;
        dims = found;
        return found.Count > 0;
    }

    /// <summary>The SI multiplier of a unit's prefix, as <see cref="ParseUnit"/> spells them.</summary>
    private static double PrefixScale(string prefix) => prefix switch
    {
        "f" => 1e-15, "p" => 1e-12, "n" => 1e-9, "µ" => 1e-6, "m" => 1e-3,
        "k" => 1e3, "M" => 1e6, "G" => 1e9,
        _ => 1,
    };

    // ── recognisers ───────────────────────────────────────────────────────────

    private static bool IsRefsWord(string word)
    {
        string w = word.TrimEnd(',', ';');
        if (w.Length == 0) return false;
        return w.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries)
                .All(p => OneRef.IsMatch(p) || RangeRef.IsMatch(p));
    }

    private static bool IsRefsCell(string cell)
    {
        var pieces = cell.Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return pieces.Length > 0 && pieces.All(IsRefsWord);
    }

    /// <summary>
    /// A do-not-populate marker: DNP, NM, "Not assembled", a dash-only cell. The whole cell must be
    /// the marker — or every piece of a "DNP/NM" style list — and a cell holding a digit never is,
    /// so a dielectric "NP0" or a value "27 nm" is not read as one.
    /// </summary>
    private static bool IsDnpWord(string s)
    {
        string t = s.Trim();
        if (t.Length == 0) return false;
        if (t.All(ch => ch is '-' or '–' or '—')) return true;
        if (t.Any(char.IsDigit)) return false;
        var pieces = t.Split(['/', ',', ';'], StringSplitOptions.RemoveEmptyEntries);
        return pieces.Length > 0 && pieces.All(p => LettersOnly(p) is
            "dnp" or "dnf" or "dni" or "dnm" or "nofit" or "notfitted" or "donotpopulate" or "donotfit"
            or "donotmount" or "nc" or "np" or "nm" or "notassembled" or "notmounted" or "notplaced"
            or "notpopulated" or "unpopulated" or "nopop" or "nopopulate");
    }

    /// <summary>A value named inside a description cell — "Resistor, 100 ohms, 0402, ±1%",
    /// "Inductor, 27n, 0402" — tried piece by piece (comma-separated), then word by word.</summary>
    private static (string Value, string Unit, UnitDimension Dim)? DescriptionValue(string cell, SymbolKind? kind)
    {
        foreach (string piece in cell.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryParseValue(piece, kind, out var v, out var u, out var d)) return (v!, u!, d);
            foreach (string word in piece.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                if (TryParseValue(word, kind, out v, out u, out d)) return (v!, u!, d);
        }
        return null;
    }

    /// <summary>A type word → its kind, or the do-not-populate marker. Prefix matching, so a PDF's
    /// "Capacito" (its "r" wrapped away) still reads as a capacitor.</summary>
    private static (SymbolKind? Kind, bool Dnp) ClassifyType(string cell, bool allowSingleLetter,
                                                              bool typeColumn = false)
    {
        // A dash-only cell means "nothing" in the TYPE column; anywhere else it is just an empty
        // manufacturer or family cell and says nothing about fitting the part.
        if (IsDnpWord(cell) && (typeColumn || LettersOnly(cell).Length > 0)) return (null, true);
        string n = LettersOnly(cell);
        if (n.Length == 0) return (null, false);
        // A single letter only when it is the WHOLE cell: "C0805" is a case code, not a type.
        if (allowSingleLetter && cell.Trim().Length == 1)
        {
            if (n == "c") return (SymbolKind.Capacitor, false);
            if (n == "r") return (SymbolKind.Resistor, false);
            if (n == "l") return (SymbolKind.Inductor, false);
        }
        if (n.Length < 3) return (null, false);
        if (n.StartsWith("capacit", StringComparison.Ordinal) || n is "cap" or "caps" or "mlcc")
            return (SymbolKind.Capacitor, false);
        if (n.StartsWith("resist", StringComparison.Ordinal) || n is "res")
            return (SymbolKind.Resistor, false);
        if (n.StartsWith("induct", StringComparison.Ordinal) || n is "ind" or "coil" or "choke")
            return (SymbolKind.Inductor, false);

        // A free-text description — "CAP CER 100PF 50V 0402" — names its type in its first word.
        var words = cell.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1 && LettersOnly(words[0]).Length >= 3)
            return ClassifyType(words[0], allowSingleLetter: false);
        return (null, false);
    }

    private static SymbolKind? KindFromRefdes(string refdes)
    {
        string prefix = new([.. refdes.TakeWhile(char.IsLetter)]);
        return prefix.ToUpperInvariant() switch
        {
            "C" => SymbolKind.Capacitor,
            "R" => SymbolKind.Resistor,
            "L" or "FB" => SymbolKind.Inductor,
            _ => null,
        };
    }

    private static SymbolKind? KindFromDimension(UnitDimension d) => d switch
    {
        UnitDimension.Capacitance => SymbolKind.Capacitor,
        UnitDimension.Resistance => SymbolKind.Resistor,
        UnitDimension.Inductance => SymbolKind.Inductor,
        _ => null,
    };

    private static UnitDimension DimensionOf(SymbolKind k) => k switch
    {
        SymbolKind.Capacitor => UnitDimension.Capacitance,
        SymbolKind.Resistor => UnitDimension.Resistance,
        _ => UnitDimension.Inductance,
    };

    private static string NounOf(SymbolKind k) => k switch
    {
        SymbolKind.Capacitor => "a capacitance",
        SymbolKind.Resistor => "a resistance",
        _ => "an inductance",
    };

    /// <summary>
    /// A unit on its own → its dimension and the spelling the parameter editor's unit list uses.
    /// A bare "R" is deliberately NOT a unit here: in PDF text it is far more often the wrapped tail
    /// of "Capacitor" than an ohm.
    /// </summary>
    internal static (UnitDimension Dim, string Unit)? ParseUnit(string s)
    {
        string t = s.Trim().TrimEnd('.');
        if (t.Length == 0) return null;
        string lower = t.ToLowerInvariant();

        // Ohms, spelled any of the ways a table writes them.
        foreach (string ohm in new[] { "ohms", "ohm", "Ω", "Ω" })
        {
            if (!lower.EndsWith(ohm.ToLowerInvariant(), StringComparison.Ordinal)) continue;
            string p = t[..^ohm.Length];
            return p switch
            {
                "" => (UnitDimension.Resistance, "Ω"),
                "m" => (UnitDimension.Resistance, "mΩ"),
                "k" or "K" => (UnitDimension.Resistance, "kΩ"),
                "M" => (UnitDimension.Resistance, "MΩ"),
                "G" => (UnitDimension.Resistance, "GΩ"),
                _ => null,
            };
        }

        char last = char.ToUpperInvariant(t[^1]);
        if (last is not ('F' or 'H')) return null;
        string pre = t[..^1];
        var dim = last == 'F' ? UnitDimension.Capacitance : UnitDimension.Inductance;
        string? prefix = pre switch
        {
            "" => "",
            "f" when last == 'F' => "f",
            "p" or "P" => "p",
            "n" or "N" => "n",
            "u" or "U" or "µ" or "μ" => "µ",
            "m" => "m",
            _ => null,
        };
        return prefix is null ? null : (dim, prefix + last);
    }

    /// <summary>A resistor's multiplier written as a word of its own — the "K" of "10 K".</summary>
    private static (UnitDimension Dim, string Unit)? ResistorMultiplier(string s) => s.Trim() switch
    {
        "k" or "K" => (UnitDimension.Resistance, "kΩ"),
        "M" => (UnitDimension.Resistance, "MΩ"),
        "G" => (UnitDimension.Resistance, "GΩ"),
        _ => null,
    };

    /// <summary>A value written with its unit in one cell or word: "100 pF", "100pF", "49.9Ω",
    /// "4k7" (a resistor), "4n7".</summary>
    private static bool TryParseValue(string cell, SymbolKind? kind,
                                      out string? value, out string? unit, out UnitDimension dim)
    {
        value = unit = null;
        dim = UnitDimension.None;
        string t = cell.Trim().TrimEnd(',', ';').Trim();
        if (t.Length == 0) return false;

        // "100 pF" in one cell.
        var parts = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && Number.IsMatch(parts[0]) && ParseUnit(parts[1]) is { } u2)
        {
            (value, unit, dim) = (Trim(parts[0]), u2.Unit, u2.Dim);
            return true;
        }
        if (parts.Length != 1) return false;

        if (NumberWithTail.Match(t) is { Success: true } m)
        {
            string tail = m.Groups[2].Value;
            if (ParseUnit(tail) is { } u1)
            {
                (value, unit, dim) = (Trim(m.Groups[1].Value), u1.Unit, u1.Dim);
                return true;
            }
            // "4.7k" / "10K" / "1M" on a resistor: a multiplier with the ohm left off.
            if (kind == SymbolKind.Resistor && ParseUnit(tail + "Ω") is { } u3)
            {
                (value, unit, dim) = (Trim(m.Groups[1].Value), u3.Unit, u3.Dim);
                return true;
            }
            // "100R", "0R": R written for the ohm, glued to its digits. Unlike a bare "R" word in PDF
            // text, a glued R is not the wrapped tail of "Capacitor" — so it reads as ohms unless
            // the part is known to be a capacitor or inductor.
            if (tail is "R" or "r" && kind is null or SymbolKind.Resistor)
            {
                (value, unit, dim) = (Trim(m.Groups[1].Value), "Ω", UnitDimension.Resistance);
                return true;
            }
            // "10U", "100N", "27n", "22p" on a capacitor or inductor: the SI prefix with the F or H
            // left off, which only the part's kind can supply.
            if (kind is SymbolKind.Capacitor or SymbolKind.Inductor && tail.Length == 1 &&
                ParseUnit(tail + (kind == SymbolKind.Capacitor ? "F" : "H")) is { } u5)
            {
                (value, unit, dim) = (Trim(m.Groups[1].Value), u5.Unit, u5.Dim);
                return true;
            }
        }

        if (LetterAsPoint.Match(t) is { Success: true } lp)
        {
            string number = $"{lp.Groups[1].Value}.{lp.Groups[3].Value}";
            string letter = lp.Groups[2].Value;
            if (kind == SymbolKind.Resistor || (kind is null && letter is "R" or "k" or "K" or "M" or "G"))
            {
                string? r = letter switch { "R" => "Ω", "m" => "mΩ", "k" or "K" => "kΩ", "M" => "MΩ", "G" => "GΩ", _ => null };
                if (r is null) return false;
                (value, unit, dim) = (Trim(number), r, UnitDimension.Resistance);
                return true;
            }
            if (kind is SymbolKind.Capacitor or SymbolKind.Inductor && letter is not ("R" or "k" or "K" or "M" or "G"))
            {
                char base_ = kind == SymbolKind.Capacitor ? 'F' : 'H';
                if (ParseUnit(letter + base_) is { } u4)
                {
                    (value, unit, dim) = (Trim(number), u4.Unit, u4.Dim);
                    return true;
                }
            }
        }
        return false;

        static string Trim(string n) =>
            double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                ? d.ToString("0.############", CultureInfo.InvariantCulture)
                : n;
    }

    /// <summary>
    /// A case-size token → the case. Three digits are an imperial code whose leading zero a
    /// spreadsheet ate ("402" → 0402). A bare four-digit code that is ALSO a metric code is read as
    /// imperial — the convention a passive-part table is written in — and reported via
    /// <paramref name="imperialReading"/>, so the reading is stated rather than silent.
    /// </summary>
    internal static bool TryParseCase(string token, out SmtCase? found, out bool imperialReading)
    {
        found = null;
        imperialReading = false;
        string t = token.Trim();
        if (t.Length == 0 || t.Length > 24) return false;

        string code = Regex.IsMatch(t, @"^\d{3}$") ? "0" + t : t;
        if (SmtCaseTable.Find(code) is { } c)
        {
            found = c;
            imperialReading = FootprintTokens.AmbiguousTokens.Contains(code, StringComparer.Ordinal);
            return true;
        }

        // A decorated spelling — "C0402", "0402M", "smt:0402@N" — through the one normaliser the
        // rest of circuitRF uses. Only an unambiguous match is taken.
        if (t.Any(char.IsDigit) && FootprintTokens.Match(t) is { Outcome: FootprintTokenOutcome.Matched, Case: { } mc })
        {
            found = mc;
            return true;
        }
        return false;
    }

    private static string LettersOnly(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char ch in s)
            if (char.IsLetter(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }
}
