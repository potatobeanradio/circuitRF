// ================================================================
//  SatwaveReader.cs — .satwave single-load drive-up reader
//
//  Brief LPW, R-lpw-5. A .satwave is one load's drive-up in .lpcwave's
//  columns: a "!" header naming the frequency, the characteristic
//  impedances and the source and load terminations, a "! TITLES:" line
//  naming the columns, then one row per drive — every column on every
//  row (no Point/Gamma/Phase, no "#" grid lines).
//
//      ! Frequency: 29 GHz
//      ! Char.Impedances = Source: 50 + 0j Ohm   Load: 50 + 0j Ohm
//      ! Load Impedances (Polar) = F0: 0.542<117.5(deg)
//      ! Source Impedances (Polar) = F0: 0.320<-179.6(deg)
//      ! TITLES:  Psource[dBm]  PinWaves[dBm]  …  A1reF0 … B2imF0 …
//
//  It reads as a one-point loadpull through LpcwaveReader.BuildBlock —
//  the same column handling, wave derivation and assembly, not a copy.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using RfCore.Data;

namespace RfCore.Loadpull
{
    public static class SatwaveReader
    {
        public static DataSet Read(string path)
        {
            using var reader = new StreamReader(path);
            return Read(reader);
        }

        public static DataSet Read(TextReader reader)
        {
            var lines = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) != null) lines.Add(line);

            double freqGhz = 0;
            Complex? load = null;
            string[]? titles = null;
            var rows = new List<double[]>();
            foreach (var l in lines)
            {
                var t = l.Trim();
                if (t.Length == 0) continue;
                if (t.StartsWith('!'))
                {
                    var body = t.TrimStart('!').Trim();
                    if (body.StartsWith("Frequency", StringComparison.OrdinalIgnoreCase))
                        freqGhz = ParseGhz(body);
                    else if (body.StartsWith("Load Impedances (Polar)", StringComparison.OrdinalIgnoreCase))
                        load = LpcwaveReader.ParsePolarF0(body);
                    else if (body.StartsWith("TITLES:", StringComparison.OrdinalIgnoreCase))
                        titles = body["TITLES:".Length..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    continue;
                }
                if (titles is null) continue;
                var parts = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                var row = new double[parts.Length];
                for (int i = 0; i < parts.Length; i++)
                    row[i] = double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN;
                rows.Add(row);
            }

            if (titles is null) throw new FormatException(".satwave: no '! TITLES:' column line.");
            if (load is null) throw new FormatException(".satwave: no '! Load Impedances (Polar)' line, so the load is not known.");
            if (rows.Count == 0) throw new FormatException(".satwave: no data rows.");

            var header = LpcwaveReader.ReadHeader(lines);
            var block = LpcwaveReader.BuildBlock(titles, new List<Complex> { LoadpullWaves.To50(load.Value, header.Z0Load) },
                                                 new List<List<double[]>> { rows }, freqGhz, isLoadpull: true, fixedLoadZ: null, header);
            return LpcwaveReader.Assemble(new List<LpcwaveReader.FreqBlock> { block });
        }

        /// <summary>"Frequency: 29 GHz" / "Frequency = 29 GHz" → 29; a unit other than GHz is scaled.</summary>
        private static double ParseGhz(string body)
        {
            int sep = body.IndexOfAny(new[] { ':', '=' });
            if (sep < 0) return 0;
            var tokens = body[(sep + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0 || !double.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return 0;
            string unit = tokens.Length > 1 ? tokens[1].ToUpperInvariant() : "GHZ";
            return unit switch { "HZ" => v / 1e9, "KHZ" => v / 1e6, "MHZ" => v / 1e3, _ => v };
        }
    }
}
