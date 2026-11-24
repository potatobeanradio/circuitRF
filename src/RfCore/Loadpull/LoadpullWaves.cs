// ================================================================
//  LoadpullWaves.cs — loadpull metrics from the measured waves
//
//  Brief LPW (R-lpw-1). A measured loadpull file carries the incident and
//  reflected waves at the device's planes (a1, b1, a2, b2, at f0 and, in
//  some files, at its harmonics) and the bias at each drive step. This is
//  the ONE derivation that turns them into the canonical cubes every
//  loadpull reader produces: every reader that carries waves calls it, and
//  none computes a wave metric of its own.
//
//  Presence-gated, as LoadpullDerivedFields is: a key already in the FOM
//  dictionary (a column the file states) is never overwritten, and a
//  quantity whose inputs are absent is not produced at all, rather than
//  produced as zero.
//
//  Storage units are LoadpullFomDialect's: dBm, dB, %, W, Ω, degrees.
//
//  RfCore firewall: no Avalonia/UI types.
// ================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace RfCore.Loadpull
{
    /// <summary>
    /// How a file's waves relate to power. <see cref="Rms"/>: P = |x|². <see cref="Peak"/>: P = ½|x|².
    /// </summary>
    public enum WaveConvention { Rms, Peak }

    /// <summary>
    /// The waves and bias of one loadpull block (one frequency), row-major over <c>gi*NPin + pi</c>.
    /// A drive a file does not hold is <c>NaN</c> in every array.
    /// </summary>
    public sealed class LoadpullWaveInputs
    {
        public int NGrid { get; init; }
        public int NPin  { get; init; }
        public WaveConvention Convention { get; init; } = WaveConvention.Rms;

        /// <summary>Reference impedance of the source-side waves, Ω.</summary>
        public double Z0Source { get; init; } = 50.0;
        /// <summary>Reference impedance of the load-side waves, Ω.</summary>
        public double Z0Load { get; init; } = 50.0;

        /// <summary>The source termination at f0, referenced to <see cref="Z0Source"/>. Zero when the file states none.</summary>
        public Complex GammaSource { get; init; }

        /// <summary>
        /// The waves per harmonic: index 0 is f0, index k is (k+1)·f0. Each array is <c>NGrid*NPin</c> long.
        /// </summary>
        public IReadOnlyList<Complex[]> A1 { get; init; } = Array.Empty<Complex[]>();
        public IReadOnlyList<Complex[]> B1 { get; init; } = Array.Empty<Complex[]>();
        public IReadOnlyList<Complex[]> A2 { get; init; } = Array.Empty<Complex[]>();
        public IReadOnlyList<Complex[]> B2 { get; init; } = Array.Empty<Complex[]>();

        /// <summary>The bias at each drive, V and A; null when the file states none.</summary>
        public double[]? Vin  { get; init; }
        public double[]? Iin  { get; init; }
        public double[]? Vout { get; init; }
        public double[]? Iout { get; init; }

        public int Harmonics => Math.Min(Math.Min(A1.Count, B1.Count), Math.Min(A2.Count, B2.Count));
    }

    public static class LoadpullWaves
    {
        /// <summary>The cube a measured load reflection per drive is stored under (complex, over {gridPoint, pinStep}).</summary>
        public const string GammaLoadMeasCube = "GammaLoadMeas";

        /// <summary><c>Pout_2f0_dBm</c>, … — the power delivered to the load at harmonic <paramref name="n"/> ≥ 2.</summary>
        public static string HarmonicPoutName(int n)   => $"Pout_{n}f0_dBm";
        /// <summary><c>PinDel_2f0_dBm</c>, … — the power delivered into the input at harmonic <paramref name="n"/> ≥ 2.</summary>
        public static string HarmonicPinDelName(int n) => $"PinDel_{n}f0_dBm";
        /// <summary><c>GammaLoad_2f0</c>, … — the per-load measured load reflection at harmonic <paramref name="n"/> ≥ 2.</summary>
        public static string HarmonicGammaName(int n)  => $"GammaLoad_{n}f0";

        /// <summary>True for a harmonic power metric name this derivation writes (<c>Pout_3f0_dBm</c>, <c>PinDel_2f0_dBm</c>).</summary>
        public static bool IsHarmonicPowerMetric(string name)
        {
            foreach (var prefix in new[] { "Pout_", "PinDel_" })
            {
                if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith("f0_dBm", StringComparison.Ordinal)) continue;
                var mid = name.AsSpan(prefix.Length, name.Length - prefix.Length - "f0_dBm".Length);
                if (int.TryParse(mid, System.Globalization.NumberStyles.None,
                                 System.Globalization.CultureInfo.InvariantCulture, out int n) && n >= 2)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Adds every metric the waves support and <paramref name="foms"/> does not already hold.
        /// <paramref name="driveComplex"/> receives complex cubes over {gridPoint, pinStep} (<see cref="GammaLoadMeasCube"/>);
        /// <paramref name="loadComplex"/> receives complex cubes over {gridPoint} (the harmonic loads).
        /// Every load reflection is renormalized from <see cref="LoadpullWaveInputs.Z0Load"/> to 50 Ω, the reference the
        /// stored load grid is read in everywhere else (<c>LoadpullSurface</c>).
        /// </summary>
        public static void Derive(
            LoadpullWaveInputs w,
            IDictionary<string, double[]> foms,
            IDictionary<string, Complex[]> driveComplex,
            IDictionary<string, Complex[]> loadComplex)
        {
            int n = w.NGrid * w.NPin;
            if (w.Harmonics == 0 || n == 0) return;
            double scale = w.Convention == WaveConvention.Peak ? 0.5 : 1.0;
            double P(Complex x) => scale * (x.Real * x.Real + x.Imaginary * x.Imaginary);

            var a1 = w.A1[0]; var b1 = w.B1[0]; var a2 = w.A2[0]; var b2 = w.B2[0];

            var pInc  = new double[n];
            var pDel  = new double[n];
            var pAvl  = new double[n];
            var pOut  = new double[n];
            double gs2 = w.GammaSource.Magnitude * w.GammaSource.Magnitude;
            for (int i = 0; i < n; i++)
            {
                if (IsNaN(a1[i]) || IsNaN(b1[i])) { pInc[i] = pDel[i] = pAvl[i] = double.NaN; }
                else
                {
                    pInc[i] = P(a1[i]);
                    pDel[i] = P(a1[i]) - P(b1[i]);
                    // The generator wave: a1 = bg + ΓS·b1, so bg = a1 − ΓS·b1, and its available power is P(bg)/(1 − |ΓS|²).
                    var bg = a1[i] - w.GammaSource * b1[i];
                    pAvl[i] = gs2 < 1.0 ? P(bg) / (1.0 - gs2) : double.NaN;
                }
                pOut[i] = IsNaN(a2[i]) || IsNaN(b2[i]) ? double.NaN : P(b2[i]) - P(a2[i]);
            }

            AddIfMissing(foms, "PavlDbm",    () => Map(pAvl, Dbm));
            AddIfMissing(foms, "PinInc_dBm", () => Map(pInc, Dbm));
            AddIfMissing(foms, "PinDel_dBm", () => Map(pDel, Dbm));
            AddIfMissing(foms, "Pout_dBm",   () => Map(pOut, Dbm));
            AddIfMissing(foms, "Pout_W",     () => (double[])pOut.Clone());
            // Gains are formed from the waves' own powers, so they are consistent with each other whatever drive
            // column a file states (an instrument-side source power is not the power available at the device's plane).
            AddIfMissing(foms, "Gt_dB",      () => Zip(pOut, pAvl, (o, a) => Db(o / a)));
            AddIfMissing(foms, "Gp_dB",      () => Zip(pOut, pDel, (o, d) => Db(o / d)));

            // ── DC ───────────────────────────────────────────────────────────
            if (w.Vin  is not null) AddIfMissing(foms, "BiasVSrc",  () => (double[])w.Vin.Clone());
            if (w.Iin  is not null) AddIfMissing(foms, "BiasISrc",  () => (double[])w.Iin.Clone());
            if (w.Vout is not null) AddIfMissing(foms, "BiasVLoad", () => (double[])w.Vout.Clone());
            if (w.Iout is not null) AddIfMissing(foms, "BiasILoad", () => (double[])w.Iout.Clone());
            // The drain side is required; the gate's V·I is added when the file states both (an instrument that logs no
            // gate current is one whose gate power is the negligible term, and leaving PAE out over it would discard it).
            if (w.Vout is not null && w.Iout is not null)
            {
                var pdc = new double[n];
                for (int i = 0; i < n; i++)
                    pdc[i] = w.Vout[i] * w.Iout[i] + (w.Vin is not null && w.Iin is not null ? w.Vin[i] * w.Iin[i] : 0.0);
                AddIfMissing(foms, "Pdc_W",      () => pdc);
                AddIfMissing(foms, "Efficiency", () => Zip(pOut, pdc, (o, d) => d > 0 ? 100.0 * o / d : double.NaN));
                AddIfMissing(foms, "PAE",        () =>
                {
                    var pae = new double[n];
                    for (int i = 0; i < n; i++)
                        pae[i] = pdc[i] > 0 ? 100.0 * (pOut[i] - pDel[i]) / pdc[i] : double.NaN;
                    return pae;
                });
            }

            // ── Input reflection ─────────────────────────────────────────────
            var gin = new Complex[n];
            for (int i = 0; i < n; i++)
                gin[i] = IsNaN(a1[i]) || IsNaN(b1[i]) || a1[i] == Complex.Zero ? NaNC : b1[i] / a1[i];
            if (!foms.ContainsKey("Zin_real") && !foms.ContainsKey("Zin_imag"))
            {
                var re = new double[n]; var im = new double[n];
                for (int i = 0; i < n; i++)
                {
                    if (IsNaN(gin[i])) { re[i] = im[i] = double.NaN; continue; }
                    var z = w.Z0Source * (1.0 + gin[i]) / (1.0 - gin[i]);
                    re[i] = z.Real; im[i] = z.Imaginary;
                }
                foms["Zin_real"] = re; foms["Zin_imag"] = im;
            }
            // The repository's IRL sign: 20·log10|Γin|, a good match NEGATIVE (LoadpullDerivedFields, the .spl reader and
            // the simulated post-processor all store it so). One cube name must not change sign with the file format.
            AddIfMissing(foms, "IRL_dB", () =>
            {
                var irl = new double[n];
                for (int i = 0; i < n; i++)
                    irl[i] = IsNaN(gin[i]) ? double.NaN : 20.0 * Math.Log10(Math.Max(gin[i].Magnitude, 1e-300));
                return irl;
            });

            // ── AM/PM: ∠(b2/a1) relative to the lowest drive, unwrapped ─────
            AddIfMissing(foms, "AMPM_deg", () =>
            {
                var ampm = new double[n];
                for (int gi = 0; gi < w.NGrid; gi++)
                {
                    var rel = new double[w.NPin];
                    double first = double.NaN;
                    for (int pi = 0; pi < w.NPin; pi++)
                    {
                        int i = gi * w.NPin + pi;
                        double ph = IsNaN(a1[i]) || IsNaN(b2[i]) || a1[i] == Complex.Zero
                            ? double.NaN : (b2[i] / a1[i]).Phase * 180.0 / Math.PI;
                        rel[pi] = ph;
                    }
                    LoadpullDerivedFields.UnwrapDegInPlace(rel);
                    foreach (var v in rel) if (!double.IsNaN(v)) { first = v; break; }
                    for (int pi = 0; pi < w.NPin; pi++)
                        ampm[gi * w.NPin + pi] = double.IsNaN(rel[pi]) || double.IsNaN(first) ? double.NaN : rel[pi] - first;
                }
                return ampm;
            });

            // ── The measured load per drive ──────────────────────────────────
            if (!driveComplex.ContainsKey(GammaLoadMeasCube))
            {
                var gl = new Complex[n];
                for (int i = 0; i < n; i++)
                    gl[i] = IsNaN(a2[i]) || IsNaN(b2[i]) || b2[i] == Complex.Zero ? NaNC : To50(a2[i] / b2[i], w.Z0Load);
                driveComplex[GammaLoadMeasCube] = gl;
            }

            // ── Harmonics n ≥ 2: separate rank-2 cubes, never a third axis ─
            // A third axis would be read by LoadpullSurface and LoadpullRecognition as a leading sweep: both key on rank.
            for (int k = 1; k < w.Harmonics; k++)
            {
                int h = k + 1;
                var ha1 = w.A1[k]; var hb1 = w.B1[k]; var ha2 = w.A2[k]; var hb2 = w.B2[k];
                AddIfMissing(foms, HarmonicPoutName(h), () =>
                {
                    var v = new double[n];
                    for (int i = 0; i < n; i++) v[i] = IsNaN(ha2[i]) || IsNaN(hb2[i]) ? double.NaN : Dbm(P(hb2[i]) - P(ha2[i]));
                    return v;
                });
                AddIfMissing(foms, HarmonicPinDelName(h), () =>
                {
                    var v = new double[n];
                    for (int i = 0; i < n; i++) v[i] = IsNaN(ha1[i]) || IsNaN(hb1[i]) ? double.NaN : Dbm(P(ha1[i]) - P(hb1[i]));
                    return v;
                });
                string gname = HarmonicGammaName(h);
                if (!loadComplex.ContainsKey(gname))
                {
                    // a2/b2 at a harmonic is noise while b2 is small (|Γ| > 1 at the first drives of a real file): each drive
                    // is weighted by the power the device sends toward the load there, P(b2), so the low drives count for
                    // almost nothing — never a plain mean, never the first row.
                    var gamma = new Complex[w.NGrid];
                    for (int gi = 0; gi < w.NGrid; gi++)
                    {
                        Complex sum = Complex.Zero; double wsum = 0;
                        for (int pi = 0; pi < w.NPin; pi++)
                        {
                            int i = gi * w.NPin + pi;
                            if (IsNaN(ha2[i]) || IsNaN(hb2[i]) || hb2[i] == Complex.Zero) continue;
                            double wt = P(hb2[i]);
                            sum += wt * (ha2[i] / hb2[i]);
                            wsum += wt;
                        }
                        gamma[gi] = wsum > 0 ? To50(sum / wsum, w.Z0Load) : NaNC;
                    }
                    loadComplex[gname] = gamma;
                }
            }
        }

        // ── helpers ─────────────────────────────────────────────────────────

        private static readonly Complex NaNC = new(double.NaN, double.NaN);

        private static bool IsNaN(Complex c) => double.IsNaN(c.Real) || double.IsNaN(c.Imaginary);

        /// <summary>W → dBm; NaN for a power that is not positive.</summary>
        internal static double Dbm(double w) => w > 0 ? 10.0 * Math.Log10(w * 1000.0) : double.NaN;

        private static double Db(double ratio) => ratio > 0 ? 10.0 * Math.Log10(ratio) : double.NaN;

        /// <summary>A reflection referenced to <paramref name="z0"/> Ω, re-referenced to 50 Ω.</summary>
        internal static Complex To50(Complex g, double z0)
        {
            if (Math.Abs(z0 - 50.0) < 1e-12 || IsNaN(g)) return g;
            var z = z0 * (1.0 + g) / (1.0 - g);
            return (z - 50.0) / (z + 50.0);
        }

        private static double[] Map(double[] x, Func<double, double> f)
        {
            var r = new double[x.Length];
            for (int i = 0; i < x.Length; i++) r[i] = double.IsNaN(x[i]) ? double.NaN : f(x[i]);
            return r;
        }

        private static double[] Zip(double[] x, double[] y, Func<double, double, double> f)
        {
            var r = new double[x.Length];
            for (int i = 0; i < x.Length; i++) r[i] = double.IsNaN(x[i]) || double.IsNaN(y[i]) ? double.NaN : f(x[i], y[i]);
            return r;
        }

        private static void AddIfMissing(IDictionary<string, double[]> foms, string key, Func<double[]> make)
        {
            if (!foms.ContainsKey(key)) foms[key] = make();
        }
    }
}
