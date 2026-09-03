// The declared coordinate format on IMPORT (docs/sonnet-briefs/brief-L4e-gerber-import-reader.md
// R-L4e-1/R-L4e-2). Export's own GerberFormat picks a format that makes 1 DBU == 1 output unit by
// construction; import gets whatever the file declares and must honour it exactly — a file's %FS is
// never assumed, and inch is at least as common in circulation as millimetre.
//
// R-L4e-2 in one line: the output-unit -> DBU mapping is the exact rational
// (DBU per file unit) / 10^DecimalDigits, reduced. Where that reduces to an integer (every mm format
// with <= 6 decimals, and inch at 4 or 5 decimals at the default DbuPerMicron=1000) the conversion is
// an integer multiply with no double anywhere. Where it does not (inch at 6 decimals: 25.4 DBU per
// unit) it is an integer multiply-then-round — and NEVER a cast. `(long)(x * s)` truncates toward
// zero, which is wrong only for NEGATIVE coordinates and therefore survives any fixture drawn in the
// first quadrant; board coordinates commonly go negative because the origin sits at the board centre
// (L4d's R-L4d-2, the identical trap).
//
// Export refuses rather than silently rounding (L4c R-L4c-1). IMPORT INVERTS THAT: it rounds and
// reports the worst-case error as a number, because refusing to read a file the user already has
// leaves them no path at all.

using System.Globalization;

namespace CircuitRF.Ui.Layout.Interchange;

/// <summary>Which zero the file omits — the first letter of <c>%FS</c>. <c>Leading</c> means a
/// coordinate's value is its plain integer; <c>Trailing</c> means it must be padded on the RIGHT to
/// the declared digit count before it means anything.</summary>
public enum GerberZeroOmission { Leading, Trailing }

/// <summary>Absolute (<c>%FS…A…</c> / <c>G90</c>) or incremental (<c>%FS…I…</c> / <c>G91</c>)
/// coordinates. R-L4e-3: incremental is legal, rare, and silently catastrophic if read as absolute,
/// so it is supported here rather than guessed at.</summary>
public enum GerberNotation { Absolute, Incremental }

public enum GerberUnit { Millimetres, Inches }

/// <summary>The resolved <c>%FS</c> + <c>%MO</c> pair, plus the exact rational that turns one output
/// unit into DBU.</summary>
public sealed class GerberCoordinateFormat
{
    public GerberCoordinateFormat(GerberUnit unit, int integerDigits, int decimalDigits,
        GerberZeroOmission zeroOmission, GerberNotation notation, int dbuPerMicron)
    {
        Unit = unit;
        IntegerDigits = integerDigits;
        DecimalDigits = decimalDigits;
        ZeroOmission = zeroOmission;
        Notation = notation;
        DbuPerMicron = dbuPerMicron;

        // DBU in one whole file unit: 1 mm = 1000 micron, 1 inch = 25400 micron. Both exact longs.
        DbuPerFileUnit = (unit == GerberUnit.Inches ? 25_400L : 1_000L) * dbuPerMicron;

        long den = 1;
        for (int i = 0; i < decimalDigits; i++) den *= 10;
        long g = Gcd(DbuPerFileUnit, den);
        ScaleNumerator = DbuPerFileUnit / g;
        ScaleDenominator = den / g;
    }

    public GerberUnit Unit { get; }
    public int IntegerDigits { get; }
    public int DecimalDigits { get; }
    public GerberZeroOmission ZeroOmission { get; }
    public GerberNotation Notation { get; }
    public int DbuPerMicron { get; }

    /// <summary>DBU in one whole declared unit (one millimetre, or one inch) — the scale aperture
    /// modifiers, <c>%SR</c> steps and <c>%OF</c> offsets are written in (those are plain decimal
    /// numbers, NOT coordinate-format integers).</summary>
    public long DbuPerFileUnit { get; }

    /// <summary>The reduced rational DBU-per-output-unit. <c>ScaleDenominator == 1</c> is the exact
    /// case (R-L4e-2's table).</summary>
    public long ScaleNumerator { get; }
    public long ScaleDenominator { get; }

    public bool IsExact => ScaleDenominator == 1;

    /// <summary>R-L4e-2's "report the worst-case error once, as a number": half a DBU whenever the
    /// mapping is inexact (round-to-nearest can be off by at most half the output grid, and the output
    /// grid IS one DBU), and exactly zero when it is not.</summary>
    public double WorstCaseRoundingErrorDbu => IsExact ? 0.0 : 0.5;

    /// <summary>One coordinate-format integer -> DBU. Integer multiply where exact; multiply and
    /// round-half-away-from-zero where not. Never a cast (see the file header).</summary>
    public long ToDbu(long rawValue) =>
        ScaleDenominator == 1 ? rawValue * ScaleNumerator : MulDivRound(rawValue, ScaleNumerator, ScaleDenominator);

    /// <summary>A plain decimal number written in the file's own unit (an aperture diameter, a
    /// <c>%SR</c> step, a <c>%OF</c> offset) -> DBU. Parsed as an exact scaled integer rather than
    /// through <c>double</c>, so "0.150" is 150/1000 of a unit exactly and not 0.1499999….</summary>
    public long DecimalToDbu(string text) => DecimalToDbu(text, out _);

    /// <summary>The same conversion, reporting whether it was EXACT — i.e. whether the decimal landed
    /// on a whole DBU with nothing rounded away. L4f's R-L4f-4 needs this per tool diameter: an inch
    /// diameter at 5 decimals and a millimetre one at 6 are exact and a drill file's tool table is
    /// where a user notices if they are not.</summary>
    public long DecimalToDbu(string text, out bool exact)
    {
        exact = true;
        text = text.Trim();
        if (text.Length == 0) return 0;

        bool negative = text[0] == '-';
        if (text[0] is '+' or '-') text = text[1..];

        int dot = text.IndexOf('.');
        string digits = dot < 0 ? text : text.Remove(dot, 1);
        int decimals = dot < 0 ? 0 : text.Length - dot - 1;
        if (digits.Length == 0) return 0;
        if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out long mantissa))
            return 0;

        long den = 1;
        for (int i = 0; i < decimals; i++) den *= 10;
        exact = den == 1 || (mantissa * DbuPerFileUnit) % den == 0;
        long dbu = den == 1 ? mantissa * DbuPerFileUnit : MulDivRound(mantissa, DbuPerFileUnit, den);
        return negative ? -dbu : dbu;
    }

    /// <summary>A macro-produced length (already a <c>double</c>, because the macro arithmetic itself
    /// is floating point) -> DBU. Rounded, never cast.</summary>
    public long ValueToDbu(double value) => (long)Math.Round(value * DbuPerFileUnit, MidpointRounding.AwayFromZero);

    /// <summary>Turns one coordinate word's raw digit text into its coordinate-format integer,
    /// applying the declared zero omission. With leading zeros omitted the text IS the integer; with
    /// TRAILING zeros omitted it must first be padded on the right to <c>IntegerDigits +
    /// DecimalDigits</c> characters, which is the whole reason the digit counts are parsed at all.</summary>
    public long ParseCoordinateWord(string word)
    {
        bool negative = false;
        int i = 0;
        if (i < word.Length && (word[i] == '+' || word[i] == '-')) { negative = word[i] == '-'; i++; }
        string digits = word[i..];
        if (digits.Length == 0) return 0;

        long value = long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out long v) ? v : 0;
        if (ZeroOmission == GerberZeroOmission.Trailing)
        {
            int total = IntegerDigits + DecimalDigits;
            for (int pad = digits.Length; pad < total; pad++) value *= 10;
        }
        return negative ? -value : value;
    }

    private static long MulDivRound(long value, long numerator, long denominator)
    {
        long product = value * numerator;
        long q = product / denominator;
        long r = product % denominator;
        if (r == 0) return q;
        return Math.Abs(r) * 2 >= denominator ? q + Math.Sign(product) : q;
    }

    private static long Gcd(long a, long b)
    {
        a = Math.Abs(a); b = Math.Abs(b);
        while (b != 0) (a, b) = (b, a % b);
        return a == 0 ? 1 : a;
    }
}
