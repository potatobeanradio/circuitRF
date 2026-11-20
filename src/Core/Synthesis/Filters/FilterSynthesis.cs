using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Systems;

namespace CircuitRF.Core.Synthesis.Filters;

/// <summary>Which part of the spectrum a filter passes.</summary>
public enum FilterBand { Lowpass, Highpass, Bandpass, Bandstop }

/// <summary>
/// What a filter is asked to do (docs/design/filter-synthesis.md). <paramref name="F1Hz"/> is a lowpass or highpass
/// filter's cutoff, a bandpass or bandstop filter's lower band edge, and <paramref name="F2Hz"/> its upper edge (unused,
/// and equal to F1, for a lowpass or highpass). <paramref name="RippleDb"/> is the passband ripple of an equiripple
/// response, <paramref name="StopAttenuationDb"/> the stopband floor of a response that has one, and
/// <paramref name="TransmissionZerosHz"/> the finite zeros a cross-coupled or elliptic design places.
/// </summary>
public sealed record FilterSpec(
    FilterResponse Response, FilterBand Band, int Order, double F1Hz, double F2Hz,
    double RippleDb = 0.1, double StopAttenuationDb = 40.0, double Z0 = 50.0,
    IReadOnlyList<double>? TransmissionZerosHz = null)
{
    /// <summary>The cutoff of a lowpass or highpass, the geometric centre of a band.</summary>
    public double CentreHz => Band is FilterBand.Bandpass or FilterBand.Bandstop ? Math.Sqrt(F1Hz * F2Hz) : F1Hz;

    /// <summary>A band's width over its centre; 0 for a lowpass or highpass.</summary>
    public double FractionalBandwidth => Band is FilterBand.Bandpass or FilterBand.Bandstop ? (F2Hz - F1Hz) / CentreHz : 0.0;
}

/// <summary>
/// The doubly terminated lowpass prototype's element values g0 … g(N+1), normalised to g0 = 1 and a 1 rad/s cutoff
/// (G. L. Matthaei, L. Young and E. M. T. Jones, <i>Microwave Filters, Impedance-Matching Networks, and Coupling
/// Structures</i>, McGraw-Hill, 1964, §4.05; a book, no DOI).
/// </summary>
public static class LowpassPrototype
{
    /// <summary>g0 … g(N+1) for <paramref name="response"/>. Butterworth and Chebyshev are closed forms, Bessel is
    /// extracted (<see cref="PrototypeLadder"/>); a response with finite zeros has resonators as well, so it is
    /// <see cref="PrototypeLadder.For"/>'s and refused here.</summary>
    public static double[] GValues(FilterResponse response, int order, double rippleDb = 0.1)
    {
        if (order < 1) throw new ArgumentOutOfRangeException(nameof(order), order, "A prototype has at least one element.");
        var g = new double[order + 2];
        g[0] = 1.0;
        switch (response)
        {
            case FilterResponse.Butterworth:
                for (int k = 1; k <= order; k++) g[k] = 2.0 * Math.Sin((2 * k - 1) * Math.PI / (2.0 * order));
                g[order + 1] = 1.0;
                return g;
            case FilterResponse.Chebyshev:
            {
                if (!(rippleDb > 0)) throw new ArgumentOutOfRangeException(nameof(rippleDb), rippleDb, "Ripple must be positive.");
                double beta = Math.Log(1.0 / Math.Tanh(rippleDb * Math.Log(10.0) / 40.0));   // the tables round 40/ln 10 to 17.37
                double gamma = Math.Sinh(beta / (2.0 * order));
                double A(int k) => Math.Sin((2 * k - 1) * Math.PI / (2.0 * order));
                double B(int k) => gamma * gamma + Math.Pow(Math.Sin(k * Math.PI / order), 2);
                g[1] = 2.0 * A(1) / gamma;
                for (int k = 2; k <= order; k++) g[k] = 4.0 * A(k - 1) * A(k) / (B(k - 1) * g[k - 1]);
                double coth = 1.0 / Math.Tanh(beta / 4.0);
                g[order + 1] = order % 2 == 1 ? 1.0 : coth * coth;
                return g;
            }
            case FilterResponse.Bessel:
                return PrototypeLadder.BesselG(order);
            default:
                throw new NotSupportedException($"No closed-form prototype for {response} yet.");
        }
    }
}

/// <summary>One site's wanted part: its role (R, L, C or via), its pins, and its value in ohms, henries or farads.</summary>
public sealed record SiteValue(string Role, string PinA, string? PinB, double Value);

/// <summary>
/// What a realiser produces: one built-in part to place, as its engine type and the parameters the schematic stores
/// (expressions with units, as typed), the values its sites want, and notes for the designer (a refusal is an
/// exception, not a note).
/// </summary>
public sealed record FilterRealisation(
    string PartType, IReadOnlyDictionary<string, string> Parameters, IReadOnlyList<SiteValue> Sites,
    IReadOnlyList<string> Notes);

/// <summary>One way to build a filter: a family and form (an edge-coupled MCRC, an MLAD stepped-impedance lowpass, a
/// lumped ladder…). <paramref name="substrate"/> is null for a realiser that needs none.</summary>
public interface IFilterRealiser
{
    /// <summary>What the designer lists, e.g. "Edge-coupled bandpass (MCRC)".</summary>
    string Name { get; }

    /// <summary>Whether this realiser can build <paramref name="spec"/>, and if not, why, in one sentence.</summary>
    bool Supports(FilterSpec spec, out string? why);

    FilterRealisation Realise(FilterSpec spec, MicrostripSubstrate? substrate);

    /// <summary>As <see cref="Realise(FilterSpec, MicrostripSubstrate?)"/>, stopping with an
    /// <see cref="OperationCanceledException"/> between its solves once <paramref name="cancel"/> is cancelled: the Filter
    /// Designer drops a realisation the specification has moved on from. A realiser that solves nothing at length need
    /// not implement it.</summary>
    FilterRealisation Realise(FilterSpec spec, MicrostripSubstrate? substrate, CancellationToken cancel) => Realise(spec, substrate);
}

/// <summary>Every realiser in this assembly, by name. Found by type so each family adds its own without a shared
/// list to edit.</summary>
public static class FilterRealisers
{
    public static IReadOnlyList<IFilterRealiser> All { get; } =
        [.. typeof(IFilterRealiser).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IFilterRealiser).IsAssignableFrom(t)
                        && t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(t => (IFilterRealiser)Activator.CreateInstance(t)!)
            .OrderBy(r => r.Name, StringComparer.Ordinal)];

    /// <summary>The realisers that can build <paramref name="spec"/>.</summary>
    public static IEnumerable<IFilterRealiser> For(FilterSpec spec) => All.Where(r => r.Supports(spec, out _));
}
