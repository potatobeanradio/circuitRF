using System.Globalization;

namespace CircuitRF.WBond;

/// <summary>
/// The profile view's plane, as the toolbar spells it (wbond.md §6.2).
///
/// <para><b>Three named choices and an open one.</b> <c>Auto</c> projects each wire onto its own
/// chord — §6.2's parameterisation, and the only mode in which two wires of different angle and
/// length are directly comparable. <c>XZ</c> and <c>YZ</c> fix the plane, which is what a user
/// wants when they are looking at one array's real geometry rather than comparing shapes. Any other
/// angle is accepted as a number of degrees, because an array bonded at 37° is ordinary and rounding
/// it to an axis would draw a foreshortened picture with no warning.</para>
///
/// <para>Framework-free and here rather than in the view, so the round trip (text → azimuth → text)
/// is testable as arithmetic, and so the persisted <c>.wBond</c> view state and the combo agree by
/// construction.</para>
/// </summary>
public static class ProfileAxisSetting
{
    public const string AutoLabel = "Auto";
    /// <summary>
    /// Spelled without the hyphen (owner, 2026-08-16) — every wBond surface says <c>XZ</c>/<c>YZ</c>.
    /// <see cref="TryParse"/> strips hyphens before matching, so a <c>.wBond</c> or a habit that still
    /// says "X-Z" is read as this and re-shown in the current spelling.
    /// </summary>
    public const string XzLabel = "XZ";

    /// <inheritdoc cref="XzLabel"/>
    public const string YzLabel = "YZ";

    /// <summary>The picker's presets. Any angle may still be typed.</summary>
    public static IReadOnlyList<string> Presets { get; } = [AutoLabel, XzLabel, YzLabel];

    /// <summary>Angles this close to an axis are SHOWN as that axis rather than as a number.</summary>
    private const double AxisToleranceRadians = 1e-9;

    /// <summary>
    /// Parses "Auto", "XZ", "YZ", or an angle in degrees ("45", "37.5°", "-90 deg"). The hyphenated
    /// spellings this control used to show ("X-Z", "Y-Z") are still accepted.
    /// </summary>
    /// <returns>False on text that means none of those — the caller puts the combo back.</returns>
    public static bool TryParse(string? text, out double? azimuthRadians)
    {
        azimuthRadians = null;

        string s = (text ?? "").Trim();
        if (s.Length == 0) return true;   // an emptied box means Auto, the default

        switch (s.ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace("_", ""))
        {
            case "auto": azimuthRadians = null; return true;
            case "xz": case "x": azimuthRadians = 0.0; return true;
            case "yz": case "y": azimuthRadians = Math.PI / 2.0; return true;
        }

        // Strip a trailing degree marker before parsing, so the value this method FORMATS is also a
        // value it accepts — a round trip that fails on its own output is a trap, not a feature.
        string number = s.TrimEnd();
        foreach (string suffix in new[] { "°", "deg", "degrees", "d" })
        {
            if (number.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                number = number[..^suffix.Length].TrimEnd();
                break;
            }
        }

        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double degrees))
            return false;
        if (!double.IsFinite(degrees)) return false;

        azimuthRadians = degrees * Math.PI / 180.0;
        return true;
    }

    /// <summary>Formats an azimuth the way the picker shows it.</summary>
    public static string Format(double? azimuthRadians)
    {
        if (azimuthRadians is not { } azimuth) return AutoLabel;

        // Folded onto [0, 180): a plane and its opposite are the same plane, so "270°" would name a
        // view the user already has a name for.
        double degrees = azimuth * 180.0 / Math.PI;
        degrees -= 180.0 * Math.Floor(degrees / 180.0);

        double tolerance = AxisToleranceRadians * 180.0 / Math.PI;
        if (degrees <= tolerance || degrees >= 180.0 - tolerance) return XzLabel;
        if (Math.Abs(degrees - 90.0) <= tolerance) return YzLabel;

        return degrees.ToString("0.###", CultureInfo.InvariantCulture) + "°";
    }
}
