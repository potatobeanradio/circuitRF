using System.Numerics;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>
/// The frequency response a controlled source (<see cref="VccsModel"/>, <see cref="VcvsModel"/>)
/// stamps: its real gain <c>K</c> — a transconductance or a voltage gain — times the delay <c>T</c>:
///
///   H(ω) = K · e^(−jωT)
///
/// <para>The delay is the ONLY source of phase, as in other frequency-domain simulators' controlled
/// sources: a positive <c>T</c> lags, by an angle proportional to frequency, and DC (ω = 0) is
/// unaffected. A complex <c>K</c> is refused by name in <c>ComponentModelFactory</c>'s
/// complex-parameter rules — the VCCS once read one as zero, silently.</para>
///
/// <para>A negative delay is allowed. It is non-causal, and in the frequency domain that is still
/// just a phase slope — the usual use is cancelling a known delay elsewhere in a chain.</para>
/// </summary>
internal static class ControlledSourceTransfer
{
    /// <summary>The first of <paramref name="names"/> present, as a real number.</summary>
    public static double Gain(ElaboratedComponent c, double fallback, params string[] names)
    {
        foreach (string n in names)
            if (c.Parameters.TryGetValue(n, out var v)) return v.AsReal();
        return fallback;
    }

    /// <summary><c>K · e^(−jωT)</c>, with <c>T</c> read from the component (0 when absent).</summary>
    public static Complex At(ElaboratedComponent c, double gain, double omega)
    {
        double t = c.Parameters.TryGetValue("T", out var tv) ? tv.AsReal() : 0.0;
        if (t == 0.0 || omega == 0.0) return new Complex(gain, 0);
        double phase = -omega * t;
        return gain * new Complex(Math.Cos(phase), Math.Sin(phase));
    }
}
