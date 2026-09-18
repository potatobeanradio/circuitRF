using System;
using System.Globalization;

namespace CircuitRF.Ui.RailRf;

/// <summary>The physical dimensions a railRF row displays.</summary>
/// <remarks>
/// <b>Deliberately not <c>MatchQuantity</c>.</b> That enum is the Match Designer's own four (L, C, R,
/// f) and railRF's rows are volts, amps, ohms, henries and hertz — two of which it does not carry. The
/// UNIT TABLES are still not duplicated: every scale factor here comes out of
/// <c>CircuitRF.Core.Expressions.Units</c> through <see cref="RailValueFormat.Scale"/>, exactly as
/// <c>MatchValueFormat</c> takes its own, so "mV" means here what it means in a parameter row.
/// </remarks>
public enum RailQuantity
{
    /// <summary>Volts.</summary>
    Voltage,

    /// <summary>Amps.</summary>
    Current,

    /// <summary>Ohms.</summary>
    Resistance,

    /// <summary>Henries.</summary>
    Inductance,

    /// <summary>Hertz.</summary>
    Frequency,
}

/// <summary>
/// Engineering formatting and value+unit parsing for the railRF window's settable rows.
/// </summary>
/// <remarks>
/// <b>Written against <c>MatchValueFormat</c>, member for member</b>, because §11.1's instruction is
/// that this window takes the Match Designer's conventions rather than resembling them: an
/// <c>InlineEditText</c> here seeds the same way, honours a typed trailing unit the same way, and
/// refuses an unrecognised one the same way. The one thing that is genuinely different is the ladder
/// set, which is why this file exists at all.
///
/// <para><b>An unrecognised trailing token is a parse FAILURE, not a token to discard</b> — the same
/// rule and the same reason: "120 mV" typed into a current field is a mistake worth refusing, and
/// silently reading it as 120 mA is off by nothing a reader could ever see.</para>
/// </remarks>
public static class RailValueFormat
{
    // Ordered small-to-large. The chosen unit is the largest whose scale still leaves |value| at or
    // above 1, so 0.12 A reads "120 mA" and not "0.12 A".
    private static readonly string[] VoltageLadder    = ["nV", "µV", "mV", "V", "kV"];
    private static readonly string[] CurrentLadder    = ["nA", "µA", "mA", "A"];
    private static readonly string[] ResistanceLadder = ["mΩ", "Ω", "kΩ", "MΩ"];
    private static readonly string[] InductanceLadder = ["fH", "pH", "nH", "µH", "mH", "H"];
    private static readonly string[] FrequencyLadder  = ["Hz", "kHz", "MHz", "GHz"];

    /// <summary>The ladder for one dimension.</summary>
    public static string[] LadderFor(RailQuantity quantity) => quantity switch
    {
        RailQuantity.Voltage     => VoltageLadder,
        RailQuantity.Current     => CurrentLadder,
        RailQuantity.Resistance  => ResistanceLadder,
        RailQuantity.Inductance  => InductanceLadder,
        _                        => FrequencyLadder,
    };

    /// <summary>
    /// The scale factor of a display unit ("mΩ" -> 1e-3), or 1.0 when it has none.
    /// </summary>
    /// <remarks>
    /// Routed through <c>UnitNormalizer.ToEngineUnit</c> and <c>Units.Scale</c>, which is where every
    /// other surface in circuitRF gets its multipliers. Nothing in this file knows one of its own —
    /// the whole reason a millivolt is a thousandth of a volt here is that it is one there.
    /// </remarks>
    public static double Scale(string? displayUnit)
    {
        string engine = CircuitRF.Core.Expressions.UnitNormalizer.ToEngineUnit(displayUnit);
        if (engine.Length == 0) return 1.0;
        return CircuitRF.Core.Expressions.Units.Scale(engine) ?? 1.0;
    }

    /// <summary>The unit an automatic display would choose for <paramref name="value"/> (base SI).</summary>
    public static string AutoUnitFor(double value, RailQuantity quantity)
    {
        var ladder = LadderFor(quantity);
        double mag = Math.Abs(value);
        // A zero or a non-finite number has no magnitude to choose from, so it takes the ladder's own
        // natural rung — the base unit for everything but frequency, which reads in MHz on a PDN band.
        if (!double.IsFinite(mag) || mag == 0.0)
            return quantity == RailQuantity.Frequency ? "MHz" : BaseUnitOf(quantity);

        string chosen = ladder[0];
        foreach (string u in ladder)
        {
            if (mag / Scale(u) < 1.0) break;
            chosen = u;
        }
        return chosen;
    }

    /// <summary>The scale-1 spelling of a dimension.</summary>
    public static string BaseUnitOf(RailQuantity quantity) => quantity switch
    {
        RailQuantity.Voltage     => "V",
        RailQuantity.Current     => "A",
        RailQuantity.Resistance  => "Ω",
        RailQuantity.Inductance  => "H",
        _                        => "Hz",
    };

    /// <summary>The one-string form an <c>InlineEditText</c> shows and seeds with — "120 mA".</summary>
    public static string FormatWithUnit(double value, RailQuantity quantity, int significantDigits = 4)
    {
        string unit = AutoUnitFor(value, quantity);
        return $"{Significant(value / Scale(unit), significantDigits)} {unit}";
    }

    /// <summary>The one-string form, or <paramref name="whenNull"/> where there is no value.</summary>
    public static string FormatWithUnit(
        double? value, RailQuantity quantity, string whenNull, int significantDigits = 4) =>
        value is { } v ? FormatWithUnit(v, quantity, significantDigits) : whenNull;

    /// <summary><paramref name="value"/> to <paramref name="digits"/> significant figures, with the
    /// trailing zeros of a round number trimmed — "2.5", not "2.500".</summary>
    public static string Significant(double value, int digits)
    {
        if (!double.IsFinite(value)) return double.IsNaN(value) ? "NaN" : value > 0 ? "∞" : "-∞";
        if (value == 0.0) return "0";

        int exponent = (int)Math.Floor(Math.Log10(Math.Abs(value)));
        int decimals = Math.Clamp(digits - 1 - exponent, 0, 15);
        string text = Math.Round(value, decimals).ToString("0.###############", CultureInfo.InvariantCulture);
        return text;
    }

    /// <summary>
    /// Parses a typed <c>"value unit"</c> string. <b>Returns false rather than throwing</b> — a
    /// half-typed field is an ordinary state of a live editor, not an error to report, and this
    /// window re-solves on every committed edit.
    /// </summary>
    /// <param name="text">What the user typed.</param>
    /// <param name="quantity">Which ladder a trailing token is matched against.</param>
    /// <param name="value">The value, in BASE SI.</param>
    public static bool TryParse(string? text, RailQuantity quantity, out double value)
    {
        value = 0.0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string trimmed = text.Trim();
        string unit = AutoUnitFor(0.0, quantity);

        int split = UnitSplit(trimmed);
        if (split == 0) return false;              // a unit with no number in front of it
        if (split > 0)
        {
            string? matched = MatchUnit(trimmed[split..].Trim(), quantity);
            if (matched is null) return false;     // "120 mV" in a current field — refused, not guessed
            unit = matched;
            trimmed = trimmed[..split].Trim();
        }

        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double raw))
            return false;
        value = raw * Scale(unit);
        return double.IsFinite(value);
    }

    /// <summary>Index of the unit token in a "value unit" string, or -1 when there is none.</summary>
    private static int UnitSplit(string text)
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

    /// <summary>The ladder spelling one typed token means, or null when it means none of them.</summary>
    /// <remarks>
    /// <b>Case is only ignored where ignoring it is unambiguous</b> — <c>MatchValueFormat</c>'s rule,
    /// and railRF has the same trap on the resistance ladder: "mΩ" and "MΩ" are a factor of 1e9 apart,
    /// so a case-insensitive match there must find exactly one candidate or none at all. "u" for "µ"
    /// and "ohm" for "Ω" are spelled out first, because they are what a keyboard can produce.
    /// </remarks>
    private static string? MatchUnit(string token, RailQuantity quantity)
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
}
