using System;
using System.Globalization;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// Shared frequency-unit helpers for SP and HB body VMs.
/// Converts between Hz expression strings and (coefficient, unit) display pairs.
/// </summary>
internal static class FreqUnitHelper
{
    internal static readonly string[] Units = ["Hz", "kHz", "MHz", "GHz"];

    internal static double Multiplier(string unit) => unit switch
    {
        "kHz" => 1e3,
        "MHz" => 1e6,
        "GHz" => 1e9,
        _     => 1.0,
    };

    /// <summary>
    /// Converts a (coefficient string, unit string) pair to a compact Hz expression.
    /// Numeric: "2.4" + "GHz" → "2.4e9". Symbolic: "f0" + "MHz" → "(f0) * 1000000".
    /// </summary>
    internal static string ToHzExpr(string coeff, string unit)
    {
        double m = Multiplier(unit);
        if (m == 1.0) return coeff;

        if (double.TryParse(coeff.Trim(),
                NumberStyles.Float | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out double v))
        {
            string suffix = unit switch { "GHz" => "e9", "MHz" => "e6", "kHz" => "e3", _ => "" };
            string c = v.ToString("G10", CultureInfo.InvariantCulture);
            return $"{c}{suffix}";
        }

        // Symbolic expression — multiply by raw factor
        return $"({coeff}) * {(long)m}";
    }

    /// <summary>
    /// Splits a Hz expression into the best (coefficient, unit) pair for display.
    /// Non-numeric expressions are returned as-is with unit "Hz".
    /// </summary>
    internal static (string Coeff, string Unit) Split(string hzExpr)
    {
        if (double.TryParse(hzExpr.Trim(),
                NumberStyles.Float | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out double hz))
        {
            double abs = Math.Abs(hz);
            if (abs >= 1e9) return (Fmt(hz / 1e9), "GHz");
            if (abs >= 1e6) return (Fmt(hz / 1e6), "MHz");
            if (abs >= 1e3) return (Fmt(hz / 1e3), "kHz");
            return (Fmt(hz), "Hz");
        }
        return (hzExpr.Trim(), "Hz");
    }

    /// <summary>
    /// Rescales a numeric coefficient from one unit to another, keeping Hz value constant.
    /// Returns the original string unchanged when the input is a symbolic expression.
    /// </summary>
    internal static string Rescale(string coeff, string fromUnit, string toUnit)
    {
        if (fromUnit == toUnit) return coeff;
        if (!double.TryParse(coeff.Trim(),
                NumberStyles.Float | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out double v)) return coeff;
        double scale = Multiplier(fromUnit) / Multiplier(toUnit);
        return Fmt(v * scale);
    }

    private static string Fmt(double v)
        => v.ToString("G6", CultureInfo.InvariantCulture);
}
