// ================================================================
//  RFNetwork.Interpolation.cs  —  Frequency interpolation of SNPs
//
//  Partial class continuation of RFNetwork.
//
//  Method: natural cubic spline (default) or piecewise linear.
//  Format: real/imag (default) or magnitude/phase (with phase unwrap).
//  Out-of-range: warn + clamp (default) or warn + linear extrapolation.
//
//  Phase-unwrap is mandatory for MagPhase interpolation — without it
//  the spline diverges at every ±π wrap.
//
//  Hero 1 critical path: S-parameter / real-imag is the fully-tested
//  default.  Z/Y and MagPhase options are wired but lightly tested in v1.
//
//  Reference: Burden & Faires "Numerical Analysis" 9e, Algorithm 3.4
//             (natural cubic spline)
// ================================================================

using System;
using System.Numerics;
using NumFlat;

namespace RfCore
{
    // ============================================================
    //  Enums for the Interpolate API
    // ============================================================

    public enum InterpolationMethod { CubicSpline, Linear }
    public enum InterpolationFormat { RealImag, MagPhase }
    public enum OutOfRangePolicy    { WarnClamp, WarnExtrapolate }

    // ============================================================
    //  RFNetwork partial — interpolation methods
    // ============================================================

    public static partial class RFNetwork
    {
        /// <summary>
        /// Interpolate <paramref name="source"/> to a new set of frequencies.
        /// </summary>
        /// <param name="source">The stored network parameter sweep.</param>
        /// <param name="targetFrequencies">Target frequencies in Hz.</param>
        /// <param name="method">Cubic spline (default) or piecewise linear.</param>
        /// <param name="format">Interpolate real/imag (default) or mag/phase components.</param>
        /// <param name="interpolateIn">Parameter domain to interpolate in.
        ///   If different from <paramref name="source"/>.Type, the SNP is converted
        ///   before interpolation and the result carries that type.</param>
        /// <param name="outOfRange">What to do when a target frequency lies outside
        ///   the stored range — clamp (default) or linear extrapolate.</param>
        public static SNP Interpolate(
            SNP                source,
            double[]           targetFrequencies,
            InterpolationMethod method      = InterpolationMethod.CubicSpline,
            InterpolationFormat format      = InterpolationFormat.RealImag,
            MatrixType         interpolateIn = MatrixType.S,
            OutOfRangePolicy   outOfRange   = OutOfRangePolicy.WarnClamp)
        {
            if (source.IsEmpty)
                throw new ArgumentException("Cannot interpolate an empty SNP.");
            if (targetFrequencies.Length == 0)
                throw new ArgumentException("targetFrequencies must not be empty.");

            // 1. Convert source to the requested parameter domain
            SNP src = ToType(source, interpolateIn);

            // 2. Check for out-of-range target frequencies and warn once
            double fMin = src.Frequencies[0];
            double fMax = src.Frequencies[src.FrequencyCount - 1];
            bool hasBelow = false, hasAbove = false;
            foreach (double f in targetFrequencies)
            {
                if (f < fMin) hasBelow = true;
                if (f > fMax) hasAbove = true;
            }
            if (hasBelow || hasAbove)
            {
                string side = (hasBelow && hasAbove) ? "both sides of"
                            : hasBelow ? "below" : "above";
                if (outOfRange == OutOfRangePolicy.WarnExtrapolate)
                    Warn($"Interpolation target(s) extend {side} the stored range " +
                         $"[{fMin/1e9:G4}–{fMax/1e9:G4} GHz]. " +
                         "Linear extrapolation will be used — extrapolated S-parameters " +
                         "are routinely non-physical.");
                else
                    Warn($"Interpolation target(s) extend {side} the stored range " +
                         $"[{fMin/1e9:G4}–{fMax/1e9:G4} GHz]. " +
                         "Out-of-range values will be clamped to the nearest endpoint.");
            }

            // 3. Build splines for every (row, col) × component
            int nPorts    = src.Ports;
            int nFreqSrc  = src.FrequencyCount;
            int nFreqTgt  = targetFrequencies.Length;
            double[] xs   = src.Frequencies;
            bool forceLinear = method == InterpolationMethod.Linear || nFreqSrc < 3;

            var resultMats = new Mat<Complex>[nFreqTgt];
            for (int t = 0; t < nFreqTgt; t++)
                resultMats[t] = new Mat<Complex>(nPorts, nPorts);

            for (int r = 0; r < nPorts; r++)
            for (int c = 0; c < nPorts; c++)
            {
                // Extract component arrays
                var comp1 = new double[nFreqSrc];
                var comp2 = new double[nFreqSrc];

                if (format == InterpolationFormat.RealImag)
                {
                    for (int fi = 0; fi < nFreqSrc; fi++)
                    {
                        comp1[fi] = src.Matrices[fi][r, c].Real;
                        comp2[fi] = src.Matrices[fi][r, c].Imaginary;
                    }
                }
                else // MagPhase
                {
                    for (int fi = 0; fi < nFreqSrc; fi++)
                    {
                        comp1[fi] = src.Matrices[fi][r, c].Magnitude;
                        comp2[fi] = src.Matrices[fi][r, c].Phase;
                    }
                    PhaseUnwrap(comp2);
                }

                var spline1 = new Spline1D(xs, comp1, forceLinear);
                var spline2 = new Spline1D(xs, comp2, forceLinear);

                for (int ti = 0; ti < nFreqTgt; ti++)
                {
                    double f  = targetFrequencies[ti];
                    bool outR = f < fMin || f > fMax;
                    double v1 = (outR && outOfRange == OutOfRangePolicy.WarnExtrapolate)
                        ? spline1.EvalExtrap(f) : spline1.Eval(f);
                    double v2 = (outR && outOfRange == OutOfRangePolicy.WarnExtrapolate)
                        ? spline2.EvalExtrap(f) : spline2.Eval(f);

                    resultMats[ti][r, c] = format == InterpolationFormat.RealImag
                        ? new Complex(v1, v2)
                        : Complex.FromPolarCoordinates(v1, v2);
                }
            }

            return new SNP(targetFrequencies, resultMats, interpolateIn, src.Format, src.Z0);
        }

        // ---- Domain conversion helper --------------------------

        private static SNP ToType(SNP src, MatrixType target) =>
            (src.Type, target) switch
            {
                var (s, t) when s == t          => src,
                (MatrixType.S, MatrixType.Z)    => SToZ(src),
                (MatrixType.S, MatrixType.Y)    => SToY(src),
                (MatrixType.Z, MatrixType.S)    => ZToS(src),
                (MatrixType.Z, MatrixType.Y)    => ZToY(src),
                (MatrixType.Y, MatrixType.S)    => YToS(src),
                (MatrixType.Y, MatrixType.Z)    => YToZ(src),
                _                               => src
            };

        // ---- Phase unwrap --------------------------------------

        private static void PhaseUnwrap(double[] phase)
        {
            for (int i = 1; i < phase.Length; i++)
            {
                double diff = phase[i] - phase[i - 1];
                // Shift by nearest multiple of 2π to minimise |diff|
                if (diff > Math.PI)
                    phase[i] -= 2.0 * Math.PI * Math.Round(diff / (2.0 * Math.PI));
                else if (diff < -Math.PI)
                    phase[i] -= 2.0 * Math.PI * Math.Round(diff / (2.0 * Math.PI));
            }
        }

        // ============================================================
        //  Natural cubic spline  (internal)
        //  S_i(x) = a_i + b_i*(x-x_i) + c_i*(x-x_i)^2 + d_i*(x-x_i)^3
        //  for x in [x_i, x_{i+1}]
        //  Boundary condition: second derivative = 0 at both endpoints.
        // ============================================================

        private readonly struct Spline1D
        {
            private readonly double[] _x;
            private readonly double[] _a; // y values at knots
            private readonly double[] _b, _c, _d;

            internal Spline1D(double[] x, double[] y, bool forceLinear)
            {
                _x = x;
                _a = y;
                int n   = x.Length;
                int nm1 = Math.Max(n - 1, 0);

                _b = new double[nm1];
                _c = new double[nm1];
                _d = new double[nm1];

                if (n <= 1) return;

                if (n == 2 || forceLinear)
                {
                    for (int i = 0; i < nm1; i++)
                        _b[i] = (y[i + 1] - y[i]) / (x[i + 1] - x[i]);
                    return;
                }

                // Burden & Faires Algorithm 3.4 — natural cubic spline
                double[] h = new double[nm1];
                for (int i = 0; i < nm1; i++) h[i] = x[i + 1] - x[i];

                double[] alpha = new double[nm1];
                for (int i = 1; i < nm1; i++)
                    alpha[i] = 3.0 / h[i] * (y[i + 1] - y[i])
                             - 3.0 / h[i - 1] * (y[i] - y[i - 1]);

                double[] l  = new double[n];
                double[] mu = new double[nm1];
                double[] z  = new double[n];
                l[0] = 1.0;

                for (int i = 1; i < nm1; i++)
                {
                    l[i]  = 2.0 * (x[i + 1] - x[i - 1]) - h[i - 1] * mu[i - 1];
                    mu[i] = h[i] / l[i];
                    z[i]  = (alpha[i] - h[i - 1] * z[i - 1]) / l[i];
                }
                l[nm1] = 1.0;

                var cc = new double[n]; // second derivatives
                for (int j = nm1 - 1; j >= 0; j--)
                {
                    cc[j]  = z[j] - mu[j] * cc[j + 1];
                    _b[j]  = (y[j + 1] - y[j]) / h[j] - h[j] * (cc[j + 1] + 2.0 * cc[j]) / 3.0;
                    _c[j]  = cc[j];
                    _d[j]  = (cc[j + 1] - cc[j]) / (3.0 * h[j]);
                }
            }

            /// <summary>Evaluate the spline, clamping out-of-range to nearest endpoint value.</summary>
            internal double Eval(double xq)
            {
                int n = _x.Length;
                if (n == 0) return double.NaN;
                if (n == 1) return _a[0];

                // Clamp: out-of-range returns the stored endpoint value (not the spline extrapolation)
                if (xq <= _x[0])    return _a[0];
                if (xq >= _x[n-1])  return _a[n-1];

                int i = FindInterval(xq, _x);
                double dx = xq - _x[i];
                return _a[i] + _b[i] * dx + _c[i] * dx * dx + _d[i] * dx * dx * dx;
            }

            /// <summary>Evaluate with linear extrapolation beyond the stored range.</summary>
            internal double EvalExtrap(double xq)
            {
                int n = _x.Length;
                if (n <= 1) return n == 0 ? double.NaN : _a[0];

                if (xq <= _x[0])
                {
                    // Linear extrapolation: slope = b[0] (derivative at x[0])
                    return _a[0] + _b[0] * (xq - _x[0]);
                }
                if (xq >= _x[n - 1])
                {
                    // Slope at right endpoint of last interval
                    int i  = n - 2;
                    double h = _x[n - 1] - _x[i];
                    double slopeRight = _b[i] + 2.0 * _c[i] * h + 3.0 * _d[i] * h * h;
                    return _a[n - 1] + slopeRight * (xq - _x[n - 1]);
                }
                return Eval(xq);
            }

            private static int FindInterval(double xq, double[] x)
            {
                int n  = x.Length;
                int lo = 0, hi = n - 2;
                if (xq <= x[0])  return 0;
                if (xq >= x[n-1]) return hi;
                while (lo < hi)
                {
                    int mid = (lo + hi + 1) / 2;
                    if (x[mid] <= xq) lo = mid; else hi = mid - 1;
                }
                return lo;
            }
        }
    }
}
