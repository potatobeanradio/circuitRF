using System.Numerics;
using CircuitRF.Core.Devices;

namespace CircuitRF.Core;

/// <summary>
/// One noise source a model declares (brief NZ-4): a noise current flowing from terminal <paramref name="TerminalA"/> to
/// terminal <paramref name="TerminalB"/> through the device, each an index into the placed component's
/// <c>ElaboratedComponent.Nodes</c> — internal nodes included, so a source on port p is <c>(2p, 2p + 1)</c>
/// (<see cref="OnPort"/>). A terminal on ground, or <c>-1</c>, drops out.
///
/// <para><b>Uncorrelated unless stated.</b> <see cref="CorrelatedWith"/> names an earlier source of the same model and
/// <see cref="Correlation"/> the complex coefficient ρ between the two, so their cross-density is
/// <c>ρ·√(S_this·S_that)</c>. The hook is there for a model that needs it (a MOSFET's induced gate noise against its
/// channel noise, at high frequency); no built-in model sets it yet.</para>
/// </summary>
public sealed record NoiseSourceDecl(string Name, int TerminalA, int TerminalB)
{
    /// <summary>The index of the source this one is correlated with, or -1.</summary>
    public int CorrelatedWith { get; init; } = -1;

    /// <summary>The correlation coefficient with <see cref="CorrelatedWith"/>, |ρ| ≤ 1.</summary>
    public Complex Correlation { get; init; }

    /// <summary>A source across port <paramref name="port"/>: from its + terminal to its − terminal.</summary>
    public static NoiseSourceDecl OnPort(string name, int port) => new(name, 2 * port, 2 * port + 1);
}

/// <summary>
/// A noise source's single-sided density at one instantaneous state: <c>S(f) = White + FlickerCoeff / f^FlickerExp</c>,
/// A²/Hz. Split so the state is evaluated once and the frequency dependence applied after — and so a harmonic-balance noise
/// analysis can modulate both parts by the large-signal waveform without evaluating the model per frequency.
/// </summary>
public readonly record struct NoiseDensity(double White, double FlickerCoeff = 0, double FlickerExp = 1)
{
    /// <summary>The density at <paramref name="hz"/>, A²/Hz.</summary>
    public double At(double hz) =>
        FlickerCoeff == 0 ? White : White + FlickerCoeff / Math.Pow(Math.Abs(hz), FlickerExp);

    /// <summary>Thermal noise of a conductance <paramref name="g"/> at <paramref name="kelvin"/>: <c>4kTg</c>.</summary>
    public static NoiseDensity Thermal(double g, double kelvin) => new(4 * Temperature.Boltzmann * kelvin * Math.Abs(g));

    /// <summary>Shot noise of a current <paramref name="i"/>: <c>2q|I|</c>.</summary>
    public static NoiseDensity Shot(double i) => new(2 * Temperature.ElemCharge * Math.Abs(i));

    /// <summary>Flicker noise <c>kf·|I|^af / f</c>; nothing when <paramref name="kf"/> is 0.</summary>
    public static NoiseDensity Flicker(double kf, double af, double i) =>
        new(0, kf == 0 ? 0 : kf * Math.Pow(Math.Abs(i), af));
}
