using System;
using System.Globalization;
using CircuitRF.Core.Matching;

namespace CircuitRF.Ui.Matching;

/// <summary>
/// Engineering formatting for the Designer's element values, and the value+unit parsing the
/// specification pane's fields do.
/// </summary>
/// <remarks>
/// <b>The unit tables are not new.</b> The display spellings come from
/// <c>ComponentTypeRegistry.UnitOptions</c> and the scale factors from
/// <c>CircuitRF.Core.Expressions.Units</c> by way of <c>UnitNormalizer.ToEngineUnit</c> — the same
/// two tables every parameter row already uses, so "µH" and "Ω" mean here exactly what they mean
/// there. Nothing in this file knows a multiplier of its own.
/// </remarks>
public static class MatchValueFormat
{
    /// <summary>The display unit that means "pick the prefix that suits the value".</summary>
    public const string AutoUnit = "Auto";

    /// <summary>The scale factor of a display unit ("pF" -> 1e-12), or 1.0 when it has none.</summary>
    public static double Scale(string? displayUnit)
    {
        string engine = CircuitRF.Core.Expressions.UnitNormalizer.ToEngineUnit(displayUnit);
        if (engine.Length == 0) return 1.0;
        return CircuitRF.Core.Expressions.Units.Scale(engine) ?? 1.0;
    }

    // The ladders the Auto unit walks. Ordered small-to-large; the chosen unit is the largest whose
    // scale still leaves |value| at or above 1, so 153.5e-12 H reads "153.517 pH" and not "0.154 nH".
    private static readonly string[] InductanceLadder  = ["fH", "pH", "nH", "µH", "mH", "H"];
    private static readonly string[] CapacitanceLadder = ["fF", "pF", "nF", "µF", "mF", "F"];
    private static readonly string[] ResistanceLadder  = ["mΩ", "Ω", "kΩ", "MΩ", "GΩ"];
    private static readonly string[] FrequencyLadder   = ["Hz", "kHz", "MHz", "GHz", "THz"];

    /// <summary>The Auto ladder for one physical dimension.</summary>
    public static string[] LadderFor(MatchQuantity quantity) => quantity switch
    {
        MatchQuantity.Inductance  => InductanceLadder,
        MatchQuantity.Capacitance => CapacitanceLadder,
        MatchQuantity.Resistance  => ResistanceLadder,
        _                         => FrequencyLadder,
    };

    /// <summary>The unit an Auto display would choose for <paramref name="value"/> (SI base units).</summary>
    public static string AutoUnitFor(double value, MatchQuantity quantity)
    {
        var ladder = LadderFor(quantity);
        double mag = Math.Abs(value);
        if (!double.IsFinite(mag) || mag == 0.0) return ladder[quantity == MatchQuantity.Frequency ? 3 : 1];

        string chosen = ladder[0];
        foreach (string u in ladder)
        {
            if (mag / Scale(u) < 1.0) break;
            chosen = u;
        }
        return chosen;
    }

    /// <summary>
    /// Formats an SI-base value in <paramref name="displayUnit"/>, or in the Auto choice when that is
    /// <see cref="AutoUnit"/>. Returns the number and the unit separately so a grid can column them.
    /// </summary>
    public static (string Text, string Unit) Format(
        double value, MatchQuantity quantity, string? displayUnit, int significantDigits)
    {
        string unit = string.IsNullOrEmpty(displayUnit) || displayUnit == AutoUnit
            ? AutoUnitFor(value, quantity)
            : displayUnit;

        double scaled = value / Scale(unit);
        int digits = Math.Clamp(significantDigits, 1, 12);
        string text = double.IsFinite(scaled)
            ? scaled.ToString("G" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
            : NonFinite(scaled);
        return (text, unit);
    }

    /// <summary>The infinity glyph — never the word.</summary>
    /// <remarks>
    /// Owner, 2026-08-19: "use a real infinity symbol instead of the words 'infinity'". Under
    /// <see cref="CultureInfo.InvariantCulture"/> .NET renders a non-finite double as the literal
    /// text <c>Infinity</c>/<c>-Infinity</c>, which is what the ladder and the value grid were
    /// showing for an element a transform parked at its threshold. NaN keeps its own spelling: it
    /// is not an infinity and saying so would be a lie about which failure happened.
    /// </remarks>
    public const string InfinityGlyph = "∞";

    private static string NonFinite(double v) =>
        double.IsPositiveInfinity(v) ? InfinityGlyph
        : double.IsNegativeInfinity(v) ? "-" + InfinityGlyph
        : "NaN";

    /// <summary>The one-string form, "153.5169 pH".</summary>
    public static string FormatWithUnit(
        double value, MatchQuantity quantity, string? displayUnit, int significantDigits)
    {
        var (text, unit) = Format(value, quantity, displayUnit, significantDigits);
        return $"{text} {unit}";
    }

    /// <summary>
    /// Parses a typed number against a display unit. <b>Returns false rather than throwing</b> — a
    /// half-typed field is an ordinary state of a live editor, not an error to report.
    /// </summary>
    public static bool TryParse(string? text, string? displayUnit, out double value)
    {
        value = 0.0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double raw))
            return false;
        value = raw * Scale(displayUnit);
        return double.IsFinite(value);
    }

    /// <summary>
    /// Parses a typed <c>"value unit"</c> string — the form an inline editor seeds and hands back.
    /// </summary>
    /// <remarks>
    /// <b>A trailing unit token is honoured, not ignored.</b> The inline editor pre-selects only the
    /// number and leaves the unit standing (<c>InlineEdit.ValueSelectionLength</c>), so the usual
    /// gesture types over "1" in "1 pF" and commits "2.2 pF"; but a user who deliberately types
    /// "2.2 nF" means nanofarads, and silently reading that as picofarads is off by a thousand with
    /// nothing said. An unrecognised trailing token is a parse FAILURE rather than a token to
    /// discard — "2.2 nH" in a capacitance field is a mistake worth refusing.
    /// </remarks>
    /// <param name="text">What the user typed.</param>
    /// <param name="quantity">Which unit ladder a trailing token is matched against.</param>
    /// <param name="fallbackUnit">The unit to assume when no token was typed.</param>
    /// <param name="value">The value in SI base units.</param>
    /// <param name="unit">The unit that was actually used.</param>
    public static bool TryParseWithUnit(
        string? text, MatchQuantity quantity, string? fallbackUnit,
        out double value, out string unit)
    {
        value = 0.0;
        unit = string.IsNullOrEmpty(fallbackUnit) || fallbackUnit == AutoUnit
            ? AutoUnitFor(0.0, quantity)
            : fallbackUnit!;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string trimmed = text.Trim();
        int split = InlineEditSplit(trimmed);
        if (split > 0)
        {
            string token = trimmed[split..].Trim();
            string? matched = MatchUnit(token, quantity);
            if (matched is null) return false;
            unit = matched;
            trimmed = trimmed[..split].Trim();
        }

        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double raw))
            return false;
        value = raw * Scale(unit);
        return double.IsFinite(value);
    }

    /// <summary>Index of the unit token in a "value unit" string, or -1 when there is none.</summary>
    private static int InlineEditSplit(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsDigit(c) || c == '.' || c == '+' || c == '-') continue;
            if ((c == 'e' || c == 'E') && i + 1 < text.Length
                && (char.IsDigit(text[i + 1]) || text[i + 1] == '+' || text[i + 1] == '-'))
                continue;
            return i;
        }
        return -1;
    }

    /// <summary>The ladder spelling one typed unit token means, or null when it means none of them.</summary>
    /// <remarks>
    /// <b>Case is only ignored when ignoring it is unambiguous.</b> On the resistance ladder "mΩ" and
    /// "MΩ" are milliohms and megaohms — a factor of 1e9 apart — so a case-insensitive match there
    /// must find exactly one candidate or find none at all. Everywhere else (a typed "ghz", "nh")
    /// there is only ever one candidate and the loose match is a convenience with no cost. "u" for
    /// "µ" and "ohm" for "Ω" are spelled out first, because they are what a keyboard can produce.
    /// </remarks>
    private static string? MatchUnit(string token, MatchQuantity quantity)
    {
        if (token.Length == 0) return null;
        string normalized = token.Replace('u', 'µ').Replace("ohm", "Ω", StringComparison.OrdinalIgnoreCase);

        var ladder = LadderFor(quantity);
        foreach (string u in ladder)
            if (string.Equals(u, token, StringComparison.Ordinal)
                || string.Equals(u, normalized, StringComparison.Ordinal))
                return u;

        string? loose = null;
        foreach (string u in ladder)
        {
            if (!string.Equals(u, token, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(u, normalized, StringComparison.OrdinalIgnoreCase))
                continue;
            if (loose is not null) return null;   // ambiguous — mΩ vs MΩ
            loose = u;
        }
        return loose;
    }

    /// <summary>The natural quantity of a ladder element: an inductor is henries, a capacitor farads.</summary>
    public static MatchQuantity QuantityOf(ElementType type) =>
        type == ElementType.L ? MatchQuantity.Inductance : MatchQuantity.Capacitance;
}

/// <summary>The four physical dimensions the Designer displays. Mirrors <c>UnitDimension</c>'s members
/// for the subset a matching network uses; kept separate so nothing here depends on the schematic
/// editor's own row model.</summary>
public enum MatchQuantity
{
    /// <summary>Henries.</summary>
    Inductance,

    /// <summary>Farads.</summary>
    Capacitance,

    /// <summary>Ohms.</summary>
    Resistance,

    /// <summary>Hertz.</summary>
    Frequency,
}
