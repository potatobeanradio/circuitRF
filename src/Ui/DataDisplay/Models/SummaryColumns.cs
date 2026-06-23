using RfCore;

namespace CircuitRF.Ui.DataDisplay
{
    /// <summary>Header/format helpers for summary-table columns (single source of truth, shared by
    /// the renderer and the auto-fill command). Mirrors the reference generator's column set.</summary>
    public static class SummaryColumns
    {
        /// <summary>Auto header for a column when SummaryColumnData.Header is empty.</summary>
        public static string AutoHeader(SummaryColumnData col) => col.Kind switch
        {
            SummaryColumnKind.Zload          => "Zload (Ω)",
            SummaryColumnKind.Zsource        => "Zsource (Ω)",
            SummaryColumnKind.Zin            => "Zin (Ω)",
            SummaryColumnKind.OperatingPoint => col.MetricName switch
            {
                // Unit is magnitude-inferred at RebuildSummary time (bug 5 option b) and stamped on
                // col.UnitLabel; fall back to the canonical unit before the first RebuildSummary.
                "BiasVLoad" => $"VDD ({(string.IsNullOrEmpty(col.UnitLabel) ? "V"  : col.UnitLabel)})",
                "BiasILoad" => $"Idq ({(string.IsNullOrEmpty(col.UnitLabel) ? "mA" : col.UnitLabel)})",
                _           => col.MetricName,
            },
            _ /* Metric */   => MetricHeader(col.MetricName),
        };

        /// <summary>
        /// Magnitude-inferred display unit + scale factor for an OperatingPoint bias value (bug 5
        /// option b). The raw value is in SI base units (Amps for BiasILoad, Volts for BiasVLoad).
        /// Returns the unit label to show and the factor to multiply the raw value by so the displayed
        /// number lands in a human-friendly range. Picks the unit from the magnitude of <paramref
        /// name="rawAbs"/> (the representative |value|, e.g. the first finite cell). NaN/zero → canonical
        /// unit (mA for current, V for voltage), matching prior behavior.
        /// </summary>
        public static (string Label, double Scale) OperatingPointUnit(string metricName, double rawAbs)
        {
            bool isCurrent = metricName == "BiasILoad";
            if (double.IsNaN(rawAbs) || rawAbs <= 0)
                return isCurrent ? ("mA", 1e3) : ("V", 1.0);

            if (isCurrent)
            {
                // Current stored in Amps.
                if (rawAbs >= 1.0)    return ("A",  1.0);
                if (rawAbs >= 1e-3)   return ("mA", 1e3);
                return ("µA", 1e6);
            }
            else
            {
                // Voltage stored in Volts.
                if (rawAbs >= 1e3)    return ("kV", 1e-3);
                if (rawAbs >= 1.0)    return ("V",  1.0);
                return ("mV", 1e3);
            }
        }

        public static string MetricHeader(string metric) => metric switch
        {
            "Pout_dBm" or "Pout"            => "Power (dBm)",
            "DE" or "Eff" or "Efficiency"   => "Efficiency (%)",
            "Gt" or "Gain"                  => "Gain (dB)",
            "Gp"                            => "Power Gain (dB)",
            "PAE"                           => "PAE (%)",
            "AMPM"                          => "AM/PM (°)",
            "IRL"                           => "IRL (dB)",
            _                               => metric,
        };

        /// <summary>True when the column renders a complex R+jX value (vs a real scalar).</summary>
        public static bool IsComplexColumn(SummaryColumnKind kind) =>
            kind is SummaryColumnKind.Zload or SummaryColumnKind.Zsource or SummaryColumnKind.Zin;

        /// <summary>The freq anchor-column header for a given unit, e.g. "Freq (GHz)".</summary>
        public static string FreqHeader(FreqUnit unit) => $"Freq ({unit.Description()})";
    }
}
