// ================================================================
//  TwoPortCircles.cs  —  a two-port's gain and stability circles on the source and load planes
//
//  Conventions, for every function here:
//    • S is a 2×2 power-wave scattering matrix on REAL references, port 1's for the source plane and
//      port 2's for the load plane. Γs is referenced to port 1's, ΓL to port 2's.
//    • Δ = S11·S22 − S12·S21. Γin = S11 + S12·S21·ΓL/(1 − S22·ΓL), Γout = S22 + S12·S21·Γs/(1 − S11·Γs).
//    • Available gain Ga(Γs) = |S21|²(1 − |Γs|²) / (|1 − S11·Γs|²(1 − |Γout|²)), the load conjugately
//      matched. Operating gain Gp(ΓL) = |S21|²(1 − |ΓL|²) / ((1 − |Γin|²)|1 − S22·ΓL|²), the source
//      conjugately matched. Both are linear power ratios.
//    • The source stability circle is where |Γout| = 1, the load stability circle where |Γin| = 1.
// ================================================================

using System;
using System.Numerics;
using NumFlat;

namespace RfCore.Data
{
    /// <summary>A circle on the Γ plane: its centre and radius.</summary>
    public readonly record struct GammaCircle(Complex Centre, double Radius);

    /// <summary>Gain and stability circles of a two-port, on the conventions in this file's header.</summary>
    public static class TwoPortCircles
    {
        /// <summary>The circle on the SOURCE plane where |Γout| = 1: centre <c>(S11 − Δ·S22*)*/(|S11|² − |Δ|²)</c>,
        /// radius <c>|S12·S21|/||S11|² − |Δ|²|</c>. NaN when |S11| = |Δ|, where the locus is a straight line.</summary>
        public static GammaCircle SourceStability(Mat<Complex> s)
            => Stability(s[0, 0], s[1, 1], s);

        /// <summary>The circle on the LOAD plane where |Γin| = 1: centre <c>(S22 − Δ·S11*)*/(|S22|² − |Δ|²)</c>,
        /// radius <c>|S12·S21|/||S22|² − |Δ|²|</c>. NaN when |S22| = |Δ|.</summary>
        public static GammaCircle LoadStability(Mat<Complex> s)
            => Stability(s[1, 1], s[0, 0], s);

        private static GammaCircle Stability(Complex own, Complex other, Mat<Complex> s)
        {
            // Squared as magnitudes, so the Data Display's stability-circle traces, which come through here, keep their bits.
            var delta = Delta(s);
            double denom = own.Magnitude * own.Magnitude - delta.Magnitude * delta.Magnitude;
            if (denom == 0) return new GammaCircle(new Complex(double.NaN, double.NaN), double.NaN);
            return new GammaCircle(Complex.Conjugate(own - delta * Complex.Conjugate(other)) / denom,
                                   (s[0, 1] * s[1, 0]).Magnitude / Math.Abs(denom));
        }

        /// <summary>
        /// Whether the stable region of the source plane is INSIDE the source stability circle. Γs = 0 gives
        /// Γout = S22, so the chart's centre is stable exactly when |S22| &lt; 1; the stable side is the side the centre
        /// is on when it is stable, and the other side when it is not.
        /// </summary>
        public static bool StableInsideSource(Mat<Complex> s)
        {
            var c = SourceStability(s);
            return s[1, 1].Magnitude < 1.0 == c.Centre.Magnitude < c.Radius;
        }

        /// <summary>The same on the load plane: ΓL = 0 gives Γin = S11.</summary>
        public static bool StableInsideLoad(Mat<Complex> s)
        {
            var c = LoadStability(s);
            return s[0, 0].Magnitude < 1.0 == c.Centre.Magnitude < c.Radius;
        }

        /// <summary>
        /// The constant available-gain circle for <paramref name="gain"/> (linear) on the SOURCE plane. With
        /// <c>g = gain/|S21|²</c> and <c>D = 1 + g(|S11|² − |Δ|²)</c>: centre <c>g·(S11 − Δ·S22*)*/D</c> and radius
        /// <c>√(1 − 2K·|S12·S21|·g + |S12·S21|²·g²)/|D|</c>. Null when there is no such circle: a gain above
        /// <paramref name="maxGain"/> (the two-port's MAG, or MSG when it is not unconditionally stable), a negative
        /// radicand, or D = 0.
        /// </summary>
        public static GammaCircle? AvailableGain(Mat<Complex> s, double gain, double maxGain)
            => GainCircle(s, gain, maxGain, s[0, 0], s[1, 1]);

        /// <summary>
        /// The constant operating-gain circle for <paramref name="gain"/> (linear) on the LOAD plane: the same form as
        /// <see cref="AvailableGain"/> with the ports exchanged, <c>D = 1 + g(|S22|² − |Δ|²)</c> and centre
        /// <c>g·(S22 − Δ·S11*)*/D</c>.
        /// </summary>
        public static GammaCircle? OperatingGain(Mat<Complex> s, double gain, double maxGain)
            => GainCircle(s, gain, maxGain, s[1, 1], s[0, 0]);

        private static GammaCircle? GainCircle(Mat<Complex> s, double gain, double maxGain, Complex own, Complex other)
        {
            // A circle at exactly the maximum is the single conjugate-match point; past it, none.
            const double Slack = 1e-9;
            if (!(gain > 0) || !(gain <= maxGain * (1 + Slack))) return null;

            var delta = Delta(s);
            double s21sq = Sq(s[1, 0]);
            double k = RFNetwork.StabilityK(s).K;
            double c = (s[0, 1] * s[1, 0]).Magnitude;
            double g = gain / s21sq;
            double d = 1 + g * (Sq(own) - Sq(delta));
            if (d == 0) return null;
            double radicand = 1 - 2 * k * c * g + c * c * g * g;
            if (radicand < 0)
            {
                // At the maximum the radicand is zero, and round-off can take it just below.
                if (radicand < -Slack) return null;
                radicand = 0;
            }
            return new GammaCircle(g * Complex.Conjugate(own - delta * Complex.Conjugate(other)) / d,
                                   Math.Sqrt(radicand) / Math.Abs(d));
        }

        /// <summary>The available gain (linear) from a source of reflection <paramref name="gammaS"/>.</summary>
        public static double AvailableGainAt(Mat<Complex> s, Complex gammaS)
        {
            var gOut = s[1, 1] + s[0, 1] * s[1, 0] * gammaS / (1 - s[0, 0] * gammaS);
            return Sq(s[1, 0]) * (1 - Sq(gammaS)) / (Sq(1 - s[0, 0] * gammaS) * (1 - Sq(gOut)));
        }

        /// <summary>The operating gain (linear) into a load of reflection <paramref name="gammaL"/>.</summary>
        public static double OperatingGainAt(Mat<Complex> s, Complex gammaL)
        {
            var gIn = s[0, 0] + s[0, 1] * s[1, 0] * gammaL / (1 - s[1, 1] * gammaL);
            return Sq(s[1, 0]) * (1 - Sq(gammaL)) / ((1 - Sq(gIn)) * Sq(1 - s[1, 1] * gammaL));
        }

        /// <summary>|Γout| for a source of reflection <paramref name="gammaS"/>.</summary>
        public static double OutputReflection(Mat<Complex> s, Complex gammaS)
            => (s[1, 1] + s[0, 1] * s[1, 0] * gammaS / (1 - s[0, 0] * gammaS)).Magnitude;

        /// <summary>|Γin| for a load of reflection <paramref name="gammaL"/>.</summary>
        public static double InputReflection(Mat<Complex> s, Complex gammaL)
            => (s[0, 0] + s[0, 1] * s[1, 0] * gammaL / (1 - s[1, 1] * gammaL)).Magnitude;

        private static Complex Delta(Mat<Complex> s) => s[0, 0] * s[1, 1] - s[0, 1] * s[1, 0];

        private static double Sq(Complex z) => z.Real * z.Real + z.Imaginary * z.Imaginary;
    }
}
