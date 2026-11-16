using System.Globalization;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Systems;

namespace CircuitRF.Core.Synthesis.Filters;

/// <summary>
/// An all-pole bandpass filter as one MCRC (docs/design/filter-synthesis.md §4): Butterworth or Chebyshev, of
/// <see cref="CoupledResonatorSpec.MaxN"/> resonators at most and <see cref="MaxBandwidth"/> wide at most. The chains
/// (Straight, ZigZag) take Cohn's closed forms (<see cref="CoupledResonatorDesign.Cohn"/>); hairpins and open loops the
/// coupling coefficients and external Q, solved on the part's own circuit model
/// (<see cref="CoupledResonatorDesign.FromCouplings"/>). Each form is a sealed class below, found by type.
/// </summary>
public abstract class CoupledResonatorRealiser(string name, ResonatorShape shape, ResonatorFeed feed = ResonatorFeed.Coupled,
    ResonatorProfile profile = ResonatorProfile.Uniform) : IFilterRealiser
{
    /// <summary>The widest band these narrow-band designs take, as a fraction of the centre.</summary>
    public const double MaxBandwidth = 0.2;

    public string Name => name;

    /// <summary>Where k and Qe are mapped to geometry (§6b): the circuit model, or with <see cref="CouplingRefine.Em"/>
    /// curves <see cref="Em"/> measures, for the hairpins and open loops.</summary>
    public CouplingRefine Refine { get; init; }

    /// <summary>The EM solver <see cref="CouplingRefine.Em"/> measures with, which the host passes in.</summary>
    public ICouplingCurveSolver? Em { get; init; }

    public bool Supports(FilterSpec spec, out string? why)
    {
        why = Check(spec)?.Message;
        return why is null;
    }

    public FilterRealisation Realise(FilterSpec spec, MicrostripSubstrate? substrate)
    {
        if (Check(spec) is { } refusal) throw refusal;
        var s = substrate ?? throw CoupledResonatorDesign.Refusal("substrate", "an MCRC is microstrip and needs its substrate.");
        var g = LowpassPrototype.GValues(spec.Response, spec.Order, spec.RippleDb);
        double f0 = spec.CentreHz, fbw = spec.FractionalBandwidth;
        int n = spec.Order;
        Dictionary<string, object> v;
        string note;
        if (shape is ResonatorShape.Straight or ResonatorShape.ZigZag)
        {
            var (w, l, gaps) = CoupledResonatorDesign.Cohn(g, fbw, f0, spec.Z0, s);
            if (shape == ResonatorShape.ZigZag) l = PassivePlan.OnGrid(l + CoupledResonatorDesign.CornerExcess(w, f0, s));
            v = new() { ["N"] = (double)n, ["W"] = w };
            for (int k = 1; k <= n; k++) v[$"L{k}"] = l;
            for (int k = 1; k <= n + 1; k++) v[$"S{k}"] = gaps[k];
            note = "Cohn's J-inverters on one strip width, each gap from the coupled pair's own modes, the lengths less the "
                 + "coupled open ends" + (shape == ResonatorShape.ZigZag ? " and plus each corner's excess." : ".");
        }
        else
        {
            double w = CoupledResonatorDesign.Width(spec.Z0, f0, s);
            var form = new Dictionary<string, object> { ["W"] = w, ["Shape"] = shape.ToString(), ["Feed"] = feed.ToString(),
                ["Profile"] = profile.ToString() };
            if (shape == ResonatorShape.Hairpin) form["Sa"] = PassivePlan.OnGrid(2.0 * w);
            if (shape == ResonatorShape.OpenLoop) form["Gap"] = w;
            if (profile == ResonatorProfile.Sir)
            {
                form["Wc"] = CoupledResonatorDesign.Width(2.0 * spec.Z0, f0, s);
                form["Uc"] = 0.3;
            }
            var k = CoupledResonatorDesign.Couplings(g, fbw);
            double qe = CoupledResonatorDesign.ExternalQ(g, fbw);
            v = CoupledResonatorDesign.FromCouplings(form, k, qe, f0, s, Refine == CouplingRefine.Em ? Em : null);
            note = $"Coupling coefficients k(1,2) = {(n > 1 ? k[0, 1] : 0.0).ToString("G3", CultureInfo.InvariantCulture)}… "
                 + $"and Qe = {qe.ToString("G3", CultureInfo.InvariantCulture)}, each gap and the "
                 + (feed == ResonatorFeed.Tapped ? "tap" : "feed gap") + " solved on the circuit model (MCLIN pairs; the "
                 + (shape == ResonatorShape.Hairpin ? "arms' own coupling" : "corners' coupling") + " left out)"
                 + (Refine == CouplingRefine.Em ? ", then the gaps" + (feed == ResonatorFeed.Tapped ? " and the tap" : "")
                    + " read off EM curves measured around it." : ".");
        }
        foreach (var (key, fallback) in new[] { ("Shape", shape.ToString()), ("Profile", profile.ToString()), ("Feed", feed.ToString()),
                     ("Form", nameof(ResonatorForm.Bandpass)), ("Arrangement", nameof(LoopArrangement.Inline)),
                     ("Coupling", nameof(ResonatorCoupling.Pairs)) })
            v.TryAdd(key, fallback);
        return new FilterRealisation(ComponentModelFactory.CoupledResonatorTypeName, Stored(v), [], [note]);
    }

    /// <summary>The stored values as a placed part holds them: lengths in mm, N and Uc as numbers, names as text.</summary>
    internal static Dictionary<string, string> Stored(IReadOnlyDictionary<string, object> v)
        => v.ToDictionary(e => e.Key, e => e.Value switch
        {
            string text => text,
            double x when e.Key is "N" or "Uc" => x.ToString("0.######", CultureInfo.InvariantCulture),
            double x => (x * 1e3).ToString("0.######", CultureInfo.InvariantCulture) + "mm",
            var other => Convert.ToString(other, CultureInfo.InvariantCulture)!,
        });

    private ParameterRefusalException? Check(FilterSpec spec) => spec switch
    {
        _ when Refine == CouplingRefine.Em && shape is ResonatorShape.Straight or ResonatorShape.ZigZag => CouplingCurves.Refusal(
            "closed-form", "Cohn's {shape} chain is closed form, with no k or Qe to measure; EM curves refine the hairpins "
            + "and open loops.", ("shape", shape)),
        _ when Refine == CouplingRefine.Em && Em is null => CouplingCurves.Refusal("no-solver",
            "Refine = Em needs an EM solver passed in, and none was."),
        { Band: not FilterBand.Bandpass } => CoupledResonatorDesign.Refusal("band",
            "these realisers build bandpass filters; a {band} filter is another family's.", ("band", spec.Band)),
        { TransmissionZerosHz.Count: > 0 } => CoupledResonatorDesign.Refusal("zeros", "{n} transmission zeros need cross "
          + "couplings from the coupling matrix; these realisers build all-pole filters.", ("n", spec.TransmissionZerosHz.Count)),
        { Response: not (FilterResponse.Butterworth or FilterResponse.Chebyshev) } => CoupledResonatorDesign.Refusal("response",
            "a {response} response has no closed-form prototype here; Butterworth and Chebyshev do.", ("response", spec.Response)),
        { Order: < 1 or > CoupledResonatorSpec.MaxN } => CoupledResonatorDesign.Refusal("order",
            "order {n} is not 1 to {max} resonators.", ("n", spec.Order), ("max", CoupledResonatorSpec.MaxN)),
        _ when !(spec.FractionalBandwidth > 0.0 && spec.FractionalBandwidth <= MaxBandwidth) => CoupledResonatorDesign.Refusal(
            "bandwidth", "a fractional bandwidth of {fbw} is outside these narrow-band designs' (0, {max}].",
            ("fbw", spec.FractionalBandwidth), ("max", MaxBandwidth)),
        _ => null,
    };
}

/// <summary>Cohn's parallel-coupled filter, the resonators in a straight staircase.</summary>
public sealed class EdgeCoupledRealiser() : CoupledResonatorRealiser("Edge-coupled bandpass (MCRC)", ResonatorShape.Straight);

/// <summary>Cohn's design folded: each resonator turned 90° at its middle, its corner's excess added.</summary>
public sealed class ZigZagRealiser() : CoupledResonatorRealiser("Zig-zag bandpass (MCRC)", ResonatorShape.ZigZag);

/// <summary>Hairpins fed by coupled strips beside the end arms.</summary>
public sealed class HairpinRealiser() : CoupledResonatorRealiser("Hairpin bandpass, coupled feed (MCRC)", ResonatorShape.Hairpin);

/// <summary>Hairpins tapped on the end arms.</summary>
public sealed class TappedHairpinRealiser()
    : CoupledResonatorRealiser("Hairpin bandpass, tapped feed (MCRC)", ResonatorShape.Hairpin, ResonatorFeed.Tapped);

/// <summary>Stepped-impedance hairpins (A. Makimoto and S. Yamashita, IEEE Trans. MTT 28(12), 1980,
/// <see href="https://doi.org/10.1109/TMTT.1980.1130258">10.1109/TMTT.1980.1130258</see>), fed by coupled strips: the U
/// a 2·Z0 line over 0.3 of the length, the arms' ends Z0, which shortens each resonator and moves its first spurious
/// response up. The coupled ends are shorter than a uniform hairpin's, so the feeds and couplings are weaker and the
/// widest band it reaches is narrower; a tap on the ends is too heavy for any band these designs take.</summary>
public sealed class SirHairpinRealiser()
    : CoupledResonatorRealiser("Stepped-impedance hairpin bandpass (MCRC)", ResonatorShape.Hairpin, ResonatorFeed.Coupled,
        ResonatorProfile.Sir);

/// <summary>Hong and Lancaster's open loops in a row, opened alternately top and bottom, tapped.</summary>
public sealed class OpenLoopRealiser()
    : CoupledResonatorRealiser("Open-loop bandpass, inline (MCRC)", ResonatorShape.OpenLoop, ResonatorFeed.Tapped);
