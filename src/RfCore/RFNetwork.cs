// ================================================================
//  RFNetwork.cs  —  RF network math: parameter conversions,
//                   renormalization, de-embedding, stability, gain
//
//  All public members are static; the class is a pure function
//  library — no state, no UI dependencies.
//
//  Generalized N-port formulas (complex per-port Z₀):
//    Let √z = diag(√Z₀₁ … √Z₀ₙ),  √y = (√z)⁻¹
//    S → Z :  Z  = √z · (I+S) · (I−S)⁻¹ · √z
//    Z → S :  Ẑ  = √y · Z · √y,   S = (Ẑ−I)(Ẑ+I)⁻¹
//    S → Y :  Y  = √y · (I−S) · (I+S)⁻¹ · √y
//    Y → S :  Ŷ  = √z · Y · √z,   S = (I−Ŷ)(I+Ŷ)⁻¹
//    S → S' (renorm, direct power-wave bilinear):
//             S_new = (R + T·S_old) · (P + Q·S_old)⁻¹
//
//  Complex √ uses principal branch: √(a+jb) with Re(√) ≥ 0
//
//  References:
//    • Pozar, "Microwave Engineering", 4th ed., §12.1
//    • Edwards & Sinsky, IEEE Trans MTT v40 n12, Dec 1992  (μ factor)
//    • Frickey, IEEE Trans MTT v42 n2 (1994)               (complex Z₀ 2-port)
//    • Reveyrand, IEEE INMMIC 2018                          (multiport matrix form)
//    • Wikipedia "Impedance parameters"                     (generalized √z formulas)
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using NumFlat;

namespace RfCore
{
    /// <summary>
    /// Static library of RF network mathematics: parameter-type conversions,
    /// S-parameter renormalization, T-matrix cascade, de-embedding,
    /// 2-port stability analysis, comparison utilities, and frequency interpolation.
    /// </summary>
    public static partial class RFNetwork
    {
        // ============================================================
        //  Warning infrastructure
        //
        //  Consumers subscribe to OnWarning to receive library diagnostics
        //  (out-of-range interpolation, etc.).  ConsoleWarnings enables
        //  optional stderr echo for CLI / test scenarios.
        // ============================================================

        /// <summary>
        /// Raised when the library emits a diagnostic that the caller should surface
        /// (e.g. out-of-range interpolation, non-physical extrapolated S-parameters).
        /// Subscribe before calling any operation that may warn.
        /// </summary>
        public static event Action<string>? OnWarning;

        /// <summary>
        /// When true, warnings are also echoed to <see cref="Console.Error"/>.
        /// Off by default — callers opt in explicitly.
        /// </summary>
        public static bool ConsoleWarnings { get; set; } = false;

        internal static void Warn(string message)
        {
            OnWarning?.Invoke(message);
            if (ConsoleWarnings)
                Console.Error.WriteLine($"[RfCore] {message}");
        }

        // ============================================================
        //  Low-level matrix building helpers (internal)
        // ============================================================

        /// <summary>Diagonal matrix of complex √Z₀ values.</summary>
        private static Mat<Complex> SqrtZ0Matrix(Complex[] z0)
        {
            int n = z0.Length;
            var m = new Mat<Complex>(n, n);
            for (int i = 0; i < n; i++)
                m[i, i] = ComplexSqrt(z0[i]);
            return m;
        }

        /// <summary>Diagonal n*n matrix of complex √Z₀ values.</summary>
        private static Mat<Complex> SqrtZ0Matrix(Complex z0, int n)
        {
            var m = new Mat<Complex>(n, n);
            for (int i = 0; i < n; i++)
                m[i, i] = ComplexSqrt(z0);
            return m;
        }

        /// <summary>Diagonal matrix of 1/√Z₀  (i.e. √Y₀) values.</summary>
        private static Mat<Complex> SqrtY0Matrix(Complex[] z0)
        {
            int n = z0.Length;
            var m = new Mat<Complex>(n, n);
            for (int i = 0; i < n; i++)
                m[i, i] = Complex.One / ComplexSqrt(z0[i]);
            return m;
        }
        /// <summary>Diagonal n*n matrix of 1/√Z₀  (i.e. √Y₀) values.</summary>
        private static Mat<Complex> SqrtY0Matrix(Complex z0, int n)
        {
            var m = new Mat<Complex>(n, n);
            for (int i = 0; i < n; i++)
                m[i, i] = Complex.One / ComplexSqrt(z0);
            return m;
        }

        private static Mat<Complex> Identity(int n)
        {
            var m = new Mat<Complex>(n, n);
            for (int i = 0; i < n; i++) m[i, i] = Complex.One;
            return m;
        }

        /// <summary>Plain (non-conjugate) transpose.</summary>
        private static Mat<Complex> Transpose(Mat<Complex> M)
        {
            int rows = M.RowCount, cols = M.ColCount;
            var R = new Mat<Complex>(cols, rows);
            for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                R[c, r] = M[r, c];
            return R;
        }

        /// <summary>
        /// Conjugate-transpose of a square diagonal matrix — simply conjugates
        /// each diagonal entry; off-diagonal entries remain zero.
        /// </summary>
        private static Mat<Complex> ConjTransposeDiag(Mat<Complex> D)
        {
            int n = D.RowCount;
            var R = new Mat<Complex>(n, n);
            for (int i = 0; i < n; i++)
                R[i, i] = Complex.Conjugate(D[i, i]);
            return R;
        }

        /// <summary>Solve A·X = B column-by-column using LU decomposition.</summary>
        private static Mat<Complex> Solve(Mat<Complex> A, Mat<Complex> B)
        {
            int n = A.RowCount;
            int m = B.ColCount;
            var lu = A.Lu();
            var X  = new Mat<Complex>(n, m);
            for (int j = 0; j < m; j++)
            {
                var rhs = B.Cols[j].Copy();
                var x   = lu.Solve(rhs);
                for (int i = 0; i < n; i++) X[i, j] = x[i];
            }
            return X;
        }

        private static Mat<Complex> Inverse(Mat<Complex> A)
            => Solve(A, Identity(A.RowCount));

        // ============================================================
        //  Public scalar utilities
        // ============================================================

        /// <summary>
        /// Principal-branch complex square root: Re(√z) ≥ 0.
        /// For real positive z this matches Math.Sqrt exactly.
        /// </summary>
        public static Complex ComplexSqrt(Complex z)
        {
            double r   = z.Magnitude;
            double ang = z.Phase;
            return Complex.FromPolarCoordinates(Math.Sqrt(r), ang / 2.0);
        }

        /// <summary>Uniform Z₀ scalar → per-port array of length <paramref name="ports"/>.</summary>
        public static Complex[] Z0Array(Complex z0, int ports) =>
            Enumerable.Repeat(z0, ports).ToArray();

        // ============================================================
        //  Parameter-type conversions  —  single-matrix overloads
        // ============================================================

        // ----------------------------------------------------------
        //  S → Z
        //  Z = √z · (I + S) · (I − S)⁻¹ · √z
        //  Valid for any N, complex per-port Z₀.
        // ----------------------------------------------------------
        public static Mat<Complex> SToZ(Mat<Complex> S, Complex[] z0)
        {
            int n   = S.RowCount;
            var I   = Identity(n);
            var sqZ = SqrtZ0Matrix(z0);
            var tmp = Solve(I - S, I + S);   // (I−S)⁻¹·(I+S)
            return sqZ * tmp * sqZ;
        }

        // ----------------------------------------------------------
        //  S → Z
        //  Z = √z · (I + S) · (I − S)⁻¹ · √z
        //  Valid for any N, uniform port Z₀.
        // ----------------------------------------------------------
        public static Mat<Complex> SToZ(Mat<Complex> S,Complex z0)
        {
            int n   = S.RowCount;
            var I   = Identity(n);
            var sqZ = SqrtZ0Matrix(z0, n);
            var tmp = Solve(I - S, I + S);   // (I−S)⁻¹·(I+S)
            return sqZ * tmp * sqZ;
        }

        // ----------------------------------------------------------
        //  Z → S
        //  Ẑ = √y · Z · √y   (normalize)
        //  S = (Ẑ − I)(Ẑ + I)⁻¹
        // ----------------------------------------------------------
        public static Mat<Complex> ZToS(Mat<Complex> Z, Complex[] z0)
        {
            int n    = Z.RowCount;
            var I    = Identity(n);
            var sqY  = SqrtY0Matrix(z0);
            var Zhat = sqY * Z * sqY;
            return Solve(Zhat + I, Zhat - I);   // (Ẑ+I)⁻¹·(Ẑ−I)
        }
        public static Mat<Complex> ZToS(Mat<Complex> Z, Complex z0)
        {
            int n = Z.RowCount;
            var I    = Identity(n);
            var sqY  = SqrtY0Matrix(z0, n);
            var Zhat = sqY * Z * sqY;
            return Solve(Zhat + I, Zhat - I);   // (Ẑ+I)⁻¹·(Ẑ−I)
        }

        // ----------------------------------------------------------
        //  S → Y
        //  Y = √y · (I − S) · (I + S)⁻¹ · √y
        // ----------------------------------------------------------
        public static Mat<Complex> SToY(Mat<Complex> S, Complex[] z0)
        {
            int n   = S.RowCount;
            var I   = Identity(n);
            var sqY = SqrtY0Matrix(z0);
            var tmp = Solve(I + S, I - S);   // (I+S)⁻¹·(I−S)
            return sqY * tmp * sqY;
        }

        public static Mat<Complex> SToY(Mat<Complex> S, Complex z0)
        {
            int n   = S.RowCount;
            var I   = Identity(n);
            var sqY = SqrtY0Matrix(z0, n);
            var tmp = Solve(I + S, I - S);   // (I+S)⁻¹·(I−S)
            return sqY * tmp * sqY;
        }

        // ----------------------------------------------------------
        //  Y → S
        //  Ŷ = √z · Y · √z
        //  S = (I − Ŷ)(I + Ŷ)⁻¹
        // ----------------------------------------------------------
        public static Mat<Complex> YToS(Mat<Complex> Y, Complex[] z0)
        {
            int n    = Y.RowCount;
            var I    = Identity(n);
            var sqZ  = SqrtZ0Matrix(z0);
            var Yhat = sqZ * Y * sqZ;
            return Solve(I + Yhat, I - Yhat);   // (I+Ŷ)⁻¹·(I−Ŷ)
        }

        public static Mat<Complex> YToS(Mat<Complex> Y, Complex z0)
        {
            int n    = Y.RowCount;
            var I    = Identity(n);
            var sqZ  = SqrtZ0Matrix(z0, n);
            var Yhat = sqZ * Y * sqZ;
            return Solve(I + Yhat, I - Yhat);   // (I+Ŷ)⁻¹·(I−Ŷ)
        }

        // ----------------------------------------------------------
        //  Z ↔ Y  (simple inversion — reference-independent)
        // ----------------------------------------------------------
        public static Mat<Complex> ZToY(Mat<Complex> Z) => Inverse(Z);
        public static Mat<Complex> YToZ(Mat<Complex> Y) => Inverse(Y);

        // ============================================================
        //  S → S  (fully general direct renormalization)
        //
        //  Supports per-port complex Z0_old AND per-port complex Z0_new.
        //  Does NOT pass through Z-parameters.
        //
        //  Derivation (Kurokawa power-wave definition):
        //  -------------------------------------------------
        //  Power waves at port i with reference Z0ᵢ:
        //    aᵢ = (Vᵢ + Z0ᵢ · Iᵢ)  / (2·√Re(Z0ᵢ))
        //    bᵢ = (Vᵢ − Z0ᵢ*· Iᵢ) / (2·√Re(Z0ᵢ))
        //
        //  Solving for V and I from the old basis then substituting into
        //  the new basis yields, across all ports in matrix form:
        //    a' = P·a + Q·b,   b' = R·a + T·b
        //    where  R = Q*,  T = P*  (diagonal)
        //
        //  Diagonal entries per port i:
        //    P_ii = (Z0_old_i* + Z0_new_i) / (2·√Re(Z0_new_i)·√Re(Z0_old_i))
        //    Q_ii = (Z0_old_i  − Z0_new_i) / (2·√Re(Z0_new_i)·√Re(Z0_old_i))
        //
        //  Since b = S_old·a:
        //    S_new = (R + T·S_old) · (P + Q·S_old)⁻¹
        // ============================================================

        /// <summary>
        /// Renormalize S-parameters from per-port complex Z0_old to per-port
        /// complex Z0_new using the fully general power-wave bilinear formula.
        /// Does NOT pass through Z-parameters.
        /// </summary>
        /// <param name="S">S-parameter matrix (N×N).</param>
        /// <param name="z0Old">Per-port old reference impedances; length must equal N.</param>
        /// <param name="z0New">Per-port new reference impedances; length must equal N.</param>
        public static Mat<Complex> SToS(Mat<Complex> S, Complex[] z0Old, Complex[] z0New)
        {
            int n = S.RowCount;
            if (z0Old.Length != n)
                throw new ArgumentException(
                    $"z0Old length ({z0Old.Length}) must equal matrix size ({n}).");
            if (z0New.Length != n)
                throw new ArgumentException(
                    $"z0New length ({z0New.Length}) must equal matrix size ({n}).");

            var P = new Mat<Complex>(n, n);
            var Q = new Mat<Complex>(n, n);

            for (int i = 0; i < n; i++)
            {
                double sqrtReOld = Math.Sqrt(z0Old[i].Real);
                double sqrtReNew = Math.Sqrt(z0New[i].Real);
                double scale2    = 2.0 * sqrtReNew * sqrtReOld;

                P[i, i] = (Complex.Conjugate(z0Old[i]) + z0New[i]) / scale2;
                Q[i, i] = (z0Old[i]                    - z0New[i]) / scale2;
            }

            var R   = ConjTransposeDiag(Q);
            var T   = ConjTransposeDiag(P);
            var lhs = R + T * S;
            var rhs = P + Q * S;

            // lhs · rhs⁻¹  =  Transpose( Solve(Transpose(rhs), Transpose(lhs)) )
            return Transpose(Solve(Transpose(rhs), Transpose(lhs)));
        }

        /// <summary>
        /// Overload: uniform scalar z0Old (same on all ports), per-port z0New.
        /// </summary>
        public static Mat<Complex> SToS(Mat<Complex> S, Complex z0Old, Complex[] z0New)
        {
            var z0OldArray = Z0Array(z0Old, S.RowCount);
            return SToS(S, z0OldArray, z0New);
        }

        /// <summary>
        /// Overload: uniform scalar z0Old and uniform scalar z0New.
        /// </summary>
        public static Mat<Complex> SToS(Mat<Complex> S, Complex z0Old, Complex z0New)
        {
            int n = S.RowCount;
            return SToS(S, Z0Array(z0Old, n), Z0Array(z0New, n));
        }

        // ============================================================
        //  General single-matrix conversion dispatcher
        // ============================================================

        /// <summary>
        /// Convert a single network parameter matrix from one type/reference to
        /// another.  Handles all nine (type, type) combinations.
        /// </summary>
        /// <param name="mat">Input matrix.</param>
        /// <param name="fromType">Parameter type of <paramref name="mat"/>.</param>
        /// <param name="z0Old">Reference impedances used by <paramref name="mat"/>.</param>
        /// <param name="toType">Desired output parameter type.</param>
        /// <param name="z0New">Reference impedances for the output.</param>
        public static Mat<Complex> Convert(Mat<Complex> mat,
                                           MatrixType fromType, Complex[] z0Old,
                                           MatrixType toType,   Complex[] z0New)
        {
            if (fromType == MatrixType.S && toType == MatrixType.S)
                return SToS(mat, z0Old, z0New);
            if (fromType == MatrixType.S && toType == MatrixType.Z)
                return SToZ(mat, z0Old);
            if (fromType == MatrixType.S && toType == MatrixType.Y)
                return SToY(mat, z0Old);

            if (fromType == MatrixType.Z && toType == MatrixType.S)
                return ZToS(mat, z0New);
            if (fromType == MatrixType.Z && toType == MatrixType.Z)
                return SToZ(ZToS(mat, z0New), z0New);
            if (fromType == MatrixType.Z && toType == MatrixType.Y)
                return SToY(ZToS(mat, z0New), z0New);

            if (fromType == MatrixType.Y && toType == MatrixType.S)
                return YToS(mat, z0New);
            if (fromType == MatrixType.Y && toType == MatrixType.Z)
                return SToY(YToS(mat, z0New), z0New);
            // Y → Y
            return SToY(YToS(mat, z0New), z0New);
        }

        /// <summary>
        /// Convert a single network parameter matrix from one type/reference to
        /// another.  Handles all nine (type, type) combinations.
        /// </summary>
        public static Mat<Complex> Convert(Mat<Complex> mat,
                                           MatrixType fromType, Complex z0Old,
                                           MatrixType toType,   Complex z0New)
        {
            if (fromType == MatrixType.S && toType == MatrixType.S)
                return SToS(mat, z0Old, z0New);
            if (fromType == MatrixType.S && toType == MatrixType.Z)
                return SToZ(mat, z0Old);
            if (fromType == MatrixType.S && toType == MatrixType.Y)
                return SToY(mat, z0Old);

            if (fromType == MatrixType.Z && toType == MatrixType.S)
                return ZToS(mat, z0New);
            if (fromType == MatrixType.Z && toType == MatrixType.Z)
                return SToZ(ZToS(mat, z0New), z0New);
            if (fromType == MatrixType.Z && toType == MatrixType.Y)
                return SToY(ZToS(mat, z0New), z0New);

            if (fromType == MatrixType.Y && toType == MatrixType.S)
                return YToS(mat, z0New);
            if (fromType == MatrixType.Y && toType == MatrixType.Z)
                return SToY(YToS(mat, z0New), z0New);
            // Y → Y
            return SToY(YToS(mat, z0New), z0New);
        }


        // ============================================================
        //  SNP sweep overloads  (parallelized over frequency)
        // ============================================================

        public static SNP SToZ(SNP s)
        {
            var mats = new Mat<Complex>[s.FrequencyCount];
            Parallel.For(0, s.FrequencyCount, i => mats[i] = SToZ(s.Matrices[i], s.Z0));
            var result = new SNP(s.Frequencies, mats, MatrixType.Z, s.Format, s.Z0);
            result.CopyMetadataFrom(s);
            return result;
        }

        public static SNP ZToS(SNP z)
        {
            var mats = new Mat<Complex>[z.FrequencyCount];
            Parallel.For(0, z.FrequencyCount, i => mats[i] = ZToS(z.Matrices[i], z.Z0));
            var result = new SNP(z.Frequencies, mats, MatrixType.S, z.Format, z.Z0);
            result.CopyMetadataFrom(z);
            return result;
        }

        public static SNP SToY(SNP s)
        {
            var mats = new Mat<Complex>[s.FrequencyCount];
            Parallel.For(0, s.FrequencyCount, i => mats[i] = SToY(s.Matrices[i], s.Z0));
            var result = new SNP(s.Frequencies, mats, MatrixType.Y, s.Format, s.Z0);
            result.CopyMetadataFrom(s);
            return result;
        }

        public static SNP YToS(SNP y)
        {
            var mats = new Mat<Complex>[y.FrequencyCount];
            Parallel.For(0, y.FrequencyCount, i => mats[i] = YToS(y.Matrices[i], y.Z0));
            var result = new SNP(y.Frequencies, mats, MatrixType.S, y.Format, y.Z0);
            result.CopyMetadataFrom(y);
            return result;
        }

        public static SNP ZToY(SNP z)
        {
            var mats = new Mat<Complex>[z.FrequencyCount];
            Parallel.For(0, z.FrequencyCount, i => mats[i] = ZToY(z.Matrices[i]));
            var result = new SNP(z.Frequencies, mats, MatrixType.Y, z.Format, z.Z0);
            result.CopyMetadataFrom(z);
            return result;
        }

        public static SNP YToZ(SNP y)
        {
            var mats = new Mat<Complex>[y.FrequencyCount];
            Parallel.For(0, y.FrequencyCount, i => mats[i] = YToZ(y.Matrices[i]));
            var result = new SNP(y.Frequencies, mats, MatrixType.Z, y.Format, y.Z0);
            result.CopyMetadataFrom(y);
            return result;
        }

        /// <summary>
        /// Renormalize an entire S-parameter SNP sweep from its current Z0 to
        /// <paramref name="z0New"/>.  Does NOT pass through Z-parameters.
        /// </summary>
        public static SNP SToS(SNP s, Complex z0New)
        {
            if (s.Type != MatrixType.S)
                throw new InvalidOperationException("SToS requires an S-parameter SNP.");

            var mats = new Mat<Complex>[s.FrequencyCount];
            Parallel.For(0, s.FrequencyCount,
                i => mats[i] = SToS(s.Matrices[i], s.Z0, z0New));
            var result = new SNP(s.Frequencies, mats, MatrixType.S, s.Format, z0New);
            result.CopyMetadataFrom(s);
            return result;
        }

        // ============================================================
        //  2-port T (wave-transfer) matrix helpers
        // ============================================================

        public static Mat<Complex> SToT2Port(Mat<Complex> S)
        {
            var S11 = S[0, 0]; var S12 = S[0, 1];
            var S21 = S[1, 0]; var S22 = S[1, 1];
            var T = new Mat<Complex>(2, 2);
            T[0, 0] = -(S11 * S22 / S21) + S12;
            T[0, 1] =  S11 / S21;
            T[1, 0] = -S22 / S21;
            T[1, 1] =  Complex.One / S21;
            return T;
        }

        public static Mat<Complex> TToS2Port(Mat<Complex> T)
        {
            var T11 = T[0, 0]; var T12 = T[0, 1];
            var T21 = T[1, 0]; var T22 = T[1, 1];
            var S = new Mat<Complex>(2, 2);
            S[0, 0] =  T12 / T22;
            S[0, 1] =  T11 - T12 * T21 / T22;
            S[1, 0] =  Complex.One / T22;
            S[1, 1] = -T21 / T22;
            return S;
        }

        // ============================================================
        //  De-embedding
        // ============================================================

        /// <summary>
        /// De-embed a DUT via 2-port wave-cascade T-matrix:
        ///   S_total = fixture_in ◦ DUT ◦ fixture_out
        /// All three SNPs must be 2-port S-parameter sweeps.
        /// </summary>
        public static SNP DeEmbed2Port(SNP sTotal, SNP sFixIn, SNP sFixOut)
        {
            if (sTotal.Ports != 2 || sFixIn.Ports != 2 || sFixOut.Ports != 2)
                throw new ArgumentException("De-embedding requires 2-port matrices.");

            var mats = new Mat<Complex>[sTotal.FrequencyCount];
            Parallel.For(0, sTotal.FrequencyCount, i =>
            {
                var Tt  = SToT2Port(sTotal.Matrices[i]);
                var Tfi = SToT2Port(sFixIn.Matrices[i]);
                var Tfo = SToT2Port(sFixOut.Matrices[i]);
                // Tfi⁻¹ · Tt · Tfo⁻¹
                var Td  = Solve(Tfi, Tt * Solve(Tfo, Identity(2)));
                mats[i] = TToS2Port(Td);
            });
            return new SNP(sTotal.Frequencies, mats, MatrixType.S,
                           sTotal.Format, sTotal.Z0);
        }

        /// <summary>
        /// N-port shunt de-embedding via Y-parameter subtraction:
        ///   Y_dut = Y_measured − Y_open_fixture
        /// </summary>
        public static SNP DeEmbedShunt(SNP sMeasured, SNP sOpenFixture)
        {
            var yMeas = SToY(sMeasured);
            var yFix  = SToY(sOpenFixture);
            var mats  = new Mat<Complex>[sMeasured.FrequencyCount];
            Parallel.For(0, sMeasured.FrequencyCount,
                i => mats[i] = yMeas.Matrices[i] - yFix.Matrices[i]);
            var yDut = new SNP(sMeasured.Frequencies, mats,
                               MatrixType.Y, sMeasured.Format, sMeasured.Z0);
            return YToS(yDut);
        }

        // ============================================================
        //  2-PORT STABILITY AND GAIN  (S-parameter based)
        //
        //  All calculations use S-parameters normalized to a uniform
        //  real reference impedance.  If the SNP is not already in that
        //  form, NormalizedS2Port() converts it automatically — the
        //  caller's object is never mutated.
        //
        //  References:
        //    • Pozar, "Microwave Engineering", 4th ed., §12.1
        //    • Edwards & Sinsky, IEEE Trans MTT v40 n12, Dec 1992  (μ factor)
        // ============================================================

        /// <summary>
        /// Return a 2-port S-parameter SNP normalized to a UNIFORM REAL reference impedance
        /// (the real part of s.Z0), converting from Z or Y if necessary. Never mutates the input.
        /// Every 2-port stability entry point goes through here, so the uniform-real precondition
        /// the per-matrix overloads document is established in exactly one place.
        /// </summary>
        private static SNP NormalizedS2Port(SNP snp)
        {
            if (snp.Ports != 2)
                throw new ArgumentException(
                    "This calculation requires a 2-port network.");

            SNP s = snp.Type switch
            {
                MatrixType.Z => ZToS(snp),
                MatrixType.Y => YToS(snp),
                _            => snp
            };

            // Every stability formula below (μ, μ′, K/|Δ|, MAG/MSG, the stability circles) is only
            // valid against a UNIFORM REAL reference impedance, so normalise to one here — this is
            // the single place that guarantee is established for the SNP path.
            //
            // This previously took a `bool forceZ0Real` whose body was `if (forceZ0Real) { if
            // (forceZ0Real) ... else ... }` — the inner `else` was unreachable, so passing `false`
            // renormalised NOTHING rather than "renormalise to the complex reference". μ, μ′ and both
            // circle functions all passed `false` while StabilityK and MaxGain used the `true`
            // default, leaving the shared math internally inconsistent: on a complex-Z0 network the
            // first group computed against a complex-referenced matrix (physically wrong) and the
            // second did not. Invisible in practice only because real Touchstone files are
            // essentially always purely real, where this renorm is an exact identity.
            //
            // Skipping the identity case is deliberate, not an optimisation: it keeps results
            // bit-for-bit identical for those real-Z0 files on BOTH former groups, so this repair
            // changes numbers only where they were already wrong.
            if (s.Z0.Imaginary != 0.0)
                s = SToS(s, new Complex(s.Z0.Real, 0.0));

            return s;
        }

        // ----------------------------------------------------------
        //  μ  (Edwards-Sinsky)
        //  μ = (1 − |S11|²) / (|S22 − Δ·S11*| + |S12·S21|)
        //  μ > 1 ⟹ unconditionally stable
        // ----------------------------------------------------------

        /// <summary>
        /// μ stability factor over frequency for a 2-port SNP.
        /// μ > 1 implies unconditional stability at that frequency.
        /// </summary>
        public static double[] StabilityMu(SNP snp)
        {
            var s      = NormalizedS2Port(snp);
            var result = new double[s.FrequencyCount];
            for (int i = 0; i < s.FrequencyCount; i++)
                result[i] = StabilityMu(s.Matrices[i]);
            return result;
        }

        /// <summary>
        /// μ stability factor for a single 2×2 S-matrix.
        /// The matrix must already be normalized to a uniform reference impedance.
        /// </summary>
        public static double StabilityMu(Mat<Complex> s)
        {
            if (s.RowCount != 2 || s.ColCount != 2)
                throw new ArgumentException(
                    "StabilityMu requires a 2-port (2×2) matrix.");
            var delta = s[0, 0] * s[1, 1] - s[0, 1] * s[1, 0];
            double num = 1.0 - s[0, 0].Magnitude * s[0, 0].Magnitude;
            double den = (s[1, 1] - delta * Complex.Conjugate(s[0, 0])).Magnitude
                       + (s[0, 1] * s[1, 0]).Magnitude;
            return num / den;
        }

        // ----------------------------------------------------------
        //  μ′  (Edwards-Sinsky)
        //  μ′ = (1 − |S22|²) / (|S11 − Δ·S22*| + |S12·S21|)
        // ----------------------------------------------------------

        /// <summary>
        /// μ′ stability factor over frequency for a 2-port SNP.
        /// μ′ > 1 implies unconditional stability at that frequency.
        /// </summary>
        public static double[] StabilityMuPrime(SNP snp)
        {
            var s      = NormalizedS2Port(snp);
            var result = new double[s.FrequencyCount];
            for (int i = 0; i < s.FrequencyCount; i++)
                result[i] = StabilityMuPrime(s.Matrices[i]);
            return result;
        }

        /// <summary>
        /// μ′ stability factor for a single 2×2 S-matrix.
        /// The matrix must already be normalized to a uniform reference impedance.
        /// </summary>
        public static double StabilityMuPrime(Mat<Complex> s)
        {
            if (s.RowCount != 2 || s.ColCount != 2)
                throw new ArgumentException(
                    "StabilityMuPrime requires a 2-port (2×2) matrix.");
            var delta = s[0, 0] * s[1, 1] - s[0, 1] * s[1, 0];
            double num = 1.0 - s[1, 1].Magnitude * s[1, 1].Magnitude;
            double den = (s[0, 0] - delta * Complex.Conjugate(s[1, 1])).Magnitude
                       + (s[0, 1] * s[1, 0]).Magnitude;
            return num / den;
        }

        // ----------------------------------------------------------
        //  Rollett K factor + auxiliary quantities
        //  K  = (1 − |S11|² − |S22|² + |Δ|²) / (2·|S12·S21|)
        //  B1 = 1 + |S11|² − |S22|² − |Δ|²
        //  B2 = 1 + |S22|² − |S11|² − |Δ|²
        //  Unconditionally stable when K > 1 AND |Δ| < 1
        // ----------------------------------------------------------

        /// <summary>
        /// Rollett K factor, auxiliary quantities B1/B2, |Δ|, and a per-frequency
        /// stable flag for a 2-port SNP.
        /// </summary>
        /// <returns>
        /// (K[], B1[], B2[], Delta[], IsStable[]) — all indexed by frequency.
        /// </returns>
        public static (double[] K, double[] B1, double[] B2,
                        double[] Delta, bool[] IsStable) StabilityK(SNP snp)
        {
            var s  = NormalizedS2Port(snp);
            int n  = s.FrequencyCount;
            var K        = new double[n];
            var B1       = new double[n];
            var B2       = new double[n];
            var deltaArr = new double[n];
            var stable   = new bool[n];

            for (int i = 0; i < n; i++)
            {
                (K[i], B1[i], B2[i], deltaArr[i], stable[i]) =
                    StabilityK(s.Matrices[i]);
            }
            return (K, B1, B2, deltaArr, stable);
        }

        /// <summary>
        /// Rollett K factor and auxiliary quantities for a single 2×2 S-matrix.
        /// The matrix must already be normalized to a uniform reference impedance.
        /// </summary>
        public static (double K, double B1, double B2,
                        double Delta, bool IsStable) StabilityK(Mat<Complex> s)
        {
            var delta    = s[0, 0] * s[1, 1] - s[0, 1] * s[1, 0];
            double d2    = delta.Magnitude * delta.Magnitude;
            double s11sq = s[0, 0].Magnitude * s[0, 0].Magnitude;
            double s22sq = s[1, 1].Magnitude * s[1, 1].Magnitude;
            double s12s21 = (s[0, 1] * s[1, 0]).Magnitude;

            double K  = (1.0 - s11sq - s22sq + d2) / (2.0 * s12s21);
            double B1 = 1.0 + s11sq - s22sq - d2;
            double B2 = 1.0 + s22sq - s11sq - d2;
            double dm = delta.Magnitude;

            return (K, B1, B2, dm, K > 1.0 && dm < 1.0);
        }

        // ----------------------------------------------------------
        //  Maximum available gain (MAG) / maximum stable gain (MSG)
        //  K ≥ 1: MAG = |S21/S12| · (K − √(K²−1))
        //  K < 1: MSG = |S21/S12|
        // ----------------------------------------------------------

        /// <summary>
        /// MAG or MSG in dB over frequency for a 2-port SNP.
        /// </summary>
        public static double[] MaxGain(SNP snp)
        {
            var s       = NormalizedS2Port(snp);
            var (Kn, _, _, _, _) = StabilityK(s);
            int n       = s.FrequencyCount;
            var gain    = new double[n];
            for (int i = 0; i < n; i++)
                gain[i] = MaxGain(s.Matrices[i], Kn[i]);
            return gain;
        }

        /// <summary>
        /// MAG or MSG in dB for a single 2×2 S-matrix.
        /// The matrix must already be normalized to a uniform reference impedance.
        /// </summary>
        public static double MaxGain(Mat<Complex> s)
        {
            var (k, _, _, _, _) = StabilityK(s);
            return MaxGain(s, k);
        }

        private static double MaxGain(Mat<Complex> s, double k)
        {
            double ratio      = s[1, 0].Magnitude / (s[0, 1].Magnitude + 1e-300);
            double linearGain = k >= 1.0
                ? ratio * (k - Math.Sqrt(k * k - 1.0))
                : ratio;
            return 20.0 * Math.Log10(linearGain + 1e-300);
        }

        // ----------------------------------------------------------
        //  Stability circles
        //
        //  Load circle:
        //    CL = (S22 − Δ·S11*)* / (|S22|² − |Δ|²)
        //    rL = |S12·S21|  / ||S22|² − |Δ|²|
        //
        //  Source circle:
        //    CS = (S11 − Δ·S22*)* / (|S11|² − |Δ|²)
        //    rS = |S12·S21|  / ||S11|² − |Δ|²|
        // ----------------------------------------------------------

        /// <summary>
        /// Load stability circles for a 2-port SNP over frequency.
        /// The load circle is the locus of ΓL values on the Smith chart
        /// that place the input on the unit circle (|Γin| = 1).
        /// </summary>
        /// <returns>(CL[], rL[]) — center (complex) and radius, one entry per frequency.</returns>
        public static (Complex[] CL, double[] rL) StabilityCirclesLoad(SNP snp)
        {
            var s  = NormalizedS2Port(snp);
            int n  = s.FrequencyCount;
            var CL = new Complex[n];
            var rL = new double[n];

            for (int i = 0; i < n; i++)
            {
                var m   = s.Matrices[i];
                var S11 = m[0, 0]; var S12 = m[0, 1];
                var S21 = m[1, 0]; var S22 = m[1, 1];
                var delta  = S11 * S22 - S12 * S21;
                double s22sq = S22.Magnitude * S22.Magnitude;
                double d2    = delta.Magnitude * delta.Magnitude;
                double denom = s22sq - d2;

                CL[i] = Complex.Conjugate(S22 - delta * Complex.Conjugate(S11)) / denom;
                rL[i] = (S12 * S21).Magnitude / Math.Abs(denom);
            }
            return (CL, rL);
        }

        // ----------------------------------------------------------
        //  Passivity  —  σ_max(S) ≤ 1   (equivalently  I − Sᴴ S ⪰ 0)
        // ----------------------------------------------------------

        /// <summary>
        /// Passivity measure of a scattering matrix: its largest singular value σ_max(S).
        /// The network is passive at this frequency iff σ_max ≤ 1; the returned value says
        /// how far from passive it is, not merely whether — 1 is the boundary.
        ///
        /// Unlike μ, μ′, K and MAG/MSG this is <b>not</b> a 2-port formula — it is defined for
        /// any N ≥ 1 (at N = 1 it degenerates to |S₁₁|). Callers may therefore pass a whole
        /// N-port matrix, not just an extracted 2-port.
        ///
        /// <para><b>The matrix must already be normalized to a uniform REAL reference impedance</b>
        /// (as with the per-matrix stability overloads). The Sᴴ S ⪯ I test presumes power waves
        /// against a common real reference; under per-port or complex references it is not the
        /// right test. Renormalize first — see <see cref="SToS(Mat{Complex}, Complex[], Complex[])"/>.</para>
        /// </summary>
        public static double Passivity(Mat<Complex> s)
        {
            if (s.RowCount != s.ColCount)
                throw new ArgumentException("Passivity requires a square scattering matrix.");
            // σ_max is the induced 2-norm; NumFlat returns singular values in descending order.
            return s.Svd().S[0];
        }

        /// <summary>
        /// σ_max(S) over frequency for an N-port SNP — the passivity measure at each point.
        /// Values ≤ 1 are passive. Converts from Z/Y if needed and normalizes to a uniform real
        /// reference first, exactly as the stability functions do.
        /// </summary>
        public static double[] Passivity(SNP snp)
        {
            SNP s = snp.Type switch
            {
                MatrixType.Z => ZToS(snp),
                MatrixType.Y => YToS(snp),
                _            => snp
            };
            // Same uniform-real requirement as the stability path (see NormalizedS2Port), but
            // without its 2-port restriction — passivity is defined for any N.
            if (s.Z0.Imaginary != 0.0)
                s = SToS(s, new Complex(s.Z0.Real, 0.0));

            var result = new double[s.FrequencyCount];
            for (int i = 0; i < s.FrequencyCount; i++)
                result[i] = Passivity(s.Matrices[i]);
            return result;
        }

        /// <summary>
        /// Source stability circles for a 2-port SNP over frequency.
        /// The source circle is the locus of ΓS values on the Smith chart
        /// that place the output on the unit circle (|Γout| = 1).
        /// </summary>
        /// <returns>(CS[], rS[]) — center (complex) and radius, one entry per frequency.</returns>
        public static (Complex[] CS, double[] rS) StabilityCirclesSource(SNP snp)
        {
            var s  = NormalizedS2Port(snp);
            int n  = s.FrequencyCount;
            var CS = new Complex[n];
            var rS = new double[n];

            for (int i = 0; i < n; i++)
            {
                var m   = s.Matrices[i];
                var S11 = m[0, 0]; var S12 = m[0, 1];
                var S21 = m[1, 0]; var S22 = m[1, 1];
                var delta  = S11 * S22 - S12 * S21;
                double s11sq = S11.Magnitude * S11.Magnitude;
                double d2    = delta.Magnitude * delta.Magnitude;
                double denom = s11sq - d2;

                CS[i] = Complex.Conjugate(S11 - delta * Complex.Conjugate(S22)) / denom;
                rS[i] = (S12 * S21).Magnitude / Math.Abs(denom);
            }
            return (CS, rS);
        }

        /// <summary>
        /// Returns true for each frequency where the stable region is inside the load stability circle.
        /// Test: check whether Γ=0 (50 Ω) is in the stable region AND whether it falls inside
        /// the circle. If both agree, the stable region is inside; otherwise it is outside.
        /// </summary>
        public static bool[] StableRegionInsideLoad(SNP snp)
        {
            var sNorm = NormalizedS2Port(snp);
            var (CL, rL) = StabilityCirclesLoad(snp);
            int n = sNorm.FrequencyCount;
            var flags = new bool[n];
            for (int i = 0; i < n; i++)
            {
                bool originStable       = sNorm.Matrices[i][0, 0].Magnitude < 1.0; // |S11| < 1
                bool originInsideCircle = CL[i].Magnitude < rL[i];
                flags[i] = originStable == originInsideCircle;
            }
            return flags;
        }

        /// <summary>
        /// Returns true for each frequency where the stable region is inside the source stability circle.
        /// </summary>
        public static bool[] StableRegionInsideSource(SNP snp)
        {
            var sNorm = NormalizedS2Port(snp);
            var (CS, rS) = StabilityCirclesSource(snp);
            int n = sNorm.FrequencyCount;
            var flags = new bool[n];
            for (int i = 0; i < n; i++)
            {
                bool originStable       = sNorm.Matrices[i][1, 1].Magnitude < 1.0; // |S22| < 1
                bool originInsideCircle = CS[i].Magnitude < rS[i];
                flags[i] = originStable == originInsideCircle;
            }
            return flags;
        }

        // ============================================================
        //  Complex formatting helper  (used by SNP.PrintElement and
        //  TouchstoneIO writer — internal access only)
        // ============================================================

        /// <summary>
        /// Decompose a Complex value into a (a, b) pair according to the
        /// requested display format:
        ///   RI → (Real, Imag)
        ///   MA → (Magnitude, Phase °)
        ///   DB → (20·log₁₀|z|, Phase °)
        /// </summary>
        internal static (double a, double b) FormatComplex(Complex c, MatrixFormat fmt) =>
            fmt switch
            {
                MatrixFormat.RI => (c.Real, c.Imaginary),
                MatrixFormat.MA => (c.Magnitude, c.Phase * 180.0 / Math.PI),
                MatrixFormat.DB => (20.0 * Math.Log10(c.Magnitude + 1e-300),
                                    c.Phase * 180.0 / Math.PI),
                _               => (c.Real, c.Imaginary)
            };

        // ============================================================
        //  Comparative test utilities
        // ============================================================

        /// <summary>
        /// Compare two SNP objects element-wise over frequency, computing the
        /// RMS error across all real and imaginary matrix components.
        /// Prints to Console: total RMS, max/min component error, and the
        /// frequency index with the highest single-component error.
        /// If the objects differ in size a diagnostic message is printed instead.
        /// </summary>
        public static void CompareRMS(SNP a, SNP b)
        {
            bool mismatch = false;
            if (a.FrequencyCount != b.FrequencyCount)
            {
                Console.WriteLine(
                    $"[CompareRMS] Frequency count mismatch: " +
                    $"a={a.FrequencyCount}, b={b.FrequencyCount}.");
                mismatch = true;
            }
            if (a.Ports != b.Ports)
            {
                Console.WriteLine(
                    $"[CompareRMS] Matrix size mismatch: " +
                    $"a={a.Ports}×{a.Ports}, b={b.Ports}×{b.Ports}.");
                mismatch = true;
            }
            if (mismatch) return;

            int nFreq  = a.FrequencyCount;
            int nPorts = a.Ports;

            double sumSq          = 0.0;
            long   totalCount     = 0;
            double maxErr         = double.MinValue;
            double minErr         = double.MaxValue;
            int    maxErrFreqIdx  = 0;
            double bestFreqMaxErr = double.MinValue;

            for (int fi = 0; fi < nFreq; fi++)
            {
                var mA = a.Matrices[fi];
                var mB = b.Matrices[fi];
                double freqMaxErr = double.MinValue;

                for (int r = 0; r < nPorts; r++)
                for (int c = 0; c < nPorts; c++)
                {
                    double errReal = mA[r, c].Real      - mB[r, c].Real;
                    double errImag = mA[r, c].Imaginary - mB[r, c].Imaginary;

                    sumSq      += errReal * errReal + errImag * errImag;
                    totalCount += 2;

                    double absReal = Math.Abs(errReal);
                    double absImag = Math.Abs(errImag);

                    if (absReal > maxErr) maxErr = absReal;
                    if (absImag > maxErr) maxErr = absImag;
                    if (absReal < minErr) minErr = absReal;
                    if (absImag < minErr) minErr = absImag;

                    double localMax = Math.Max(absReal, absImag);
                    if (localMax > freqMaxErr) freqMaxErr = localMax;
                }

                if (freqMaxErr > bestFreqMaxErr)
                {
                    bestFreqMaxErr = freqMaxErr;
                    maxErrFreqIdx  = fi;
                }
            }

            double rms         = Math.Sqrt(sumSq / totalCount);
            double maxErrFreqHz = a.Frequencies[maxErrFreqIdx];

            Console.WriteLine($"[CompareRMS] {nPorts}-port, {nFreq} frequency points:");
            Console.WriteLine($"  Total RMS error (real & imag)  : {rms:G6}");
            Console.WriteLine($"  Max component error            : {maxErr:G6}");
            Console.WriteLine($"  Min component error            : {minErr:G6}");
            Console.WriteLine(
                $"  Highest-error frequency        : index {maxErrFreqIdx},  " +
                $"{maxErrFreqHz:G6} Hz  ({maxErrFreqHz / 1e9:G6} GHz)");
        }

        /// <summary>
        /// Returns the RMS error between two SNPs without printing.
        /// Returns <see cref="double.MaxValue"/> if the SNPs differ in shape.
        /// Use this overload in automated tests that assert on tolerance.
        /// </summary>
        public static double CompareRMSValue(SNP a, SNP b)
        {
            if (a.FrequencyCount != b.FrequencyCount || a.Ports != b.Ports)
                return double.MaxValue;

            int nFreq  = a.FrequencyCount;
            int nPorts = a.Ports;
            double sumSq = 0.0;
            long   count = 0;

            for (int fi = 0; fi < nFreq; fi++)
            {
                var mA = a.Matrices[fi];
                var mB = b.Matrices[fi];
                for (int r = 0; r < nPorts; r++)
                for (int c = 0; c < nPorts; c++)
                {
                    double errReal = mA[r, c].Real      - mB[r, c].Real;
                    double errImag = mA[r, c].Imaginary - mB[r, c].Imaginary;
                    sumSq += errReal * errReal + errImag * errImag;
                    count += 2;
                }
            }
            return Math.Sqrt(sumSq / count);
        }
    }
}
