using System.Numerics;

namespace CircuitRF.Engine.HarmonicBalance;

/// <summary>
/// Two-tone IMD measurement selectors over a multi-tone <see cref="HbResult"/>
/// (harmonic-balance.md §6.3, measurements.md). These invert the locked mixIndex enumeration
/// (<see cref="MixingGrid"/>) so callers address products by tone pair (k₁,k₂):
///   carriers (1,0)/(0,1); IM2 baseband (1,−1); IM3 (2,−1)/(−1,2); IM5 (3,−2)/(−2,3).
///
/// Non-retained half-plane reps (e.g. (−1,2), (−2,3)) are read via conjugate symmetry from their
/// retained partner (their conjugate (1,−2)/(2,−3)); power is invariant under that conjugation.
///
/// Power/sign convention follows the engine's INl contract (HbEngine, "INl current-direction"):
///   output power delivered into the load at node n, product (k₁,k₂):
///     Pout = −½·Re(V[n] · conj(INl[n]))           (positive when the device drives the load)
/// dBc is referenced to a chosen carrier: IMn_dBc = 10·log10(P_IM / P_carrier).
/// </summary>
public static class TwoToneMeasurements
{
    /// <summary>Complex voltage phasor at node, mixing product (k₁,k₂), sweep point.</summary>
    public static Complex Tone(HbResult r, int sweepIdx, string node, int k1, int k2)
        => PhasorsAt(r, sweepIdx, NodeIndex(r, node), k1, k2).V;

    /// <summary>Output power (W) delivered into the load at node, product (k₁,k₂).</summary>
    public static double PoutW(HbResult r, int sweepIdx, string node, int k1, int k2)
    {
        var (v, i) = PhasorsAt(r, sweepIdx, NodeIndex(r, node), k1, k2);
        return -0.5 * (v * Complex.Conjugate(i)).Real;
    }

    /// <summary>Output power (dBm) at node, product (k₁,k₂). −∞ for non-positive power.</summary>
    public static double PoutDbm(HbResult r, int sweepIdx, string node, int k1, int k2)
    {
        double p = PoutW(r, sweepIdx, node, k1, k2);
        return p > 0 ? 10.0 * Math.Log10(p) + 30.0 : double.NegativeInfinity;
    }

    /// <summary>
    /// Intermodulation level in dBc: 10·log10(P(imK₁,imK₂) / P(carK₁,carK₂)) at node, sweep point.
    /// Use e.g. ImDbc(r, s, "n_drain", 2,−1, 1,0) for the lower IM3 relative to the lower carrier.
    /// </summary>
    public static double ImDbc(HbResult r, int sweepIdx, string node,
        int imK1, int imK2, int carK1, int carK2)
    {
        double pIm  = PoutW(r, sweepIdx, node, imK1, imK2);
        double pCar = PoutW(r, sweepIdx, node, carK1, carK2);
        if (pCar <= 0) return double.NaN;
        if (pIm  <= 0) return double.NegativeInfinity;
        return 10.0 * Math.Log10(pIm / pCar);
    }

    /// <summary>Signed physical frequency (Hz) of product (k₁,k₂) = k₁f₁ + k₂f₂.</summary>
    public static double FrequencyOf(HbResult r, int k1, int k2)
        => k1 * r.ToneFreqsHz[0] + k2 * r.ToneFreqsHz[1];

    // ── internals ──────────────────────────────────────────────────────────────

    private static int NodeIndex(HbResult r, string node)
    {
        int idx = Array.FindIndex(r.InterfaceNodeNames,
            s => s.Equals(node, StringComparison.Ordinal));
        if (idx < 0)
            throw new ArgumentException(
                $"Interface node '{node}' not found among [{string.Join(", ", r.InterfaceNodeNames)}].");
        return idx;
    }

    // (V, INl) at (k1,k2): direct for a retained rep, else conjugate of the retained partner.
    private static (Complex V, Complex I) PhasorsAt(HbResult r, int sweepIdx, int n, int k1, int k2)
    {
        if (r.Grid is null)
            throw new InvalidOperationException(
                "TwoToneMeasurements requires a multi-tone HbResult (Grid is null — single-tone run).");

        int m = r.Grid.IndexOf(k1, k2);
        if (m >= 0)
            return (r.V[sweepIdx][n, m], r.INl[sweepIdx][n, m]);

        int mc = r.Grid.IndexOf(-k1, -k2);   // conjugate partner in the stored half-plane
        if (mc >= 0)
            return (Complex.Conjugate(r.V[sweepIdx][n, mc]),
                    Complex.Conjugate(r.INl[sweepIdx][n, mc]));

        throw new ArgumentException(
            $"Mixing product ({k1},{k2}) and its conjugate are not in the retained set " +
            $"(MaxMixOrder={r.Grid.MaxMixOrder}).");
    }
}
