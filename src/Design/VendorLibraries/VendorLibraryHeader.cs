using System.Globalization;
using System.Text.RegularExpressions;
using CircuitRF.Core.Netlist.Spice;

namespace CircuitRF.Design.VendorLibraries;

/// <summary>The physical quantity a part's header says it is, from its <c>Property</c> line.</summary>
public enum VendorPartQuantity
{
    /// <summary>The header states no quantity, or none that parsed.</summary>
    None,
    Capacitance,
    Inductance,
    Resistance,
    /// <summary>A quantity letter other than C, L or R.</summary>
    Other,
}

/// <summary>
/// What a part's comment header says about it — every field optional, none an error when absent
/// (brief vendorlib-1 R-vl1-2a/2b).
///
/// <para><b>Nothing here reads a part NUMBER.</b> Every field comes from the header TEXT; the case-code
/// pair is a Description FIELD that states a size in both systems, not a decoding of the part's
/// name.</para>
/// </summary>
public sealed record VendorLibraryHeader
{
    public static readonly VendorLibraryHeader Empty = new();

    /// <summary>The Description line's text as written, or null when there is none.</summary>
    public string? Description { get; init; }

    /// <summary>The Description with its unit brackets dropped (<c>50[V]</c> → <c>50V</c>) — what a search
    /// token is also matched against, since nobody types the brackets. Null when there is no Description.</summary>
    public string? DescriptionPlain { get; init; }

    /// <summary>The Description split on <c>/</c>, each field trimmed. Empty when there is none.</summary>
    public IReadOnlyList<string> DescriptionFields { get; init; } = [];

    public VendorPartQuantity Quantity { get; init; }

    /// <summary>The Property value in SI (farads, henries, ohms), or null.</summary>
    public double? Value { get; init; }

    /// <summary>The stated frequency range's ends in hertz, or null.</summary>
    public double? FrequencyLowHz  { get; init; }
    public double? FrequencyHighHz { get; init; }

    /// <summary>The temperature line's value as text (e.g. <c>25 degC</c>), or null.</summary>
    public string? Temperature { get; init; }

    /// <summary>The DC-bias line as text, its field name included (e.g. <c>DC Bias Voltage = 0V</c>), or null.</summary>
    public string? Bias { get; init; }

    /// <summary>A stated tolerance line as text (e.g. <c>±5%</c>, <c>10%</c>), or null.</summary>
    public string? Tolerance { get; init; }

    /// <summary>
    /// A case size stated in both systems — a Description field reading <c>&lt;digits&gt;M(&lt;digits&gt;)</c>,
    /// metric first — with both codes exactly as written. Null when no field has that shape.
    /// </summary>
    public string? CaseMetric   { get; init; }
    public string? CaseImperial { get; init; }

    /// <summary>The stated value with an engineering prefix and its unit (<c>10 pF</c>, <c>4.7 nH</c>),
    /// or empty when the header states none. One spelling, for the palette and the CLI alike.</summary>
    public string ValueText()
    {
        if (Value is not { } v) return "";
        string unit = Quantity switch
        {
            VendorPartQuantity.Capacitance => "F",
            VendorPartQuantity.Inductance  => "H",
            VendorPartQuantity.Resistance  => "Ω",
            _                              => "",
        };
        return Engineering(v) + unit;
    }

    /// <summary>The stated frequency range as <c>100 MHz – 30 GHz</c>, or empty.</summary>
    public string FrequencyRangeText()
        => FrequencyLowHz is { } lo && FrequencyHighHz is { } hi ? $"{Engineering(lo)}Hz – {Engineering(hi)}Hz" : "";

    private static string Engineering(double v)
    {
        if (v == 0) return "0 ";
        (double Scale, string Prefix)[] steps =
            [(1e12, "T"), (1e9, "G"), (1e6, "M"), (1e3, "k"), (1, ""), (1e-3, "m"), (1e-6, "µ"), (1e-9, "n"),
             (1e-12, "p"), (1e-15, "f")];
        double a = Math.Abs(v);
        foreach (var (scale, prefix) in steps)
            if (a >= scale * 0.9995)
                return (v / scale).ToString("0.###", CultureInfo.InvariantCulture) + " " + prefix;
        return v.ToString("0.###e+0", CultureInfo.InvariantCulture) + " ";
    }

    // ── Parsing ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the fields this series uses out of a header's comment lines (the leading <c>*</c> already
    /// stripped or not — both are accepted). <paramref name="unparsed"/> counts fields that were
    /// PRESENT but did not parse; they are left empty, never thrown.
    ///
    /// <para><b>Field names match case-insensitively, with any spacing either side of <c>:</c> or
    /// <c>=</c></b>, because different editions of one library lay the same header out differently.
    /// When a field appears twice, the LAST one wins — it is the one nearest the <c>.subckt</c>, and a
    /// file-level preamble that happens to carry a field must not overrule the part's own.</para>
    /// </summary>
    public static VendorLibraryHeader Parse(IEnumerable<string> commentLines, out int unparsed)
    {
        unparsed = 0;
        string? description = null, property = null, range = null, temperature = null, bias = null,
                tolerance = null;

        foreach (string raw in commentLines)
        {
            if (!TrySplitField(raw, out string name, out string value)) continue;

            switch (name)
            {
                case "description":     description = value; break;
                case "property":        property    = value; break;
                case "frequency range": range       = value; break;
                case "temperature":     temperature = value; break;
                case "tolerance":
                case "tol":             tolerance   = value; break;
                default:
                    if (name.StartsWith("dc bias", StringComparison.Ordinal))
                        bias = $"{FieldName(raw)} = {value}";
                    break;
            }
        }

        var quantity = VendorPartQuantity.None;
        double? partValue = null;
        if (property is not null)
        {
            int eq = property.IndexOf('=');
            if (eq > 0 && TryParseQuantity(property[..eq].Trim(), out quantity)
                       && TryParseValue(property[(eq + 1)..], out double v))
                partValue = v;
            else
            {
                quantity = VendorPartQuantity.None;
                unparsed++;
            }
        }

        double? low = null, high = null;
        if (range is not null)
        {
            if (TrySplitRange(range, out string a, out string b)
                && TryParseValue(a, out double lo) && TryParseValue(b, out double hi))
                (low, high) = (lo, hi);
            else
                unparsed++;
        }

        string[] fields = description is null
            ? []
            : [.. description.Split('/').Select(f => f.Trim()).Where(f => f.Length > 0)];

        string? metric = null, imperial = null;
        foreach (string f in fields)
        {
            var m = CasePair.Match(f);
            if (!m.Success) continue;
            (metric, imperial) = (m.Groups[1].Value, m.Groups[2].Value);
            break;
        }

        return new VendorLibraryHeader
        {
            Description       = description,
            DescriptionPlain  = description?.Replace("[", "").Replace("]", ""),
            DescriptionFields = fields,
            Quantity          = quantity,
            Value             = partValue,
            FrequencyLowHz    = low,
            FrequencyHighHz   = high,
            Temperature       = temperature,
            Bias              = bias,
            Tolerance         = tolerance,
            CaseMetric        = metric,
            CaseImperial      = imperial,
        };
    }

    private static readonly Regex CasePair = new(@"^(\d+)M\((\d+)\)$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Splits <c>* Name : value</c> / <c>* Name = value</c> at whichever separator comes FIRST, so a
    /// value holding the other one (<c>Property : C = 10[nH]</c>) keeps it. The name comes back
    /// lower-case with its inner spacing collapsed.
    /// </summary>
    private static bool TrySplitField(string raw, out string name, out string value)
    {
        name = value = "";
        string line = StripComment(raw);
        int colon = line.IndexOf(':'), equals = line.IndexOf('=');
        int sep = colon < 0 ? equals : equals < 0 ? colon : Math.Min(colon, equals);
        if (sep <= 0) return false;

        name  = CollapseSpaces(line[..sep]).ToLowerInvariant();
        value = line[(sep + 1)..].Trim();
        return name.Length > 0 && value.Length > 0;
    }

    private static string FieldName(string raw)
    {
        string line = StripComment(raw);
        int colon = line.IndexOf(':'), equals = line.IndexOf('=');
        int sep = colon < 0 ? equals : equals < 0 ? colon : Math.Min(colon, equals);
        return CollapseSpaces(line[..sep]);
    }

    private static string StripComment(string raw) => raw.TrimStart().TrimStart('*', ';').Trim();

    private static string CollapseSpaces(string s)
        => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool TryParseQuantity(string letter, out VendorPartQuantity quantity)
    {
        quantity = letter.ToUpperInvariant() switch
        {
            "C" => VendorPartQuantity.Capacitance,
            "L" => VendorPartQuantity.Inductance,
            "R" => VendorPartQuantity.Resistance,
            _   => VendorPartQuantity.Other,
        };
        return letter.Length > 0;
    }

    /// <summary>
    /// <c>100[MHz]-30[GHz]</c>, <c>50MHz - 20GHz</c>, <c>1 GHz to 6 GHz</c> → the two ends. The dash is
    /// the first one that is not an exponent's sign.
    /// </summary>
    private static bool TrySplitRange(string range, out string low, out string high)
    {
        low = high = "";
        int to = range.IndexOf(" to ", StringComparison.OrdinalIgnoreCase);
        if (to > 0) { low = range[..to]; high = range[(to + 4)..]; return true; }

        for (int i = 1; i < range.Length; i++)
        {
            if (range[i] is not ('-' or '–' or '~')) continue;
            int p = i - 1;
            while (p >= 0 && range[p] == ' ') p--;
            if (p < 0 || range[p] is 'e' or 'E') continue;
            low = range[..i]; high = range[(i + 1)..];
            return true;
        }
        return false;
    }

    private static readonly Regex NumberThenUnit =
        new(@"^\s*([-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?)\s*\[?\s*([^\]\s]*)\s*\]?\s*$",
            RegexOptions.CultureInvariant);

    /// <summary>
    /// A header value — a number, then an optional unit with an optional prefix, bracketed or not:
    /// <c>1.00e-05[uF]</c>, <c>10[nH]</c>, <c>50MHz</c>, <c>0.1 Ohm</c>.
    ///
    /// <para><b>The number goes through <see cref="SpiceNumber"/>; the unit does not.</b> A header is
    /// prose, not the dialect: <c>MHz</c> there is megahertz, while the dialect reads a leading
    /// <c>M</c> as milli. So the prefix is read on SI's case-sensitive terms, and applied by
    /// re-reading the literal with the exponent appended rather than by multiplying.</para>
    /// </summary>
    internal static bool TryParseValue(string text, out double value)
    {
        value = 0;
        var m = NumberThenUnit.Match(text);
        if (!m.Success || !SpiceNumber.TryParse(m.Groups[1].Value, out double number)) return false;

        string unit = m.Groups[2].Value;
        if (!TryPrefixExponent(unit, out int exponent)) return false;
        if (exponent == 0) { value = number; return true; }

        // Re-read with the exponent folded in, so 4.7 nH is the double the text means rather than
        // 4.7 × 1e-9 (SpiceNumber's own rule, for the same reason).
        string mantissa = m.Groups[1].Value;
        int e = mantissa.IndexOfAny(['e', 'E']);
        int baseExp = e >= 0 ? int.Parse(mantissa[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) : 0;
        string digits = e >= 0 ? mantissa[..e] : mantissa;
        value = double.TryParse($"{digits}e{baseExp + exponent}", NumberStyles.Float,
                                CultureInfo.InvariantCulture, out double reread)
            ? reread
            : number * Math.Pow(10, exponent);
        return true;
    }

    private static readonly string[] BaseUnits = ["Hz", "Ohm", "ohm", "OHM", "Ω", "F", "H", "V", "A"];

    private static bool TryPrefixExponent(string unit, out int exponent)
    {
        exponent = 0;
        if (unit.Length == 0) return true;

        string prefix = unit;
        foreach (string b in BaseUnits)
            if (unit.EndsWith(b, StringComparison.Ordinal)) { prefix = unit[..^b.Length]; break; }

        int? e = prefix switch
        {
            ""                     => 0,
            "T"                    => 12,
            "G"                    => 9,
            "M" or "MEG" or "Meg" or "meg" => 6,
            "k" or "K"             => 3,
            "m"                    => -3,
            "u" or "µ" or "μ"      => -6,
            "n"                    => -9,
            "p"                    => -12,
            "f"                    => -15,
            _                      => null,
        };
        if (e is null) return false;
        exponent = e.Value;
        return true;
    }
}
