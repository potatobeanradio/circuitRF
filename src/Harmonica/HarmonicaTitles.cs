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

    /// <summary>
    /// R-h9c-6 (R1C §5) — the MXP/MXE readout column header: "MXP 1f0 Load", "MXE 2f0 Source", …
    /// <b>Literally as the owner specified</b> — always numeric ("1f0"), unlike <see cref="PlaneRow"/>'s
    /// row 2, which spells band 1 as "Fundamental". Two different rows the owner asked to look
    /// different are kept different rather than unified for tidiness.
    ///
    /// <para>R8C §1 — when the optimum is solved, the header also carries its REAL impedance, named
    /// by the termination it corresponds to ("MXP 1f0 ZL1=12.500-j3.200 Ω") — the same <c>Z{side}
    /// {harmonic}</c> spelling the Source/Load termination rows already use.</para>
    /// </summary>
    /// <param name="zText">The optimum's impedance, already formatted by the caller
    /// (HarmonicaReadoutFormatting.FormatZ, which is in src/Ui and must stay there). Null or empty
    /// keeps the old plane-only header — the "no optimum" case.</param>
    public static string MxHeaderRow(string label, TerminationSide side, int harmonic, string? zText = null)
        => zText is { Length: > 0 }
            ? $"{label} {harmonic}f0 Z{(side == TerminationSide.Source ? "S" : "L")}{harmonic}={zText}"
            : $"{label} {harmonic}f0 {(side == TerminationSide.Source ? "Source" : "Load")}";

    private static string FormatTrim(double v)
        => v == Math.Floor(v)
            ? v.ToString("0", CultureInfo.InvariantCulture)
            : v.ToString("0.####", CultureInfo.InvariantCulture);

    private static string FormatZ0(double z0)
        => z0 == Math.Floor(z0)
            ? ((long)z0).ToString(CultureInfo.InvariantCulture)
            : z0.ToString("0.##", CultureInfo.InvariantCulture);
}
