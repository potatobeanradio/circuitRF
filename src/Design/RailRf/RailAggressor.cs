namespace CircuitRF.Design.RailRf;

/// <summary>Where an aggressor row came from. Shown on the row — see <see cref="RailAggressor.Origin"/>.</summary>
public enum RailAggressorOrigin
{
    /// <summary>Someone typed it. The default, because nothing else can be assumed about a row.</summary>
    Typed,

    /// <summary>Pre-filled from the BOM, where a crystal or a converter could be recognised (brief 2).</summary>
    Bom,
}

/// <summary>
/// One thing on this board that excites the PDN — a 32 kHz crystal, the converter's switching
/// frequency, an RF crystal.
///
/// <para><b>An input, not a decoration</b> (railrf.md §2.2, last paragraph): <i>a PDN peak matters if
/// something on the board excites it, and this is the only input that knows whether it does.</i> The
/// list drives both the plot markers and the coincidence check of §2.4.</para>
///
/// <para>Brief 2's BOM reader pre-fills rows where a part can be recognised and brief 12 draws them
/// and runs the check. Brief 1 holds them, and holds <b>where each came from</b>.</para>
/// </summary>
/// <param name="Name">What it is, as the row reads — <c>32 kHz xtal</c>, <c>converter</c>.</param>
/// <param name="FrequencyHz">The fundamental, in HERTZ. Base SI, like every other frequency in this
/// document: a mark read without its scale once produced a run at 2 Hz that looked entirely normal
/// (src/Engine/RESOLVED.md).</param>
/// <param name="Harmonics">How many harmonics of it to draw and check. 1 is the fundamental alone.</param>
public sealed record RailAggressor(string Name, double FrequencyHz, int Harmonics)
{
    /// <summary>Where this row came from — recognised from the BOM, or typed. Shown on the row,
    /// because a pre-filled frequency a user did not check is exactly the one that will be wrong.</summary>
    public RailAggressorOrigin Origin { get; init; } = RailAggressorOrigin.Typed;

    /// <summary>Null when this row can be drawn and checked, or the sentence saying why not.</summary>
    public string? Refusal(string where)
    {
        if (string.IsNullOrWhiteSpace(Name))
            return $"{where} has an aggressor with no name. Every row is named, because the " +
                   "coincidence check reports which one landed on a peak.";
        if (!(FrequencyHz > 0) || double.IsInfinity(FrequencyHz))
            return $"{where}'s aggressor '{Name}' states {FrequencyHz} Hz. State its fundamental in " +
                   "hertz.";
        if (Harmonics < 1)
            return $"{where}'s aggressor '{Name}' states {Harmonics} harmonics. 1 is the fundamental " +
                   "alone, which is the fewest a row can mean.";
        return null;
    }
}
