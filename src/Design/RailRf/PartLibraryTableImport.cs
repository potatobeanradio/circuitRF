// Reads a table of parts — a .csv a designer exported from a spreadsheet or a supplier's selection
// tool — into a part library: part number, capacitance, self-resonance, ESL, ESR, class, rating,
// footprint, description (field report, 2026-09-23; the owner chose .csv only).
//
// ── A NUMBER WITH NO UNIT IS NOT READ ────────────────────────────────────────────────────────────
//
// The library editor refuses a bare capacitance, self-resonance or inductance, and so does this: a
// column is read only where its HEADER states the unit ("C (pF)", "resonance (MHz)", "L (nH)") or its
// cells do ("100nF"). The table this was written for carries both an "L" column in henries with no
// unit and an "L (nH)" column; the first is reported as not read and the second is used. Ohms and
// volts are the exception, as they are in the editor: a bare ESR or rating means what anybody means.
//
// ── A VALUE COLUMN THAT DISAGREES WITH THE C COLUMN STOPS THE C ─────────────────────────────────
//
// That same table states one part as "150nF" in its Value column and "1500000.00" in "C (pF)" —
// 1.5 µF, ten times more. Nothing in either cell looks wrong on its own. Where a row has both and they
// differ by more than 5 %, neither is imported for that row and the row is named: choosing one would
// be a guess, and a capacitance off by ten moves a resonance by √10.
//
// ── A PART NUMBER MAY BE A PREFIX OF THE LIBRARY'S ───────────────────────────────────────────────
//
// A supplier's selection tool often finds a part only once the trailing packaging code is deleted, so
// a table exported from one can name "…KA88" for the library's "…KA88D". A row matches exactly
// (ignoring case) first; failing that, it matches the ONE library row whose part number it is a prefix
// of, or that is a prefix of it — at least eight characters, and never where two rows would qualify.
// Every such match is named in the report, because it is the one decision here a reader should check.

using System.Globalization;
using System.Text.RegularExpressions;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Matching;

namespace CircuitRF.Design.RailRf;

/// <summary>What an import did to a library, and everything a reader should check.</summary>
/// <param name="Added">Part numbers that got a new row.</param>
/// <param name="Updated">Part numbers whose existing row took at least one value.</param>
/// <param name="Notes">Columns not read, rows skipped, prefix matches, disagreements — in order.</param>
public sealed record PartLibraryImportReport(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Updated,
    IReadOnlyList<string> Notes)
{
    /// <summary>The one-line summary a window shows above the notes.</summary>
    public string Summary =>
        $"{Added.Count} part(s) added, {Updated.Count} updated" +
        (Notes.Count > 0 ? $"; {Notes.Count} note(s) below." : ".");
}

public static class PartLibraryTableImport
{
    /// <summary>The shortest part number a prefix match is allowed on — short enough for a trimmed
    /// packaging code, long enough that a bare series name never matches a whole family.</summary>
    public const int MinimumPrefixLength = 8;

    private static readonly string[] PartNumberNames =
        ["part number", "partnumber", "part no", "part num", "pn", "internal part number", "item number",
         "ipn", "manufacturer part number", "mpn", "mfr part number", "mfg part number"];
    private static readonly string[] DescriptionNames = ["description", "desc", "note", "notes", "comment"];
    private static readonly string[] FootprintNames = ["footprint", "package", "case", "size", "pkg"];
    private static readonly string[] ClassNames =
        ["class", "dielectric", "dielectric class", "tc", "temperature characteristic"];
    private static readonly string[] RatingNames =
        ["v rating", "voltage rating", "rated voltage", "voltage", "vr"];
    private static readonly string[] ValueNames = ["value", "val"];
    private static readonly string[] CapacitanceNames = ["c", "capacitance", "cap"];
    private static readonly string[] ResonanceNames =
        ["f0", "fo", "f", "srf", "resonance", "self resonance", "self resonant frequency",
         "resonant frequency", "resonance frequency"];
    private static readonly string[] InductanceNames =
        ["l", "esl", "inductance", "esl datasheet", "l stated"];
    private static readonly string[] EsrNames = ["esr"];

    private static readonly Regex HeaderUnit = new(@"[\(\[]\s*([^\)\]]+?)\s*[\)\]]\s*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads <paramref name="text"/> and applies it to <paramref name="library"/> in place.
    /// </summary>
    /// <param name="fileName">What the report calls the table.</param>
    public static PartLibraryImportReport Apply(PartLibrary library, string text, string fileName)
    {
        ArgumentNullException.ThrowIfNull(library);
        var notes = new List<string>();

        var table = DelimitedTables.Parse(text ?? "");
        int headerIndex = -1;
        for (int i = 0; i < table.Rows.Count && headerIndex < 0; i++)
            if (DelimitedTables.IndexOf(table.Rows[i].Fields, PartNumberNames) >= 0) headerIndex = i;

        if (headerIndex < 0)
        {
            notes.Add($"{fileName} has no part-number column, so nothing was read. The library is keyed " +
                      "by part number; name the column \"Part number\".");
            return new([], [], notes);
        }

        var header = table.Rows[headerIndex].Fields;
        int pn   = DelimitedTables.IndexOf(header, PartNumberNames);
        int desc = DelimitedTables.IndexOf(header, DescriptionNames);
        int fp   = DelimitedTables.IndexOf(header, FootprintNames);
        int cls  = DelimitedTables.IndexOf(header, ClassNames);
        int vr   = DelimitedTables.IndexOf(header, RatingNames);
        int val  = DelimitedTables.IndexOf(header, ValueNames);

        var c   = Pick(header, CapacitanceNames, MatchQuantity.Capacitance, table, headerIndex, "capacitance", notes);
        var f0  = Pick(header, ResonanceNames,  MatchQuantity.Frequency,   table, headerIndex, "self-resonance", notes);
        var l   = Pick(header, InductanceNames, MatchQuantity.Inductance,  table, headerIndex, "inductance", notes);
        var esr = Pick(header, EsrNames,        MatchQuantity.Resistance,  table, headerIndex, "ESR", notes, bareUnit: "Ω");

        var added = new List<string>();
        var updated = new List<string>();
        var unread = new Dictionary<int, int>();   // column → cells that held text and did not read
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int r = headerIndex + 1; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];
            if (row.Field(pn) is not { } partNumber) continue;
            if (!seen.Add(partNumber))
            {
                notes.Add($"Line {row.Line}: {partNumber} appears again; its first row was used.");
                continue;
            }

            // With no capacitance column, a Value that carries a farad unit IS the marked value.
            double? cap = c is null
                ? (val >= 0 ? ReadValue(row.Field(val)) : null)
                : Read(row, c, MatchQuantity.Capacitance, unread);
            if (cap is { } cv && val >= 0 && ReadValue(row.Field(val)) is { } marked
                && Math.Abs(marked - cv) > 0.05 * marked)
            {
                notes.Add($"Line {row.Line}: {partNumber}'s value says {Farads(marked)} and its " +
                          $"capacitance column says {Farads(cv)}, so neither was imported — correct " +
                          "the table, or type it on the row.");
                cap = null;
            }

            var target = Find(library, partNumber, row.Line, notes, out bool ambiguous);
            if (ambiguous) continue;

            bool isNew = target is null;
            target ??= new PartLibraryRow { PartNumber = partNumber };

            bool changed = false;
            changed |= Set(target.Description, row.Field(desc), v => target.Description = v);
            changed |= Set(target.Footprint, row.Field(fp), v => target.Footprint = v);
            changed |= Set(target.DielectricClass, row.Field(cls), v => target.DielectricClass = v);
            changed |= Set(target.VoltageRatingV, ReadVolts(row.Field(vr)), v => target.VoltageRatingV = v);
            changed |= Set(target.CapacitanceFarads, cap, v => target.CapacitanceFarads = v);
            changed |= Set(target.SelfResonantFrequencyHz, Read(row, f0, MatchQuantity.Frequency, unread), v => target.SelfResonantFrequencyHz = v);
            changed |= Set(target.StatedInductanceHenries, Read(row, l, MatchQuantity.Inductance, unread), v => target.StatedInductanceHenries = v);
            changed |= Set(target.EsrOhms, Read(row, esr, MatchQuantity.Resistance, unread), v => target.EsrOhms = v);

            if (isNew) { library.Rows.Add(target); added.Add(target.PartNumber); }
            else if (changed) updated.Add(target.PartNumber);
        }

        foreach (var (column, count) in unread)
            notes.Add($"{count} cell(s) of column \"{header[column].Trim()}\" did not read as a number " +
                      "with a unit, and were left out. A decimal comma is one cause: write 28.89, not 28,89.");

        return new(added, updated, notes);
    }

    /// <summary>A numeric column, and the unit its header states (null where only the cells can).</summary>
    private sealed record Column(int Index, string? Unit, string? BareUnit);

    /// <summary>
    /// The column for one quantity: of every column its name matches, the first whose HEADER states a
    /// unit, else the first whose cells carry one. The rest are reported, so "L" beside "L (nH)" says
    /// which was read.
    /// </summary>
    private static Column? Pick(
        IReadOnlyList<string> header, string[] names, MatchQuantity quantity, DelimitedTable table,
        int headerIndex, string what, List<string> notes, string? bareUnit = null)
    {
        var candidates = Enumerable.Range(0, header.Count)
            .Where(i => names.Contains(DelimitedTables.NormalizeHeader(header[i]), StringComparer.Ordinal))
            .ToList();
        if (candidates.Count == 0) return null;

        Column? chosen = null;
        var refused = new List<string>();
        foreach (int i in candidates)
        {
            var m = HeaderUnit.Match(header[i]);
            string? unit = m.Success ? MatchValueFormat.TryMatchUnit(m.Groups[1].Value, quantity) : null;
            if (m.Success && unit is null)
            {
                notes.Add($"Column \"{header[i].Trim()}\" states a unit that is not a {what} unit, so it " +
                          "was not read.");
                continue;
            }

            bool cellsCarryUnits = unit is null && bareUnit is null && table.Rows.Skip(headerIndex + 1)
                .Select(row => row.Field(i)).OfType<string>().Take(20)
                .Any(cell => MatchValueFormat.SplitTypedValue(cell).Unit.Length > 0);

            if (unit is not null || cellsCarryUnits || bareUnit is not null)
            {
                if (chosen is null) chosen = new Column(i, unit, bareUnit);
                else refused.Add(header[i].Trim());
                continue;
            }
            refused.Add(header[i].Trim());
        }

        foreach (string name in refused)
            notes.Add(chosen is null
                ? $"Column \"{name}\" states no unit, so its {what} was not read — a bare number's scale " +
                  "would be a guess. Put the unit in the header, e.g. \"" + Example(quantity) + "\"."
                : $"Column \"{name}\" was not read: \"{header[chosen.Index].Trim()}\" is the {what} used.");
        return chosen;
    }

    private static string Example(MatchQuantity q) => q switch
    {
        MatchQuantity.Capacitance => "C (pF)",
        MatchQuantity.Frequency   => "resonance (MHz)",
        MatchQuantity.Inductance  => "L (nH)",
        _                         => "ESR (mΩ)",
    };

    /// <summary>One cell in base SI, or null where it is blank or does not read.</summary>
    private static double? Read(DelimitedRow row, Column? column, MatchQuantity quantity, Dictionary<int, int> unread)
    {
        if (column is null || row.Field(column.Index) is not { } cell) return null;
        var (number, token) = MatchValueFormat.SplitTypedValue(cell);
        string? unit = token.Length > 0 ? MatchValueFormat.TryMatchUnit(token, quantity)
                     : column.Unit ?? column.BareUnit;
        double v = double.NaN;
        if (unit is not null
            && double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double raw))
            v = raw * MatchValueFormat.Scale(unit);
        if (double.IsFinite(v) && v > 0) return v;

        unread[column.Index] = unread.GetValueOrDefault(column.Index) + 1;
        return null;
    }

    /// <summary>A Value cell read as a capacitance — only where it carries a farad unit.</summary>
    private static double? ReadValue(string? cell)
    {
        if (cell is null) return null;
        var (number, token) = MatchValueFormat.SplitTypedValue(cell);
        if (token.Length == 0 || MatchValueFormat.TryMatchUnit(token, MatchQuantity.Capacitance) is not { } unit)
            return null;
        return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double raw)
            ? raw * MatchValueFormat.Scale(unit) : null;
    }

    private static double? ReadVolts(string? cell)
    {
        if (cell is null) return null;
        string s = cell.Trim().TrimEnd('V', 'v').Trim();
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0 ? v : null;
    }

    private static PartLibraryRow? Find(PartLibrary library, string partNumber, int line, List<string> notes,
                                        out bool ambiguous)
    {
        ambiguous = false;
        if (library.Part(partNumber) is { } exact) return exact;

        var prefixed = library.Rows.Where(r =>
                Math.Min(r.PartNumber.Length, partNumber.Length) >= MinimumPrefixLength
                && (r.PartNumber.StartsWith(partNumber, StringComparison.OrdinalIgnoreCase)
                    || partNumber.StartsWith(r.PartNumber, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (prefixed.Count == 1)
        {
            notes.Add($"Line {line}: {partNumber} was taken as the library's {prefixed[0].PartNumber} — one " +
                      "is the other with its end trimmed. Check that they are the same part.");
            return prefixed[0];
        }
        if (prefixed.Count > 1)
        {
            ambiguous = true;
            notes.Add($"Line {line}: {partNumber} is the start of {prefixed.Count} library part numbers " +
                      $"({string.Join(", ", prefixed.Select(p => p.PartNumber))}), so it was not imported.");
        }
        return null;
    }

    private static bool Set<T>(T? current, T? incoming, Action<T> assign) where T : struct
    {
        if (incoming is not { } v || Equals(current, v)) return false;
        assign(v);
        return true;
    }

    private static bool Set(string? current, string? incoming, Action<string> assign)
    {
        if (incoming is null || string.Equals(current, incoming, StringComparison.Ordinal)) return false;
        assign(incoming);
        return true;
    }

    private static string Farads(double f) =>
        f >= 1e-6 ? $"{f * 1e6:0.###} µF" : f >= 1e-9 ? $"{f * 1e9:0.###} nF" : $"{f * 1e12:0.###} pF";
}
