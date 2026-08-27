using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>
/// Keeps every wire on the PIN it was on when a selection is rotated or mirrored (owner, 2026-08-26).
///
/// <para><b>The rule these operations owe the user: a rotate or a mirror re-draws the picture, it
/// never re-wires the circuit.</b> Both are geometric, and this editor's connectivity is geometric
/// too — a port and a wire vertex at the same point ARE one node
/// (<see cref="SchematicEditModel.ComputeConnectivityGeometry"/>) — so moving a symbol without moving
/// what is attached to it silently produces a different netlist. Two things used to go wrong, and
/// both are the same failure from the user's side: the schematic still looks wired up, and simulates
/// as something else.</para>
///
/// <list type="number">
///   <item><b>The re-route could short the symbol it was following.</b> The old code re-drew each
///   moved wire as <c>WireGeometry.OrthogonalRoute</c>'s bare L — horizontal first, then vertical —
///   with nothing checking what that L ran over. Two resistors in series, both selected, rotated
///   once: R1's own two pins land on the same horizontal as the new route's first leg, the L is laid
///   straight across the far pin, and R1 is shorted out. Reproduced end to end against
///   <c>NetExtractor</c> — the series pair <c>R1[n1,n2] R2[n2,n3]</c> came back as
///   <c>R1[n1,n1] R2[n1,n2]</c>. That is why a route here is CHOSEN rather than computed: candidates
///   are generated nearest-first and the first one that touches nothing but its own two endpoints
///   wins.</item>
///   <item><b>Mirror re-routed nothing at all.</b> <c>MirrorCommand</c> flipped the components and
///   left every wire where it lay, so on any symbol whose pins are not symmetric about its mirror
///   axis — a TLIN, an MTee, a FET, anything with a left pin and a right pin — the wires simply swap
///   ends. Pin 1's wire is on pin 2, silently, and the schematic looks untouched.</item>
/// </list>
///
/// <para><b>Ports come from the MODEL, not from <c>SymbolPortDefs</c>.</b> The old rotate asked
/// <c>SymbolPortDefs.For(comp.Symbol)</c> — the two-pin convenience overload. That is the wrong
/// answer for everything whose pins are not the built-in default: a cell instance or a kit part
/// (pins live in the referenced symbol, so it returned NOTHING and no wire moved at all), an SnP
/// (RefNode/PinConfig/Pitch decide the pins), and anything variadic (an SDD's pin count is a
/// parameter). <see cref="SchematicEditModel.PortDefsOf"/> is the same source connectivity itself
/// reads, so what follows a pin and what counts as connected to it can no longer disagree.</para>
///
/// <para><b>This handles the BOUNDARY only.</b> A rotate or a mirror carries its selection as one
/// rigid body (<see cref="SchematicGroupTransform"/>), so everything wholly inside it — including the
/// wires between selected parts, and the pin-to-pin contacts that have no wire at all — survives by
/// construction and never reaches this code. What is left is the wire with one end on a moving pin
/// and the other end somewhere that is staying put: it cannot be carried, so it is re-drawn, and the
/// re-draw is where the two failures above lived.</para>
/// </summary>
internal static class PinFollowReroute
{
    /// <summary>A component's placement AFTER the transform the caller is about to apply.</summary>
    internal readonly record struct Transformed(
        EditableComponent Comp, double NewX, double NewY, SymbolRotation NewRotation, bool NewMirrorX);

    /// <summary>One pin's world position before and after the transform.</summary>
    private readonly record struct PinMove(double Ox, double Oy, double Nx, double Ny);

    /// <summary>
    /// The wire snapshots that keep every pin attachment intact across <paramref name="transformed"/>.
    /// Empty when nothing the transform touches has a wire on it.
    ///
    /// <para>Call this BEFORE mutating the components — it reads their current placement as the
    /// "before" state and takes the "after" state from the tuples.</para>
    /// </summary>
    /// <param name="carried">
    /// Wires the caller is transforming rigidly (id → their new points), which is the whole of a
    /// group rotate's interior. They are excluded from re-routing — they are already right, and
    /// re-drawing them would throw away bends the user placed — but their NEW geometry is what the
    /// obstacle test sees, so a wire that IS re-routed does not get laid across one of them.
    /// </param>
    public static List<WireMoveSnapshot> Build(
        SchematicEditModel model, IReadOnlyList<Transformed> transformed,
        IReadOnlyDictionary<string, IReadOnlyList<(double X, double Y)>>? carried = null)
    {
        const double tol = SchematicEditModel.ConnectTolerance;

        // ── Which pins move, and where to ────────────────────────────────────
        // A DETACHED port is skipped: it is a port the user has explicitly disconnected, so nothing
        // is attached to it and a wire that merely passes over it must not be dragged along. This is
        // the same exclusion ComputeConnectivityGeometry makes.
        var moves = new List<PinMove>();
        foreach (var (comp, newX, newY, newRot, newMirror) in transformed)
        {
            foreach (var def in model.PortDefsOf(comp))
            {
                if (comp.IsPortDetached(def.PortIndex)) continue;
                var (ox, oy) = SchematicGeometry.LocalToWorld(
                    def.LocalX, def.LocalY, comp.X, comp.Y, comp.Rotation, comp.MirrorX);
                var (nx, ny) = SchematicGeometry.LocalToWorld(
                    def.LocalX, def.LocalY, newX, newY, newRot, newMirror);
                if (SchematicGeometry.CoincidentPoints(ox, oy, nx, ny, tol)) continue;   // pin stays put
                moves.Add(new PinMove(ox, oy, nx, ny));
            }
        }
        if (moves.Count == 0) return [];

        // ── Every connection point the schematic will HAVE afterwards ────────
        // A route may END on one of these — that is the attachment it exists to preserve — but it
        // must never run through one, which is exactly what the old bare-L did.
        var newState = transformed.ToDictionary(
            t => t.Comp.Id, t => (t.NewX, t.NewY, t.NewRotation, t.NewMirrorX), StringComparer.Ordinal);
        var pinPoints = new List<(double X, double Y)>();
        foreach (var comp in model.Components)
        {
            var (cx, cy, rot, mirror) = newState.TryGetValue(comp.Id, out var st)
                ? st
                : (comp.X, comp.Y, comp.Rotation, comp.MirrorX);
            foreach (var def in model.PortDefsOf(comp))
            {
                if (comp.IsPortDetached(def.PortIndex)) continue;
                pinPoints.Add(SchematicGeometry.LocalToWorld(
                    def.LocalX, def.LocalY, cx, cy, rot, mirror));
            }
        }

        // Working copy of every wire's geometry so a route already chosen for one wire counts as an
        // obstacle for the next — two wires re-routed in the same gesture would otherwise be free to
        // land on top of each other.
        var live = model.Wires.ToDictionary(
            w => w.Id,
            w => carried is not null && carried.TryGetValue(w.Id, out var moved)
                ? moved
                : (IReadOnlyList<(double X, double Y)>)w.Points.ToList(),
            StringComparer.Ordinal);

        var snaps = new List<WireMoveSnapshot>();
        foreach (var wire in model.Wires)
        {
            if (wire.Points.Count < 2) continue;
            if (carried is not null && carried.ContainsKey(wire.Id)) continue;   // already carried whole

            var (sx, sy) = wire.Points[0];
            var (ex, ey) = wire.Points[^1];
            var sMove = FindMove(moves, sx, sy, tol);
            var eMove = FindMove(moves, ex, ey, tol);
            if (sMove is null && eMove is null) continue;

            double nsx = sMove?.Nx ?? sx, nsy = sMove?.Ny ?? sy;
            double nex = eMove?.Nx ?? ex, ney = eMove?.Ny ?? ey;

            // A wire whose two ends land on the same point has nothing left to draw. It is left
            // alone rather than collapsed to a degenerate stub: the two pins are coincident now, so
            // they are one node with or without it.
            if (SchematicGeometry.CoincidentPoints(nsx, nsy, nex, ney, tol)) continue;

            var others = live.Where(kv => !string.Equals(kv.Key, wire.Id, StringComparison.Ordinal))
                             .Select(kv => kv.Value)
                             .ToList();
            var route = ChooseRoute(nsx, nsy, nex, ney, pinPoints, others, model.GridSize, tol);

            live[wire.Id] = route;
            snaps.Add(new WireMoveSnapshot(wire, wire.Points.ToList(), route));
        }
        return snaps;
    }

    /// <summary>
    /// The pin this endpoint is attached to, or null if it is on none.
    ///
    /// <para>Matched at <see cref="SchematicEditModel.ConnectTolerance"/> — the tolerance
    /// CONNECTIVITY uses — rather than the old code's 8.0. A point 4 units off a pin is not on that
    /// net, so dragging a wire there would move something that was never attached.</para>
    ///
    /// <para>First match wins when several moved pins share a point. That case is unresolvable by
    /// re-routing (one wire, two pins, and the pins are about to go to different places), so it is
    /// settled deterministically rather than left to enumeration order: selection order, then port
    /// order.</para>
    /// </summary>
    private static PinMove? FindMove(List<PinMove> moves, double x, double y, double tol)
    {
        foreach (var m in moves)
            if (SchematicGeometry.CoincidentPoints(x, y, m.Ox, m.Oy, tol))
                return m;
        return null;
    }

    /// <summary>
    /// The nearest candidate route that connects the two endpoints and touches nothing else.
    ///
    /// <para>Falls back to the first candidate — the plain L this code used to emit unconditionally —
    /// when every candidate is obstructed, so the worst case is what shipped before rather than a
    /// wire that fails to appear.</para>
    /// </summary>
    private static IReadOnlyList<(double X, double Y)> ChooseRoute(
        double x0, double y0, double x1, double y1,
        List<(double X, double Y)> pinPoints,
        List<IReadOnlyList<(double X, double Y)>> others,
        double gridSize, double tol)
    {
        IReadOnlyList<(double X, double Y)>? first = null;
        foreach (var candidate in Candidates(x0, y0, x1, y1, gridSize))
        {
            first ??= candidate;
            if (IsClean(candidate, pinPoints, others, tol)) return candidate;
        }
        return first ?? [(x0, y0), (x1, y1)];
    }

    /// <summary>
    /// Orthogonal routes between the two endpoints, simplest and straightest first: the direct run
    /// when they line up, then the two Ls, then Z-shaped detours whose middle leg steps away from the
    /// midpoint a grid pitch at a time, alternating sides.
    ///
    /// <para>The channel that would collapse is skipped: when the endpoints share a Y a
    /// vertical-channel Z is three collinear legs (i.e. the straight run again), and the useful
    /// detour is the horizontal one that jogs around the obstacle. Same the other way round.</para>
    /// </summary>
    private static IEnumerable<IReadOnlyList<(double X, double Y)>> Candidates(
        double x0, double y0, double x1, double y1, double gridSize)
    {
        bool sameX = Math.Abs(x1 - x0) < 1e-6;
        bool sameY = Math.Abs(y1 - y0) < 1e-6;

        if (sameX || sameY)
            yield return [(x0, y0), (x1, y1)];
        else
        {
            yield return [(x0, y0), (x1, y0), (x1, y1)];   // H first — the historical route
            yield return [(x0, y0), (x0, y1), (x1, y1)];   // V first
        }

        double step = gridSize > 0 ? gridSize : 100.0;
        foreach (double d in Offsets(step))
        {
            if (!sameY)
            {
                double ym = (y0 + y1) * 0.5 + d;
                yield return [(x0, y0), (x0, ym), (x1, ym), (x1, y1)];
            }
            if (!sameX)
            {
                double xm = (x0 + x1) * 0.5 + d;
                yield return [(x0, y0), (xm, y0), (xm, y1), (x1, y1)];
            }
            if (sameY)
            {
                double ym = y0 + d;
                if (Math.Abs(d) > 1e-6)
                    yield return [(x0, y0), (x0, ym), (x1, ym), (x1, y1)];
            }
            if (sameX)
            {
                double xm = x0 + d;
                if (Math.Abs(d) > 1e-6)
                    yield return [(x0, y0), (xm, y0), (xm, y1), (x1, y1)];
            }
        }
    }

    /// <summary>0, then ±1, ±2 … grid pitches — nearest channel first, so the chosen detour is the
    /// smallest one that clears.</summary>
    private static IEnumerable<double> Offsets(double step)
    {
        yield return 0.0;
        for (int k = 1; k <= 8; k++)
        {
            yield return k * step;
            yield return -k * step;
        }
    }

    /// <summary>
    /// True when this route creates no connection beyond its own two endpoints.
    ///
    /// <para>The three ways a route can wire something up, all from
    /// <see cref="SchematicEditModel.ComputeConnectivityGeometry"/>: a PIN anywhere on it; another
    /// wire's VERTEX on it (that is a T-junction, and it auto-dots); one of its own bends landing on
    /// another wire's body (the same T-junction seen from the other side). A plain 4-way crossing is
    /// NOT one of them — two wires passing through each other with no vertex connect only via a
    /// user-placed dot — so a route is free to cross, and requiring it not to would leave most
    /// schematics unroutable.</para>
    ///
    /// <para>Coincidence with either ENDPOINT is always allowed. The endpoints are where the pins
    /// now are; whatever else sits there sits there because of the rotation, not because of the
    /// route, and no choice of path can avoid it.</para>
    /// </summary>
    private static bool IsClean(
        IReadOnlyList<(double X, double Y)> route,
        List<(double X, double Y)> pinPoints,
        List<IReadOnlyList<(double X, double Y)>> others,
        double tol)
    {
        var a = route[0];
        var b = route[^1];

        bool AtEnd(double px, double py) =>
            SchematicGeometry.CoincidentPoints(px, py, a.X, a.Y, tol) ||
            SchematicGeometry.CoincidentPoints(px, py, b.X, b.Y, tol);

        bool OnRoute(double px, double py)
        {
            for (int i = 0; i < route.Count - 1; i++)
                if (SchematicGeometry.PointOnSegment(
                        px, py, route[i].X, route[i].Y, route[i + 1].X, route[i + 1].Y, tol))
                    return true;
            return false;
        }

        foreach (var (px, py) in pinPoints)
            if (!AtEnd(px, py) && OnRoute(px, py)) return false;

        foreach (var w in others)
        {
            foreach (var (px, py) in w)
                if (!AtEnd(px, py) && OnRoute(px, py)) return false;

            for (int i = 1; i < route.Count - 1; i++)
                for (int j = 0; j < w.Count - 1; j++)
                    if (SchematicGeometry.PointOnSegmentInterior(
                            route[i].X, route[i].Y, w[j].X, w[j].Y, w[j + 1].X, w[j + 1].Y, tol))
                        return false;
        }
        return true;
    }
}
