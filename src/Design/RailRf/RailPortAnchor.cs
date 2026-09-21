namespace CircuitRF.Design.RailRf;

/// <summary>
/// Where a source or a load sits on the board (railrf.md §2.2).
///
/// <para><b>A refdes and a pin, wherever one can be had.</b> A coordinate is accepted where there is
/// no placement file and no board netlist, and it is the FALLBACK rather than the spelling: a pad
/// moves when the board is re-laid out and a coordinate does not, which is exactly what brief 16's
/// comparison would get wrong on the one input it cannot check — it pairs two designs by refdes and
/// pin, so a document whose ports are coordinates silently points at different copper after a
/// re-layout and the comparison then compares nothing while looking entirely normal.</para>
///
/// <para><b>Both forms are representable and exactly one is set.</b>
/// <see cref="RailDocumentIo"/> refuses a document carrying both or neither, naming the rail and the
/// row — not because it is malformed JSON but because a silently preferred one of the two is the bug
/// this record exists to prevent.</para>
/// </summary>
public sealed record RailPortAnchor
{
    /// <summary>The component reference — <c>U1</c>, <c>BT1</c>. Null only on a coordinate anchor.</summary>
    public string? Refdes { get; init; }

    /// <summary>The pin, as the netlist or the footprint names it — <c>VDD</c>, <c>1</c>. A pin FIELD
    /// (an IC's whole set of power pins) is named by its net at that refdes, and resolves to every
    /// matching pad.
    ///
    /// <para><b>The resolution to a SET of pads is brief 3's extractor, against the netlist and the
    /// placement — not this record's.</b> What is owned here is that the anchor is CAPABLE of naming
    /// a field: <c>U1.VDD</c> where <c>VDD</c> reaches six pads resolves to six pads, and a
    /// single-pin spelling resolves to one. railRF ties a field into one port, because that is what
    /// the die sees (§2.2), and a record that could only ever name one pad would make the note's own
    /// worked example unrepresentable.</para></summary>
    public string? Pin { get; init; }

    /// <summary>The fallback, in DBU on the artwork's own coordinate system. Null on a pad anchor.</summary>
    public (long X, long Y)? Point { get; init; }

    /// <summary>True when this is the spelling — a refdes, with or without a pin — rather than the
    /// fallback.</summary>
    public bool IsPad => Refdes is not null;

    /// <summary>
    /// The refusal sentence for an anchor that is neither form or both, or null when it is exactly
    /// one. <paramref name="where"/> names the rail and the row, because a document with two loads
    /// and a source is a document where "an anchor is malformed" is not an answer anyone can act on.
    /// </summary>
    public string? Refusal(string where, RailLengthFormat? format = null)
    {
        bool pad   = Refdes is { Length: > 0 };
        bool point = Point is not null;
        var fmt = format ?? RailLengthFormat.Dbu;

        if (pad && point)
            return $"{where} states both a component pad ({Describe(format)}) and a coordinate " +
                   $"{fmt.Point(Point!.Value.X, Point.Value.Y)}. An anchor is one or the other: give " +
                   "the refdes and pin, or remove them and keep the coordinate.";

        if (!pad && !point)
            return $"{where} states neither a component pad nor a coordinate. Give a refdes (and a " +
                   "pin, where the part has more than one), or a coordinate where there is no " +
                   "placement file to name one.";

        // A coordinate anchor carrying a pin names a pin on no component — a fragment of the pad
        // spelling left behind, and the one shape that reads as a pad anchor without being one.
        if (point && Pin is { Length: > 0 })
            return $"{where} is a coordinate anchor but also names pin '{Pin}'. A pin belongs to a " +
                   "refdes; give the refdes too, or remove the pin.";

        return null;
    }

    /// <summary>
    /// How this anchor reads on a report row — <c>U1.VDD</c>, <c>BT1</c>, or the point.
    /// </summary>
    /// <param name="format">
    /// The artwork's own units, so a coordinate anchor reads as a place on the board rather than as a
    /// database integer (owner, 2026-09-18). Omitted where there is no artwork to take units from —
    /// the refusals in this file, which are about the FILE and are in the file's own unit — and the
    /// string then says DBU out loud rather than picking a unit nobody stated.
    /// </param>
    public string Describe(RailLengthFormat? format = null) =>
        IsPad
            ? (Pin is { Length: > 0 } p ? $"{Refdes}.{p}" : Refdes!)
            : Point is { } xy ? (format ?? RailLengthFormat.Dbu).Point(xy.X, xy.Y) : "(no anchor)";
}
