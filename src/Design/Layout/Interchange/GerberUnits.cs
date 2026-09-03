// Coordinate format resolution (docs/sonnet-briefs/brief-L4c-gerber-export.md R-L4c-1). Gerber's
// number format is declared, not fixed: with %MOMM*% (millimetres) and %FSLAX46Y46*% (4 integer + 6
// decimal digits, leading zeros omitted, absolute), one output unit is 10^-6 mm = 1 nanometre — exactly
// one DBU at the default DbuPerMicron=1000. GerberFormat.DecimalDigits is chosen so a DBU integer maps
// to the output integer by literal copy: no scaling, no rounding, no accumulated error. If DbuPerMicron
// is finer than 1000, the decimal count widens to match rather than rounding a coordinate into a
// declared format that can't hold it exactly — R-L4c-1's explicit "never silently round" rule.

namespace CircuitRF.Design.Layout.Interchange;

/// <summary>The declared %FSLAX..Y..% format — <see cref="IntegerDigits"/> integer digits and
/// <see cref="DecimalDigits"/> decimal digits, millimetres, absolute, leading zeros omitted (the "L"
/// in FSLAX). Chosen so that <c>1 DBU == 1 output integer unit</c> exactly (R-L4c-1).</summary>
public sealed record GerberFormat(int IntegerDigits, int DecimalDigits)
{
    /// <summary>The <c>%FSLAX46Y46*%</c>-style digit pair, e.g. "46" for 4 integer + 6 decimal.</summary>
    public string DigitPair => $"{IntegerDigits}{DecimalDigits}";

    /// <summary>Formats one DBU coordinate as the format's own on-wire integer text (no decimal point,
    /// no scaling — literal copy of the DBU value, per R-L4c-1). Gerber allows an explicit sign and
    /// omits leading zeros; this always emits the plain decimal integer, which satisfies both.</summary>
    public string FormatCoordinate(long dbu) => dbu.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Formats a DBU quantity (an aperture/tool diameter, or an Excellon coordinate) as an
    /// EXPLICIT decimal-point millimetre string at this format's own <see cref="DecimalDigits"/> — pure
    /// integer string manipulation (insert a decimal point <see cref="DecimalDigits"/> digits from the
    /// right), never a <c>double</c> conversion, so it is exact by construction rather than by luck —
    /// the same R-L4c-1 exactness discipline the bare integer coordinates get, just rendered with an
    /// explicit point (Excellon's own classic implied-decimal/zero-suppression convention is a well-known
    /// source of parser ambiguity; an explicit point sidesteps it entirely while remaining exact).</summary>
    public string FormatDecimalMm(long valueDbu)
    {
        bool negative = valueDbu < 0;
        long abs = Math.Abs(valueDbu);
        string digits = abs.ToString(System.Globalization.CultureInfo.InvariantCulture)
            .PadLeft(DecimalDigits + 1, '0');
        string intPart = digits[..^DecimalDigits];
        string fracPart = digits[^DecimalDigits..];
        return (negative ? "-" : "") + intPart + "." + fracPart;
    }

    /// <summary>The largest (positive) coordinate this format's integer-digit count can hold, in DBU —
    /// used to decide whether <see cref="IntegerDigits"/> must widen for a design's actual extent.</summary>
    public long MaxAbsCoordinateDbu(int dbuPerMicron)
    {
        long mmScale = 1000L * dbuPerMicron; // DBU per millimetre
        long limit = 1;
        for (int i = 0; i < IntegerDigits; i++) limit *= 10;
        return limit * mmScale - 1;
    }
}

/// <summary>Thrown when a design's <c>DbuPerMicron</c> cannot be represented exactly in Gerber's decimal
/// millimetre format — R-L4c-1's "never silently round" rule applied as a refusal rather than a
/// best-effort approximation.</summary>
public sealed class GerberUnitsException(string message) : System.Exception(message);

public static class GerberUnits
{
    /// <summary>Four integer digits — "comfortably beyond any board" per the brief (9,999 mm) — widened
    /// automatically by <see cref="Resolve"/> only when a design's own extent actually exceeds it.</summary>
    public const int DefaultIntegerDigits = 4;

    /// <summary>
    /// Resolves the exact <see cref="GerberFormat"/> for <paramref name="dbuPerMicron"/>, widening
    /// <see cref="GerberFormat.IntegerDigits"/> if <paramref name="maxAbsCoordinateDbu"/> (the design's
    /// own largest coordinate magnitude, 0 = unknown/not yet computed) would overflow the default 4
    /// digits. Throws <see cref="GerberUnitsException"/> when <paramref name="dbuPerMicron"/> is not an
    /// exact power of ten — the only case where 1 DBU cannot be mapped to the declared decimal format by
    /// literal integer copy; refusing is the "never silently round" alternative to approximating.
    /// </summary>
    public static GerberFormat Resolve(int dbuPerMicron, long maxAbsCoordinateDbu = 0)
    {
        int decimals = DecimalDigitsFor(dbuPerMicron);
        int integers = DefaultIntegerDigits;

        var format = new GerberFormat(integers, decimals);
        while (maxAbsCoordinateDbu > format.MaxAbsCoordinateDbu(dbuPerMicron))
        {
            integers++;
            format = new GerberFormat(integers, decimals);
        }
        return format;
    }

    /// <summary>R-L4c-1's exact identity: <c>10^-D mm == 1 DBU</c>, i.e. <c>D = 3 + log10(DbuPerMicron)</c>
    /// (3 because 1 micron == 10^-3 mm). Only exact when <paramref name="dbuPerMicron"/> is itself a
    /// power of ten (every technology this codebase ships uses one — default 1000); anything else cannot
    /// be mapped by literal integer copy at all, so this refuses rather than picking an approximate digit
    /// count that would silently lose precision on some coordinates and not others.</summary>
    private static int DecimalDigitsFor(int dbuPerMicron)
    {
        if (dbuPerMicron <= 0)
            throw new GerberUnitsException($"DbuPerMicron must be positive (was {dbuPerMicron}).");

        int log = (int)System.Math.Round(System.Math.Log10(dbuPerMicron));
        long check = 1;
        for (int i = 0; i < log; i++) check *= 10;
        if (check != dbuPerMicron)
            throw new GerberUnitsException(
                $"DbuPerMicron={dbuPerMicron} is not an exact power of ten — Gerber export requires this " +
                "for R-L4c-1's exact-integer-copy coordinate mapping (1 DBU == 1 output unit, no scaling, " +
                "no rounding). Export refused rather than silently approximating coordinates.");
        return 3 + log;
    }
}
