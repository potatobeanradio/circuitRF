// What a word on a schematic picture may say — brief-img-9-text-and-values.md R-im9-3.
//
// A word is accepted only as one of four things: a designator, a value, a set of line parameters, or a net or port name.
// Anything else is unread. The grammar is what turns the glyph matcher's near-ties into a reading: "l0pF" has no reading
// but as a value, so its first glyph is a 1.
//
// A VALUE IS READ BY THE BILL-OF-MATERIALS READER, never a second way: BomTablePaste.TryReadValueOfAnyKind, which is
// TryReadValue tried for each kind that could supply a unit left off. "10p" is then 10 pico-something, a capacitance or
// an inductance, and the part it lands beside says which (LabelAssociation).

using System.Globalization;
using System.Text.RegularExpressions;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout.Recognition.Silkscreen;

namespace CircuitRF.Design.Schematic.Recognition;

/// <summary>What a word was read as (R-im9-3).</summary>
public enum WordClass
{
    /// <summary>No grammatical reading: the word keeps its picture box.</summary>
    Unread,
    Designator,
    Value,
    LineParameters,
    Name,
}

/// <summary>A value: the number in base SI and every dimension it could be.</summary>
/// <param name="Dimensions">One when the word states its unit; the kinds that could supply it when it does not.</param>
/// <param name="NotFitted">DNP, NC and the like: the part is not fitted, and <paramref name="Si"/> is 0.</param>
public sealed record WordValue(double Si, IReadOnlyList<UnitDimension> Dimensions, bool NotFitted)
{
    /// <summary>Whether a part whose value is in <paramref name="dim"/> may take this value.</summary>
    public bool Fits(UnitDimension dim) => NotFitted || Dimensions.Contains(dim);
}

/// <summary>One parameter of a line set.</summary>
/// <param name="Key">As written: Z, Z0, E, θ, L, W or F.</param>
/// <param name="Si">Base SI: ohms, degrees, metres, hertz.</param>
public sealed record LineParameter(string Key, double Si, UnitDimension Dimension);

/// <summary>One grammatical reading of a word.</summary>
/// <param name="IsPortWord">A name — or a designator — that a port is called by (RFIN, OUT, P1, J1 …).</param>
public sealed record WordReading(string Text, WordClass Class, WordValue? Value = null,
                                 IReadOnlyList<LineParameter>? Parameters = null, bool IsPortWord = false)
{
    /// <summary>How sure a class is, best first, for choosing among a word's readings within the margin: a designator,
    /// then a port word, a value, a line set, and any other name last — a name is what nearly any letters spell, so it
    /// is the reading of last resort.</summary>
    public int Rank => Class switch
    {
        WordClass.Designator => 0,
        WordClass.Name when IsPortWord => 1,
        WordClass.Value => 2,
        WordClass.LineParameters => 3,
        WordClass.Name => 4,
        _ => 5,
    };
}

public static class ValueGrammar
{
    private static readonly Regex DesignatorShape = new("^[A-Z]{1,3}[1-9][0-9]{0,3}$", RegexOptions.CultureInvariant);
    // Two characters at least: a lone glyph gives the grammar nothing to check, and a stray mark is always near SOME
    // letter. No port word is one character.
    private static readonly Regex NameShape = new("^[A-Za-z][A-Za-z0-9_+-]{1,15}$", RegexOptions.CultureInvariant);
    private static readonly Regex PortShape = new("^(RF_?IN|RF_?OUT|IN|OUT|INPUT|OUTPUT|P[1-9][0-9]?|PORT_?[1-9][0-9]?|J[1-9][0-9]?)$",
                                                  RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Number = new(@"^[+-]?(\d+(\.\d*)?|\.\d+)$", RegexOptions.CultureInvariant);

    /// <summary>The word's one reading under the grammar, or null: a designator, a value, a line set or a name, in
    /// that order where a string is more than one.</summary>
    public static WordReading? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string t = text.Trim();
        bool port = PortShape.IsMatch(t);
        if (DesignatorShape.IsMatch(t)) return new WordReading(t, WordClass.Designator, IsPortWord: port);
        if (ReadValue(t) is { } v) return new WordReading(t, WordClass.Value, v);
        if (ReadLineParameters(t) is { } ps) return new WordReading(t, WordClass.LineParameters, Parameters: ps);
        if (NameShape.IsMatch(t)) return new WordReading(t, WordClass.Name, IsPortWord: port);
        return null;
    }

    /// <summary>Whether a designator's letters are a prefix designators are known to be written with (AS-10's list).</summary>
    public static bool KnownPrefix(string designator) =>
        StrokeGlyphs.KnownPrefixes.Contains(new string([.. designator.TakeWhile(char.IsAsciiLetter)]));

    /// <summary>A value in any spelling a schematic writes — through the bill-of-materials reader — or DNP/NC.</summary>
    public static WordValue? ReadValue(string text)
    {
        if (BomTablePaste.IsNotFittedMarker(text) && !text.Any(ch => ch is '-' or '–' or '—'))
            return new WordValue(0, [], NotFitted: true);
        return BomTablePaste.TryReadValueOfAnyKind(text, out double si, out var dims) ? new WordValue(si, dims, false) : null;
    }

    // ── line parameters ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A set of line parameters — <c>Z=50Ω</c>, <c>Z0=50</c>, <c>E=90°</c>, <c>θ=45°</c>, <c>L=3.2mm</c>,
    /// <c>W=0.3mm</c>, <c>F=2GHz</c> — separated by commas or spaces; null unless every piece is one, each key once.
    /// </summary>
    public static IReadOnlyList<LineParameter>? ReadLineParameters(string text)
    {
        var pieces = text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries);
        if (pieces.Length == 0) return null;
        var result = new List<LineParameter>();
        foreach (string piece in pieces)
        {
            int eq = piece.IndexOf('=');
            if (eq <= 0 || eq == piece.Length - 1) return null;
            string key = piece[..eq], value = piece[(eq + 1)..];
            if (result.Any(p => p.Key == key)) return null;
            LineParameter? p = key switch
            {
                "Z" or "Z0" => Impedance(key, value),
                "E" or "θ" => Angle(key, value),
                "L" or "W" => Length(key, value),
                "F" => Frequency(key, value),
                _ => null,
            };
            if (p is null) return null;
            result.Add(p);
        }
        return result;
    }

    /// <summary>A bare number or ohms written out (50Ω, 1kΩ) — never a bare multiplier, which a resistor's value may
    /// leave its Ω off but an impedance does not (Z=50G is a misread Ω, not fifty gigohms), and nothing past a kΩ (Z=5GΩ
    /// is a misread 0).</summary>
    private static LineParameter? Impedance(string key, string v)
    {
        if (Plain(v) is { } n) return new(key, n, UnitDimension.Resistance);
        return BomTablePaste.TryReadValue(v, null, false, out double si, out var dim) && dim == UnitDimension.Resistance
               && si < 1e6 ? new(key, si, dim) : null;
    }

    private static LineParameter? Angle(string key, string v)
    {
        string t = v.EndsWith('°') ? v[..^1] : v.EndsWith("deg", StringComparison.Ordinal) ? v[..^3] : v;
        return Plain(t) is { } n ? new(key, n, UnitDimension.Angle) : null;
    }

    private static readonly (string Unit, double Scale)[] Lengths =
    [
        ("mm", 1e-3), ("µm", 1e-6), ("μm", 1e-6), ("um", 1e-6), ("nm", 1e-9), ("cm", 1e-2), ("mil", 25.4e-6),
        ("in", 25.4e-3), ("m", 1),
    ];

    private static readonly (string Unit, double Scale)[] Frequencies =
        [("GHz", 1e9), ("MHz", 1e6), ("kHz", 1e3), ("THz", 1e12), ("Hz", 1)];

    private static LineParameter? Length(string key, string v) => Scaled(key, v, Lengths, UnitDimension.Length);

    private static LineParameter? Frequency(string key, string v) => Scaled(key, v, Frequencies, UnitDimension.Frequency);

    private static LineParameter? Scaled(string key, string v, (string Unit, double Scale)[] units, UnitDimension dim)
    {
        foreach (var (unit, scale) in units)
            if (v.EndsWith(unit, StringComparison.Ordinal) && Plain(v[..^unit.Length].TrimEnd()) is { } n)
                return new(key, n * scale, dim);
        return null;
    }

    private static double? Plain(string s) =>
        Number.IsMatch(s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) ? n : null;
}
