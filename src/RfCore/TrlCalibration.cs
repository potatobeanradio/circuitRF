// ================================================================
//  TrlCalibration.cs  —  two fixtures from Thru, Reflect and Line
//
//  brief-de-1. The instrument measures every standard through the same
//  two fixtures: A on port 1 (its port 1 the instrument side, port 2
//  the part's) and B on port 2 (port 1 the part's side, port 2 the
//  instrument's). In cascade matrices (NetworkInverse):
//
//      Mt = A·B            the Thru
//      Ml = A·L·B          the Line, L = diag(e^(−γℓ), e^(+γℓ))
//
//  ℓ being the Line's extra length over the Thru. Then
//
//      P = Ml·Mt⁻¹ = A·L·A⁻¹
//
//  so A's columns are P's eigenvectors u (for e^(−γℓ)) and w (for
//  e^(+γℓ)), each up to a scale: A = [u w]·diag(p, q). And B follows from
//  the Thru: B = A⁻¹·Mt = diag(1/p, 1/q)·N with N = [u w]⁻¹·Mt.
//
//  Two unknowns are left, p and q. The Reflect fixes their ratio
//  r = p/q. The same unknown Γ terminates both fixtures' part sides:
//
//      port 1:  Γ1 = (u0·rΓ + w0)/(u1·rΓ + w1)       ⇒  rΓ  = (w0 − Γ1·w1)/(Γ1·u1 − u0)
//      port 2:  N1·(1, Γ2) = (Γ/r)·N0·(1, Γ2)        ⇒  Γ/r = (N10 + N11·Γ2)/(N00 + N01·Γ2)
//
//  so r = ±√(rΓ ÷ Γ/r); the sign is the one that puts Γ on the side of
//  the Smith chart the user states (open or short). Reciprocity of A
//  (S12_A = S21_A) fixes the product: p·q = 1/det[u w].
//
//  Written in [u w] rather than in ratios of their entries (S11_A,
//  Δ_A/S22_A, …) because those ratios divide by S22_A, and a perfectly
//  matched fixture has none.
//
//  Two signs are left free by the algebra and must follow frequency:
//  which eigenvalue is e^(−γℓ), and the sign of q (S21_A's square root).
//  The part de-embedded with A and B depends on neither, only on
//  products of them, so it is tempting to drop the tracking. The fixture
//  FILES do depend on them: a file whose S21 changes sign between two
//  samples interpolates through zero, and a fixture file is read by
//  interpolation.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using NumFlat;

namespace RfCore
{
    /// <summary>Which side of the Smith chart the Reflect standard is on. Its exact value comes out of the solve.</summary>
    public enum TrlReflect { Open, Short }

    /// <summary>
    /// What a TRL calibration found.
    /// </summary>
    /// <param name="FixtureA">Fixture A: port 1 the instrument side, port 2 the part's side.</param>
    /// <param name="FixtureB">Fixture B: port 1 the part's side, port 2 the instrument side.</param>
    /// <param name="LineDegrees">The Line's electrical length over the Thru's, in degrees, unwrapped across frequency.</param>
    /// <param name="LineLossDb">The Line's extra loss over the Thru's, in dB.</param>
    /// <param name="Gamma">The Reflect standard as solved, at the reference plane.</param>
    /// <param name="Usable">Whether <see cref="LineDegrees"/> lies inside [20°, 160°] modulo 180°, where the line separates
    /// the two fixtures well enough for the answer to be trusted.</param>
    public sealed record TrlResult(
        SNP FixtureA, SNP FixtureB,
        double[] LineDegrees, double[] LineLossDb, Complex[] Gamma, bool[] Usable)
    {
        /// <summary>How many points fall outside the usable interval.</summary>
        public int UnusableCount
        {
            get { int n = 0; foreach (bool u in Usable) if (!u) n++; return n; }
        }
    }

    /// <summary>
    /// Thru-Reflect-Line calibration of a two-port measurement: the two fixtures between the instrument and the part, from
    /// three standards measured through them. Pure function, no file I/O. brief-de-1.
    ///
    /// <para><b>Things a user asks, answered here once.</b></para>
    /// <list type="bullet">
    /// <item>The reference plane is the middle of the Thru. A Thru with length moves it there, and the Line's "extra
    /// length" is then the Line's minus the Thru's.</item>
    /// <item>The part's S-parameters, de-embedded with these fixtures, are referenced to the <b>Line's characteristic
    /// impedance</b>, not to the instrument's. That is a property of TRL.</item>
    /// <item>The fixtures are assumed reciprocal. The standards fix only the products S21·S12 of each fixture; reciprocity
    /// is what splits them into two files.</item>
    /// <item>The Reflect need only be the same on both ports and known to be an open or a short; its value is solved for
    /// and returned.</item>
    /// <item>The answer is trusted where the Line's extra length is 20°-160° modulo 180°. Near 0° and 180° the line and
    /// the thru are nearly the same standard and the solve loses its footing, which is why one line covers 8:1.</item>
    /// </list>
    /// </summary>
    public static class TrlCalibration
    {
        /// <summary>The usable interval of the Line's extra electrical length, in degrees, modulo 180°.</summary>
        public const double UsableFromDeg = 20, UsableToDeg = 160;

        /// <summary>
        /// Solves for both fixtures at every frequency of the three files, which must share one grid, be two-ports of
        /// S-parameters and share one reference impedance. Returns the result, or a refusal sentence and no result.
        /// <paramref name="track"/> turns off the continuity rules; it exists for the test that shows why they are there.
        /// </summary>
        public static (TrlResult? Result, string? Refusal) Solve(SNP thru, SNP reflect, SNP line, TrlReflect reflectKind, bool track = true)
        {
            if ((Check(thru, "Thru") ?? Check(reflect, "Reflect") ?? Check(line, "Line")) is { } bad) return (null, bad);
            if ((GridMismatch(thru, reflect, "Reflect") ?? GridMismatch(thru, line, "Line")) is { } grid) return (null, grid);
            if (reflect.Z0 != thru.Z0 || line.Z0 != thru.Z0)
                return (null, $"the three standards must share one reference impedance; the Thru is referenced to {Ohms(thru.Z0)}, " +
                              $"the Reflect to {Ohms(reflect.Z0)} and the Line to {Ohms(line.Z0)}");

            int n = thru.FrequencyCount;
            var fa = new Mat<Complex>[n];
            var fb = new Mat<Complex>[n];
            var deg = new double[n];
            var loss = new double[n];
            var gamma = new Complex[n];
            var usable = new bool[n];

            Complex? prevL = null, prev2L = null;
            Complex? prevS21 = null;
            double prevF = 0, prev2F = 0, prevAngle = 0;

            for (int k = 0; k < n; k++)
            {
                double f = thru.Frequencies[k];
                string at = $" at {Ghz(f)}";

                if (!NetworkInverse.TryToCascade(thru.Matrices[k], out var tt, out var r1))
                    return (null, $"the Thru {r1}{at}");
                if (!NetworkInverse.TryToCascade(line.Matrices[k], out var tl, out var r2))
                    return (null, $"the Line {r2}{at}");
                if (!NetworkInverse.TryInverse(tt, out var ttInv, out _))
                    return (null, $"the Thru cannot be inverted{at}");

                var p = tl * ttInv;
                var (l1, l2) = Eigenvalues(p);

                // Which eigenvalue is e^(−γℓ). At the first point, the one with the negative imaginary part — right while
                // the line is under 180°. After that, the one nearer the previous point's, carried forward in phase.
                bool firstIsForward;
                if (!track || prevL is null)
                    firstIsForward = l1.Imaginary <= l2.Imaginary;
                else
                {
                    Complex predicted = prevL.Value;
                    if (prev2L is { } p2 && prevF != prev2F)
                    {
                        double step = (f - prevF) / (prevF - prev2F);
                        predicted = prevL.Value * Complex.Exp(step * Complex.Log(prevL.Value / p2));
                    }
                    firstIsForward = (l1 - predicted).Magnitude <= (l2 - predicted).Magnitude;
                }
                var (lf, lb) = firstIsForward ? (l1, l2) : (l2, l1);

                var u = Eigenvector(p, lf);
                var w = Eigenvector(p, lb);
                var e = new Mat<Complex>(2, 2);
                e[0, 0] = u.X; e[1, 0] = u.Y; e[0, 1] = w.X; e[1, 1] = w.Y;
                if (!NetworkInverse.TryInverse(e, out var eInv, out _))
                    return (null, $"the Thru and the Line are the same standard{at} (the Line's extra length is a multiple of 180°, " +
                                  "or the Line is no longer than the Thru); choose a band or a line length that avoids it");
                var nMat = eInv * tt;
                Complex detE = u.X * w.Y - w.X * u.Y;

                Complex g1 = reflect.Matrices[k][0, 0], g2 = reflect.Matrices[k][1, 1];
                Complex rGamma = (w.X - g1 * w.Y) / (g1 * u.Y - u.X);
                Complex gammaOverR = (nMat[1, 0] + nMat[1, 1] * g2) / (nMat[0, 0] + nMat[0, 1] * g2);
                Complex r = Complex.Sqrt(rGamma / gammaOverR);
                Complex gm = rGamma / r;
                if ((reflectKind == TrlReflect.Short) == (gm.Real > 0)) { r = -r; gm = -gm; }

                Complex q = Complex.Sqrt(Complex.One / (r * detE));
                Complex s21a = Complex.One / (w.Y * q);
                if (track)
                {
                    bool flip = prevS21 is { } ps ? (s21a - ps).Magnitude > (-s21a - ps).Magnitude : s21a.Real < 0;
                    if (flip) { q = -q; s21a = -s21a; }
                }
                Complex pScale = r * q;

                var ta = new Mat<Complex>(2, 2);
                ta[0, 0] = u.X * pScale; ta[1, 0] = u.Y * pScale; ta[0, 1] = w.X * q; ta[1, 1] = w.Y * q;
                var tb = new Mat<Complex>(2, 2);
                tb[0, 0] = nMat[0, 0] / pScale; tb[0, 1] = nMat[0, 1] / pScale;
                tb[1, 0] = nMat[1, 0] / q;      tb[1, 1] = nMat[1, 1] / q;

                if (!NetworkInverse.TryFromCascade(ta, out fa[k], out var ra)) return (null, $"fixture A {ra}{at}");
                if (!NetworkInverse.TryFromCascade(tb, out fb[k], out var rb)) return (null, $"fixture B {rb}{at}");

                // The line's electrical length, unwrapped point to point.
                double angle = -lf.Phase * 180 / Math.PI;
                if (k == 0) deg[k] = angle;
                else
                {
                    double d = angle - prevAngle;
                    d -= 360 * Math.Round(d / 360);
                    deg[k] = deg[k - 1] + d;
                }
                prevAngle = angle;
                loss[k] = -20 * Math.Log10(lf.Magnitude);
                gamma[k] = gm;
                double m = ((deg[k] % 180) + 180) % 180;
                usable[k] = m >= UsableFromDeg && m <= UsableToDeg;

                prev2L = prevL; prevL = lf;
                prev2F = prevF; prevF = f;
                prevS21 = s21a;
            }

            return (new TrlResult(Fixture(thru, fa), Fixture(thru, fb), deg, loss, gamma, usable), null);
        }

        private static SNP Fixture(SNP like, Mat<Complex>[] mats)
            => new(like.Frequencies, mats, MatrixType.S, MatrixFormat.RI, like.Z0) { FreqUnit = FrequencyUnit.GHz };

        private static string? Check(SNP s, string name)
        {
            if (s.IsEmpty) return $"the {name} file holds no data";
            if (s.Type != MatrixType.S) return $"the {name} file holds {s.Type}-parameters; TRL reads S-parameters";
            if (s.Ports != 2) return $"the {name} file is a {s.Ports}-port; TRL reads two-ports";
            if (s.Z0PerPort is { } per && (per[0] != per[1]))
                return $"the {name} file's two ports are referenced to different impedances ({Ohms(per[0])} and {Ohms(per[1])})";
            return null;
        }

        private static string? GridMismatch(SNP thru, SNP other, string name)
        {
            if (other.FrequencyCount != thru.FrequencyCount)
                return $"the {name} file has {other.FrequencyCount} frequencies and the Thru file {thru.FrequencyCount}; " +
                       "the three standards must be measured on one frequency grid";
            for (int k = 0; k < thru.FrequencyCount; k++)
            {
                double a = thru.Frequencies[k], b = other.Frequencies[k];
                if (Math.Abs(a - b) > 1e-9 * Math.Max(Math.Abs(a), 1.0))
                    return $"the {name} file's point {k + 1} is at {Ghz(b)} and the Thru file's at {Ghz(a)}; " +
                           "the three standards must be measured on one frequency grid";
            }
            return null;
        }

        /// <summary>The two eigenvalues of a 2×2 matrix.</summary>
        private static (Complex, Complex) Eigenvalues(Mat<Complex> m)
        {
            Complex half = (m[0, 0] + m[1, 1]) / 2;
            Complex det = m[0, 0] * m[1, 1] - m[0, 1] * m[1, 0];
            Complex disc = Complex.Sqrt(half * half - det);
            return (half + disc, half - disc);
        }

        /// <summary>An eigenvector of a 2×2 matrix for eigenvalue <paramref name="l"/>, from whichever row of (M − λI) is
        /// the better conditioned.</summary>
        private static (Complex X, Complex Y) Eigenvector(Mat<Complex> m, Complex l)
        {
            // Row 1: (m00 − λ)·x + m01·y = 0  ⇒  (m01, λ − m00).   Row 2: m10·x + (m11 − λ)·y = 0  ⇒  (λ − m11, m10).
            Complex a0 = m[0, 1], a1 = l - m[0, 0];
            Complex b0 = l - m[1, 1], b1 = m[1, 0];
            return a0.Magnitude + a1.Magnitude >= b0.Magnitude + b1.Magnitude ? (a0, a1) : (b0, b1);
        }

        private static string Ghz(double hz) => (hz / 1e9).ToString("G6", CultureInfo.InvariantCulture) + " GHz";

        private static string Ohms(Complex z) => z.Imaginary == 0
            ? z.Real.ToString("G4", CultureInfo.InvariantCulture) + " Ω"
            : $"{z.Real.ToString("G4", CultureInfo.InvariantCulture)}{(z.Imaginary < 0 ? "−" : "+")}j{Math.Abs(z.Imaginary).ToString("G4", CultureInfo.InvariantCulture)} Ω";
    }
}
