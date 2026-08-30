// ================================================================
//  SnpInterpolator.cs  —  fit an SNP's splines ONCE, evaluate per ω
//
//  Splits RFNetwork.Interpolate into its two halves. Nothing in the
//  FIT depends on the target frequency: converting the source to the
//  requested domain, extracting the 2·N² component arrays, unwrapping
//  phase and solving the spline systems all depend only on
//  (source, method, format, interpolateIn). Only the Eval/EvalExtrap
//  calls and the out-of-range decision depend on the target.
//
//  Before this split, SnpModel.Stamp called Interpolate once per
//  frequency point, so a 2001-point 2-port file re-solved eight spline
//  systems at every point of the sweep and produced the same
//  coefficients every time (SP-P1: 91.5 ms for 401 one-target calls
//  against 0.26 ms for one 401-target call on the same file).
//
//  The fit lives in the immutable, shareable SnpFit; SnpInterpolator is
//  the thin per-consumer wrapper that carries the out-of-range policy
//  and the warn-once flag. That is what lets TouchstoneCache hand the
//  same fit to every SnpModel reading one file while each run still
//  warns for itself.
// ================================================================

using System;
using System.Numerics;
using NumFlat;

namespace RfCore
{
    /// <summary>
    /// The frequency-independent half of <see cref="RFNetwork.Interpolate"/>: the domain-converted
    /// source plus one fitted spline pair per (row, column). Immutable once built, so a single
    /// instance is safe to share across models and threads.
    /// </summary>
    internal sealed class SnpFit
    {
        internal readonly SNP                 Source;       // after ToType
        internal readonly MatrixType          InterpolateIn;
        internal readonly InterpolationFormat Format;
        internal readonly int                 Ports;
        internal readonly double              FMin;
        internal readonly double              FMax;

        // [r * Ports + c] — component 1 (Real or Magnitude) and component 2 (Imag or Phase).
        private readonly RFNetwork.Spline1D[] _s1;
        private readonly RFNetwork.Spline1D[] _s2;

        internal SnpFit(SNP source,
                        InterpolationMethod method,
                        InterpolationFormat format,
                        MatrixType          interpolateIn)
        {
            if (source.IsEmpty)
                throw new ArgumentException("Cannot interpolate an empty SNP.");

            // 1. Convert source to the requested parameter domain
            SNP src = RFNetwork.ToType(source, interpolateIn);

            Source        = src;
            InterpolateIn = interpolateIn;
            Format        = format;
            Ports         = src.Ports;
            FMin          = src.Frequencies[0];
            FMax          = src.Frequencies[src.FrequencyCount - 1];

            int nPorts   = Ports;
            int nFreqSrc = src.FrequencyCount;
            double[] xs  = src.Frequencies;
            bool forceLinear = method == InterpolationMethod.Linear || nFreqSrc < 3;

            _s1 = new RFNetwork.Spline1D[nPorts * nPorts];
            _s2 = new RFNetwork.Spline1D[nPorts * nPorts];

            // 3. Build splines for every (row, col) x component
            for (int r = 0; r < nPorts; r++)
            for (int c = 0; c < nPorts; c++)
            {
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
                    RFNetwork.PhaseUnwrap(comp2);
                }

                int k = r * nPorts + c;
                _s1[k] = new RFNetwork.Spline1D(xs, comp1, method, forceLinear);
                _s2[k] = new RFNetwork.Spline1D(xs, comp2, method, forceLinear);
            }
        }

        /// <summary>
        /// Evaluate a whole grid. Same arithmetic as <see cref="Evaluate(double, OutOfRangePolicy)"/>
        /// per element — the ONLY difference is loop order: (row, col) outermost keeps one spline
        /// pair's knot and coefficient arrays hot across every target, which on a 2001-point file is
        /// worth ~1.5x over sweeping all 2*N^2 splines once per target. Element-for-element identical,
        /// which is what SnpInterpolatorTests asserts with no tolerance.
        /// </summary>
        internal Mat<Complex>[] EvaluateAll(double[] hz, OutOfRangePolicy outOfRange)
        {
            var mats = new Mat<Complex>[hz.Length];
            for (int t = 0; t < hz.Length; t++)
                mats[t] = new Mat<Complex>(Ports, Ports);

            for (int r = 0; r < Ports; r++)
            for (int c = 0; c < Ports; c++)
            {
                int k = r * Ports + c;
                // Hoisted out of the target loop on purpose: read from the array each iteration,
                // the JIT re-loads the struct's five array fields every time and the batch path
                // measures ~1.5x slower than the hand-written loop this replaced.
                RFNetwork.Spline1D sp1 = _s1[k], sp2 = _s2[k];
                for (int t = 0; t < hz.Length; t++)
                {
                    double f    = hz[t];
                    bool extrap = (f < FMin || f > FMax)
                                  && outOfRange == OutOfRangePolicy.WarnExtrapolate;
                    double v1 = extrap ? sp1.EvalExtrap(f) : sp1.Eval(f);
                    double v2 = extrap ? sp2.EvalExtrap(f) : sp2.Eval(f);

                    mats[t][r, c] = Format == InterpolationFormat.RealImag
                        ? new Complex(v1, v2)
                        : Complex.FromPolarCoordinates(v1, v2);
                }
            }
            return mats;
        }

        /// <summary>Evaluate every (row, column) at one target frequency.</summary>
        internal Mat<Complex> Evaluate(double hz, OutOfRangePolicy outOfRange)
        {
            var m = new Mat<Complex>(Ports, Ports);
            bool outR    = hz < FMin || hz > FMax;
            bool extrap  = outR && outOfRange == OutOfRangePolicy.WarnExtrapolate;

            for (int r = 0; r < Ports; r++)
            for (int c = 0; c < Ports; c++)
            {
                int k = r * Ports + c;
                double v1 = extrap ? _s1[k].EvalExtrap(hz) : _s1[k].Eval(hz);
                double v2 = extrap ? _s2[k].EvalExtrap(hz) : _s2[k].Eval(hz);

                m[r, c] = Format == InterpolationFormat.RealImag
                    ? new Complex(v1, v2)
                    : Complex.FromPolarCoordinates(v1, v2);
            }
            return m;
        }
    }

    /// <summary>
    /// Interpolates one stored <see cref="SNP"/> to arbitrary frequencies, fitting its splines
    /// exactly once. Construct with the same five inputs <see cref="RFNetwork.Interpolate"/> takes,
    /// then call <see cref="Evaluate(double)"/> per frequency (the engine's stamp path) or
    /// <see cref="Evaluate(double[])"/> for a whole grid (what <c>Interpolate</c> itself does).
    ///
    /// <para>Out-of-range warning: an interpolator warns AT MOST ONCE, the first time a target
    /// falls outside the stored range. <c>RFNetwork.Interpolate</c> builds a fresh interpolator per
    /// call, which reproduces its historic one-warning-per-call behaviour exactly.</para>
    /// </summary>
    public sealed class SnpInterpolator
    {
        private readonly SnpFit           _fit;
        private readonly OutOfRangePolicy _outOfRange;
        private          bool             _warnedOutOfRange;

        /// <inheritdoc cref="RFNetwork.Interpolate"/>
        public SnpInterpolator(
            SNP                 source,
            InterpolationMethod method        = InterpolationMethod.CubicSpline,
            InterpolationFormat format        = InterpolationFormat.RealImag,
            MatrixType          interpolateIn = MatrixType.S,
            OutOfRangePolicy    outOfRange    = OutOfRangePolicy.WarnClamp)
            : this(new SnpFit(source, method, format, interpolateIn), outOfRange)
        {
        }

        /// <summary>Wrap an already-fitted <see cref="SnpFit"/> — the cache's entry point. Cheap:
        /// no fitting, and the fresh warn flag keeps the warning per consumer, not per process.</summary>
        internal SnpInterpolator(SnpFit fit, OutOfRangePolicy outOfRange)
        {
            _fit        = fit;
            _outOfRange = outOfRange;
        }

        /// <summary>The domain-converted source this interpolator was fitted from.</summary>
        public SNP Source => _fit.Source;

        /// <summary>Lowest stored frequency, Hz.</summary>
        public double MinFrequency => _fit.FMin;

        /// <summary>Highest stored frequency, Hz.</summary>
        public double MaxFrequency => _fit.FMax;

        /// <summary>Interpolate to a single frequency. Returns a fresh N x N matrix.</summary>
        public Mat<Complex> Evaluate(double hz)
        {
            WarnIfOutOfRange(hz < _fit.FMin, hz > _fit.FMax);
            return _fit.Evaluate(hz, _outOfRange);
        }

        /// <summary>
        /// Interpolate to a whole frequency grid, returning the same <see cref="SNP"/>
        /// <see cref="RFNetwork.Interpolate"/> has always returned.
        /// </summary>
        public SNP Evaluate(double[] targetFrequencies)
        {
            if (targetFrequencies.Length == 0)
                throw new ArgumentException("targetFrequencies must not be empty.");

            // 2. Check for out-of-range target frequencies and warn once
            bool hasBelow = false, hasAbove = false;
            foreach (double f in targetFrequencies)
            {
                if (f < _fit.FMin) hasBelow = true;
                if (f > _fit.FMax) hasAbove = true;
            }
            WarnIfOutOfRange(hasBelow, hasAbove);

            var resultMats = _fit.EvaluateAll(targetFrequencies, _outOfRange);

            return new SNP(targetFrequencies, resultMats,
                           _fit.InterpolateIn, _fit.Source.Format, _fit.Source.Z0);
        }

        private void WarnIfOutOfRange(bool hasBelow, bool hasAbove)
        {
            if (!hasBelow && !hasAbove) return;
            if (_warnedOutOfRange) return;
            _warnedOutOfRange = true;

            string side = (hasBelow && hasAbove) ? "both sides of"
                        : hasBelow ? "below" : "above";
            if (_outOfRange == OutOfRangePolicy.WarnExtrapolate)
                RFNetwork.Warn($"Interpolation target(s) extend {side} the stored range " +
                     $"[{_fit.FMin/1e9:G4}–{_fit.FMax/1e9:G4} GHz]. " +
                     "Linear extrapolation will be used — extrapolated S-parameters " +
                     "are routinely non-physical.");
            else
                RFNetwork.Warn($"Interpolation target(s) extend {side} the stored range " +
                     $"[{_fit.FMin/1e9:G4}–{_fit.FMax/1e9:G4} GHz]. " +
                     "Out-of-range values will be clamped to the nearest endpoint.");
        }
    }
}
