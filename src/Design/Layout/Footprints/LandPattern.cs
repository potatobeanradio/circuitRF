// IPC-7351B land geometry from a case plus a density level — R-fp1-2.
//
// COMPUTED, never tabulated. 23 cases x 3 levels is 69 sets of numbers nobody will ever re-check,
// where the formula is one function with a test per level. What IS tabulated is the thing IPC
// tabulates: the three fillet goals per density level.
//
// Everything is decimal millimetres until the last step, which converts to DBU through
// LayoutUnits.ToDbu — the one rounding rule in the codebase (round-half-away-from-zero, computed in
// decimal), so a 0.125 mm edge lands on the same DBU on every machine. That is the PCell contract's
// R5 determinism obligation, not a nicety: PCellGeometryCache keys on the inputs, so a generator
// that answers differently for one key hands the second caller the first one's geometry in silence.

namespace CircuitRF.Design.Layout.Footprints;

/// <summary>
/// IPC-7351B's three density levels — the same body at three land protrusions. Spelled
/// <c>M</c>/<c>N</c>/<c>L</c> in a footprint reference (R-fp1-5) and <c>-M</c>/<c>""</c>/<c>-L</c>
/// as a cell-view suffix, which is the spelling <c>ComponentImport</c> already uses for an IMPORTED
/// part's density variants (R-fp1-2c). One vocabulary, two renderings of it.
/// </summary>
public enum DensityLevel
{
    /// <summary>Maximum land protrusion — wave solder, hand rework, high reliability.</summary>
    Most,

    /// <summary>The default. General commercial reflow.</summary>
    Nominal,

    /// <summary>Minimum land — high density, fine pitch.</summary>
    Least,
}

/// <summary>
/// One land pattern, in DBU at the view's own resolution (R-fp1-2a).
///
/// <para><b>The two pad dimensions are named for the DRAWING, not for IPC's letters.</b> A pattern
/// is generated with its termination axis along +X, so <see cref="PadWidthDbu"/> is the pad's extent
/// along X — IPC's <c>Y</c>, the toe-to-heel length — and <see cref="PadHeightDbu"/> is its extent
/// along Y, IPC's <c>X</c>. Two conventions for one rectangle is one too many, and the drawing is
/// the one a reader can check against the picture.</para>
/// </summary>
/// <param name="SpanDbu">IPC's <c>Z</c>: outside of one land to outside of the other.</param>
/// <param name="GapDbu">IPC's <c>G</c>: inside to inside.</param>
/// <param name="PadPitchDbu">Land centre to land centre — <c>(Z + G) / 2</c>.</param>
/// <param name="CourtyardWidthDbu">The courtyard rectangle, centred on the origin like everything
/// else here.</param>
public sealed record LandPattern(
    SmtCase Case,
    DensityLevel Density,
    long PadWidthDbu,
    long PadHeightDbu,
    long PadPitchDbu,
    long SpanDbu,
    long GapDbu,
    long CourtyardWidthDbu,
    long CourtyardHeightDbu,
    int DbuPerMicron,
    bool GapWasClamped)
{
    /// <summary>Pad 1's centre X — negative. The pattern is centred on the ORIGIN; see
    /// <see cref="ChipLandPatternGenerator"/> for why that and not pin 1 at the origin.</summary>
    public long Pad1CentreXDbu => -(PadPitchDbu / 2);

    /// <summary>Pad 2's centre X. Exactly <see cref="Pad1CentreXDbu"/> mirrored, so the pattern is
    /// symmetric about x = 0 by construction — on an odd pitch that costs one DBU of spacing, which
    /// is a nanometre at the default resolution, and buys a symmetry the picker, the placement and
    /// the gate can all rely on.</summary>
    public long Pad2CentreXDbu => PadPitchDbu / 2;

    /// <summary>
    /// The land geometry for one case at one density (R-fp1-2a).
    /// </summary>
    public static LandPattern For(SmtCase c, DensityLevel density, int dbuPerMicron)
    {
        ArgumentNullException.ThrowIfNull(c);

        var goals = FilletGoals.For(c, density);

        // Worst-case material condition, which is what the fillet goals are stated against.
        decimal lMax = c.BodyLengthMm + c.BodyToleranceMm;
        decimal lMin = c.BodyLengthMm - c.BodyToleranceMm;
        decimal tMax = c.TerminationLengthMm + c.TerminationToleranceMm;
        decimal wMax = c.EffectiveTerminationWidthMm + c.BodyToleranceMm;

        // S — the separation between the two termination bands, at its minimum.
        decimal sMin = lMin - 2m * tMax;

        decimal z = lMax + 2m * goals.ToeMm;                 // outside to outside
        decimal g = sMin - 2m * goals.HeelMm;                // inside to inside
        decimal x = wMax + 2m * goals.SideMm;                // across the axis

        // IPC's statistical (RSS) fabrication-and-placement allowance is deliberately NOT applied.
        // It needs the BOARD's own fabrication tolerance and the assembler's placement accuracy, and
        // a Technology declares neither — inventing values for them would put two invented numbers
        // inside a square root and call the result a standard. What this returns is the geometric
        // land pattern; a fabrication allowance is a board-level setting and belongs with DRC.

        bool clamped = false;
        if (g < 0m) { g = 0m; clamped = true; }   // a negative gap is two pads merged into one land
        if (x < 0m) x = 0m;

        long span  = Mm(z, dbuPerMicron);
        long gap   = Mm(g, dbuPerMicron);
        long padW  = (span - gap) / 2;
        long padH  = Mm(x, dbuPerMicron);
        long pitch = (span + gap) / 2;

        // The courtyard encloses whichever is larger, the lands or the body, plus the level's own
        // excess. The body can be the larger one: every reverse-geometry case is wider than its
        // lands are long, and a courtyard that cleared only the copper would let a neighbour sit
        // under the part.
        decimal bodyW = c.BodyWidthMm + c.BodyToleranceMm;
        decimal courtX = Math.Max(z, lMax) + 2m * goals.CourtyardMm;
        decimal courtY = Math.Max(x, bodyW) + 2m * goals.CourtyardMm;

        return new LandPattern(
            c, density, padW, padH, pitch, span, gap,
            Mm(courtX, dbuPerMicron), Mm(courtY, dbuPerMicron),
            dbuPerMicron, clamped);
    }

    internal static long Mm(decimal mm, int dbuPerMicron) => LayoutUnits.ToDbu(mm, LayoutUnit.Mm, dbuPerMicron);
}

/// <summary>
/// IPC-7351B's three fillet goals plus the courtyard excess, for one case at one density.
/// </summary>
internal readonly record struct FilletGoals(decimal ToeMm, decimal HeelMm, decimal SideMm, decimal CourtyardMm)
{
    /// <summary>
    /// IPC-7351B splits chip components at metric 1608 (imperial 0603) and gives the smaller ones
    /// their own, much reduced goals. Without that split an 008004 would get a 0.35 mm toe on a
    /// 0.25 mm body — a land pattern almost four times the part, which places, renders, exports and
    /// is wrong.
    ///
    /// <para><b>The split is on the body's TERMINATION-AXIS length alone</b>, not on its area. The
    /// toe and heel goals are distances along that axis, and a reverse-geometry 0306 — a physically
    /// large part that is only 0.8 mm long — needs the short-axis goals for exactly the reason the
    /// split exists.</para>
    /// </summary>
    public static FilletGoals For(SmtCase c, DensityLevel density)
    {
        bool small = c.BodyLengthMm < 1.6m;
        return (small, density) switch
        {
            //                                    toe     heel    side   courtyard
            (false, DensityLevel.Most)    => new(0.55m,  0.05m,  0.05m,  0.50m),
            (false, DensityLevel.Nominal) => new(0.35m,  0.00m,  0.00m,  0.25m),
            (false, DensityLevel.Least)   => new(0.15m,  0.00m, -0.05m,  0.12m),

            // The sub-1608 set. The toe goals are IPC's; the heel, side and courtyard figures are
            // scaled with them, because IPC's own large-chip numbers do not survive the arithmetic
            // at this size:
            //   - a 0.05 mm HEEL on an 008004 eats the whole 0.075 mm separation its termination
            //     bands leave and merges the two lands into one, which is not a dense land pattern,
            //     it is a short;
            //   - a -0.05 mm SIDE reduction on a body 0.125 mm wide leaves a 40 um land;
            //   - a 0.5 mm courtyard excess around a 0.25 mm part is four times the part.
            // The toe alone therefore carries the density difference at this size, which is what it
            // is for: it is the fillet a small chip can actually form.
            (true,  DensityLevel.Most)    => new(0.20m,  0.00m,  0.02m,  0.15m),
            (true,  DensityLevel.Nominal) => new(0.15m,  0.00m,  0.00m,  0.10m),
            (true,  DensityLevel.Least)   => new(0.10m,  0.00m, -0.02m,  0.05m),

            _ => throw new ArgumentOutOfRangeException(nameof(density), density, null),
        };
    }
}
