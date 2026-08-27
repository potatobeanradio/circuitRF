using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>Component placement before/after a group transform — position, rotation and mirror.</summary>
internal readonly record struct ComponentTransformSnapshot(
    EditableComponent Comp,
    double StartX, double StartY, SymbolRotation StartRot, bool StartMirrorX,
    double EndX,   double EndY,   SymbolRotation EndRot,   bool EndMirrorX);

/// <summary>Canvas-object placement before/after a group transform.</summary>
internal readonly record struct CanvasObjectTransformSnapshot(
    EditableCanvasObject Obj,
    double StartX, double StartY, double StartDeg,
    double EndX,   double EndY,   double EndDeg);

/// <summary>
/// <b>Rotate and mirror move the selection as ONE RIGID BODY</b> (owner, 2026-08-26).
///
/// <para>The rule the owner stated, and the reason this file exists: <i>if two components have their
/// pins touching, they are still touching afterwards.</i> That cannot be done by spinning each symbol
/// about its own origin — two symbols meeting at a point send that point in different directions the
/// instant they turn independently, and no amount of re-routing puts it back, because the connection
/// was the overlap and there is no wire to re-route. The only transform that preserves every contact
/// is the one that preserves every distance: one pivot, one angle, everything selected carried
/// together. Wires included, whether the user marquee'd them or just the parts either side of them.
/// This is what the Layout Editor already does with its own selection
/// (<c>LayoutEditorViewModel.Rotate.cs</c>), for the same reason.</para>
///
/// <para><b>Rigidity is what makes connectivity safe, and it is provable rather than tested-for.</b>
/// A pin's world position is <c>O + Rot(θ)·L</c>. Map the origin by <c>O' = P + M·(O−P)</c> and
/// compose the same <c>M</c> into the orientation (<c>Rot(θ') = M ∘ Rot(θ)</c>) and the pin lands at
/// <c>P + M·(pin − P)</c> — the rigid image of where it was. Every coincidence between two members of
/// the group therefore survives, including the ones no wire records.</para>
///
/// <para><b>The pivot is snapped to the CONNECTION grid, and that is load-bearing.</b> A 90° rotation
/// and a reflection both map the grid onto itself, so an on-grid part rotated about an on-grid pivot
/// stays on-grid — and a pin half a grid step off is a pin that no longer touches anything, since
/// connectivity here is coincidence at 0.5 world units. A single selected component pivots on its own
/// origin instead, which makes the everyday "place a resistor, press R" case a pure re-orientation
/// with no translation at all, exactly as it has always been.</para>
///
/// <para><b>What comes along.</b> Selected components and canvas objects; selected wires; wires the
/// user did NOT select but whose two ends are both on pins of selected components (click one part,
/// shift-click the other, rotate — the wire between them is plainly part of what is being turned);
/// and user junction dots that sit on a carried wire and on nothing else. A wire with only ONE end on
/// a moved pin straddles the boundary and cannot be carried — it is re-routed instead, by
/// <see cref="PinFollowReroute"/>, which keeps it on the same pin and picks a path that touches
/// nothing else. Net labels need no snapshot: their draw position is derived from their owner wire's
/// geometry on every build, so they follow it for free.</para>
/// </summary>
internal sealed class SchematicGroupTransform
{
    /// <summary>
    /// The transform, expressed once. <paramref name="MapOffset"/> is the linear part applied to
    /// positions relative to the pivot; <paramref name="MapRotation"/> is the SAME map composed into
    /// a symbol's own orientation. The two must agree or pins drift off the geometry they belong to.
    /// </summary>
    internal readonly record struct Spec(
        Func<double, double, (double X, double Y)> MapOffset,
        Func<SymbolRotation, SymbolRotation> MapRotation,
        bool TogglesMirrorX,
        Func<double, double> MapObjectAngleDeg);

    private readonly SchematicEditModel _model;

    public List<ComponentTransformSnapshot>    Components { get; } = [];
    public List<CanvasObjectTransformSnapshot> Objects    { get; } = [];
    public List<WireMoveSnapshot>              Wires      { get; } = [];
    public List<DotMoveSnapshot>               Dots       { get; } = [];

    public bool IsEmpty => Components.Count == 0 && Objects.Count == 0
                        && Wires.Count == 0 && Dots.Count == 0;

    private SchematicGroupTransform(SchematicEditModel model) => _model = model;

    /// <summary>
    /// Works out everything the transform moves, against the CURRENT model state, without mutating
    /// anything. Call it before <see cref="Apply"/> so the command has a matching before-picture for
    /// Undo and so Execute is a pure replay on redo.
    /// </summary>
    public static SchematicGroupTransform Build(
        SchematicEditModel model, IReadOnlyList<string> selectedIds, Spec spec)
    {
        var t = new SchematicGroupTransform(model);

        var comps   = new List<EditableComponent>();
        var objs    = new List<EditableCanvasObject>();
        var wires   = new List<EditableWire>();
        var wireIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var id in selectedIds)
        {
            if (model.FindComponent(id) is { } c)   { comps.Add(c); continue; }
            if (model.FindCanvasObject(id) is { } o) { objs.Add(o); continue; }
            if (model.FindWire(id) is { } w && wireIds.Add(w.Id)) wires.Add(w);
        }
        if (comps.Count == 0 && objs.Count == 0 && wires.Count == 0) return t;

        // ── Pivot ────────────────────────────────────────────────────────────
        // One component on its own turns about its own origin and does not move — the historical
        // behaviour, and the one that makes "place a part, press R" feel like re-orienting a part
        // rather than sliding it. Anything larger pivots on the group's centroid, snapped to the
        // connection grid so every pin lands back on it.
        double px, py;
        if (comps.Count == 1 && objs.Count == 0)
        {
            px = comps[0].X;
            py = comps[0].Y;
        }
        else
        {
            var anchors = comps.Select(c => (X: c.X, Y: c.Y))
                               .Concat(objs.Select(o => (X: o.X, Y: o.Y)))
                               .ToList();
            if (anchors.Count == 0)
                anchors = wires.SelectMany(w => w.Points).Select(p => (X: p.X, Y: p.Y)).ToList();
            px = model.SnapToGrid(anchors.Average(a => a.X));
            py = model.SnapToGrid(anchors.Average(a => a.Y));
        }

        (double X, double Y) Map(double x, double y)
        {
            var (dx, dy) = spec.MapOffset(x - px, y - py);
            return (px + dx, py + dy);
        }

        // ── Components ───────────────────────────────────────────────────────
        var moved = new List<PinFollowReroute.Transformed>(comps.Count);
        foreach (var c in comps)
        {
            var (nx, ny) = Map(c.X, c.Y);
            var newRot    = spec.MapRotation(c.Rotation);
            var newMirror = spec.TogglesMirrorX ? !c.MirrorX : c.MirrorX;
            t.Components.Add(new ComponentTransformSnapshot(
                c, c.X, c.Y, c.Rotation, c.MirrorX, nx, ny, newRot, newMirror));
            moved.Add(new PinFollowReroute.Transformed(c, nx, ny, newRot, newMirror));
        }

        foreach (var o in objs)
        {
            var (nx, ny) = Map(o.X, o.Y);
            t.Objects.Add(new CanvasObjectTransformSnapshot(
                o, o.X, o.Y, o.RotationDeg, nx, ny, spec.MapObjectAngleDeg(o.RotationDeg)));
        }

        // ── Wires carried whole ──────────────────────────────────────────────
        // Selected outright, or implied: both ends on pins of components that are moving. An implied
        // wire is not a guess — its two ends are pinned to two members of the group, so it has
        // nowhere else to be, and carrying it keeps its bends rather than re-drawing them.
        foreach (var w in model.Wires)
        {
            if (wireIds.Contains(w.Id)) continue;
            if (w.Points.Count < 2) continue;
            if (BothEndsOnMovingPin(model, w, moved)) { wires.Add(w); wireIds.Add(w.Id); }
        }

        var carried = new Dictionary<string, IReadOnlyList<(double X, double Y)>>(StringComparer.Ordinal);
        foreach (var w in wires)
        {
            var pts = w.Points.Select(p => Map(p.X, p.Y)).ToList();
            carried[w.Id] = pts;
            t.Wires.Add(new WireMoveSnapshot(w, w.Points.ToList(), pts));
        }

        // ── Junction dots ────────────────────────────────────────────────────
        // A user dot marks a crossing. It rides along only when every wire through it is being
        // carried; if one of them is staying put the crossing is coming apart either way, and moving
        // the dot would only strand it somewhere nothing crosses.
        foreach (var dot in model.Dots)
        {
            bool onCarried = false, onStaying = false;
            foreach (var w in model.Wires)
            {
                if (!WireGeometry.PointOnWire(w, dot.X, dot.Y, SchematicEditModel.ConnectTolerance)) continue;
                if (wireIds.Contains(w.Id)) onCarried = true; else onStaying = true;
            }
            if (!onCarried || onStaying) continue;
            var (nx, ny) = Map(dot.X, dot.Y);
            t.Dots.Add(new DotMoveSnapshot(dot, dot.X, dot.Y, nx, ny));
        }

        // ── Wires straddling the boundary ────────────────────────────────────
        t.Wires.AddRange(PinFollowReroute.Build(model, moved, carried));
        return t;
    }

    /// <summary>True when both of this wire's ends sit on a pin of a component that is moving.</summary>
    private static bool BothEndsOnMovingPin(
        SchematicEditModel model, EditableWire w, List<PinFollowReroute.Transformed> moved)
    {
        var (sx, sy) = w.Points[0];
        var (ex, ey) = w.Points[^1];
        bool s = false, e = false;
        foreach (var m in moved)
            foreach (var def in model.PortDefsOf(m.Comp))
            {
                if (m.Comp.IsPortDetached(def.PortIndex)) continue;
                var (wx, wy) = SchematicGeometry.LocalToWorld(
                    def.LocalX, def.LocalY, m.Comp.X, m.Comp.Y, m.Comp.Rotation, m.Comp.MirrorX);
                if (SchematicGeometry.CoincidentPoints(sx, sy, wx, wy, SchematicEditModel.ConnectTolerance)) s = true;
                if (SchematicGeometry.CoincidentPoints(ex, ey, wx, wy, SchematicEditModel.ConnectTolerance)) e = true;
            }
        return s && e;
    }

    public void Apply()
    {
        foreach (var s in Components)
        {
            s.Comp.X = s.EndX; s.Comp.Y = s.EndY;
            s.Comp.Rotation = s.EndRot; s.Comp.MirrorX = s.EndMirrorX;
        }
        foreach (var s in Objects) { s.Obj.X = s.EndX; s.Obj.Y = s.EndY; s.Obj.RotationDeg = s.EndDeg; }
        foreach (var s in Wires)   { s.Wire.Points.Clear(); s.Wire.Points.AddRange(s.EndPoints); }
        foreach (var s in Dots)    { s.Dot.X = s.EndX; s.Dot.Y = s.EndY; }
    }

    public void Revert()
    {
        foreach (var s in Components)
        {
            s.Comp.X = s.StartX; s.Comp.Y = s.StartY;
            s.Comp.Rotation = s.StartRot; s.Comp.MirrorX = s.StartMirrorX;
        }
        foreach (var s in Objects) { s.Obj.X = s.StartX; s.Obj.Y = s.StartY; s.Obj.RotationDeg = s.StartDeg; }
        foreach (var s in Wires)   { s.Wire.Points.Clear(); s.Wire.Points.AddRange(s.StartPoints); }
        foreach (var s in Dots)    { s.Dot.X = s.StartX; s.Dot.Y = s.StartY; }
    }
}
