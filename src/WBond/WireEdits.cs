namespace CircuitRF.WBond;

/// <summary>
/// Every geometric edit the wBond editor performs, as pure functions on the model
/// (wbond.md §6.2.1 and §6.4; brief-wbond-wbc R-wbc-2/3/4).
///
/// <para><b>None of this needs a pixel</b>, and that is deliberate: these are the rules that can be
/// wrong, so they are tested against arithmetic rather than through a canvas (brief §0.2).</para>
/// </summary>
public static class WireEdits
{
    // ---------------------------------------------------------------- alt-drag (§6.2.1)

    /// <summary>
    /// <b>Alt + vertical drag — scale loop height about the CHORD (WB24a / D2).</b>
    ///
    /// <para>Height is measured above the straight 3D line joining the two feet, so both endpoints
    /// have height exactly 0 and the scale factor multiplies them by zero. <b>The feet therefore
    /// cannot move, bit-exactly</b> — which is the whole reason for the chord-relative formulation
    /// and not merely a convenience.</para>
    ///
    /// <para>Scaling about a flat baseline instead would drag one foot off its pad in the case that
    /// motivates chip-and-wire in the first place: the two feet at <i>different</i> z, die surface to
    /// package lead.</para>
    /// </summary>
    public static void ScaleHeightAboutChord(Wire wire, double factor)
    {
        ArgumentNullException.ThrowIfNull(wire);
        if (!double.IsFinite(factor) || factor <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(factor), factor, "Height scale must be positive and finite.");
        if (wire.Points.Count < 3) return;   // two feet and nothing between them: nothing to scale

        var start = wire.Points[0];
        var end = wire.Points[^1];

        for (int i = 1; i < wire.Points.Count - 1; i++)
        {
            var p = wire.Points[i];
            double s = ChordParameter(start, end, p);

            long chordZ = start.Z + (long)Math.Round((end.Z - start.Z) * s);
            long height = p.Z - chordZ;

            wire.Points[i] = p with { Z = chordZ + (long)Math.Round(height * factor) };
        }

        // Untouched by construction, and restated so the invariant is visible at the call site.
        wire.Points[0] = start;
        wire.Points[^1] = end;
    }

    /// <summary>
    /// <b>Alt + horizontal drag — scale span, holding loop height absolute (WB24b / D3).</b>
    ///
    /// <para>The foot on the dragged side moves along the chord; the other is pinned; interior points
    /// keep their normalised span position <i>and</i> their absolute height above the chord.</para>
    ///
    /// <para>Height is deliberately NOT scaled with span: a bonder running the same loop program over
    /// a longer span does not raise the loop proportionally. <see cref="ScaleSimilarity"/> is the
    /// alt+shift gesture for the cases where true similarity is wanted.</para>
    /// </summary>
    /// <param name="moveOutputFoot">
    /// True to move <c>Points[^1]</c> and pin <c>Points[0]</c>; false for the reverse.
    /// </param>
    public static void ScaleSpan(Wire wire, double factor, bool moveOutputFoot = true)
    {
        ArgumentNullException.ThrowIfNull(wire);
        if (!double.IsFinite(factor) || factor <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(factor), factor, "Span scale must be positive and finite.");
        if (wire.Points.Count < 2) return;

        var start = wire.Points[0];
        var end = wire.Points[^1];

        // Heights above the chord, captured BEFORE the feet move — they are what must survive.
        int n = wire.Points.Count;
        var spans = new double[n];
        var heights = new long[n];
        for (int i = 0; i < n; i++)
        {
            spans[i] = ChordParameter(start, end, wire.Points[i]);
            long chordZ = start.Z + (long)Math.Round((end.Z - start.Z) * spans[i]);
            heights[i] = wire.Points[i].Z - chordZ;
        }

        Point3 newStart = start, newEnd = end;
        if (moveOutputFoot)
            newEnd = Lerp(start, end, factor);
        else
            newStart = Lerp(end, start, factor);

        for (int i = 1; i < n - 1; i++)
        {
            double s = spans[i];
            long x = newStart.X + (long)Math.Round((newEnd.X - newStart.X) * s);
            long y = newStart.Y + (long)Math.Round((newEnd.Y - newStart.Y) * s);
            long chordZ = newStart.Z + (long)Math.Round((newEnd.Z - newStart.Z) * s);
            wire.Points[i] = new Point3(x, y, chordZ + heights[i]);
        }

        wire.Points[0] = newStart;
        wire.Points[^1] = newEnd;
    }

    /// <summary>Alt+Shift — true similarity: span and height together.</summary>
    public static void ScaleSimilarity(Wire wire, double factor, bool moveOutputFoot = true)
    {
        ScaleSpan(wire, factor, moveOutputFoot);
        ScaleHeightAboutChord(wire, factor);
    }

    /// <summary>
    /// Scales every wire bound to <paramref name="profile"/> <b>by the same factor</b> (WB24c / D4).
    ///
    /// <para>By factor, never to a common value: an array whose wires deliberately have different
    /// spans — a fan-out from a common pad — keeps their ratios. Setting a common absolute span would
    /// silently destroy exactly the geometry the flexible model exists to allow.</para>
    /// </summary>
    /// <returns>How many wires were rescaled.</returns>
    public static int ScaleBoundWires(WBondDesign design, LoopProfile profile, double heightFactor,
                                      double spanFactor)
    {
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(profile);

        int moved = 0;
        foreach (var wire in design.AllWires())
        {
            if (!string.Equals(wire.ProfileBinding, profile.Name, StringComparison.OrdinalIgnoreCase))
                continue;

            if (spanFactor != 1.0) ScaleSpan(wire, spanFactor);
            if (heightFactor != 1.0) ScaleHeightAboutChord(wire, heightFactor);
            moved++;
        }

        if (heightFactor != 1.0) profile.ScaleHeight(heightFactor);
        return moved;
    }

    // ---------------------------------------------------------------- transforms (§6.4)

    /// <summary>
    /// <b>Rotate about an end point (WB26a / D6)</b> — the grabbed end swings, the opposite end is
    /// pinned, and the wire is carried rigidly between them.
    ///
    /// <para>The pivot is the end <i>further</i> from the grab, so the gesture needs no mode switch:
    /// grab near the end you want to move.</para>
    /// </summary>
    /// <param name="view">
    /// Layout rotates about the vertical (z) axis through the pinned foot — the fan-out gesture.
    /// Profile rotates in the view plane, which tilts the wire's rise.
    /// </param>
    public static void RotateAboutEndPoint(Wire wire, bool pivotOnInputFoot, double radians,
                                           EditorView view = EditorView.Layout)
    {
        ArgumentNullException.ThrowIfNull(wire);
        if (wire.Points.Count < 2) return;

        var pivot = pivotOnInputFoot ? wire.Points[0] : wire.Points[^1];
        double cos = Math.Cos(radians), sin = Math.Sin(radians);

        for (int i = 0; i < wire.Points.Count; i++)
        {
            var p = wire.Points[i];
            double dx = p.X - pivot.X, dy = p.Y - pivot.Y, dz = p.Z - pivot.Z;

            wire.Points[i] = view == EditorView.Layout
                ? new Point3(
                    pivot.X + (long)Math.Round(dx * cos - dy * sin),
                    pivot.Y + (long)Math.Round(dx * sin + dy * cos),
                    p.Z)
                : new Point3(
                    pivot.X + (long)Math.Round(dx * cos - dz * sin),
                    p.Y,
                    pivot.Z + (long)Math.Round(dx * sin + dz * cos));
        }

        // The pivot is fixed by definition; restate it so rounding cannot move it by a nanometre.
        if (pivotOnInputFoot) wire.Points[0] = pivot;
        else wire.Points[^1] = pivot;
    }

    /// <summary>
    /// Mirrors a wire about an axis-aligned plane.
    ///
    /// <para><paramref name="reverseTraversal"/> defaults to true because a mirrored wire's input
    /// should normally stay on the input side — and getting it wrong flips every mutual-inductance
    /// sign involving this wire (WB3). Surfaced as a checkbox for the same reason.</para>
    /// </summary>
    public static void Mirror(Wire wire, char axis, long about, bool reverseTraversal = true)
    {
        ArgumentNullException.ThrowIfNull(wire);

        for (int i = 0; i < wire.Points.Count; i++)
        {
            var p = wire.Points[i];
            wire.Points[i] = char.ToLowerInvariant(axis) switch
            {
                'x' => p with { X = 2 * about - p.X },
                'y' => p with { Y = 2 * about - p.Y },
                'z' => p with { Z = 2 * about - p.Z },
                _ => throw new ArgumentOutOfRangeException(nameof(axis), axis, "Mirror axis must be x, y or z."),
            };
        }

        if (reverseTraversal) wire.Reverse();
    }

    /// <summary>
    /// Displaces the interior points laterally, endpoints pinned. The displacement follows a half-sine
    /// so the bend is smooth and vanishes at both feet.
    /// </summary>
    public static void Bend(Wire wire, long dx, long dy, long dz)
    {
        ArgumentNullException.ThrowIfNull(wire);
        if (wire.Points.Count < 3) return;

        var start = wire.Points[0];
        var end = wire.Points[^1];

        for (int i = 1; i < wire.Points.Count - 1; i++)
        {
            double s = ChordParameter(start, end, wire.Points[i]);
            double shape = Math.Sin(Math.PI * s);
            var p = wire.Points[i];
            wire.Points[i] = new Point3(
                p.X + (long)Math.Round(dx * shape),
                p.Y + (long)Math.Round(dy * shape),
                p.Z + (long)Math.Round(dz * shape));
        }
    }

    /// <summary>
    /// Collapses the interior points onto the chord <b>in plan view only — z is untouched.</b>
    ///
    /// <para>This straightens the wire's ROUTE, not its loop. A wirebond's height profile is the
    /// thing the loop exists to be: flattening it as well would turn "tidy up a wire that wanders
    /// sideways" into "destroy the loop", which is a different operation and not one anyone reaches
    /// for by this name. The two are separable because span and height are independent in this model
    /// (§6.2) — the profile owns z, the route owns x and y.</para>
    ///
    /// <para><b>The point count is preserved</b>, so a profile can be re-applied afterwards and return
    /// the wire to exactly where it was — which is what makes this a reversible edit rather than a
    /// destructive one, and is why it does not go through a mesh rebuild.</para>
    /// </summary>
    public static void Straighten(Wire wire)
    {
        ArgumentNullException.ThrowIfNull(wire);
        if (wire.Points.Count < 3) return;

        var start = wire.Points[0];
        var end = wire.Points[^1];
        int n = wire.Points.Count;

        for (int i = 1; i < n - 1; i++)
        {
            double s = (double)i / (n - 1);
            var flat = Lerp(start, end, s);
            wire.Points[i] = flat with { Z = wire.Points[i].Z };
        }
    }

    /// <summary>
    /// Extends or shortens along the wire's own chord direction, from one end.
    /// A factor of 1 is a no-op; 1.2 makes it 20 % longer.
    /// </summary>
    public static void ExtendAlongAxis(Wire wire, double factor, bool fromOutputFoot = true) =>
        ScaleSpan(wire, factor, fromOutputFoot);

    /// <summary>
    /// <b>Duplicate with pitch (WB26 / §6.4)</b> — the array-authoring workhorse.
    ///
    /// <para><b>One call, N wires, one array assignment, one fill.</b> That is a performance
    /// requirement rather than a convenience: creating 200 wires as 200 separate operations is 200
    /// cold fills, which is the difference between usable and unusable at the stated worst case.</para>
    /// </summary>
    /// <param name="count">How many COPIES to make. The source is not counted.</param>
    public static IReadOnlyList<Wire> DuplicateWithPitch(
        WBondDesign design, Wire source, long pitchX, long pitchY, int count)
    {
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(source);
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count), count, "Duplicate count must be at least 1.");
        if (pitchX == 0 && pitchY == 0)
            throw new ArgumentException("Duplicate pitch is zero in both axes — the copies would land on the source.");

        var array = design.Arrays.FirstOrDefault(a => a.Wires.Contains(source))
            ?? throw new ArgumentException("The source wire does not belong to any array in this design.", nameof(source));

        var made = new List<Wire>(count);
        for (int k = 1; k <= count; k++)
        {
            var copy = new Wire
            {
                DiameterNm = source.DiameterNm,
                Material = source.Material,
                ProfileBinding = source.ProfileBinding,
                Locked = source.Locked,
            };

            foreach (var p in source.Points)
                copy.Points.Add(new Point3(p.X + pitchX * k, p.Y + pitchY * k, p.Z));

            made.Add(copy);
        }

        // One assignment, after the whole batch is built.
        array.Wires.AddRange(made);
        return made;
    }

    // ---------------------------------------------------------------- nudge (R-wbc-4 / D8)

    /// <summary>
    /// Moves a selection by one nudge step.
    ///
    /// <para>The step is a bonder-process quantity — 1 mil, or 5 mil with shift — and stays so
    /// <b>regardless of the display unit</b> (WB25). The view decides only which axis "up" means:
    /// +z in the profile view, +y in the layout view. That mapping is a parameter here rather than a
    /// branch inside the arithmetic.</para>
    /// </summary>
    public static void Nudge(WBondDesign design, WireSelection selection,
                             int dxSteps, int dyOrDzSteps, long stepNm, EditorView view) =>
        Translate(design, selection, dxSteps * stepNm, dyOrDzSteps * stepNm, view);

    /// <summary>
    /// Moves a selection by an arbitrary displacement in nanometres — the free-drag counterpart of
    /// <see cref="Nudge"/>, which is now expressed in terms of it.
    ///
    /// <para><b>One implementation, two callers.</b> A drag and a nudge differ only in where the
    /// displacement comes from; giving them separate arithmetic would be two chances to disagree
    /// about which points a selection actually moves — and <see cref="WireSelection.MovingPoints"/>,
    /// which resolves that, is exactly the rule that can be wrong.</para>
    ///
    /// <para><paramref name="dyOrDzNm"/> is +y in the layout view and +z in the profile view, the
    /// same mapping <see cref="Nudge"/> documents.</para>
    /// </summary>
    public static void Translate(WBondDesign design, WireSelection selection,
                                 long dxNm, long dyOrDzNm, EditorView view)
    {
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(selection);

        long lateral = view == EditorView.Layout ? dyOrDzNm : 0;
        long vertical = view == EditorView.Profile ? dyOrDzNm : 0;

        var wires = design.AllWires().ToList();

        foreach (int index in selection.TouchedWires())
        {
            if (index < 0 || index >= wires.Count) continue;
            var wire = wires[index];

            foreach (int i in selection.MovingPoints(index, wire.Points.Count))
            {
                var p = wire.Points[i];
                wire.Points[i] = new Point3(p.X + dxNm, p.Y + lateral, p.Z + vertical);
            }
        }
    }

    /// <summary>The shipped nudge steps (WB25). Both are settings; these are the defaults.</summary>
    public static long DefaultNudgeNm => WBondUnits.ToNm(1.0, WBondUnit.Mil);

    /// <summary>The shift-modified nudge step.</summary>
    public static long CoarseNudgeNm => WBondUnits.ToNm(5.0, WBondUnit.Mil);

    // ---------------------------------------------------------------- shared geometry

    /// <summary>
    /// Where <paramref name="p"/> falls along the chord from <paramref name="start"/> to
    /// <paramref name="end"/>, as a fraction — measured <b>in XY only</b>.
    ///
    /// <para><b>XY, not 3D, and the distinction is load-bearing.</b> A full 3D projection makes a
    /// point's loop height feed back into its own span position: raise the loop and every point
    /// slides along the chord, so "scale the height" is no longer a self-consistent operation —
    /// measured, a nominal 1.5x height scale came out as 1.498x and a 2x similarity scale as 1.987x.
    /// With the span taken from the horizontal path the two are independent, which is what
    /// <c>wbond.md</c> §6.2 means by "position along its own XY path".</para>
    ///
    /// <para>It is also the profile view's horizontal coordinate: normalised span is what makes wire
    /// angle and wire length stop being profile differences at all. One definition, or the canvas and
    /// the scaling drift and a wire renders at a different place than it scales about.</para>
    ///
    /// <para>A wire whose two feet share an XY position — a purely vertical stub — has no horizontal
    /// span, so its z extent is used instead. That keeps the function total rather than returning a
    /// meaningless zero for every point.</para>
    /// </summary>
    public static double ChordParameter(Point3 start, Point3 end, Point3 p)
    {
        double ex = end.X - start.X, ey = end.Y - start.Y;
        double lengthSquared = ex * ex + ey * ey;

        if (lengthSquared > 0.0)
        {
            double px = p.X - start.X, py = p.Y - start.Y;
            return (px * ex + py * ey) / lengthSquared;
        }

        // Degenerate footprint: fall back to the vertical extent.
        double ez = end.Z - start.Z;
        return ez == 0.0 ? 0.0 : (p.Z - start.Z) / ez;
    }

    private static Point3 Lerp(Point3 a, Point3 b, double t) => new(
        a.X + (long)Math.Round((b.X - a.X) * t),
        a.Y + (long)Math.Round((b.Y - a.Y) * t),
        a.Z + (long)Math.Round((b.Z - a.Z) * t));
}
