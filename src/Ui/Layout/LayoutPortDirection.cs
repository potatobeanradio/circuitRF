// Framework-free. No Avalonia, no SkiaSharp — the renderer, the editor and the EM extractor all
// read this, and only the renderer is allowed to know about Skia.

using System.Collections.Generic;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// An EM port's DIRECTION — the way current flows INTO the structure — and the conductor width it
/// spans, resolved from a port <see cref="LabelShape"/> and the artwork beneath it.
///
/// <para><b>The convention, stated once and never re-derived at a call site:</b> <c>R0</c> = +x̂,
/// <c>R90</c> = +ŷ, <c>R180</c> = −x̂, <c>R270</c> = −ŷ — the usual counter-clockwise convention in
/// layout's y-up world. A port whose current flows +x̂ sits on the conductor's LOW-x end, so the
/// direction points AWAY from the end the label is on and INTO the metal. Get that backwards and
/// S₂₁ picks up a hard π that no magnitude plot can show.</para>
///
/// <para><b>Why this exists as its own file (owner report, 2026-08-09).</b> Before it, a port was a
/// label with a flag: it drew as text, the Properties Inspector called it a Label, and its side was
/// inferred from geometry at extraction time and nowhere else — so there was nothing on screen
/// saying which way it faced and nothing to rotate. This is the one place the direction↔geometry
/// relationship is written down, shared by the Port tool (which seeds it), the renderer (which draws
/// it) and <c>EmPortExtraction</c> (which consumes it).</para>
/// </summary>
public static class LayoutPortDirection
{
    /// <summary>What a resolved port marker needs to draw itself, and what extraction needs to
    /// know: which way current flows in, how wide the conductor is across that direction, and —
    /// owner report, 2026-08-09 ("I can't tell from the port glyph where the actual reference plane
    /// is") — WHERE that plane sits.
    ///
    /// <para><b><see cref="PlaneX"/>/<see cref="PlaneY"/> is the centre of the conductor END the port
    /// names, not the label's own anchor.</b> The two differ whenever the user clicked somewhere
    /// other than exactly the end of the metal, which is nearly always — and drawing the width bar at
    /// the anchor is what made the plane's position ambiguous. The kernel's own plane is fixed one
    /// mesh cell IN from this edge (<c>PlanarPort</c> D2), a sub-cell offset no drawn glyph can
    /// honestly resolve; the post-run overlay (§10.6) draws the engine's real planes over its own
    /// coordinates.</para>
    ///
    /// <para><see cref="LengthDbu"/> is the conductor's extent ALONG the direction — how much metal
    /// there is for the arrow to point into. Without it the marker has no way to know it is about to
    /// draw past the end of the thing it annotates (owner report, 2026-08-09).</para></summary>
    public readonly record struct PortHint(
        LayoutRotation Direction, long WidthDbu, bool Inferred, long PlaneX, long PlaneY, long LengthDbu);

    /// <summary>
    /// A cell PIN's own exact statement about the conductor end at a point: where the edge is, how
    /// wide it is, and which way current flows into the metal from it.
    ///
    /// <para><b>This is a MEASUREMENT, where <see cref="Bbox"/> is an approximation</b> — see
    /// <see cref="ConductorInfo"/> for why the distinction is load-bearing.</para>
    /// </summary>
    public readonly record struct PinFacts(long X, long Y, long WidthDbu, LayoutRotation Direction);

    /// <summary>
    /// What the artwork under a point says about the conductor there: always a bounding box, and —
    /// when the point names a cell pin — that pin's own exact facts.
    ///
    /// <para><b>Why the pin has to be carried separately (owner report, 2026-08-09: "the Port
    /// snapping and resultant port width is incorrect on Port 1 of my MKLOPF").</b> A port placed on
    /// a placed <see cref="LayoutInstance"/> used to resolve against the instance's array-expanded
    /// BOUNDING BOX, because that was the only thing the lookup could return. For a straight run of
    /// metal that is a fair approximation. For a TAPER it is not remotely one: on the reported
    /// design the box spans the whole 63 × 9 mm envelope, so the port reported a width of 9.23 mm
    /// against the pin's real 1.06 mm (8.7× too wide) and drew its reference-plane bar at the box's
    /// mid-height — 4.01 mm away from where pin 1 actually is, which is what reads as the snap being
    /// wrong: the marker lands nowhere near the point that was clicked.</para>
    ///
    /// <para><b>The cell already knew the answer.</b> <see cref="LayoutPin"/> has carried
    /// <see cref="LayoutPin.WidthDbu"/> and <see cref="LayoutPin.OutwardDeg"/> since pins became
    /// first-class — precisely "an edge, with a width and a direction". Nothing needed measuring;
    /// the lookup simply had no way to say it. <b>A bbox is what you fall back to when there is no
    /// pin, not the thing you prefer.</b></para>
    /// </summary>
    public readonly record struct ConductorInfo(Bbox Box, PinFacts? Pin);

    /// <summary>
    /// Where a conductor is looked up from a point. A delegate rather than a shape list because the
    /// answer has two sources that cannot be unified into one collection: top-level shapes, and
    /// geometry that only exists inside a placed <see cref="LayoutInstance"/> (a PCell's artwork,
    /// which is exactly what "Update Layout from Schematic" produces — see
    /// <see cref="LookupFor(LayoutView, Technology?, string, long)"/>).
    /// </summary>
    public delegate ConductorInfo? ConductorLookup(long x, long y);

    /// <summary>The unit vector current flows along, entering the structure. Integer, because the
    /// four cases are axis-aligned by construction.</summary>
    public static (int X, int Y) UnitVector(LayoutRotation r) => r switch
    {
        LayoutRotation.R0   => (1, 0),
        LayoutRotation.R90  => (0, 1),
        LayoutRotation.R180 => (-1, 0),
        _                   => (0, -1),
    };

    /// <summary>The perpendicular unit vector — the axis the port's width bar is drawn across.</summary>
    public static (int X, int Y) PerpendicularVector(LayoutRotation r)
    {
        var (ux, uy) = UnitVector(r);
        return (-uy, ux);
    }

    /// <summary>
    /// Which direction a port at <paramref name="x"/>,<paramref name="y"/> faces, given the
    /// conductor bounding box it sits on: the NEAREST side is the end the port names, and current
    /// flows away from it into the metal.
    /// </summary>
    public static LayoutRotation FromBbox(Bbox bb, long x, long y)
    {
        long dMinX = System.Math.Abs(x - bb.MinX);
        long dMaxX = System.Math.Abs(bb.MaxX - x);
        long dMinY = System.Math.Abs(y - bb.MinY);
        long dMaxY = System.Math.Abs(bb.MaxY - y);

        long best = dMinX;
        var dir = LayoutRotation.R0;                 // low-x end  -> current flows +x̂
        if (dMaxX < best) { best = dMaxX; dir = LayoutRotation.R180; }
        if (dMinY < best) { best = dMinY; dir = LayoutRotation.R90;  }
        if (dMaxY < best) {               dir = LayoutRotation.R270; }
        return dir;
    }

    /// <summary>
    /// The centre of the conductor END the port names — the reference plane's own position. Current
    /// flows away from this edge into the metal, so the edge is the one OPPOSITE the direction:
    /// <c>R0</c> (current +x̂) names the LOW-x edge, and so on.
    /// </summary>
    public static (long X, long Y) PlaneOf(Bbox bb, LayoutRotation direction)
    {
        long midX = bb.MinX + (bb.MaxX - bb.MinX) / 2;
        long midY = bb.MinY + (bb.MaxY - bb.MinY) / 2;
        return direction switch
        {
            LayoutRotation.R0   => (bb.MinX, midY),
            LayoutRotation.R180 => (bb.MaxX, midY),
            LayoutRotation.R90  => (midX, bb.MinY),
            _                   => (midX, bb.MaxY),
        };
    }

    /// <summary>The conductor's extent ACROSS <paramref name="direction"/> — the port's width.</summary>
    public static long WidthAcross(Bbox bb, LayoutRotation direction) =>
        direction is LayoutRotation.R0 or LayoutRotation.R180
            ? bb.MaxY - bb.MinY
            : bb.MaxX - bb.MinX;

    /// <summary>
    /// How much metal runs AHEAD of a pin — the same quantity <see cref="LengthAlong"/> reports for a
    /// whole box, measured from the pin's own position instead of from the box's far edge, because a
    /// pin sits ON one end rather than spanning the extent.
    /// </summary>
    public static long LengthAheadOf(Bbox bb, long x, long y, LayoutRotation direction)
    {
        long len = direction switch
        {
            LayoutRotation.R0   => bb.MaxX - x,
            LayoutRotation.R180 => x - bb.MinX,
            LayoutRotation.R90  => bb.MaxY - y,
            _                   => y - bb.MinY,
        };
        return len > 0 ? len : 0;
    }

    /// <summary>
    /// The direction current flows INTO the structure at a pin — the OPPOSITE of the pin's own
    /// outward direction, which points out of the cell. Snapped to the nearest quadrant: a pin whose
    /// outward direction is not axis-aligned has no representable port direction, and rounding is a
    /// better seed than refusing one the user can rotate.
    /// </summary>
    public static LayoutRotation FromPinOutward(double outwardDeg)
    {
        double inward = outwardDeg + 180.0;
        int q = ((int)System.Math.Round(inward / 90.0) % 4 + 4) % 4;
        return q switch
        {
            0 => LayoutRotation.R0,
            1 => LayoutRotation.R90,
            2 => LayoutRotation.R180,
            _ => LayoutRotation.R270,
        };
    }

    /// <summary>The conductor's extent ALONG <paramref name="direction"/> — how far the current has
    /// to run, and therefore how much room the direction arrow actually has.</summary>
    public static long LengthAlong(Bbox bb, LayoutRotation direction) =>
        direction is LayoutRotation.R0 or LayoutRotation.R180
            ? bb.MaxX - bb.MinX
            : bb.MaxY - bb.MinY;

    /// <summary>
    /// The smallest-area shape whose bounding box contains the point, excluding labels and bitmaps
    /// (neither is conductor). Bounding boxes rather than exact containment, deliberately: this
    /// answer only ever SEEDS a direction the user can then rotate, and it is recomputed per frame
    /// for a handful of port labels. Exact geometry is <c>EmPortExtraction</c>'s job, where a wrong
    /// answer is refused rather than merely drawn.
    /// </summary>
    public static Bbox? ConductorUnder(IReadOnlyList<LayoutShape> shapes, long x, long y)
    {
        Bbox? best = null;
        double bestArea = double.MaxValue;
        foreach (var s in shapes)
        {
            if (s is LabelShape or BitmapShape) continue;
            var bb = LayoutGeometry.BboxOf(s);
            if (bb.IsEmpty) continue;
            if (x < bb.MinX || x > bb.MaxX || y < bb.MinY || y > bb.MaxY) continue;

            double area = (double)(bb.MaxX - bb.MinX) * (bb.MaxY - bb.MinY);
            if (area < bestArea) { bestArea = area; best = bb; }
        }
        return best;
    }

    /// <summary>
    /// Resolve a port label's direction and width.
    ///
    /// <para><b>The width is the CONDUCTOR's, always — the label's own text size never enters into
    /// it</b> (owner report, 2026-08-09: <i>"I made my port Text size 60 and placed it on the edge of
    /// a 42 mil wide MLIN. Now the Port width is saying it's 60. I thought the port width was always
    /// a function of the edge it touches."</i>). Both branches below used to floor the width at
    /// <c>label.Height</c> — a legibility hack for a marker that would otherwise draw thin — and that
    /// floor leaked straight into the number the Properties Inspector reports and into the width bar
    /// the marker draws. A port's width is a property of the metal; making it a function of a font
    /// size means the same artwork reports two different excitation widths depending on how big
    /// someone typed the label. <b>A marker too small to see is a zoom problem, not a data
    /// problem.</b></para>
    ///
    /// <para>An explicit
    /// <see cref="LabelShape.PortDirection"/> is honoured as given (the user pointed it); a null one
    /// is inferred from the artwork, which is what every <c>.clay</c> written before the field
    /// existed carries. Returns null only when the label is not a port at all.
    /// </summary>
    public static PortHint? Resolve(IReadOnlyList<LayoutShape> shapes, LabelShape label) =>
        Resolve(LookupFor(shapes), label);

    /// <summary>
    /// Resolve against an arbitrary conductor source — the form the renderer and the Port tool both
    /// use, so a port sitting on a PCell INSTANCE (which owns no top-level shape at all) resolves
    /// exactly like one sitting on a drawn rectangle.
    /// </summary>
    public static PortHint? Resolve(ConductorLookup? conductorAt, LabelShape label)
    {
        if (!label.IsPort) return null;

        var info = conductorAt?.Invoke(label.X, label.Y);

        if (label.PortDirection is { } stated)
        {
            // No conductor found: the direction is still the user's, so the marker is still drawn —
            // but the plane can only be the anchor, and the width is a legible stand-in rather than
            // a measurement. EmPortExtraction refuses such a port by name at run time.
            if (info is not { } si)
                return new PortHint(stated, label.Height * 2, Inferred: false, label.X, label.Y,
                                    LengthDbu: label.Height * 2);

            // A pin's width is measured across the pin's OWN axis, so it only answers the question
            // being asked while the stated direction still agrees with it. A user who rotated the
            // port has overruled the geometry; measuring the box across their chosen axis is then the
            // honest answer, even though it is the coarser one.
            if (si.Pin is { } sp && sp.Direction == stated)
                return new PortHint(stated, sp.WidthDbu, Inferred: false, sp.X, sp.Y,
                                    LengthAheadOf(si.Box, sp.X, sp.Y, stated));

            var (spx, spy) = PlaneOf(si.Box, stated);
            return new PortHint(stated, WidthAcross(si.Box, stated),
                                Inferred: false, spx, spy, LengthAlong(si.Box, stated));
        }

        if (info is not { } inf) return null;

        if (inf.Pin is { } pin)
            return new PortHint(pin.Direction, pin.WidthDbu, Inferred: true, pin.X, pin.Y,
                                LengthAheadOf(inf.Box, pin.X, pin.Y, pin.Direction));

        var dir = FromBbox(inf.Box, label.X, label.Y);
        var (px, py) = PlaneOf(inf.Box, dir);
        return new PortHint(dir, WidthAcross(inf.Box, dir),
                            Inferred: true, px, py, LengthAlong(inf.Box, dir));
    }

    /// <summary>
    /// Which direction a port placed at a point should face, from whatever the artwork there says —
    /// the pin's own inward direction when the point names a pin, else the nearest-side inference
    /// from the conductor's box. The Port tool stamps this at placement, so it must be the SAME
    /// answer <see cref="Resolve(ConductorLookup?, LabelShape)"/> would infer; deriving it a second
    /// way is how a placed port comes to disagree with its own marker.
    /// </summary>
    public static LayoutRotation DirectionAt(ConductorInfo info, long x, long y) =>
        info.Pin is { } pin ? pin.Direction : FromBbox(info.Box, x, y);

    /// <summary>Top-level shapes only — the cheap form, and all a hand-drawn layout ever needs.</summary>
    public static ConductorLookup LookupFor(IReadOnlyList<LayoutShape> shapes) =>
        (x, y) => ConductorUnder(shapes, x, y) is { } bb ? new ConductorInfo(bb, null) : null;

    /// <summary>
    /// The full form: top-level shapes FIRST (exact hit-testing, so a click on an edge counts and a
    /// hidden or non-selectable layer does not), then placed instances.
    ///
    /// <para><b>Why instances have to be in here (owner report, 2026-08-09: "placing a port does not
    /// set a direction, when I placed it by clicking on the metal").</b> A layout built by "Update
    /// Layout from Schematic" is ALL instances and no top-level shapes, so a shapes-only lookup finds
    /// nothing on artwork the user can plainly see, and the port silently gets no direction at all.
    /// </para>
    ///
    /// <para><b>An instance answers with its PIN when the point names one, and only falls back to
    /// its bbox otherwise</b> (owner report, 2026-08-09). The array-expanded bbox is a fair seed for
    /// a straight run of metal and a badly wrong one for anything else — an MTee's box spans both
    /// arms, and a TAPER's spans a width it has nowhere along its length. Since a cell's pins carry
    /// an exact width and outward direction, preferring them makes the common case (a port placed on
    /// a PCell's own pin, which is what the snap lands on) exact instead of approximate. The bbox
    /// survives for a port placed on the metal but NOT at a pin, where there is genuinely nothing
    /// better to say. <c>EmPortExtraction</c> still re-derives the side from exact flattened geometry
    /// and refuses rather than guessing — it does not read this at all.</para>
    ///
    /// <para><paramref name="tolDbu"/> is how close the point must be to a pin to be naming it. Zero
    /// — every caller's default — means exact coincidence, which is precisely what a port snapped
    /// onto a pin has, and correctly declines to claim a pin the user placed the port merely NEAR.</para>
    /// </summary>
    public static ConductorLookup LookupFor(LayoutView view, Technology? tech, string baseDir, long tolDbu = 0)
        => (x, y) =>
        {
            foreach (int i in LayoutHitTest.HitStack(view, tech, x, y, tolDbu))
            {
                if (view.Shapes[i] is LabelShape or BitmapShape) continue;
                var bb = LayoutGeometry.BboxOf(view.Shapes[i]);
                if (!bb.IsEmpty) return new ConductorInfo(bb, null);
            }

            foreach (int i in LayoutHitTest.HitInstanceStack(view, tech, baseDir, x, y, tolDbu))
            {
                var inst = view.Instances[i];
                var bb = CellHierarchy.InstanceBbox(inst, baseDir);
                if (bb.IsEmpty) continue;
                return new ConductorInfo(bb, PinAt(inst, baseDir, tech, x, y, tolDbu));
            }

            return null;
        };

    /// <summary>
    /// The pin of <paramref name="inst"/>'s sub-cell that <paramref name="x"/>,<paramref name="y"/>
    /// names, in the PARENT's frame — nearest within <paramref name="tolDbu"/>, or null.
    ///
    /// <para>Walks every array placement, exactly as <c>LayoutSnapQuery</c>'s own instance recursion
    /// does and at the same cost: a placement is a handful of integer operations per pin, and the
    /// caller has already narrowed to instances whose bbox contains the point.</para>
    /// </summary>
    private static PinFacts? PinAt(LayoutInstance inst, string baseDir, Technology? tech,
                                   long x, long y, long tolDbu)
    {
        var res = CellLayoutResolver.Resolve(inst.CellRef, baseDir);
        if (res.State != CellLayoutState.Resolved) return null;

        var pins = CellPins.Resolve(res.View!, tech);
        if (pins.Count == 0) return null;

        int rows = System.Math.Max(1, inst.Rows), cols = System.Math.Max(1, inst.Cols);
        double bestSq = (double)tolDbu * tolDbu;
        PinFacts? best = null;

        for (int r = 0; r < rows; r++)
        for (int c = 0; c < cols; c++)
        foreach (var pin in pins)
        {
            var (wx, wy) = LayoutInstanceTransform.TransformPoint(pin.X, pin.Y, inst, r, c);
            double dx = wx - x, dy = wy - y;
            double d2 = dx * dx + dy * dy;
            if (d2 > bestSq) continue;

            bestSq = d2;
            best = new PinFacts(wx, wy,
                                ScaleWidth(pin.WidthDbu, inst.Mag),
                                TransformDirection(FromPinOutward(pin.OutwardDeg), inst));
        }

        // A pin that states no width is a connection point with nothing to say about the metal's
        // extent; falling through to the box is more honest than reporting a zero-width port.
        return best is { WidthDbu: > 0 } ? best : null;
    }

    private static long ScaleWidth(long widthDbu, double mag)
    {
        double w = widthDbu * System.Math.Abs(mag);
        return w > 0 ? (long)System.Math.Round(w) : 0;
    }

    /// <summary>
    /// Carries a cell-local direction into the parent's frame. Mirror-then-rotate, the SAME ordering
    /// (and the same rotation table) as <see cref="LayoutInstanceTransform.TransformPoint"/> — a
    /// direction that composed differently from the position it belongs to would put the arrow and
    /// the plane bar on different sides of the same pin.
    /// </summary>
    private static LayoutRotation TransformDirection(LayoutRotation local, LayoutInstance inst)
    {
        var (ux, uy) = UnitVector(local);
        if (inst.MirrorX) ux = -ux;

        var (rx, ry) = inst.Rot switch
        {
            LayoutRotation.R90  => (-uy, ux),
            LayoutRotation.R180 => (-ux, -uy),
            LayoutRotation.R270 => (uy, -ux),
            _                   => (ux, uy),
        };

        if (rx > 0) return LayoutRotation.R0;
        if (rx < 0) return LayoutRotation.R180;
        return ry >= 0 ? LayoutRotation.R90 : LayoutRotation.R270;
    }
}
