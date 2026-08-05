using System.Globalization;

namespace CircuitRF.Core.Netlist.Spice;

/// <summary>
/// Numeric literals in the SPICE dialect, resolved to plain numbers.
///
/// <para><b>Why this cannot be left to circuitRF's own unit table, and why that is the load-bearing
/// decision in this reader.</b> The two scales disagree on the single most common suffix in a model
/// card. circuitRF's table is SI and case-sensitive: <c>M</c> is mega and <c>m</c> is milli. This
/// dialect is case-INsensitive and <c>M</c> is milli in either case, with mega spelled <c>MEG</c>.
/// So a capacitance written <c>1M</c> — meaning one millifarad — read through the SI table is one
/// megafarad: a factor of 10⁹, in a value that still parses, still stamps, and still converges.
/// Every literal is therefore resolved HERE and handed on as a plain decimal string, so the SI table
/// is never consulted for a suffix that came out of this dialect.</para>
///
/// <para><b>Trailing text after the suffix is ignored, deliberately.</b> <c>1kohm</c> is 1000 and
/// <c>2.5pF</c> is 2.5e-12 — that is the dialect's own rule, not a leniency added here. It has one
/// sharp edge worth knowing rather than smoothing over: <c>1F</c> is one FEMTO-unit, not one farad,
/// because <c>F</c> is the femto prefix and what follows a recognised prefix is decoration. A reader
/// that "fixed" that would disagree with every file it is meant to read.</para>
/// </summary>
public static class SpiceNumber
{
    /// <summary>
    /// Prefixes, longest first — <b>the order is the correctness condition, not a tidiness one</b>.
    /// <c>MEG</c> and <c>MIL</c> both begin with <c>M</c>, so matching a single character first
    /// reads a megohm as a milliohm and keeps going.
    /// </summary>
    /// <summary>
    /// <paramref name="Exponent"/> is the power of ten, applied by RE-READING the literal with that
    /// exponent appended rather than by multiplying. The two differ: <c>3.0 * 1e-9</c> is
    /// 3.0000000000000004e-9, while <c>3e-9</c> is the nearest double to the value the file actually
    /// wrote. The multiplied form is not wrong enough to matter numerically and is wrong enough to
    /// be read back out as noise, which is what a user sees.
    ///
    /// <para><see cref="NoExponent"/> marks a prefix that is not a power of ten and must be
    /// multiplied.</para>
    /// </summary>
    private const int NoExponent = int.MinValue;

    private static readonly (string Suffix, int Exponent, double Scale)[] Prefixes =
    [
        ("MEG", 6,          1e6),
        ("MIL", NoExponent, 25.4e-6),
        ("T",   12,         1e12),
        ("G",   9,          1e9),
        ("K",   3,          1e3),
        ("M",   -3,         1e-3),
        ("U",   -6,         1e-6),
        ("N",   -9,         1e-9),
        ("P",   -12,        1e-12),
        ("F",   -15,        1e-15),
    ];

    // `A` for atto is deliberately absent. No model card in this dialect uses it, while a bare `A`
    // meaning amperes is entirely plausible — so recognising it can only ever turn a value into
    // 1e-18 times itself, silently, and can never read a file that would otherwise fail.

    /// <summary>
    /// Reads a literal, returning false for anything that is not one — an identifier, an expression,
    /// a net name. Callers use that answer to tell a component's VALUE from the name of a model card,
    /// so "is this a number" has to be decided here and nowhere else.
    /// </summary>
    public static bool TryParse(string token, out double value)
    {
        value = 0.0;
        if (string.IsNullOrEmpty(token)) return false;

        int end = ScanNumeric(token, 0, out bool hasExponent);
        if (end == 0) return false;

        ScanPrefixAndTrailer(token, end, hasExponent, out double scale, out int exponent);
        return TryCompose(token.AsSpan(0, end), scale, exponent, out value);
    }

    /// <summary>
    /// Applies the prefix. A power-of-ten prefix is applied by re-reading the literal with the
    /// exponent appended, which lands on the same double the file would have got by writing the
    /// exponent itself; anything else is multiplied.
    /// </summary>
    private static bool TryCompose(ReadOnlySpan<char> numeric, double scale, int exponent, out double value)
    {
        if (exponent != NoExponent && exponent != 0)
            return double.TryParse($"{numeric}E{exponent.ToString(CultureInfo.InvariantCulture)}",
                                   NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        if (!double.TryParse(numeric, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return false;

        value *= scale;
        return true;
    }

    /// <summary>
    /// The literal as circuitRF spells it: an ordinary decimal with the prefix already applied.
    /// <c>"R"</c> round-trips the double exactly, which matters because this string is what the
    /// expression engine will re-parse.
    /// </summary>
    public static string Normalise(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>
    /// Rewrites every literal inside an expression, leaving everything else alone.
    ///
    /// <para>A literal is only recognised where one can legally START — after an operator, a comma,
    /// an opening bracket, or at the beginning. Without that rule the <c>1</c> in <c>r1</c> would be
    /// read as a number and the identifier would come apart.</para>
    /// </summary>
    public static string NormaliseLiterals(string expr)
    {
        var sb = new System.Text.StringBuilder(expr.Length);
        int i = 0;
        bool valuePosition = true;             // true where a literal may begin

        while (i < expr.Length)
        {
            char c = expr[i];

            if (valuePosition && (char.IsAsciiDigit(c) || (c == '.' && i + 1 < expr.Length && char.IsAsciiDigit(expr[i + 1]))))
            {
                int end = ScanNumeric(expr, i, out bool hasExponent);
                int after = ScanPrefixAndTrailer(expr, end, hasExponent, out double scale, out int exponent);

                if (end > i && TryCompose(expr.AsSpan(i, end - i), scale, exponent, out double resolved))
                {
                    sb.Append(Normalise(resolved));
                    i = after;
                    valuePosition = false;
                    continue;
                }
            }

            sb.Append(c);
            if (!char.IsWhiteSpace(c))
                valuePosition = c is '+' or '-' or '*' or '/' or '^' or '(' or ',' or '<' or '>' or '=' or '!' or '&' or '|' or '?' or ':';
            i++;
        }

        return sb.ToString();
    }

    // ── scanning ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The numeric part alone — digits, one decimal point, and an exponent. The exponent is taken
    /// only when it is followed by digits, so the <c>e</c> of <c>1e</c> stays available to be read as
    /// a suffix letter rather than swallowing the literal.
    /// </summary>
    private static int ScanNumeric(string s, int start, out bool hasExponent)
    {
        hasExponent = false;

        int i = start;
        if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;

        int digits = 0;
        while (i < s.Length && char.IsAsciiDigit(s[i])) { i++; digits++; }
        if (i < s.Length && s[i] == '.')
        {
            i++;
            while (i < s.Length && char.IsAsciiDigit(s[i])) { i++; digits++; }
        }
        if (digits == 0) return start;

        if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
        {
            int j = i + 1;
            if (j < s.Length && (s[j] == '+' || s[j] == '-')) j++;
            if (j < s.Length && char.IsAsciiDigit(s[j]))
            {
                while (j < s.Length && char.IsAsciiDigit(s[j])) j++;
                i = j;
                hasExponent = true;
            }
        }

        return i;
    }

    /// <summary>
    /// Consumes an optional prefix plus whatever decoration follows it, and reports the scale.
    ///
    /// <para>The trailer is <b>letters only</b>. Admitting <c>/</c> so that a unit like <c>F/m</c>
    /// could be swallowed whole would eat the division in <c>1/2</c> and silently produce <c>12</c> —
    /// a wrong number from a valid expression, which is the worst outcome available here.</para>
    ///
    /// <para><b>After an explicit exponent no prefix is applied</b>, so <c>1e-12F</c> is one
    /// picofarad and not 1e-27. The number is already complete at that point and a trailing letter
    /// can only be a unit — nobody writes an exponent and then a prefix meaning to multiply the two.
    /// Read the other way it scales twice, quietly, in the one notation a careful author reaches for
    /// precisely to be unambiguous.</para>
    /// </summary>
    private static int ScanPrefixAndTrailer(
        string s, int start, bool hasExponent, out double scale, out int exponent)
    {
        scale    = 1.0;
        exponent = 0;

        foreach (var (suffix, power, factor) in Prefixes)
        {
            if (hasExponent) break;
            if (start + suffix.Length > s.Length) continue;
            if (!s.AsSpan(start, suffix.Length).Equals(suffix, StringComparison.OrdinalIgnoreCase)) continue;

            scale    = factor;
            exponent = power;
            start   += suffix.Length;
            break;
        }

        int i = start;
        while (i < s.Length && char.IsAsciiLetter(s[i])) i++;
        return i;
    }
}
