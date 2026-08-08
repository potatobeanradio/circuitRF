using System.Globalization;

namespace CircuitRF.WBond;

/// <summary>The design units a wBond may be authored and displayed in (wbond.md §6.5).</summary>
public enum WBondUnit
{
    Nm,
    Um,
    Mm,
    Mil,
    Inch,
}

/// <summary>
/// Length units for wBond. Storage is integer nanometres (DBU), exactly as the layout editor does,
/// so switching display units is lossless and free.
///
/// <para><b>Why this file exists rather than a reference to
/// <c>src/Ui/Layout/LayoutUnits.cs</c>.</b> That file is the authority for this table and contains
/// no Avalonia in its source — but it lives in <c>CircuitRF.Ui</c>, which references Avalonia, so
/// referencing it from this framework-free project fails <c>tests/Firewall.Tests</c>. Duplicating
/// five integer constants is the right trade; taking a UI-framework dependency to avoid it is not
/// (brief-wbond-wba §0.3 item 1).</para>
///
/// <para><b>Keep in sync with <c>LayoutUnits.NmPerUnit</c>.</b> If the display layer is ever lifted
/// out of <c>src/Ui</c> (<c>ui-architecture.md</c> §4's deferred refactor), this copy folds into it
/// and this note goes away. <c>WBondUnitsParityTests</c> pins the shared values so the two cannot
/// silently diverge in the meantime.</para>
/// </summary>
public static class WBondUnits
{
    /// <summary>Exact size of one unit, in nanometres. Every value here is exact in a long.</summary>
    public static long NmPerUnit(WBondUnit unit) => unit switch
    {
        WBondUnit.Nm   => 1,
        WBondUnit.Um   => 1_000,
        WBondUnit.Mm   => 1_000_000,
        WBondUnit.Mil  => 25_400,
        WBondUnit.Inch => 25_400_000,
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unknown wBond unit."),
    };

    /// <summary>Nanometres in one metre — the conversion the physics layer uses, once per mesh.</summary>
    public const double NmPerMetre = 1e9;

    /// <summary>Nanometres to metres. The physics works in SI; the model stores DBU.</summary>
    public static double ToMetres(long nm) => nm / NmPerMetre;

    /// <summary>Metres to nanometres, rounded to the nearest DBU.</summary>
    public static long FromMetres(double metres) => (long)Math.Round(metres * NmPerMetre);

    /// <summary>A value expressed in <paramref name="unit"/>, converted to DBU.</summary>
    public static long ToNm(double value, WBondUnit unit) => (long)Math.Round(value * NmPerUnit(unit));

    /// <summary>A DBU value expressed in <paramref name="unit"/>.</summary>
    public static double FromNm(long nm, WBondUnit unit) => (double)nm / NmPerUnit(unit);

    /// <summary>The suffix shown in the UI and written to <c>.wBond</c>.</summary>
    public static string Suffix(WBondUnit unit) => unit switch
    {
        WBondUnit.Nm   => "nm",
        WBondUnit.Um   => "um",
        WBondUnit.Mm   => "mm",
        WBondUnit.Mil  => "mil",
        WBondUnit.Inch => "in",
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unknown wBond unit."),
    };

    /// <summary>
    /// Parses a length written as a number with an OPTIONAL unit suffix — <c>"2 mil"</c>,
    /// <c>"50um"</c>, <c>"1.6mm"</c>, or a bare <c>"125"</c>, which is read in
    /// <paramref name="defaultUnit"/>.
    ///
    /// <para>This is the one parser behind every "type a new value" prompt in the wBond editor, so
    /// "in any units" means the same thing in all of them. A bare number falling back to the
    /// document's own display unit is what makes the common case (retype the number you are already
    /// looking at) work without typing a suffix — and stating the unit always overrides it, so a user
    /// who is thinking in mil while the document displays microns is never silently misread.</para>
    /// </summary>
    /// <returns>False on unparseable text, a non-finite value, or an unrecognised suffix.</returns>
    public static bool TryParseLength(string? text, WBondUnit defaultUnit, out long nm)
    {
        nm = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string s = text.Trim();

        // Split at the end of the numeric run rather than by whitespace: "50um" has no space, and
        // requiring one would refuse the spelling people actually type.
        int end = 0;
        while (end < s.Length && (char.IsDigit(s[end]) || s[end] is '+' or '-' or '.' or ',' or 'e' or 'E'))
        {
            // 'e'/'E' only counts as exponent notation when a sign or digit follows it — otherwise it
            // is the first letter of a suffix and the number has already ended.
            if (s[end] is 'e' or 'E')
            {
                int next = end + 1;
                if (next >= s.Length || !(char.IsDigit(s[next]) || s[next] is '+' or '-')) break;
            }
            end++;
        }

        if (end == 0) return false;

        if (!double.TryParse(s[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            return false;
        if (!double.IsFinite(value)) return false;

        string suffix = s[end..].Trim();
        var unit = defaultUnit;
        if (suffix.Length > 0 && !TryParseUnit(suffix, out unit)) return false;

        nm = ToNm(value, unit);
        return true;
    }

    /// <summary>Parses a unit suffix. Accepts the µ and μ spellings of micron.</summary>
    public static bool TryParseUnit(string suffix, out WBondUnit unit)
    {
        switch (suffix.Trim().ToLowerInvariant())
        {
            case "nm":                     unit = WBondUnit.Nm;   return true;
            case "um": case "µm": case "μm": unit = WBondUnit.Um;  return true;
            case "mm":                     unit = WBondUnit.Mm;   return true;
            case "mil":                    unit = WBondUnit.Mil;  return true;
            case "in": case "inch":        unit = WBondUnit.Inch; return true;
            default:                       unit = WBondUnit.Nm;   return false;
        }
    }
}
