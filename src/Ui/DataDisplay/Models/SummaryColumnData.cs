namespace CircuitRF.Ui.DataDisplay
{
    /// <summary>What a summary-table column reports. Drives how the cell value is computed.</summary>
    public enum SummaryColumnKind
    {
        Metric,         // a surface metric read at the optimum (Pout, DE, Gt, AMPM, IRL, ...)
        Zload,          // optimum load impedance ZL (complex, derived from MXP/MXE)
        Zsource,        // per-freq source impedance (complex, read directly)
        Zin,            // input impedance: Zin_real + j*Zin_imag at the optimum (complex)
        OperatingPoint, // per-freq bias scalar (VDD from BiasVLoad, Idq from BiasILoad)
    }

    /// <summary>
    /// Per-trace authoring state for one summary-table column (Phase 7.5). A summary trace
    /// carries this instead of (or alongside) network/cube binding. The frequency anchor column
    /// is implicit (the renderer always emits the freq column); SummaryColumnData describes the
    /// metric/impedance/bias column the user added.
    ///
    /// Compression is NOT stored here — it is a single table-wide value (Plot.TableCompression).
    /// MXP/MXE and Interp/Nearest are also table-wide (Plot.TableOptimum / Plot.TableReadMode).
    /// Persisted in .cdd via SummaryColumnConfig.
    /// </summary>
    public sealed class SummaryColumnData
    {
        /// <summary>What kind of value this column reports.</summary>
        public SummaryColumnKind Kind { get; set; } = SummaryColumnKind.Metric;

        /// <summary>
        /// Canonical metric/cube name for Kind==Metric (e.g. "Pout","DE","Gt","AMPM","IRL")
        /// or the bias cube for Kind==OperatingPoint ("BiasVLoad","BiasILoad").
        /// Ignored for Zload/Zsource/Zin (their source is fixed by Kind).
        /// </summary>
        public string MetricName { get; set; } = "Pout";

        /// <summary>Column header text. Empty means auto-generate from Kind/MetricName (see SummaryColumns).</summary>
        public string Header { get; set; } = "";

        /// <summary>
        /// Display unit label for OperatingPoint columns (Idq/VDD), chosen by magnitude at RebuildSummary
        /// time (bug 5 option b): e.g. "mA"/"µA"/"A" for Idq, "V"/"mV"/"kV" for VDD. Empty for all other
        /// column kinds (their unit is fixed: Ω for impedances, metric-specific for Metric columns).
        /// Single source of truth so the header (AutoHeader) and the card unit label stay consistent with
        /// the scaled CellsReal values. Not persisted — recomputed each RebuildSummary.
        /// </summary>
        public string UnitLabel { get; set; } = "";

        /// <summary>Display precision for the cell value (real columns). Complex columns use 2-dp R+jX.</summary>
        public int    FractionDigits { get; set; } = 1;

        /// <summary>Per-column width override (0 means fall back to plot.ColumnWidth).</summary>
        public double ColumnWidth { get; set; } = 0;

        // ---- Derived cell values (set by the VM's RebuildSummary; NOT persisted) ----
        // One entry per frequency row (same length/order as the table's frequency list).
        // Real columns use CellsReal; complex columns (Zload/Zsource/Zin) use CellsComplex.
        // Null/empty until the VM populates them. NaN entry renders as blank/"NaN".

        /// <summary>Per-frequency real cell values (Metric / OperatingPoint columns). Null until populated.</summary>
        public double[]? CellsReal { get; set; }

        /// <summary>Per-frequency complex cell values (Zload / Zsource / Zin columns). Null until populated.</summary>
        public System.Numerics.Complex[]? CellsComplex { get; set; }

        /// <summary>Deep copy for paste (derived arrays left null so the pasted trace recomputes).</summary>
        public SummaryColumnData Clone() => new SummaryColumnData
        {
            Kind           = Kind,
            MetricName     = MetricName,
            Header         = Header,
            FractionDigits = FractionDigits,
            ColumnWidth    = ColumnWidth,
            // CellsReal and CellsComplex are derived — not cloned.
        };
    }
}
