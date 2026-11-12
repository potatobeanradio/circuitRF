// Frequency-scalable stage parameters. A parameter may be written three ways:
//
//   a number         "8.5", "8.5 dB", "2.35 GHz"     as always
//   an expression    "0.05*f_GHz + 0.6"              in the frequency at the stage's input, freq in Hz and f_GHz in GHz,
//                                                    with the expression language's functions and unit literals
//                                                    (log10, ceil, min, max, a < b ? x : y; a unit glued to its number, "freq - 12GHz")
//   a table          "lna_nf.csv"                    rows of frequency and value, linear between rows and NEVER
//                                                    extrapolated: a frequency outside the rows is a refusal
//
// Before a chain is budgeted it is RESOLVED (ChainDesign.Resolve): stage by stage from the input, each such text is
// replaced by its number at that stage's own input frequency — the chain's, moved to |f − Flo| by every mixer passed,
// the LO itself resolved first — so the budget, the levels and the benches all read plain numbers, and a bench carries
// exactly the number its budget used. The netlist could not take the expression itself: an ideal block does not bind
// freq. A value is in its parameter's own unit (dB, dBm, Hz); the number is written round-trip, so nothing is rounded.

using System.Globalization;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Design.Chain;

public static class ChainScaling
{
    /// <summary>The names an expression reads the frequency by: Hz, as everywhere in circuitRF, and GHz.</summary>
    public const string FreqVar = "freq", FreqGhzVar = "f_GHz";

    /// <summary>True when the text is a value of the frequency: it names a .csv table, or it is an expression that reads
    /// no name but <c>freq</c> and <c>f_GHz</c> and is not a plain number. Anything else is left to the parameter's own
    /// refusal.</summary>
    public static bool IsScaled(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || ChainValue.TryParse(text, out _)) return false;
        if (ChainTable.File(text) is not null) return true;
        try { return AstWalker.CollectRefs(Parser.Parse(text.Trim())).All(n => n is FreqVar or FreqGhzVar); }
        catch (ExpressionException) { return false; }
    }

    /// <summary>The value of a number, an expression or a table at <paramref name="fHz"/>, or null and why not (a phrase
    /// to follow "NF = '…'"). Never throws.</summary>
    public static double? At(string text, double fHz, string? baseDirectory, out string why)
    {
        why = "";
        if (ChainValue.TryParse(text, out double v)) return v;
        if (ChainTable.File(text) is { } file) return ChainTable.At(file, fHz, baseDirectory, out why);
        try
        {
            var scope = new Scope("chain");
            scope.Bind(FreqVar, fHz.ToString("R", CultureInfo.InvariantCulture));
            scope.Bind(FreqGhzVar, (fHz / 1e9).ToString("R", CultureInfo.InvariantCulture));
            var value = new Evaluator().Eval(text.Trim(), scope);
            if (value.Kind != ValueKind.Real) { why = $"is a {value.Kind.ToString().ToLowerInvariant()} value at {ChainSnp.Hz(fHz)}, not a real number"; return null; }
            double x = value.AsReal();
            if (double.IsFinite(x)) return x;
            why = $"is {x.ToString(CultureInfo.InvariantCulture)} at {ChainSnp.Hz(fHz)}";
        }
        catch (Exception ex) when (ex is ExpressionException or InvalidCastException or ArithmeticException)
        {
            why = $"cannot be evaluated at {ChainSnp.Hz(fHz)}: {ex.Message.TrimEnd('.')}";
        }
        return null;
    }

    /// <summary>
    /// The design as the budget reads it: a copy in which every stage parameter written as an expression or a table is
    /// its number at that stage's input frequency. A value that cannot be read adds its reason to
    /// <paramref name="refusals"/>, naming the stage. A parameter that names a choice or a file is not read; a combiner's
    /// per-level list is read when it is one expression or one table.
    /// </summary>
    public static ChainDesign Resolve(ChainDesign d, List<string> refusals)
    {
        var r = d.Clone();
        if (!(d.FrequencyHz > 0)) return r;   // the frequency's own refusal says why
        double f = d.FrequencyHz;
        foreach (var s in r.Stages)
        {
            if (ChainParts.Find(s.Part) is not { } part) continue;
            foreach (var p in part.Parameters)
            {
                if (p.IsFile || p.Options is { Count: > 0 }) continue;
                string text = s.Get(p.Name);
                if (!IsScaled(text)) continue;
                if (At(text, f, d.BaseDirectory, out string why) is { } v) s.Parameters[p.Name] = v.ToString("R", CultureInfo.InvariantCulture);
                else refusals.Add($"Stage '{s.Name}': {p.Name} = '{text.Trim()}' {why}.");
            }
            if (s.Part == ChainParts.Mixer) f = Math.Abs(f - ChainValue.Or(s.Get("Flo"), 0));
        }
        return r;
    }
}

/// <summary>A value against frequency from a CSV file: <c>freq,value</c> rows, the frequency in Hz or with its unit
/// (<c>140 GHz</c>), a first line that is not one the header; linear between rows, never extrapolated.</summary>
public static class ChainTable
{
    /// <summary>Within this relative distance of a table's end, a frequency is at that end (SnP's own tolerance).</summary>
    private const double EdgeTol = 1e-9;

    /// <summary>A table reference as written, unquoted and trimmed, or null when the text does not name a .csv.</summary>
    public static string? File(string text)
    {
        string t = text.Trim();
        if (t.Length >= 2 && t[0] == '"' && t[^1] == '"') t = t[1..^1].Trim();
        return t.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? t : null;
    }

    // ponytail: the file is read on every evaluation (a sweep reads it once a point); cache by path and write time if
    // a long sweep over many tables ever shows in a profile.
    /// <summary>The table's value at <paramref name="fHz"/>, or null and why not.</summary>
    public static double? At(string reference, double fHz, string? baseDirectory, out string why)
    {
        why = "";
        if (ChainSnp.Resolve(reference, baseDirectory) is not { } full)
        {
            why = "names a table by a relative path, and the chain is not saved anywhere it could be read from. Save the " +
                  "chain, or give the table's full path";
            return null;
        }
        string[] lines;
        try { lines = System.IO.File.ReadAllLines(full); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { why = $"names a table that cannot be read: {ex.Message.TrimEnd('.')}"; return null; }
        var rows = new List<(double F, double V)>();
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var cells = line.Split([',', ';', '\t'], StringSplitOptions.TrimEntries);
            if (cells.Length >= 2 && ChainValue.TryParse(cells[0], out double f) && ChainValue.TryParse(cells[1], out double x))
                rows.Add((f, x));
            else if (rows.Count > 0 || i > Array.FindIndex(lines, l => l.Trim().Length > 0 && l.Trim()[0] != '#'))
            {
                why = $"names a table whose line {i + 1} is not a frequency and a value: '{line}'";
                return null;
            }
        }
        rows.Sort((a, b) => a.F.CompareTo(b.F));
        if (rows.Count < 2) { why = $"names a table of {rows.Count} row{(rows.Count == 1 ? "" : "s")}, and a table needs two at least"; return null; }
        for (int i = 1; i < rows.Count; i++)
            if (rows[i].F == rows[i - 1].F) { why = $"names a table that lists {ChainSnp.Hz(rows[i].F)} twice"; return null; }
        double lo = rows[0].F, hi = rows[^1].F, tol = EdgeTol * hi;
        if (fHz < lo - tol || fHz > hi + tol)
        {
            why = $"covers {ChainSnp.Hz(lo)} to {ChainSnp.Hz(hi)} and is read at {ChainSnp.Hz(fHz)}: a table is never extrapolated";
            return null;
        }
        if (fHz <= lo) return rows[0].V;
        if (fHz >= hi) return rows[^1].V;
        int k = 1;
        while (rows[k].F < fHz) k++;
        double t = (fHz - rows[k - 1].F) / (rows[k].F - rows[k - 1].F);
        return rows[k - 1].V + t * (rows[k].V - rows[k - 1].V);
    }
}
