// ================================================================
//  NetworkInverse.cs  —  the network that undoes a 2K-port, and the
//                        cascade (wave-transfer) algebra it rests on.
//
//  brief-de-1. A 2K-port's ports split into side 1 (ports 1…K) and
//  side 2 (ports K+1…2K); write its S in K×K blocks S11, S12, S21, S22.
//  The cascade matrix T is defined by
//
//      [b1]       [a2]
//      [a1] = T · [b2]
//
//  so a chain of X then Y (X's side 2 joined to Y's side 1) is T_X·T_Y.
//
//      S → T:  T = [ S12 − S11·S21⁻¹·S22    S11·S21⁻¹ ]
//                  [ −S21⁻¹·S22             S21⁻¹     ]
//      T → S:  S = [ T12·T22⁻¹    T11 − T12·T22⁻¹·T21 ]
//                  [ T22⁻¹        −T22⁻¹·T21          ]
//
//  The inverse network F⁻¹ has T_F⁻¹, which in F's own blocks is
//
//      T(F⁻¹) = [ S12⁻¹           −S12⁻¹·S11          ]
//               [ S22·S12⁻¹       S21 − S22·S12⁻¹·S11 ]
//
//  (block inversion of T_F; its Schur complement is exactly S12). It
//  needs only S12 — the transmission from side 2 back to side 1 — to be
//  invertible. Its S-matrix needs more: M = S21 − S22·S12⁻¹·S11 must be
//  invertible too, and that fails for ordinary fixtures (a series 100 Ω
//  in a 50 Ω system has det S = 0, and the series −100 Ω that undoes it
//  shorts the two reference terminations into each other). So the
//  cascade form is the one to stamp; the S form is for files and plots.
//
//  F⁻¹ cascaded with F, in either order, is a through connection.
// ================================================================

using System;
using System.Numerics;
using NumFlat;

namespace RfCore
{
    /// <summary>
    /// The network that undoes a 2K-port (<c>F⁻¹</c>, which cascaded with <c>F</c> in either order is a through
    /// connection), and the cascade algebra it is built on. Pure functions; every matrix is in the 2K-port's own
    /// reference impedance, which all its ports share. brief-de-1.
    /// </summary>
    public static class NetworkInverse
    {
        /// <summary>The 1-norm condition number above which a block is treated as singular. A transmission block this
        /// ill-conditioned means some port on one side does not reach the other side, and an inverse built from it would
        /// be rounding noise.</summary>
        public const double MaxCondition = 1e12;

        /// <summary>K, half the port count of <paramref name="s"/>. Throws on a non-square or odd-sized matrix: a network
        /// that can be undone has as many ports on one side as on the other.</summary>
        public static int SideSize(Mat<Complex> s)
        {
            if (s.RowCount != s.ColCount)
                throw new ArgumentException($"An S-matrix must be square; this one is {s.RowCount}×{s.ColCount}.", nameof(s));
            if (s.RowCount == 0 || s.RowCount % 2 != 0)
                throw new ArgumentException(
                    $"A network can only be undone when it has an even number of ports, one side matching the other; this one has {s.RowCount}.",
                    nameof(s));
            return s.RowCount / 2;
        }

        // ── Cascade algebra ──────────────────────────────────────────────────

        /// <summary>The cascade matrix of a 2K-port. Refuses (returns false) when S21 cannot be inverted.</summary>
        public static bool TryToCascade(Mat<Complex> s, out Mat<Complex> t, out string? refusal)
        {
            int k = SideSize(s);
            var (s11, s12, s21, s22) = Blocks(s, k);
            t = default;
            if (!TryInverse(s21, out var i21, out double cond))
            {
                refusal = $"its forward transmission (side 1 to side 2) cannot be inverted (condition number {Fmt(cond)})";
                return false;
            }
            var s11i21 = s11 * i21;
            t = Assemble(s12 - s11i21 * s22, s11i21, Negate(i21 * s22), i21);
            refusal = null;
            return true;
        }

        /// <summary>The S-matrix of a cascade matrix. Refuses (returns false) when T22 cannot be inverted.</summary>
        public static bool TryFromCascade(Mat<Complex> t, out Mat<Complex> s, out string? refusal)
        {
            int k = SideSize(t);
            var (t11, t12, t21, t22) = Blocks(t, k);
            s = default;
            if (!TryInverse(t22, out var i22, out double cond))
            {
                refusal = $"the network has no S-matrix at this reference impedance (condition number {Fmt(cond)})";
                return false;
            }
            var t12i22 = t12 * i22;
            s = Assemble(t12i22, t11 - t12i22 * t21, i22, Negate(i22 * t21));
            refusal = null;
            return true;
        }

        /// <summary>
        /// The S-matrix of <paramref name="x"/> followed by <paramref name="y"/>, X's side 2 joined to Y's side 1, both
        /// 2K-ports on one reference impedance. Built from the S blocks directly (the connection of the two inner
        /// sides), so it never inverts a transmission block and works for any pair the connection itself allows.
        /// </summary>
        public static Mat<Complex> Cascade(Mat<Complex> x, Mat<Complex> y)
        {
            int k = SideSize(x);
            if (SideSize(y) != k)
                throw new ArgumentException($"Both networks must have the same port count ({2 * k} and {y.RowCount}).", nameof(y));
            var (a11, a12, a21, a22) = Blocks(x, k);
            var (b11, b12, b21, b22) = Blocks(y, k);
            var id = Identity(k);
            // Waves bouncing between X's side 2 and Y's side 1: (I − b11·a22)⁻¹ and (I − a22·b11)⁻¹.
            if (!TryInverse(id - b11 * a22, out var l, out double c1))
                throw new ArgumentException($"The two networks resonate against each other at their junction (condition number {Fmt(c1)}).");
            if (!TryInverse(id - a22 * b11, out var r, out double c2))
                throw new ArgumentException($"The two networks resonate against each other at their junction (condition number {Fmt(c2)}).");
            return Assemble(
                a11 + a12 * l * b11 * a21,
                a12 * l * b12,
                b21 * r * a21,
                b22 + b21 * r * a22 * b12);
        }

        // ── The inverse ──────────────────────────────────────────────────────

        /// <summary>
        /// The cascade matrix of <c>F⁻¹</c>, from <paramref name="s"/> = F's S-matrix. Needs only F's S12 (side 2 back to
        /// side 1) to be invertible; refuses (returns false) when it is not, with a reason the caller prefixes with the
        /// instance and the frequency.
        /// </summary>
        public static bool TryInverseCascade(Mat<Complex> s, out Mat<Complex> t, out string? refusal)
        {
            int k = SideSize(s);
            var (s11, s12, s21, s22) = Blocks(s, k);
            t = default;
            if (!TryInverse(s12, out var i12, out double cond))
            {
                refusal = $"its transmission between the two sides cannot be inverted (condition number {Fmt(cond)}): " +
                          "some port on one side does not reach the other";
                return false;
            }
            var i12s11 = i12 * s11;
            t = Assemble(i12, Negate(i12s11), s22 * i12, s21 - s22 * i12s11);
            refusal = null;
            return true;
        }

        /// <summary>
        /// The S-matrix of <c>F⁻¹</c>, from <paramref name="s"/> = F's S-matrix, in F's reference impedance. Refuses where
        /// <see cref="TryInverseCascade"/> does, and also where the inverse exists but has no S-matrix at this reference
        /// impedance (a series 100 Ω in a 50 Ω system). A circuit containing such an inverse can still be solved from its
        /// cascade form.
        /// </summary>
        public static bool TryInvert(Mat<Complex> s, out Mat<Complex> inverse, out string? refusal)
        {
            inverse = default;
            if (!TryInverseCascade(s, out var t, out refusal)) return false;
            if (!TryFromCascade(t, out inverse, out var r))
            {
                refusal = "the network that undoes it " + r!.Replace("the network has", "has");
                return false;
            }
            return true;
        }

        // ── Small dense helpers ──────────────────────────────────────────────

        internal static (Mat<Complex> M11, Mat<Complex> M12, Mat<Complex> M21, Mat<Complex> M22) Blocks(Mat<Complex> m, int k)
        {
            Mat<Complex> Block(int r0, int c0)
            {
                var b = new Mat<Complex>(k, k);
                for (int i = 0; i < k; i++)
                    for (int j = 0; j < k; j++) b[i, j] = m[r0 + i, c0 + j];
                return b;
            }
            return (Block(0, 0), Block(0, k), Block(k, 0), Block(k, k));
        }

        internal static Mat<Complex> Assemble(Mat<Complex> m11, Mat<Complex> m12, Mat<Complex> m21, Mat<Complex> m22)
        {
            int k = m11.RowCount;
            var m = new Mat<Complex>(2 * k, 2 * k);
            for (int i = 0; i < k; i++)
                for (int j = 0; j < k; j++)
                {
                    m[i, j]         = m11[i, j];
                    m[i, j + k]     = m12[i, j];
                    m[i + k, j]     = m21[i, j];
                    m[i + k, j + k] = m22[i, j];
                }
            return m;
        }

        internal static Mat<Complex> Negate(Mat<Complex> m)
        {
            var r = new Mat<Complex>(m.RowCount, m.ColCount);
            for (int i = 0; i < m.RowCount; i++)
                for (int j = 0; j < m.ColCount; j++) r[i, j] = -m[i, j];
            return r;
        }

        internal static Mat<Complex> Identity(int n)
        {
            var m = new Mat<Complex>(n, n);
            for (int i = 0; i < n; i++) m[i, i] = Complex.One;
            return m;
        }

        /// <summary>
        /// The inverse of a square matrix, refused (false) when its 1-norm condition number exceeds
        /// <see cref="MaxCondition"/> or the factorisation produces anything that is not finite. <paramref name="condition"/>
        /// is +∞ for an exactly singular matrix.
        /// </summary>
        internal static bool TryInverse(Mat<Complex> a, out Mat<Complex> inverse, out double condition)
        {
            int n = a.RowCount;
            inverse = default;
            condition = double.PositiveInfinity;
            double normA = Norm1(a);
            if (normA == 0 || !double.IsFinite(normA)) return false;

            Mat<Complex> inv;
            if (n == 1)
            {
                if (a[0, 0] == Complex.Zero) return false;
                inv = new Mat<Complex>(1, 1);
                inv[0, 0] = Complex.One / a[0, 0];
            }
            else
            {
                try
                {
                    var lu = a.Lu();
                    inv = new Mat<Complex>(n, n);
                    for (int j = 0; j < n; j++)
                    {
                        var e = new Vec<Complex>(n);
                        e[j] = Complex.One;
                        var x = lu.Solve(e);
                        for (int i = 0; i < n; i++) inv[i, j] = x[i];
                    }
                }
                catch (Exception ex) when (ex is ArithmeticException or InvalidOperationException or ArgumentException)
                {
                    return false;
                }
            }

            double normInv = Norm1(inv);
            if (!double.IsFinite(normInv)) return false;
            condition = normA * normInv;
            if (condition > MaxCondition) return false;
            inverse = inv;
            return true;
        }

        /// <summary>The 1-norm: the largest column sum of magnitudes.</summary>
        internal static double Norm1(Mat<Complex> a)
        {
            double max = 0;
            for (int j = 0; j < a.ColCount; j++)
            {
                double sum = 0;
                for (int i = 0; i < a.RowCount; i++) sum += a[i, j].Magnitude;
                if (double.IsNaN(sum)) return double.NaN;
                max = Math.Max(max, sum);
            }
            return max;
        }

        private static string Fmt(double v) => double.IsFinite(v) ? v.ToString("G3", System.Globalization.CultureInfo.InvariantCulture) : "infinite";
    }
}
