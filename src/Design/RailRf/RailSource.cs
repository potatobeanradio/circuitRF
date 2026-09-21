namespace CircuitRF.Design.RailRf;

/// <summary>
/// One branch feeding a rail (railrf.md §2.2, "The sources").
///
/// <para><b>More than one per rail, and each one sits on a pad.</b> A rail can be fed by a cell
/// through its connector, by a regulator's output pin field, or by both on a design that runs from
/// either — so the sources are a LIST with add and remove, not a field. Two sources on one rail are
/// two branches in the same mesh and nothing in the solve is special-cased for them: the geometry is
/// what decides how they share, which is a large part of why this is a board tool rather than a
/// spreadsheet.</para>
///
/// <para><b>A regulator's output row is an ordinary source row.</b> Nothing here says it is a
/// regulator — what makes it one is that the same refdes also appears as a <see cref="RailLoad"/> on
/// another rail, which is the only link between the two and the whole of what
/// <see cref="RailOrder"/> reads (§2.2).</para>
/// </summary>
public sealed record RailSource
{
    /// <summary>Where it sits. A refdes and a pin wherever one can be had — see <see cref="RailPortAnchor"/>.</summary>
    public RailPortAnchor Anchor { get; init; } = new();

    /// <summary>
    /// The open-circuit voltage this branch holds the rail at, in VOLTS — a cell's terminal voltage,
    /// a regulator's regulated output.
    ///
    /// <para><b>Null is not zero.</b> A source with no stated voltage is a branch whose impedance is
    /// known and whose level is not: it is an impedance the frequency answer can use and the DC
    /// answer cannot, and brief 5 reports that rather than solving against a defaulted rail
    /// voltage.</para></summary>
    public double? OpenCircuitVoltageV { get; init; }

    /// <summary>
    /// The series resistance of the source model, in OHMS. A battery is the default model — a series
    /// R-L whose R is swept across the cell's life (§8.2) — and a converter or an LDO is the same R-L
    /// unless its published output-impedance curve was supplied.
    /// </summary>
    public double? SeriesResistanceOhms { get; init; }

    /// <summary>The series inductance of the same model, in HENRIES.</summary>
    public double? SeriesInductanceHenries { get; init; }

    /// <summary>
    /// The source's own measured output impedance, as a Touchstone file — <b>relative to the
    /// <c>.crail</c></b>, on the portability rule every other circuitRF document reference follows.
    ///
    /// <para>Where one is given it is the model, and the R-L fields are then the bug this refuses
    /// rather than a fallback: a silently preferred one of two stated models is exactly the failure
    /// <see cref="RailPortAnchor"/> exists to prevent, arriving on a different row. Where one is NOT
    /// given, railRF says the sub-MHz answer is optimistic near the loop crossover rather than
    /// pretending an R-L is the whole story (§2.2) — brief 5 and brief 12 report that; brief 1 is
    /// what makes the absence representable.</para></summary>
    public string? TouchstoneRef { get; init; }

    /// <summary>True when this row carries the R-L form rather than a Touchstone file.</summary>
    public bool IsRl => SeriesResistanceOhms is not null || SeriesInductanceHenries is not null;

    /// <summary>Null when this row is usable, or the refusal sentence naming the rail and the row.</summary>
    public string? Refusal(string where, RailLengthFormat? format = null)
    {
        if (Anchor.Refusal(where, format) is { } anchor) return anchor;

        if (TouchstoneRef is { Length: > 0 } && IsRl)
            return $"{where} states both a series R-L and a Touchstone file ('{TouchstoneRef}'). " +
                   "A source has one model: keep the measured file, or remove it and keep the R-L.";

        if (SeriesResistanceOhms is { } r && (r < 0 || double.IsNaN(r)))
            return $"{where} states a series resistance of {r} Ω.";

        if (SeriesInductanceHenries is { } l && (l < 0 || double.IsNaN(l)))
            return $"{where} states a series inductance of {l} H.";

        return null;
    }
}
