using System.Globalization;
using System.Text;

namespace CircuitRF.WBond;

/// <summary>
/// The tab-delimited spreadsheet exchange for a loop profile's own shape — the one codec behind the
/// profile view's <b>Copy Coordinates</b> and <b>Paste</b> (wbond.md §6.4a).
///
/// <para><b>The columns are (span fraction, height in a real unit), and that asymmetry is the whole
/// point.</b> A <see cref="LoopProfile"/> is a SHAPE several wires share, stored as normalised
/// <see cref="ProfilePoint"/>s plus one <see cref="LoopProfile.LoopHeightNm"/>. Writing an ABSOLUTE
/// span would bake one wire's own chord length into a shape meant to be reusable — paste it onto a
/// group whose wires are a different length and the numbers would mean something else. Span
/// therefore travels as the fraction it actually is; height travels as a real length because that is
/// the number an engineer is reasoning about, and it is what carries the loop height back.</para>
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
    /// The profile's shape as tab-delimited text with a header, ready to paste into a spreadsheet.
    /// </summary>
    public static string Write(LoopProfile profile, WBondUnit unit)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var sb = new StringBuilder();
        sb.Append(HeaderPrefix).Append(" — height in ").Append(WBondUnits.Suffix(unit)).Append('\n');
        sb.Append("span\theight (").Append(WBondUnits.Suffix(unit)).Append(")\n");

        foreach (var p in profile.Shape)
        {
            double heightNm = p.Height * profile.LoopHeightNm;
            sb.Append(Num(p.Span)).Append('\t')
              .Append(Num(WBondUnits.FromNm((long)Math.Round(heightNm), unit))).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Reads a shape back into a profile named <paramref name="name"/>.
    /// <paramref name="fallbackUnit"/> applies only when the text carries no unit header of its own.
    ///
    /// <para>The loop height is recovered as the TALLEST row, and every height is renormalised
    /// against it — which is what makes a copy-then-paste onto a different group carry the shape AND
    /// its height rather than only one of them.</para>
    /// </summary>
    /// <returns>False when the text is not a profile at all; this is what greys out Paste.</returns>
    public static bool TryRead(string? text, WBondUnit fallbackUnit, string name, out LoopProfile profile)
    {
        profile = null!;
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

        double loopHeightNm = heightsNm.Max();
        if (!(loopHeightNm > 0)) return false;   // a flat "profile" is not a loop

        double spanMax = spans.Max();
        if (!(spanMax > 0)) return false;

        // Renormalise both axes. Accepting an absolute span as well as a 0..1 one costs nothing and
        // means a user who typed millimetres into the span column still gets the shape they drew.
        double spanScale = spanMax > 1.0 ? 1.0 / spanMax : 1.0;

        var shape = new List<ProfilePoint>(spans.Count);
        for (int i = 0; i < spans.Count; i++)
        {
            shape.Add(new ProfilePoint(
                Math.Clamp(spans[i] * spanScale, 0.0, 1.0),
                Math.Clamp(heightsNm[i] / loopHeightNm, 0.0, 1.0)));
        }

        profile = new LoopProfile
        {
            Name = name,
            LoopHeightNm = (long)Math.Round(loopHeightNm),
            Shape = shape,
        };

        return true;
    }

    /// <summary>True when the clipboard holds something this can read — drives Paste's enabled state.</summary>
    public static bool CanRead(string? text, WBondUnit fallbackUnit) =>
        TryRead(text, fallbackUnit, "probe", out _);

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
