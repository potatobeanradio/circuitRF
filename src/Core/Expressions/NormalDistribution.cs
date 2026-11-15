namespace CircuitRF.Core.Expressions;

/// <summary>
/// The standard normal distribution — its CDF Φ, its inverse Φ⁻¹, its density φ, and the complementary error
/// function they rest on (docs/design/yield.md §5).
///
/// <para>The implementation is <see cref="RfCore.Statistics.Normal"/>, one layer down, because the propagation
/// models in RfCore need it too and RfCore references nothing above it. This class keeps its name and members so
/// the expression engine and <c>Engine.Statistics.SpecialFunctions</c> read as before; it forwards.</para>
/// </summary>
public static class NormalDistribution
{
    /// <inheritdoc cref="RfCore.Statistics.Normal.Erfc"/>
    public static double Erfc(double x) => RfCore.Statistics.Normal.Erfc(x);

    /// <inheritdoc cref="RfCore.Statistics.Normal.Cdf"/>
    public static double Cdf(double z) => RfCore.Statistics.Normal.Cdf(z);

    /// <inheritdoc cref="RfCore.Statistics.Normal.InverseCdf"/>
    public static double InverseCdf(double p) => RfCore.Statistics.Normal.InverseCdf(p);

    /// <inheritdoc cref="RfCore.Statistics.Normal.Pdf"/>
    public static double Pdf(double z) => RfCore.Statistics.Normal.Pdf(z);
}
