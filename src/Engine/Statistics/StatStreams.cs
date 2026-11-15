using CircuitRF.Engine.Optimization;

namespace CircuitRF.Engine.Statistics;

/// <summary>
/// Counter-based random streams (yield overview D5): a draw is a pure function
/// <c>Hash(seed, trial, stream, k) → uniform</c>, not the next number of a generator. So trial N is
/// the same drawn alone or in a batch, in any order and on any thread, and a stream's draws do not
/// depend on which other streams exist. Built on <see cref="SplitMix64.Mix"/>, whose output is fixed on
/// every platform.
/// </summary>
public static class StatStreams
{
    /// <summary>
    /// A stream's id: a stable 64-bit hash of its text — a statistical entry's key (<c>DUT:R3.R</c>,
    /// <c>mag(ZL)</c>) or, for a kit draw, its instance path and parameter. FNV-1a over the UTF-8 bytes,
    /// then mixed, so similar keys land far apart.
    /// </summary>
    public static ulong Id(string text) => RfCore.Statistics.StatDraws.Id(text);

    /// <summary>The 64 bits of draw <paramref name="k"/> of <paramref name="stream"/> in
    /// <paramref name="trial"/> under <paramref name="seed"/> (<see cref="RfCore.Statistics.StatDraws.Hash"/>, moved down to
    /// RfCore by brief PCH-7 so the propagation channel's fading draws are the yield engine's).</summary>
    public static ulong Hash(ulong seed, long trial, ulong stream, int k) => RfCore.Statistics.StatDraws.Hash(seed, trial, stream, k);

    /// <summary>Uniform on the OPEN interval (0, 1) — the top 53 bits, offset by half a step — so its
    /// normal quantile is always finite.</summary>
    public static double Uniform(ulong seed, long trial, ulong stream, int k = 0)
        => RfCore.Statistics.StatDraws.Uniform(seed, trial, stream, k);

    /// <summary>A standard normal, by inversion: Φ⁻¹ of <see cref="Uniform"/>.</summary>
    public static double Normal(ulong seed, long trial, ulong stream, int k = 0)
        => SpecialFunctions.InverseNormalCdf(Uniform(seed, trial, stream, k));
}
