// ================================================================
//  SplWriter.cs — writes a canonical loadpull DataSet to a HarmonicaRF
//  .spl file (Phase 2 of docs/design/loadpull-postprocessor.md).
//
//  Inverse of SplReader: emits the header (Number of Frequencies, the
//  per-freq "<fGHz> <nGrid> <nPin>" lines), then one "Freq = X GHz" block
//  per frequency with a "valid gamma_src1 gamma_ld1 Psource[dBm] <FOMs…>"
//  column header and nGrid*nPin data rows.  Multi-frequency aware.
//
//  Column naming: the simulation's own canonical cube names are written as
//  headers (the user-approved choice — docs §6), recognised on read-back via
//  the self-mapping entries in LoadpullFomDialect.  A file written here
//  round-trips through SplReader; it is NOT intended to feed external
//  HarmonicaRF tools (which expect Eff_%/Iq_out_mA spellings).
//
//  RfCore firewall: headless, no Avalonia/UI types.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using RfCore.Data;

namespace RfCore.Loadpull
{
    public static class SplWriter
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ── Public API ──────────────────────────────────────────────────────────

        /// <summary>Write the loadpull cubes in <paramref name="group"/> ("" = top level) as a .spl file.</summary>
        public static void WriteSpl(DataSet ds, string path, string group = "")
        {
            using var w = new StreamWriter(path);
            WriteSpl(ds, w, group);
        }

        public static void WriteSpl(DataSet ds, TextWriter w, string group = "")
        {
            var blocks = LoadpullExportModel.Build(ds, group);
            if (blocks.Count == 0) throw new InvalidOperationException("Loadpull DataSet has no frequency blocks.");

            // ── File header ─────────────────────────────────────────────────────
            w.WriteLine("! circuitRF simulated loadpull export");
            w.WriteLine("! HarmonicaRF Fundamental");
            w.WriteLine();
            w.WriteLine($"Number of Frequencies = {blocks.Count}");
            w.WriteLine("Number of Variables = 1");
            w.WriteLine("VAR=<F0 Load Gamma>, Units=<>");
            w.WriteLine("VAR=<Pin_avail>, Units=<dBm>");
            w.WriteLine("! Freq Points per VAR");
            foreach (var b in blocks)
                w.WriteLine($"{Fmt(b.FreqGHz)} {b.NGrid} {b.NPin}");

            // ── Per-frequency blocks ────────────────────────────────────────────
            foreach (var b in blocks)
                WriteBlock(w, b);
        }

        // ── One Freq = X GHz block ────────────────────────────────────────────────

        private static void WriteBlock(TextWriter w, LoadpullFreqBlock b)
        {
            // Columns present (canonical FOM names), in display order.
            var fomNames = LoadpullExportModel.FomColumns.Where(b.Foms.ContainsKey).ToList();
            bool hasSrc  = b.HasSourceGamma;

            w.WriteLine($"Freq = {Fmt(b.FreqGHz)} GHz");
            w.WriteLine("num_src_harmonics = 1, num_ld_harmonics = 1");

            // Header — gamma tokens are single names; SplReader expands each into a real/imag pair.
            var header = new List<string> { "valid" };
            if (hasSrc) header.Add("gamma_src1");
            header.Add("gamma_ld1");
            header.Add("Psource[dBm]");
            header.AddRange(fomNames);
            w.WriteLine(string.Join(" ", header));

            // "validity" probe — a row is valid when its headline FOM is finite.
            string probe = fomNames.Count > 0 ? fomNames[0] : null!;

            for (int gi = 0; gi < b.NGrid; gi++)
            {
                var gld = b.GammaLoad[gi];
                for (int pi = 0; pi < b.NPin; pi++)
                {
                    int idx = gi * b.NPin + pi;
                    bool valid = probe == null || !double.IsNaN(b.Foms[probe][idx]);

                    var cells = new List<string> { valid ? "1" : "0" };
                    if (hasSrc) { cells.Add(Fmt(b.SourceGamma.Real)); cells.Add(Fmt(b.SourceGamma.Imaginary)); }
                    cells.Add(Fmt(gld.Real)); cells.Add(Fmt(gld.Imaginary));
                    cells.Add(Fmt(b.PinAxis[pi]));
                    foreach (var name in fomNames)
                        cells.Add(Fmt(b.Foms[name][idx]));

                    w.WriteLine(string.Join(" ", cells));
                }
            }
        }

        // ── Formatting ────────────────────────────────────────────────────────────

        private static string Fmt(double v) =>
            double.IsNaN(v) ? "nan" : v.ToString("G9", Inv);
    }
}
