using System;
using System.Collections.Generic;
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
    // ── R6C §4 / R-hui-2 — fixed-DECIMAL formatting, column stability from the CONTROL, not the text ─
    //
    // Every quantity is rendered to a fixed number of DECIMAL PLACES (never "shortest form" — 0.###
    // would turn 10.01 into 10.1, one character shorter) via FixedWidth, so a value that moves without
    // changing its digit COUNT (10.123 → 10.120) never changes the string's length. A digit-count
    // change (9.99 → 10.01, or an impedance running 0.5 Ω → 5000 Ω) still changes the length — the
    // owner is explicit that is acceptable jitter, unlike the padded-real/imaginary-parts scheme this
    // replaced ("x+j     y"), which stuffed LEADING SPACES into the string itself to hold every row of
    // one kind to one length. Column stability instead comes from the value CONTROL: ReadoutStripView
    // reserves a fixed pixel Width per row kind (<see cref="WorstCaseValueTexts"/>, measured against
    // the live typeface), and pins the LABEL column to a per-chunk measured width the same way (R7C
    // §1.5 — Grid.IsSharedSizeScope/SharedSizeGroup does NOT align columns hosted in a StackPanel in
    // this Avalonia build, confirmed empirically), so values align across rows AND never reflow live
    // off a dragged value's own changing text.
    // <paramref name="budget"/> still bounds the character count, but only as an EXPONENT-form
    // fallback for a pathologically large value — it no longer pads a short one up to it.

    /// <summary>
    /// Formats <paramref name="value"/> to <paramref name="decimals"/> fixed places — switched to a
    /// fixed-width exponent form only past <paramref name="budget"/> characters, for a value so large
    /// the fixed-decimal form would otherwise run unbounded. NaN renders as "—"; callers append a unit
    /// suffix themselves, which never varies in length and so never has to go through this at all.
    /// </summary>
    public static string FixedWidth(double value, int decimals, int budget)
    {
        if (double.IsNaN(value)) return "—";

        string fixedForm = value.ToString("F" + decimals, CultureInfo.InvariantCulture);
        if (fixedForm.Length <= budget) return fixedForm;

        // Past the budget: a FIXED-WIDTH exponent form (mantissa digit count never varies with
        // magnitude, unlike the integer side of a fixed-decimal string) — "1.23e+04", not "12345.68".
        int mantissaDecimals = Math.Max(decimals, 1);
        return value.ToString("0." + new string('0', mantissaDecimals) + "e+00", CultureInfo.InvariantCulture);
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

    public static string FormatZ(Complex z, ReadoutFormat format)
        => FormatComplex(z, format) + " Ω";

    public static string FormatGamma(Complex g, ReadoutFormat format)
        => FormatComplex(g, format);

    // ── R-hui-4 — value/unit split for column-aligned rendering ─────────────────────────────────
    //
    // A closed whitelist of the exact suffixes THIS file's own Format* functions produce — never a
    // blind "last space" search, which would misparse a plain status string with a space in it
    // ("no optimum", "not located") as if it carried a unit. Longest-specific-first doesn't actually
    // matter here (no suffix here is itself a suffix of another: "dBm" does not end in " dB"), but
    // they're listed that way for a reader's sake. Degrees ("45.0°") is deliberately excluded — it is
    // one of this file's own attached-with-no-space forms (see FormatDegrees), so it stays one token.
    private static readonly string[] KnownUnitSuffixes = [" dBm", " dB", " %", " W", " Ω"];

    /// <summary>
    /// Splits a Format* function's own rendered text back into its bare value and its unit, so a
    /// renderer can lay the two out in separate, independently column-aligned cells (owner: "I want
    /// the units to be as close to the values as possible... while aligning horizontally"). Returns
    /// <c>(formatted, "")</c> for anything that doesn't end in a known suffix — a Γ row, an intrinsic
    /// VDS/IDS row (unit lives in the chunk's own header), "—", or a plain status string.
    /// </summary>
    public static (string Value, string Unit) SplitUnit(string formatted)
    {
        foreach (var suffix in KnownUnitSuffixes)
            if (formatted.EndsWith(suffix, StringComparison.Ordinal))
                return (formatted[..^suffix.Length], suffix.TrimStart());
        return (formatted, "");
    }

    // ── R7C §1.3 — the worst-case VALUE text a row kind can ever render, as a LITERAL string ───────
    //
    // R-hui-5's own reasoning still holds — pin the VALUE control's own reserved size to a pure
    // function of the row's KIND, never of its current text, so a dragged value's own changing digit
    // count cannot be the thing that moves the column. What changes below is WHAT that reservation is
    // measured FROM: a literal worst-case string, actually measured against the live typeface, rather
    // than a character count times an assumed per-character advance.
    //
    // R-hui-5/R-hui-7 pinned a row's reserved width to a CHARACTER COUNT times an assumed 0.55
    // per-character advance for a proportional font — wrong by a different amount for digits, '−',
    // '+j', '∠', '°', 'Ω', and '—', and wrong by a different amount again at every (non-integer) font
    // size the strip actually renders at. R7C replaces the guess with an actual measurement of the
    // typeface (ReadoutStripView.ReservedValueWidth), and replaces the character-count budget with the
    // literal worst-case STRING that measurement is taken of — tied to the same decimals/budget
    // constants above so a change to one cannot silently outgrow the other.

    private static string WorstCaseFixed(int intDigits, int decimals, bool signed)
        => (signed ? "-" : "") + new string('0', intDigits) + "." + new string('0', decimals);

    // Integer-digit budgets, one per quantity — the counterpart to each Format*'s own Decimals/Budget
    // pair above. Kept as named constants (not derived from Budget, which is a switchover threshold
    // with its own slack, not a digit count) so the worst case stays a readable, auditable literal.
    public const int ComplexPartIntDigits = 4;   // "-0000.000-j0000.000"
    public const int ComplexMagIntDigits  = 4;   // "0000.000∠…"
    public const int AngleIntDigits       = 3;   // "…∠-000.0°"
    public const int DbmIntDigits         = 4;   // "-0000.00"
    public const int DbIntDigits          = 3;   // "-000.00"
    public const int PercentIntDigits     = 4;   // "-0000.0"
    public const int WattIntDigits        = 6;   // "-000000.000"
    public const int DegreeIntDigits      = 3;   // "-000.0°"

    /// <summary>A complex row's REAL/IMAGINARY worst case — one of the two a right-click can flip to
    /// (R-hui-5's own reasoning, formerly a character-count budget named MaxComplexChars), so callers
    /// measure both this and <see cref="WorstCasePolarComplex"/> and reserve the wider.</summary>
    public static string WorstCaseRectComplex =>
        WorstCaseFixed(ComplexPartIntDigits, ComplexPartDecimals, true) + "-j" +
        WorstCaseFixed(ComplexPartIntDigits, ComplexPartDecimals, false);

    /// <summary>A complex row's MAGNITUDE/ANGLE worst case — also γ's own, permanently (§2.4: γ never
    /// flips format, so it only ever needs this one).</summary>
    public static string WorstCasePolarComplex =>
        WorstCaseFixed(ComplexMagIntDigits, ComplexMagDecimals, false) + "∠" +
        WorstCaseFixed(AngleIntDigits, AngleDecimals, true) + "°";

    /// <summary>
    /// Every worst-case VALUE literal <paramref name="item"/>'s row kind could ever render — one string
    /// for a scalar row, two (rect and polar) for a complex row, since a right-click can flip which one
    /// is showing without a re-solve. <c>ReadoutStripView.ReservedValueWidth</c> measures each and
    /// reserves the widest — never a character count times an assumed per-character advance.
    /// </summary>
    public static IReadOnlyList<string> WorstCaseValueTexts(HarmonicaReadout item)
    {
        if (item.IsComplex) return [WorstCaseRectComplex, WorstCasePolarComplex];
        if (item.Label == "γ") return [WorstCasePolarComplex];

        return item.Label switch
        {
            "Pin" or "Pout" => [WorstCaseFixed(DbmIntDigits, DbmDecimals, true)],
            "Gain" or "Gp"   => [WorstCaseFixed(DbIntDigits, DbDecimals, true)],
            "Eff" or "PAE"   => [WorstCaseFixed(PercentIntDigits, PercentDecimals, true)],
            "Pdc"            => [WorstCaseFixed(WattIntDigits, WattDecimals, true)],
            "AM/PM"          => [WorstCaseFixed(DegreeIntDigits, DegreeDecimals, true) + "°"],
            _                 => ["0000000000—"],   // a generous default for anything else, incl. "—"
        };
    }

    /// <summary>R8C §2 — below this magnitude the angle is numerical noise: γ = V₂·conj(V₁)²/|V₁|³
    /// divides by |V₁|³, so a vanishing 2nd harmonic leaves an angle that swings freely with the last
    /// bits of V₂. The MAGNITUDE is still real information and is still shown; only the angle is
    /// replaced by "—", the same em-dash every other unavailable value in this strip uses.</summary>
    public const double GammaPhaseNoiseFloor = 1e-3;

    /// <summary>
    /// R7C §2.4 — the input nonlinearity factor, ALWAYS magnitude ∠ angle: the owner is explicit that
    /// real/imaginary "does not make sense because of the way it is defined" (§2.1's γ = φ₂ − 2·φ₁ has
    /// no meaningful real/imaginary decomposition). NaN — <see cref="HarmonicaSolver"/>'s own signal
    /// for "cannot be computed this frame" — renders as "—", the same as every other formatter here.
    /// R8C §2 — below <see cref="GammaPhaseNoiseFloor"/> the phase is noise and is shown as "—" while
    /// the magnitude is still real information and still shown.
    /// </summary>
    public static string FormatGammaFactor(Complex g)
    {
        if (double.IsNaN(g.Real) || double.IsNaN(g.Imaginary)) return "—";
        if (g.Magnitude < GammaPhaseNoiseFloor)
            return FixedWidth(g.Magnitude, ComplexMagDecimals, ComplexMagBudget) + "∠—";
        return FormatComplex(g, ReadoutFormat.MagnitudeAngle);
    }

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

    /// <summary>R8B §7.3 — the saturated-aware sibling. Round 10 changed what "saturated" MEANS: there
    /// is no search bracket to run off any more (<see cref="HarmonicaVswrHandle.VswrThroughEx"/> is a
    /// closed form), so it is now true only where the answer is genuinely infinite — the drag point
    /// sits on the rim of the marker's own power-wave circle, the one place no finite VSWR reaches.
    /// Showing <c>∞</c> there is honest; showing <see cref="HarmonicaVswrHandle.InfiniteVswr"/>'s own
    /// stand-in 1e9 would read as a measurement.</summary>
    public static string FormatVswr(double vswr, bool saturated)
        => saturated ? $"VSWR: {(vswr < 0 ? "-" : "")}∞" : FormatVswr(vswr);

    /// <summary>
    /// The format a row's <c>FormatKey</c> resolves to when nothing has overridden it yet — real/
    /// imaginary for every row except R6C §2's intrinsic VDS/IDS chunks, which the owner is explicit
    /// default to magnitude ∠ angle. One place, so <c>HarmonicaSolver</c>'s own null-resolver fallback
    /// and <c>HarmonicaViewModel.ReadoutFormatLookup</c>'s unrecognized-key fallback cannot disagree.
    /// </summary>
    public static ReadoutFormat DefaultReadoutFormat(string key)
        => IsIntrinsicVoltageOrCurrentKey(key) ? ReadoutFormat.MagnitudeAngle : ReadoutFormat.RealImaginary;

    public static string FormatComplex(Complex z, ReadoutFormat format,
        int partDecimals = ComplexPartDecimals, int magDecimals = ComplexMagDecimals)
    {
        if (double.IsNaN(z.Real) || double.IsNaN(z.Imaginary)) return "—";
        if (format == ReadoutFormat.MagnitudeAngle)
            return $"{FixedWidth(z.Magnitude, magDecimals, ComplexMagBudget)}" +
                   $"∠{FixedWidth(z.Phase * 180.0 / Math.PI, AngleDecimals, AngleBudget)}°";
        return $"{FixedWidth(z.Real, partDecimals, ComplexPartBudget)}" +
               $"{(z.Imaginary >= 0 ? "+j" : "-j")}{FixedWidth(Math.Abs(z.Imaginary), partDecimals, ComplexPartBudget)}";
    }

    /// <summary>R9A §4 — the MXP/MXE header's own impedance, at ONE decimal. This is the argmax of a
    /// fitted RBF surface, not a measured value: three decimals (<see cref="ComplexPartDecimals"/>, which
    /// every other complex row uses and which stays untouched) reads as a precision the fit does not
    /// carry. One named constant, so the digit count is changed in one place.</summary>
    public const int MxHeaderZDecimals = 1;

    public static string FormatZCompact(Complex z, ReadoutFormat format)
        => FormatComplex(z, format, partDecimals: MxHeaderZDecimals, magDecimals: MxHeaderZDecimals) + " Ω";

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
