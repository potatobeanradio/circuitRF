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

    // ── the integer nm ⇄ DBU pair ────────────────────────────────────────────
    //
    // Moved down from `WBondSnap` (src/Ui/WBond) when the DRC engine crossed the UI firewall
    // (brief-automation-4-check-and-explain.md R-aut4-3): `WBondClearance` is the one crossing that
    // converts a LAYOUT into nanometres so a 3D wire point can be measured against it, and the
    // engine could not come down here while the function it converts with stayed up there.
    // `WBondSnap.ToDbu`/`ToNm` now forward to these, so there is still exactly ONE implementation —
    // which is the property that file's own header depends on, and the one a second copy would end.
    //
    // **The arithmetic is `double`, deliberately, and is NOT the decimal pair above.** These convert
    // a whole coordinate rather than a user-entered quantity, they were written in double, and every
    // clearance number circuitRF has ever reported came out of them. Re-deriving them in decimal
    // would be more exact past 2^53 and would also change measured results on a document nobody has
    // — a numeric change smuggled in under a file move, which is precisely what R-aut0-4 forbids.

    /// <summary>Nanometres to a layout's own DBU. 1 µm = 1,000 nm = <paramref name="dbuPerMicron"/> DBU.</summary>
    public static long NmToDbu(long nm, int dbuPerMicron) =>
        dbuPerMicron <= 0 ? nm : (long)Math.Round(nm * (double)dbuPerMicron / 1000.0, MidpointRounding.AwayFromZero);

    /// <summary>A layout's own DBU back to nanometres.</summary>
    public static long DbuToNm(long dbu, int dbuPerMicron) =>
        dbuPerMicron <= 0 ? dbu : (long)Math.Round(dbu * 1000.0 / dbuPerMicron, MidpointRounding.AwayFromZero);

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

    /// <summary>
    /// The suffix <see cref="TryParse"/> reads for <paramref name="unit"/>, <b>in ASCII</b> — the
    /// spelling a coordinate is WRITTEN in, as against <see cref="Suffix"/>, which is what a label
    /// beside a dimension field DISPLAYS.
    ///
    /// <para>They differ in one row and it matters: <see cref="Suffix"/> gives micrometres as
    /// <c>µm</c>, which <see cref="TryParse"/> does accept but which travels through an argument
    /// list, a JSON document and somebody's shell before it gets back here. A coordinate is emitted
    /// to be pasted into <c>circuitrf render --window</c>, so it is emitted in the seven bits every
    /// one of those hops carries unchanged.</para>
    ///
    /// <para>Said once, here, because <c>render</c>'s "a bare number is a refusal" message offers
    /// these spellings and <c>explain --extents</c> emits them. Two tables would be free to disagree,
    /// and the symptom would be one verb printing a coordinate the other refuses — which is
    /// R-aut12-3 exactly.</para>
    /// </summary>
    public static string AsciiSuffix(LayoutUnit unit) => unit switch
    {
        LayoutUnit.Nm   => "nm",
        LayoutUnit.Um   => "um",
        LayoutUnit.Mm   => "mm",
        LayoutUnit.Mil  => "mil",
        LayoutUnit.Inch => "in",
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, null),
    };

    /// <summary>
    /// A DBU value written as a unit-bearing string <see cref="TryParse"/> reads back to the SAME
    /// DBU — <c>6000um</c>, <c>-3mm</c>, <c>0nm</c>. The inverse of <see cref="TryParse"/>, and the
    /// spelling <c>circuitrf render --window</c> accepts (R-aut12-3: the one verb that says where the
    /// content is must emit what the other verb takes).
    ///
    /// <para><b>The decimal count is derived, not chosen.</b> One DBU is
    /// <c>1000 / (nm-per-unit x dbu-per-micron)</c> of this unit, so that many places resolve a single
    /// DBU and one more place puts the formatter's nearest-value rounding and
    /// <see cref="ToDbu"/>'s away-from-zero rounding a full order of magnitude apart, where they
    /// cannot land on opposite sides of a boundary. A fixed four places silently quantises a
    /// nanometre-resolution layout to 10 nm when it is written in millimetres — a window off by a
    /// hair, which is invisible.</para>
    /// </summary>
    public static string Spell(long dbu, LayoutUnit unit, int dbuPerMicron)
        => Format(dbu, unit, dbuPerMicron, SpellDecimals(unit, dbuPerMicron)) + AsciiSuffix(unit);

    /// <summary>
    /// How many decimal places are needed to resolve one DBU in <paramref name="unit"/>, plus one —
    /// the count at which <see cref="Format"/> and <see cref="TryParse"/> are a LOSSLESS round trip.
    ///
    /// <para><b>Public because an EDITABLE field needs it as much as <see cref="Spell"/> does.</b>
    /// Anything that formats a stored length into a box and parses that same box back must format at
    /// this count: at <see cref="Format"/>'s default of four places, a mil is quantised to 2.54 nm —
    /// coarser than the value being displayed — so the box shows a rounded number and committing the
    /// field writes the rounding back. Measured: 35 um entered, the display unit changed to mil, and
    /// a focus in and out with nothing typed turned it into 35.001 um. See
    /// <c>StackupLayerRowViewModel.RefreshFromModel</c>.</para>
    /// </summary>
    public static int SpellDecimals(LayoutUnit unit, int dbuPerMicron)
    {
        double dbuPerUnit = NmPerUnit(unit) * (double)Math.Max(1, dbuPerMicron) / 1000.0;
        return Math.Clamp((int)Math.Ceiling(Math.Log10(Math.Max(1.0, dbuPerUnit))) + 1, 1, 15);
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
