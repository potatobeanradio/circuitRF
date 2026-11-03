// Carrying a component's ROTATION between its schematic symbol and its layout placement.
//
// The two documents use opposite frames. A schematic is Y-DOWN (SchematicGeometry.LocalToWorld's
// R90 takes local +x to world +y, which is DOWN the screen — a clockwise quarter turn), and a layout
// is Y-UP with angles counter-clockwise (LayoutAngle's header). Both apply mirror-then-rotate, and
// both mirrors negate local X. Conjugating the schematic's transform by the Y flip gives the one
// correspondence everything below rests on: a symbol at Rθ with mirror m LOOKS like a placement at
// −θ with the same mirror.
//
// WHAT IS CARRIED IS A CHANGE, NOT AN ANGLE. A symbol's R0 and a cell's R0 need not face the same way
// (a transistor symbol with its gate on the left, a cell with its gate at the bottom), so matching
// absolute orientations would rotate parts the user had deliberately arranged. Each sync instead
// records the pair it left behind (OrientationLink); the next one compares each side against its own
// half of that pair, and turns the other side by exactly the world-frame rotation/flip the moved side
// underwent.

namespace CircuitRF.Design.Layout;

/// <summary>The baseline one sync leaves on a <see cref="LayoutInstance"/> — see
/// <see cref="SchematicLayoutOrientation"/>. <see cref="SchematicDeg"/> is the schematic's own
/// <c>SymbolRotation</c> value (0/90/180/270, its Y-down sense); <see cref="LayoutDeg"/> is the
/// placement's <see cref="LayoutInstance.RotationDegrees"/>.</summary>
public sealed record OrientationLink
{
    public int    SchematicDeg    { get; init; }
    public bool   SchematicMirror { get; init; }
    public double LayoutDeg       { get; init; }
    public bool   LayoutMirror    { get; init; }
}

/// <summary>An orientation in ONE common frame — Y-up, degrees counter-clockwise, mirror (negate
/// local X) applied first: the layout's own convention.</summary>
public readonly record struct VisualOrientation(bool Mirror, double Deg)
{
    /// <summary><c>this ∘ inner</c> — apply <paramref name="inner"/> first. Derivation:
    /// <c>R(a)·Mx^ma · R(b)·Mx^mb = R(a ± b)·Mx^(ma⊕mb)</c>, the sign negative when <c>ma</c> is set,
    /// because a reflection reverses the sense of every rotation after it.</summary>
    public VisualOrientation Compose(VisualOrientation inner) =>
        new(Mirror ^ inner.Mirror, LayoutAngle.Normalize(Deg + (Mirror ? -inner.Deg : inner.Deg)));

    /// <summary>A reflection is its own inverse whatever its angle (<c>Mx·R(−a) = R(a)·Mx</c>); a pure
    /// rotation inverts to its negative.</summary>
    public VisualOrientation Inverse() => Mirror ? this : new(false, LayoutAngle.Normalize(-Deg));

    public bool SameAs(VisualOrientation other)
    {
        if (Mirror != other.Mirror) return false;
        double d = Math.Abs(LayoutAngle.Normalize(Deg) - LayoutAngle.Normalize(other.Deg));
        return Math.Min(d, 360.0 - d) <= 1e-9;
    }
}

public static class SchematicLayoutOrientation
{
    /// <summary>What a symbol at <paramref name="schematicDeg"/> (0/90/180/270, Y-down) looks like in
    /// the common frame.</summary>
    public static VisualOrientation FromSchematic(int schematicDeg, bool mirror) =>
        new(mirror, LayoutAngle.Normalize(-schematicDeg));

    /// <summary>
    /// The placement's facing as the link compares it: as seen on its OWN face (brief-bsp-4 R-bsp4-5).
    ///
    /// <para>A Flip is the board's left–right reflection plus a change of face — rotation to −θ and
    /// <c>MirrorX</c> toggled — so compared raw, the link would read every flip as a mirror (and, at any
    /// angle off the horizontal, a turn) made on the layout side, and the next schematic change would
    /// be carried onto it and un-flip the part. Composing that same reflection back in for a bottom
    /// placement makes a flip change nothing the link sees, and a symbol mirrored afterwards mirrors
    /// the part on the face it is on. The reflection is its own inverse, which is why
    /// <see cref="ToLayout"/> is the same composition.</para>
    /// </summary>
    public static VisualOrientation FromLayout(LayoutInstance inst) =>
        OnFace(new VisualOrientation(inst.MirrorX, inst.RotationDegrees), inst.Side);

    /// <summary>A facing from <see cref="FromLayout"/> back to the <c>MirrorX</c> and rotation a placement on
    /// <paramref name="side"/> carries.</summary>
    public static VisualOrientation ToLayout(VisualOrientation facing, PlacementSide side) => OnFace(facing, side);

    private static VisualOrientation OnFace(VisualOrientation v, PlacementSide side) =>
        side == PlacementSide.Bottom ? new VisualOrientation(true, 0).Compose(v) : v;

    /// <summary>The schematic rotation nearest <paramref name="v"/> — a symbol can only take quarter
    /// turns. <paramref name="exact"/> is false when the layout angle had to be rounded to get there.</summary>
    public static int ToSchematicDeg(VisualOrientation v, out bool exact)
    {
        double s = LayoutAngle.Normalize(-v.Deg);
        exact = LayoutAngle.DistanceToNearestCardinal(s) <= 1e-9;
        return (int)LayoutAngle.OfCardinal(LayoutAngle.NearestCardinal(s));
    }

    public static OrientationLink Link(int schematicDeg, bool schematicMirror, VisualOrientation layout) => new()
    {
        SchematicDeg = schematicDeg, SchematicMirror = schematicMirror,
        LayoutDeg = LayoutAngle.Normalize(layout.Deg), LayoutMirror = layout.Mirror,
    };

    /// <summary>
    /// Where the OTHER side should be, given that this side moved from <paramref name="fromBaseline"/>
    /// to <paramref name="now"/>: the same world-frame change, applied to the other side's baseline.
    /// <c>target = (now ∘ baseline⁻¹) ∘ otherBaseline</c>.
    /// </summary>
    public static VisualOrientation Carry(VisualOrientation now, VisualOrientation fromBaseline,
                                          VisualOrientation otherBaseline) =>
        now.Compose(fromBaseline.Inverse()).Compose(otherBaseline);

    /// <summary>
    /// The rotation that turns a placement at "the same orientation as its symbol" into one whose
    /// pins actually point the way the symbol's do: <c>placement = symbol ∘ alignment</c>.
    ///
    /// <para><b>Why there has to be one.</b> A symbol's R0 and its cell's R0 are drawn independently.
    /// A resistor symbol stands upright with its pins top and bottom; its land pattern lies flat with
    /// its pads left and right. Placing one at "the same angle" as the other puts a north–south part
    /// east–west. So the two are lined up by their PINS: symbol port <i>k</i> is the cell's pin named
    /// <i>k</i> (every built-in generator and land pattern numbers its pins so; list order when the
    /// cell's pins are not all numbered), and the first two pins that sit apart on both sides fix
    /// the direction. Rounded to a quarter turn. Identity when there is nothing to line up — fewer
    /// than two pins on either side.</para>
    /// </summary>
    /// <param name="symbolPorts">The symbol's ports in port order, in its own LOCAL frame (Y-down).</param>
    /// <param name="cellPins">The placed cell's own pins, in its local frame (Y-up).</param>
    public static VisualOrientation PinAlignment(IReadOnlyList<(double X, double Y)> symbolPorts,
                                                 IReadOnlyList<LayoutPin> cellPins)
    {
        var identity = new VisualOrientation(false, 0);
        if (symbolPorts.Count < 2 || cellPins.Count < 2) return identity;

        LayoutPin? CellPin(int k) => CellPinFor(k, cellPins);

        if (CellPin(1) is not { } c1) return identity;
        var s1 = symbolPorts[0];
        for (int k = 2; k <= symbolPorts.Count; k++)
        {
            if (CellPin(k) is not { } ck) continue;
            double sx = symbolPorts[k - 1].X - s1.X, sy = -(symbolPorts[k - 1].Y - s1.Y);   // into Y-up
            double cx = ck.X - c1.X, cy = ck.Y - c1.Y;
            if ((sx == 0 && sy == 0) || (cx == 0 && cy == 0)) continue;

            var rotated = new VisualOrientation(false, Snap(Math.Atan2(sy, sx) - Math.Atan2(cy, cx)));
            if (PinsFace(rotated, symbolPorts[0], symbolPorts[k - 1], c1, ck) is not false) return rotated;

            // A rotation cannot change a part's HANDEDNESS. A bend symbol turns one way and its cell
            // may be drawn turning the other (MBEND/MMITER at a positive Angle turn counter-clockwise
            // in the layout's Y-up frame, while their symbol at R0 turns clockwise), and the rotation
            // that lines the pins' POSITIONS up then points both pins the wrong way: the copper is
            // the mirror image of the drawing. Mirrored, the pins face the way the symbol's do.
            var mirrored = new VisualOrientation(true, Snap(Math.Atan2(sy, sx) - Math.Atan2(cy, -cx)));
            if (PinsFace(mirrored, symbolPorts[0], symbolPorts[k - 1], c1, ck) == true) return mirrored;

            // Neither faces within 45°: a bend drawn at less than a right angle (an MCURVE at 30°, an MMITER at
            // 45°) under a symbol that is always drawn as a quarter turn. The handedness is still the symbol's,
            // and the facing closer to it is the one that keeps it (brief-sdl-1 D2: falling back to the
            // rotation alone turned a 30° curve the opposite way to its symbol).
            double? r = FacingError(rotated, symbolPorts[0], symbolPorts[k - 1], c1, ck);
            double? m = FacingError(mirrored, symbolPorts[0], symbolPorts[k - 1], c1, ck);
            return r is { } re && m is { } me && me < re ? mirrored : rotated;
        }
        return identity;

        static double Snap(double rad) => LayoutAngle.OfCardinal(LayoutAngle.NearestCardinal(rad * 180.0 / Math.PI));
    }

    /// <summary>
    /// Whether <paramref name="v"/> points both cell pins out the way the symbol's ports point — a port's
    /// direction being its bearing from the symbol's origin (a bend's corner, a line's centre). Null when
    /// a port sits ON the origin and so has no direction to compare.
    /// </summary>
    private static bool? PinsFace(VisualOrientation v, (double X, double Y) port1, (double X, double Y) portK,
                                  LayoutPin pin1, LayoutPin pinK)
    {
        bool? Faces((double X, double Y) port, LayoutPin pin)
        {
            if (port.X == 0 && port.Y == 0) return null;
            double symbolDeg = Math.Atan2(-port.Y, port.X) * 180.0 / Math.PI;                 // into Y-up
            double cellDeg = (v.Mirror ? 180.0 - pin.OutwardDeg : pin.OutwardDeg) + v.Deg;
            double d = Math.Abs(LayoutAngle.Normalize(symbolDeg - cellDeg));
            return Math.Min(d, 360.0 - d) < 45.0;
        }

        return (Faces(port1, pin1), Faces(portK, pinK)) switch
        {
            (false, _) or (_, false) => false,
            (true, true) => true,
            _ => null,
        };
    }

    /// <summary>How far, in degrees summed over both pins, <paramref name="v"/> leaves the cell pins from facing the
    /// way the symbol's ports point — <see cref="PinsFace"/>'s measure without its threshold. Null when a port sits
    /// on the symbol's origin.</summary>
    private static double? FacingError(VisualOrientation v, (double X, double Y) port1, (double X, double Y) portK,
                                       LayoutPin pin1, LayoutPin pinK)
    {
        double? Error((double X, double Y) port, LayoutPin pin)
        {
            if (port.X == 0 && port.Y == 0) return null;
            double symbolDeg = Math.Atan2(-port.Y, port.X) * 180.0 / Math.PI;
            double cellDeg = (v.Mirror ? 180.0 - pin.OutwardDeg : pin.OutwardDeg) + v.Deg;
            double d = Math.Abs(LayoutAngle.Normalize(symbolDeg - cellDeg));
            return Math.Min(d, 360.0 - d);
        }
        return Error(port1, pin1) is { } a && Error(portK, pinK) is { } b ? a + b : null;
    }

    /// <summary>
    /// The cell pin that schematic terminal <paramref name="terminal"/> (1-based, the symbol's port order) lands on:
    /// by the pin names' trailing numbers when every pin has one, else by position. The one rule both the
    /// orientation above and the ground vias at ground-symbol pins (<c>GroundArtwork</c>) read.
    /// </summary>
    public static LayoutPin? CellPinFor(int terminal, IReadOnlyList<LayoutPin> cellPins)
    {
        var byNumber = new Dictionary<int, LayoutPin>();
        foreach (var pin in cellPins)
            if (TrailingNumber(pin.Name) is { } n) byNumber.TryAdd(n, pin);
        return byNumber.Count == cellPins.Count ? byNumber.GetValueOrDefault(terminal)
             : terminal >= 1 && terminal <= cellPins.Count ? cellPins[terminal - 1] : null;
    }

    private static int? TrailingNumber(string name)
    {
        int i = name.Length;
        while (i > 0 && char.IsDigit(name[i - 1])) i--;
        return i < name.Length && int.TryParse(name[i..], out int n) ? n : null;
    }

    /// <summary>"90°", "270° mirrored" — the layout's own spelling, used in both commands' reports.</summary>
    public static string Describe(VisualOrientation v) =>
        LayoutAngle.Normalize(v.Deg).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "°"
        + (v.Mirror ? " mirrored" : "");

    /// <summary>"R90", "R270 mirrored" — the schematic's own spelling.</summary>
    public static string DescribeSchematic(int deg, bool mirror) => $"R{deg}" + (mirror ? " mirrored" : "");
}
