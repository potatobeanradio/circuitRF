namespace CircuitRF.Design.Layout;

/// <summary>
/// brief-sdl-2 D1: which way each of a footprint's pads faces — the direction a connection leaves the part — by
/// ONE rule, used by every reader that builds a footprint, by <see cref="ComponentImport"/>, and by placement.
///
/// <para><b>The rule.</b> A pad faces away from the centroid of all of the part's pads, in the frame its stored
/// direction gives it: of the stored direction turned by 0°, 90°, 180° and 270°, the one nearest the direction away
/// from the centroid. A pad stored at a multiple of 90° therefore gets exactly the nearest 90° to "away from the
/// centroid", which is what <see cref="ComponentImport"/> has always written; a pad stored at 30° stays in its 30°
/// frame. Readers that know only a pad's long AXIS (the board reader, both library readers) store a direction whose
/// sign — and, for an 0603 whose pads are taller than they are wide, whose axis — can be wrong; this corrects both.</para>
///
/// <para><b>A part whose pads all sit at their centroid</b> (one pad, or pads stacked on one point) has no "away":
/// every pad keeps its stored direction, and <see cref="Result.Degenerate"/> says so, so the caller can report it.</para>
///
/// <para>Files written before this rule keep the direction they were written with, so placement applies it too
/// rather than trusting the stored value.</para>
/// </summary>
public static class PadFacing
{
    /// <summary>Each pin's effective direction (degrees, [0, 360)), in the order given, and whether the part had no
    /// centroid to face away from.</summary>
    public sealed record Result(IReadOnlyList<double> Deg, bool Degenerate);

    /// <summary>D1's rule over a part's pins — every pin of the footprint, since the centroid is of all its pads.</summary>
    public static Result Effective(IReadOnlyList<LayoutPin> cellPins)
    {
        var deg = new double[cellPins.Count];
        if (cellPins.Count == 0) return new Result(deg, true);

        double cx = 0, cy = 0;
        foreach (var p in cellPins) { cx += p.X; cy += p.Y; }
        cx /= cellPins.Count;
        cy /= cellPins.Count;

        bool degenerate = true;
        for (int i = 0; i < cellPins.Count; i++)
        {
            var pin = cellPins[i];
            double dx = pin.X - cx, dy = pin.Y - cy;
            if (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9) { deg[i] = LayoutAngle.Normalize(pin.OutwardDeg); continue; }
            degenerate = false;
            deg[i] = Away(pin.OutwardDeg, Math.Atan2(dy, dx) * 180.0 / Math.PI);
        }
        // A part with SOME pads off the centroid is not degenerate; one at the centroid (a three-pad part's middle pad
        // exactly between the other two) keeps its stored direction without the part being reported.
        return new Result(deg, degenerate);
    }

    /// <summary>The effective direction of <paramref name="pin"/>, one of <paramref name="cellPins"/> (by reference,
    /// else by name); its stored direction when it is not among them.</summary>
    public static double Of(IReadOnlyList<LayoutPin> cellPins, LayoutPin pin)
    {
        int at = -1;
        for (int i = 0; i < cellPins.Count && at < 0; i++) if (ReferenceEquals(cellPins[i], pin)) at = i;
        for (int i = 0; i < cellPins.Count && at < 0; i++)
            if (cellPins[i].Name == pin.Name && cellPins[i].X == pin.X && cellPins[i].Y == pin.Y) at = i;
        return at < 0 ? pin.OutwardDeg : Effective(cellPins).Deg[at];
    }

    /// <summary>Writes D1's direction into every pin of <paramref name="cellPins"/> — what a reader calls once it has
    /// read all of a footprint's pads, so the file it produces stores the right sign.</summary>
    public static bool Apply(IReadOnlyList<LayoutPin> cellPins)
    {
        var result = Effective(cellPins);
        for (int i = 0; i < cellPins.Count; i++) cellPins[i].OutwardDeg = result.Deg[i];
        return result.Degenerate;
    }

    /// <summary>The stored direction turned by the multiple of 90° that brings it nearest <paramref name="awayDeg"/>.
    /// Computed in the stored direction's own frame offset, so a stored multiple of 90° rounds exactly as the nearest
    /// 90° to <paramref name="awayDeg"/> (half-way rounds away from zero).</summary>
    private static double Away(double storedDeg, double awayDeg)
    {
        double offset = LayoutAngle.Normalize(storedDeg) % 90.0;
        if (90.0 - offset < 1e-9) offset = 0;
        double rel = awayDeg - offset;
        rel = LayoutAngle.Normalize(rel + 180.0) - 180.0;   // (−180, 180]
        if (rel <= -180.0) rel += 360.0;
        return LayoutAngle.Normalize(offset + Math.Round(rel / 90.0, MidpointRounding.AwayFromZero) * 90.0);
    }
}
