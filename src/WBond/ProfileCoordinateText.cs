using System.Globalization;
using System.Text;

namespace CircuitRF.WBond;

/// <summary>
/// The tab-delimited spreadsheet exchange for a loop shape — the one codec behind the profile view's
/// <b>Copy Coordinates</b> and <b>Paste</b> (wbond.md §6.4a).
///
/// <para><b>A one-shot TRANSFER, which is why it survived the removal of loop profiles</b>
/// (2026-08-18). What the owner rejected was a persistent shared object several arrays follow; this
/// is a copy the user asks for by name, reads out of one array and stamps onto another, and links
/// nothing to anything afterwards.</para>
///
/// <para><b>The columns are (span fraction, height in a real unit), and that asymmetry is the whole
/// point.</b> What travels is a normalised <see cref="ShapePoint"/> list plus one loop height, read
/// off a wire with <see cref="LoopShape.Read"/>. Writing an ABSOLUTE span would bake one wire's own
/// chord length into a shape meant to be transferable — paste it onto a group whose wires are a
/// different length and the numbers would mean something else. Span therefore travels as the fraction
/// it actually is; height travels as a real length because that is the number an engineer is
/// reasoning about, and it is what carries the loop height back.</para>
///
/// <para><b>Height is written in the caller's own display unit, stated in the header.</b> A user
/// pasting into Excel reads numbers in the unit they were already looking at, and the header carries
/// it back so a round trip is exact regardless of what the destination document displays in.</para>
///
/// <para><b>Parsing is forgiving in every direction that cannot change the answer</b> — a missing
/// header (height falls back to the caller's unit), blank lines, tab or comma delimiters, CRLF or LF,
/// a column-name row, and a leading index column that spreadsheet users routinely paste back. It is
/// strict about the one thing that can: a row carrying exactly one number is ambiguous about which
/// column it is, so it fails the whole paste rather than silently contributing a zero.</para>
/// </summary>
public static class ProfileCoordinateText
{
    private const string HeaderPrefix = "# wBond profile";

    /// <summary>
    /// A shape as tab-delimited text with a header, ready to paste into a spreadsheet.
    /// </summary>
    public static string Write(IReadOnlyList<ShapePoint> shape, long loopHeightNm, WBondUnit unit)
    {
        ArgumentNullException.ThrowIfNull(shape);

        var sb = new StringBuilder();
        sb.Append(HeaderPrefix).Append(" — height in ").Append(WBondUnits.Suffix(unit)).Append('\n');
        sb.Append("span\theight (").Append(WBondUnits.Suffix(unit)).Append(")\n");

        foreach (var p in shape)
        {
            double heightNm = p.Height * loopHeightNm;
            sb.Append(Num(p.Span)).Append('\t')
              .Append(Num(WBondUnits.FromNm((long)Math.Round(heightNm), unit))).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Reads a shape back.
    /// <paramref name="fallbackUnit"/> applies only when the text carries no unit header of its own.
    ///
    /// <para>The loop height is recovered as the TALLEST row, and every height is renormalised
    /// against it — which is what makes a copy-then-paste onto a different group carry the shape AND
    /// its height rather than only one of them.</para>
    /// </summary>
    /// <returns>False when the text is not a shape at all; this is what greys out Paste.</returns>
    public static bool TryRead(string? text, WBondUnit fallbackUnit,
                               out IReadOnlyList<ShapePoint> shape, out long loopHeightNm)
    {
        shape = [];
        loopHeightNm = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var unit = fallbackUnit;
        var spans = new List<double>();
        var heightsNm = new List<double>();

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim('\r', ' ', '\t');
            if (line.Length == 0) continue;

            if (line.StartsWith('#'))
            {
                TryReadUnitHeader(line, ref unit);
                continue;
            }

            string[] fields = line.Split(['\t', ','], StringSplitOptions.None);

            var nums = new List<double>(fields.Length);
            foreach (string f in fields)
            {
                string t = f.Trim();
                if (t.Length == 0) continue;
                if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                    && double.IsFinite(v))
                    nums.Add(v);
            }

            // A column-name row ("span   height (mil)") contributes no numbers — skip, don't fail.
            if (nums.Count == 0) continue;

            // One lone number cannot say which column it is. Refuse rather than guess.
            if (nums.Count < 2) return false;

            // The LAST two numbers are span and height, so a leading index column is simply ignored.
            spans.Add(nums[^2]);
            heightsNm.Add(WBondUnits.ToNm(nums[^1], unit));
        }

        // A shape needs at least a start and an end.
        if (spans.Count < 2) return false;

        double peakNm = heightsNm.Max();
        if (!(peakNm > 0)) return false;   // a flat "shape" is not a loop

        double spanMax = spans.Max();
        if (!(spanMax > 0)) return false;

        // Renormalise both axes. Accepting an absolute span as well as a 0..1 one costs nothing and
        // means a user who typed millimetres into the span column still gets the shape they drew.
        double spanScale = spanMax > 1.0 ? 1.0 / spanMax : 1.0;

        var points = new List<ShapePoint>(spans.Count);
        for (int i = 0; i < spans.Count; i++)
        {
            points.Add(new ShapePoint(
                Math.Clamp(spans[i] * spanScale, 0.0, 1.0),
                Math.Clamp(heightsNm[i] / peakNm, 0.0, 1.0)));
        }

        shape = points;
        loopHeightNm = (long)Math.Round(peakNm);
        return true;
    }

    /// <summary>True when the clipboard holds something this can read — drives Paste's enabled state.</summary>
    public static bool CanRead(string? text, WBondUnit fallbackUnit) =>
        TryRead(text, fallbackUnit, out _, out _);

    private static void TryReadUnitHeader(string line, ref WBondUnit unit)
    {
        // "# wBond profile — height in mil": the unit is the last whitespace-separated token.
        string[] parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;

        if (WBondUnits.TryParseUnit(parts[^1], out var parsed))
            unit = parsed;
    }

    /// <summary>Round-trip-exact, culture-invariant, and without a trailing pile of zeros.</summary>
    private static string Num(double v) => v.ToString("0.##########", CultureInfo.InvariantCulture);
}
