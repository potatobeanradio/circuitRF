// ================================================================
//  LoadpullDerivedFields.cs — shared loadpull derivation helper
//
//  Computes Zin_real/Zin_imag, AMPM, and IRL from raw input columns
//  captured during parsing, porting SPLData.py.__init__ derivations.
//  Shared by SplReader and LpcwaveReader.
//
//  All outputs are presence-gated: when an input is absent the
//  corresponding output is not produced. Operates per grid point
//  on drive-up arrays (row-major gi*nPin+pi).
//
//  RfCore firewall: no Avalonia/UI types.
// ================================================================

using System;
using System.Collections.Generic;

namespace RfCore.Loadpull
{
    internal static class LoadpullDerivedFields
    {
        /// <summary>
        /// Add derived FOM arrays into <paramref name="foms"/> (keyed by canonical name,
        /// row-major gi*nPin+pi) from raw input arrays. No-op for any input that is null.
        /// Existing keys are NOT overwritten (presence guard per Part C).
        /// </summary>
        public static void Derive(
            Dictionary<string, double[]> foms,
            int nGrid, int nPin,
            double[]? ginMag, double[]? ginPhaseDeg,
            double[]? transPhaseDeg,
            double[]? reflDb, double[]? reflLin)
        {
            int n = nGrid * nPin;

            // ── Pout_W (Watts, derived from Pout_dBm when present) ───────────────
            if (foms.TryGetValue("Pout_dBm", out var poutDbm) && !foms.ContainsKey("Pout_W"))
            {
                var pw = new double[poutDbm.Length];
                for (int i = 0; i < pw.Length; i++)
                    pw[i] = double.IsNaN(poutDbm[i]) ? double.NaN : Math.Pow(10.0, (poutDbm[i] - 30.0) / 10.0);
                foms["Pout_W"] = pw;
            }

            // ── Zin_real / Zin_imag ──────────────────────────────────────────────
            // SPLData: x,y = pol2cart(GinPhase*pi/180, GinMag); Zin = g2z(x+jy)*50
            if (!foms.ContainsKey("Zin_real") && !foms.ContainsKey("Zin_imag")
                && ginMag is not null && ginPhaseDeg is not null)
            {
                var zinRe = new double[n];
                var zinIm = new double[n];
                for (int i = 0; i < n; i++)
                {
                    double mag = ginMag[i], phDeg = ginPhaseDeg[i];
                    if (double.IsNaN(mag) || double.IsNaN(phDeg))
                    {
                        zinRe[i] = double.NaN; zinIm[i] = double.NaN;
                        continue;
                    }
                    double ph  = phDeg * Math.PI / 180.0;
                    var gin    = new System.Numerics.Complex(mag * Math.Cos(ph), mag * Math.Sin(ph));
                    var zin    = RfHelpers.G2Z(gin) * 50.0;
                    zinRe[i]   = zin.Real;
                    zinIm[i]   = zin.Imaginary;
                }
                foms["Zin_real"] = zinRe;
                foms["Zin_imag"] = zinIm;
            }

            // ── AMPM ─────────────────────────────────────────────────────────────
            // SPLData per grid point drive-up: AMPM = trans_phase[0] - trans_phase, then unwrap (deg).
            if (!foms.ContainsKey("AMPM_deg") && transPhaseDeg is not null)
            {
                var ampm = new double[n];
                for (int gi = 0; gi < nGrid; gi++)
                {
                    int    baseI = gi * nPin;
                    double first = transPhaseDeg[baseI];
                    var    rel   = new double[nPin];
                    for (int pi = 0; pi < nPin; pi++)
                    {
                        double v = transPhaseDeg[baseI + pi];
                        rel[pi]  = (double.IsNaN(v) || double.IsNaN(first)) ? double.NaN : (first - v);
                    }
                    UnwrapDegInPlace(rel);
                    Array.Copy(rel, 0, ampm, baseI, nPin);
                }
                foms["AMPM_deg"] = ampm;
            }

            // ── IRL_dB (input return loss, dB) ───────────────────────────────────
            // Sign convention (RF-engineer standard, S11-style): a good input match is NEGATIVE
            // (e.g. −200 dB ≈ perfect match); 0 dB = total reflection; > 0 = reflection gain (active).
            // IRL = 20·log10|Γin|.  Priority: stored dB alias → stored linear alias → derive from Γin.
            if (!foms.ContainsKey("IRL_dB"))
            {
                if (reflDb is not null)
                {
                    foms["IRL_dB"] = (double[])reflDb.Clone();
                }
                else if (reflLin is not null)
                {
                    var irl = new double[n];
                    for (int i = 0; i < n; i++)
                        irl[i] = double.IsNaN(reflLin[i]) ? double.NaN
                               : 20.0 * Math.Log10(Math.Max(Math.Abs(reflLin[i]), 1e-300));
                    foms["IRL_dB"] = irl;
                }
                else if (ginMag is not null)
                {
                    var irl = new double[n];
                    for (int i = 0; i < n; i++)
                        irl[i] = double.IsNaN(ginMag[i]) ? double.NaN
                               : 20.0 * Math.Log10(Math.Max(ginMag[i], 1e-300));
                    foms["IRL_dB"] = irl;
                }
                // else: no IRL inputs → omit
            }
        }

        /// <summary>
        /// In-place phase unwrap of a degree-valued array (port of np.unwrap on radians,
        /// applied in degrees). NaN-aware: NaN entries are left as NaN and do not affect
        /// the running offset.
        /// </summary>
        internal static void UnwrapDegInPlace(double[] deg)
        {
            const double period = 360.0;
            double offset  = 0.0;
            double? prevRaw = null;
            for (int i = 0; i < deg.Length; i++)
            {
                double v = deg[i];
                if (double.IsNaN(v)) continue;
                if (prevRaw is double p)
                {
                    double d    = v - p;
                    double corr = d - Math.Round(d / period) * period;
                    offset     += corr - d;
                }
                prevRaw = v;
                deg[i]  = v + offset;
            }
        }
    }
}
