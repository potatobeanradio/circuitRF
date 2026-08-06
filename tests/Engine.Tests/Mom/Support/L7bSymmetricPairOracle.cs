using System.Numerics;
using CircuitRF.Engine.Mom;
using NumFlat;

namespace CircuitRF.Engine.Tests.Mom.Support;

/// <summary>
/// <b>D1 — L7b's fixed-matrix symmetric-pair construction, kept as a TEST ORACLE rather than as a
/// production branch.</b>
///
/// <para>Two code paths that must agree are two code paths that will eventually disagree, and the
/// one that drifts would be the rarely-exercised one — so the general decomposition SUBSUMES the
/// symmetric pair in production. What is genuinely worth keeping is the <i>oracle</i>: for a
/// symmetric pair the modal matrix <c>[1 1; 1 −1]</c> is exact by symmetry alone, with or without
/// loss, because it diagonalises ANY 2×2 of the form <c>[a b; b a]</c> whatever a and b are. That
/// makes this an exact, eigensolver-free answer for a genuinely coupled, genuinely lossy structure —
/// the only such answer in the ladder, and therefore the one thing that can hold the general path
/// honest at N = 2.</para>
///
/// <para>Copied verbatim from <c>RlgcToSparams.BuildCoupledPair</c> as it shipped in L7b, so the
/// continuity gate compares against what was actually released rather than against a re-derivation
/// of it.</para>
/// </summary>
public static class L7bSymmetricPairOracle
{
    /// <summary>The 4×4 S at one frequency, by L7b's own even/odd block construction.</summary>
    public static Mat<Complex> S4(RlgcModel rlgc, double lengthMeters, double freqHz, Complex[] z0)
    {
        var modes = ModalDecomposition.Decompose(rlgc);
        double w = 2.0 * Math.PI * freqHz;

        var (rEven, rOdd) = ModalDecomposition.ModalR(rlgc, w);

        var (zE, yE) = ModeZY(modes.Even, rEven, w);
        var (zO, yO) = ModeZY(modes.Odd,  rOdd,  w);

        var gammaE = Principal(zE * yE);
        var gammaO = Principal(zO * yO);
        var zChE   = Principal(zE / yE);
        var zChO   = Principal(zO / yO);

        var e2 = LineZ(zChE, gammaE * lengthMeters);
        var o2 = LineZ(zChO, gammaO * lengthMeters);

        var zMat = new Mat<Complex>(4, 4);
        for (int a = 0; a < 2; a++)
        for (int b = 0; b < 2; b++)
        {
            var self   = 0.5 * (e2[a, b] + o2[a, b]);
            var mutual = 0.5 * (e2[a, b] - o2[a, b]);
            zMat[a,     b]     = self;    zMat[2 + a, 2 + b] = self;
            zMat[a,     2 + b] = mutual;  zMat[2 + a, b]     = mutual;
        }
        return RfCore.RFNetwork.ZToS(zMat, z0);
    }

    /// <summary>L7b's per-mode static Z_e / Z_o — the ohms R-gen-3a's gate is asserted against.</summary>
    public static (double Even, double Odd) StaticModalImpedances(RlgcModel rlgc)
    {
        var m = ModalDecomposition.Decompose(rlgc);
        return (m.Even.Z0, m.Odd.Z0);
    }

    /// <summary>L7b's per-mode ε_eff — C_mode/C₀_mode.</summary>
    public static (double Even, double Odd) ModalEeff(RlgcModel rlgc)
    {
        var m = ModalDecomposition.Decompose(rlgc);
        return (m.Even.Eeff, m.Odd.Eeff);
    }

    /// <summary>L7b's per-mode frequency-dependent Zc — the ohms the tline group publishes.</summary>
    public static (Complex Even, Complex Odd) FrequencyZc(RlgcModel rlgc, double freqHz)
    {
        var modes = ModalDecomposition.Decompose(rlgc);
        double w = 2.0 * Math.PI * freqHz;
        var (rEven, rOdd) = ModalDecomposition.ModalR(rlgc, w);
        var (zE, yE) = ModeZY(modes.Even, rEven, w);
        var (zO, yO) = ModeZY(modes.Odd,  rOdd,  w);
        return (Principal(zE / yE), Principal(zO / yO));
    }

    private static (Complex Z, Complex Y) ModeZY(ModeRlgc mode, double rPerM, double w)
        => (new Complex(rPerM, w * mode.LPerM), Complex.ImaginaryOne * w * mode.CComplexPerM);

    private static Mat<Complex> LineZ(Complex zc, Complex gl)
    {
        var sinh = Complex.Sinh(gl);
        var m = new Mat<Complex>(2, 2);
        m[0, 0] = m[1, 1] = zc * Complex.Cosh(gl) / sinh;
        m[0, 1] = m[1, 0] = zc / sinh;
        return m;
    }

    private static Complex Principal(Complex v)
    {
        var s = Complex.Sqrt(v);
        return s.Real < 0 ? -s : s;
    }
}
