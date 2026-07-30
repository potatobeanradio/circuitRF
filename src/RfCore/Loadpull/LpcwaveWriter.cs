// ================================================================
//  LpcwaveWriter.cs — writes a canonical loadpull DataSet to a .lpcwave
//  file (Phase 2 of docs/design/loadpull-postprocessor.md).
//
//  Inverse of LpcwaveReader: a "! Frequency = X GHz" comment per block,
//  a "Point Gamma Phase[deg] Psource[dBm] <FOMs…>" column header, then per
//  grid point a "# NNN  Γmag  Γphase(deg)" line followed by drive-up rows
//  carrying only the data columns (Psource[dBm] + FOMs).  Multi-frequency
//  aware (one "! Frequency" block per frequency).
//
//  Column naming uses the simulation's canonical cube names (recognised on
//  read-back via LoadpullFomDialect's self-mapping entries).  The source
//  termination round-trips via |GS@F0| / PhiS@F0[deg] columns when present.
//  A file written here round-trips through LpcwaveReader.
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
    public static class LpcwaveWriter
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ── Public API ──────────────────────────────────────────────────────────

        /// <summary>Write the loadpull cubes in <paramref name="group"/> ("" = top level) as a .lpcwave file.</summary>
        public static void WriteLpcwave(DataSet ds, string path, string group = "")
        {
            using var w = new StreamWriter(path);
            WriteLpcwave(ds, w, group);
        }

        public static void WriteLpcwave(DataSet ds, TextWriter w, string group = "")
        {
            var blocks = LoadpullExportModel.Build(ds, group);
            if (blocks.Count == 0) throw new InvalidOperationException("Loadpull DataSet has no frequency blocks.");

            w.WriteLine("! Power Sweep Load Pull Measurement Data");
            w.WriteLine("! circuitRF simulated loadpull export");
            w.WriteLine("!--------------------------------------------------------");

            foreach (var b in blocks)
                WriteBlock(w, b);
        }

        // ── One frequency block ────────────────────────────────────────────────

        private static void WriteBlock(TextWriter w, LoadpullFreqBlock b)
        {
            var fomNames = LoadpullExportModel.FomColumns.Where(b.Foms.ContainsKey).ToList();
            bool hasSrc  = b.HasSourceGamma;

            w.WriteLine($"! Frequency = {Fmt(b.FreqGHz)} GHz");
            // A "! Source Impedance" comment marks this as load-pull (source fixed, load swept).
            if (hasSrc)
            {
                var zs = GammaToZ(b.SourceGamma);
                w.WriteLine($"! Source Impedance = {Fmt(zs.Real)} +j {Fmt(zs.Imaginary)} Ohm");
            }
            else
            {
                w.WriteLine("! Source Impedance = 50 +j 0 Ohm");
            }
            w.WriteLine("! Reference Plane = DUT");
            w.WriteLine("!--------------------------------------------------------");

            // Column header: first three (Point/Gamma/Phase) come from the "# …" line; data rows
            // provide columns from index 3 onward.
            var header = new List<string> { "Point", "Gamma", "Phase[deg]", "Psource[dBm]" };
            header.AddRange(fomNames);
            if (hasSrc) { header.Add("|GS@F0|"); header.Add("PhiS@F0[deg]"); }
            w.WriteLine(string.Join("  ", header));
            w.WriteLine("!--------------------------------------------------------");

            double srcMag = hasSrc ? b.SourceGamma.Magnitude : 0.0;
            double srcPhDeg = hasSrc ? b.SourceGamma.Phase * 180.0 / Math.PI : 0.0;

            for (int gi = 0; gi < b.NGrid; gi++)
            {
                var gld = b.GammaLoad[gi];
                double gMag = gld.Magnitude;
                double gPhDeg = gld.Phase * 180.0 / Math.PI;
                w.WriteLine($"# {(gi + 1).ToString("000", Inv)}  {Fmt(gMag)}  {Fmt(gPhDeg)}");

                for (int pi = 0; pi < b.NPin; pi++)
                {
                    int idx = gi * b.NPin + pi;
                    var cells = new List<string> { Fmt(b.PinAxis[pi]) };
                    foreach (var name in fomNames)
                        cells.Add(Fmt(b.Foms[name][idx]));
                    if (hasSrc) { cells.Add(Fmt(srcMag)); cells.Add(Fmt(srcPhDeg)); }
                    w.WriteLine("     " + string.Join("  ", cells));
                }
            }
        }

        // ── Formatting ────────────────────────────────────────────────────────────

        private static string Fmt(double v) =>
            double.IsNaN(v) ? "nan" : v.ToString("G9", Inv);

        private static Complex GammaToZ(Complex gamma, double z0 = 50.0)
        {
            var denom = 1.0 - gamma;
            if (denom.Magnitude < 1e-12) return new Complex(z0 * 1e6, 0);
            return z0 * (1.0 + gamma) / denom;
        }
    }
}
