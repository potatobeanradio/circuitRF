// brief-em3d-21 R-em3d21-4c — what each Palace quality preset costs and how far it lands from
// Accurate, MEASURED once on F0's case A (one 1 mil wire over ground, 1-40 GHz) and case B (the via
// transition, 0.1-20 GHz), through circuitRF, on the machine F0 ran on. The panel's tooltip and the
// .cem reference page print these figures; src/Design/RESOLVED.md §brief-em3d-21 holds the commands
// and the raw numbers. A preset is a claim, so these are the measurement's numbers and nothing else.

using System.Globalization;
using CircuitRF.Design.Layout.Em;

namespace CircuitRF.Design.Em3d;

public static class PalacePresetTable
{
    /// <summary>One run of the measurement: wall time, Palace's own peak memory, and the largest
    /// |ΔS21| (dB) and ∠ΔS21 (°) against the same case at <see cref="Reference"/> over the band (null
    /// for the reference run itself).</summary>
    public sealed record Row(PalaceQuality Preset, string Case, double WallSeconds, double PeakMemoryGB,
                             double? MaxDeltaS21Db, double? MaxDeltaS21Deg);

    /// <summary>
    /// What the deviations are measured against. The brief asks for Accurate; the one measurement was
    /// stopped during case B's Accurate run (it was taking hours on this machine), so the deviations
    /// here are against Standard, and case A was not run. src/Design/RESOLVED.md §brief-em3d-21.
    /// </summary>
    public const PalaceQuality Reference = PalaceQuality.Standard;

    /// <summary>The measurement (2026-09-25, Apple M4, 10 cores, 16 GB, Palace 0.18.1, 10 MPI ranks),
    /// F0's case B, 0.1-20 GHz, 200 points.</summary>
    public static IReadOnlyList<Row> Measured { get; } =
    [
        new(PalaceQuality.Draft,    "B", 74,   3.8, 0.096, 20.6),
        new(PalaceQuality.Standard, "B", 2075, 9.3, null,  null),
    ];

    /// <summary>The preset picker's tooltip: what the user trades, per preset, in the measured figures.</summary>
    public static string Tooltip
    {
        get
        {
            var lines = new List<string>
            {
                "Draft, Standard or Accurate: element order, refinement passes and sweep tolerance, together. " +
                "A box filled in below overrides the preset for that field only.",
            };
            foreach (var q in new[] { PalaceQuality.Draft, PalaceQuality.Standard, PalaceQuality.Accurate })
            {
                var rows = Measured.Where(r => r.Preset == q).ToList();
                if (rows.Count == 0) continue;
                lines.Add($"{q}: " + string.Join("; ", rows.Select(r =>
                    $"case {r.Case} {Secs(r.WallSeconds)}, {F(r.PeakMemoryGB, "0.0")} GB" +
                    (r.MaxDeltaS21Db is { } db && r.MaxDeltaS21Deg is { } deg
                        ? $", |S21| within {F(db, "0.00#")} dB and ∠S21 within {F(deg, "0.0#")}° of {Reference}" : ""))));
            }
            lines.Add("Measured on F0's via transition (case B) on a 10-core, 16 GB machine. Accurate has not been measured.");
            return string.Join("\n", lines);
        }
    }

    private static string F(double v, string format) => v.ToString(format, CultureInfo.InvariantCulture);

    private static string Secs(double s) => s < 90 ? F(s, "0") + " s" : F(s / 60, "0.#") + " min";
}
