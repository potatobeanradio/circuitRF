// ================================================================
//  NoiseMetrics.cs  —  two-port small-signal noise: parameters, correlation matrices, circles
//
//  Conventions, for every function here:
//    • Spectral densities are SINGLE-SIDED and ABSOLUTE (V²/Hz, V·A/Hz, A²/Hz, W/Hz) — never
//      normalised to kT0. A resistor R at T is 4kTR V²/Hz; a matched load's outgoing wave is kT W/Hz.
//    • The noise factor F is referred to T0 = 290 K; k is the exact SI value.
//    • Noise waves are Kurokawa power waves: Cs = <c cᴴ> of the OUTGOING waves with every port
//      terminated in its own noiseless Z0 (Re Z0 > 0). A passive network at a uniform T has
//      Cs = kT(I − S·Sᴴ) (Bosma).
//    • C_Y = <i iᴴ> of the short-circuit noise currents, port currents I = Y·V + i (into the ports).
//    • The chain correlation C_A = <[v; i][v; i]ᴴ> refers the noise to the INPUT: v in series, i in
//      shunt, so F(Ys) = 1 + (C11·|Ys|² + 2·Re(Ys·C12) + C22) / (4kT0·Re Ys) (Hillbrand & Russer 1976).
//    • Γ is referenced to a REAL resistance R: Γ = (1 − R·Y)/(1 + R·Y). For a Touchstone file R is the
//      option line's R; for a computed result it is Re Z0 of port 1.
//    • Matrices are 0-based: [0,0] is port 1, [1,1] port 2; C12 = [0,1] = <x1·conj(x2)>.
// ================================================================

using System;
using System.Linq;
using System.Numerics;
using NumFlat;

namespace RfCore.Data
{
    /// <summary>The four noise parameters of a two-port.</summary>
    /// <param name="Fmin">Minimum noise factor, linear (≥ 1).</param>
    /// <param name="GammaOpt">Source reflection coefficient that reaches <paramref name="Fmin"/>,
    /// referenced to <paramref name="R"/>.</param>
    /// <param name="Rn">Equivalent noise resistance, Ω (not normalised).</param>
    /// <param name="R">The real reference resistance <paramref name="GammaOpt"/> is referred to, Ω.</param>
    public readonly record struct TwoPortNoise(double Fmin, Complex GammaOpt, double Rn, double R)
    {
        /// <summary>Minimum noise figure, dB.</summary>
        public double NFminDb => 10.0 * Math.Log10(Fmin);

        /// <summary>Optimum source admittance, S.</summary>
        public Complex Yopt => (1 - GammaOpt) / (R * (1 + GammaOpt));
    }

    /// <summary>Two-port noise math on the conventions in this file's header.</summary>
    public static class NoiseMetrics
    {
        /// <summary>Boltzmann's constant, J/K — exact since the 2019 SI. RfCore cannot reach
        /// <c>Core</c>'s <c>Temperature.Boltzmann</c>, which is the same number.</summary>
        public const double Boltzmann = 1.380649e-23;

        /// <summary>The standard noise temperature the noise factor is referred to, K.</summary>
        public const double T0 = 290.0;

        /// <summary>k·T0, W/Hz.</summary>
        public const double KT0 = Boltzmann * T0;

        // ── Noise parameters ↔ chain correlation ─────────────────────────────

        /// <summary>
        /// C_A from the noise parameters:
        /// <c>4kT0·[[Rn, (Fmin−1)/2 − Rn·Yopt*], [(Fmin−1)/2 − Rn·Yopt, Rn·|Yopt|²]]</c>.
        /// </summary>
        public static Mat<Complex> ChainCorrelation(TwoPortNoise p)
        {
            Complex y = p.Yopt;
            double  a = (p.Fmin - 1) / 2;
            var c = new Mat<Complex>(2, 2);
            c[0, 0] = 4 * KT0 * p.Rn;
            c[0, 1] = 4 * KT0 * (a - p.Rn * Complex.Conjugate(y));
            c[1, 0] = Complex.Conjugate(c[0, 1]);
            c[1, 1] = 4 * KT0 * p.Rn * Sq(y);
            return c;
        }

        /// <summary>
        /// The noise parameters of C_A, with Γopt referenced to <paramref name="r"/>:
        /// <c>Rn = C11/4kT0</c>, <c>Bopt = Im C12/C11</c>, <c>Gopt = √(C22/C11 − Bopt²)</c>,
        /// <c>Fmin = 1 + (Re C12 + C11·Gopt)/2kT0</c>.
        /// <para>A C_A with no series noise voltage (C11 = 0 — a noiseless two-port, in practice) has
        /// no finite optimum: it reads Fmin = 1, Rn = 0, and Γopt = −1 (the short its shunt noise
        /// vanishes into) or 0 when there is no noise at all.</para>
        /// </summary>
        public static TwoPortNoise FromChainCorrelation(Mat<Complex> ca, double r)
        {
            double  c11 = ca[0, 0].Real, c22 = ca[1, 1].Real;
            Complex c12 = ca[0, 1];
            if (!(c11 > 0)) return new(1, c22 > 0 ? -1 : 0, 0, r);

            double bopt = c12.Imaginary / c11;
            double gopt = Math.Sqrt(Math.Max(0, c22 / c11 - bopt * bopt));
            var    yopt = new Complex(gopt, bopt);
            return new(1 + (c12.Real + c11 * gopt) / (2 * KT0),
                       (1 - r * yopt) / (1 + r * yopt),
                       c11 / (4 * KT0), r);
        }

        // ── Noise waves → admittance → chain ─────────────────────────────────

        /// <summary>
        /// The short-circuit noise-current correlation C_Y (A²/Hz) of an N-port from its S-matrix, its
        /// noise-wave correlation <paramref name="cs"/> (W/Hz) and per-port <paramref name="z0"/>
        /// (complex allowed, Re &gt; 0): <c>C_Y = (I + Y·Z0)·R^−½·Cs·R^−½·(I + Y·Z0)ᴴ</c>, with
        /// <c>Z0 = diag(z0)</c>, <c>R = diag(Re z0)</c> and Y the power-wave admittance of S.
        /// </summary>
        public static Mat<Complex> AdmittanceCorrelation(Mat<Complex> s, Mat<Complex> cs, Complex[] z0) =>
            CyFromWaves(PowerWaveAdmittance(s, z0), cs, z0);

        private static Mat<Complex> CyFromWaves(Mat<Complex> y, Mat<Complex> cs, Complex[] z0)
        {
            int n = y.RowCount;
            var m = new Mat<Complex>(n, n);
            for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                m[i, j] = ((i == j ? 1 : 0) + y[i, j] * z0[j]) / Math.Sqrt(z0[j].Real);
            return m * cs * m.ConjugateTranspose();
        }

        /// <summary>C_A = T·C_Y·Tᴴ, <c>T = [[0, −1/Y21], [1, −Y11/Y21]]</c>, for a two-port with
        /// admittance matrix <paramref name="y"/>.</summary>
        public static Mat<Complex> ChainFromAdmittance(Mat<Complex> cy, Mat<Complex> y)
        {
            var t = new Mat<Complex>(2, 2);
            t[0, 1] = -1 / y[1, 0];
            t[1, 0] = 1;
            t[1, 1] = -y[0, 0] / y[1, 0];
            return t * cy * t.ConjugateTranspose();
        }

        /// <summary>The inverse of <see cref="ChainFromAdmittance"/>: C_Y = T⁻¹·C_A·T⁻ᴴ,
        /// <c>T⁻¹ = [[−Y11, 1], [−Y21, 0]]</c> — how a two-port given by its noise parameters becomes
        /// two noise currents across its ports.</summary>
        public static Mat<Complex> AdmittanceFromChain(Mat<Complex> ca, Mat<Complex> y)
        {
            var t = new Mat<Complex>(2, 2);
            t[0, 0] = -y[0, 0];
            t[0, 1] = 1;
            t[1, 0] = -y[1, 0];
            return t * ca * t.ConjugateTranspose();
        }

        /// <summary>
        /// The noise parameters of a two-port from its S-matrix, noise-wave correlation
        /// <paramref name="cs"/> (W/Hz) and per-port <paramref name="z0"/> (complex allowed, Re &gt; 0),
        /// through C_Y and C_A. Γopt is referenced to <paramref name="r"/>.
        /// </summary>
        public static TwoPortNoise FromWaves(Mat<Complex> s, Mat<Complex> cs, Complex[] z0, double r)
        {
            var y = PowerWaveAdmittance(s, z0);
            return FromChainCorrelation(ChainFromAdmittance(CyFromWaves(y, cs, z0), y), r);
        }

        /// <summary>
        /// The noise factor with port 1 driven from its own Z0 and port 2 loaded in its own:
        /// <c>F = 1 + Cs[1,1]/(kT0·|S21|²)</c>.
        /// </summary>
        public static double NoiseFactorAtZ0(Mat<Complex> s, Mat<Complex> cs) =>
            1 + cs[1, 1].Real / (KT0 * Sq(s[1, 0]));

        /// <summary>
        /// The noise parameters straight from the noise waves, for a uniform REAL
        /// <paramref name="z0"/> (Wedge &amp; Rutledge 1992), Γopt referenced to <paramref name="z0"/>:
        /// <code>
        ///   η1   = c11|S21|² − 2Re(c12·S21·S11*) + c22|S11|²,   ζ = c22·S11 − c12·S21
        ///   Γopt = 2ζ* / (c22 + η1 + √((c22 + η1)² − 4|ζ|²))      (= η2(1 − √(1 − 1/|η2|²)), η2 = (c22 + η1)/2ζ)
        ///   Fmin = 1 + (c22 − η1|Γopt|²) / (kT0|S21|²(1 + |Γopt|²))
        ///   Rn   = z0·(c22 + η1 + 2Re ζ) / (4kT0|S21|²)              (F·(1 − |Γs|²) at Γs = −1)
        /// </code>
        /// An independent route to <see cref="FromWaves"/>, which the tests hold it against. The Γopt
        /// form is the textbook one rationalised, so Γopt = 0 (ζ = 0) needs no special case.
        /// </summary>
        public static TwoPortNoise FromWavesDirect(Mat<Complex> s, Mat<Complex> cs, double z0)
        {
            Complex s11 = s[0, 0], s21 = s[1, 0], c12 = cs[0, 1];
            double  c11 = cs[0, 0].Real, c22 = cs[1, 1].Real, g = KT0 * Sq(s21);

            double  eta1 = c11 * Sq(s21) - 2 * (c12 * s21 * Complex.Conjugate(s11)).Real + c22 * Sq(s11);
            Complex zeta = c22 * s11 - c12 * s21;
            double  h    = c22 + eta1;
            Complex gopt = 2 * Complex.Conjugate(zeta) / (h + Math.Sqrt(Math.Max(0, h * h - 4 * Sq(zeta))));
            double  g2   = Sq(gopt);
            return new(1 + (c22 - eta1 * g2) / (g * (1 + g2)),
                       gopt,
                       z0 * (h + 2 * zeta.Real) / (4 * g),
                       z0);
        }

        // ── Noise factor, circles, realizability ─────────────────────────────

        /// <summary>
        /// The noise factor (linear) from a source of reflection <paramref name="gammaS"/>, referenced to
        /// <c>p.R</c>: <c>F = Fmin + 4(Rn/R)|Γs − Γopt|² / ((1 − |Γs|²)|1 + Γopt|²)</c>.
        /// </summary>
        public static double NoiseFactor(TwoPortNoise p, Complex gammaS) =>
            p.Fmin + 4 * (p.Rn / p.R) * Sq(gammaS - p.GammaOpt) / ((1 - Sq(gammaS)) * Sq(1 + p.GammaOpt));

        /// <summary>
        /// The constant-noise-factor circle for <paramref name="f"/> (linear) in the Γs plane referenced
        /// to <c>p.R</c>: with <c>N = (R/4Rn)(F − Fmin)|1 + Γopt|²</c>, centre <c>Γopt/(1 + N)</c> and
        /// radius <c>√(N² + N(1 − |Γopt|²))/(1 + N)</c>. Below Fmin there is no circle (radius NaN).
        /// </summary>
        public static (Complex Center, double Radius) NoiseCircle(TwoPortNoise p, double f)
        {
            double n = p.R / (4 * p.Rn) * (f - p.Fmin) * Sq(1 + p.GammaOpt);
            return (p.GammaOpt / (1 + n), Math.Sqrt(n * n + n * (1 - Sq(p.GammaOpt))) / (1 + n));
        }

        /// <summary>
        /// Whether the parameters describe a physical two-port — C_A positive semidefinite, which holds
        /// exactly when <c>0 ≤ Fmin − 1 ≤ 4·Rn·Gopt</c> (det C_A = (4kT0)²·a(2·Rn·Gopt − a),
        /// a = (Fmin − 1)/2). <paramref name="tolerance"/> is absolute on the noise factor.
        /// </summary>
        public static bool IsRealizable(TwoPortNoise p, double tolerance = 1e-9) =>
            p.Rn >= 0 && p.Fmin - 1 >= -tolerance && p.Fmin - 1 <= 4 * p.Rn * p.Yopt.Real + tolerance;

        // ── Helpers ──────────────────────────────────────────────────────────

        /// <summary>
        /// Y of a power-wave S at per-port complex Z0. <see cref="RFNetwork.SToY(Mat{Complex}, Complex[])"/>
        /// is the √Z0 (pseudo-wave) form, which agrees with power waves only for a REAL reference, so S
        /// is first renormalised to Re Z0 with the power-wave <see cref="RFNetwork.SToS(Mat{Complex}, Complex[], Complex[])"/>.
        /// </summary>
        private static Mat<Complex> PowerWaveAdmittance(Mat<Complex> s, Complex[] z0)
        {
            if (z0.All(z => z.Imaginary == 0)) return RFNetwork.SToY(s, z0);
            var r = z0.Select(z => new Complex(z.Real, 0)).ToArray();
            return RFNetwork.SToY(RFNetwork.SToS(s, z0, r), r);
        }

        private static double Sq(Complex z) => z.Real * z.Real + z.Imaginary * z.Imaginary;
    }
}
