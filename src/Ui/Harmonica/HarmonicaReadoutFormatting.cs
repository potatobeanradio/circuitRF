using System;
using System.Globalization;
using System.Numerics;

namespace CircuitRF.Ui.Harmonica;

/// <summary>
/// R-h9c-7 (R1C §5) — the ONE place a Z/Γ readout is formatted for display and parsed back from
/// what the inline editor lets the user type. <c>HarmonicaSolver.BuildReadouts</c> calls the format
/// half; <c>ReadoutStripView</c>'s inline editor calls the parse half on commit — the same contract
/// as everywhere else in this codebase that a value is both shown and edited: what you see is what
/// you can type back.
/// </summary>
public static class HarmonicaReadoutFormatting
{
    // ── R6C §4 — fixed-width formatting ──────────────────────────────────────────────────────────
    //
    // Two independent sources of column-width churn during a drag: trailing zeros dropped by
    // "shortest" formats (0.### turns 10.01 into 10.1 — one character shorter) and the INTEGER side
    // growing (9.99 → 10.01, or an impedance running 0.5 Ω → 5000 Ω). Fixed decimal places fixes the
    // first; neither fixes the second, because the STRING LENGTH still depends on the VALUE. Every
    // quantity below is instead rendered through FixedWidth with a stated character BUDGET, so the
    // string length is a function of the ROW (what kind of quantity it is), never of the value.

    /// <summary>
    /// Formats <paramref name="value"/> to <paramref name="decimals"/> fixed places, padded (or, past
    /// the budget, switched to a fixed-width exponent form) to exactly <paramref name="budget"/>
    /// characters — never more, never fewer, for any value the row's own budget was sized for. NaN is
    /// the one value with no numeric width to reserve; callers append a unit suffix themselves, which
    /// never varies in length and so never has to go through this at all.
    /// </summary>
    /// <param name="pad">
    /// True (the default) pads to exactly <paramref name="budget"/> characters, for a row's own
    /// DISPLAY text. False leaves the number unpadded — still fixed-decimal, still exponent-clamped
    /// past the budget, just without the leading spaces — for anywhere the string becomes EDITABLE
    /// text (an inline editor's seed, a dialog's text box): a leading-space run inside editable text
    /// sits ahead of the caret and confuses select-then-type and caret-relative insertion alike, which
    /// is a real defect, not a cosmetic one — §4.2's reserved pixel WIDTH on the control is what keeps
    /// the column stable for these callers instead.
    /// </param>
    public static string FixedWidth(double value, int decimals, int budget, bool pad = true)
    {
        if (double.IsNaN(value)) return pad ? "—".PadLeft(budget) : "—";

        string fixedForm = value.ToString("F" + decimals, CultureInfo.InvariantCulture);
        if (fixedForm.Length <= budget) return pad ? fixedForm.PadLeft(budget) : fixedForm;

        // Past the budget: a FIXED-WIDTH exponent form (mantissa digit count never varies with
        // magnitude, unlike the integer side of a fixed-decimal string) — "1.23e+04", not "12345.68".
        int mantissaDecimals = Math.Max(decimals, 1);
        string exp = value.ToString("0." + new string('0', mantissaDecimals) + "e+00", CultureInfo.InvariantCulture);
        return pad ? exp.PadLeft(budget) : exp;
    }

    // Per-quantity budgets — a constant per ROW TYPE (§4.1's own instruction), not a magic number at
    // each call site. Complex rows (Z/Γ/V/I) share one generous part/magnitude/angle budget: Z can run
    // 0.5 Ω .. 5000+ Ω, Γ/V/I are smaller, and a slightly wider column for the smaller ones costs
    // nothing but a little blank space.
    public const int ComplexPartDecimals = 3;   public const int ComplexPartBudget = 10;   // "-5000.000"
    public const int ComplexMagDecimals  = 3;   public const int ComplexMagBudget  = 9;    // "5000.000"
    public const int AngleDecimals       = 1;   public const int AngleBudget       = 7;    // "-180.0"

    public const int DbmDecimals     = 2; public const int DbmBudget     = 9;   // "-1000.00"
    public const int DbDecimals      = 2; public const int DbBudget      = 8;   // "-100.00"
    public const int PercentDecimals = 1; public const int PercentBudget = 7;   // "-1000.0"
    public const int WattDecimals    = 3; public const int WattBudget    = 12;  // "-100000.000"
    public const int DegreeDecimals  = 1; public const int DegreeBudget  = 7;   // "-180.0"

    public static string FormatDbm(double value)
        => double.IsNaN(value) ? "—" : FixedWidth(value, DbmDecimals, DbmBudget) + " dBm";

    public static string FormatDb(double value)
        => double.IsNaN(value) ? "—" : FixedWidth(value, DbDecimals, DbBudget) + " dB";

    public static string FormatPercent(double value)
        => double.IsNaN(value) ? "—" : FixedWidth(value, PercentDecimals, PercentBudget) + " %";

    public static string FormatWatt(double value)
        => double.IsNaN(value) ? "—" : FixedWidth(value, WattDecimals, WattBudget) + " W";

    public static string FormatDegrees(double value)
        => double.IsNaN(value) ? "—" : FixedWidth(value, DegreeDecimals, DegreeBudget) + "°";

    public static string FormatZ(Complex z, ReadoutFormat format, bool pad = true)
        => FormatComplex(z, format, pad) + " Ω";

    public static string FormatGamma(Complex g, ReadoutFormat format, bool pad = true)
        => FormatComplex(g, format, pad);

    /// <summary>
    /// True for R6C §2's intrinsic VDS/IDS chunk keys (<c>"VDSi.0"</c>, <c>"IDSi.3f0"</c>, …) — a
    /// Volts/Amps quantity, never an impedance. The chunk's own HEADER row states the unit once, in
    /// brackets ("Intrinsic VDS (V)"), so a per-harmonic VALUE row carries NO unit suffix at all —
    /// unlike a Z row, which always carries " Ω" on every value. One place this distinction is made,
    /// so <see cref="DefaultReadoutFormat"/> and <c>ReadoutStripView</c>'s render-time reformat
    /// (R-h9r2-25) can never disagree about which rows these are.
    /// </summary>
    public static bool IsIntrinsicVoltageOrCurrentKey(string formatKey)
        => formatKey.StartsWith("VDSi.", StringComparison.Ordinal)
        || formatKey.StartsWith("IDSi.", StringComparison.Ordinal);

    /// <summary>brief-harmonicarf-r6b §1.3/§2.1 — the ONE format shared by the VSWR-circle drag's live
    /// readout and the marker context menu's <c>VSWR: …</c> header, so the number a user drags to is
    /// the number the menu then shows. <c>0.##</c> is harmonicaRF's own convention (Data Display's
    /// analogous readout uses <c>F4</c>; the two are unrelated codepaths and are not required to
    /// agree).</summary>
    public static string FormatVswr(double vswr) => $"VSWR: {vswr:0.##}";

    /// <summary>
    /// The format a row's <c>FormatKey</c> resolves to when nothing has overridden it yet — real/
    /// imaginary for every row except R6C §2's intrinsic VDS/IDS chunks, which the owner is explicit
    /// default to magnitude ∠ angle. One place, so <c>HarmonicaSolver</c>'s own null-resolver fallback
    /// and <c>HarmonicaViewModel.ReadoutFormatLookup</c>'s unrecognized-key fallback cannot disagree.
    /// </summary>
    public static ReadoutFormat DefaultReadoutFormat(string key)
        => IsIntrinsicVoltageOrCurrentKey(key) ? ReadoutFormat.MagnitudeAngle : ReadoutFormat.RealImaginary;

    // ── R6C §4.2 — the reserved WIDTH a row's value control never has to grow past ────────────────
    //
    // Every possible presentation of a row's own quantity, in characters — the WIDER of rectangular
    // ("R+jI") and polar ("M∠A°") for a complex row, so toggling the format (R-h9c-7's own flyout)
    // never moves a column either, on top of §4.1's fixed-width numbers making a VALUE update never
    // move one.
    public const int RectComplexChars  = 2 * ComplexPartBudget + 2;                  // "R+jI"
    public const int PolarComplexChars = ComplexMagBudget + 1 + AngleBudget + 1;     // "M∠A°"
    public const int MaxComplexChars   = RectComplexChars > PolarComplexChars ? RectComplexChars : PolarComplexChars;

    /// <summary>
    /// The widest this row could EVER render, in characters — a pure function of what KIND of row it
    /// is (its Label/IsComplex/IsGamma shape), never of its current value or format. <c>ReadoutStripView</c>
    /// writes this to a row's value control's <c>Width</c> on every refresh (not just when the row is
    /// rebuilt) — since it never changes for a fixed row kind, writing it every frame is a no-op on
    /// screen, and it stays correct across a live font-size change.
    /// </summary>
    public static int ReservedValueChars(HarmonicaReadout item)
    {
        if (item.IsComplex)
        {
            // " Ω" is 2 characters; a bare Γ row and an intrinsic VDS/IDS row (unit stated once in
            // the chunk's own HEADER, not per value — see IsIntrinsicVoltageOrCurrentKey) carry none.
            bool noUnit = item.IsGamma
                || (item.FormatKey is { } key && IsIntrinsicVoltageOrCurrentKey(key));
            return MaxComplexChars + (noUnit ? 0 : 2);
        }

        return item.Label switch
        {
            "Pin" or "Pout"                => DbmBudget + 4,     // " dBm"
            "Gain" or "Gp"                  => DbBudget + 3,      // " dB"
            "DE" or "PAE" or "Efficiency"   => PercentBudget + 2, // " %"
            "Pdc"                           => WattBudget + 2,    // " W"
            "AM/PM"                         => DegreeBudget + 1,  // "°"
            _                                => 12,                // a generous default for anything else
        };
    }

    public static string FormatComplex(Complex z, ReadoutFormat format, bool pad = true)
    {
        if (double.IsNaN(z.Real) || double.IsNaN(z.Imaginary)) return "—";
        if (format == ReadoutFormat.MagnitudeAngle)
            return $"{FixedWidth(z.Magnitude, ComplexMagDecimals, ComplexMagBudget, pad)}" +
                   $"∠{FixedWidth(z.Phase * 180.0 / Math.PI, AngleDecimals, AngleBudget, pad)}°";
        return $"{FixedWidth(z.Real, ComplexPartDecimals, ComplexPartBudget, pad)}" +
               $"{(z.Imaginary >= 0 ? "+j" : "-j")}{FixedWidth(Math.Abs(z.Imaginary), ComplexPartDecimals, ComplexPartBudget, pad)}";
    }

    /// <summary>
    /// Parses text back into a <see cref="Complex"/>, in the format it was DISPLAYED in. Refuses
    /// (returns false) anything it cannot parse with confidence — a misread value silently kept is
    /// worse than an edit that stays open for another try.
    /// </summary>
    public static bool TryParse(string? text, ReadoutFormat format, out Complex value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (text.EndsWith('Ω')) text = text[..^1].Trim();

        return format == ReadoutFormat.MagnitudeAngle
            ? TryParseMagnitudeAngle(text, out value)
            : TryParseRectangular(text, out value);
    }

    private static bool TryParseMagnitudeAngle(string text, out Complex value)
    {
        value = default;
        int at = text.IndexOf('∠');
        string magPart = (at >= 0 ? text[..at] : text).Trim();
        string angPart = (at >= 0 ? text[(at + 1)..] : "0").TrimEnd('°', ' ').Trim();

        if (!TryDouble(magPart, out double mag) || !TryDouble(angPart, out double angDeg)) return false;
        value = Complex.FromPolarCoordinates(mag, angDeg * Math.PI / 180.0);
        return true;
    }

    private static bool TryParseRectangular(string text, out Complex value)
    {
        value = default;
        text = text.Replace(" ", "");
        if (text.Length == 0) return false;

        // The split point is the LAST '+'/'-' after index 0 that is not an exponent sign — the
        // imaginary term is always the trailing one ("R+jX" / "R-jX"), so its own leading sign is
        // the last candidate rather than the first.
        int split = -1;
        for (int i = 1; i < text.Length; i++)
        {
            if (text[i] is '+' or '-' && text[i - 1] is not ('e' or 'E'))
                split = i;
        }

        string realPart, imagPart;
        if (split < 0)
        {
            if (text.Contains('j') || text.Contains('J')) { realPart = "0"; imagPart = text; }
            else { realPart = text; imagPart = "j0"; }
        }
        else
        {
            realPart = text[..split];
            imagPart = text[split..];
        }

        if (!TryDouble(realPart, out double re)) return false;

        string imClean = imagPart.Replace("j", "").Replace("J", "");
        imClean = imClean switch { "" or "+" => "1", "-" => "-1", _ => imClean };
        if (!TryDouble(imClean, out double im)) return false;

        value = new Complex(re, im);
        return true;
    }

    private static bool TryDouble(string s, out double v)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
}
