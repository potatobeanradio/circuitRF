// L8c — the ORACLE kernel for Tier 2: direct Sommerfeld integration, made affordable enough to fill
// an integral with.
//
// R-fil-6: every entry is validated against SommerfeldIntegral, NEVER against Dcim. DCIM is the
// production path and validating it against itself proves nothing. But one Sommerfeld evaluation
// costs 3-8 ms (measured on both starters at 2/10/20 GHz), and even a single cell-pair integral wants
// thousands of kernel samples — so the oracle is sampled once onto a radial grid and interpolated.
//
// TWO CHOICES MAKE THAT SAFE, AND BOTH ARE ABOUT THE SHAPE OF THE FUNCTION RATHER THAN ABOUT TASTE:
//
//   • It is S(ρ) = 4πρ·G(ρ) that is tabulated, not G. S is O(1) and bounded everywhere including
//     ρ → 0, where G itself diverges — and it is EXACTLY L8a's own "scaled error" measure ("error as
//     a fraction of the free-space kernel at the same ρ, which is what a matrix fill experiences").
//     So an interpolation error in S is, term for term, an error in the quantity R-lgf-4 reports.
//   • The grid is logarithmic in ρ, because that is the variable S is smooth in: measured on FR-4 at
//     10 GHz, S moves from 0.37027 at ρ = 1 nm to 0.18325 at ρ = 10 mm — seven decades of ρ for a
//     factor of two in S.
//
// Below the smallest sample S is clamped. That is not a fudge: S varies by 4e-5 between ρ = 1e-9 and
// 1e-7 m, and the region ρ < ρ_min contributes ~ρ_min/cell of the integral, so the product is far
// below anything being measured. The tolerance for it is the same one the tests apply to the oracle
// itself — REFINE IT AND SEE WHETHER THE ANSWER MOVES, which is the discipline L8a's own two
// oracle-was-wrong findings established.

using System.Numerics;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Engine.Tests.Mom.Support;

public sealed class SommerfeldRadialTable
{
    private readonly Complex[] _s;      // 4πρ·G at the sample points
    private readonly double   _logLo, _dLog;

    public int SampleCount => _s.Length;
    public double RhoMin { get; }
    public double RhoMax { get; }

    private SommerfeldRadialTable(Complex[] s, double rhoMin, double rhoMax, double logLo, double dLog)
    { _s = s; RhoMin = rhoMin; RhoMax = rhoMax; _logLo = logLo; _dLog = dLog; }

    public static SommerfeldRadialTable Build(SpectralGreens greens, GreensKernel kernel,
                                              double rhoMin, double rhoMax, int pointsPerDecade = 12)
    {
        var ok = SommerfeldIntegral.CanIntegrate(greens);
        Assert.True(ok.Ok, ok.Reason);

        double logLo = Math.Log10(rhoMin), logHi = Math.Log10(rhoMax);
        int n = Math.Max(8, (int)Math.Ceiling((logHi - logLo) * pointsPerDecade) + 1);
        double dLog = (logHi - logLo) / (n - 1);

        var s = new Complex[n];
        System.Threading.Tasks.Parallel.For(0, n, i =>
        {
            double rho = Math.Pow(10.0, logLo + i * dLog);
            s[i] = SommerfeldIntegral.Evaluate(greens, kernel, rho).Value * (4.0 * Math.PI * rho);
        });

        return new SommerfeldRadialTable(s, rhoMin, rhoMax, logLo, dLog);
    }

    public Complex Evaluate(double rhoM)
    {
        if (!(rhoM > 0)) return Complex.Zero;

        double t = (Math.Log10(rhoM) - _logLo) / _dLog;
        Complex s;
        if (t <= 0)                  s = _s[0];
        else if (t >= _s.Length - 1) s = _s[^1];
        else
        {
            int i = (int)t;
            t -= i;
            Complex p0 = _s[Math.Max(i - 1, 0)];
            Complex p1 = _s[i];
            Complex p2 = _s[Math.Min(i + 1, _s.Length - 1)];
            Complex p3 = _s[Math.Min(i + 2, _s.Length - 1)];
            Complex a = 2.0 * p1, b = p2 - p0;
            Complex c = 2.0 * p0 - 5.0 * p1 + 4.0 * p2 - p3;
            Complex d = -p0 + 3.0 * p1 - 3.0 * p2 + p3;
            s = 0.5 * (a + b * t + c * (t * t) + d * (t * t * t));
        }
        return s / (4.0 * Math.PI * rhoM);
    }
}
