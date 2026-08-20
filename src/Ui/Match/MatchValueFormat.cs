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
            : scaled.ToString(CultureInfo.InvariantCulture);
        return (text, unit);
    }

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
