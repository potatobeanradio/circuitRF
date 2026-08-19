namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Where a pasted schematic fragment lands.
///
/// Paste used to be paste-IN-PLACE: the fragment kept its source coordinates, so copy-paste inside
/// one schematic dropped every object EXACTLY on top of its original. That is not merely untidy —
/// it is unusable: every pasted wire endpoint then coincides with an unselected original port, which
/// pins the pasted wires (they re-route instead of moving) and sprouts an auto-wire stub per pin the
/// moment the selection is dragged. Landing the fragment where the user is looking, and never
/// exactly on existing content, is what makes the pasted copy draggable at all.
///
/// The rule, in world coordinates:
/// <list type="bullet">
/// <item>No viewport known (headless, or the canvas has never been laid out) → paste in place.</item>
/// <item>Fragment bbox fully inside the viewport → keep it where it is, offset by one connection
///       grid step, so the copy is visibly distinct from its original without jumping away.</item>
/// <item>Otherwise (the user panned away, or the fragment is bigger than the view) → translate so
///       the fragment's bbox centre lands on the viewport centre.</item>
/// </list>
/// The delta is always a whole multiple of the connection grid P, so every pasted connection point
/// stays exactly on grid (P is itself a whole multiple of the fine author grid p, so canvas objects
/// stay on their grid too). A final pass nudges the fragment diagonally if it would still land with
/// a component exactly on an existing component's origin.
/// </summary>
public static class SchematicPasteGeometry
{
    /// <summary>World-coordinate rectangle currently visible on the canvas.</summary>
    public readonly record struct ViewRect(double MinX, double MinY, double MaxX, double MaxY)
    {
        public double CenterX => (MinX + MaxX) / 2.0;
        public double CenterY => (MinY + MaxY) / 2.0;
        public bool Contains(double minX, double minY, double maxX, double maxY) =>
            minX >= MinX && maxX <= MaxX && minY >= MinY && maxY <= MaxY;
    }

    /// <summary>
    /// Bounding box of a fragment, from component origins, wire vertices and canvas-object boxes.
    /// Returns null when the fragment has no geometry at all.
    /// </summary>
    public static (double MinX, double MinY, double MaxX, double MaxY)? BoundingBox(
        IReadOnlyList<EditableComponent> comps,
        IReadOnlyList<EditableWire> wires,
        IReadOnlyList<EditableCanvasObject> cobjs)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;

        void Add(double x, double y)
        {
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
        }

        foreach (var c in comps) Add(c.X, c.Y);
        foreach (var w in wires)
            foreach (var (x, y) in w.Points) Add(x, y);
        foreach (var o in cobjs)
        {
            var bb = o.GetBoundingBox();
            Add(bb.MinX, bb.MinY);
            Add(bb.MaxX, bb.MaxY);
        }

        return double.IsInfinity(minX) ? null : (minX, minY, maxX, maxY);
    }

    /// <summary>
    /// Delta to apply to a pasted fragment. <paramref name="occupied"/> reports whether a component
    /// origin already exists at (x, y) in the destination model — used only to break an exact
    /// overlap; pass null to skip that pass.
    /// </summary>
    public static (double Dx, double Dy) Offset(
        IReadOnlyList<EditableComponent> comps,
        IReadOnlyList<EditableWire> wires,
        IReadOnlyList<EditableCanvasObject> cobjs,
        ViewRect? view,
        double gridP,
        Func<double, double, bool>? occupied = null)
    {
        if (BoundingBox(comps, wires, cobjs) is not { } bb) return (0, 0);
        double p = gridP > 0 ? gridP : 100.0;

        double dx, dy;
        if (view is not { } v)
        {
            dx = dy = 0;                                    // headless / no canvas — paste in place
        }
        else if (v.Contains(bb.MinX, bb.MinY, bb.MaxX, bb.MaxY))
        {
            dx = dy = p;                                    // already in view — one step off the source
        }
        else
        {
            double fx = (bb.MinX + bb.MaxX) / 2.0;
            double fy = (bb.MinY + bb.MaxY) / 2.0;
            dx = Math.Round((v.CenterX - fx) / p) * p;
            dy = Math.Round((v.CenterY - fy) / p) * p;
        }

        // Never land a component exactly on an existing one: that is the state in which a dragged
        // selection pins itself to the content underneath it. Step diagonally until clear.
        if (occupied is not null && comps.Count > 0)
        {
            for (int step = 0; step < 20; step++)
            {
                bool clash = false;
                foreach (var c in comps)
                    if (occupied(c.X + dx, c.Y + dy)) { clash = true; break; }
                if (!clash) break;
                dx += p;
                dy += p;
            }
        }

        return (dx, dy);
    }

    /// <summary>Translates a fragment in place by (dx, dy).</summary>
    public static void Translate(
        IReadOnlyList<EditableComponent> comps,
        IReadOnlyList<EditableWire> wires,
        IReadOnlyList<EditableCanvasObject> cobjs,
        double dx, double dy)
    {
        if (dx == 0 && dy == 0) return;

        foreach (var c in comps) { c.X += dx; c.Y += dy; }
        foreach (var w in wires)
            for (int i = 0; i < w.Points.Count; i++)
                w.Points[i] = (w.Points[i].X + dx, w.Points[i].Y + dy);
        foreach (var o in cobjs) { o.X += dx; o.Y += dy; }
    }
}
