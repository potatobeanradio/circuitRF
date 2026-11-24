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
using System.Globalization;
using System.IO;
using System.Linq;
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

        /// <summary>
        /// One frequency's loadpull, before assembly: FOMs over <c>gi*NPin + pi</c>, the load grid, and the complex cubes
        /// the waves give. Built by this reader, by <see cref="SatwaveReader"/> through <see cref="BuildBlock"/>, and by
        /// <see cref="CstReader"/> directly; assembled by <see cref="Assemble"/> for all three.
        /// </summary>
        internal sealed class FreqBlock
        {
            public double FreqGHz;
            public int NGrid;
            public int NPin;           // max drive-up steps (ragged → NaN-padded)
            public bool IsLoadpull = true; // false = sourcepull
            public Complex[] GammaLoad = Array.Empty<Complex>();   // referenced to 50 Ω
            public double    Z0Load = 50.0;                        // the reference ZLoad is computed at
            public double[]  PinAxis   = Array.Empty<double>();
            public Dictionary<string, double[]> Foms = new(StringComparer.Ordinal);
            /// <summary>Complex cubes over {gridPoint, pinStep} (the measured load per drive).</summary>
            public Dictionary<string, Complex[]> DriveComplex = new(StringComparer.Ordinal);
            /// <summary>Complex cubes over {gridPoint} (the per-load harmonic loads).</summary>
            public Dictionary<string, Complex[]> LoadComplex = new(StringComparer.Ordinal);
            // Raw derivation inputs (null when column absent)
            public double[]? GinMag;       // |Γin| (linear)
            public double[]? GinPhase;     // ∠Γin (degrees)
            public double[]? TransPhase;   // transducer phase (degrees)
            public Complex   SourceGamma;  // per-freq source Γ (from first grid/pin, or the header)
            public bool      HasSourceGamma;
            public double    Z0Source = 50.0;
            public Complex   FixedLoadZ;   // a sourcepull's "! Load Impedance", Ω
            public bool      HasFixedLoadZ;
        }

        private static FreqBlock ParseFreqBlock(List<string> lines, double freqGhz)
        {
            // ── Detect sourcepull vs loadpull from FileInfo comment ──────────────
            // "! Source Impedance = ..." → source is fixed → load is swept → loadpull
            // "! Load Impedance = ..."   → load is fixed → source is swept → sourcepull
            bool isLoadpull = true;
            Complex? fixedLoadZ = null;
            foreach (var l in lines)
            {
                var t = l.Trim();
                if (t.StartsWith("! Load Impedance", StringComparison.OrdinalIgnoreCase))
                { isLoadpull = false; fixedLoadZ = ParseImpedanceComment(t); break; }
                if (t.StartsWith("! Source Impedance", StringComparison.OrdinalIgnoreCase))
                { isLoadpull = true; break; }
            }
            var header = ReadHeader(lines);

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
            const int dataOffset = 3; // skip Point, Gamma, Phase[deg]
            var columns = headerParts.Length > dataOffset ? headerParts[dataOffset..] : Array.Empty<string>();

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
                    var vals = ParseDoubleRow(t, columns.Length);
                    if (vals is not null)
                        currentDriveup.Add(vals);
                }
            }

            if (gridGammas.Count == 0)
                throw new FormatException(".lpcwave: no grid points (# lines) found.");

            return BuildBlock(columns, gridGammas, driveups, freqGhz, isLoadpull, fixedLoadZ, header);
        }

        /// <summary>What a <c>!</c> header states that the waves need: the characteristic impedances and the source Γ.</summary>
        internal readonly record struct WaveHeader(double Z0Source, double Z0Load, Complex? SourceGamma);

        /// <summary>
        /// Reads <c>! Char.Impedances = Source: 50 + 0j Ohm   Load: 50 + 0j Ohm</c> and the source termination at f0 —
        /// <c>! Source Impedance (Polar) = 0.32, -179.65(deg)</c>, <c>! Source Impedances (Polar) F0: 0.32, -179.65(deg)</c>
        /// or <c>! Source Impedances (Polar) = F0: 0.320&lt;-179.6(deg)</c>.
        /// </summary>
        internal static WaveHeader ReadHeader(IEnumerable<string> lines)
        {
            double z0s = 50.0, z0l = 50.0;
            Complex? gs = null;
            foreach (var l in lines)
            {
                var t = l.Trim();
                if (!t.StartsWith('!')) continue;
                var body = t.TrimStart('!').Trim();
                if (body.StartsWith("Char.Impedances", StringComparison.OrdinalIgnoreCase))
                {
                    z0s = RealAfter(body, "Source:") ?? z0s;
                    z0l = RealAfter(body, "Load:") ?? z0l;
                }
                else if (gs is null && body.StartsWith("Source Impedance", StringComparison.OrdinalIgnoreCase)
                         && body.Contains("(Polar)", StringComparison.OrdinalIgnoreCase))
                {
                    gs = ParsePolarF0(body);
                }
            }
            return new WaveHeader(z0s, z0l, gs);
        }

        /// <summary>The first number after <paramref name="key"/> in <paramref name="s"/>, or null.</summary>
        private static double? RealAfter(string s, string key)
        {
            int k = s.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (k < 0) return null;
            var tokens = s[(k + key.Length)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return tokens.Length > 0 && double.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                ? v : null;
        }

        /// <summary>
        /// The f0 reflection of a polar header: the text after <c>=</c> (and after <c>F0:</c> when present), read as
        /// <c>mag, deg</c> or <c>mag&lt;deg</c>, up to <c>(deg)</c>. Null when it does not read.
        /// </summary>
        internal static Complex? ParsePolarF0(string body)
        {
            string rest = body;
            int f0 = rest.IndexOf("F0:", StringComparison.OrdinalIgnoreCase);
            if (f0 >= 0) rest = rest[(f0 + 3)..];
            else
            {
                int eq = rest.IndexOf('=');
                if (eq < 0) return null;
                rest = rest[(eq + 1)..];
            }
            int degEnd = rest.IndexOf("(deg)", StringComparison.OrdinalIgnoreCase);
            if (degEnd < 0) return null;
            var parts = rest[..degEnd].Split(new[] { ',', '<', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return null;
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double mag)) return null;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double deg)) return null;
            return Complex.FromPolarCoordinates(mag, deg * Math.PI / 180.0);
        }

        /// <summary>
        /// The column handling every <c>.lpcwave</c>-dialect file shares (R-lpw-5): maps the data-row
        /// <paramref name="columns"/> through <see cref="LoadpullFomDialect"/>, pads ragged drive-ups with NaN, reads the
        /// wave columns (<c>A1reF0 … B2imF0</c>, and <c>…F1</c>, <c>…F2</c> for 2f0, 3f0) as RMS, and derives what the
        /// file does not state. A <c>.lpcwave</c> hands it one grid point per <c>#</c> line; a <c>.satwave</c> one.
        /// </summary>
        internal static FreqBlock BuildBlock(
            IReadOnlyList<string> columns, List<Complex> gridGammas, List<List<double[]>> driveups,
            double freqGhz, bool isLoadpull, Complex? fixedLoadZ, WaveHeader header)
        {
            int Col(string name)
            {
                for (int i = 0; i < columns.Count; i++)
                    if (columns[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
                return -1;
            }

            // ── Drive column: the available power AT THE DEVICE, PinWaves, over the generator's setting, Psource ─
            // Both map to PavlDbm, and they are not the same power: in a measured file Psource sat 24 dB below PinWaves (a
            // driver between them), while the file's own transducer gain is PoutWaves − PinWaves. So a file stating
            // PinWaves takes it, load by load, and its Psource is not read as PavlDbm.
            int pinWavesIdx = Col("PinWaves[dBm]"), psourceIdx = Col("Psource[dBm]");
            int pinRelIdx = pinWavesIdx >= 0 ? pinWavesIdx : psourceIdx;
            bool perLoadDrive = pinWavesIdx >= 0;

            var fomCols = new List<(int RelIdx, string CanonicalName, FomScale Scale)>();
            for (int i = 0; i < columns.Count; i++)
                if (LoadpullFomDialect.Map.TryGetValue(columns[i], out var entry) && !(perLoadDrive && i == psourceIdx))
                    fomCols.Add((i, entry.CanonicalName, entry.Scale));

            // ── Raw derivation input columns (not in dialect Map) ────────────────
            int ginMagRelIdx  = -1, ginPhRelIdx  = -1;
            int transPhRelIdx = -1;
            int srcGMagRelIdx = -1, srcGPhRelIdx = -1;
            for (int rel = 0; rel < columns.Count; rel++)
            {
                var h = columns[rel];
                if      (h.Equals("|GinWaves@F0|",       StringComparison.OrdinalIgnoreCase)) ginMagRelIdx  = rel;
                else if (h.Equals("PhiinWaves@F0[deg]",  StringComparison.OrdinalIgnoreCase)) ginPhRelIdx   = rel;
                else if (h.Equals("PhiLWaves@F0[deg]",   StringComparison.OrdinalIgnoreCase)) transPhRelIdx = rel;
                else if (h.Equals("|GS@F0|",             StringComparison.OrdinalIgnoreCase)) srcGMagRelIdx = rel;
                else if (h.Equals("PhiS@F0[deg]",        StringComparison.OrdinalIgnoreCase)) srcGPhRelIdx  = rel;
                // SPL-style names (lpcwave files may mix both dialects)
                else if (h.Equals("Gamma_in_mag",    StringComparison.OrdinalIgnoreCase) && ginMagRelIdx  < 0) ginMagRelIdx  = rel;
                else if (h.Equals("Gamma_in_phase",  StringComparison.OrdinalIgnoreCase) && ginPhRelIdx   < 0) ginPhRelIdx   = rel;
                else if (h.Equals("trans_phase",     StringComparison.OrdinalIgnoreCase) && transPhRelIdx < 0) transPhRelIdx = rel;
            }

            int nGrid = gridGammas.Count;

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
                    if (pinRelIdx < 0)
                        pinAxis[pi] = pi + 1;            // no drive column: the step number (PavlDbm comes from the waves)
                    else if (pi < du0.Count && pinRelIdx < du0[pi].Length)
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
                FixedLoadZ = fixedLoadZ ?? Complex.Zero,
                HasFixedLoadZ = fixedLoadZ is not null,
                GammaLoad  = gridGammas.ToArray(),
                PinAxis    = pinAxis,
                Z0Source   = header.Z0Source,
                Z0Load     = header.Z0Load,
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

            // Every column of every row, NaN where the drive-up is short — the one place a ragged row is padded.
            double Cell(int gi, int pi, int rel)
            {
                var du = gi < driveups.Count ? driveups[gi] : null;
                if (du is null || pi >= du.Count || rel < 0) return double.NaN;
                var row = du[pi];
                return rel < row.Length ? row[rel] : double.NaN;
            }

            // ── Fill FOM data ───────────────────────────────────────────────────
            for (int gi = 0; gi < nGrid; gi++)
                for (int pi = 0; pi < nPin; pi++)
                {
                    int idx = gi * nPin + pi;
                    foreach (var (relIdx, canon, scale) in fomCols)
                    {
                        double v = Cell(gi, pi, relIdx);
                        if (!double.IsNaN(v)) block.Foms[canon][idx] = LoadpullFomDialect.Apply(v, scale);
                    }
                }

            // A generator setting (Psource) is one ladder for every load: PavlDbm is the pin axis at every grid point.
            // PinWaves is measured per load and is kept as it reads.
            if (!perLoadDrive && pinRelIdx >= 0)
            {
                if (!block.Foms.ContainsKey("PavlDbm"))
                    block.Foms["PavlDbm"] = new double[nGrid * nPin];
                for (int gi2 = 0; gi2 < nGrid; gi2++)
                    for (int pi2 = 0; pi2 < nPin; pi2++)
                        block.Foms["PavlDbm"][gi2 * nPin + pi2] = block.PinAxis[pi2];
            }

            // ── Source Γ: the setup constant (MA pair columns, first grid/pin), else the header; a sourcepull's is the swept one ─
            if (isLoadpull && srcGMagRelIdx >= 0 && srcGPhRelIdx >= 0)
            {
                double mag = Cell(0, 0, srcGMagRelIdx), phd = Cell(0, 0, srcGPhRelIdx);
                if (!double.IsNaN(mag) && !double.IsNaN(phd))
                {
                    double phR = phd * Math.PI / 180.0;
                    block.SourceGamma    = new Complex(mag * Math.Cos(phR), mag * Math.Sin(phR));
                    block.HasSourceGamma = true;
                }
            }
            if (isLoadpull && !block.HasSourceGamma && header.SourceGamma is { } hgs)
            {
                block.SourceGamma    = hgs;
                block.HasSourceGamma = true;
            }

            // ── Raw derivation capture arrays ────────────────────────────────────
            double[] Capture(int rel)
            {
                var a = new double[nGrid * nPin];
                for (int gi = 0; gi < nGrid; gi++)
                    for (int pi = 0; pi < nPin; pi++)
                        a[gi * nPin + pi] = Cell(gi, pi, rel);
                return a;
            }
            if (ginMagRelIdx >= 0 && ginPhRelIdx >= 0)
            {
                block.GinMag   = Capture(ginMagRelIdx);
                block.GinPhase = Capture(ginPhRelIdx);
            }
            if (transPhRelIdx >= 0)
                block.TransPhase = Capture(transPhRelIdx);

            // .lpcwave has no reflection-coefficient/return-loss column (unlike .spl's "Refl_dB"/
            // "ReflectCoefficient") — confirmed directly against the real test fixtures' own column
            // headers, not assumed — so IRL_dB here comes from |Γin| (or, below, from the waves).
            LoadpullDerivedFields.Derive(
                block.Foms, block.NGrid, block.NPin,
                block.GinMag, block.GinPhase, block.TransPhase, reflDb: null, reflLin: null);

            // ── The waves (R-lpw-2): A1reF0 … B2imF0, then …F1 (2f0), …F2 (3f0) while all eight are there ─
            // RMS: this dialect's own computed columns (PinDelWaves = |a1|² − |b1|², PoutWaves = |b2|² − |a2|², in W) are
            // reproduced from its wave columns with P = |x|² to the file's rounding.
            var a1 = new List<Complex[]>(); var b1 = new List<Complex[]>();
            var a2 = new List<Complex[]>(); var b2 = new List<Complex[]>();
            for (int k = 0; ; k++)
            {
                int[] idx =
                {
                    Col($"A1reF{k}"), Col($"A1imF{k}"), Col($"B1reF{k}"), Col($"B1imF{k}"),
                    Col($"A2reF{k}"), Col($"A2imF{k}"), Col($"B2reF{k}"), Col($"B2imF{k}"),
                };
                if (Array.Exists(idx, i => i < 0)) break;
                Complex[] Wave(int re, int im)
                {
                    var w = new Complex[nGrid * nPin];
                    for (int gi = 0; gi < nGrid; gi++)
                        for (int pi = 0; pi < nPin; pi++)
                            w[gi * nPin + pi] = new Complex(Cell(gi, pi, re), Cell(gi, pi, im));
                    return w;
                }
                a1.Add(Wave(idx[0], idx[1])); b1.Add(Wave(idx[2], idx[3]));
                a2.Add(Wave(idx[4], idx[5])); b2.Add(Wave(idx[6], idx[7]));
            }
            if (a1.Count > 0 && isLoadpull)
            {
                block.Foms.TryGetValue("BiasVSrc", out var vin);
                block.Foms.TryGetValue("BiasISrc", out var iin);
                block.Foms.TryGetValue("BiasVLoad", out var vout);
                block.Foms.TryGetValue("BiasILoad", out var iout);
                LoadpullWaves.Derive(new LoadpullWaveInputs
                {
                    NGrid = nGrid, NPin = nPin, Convention = WaveConvention.Rms,
                    Z0Source = header.Z0Source, Z0Load = header.Z0Load,
                    GammaSource = block.HasSourceGamma ? block.SourceGamma : Complex.Zero,
                    A1 = a1, B1 = b1, A2 = a2, B2 = b2,
                    Vin = vin, Iin = iin, Vout = vout, Iout = iout,
                }, block.Foms, block.DriveComplex, block.LoadComplex);
            }

            return block;
        }

        // ── Assemble DataSet ─────────────────────────────────────────────────────

        /// <summary>
        /// The canonical loadpull DataSet from parsed blocks — one per frequency; more than one is a leading <c>freq</c>
        /// axis. Blocks with different load or drive counts are NaN-padded to the largest of each. Shared by every
        /// reader of this family (R-lpw-4/5), so a <c>.cst</c>, a <c>.satwave</c> and a <c>.lpcwave</c> have one shape.
        /// <paramref name="provenance"/>, when given, is kept as a hidden <c>__Provenance</c> cube whose axis labels are
        /// the lines.
        /// </summary>
        internal static DataSet Assemble(List<FreqBlock> blocks, IReadOnlyList<string>? provenance = null)
        {
            var ds = AssembleDataSet(blocks, blocks.Count > 1);
            if (provenance is { Count: > 0 })
            {
                var vals = new double[provenance.Count];
                for (int i = 0; i < vals.Length; i++) vals[i] = i;
                ds.Add(ProvenanceCube, new DataCube(new[] { new Axis("line", vals, "", provenance.ToArray()) }, vals));
            }
            return ds;
        }

        /// <summary>The hidden cube a reader keeps a file's provenance lines in (axis labels), e.g. a <c>.cst</c>'s
        /// measurement type, swept harmonic, source termination, pulse and bias lines.</summary>
        public const string ProvenanceCube = "__Provenance";

        private static DataSet AssembleDataSet(List<FreqBlock> blocks, bool multiFreq)
        {
            var ds = new DataSet();
            if (blocks.Count == 0) return ds;
            if (multiFreq) PadToCommonShape(blocks);

            // A sourcepull says so, and carries its fixed load — rank 0 for one frequency, over {freq} for several.
            if (!blocks[0].IsLoadpull)
            {
                ds.Add(LoadpullExportModel.SweptSourceCube, new DataCube(Array.Empty<Axis>(), new[] { 1.0 }));
                if (blocks.TrueForAll(b => b.HasFixedLoadZ))
                {
                    var zl = blocks.ConvertAll(b => b.FixedLoadZ).ToArray();
                    var fq = blocks.ConvertAll(b => b.FreqGHz * 1e9).ToArray();
                    ds.Add(LoadpullExportModel.FixedLoadZCube, blocks.Count == 1
                        ? new DataCube(Array.Empty<Axis>(), zl)
                        : new DataCube(new[] { new Axis("freq", fq, "Hz") }, zl));
                }
            }

            if (!multiFreq)
            {
                var b0 = blocks[0];
                AddFreqSlice(ds, b0);
                if (b0.HasSourceGamma)
                {
                    var fa = new Axis("freq", new[] { b0.FreqGHz * 1e9 }, "Hz");
                    ds.Add("ZSource", new DataCube(new[] { fa }, new Complex[] { GammaToZ(b0.SourceGamma, b0.Z0Source) }));
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
                var allDrive = new HashSet<string>(StringComparer.Ordinal);
                var allLoad = new HashSet<string>(StringComparer.Ordinal);
                foreach (var b in blocks)
                {
                    allFoms.UnionWith(b.Foms.Keys);
                    allDrive.UnionWith(b.DriveComplex.Keys);
                    allLoad.UnionWith(b.LoadComplex.Keys);
                }

                foreach (var canonName in allFoms)
                {
                    var buf = new double[nF * nGrid * nPin];
                    for (int fi = 0; fi < nF; fi++)
                    {
                        if (!blocks[fi].Foms.TryGetValue(canonName, out var slice))
                            Array.Fill(buf, double.NaN, fi * nGrid * nPin, nGrid * nPin);
                        else
                            Array.Copy(slice, 0, buf, fi * nGrid * nPin, nGrid * nPin);
                    }
                    ds.Add(canonName, new DataCube(new[] { freqAxis, gridAxis, pinAxis }, buf));
                }
                foreach (var name in allDrive)
                    ds.Add(name, new DataCube(new[] { freqAxis, gridAxis, pinAxis },
                        Stack(blocks, b => b.DriveComplex.GetValueOrDefault(name), nGrid * nPin)));
                foreach (var name in allLoad)
                    ds.Add(name, new DataCube(new[] { freqAxis, gridAxis },
                        Stack(blocks, b => b.LoadComplex.GetValueOrDefault(name), nGrid)));

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
                        zSrcVals[fi] = blocks[fi].HasSourceGamma ? GammaToZ(blocks[fi].SourceGamma, blocks[fi].Z0Source) : Complex.Zero;
                    ds.Add("ZSource", new DataCube(new[] { freqAxis }, zSrcVals));
                }
            }

            return ds;
        }

        private static Complex[] Stack(List<FreqBlock> blocks, Func<FreqBlock, Complex[]?> pick, int per)
        {
            var buf = new Complex[blocks.Count * per];
            for (int fi = 0; fi < blocks.Count; fi++)
            {
                var slice = pick(blocks[fi]);
                if (slice is null) Array.Fill(buf, NaNC, fi * per, per);
                else Array.Copy(slice, 0, buf, fi * per, per);
            }
            return buf;
        }

        private static readonly Complex NaNC = new(double.NaN, double.NaN);

        /// <summary>
        /// Pads every block to the largest load count and the largest drive count among them, NaN where a block has
        /// fewer, so the per-frequency slices stack. A padded load's Γ is NaN.
        /// </summary>
        private static void PadToCommonShape(List<FreqBlock> blocks)
        {
            int nGrid = 0, nPin = 0;
            foreach (var b in blocks) { nGrid = Math.Max(nGrid, b.NGrid); nPin = Math.Max(nPin, b.NPin); }
            foreach (var b in blocks)
            {
                if (b.NGrid == nGrid && b.NPin == nPin) continue;
                double[] PadR(double[] v)
                {
                    var r = new double[nGrid * nPin];
                    Array.Fill(r, double.NaN);
                    for (int gi = 0; gi < b.NGrid; gi++) Array.Copy(v, gi * b.NPin, r, gi * nPin, b.NPin);
                    return r;
                }
                Complex[] PadC(Complex[] v, int per, int newPer, int rows)
                {
                    var r = new Complex[nGrid * newPer];
                    Array.Fill(r, NaNC);
                    for (int gi = 0; gi < rows; gi++) Array.Copy(v, gi * per, r, gi * newPer, per);
                    return r;
                }
                foreach (var k in new List<string>(b.Foms.Keys)) b.Foms[k] = PadR(b.Foms[k]);
                foreach (var k in new List<string>(b.DriveComplex.Keys)) b.DriveComplex[k] = PadC(b.DriveComplex[k], b.NPin, nPin, b.NGrid);
                foreach (var k in new List<string>(b.LoadComplex.Keys)) b.LoadComplex[k] = PadC(b.LoadComplex[k], 1, 1, b.NGrid);
                b.GammaLoad = PadC(b.GammaLoad, 1, 1, b.NGrid);
                var pin = new double[nPin];
                Array.Fill(pin, double.NaN);
                Array.Copy(b.PinAxis, pin, b.PinAxis.Length);
                b.PinAxis = pin;
                b.NGrid = nGrid; b.NPin = nPin;
            }
        }

        private static void AddFreqSlice(DataSet ds, FreqBlock b)
        {
            var gridAxis = MakeGridAxis(b);
            var pinAxis  = MakePinAxis(b);

            foreach (var (canonName, data) in b.Foms)
                ds.Add(canonName, new DataCube(new[] { gridAxis, pinAxis }, data));
            foreach (var (name, data) in b.DriveComplex)
                ds.Add(name, new DataCube(new[] { gridAxis, pinAxis }, data));
            foreach (var (name, data) in b.LoadComplex)
                ds.Add(name, new DataCube(new[] { gridAxis }, data));

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
        /// <summary>"! Load Impedance = R +j X Ohm" (also "R -j X", "R + jX") → R + jX, or null when it does not read.</summary>
        private static Complex? ParseImpedanceComment(string line)
        {
            int eq = line.IndexOf('=');
            if (eq < 0) return null;
            string t = line[(eq + 1)..].Replace("Ohm", "", StringComparison.OrdinalIgnoreCase).Replace(" ", "");
            int j = t.IndexOf('j');
            if (j < 1) return null;
            char sign = t[j - 1];
            if (sign is not ('+' or '-')) return null;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (!double.TryParse(t[..(j - 1)], System.Globalization.NumberStyles.Float, inv, out double re)) return null;
            if (!double.TryParse(t[(j + 1)..], System.Globalization.NumberStyles.Float, inv, out double im)) return null;
            return new Complex(re, sign == '-' ? -im : im);
        }

        private static Complex GammaToZ(Complex gamma, double z0 = 50.0)
        {
            var denom = 1.0 - gamma;
            if (denom.Magnitude < 1e-12) return new Complex(z0 * 1e6, 0);
            return z0 * (1.0 + gamma) / denom;
        }
    }
}
