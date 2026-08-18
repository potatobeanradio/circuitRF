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
    /// Scales an explicit set of wires — the alt-drag primitive, applied to whatever the caller
    /// decided the gesture means (its selection, or every wire of the arrays it touches).
    ///
    /// <para><b>By factor, never to a common value</b>: an array whose wires deliberately have
    /// different spans — a fan-out from a common pad — keeps their ratios. Setting a common absolute
    /// span would silently destroy exactly the geometry the flexible model exists to allow.</para>
    /// </summary>
    /// <param name="moveOutputFoot">
    /// Which foot the span scale moves. <b>The pinned foot is the one the user is NOT dragging</b> —
    /// the same rule the rotate-about-end-point tool uses (WB26a), and the same reason: grabbing near
    /// an end IS the instruction to move that end, so no mode switch is needed. Always pinning
    /// <c>Points[0]</c> made an alt-drag on the left end of a wire pull the RIGHT end towards the
    /// cursor, which is the opposite of what the hand asked for.
    /// </param>
    /// <returns>How many wires were rescaled.</returns>
    public static int ScaleWires(IEnumerable<Wire> wires, double heightFactor, double spanFactor,
                                 bool moveOutputFoot = true)
    {
        ArgumentNullException.ThrowIfNull(wires);

        int moved = 0;
        foreach (var wire in wires)
        {
            if (spanFactor != 1.0) ScaleSpan(wire, spanFactor, moveOutputFoot);
            if (heightFactor != 1.0) ScaleHeightAboutChord(wire, heightFactor);
            moved++;
        }

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
    ///
    /// <para><b><paramref name="dxNm"/> is world x in the layout view, and displacement ALONG EACH
    /// WIRE'S OWN XY CHORD in the profile view.</b> That is what the profile view's horizontal axis
    /// actually is — span along the chord (<see cref="ProfileProjection"/>) — so a wire running at 90°
    /// moves in y and one running at 0° moves in x, which is exactly what a user dragging sideways in
    /// that view is pointing at. Treating profile dx as world x instead was silently wrong for every
    /// wire not already parallel to the x axis: the point moved off the chord rather than along it,
    /// and its span barely changed.</para>
    ///
    /// <para>Each wire's chord direction is read ONCE, before any of its points move, so a selection
    /// that includes a foot cannot rotate its own reference direction part-way through the move. A
    /// wire with coincident feet in XY has no chord direction and its horizontal component is skipped
    /// rather than guessed.</para>
    ///
    /// <para><paramref name="azimuthRadians"/> is the profile view's own fixed plane, when it has
    /// one: horizontal then means that direction for every wire rather than each wire's own chord.
    /// It comes from <see cref="ProfileProjection.HorizontalDirection"/>, the SAME function the
    /// projection uses, so a point cannot render in one place and move in another.</para>
    /// </summary>
    public static void Translate(WBondDesign design, WireSelection selection,
                                 long dxNm, long dyOrDzNm, EditorView view,
                                 double? azimuthRadians = null)
    {
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(selection);

        bool profile = view == EditorView.Profile;
        long lateral = profile ? 0 : dyOrDzNm;
        long vertical = profile ? dyOrDzNm : 0;

        var wires = design.AllWires().ToList();

        foreach (int index in selection.TouchedWires())
        {
            if (index < 0 || index >= wires.Count) continue;
            var wire = wires[index];

            long stepX = dxNm, stepY = lateral;
            if (profile && dxNm != 0)
            {
                var (ux, uy) = ProfileProjection.HorizontalDirection(wire, azimuthRadians);
                stepX = (long)Math.Round(dxNm * ux);
                stepY = (long)Math.Round(dxNm * uy);
            }

            foreach (int i in selection.MovingPoints(index, wire.Points.Count))
            {
                // A selection outlives the point list it was resolved against — the quality ladder's
                // chord collapse changes a wire's point COUNT mid-drag (WB15), and an undo can too. An
                // index that no longer exists is a point that is not there to move, not a crash.
                if ((uint)i >= (uint)wire.Points.Count) continue;

                var p = wire.Points[i];
                wire.Points[i] = new Point3(p.X + stepX, p.Y + stepY, p.Z + vertical);
            }
        }
    }

    /// <summary>
    /// A wire's foot-to-foot direction in XY as a unit vector, or (0,0) when its feet coincide there.
    ///
    /// <para>XY only, for the same reason <see cref="ChordParameter"/> is XY only: the profile view's
    /// horizontal coordinate is position along the wire's own horizontal path, so a wire's rise must
    /// not tilt the direction "sideways" means in it.</para>
    /// </summary>
    public static (double X, double Y) ChordDirectionXY(Wire wire)
    {
        ArgumentNullException.ThrowIfNull(wire);
        if (wire.Points.Count < 2) return (0.0, 0.0);

        double dx = wire.Points[^1].X - wire.Points[0].X;
        double dy = wire.Points[^1].Y - wire.Points[0].Y;
        double length = Math.Sqrt(dx * dx + dy * dy);

        return length <= 0.0 ? (0.0, 0.0) : (dx / length, dy / length);
    }

    // ---------------------------------------------------------------- vertices (owner, 2026-08-17)

    /// <summary>
    /// Inserts a vertex into <paramref name="segmentIndex"/> at parameter <paramref name="t"/> along
    /// it, <b>on the straight line between that segment's own endpoints</b>.
    ///
    /// <para>Owner, 2026-08-17: <i>"add the vertex such that it makes straight lines with the adjacent
    /// vertices; make its z-height an interpolated value from the adjacent vertices."</i> Both fall
    /// out of one <see cref="Lerp"/>: a point ON the segment is collinear with its neighbours by
    /// construction, and lerping all three coordinates interpolates z with them. So the insert changes
    /// the wire's SHAPE not at all — it only gives the user a handle where there was none, which is
    /// the whole point of the command.</para>
    ///
    /// <para><paramref name="t"/> is clamped to [0,1]: the caller projects a click onto the segment,
    /// and a click past either end would otherwise place the vertex outside the segment it names —
    /// a kink, from a command whose contract is that it makes none.</para>
    /// </summary>
    /// <returns>The index of the inserted point, or −1 when the segment does not exist.</returns>
    public static int InsertPointOnSegment(Wire wire, int segmentIndex, double t)
    {
        ArgumentNullException.ThrowIfNull(wire);
        if (segmentIndex < 0 || segmentIndex >= wire.Points.Count - 1) return -1;

        double clamped = double.IsFinite(t) ? Math.Clamp(t, 0.0, 1.0) : 0.5;

        var inserted = Lerp(wire.Points[segmentIndex], wire.Points[segmentIndex + 1], clamped);
        wire.Points.Insert(segmentIndex + 1, inserted);
        return segmentIndex + 1;
    }

    /// <summary>
    /// Straightens a wire <b>in the XY plane, with both feet anchored</b> — every interior point moves
    /// onto the straight line between them, keeping its own position along that line and <b>its own
    /// z</b> (owner, 2026-08-17).
    ///
    /// <para>The loop is therefore untouched: this removes lateral bow, not height. Each point keeps
    /// the chord parameter it already had (<see cref="ChordParameter"/>), so a wire whose points were
    /// bunched near one foot stays bunched — the command straightens, it does not re-space.</para>
    ///
    /// <para>A wire just stamped out from a <see cref="LoopShape"/> is already straight in XY, because
    /// <see cref="LoopShape.Write"/> writes X and Y by linear interpolation between the feet — so this
    /// is a no-op there.</para>
    /// </summary>
    /// <returns>How many points moved.</returns>
    public static int StraightenXy(Wire wire)
    {
        ArgumentNullException.ThrowIfNull(wire);
        if (wire.Points.Count < 3) return 0;   // two feet and nothing between them: already straight

        var start = wire.Points[0];
        var end = wire.Points[^1];

        // Captured BEFORE anything moves: each parameter is measured against the ORIGINAL geometry,
        // and reading them as we go would measure later points against already-straightened ones.
        int n = wire.Points.Count;
        var spans = new double[n];
        for (int i = 0; i < n; i++) spans[i] = ChordParameter(start, end, wire.Points[i]);

        int moved = 0;
        for (int i = 1; i < n - 1; i++)
        {
            var p = wire.Points[i];
            long x = start.X + (long)Math.Round((end.X - start.X) * spans[i]);
            long y = start.Y + (long)Math.Round((end.Y - start.Y) * spans[i]);

            if (x == p.X && y == p.Y) continue;

            wire.Points[i] = new Point3(x, y, p.Z);
            moved++;
        }

        return moved;
    }

    /// <summary>
    /// Where a point falls along a 2D segment, as a fraction, clamped to the segment itself.
    ///
    /// <para>The caller's own plane: the layout view projects a click in XY, the profile view in
    /// (span, z). One helper for both, because "where along this segment did they click" is the same
    /// question in either and a second copy would drift.</para>
    /// </summary>
    public static double SegmentParameter(double ax, double ay, double bx, double by,
                                          double px, double py)
    {
        double ex = bx - ax, ey = by - ay;
        double lengthSquared = ex * ex + ey * ey;
        if (lengthSquared <= 0.0) return 0.0;   // a degenerate segment: either end is the same answer

        return Math.Clamp(((px - ax) * ex + (py - ay) * ey) / lengthSquared, 0.0, 1.0);
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

    // ---------------------------------------------------------------- loop height, in place

    /// <summary>
    /// Sets a wire's loop height <b>by rescaling its own rise above the chord</b> — keeping every
    /// point's X and Y exactly, and both feet bit-exactly.
    ///
    /// <h3>Why this exists rather than re-stamping a <see cref="LoopShape"/></h3>
    /// <para>Owner, 2026-08-17: <i>"I don't like this ball/wedge profile thing. It doesn't offer the
    /// user anything. Its setting should never affect the geometry that the user authors."</i></para>
    ///
    /// <para><see cref="LoopShape.Write"/> writes a wire's X and Y by linear interpolation between the
    /// feet, so applying a loop height by re-stamping a shape <b>straightens any path the user routed by
    /// hand</b> — a wire taken around an obstacle comes back as a plain planar arc. That is fine when a
    /// shape is genuinely generating a new wire and destructive the moment someone has routed one. This
    /// changes the one quantity that was asked for and nothing else.</para>
    ///
    /// <para><b>No span ordering is required</b>, which is the second reason not to route this through a
    /// shape: <see cref="LoopShape.Validate"/> demands strictly increasing spans, and a hand-routed
    /// wire that doubles back in XY does not have them. Nothing here needs the points ordered along the
    /// chord at all.</para>
    ///
    /// <h3>The solve</h3>
    /// <para>Every point moves as <c>z(k) = chord(t) + k·rise</c>, so the measured height
    /// <c>max z − min z</c> is a convex function of <c>k</c> that starts at the foot drop (<c>k = 0</c>,
    /// every point on the chord) and increases — so the <c>k</c> reaching a requested height is unique
    /// and is found by bisection. A closed form over the positive rises alone would be exact only while
    /// no point dips BELOW the chord, which a hand-routed wire may well do.</para>
    ///
    /// <para><b>Clamped at the foot drop</b>, exactly as <see cref="LoopShape.SolveAmplitudeNm"/> is
    /// and for the same reason: with the feet <c>|z₁ − z₂|</c> apart even a dead-straight wire measures
    /// that much, so a smaller request is not achievable by any shape (see
    /// <see cref="Wire.LoopHeightNm"/>).</para>
    /// </summary>
    /// <returns>False when there is nothing to scale — fewer than two points, or a wire that is already
    /// dead straight and so has no rise to grow from.</returns>
    public static bool SetLoopHeightPreservingPath(Wire wire, long targetNm)
    {
        ArgumentNullException.ThrowIfNull(wire);
        if (targetNm <= 0 || wire.Points.Count < 2) return false;

        var start = wire.Points[0];
        var end = wire.Points[^1];

        int n = wire.Points.Count;
        var chord = new double[n];
        var rise = new double[n];
        bool anyRise = false;

        for (int i = 0; i < n; i++)
        {
            double t = ChordParameter(start, end, wire.Points[i]);
            chord[i] = start.Z + t * (end.Z - start.Z);
            rise[i] = wire.Points[i].Z - chord[i];
            if (Math.Abs(rise[i]) > 0.5) anyRise = true;
        }

        // The feet ARE the chord by construction; force them so rounding in ChordParameter cannot
        // creep a nanometre into a pad position.
        chord[0] = start.Z; rise[0] = 0.0;
        chord[^1] = end.Z;  rise[^1] = 0.0;

        if (!anyRise) return false;   // a straight wire has no shape to scale; nothing honest to do

        double MeasuredAt(double k)
        {
            double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
            for (int i = 0; i < n; i++)
            {
                double z = chord[i] + k * rise[i];
                if (z < lo) lo = z;
                if (z > hi) hi = z;
            }
            return hi - lo;
        }

        double footDrop = Math.Abs((double)end.Z - start.Z);
        double k;

        if (targetNm <= footDrop)
        {
            k = 0.0;   // not achievable by any shape — the wire comes back at its floor
        }
        else
        {
            // Grow an upper bound rather than assuming one: the current shape may be very shallow, so
            // the k reaching a tall request can be large.
            double hiK = 1.0;
            for (int guard = 0; guard < 64 && MeasuredAt(hiK) < targetNm; guard++) hiK *= 2.0;

            double loK = 0.0;
            for (int iter = 0; iter < 60; iter++)
            {
                double mid = 0.5 * (loK + hiK);
                if (MeasuredAt(mid) < targetNm) loK = mid; else hiK = mid;
            }
            k = 0.5 * (loK + hiK);
        }

        for (int i = 1; i < n - 1; i++)
        {
            var p = wire.Points[i];
            wire.Points[i] = p with { Z = (long)Math.Round(chord[i] + k * rise[i]) };
        }

        return true;
    }
}
