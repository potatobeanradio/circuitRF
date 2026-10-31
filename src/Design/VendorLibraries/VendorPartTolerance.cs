using System.Globalization;
using System.Text.RegularExpressions;

namespace CircuitRF.Design.VendorLibraries;

/// <summary>Whether a part's tolerance is a value in the part's own unit or a percent of its value.</summary>
public enum VendorToleranceKind { Absolute, Percent }

/// <summary>Where a part's tolerance came from: a field of its header, or the letter in its part number.</summary>
public enum VendorToleranceSource { Header, PartNumber }

/// <summary>
/// A vendor part's tolerance (brief vendorlib-5 R-vl5-1): ± <see cref="Value"/>, either in base SI (farads, henries)
/// or as a percent of the part's stated value.
/// </summary>
/// <param name="Value">The half-width: base SI when <see cref="Kind"/> is absolute, a percent number otherwise.</param>
/// <param name="Quantity">The part's quantity, which names the unit an absolute tolerance is written in.</param>
/// <param name="Offset">Where the decoded letter sits, counted from the END of the part number (1 = the last
/// character); 0 for a tolerance the header states.</param>
public sealed record VendorPartTolerance(
    double                Value,
    VendorToleranceKind   Kind,
    VendorToleranceSource Source,
    VendorPartQuantity    Quantity,
    int                   Offset = 0)
{
    /// <summary>The spread text a <c>tune</c> line would carry for it — <c>0.1 pF</c>, <c>5%</c> — which is what
    /// <c>tol=part</c> resolves to.</summary>
    public string SpreadText => Kind == VendorToleranceKind.Percent
        ? Value.ToString("0.###", CultureInfo.InvariantCulture) + "%"
        : AbsoluteText();

    /// <summary>The tolerance as a person reads it: <c>±0.1 pF</c>, <c>±5 %</c>.</summary>
    public string DisplayText => Kind == VendorToleranceKind.Percent
        ? "±" + Value.ToString("0.###", CultureInfo.InvariantCulture) + " %"
        : "±" + AbsoluteText();

    /// <summary>The JSON word for <see cref="Kind"/>.</summary>
    public string KindWord => Kind == VendorToleranceKind.Percent ? "percent" : "absolute";

    /// <summary>The JSON word for <see cref="Source"/>.</summary>
    public string SourceWord => Source == VendorToleranceSource.Header ? "header" : "part-number";

    /// <summary>The half-width in base SI about a part of <paramref name="nominalSi"/>.</summary>
    public double WidthSi(double nominalSi) => Kind == VendorToleranceKind.Percent ? Math.Abs(nominalSi) * Value / 100 : Value;

    /// <summary>An absolute tolerance in pF or nH — the unit the letter table is written in, whatever its size.</summary>
    private string AbsoluteText() => Quantity == VendorPartQuantity.Inductance
        ? (Value / 1e-9).ToString("0.###", CultureInfo.InvariantCulture) + " nH"
        : (Value / 1e-12).ToString("0.###", CultureInfo.InvariantCulture) + " pF";
}

/// <summary>
/// Reads a vendor part's tolerance (brief vendorlib-5 R-vl5-1): a field of its header when the header states one,
/// otherwise the tolerance letter of its part number.
///
/// <para><b>The letter is found by anchoring on the value code computed from the header's own stated value</b> —
/// <c>100</c> for 10 pF, <c>R60</c> for 0.6 pF, <c>1N3</c> for 1.3 nH — and taking the letter right after it. Never
/// by a position, a prefix or a series: a fixed position is one manufacturer's layout and decodes garbage with no
/// error on the next. A letter that is not in the shared table, or an absolute letter that is implausible for the
/// value, is not a match, and anything short of exactly one answer is a refusal with its reason.</para>
/// </summary>
public static class VendorPartToleranceDecoder
{
    /// <summary>The refusal for a part whose part number holds no value code followed by a known letter.</summary>
    public const string NotKnown = "its part number holds no tolerance letter after its value code";

    /// <summary>
    /// The tolerance of one part: <paramref name="stated"/> when the header states one, otherwise the decode of
    /// <paramref name="partNumber"/> against the stated <paramref name="value"/>. Null with <paramref name="note"/>
    /// set when it is not known; null with a null note when the part has no C or L value to have one.
    /// </summary>
    public static VendorPartTolerance? Of(VendorLibraryHeader header, string partNumber, out string? note)
    {
        note = null;
        if (header.Quantity is not (VendorPartQuantity.Capacitance or VendorPartQuantity.Inductance)
            || header.Value is not { } value || value <= 0)
            return null;
        if (header.StatedTolerance is { } stated) return stated;
        return Decode(value, header.Quantity, partNumber, out note);
    }

    /// <summary>The letter right after the value code of <paramref name="value"/> in <paramref name="partNumber"/>.</summary>
    public static VendorPartTolerance? Decode(double value, VendorPartQuantity quantity, string partNumber, out string? note)
    {
        note = null;
        if (quantity is not (VendorPartQuantity.Capacitance or VendorPartQuantity.Inductance) || value <= 0
            || string.IsNullOrWhiteSpace(partNumber))
            return null;

        var found = new List<VendorPartTolerance>();
        var rejected = new List<char>();
        foreach (string code in ValueCodes(value, quantity))
        {
            int at = 0;
            while ((at = partNumber.IndexOf(code, at, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                int letterAt = at + code.Length;
                bool preceded = at > 0 && char.IsAsciiDigit(partNumber[at - 1]);
                if (!preceded && letterAt < partNumber.Length && char.IsAsciiLetter(partNumber[letterAt]))
                {
                    char letter = char.ToUpperInvariant(partNumber[letterAt]);
                    if (Letter(letter, value, quantity) is { } t) found.Add(t with { Offset = partNumber.Length - letterAt });
                    else rejected.Add(letter);
                }
                at++;
            }
        }

        if (found.Count == 0)
        {
            note = rejected.Count == 0 ? NotKnown
                : $"the letter after its value code ({string.Join(", ", rejected.Distinct())}) is not a tolerance a part of this value can have";
            return null;
        }
        var first = found[0];
        if (found.Any(t => t.Kind != first.Kind || t.Value != first.Value))
        {
            note = "its part number spells its value code more than once, with different letters after it";
            return null;
        }
        return first;
    }

    // ── The letter table (R-vl5-1c) ──────────────────────────────────────────

    /// <summary>What a letter means on a part of <paramref name="value"/> (base SI), or null when it means nothing
    /// here — not in the shared table, or an absolute tolerance that is implausible for the value.</summary>
    internal static VendorPartTolerance? Letter(char letter, double value, VendorPartQuantity quantity)
    {
        double? percent = letter switch { 'F' => 1, 'G' => 2, 'H' => 3, 'J' => 5, 'K' => 10, 'M' => 20, _ => null };
        if (percent is { } p) return new VendorPartTolerance(p, VendorToleranceKind.Percent, VendorToleranceSource.PartNumber, quantity);

        bool capacitor = quantity == VendorPartQuantity.Capacitance;
        double unit = capacitor ? 1e-12 : 1e-9;
        double? absolute = (letter, capacitor) switch
        {
            ('W', _)     => 0.05,
            ('B', _)     => 0.1,
            ('C', true)  => 0.25,
            ('C', false) => 0.2,
            ('S', false) => 0.3,
            ('D', _)     => 0.5,
            _            => null,
        };
        if (absolute is not { } a) return null;

        // Plausible only on a part of at most 10 pF / 10 nH whose value is at least the tolerance: a 0.1 pF part at
        // ±0.1 pF is a real, published combination; a 0.1 pF part read as ±0.25 pF is not.
        double width = a * unit;
        if (value > 10 * unit * (1 + 1e-9) || value < width * (1 - 1e-9)) return null;
        return new VendorPartTolerance(width, VendorToleranceKind.Absolute, VendorToleranceSource.PartNumber, quantity);
    }

    // ── The value codes (R-vl5-1b) ───────────────────────────────────────────

    /// <summary>Every spelling of <paramref name="value"/> (base SI) in each of the quantity's units: two significant
    /// digits and a power of ten, three characters with <c>R</c> as the decimal point, and for an inductance in nH
    /// three characters with <c>N</c> as the decimal point.</summary>
    public static IReadOnlyList<string> ValueCodes(double value, VendorPartQuantity quantity)
    {
        var codes = new List<string>();
        (double Scale, bool Nano)[] units = quantity == VendorPartQuantity.Capacitance
            ? [(1e-12, false), (1e-9, false), (1e-6, false)]
            : [(1e-9, true), (1e-6, false)];

        foreach (var (scale, nano) in units)
        {
            double v = value / scale;

            // Significand–multiplier, for at least 10 units: 100, 104, 226.
            if (v >= 10 * (1 - 1e-9))
            {
                int k = (int)Math.Floor(Math.Log10(v) + 1e-9) - 1;
                double sig = v / Math.Pow(10, k);
                int digits = (int)Math.Round(sig);
                if (k is >= 0 and <= 9 && digits is >= 10 and <= 99 && Near(sig, digits))
                    codes.Add(digits.ToString(CultureInfo.InvariantCulture) + k.ToString(CultureInfo.InvariantCulture));
            }

            // A letter as the decimal point, for below 100 units — every three-character spelling.
            if (v < 100)
            {
                foreach (string s in PointSpellings(v))
                {
                    codes.Add(s.Replace('.', 'R'));
                    if (nano) codes.Add(s.Replace('.', 'N'));
                }
            }
        }
        return [.. codes.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The three-character spellings of <paramref name="v"/> with a decimal point — <c>.60</c>, <c>0.6</c>,
    /// <c>12.</c> — each exact, never rounded.</summary>
    private static IEnumerable<string> PointSpellings(double v)
    {
        // Two digits after the point: .60, .05
        if (v < 1 && Near(v * 100, Math.Round(v * 100)))
            yield return "." + ((int)Math.Round(v * 100)).ToString("D2", CultureInfo.InvariantCulture);
        // One digit either side: 0.6, 1.5, 8.0
        if (v < 10 && Near(v * 10, Math.Round(v * 10)))
        {
            int tenths = (int)Math.Round(v * 10);
            yield return (tenths / 10).ToString(CultureInfo.InvariantCulture) + "." + (tenths % 10).ToString(CultureInfo.InvariantCulture);
        }
        // Two digits before the point: 12., 33.
        if (v >= 10 * (1 - 1e-9) && Near(v, Math.Round(v)) && Math.Round(v) <= 99)
            yield return ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture) + ".";
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) <= 1e-6 * Math.Max(1, Math.Abs(b));

    // ── The one-offset guard (R-vl5-1d) ──────────────────────────────────────

    /// <summary>
    /// Decodes every entry of ONE file, then applies the guard: when the decoded letters sit at more than one offset
    /// from the end of their part numbers, none of the file's part-number decodes stands. A tolerance the header
    /// states is never affected. Returns the entries with their tolerance set, and the file's refusal (or null).
    /// </summary>
    public static IReadOnlyList<VendorLibraryEntry> DecodeFile(IReadOnlyList<VendorLibraryEntry> entries, out string? fileRefusal)
    {
        fileRefusal = null;
        var decoded = new List<VendorLibraryEntry>(entries.Count);
        foreach (var e in entries)
        {
            var t = Of(e.Header, e.PartNumber, out string? note);
            decoded.Add(e with { Tolerance = t, ToleranceNote = note });
        }

        var offsets = decoded.Where(e => e.Tolerance is { Source: VendorToleranceSource.PartNumber })
                             .Select(e => e.Tolerance!.Offset).Distinct().Order().ToList();
        if (offsets.Count <= 1) return decoded;

        fileRefusal = $"its tolerance letters sit at {offsets.Count} different positions from the end of the part numbers " +
                      $"({string.Join(", ", offsets)}), so none is trusted";
        const string guarded = "its file's tolerance letters sit at more than one position, so none is trusted";
        for (int i = 0; i < decoded.Count; i++)
            if (decoded[i].Tolerance is { Source: VendorToleranceSource.PartNumber })
                decoded[i] = decoded[i] with { Tolerance = null, ToleranceNote = guarded };
        return decoded;
    }

    // ── A tolerance the header states (R-vl5-1e) ─────────────────────────────

    private static readonly Regex Stated = new(
        @"(?:±|\+/-|\+-)\s*(\d+(?:\.\d*)?|\.\d+)\s*\[?\s*(%|[pnuµμ]?[FH])\s*\]?",
        RegexOptions.CultureInvariant);

    private static readonly Regex DescriptionTolerance = new(
        @"\btol(?:erance)?\s*[:=]?\s*(?=±|\+/-|\+-)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// A tolerance a Description states, NAMED as one — <c>Tolerance=±5%</c>, <c>Tol ±0.1pF</c> — or null. A bare ±
    /// in a Description is not taken: a dielectric's temperature characteristic is written the same way.
    /// </summary>
    internal static VendorPartTolerance? ParseStatedInDescription(string? description, VendorPartQuantity quantity)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;
        var m = DescriptionTolerance.Match(description);
        return m.Success ? ParseStated(description[(m.Index + m.Length)..], quantity) : null;
    }

    /// <summary>
    /// A tolerance written in header text — <c>±5%</c>, <c>±0.1pF</c>, <c>+/-0.1[pF]</c> — or null. A percent is taken
    /// as written; an absolute value must be in the quantity's own unit.
    /// </summary>
    internal static VendorPartTolerance? ParseStated(string? text, VendorPartQuantity quantity)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Stated.Match(text);
        if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) || n <= 0)
            return null;
        string unit = m.Groups[2].Value;
        if (unit == "%") return new VendorPartTolerance(n, VendorToleranceKind.Percent, VendorToleranceSource.Header, quantity);

        char baseUnit = unit[^1];
        if ((baseUnit == 'F') != (quantity == VendorPartQuantity.Capacitance)) return null;
        double scale = unit.Length == 1 ? 1 : unit[0] switch { 'p' => 1e-12, 'n' => 1e-9, _ => 1e-6 };
        double si = double.Parse($"{m.Groups[1].Value}e{(int)Math.Round(Math.Log10(scale))}", CultureInfo.InvariantCulture);
        return new VendorPartTolerance(si, VendorToleranceKind.Absolute, VendorToleranceSource.Header, quantity);
    }
}
