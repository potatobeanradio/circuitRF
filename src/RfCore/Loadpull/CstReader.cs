// ================================================================
//  CstReader.cs — .cst measured-wave loadpull reader
//
//  Brief LPW, R-lpw-4. A .cst holds one block per f0 load:
//
//      #MEASUREMENT TYPE: Power Sweep
//      #FUNDAMENTAL FREQUENCY: 14.0 GHz
//      #HARMONICS: 2                    ← harmonics ABOVE f0 (rows per drive = n + 1)
//      #SWEPT HARMONIC: 0
//      #Z0SOURCE: 50.0
//      #Z0LOAD: 50.0
//      #GAMMASOURCE: +7.8E-01 +4.6E+01  ← |Γ| ∠deg, optional
//      #GAMMALOAD: +7.5E-01 +2.5E+01    ← |Γ| ∠deg — the f0 load this block measured
//      #OPERATING CONDITIONS / #BIAS PATH(S) / #DC PATH / #QUIESCENT … / #PULSE …
//      #Frequency(Hz) Vin(V) Iin(A) Vout(V) Iout(A) Re[a1] Im[a1] Re[b1] Im[b1] Re[a2] Im[a2] Re[b2] Im[b2]
//      #BEGIN
//      +1.7E+10 …                       ← drive 1, f0
//      +3.4E+10 …                       ← drive 1, 2f0 (DC columns repeat)
//      +1.7E+10 …                       ← drive 2, f0
//      #END
//
//  Blocks are ragged (different drive counts); only the f0 load varies
//  between blocks, and the harmonic loads are not in the header — they
//  come from the waves (LoadpullWaves, |b2|²-weighted). CRLF or LF.
//
//  Every metric comes from LoadpullWaves; the assembly is LpcwaveReader's,
//  so a .cst has exactly the shape of the other loadpull files.
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
    public static class CstReader
    {
        /// <summary>
        /// The waves of a .cst are PEAK amplitudes (P = ½|a|²). The format states no convention; read as RMS, the
        /// measured blocks this reader was developed against give drain efficiencies up to 97 % and about 12–13 W/mm on
        /// their stated peripheries, which no device of that kind delivers; read as peak they give 35–49 % and
        /// 6–7 W/mm. The .lpcwave dialect's waves, by contrast, are RMS: its own computed columns say so.
        /// </summary>
        public const WaveConvention Convention = WaveConvention.Peak;

        /// <summary>The header keys kept on the DataSet as provenance (<see cref="LpcwaveReader.ProvenanceCube"/>).</summary>
        private static readonly string[] ProvenanceKeys =
        {
            "MEASUREMENT TYPE", "SWEPT HARMONIC", "GAMMASOURCE", "OPERATING CONDITIONS", "BIAS PATH(S)", "DC PATH",
            "QUIESCENT COMMAND", "QUIESCENT POINT", "PULSE COMMAND", "PULSE POINT",
        };

        public static DataSet Read(string path)
        {
            using var reader = new StreamReader(path);
            return Read(reader);
        }

        public static DataSet Read(TextReader reader)
        {
            var blocks = ParseBlocks(reader);
            if (blocks.Count == 0) throw new FormatException(".cst: no #BEGIN … #END block.");

            // Blocks grouped by fundamental, in the order each first appears: more than one is a leading freq axis.
            var freqs = new List<double>();
            foreach (var b in blocks)
                if (!freqs.Exists(f => Math.Abs(f - b.F0Hz) <= 1e-9 * Math.Max(1.0, f))) freqs.Add(b.F0Hz);
            var freqBlocks = new List<LpcwaveReader.FreqBlock>();
            foreach (var f in freqs)
                freqBlocks.Add(BuildFreqBlock(blocks.FindAll(b => Math.Abs(b.F0Hz - f) <= 1e-9 * Math.Max(1.0, f))));

            var provenance = new List<string>();
            foreach (var b in blocks)
                foreach (var line in b.Provenance)
                    if (!provenance.Contains(line)) provenance.Add(line);
            return LpcwaveReader.Assemble(freqBlocks, provenance);
        }

        // ── One load's block, as read ───────────────────────────────────────

        private sealed class Block
        {
            public int Number;                 // 1-based, in file order, for refusals
            public double F0Hz;
            public int Harmonics;              // rows per drive = Harmonics + 1
            public double Z0Source = 50, Z0Load = 50;
            public Complex? GammaSource;
            public Complex GammaLoad;
            public List<string> Provenance = new();
            // per drive, per harmonic: a1, b1, a2, b2; and the DC columns from the drive's f0 row
            public List<Complex[][]> Waves = new();   // [drive][harmonic] = {a1, b1, a2, b2}
            public List<double[]> Dc = new();          // [drive] = {Vin, Iin, Vout, Iout}
        }

        private static List<Block> ParseBlocks(TextReader reader)
        {
            var blocks = new List<Block>();
            var header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var provenance = new List<string>();
            string[]? titles = null;
            List<double[]>? rows = null;
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var t = line.Trim();
                if (t.Length == 0) continue;
                if (t.StartsWith('#'))
                {
                    var body = t[1..].Trim();
                    if (body.Equals("BEGIN", StringComparison.OrdinalIgnoreCase)) { rows = new List<double[]>(); continue; }
                    if (body.Equals("END", StringComparison.OrdinalIgnoreCase))
                    {
                        if (rows is null) throw new FormatException($".cst: block {blocks.Count + 1} has #END with no #BEGIN.");
                        blocks.Add(MakeBlock(blocks.Count + 1, header, provenance, titles, rows));
                        rows = null;
                        // The next block states its own header; the file-level lines above the first one are not repeated.
                        header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        provenance = new List<string>();
                        continue;
                    }
                    int colon = body.IndexOf(':');
                    if (colon > 0)
                    {
                        string key = body[..colon].Trim(), value = body[(colon + 1)..].Trim();
                        header[key] = value;
                        if (Array.Exists(ProvenanceKeys, k => k.Equals(key, StringComparison.OrdinalIgnoreCase)))
                            provenance.Add($"{key}: {value}");
                    }
                    else if (body.StartsWith("Frequency", StringComparison.OrdinalIgnoreCase))
                        titles = body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    continue;
                }
                if (rows is null) continue;
                var parts = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                var row = new double[parts.Length];
                for (int i = 0; i < parts.Length; i++) row[i] = Num(parts[i]);
                rows.Add(row);
            }
            if (rows is not null) throw new FormatException($".cst: block {blocks.Count + 1} has #BEGIN with no #END.");
            return blocks;
        }

        private static Block MakeBlock(int number, Dictionary<string, string> h, List<string> provenance,
                                       string[]? titles, List<double[]> rows)
        {
            string Need(string key) => h.TryGetValue(key, out var v) ? v
                : throw new FormatException($".cst: block {number} does not state #{key}.");

            string type = Need("MEASUREMENT TYPE");
            if (!type.Equals("Power Sweep", StringComparison.OrdinalIgnoreCase))
                throw new FormatException($".cst: block {number} is a '{type}' measurement; only Power Sweep is read.");
            if (titles is null) throw new FormatException($".cst: block {number} has no '#Frequency(Hz) …' column line.");

            int Col(string name)
            {
                int i = Array.FindIndex(titles, c => c.StartsWith(name, StringComparison.OrdinalIgnoreCase));
                return i >= 0 ? i : throw new FormatException($".cst: block {number} has no {name} column.");
            }
            int cF = Col("Frequency"), cVin = Col("Vin"), cIin = Col("Iin"), cVout = Col("Vout"), cIout = Col("Iout");
            int[] cw = { Col("Re[a1]"), Col("Im[a1]"), Col("Re[b1]"), Col("Im[b1]"), Col("Re[a2]"), Col("Im[a2]"), Col("Re[b2]"), Col("Im[b2]") };

            var b = new Block
            {
                Number     = number,
                F0Hz       = ParseHz(Need("FUNDAMENTAL FREQUENCY")),
                Harmonics  = (int)Num(Need("HARMONICS")),
                Z0Source   = h.TryGetValue("Z0SOURCE", out var zs) ? Num(zs) : 50,
                Z0Load     = h.TryGetValue("Z0LOAD", out var zl) ? Num(zl) : 50,
                GammaSource = h.TryGetValue("GAMMASOURCE", out var gs) ? Polar(gs, number, "GAMMASOURCE") : null,
                GammaLoad  = Polar(Need("GAMMALOAD"), number, "GAMMALOAD"),
                Provenance = provenance,
            };
            if (b.Harmonics < 0) throw new FormatException($".cst: block {number} states #HARMONICS: {b.Harmonics}.");
            int perDrive = b.Harmonics + 1;

            // One row per harmonic per drive, f0 first: a drive whose rows do not run f0, 2f0, … as #HARMONICS says is a
            // refusal naming the block and the drive — taking the rows in step would put a harmonic's waves on f0.
            for (int r = 0; r < rows.Count; r++)
            {
                int drive = r / perDrive + 1, k = r % perDrive;
                double want = b.F0Hz * (k + 1);
                double got = cF < rows[r].Length ? rows[r][cF] : double.NaN;
                if (!(Math.Abs(got - want) <= 1e-6 * want))
                    throw new FormatException($".cst: block {number}, drive {drive}: expected the {Ord(k + 1)} row at " +
                        $"{want / 1e9:G6} GHz and found {(double.IsNaN(got) ? "none" : (got / 1e9).ToString("G6", CultureInfo.InvariantCulture) + " GHz")}" +
                        $" — the block's #HARMONICS: {b.Harmonics} says each drive has {perDrive} row(s).");
            }
            if (rows.Count % perDrive != 0)
            {
                int drive = rows.Count / perDrive + 1;
                throw new FormatException($".cst: block {number}, drive {drive}: the {Ord(rows.Count % perDrive + 1)} row " +
                    $"({b.F0Hz * (rows.Count % perDrive + 1) / 1e9:G6} GHz) is missing — #HARMONICS: {b.Harmonics} says each drive has {perDrive} row(s).");
            }

            double V(double[] row, int c) => c < row.Length ? row[c] : double.NaN;
            for (int d = 0; d < rows.Count / perDrive; d++)
            {
                var drive = new Complex[perDrive][];
                for (int k = 0; k < perDrive; k++)
                {
                    var row = rows[d * perDrive + k];
                    drive[k] = new[]
                    {
                        new Complex(V(row, cw[0]), V(row, cw[1])), new Complex(V(row, cw[2]), V(row, cw[3])),
                        new Complex(V(row, cw[4]), V(row, cw[5])), new Complex(V(row, cw[6]), V(row, cw[7])),
                    };
                }
                b.Waves.Add(drive);
                var f0row = rows[d * perDrive];
                b.Dc.Add(new[] { V(f0row, cVin), V(f0row, cIin), V(f0row, cVout), V(f0row, cIout) });
            }
            return b;
        }

        // ── One frequency's loads → a FreqBlock ─────────────────────────────

        private static LpcwaveReader.FreqBlock BuildFreqBlock(List<Block> loads)
        {
            int nGrid = loads.Count;
            int nPin = Math.Max(1, loads.Max(l => l.Waves.Count));
            int nHarm = loads.Max(l => l.Harmonics) + 1;
            int n = nGrid * nPin;
            var first = loads[0];
            var nan = new Complex(double.NaN, double.NaN);

            Complex[][] Waves(int which)
            {
                var perH = new Complex[nHarm][];
                for (int k = 0; k < nHarm; k++)
                {
                    var arr = new Complex[n];
                    Array.Fill(arr, nan);
                    for (int gi = 0; gi < nGrid; gi++)
                        for (int pi = 0; pi < loads[gi].Waves.Count; pi++)
                            if (k < loads[gi].Waves[pi].Length) arr[gi * nPin + pi] = loads[gi].Waves[pi][k][which];
                    perH[k] = arr;
                }
                return perH;
            }
            double[] Dc(int which)
            {
                var arr = new double[n];
                Array.Fill(arr, double.NaN);
                for (int gi = 0; gi < nGrid; gi++)
                    for (int pi = 0; pi < loads[gi].Dc.Count; pi++) arr[gi * nPin + pi] = loads[gi].Dc[pi][which];
                return arr;
            }

            // The drive axis is the step number: a .cst states no source power column, and each load's own available
            // power (PavlDbm, from its waves) is per load and per drive.
            var pinAxis = new double[nPin];
            for (int i = 0; i < nPin; i++) pinAxis[i] = i + 1;

            var block = new LpcwaveReader.FreqBlock
            {
                FreqGHz        = first.F0Hz / 1e9,
                NGrid          = nGrid,
                NPin           = nPin,
                IsLoadpull     = true,
                // The stored load grid is read at 50 Ω everywhere (LoadpullSurface); ZLoad is the header's Γ at #Z0LOAD.
                GammaLoad      = loads.Select(l => LoadpullWaves.To50(l.GammaLoad, l.Z0Load)).ToArray(),
                PinAxis        = pinAxis,
                Z0Source       = first.Z0Source,
                Z0Load         = 50.0,
                SourceGamma    = first.GammaSource ?? Complex.Zero,
                HasSourceGamma = first.GammaSource is not null,
            };
            LoadpullWaves.Derive(new LoadpullWaveInputs
            {
                NGrid = nGrid, NPin = nPin, Convention = Convention,
                Z0Source = first.Z0Source, Z0Load = first.Z0Load,
                GammaSource = first.GammaSource ?? Complex.Zero,
                A1 = Waves(0), B1 = Waves(1), A2 = Waves(2), B2 = Waves(3),
                Vin = Dc(0), Iin = Dc(1), Vout = Dc(2), Iout = Dc(3),
            }, block.Foms, block.DriveComplex, block.LoadComplex);
            return block;
        }

        // ── helpers ─────────────────────────────────────────────────────────

        private static double Num(string s) =>
            double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN;

        /// <summary>"14.0 GHz" → 14e9 (Hz, kHz, MHz, GHz; a bare number is GHz).</summary>
        private static double ParseHz(string s)
        {
            var parts = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            double v = parts.Length > 0 ? Num(parts[0]) : double.NaN;
            string unit = parts.Length > 1 ? parts[1].ToUpperInvariant() : "GHZ";
            return unit switch { "HZ" => v, "KHZ" => v * 1e3, "MHZ" => v * 1e6, _ => v * 1e9 };
        }

        /// <summary>"+7.5E-01 +2.5E+01" → 0.75∠25°.</summary>
        private static Complex Polar(string s, int block, string key)
        {
            var parts = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || double.IsNaN(Num(parts[0])) || double.IsNaN(Num(parts[1])))
                throw new FormatException($".cst: block {block}'s #{key} '{s}' is not '|Γ| angle(deg)'.");
            return Complex.FromPolarCoordinates(Num(parts[0]), Num(parts[1]) * Math.PI / 180.0);
        }

        private static string Ord(int n) => n switch { 1 => "f0", _ => $"{n}f0" };
    }
}
