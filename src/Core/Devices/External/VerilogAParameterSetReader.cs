using System.Globalization;
using System.Text.RegularExpressions;

namespace CircuitRF.Core.Devices.External;

/// <summary>One parameter read out of a Verilog-A parameter set.</summary>
/// <param name="Name">Exactly as written in the file, before any case alignment.</param>
/// <param name="ValueText">The literal as written — <c>1.3e7</c>, <c>1.3p</c>, <c>-4</c>, <c>"cmos"</c>
/// with its quotes removed. Carried as TEXT, never parsed to a double and re-rendered: the model
/// takes it as text, and a round trip through a double is a chance to change a value nobody asked to
/// change.</param>
public sealed record VerilogAParameterAssignment(string Name, string ValueText);

/// <summary>
/// What a parameter-set file said, and what circuitRF could do with it.
/// </summary>
/// <param name="Applied">Names the chosen model declares, respelled the way the model declares them
/// — the ones that will actually be written onto the component.</param>
/// <param name="Unknown">Names the file declared that the model does not. <b>Reported, never
/// dropped silently.</b> A parameter set written for a different version of the same family is the
/// common case and not the exotic one, and a silent drop is a wrong answer that converges: the
/// device runs, on the model's own defaults for everything that went missing, and looks fine.</param>
/// <param name="Duplicates">Names the file assigned more than once. The LAST assignment wins, which
/// is what a declaration list means, but a set that says a thing twice is worth mentioning.</param>
public sealed record VerilogAParameterSet(
    IReadOnlyList<VerilogAParameterAssignment> Applied,
    IReadOnlyList<string>                      Unknown,
    IReadOnlyList<string>                      Duplicates);

/// <summary>
/// Reads a parameter set written in Verilog-A declaration syntax —
/// <c>parameter real vxo = 1.3e7;  // comment</c> — which is the form both published physics-based
/// model families actually ship their fitted sets in.
///
/// <para><b>Why this exists rather than being added to the SPICE model-card reader.</b> That reader
/// (<c>SpiceModelCardTranslation</c>) reads a different dialect for a different purpose: a
/// <c>.model</c> card, case-insensitive, with its own continuation and keyword rules. This is
/// declaration syntax from a source language. Teaching one reader both would give it two grammars
/// and a mode flag, and the two would drift.</para>
///
/// <para><b>Without it, a fitted set is 50–200 individual picker gestures per placed device</b>,
/// which is the difference between usable and merely demonstrable.</para>
///
/// <para><b>It never materialises the parameters the model already defaults.</b> Only names the file
/// actually assigns are written, because a parameter the component does not carry is not forwarded,
/// which already means "use the model's own default" — and freezing every default at the moment of
/// placement would mean recompiling with a changed default silently did not take effect.</para>
/// </summary>
public static class VerilogAParameterSetReader
{
    /// <summary>
    /// <c>parameter</c> / <c>localparam</c>, an optional type and an optional bit range, the name,
    /// and everything up to the terminating semicolon.
    ///
    /// <para>The value is captured NON-GREEDILY up to a semicolon, and the text this runs on has had
    /// its comments blanked first — which is what makes a comment containing a semicolon harmless.
    /// A <c>from [...]</c> or <c>exclude</c> range trailing the value is stripped afterwards rather
    /// than expressed here, because those are constraints on the parameter and not part of its
    /// value.</para>
    /// </summary>
    private static readonly Regex RxDeclaration = new(
        @"\b(?:parameter|localparam)\s+                    # the keyword
          (?:(?:real|integer|string|logic|reg|int)\s+)?    # optional type
          (?:\[[^\]]*\]\s*)?                               # optional bit range
          ([A-Za-z_][A-Za-z0-9_$]*)\s*                     # the name
          =\s*
          (.*?)\s*;                                        # the value, up to the semicolon",
        RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace | RegexOptions.Singleline);

    /// <summary>A <c>from</c>/<c>exclude</c> range constraint trailing a value.</summary>
    private static readonly Regex RxRangeConstraint = new(
        @"\s*\b(?:from|exclude)\b\s*(?:\[[^\]]*\]|\([^\)]*\)|[-+0-9a-zA-Z_.]+)\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Every <c>parameter</c>/<c>localparam</c> assignment in <paramref name="text"/>, in file order.
    ///
    /// <para>Comments are blanked before matching, so <c>// set to 1.3; measured</c> after a
    /// declaration cannot truncate it and a commented-out declaration is not read as a live one.</para>
    /// </summary>
    public static IReadOnlyList<VerilogAParameterAssignment> Parse(string? text)
    {
        var found = new List<VerilogAParameterAssignment>();
        if (string.IsNullOrWhiteSpace(text)) return found;

        foreach (Match m in RxDeclaration.Matches(VerilogASourceCompiler.StripComments(text)))
        {
            string name  = m.Groups[1].Value;
            string value = CleanValue(m.Groups[2].Value);
            if (value.Length == 0) continue;
            found.Add(new VerilogAParameterAssignment(name, value));
        }

        return found;
    }

    /// <summary>Reads a parameter-set FILE. A file that cannot be read is an ordinary outcome and
    /// comes back empty rather than as an exception.</summary>
    public static IReadOnlyList<VerilogAParameterAssignment> ParseFile(string path, out string? error)
    {
        error = null;
        try { return Parse(File.ReadAllText(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            error = ex.Message;
            return [];
        }
    }

    /// <summary>
    /// Matches a parsed set against what a model actually declares.
    ///
    /// <para>Names are respelled through <see cref="OsdiModelDiscovery.AlignParameterCase"/>, which
    /// respells only on a case-insensitive match — so a set written in lower case reaches a model
    /// that declares upper case (the worker matches with <c>strcmp</c> and would otherwise refuse
    /// every one of them), while a genuine typo is left exactly as written and lands in
    /// <see cref="VerilogAParameterSet.Unknown"/> by name rather than being quietly turned into
    /// something the model does accept.</para>
    /// </summary>
    public static VerilogAParameterSet MatchToModel(
        IReadOnlyList<VerilogAParameterAssignment> parsed, IReadOnlyList<string> declared)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(declared);

        var declaredSet = declared.ToHashSet(StringComparer.Ordinal);

        var applied     = new List<VerilogAParameterAssignment>();
        var unknown     = new List<string>();
        var duplicates  = new List<string>();
        var atIndex     = new Dictionary<string, int>(StringComparer.Ordinal);
        var seenUnknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in parsed)
        {
            string aligned = OsdiModelDiscovery.AlignParameterCase(declared, p.Name);

            if (!declaredSet.Contains(aligned))
            {
                // Reported ONCE per distinct name — a set that assigns an unknown name twice has one
                // problem, not two.
                if (seenUnknown.Add(p.Name)) unknown.Add(p.Name);
                continue;
            }

            var entry = new VerilogAParameterAssignment(aligned, p.ValueText);

            // Last assignment wins, which is what a declaration list means; the earlier one is
            // replaced in place so file order is preserved.
            if (atIndex.TryGetValue(aligned, out int at))
            {
                applied[at] = entry;
                if (!duplicates.Contains(aligned, StringComparer.Ordinal)) duplicates.Add(aligned);
            }
            else
            {
                atIndex[aligned] = applied.Count;
                applied.Add(entry);
            }
        }

        return new VerilogAParameterSet(applied, unknown, duplicates);
    }

    /// <summary>
    /// One sentence describing what a load did and, above all, what it DROPPED. Empty when
    /// everything landed.
    /// </summary>
    public static string DescribeOutcome(VerilogAParameterSet set, string fileName)
    {
        ArgumentNullException.ThrowIfNull(set);

        var parts = new List<string>
        {
            set.Applied.Count == 1
                ? $"Loaded 1 parameter from '{fileName}'."
                : $"Loaded {set.Applied.Count} parameters from '{fileName}'.",
        };

        if (set.Unknown.Count > 0)
            parts.Add($"Not declared by this model, so not applied: {string.Join(", ", set.Unknown)}. "
                    + "A set written for a different version of a model usually looks like this.");

        if (set.Duplicates.Count > 0)
            parts.Add($"Assigned more than once, last value used: {string.Join(", ", set.Duplicates)}.");

        return string.Join(" ", parts);
    }

    // ── Value cleaning ────────────────────────────────────────────────────────

    /// <summary>
    /// The value as circuitRF will store it: range constraints stripped, string quotes removed,
    /// whitespace collapsed. Not evaluated and not reformatted — see
    /// <see cref="VerilogAParameterAssignment.ValueText"/>.
    /// </summary>
    private static string CleanValue(string raw)
    {
        string v = RxRangeConstraint.Replace(raw, " ").Trim();

        // A quoted string parameter: the quotes are the literal's syntax, not part of the value.
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') return v[1..^1];

        // Collapse the whitespace a line continuation or a wrapped expression leaves behind, so a
        // value that spanned two lines becomes one storable token rather than carrying a newline.
        return string.Join(' ', v.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// True when <paramref name="value"/> is a number circuitRF's own expression engine will read
    /// the same way the model does — used only to decide whether to warn, never to convert.
    ///
    /// <para>Both spellings a fitted set actually uses are covered: bare exponent form
    /// (<c>1.3e7</c>) and engineering notation (<c>1.3p</c>), which the expression engine already
    /// understands.</para>
    /// </summary>
    public static bool IsPlainNumber(string value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
}
