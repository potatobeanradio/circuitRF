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
    public static string FormatZ(Complex z, ReadoutFormat format) => FormatComplex(z, format) + " Ω";

    public static string FormatGamma(Complex g, ReadoutFormat format) => FormatComplex(g, format);

    public static string FormatComplex(Complex z, ReadoutFormat format)
    {
        if (double.IsNaN(z.Real) || double.IsNaN(z.Imaginary)) return "—";
        if (format == ReadoutFormat.MagnitudeAngle)
            return $"{z.Magnitude:0.###}∠{z.Phase * 180.0 / Math.PI:0.#}°";
        return $"{z.Real:0.###}{(z.Imaginary >= 0 ? "+j" : "-j")}{Math.Abs(z.Imaginary):0.###}";
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
