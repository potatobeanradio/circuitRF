// ================================================================
//  LoadpullFomDialect.cs — FOM-name dialect map for .spl / .lpcwave
//
//  Two measured-data dialects (HarmonicaRF and lpcwave-derived) use
//  different column names for the same physical quantities.  This
//  shared table maps every known measured name to the canonical name
//  used by BuildLoadpullDataSet (the engine's contract), plus the
//  unit conversion needed to reach the engine's stored unit.
// ================================================================

using System;
using System.Collections.Generic;

namespace RfCore.Loadpull
{
    public enum FomScale
    {
        PassThrough,   // stored unit matches measured unit
        DbmToW,        // 10^(x/10)/1000
        PctToLinear,   // x/100
        MaToA,         // x/1000
        UaToA,         // x/1_000_000
    }

    public sealed record FomEntry(string CanonicalName, FomScale Scale);

    /// <summary>
    /// Shared dialect table: measured column name → (canonical DataSet cube name, unit scale).
    /// Add new dialect entries here; readers use Apply() for conversion.
    /// Canonical names match BuildLoadpullDataSet exactly.
    /// </summary>
    public static class LoadpullFomDialect
    {
        public static readonly IReadOnlyDictionary<string, FomEntry> Map =
            new Dictionary<string, FomEntry>(StringComparer.OrdinalIgnoreCase)
        {
            // Canonical names carry a unit suffix and the stored value is in the displayed unit
            // (dBm/dB/%) — matching the simulated post-processor output so measured and simulated
            // loadpull DataSets are interchangeable in the display layer.
            // ── Output power (kept in dBm; readers also derive Pout_W) ─
            ["Pout_dBm"]          = new("Pout_dBm",  FomScale.PassThrough),
            ["PoutWaves[dBm]"]    = new("Pout_dBm",  FomScale.PassThrough),

            // ── Transducer gain ───────────────────────────────────
            ["Gt_dB"]             = new("Gt_dB",     FomScale.PassThrough),
            ["GainWavesTrd[dB]"]  = new("Gt_dB",     FomScale.PassThrough),

            // ── Power gain ────────────────────────────────────────
            ["Gp_dB"]             = new("Gp_dB",     FomScale.PassThrough),
            ["GainWavesPwr[dB]"]  = new("Gp_dB",     FomScale.PassThrough),

            // ── Drain efficiency (kept in %) ──────────────────────
            ["Eff_%%"]            = new("Efficiency", FomScale.PassThrough),
            ["Eff_%"]             = new("Efficiency", FomScale.PassThrough),
            ["OutEffWaves[%]"]    = new("Efficiency", FomScale.PassThrough),

            // ── PAE (kept in %) ───────────────────────────────────
            ["PAE"]               = new("PAE",       FomScale.PassThrough),
            ["PAEffWaves[%]"]     = new("PAE",       FomScale.PassThrough),

            // ── Available source power (PavlDbm axis) ─────────────
            ["Pin_avail_dBm"]     = new("PavlDbm",   FomScale.PassThrough),
            ["Psource[dBm]"]      = new("PavlDbm",   FomScale.PassThrough),
            ["PinWaves[dBm]"]     = new("PavlDbm",   FomScale.PassThrough),

            // ── Load-side bias (drain) ────────────────────────────
            ["Vq_out_v"]          = new("BiasVLoad", FomScale.PassThrough),
            ["V2[V]"]             = new("BiasVLoad", FomScale.PassThrough),
            ["Iq_out_mA"]         = new("BiasILoad", FomScale.MaToA),
            ["I2[mA]"]            = new("BiasILoad", FomScale.MaToA),

            // ── Source-side bias (gate) ───────────────────────────
            ["Vq_in_v"]           = new("BiasVSrc",  FomScale.PassThrough),
            ["V1[V]"]             = new("BiasVSrc",  FomScale.PassThrough),
            ["Iq_in_mA"]          = new("BiasISrc",  FomScale.MaToA),
            ["I1[mA]"]            = new("BiasISrc",  FomScale.MaToA),
            ["I1[uA]"]            = new("BiasISrc",  FomScale.UaToA),

            // ── Canonical self-mapping entries ─────────────────────────────────
            // circuitRF's SplWriter/LpcwaveWriter emit the simulation's own canonical cube names as
            // column headers (the user-approved choice — see docs/design/loadpull-postprocessor.md §6),
            // so a written file round-trips back through these readers. Values are stored in the same
            // unit they are displayed in, so every entry is PassThrough. These never collide with the
            // vendor names above (a measured file carries Eff_%/Iq_out_mA, not Efficiency/BiasILoad).
            ["Efficiency"]        = new("Efficiency", FomScale.PassThrough),
            ["Pdc_W"]             = new("Pdc_W",      FomScale.PassThrough),
            ["BiasVLoad"]         = new("BiasVLoad",  FomScale.PassThrough),
            ["BiasILoad"]         = new("BiasILoad",  FomScale.PassThrough),  // stored in A (canonical)
            ["BiasVSrc"]          = new("BiasVSrc",   FomScale.PassThrough),
            ["BiasISrc"]          = new("BiasISrc",   FomScale.PassThrough),  // stored in A (canonical)
            ["Zin_real"]          = new("Zin_real",   FomScale.PassThrough),
            ["Zin_imag"]          = new("Zin_imag",   FomScale.PassThrough),
            ["IRL_dB"]            = new("IRL_dB",      FomScale.PassThrough),
            ["AMPM_deg"]          = new("AMPM_deg",   FomScale.PassThrough),
        };

        public static double Apply(double v, FomScale scale) => scale switch
        {
            FomScale.DbmToW      => Math.Pow(10.0, v / 10.0) / 1000.0,
            FomScale.PctToLinear => v / 100.0,
            FomScale.MaToA       => v / 1000.0,
            FomScale.UaToA       => v / 1_000_000.0,
            _                    => v,
        };
    }
}
