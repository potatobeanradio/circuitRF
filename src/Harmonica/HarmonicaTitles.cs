using System;
using System.Globalization;

namespace CircuitRF.Harmonica;

/// <summary>
/// R-h9b-4 — the Smith panels' two title rows, built in ONE place so the two charts (and the glyph's
/// own readout, when 1C reads the same numbers) cannot disagree about how a compression setting, a
/// metric or a Z0 is spelled.
///
/// <para>Framework-free on purpose: it takes plain values and returns strings, so
/// <c>HarmonicaPanelRenderer</c> (drawing) and any future readout can call the identical formatter.</para>
/// </summary>
public static class HarmonicaTitles
{
    /// <summary>"P-3dB" / "P-2.5dB" — no trailing zeros.</summary>
    public static string CompressionLabel(double compressionDb)
        => "P-" + FormatTrim(compressionDb) + "dB";

    /// <summary>Row 1 — the metric this chart maps: "P-3dB Power (dBm)" or "P-3dB Efficiency (%)" /
    /// "P-3dB PAE (%)", depending on <paramref name="efficiencyMetric"/>.</summary>
    public static string MetricRow(bool isPowerChart, GridMetric efficiencyMetric, double compressionDb)
    {
        string prefix = CompressionLabel(compressionDb);
        if (isPowerChart) return $"{prefix} Power (dBm)";
        return efficiencyMetric == GridMetric.Pae ? $"{prefix} PAE (%)" : $"{prefix} Efficiency (%)";
    }

    /// <summary>Row 2 — the swept plane: "Fundamental Load Plane, Z0=50Ω", "2f0 Source Plane, Z0=50Ω",
    /// and so on. Band 1 reads "Fundamental"; bands ≥ 2 read "{n}f0". Z0 is an integer where it is one.</summary>
    public static string PlaneRow(TerminationSide side, int harmonic, double z0)
    {
        string band  = harmonic <= 1 ? "Fundamental" : $"{harmonic}f0";
        string plane = side == TerminationSide.Source ? "Source" : "Load";
        return $"{band} {plane} Plane, Z0={FormatZ0(z0)}Ω";
    }

    private static string FormatTrim(double v)
        => v == Math.Floor(v)
            ? v.ToString("0", CultureInfo.InvariantCulture)
            : v.ToString("0.####", CultureInfo.InvariantCulture);

    private static string FormatZ0(double z0)
        => z0 == Math.Floor(z0)
            ? ((long)z0).ToString(CultureInfo.InvariantCulture)
            : z0.ToString("0.##", CultureInfo.InvariantCulture);
}
