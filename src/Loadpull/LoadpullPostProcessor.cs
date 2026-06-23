// ================================================================
//  LoadpullPostProcessor.cs — derived-metric enrichment for simulated loadpull
//
//  Takes a LoadpullEngine DataSet (raw spectra + core FOMs + node-identity
//  provenance) and adds the derived display metrics a measured .spl/.lpcwave
//  carries — Pout_dBm, Zin_real/Zin_imag, IRL, AMPM — using the SAME
//  LoadpullDerivedFields math the readers use, so simulated contours render
//  identically to measured ones.  Design: docs/design/loadpull-postprocessor.md.
//
//  Presence-gated + idempotent: never overwrites an existing key, no-op when an
//  input is absent (safe to run on any loadpull DataSet, incl. a measured one
//  that already has the derived fields).  Group-aware (LP run.npy nests cubes
//  under an analysis group; flat .spl is top level).
//
//  RfCore firewall: headless, no Avalonia/UI types.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using RfCore.Data;

namespace RfCore.Loadpull
{
    public static class LoadpullPostProcessor
    {
        private const int    FundamentalHarmonic = 1;     // harmonic-axis index: 0=DC, 1=fundamental
        private const double Z0 = 50.0;

        /// <summary>
        /// Enrich a loadpull <see cref="DataSet"/> with derived display metrics, in-place, returning it.
        /// <paramref name="group"/> = "" for a flat (.spl-style) DataSet, or the analysis group
        /// (e.g. "LP1") for a simulated run. Adds only missing cubes (idempotent); each output is
        /// gated on its inputs being present.
        /// </summary>
        public static DataSet Enrich(DataSet ds, string group = "")
        {
            if (ds is null) throw new ArgumentNullException(nameof(ds));

            string Q(string name) => group.Length > 0 ? $"{group}.{name}" : name;
            bool   Has(string name) => ds.Contains(Q(name));
            void   AddCube(string name, DataCube cube)
            {
                if (group.Length > 0) ds.AddToGroup(group, name, cube);
                else                  ds.Add(name, cube);
            }

            // Idempotence: once enriched, re-running is a no-op (the display-convention fixes below
            // mutate in place and must not be applied twice).
            if (Has("__lpEnriched")) return ds;

            // Replace an existing real cube with f(value), preserving axes. Used for the
            // display-convention fixes (sign/scale) — no-op when the cube is absent.
            void TransformInPlace(string name, Func<double, double> f)
            {
                if (!Has(name)) return;
                var cube = ds[Q(name)];
                var src  = cube.RealValues;
                var dst  = new double[src.Length];
                for (int i = 0; i < src.Length; i++) dst[i] = double.IsNaN(src[i]) ? double.NaN : f(src[i]);
                AddCube(name, new DataCube(cube.Axes.ToArray(), dst));
            }

            // ── Pout_dBm ─────────────────────────────────────────────────────────
            if (Has("Pout") && !Has("Pout_dBm"))
            {
                var poutCube = ds[Q("Pout")];
                var pw       = poutCube.RealValues;
                var dbm      = new double[pw.Length];
                for (int i = 0; i < pw.Length; i++)
                    dbm[i] = (double.IsNaN(pw[i]) || pw[i] <= 0.0)
                        ? double.NaN
                        : 10.0 * Math.Log10(pw[i]) + 30.0;
                AddCube("Pout_dBm", new DataCube(poutCube.Axes.ToArray(), dbm));
            }

            // ── Zin_real/Zin_imag, IRL, AMPM (from the interface spectra) ─────────
            // Requires V + INl spectra and the source-DUT node index (provenance).
            if (Has("V") && Has("INl") && Has("__SrcNodeIdx"))
            {
                var vCube   = ds[Q("V")];
                var inlCube = ds[Q("INl")];
                int srcIdx  = NodeIdx(ds, Q("__SrcNodeIdx"));
                int loadIdx = Has("__LoadNodeIdx") ? NodeIdx(ds, Q("__LoadNodeIdx")) : -1;

                int gDim = AxisDim(vCube, "gridPoint");
                int pDim = AxisDim(vCube, "pinStep");
                int nDim = AxisDim(vCube, "node");
                int hDim = AxisDim(vCube, "harmonic");

                if (gDim == 0 && pDim == 1 && nDim == 2 && hDim == 3 && srcIdx >= 0)
                {
                    int nG = vCube.Axes[gDim].Length;
                    int nP = vCube.Axes[pDim].Length;
                    int nN = vCube.Axes[nDim].Length;
                    int nH = vCube.Axes[hDim].Length;

                    if (nH > FundamentalHarmonic && srcIdx < nN)
                    {
                        var v   = vCube.ComplexValues;
                        var inl = inlCube.ComplexValues;
                        int n   = nG * nP;

                        var ginMag       = new double[n];
                        var ginPhaseDeg  = new double[n];
                        var transPhaseDeg = loadIdx >= 0 && loadIdx < nN ? new double[n] : null;

                        // Flat index into {gridPoint, pinStep, node, harmonic}.
                        int Vi(int gi, int pi, int ni) =>
                            ((gi * nP + pi) * nN + ni) * nH + FundamentalHarmonic;

                        for (int gi = 0; gi < nG; gi++)
                        for (int pi = 0; pi < nP; pi++)
                        {
                            int fom   = gi * nP + pi;
                            Complex vs = v[Vi(gi, pi, srcIdx)];
                            Complex isr = inl[Vi(gi, pi, srcIdx)];

                            if (isr == Complex.Zero || IsNan(vs) || IsNan(isr))
                            {
                                ginMag[fom] = double.NaN; ginPhaseDeg[fom] = double.NaN;
                                if (transPhaseDeg is not null) transPhaseDeg[fom] = double.NaN;
                                continue;
                            }

                            Complex zin = vs / isr;                 // input impedance into the DUT
                            Complex gin = RfHelpers.Z2G(zin / Z0);  // normalized reflection coefficient
                            ginMag[fom]      = gin.Magnitude;
                            ginPhaseDeg[fom] = gin.Phase * 180.0 / Math.PI;

                            if (transPhaseDeg is not null)
                            {
                                Complex vl = v[Vi(gi, pi, loadIdx)];
                                transPhaseDeg[fom] = IsNan(vl)
                                    ? double.NaN
                                    : (vl.Phase - vs.Phase) * 180.0 / Math.PI;
                            }
                        }

                        var foms = new Dictionary<string, double[]>(StringComparer.Ordinal);
                        // Seed the dict with already-present derived cubes so Derive's presence
                        // guard skips them (idempotence across re-runs / measured-then-enriched).
                        foreach (var k in new[] { "Zin_real", "Zin_imag", "AMPM", "IRL" })
                            if (Has(k)) foms[k] = Array.Empty<double>();

                        LoadpullDerivedFields.Derive(
                            foms, nG, nP,
                            ginMag, ginPhaseDeg,
                            transPhaseDeg,
                            reflDb: null, reflLin: null);

                        var poutAxes = ds[Q("Pout")].Axes.ToArray();   // {gridPoint, pinStep}
                        foreach (var kv in foms)
                        {
                            if (kv.Value.Length == 0) continue;        // seeded placeholder — already present
                            if (Has(kv.Key)) continue;
                            AddCube(kv.Key, new DataCube(poutAxes, kv.Value));
                        }
                    }
                }
            }

            // ── Display-convention fixes (simulated runs only) ────────────────────
            // The engine reports bias currents with the passive sign (current into the device node
            // → drain Idq negative) and efficiency as a 0..1 fraction. The Summary Table / users expect
            // a positive Idq and efficiency in %. Gate on __SrcNodeIdx so these never touch a measured
            // .spl/.lpcwave DataSet (which already carries +Idq and %-efficiency and has no such marker).
            if (Has("__SrcNodeIdx"))
            {
                TransformInPlace("BiasILoad", v => -v);   // drain quiescent current → positive
                TransformInPlace("BiasISrc",  v => -v);   // gate  quiescent current → positive
                TransformInPlace("DE",  v => v * 100.0);  // drain efficiency  fraction → %
                TransformInPlace("PAE", v => v * 100.0);  // power-added eff   fraction → %
            }

            AddCube("__lpEnriched", new DataCube(Array.Empty<Axis>(), new[] { 1.0 }));
            return ds;
        }

        private static bool IsNan(Complex c) => double.IsNaN(c.Real) || double.IsNaN(c.Imaginary);

        private static int NodeIdx(DataSet ds, string spec)
        {
            var vals = ds[spec].RealValues;
            return vals.Length > 0 ? (int)Math.Round(vals[0]) : -1;
        }

        private static int AxisDim(DataCube cube, string axisName)
        {
            for (int d = 0; d < cube.Axes.Count; d++)
                if (cube.Axes[d].Name == axisName) return d;
            return -1;
        }
    }
}
