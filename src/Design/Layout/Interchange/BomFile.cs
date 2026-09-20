// The BILL OF MATERIALS a schematic tool exports beside the artwork
// (docs/sonnet-briefs/brief-railrf-2-companion-readers.md §2, railrf.md §2.2).
//
// THE NAMING HAZARD, AND THIS FILE IS WHERE IT BITES (R-rail2-14 item 4): in railRF "BOM" is a BILL
// OF MATERIALS, and in every text reader ever written "BOM" is a BYTE ORDER MARK. "BomFile strips a
// Bom" is a sentence that will be misread by whoever maintains this next. So: the BYTE ORDER MARK IS
// SPELLED OUT IN FULL, every time, everywhere in this file, and the abbreviation is only ever the
// bill of materials. The byte order mark itself is handled once, in DelimitedTables.Parse.
//
// THE GOVERNING RULE IS BoardNetlistFile's VERBATIM (R-rail2-1): THIS FILE IS EVIDENCE ABOUT THE
// ARTWORK. IT IS NEVER GEOMETRY. Nothing here creates a shape. A row naming a refdes the artwork does
// not have is a message with a count in it, never a part.
//
// WHAT IS PARSED OUT OF THE DESCRIPTION, AND WHY IT IS SHOWN (R-rail2-5). The description
// conventionally carries the dielectric class and the voltage rating — "MLCC 10n0 50V 0402 X7R
// ±10%" — which is exactly what derating (Q-12) and the ESR fallback (Q-15) need. So railRF PARSES
// IT AND SHOWS WHAT IT PARSED, beside the description it came from, for correction. IT NEVER
// SILENTLY ACTS ON A GUESS ABOUT A FREE-TEXT FIELD, and a field the parse could not recognise IS
// NULL AND STAYS NULL: not filled from the value column, not inferred from the case code, not
// defaulted per class. Brief 11's ESR fallback keys on the dielectric class, and a null one produces
// a part marked as having NO class rather than a part quietly given X7R's dissipation factor.
//
// THE JOIN IS ONE-TO-MANY AND THAT IS ORDINARY (R-rail2-14 item 1). §2.2: behind one internal part
// number sits a list of approved manufacturers with their own item codes, so a bill of materials
// with one row per approved manufacturer per part is an ordinary document. Nothing here keys a
// dictionary on refdes and assumes one row — RowsFor returns a LIST and the count above one is
// reported, because taking the first silently is the shape that produces a plausible model from the
// wrong manufacturer's part.

using System.Globalization;

using CircuitRF.Design.RailRf;

namespace CircuitRF.Design.Layout.Interchange;

/// <summary>
/// What was recognised in a free-text description — each field null where nothing was.
///
/// <para><b>Shown in the parts table beside the description it came from</b>, for correction. A
/// parsed dielectric class drives the ESR default (brief 11) and the derating (Q-12), and both of
/// those are wrong in an invisible way if the parse was.</para>
/// </summary>
public sealed record BomDescriptionParse(
    string? DielectricClass,   // X7R, X5R, C0G/NP0, …
    double? VoltageRatingV,
    string? CaseCode,          // 0402, 0603, …
    double? TolerancePercent)
{
    public static readonly BomDescriptionParse Nothing = new(null, null, null, null);

    /// <summary>Whether the parse recognised anything at all. A description that yielded nothing is
    /// shown as such rather than as an empty row of blanks.</summary>
    public bool Any => DielectricClass is not null || VoltageRatingV is not null ||
                       CaseCode is not null || TolerancePercent is not null;
}

/// <summary>One bill-of-materials row, after a grouped reference cell has been expanded — so one row
/// here is one REFERENCE, and a grouped source line produces several of these sharing a
/// <see cref="Line"/>.</summary>
public sealed record BomRow(
    string Refdes,
    string? PartNumber,
    string? Value,
    string? Footprint,
    string? Description)
{
    /// <summary>What was recognised in <see cref="Description"/> — each field null where nothing was.
    /// <b>Shown in the parts table beside the description it came from</b>, for correction. A parsed
    /// dielectric class drives the ESR default (brief 11) and the derating (Q-12), and both of those
    /// are wrong in an invisible way if the parse was.</summary>
    public BomDescriptionParse Parsed { get; init; } = BomDescriptionParse.Nothing;

    /// <summary>The 1-based line of the source row this came from. Several rows share one where a
    /// reference cell was grouped.</summary>
    public int Line { get; init; }

    /// <summary>The manufacturer's own item code, where the file carries a column for it. Carried
    /// and never used to key anything: <b>parts are identified by internal PART NUMBER</b> (§2.2),
    /// and behind one of those sits a list of approved manufacturers.</summary>
    public string? ManufacturerItem { get; init; }
}

/// <summary>Everything one bill of materials turned out to be. <see cref="Refusal"/> non-null means
/// nothing was read and nothing may be used.</summary>
public sealed record BomTable(
    string Path,
    string? Refusal,
    char Delimiter,
    IReadOnlyList<BomRow> Rows,
    int SourceRowCount,
    int UnreadableRows,
    IReadOnlyList<RailAggressor> RecognisedAggressors,
    IReadOnlyList<string> Diagnostics)
{
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>
    /// Every reference cell that looked like a range and could NOT be expanded, taken as written.
    /// </summary>
    /// <remarks>
    /// <b>A COUNT a caller can report, not only a sentence in <see cref="Diagnostics"/></b>
    /// (R-rail22-3b). The three numbers that say what a bill of materials actually gave up —
    /// <see cref="SourceRowCount"/>, <c>Rows.Count</c> and this — were all computed and only the
    /// first two were reachable, so a BOM that read nine parts out of thirteen could only be
    /// inferred from an odd answer later.
    /// </remarks>
    public IReadOnlyList<string> UnexpandedCells { get; init; } = [];

    /// <summary>
    /// Every row for one reference. <b>A LIST, and that is R-rail2-14 item 1</b>: one internal part
    /// number sits in front of a list of approved manufacturers, so more than one row per reference
    /// is ordinary and taking the first silently is what produces a plausible model from the wrong
    /// manufacturer's part.
    /// </summary>
    public IReadOnlyList<BomRow> RowsFor(string refdes) =>
        [.. Rows.Where(r => string.Equals(r.Refdes, refdes, StringComparison.OrdinalIgnoreCase))];

    /// <summary>Every reference carrying more than one row, with its count. The number a caller
    /// reports rather than resolving.</summary>
    public IReadOnlyDictionary<string, int> Ambiguous =>
        Rows.GroupBy(r => r.Refdes, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Every distinct internal part number this bill of materials names, which is what a
    /// part library's coverage is measured over.</summary>
    public IReadOnlyCollection<string> PartNumbers =>
        [.. Rows.Select(r => r.PartNumber).OfType<string>()
               .Distinct(StringComparer.OrdinalIgnoreCase)];
}

public static class BomFile
{
    /// <summary>How many data rows are needed before a header that matches is believed.
    /// <see cref="PlacementFile.MinimumRows"/>'s reason.</summary>
    public const int MinimumRows = 2;

    /// <summary>The two flags that separate a bill of materials from a placement table when a header
    /// matches both (R-rail2-12).</summary>
    public const string KindFlags = PlacementFile.KindFlags;

    private static readonly string[] RefdesNames =
        ["refdes", "ref", "reference", "references", "designator", "designators", "part reference",
         "component", "components", "comp", "ref des", "reference designator"];

    private static readonly string[] PartNumberNames =
        ["part number", "partnumber", "part no", "part num", "pn", "internal part number",
         "item number", "item no", "ipn", "internal pn", "material", "part id"];

    private static readonly string[] ManufacturerItemNames =
        ["manufacturer part number", "mpn", "manufacturer part", "mfr part number", "mfg part number",
         "supplier part number", "vendor part number", "manufacturer item"];

    private static readonly string[] ValueNames = ["value", "val", "rating", "component value"];

    private static readonly string[] FootprintNames =
        ["footprint", "package", "pattern", "land pattern", "fp", "decal", "pkg", "case",
         "footprint name"];

    private static readonly string[] DescriptionNames =
        ["description", "desc", "part description", "text", "comment", "notes", "long description"];

    private static readonly string[] QuantityNames = ["qty", "quantity", "qnty", "count"];

    // ── Recognition ───────────────────────────────────────────────────────────
    //
    // Runs AFTER the artwork, drill and board-netlist tests, and after the placement test, which is
    // GI4's ordering doctrine as R-rail2-12 restates it: nothing here may take a file one of those
    // already claimed.

    /// <summary>Whether <paramref name="head"/> is a bill of materials, over text already in hand.</summary>
    public static bool Recognize(string head, out string why)
    {
        why = "";
        var table = DelimitedTables.Parse(head);
        if (FindHeader(table) is not { } index) return false;
        if (table.Rows.Count - index - 1 < MinimumRows) return false;
        if (PlacementFile.HeaderMatches(table.Rows[index].Fields)) return false;   // ambiguous: not claimed

        why = $"a bill of materials ({table.Rows.Count - index - 1} row(s), " +
              $"'{table.Delimiter}' separated)";
        return true;
    }

    /// <summary>
    /// Whether a header row names the columns this reader needs. The "stated minimum" R-rail2-12
    /// requires: a reference, AND one of the three columns that make the table a bill of materials
    /// rather than something else with a reference column in it.
    ///
    /// <para><b>A value column alone does not qualify</b>, deliberately: a placement export very
    /// often carries one, and counting it would make every such file ambiguous and every such import
    /// a refusal. The distinguishing columns are the part number, the quantity and the
    /// description.</para>
    /// </summary>
    public static bool HeaderMatches(IReadOnlyList<string> header) =>
        DelimitedTables.IndexOf(header, RefdesNames) >= 0 &&
        (DelimitedTables.IndexOf(header, PartNumberNames) >= 0 ||
         DelimitedTables.IndexOf(header, QuantityNames) >= 0 ||
         DelimitedTables.IndexOf(header, DescriptionNames) >= 0);

    // ── Reading ───────────────────────────────────────────────────────────────

    /// <param name="columns">R-rail2-14 item 3: the column names in order, for a file with NO HEADER
    /// ROW AT ALL. Null and a missing header is a refusal naming the flag — never a positional
    /// guess.</param>
    public static BomTable Read(
        string path, string text,
        char? delimiter = null,
        IReadOnlyList<string>? columns = null)
    {
        string full = System.IO.Path.GetFullPath(path);
        var diagnostics = new List<string>();
        var table = DelimitedTables.Parse(text, delimiter);

        BomTable Refuse(string sentence) =>
            new(full, sentence, table.Delimiter, [], 0, 0, [], diagnostics);

        if (table.Rows.Count == 0)
            return Refuse($"{System.IO.Path.GetFileName(full)} holds no rows that could be read, so " +
                          "nothing was taken from it.");

        IReadOnlyList<string> header;
        int firstDataRow;

        if (FindHeader(table) is { } index)
        {
            header = table.Rows[index].Fields;
            firstDataRow = index + 1;

            if (PlacementFile.HeaderMatches(header))
                return Refuse(
                    $"{System.IO.Path.GetFileName(full)} has a header row that reads both as a bill " +
                    $"of materials and as a placement table, and the two are not distinguishable " +
                    $"from it. State which it is with {KindFlags}.");
        }
        else if (columns is { Count: > 0 })
        {
            header = columns;
            firstDataRow = 0;
            diagnostics.Add(
                $"{System.IO.Path.GetFileName(full)} states no header row; the {columns.Count} column " +
                "name(s) given for this run were used.");
        }
        else
        {
            return Refuse(
                $"{System.IO.Path.GetFileName(full)} states no header row naming its columns, and " +
                "column order is not a standard — a positional reading would put the value column in " +
                $"the footprint column, silently. Name them with --columns. {Classify(text)}");
        }

        int cRefdes = DelimitedTables.IndexOf(header, RefdesNames);
        if (cRefdes < 0)
            return Refuse(
                $"{System.IO.Path.GetFileName(full)} names no reference column, so its rows cannot be " +
                $"attached to anything on the board. {Classify(text)}");

        int cPartNumber = DelimitedTables.IndexOf(header, PartNumberNames);
        int cManufacturerItem = DelimitedTables.IndexOf(header, ManufacturerItemNames);
        int cValue = DelimitedTables.IndexOf(header, ValueNames);
        int cFootprint = DelimitedTables.IndexOf(header, FootprintNames);
        int cDescription = DelimitedTables.IndexOf(header, DescriptionNames);

        var rows = new List<BomRow>();
        var unexpanded = new List<string>();
        int sourceRows = 0, unreadable = 0, grouped = 0;

        for (int i = firstDataRow; i < table.Rows.Count; i++)
        {
            var source = table.Rows[i];
            string? cell = source.Field(cRefdes);
            if (cell is null) continue;

            sourceRows++;

            // R-rail2-14 item 2. A grouped cell — "C1-C9", "C1,C2,C3", "C1 C2 C3" — is the NORMAL
            // way a purchasing document is written, and a reader that treats "C1-C9" as a reference
            // named "C1-C9" matches nothing and reports nine unresolved parts, which reads as a
            // broken board.
            var set = RefdesCell.Parse(cell);
            if (set.Refdes.Count == 0) { unreadable++; continue; }
            if (set.Expanded) grouped++;
            unexpanded.AddRange(set.Unexpanded);

            string? description = cDescription >= 0 ? source.Field(cDescription) : null;
            var parsed = ParseDescription(description);

            foreach (string refdes in set.Refdes)
                rows.Add(new BomRow(
                    refdes,
                    cPartNumber >= 0 ? source.Field(cPartNumber) : null,
                    cValue >= 0 ? source.Field(cValue) : null,
                    cFootprint >= 0 ? source.Field(cFootprint) : null,
                    description)
                {
                    Parsed = parsed,
                    Line = source.Line,
                    ManufacturerItem = cManufacturerItem >= 0 ? source.Field(cManufacturerItem) : null,
                });
        }

        if (rows.Count == 0)
            return Refuse($"{System.IO.Path.GetFileName(full)} has a bill-of-materials header and no " +
                          "row under it that could be read.");

        var (aggressors, unfrequenced) = RecogniseAggressors(rows);

        if (table.HadByteOrderMark)
            diagnostics.Add("The file begins with a UNICODE BYTE ORDER MARK (U+FEFF) — not a bill of " +
                            "materials; see this reader's own header — which was removed before the " +
                            "header row was read.");
        if (grouped > 0)
            diagnostics.Add($"{grouped:N0} source row(s) name more than one reference in one cell and " +
                            $"were expanded into {rows.Count:N0} row(s).");
        if (unexpanded.Count > 0)
            diagnostics.Add($"{unexpanded.Count:N0} reference cell fragment(s) look like a range and " +
                            $"were NOT expanded, and were taken as written: " +
                            $"{string.Join(", ", unexpanded.Take(8))}" +
                            (unexpanded.Count > 8 ? ", …" : "") + ".");
        if (unreadable > 0)
            diagnostics.Add($"{unreadable:N0} row(s) carried a reference cell nothing could be read " +
                            "from, and were skipped.");

        var duplicates = rows.GroupBy(r => r.Refdes, StringComparer.OrdinalIgnoreCase)
                             .Count(g => g.Count() > 1);
        if (duplicates > 0)
            diagnostics.Add($"{duplicates:N0} reference(s) carry more than one row — one internal part " +
                            "number sits in front of a list of approved manufacturers, so this is " +
                            "ordinary. All of them were kept; none was chosen.");

        if (unfrequenced > 0)
            diagnostics.Add($"{unfrequenced:N0} part(s) read as a crystal, oscillator or converter and " +
                            "state no frequency, so no aggressor row was pre-filled for them. Type " +
                            "them if they excite this rail.");

        return new BomTable(full, null, table.Delimiter, rows, sourceRows, unreadable, aggressors,
                            diagnostics)
        {
            UnexpandedCells = unexpanded,
        };
    }

    /// <summary>Reads a bill of materials from disk. A companion file that cannot be read is not a
    /// reason to fail an import of the artwork beside it.</summary>
    public static BomTable? ReadFile(string path, char? delimiter = null, IReadOnlyList<string>? columns = null)
    {
        try
        {
            return Read(path, File.ReadAllText(path), delimiter, columns);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    // ── R-rail2-5: the description parse ──────────────────────────────────────

    /// <summary>The EIA dielectric class codes. Two families: the Class II/III three-character codes
    /// (a letter for the low temperature, a digit for the high, a letter for the tolerance over that
    /// range) and the Class I codes, which are their own words.</summary>
    private static readonly string[] ClassOneCodes = ["C0G", "NP0", "NPO", "U2J", "P90", "C0H", "C0K"];

    /// <summary>
    /// The imperial case codes. IMPERIAL ONLY, and deliberately: 0402 imperial and 0402 metric are
    /// different parts (the metric one is 01005 imperial), and a description carrying a bare four
    /// digits does not say which system it is in. Matching one system and leaving the other's codes
    /// unrecognised produces a NULL, which R-rail2-5 requires; matching both would produce a
    /// confident wrong answer on exactly the small parts where it matters.
    /// </summary>
    private static readonly string[] CaseCodes =
        ["01005", "0201", "0402", "0603", "0805", "1008", "1111", "1206", "1210", "1218", "1806",
         "1812", "2010", "2220", "2225", "2512", "2920"];

    /// <summary>
    /// R-rail2-5. What can be recognised in a free-text description, and nothing more.
    ///
    /// <para><b>A field the parse could not recognise is null and stays null</b> — not filled from
    /// the value column, not inferred from the case code, not defaulted per class.</para>
    /// </summary>
    public static BomDescriptionParse ParseDescription(string? description)
    {
        if (description is null || description.Trim().Length == 0) return BomDescriptionParse.Nothing;

        string? dielectric = null, caseCode = null;
        double? volts = null, tolerance = null;

        foreach (string raw in Tokens(description))
        {
            string token = raw.Trim('(', ')', '[', ']', ',', ';');
            if (token.Length == 0) continue;
            string upper = token.ToUpperInvariant();

            dielectric ??= DielectricClass(upper);
            caseCode ??= CaseCodes.Contains(upper, StringComparer.Ordinal) ? upper : null;
            volts ??= Volts(upper);
            tolerance ??= Tolerance(token);
        }

        return new BomDescriptionParse(dielectric, volts, caseCode, tolerance);
    }

    private static IEnumerable<string> Tokens(string text) =>
        text.Split([' ', '\t', ',', ';', '/', '|'], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>A dielectric class token, or null. The Class II/III pattern is letter-digit-letter
    /// over the EIA temperature codes; anything else has to be in the Class I list by name.</summary>
    private static string? DielectricClass(string upper)
    {
        if (ClassOneCodes.Contains(upper, StringComparer.Ordinal))
            return upper is "NPO" ? "NP0" : upper;          // the letter O for the digit zero, which is a typo in wide circulation

        if (upper.Length != 3) return null;
        if (upper[0] is not ('X' or 'Y' or 'Z')) return null;
        if (!char.IsAsciiDigit(upper[1])) return null;
        if (upper[2] is not ('D' or 'E' or 'F' or 'P' or 'R' or 'S' or 'T' or 'U' or 'V')) return null;
        return upper;
    }

    /// <summary>A voltage rating token — <c>50V</c>, <c>6.3V</c>, <c>50VDC</c>, <c>16WV</c> — or null.
    /// The unit letter is REQUIRED: a bare number in a description is a quantity, a length, a part of
    /// a code, or anything else, and reading one as a voltage rating is the kind of guess this whole
    /// parse exists not to make.</summary>
    private static double? Volts(string upper)
    {
        int i = 0;
        while (i < upper.Length && (char.IsAsciiDigit(upper[i]) || upper[i] == '.')) i++;
        if (i == 0) return null;

        string suffix = upper[i..];
        if (suffix is not ("V" or "VDC" or "VD" or "WV" or "VW" or "VOLT" or "VOLTS")) return null;
        if (!double.TryParse(upper[..i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return null;
        return v > 0 ? v : null;
    }

    /// <summary>A tolerance token — <c>±10%</c>, <c>+/-10%</c>, <c>10%</c> — or null. <b>The percent
    /// sign is required.</b> The single-letter EIA tolerance codes (J, K, M) are NOT read: turning a
    /// letter in a free-text field into a number is exactly the silent guess R-rail2-5 forbids, and a
    /// K in a description is as likely to be a kilo- prefix.</summary>
    private static double? Tolerance(string token)
    {
        string t = token.Replace("±", "").Replace("+/-", "").Replace("+-", "").Trim();
        if (!t.EndsWith('%')) return null;
        return double.TryParse(t[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0
            ? v : null;
    }

    // ── R-rail2-6: the aggressor pre-fill, and it is a SUGGESTION ─────────────

    private static readonly string[] CrystalWords =
        ["XTAL", "CRYSTAL", "RESONATOR", "OSCILLATOR", "OSC", "TCXO", "VCXO", "SAW"];

    private static readonly string[] ConverterWords =
        ["DCDC", "DC-DC", "BUCK", "BOOST", "SMPS", "CONVERTER", "SWITCHER", "SWITCHING"];

    /// <summary>
    /// R-rail2-6. Where the bill of materials names a crystal or a converter AND states a frequency,
    /// emit an aggressor row marked as recognised rather than typed.
    ///
    /// <para><b>It is a row the user can DELETE.</b> A pre-filled frequency nobody checked is exactly
    /// the one that will be wrong, which is why <see cref="RailAggressor.Origin"/> exists and
    /// why the row says where it came from. Recognition is <b>by value and description shape only</b>;
    /// a part this cannot classify contributes NO ROW rather than a guessed one — and a part it
    /// classifies but that states no frequency also contributes no row, and is counted instead.</para>
    ///
    /// <para>The HARMONIC COUNT is 1 — the fundamental alone, which is the fewest a row can mean.
    /// Nothing in a bill of materials carries a harmonic count, so nothing here invents one.</para>
    /// </summary>
    private static (IReadOnlyList<RailAggressor> Rows, int Unfrequenced) RecogniseAggressors(
        IReadOnlyList<BomRow> rows)
    {
        var found = new List<RailAggressor>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int unfrequenced = 0;

        foreach (var row in rows)
        {
            string text = $"{row.Value} {row.Description}".ToUpperInvariant();
            bool crystal = CrystalWords.Any(w => text.Contains(w, StringComparison.Ordinal));
            bool converter = ConverterWords.Any(w => text.Contains(w, StringComparison.Ordinal));
            if (!crystal && !converter) continue;

            if (Frequency(text) is not { } hz) { unfrequenced++; continue; }

            string name = $"{row.Refdes} {(crystal ? "crystal" : "converter")}";
            if (!seen.Add($"{name}|{hz}")) continue;

            found.Add(new RailAggressor(name, hz, 1)
            {
                Origin = RailAggressorOrigin.Bom,
            });
        }

        return (found, unfrequenced);
    }

    /// <summary>The first frequency in a piece of text, in HERTZ — base SI, like every other
    /// frequency in this feature. <b>The unit is required</b>: a mark read without its scale once
    /// produced a run at 2 Hz that looked entirely normal (src/Engine/RESOLVED.md).</summary>
    private static double? Frequency(string upper)
    {
        for (int i = 0; i < upper.Length; i++)
        {
            if (!char.IsAsciiDigit(upper[i])) continue;
            if (i > 0 && (char.IsAsciiLetterOrDigit(upper[i - 1]) || upper[i - 1] == '.')) continue;

            int j = i;
            while (j < upper.Length && (char.IsAsciiDigit(upper[j]) || upper[j] == '.')) j++;
            if (!double.TryParse(upper[i..j], NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
            { i = j; continue; }

            int k = j;
            while (k < upper.Length && upper[k] == ' ') k++;

            double scale = 0;
            int after = k;
            if (upper.AsSpan(k).StartsWith("GHZ")) { scale = 1e9; after = k + 3; }
            else if (upper.AsSpan(k).StartsWith("MHZ")) { scale = 1e6; after = k + 3; }
            else if (upper.AsSpan(k).StartsWith("KHZ")) { scale = 1e3; after = k + 3; }
            else if (upper.AsSpan(k).StartsWith("HZ")) { scale = 1; after = k + 2; }

            // A suffix that runs on into another word is part of that word, not a unit.
            if (scale > 0 && (after >= upper.Length || !char.IsAsciiLetterOrDigit(upper[after])))
                return n * scale;

            i = j;
        }
        return null;
    }

    // ── shared ────────────────────────────────────────────────────────────────

    private static int? FindHeader(DelimitedTable table)
    {
        int limit = Math.Min(table.Rows.Count, 5);
        for (int i = 0; i < limit; i++)
            if (HeaderMatches(table.Rows[i].Fields)) return i;
        return null;
    }

    /// <summary>R-rail2-12: what this file actually is, through the import's OWN classifier, so a
    /// Gerber or a drill file handed to this reader is NAMED rather than called unreadable.</summary>
    private static string Classify(string text)
    {
        var kind = GerberFileClassifier.ClassifyContent("", text);
        return kind.Kind == GerberFileKind.Other
            ? "It is not any file kind circuitRF recognises."
            : $"It reads as {kind.Why}.";
    }
}
