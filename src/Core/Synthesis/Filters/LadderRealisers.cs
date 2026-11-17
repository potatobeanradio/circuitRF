using System.Globalization;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Systems;
using static CircuitRF.Core.Synthesis.Filters.LadderSynthesis;

namespace CircuitRF.Core.Synthesis.Filters;

/// <summary>
/// One <see cref="LadderSynthesis"/> design as a realiser: the checks every ladder shares, and the MLAD it places (its
/// Sections, Z0 and F, F the cutoff or the band's centre). A band's centre here is the arithmetic one, (F1 + F2)/2, and
/// Δ = (F2 − F1)/f0: these are commensurate-line filters, whose response is symmetric in frequency about f0.
/// </summary>
public abstract class LadderRealiser : IFilterRealiser
{
    public abstract string Name { get; }

    private protected abstract FilterBand Band { get; }

    /// <summary>Whether the design places the spec's transmission zeros; one that cannot is not offered for a spec with any.</summary>
    private protected virtual bool TakesZeros => false;

    private protected virtual int MinOrder => 1;

    public virtual bool Supports(FilterSpec spec, out string? why)
    {
        int zeros = spec.TransmissionZerosHz?.Count ?? 0;
        why = spec.Band != Band ? $"It is a {Band.ToString().ToLowerInvariant()} filter."
            : spec.Response is not (FilterResponse.Butterworth or FilterResponse.Chebyshev)
                ? "It takes a Butterworth or Chebyshev prototype."
            : spec.Response == FilterResponse.Chebyshev && spec.Order % 2 == 0
                ? "An even-order Chebyshev prototype ends on a load other than Z0; take an odd order."
            : spec.Order < MinOrder ? $"It needs an order of {MinOrder} or more."
            : TakesZeros && zeros == 0 ? "It places transmission zeros; give at least one."
            : !TakesZeros && zeros > 0 ? "It places no transmission zeros."
            : null;
        return why is null;
    }

    public FilterRealisation Realise(FilterSpec spec, MicrostripSubstrate? substrate)
    {
        if (!Supports(spec, out string? why))
            throw Refuse("synthesis.ladder.unsupported", "{name}: {why}", ("name", Name), ("why", why));
        if (substrate is null)
            throw Refuse("synthesis.ladder.no-substrate", "{name} is drawn on a substrate, and none was given.", ("name", Name));
        var g = LowpassPrototype.GValues(spec.Response, spec.Order, spec.RippleDb);
        double f = Band == FilterBand.Lowpass ? spec.F1Hz : 0.5 * (spec.F1Hz + spec.F2Hz);
        IReadOnlyList<LadderSection> sections;
        try { sections = Design(spec, g, f, substrate); }
        catch (ParameterRefusalException e) when (e.Diagnostic?.Id == "device.ladder.width-unreachable")
        {
            throw Refuse("synthesis.ladder.width-unreachable",
                "{name}: one of its lines is an impedance no width on this substrate gives ({why}).", ("name", Name),
                ("why", e.Message));
        }
        return new FilterRealisation(ComponentModelFactory.LadderTypeName, new Dictionary<string, string>
        {
            ["Sections"] = LadderPlan.Format(sections),
            ["Z0"] = spec.Z0.ToString("0.######", CultureInfo.InvariantCulture) + " Ohm",
            ["F"] = (f / 1e9).ToString("0.#########", CultureInfo.InvariantCulture) + " GHz",
        }, [], [.. Notes, .. WidthNote(sections, substrate)]);
    }

    /// <summary>A note when a line or stub is narrower than <see cref="LadderSteppedImpedanceRealiser.NarrowestOverH"/>·H
    /// or wider than <see cref="LadderSteppedImpedanceRealiser.WidestOverH"/>·H, the widths a substrate is taken to realise.</summary>
    private static IEnumerable<string> WidthNote(IReadOnlyList<LadderSection> sections, MicrostripSubstrate s)
    {
        var w = sections.Select(x => x switch { LadderLine l => l.W, LadderStub b => b.W, _ => double.NaN })
            .Where(double.IsFinite).ToArray();
        double lo = LadderSteppedImpedanceRealiser.NarrowestOverH * s.HMeters;
        double hi = LadderSteppedImpedanceRealiser.WidestOverH * s.HMeters;
        if (w.Any(x => x < lo || x > hi))
            yield return $"Its widths run from {Fmt(w.Min() * 1e3)} to {Fmt(w.Max() * 1e3)} mm, past the {Fmt(lo * 1e3)} to "
                       + $"{Fmt(hi * 1e3)} mm (H/4 to 10·H) this substrate is taken to realise; check them against the process.";
    }

    /// <summary>The sections for <paramref name="spec"/>, from its prototype <paramref name="g"/>, at
    /// <paramref name="f"/>, the cutoff or the centre.</summary>
    private protected abstract IReadOnlyList<LadderSection> Design(FilterSpec spec, double[] g, double f, MicrostripSubstrate s);

    private protected virtual IReadOnlyList<string> Notes => [];

    private protected static double Fbw(FilterSpec spec) => (spec.F2Hz - spec.F1Hz) / (0.5 * (spec.F1Hz + spec.F2Hz));
}

/// <summary>The stepped-impedance lowpass (<see cref="LadderSynthesis.SteppedImpedance"/>). Its low and high impedances
/// are <see cref="ZLow"/> and <see cref="ZHigh"/>, or by default the widest and narrowest lines the substrate realises
/// (<see cref="WidestOverH"/>, <see cref="NarrowestOverH"/>).</summary>
public sealed class LadderSteppedImpedanceRealiser : LadderRealiser
{
    /// <summary>The widest default line, 10·H: the top of MLIN's dispersion fit (Kirschning and Jansen's 0.1 ≤ W/H ≤ 10).</summary>
    public const double WidestOverH = 10.0;

    /// <summary>The narrowest default line, H/4: inside that fit, and as narrow as a laminate is commonly etched
    /// (0.13 mm on 20 mil).</summary>
    public const double NarrowestOverH = 0.25;

    public double? ZLow { get; init; }
    public double? ZHigh { get; init; }

    public override string Name => "Stepped-impedance lowpass (MLAD)";
    private protected override FilterBand Band => FilterBand.Lowpass;

    private protected override IReadOnlyList<LadderSection> Design(FilterSpec spec, double[] g, double f, MicrostripSubstrate s)
    {
        var (zLow, zHigh) = Impedances(ZLow, ZHigh, f, s);
        if (!(zLow < spec.Z0 && spec.Z0 < zHigh))
            throw Refuse("synthesis.ladder.impedance-order",
                "{name}: the low impedance ({lo} Ω) must be under Z0 ({z0} Ω) and the high one ({hi} Ω) over it.",
                ("name", Name), ("lo", Fmt(zLow)), ("z0", Fmt(spec.Z0)), ("hi", Fmt(zHigh)));
        return SteppedImpedance(g, f, spec.Z0, zLow, zHigh, s);
    }

    /// <summary>The low and high impedances: those given, or MLIN's at <see cref="WidestOverH"/> and
    /// <see cref="NarrowestOverH"/> at F.</summary>
    internal static (double Low, double High) Impedances(double? zLow, double? zHigh, double f, MicrostripSubstrate s)
        => (zLow ?? BranchLinePlan.Line(WidestOverH * s.HMeters, s).LineParameters(f).Z0,
            zHigh ?? BranchLinePlan.Line(NarrowestOverH * s.HMeters, s).LineParameters(f).Z0);
}

/// <summary>The open-stub lowpass, Richards and Kuroda (<see cref="LadderSynthesis.KurodaStubs"/>).</summary>
public sealed class LadderStubLowpassRealiser : LadderRealiser
{
    public override string Name => "Open-stub lowpass, Richards-Kuroda (MLAD)";
    private protected override FilterBand Band => FilterBand.Lowpass;

    private protected override IReadOnlyList<LadderSection> Design(FilterSpec spec, double[] g, double f, MicrostripSubstrate s)
        => KurodaStubs(g, f, spec.Z0, s);
}

/// <summary>The stub lowpass with transmission zeros, an elliptic-like response from an all-pole prototype
/// (<see cref="LadderSynthesis.StubZeros"/>): one zero for every stub, or one per stub, each above the cutoff. The series
/// lines are <see cref="ZHigh"/>, by default as the stepped-impedance lowpass's.</summary>
public sealed class LadderStubZeroLowpassRealiser : LadderRealiser
{
    public double? ZHigh { get; init; }

    public override string Name => "Stub lowpass with zeros (MLAD)";
    private protected override FilterBand Band => FilterBand.Lowpass;
    private protected override bool TakesZeros => true;
    private protected override int MinOrder => 2;

    private protected override IReadOnlyList<LadderSection> Design(FilterSpec spec, double[] g, double f, MicrostripSubstrate s)
    {
        var zeros = spec.TransmissionZerosHz!;
        int stubs = spec.Order / 2;
        if (zeros.Count != 1 && zeros.Count != stubs)
            throw Refuse("synthesis.ladder.zero-count",
                "{name}: order {n} has {stubs} stubs; give one zero for them all, or one each, not {count}.",
                ("name", Name), ("n", spec.Order), ("stubs", stubs), ("count", zeros.Count));
        foreach (double z in zeros)
            if (!(z > f))
                throw Refuse("synthesis.ladder.zero-in-band", "{name}: a zero at {z} GHz is not above the {fc} GHz cutoff.",
                    ("name", Name), ("z", Fmt(z / 1e9)), ("fc", Fmt(f / 1e9)));
        var (_, zHigh) = LadderSteppedImpedanceRealiser.Impedances(null, ZHigh, f, s);
        return StubZeros(g, f, spec.Z0, zHigh, zeros, s);
    }
}

/// <summary>The shorted-stub bandpass (<see cref="LadderSynthesis.ShortedStubBandpass"/>). Its stubs are wide unless
/// the band is: a narrow band asks for widths no substrate gives, and is refused.</summary>
public sealed class LadderShortedStubBandpassRealiser : LadderRealiser
{
    public override string Name => "Shorted-stub bandpass (MLAD)";
    private protected override FilterBand Band => FilterBand.Bandpass;
    private protected override int MinOrder => 2;

    private protected override IReadOnlyList<LadderSection> Design(FilterSpec spec, double[] g, double f, MicrostripSubstrate s)
        => ShortedStubBandpass(g, f, Fbw(spec), spec.Z0, s);

    private protected override IReadOnlyList<string> Notes => ["Each G pin takes a via to ground."];
}

/// <summary>The quarter-wave open-stub bandstop (<see cref="LadderSynthesis.StubBandstop"/>).</summary>
public sealed class LadderStubBandstopRealiser : LadderRealiser
{
    public override string Name => "Quarter-wave stub bandstop (MLAD)";
    private protected override FilterBand Band => FilterBand.Bandstop;

    private protected override IReadOnlyList<LadderSection> Design(FilterSpec spec, double[] g, double f, MicrostripSubstrate s)
        => StubBandstop(g, f, Fbw(spec), spec.Z0, s);
}

/// <summary>The end-coupled (gap-coupled) bandpass (<see cref="LadderSynthesis.EndCoupled"/>).</summary>
public sealed class LadderEndCoupledRealiser : LadderRealiser
{
    public override string Name => "End-coupled bandpass (MLAD)";
    private protected override FilterBand Band => FilterBand.Bandpass;

    private protected override IReadOnlyList<LadderSection> Design(FilterSpec spec, double[] g, double f, MicrostripSubstrate s)
        => EndCoupled(g, f, Fbw(spec), spec.Z0, s);
}
