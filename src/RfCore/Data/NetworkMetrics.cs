// ================================================================
//  NetworkMetrics.cs  —  cube-direct stability / passivity metrics
//
//  brief-stability-passivity-touchstone.md §2–§4.
//
//  R-stb-1: this file computes NOTHING itself. It is purely the adapter that turns a DataSet
//  S cube (+ its per-port Z0 cube) into the uniform-real-referenced matrices that RFNetwork's
//  EXISTING per-matrix stability overloads already require, then calls them. There is exactly one
//  implementation of μ, μ′, K, |Δ|, MAG/MSG and σ_max, and it lives in RFNetwork — the SNP path
//  and the cube path both reach it.
//
//  R-stb-2: the substantive difference between the two paths. Touchstone is uniform by
//  construction (SNP.Z0 is a single value), so the SNP path never had to renormalize. A simulator
//  may reference S to per-port, possibly COMPLEX terminations, and every formula here presumes a
//  uniform REAL reference — so the cube path must renormalize first, always, even when a
//  particular cube happens to be uniform 50 Ω.
// ================================================================

using System;
using System.Linq;
using System.Numerics;
using NumFlat;

namespace RfCore.Data
{
    /// <summary>Scalar-versus-frequency network metrics available from an S cube.</summary>
    public enum NetworkMetric
    {
        /// <summary>Edwards-Sinsky μ — the LOAD stability factor. 2-port.</summary>
        Mu,
        /// <summary>Edwards-Sinsky μ′ — the SOURCE stability factor. 2-port.</summary>
        MuPrime,
        /// <summary>Rollett K. 2-port.</summary>
        K,
        /// <summary>|Δ| — determinant magnitude. 2-port.</summary>
        DeltaMag,
        /// <summary>MAG/MSG in dB. 2-port.</summary>
        MaxGain,
        /// <summary>σ_max(S) — passivity measure. Defined for ANY N (see R-stb-6).</summary>
        Passivity,
        /// <summary>
        /// MAG/MSG as a LINEAR power ratio. 2-port. APPENDED — the Data Display offers the user a
        /// choice of linear or 10·log10 for a Max Gain trace, and the two spellings come from the
        /// one implementation in <see cref="RFNetwork"/> rather than from the UI undoing a log.
        /// </summary>
        MaxGainLinear,
    }

    public static class NetworkMetrics
    {
        public const string SCubeName  = "S";
        public const string Z0CubeName = "Z0";

        /// <summary>True when this metric is a 2-port formula and therefore needs a port pair.</summary>
        public static bool IsTwoPortOnly(NetworkMetric m) => m != NetworkMetric.Passivity;

        /// <summary>
        /// The S cube of <paramref name="ds"/>, or null when it carries none — found whether it is
        /// bare (Touchstone-shaped) or inside a named analysis group ("SP1.S").
        ///
        /// <para>Group-aware deliberately: a bare <c>Contains("S")</c> is FALSE for every simulated
        /// run, because bare resolution refuses analysis cubes by design. Using it here made these
        /// adapters — the whole point of which is to serve a SIMULATED source — silently find
        /// nothing for exactly that case.</para>
        /// </summary>
        public static DataCube? FindSCube(DataSet ds)
            => DataSetBuilder.FindCubeSpec(ds, SCubeName) is { } spec ? ds[spec] : null;

        /// <summary>The S cube's own spec ("S" or "SP1.S"), or null when the DataSet carries none.</summary>
        public static string? FindSCubeSpec(DataSet ds) => DataSetBuilder.FindCubeSpec(ds, SCubeName);

        /// <summary>
        /// True when the S cube is shaped like a plain network — [freq, i, j] with square port axes —
        /// and can therefore be viewed as an SNP. A SWEPT S cube (rank 4, e.g. [param, freq, i, j])
        /// is deliberately NOT network-shaped: an SNP cannot carry the extra axis, and flattening it
        /// would silently plot one arbitrary slice.
        /// </summary>
        public static bool IsNetworkShaped(DataSet ds)
        {
            var c = FindSCube(ds);
            return c is { Rank: 3 } && c.Axes[1].Length == c.Axes[2].Length && c.Axes[1].Length >= 1;
        }

        /// <summary>Port count of an S cube laid out as [freq, i, j]; 0 when absent/malformed.</summary>
        public static int PortCount(DataSet ds)
        {
            var c = FindSCube(ds);
            return c is null || c.Rank < 3 ? 0 : c.Axes[1].Length;
        }

        /// <summary>
        /// Converts an S-shaped DataCube (<c>[&lt;optional sweep&gt;, freq, i, j]</c>) to Z or Y,
        /// element-wise per leading-axis combination, using the group's per-port reference
        /// impedance — the same axis layout as S, so every DataSet consumer that already
        /// understands an S cube (the spec parser, TraceExpression, the Table, export, .cdd
        /// persistence) understands the result with no further change
        /// (brief-dd-network-params-and-stability.md §2). The last two axes of
        /// <paramref name="sCube"/> must be the square port axes (i, j); any number of leading
        /// axes is handled identically, since they only change how many N×N blocks there are.
        /// </summary>
        public static DataCube ConvertSCube(DataCube sCube, Complex[] z0PerPort, MatrixType targetType)
        {
            if (targetType == MatrixType.S)
                throw new ArgumentException("targetType must be Z or Y.", nameof(targetType));

            int rank    = sCube.Rank;
            int nPorts  = sCube.Axes[rank - 1].Length;
            int matSize = nPorts * nPorts;
            var raw     = sCube.ComplexValues;
            int nMats   = matSize == 0 ? 0 : raw.Length / matSize;
            var outRaw  = new Complex[raw.Length];

            for (int k = 0; k < nMats; k++)
            {
                int baseIdx = k * matSize;
                var m = new Mat<Complex>(nPorts, nPorts);
                for (int i = 0; i < nPorts; i++)
                for (int j = 0; j < nPorts; j++)
                    m[i, j] = raw[baseIdx + i * nPorts + j];

                var converted = targetType == MatrixType.Z ? RFNetwork.SToZ(m, z0PerPort)
                                                            : RFNetwork.SToY(m, z0PerPort);

                for (int i = 0; i < nPorts; i++)
                for (int j = 0; j < nPorts; j++)
                    outRaw[baseIdx + i * nPorts + j] = converted[i, j];
            }

            return new DataCube(sCube.Axes.ToArray(), outRaw);
        }

        /// <summary>
        /// Renormalizes an S-shaped DataCube (<c>[&lt;optional sweep&gt;, freq, i, j]</c>) from
        /// <paramref name="z0Src"/> to <paramref name="z0New"/>, per leading-axis combination —
        /// a whole-matrix operation (<see cref="RFNetwork.SToS"/>) applied to each N×N block, never
        /// an element-wise shortcut (brief-dd-z0-renormalization.md §1: renormalizing a single S
        /// element in isolation is wrong and silently produces plausible-looking numbers). Same
        /// axis-layout generality as <see cref="ConvertSCube"/> — the last two axes of
        /// <paramref name="sCube"/> must be the square port axes (i, j).
        /// </summary>
        public static DataCube RenormalizeSCube(DataCube sCube, Complex[] z0Src, Complex[] z0New)
        {
            foreach (var z in z0New)
                if (z.Real <= 0.0)
                    throw new ArgumentException(
                        $"Reference impedance must have Re(Z0) > 0 (got {z}) — the power-wave form divides by √Re(Z0).",
                        nameof(z0New));

            int rank    = sCube.Rank;
            int nPorts  = sCube.Axes[rank - 1].Length;
            int matSize = nPorts * nPorts;
            var raw     = sCube.ComplexValues;
            int nMats   = matSize == 0 ? 0 : raw.Length / matSize;
            var outRaw  = new Complex[raw.Length];

            for (int k = 0; k < nMats; k++)
            {
                int baseIdx = k * matSize;
                var m = new Mat<Complex>(nPorts, nPorts);
                for (int i = 0; i < nPorts; i++)
                for (int j = 0; j < nPorts; j++)
                    m[i, j] = raw[baseIdx + i * nPorts + j];

                var renormed = RFNetwork.SToS(m, z0Src, z0New);

                for (int i = 0; i < nPorts; i++)
                for (int j = 0; j < nPorts; j++)
                    outRaw[baseIdx + i * nPorts + j] = renormed[i, j];
            }

            return new DataCube(sCube.Axes.ToArray(), outRaw);
        }

        /// <summary>
        /// True when <paramref name="cubeSpec"/> names an S/Z/Y matrix cube ("S", "SP1.Z", …)
        /// belonging to a group that carries both S and Z0 — the single authority for "this is a
        /// network-parameter matrix element", shared by the trace card's S/Z/Y matrix-type selector
        /// (brief-dd-network-params-and-stability.md §1) and its virtual Z/Y cubes (§2).
        ///
        /// <para>Deliberately does NOT require <paramref name="cubeSpec"/> itself to resolve in
        /// <paramref name="ds"/> — only that its GROUP carries S and Z0 — so this stays correct for
        /// a virtual Z/Y cube that is never actually added to the DataSet, only derived on demand.
        /// </para>
        /// </summary>
        public static bool IsNetworkParamCubeSpec(DataSet ds, string cubeSpec)
        {
            int dot = cubeSpec.LastIndexOf('.');
            string bare = dot < 0 ? cubeSpec : cubeSpec[(dot + 1)..];
            if (bare is not (SCubeName or "Z" or "Y")) return false;

            string group  = dot < 0 ? "" : cubeSpec[..dot];
            string sSpec  = group.Length == 0 ? SCubeName  : $"{group}.{SCubeName}";
            string z0Spec = group.Length == 0 ? Z0CubeName : $"{group}.{Z0CubeName}";
            if (!ds.Contains(sSpec) || !ds.Contains(z0Spec)) return false;

            // Shape check, not just presence — mirrors the guard the Z/Y materializer itself
            // applies, so "this predicate says yes" and "Z/Y actually got materialized" never
            // disagree. A "Z0" cube that happens to exist but isn't genuinely per-port (wrong
            // length) is not a real reference impedance for this S cube.
            var sCube = ds[sSpec];
            if (sCube.Rank < 3) return false;
            int nPorts = sCube.Axes[sCube.Rank - 1].Length;
            return ds[z0Spec].ComplexValues.Length == nPorts;
        }

        /// <summary>
        /// Per-port reference impedances, length <paramref name="nPorts"/>. Falls back to 50 Ω when
        /// the DataSet carries no Z0 cube (legacy `.npy`), matching DataSetBuilder.ToSnp's own
        /// fallback so the two never disagree about an unlabelled file's reference.
        /// </summary>
        public static Complex[] ReadZ0(DataSet ds, int nPorts)
        {
            var z0 = new Complex[nPorts];
            // Group-aware for the same reason FindSCube is — a run's Z0 lives at "SP1.Z0".
            if (DataSetBuilder.FindCubeSpec(ds, Z0CubeName) is { } z0Spec)
            {
                var vals = ds[z0Spec].ComplexValues;
                for (int p = 0; p < nPorts; p++)
                    z0[p] = p < vals.Length ? vals[p] : new Complex(50, 0);
            }
            else
            {
                for (int p = 0; p < nPorts; p++) z0[p] = new Complex(50, 0);
            }
            return z0;
        }

        /// <summary>Per-frequency N×N matrices read straight out of the [freq, i, j] S cube.</summary>
        private static Mat<Complex>[] ReadMatrices(DataCube sCube, out double[] freqs)
        {
            int nFreq  = sCube.Axes[0].Length;
            int nPorts = sCube.Axes[1].Length;
            freqs      = sCube.Axes[0].Values;
            var raw    = sCube.ComplexValues;

            var mats = new Mat<Complex>[nFreq];
            for (int f = 0; f < nFreq; f++)
            {
                var m = new Mat<Complex>(nPorts, nPorts);
                for (int i = 0; i < nPorts; i++)
                for (int j = 0; j < nPorts; j++)
                    m[i, j] = raw[f * nPorts * nPorts + i * nPorts + j];
                mats[f] = m;
            }
            return mats;
        }

        /// <summary>
        /// The ordered (input, output) 2-port sub-matrix per frequency, renormalized to a uniform
        /// REAL reference and therefore ready for RFNetwork's per-matrix overloads.
        ///
        /// <para><b>Extract first, then renormalize — the order is load-bearing.</b> R-stb-4's
        /// termination assumption is that the OTHER ports are terminated in *the reference
        /// impedance*, i.e. each in its own Z0. Under that assumption a_k = 0 for every unselected
        /// port k, so the 2×2 sub-matrix is exactly the 2-port S-matrix referenced to
        /// (Z0[in], Z0[out]) — which this then renormalizes to uniform real. Renormalizing the full
        /// N×N to a uniform reference FIRST and extracting afterwards would silently encode a
        /// different physical setup (other ports matched to the new uniform value, not their own),
        /// and would give different numbers on any non-uniform network.</para>
        ///
        /// <para>The uniform real target is the real part of the INPUT port's reference impedance.
        /// That is the direct generalisation of the SNP path (NormalizedS2Port renormalizes to the
        /// real part of its single Z0), which is what lets a uniform-Z0 cube and the equivalent
        /// Touchstone file produce bit-comparable results — including for a 75 Ω part, where
        /// hardcoding 50 Ω would make the two paths disagree.</para>
        /// </summary>
        /// <param name="inPort">1-based input port.</param>
        /// <param name="outPort">1-based output port; must differ from <paramref name="inPort"/>.</param>
        public static Mat<Complex>[] TwoPortUniformReal(
            DataSet ds, int inPort, int outPort, out double[] freqs)
        {
            var sCube = FindSCube(ds)
                ?? throw new ArgumentException("DataSet carries no \"S\" cube.", nameof(ds));
            var full = ReadMatrices(sCube, out freqs);
            return TwoPortUniformReal(full, ReadZ0(ds, sCube.Axes[1].Length), inPort, outPort);
        }

        /// <summary>
        /// Matrix-level core of <see cref="TwoPortUniformReal(DataSet,int,int,out double[])"/> — the
        /// form the Trace layer uses, where the matrices come from an already-built SNP and the true
        /// per-port references come from the Z0 cube alongside it. Keeping the DataSet entry point a
        /// thin adapter over THIS is what makes R-stb-1's "one implementation" hold across both the
        /// cube-addressed and SNP-addressed callers.
        /// </summary>
        public static Mat<Complex>[] TwoPortUniformReal(
            Mat<Complex>[] full, Complex[] z0, int inPort, int outPort)
        {
            int nPorts = full.Length > 0 ? full[0].RowCount : z0.Length;
            ValidatePortPair(inPort, outPort, nPorts);

            int a = inPort - 1, b = outPort - 1;
            var z0Old = new[] { z0[a], z0[b] };
            var target = new Complex(z0[a].Real, 0.0);
            var z0New = new[] { target, target };
            bool identity = z0Old[0] == z0New[0] && z0Old[1] == z0New[1];

            var result = new Mat<Complex>[full.Length];
            for (int f = 0; f < full.Length; f++)
            {
                var m = full[f];
                var sub = new Mat<Complex>(2, 2);
                sub[0, 0] = m[a, a]; sub[0, 1] = m[a, b];
                sub[1, 0] = m[b, a]; sub[1, 1] = m[b, b];
                // Skip a provably-identity renorm so a uniform-real cube stays bit-identical to the
                // matrix it came from (same reasoning as NormalizedS2Port's own guard).
                result[f] = identity ? sub : RFNetwork.SToS(sub, z0Old, z0New);
            }
            return result;
        }

        /// <summary>
        /// Whole-network N×N matrices per frequency, renormalized to a uniform REAL reference —
        /// for passivity, which is not 2-port-limited (R-stb-6). The uniform target is the real
        /// part of port 1's reference.
        /// </summary>
        public static Mat<Complex>[] FullUniformReal(DataSet ds, out double[] freqs)
        {
            var sCube = FindSCube(ds)
                ?? throw new ArgumentException("DataSet carries no \"S\" cube.", nameof(ds));
            var full = ReadMatrices(sCube, out freqs);
            return FullUniformReal(full, ReadZ0(ds, sCube.Axes[1].Length));
        }

        /// <summary>Matrix-level core of <see cref="FullUniformReal(DataSet,out double[])"/>.</summary>
        public static Mat<Complex>[] FullUniformReal(Mat<Complex>[] full, Complex[] z0)
        {
            int nPorts = full.Length > 0 ? full[0].RowCount : z0.Length;

            var target = new Complex(z0[0].Real, 0.0);
            var z0New  = new Complex[nPorts];
            bool identity = true;
            for (int p = 0; p < nPorts; p++)
            {
                z0New[p] = target;
                if (z0[p] != target) identity = false;
            }
            if (identity) return full;

            var result = new Mat<Complex>[full.Length];
            for (int f = 0; f < full.Length; f++)
                result[f] = RFNetwork.SToS(full[f], z0, z0New);
            return result;
        }

        /// <summary>
        /// Computes a 2-port metric versus frequency straight from an S cube (R-stb-1/2/3).
        /// <paramref name="inPort"/>/<paramref name="outPort"/> are 1-based and ORDERED — swapping
        /// them swaps which factor is the load and which the source, so μ and μ′ exchange roles.
        /// </summary>
        public static double[] TwoPortMetric(
            DataSet ds, NetworkMetric metric, int inPort, int outPort, out double[] freqs)
        {
            if (!IsTwoPortOnly(metric))
                throw new ArgumentException($"{metric} is not a 2-port metric.", nameof(metric));
            return EvaluateTwoPort(TwoPortUniformReal(ds, inPort, outPort, out freqs), metric);
        }

        /// <summary>Matrix-level core: the ordered 2-port metric from raw matrices + per-port Z0.</summary>
        public static double[] TwoPortMetric(
            Mat<Complex>[] full, Complex[] z0, NetworkMetric metric, int inPort, int outPort)
        {
            if (!IsTwoPortOnly(metric))
                throw new ArgumentException($"{metric} is not a 2-port metric.", nameof(metric));
            return EvaluateTwoPort(TwoPortUniformReal(full, z0, inPort, outPort), metric);
        }

        private static double[] EvaluateTwoPort(Mat<Complex>[] mats, NetworkMetric metric)
        {
            var outv = new double[mats.Length];
            for (int f = 0; f < mats.Length; f++)
            {
                var m = mats[f];
                outv[f] = metric switch
                {
                    NetworkMetric.Mu       => RFNetwork.StabilityMu(m),
                    NetworkMetric.MuPrime  => RFNetwork.StabilityMuPrime(m),
                    NetworkMetric.MaxGain  => RFNetwork.MaxGain(m),
                    NetworkMetric.MaxGainLinear => RFNetwork.MaxGainLinear(m),
                    NetworkMetric.K        => RFNetwork.StabilityK(m).K,
                    NetworkMetric.DeltaMag => RFNetwork.StabilityK(m).Delta,
                    _ => throw new ArgumentOutOfRangeException(nameof(metric)),
                };
            }
            return outv;
        }

        /// <summary>
        /// Group delay in SECONDS versus frequency for the transmission from
        /// <paramref name="inPort"/> to <paramref name="outPort"/> (both 1-based), straight from an
        /// S cube.
        /// </summary>
        /// <remarks>
        /// <b>It is not a member of <see cref="NetworkMetric"/>, and that is structural.</b> Every
        /// metric in that enum is a function of ONE matrix, which is what lets
        /// <c>EvaluateTwoPort</c> loop over the sweep evaluating them point by point. Group delay is
        /// a derivative ALONG the sweep — it needs the frequency axis and its neighbours — so routing
        /// it through the same enum would mean handing <c>EvaluateTwoPort</c> a frequency array it
        /// has no use for in five cases out of six. It gets its own entry point instead.
        ///
        /// <para>The renormalisation is the same as every other 2-port metric's (R-stb-2): S21's
        /// phase depends on the reference impedance, so a cube referenced per-port or complex is
        /// renormalised to a uniform real reference first, always.</para>
        /// </remarks>
        public static double[] GroupDelay(DataSet ds, int inPort, int outPort, out double[] freqs)
        {
            var mats = TwoPortUniformReal(ds, inPort, outPort, out freqs);
            return RFNetwork.GroupDelay(mats, freqs, 1, 0);
        }

        /// <summary>Matrix-level core: group delay in seconds of the extracted S21.</summary>
        public static double[] GroupDelay(
            Mat<Complex>[] full, Complex[] z0, double[] freqs, int inPort, int outPort)
            => RFNetwork.GroupDelay(TwoPortUniformReal(full, z0, inPort, outPort), freqs, 1, 0);

        /// <summary>
        /// σ_max(S) versus frequency for the WHOLE network (R-stb-6) — any N ≥ 1, no port pair.
        /// </summary>
        public static double[] PassivityFull(DataSet ds, out double[] freqs)
            => EvaluatePassivity(FullUniformReal(ds, out freqs));

        /// <summary>Matrix-level core: whole-network σ_max from raw matrices + per-port Z0.</summary>
        public static double[] PassivityFull(Mat<Complex>[] full, Complex[] z0)
            => EvaluatePassivity(FullUniformReal(full, z0));

        private static double[] EvaluatePassivity(Mat<Complex>[] mats)
        {
            var outv = new double[mats.Length];
            for (int f = 0; f < mats.Length; f++) outv[f] = RFNetwork.Passivity(mats[f]);
            return outv;
        }

        /// <summary>
        /// σ_max versus frequency for the extracted (input, output) 2-port. Note this is NOT the
        /// passivity of the whole device: a 2-port extracted from a 4-port can test passive while
        /// the full network is not (R-stb-6) — which is why the card states the extraction.
        /// </summary>
        public static double[] PassivityPair(DataSet ds, int inPort, int outPort, out double[] freqs)
            => EvaluatePassivity(TwoPortUniformReal(ds, inPort, outPort, out freqs));

        /// <summary>Matrix-level core: σ_max of the extracted (input, output) 2-port.</summary>
        public static double[] PassivityPair(
            Mat<Complex>[] full, Complex[] z0, int inPort, int outPort)
            => EvaluatePassivity(TwoPortUniformReal(full, z0, inPort, outPort));

        private static void ValidatePortPair(int inPort, int outPort, int nPorts)
        {
            if (nPorts < 2)
                throw new ArgumentException(
                    $"2-port metrics need at least 2 ports; this network has {nPorts}.");
            if (inPort < 1 || inPort > nPorts)
                throw new ArgumentOutOfRangeException(nameof(inPort),
                    $"Input port {inPort} is outside 1..{nPorts}.");
            if (outPort < 1 || outPort > nPorts)
                throw new ArgumentOutOfRangeException(nameof(outPort),
                    $"Output port {outPort} is outside 1..{nPorts}.");
            if (inPort == outPort)
                throw new ArgumentException("Input and output port must differ.");
        }
    }
}
