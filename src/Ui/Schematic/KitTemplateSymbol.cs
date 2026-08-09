using CircuitRF.Core.Pdk;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Builds a placeable symbol from what a kit states about one part.
///
/// <para>There are two sources and they are NOT interchangeable. A symbol LIBRARY gives only named
/// terminals at known positions, shared by many parts — so the pins are the kit's and the body is
/// circuitRF's own box, because drawing artwork we were never given would be inventing the kit's.
/// A record symbol FILE gives the terminals AND the drawing, so both are the kit's.</para>
///
/// <para><b>The two also disagree about which way the y axis points, which is why they are separate
/// entry points rather than one with a flag.</b> The library format is y-up; the drawing format is
/// y-down, the same sense circuitRF's own symbol coordinates use. Running a drawing through the
/// library's flip turns every part upside down — invisible while the body was a symmetric box, and
/// glaring the moment a real drawing is rendered.</para>
///
/// <para><b>Pin placement goes through the same scale and snap the drawing reader uses</b> —
/// deliberately shared rather than reimplemented. Pins must land on exact multiples of the
/// connection grid or a wire will not attach, and two rules for that would drift apart at the first
/// change.</para>
///
/// <para><b><see cref="DsnSymbolReader.TranslationVersion"/> governs every source that arrives
/// here.</b> A workspace records the version its kits were translated under, so a change to the
/// scale, snap, axis handling or ordering below MOVES pins and needs the bump. Moved pins silently
/// disconnect wires the user already placed, which is the failure that rule exists for.</para>
/// </summary>
internal static class KitTemplateSymbol
{
    /// <summary>Half-size the body never shrinks below, so a colinear part still has one.</summary>
    private const double MinHalfSpan = DsnSymbolReader.PinGrid;

    /// <summary>
    /// From a symbol LIBRARY's terminals: the kit's pins, a box body of circuitRF's own.
    /// Null when the template declares no terminals — there is nothing to place.
    /// </summary>
    internal static Symbol? Build(IReadOnlyList<KitSymbolPin>? pins)
    {
        if (pins is null || pins.Count == 0) return null;

        double scale  = ChooseScaleFor(pins, body: null);
        var    placed = PlacePins(pins, scale, flipY: true);   // library is y-up, symbols y-down

        return new Symbol(BoxBodyFor(placed), placed);
    }

    /// <summary>
    /// From a record symbol FILE: the kit's pins AND the kit's own drawing, at the scale chosen for
    /// the WHOLE kit (<see cref="ChooseKitScale"/>).
    ///
    /// <para>Falls back to the box body when the file declared terminals and drew nothing — an
    /// honest outcome for an annotation-only symbol, and the same thing the library path produces.</para>
    /// </summary>
    internal static Symbol? BuildFromDrawing(IReadOnlyList<KitSymbolPin>? pins,
                                             IReadOnlyList<KitSymbolShape>? body,
                                             double scale)
    {
        if (pins is null || pins.Count == 0) return null;
        if (!double.IsFinite(scale) || scale <= 0) scale = ChooseScaleFor(pins, body);

        var placed = PlacePins(pins, scale, flipY: false);      // drawing is already y-down

        // Where each pin WOULD have been without the connection-grid snap, keyed by the file's own
        // untouched coordinates so the match is exact rather than a tolerance. See PinFollow below.
        var follow = new Dictionary<(double X, double Y), (double X, double Y)>(pins.Count);
        for (int i = 0; i < pins.Count; i++)
            follow[(pins[i].X, pins[i].Y)] = (placed[i].LocalX, placed[i].LocalY);

        var attached = new HashSet<(double X, double Y)>();
        var drawn = new List<SymbolPrimitive>();
        foreach (var shape in body ?? [])
            if (Convert(shape, scale, follow, attached) is { } prim) drawn.Add(prim);

        if (drawn.Count == 0) return new Symbol(BoxBodyFor(placed), placed);

        drawn.AddRange(StubsForDetachedPins(pins, placed, scale, attached));
        return new Symbol(drawn, placed);
    }

    /// <summary>
    /// A short lead from a pin's snapped position back to where the scale alone put it, for any pin
    /// the drawing does not already reach.
    ///
    /// <para><b>Why any pin needs one.</b> Pins snap to the connection grid and artwork does not —
    /// they have to, or a wire cannot attach — so the snap displaces a pin from the lead the kit drew
    /// to it, by up to half a grid step. Measured on a real open kit: 372 of 374 pins move, the worst
    /// by nearly half a step, which is exactly the reported "the pins render out in white space of
    /// the symbol". <see cref="PinFollow"/> closes that for the 93% of pins the drawing puts a vertex
    /// on; this is the remainder — a pin sitting on the INTERIOR of a shape, or at the base of a
    /// filled arrow, where there is no vertex to move.</para>
    ///
    /// <para>Under half a connection grid step is left alone: at that distance the pin marker still
    /// overlaps the metal it belongs to, and a stub shorter than the marker is only clutter.</para>
    /// </summary>
    private static IEnumerable<SymbolPrimitive> StubsForDetachedPins(
        IReadOnlyList<KitSymbolPin> pins, List<SymbolPin> placed, double scale,
        HashSet<(double X, double Y)> attached)
    {
        for (int i = 0; i < pins.Count; i++)
        {
            if (attached.Contains((pins[i].X, pins[i].Y))) continue;

            double ux = pins[i].X * scale, uy = pins[i].Y * scale;
            double dx = placed[i].LocalX - ux, dy = placed[i].LocalY - uy;
            if (dx * dx + dy * dy < StubThreshold * StubThreshold) continue;

            yield return new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Thin,
                                           placed[i].LocalX, placed[i].LocalY, ux, uy);
        }
    }

    /// <summary>Displacement below which a snapped pin still visually touches its own artwork, so no
    /// stub is drawn. A quarter of the connection grid is well inside the pin marker itself.</summary>
    private const double StubThreshold = DsnSymbolReader.PinGrid / 4.0;

    /// <summary>
    /// A drawn vertex's position, moved to a pin's snapped position when the kit drew it exactly on
    /// that pin.
    ///
    /// <para><b>This is what keeps a kit's own lead lines attached to their terminals.</b> A symbol
    /// declares a terminal and draws a lead ending on it; scaling moves both together, but snapping
    /// moves only the pin, so the lead is left short (or long) by the snap displacement. Carrying the
    /// vertex along with the pin restores the drawing's own intent and costs nothing anywhere else —
    /// the match is on the file's untouched coordinates, so only a vertex the kit put ON the terminal
    /// is ever moved.</para>
    ///
    /// <para>Matched on the raw coordinates rather than after scaling, deliberately: the kit's
    /// coincidence is exact in its own units, and comparing scaled doubles would need a tolerance
    /// that either misses it or catches a neighbouring vertex that was never on the pin.</para>
    /// </summary>
    private static (double X, double Y) PinFollow(
        double x, double y, double scale,
        IReadOnlyDictionary<(double X, double Y), (double X, double Y)> follow,
        HashSet<(double X, double Y)> attached)
    {
        if (!follow.TryGetValue((x, y), out var snapped)) return (x * scale, y * scale);
        attached.Add((x, y));
        return snapped;
    }

    // ── shared placement ──────────────────────────────────────────────────────

    /// <summary>
    /// How large circuitRF's OWN symbols are, in symbol-local units: a built-in two-terminal part
    /// spans pin to pin from (0,−200) to (0,+200). This is the size a kit's parts are normalised
    /// against, so a kit component sits on the schematic at the same visual weight as an R or a C
    /// rather than towering over it.
    /// </summary>
    internal const double ReferenceSymbolExtent = 400.0;

    /// <summary>
    /// ONE scale for a whole kit, chosen so the kit's TYPICAL part matches
    /// <see cref="ReferenceSymbolExtent"/>.
    ///
    /// <para><b>Per kit, not per part, and that is the point.</b> A kit draws every one of its
    /// symbols in one coordinate system, so their relative sizes are a choice its author made — a
    /// transistor larger than a ground marker. Scaling each part to fill the same band independently
    /// throws that away and lands a tiny label on the schematic bigger than the device beside it.
    /// One scale keeps every proportion exactly as drawn.</para>
    ///
    /// <para><b>Which part sets it changed, and a power-of-ten ladder could not have fixed this.</b>
    /// This used to clamp the kit's LARGEST part into a legibility band a full decade wide, so where
    /// in that band a kit landed was an accident of its biggest symbol. Measured: its
    /// two-terminal parts came out 600 local units against a built-in's 400, and its capacitor 750 —
    /// consistently half again to twice the size of the circuitRF part beside it. No power of ten
    /// fixes that: the next rung down is 10x, which would have made the whole kit unreadably small.
    /// So the scale is continuous, and pins still land on the connection grid because
    /// <see cref="PlacePins"/> snaps them there afterwards — that was never the scale's job.</para>
    ///
    /// <para><b>The MEDIAN, not the largest or the mean.</b> What should match circuitRF's own parts
    /// is the kit's ordinary two-terminal component, not its five-pin bipolar and not its title
    /// block. A median is what makes one unusually large part unable to shrink the whole kit, which
    /// is exactly the failure the old largest-part rule had.</para>
    ///
    /// <para>Still bounded: the largest part is held inside the legibility band afterwards, so a kit
    /// whose parts differ wildly in size cannot produce something absurd at either end.</para>
    ///
    /// <para>Returns 0 when the kit states no drawing-backed part to measure; callers fall back to
    /// their own per-part scale, which is what the symbol-library path uses throughout.</para>
    /// </summary>
    internal static double ChooseKitScale(
        IEnumerable<(IReadOnlyList<KitSymbolPin>? Pins, IReadOnlyList<KitSymbolShape>? Body)> parts)
    {
        var spans   = new List<double>();   // pin to pin — what the reference extent is
        var extents = new List<double>();   // pins AND artwork — what the legibility band bounds
        foreach (var (pins, body) in parts)
        {
            if (pins is null || pins.Count == 0) continue;
            double span = PinSpanOf(pins);
            if (span > 0) spans.Add(span);
            double extent = ExtentOf(pins, body);
            if (extent > 0) extents.Add(extent);
        }

        if (spans.Count == 0 || extents.Count == 0) return 0;

        spans.Sort();
        extents.Sort();
        double medianSpan = spans[spans.Count / 2];
        double largest    = extents[^1];

        // Normalised on the PIN SPAN, because that is what ReferenceSymbolExtent IS — a built-in
        // two-terminal part's own pin-to-pin distance. Normalising the DRAWING extent against it
        // compares two different quantities and makes every kit part smaller than a built-in by
        // however far its artwork reaches past its terminals: measured on a real open kit, the median
        // drawing extent is about twice the median pin span, so its parts came out at half the size
        // of the R or C beside them. Like for like, they land on it.
        double scale = ReferenceSymbolExtent / medianSpan;

        // The band is the same one the per-part fallback uses, applied to the kit's largest part so
        // nothing in it can end up microscopic or off the canvas. Only ever tightens the median
        // result; on a kit whose parts are all a similar size it never binds at all.
        scale = DsnSymbolReader.ClampScaleForExtent(scale, largest);
        return scale;
    }

    /// <summary>
    /// A power-of-ten scale for ONE part, so a kit authored in any drawing unit lands at a legible
    /// size without this code knowing anything about that kit. Used when there is no kit-wide scale.
    /// </summary>
    private static double ChooseScaleFor(IReadOnlyList<KitSymbolPin> pins,
                                         IReadOnlyList<KitSymbolShape>? body)
        => DsnSymbolReader.ChooseScale(ExtentOf(pins, body));

    /// <summary>
    /// The larger of the drawing's two dimensions, over the pins AND the artwork together. Measuring
    /// the pins alone would scale a symbol whose artwork reaches well past its terminals — a ground
    /// marker, a wide body with close-set pins — by the wrong decade.
    /// </summary>
    /// <summary>The larger of the two dimensions of the PINS' own bounding box — the quantity
    /// <see cref="ReferenceSymbolExtent"/> is expressed in.</summary>
    private static double PinSpanOf(IReadOnlyList<KitSymbolPin> pins)
        => Math.Max(pins.Max(p => (double)p.X) - pins.Min(p => (double)p.X),
                    pins.Max(p => (double)p.Y) - pins.Min(p => (double)p.Y));

    private static double ExtentOf(IReadOnlyList<KitSymbolPin> pins,
                                   IReadOnlyList<KitSymbolShape>? body)
    {
        double minX = pins.Min(p => (double)p.X), maxX = pins.Max(p => (double)p.X);
        double minY = pins.Min(p => (double)p.Y), maxY = pins.Max(p => (double)p.Y);

        foreach (var shape in body ?? [])
            foreach (var (x, y) in ExtentPointsOf(shape))
            {
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }

        return Math.Max(maxX - minX, maxY - minY);
    }

    private static IEnumerable<(double X, double Y)> ExtentPointsOf(KitSymbolShape shape)
    {
        switch (shape)
        {
            case KitSymbolLine l:
                yield return (l.X1, l.Y1);
                yield return (l.X2, l.Y2);
                break;
            case KitSymbolRectangle r:
                yield return (r.X1, r.Y1);
                yield return (r.X2, r.Y2);
                break;
            case KitSymbolPath p:
                for (int i = 0; i + 1 < p.Xy.Count; i += 2) yield return (p.Xy[i], p.Xy[i + 1]);
                break;
            case KitSymbolArc a:
                // The bounding box of the whole circle, not of the swept span. Over-measuring can
                // only choose a smaller scale, which is safe; under-measuring would clip artwork.
                yield return (a.Cx - a.Radius, a.Cy - a.Radius);
                yield return (a.Cx + a.Radius, a.Cy + a.Radius);
                break;
        }
    }

    private static List<SymbolPin> PlacePins(IReadOnlyList<KitSymbolPin> pins, double scale, bool flipY)
    {
        var placed = new List<SymbolPin>(pins.Count);
        for (int i = 0; i < pins.Count; i++)
        {
            double y = flipY ? -pins[i].Y * scale : pins[i].Y * scale;
            placed.Add(new SymbolPin(
                DsnSymbolReader.SnapToPinGrid(pins[i].X * scale),
                DsnSymbolReader.SnapToPinGrid(y),
                i + 1,
                string.IsNullOrWhiteSpace(pins[i].Name) ? (i + 1).ToString() : pins[i].Name));
        }
        return placed;
    }

    /// <summary>
    /// circuitRF's own body for a part that supplied no artwork: the pins' bounding box drawn in one
    /// grid, never smaller than <see cref="MinHalfSpan"/>, with a lead from each pin to it.
    ///
    /// <para>The floor is what keeps a two-terminal part — whose pins are colinear, so one dimension
    /// of the box is zero — from collapsing into a line the user cannot see or click.</para>
    /// </summary>
    private static List<SymbolPrimitive> BoxBodyFor(List<SymbolPin> placed)
    {
        double pMinX = placed.Min(p => p.LocalX), pMaxX = placed.Max(p => p.LocalX);
        double pMinY = placed.Min(p => p.LocalY), pMaxY = placed.Max(p => p.LocalY);
        double cx = (pMinX + pMaxX) / 2, cy = (pMinY + pMaxY) / 2;
        double hx = Math.Max((pMaxX - pMinX) / 2 - DsnSymbolReader.PinGrid, MinHalfSpan);
        double hy = Math.Max((pMaxY - pMinY) / 2 - DsnSymbolReader.PinGrid, MinHalfSpan);

        double bx0 = cx - hx, bx1 = cx + hx, by0 = cy - hy, by1 = cy + hy;

        var primitives = new List<SymbolPrimitive>
        {
            new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal, bx0, by0, bx1, by0),
            new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal, bx1, by0, bx1, by1),
            new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal, bx1, by1, bx0, by1),
            new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal, bx0, by1, bx0, by0),
        };

        // A lead from each pin to the nearest point on the body. Clamping rather than picking a side
        // keeps this correct for every arrangement a kit can state, including a pin that sits inside
        // the body — which draws nothing rather than a stray mark.
        foreach (var pin in placed)
        {
            double tx = Math.Clamp(pin.LocalX, bx0, bx1);
            double ty = Math.Clamp(pin.LocalY, by0, by1);
            if (Math.Abs(tx - pin.LocalX) > 0.5 || Math.Abs(ty - pin.LocalY) > 0.5)
                primitives.Add(new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Thin,
                                                 pin.LocalX, pin.LocalY, tx, ty));
        }

        return primitives;
    }

    // ── drawing conversion ────────────────────────────────────────────────────

    /// <summary>
    /// One drawn shape as a symbol primitive. Every shape carries circuitRF's own line role and
    /// stroke tier: the file's layer number is a colour choice made by the authoring editor, and
    /// honouring it would put a foreign palette on a schematic the user themes themselves.
    /// </summary>
    /// <remarks>
    /// Only the vertex-listed shapes take part in <see cref="PinFollow"/>. A rectangle and an arc are
    /// stated as a centre and a size, so moving one "corner" would resize the shape rather than
    /// reconnect it — a pin on one of those is left to <see cref="StubsForDetachedPins"/>.
    /// </remarks>
    private static SymbolPrimitive? Convert(
        KitSymbolShape shape, double s,
        IReadOnlyDictionary<(double X, double Y), (double X, double Y)> follow,
        HashSet<(double X, double Y)> attached) => shape switch
    {
        KitSymbolLine l => LineWithPinFollow(l, s, follow, attached),

        KitSymbolRectangle r => new RectPrimitive
        {
            ColorRole  = SymbolColorRole.SymbolLine,
            StrokeTier = SymbolStrokeTier.Normal,
            Filled     = r.Filled,
            Cx = (r.X1 + r.X2) / 2 * s,
            Cy = (r.Y1 + r.Y2) / 2 * s,
            W  = Math.Abs(r.X2 - r.X1) * s,
            H  = Math.Abs(r.Y2 - r.Y1) * s,
        },

        KitSymbolPath { Closed: true } p => new PolygonPrimitive
        {
            ColorRole  = SymbolColorRole.SymbolLine,
            StrokeTier = SymbolStrokeTier.Normal,
            Filled     = p.Filled,
            Points     = ScalePoints(p.Xy, s, follow, attached),
        },

        KitSymbolPath p => new PolylinePrimitive
        {
            ColorRole  = SymbolColorRole.SymbolLine,
            StrokeTier = SymbolStrokeTier.Normal,
            Points     = ScalePoints(p.Xy, s, follow, attached),
        },

        // The file measures its angles counter-clockwise on screen; circuitRF's arc primitive
        // measures clockwise, matching the renderer beneath it. That is a sign flip on BOTH, and
        // flipping only the sweep would draw the correct span from the wrong end — a mirrored arc
        // that still looks like an arc, which is exactly the kind of wrong that survives review.
        KitSymbolArc a => new ArcPrimitive
        {
            ColorRole  = SymbolColorRole.SymbolLine,
            StrokeTier = SymbolStrokeTier.Normal,
            Cx = a.Cx * s,
            Cy = a.Cy * s,
            R  = a.Radius * s,
            StartDeg = -a.StartDeg,
            SweepDeg = -a.SweepDeg,
        },

        _ => null,
    };

    private static LinePrimitive LineWithPinFollow(
        KitSymbolLine l, double s,
        IReadOnlyDictionary<(double X, double Y), (double X, double Y)> follow,
        HashSet<(double X, double Y)> attached)
    {
        var (x1, y1) = PinFollow(l.X1, l.Y1, s, follow, attached);
        var (x2, y2) = PinFollow(l.X2, l.Y2, s, follow, attached);
        return new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal, x1, y1, x2, y2);
    }

    private static List<double[]> ScalePoints(
        IReadOnlyList<double> xy, double s,
        IReadOnlyDictionary<(double X, double Y), (double X, double Y)> follow,
        HashSet<(double X, double Y)> attached)
    {
        var pts = new List<double[]>(xy.Count / 2);
        for (int i = 0; i + 1 < xy.Count; i += 2)
        {
            var (x, y) = PinFollow(xy[i], xy[i + 1], s, follow, attached);
            pts.Add([x, y]);
        }
        return pts;
    }
}
