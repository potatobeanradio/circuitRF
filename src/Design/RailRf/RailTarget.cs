namespace CircuitRF.Design.RailRf;

/// <summary>What a target IS. The document says which, and there is no inference (railrf.md §2.2).</summary>
public enum RailTargetKind
{
    /// <summary>Millivolts, and it is what the DC mode is judged against.</summary>
    DropBudget,

    /// <summary>A flat Z_target in milliohms.</summary>
    FlatImpedance,

    /// <summary>A table of (frequency, limit) points. <b>Per observation port</b>, so it lives on the
    /// load row rather than on the rail.</summary>
    Mask,

    /// <summary>ΔI, ΔV and a rise time, from which railRF DERIVES the flat target and the top of the
    /// band that matters — see <see cref="RailTransientSpec"/>.</summary>
    Transient,
}

/// <summary>One row of a piecewise mask: a frequency in HERTZ and a limit in OHMS, both base SI.</summary>
public readonly record struct RailMaskPoint(double FrequencyHz, double LimitOhms);

/// <summary>
/// A transient specification, and the arithmetic railRF derives from it.
///
/// <para><b>The derivation belongs here and not in the window</b> (§2.2, and brief 1 §5): the same
/// document opened headlessly has to produce the same target, so it is a pure function of the three
/// stated numbers and nothing else. It is tested as one — two reads of the same document derive the
/// same flat Z and the same band top.</para>
///
/// <para>All three fields are base SI: amps, volts, seconds.</para>
/// </summary>
public sealed record RailTransientSpec(double DeltaIAmps, double DeltaVVolts, double RiseTimeSeconds)
{
    /// <summary>
    /// The flat impedance target this transient implies, in OHMS: <c>ΔV / ΔI</c>. The whole of the
    /// classic PDN target — the rail may not move more than ΔV while the load steps by ΔI, and an
    /// impedance is what enforces that at every frequency in the band.
    /// </summary>
    public double FlatTargetOhms => DeltaVVolts / DeltaIAmps;

    /// <summary>
    /// The top of the band that matters, in HERTZ: the knee frequency <c>0.35 / t_rise</c>.
    ///
    /// <para>0.35 is the 10–90 % rise time's own bandwidth relation, and it is written as a named
    /// constant rather than folded into the expression precisely because it is a CONVENTION: a
    /// reader who assumes 0.5 (a 20–80 % rise) gets a band top 43 % too high, and nothing in the
    /// answer would look wrong.</para>
    /// </summary>
    public double BandTopHz => KneeFactor / RiseTimeSeconds;

    /// <summary>The 10–90 % rise-time-to-knee-frequency factor. See <see cref="BandTopHz"/>.</summary>
    public const double KneeFactor = 0.35;

    /// <summary>Null when the three numbers can be used, or the sentence saying why not.</summary>
    public string? Refusal(string where)
    {
        if (!(DeltaIAmps > 0))
            return $"{where}'s transient target states ΔI = {DeltaIAmps} A. A current step is what " +
                   "the impedance target divides by; state a positive one.";
        if (!(DeltaVVolts > 0))
            return $"{where}'s transient target states ΔV = {DeltaVVolts} V. State the voltage the " +
                   "rail is allowed to move by, as a positive number.";
        if (!(RiseTimeSeconds > 0))
            return $"{where}'s transient target states a rise time of {RiseTimeSeconds} s. The band " +
                   "top is derived from it, so it must be positive.";
        return null;
    }
}

/// <summary>
/// One target. Four kinds (§2.2's own list), and the document says which — <see cref="Kind"/> picks
/// exactly one of the four payloads and the other three are null.
///
/// <para><b>A rail carries a drop budget AND one frequency-domain target; they are not
/// alternatives.</b> The DC answer is judged against millivolts and the frequency answer against
/// milliohms, and a board that states only one of them is simply a board with one of the two
/// questions unanswered. So <see cref="RailSpec.DropBudget"/> and
/// <see cref="RailSpec.ImpedanceTarget"/> are separate slots rather than one field of four kinds,
/// and <see cref="RailLoad.Mask"/> is a third — a mask is per observation port.</para>
/// </summary>
public sealed record RailTarget
{
    public RailTargetKind Kind { get; init; }

    /// <summary>Set iff <see cref="Kind"/> is <see cref="RailTargetKind.DropBudget"/>. MILLIVOLTS —
    /// the one field on this record that is not base SI, because it is what the window's own row
    /// says and a budget of 0.08 V reads as a typo where 80 mV does not.</summary>
    public double? DropBudgetMillivolts { get; init; }

    /// <summary>Set iff <see cref="Kind"/> is <see cref="RailTargetKind.FlatImpedance"/>.
    /// MILLIOHMS, for <see cref="DropBudgetMillivolts"/>' reason.</summary>
    public double? FlatMilliohms { get; init; }

    /// <summary>Set iff <see cref="Kind"/> is <see cref="RailTargetKind.Mask"/>. Base SI throughout
    /// — hertz and ohms — because nothing types these row by row.</summary>
    public IReadOnlyList<RailMaskPoint>? Mask { get; init; }

    /// <summary>Set iff <see cref="Kind"/> is <see cref="RailTargetKind.Transient"/>.</summary>
    public RailTransientSpec? Transient { get; init; }

    public static RailTarget OfDropBudget(double millivolts) =>
        new() { Kind = RailTargetKind.DropBudget, DropBudgetMillivolts = millivolts };

    public static RailTarget OfFlatImpedance(double milliohms) =>
        new() { Kind = RailTargetKind.FlatImpedance, FlatMilliohms = milliohms };

    public static RailTarget OfMask(IEnumerable<RailMaskPoint> points) =>
        new() { Kind = RailTargetKind.Mask, Mask = [.. points] };

    public static RailTarget OfTransient(double deltaIAmps, double deltaVVolts, double riseTimeSeconds) =>
        new() { Kind = RailTargetKind.Transient,
                Transient = new RailTransientSpec(deltaIAmps, deltaVVolts, riseTimeSeconds) };

    /// <summary>
    /// The flat impedance target in OHMS this target implies, or null where it implies none. A
    /// <see cref="RailTargetKind.FlatImpedance"/> states it; a <see cref="RailTargetKind.Transient"/>
    /// DERIVES it; a mask has a limit per frequency rather than one number, and a drop budget is the
    /// DC question.
    /// </summary>
    public double? FlatTargetOhms => Kind switch
    {
        RailTargetKind.FlatImpedance => FlatMilliohms is { } m ? m * 1e-3 : null,
        RailTargetKind.Transient     => Transient?.FlatTargetOhms,
        _                            => null,
    };

    /// <summary>The top of the band this target implies, in HERTZ, or null where it implies none.
    /// Only a transient does (§2.2) — the band is otherwise the rail's own.</summary>
    public double? BandTopHz => Kind == RailTargetKind.Transient ? Transient?.BandTopHz : null;

    /// <summary>
    /// Null when exactly the payload <see cref="Kind"/> names is set, or the refusal sentence.
    /// <paramref name="allowed"/> is what the SLOT accepts — a rail's DC slot takes a drop budget
    /// and nothing else — so a mask written into it is named rather than silently carried into a
    /// solve that would ignore it.
    /// </summary>
    public string? Refusal(string where, params RailTargetKind[] allowed)
    {
        if (allowed.Length > 0 && Array.IndexOf(allowed, Kind) < 0)
            return $"{where} holds a {Name(Kind)} target, which does not belong there — that slot " +
                   $"takes {string.Join(" or ", allowed.Select(Name))}.";

        int stated = (DropBudgetMillivolts is not null ? 1 : 0)
                   + (FlatMilliohms        is not null ? 1 : 0)
                   + (Mask                 is not null ? 1 : 0)
                   + (Transient            is not null ? 1 : 0);

        if (stated != 1)
            return $"{where} is a {Name(Kind)} target stating {stated} values. A target carries " +
                   "exactly the one its kind names.";

        bool matched = Kind switch
        {
            RailTargetKind.DropBudget    => DropBudgetMillivolts is not null,
            RailTargetKind.FlatImpedance => FlatMilliohms        is not null,
            RailTargetKind.Mask          => Mask                 is not null,
            RailTargetKind.Transient     => Transient            is not null,
            _                            => false,
        };
        if (!matched)
            return $"{where} calls itself a {Name(Kind)} target but states a different quantity.";

        if (Kind == RailTargetKind.Mask && Mask!.Count < 2)
            return $"{where}'s mask has {Mask.Count} point(s). A piecewise mask needs at least two " +
                   "so it spans a band.";

        return Transient?.Refusal(where);
    }

    /// <summary>
    /// <b>Value equality, including the mask's POINTS.</b>
    ///
    /// <para>A record's generated <c>Equals</c> compares each member with its own — and
    /// <see cref="Mask"/> is an <see cref="IReadOnlyList{T}"/>, whose own is REFERENCE equality. So
    /// two targets read from the same bytes would compare unequal, silently, and the round trip that
    /// is supposed to prove the format carries everything would prove nothing. Worse downstream:
    /// brief 16 compares two documents for a living, and "these two masks differ" is exactly the kind
    /// of false finding a comparison tool must not produce.</para>
    /// </summary>
    public bool Equals(RailTarget? other) =>
        other is not null
        && Kind                 == other.Kind
        && DropBudgetMillivolts == other.DropBudgetMillivolts
        && FlatMilliohms        == other.FlatMilliohms
        && Transient            == other.Transient
        && (ReferenceEquals(Mask, other.Mask)
            || (Mask is not null && other.Mask is not null && Mask.SequenceEqual(other.Mask)));

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(Kind);
        h.Add(DropBudgetMillivolts);
        h.Add(FlatMilliohms);
        h.Add(Transient);
        foreach (var p in Mask ?? []) h.Add(p);
        return h.ToHashCode();
    }

    /// <summary>The wire spelling, and what a refusal calls it.</summary>
    public static string Name(RailTargetKind k) => k switch
    {
        RailTargetKind.DropBudget    => "drop-budget",
        RailTargetKind.FlatImpedance => "flat-impedance",
        RailTargetKind.Mask          => "mask",
        RailTargetKind.Transient     => "transient",
        _                            => "unknown",
    };
}
