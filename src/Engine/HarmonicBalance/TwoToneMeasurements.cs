using System.Numerics;
using RfCore.Data;

namespace CircuitRF.Engine.HarmonicBalance;

/// <summary>
/// Two-tone IMD measurement selectors over a multi-tone <see cref="DataSet"/>
/// (harmonic-balance.md §6.3, measurements.md). These invert the locked mixIndex enumeration
/// encoded in the "mixIndex" axis Labels of the "V" cube so callers address products by tone
/// pair (k₁,k₂): carriers (1,0)/(0,1); IM2 baseband (1,−1); IM3 (2,−1)/(−1,2); IM5 (3,−2)/(−2,3).
///
/// Non-retained half-plane reps (e.g. (−1,2), (−2,3)) are read via conjugate symmetry from their
/// retained partner (their conjugate (1,−2)/(2,−3)); power is invariant under that conjugation.
///
/// Power/sign convention follows the engine's INl contract (HbEngine, "INl current-direction"):
///   output power delivered into the load at node n, product (k₁,k₂):
///     Pout = −½·Re(V[n] · conj(INl[n]))           (positive when the device drives the load)
/// dBc is referenced to a chosen carrier: IMn_dBc = 10·log10(P_IM / P_carrier).
///
/// The DataSet must have been returned by HbEngine.Run() with a multi-tone HbAnalysisParams.
/// Expected cubes: "V" [node, mixIndex, Pin], "INl" [node, mixIndex, Pin] (Complex).
/// MixIndex axis Labels = "(k1,k2)" strings, Values = k1·f1+k2·f2 Hz.
/// Required metadata cubes: "ToneFreqs" [tone] (Real), "MetaMixOrder" [1] (Real).
///
/// <para><b>Three or more tones use the same selectors through the <c>int[] k</c> overloads</b>
/// (harmonic-balance.md §6.5), which carry the implementation; the (k₁,k₂) signatures above are
/// thin forwarders so every existing two-tone caller is byte-compatible. The T-tone lattice tags
/// products as "(k1,…,kT)" and the conjugate-partner fallback generalizes unchanged — power is
/// invariant under that conjugation at any tone count. The class name is historical.</para>
/// </summary>
public static class TwoToneMeasurements
{
    /// <summary>Complex voltage phasor at node, mixing product (k₁,k₂), sweep point.</summary>
    public static Complex Tone(DataSet ds, int sweepIdx, string node, int k1, int k2)
        => Tone(ds, sweepIdx, node, [k1, k2]);

    /// <summary>Complex voltage phasor at node, mixing product k = (k₁…k_T), sweep point.</summary>
    public static Complex Tone(DataSet ds, int sweepIdx, string node, int[] k)
        => PhasorsAt(ds, sweepIdx, NodeIndex(ds, node), k).V;

    /// <summary>Output power (W) delivered into the load at node, product (k₁,k₂).</summary>
    public static double PoutW(DataSet ds, int sweepIdx, string node, int k1, int k2)
        => PoutW(ds, sweepIdx, node, [k1, k2]);

    /// <summary>Output power (W) delivered into the load at node, product k = (k₁…k_T).</summary>
    public static double PoutW(DataSet ds, int sweepIdx, string node, int[] k)
    {
        var (v, i) = PhasorsAt(ds, sweepIdx, NodeIndex(ds, node), k);
        return -0.5 * (v * Complex.Conjugate(i)).Real;
    }

    /// <summary>Output power (dBm) at node, product (k₁,k₂). −∞ for non-positive power.</summary>
    public static double PoutDbm(DataSet ds, int sweepIdx, string node, int k1, int k2)
        => PoutDbm(ds, sweepIdx, node, [k1, k2]);

    /// <summary>Output power (dBm) at node, product k = (k₁…k_T). −∞ for non-positive power.</summary>
    public static double PoutDbm(DataSet ds, int sweepIdx, string node, int[] k)
    {
        double p = PoutW(ds, sweepIdx, node, k);
        return p > 0 ? 10.0 * Math.Log10(p) + 30.0 : double.NegativeInfinity;
    }

    /// <summary>
    /// Intermodulation level in dBc: 10·log10(P(imK₁,imK₂) / P(carK₁,carK₂)) at node, sweep point.
    /// Use e.g. ImDbc(ds, s, "n_drain", 2,−1, 1,0) for the lower IM3 relative to the lower carrier.
    /// </summary>
    public static double ImDbc(DataSet ds, int sweepIdx, string node,
        int imK1, int imK2, int carK1, int carK2)
        => ImDbc(ds, sweepIdx, node, [imK1, imK2], [carK1, carK2]);

    /// <summary>
    /// Intermodulation level in dBc at any tone count: 10·log10(P(imK) / P(carK)) at node,
    /// sweep point. E.g. <c>ImDbc(ds, s, "n_drain", [1,1,-1], [1,0,0])</c> for a three-tone
    /// product relative to the first carrier.
    /// </summary>
    public static double ImDbc(DataSet ds, int sweepIdx, string node, int[] imK, int[] carK)
    {
        double pIm  = PoutW(ds, sweepIdx, node, imK);
        double pCar = PoutW(ds, sweepIdx, node, carK);
        if (pCar <= 0) return double.NaN;
        if (pIm  <= 0) return double.NegativeInfinity;
        return 10.0 * Math.Log10(pIm / pCar);
    }

    /// <summary>Signed physical frequency (Hz) of product (k₁,k₂) = k₁f₁ + k₂f₂.</summary>
    public static double FrequencyOf(DataSet ds, int k1, int k2)
        => FrequencyOf(ds, [k1, k2]);

    /// <summary>Signed physical frequency (Hz) of product k: Σ_t k_t·f_t.</summary>
    public static double FrequencyOf(DataSet ds, int[] k)
    {
        var tf = ds["ToneFreqs"].RealValues;
        double f = 0;
        for (int t = 0; t < k.Length && t < tf.Length; t++) f += k[t] * tf[t];
        return f;
    }

    // ── internals ──────────────────────────────────────────────────────────────

    private static int NodeIndex(DataSet ds, string node)
    {
        // Find the "node" axis by name (sweep axes are prepended before it).
        var vCube = ds["V"];
        int nodeAxisIdx = -1;
        for (int a = 0; a < vCube.Rank; a++)
            if (vCube.Axes[a].Name == "node") { nodeAxisIdx = a; break; }
        if (nodeAxisIdx < 0)
            throw new InvalidOperationException(
                "V cube has no axis named 'node' — not a valid HB DataSet.");
        var labels = vCube.Axes[nodeAxisIdx].Labels
            ?? throw new InvalidOperationException("V cube node axis has no labels.");
        int idx = Array.FindIndex(labels,
            s => s.Equals(node, StringComparison.Ordinal));
        if (idx < 0)
            throw new ArgumentException(
                $"Interface node '{node}' not found among [{string.Join(", ", labels)}].");
        return idx;
    }

    /// <summary>
    /// Find the mix index for k in the mixIndex axis Labels ("(k1,…,kT)").
    /// Returns -1 if not found (non-retained rep). The tag is built the same way the engine
    /// writes it, so this inverts the lattice's LOCKED enumeration without depending on its order.
    /// </summary>
    private static int FindMixIndex(DataSet ds, int[] k)
    {
        // Find the "mixIndex" axis by name (sweep axes are prepended before it).
        var vCube = ds["V"];
        for (int a = 0; a < vCube.Rank; a++)
        {
            if (vCube.Axes[a].Name == "mixIndex")
            {
                var labels = vCube.Axes[a].Labels;
                if (labels is null) return -1;
                return Array.IndexOf(labels, MixingLattice.Label(k));
            }
        }
        return -1;
    }

    // (V, INl) at k: direct for a retained rep, else conjugate of the retained partner.
    private static (Complex V, Complex I) PhasorsAt(DataSet ds, int sweepIdx, int n, int[] k)
    {
        // Find node and mixIndex axis positions by name; sweep axes are prepended before node.
        var vCube = ds["V"];
        int nodeAxisIdx = -1, mixAxisIdx = -1;
        for (int a = 0; a < vCube.Rank; a++)
        {
            if (vCube.Axes[a].Name == "node")     nodeAxisIdx = a;
            if (vCube.Axes[a].Name == "mixIndex") mixAxisIdx  = a;
        }
        if (mixAxisIdx < 0)
            throw new InvalidOperationException(
                "TwoToneMeasurements requires a multi-tone DataSet (no 'mixIndex' axis found — " +
                "this is a single-tone result).");

        int m = FindMixIndex(ds, k);
        if (m >= 0)
            return (ReadAt(ds["V"],     vCube.Rank, nodeAxisIdx, mixAxisIdx, sweepIdx, n, m),
                    ReadAt(ds["INl"], vCube.Rank, nodeAxisIdx, mixAxisIdx, sweepIdx, n, m));

        var neg = new int[k.Length];
        for (int t = 0; t < k.Length; t++) neg[t] = -k[t];
        int mc = FindMixIndex(ds, neg);   // conjugate partner in the stored half-space
        if (mc >= 0)
            return (Complex.Conjugate(ReadAt(ds["V"],     vCube.Rank, nodeAxisIdx, mixAxisIdx, sweepIdx, n, mc)),
                    Complex.Conjugate(ReadAt(ds["INl"], vCube.Rank, nodeAxisIdx, mixAxisIdx, sweepIdx, n, mc)));

        int maxOrder = (int)Math.Round(ds["MetaMixOrder"].RealValues[0]);
        throw new ArgumentException(
            $"Mixing product {MixingLattice.Label(k)} and its conjugate are not in the retained set " +
            $"(MaxMixOrder={maxOrder}).");
    }

    // Build a slice selecting node=n, mixIndex=m, all sweep axes = sweepIdx.
    private static Complex ReadAt(DataCube cube, int rank, int nodeAxisIdx, int mixAxisIdx,
        int sweepIdx, int n, int m)
    {
        var args = new object[rank];
        args[nodeAxisIdx] = n;
        args[mixAxisIdx]  = m;
        for (int a = 0; a < rank; a++)
            if (a != nodeAxisIdx && a != mixAxisIdx)
                args[a] = sweepIdx;
        return (Complex)cube[args];
    }
}
