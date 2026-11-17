namespace CircuitRF.Ui.Schematic;

/// <summary>
/// The wire a part shorts out the moment it is placed across it — a two-terminal part, or the two
/// through pins of a tee or cross — and the rule for when circuitRF may clear it.
///
/// <para><b>Why this exists.</b> The natural gesture for putting a part in series is to drop it onto
/// the wire whose current is to go through it — at which point both of its pins land on that wire
/// and the part is shorted by the very run it was meant to break into. Every user then performs the
/// same second gesture: delete the stretch of wire between the two pins. This finds that stretch so
/// the placement can do it for them.</para>
///
/// <para><b>There is no list of kinds, and deliberately so.</b> It began as an IProbe affordance and
/// grew a second kind (the WSProbe) by the end of the same week; the list was the part that had to
/// be remembered, and a part left off it fails by doing nothing. What qualifies is asked
/// GEOMETRICALLY instead, here and nowhere else: exactly two pins landing on ONE straight segment
/// of wire, and any other pins off it (brief DFR26-5: an MTEE dropped along a wire cuts between pins
/// 1 and 2, its branch left for the user; an MCROSS cuts between the two pins on the wire). A part whose pins are not collinear with the run — a vertical R dropped on a
/// horizontal wire, an MBEND, whose two pins are at right angles — cannot satisfy it as dropped. R, L,
/// C, NonlinearC, SRLC, a diode, a TLIN, a 2-port SnP, a two-pin cell of the user's own and both series
/// probes all reach it by the same route.</para>
///
/// <para><b>A part dropped ACROSS a run is turned to lie along it</b> (<see cref="TurnAlongRun"/>). The
/// lumped primitives draw vertically and an SnP or a TLIN horizontally, so each fitted only one of the
/// two directions a run can take: an S2P dropped on a vertical wire landed straddling it, its pins either
/// side, connected to nothing. A part placed so is in no circuit at all, so there was no gesture to
/// protect. It is turned a quarter turn only when it touches no wire as dropped (a part with a pin on a
/// wire is a shunt, or a deliberate connection, and is left as placed), when the turn lands exactly two
/// of its pins on one run, and when that cut passes every test below. An MBEND, whose pins are at right
/// angles, cannot pass at either orientation and is never turned.</para>
///
/// <para><b>A shorted part is worse than it looks</b>, which is why this is not cosmetic. A probe
/// that is shorted reports nothing — an IProbe reads no current, and a WSProbe's whole output is the
/// <c>wsp</c> matrix of a node it never split. A shorted R, L or C is quieter still: the design
/// SIMULATES, converges and returns a plausible answer with the part contributing nothing, and
/// nothing on the sheet says so because the wire and the part draw on top of each other. The
/// headless twin of the probe half of this rule is <c>check</c>'s <c>wsprobe.shorted</c>.</para>
///
/// <para><b>The result must be electrically identical to the hand edit</b>, and that is the whole
/// constraint on <see cref="FindShortedSpans"/>. The cut is allowed only when the two pins sit on
/// ONE straight segment and the open stretch between them carries nothing else — no
/// junction dot, no other wire touching or crossing it, no other component's pin, no net label
/// anchored inside it. Anything in there is a connection to a third thing, and removing the copper
/// under it would re-partition the net rather than break it in series. In that case this returns
/// nothing and the placement behaves exactly as it always did; the user still has the manual route.</para>
///
/// <para><b>Several wires can carry the same run, and then every one of them is cut.</b> Two wires
/// drawn along the same line draw as ONE line, so a run that is doubled is invisible on the sheet —
/// and cutting only one of them leaves the other still shorting the part, which is why this used
/// to refuse the whole placement there rather than half-do it. Refusing was silent, and the sheet
/// gives the user nothing to look at, so the affordance simply stopped working on that run with no
/// way to find out why. Cutting the span out of ALL of them is the hand edit, exactly: the same
/// stretch of copper is gone from every wire that carried it. That it is safe is not a new
/// judgement — a second wire whose own vertex falls INSIDE the span is still refused below, so any
/// wire that reaches this point spans the whole cut and is in the same position as the first.</para>
///
/// <para><b>When a part ARRIVES, not whenever it moves.</b> The callers are
/// <c>SchematicViewModel.CommitPlacement</c> and <c>CommitCellPlacementAsync</c> — the two commit
/// paths, covering the click-arm, the drag-and-drop and the cell/kit placements — and the three
/// ways a part arrives as a COPY: a paste of one part, a Ctrl-drag duplicate of one part, and the
/// drag of a single part that is connected to nothing (which is what a pasted or duplicated part
/// is until it is dropped somewhere). A copied line dragged onto the run it was copied for is the
/// same gesture as a palette drop, and leaving it shorted was a field report. A drag of a part that
/// is already wired in, a multi-part drag and a type-change still reach none of this. There is
/// deliberately no "has this already run" flag on the component: the entry points ARE the once.</para>
///
/// <para>Framework-free (no Avalonia), like <see cref="WireGeometry"/> — headless-testable.</para>
/// </summary>
public static class SeriesPartInsertion
{
    /// <summary>
    /// The stretch of wire to remove: which wire, which of its segments, and the two cut points
    /// in that segment's own order — <paramref name="First"/> is the one nearer the segment's
    /// start vertex, so the piece before the cut ends at First and the piece after starts at Second.
    /// </summary>
    public readonly record struct ShortedSpan(
        EditableWire         Wire,
        int                  SegmentIndex,
        (double X, double Y) First,
        (double X, double Y) Second);

    /// <summary>
    /// The spans <paramref name="part"/> would be shorted by — one per wire carrying the run, or
    /// empty when there is none, when no single pair of the part's pins lies on the run, or when
    /// removing them would change the circuit. <paramref name="part"/> is the component about to be placed and is NOT
    /// expected to be in <paramref name="model"/> yet — which is also what keeps its own pins out of
    /// the "something else is in the way" scan. For a DRAG, <paramref name="part"/> is a copy at the
    /// landing position and <paramref name="moving"/> is the part itself, still at its start: its
    /// own pins are skipped by the same rule, since they are about to leave.
    /// </summary>
    public static IReadOnlyList<ShortedSpan> FindShortedSpans(
        SchematicEditModel model, EditableComponent part, EditableComponent? moving = null)
    {
        const double tol = SchematicEditModel.ConnectTolerance;

        // Exactly two pins ON THE SEGMENT is the membership test — see the class remarks. A one-pin
        // part has no span; a part with more pins qualifies when exactly two of them land on one
        // straight segment and the rest are off it (brief DFR26-5 R-dfr26-5-2): an MTEE's through
        // pins along a wire, its branch pin left for the user; an MCROSS's two collinear pins.
        var pins = model.PortDefsOf(part).Select(d => model.PortWorldOf(part, d)).ToList();
        if (pins.Count < 2) return [];

        // The pair on ONE straight segment. Two pins on two different segments of the same run
        // means it turns a corner between them, which is not the shape this affordance is for.
        List<ShortedSpan> found = [];
        (int A, int B)? pair = null;
        foreach (var w in model.Wires)
        {
            var pts = w.Points;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                var (px, py) = pts[i];
                var (qx, qy) = pts[i + 1];
                var on = Enumerable.Range(0, pins.Count)
                    .Where(k => SchematicGeometry.PointOnSegment(pins[k].X, pins[k].Y, px, py, qx, qy, tol))
                    .ToList();
                if (on.Count < 2) continue;
                if (on.Count > 2) return [];   // three pins on one run: no unambiguous pair to break between

                // Every wire carrying the run must be carrying the SAME pair — a cross dropped where
                // two wires meet it on different pairs is not one series cut.
                if (pair is { } seen && seen != (on[0], on[1])) return [];
                pair = (on[0], on[1]);
                var (a, b) = (pins[on[0]], pins[on[1]]);
                if (SchematicGeometry.CoincidentPoints(a.X, a.Y, b.X, b.Y, tol)) return [];

                // One wire doubling back over its own run: two cuts on one wire object, which the
                // cut command has no shape for. Rare, and not what a doubled run looks like —
                // leave the sheet alone rather than half-cut it.
                if (found.Exists(f => ReferenceEquals(f.Wire, w))) return [];

                bool aFirst = Dist2(px, py, a.X, a.Y) <= Dist2(px, py, b.X, b.Y);
                found.Add(new ShortedSpan(w, i, aFirst ? a : b, aFirst ? b : a));
            }
        }
        if (found.Count == 0) return [];

        // The part's other pins must be off every wire being cut, not just off the span: a branch pin
        // on another segment of the same wire would stay tied to one of the cut pieces, still shorting
        // that pin to one of the pair.
        for (int k = 0; k < pins.Count; k++)
        {
            if (k == pair!.Value.A || k == pair.Value.B) continue;
            foreach (var cut in found)
                if (OnWire(cut.Wire, pins[k].X, pins[k].Y, tol)) return [];
        }

        return SpanIsClear(model, found, moving) ? found : [];
    }

    /// <summary>
    /// The quarter turn that puts <paramref name="part"/>, dropped ACROSS a straight run of wire, along
    /// it in series — or null when it is already along one, touches any wire as dropped, or no turn
    /// lands exactly two of its pins on one run with a clear span between them. Of the two turns that
    /// fit, the one putting the lower-numbered of the pair at the run's left or top end, so a turned S2P
    /// reads port 1 to port 2 the way it reads left to right. <paramref name="part"/> is left at the
    /// rotation it came with; the caller applies the answer.
    /// </summary>
    public static SymbolRotation? TurnAlongRun(SchematicEditModel model, EditableComponent part)
    {
        const double tol = SchematicEditModel.ConnectTolerance;
        var pins = model.PortDefsOf(part).Select(d => model.PortWorldOf(part, d)).ToList();
        if (pins.Count < 2 || FindShortedSpans(model, part).Count > 0) return null;
        // touching a wire as dropped is a connection somebody made: not ours to turn away from
        foreach (var (x, y) in pins)
            foreach (var w in model.Wires)
                if (OnWire(w, x, y, tol)) return null;

        var original = part.Rotation;
        try
        {
            SymbolRotation? best = null;
            double bestLead = double.MaxValue;
            foreach (int quarters in (int[])[1, 3])
            {
                part.Rotation = (SymbolRotation)(((int)original + 90 * quarters) % 360);   // the enum's values are degrees
                var cuts = FindShortedSpans(model, part);
                if (cuts.Count == 0) continue;
                // which of the pair comes first along the run: the lower-numbered pin should
                var turned = model.PortDefsOf(part).Select(d => model.PortWorldOf(part, d)).ToList();
                int first = Enumerable.Range(0, turned.Count).First(k =>
                    SchematicGeometry.CoincidentPoints(turned[k].X, turned[k].Y, cuts[0].First.X, cuts[0].First.Y, tol)
                    || SchematicGeometry.CoincidentPoints(turned[k].X, turned[k].Y, cuts[0].Second.X, cuts[0].Second.Y, tol));
                double lead = turned[first].X + turned[first].Y;   // smaller: further left, or further up
                if (lead < bestLead) { bestLead = lead; best = part.Rotation; }
            }
            return best;
        }
        finally { part.Rotation = original; }
    }

    private static bool OnWire(EditableWire w, double x, double y, double tol)
    {
        var pts = w.Points;
        if (pts.Count == 1) return SchematicGeometry.CoincidentPoints(x, y, pts[0].X, pts[0].Y, tol);
        for (int i = 0; i < pts.Count - 1; i++)
            if (SchematicGeometry.PointOnSegment(x, y, pts[i].X, pts[i].Y, pts[i + 1].X, pts[i + 1].Y, tol))
                return true;
        return false;
    }

    /// <summary>
    /// True when nothing but wire occupies the OPEN stretch between the two cut points. The two
    /// ends themselves are excluded on purpose: that is where the part's own pins land, and a
    /// wire, pin or dot meeting the run exactly there stays connected to the part afterwards.
    /// </summary>
    private static bool SpanIsClear(
        SchematicEditModel model, List<ShortedSpan> cuts, EditableComponent? moving)
    {
        const double tol = SchematicEditModel.ConnectTolerance;
        var (ax, ay) = cuts[0].First;
        var (bx, by) = cuts[0].Second;

        bool Inside(double x, double y)
            => SchematicGeometry.PointOnSegmentInterior(x, y, ax, ay, bx, by, tol);

        // A user junction dot — by definition a 4-way crossing that is connected.
        foreach (var d in model.Dots)
            if (Inside(d.X, d.Y)) return false;

        // Another component's pin tapping the run mid-span.
        foreach (var c in model.Components)
        {
            if (ReferenceEquals(c, moving)) continue;   // its pins are leaving: they are not in the run
            foreach (var def in model.PortDefsOf(c))
            {
                var (px, py) = model.PortWorldOf(c, def);
                if (Inside(px, py)) return false;
            }
        }

        // A net label anchored inside the run: the name belongs to the copper about to go away.
        foreach (var l in model.NetLabels)
            if (Inside(l.X - l.OffsetX, l.Y - l.OffsetY)) return false;

        // Any OTHER wire meeting the run — a T-junction (its vertex lands on the run), a crossing
        // (its body passes through), or a collinear duplicate lying along it.
        foreach (var w in model.Wires)
        {
            if (cuts.Exists(c => ReferenceEquals(c.Wire, w))) continue;
            var pts = w.Points;
            if (pts.Count == 1 && Inside(pts[0].X, pts[0].Y)) return false;

            for (int i = 0; i < pts.Count - 1; i++)
            {
                var p = pts[i];
                var q = pts[i + 1];
                if (Inside(p.X, p.Y) || Inside(q.X, q.Y)) return false;
                if (SchematicGeometry.SegmentsIntersectInterior(
                        ax, ay, bx, by, p.X, p.Y, q.X, q.Y, out _, out _)) return false;
                if (RunsAlongSpan(ax, ay, bx, by, p.X, p.Y, q.X, q.Y, tol)) return false;
            }
        }

        // A cut wire's own vertices cannot fall inside the span — both cut points lie on one
        // straight segment, whose interior has none by construction. Nothing to check.
        return true;
    }

    /// <summary>
    /// True when segment (p,q) lies along the same line as the span and overlaps more than a single
    /// point of it — a duplicate run of wire the cut would leave behind, still shorting the part.
    /// Neither <see cref="SchematicGeometry.SegmentsIntersectInterior"/> (which rejects parallels)
    /// nor the vertex test above sees this one when the duplicate outreaches the span at both ends.
    /// </summary>
    private static bool RunsAlongSpan(
        double ax, double ay, double bx, double by,
        double px, double py, double qx, double qy, double tol)
    {
        if (Math.Abs(ay - by) <= tol && Math.Abs(py - qy) <= tol && Math.Abs(py - ay) <= tol)
            return Overlap(ax, bx, px, qx) > tol;
        if (Math.Abs(ax - bx) <= tol && Math.Abs(px - qx) <= tol && Math.Abs(px - ax) <= tol)
            return Overlap(ay, by, py, qy) > tol;
        return false;
    }

    private static double Overlap(double a1, double a2, double b1, double b2)
        => Math.Min(Math.Max(a1, a2), Math.Max(b1, b2)) - Math.Max(Math.Min(a1, a2), Math.Min(b1, b2));

    private static double Dist2(double ax, double ay, double bx, double by)
        => (ax - bx) * (ax - bx) + (ay - by) * (ay - by);
}
