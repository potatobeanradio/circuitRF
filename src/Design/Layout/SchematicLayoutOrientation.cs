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

    public static VisualOrientation FromLayout(LayoutInstance inst) => new(inst.MirrorX, inst.RotationDegrees);

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

        var byNumber = new Dictionary<int, LayoutPin>();
        foreach (var pin in cellPins)
            if (TrailingNumber(pin.Name) is { } n) byNumber.TryAdd(n, pin);
        LayoutPin? CellPin(int k) =>
            byNumber.Count == cellPins.Count ? byNumber.GetValueOrDefault(k)
                                             : k <= cellPins.Count ? cellPins[k - 1] : null;

        if (CellPin(1) is not { } c1) return identity;
        var s1 = symbolPorts[0];
        for (int k = 2; k <= symbolPorts.Count; k++)
        {
            if (CellPin(k) is not { } ck) continue;
            double sx = symbolPorts[k - 1].X - s1.X, sy = -(symbolPorts[k - 1].Y - s1.Y);   // into Y-up
            double cx = ck.X - c1.X, cy = ck.Y - c1.Y;
            if ((sx == 0 && sy == 0) || (cx == 0 && cy == 0)) continue;

            double deg = (Math.Atan2(sy, sx) - Math.Atan2(cy, cx)) * 180.0 / Math.PI;
            return new VisualOrientation(false, LayoutAngle.OfCardinal(LayoutAngle.NearestCardinal(deg)));
        }
        return identity;
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
