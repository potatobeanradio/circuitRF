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

        var drawn = new List<SymbolPrimitive>();
        foreach (var shape in body ?? [])
            if (Convert(shape, scale) is { } prim) drawn.Add(prim);

        return new Symbol(drawn.Count > 0 ? drawn : BoxBodyFor(placed), placed);
    }

    // ── shared placement ──────────────────────────────────────────────────────

    /// <summary>
    /// ONE power-of-ten scale for a whole kit, taken from its LARGEST part.
    ///
    /// <para><b>Per kit, not per part, and that is the point.</b> A kit draws every one of its
    /// symbols in one coordinate system, so their relative sizes are a choice its author made — a
    /// transistor larger than a ground marker. Scaling each part to fill the same band independently
    /// throws that away and lands a tiny label on the schematic bigger than the device beside it.
    /// Taking the largest keeps nothing in the kit oversized and every proportion exactly as drawn.</para>
    ///
    /// <para>Returns 0 when the kit states no drawing-backed part to measure; callers fall back to
    /// their own per-part scale, which is what the symbol-library path uses throughout.</para>
    /// </summary>
    internal static double ChooseKitScale(
        IEnumerable<(IReadOnlyList<KitSymbolPin>? Pins, IReadOnlyList<KitSymbolShape>? Body)> parts)
    {
        double largest = 0;
        foreach (var (pins, body) in parts)
        {
            if (pins is null || pins.Count == 0) continue;
            double extent = ExtentOf(pins, body);
            if (extent > largest) largest = extent;
        }
        return largest > 0 ? DsnSymbolReader.ChooseScale(largest) : 0;
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
    private static SymbolPrimitive? Convert(KitSymbolShape shape, double s) => shape switch
    {
        KitSymbolLine l => new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal,
                                             l.X1 * s, l.Y1 * s, l.X2 * s, l.Y2 * s),

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
            Points     = ScalePoints(p.Xy, s),
        },

        KitSymbolPath p => new PolylinePrimitive
        {
            ColorRole  = SymbolColorRole.SymbolLine,
            StrokeTier = SymbolStrokeTier.Normal,
            Points     = ScalePoints(p.Xy, s),
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

    private static List<double[]> ScalePoints(IReadOnlyList<double> xy, double s)
    {
        var pts = new List<double[]>(xy.Count / 2);
        for (int i = 0; i + 1 < xy.Count; i += 2) pts.Add([xy[i] * s, xy[i + 1] * s]);
        return pts;
    }
}
