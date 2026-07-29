// ================================================================
//  LoadpullExportModel.cs — flattens a canonical loadpull DataSet into
//  per-frequency blocks for the .spl / .lpcwave writers (Phase 2 of
//  docs/design/loadpull-postprocessor.md).
//
//  Input contract: a loadpull DataSet (LoadpullPostProcessor.Enrich output,
//  or a measured .spl/.lpcwave DataSet — both share the canonical layout):
//    FOM cubes over {[freq,] gridPoint, pinStep}  (Real)
//    GammaLoad / ZLoad over {[freq,] gridPoint}    (Complex)
//    ZSource over {freq} (Complex, optional) — source termination per freq
//  Multi-frequency: a leading "freq" axis on the FOM/termination cubes yields
//  one block per frequency (the engine's freq-swept LP stacks freq outermost).
//
//  This mirrors the readers' FreqBlock so SplWriter/LpcwaveWriter serialize the
//  exact shape SplReader/LpcwaveReader parse back.  Format-agnostic: the same
//  block list feeds either writer, which is why .spl ↔ .lpcwave is a free
//  round-trip (both go through this canonical model).
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
    /// <summary>One frequency's worth of loadpull data, flattened for export.</summary>
    public sealed class LoadpullFreqBlock
    {
        public double FreqGHz;
        public int    NGrid;
        public int    NPin;
        public Complex[] GammaLoad = Array.Empty<Complex>();   // [NGrid]
        public double[]  PinAxis   = Array.Empty<double>();    // [NPin] — Pavl (dBm)
        /// <summary>Canonical FOM name → row-major [NGrid*NPin] (gi*NPin+pi). NaN = invalid point.</summary>
        public Dictionary<string, double[]> Foms = new(StringComparer.Ordinal);
        public Complex SourceGamma;
        public bool    HasSourceGamma;
    }

    public static class LoadpullExportModel
    {
        // Canonical FOM cubes written as columns, in display order. Each is presence-gated:
        // absent cubes simply produce no column. Power is written as Pout_dBm (the reader
        // re-derives Pout_W); the pin axis is written as the drive column, not a FOM.
        public static readonly string[] FomColumns =
        [
            "Pout_dBm", "Gt_dB", "Gp_dB", "Efficiency", "PAE", "Pdc_W",
            "BiasVLoad", "BiasILoad", "BiasVSrc", "BiasISrc",
            "Zin_real", "Zin_imag", "IRL_dB", "AMPM_deg",
        ];

        private const double Z0 = 50.0;

        /// <summary>
        /// Flatten <paramref name="ds"/>'s loadpull cubes (in <paramref name="group"/>; "" = top level)
        /// into one block per frequency. Throws when the group has no recognizable loadpull termination.
        /// </summary>
        public static List<LoadpullFreqBlock> Build(DataSet ds, string group = "")
        {
            if (ds is null) throw new ArgumentNullException(nameof(ds));

            string Q(string name) => group.Length > 0 ? $"{group}.{name}" : name;
            bool   Has(string name) => ds.Contains(Q(name));
            DataCube Get(string name) => ds[Q(name)];

            // Termination cube — GammaLoad preferred; else derive from ZLoad.
            DataCube termCube;
            bool termIsGamma;
            if (Has("GammaLoad"))      { termCube = Get("GammaLoad"); termIsGamma = true;  }
            else if (Has("ZLoad"))     { termCube = Get("ZLoad");     termIsGamma = false; }
            else throw new InvalidOperationException(
                "DataSet has no GammaLoad/ZLoad termination cube — not a loadpull dataset.");

            int gDimT = AxisDim(termCube, "gridPoint");
            if (gDimT < 0) throw new InvalidOperationException("Termination cube has no 'gridPoint' axis.");
            int nGrid = termCube.Axes[gDimT].Length;

            // Pin axis: from any present FOM cube's pinStep axis (length 1 when no drive sweep).
            int nPin = 1;
            double[] pinAxisValues = { 0.0 };
            foreach (var name in FomColumns)
            {
                if (!Has(name)) continue;
                var c = Get(name);
                int pDim = AxisDim(c, "pinStep");
                if (pDim >= 0) { nPin = c.Axes[pDim].Length; pinAxisValues = c.Axes[pDim].Values; break; }
            }

            // Frequency axis: a leading "freq" axis on the termination cube ⇒ multi-freq. Else single
            // freq recovered from the __Freq carrier (engine/reader provenance), else 0.
            double[] freqHz;
            int fDimT = AxisDim(termCube, "freq");
            if (fDimT >= 0)
                freqHz = termCube.Axes[fDimT].Values;
            else if (Has("__Freq"))
                freqHz = Get("__Freq").RealValues;
            else
                freqHz = new[] { 0.0 };

            int nFreq = freqHz.Length;
            var blocks = new List<LoadpullFreqBlock>(nFreq);

            // Source termination (ZSource over {freq}) → per-freq source Γ.
            Complex[]? zSource = Has("ZSource") ? Get("ZSource").ComplexValues : null;

            for (int fi = 0; fi < nFreq; fi++)
            {
                var block = new LoadpullFreqBlock
                {
                    FreqGHz = freqHz[fi] / 1e9,
                    NGrid   = nGrid,
                    NPin    = nPin,
                    GammaLoad = new Complex[nGrid],
                    PinAxis   = (double[])pinAxisValues.Clone(),
                };

                var termVals = termCube.ComplexValues;
                for (int gi = 0; gi < nGrid; gi++)
                {
                    var t = termVals[FlatIndex(termCube, fi, gi, 0)];
                    block.GammaLoad[gi] = termIsGamma ? t : Z2G(t / Z0);
                }

                foreach (var name in FomColumns)
                {
                    if (!Has(name)) continue;
                    var c   = Get(name);
                    var src = c.RealValues;
                    var buf = new double[nGrid * nPin];
                    for (int gi = 0; gi < nGrid; gi++)
                        for (int pi = 0; pi < nPin; pi++)
                            buf[gi * nPin + pi] = src[FlatIndex(c, fi, gi, pi)];
                    block.Foms[name] = buf;
                }

                if (zSource != null && fi < zSource.Length)
                {
                    var zs = zSource[fi];
                    if (!(double.IsNaN(zs.Real) || double.IsNaN(zs.Imaginary)))
                    {
                        block.SourceGamma    = Z2G(zs / Z0);
                        block.HasSourceGamma = true;
                    }
                }

                blocks.Add(block);
            }

            return blocks;
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        private static int AxisDim(DataCube cube, string axisName)
        {
            for (int d = 0; d < cube.Axes.Count; d++)
                if (cube.Axes[d].Name == axisName) return d;
            return -1;
        }

        // Row-major flat index using the cube's actual axis order; coordinate for each axis is taken by
        // name (freq→fi, gridPoint→gi, pinStep→pi); any other axis contributes index 0.
        private static int FlatIndex(DataCube c, int fi, int gi, int pi)
        {
            int idx = 0;
            for (int d = 0; d < c.Rank; d++)
            {
                int coord = c.Axes[d].Name switch
                {
                    "freq"      => fi,
                    "gridPoint" => gi,
                    "pinStep"   => pi,
                    _           => 0,
                };
                idx = idx * c.Axes[d].Length + coord;
            }
            return idx;
        }

        private static Complex Z2G(Complex z) => (z - Complex.One) / (z + Complex.One);
    }
}
