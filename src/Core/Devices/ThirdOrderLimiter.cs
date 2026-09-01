namespace CircuitRF.Core.Devices;

/// <summary>
/// The soft limiter, and the arithmetic that turns a stated third-order intercept into its scale.
/// One copy, shared by every ideal block in this repository that compresses:
/// <see cref="MixerModel"/>'s RF path and <see cref="AmplifierModel"/>'s forward path.
///
/// <para><b>Why tanh and not the textbook cubic.</b> A bare <c>a₁·x − a₃·x³</c> turns over and goes
/// NEGATIVE past its peak, so Newton finds the wrong root and the run converges cleanly to
/// nonsense. <c>tanh</c> is monotone and bounded everywhere, and its own expansion
/// <c>x − x³/3 + 2x⁵/15 − …</c> fixes the third-order intercept EXACTLY: matching the cubic term to
/// <c>a₁ = 1</c>, <c>a₃ = 1/(3·Vsat²)</c> in <c>IIP3 = √(4·a₁/(3·a₃))</c> gives
/// <c>IIP3 = 2·Vsat</c> in volts.</para>
///
/// <para><b>What that costs, and it is not free.</b> tanh's fifth-order term is what a cubic does
/// not have, and it shows up in two places a user can measure:</para>
/// <list type="bullet">
/// <item><description><b>IM3 sits slightly BELOW the two-tone extrapolation</b> as the drive rises
/// — measured at −0.06 dB 30 dB below the intercept, −0.18 dB at 25, −0.56 dB at 20 and −4.7 dB at
/// 10, so an intercept read off a curve has to be read from well down the curve, exactly as it is
/// on a bench.</description></item>
/// <item><description><b>The 1 dB compression point is <c>IIP3 − 8.9625 dB</c> input-referred, not
/// the cubic's <c>IIP3 − 9.6357 dB</c>.</b> That is tanh's own describing function
/// <c>(2/π)∫₀^π tanh(u·cos θ)·cos θ dθ / u = 10^(−1/20)</c> at <c>u = 0.712697</c>, against the
/// cubic's <c>u = √(4(1 − 10^(−1/20))) = 0.659542</c> — computed, not quoted, and computed again
/// inside the gate that asserts it. brief-sys-5 attributes 9.6 dB to the tanh limiter; 9.6 dB is
/// the CUBIC's number and the two differ by 0.67 dB, which is more than that gate's own 0.2 dB
/// tolerance.</description></item>
/// </list>
/// </summary>
internal static class ThirdOrderLimiter
{
    /// <summary>
    /// The soft-limit scale in VOLTS, from an INPUT-referred third-order intercept in dBm stated at
    /// a port of <paramref name="zRef"/> ohms.
    ///
    /// <para><c>IIP3 = 2·Vsat</c> in volts (see the class remarks), and the peak voltage a matched
    /// port of <paramref name="zRef"/> ohms carries at an available power P is <c>√(2·P·Z)</c>,
    /// which is the whole of the line below.</para>
    /// </summary>
    public static double SaturationVolts(double iip3Dbm, double zRef)
        => 0.5 * Math.Sqrt(2.0 * 1e-3 * Math.Pow(10.0, iip3Dbm / 10.0) * zRef);

    /// <summary>
    /// The limiter and its slope: <c>u = Vsat·tanh(x/Vsat)</c> and <c>du = ∂u/∂x</c>.
    ///
    /// <para>A <paramref name="vsat"/> of zero is the EXACTLY linear path — the identity, and a
    /// slope of exactly one — rather than a tanh whose argument is ~1e−5. That is the family's
    /// standing rule: an ideal device takes the identity path, not a path that merely rounds to
    /// it. Which stated dBm counts as "off" is the caller's decision, because the sentinel differs
    /// per component (the mixer's IIP3 default is 100 dBm, the amplifier's IP3 is 200).</para>
    /// </summary>
    public static void Apply(double vsat, double x, out double u, out double du)
    {
        if (vsat > 0.0)
        {
            double t = Math.Tanh(x / vsat);
            u  = vsat * t;
            du = 1.0 - t * t;
        }
        else
        {
            u  = x;
            du = 1.0;
        }
    }
}
