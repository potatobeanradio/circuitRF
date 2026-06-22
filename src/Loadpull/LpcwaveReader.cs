// ================================================================
//  LpcwaveReader.cs — .lpcwave loadpull data reader
//
//  Grammar: brief-7.4f-loadpull-ingest.md §7.4f-2.
//  Reference for file grammar: SPLData.py read_lpcwave (read-only).
//
//  Format overview:
//    ! comment / FileInfo block (Frequency, Source/Load Impedance, ...)
//    <column header>                 ← columns: Point Gamma Phase[deg] Psource...
//    !---
//    # NNN  Γmag  Γphase(deg)        ← new grid point
//      <drive-up row 1>              ← values for columns AFTER Phase[deg]
//      <drive-up row 2>
//    # NNN  Γmag  Γphase(deg)
//      ...
//    ! Frequency = X GHz             ← next freq block
//
//  Key: data rows do NOT include Point/Gamma/Phase columns — those come
//  from the # line.  So data rows map to header columns starting at
//  index 3 (i.e., columns[3:] are the FOM columns).
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using RfCore.Data;

namespace RfCore.Loadpull
{
    public static class LpcwaveReader
    {
        // ── Public API ──────────────────────────────────────────────────────────

        public static DataSet ReadLpcwave(string path)
        {
            using var reader = new StreamReader(path);
            return Parse(reader);
        }

        public static DataSet ReadLpcwave(TextReader reader) => Parse(reader);

        // ── Parser ──────────────────────────────────────────────────────────────

        private static DataSet Parse(TextReader rdr)
        {
            var lines = new List<string>();
            string? line;
            while ((line = rdr.ReadLine()) != null)
                lines.Add(line);

            // Split into frequency blocks at "! Frequency = X GHz" lines
            // (or the initial header if only one freq)
            var blocks = SplitIntoFreqBlocks(lines);
            bool multiFreq = blocks.Count > 1;

            var parsed = new List<FreqBlock>();
            foreach (var bl in blocks)
                parsed.Add(ParseFreqBlock(bl.Lines, bl.FreqGHz));

            return AssembleDataSet(parsed, multiFreq);
        }

        // ── Split lines into per-frequency blocks ────────────────────────────────

        private sealed class RawBlock
        {
            public List<string> Lines = new();
            public double FreqGHz;
        }

        private static List<RawBlock> SplitIntoFreqBlocks(List<string> lines)
        {
            var result = new List<RawBlock>();
            // Lines before the first "! Frequency" line (file-level header with
            // "! Source/Load Impedance" etc.) are prepended to the first block so
            // ParseFreqBlock can detect loadpull vs sourcepull.
            var preHeader = new List<string>();
            RawBlock? current = null;

            foreach (var l in lines)
            {
                var trimmed = l.Trim();

                if (trimmed.StartsWith("! Frequency", StringComparison.OrdinalIgnoreCase)
                    || trimmed.StartsWith("!Frequency", StringComparison.OrdinalIgnoreCase))
                {
                    if (current is not null)
                        result.Add(current);
                    current = new RawBlock { FreqGHz = ParseFreqGhz(trimmed) };
                    if (result.Count == 0)
                        current.Lines.AddRange(preHeader); // only for first block
                }

                if (current is null)
                    preHeader.Add(l); // accumulate before first ! Frequency line
                else
                    current.Lines.Add(l);
            }

            if (current is not null && current.Lines.Count > 0)
                result.Add(current);

            // No "! Frequency" line found — treat entire file as one block (freq=0)
            return result.Count > 0 ? result : new List<RawBlock> { new() { Lines = lines, FreqGHz = 0 } };
        }

        private static double ParseFreqGhz(string line)
        {
            // "! Frequency = 2 GHz"
            int eq = line.IndexOf('=');
            if (eq < 0) return 0;
            var rest = line.AsSpan(eq + 1).Trim();
            int space = rest.IndexOf(' ');
            var numPart = space > 0 ? rest[..space] : rest;
            return double.TryParse(numPart, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 0;
        }

        // ── Parse one frequency block ────────────────────────────────────────────

        private sealed class FreqBlock
        {
            public double FreqGHz;
            public int NGrid;
            public int NPin;           // max drive-up steps (ragged → NaN-padded)
            public bool IsLoadpull = true; // false = sourcepull
            public Complex[] GammaLoad = Array.Empty<Complex>();
            public double[]  PinAxis   = Array.Empty<double>();
            public Dictionary<string, double[]> Foms = new(StringComparer.Ordinal);
            // Raw derivation inputs (null when column absent)
            public double[]? GinMag;       // |Γin| (linear)
            public double[]? GinPhase;     // ∠Γin (degrees)
            public double[]? TransPhase;   // transducer phase (degrees)
            public double[]? ReflDb;       // input return loss dB (stored alias)
            public double[]? ReflLin;      // reflection coeff (linear, stored alias)
            public Complex   SourceGamma;  // per-freq source Γ (from first grid/pin)
            public bool      HasSourceGamma;
        }

        private static FreqBlock ParseFreqBlock(List<string> lines, double freqGhz)
        {
            // ── Detect sourcepull vs loadpull from FileInfo comment ──────────────
            // "! Source Impedance = ..." → source is fixed → load is swept → loadpull
            // "! Load Impedance = ..."   → load is fixed → source is swept → sourcepull
            bool isLoadpull = true;
            foreach (var l in lines)
            {
                var t = l.Trim();
                if (t.StartsWith("! Load Impedance", StringComparison.OrdinalIgnoreCase))
                { isLoadpull = false; break; }
                if (t.StartsWith("! Source Impedance", StringComparison.OrdinalIgnoreCase))
                { isLoadpull = true; break; }
            }

            // ── Find column header line ──────────────────────────────────────────
            // First non-comment, non-empty, non-separator line after the FileInfo block.
            // Identified by starting with "Point" (case-insensitive).
            int headerLine = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                var t = lines[i].Trim();
                if (t.Length == 0 || t.StartsWith('!')) continue;
                var parts = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0 && parts[0].Equals("Point", StringComparison.OrdinalIgnoreCase))
                {
                    headerLine = i;
                    break;
                }
            }
            if (headerLine < 0)
                throw new FormatException(".lpcwave: no column header ('Point Gamma Phase[deg] ...') found.");

            // Columns in the header; the first 3 are Point/Gamma/Phase from the # line.
            // Data rows provide columns [3..] only.
            var headerParts = lines[headerLine].Trim()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            // FOM columns (indices into headerParts starting at 3)
            const int dataOffset = 3; // skip Point, Gamma, Phase[deg]
            var fomCols = new List<(int RelIdx, string CanonicalName, FomScale Scale)>();
            for (int i = dataOffset; i < headerParts.Length; i++)
            {
                if (LoadpullFomDialect.Map.TryGetValue(headerParts[i], out var entry))
                    fomCols.Add((i - dataOffset, entry.CanonicalName, entry.Scale));
            }

            int nDataCols = headerParts.Length - dataOffset;

            // ── Pin column (within data-row relative index) ──────────────────────
            int pinRelIdx = -1;
            for (int i = dataOffset; i < headerParts.Length; i++)
            {
                var h = headerParts[i];
                if (h.Equals("Psource[dBm]",  StringComparison.OrdinalIgnoreCase)
                    || h.Equals("PinWaves[dBm]", StringComparison.OrdinalIgnoreCase))
                {
                    pinRelIdx = i - dataOffset;
                    break;
                }
            }

            // ── Raw derivation input columns (not in dialect Map) ────────────────
            int ginMagRelIdx  = -1, ginPhRelIdx  = -1;
            int transPhRelIdx = -1;
            int srcGMagRelIdx = -1, srcGPhRelIdx = -1;
            for (int i = dataOffset; i < headerParts.Length; i++)
            {
                var h = headerParts[i];
                int rel = i - dataOffset;
                if      (h.Equals("|GinWaves@F0|",       StringComparison.OrdinalIgnoreCase)) ginMagRelIdx  = rel;
                else if (h.Equals("PhiinWaves@F0[deg]",  StringComparison.OrdinalIgnoreCase)) ginPhRelIdx   = rel;
                else if (h.Equals("PhiLWaves@F0[deg]",   StringComparison.OrdinalIgnoreCase)) transPhRelIdx = rel;
                else if (h.Equals("|GS@F0|",             StringComparison.OrdinalIgnoreCase)) srcGMagRelIdx = rel;
                else if (h.Equals("PhiS@F0[deg]",        StringComparison.OrdinalIgnoreCase)) srcGPhRelIdx  = rel;
                // SPL-style names (lpcwave files may mix both dialects)
                else if (h.Equals("Gamma_in_mag",    StringComparison.OrdinalIgnoreCase) && ginMagRelIdx  < 0) ginMagRelIdx  = rel;
                else if (h.Equals("Gamma_in_phase",  StringComparison.OrdinalIgnoreCase) && ginPhRelIdx   < 0) ginPhRelIdx   = rel;
                else if (h.Equals("trans_phase",     StringComparison.OrdinalIgnoreCase) && transPhRelIdx < 0) transPhRelIdx = rel;
                else if (h.Equals("Refl_dB",         StringComparison.OrdinalIgnoreCase)) { /* handled via ginMag fallback */ }
            }

            // ── Scan grid points ─────────────────────────────────────────────────
            var gridGammas  = new List<Complex>();
            var driveups    = new List<List<double[]>>(); // per grid-point rows
            List<double[]>? currentDriveup = null;

            for (int i = headerLine + 1; i < lines.Count; i++)
            {
                var raw = lines[i];
                var t   = raw.Trim();
                if (t.Length == 0) continue;
                if (t.StartsWith('!') || t.StartsWith("Freq", StringComparison.OrdinalIgnoreCase)) continue;

                if (t.StartsWith('#'))
                {
                    // New grid point: "# NNN  Γmag  Γphase(deg)"
                    currentDriveup = new List<double[]>();
                    driveups.Add(currentDriveup);
                    var gp = ParseGammaLine(t);
                    gridGammas.Add(gp);
                }
                else if (currentDriveup is not null)
                {
                    // Data row: values for FOM columns
                    var vals = ParseDoubleRow(t, nDataCols);
                    if (vals is not null)
                        currentDriveup.Add(vals);
                }
            }

            int nGrid = gridGammas.Count;
            if (nGrid == 0)
                throw new FormatException(".lpcwave: no grid points (# lines) found.");

            // Find max drive-up length (for ragged NaN-padding)
            int nPin = 0;
            foreach (var du in driveups)
                nPin = Math.Max(nPin, du.Count);
            if (nPin == 0) nPin = 1;

            // ── Build pin axis from first grid point ────────────────────────────
            var pinAxis = new double[nPin];
            if (driveups.Count > 0)
            {
                var du0 = driveups[0];
                for (int pi = 0; pi < nPin; pi++)
                {
                    if (pi < du0.Count && pinRelIdx >= 0 && pinRelIdx < du0[pi].Length)
                        pinAxis[pi] = du0[pi][pinRelIdx];
                    else
                        pinAxis[pi] = double.NaN;
                }
            }

            // ── Allocate FOM buffers ─────────────────────────────────────────────
            var block = new FreqBlock
            {
                FreqGHz   = freqGhz,
                NGrid     = nGrid,
                NPin      = nPin,
                IsLoadpull = isLoadpull,
                GammaLoad  = gridGammas.ToArray(),
                PinAxis    = pinAxis,
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

            // ── Fill FOM data ───────────────────────────────────────────────────
            for (int gi = 0; gi < nGrid; gi++)
            {
                var du = gi < driveups.Count ? driveups[gi] : null;
                for (int pi = 0; pi < nPin; pi++)
                {
                    int idx = gi * nPin + pi;
                    if (du is null || pi >= du.Count) continue; // stays NaN

                    var row = du[pi];
                    foreach (var (relIdx, canon, scale) in fomCols)
                    {
                        double v = relIdx < row.Length ? row[relIdx] : double.NaN;
                        block.Foms[canon][idx] = double.IsNaN(v) ? v : LoadpullFomDialect.Apply(v, scale);
                    }
                }
            }

            // Overwrite PavlDbm with pin-axis values for consistency with pinStep axis.
            if (!block.Foms.ContainsKey("PavlDbm"))
                block.Foms["PavlDbm"] = new double[nGrid * nPin];
            for (int gi2 = 0; gi2 < nGrid; gi2++)
                for (int pi2 = 0; pi2 < nPin; pi2++)
                    block.Foms["PavlDbm"][gi2 * nPin + pi2] = block.PinAxis[pi2];

            // ── Capture source Γ from first grid/pin (setup constant, MA pair) ───
            if (srcGMagRelIdx >= 0 && srcGPhRelIdx >= 0 && driveups.Count > 0 && driveups[0].Count > 0)
            {
                var r0  = driveups[0][0];
                double mag = srcGMagRelIdx < r0.Length ? r0[srcGMagRelIdx] : double.NaN;
                double phd = srcGPhRelIdx  < r0.Length ? r0[srcGPhRelIdx]  : double.NaN;
                if (!double.IsNaN(mag) && !double.IsNaN(phd))
                {
                    double phR = phd * Math.PI / 180.0;
                    block.SourceGamma    = new Complex(mag * Math.Cos(phR), mag * Math.Sin(phR));
                    block.HasSourceGamma = true;
                }
            }

            // ── Allocate raw derivation capture arrays ───────────────────────────
            bool hasGin = ginMagRelIdx >= 0 && ginPhRelIdx >= 0;
            if (hasGin)
            {
                block.GinMag   = new double[nGrid * nPin];
                block.GinPhase = new double[nGrid * nPin];
                Array.Fill(block.GinMag,   double.NaN);
                Array.Fill(block.GinPhase, double.NaN);
            }
            if (transPhRelIdx >= 0)
            {
                block.TransPhase = new double[nGrid * nPin];
                Array.Fill(block.TransPhase, double.NaN);
            }

            if (hasGin || transPhRelIdx >= 0)
            {
                for (int gi = 0; gi < nGrid; gi++)
                {
                    var du = gi < driveups.Count ? driveups[gi] : null;
                    for (int pi = 0; pi < nPin; pi++)
                    {
                        if (du is null || pi >= du.Count) continue;
                        var row = du[pi];
                        int idx = gi * nPin + pi;
                        if (hasGin)
                        {
                            block.GinMag![idx]   = ginMagRelIdx < row.Length ? row[ginMagRelIdx]  : double.NaN;
                            block.GinPhase![idx] = ginPhRelIdx  < row.Length ? row[ginPhRelIdx]   : double.NaN;
                        }
                        if (transPhRelIdx >= 0)
                            block.TransPhase![idx] = transPhRelIdx < row.Length ? row[transPhRelIdx] : double.NaN;
                    }
                }
            }

            LoadpullDerivedFields.Derive(
                block.Foms, block.NGrid, block.NPin,
                block.GinMag, block.GinPhase, block.TransPhase, block.ReflDb, block.ReflLin);

            return block;
        }

        // ── Assemble DataSet ─────────────────────────────────────────────────────

        private static DataSet AssembleDataSet(List<FreqBlock> blocks, bool multiFreq)
        {
            var ds = new DataSet();
            if (blocks.Count == 0) return ds;

            if (!multiFreq)
            {
                var b0 = blocks[0];
                AddFreqSlice(ds, b0);
                if (b0.HasSourceGamma)
                {
                    var fa = new Axis("freq", new[] { b0.FreqGHz * 1e9 }, "Hz");
                    ds.Add("ZSource", new DataCube(new[] { fa }, new Complex[] { GammaToZ(b0.SourceGamma) }));
                }
            }
            else
            {
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
                var gridAxis = MakeGridAxis(blocks[0]);
                var pinAxis  = MakePinAxis(blocks[0]);

                var allFoms = new HashSet<string>(StringComparer.Ordinal);
                foreach (var b in blocks)
                    foreach (var k in b.Foms.Keys)
                        allFoms.Add(k);

                foreach (var canonName in allFoms)
                {
                    var buf = new double[nF * nGrid * nPin];
                    for (int fi = 0; fi < nF; fi++)
                    {
                        if (!blocks[fi].Foms.TryGetValue(canonName, out var slice))
                        {
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

                var gammaAll = new Complex[nF * nGrid];
                var zAll     = new Complex[nF * nGrid];
                for (int fi = 0; fi < nF; fi++)
                {
                    for (int gi = 0; gi < blocks[fi].NGrid && gi < nGrid; gi++)
                    {
                        var g = blocks[fi].GammaLoad[gi];
                        gammaAll[fi * nGrid + gi] = g;
                        zAll[fi * nGrid + gi]     = GammaToZ(g);
                    }
                }
                ds.Add("GammaLoad", new DataCube(new[] { freqAxis, gridAxis }, gammaAll));
                ds.Add("ZLoad",     new DataCube(new[] { freqAxis, gridAxis }, zAll));

                // ZSource — rank-1 {freq} cube, presence-gated
                bool anySource = false;
                foreach (var b in blocks) if (b.HasSourceGamma) { anySource = true; break; }
                if (anySource)
                {
                    var zSrcVals = new Complex[nF];
                    for (int fi = 0; fi < nF; fi++)
                        zSrcVals[fi] = blocks[fi].HasSourceGamma ? GammaToZ(blocks[fi].SourceGamma) : Complex.Zero;
                    ds.Add("ZSource", new DataCube(new[] { freqAxis }, zSrcVals));
                }
            }

            return ds;
        }

        private static void AddFreqSlice(DataSet ds, FreqBlock b)
        {
            var gridAxis = MakeGridAxis(b);
            var pinAxis  = MakePinAxis(b);

            foreach (var (canonName, data) in b.Foms)
                ds.Add(canonName, new DataCube(new[] { gridAxis, pinAxis }, data));

            var zLoad = new Complex[b.NGrid];
            for (int gi = 0; gi < b.NGrid; gi++)
                zLoad[gi] = GammaToZ(b.GammaLoad[gi]);

            ds.Add("GammaLoad", new DataCube(new[] { gridAxis }, b.GammaLoad));
            ds.Add("ZLoad",     new DataCube(new[] { gridAxis }, zLoad));

            // Preserve the single measured frequency on a rank-1 "__Freq" carrier so the surface
            // engine reports the real freq (not 0) for single-freq datasets. "__"-prefixed cubes
            // are hidden from the trace picker.
            ds.Add("__Freq", new DataCube(new[] { new Axis("freq", new[] { b.FreqGHz * 1e9 }, "Hz") },
                                          new[] { b.FreqGHz * 1e9 }));
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
                labels[i] = double.IsNaN(b.PinAxis[i]) ? $"{i}" : $"{b.PinAxis[i]:G4}";
            return new Axis("pinStep", b.PinAxis, "", labels);
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        // Parse "# NNN  Gmag  Gphase(deg)" → complex Γ
        private static Complex ParseGammaLine(string line)
        {
            // Remove leading '#' and parse remaining tokens
            var rest   = line.TrimStart('#', ' ').Trim();
            var tokens = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 3) return Complex.Zero;

            // tokens[0] = point index (skip), tokens[1] = Γ mag, tokens[2] = phase deg
            double mag = ParseDouble(tokens[1]);
            double phaseDeg = ParseDouble(tokens[2]);
            double phaseRad = phaseDeg * Math.PI / 180.0;
            return new Complex(mag * Math.Cos(phaseRad), mag * Math.Sin(phaseRad));
        }

        private static double ParseDouble(string s) =>
            double.TryParse(s, System.Globalization.NumberStyles.Float |
                               System.Globalization.NumberStyles.AllowExponent,
                System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : double.NaN;

        private static double[]? ParseDoubleRow(string line, int expectedCount)
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;
            var result = new double[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                result[i] = ParseDouble(parts[i]);
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
