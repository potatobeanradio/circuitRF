// ================================================================
//  SplReader.cs — HarmonicaRF / lpcwave-derived .spl reader
//
//  Produces a loadpull DataSet matching BuildLoadpullDataSet's shape:
//    axes "gridPoint" and "pinStep" (pinStep values = PavlDbm)
//    cubes: Pout(W), Gt(dB), Gp(dB), DE(linear), PAE(linear),
//           PavlDbm(dBm), BiasV/I{Load,Src}, GammaLoad(complex),
//           ZLoad(complex).
//
//  Two dialects (auto-detected by column header):
//    HarmonicaRF: Pout_dBm, Gt_dB, Eff_%%, gamma_ldN = RI pair in data
//    lpcwave-derived (ConvertedFile.spl): PoutWaves[dBm], GainWavesTrd[dB],
//      PAEffWaves[%]; same gamma_ldN RI pair encoding.
//
//  Grammar: brief-7.4f-loadpull-ingest.md §7.4f-1.
//  Reference for file grammar: SPLData.py read_spl (read-only reference).
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using RfCore.Data;

namespace RfCore.Loadpull
{
    public static class SplReader
    {
        // ── Public API ──────────────────────────────────────────────────────────

        public static DataSet ReadSpl(string path)
        {
            using var reader = new StreamReader(path);
            return Parse(reader);
        }

        public static DataSet ReadSpl(TextReader reader) => Parse(reader);

        // ── Parser ──────────────────────────────────────────────────────────────

        private static DataSet Parse(TextReader rdr)
        {
            var lines = new List<string>();
            string? line;
            while ((line = rdr.ReadLine()) != null)
                lines.Add(line);

            // ── Header metadata ─────────────────────────────────────────────────
            int numFreq = 1;
            // freq → (nGrid, nPin)
            var freqGrids = new List<(double FreqGHz, int NGrid, int NPin)>();

            for (int i = 0; i < lines.Count; i++)
            {
                var l = lines[i].Trim();
                if (l.StartsWith("Number of Frequencies", StringComparison.OrdinalIgnoreCase))
                {
                    var eq = l.IndexOf('=');
                    if (eq >= 0 && int.TryParse(l.AsSpan(eq + 1).Trim(), out int nf))
                        numFreq = nf;
                }
                // "1.8 145 70" — one per freq
                else if (freqGrids.Count < numFreq && !l.StartsWith("!") && !l.StartsWith("Number") && !l.StartsWith("VAR"))
                {
                    var parts = l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3
                        && double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double fghz)
                        && int.TryParse(parts[1], out int ng)
                        && int.TryParse(parts[2], out int np))
                    {
                        freqGrids.Add((fghz, ng, np));
                    }
                }
            }

            if (freqGrids.Count == 0)
                throw new FormatException(".spl: no freq/grid/pin header line found.");

            // ── Parse each frequency block ──────────────────────────────────────
            // Locate "Freq = X GHz" lines; each starts a block.
            var freqBlockStarts = new List<int>();
            for (int i = 0; i < lines.Count; i++)
            {
                var l = lines[i].Trim();
                if (l.StartsWith("Freq =", StringComparison.OrdinalIgnoreCase)
                    || l.StartsWith("Freq=", StringComparison.OrdinalIgnoreCase))
                    freqBlockStarts.Add(i);
            }

            // Build the DataSet; if single-freq omit freq axis, else include it.
            bool multiFreq = freqBlockStarts.Count > 1;

            // We'll accumulate per-frequency results then assemble.
            var freqResults = new List<FreqBlock>();

            for (int fi = 0; fi < freqBlockStarts.Count; fi++)
            {
                int blockStart = freqBlockStarts[fi];
                int blockEnd   = (fi + 1 < freqBlockStarts.Count)
                                 ? freqBlockStarts[fi + 1] - 1
                                 : lines.Count - 1;

                var freqInfo = fi < freqGrids.Count ? freqGrids[fi] : freqGrids[0];
                int nGrid = freqInfo.NGrid;
                int nPin  = freqInfo.NPin;

                var block = ParseFreqBlock(lines, blockStart, blockEnd, nGrid, nPin, freqInfo.FreqGHz);
                freqResults.Add(block);
            }

            return AssembleDataSet(freqResults, multiFreq);
        }

        // ── Parse one Freq = X GHz block ────────────────────────────────────────

        private sealed class FreqBlock
        {
            public double FreqGHz;
            public int NGrid;
            public int NPin;
            // Indexed [gi, pi]
            public Complex[] GammaLoad = Array.Empty<Complex>();
            public double[]  PinAxis   = Array.Empty<double>(); // pinStep values (PavlDbm)
            // FOM data — each double[NGrid * NPin], row-major gi*NPin + pi
            public Dictionary<string, double[]> Foms = new(StringComparer.Ordinal);
        }

        private static FreqBlock ParseFreqBlock(
            List<string> lines, int start, int end,
            int nGrid, int nPin, double freqGhz)
        {
            // Find column header line: the first line starting with "valid" or
            // containing gamma_ld1 (and not starting with '!')
            int headerLine = -1;
            for (int i = start; i <= end; i++)
            {
                var l = lines[i].Trim();
                if (l.Length == 0 || l.StartsWith('!')) continue;
                var parts = l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0 && parts[0].Equals("valid", StringComparison.OrdinalIgnoreCase))
                {
                    headerLine = i;
                    break;
                }
            }
            if (headerLine < 0)
                throw new FormatException($".spl Freq={freqGhz} GHz: no column header ('valid ...') found.");

            // Expand gamma columns: each gamma_srcN / gamma_ldN in header
            // represents TWO consecutive values (real, imag) in each data row.
            var rawHeader = lines[headerLine].Trim()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var header = ExpandGammaHeader(rawHeader);

            // Build column index map
            var colIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < header.Count; i++)
                colIdx[header[i]] = i;

            // Detect dialect
            bool isLpcwaveDerived = colIdx.ContainsKey("PoutWaves[dBm]");

            // Identify swept gamma column pair (gamma_ld1_real / gamma_ld1_imag)
            bool hasGammaRi = colIdx.ContainsKey("gamma_ld1_real") && colIdx.ContainsKey("gamma_ld1_imag");
            int ld1RealCol  = hasGammaRi ? colIdx["gamma_ld1_real"] : -1;
            int ld1ImagCol  = hasGammaRi ? colIdx["gamma_ld1_imag"] : -1;

            // Pin column (PavlDbm source)
            int pinCol = colIdx.TryGetValue("Pin_avail_dBm", out int pc1) ? pc1
                       : colIdx.TryGetValue("Psource[dBm]",  out int pc2) ? pc2
                       : colIdx.TryGetValue("PinWaves[dBm]", out int pc3) ? pc3
                       : -1;

            int validCol = colIdx.TryGetValue("valid", out int vc) ? vc : 0;

            // Build FOM column mapping: header col index → FomEntry
            var fomCols = new List<(int ColIdx, string CanonicalName, FomScale Scale)>();
            for (int i = 0; i < header.Count; i++)
            {
                if (LoadpullFomDialect.Map.TryGetValue(header[i], out var entry))
                    fomCols.Add((i, entry.CanonicalName, entry.Scale));
            }

            // Data rows start immediately after the header line
            int dataStart = headerLine + 1;

            // Read raw data
            int totalRows = nGrid * nPin;
            var rawData = new List<double[]>(totalRows);
            for (int i = dataStart; i <= end && rawData.Count < totalRows; i++)
            {
                var l = lines[i].Trim();
                if (l.Length == 0 || l.StartsWith('!')) continue;
                var vals = ParseDoubleRow(l, header.Count);
                if (vals is not null)
                    rawData.Add(vals);
            }

            // Pad to totalRows if file is short
            while (rawData.Count < totalRows)
                rawData.Add(new double[header.Count]);

            // Allocate output
            var block = new FreqBlock
            {
                FreqGHz  = freqGhz,
                NGrid    = nGrid,
                NPin     = nPin,
                GammaLoad = new Complex[nGrid],
                PinAxis   = new double[nPin],
            };
            foreach (var (_, canon, _) in fomCols)
            {
                if (!block.Foms.ContainsKey(canon))
                {
                    var buf = new double[nGrid * nPin];
                    Array.Fill(buf, double.NaN);
                    block.Foms[canon] = buf;
                }
            }

            // Fill Pin axis from grid point 0 (all nPin rows, even if valid=0)
            for (int pi = 0; pi < nPin && pi < rawData.Count; pi++)
            {
                block.PinAxis[pi] = pinCol >= 0 && pinCol < rawData[pi].Length
                    ? rawData[pi][pinCol]
                    : (double)pi;
            }

            // Fill grid points
            for (int gi = 0; gi < nGrid; gi++)
            {
                int rowBase = gi * nPin;

                // Gamma from first row of this grid point
                if (rowBase < rawData.Count && ld1RealCol >= 0)
                {
                    var r0 = rawData[rowBase];
                    double gReal = ld1RealCol < r0.Length ? r0[ld1RealCol] : 0.0;
                    double gImag = ld1ImagCol >= 0 && ld1ImagCol < r0.Length ? r0[ld1ImagCol] : 0.0;
                    block.GammaLoad[gi] = new Complex(gReal, gImag);
                }

                // Drive-up rows
                for (int pi = 0; pi < nPin; pi++)
                {
                    int ri  = rowBase + pi;
                    int idx = gi * nPin + pi;
                    if (ri >= rawData.Count) { SetNaN(block.Foms, idx); continue; }
                    var row = rawData[ri];
                    bool isValid = validCol < row.Length && row[validCol] != 0.0;
                    if (!isValid) { SetNaN(block.Foms, idx); continue; }

                    foreach (var (colI, canon, scale) in fomCols)
                    {
                        double v = colI < row.Length ? row[colI] : double.NaN;
                        block.Foms[canon][idx] = double.IsNaN(v) ? v : LoadpullFomDialect.Apply(v, scale);
                    }
                }
            }

            // Overwrite PavlDbm with the pin-axis values so it is always consistent
            // with the pinStep axis regardless of which column the dialect map picked.
            if (!block.Foms.ContainsKey("PavlDbm"))
                block.Foms["PavlDbm"] = new double[nGrid * nPin];
            for (int gi = 0; gi < nGrid; gi++)
                for (int pi = 0; pi < nPin; pi++)
                    block.Foms["PavlDbm"][gi * nPin + pi] = block.PinAxis[pi];

            return block;
        }

        // ── Assemble DataSet from parsed blocks ──────────────────────────────────

        private static DataSet AssembleDataSet(List<FreqBlock> blocks, bool multiFreq)
        {
            var ds = new DataSet();
            if (blocks.Count == 0) return ds;

            if (!multiFreq)
            {
                var b = blocks[0];
                AddFreqSlice(ds, b);
            }
            else
            {
                // Multi-freq: freq as outermost axis
                int nF    = blocks.Count;
                int nGrid = blocks[0].NGrid;
                int nPin  = blocks[0].NPin;

                var freqVals   = new double[nF];
                var freqLabels = new string[nF];
                for (int fi = 0; fi < nF; fi++)
                {
                    freqVals[fi]   = blocks[fi].FreqGHz * 1e9;
                    freqLabels[fi] = $"{blocks[fi].FreqGHz:G4} GHz";
                }
                var freqAxis = new Axis("freq", freqVals, "Hz", freqLabels);

                // Collect gamma and pin from first block as canonical
                var gridAxis = MakeGridAxis(blocks[0]);
                var pinAxis  = MakePinAxis(blocks[0]);

                // Collect all unique FOM names
                var allFoms = new HashSet<string>(StringComparer.Ordinal);
                foreach (var b in blocks)
                    foreach (var k in b.Foms.Keys)
                        allFoms.Add(k);

                foreach (var canonName in allFoms)
                {
                    var buf = new double[nF * nGrid * nPin];
                    for (int fi = 0; fi < nF; fi++)
                    {
                        var b = blocks[fi];
                        if (!b.Foms.TryGetValue(canonName, out var slice))
                        {
                            // NaN-fill this freq slice
                            for (int i = 0; i < nGrid * nPin; i++)
                                buf[fi * nGrid * nPin + i] = double.NaN;
                        }
                        else
                        {
                            Array.Copy(slice, 0, buf, fi * nGrid * nPin, nGrid * nPin);
                        }
                    }
                    ds.Add(canonName, new DataCube(new[] { freqAxis, gridAxis, pinAxis }, buf));
                }

                // GammaLoad and ZLoad over {freq, gridPoint}
                var gammaAll = new Complex[nF * nGrid];
                var zAll     = new Complex[nF * nGrid];
                for (int fi = 0; fi < nF; fi++)
                {
                    for (int gi = 0; gi < nGrid; gi++)
                    {
                        var g = fi < blocks.Count && gi < blocks[fi].GammaLoad.Length
                            ? blocks[fi].GammaLoad[gi]
                            : Complex.Zero;
                        gammaAll[fi * nGrid + gi] = g;
                        zAll[fi * nGrid + gi]     = GammaToZ(g);
                    }
                }
                ds.Add("GammaLoad", new DataCube(new[] { freqAxis, gridAxis }, gammaAll));
                ds.Add("ZLoad",     new DataCube(new[] { freqAxis, gridAxis }, zAll));
            }

            return ds;
        }

        private static void AddFreqSlice(DataSet ds, FreqBlock b)
        {
            var gridAxis = MakeGridAxis(b);
            var pinAxis  = MakePinAxis(b);

            foreach (var (canonName, data) in b.Foms)
                ds.Add(canonName, new DataCube(new[] { gridAxis, pinAxis }, data));

            var gammaLoad = b.GammaLoad;
            var zLoad     = new Complex[b.NGrid];
            for (int gi = 0; gi < b.NGrid; gi++)
                zLoad[gi] = GammaToZ(gammaLoad[gi]);

            ds.Add("GammaLoad", new DataCube(new[] { gridAxis }, gammaLoad));
            ds.Add("ZLoad",     new DataCube(new[] { gridAxis }, zLoad));
        }

        // ── Axes ────────────────────────────────────────────────────────────────

        private static Axis MakeGridAxis(FreqBlock b)
        {
            var vals   = new double[b.NGrid];
            var labels = new string[b.NGrid];
            for (int i = 0; i < b.NGrid; i++)
            {
                vals[i] = i;
                var g = i < b.GammaLoad.Length ? b.GammaLoad[i] : Complex.Zero;
                var z = GammaToZ(g);
                labels[i] = $"{z.Real:G6}{(z.Imaginary >= 0 ? "+" : "")}{z.Imaginary:G6}j";
            }
            return new Axis("gridPoint", vals, "", labels);
        }

        private static Axis MakePinAxis(FreqBlock b)
        {
            var labels = new string[b.NPin];
            for (int i = 0; i < b.NPin; i++)
                labels[i] = $"{b.PinAxis[i]:G4}";
            return new Axis("pinStep", b.PinAxis, "", labels);
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        // Expand gamma_srcN / gamma_ldN column names into _real / _imag pairs.
        // Each such column in the file header represents TWO consecutive values.
        private static List<string> ExpandGammaHeader(string[] raw)
        {
            var result = new List<string>(raw.Length * 2);
            foreach (var col in raw)
            {
                if (col.StartsWith("gamma_src", StringComparison.OrdinalIgnoreCase)
                    || col.StartsWith("gamma_ld", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(col + "_real");
                    result.Add(col + "_imag");
                }
                else
                {
                    result.Add(col);
                }
            }
            return result;
        }

        private static void SetNaN(Dictionary<string, double[]> foms, int idx)
        {
            foreach (var arr in foms.Values)
                arr[idx] = double.NaN;
        }

        private static double[]? ParseDoubleRow(string line, int expectedLen)
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;
            var result = new double[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!double.TryParse(parts[i],
                    System.Globalization.NumberStyles.Float |
                    System.Globalization.NumberStyles.AllowExponent,
                    System.Globalization.CultureInfo.InvariantCulture, out result[i]))
                    result[i] = double.NaN;
            }
            return result;
        }

        // Γ → Z  (Z0 = 50 Ω)
        private static Complex GammaToZ(Complex gamma, double z0 = 50.0)
        {
            var denom = 1.0 - gamma;
            if (denom.Magnitude < 1e-12) return new Complex(z0 * 1e6, 0);
            return z0 * (1.0 + gamma) / denom;
        }
    }
}
