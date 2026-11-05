namespace CircuitRF.Core.Expressions;

/// <summary>brief-sar-10 R-sar10-1 — the field measurements a <c>fields</c> analysis answers (Evaluator.Fields.cs).</summary>
public enum FieldMetric
{
    /// <summary>Mean |B1+| over a region, volume-weighted, T per √W.</summary>
    B1pMean,
    /// <summary>The smallest |B1+| of a region's cells, T per √W.</summary>
    B1pMin,
    /// <summary>|B1+|'s coefficient of variation over a region (volume-weighted standard deviation over the mean).</summary>
    B1pCoV,
    /// <summary>Peak spatial-average SAR of one mass, W/kg per W — the full average, never approximated.</summary>
    PsSar,
    /// <summary>A region's absorbed power over its mass, W/kg per W.</summary>
    SarWb,
    /// <summary>B1pMean over √psSAR, T per √(W/kg).</summary>
    SarEff,
    /// <summary>The power absorbed in every tissue cell, W per W.</summary>
    Pabs,
    /// <summary>brief-cosim-pattern-4 R-cp4-4 — each of these is the far-field cube of the same name: realized gain, dBi, with mismatch.</summary>
    RealizedGainDbi,
    /// <summary>Gain, dBi: per watt accepted, without mismatch.</summary>
    GainDbi,
    /// <summary>Directivity, dBi.</summary>
    DirectivityDbi,
    /// <summary>Radiated over accepted, percent.</summary>
    RadiationEfficiency,
    /// <summary>Total radiated power against the result's reference input power, dBm.</summary>
    TrpDbm,
    /// <summary>Peak EIRP against the result's reference input power, dBm.</summary>
    PeakEirpDbm,
}

/// <summary>brief-sar-10 — the field measurements' function names (Evaluator.Fields.cs); each takes a fields analysis's
/// name as its first argument.</summary>
public static class FieldMetricFunctions
{
    public static IReadOnlyList<string> Names { get; } =
        ["B1pMean", "B1pMin", "B1pCoV", "psSAR", "SARwb", "SarEff", "Pabs",
         "RealizedGainDbi", "GainDbi", "DirectivityDbi", "RadiationEfficiency", "TrpDbm", "PeakEirpDbm"];

    public static bool IsFieldMetric(string name) => name is
        "B1pMean" or "B1pMin" or "B1pCoV" or "psSAR" or "SARwb" or "SarEff" or "Pabs" || IsPatternMetric(name);

    /// <summary>The function a measurement is written as, which for two of them is not the enum's spelling.</summary>
    public static string NameOf(FieldMetric metric) => metric switch
    {
        FieldMetric.PsSar => "psSAR",
        FieldMetric.SarWb => "SARwb",
        _ => metric.ToString(),
    };

    /// <summary>brief-cosim-pattern-4 R-cp4-4 — the functions that read a far field rather than a field set.</summary>
    public static bool IsPatternMetric(string name) => name is
        "RealizedGainDbi" or "GainDbi" or "DirectivityDbi" or "RadiationEfficiency" or "TrpDbm" or "PeakEirpDbm";
}

/// <summary>
/// brief-sar-10 — one fields analysis's recombined field, as the expression engine reads it: the measurements of
/// <see cref="FieldMetric"/>, each over a region (a tissue, a set of tissues, or a box; null for the setup's own) and,
/// for an average, a mass in grams. Every value is normalised as its analysis says (per watt accepted, or available).
/// Implemented beside the field set (src/Design), so nothing here knows where the fields are kept.
/// </summary>
public interface IFieldMetrics
{
    /// <summary>The value of <paramref name="metric"/>; throws <see cref="ExpressionException"/> with the sentence that
    /// refuses it (an unknown region, a mass that is not positive, an average deferred while a knob moves).</summary>
    double Evaluate(FieldMetric metric, string? region, double? massGrams);

    /// <summary>brief-cosim-pattern-4 R-cp4-4 — a far-field measurement: the peak, or that direction of the grid when <paramref name="thetaDeg"/>
    /// and <paramref name="phiDeg"/> are given. Throws <see cref="ExpressionException"/> with the sentence that refuses it.</summary>
    double EvaluatePattern(FieldMetric metric, double? thetaDeg, double? phiDeg);
}
