namespace CircuitRF.Design.RailRf;

/// <summary>
/// One branch drawing from a rail (railrf.md §2.2, "The loads and their currents").
///
/// <para><b>A list with add and remove, and a load is a pad plus a current.</b> A load port is an
/// IC's power/ground pin field — a set of pads, not a point — and railRF ties them into one port,
/// because that is what the die sees. A regulator's INPUT pin field is a load like any other, and it
/// is the row that links two rails: the same refdes appearing as a <see cref="RailSource"/> on
/// another rail is what <see cref="RailOrder"/> reads, and nothing else in the model links
/// them.</para>
/// </summary>
public sealed record RailLoad
{
    /// <summary>Where it sits. A refdes and a pin wherever one can be had — see <see cref="RailPortAnchor"/>.</summary>
    public RailPortAnchor Anchor { get; init; } = new();

    /// <summary>
    /// The DC current this port draws, in AMPS. <b>Null means this is not a load</b> — it is an
    /// OBSERVATION port over frequency and contributes nothing to the DC solve (§2.2, Q-16).
    ///
    /// <para><b>Never zero-by-default:</b> a defaulted zero and a stated zero are the same number and
    /// mean different things, and the DC report has to list an observed port AS observed rather than
    /// omitting it. Nothing in a BOM or a placement file carries a current, so this is typed — which
    /// is exactly why an untyped one must not read as a number somebody chose.</para></summary>
    public double? DcCurrentA { get; init; }

    /// <summary>Optional peak current, in AMPS, which the transient form of the target consumes.</summary>
    public double? PeakCurrentA { get; init; }

    /// <summary>
    /// This port's own impedance mask (§2.2's target list). <b>Per observation port</b>, which is why
    /// it lives on the load row rather than on the rail — one board's ICs do not share one limit.
    /// Null where this port is judged against the rail's own flat target instead.
    /// </summary>
    public RailTarget? Mask { get; init; }

    /// <summary>
    /// The regulator's own input current at the operating point being judged, in AMPS. Null on an
    /// ordinary load, which states its current in <see cref="DcCurrentA"/> like any other (Q-20).
    /// </summary>
    public double? RegulatorInputCurrentA { get; init; }

    /// <summary>
    /// The minimum input voltage this part needs to regulate, in VOLTS.
    ///
    /// <para><b>Null is not zero and not a default.</b> Without it railRF can report the input rail's
    /// drop and cannot report that the drop BROKE the rail downstream — which is the finding the
    /// whole chain exists to produce (§2.2). Brief 5 reports the drop always and the headroom finding
    /// only where this is stated, and says on the report which rails had no minimum.</para></summary>
    public double? MinimumInputVoltageV { get; init; }

    /// <summary>
    /// Which side of the rail's series element this port sits on, <b>where nothing measured it</b>
    /// (brief 25, R-rail25-2d).
    ///
    /// <para><b>Read only on a rail with no artwork</b>, and only on a rail that HAS a series
    /// element. With artwork, <see cref="RailSeriesPartition"/> cuts the rail at the element's two
    /// pads and this port lands on a side by where its own pads are — a measurement off the board
    /// in the same currency as the mounting inductances, which follows a re-layout by itself.
    /// Defaulted to <see cref="RailSection.Downstream"/>, which is the side a load is on.</para>
    /// </summary>
    public RailSection Side { get; init; } = RailSection.Downstream;

    /// <summary>
    /// The refdes of the series element this port sits directly behind, where nothing measured it —
    /// or null, where <see cref="Side"/> says which end of the chain (brief 35, R-rail35-3c).
    /// <see cref="RailPart.Behind"/> states the rule and why a section is named by its element.
    /// </summary>
    public string? Behind { get; init; }

    /// <summary>True when this row states no current: an observation port, and nothing else.</summary>
    public bool IsObservationOnly => DcCurrentA is null;

    /// <summary>Null when this row is usable, or the refusal sentence naming the rail and the row.</summary>
    public string? Refusal(string where, RailLengthFormat? format = null)
    {
        if (Anchor.Refusal(where, format) is { } anchor) return anchor;

        if (DcCurrentA is { } i && double.IsNaN(i))
            return $"{where} states a DC current that is not a number. Leave it out to make this an " +
                   "observation port, or state amps.";

        if (PeakCurrentA is { } p && double.IsNaN(p))
            return $"{where} states a peak current that is not a number.";

        if (Behind is { Length: > 0 } behind && Side == RailSection.Upstream)
            return $"{where} is stated both UPSTREAM and behind '{behind}'. Upstream is the " +
                   "source's own section, in front of every element; name the element, or state " +
                   "upstream, not both.";

        return Mask?.Refusal($"{where}'s mask", RailTargetKind.Mask);
    }
}
