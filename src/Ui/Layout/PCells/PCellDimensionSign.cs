namespace CircuitRF.Ui.Layout.PCells;

/// <summary>
/// Which of a built-in generator's parameters are dimensions that cannot meaningfully be negative,
/// and what a negative one becomes when a drag lets go of it.
///
/// <para><b>Owner report, 2026-08-12:</b> "for MCROSS, after the user has completed the geometry edit
/// using grippers, check for negative W or L and recalculate their values in the positive such that
/// the exact same geometry is rendered. Similar story for MTEE; I was also able to get a negative L
/// value using grippers which causes the MTEE render to glitch out." Followed by: "a negative value
/// is OK during drag for MCROSS and MTEE, just not after mouse up."</para>
///
/// <para><b>Widths and lengths are handled differently, on the owner's own call</b> ("clamp the L
/// parameters so they never go negative during a drag; keep W untouched, I like how they are currently
/// working"):
/// <list type="bullet">
/// <item><b>A width is free during the drag and normalised here.</b> It recovers exactly (below), so
/// there is nothing to be gained by stopping the grip.</item>
/// <item><b>A length is BOUNDED during the drag</b> — each junction generator declares <c>Min</c> on
/// its own length axis, so <c>PCellHandleSolver.Propose</c> clamps every candidate and the grip stops.
/// This class is then the second half rather than the only one: <c>Propose</c> applies the snap
/// lattice AFTER the bound, so a coarse snap step can still round a clamped value back down, and
/// nothing outside the solver goes through <c>Propose</c> at all.</item>
/// </list></para>
///
/// <para><b>Why the bound lives on the HANDLE and never inside the generator.</b>
/// <see cref="PCellHandleSolver"/> measures a grip's sensitivity ONCE, at the drag's starting value,
/// and <c>Propose</c> clamps only the candidate it derives from it — so the map it measured stays
/// intact and the grip stops cleanly at the floor. Clamping inside the generator instead would flatten
/// that map below the floor, leaving the solver nothing to measure and the grip refusing to follow the
/// cursor at all. Each generator's own <c>Min</c> is therefore set to exactly the minimum its
/// crossing-width clamp already enforces, derived from the same integer, so the two cannot disagree
/// about where the grip should stop.</para>
///
/// <para><b>What "the same geometry" means, honestly, for each kind.</b>
/// <list type="bullet">
/// <item><b>A width: exactly.</b> <c>PCellGeometryHelpers.BuildArmRect</c> spans
/// <c>origin ± width/2</c>, so a negative width names the same two coordinates in the other order.
/// Taking the magnitude reproduces the identical rectangle — and fixes its winding, which matters
/// because a backwards ring cancels against its own layer instead of filling it (see
/// <c>LayoutRenderer</c>'s <c>NormalizeOuterWinding</c>).</item>
/// <item><b>An arm length: not exactly, and the difference is stated rather than glossed.</b> A
/// negative length draws that arm on the WRONG SIDE of the junction, on top of the arm that belongs
/// there — the reported glitch. No positive parameter set reproduces that, so nothing can. The
/// magnitude is used because it preserves the one thing the user did express — how far they dragged —
/// and snaps the arm to its own side. Both alternatives are worse: holding it at zero throws that
/// away, and falling back to the derived length silently substitutes a third, unrelated number.</item>
/// </list></para>
/// </summary>
internal static class PCellDimensionSign
{
    /// <summary>
    /// The generators whose W/L parameters are strictly positive dimensions, keyed by generator id.
    ///
    /// <para>A TABLE rather than a name-shaped rule, because a name is not enough on its own: MKlopf's
    /// <c>Offset</c> is a length whose sign is meaningful (off-axis either way), and MBend's
    /// <c>Angle</c> likewise. Adding a generator here is one line; leaving one out costs nothing but
    /// the fix.</para>
    ///
    /// <para>Scoped to the two junction cells the report names. MLIN, MTaper and MBend carry the same
    /// latent issue — their own width grips can be dragged past their anchor edge — and are
    /// deliberately not widened into here without being asked; see this file's own entry in
    /// <c>src/Ui/CLAUDE.md</c>.</para>
    /// </summary>
    private static readonly Dictionary<string, string[]> PositiveDimensions = new(StringComparer.Ordinal)
    {
        [MTeePCell.GeneratorId]   = ["W1", "W2", "W3", "L1", "L2", "L3"],
        [MCrossPCell.GeneratorId] = ["W1", "W2", "W3", "W4", "L1", "L2", "L3", "L4"],
    };

    /// <summary>True when a negative value for this parameter is a drag overshoot to normalise rather
    /// than a number the cell means.</summary>
    internal static bool IsPositiveDimension(string generatorId, string parameterName)
        => PositiveDimensions.TryGetValue(generatorId, out var names)
           && Array.IndexOf(names, parameterName) >= 0;

    /// <summary>
    /// The value to commit for one parameter: unchanged unless it is a strictly-positive dimension
    /// that came back negative, in which case its own magnitude.
    ///
    /// <para>Zero is deliberately left alone. There is no magnitude to recover from it, it is a value
    /// a drag can legitimately pass through and stop on, and a zero-length arm still draws a valid
    /// junction from the arms that remain — unlike a negative one, which draws over them.</para>
    /// </summary>
    internal static PCellValue Normalize(string generatorId, string parameterName, PCellValue value)
    {
        if (!IsPositiveDimension(generatorId, parameterName)) return value;
        if (value.Kind != PCellValueKind.Real) return value;

        double v = value.AsReal(0.0);
        return double.IsFinite(v) && v < 0 ? PCellValue.Real(-v) : value;
    }
}
