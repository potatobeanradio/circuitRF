using System.Numerics;
using CircuitRF.Engine.Mom;
using NumFlat;

namespace CircuitRF.Engine.Tests.Mom.Support;

/// <summary>
/// <b>The EXACT 2-conductor multiconductor line, in closed form, with no eigensolver.</b>
///
/// <para>This is the oracle Tier G1 actually needs, and it exists because of a finding the brief did
/// not anticipate: <b>Route A is exact for a symmetric pair, so a symmetric pair cannot measure its
/// error.</b> The matrix <c>[1 1; 1 −1]</c> diagonalises ANY 2×2 of the form <c>[a b; b a]</c>, and
/// for a mirror-symmetric pair every one of [R], [L], [G], [C] has that form — so the lossless
/// <c>[L][C]</c> and the lossy <c>[Z][Y]</c> have the SAME eigenvectors and Route A's perturbative
/// step discards exactly nothing. Comparing Route A against L7b there measures continuity (which is
/// Tier G2's job and worth having) but not accuracy.</para>
///
/// <para>An ASYMMETRIC pair is where Route A can be wrong, and there is no closed-form even/odd
/// answer to compare against — but a 2×2 complex eigenproblem has a closed form (the quadratic
/// formula), so the exact modal decomposition of the lossy <c>[Z][Y]</c> can be written down
/// directly. That is what this does. It shares R-gen-2's block construction with production
/// deliberately: the ONLY difference from Route A is <b>which Tv</b> is used — the exact
/// frequency-dependent one from <c>[Z][Y]</c> here, the frequency-independent lossless one there —
/// so the comparison isolates precisely the approximation being measured and nothing else.</para>
/// </summary>
public static class ExactTwoConductorOracle
{
    /// <summary>The exact 4×4 S at one frequency.</summary>
    public static Mat<Complex> S4(RlgcModel rlgc, double lengthMeters, double freqHz, Complex[] z0)
    {
        double w = 2.0 * Math.PI * freqHz;

        var z = new Mat<Complex>(2, 2);
        var r = ModalDecomposition.Symmetrise(rlgc.RMatrix(w));
        var l = ModalDecomposition.Symmetrise(rlgc.L);
        for (int i = 0; i < 2; i++)
        for (int j = 0; j < 2; j++)
            z[i, j] = new Complex(r[i, j], w * l[i, j]);

        var c = ModalDecomposition.Symmetrise(rlgc.CComplex);
        var y = new Mat<Complex>(2, 2);
        for (int i = 0; i < 2; i++)
        for (int j = 0; j < 2; j++)
            y[i, j] = Complex.ImaginaryOne * w * c[i, j];

        var (tv, _) = Eigen2(Mul(z, y));
        var tvInv = Inv2(tv);

        // Ti = (Tvᵀ)⁻¹ rescaled per mode so each mode's current pattern matches its voltage pattern
        // — the SAME rule production uses, so the reported Zc is comparable as well as the S.
        var ti = new Mat<Complex>(2, 2);
        for (int m = 0; m < 2; m++)
        {
            Complex bilinear = tvInv[m, 0] * tvInv[m, 0] + tvInv[m, 1] * tvInv[m, 1];
            for (int k = 0; k < 2; k++) ti[k, m] = tvInv[m, k] / bilinear;
        }
        var tiInv = Inv2(ti);

        var zm = Mul(Mul(tvInv, z), ti);
        var ym = Mul(Mul(tiInv, y), tv);

        var xSelf  = new Complex[2];
        var xCross = new Complex[2];
        for (int m = 0; m < 2; m++)
        {
            var g  = Principal(zm[m, m] * ym[m, m]);
            var zc = Principal(zm[m, m] / ym[m, m]);
            var gl = g * lengthMeters;
            var sh = Complex.Sinh(gl);
            xSelf[m]  = zc * Complex.Cosh(gl) / sh;
            xCross[m] = zc / sh;
        }

        var self  = Mul(Mul(tv, Diag(xSelf)),  tiInv);
        var cross = Mul(Mul(tv, Diag(xCross)), tiInv);

        var zMat = new Mat<Complex>(4, 4);
        for (int a = 0; a < 2; a++)
        for (int b = 0; b < 2; b++)
        {
            zMat[2 * a,     2 * b]     = self[a, b];
            zMat[2 * a + 1, 2 * b + 1] = self[a, b];
            zMat[2 * a,     2 * b + 1] = cross[a, b];
            zMat[2 * a + 1, 2 * b]     = cross[a, b];
        }
        return RfCore.RFNetwork.ZToS(zMat, z0);
    }

    /// <summary>The closed-form eigen-decomposition of a 2×2 complex matrix — the quadratic formula
    /// for the eigenvalues, and the null-space of (M − λI) for each eigenvector.</summary>
    private static (Mat<Complex> V, Complex[] Lambda) Eigen2(Mat<Complex> m)
    {
        var tr  = m[0, 0] + m[1, 1];
        var det = m[0, 0] * m[1, 1] - m[0, 1] * m[1, 0];
        var disc = Complex.Sqrt(tr * tr / 4.0 - det);
        var lam = new[] { tr / 2.0 + disc, tr / 2.0 - disc };

        var v = new Mat<Complex>(2, 2);
        for (int k = 0; k < 2; k++)
        {
            Complex a, b;
            if (m[0, 1].Magnitude >= m[1, 0].Magnitude && m[0, 1].Magnitude > 0)
            {
                a = m[0, 1];              b = lam[k] - m[0, 0];
            }
            else if (m[1, 0].Magnitude > 0)
            {
                a = lam[k] - m[1, 1];     b = m[1, 0];
            }
            else
            {
                a = k == 0 ? Complex.One : Complex.Zero;
                b = k == 0 ? Complex.Zero : Complex.One;
            }
            double norm = Math.Sqrt(a.Magnitude * a.Magnitude + b.Magnitude * b.Magnitude);
            v[0, k] = a / norm;
            v[1, k] = b / norm;
        }
        return (v, lam);
    }

    private static Mat<Complex> Inv2(Mat<Complex> m)
    {
        var det = m[0, 0] * m[1, 1] - m[0, 1] * m[1, 0];
        var r = new Mat<Complex>(2, 2);
        r[0, 0] =  m[1, 1] / det; r[0, 1] = -m[0, 1] / det;
        r[1, 0] = -m[1, 0] / det; r[1, 1] =  m[0, 0] / det;
        return r;
    }

    private static Mat<Complex> Diag(Complex[] d)
    {
        var r = new Mat<Complex>(d.Length, d.Length);
        for (int i = 0; i < d.Length; i++) r[i, i] = d[i];
        return r;
    }

    private static Mat<Complex> Mul(Mat<Complex> a, Mat<Complex> b)
    {
        var r = new Mat<Complex>(a.RowCount, b.ColCount);
        for (int i = 0; i < a.RowCount; i++)
        for (int k = 0; k < a.ColCount; k++)
        for (int j = 0; j < b.ColCount; j++)
            r[i, j] += a[i, k] * b[k, j];
        return r;
    }

    private static Complex Principal(Complex v)
    {
        var s = Complex.Sqrt(v);
        return s.Real < 0 ? -s : s;
    }
}
