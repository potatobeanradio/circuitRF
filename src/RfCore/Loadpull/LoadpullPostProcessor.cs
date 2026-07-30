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

            void Remove(string name) => ds.RemoveFromGroup(group.Length > 0 ? group : DataSet.DefaultGroup, name);

            // Rename a cube (same values/axes), dropping the old name so pickers/summary show only the
            // canonical unit-suffixed name.
            void RenameRaw(string oldName, string newName)
            {
                if (!Has(oldName)) return;
                if (!Has(newName)) AddCube(newName, ds[Q(oldName)]);
                Remove(oldName);
            }
            // Rename + scale (e.g. fraction → %).
            void RenameScaled(string oldName, string newName, double f)
            {
                if (!Has(oldName)) return;
                var c = ds[Q(oldName)]; var s = c.RealValues; var d = new double[s.Length];
                for (int i = 0; i < s.Length; i++) d[i] = double.IsNaN(s[i]) ? double.NaN : s[i] * f;
                AddCube(newName, new DataCube(c.Axes.ToArray(), d));
                if (oldName != newName) Remove(oldName);
            }

            // ── Canonical naming + display-convention fixes (simulated runs only) ──
            // Gate on __SrcNodeIdx (engine provenance) so a measured .spl/.lpcwave — which already
            // carries the canonical names, +Idq, and %-efficiency — is never touched. The engine emits
            // raw physics names/units (Pout in W, DE as a fraction, passive-sign bias); the post-processor
            // is the single normalization point to the user-facing, unit-suffixed names.
            if (Has("__SrcNodeIdx"))
            {
                // Power: Pout (W) → Pout_W (Watts) + Pout_dBm (dBm); bare "Pout" is dropped (ambiguous).
                if (Has("Pout"))
                {
                    var pc = ds[Q("Pout")]; var pw = pc.RealValues; var axes = pc.Axes.ToArray();
                    var dbm = new double[pw.Length];
                    for (int i = 0; i < pw.Length; i++)
                        dbm[i] = (double.IsNaN(pw[i]) || pw[i] <= 0.0) ? double.NaN
                               : 10.0 * Math.Log10(pw[i]) + 30.0;
                    if (!Has("Pout_W"))   AddCube("Pout_W",   new DataCube(axes, (double[])pw.Clone()));
                    if (!Has("Pout_dBm")) AddCube("Pout_dBm", new DataCube(axes, dbm));
                    Remove("Pout");
                }
                RenameRaw("Gt",  "Gt_dB");
                RenameRaw("Gp",  "Gp_dB");
                RenameRaw("Pdc", "Pdc_W");
                RenameScaled("DE", "Efficiency", 100.0);   // drain efficiency fraction → %
                TransformInPlace("PAE", v => v * 100.0);   // power-added efficiency fraction → %
                TransformInPlace("BiasILoad", v => -v);    // drain quiescent current → positive (Idq)
                TransformInPlace("BiasISrc",  v => -v);    // gate  quiescent current → positive
            }

            // ── Zin_real/Zin_imag, AMPM_deg, IRL_dB (from the interface spectra) ──
            // Requires V + INl spectra and the source-DUT node index (provenance).
            if (Has("V") && Has("INl") && Has("__SrcNodeIdx"))
            {
                var vCube   = ds[Q("V")];
                var inlCube = ds[Q("INl")];
                // Prefer the engine-provided source-delivered input current (Iin) for Zin/Γin: it is the
                // true current INTO the DUT input node (accounts for passives at the gate), whereas
                // INl[src] is only the device gate current. Absent (imported data) → fall back to INl[src].
                var iinCube = Has("Iin") ? ds[Q("Iin")] : null;
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

                        // Iin is {gridPoint, pinStep, harmonic} (no node axis) — flat index helper.
                        var iin  = iinCube?.ComplexValues;
                        int nHi  = iinCube is not null ? AxisDim(iinCube, "harmonic") >= 0
                                       ? iinCube.Axes[AxisDim(iinCube, "harmonic")].Length : nH
                                   : nH;
                        int Ii(int gi, int pi) => (gi * nP + pi) * nHi + FundamentalHarmonic;
                        bool useIin = iin is not null && nHi > FundamentalHarmonic;

                        // Input return loss is referenced to the SOURCE impedance the tuner presents at
                        // the fundamental (ZSource, per grid point) — NOT a fixed 50 Ω. When ZSource is
                        // present we compute the power-wave (conjugate-match) reflection Γ_s and pass it
                        // to Derive as the IRL; absent it, Derive falls back to the 50 Ω Γin (legacy).
                        var zsVals = Has("__SrcZ") ? ds[Q("__SrcZ")].ComplexValues : null;
                        var irlSrc = zsVals is not null ? new double[n] : null;

                        for (int gi = 0; gi < nG; gi++)
                        for (int pi = 0; pi < nP; pi++)
                        {
                            int fom   = gi * nP + pi;
                            Complex vs = v[Vi(gi, pi, srcIdx)];
                            Complex isr = useIin ? iin![Ii(gi, pi)] : inl[Vi(gi, pi, srcIdx)];

                            if (isr == Complex.Zero || IsNan(vs) || IsNan(isr))
                            {
                                ginMag[fom] = double.NaN; ginPhaseDeg[fom] = double.NaN;
                                if (transPhaseDeg is not null) transPhaseDeg[fom] = double.NaN;
                                if (irlSrc is not null) irlSrc[fom] = double.NaN;
                                continue;
                            }

                            Complex zin = vs / isr;                 // input impedance into the DUT
                            Complex gin = RfHelpers.Z2G(zin / Z0);  // 50 Ω-normalized Γ (for Zin reconstruction)
                            ginMag[fom]      = gin.Magnitude;
                            ginPhaseDeg[fom] = gin.Phase * 180.0 / Math.PI;

                            // Source-referenced input match: Γ_s = (Zin − Zs*)/(Zin + Zs) (Kurokawa
                            // power wave). Γ_s → 0 at conjugate match (Zin = Zs*) → IRL → −∞, exactly
                            // what a source-pull expects when the tuner presents the matched Zsource.
                            if (irlSrc is not null)
                            {
                                Complex zs  = gi < zsVals!.Length ? zsVals[gi] : new Complex(Z0, 0);
                                Complex den = zin + zs;
                                if (IsNan(zs) || den == Complex.Zero)
                                {
                                    irlSrc[fom] = double.NaN;
                                }
                                else
                                {
                                    double gs = ((zin - Complex.Conjugate(zs)) / den).Magnitude;
                                    irlSrc[fom] = 20.0 * Math.Log10(Math.Max(gs, 1e-300));
                                }
                            }

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
                        foreach (var k in new[] { "Zin_real", "Zin_imag", "AMPM_deg", "IRL_dB" })
                            if (Has(k)) foms[k] = Array.Empty<double>();

                        LoadpullDerivedFields.Derive(
                            foms, nG, nP,
                            ginMag, ginPhaseDeg,
                            transPhaseDeg,
                            reflDb: irlSrc, reflLin: null);   // source-referenced IRL (null → 50 Ω legacy)

                        // FOM axes {gridPoint, pinStep} taken from the spectra cube (Pout was renamed).
                        var poutAxes = new[] { vCube.Axes[gDim], vCube.Axes[pDim] };
                        foreach (var kv in foms)
                        {
                            if (kv.Value.Length == 0) continue;        // seeded placeholder — already present
                            if (Has(kv.Key)) continue;
                            AddCube(kv.Key, new DataCube(poutAxes, kv.Value));
                        }
                    }
                }
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
