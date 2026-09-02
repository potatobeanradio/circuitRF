// ================================================================
//  ComplexStringHelper.cs  —  parse and format Complex numbers from/to strings
//
//  Supported input forms (after normalising whitespace, *, and i→j):
//    Pure real:        "50", "-3.14", "1e9"
//    Pure imaginary:   "j5", "-j5", "5j", "-5j", "j", "-j"
//    Complex a±jb:     "5+j2", "5-j2", "-5+j2"
//    Complex a±bj:     "5+2j", "5-2j", "5+2.5j"
// ================================================================

using System;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

internal static class ComplexStringHelper
{
    // Signed number  (allows leading sign, decimal, scientific notation)
    private const string Num  = @"[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?";
    // Unsigned number (no leading sign)
    private const string UNum = @"(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?";

    private static readonly Regex _pureReal = new($"^({Num})$",                          RegexOptions.Compiled);
    private static readonly Regex _pureImJ  = new($"^([+-]?)j({UNum})?$",               RegexOptions.Compiled);
    private static readonly Regex _pureImJt = new($"^({Num})j$",                        RegexOptions.Compiled);
    private static readonly Regex _formAJB  = new($"^({Num})([+-])j({UNum})?$",         RegexOptions.Compiled);
    private static readonly Regex _formABJ  = new($"^({Num})([+-])({UNum})j$",          RegexOptions.Compiled);

    /// <summary>
    /// Attempts to parse <paramref name="raw"/> as a complex number.
    /// Returns true and sets <paramref name="result"/> on success.
    /// </summary>
    internal static bool TryParse(string? raw, out Complex result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        // Normalise: remove spaces and *, lowercase, map i → j
        string s = Regex.Replace(raw.Trim(), @"\s+", "")
                        .Replace("*", "")
                        .ToLowerInvariant()
                        .Replace("i", "j");

        if (s.Length == 0) return false;

        // Pure real: "50", "-3.14"
        var m = _pureReal.Match(s);
        if (m.Success && D(m.Groups[1].Value, out double rv))
        { result = new Complex(rv, 0); return true; }

        // Pure imaginary (j-first): "j5", "-j5", "j" → 1j
        m = _pureImJ.Match(s);
        if (m.Success)
        {
            double sign = m.Groups[1].Value == "-" ? -1 : 1;
            double mag  = m.Groups[2].Success ? double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 1;
            result = new Complex(0, sign * mag);
            return true;
        }

        // Pure imaginary (j-last): "5j", "-5j"
        m = _pureImJt.Match(s);
        if (m.Success && D(m.Groups[1].Value, out double iv))
        { result = new Complex(0, iv); return true; }

        // Complex a±jb: "5+j2", "-5-j2.5", "5+j" → 5+1j
        m = _formAJB.Match(s);
        if (m.Success && D(m.Groups[1].Value, out double re1))
        {
            double isign = m.Groups[2].Value == "-" ? -1 : 1;
            double imag  = m.Groups[3].Success
                ? double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 1;
            result = new Complex(re1, isign * imag);
            return true;
        }

        // Complex a±bj: "5+2j", "-5-2.5j"
        m = _formABJ.Match(s);
        if (m.Success && D(m.Groups[1].Value, out double re2))
        {
            double isign = m.Groups[2].Value == "-" ? -1 : 1;
            double imag  = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            result = new Complex(re2, isign * imag);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Formats a complex number as a short string suitable for display and round-trip parsing.
    ///
    /// <para><b>Every part is InvariantCulture, and it has to be:</b> <see cref="TryParse"/>'s
    /// grammar admits <c>'.'</c> as the decimal separator and nothing else, so this is one half of
    /// a round trip rather than a display formatter. The imaginary branch used to format its two
    /// components with the ambient culture while the real branch was already invariant — on a
    /// comma-decimal machine that wrote <c>50,5+j10,2</c> into the Z0 box and then rejected it as
    /// "Invalid Z0", a value the application itself had produced.</para>
    /// </summary>
    internal static string Format(Complex z, string fmt = "G6")
    {
        if (z.Imaginary == 0) return z.Real.ToString(fmt, CultureInfo.InvariantCulture);
        string sign = z.Imaginary >= 0 ? "+" : "-";
        return $"{z.Real.ToString(fmt, CultureInfo.InvariantCulture)}{sign}j" +
               $"{Math.Abs(z.Imaginary).ToString(fmt, CultureInfo.InvariantCulture)}";
    }

    private static bool D(string s, out double v) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
}
