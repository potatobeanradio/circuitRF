// Reads a capacitance-versus-voltage table — a supplier's characteristic-data export, or two columns
// pasted from a spreadsheet or a datasheet (owner request, 2026-09-23). Two editors take one: railRF's
// bias-curve editor (PartBiasCurveImport, which adds the checks a library row makes possible) and the
// schematic's NonlinearC C-V editor.
//
// The export this was written against is a .csv with a '#' preamble naming the part and the
// measurement conditions, then a header "DC Bias[V],Capacitance[F]," and ~200 rows in base SI with a
// trailing comma on every line. DelimitedTables already strips the preamble and the trailing
// separator; the rest is here.
//
// ── A CAPACITANCE WITH NO UNIT IS NOT READ ───────────────────────────────────────────────────────
//
// On PartLibraryTableImport's terms: the unit comes from the HEADER ("Capacitance[F]", "C (uF)") or
// from the CELLS ("470nF", "470n"). A bare "0.47" is 0.47 F, 0.47 µF or 470 nF with equal
// plausibility, and a curve read at the wrong scale is wrong by a factor of a thousand. Volts are the
// exception, as they are everywhere else: a bare voltage means volts.
//
// The voltage keeps its SIGN. A varactor's table runs through reverse bias, and what a negative
// voltage means for a part is the caller's to decide.

using System.Globalization;
using System.Text.RegularExpressions;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Matching;

namespace CircuitRF.Design.Interchange;

/// <summary>One (voltage, capacitance) sample, in volts and farads.</summary>
public readonly record struct CvSample(double Volts, double Farads);

/// <summary>What a table read. <see cref="Samples"/> is empty where nothing may be used, and
/// <see cref="Notes"/> then leads with why.</summary>
/// <param name="Samples">Sorted by voltage, one per voltage.</param>
/// <param name="Preamble">The comment lines above the table — where a supplier export names its part.</param>
public sealed record CapacitanceVoltageTableResult(
    IReadOnlyList<CvSample> Samples,
    IReadOnlyList<string> Preamble,
    IReadOnlyList<string> Notes);

public static class CapacitanceVoltageTable
{
    private static readonly string[] VoltageNames =
        ["dc bias", "bias", "dc bias voltage", "bias voltage", "voltage", "v", "vdc", "v dc", "dc voltage",
         "applied voltage", "vbias", "v bias", "dc", "volts", "applied dc voltage", "vr", "reverse voltage"];
    private static readonly string[] CapacitanceNames =
        ["capacitance", "c", "cap", "capacitance value", "effective capacitance", "cj"];

    private static readonly Regex HeaderUnit = new(@"[\(\[]\s*([^\)\]]+?)\s*[\)\]]\s*$", RegexOptions.CultureInvariant);

    /// <param name="sourceName">The file name, or null for pasted text — only what the notes call it.</param>
    public static CapacitanceVoltageTableResult Read(string text, string? sourceName)
    {
        var notes = new List<string>();
        string what = sourceName ?? "The pasted text";

        var table = DelimitedTables.Parse(text ?? "");
        var rows = table.Rows.Select(r => r.Fields).ToList();

        // Text copied out of a datasheet separates its columns with spaces, which no delimiter
        // candidate is. Only then is whitespace a separator — and a unit standing alone ("470 nF")
        // is put back on the number it follows.
        if (rows.Count > 0 && rows.All(f => f.Count(c => c.Trim().Length > 0) <= 1))
            rows = [.. rows.Select(f => SplitOnWhitespace(string.Join(" ", f)))];

        CapacitanceVoltageTableResult Refuse(string why)
        {
            notes.Insert(0, why);
            return new([], table.Preamble, notes);
        }

        // ── the header ──────────────────────────────────────────────────────────────────────
        int headerIndex = -1, vColumn = 0, cColumn = 1;
        for (int i = 0; i < rows.Count && i < 10 && headerIndex < 0; i++)
        {
            int v = IndexOf(rows[i], VoltageNames), c = IndexOf(rows[i], CapacitanceNames);
            if (v >= 0 && c >= 0) { headerIndex = i; vColumn = v; cColumn = c; }
        }

        // A first row that is not numbers is a header of names this does not know, and its first two
        // columns are read as voltage and capacitance — said, because it is a reading.
        if (headerIndex < 0 && rows.Count > 0 && !IsNumber(Cell(rows[0], 0)))
        {
            headerIndex = 0;
            notes.Add($"{what} does not name a voltage and a capacitance column, so its first two " +
                      $"columns (\"{Cell(rows[0], 0)}\", \"{Cell(rows[0], 1)}\") were read as voltage " +
                      "and capacitance.");
        }

        string? vUnit = null, cUnit = null;
        if (headerIndex >= 0)
        {
            var header = rows[headerIndex];
            vUnit = UnitOf(Cell(header, vColumn));
            cUnit = UnitOf(Cell(header, cColumn));
            if (vUnit is not null && VoltScale(vUnit) is null)
                return Refuse($"The voltage column \"{Cell(header, vColumn)}\" states a unit that is not " +
                              "a voltage, so nothing was imported.");
            if (cUnit is not null && CapacitanceUnit(cUnit) is null)
                return Refuse($"The capacitance column \"{Cell(header, cColumn)}\" states a unit that is " +
                              "not a capacitance, so nothing was imported.");
        }

        // ── the rows ────────────────────────────────────────────────────────────────────────
        var samples = new List<CvSample>();
        var seen = new HashSet<double>();
        int bare = 0, unreadable = 0, duplicates = 0;
        for (int r = headerIndex + 1; r < rows.Count; r++)
        {
            string vCell = Cell(rows[r], vColumn), cCell = Cell(rows[r], cColumn);
            if (vCell.Length == 0 && cCell.Length == 0) continue;

            double? c = ReadFarads(cCell, cUnit, out bool wasBare);
            if (wasBare) { bare++; continue; }
            if (ReadVolts(vCell, vUnit) is not { } volts || c is not { } farads || !(farads > 0))
            {
                unreadable++;
                continue;
            }
            if (!seen.Add(volts)) { duplicates++; continue; }
            samples.Add(new CvSample(volts, farads));
        }

        if (bare > 0 && samples.Count == 0)
            return Refuse($"{what} states no capacitance unit — neither in a header nor in the cells — " +
                          "so nothing was imported: a bare number's scale would be a guess. Include the " +
                          "header row (e.g. \"Bias (V)\", \"C (µF)\") or write the unit on each value " +
                          "(\"4.7uF\").");
        if (bare > 0)
            notes.Add($"{bare} row(s) gave a capacitance with no unit and were left out.");
        if (unreadable > 0)
            notes.Add($"{unreadable} row(s) did not read as a voltage and a capacitance and were left " +
                      "out. A decimal comma is one cause: write 4.7, not 4,7.");
        if (duplicates > 0)
            notes.Add($"{duplicates} row(s) repeated a voltage already read; the first of each was used.");
        if (samples.Count == 0)
            return Refuse($"{what} held no voltage and capacitance pair, so nothing was imported.");

        samples.Sort((a, b) => a.Volts.CompareTo(b.Volts));
        return new(samples, table.Preamble, notes);
    }

    // ── cells ─────────────────────────────────────────────────────────────────────────────────

    private static string Cell(IReadOnlyList<string> fields, int index) =>
        index >= 0 && index < fields.Count ? fields[index].Trim() : "";

    private static int IndexOf(IReadOnlyList<string> fields, string[] names)
    {
        for (int i = 0; i < fields.Count; i++)
        {
            // NormalizeHeader strips a "(unit)" but not a "[unit]", and a supplier export writes the
            // second.
            string name = HeaderUnit.Replace(fields[i], "");
            if (names.Contains(DelimitedTables.NormalizeHeader(name), StringComparer.Ordinal)) return i;
        }
        return -1;
    }

    private static string? UnitOf(string headerCell)
    {
        var m = HeaderUnit.Match(headerCell);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static bool IsNumber(string cell) =>
        double.TryParse(MatchValueFormat.SplitTypedValue(cell).Number, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out _);

    private static double? VoltScale(string unit) => unit.Trim() switch
    {
        "V" or "v" or "Vdc" or "VDC" or "volt" or "volts" => 1.0,
        "mV" or "mv" => 1e-3,
        _ => null,
    };

    /// <summary>A capacitance unit on the editor's ladder — or a bare SI prefix ("470n", "4.7u"),
    /// which is how a spreadsheet commonly writes one.</summary>
    private static string? CapacitanceUnit(string token) =>
        MatchValueFormat.TryMatchUnit(token, MatchQuantity.Capacitance)
        ?? (token.Length == 1 ? MatchValueFormat.TryMatchUnit(token + "F", MatchQuantity.Capacitance) : null);

    private static double? ReadVolts(string cell, string? headerUnit)
    {
        var (number, token) = MatchValueFormat.SplitTypedValue(cell);
        if (VoltScale(token.Length > 0 ? token : headerUnit ?? "V") is not { } scale
            || !double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            return null;
        v *= scale;
        return double.IsFinite(v) ? v : null;
    }

    private static double? ReadFarads(string cell, string? headerUnit, out bool bare)
    {
        bare = false;
        var (number, token) = MatchValueFormat.SplitTypedValue(cell);
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double raw)) return null;
        string? unit = token.Length > 0 ? CapacitanceUnit(token)
                     : headerUnit is null ? null : CapacitanceUnit(headerUnit);
        if (unit is null)
        {
            bare = token.Length == 0;
            return null;
        }
        double f = raw * MatchValueFormat.Scale(unit);
        return double.IsFinite(f) ? f : null;
    }

    private static List<string> SplitOnWhitespace(string line)
    {
        var parts = new List<string>();
        foreach (string token in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            bool unitOnly = parts.Count > 0 && !IsNumber(token) && IsNumber(parts[^1])
                            && (VoltScale(token) is not null || CapacitanceUnit(token) is not null);
            if (unitOnly) parts[^1] += token;
            else parts.Add(token);
        }
        return parts;
    }
}
