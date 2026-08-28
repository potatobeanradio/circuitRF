// Exact integer unit arithmetic for the layout database unit (DBU).
// Framework-free: no Avalonia, no SkiaSharp.
// See docs/design/layout-view.md §1.

using System.Globalization;
using System.Text.RegularExpressions;

namespace CircuitRF.Design.Layout;

public enum LayoutUnit { Nm, Um, Mm, Mil, Inch }

/// <summary>
/// Converts between physical units and integer database units (DBU), exactly.
/// All conversions are computed in <see cref="decimal"/>, never <see cref="double"/> —
/// doubles cannot represent 1 mil = 25400 nm exactly and the exactness guarantee
/// (docs/design/layout-view.md §1.1 R1) would evaporate.
/// </summary>
public static class LayoutUnits
{
    /// <summary>1 DBU = 1 nm at this resolution (1000 DBU per micron).</summary>
    public const int DefaultDbuPerMicron = 1000;

    private static readonly Regex ParsePattern = new(
        @"^\s*([+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?)\s*([a-zA-Zµμ]*)\s*$",
        RegexOptions.Compiled);

    /// <summary>Exact size of one unit, in nanometres.</summary>
    private static long NmPerUnit(LayoutUnit unit) => unit switch
    {
        LayoutUnit.Nm   => 1,
        LayoutUnit.Um   => 1_000,
        LayoutUnit.Mm   => 1_000_000,
        LayoutUnit.Mil  => 25_400,
        LayoutUnit.Inch => 25_400_000,
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, null),
    };

    /// <summary>Converts a value in <paramref name="unit"/> to DBU, rounding away from zero.</summary>
    public static long ToDbu(decimal value, LayoutUnit unit, int dbuPerMicron)
    {
        decimal raw = value * NmPerUnit(unit) * dbuPerMicron / 1000m;
        return (long)Math.Round(raw, MidpointRounding.AwayFromZero);
    }

    /// <summary>Converts a DBU value back to <paramref name="unit"/>, exactly (no rounding).</summary>
    public static decimal FromDbu(long dbu, LayoutUnit unit, int dbuPerMicron)
    {
        return dbu * 1000m / (NmPerUnit(unit) * dbuPerMicron);
    }

    /// <summary>
    /// Parses a bare number (interpreted in <paramref name="fallbackUnit"/>) or a number with a
    /// unit suffix (nm, u/um/µm, mm, mil, in/inch). Case-insensitive, whitespace-tolerant,
    /// leading +/- accepted, InvariantCulture.
    /// </summary>
    public static bool TryParse(string text, LayoutUnit fallbackUnit, int dbuPerMicron, out long dbu)
    {
        dbu = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var m = ParsePattern.Match(text);
        if (!m.Success)
            return false;

        if (!decimal.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return false;

        var suffix = m.Groups[2].Value.ToLowerInvariant();
        LayoutUnit unit;
        switch (suffix)
        {
            case "":                                     unit = fallbackUnit;  break;
            case "nm":                                    unit = LayoutUnit.Nm;   break;
            case "u": case "um": case "µm": case "μm":     unit = LayoutUnit.Um;   break;
            case "mm":                                     unit = LayoutUnit.Mm;   break;
            case "mil":                                    unit = LayoutUnit.Mil;  break;
            case "in": case "inch":                        unit = LayoutUnit.Inch; break;
            default: return false;
        }

        dbu = ToDbu(value, unit, dbuPerMicron);
        return true;
    }

    /// <summary>Formats a DBU value in <paramref name="unit"/>, trailing zeros trimmed, InvariantCulture.</summary>
    public static string Format(long dbu, LayoutUnit unit, int dbuPerMicron, int maxDecimals = 4)
    {
        var value = FromDbu(dbu, unit, dbuPerMicron);
        var fmt = maxDecimals > 0 ? "0." + new string('#', maxDecimals) : "0";
        return value.ToString(fmt, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// How a formatted length is spelled — the .NET standard numeric specifiers, named.
    ///
    /// <para><b><see cref="General"/> is what this file has always done</b> and stays the default: up
    /// to <c>maxDecimals</c> places with trailing zeros TRIMMED, so 40 reads "40" and not "40.0000".
    /// <see cref="Fixed"/> is the same count of places with the zeros KEPT, which is what a column of
    /// numbers meant to line up wants and the one thing General cannot do. The two exponential forms
    /// differ only in the case of the letter, exactly as .NET's own <c>E</c>/<c>e</c> do.</para>
    /// </summary>
    public enum LayoutNumberFormat { General, Fixed, Exponential, ExponentialLower }

    /// <summary>
    /// Formats a DBU value in <paramref name="unit"/> with an explicit spelling.
    ///
    /// <para><b><paramref name="decimals"/> means DECIMAL PLACES in <see cref="LayoutNumberFormat.General"/>
    /// and <see cref="LayoutNumberFormat.Fixed"/>, and MANTISSA digits in the exponential forms</b> —
    /// which is what "the number of decimals" means in each. It is deliberately NOT passed through as
    /// .NET's <c>G</c> precision, which counts SIGNIFICANT digits: a caller asking for one decimal
    /// place would get <c>G1</c>, i.e. one significant digit, and 40 would render as "4E+01".</para>
    /// </summary>
    public static string Format(long dbu, LayoutUnit unit, int dbuPerMicron, int decimals,
                                LayoutNumberFormat format)
    {
        if (format == LayoutNumberFormat.General) return Format(dbu, unit, dbuPerMicron, decimals);

        decimals = Math.Clamp(decimals, 0, 15);
        var value = FromDbu(dbu, unit, dbuPerMicron);
        string spec = format switch
        {
            LayoutNumberFormat.Fixed            => "F",
            LayoutNumberFormat.Exponential      => "E",
            LayoutNumberFormat.ExponentialLower => "e",
            _                                   => "F",
        };
        return value.ToString(spec + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    /// <summary>Short display suffix for a unit (e.g. for a "units reminder" label next to a
    /// dimension field) — the single source other call sites should share rather than
    /// re-deriving their own copy of this switch.</summary>
    public static string Suffix(LayoutUnit unit) => unit switch
    {
        LayoutUnit.Nm   => "nm",
        LayoutUnit.Um   => "µm",
        LayoutUnit.Mm   => "mm",
        LayoutUnit.Mil  => "mil",
        LayoutUnit.Inch => "in",
        _ => "",
    };
}
