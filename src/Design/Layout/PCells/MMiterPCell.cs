using CircuitRF.Core.Devices.Microstrip;

namespace CircuitRF.Design.Layout.PCells;

/// <summary>
/// MMiter artwork: a chamfered microstrip bend of width <c>W</c>, turn <c>Angle</c> (degrees,
/// counter-clockwise positive from arm 1's +X, MBend's convention) and Douville-James miter fraction
/// <c>M</c>. R-pc-3: pin 1 at the origin, arriving along +X.
///
/// <para><b>The geometry is the electrical model's, to the vertex.</b> The cut is perpendicular to the
/// outer-to-inner corner diagonal at <c>M·d</c> from the outer corner, so its leg along each outer edge
/// is <see cref="MicrostripMiterBend.ChamferLeg"/> = <c>2·M·W/sin θ</c> (a <c>W</c> leg at 90°, 50 %,
/// i.e. corner to corner). The pins sit at <see cref="MicrostripMiterBend.PinOffset"/> from the
/// centre-line corner — the inner-corner planes, or further out where a deep chamfer runs past them —
/// which is exactly where <c>MicrostripMiterBendModel</c>'s ports are, so an MLIN drawn from a pin is a
/// full-width rectangle that never fills the chamfer back in and never double-counts copper the model
/// already owns. (MBendPCell's chamfer projects the same fraction to an <c>M·W</c> leg — half this one;
/// see <c>src/Core/RESOLVED.md</c> §MMITER. That cell is unchanged.)</para>
///
/// <para><c>M</c> absent means the Douville-James optimum on the resolved substrate (the W/h→∞ value
/// 0.52 with no technology), capped at <see cref="MicrostripMiterBend.MaxMiter"/>, exactly as the
/// model resolves it. Geometry the model refuses (|Angle| outside (0, 120°], M outside
/// [0, MaxMiter]) is drawn clamped and reported, never as a self-intersecting outline.</para>
/// </summary>
public static class MMiterPCell
{
    public const string GeneratorId = "MMITER";

    public static PCellResult Generate(
        IReadOnlyDictionary<string, PCellValue> parameters,
        Technology? technology,
        PCellLayerSelection layerSelection)
    {
        double wMeters = parameters.Real("W", 0.0);
        double angleDeg = parameters.Real("Angle", 90.0);
        int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
        long w = PCellUnits.MetresToDbu(wMeters, dbuPerMicron);
        var signalLayer = SubstrateResolver.ResolveSignalLayerKey(technology, layerSelection, out _);
        List<string>? diagnostics = null;

        double turn = Math.Abs(angleDeg);
        if (!(turn > 0.0) || turn > MicrostripMiterBend.MaxAngleDeg)
        {
            double clamped = Math.Clamp(turn, 1.0, MicrostripMiterBend.MaxAngleDeg);
            (diagnostics ??= []).Add(
                $"MMiter: Angle {angleDeg:G6} deg is outside (0, {MicrostripMiterBend.MaxAngleDeg:G4}] deg; drawn at " +
                $"{clamped:G4} deg, and the simulation refuses this geometry.");
            turn = clamped;
        }
        double sign = angleDeg < 0 ? -1.0 : 1.0;

        double miter;
        if (parameters.ContainsKey("M"))
        {
            miter = parameters.Real("M", 0.5);
        }
        else
        {
            var (substrate, _, _) = SubstrateResolver.ResolveElectrical(technology, layerSelection);
            miter = substrate is not null
                ? MicrostripMiterBend.DouvilleJamesOptimum(wMeters, substrate.HeightMeters)
                : 0.52;
        }
        double maxM = MicrostripMiterBend.MaxMiter(turn);
        if (!(miter >= 0.0) || miter > maxM)
        {
            double clamped = Math.Clamp(double.IsNaN(miter) ? 0.0 : miter, 0.0, maxM);
            (diagnostics ??= []).Add(
                $"MMiter: M {miter:G4} is outside [0, {maxM:G3}] at {turn:G4} deg; drawn at {clamped:G3}" +
                (parameters.ContainsKey("M") ? ", and the simulation refuses this geometry." : " (the Douville-James optimum is capped there)."));
            miter = clamped;
        }

        if (w <= 0)
        {
            (diagnostics ??= []).Add("MMiter: W must be positive; nothing is drawn.");
            return new PCellResult([], [new PCellPin("1", 0, 0, signalLayer, w, 180.0),
                                        new PCellPin("2", 0, 0, signalLayer, w, angleDeg)], diagnostics);
        }

        // Everything below in DBU, as doubles until the final rounding.
        double wd = w;
        double s = MicrostripMiterBend.PlaneOffset(wd, turn);           // inner-corner plane offset
        double r = MicrostripMiterBend.PinOffset(wd, turn, miter);      // pin offset from the corner
        double leg = MicrostripMiterBend.ChamferLeg(wd, turn, miter);
        double th = sign * turn * Math.PI / 180.0;

        double pX = r, pY = 0.0;                                         // centre-line corner
        double d1X = 1.0, d1Y = 0.0, d2X = Math.Cos(th), d2Y = Math.Sin(th);
        // Outer side = right of travel for a CCW (left) turn, left for a CW (right) turn.
        double oX1 = 0.0, oY1 = -sign, oX2 = sign * d2Y, oY2 = -sign * d2X;   // unit normals toward the outside
        double half = wd / 2.0;

        // Outer corner O and inner corner I sit on the bisector, ±s along each arm from the corner.
        double outerX = pX + oX1 * half + d1X * s, outerY = pY + oY1 * half + d1Y * s;
        double innerX = pX - oX1 * half - d1X * s, innerY = pY - oY1 * half - d1Y * s;

        // Pin 2 is rounded FIRST and its end edge is built round the rounded point, so the pin sits exactly on the
        // midpoint of its own copper's edge at any angle. Rounded the other way round, a 45° miter's pin landed a third
        // of a DBU outside its copper and LVS read whatever was joined to it as open (brief-sdl-1).
        double pin2X = Math.Round(pX + d2X * r, MidpointRounding.AwayFromZero);
        double pin2Y = Math.Round(pY + d2Y * r, MidpointRounding.AwayFromZero);
        var xy = new List<long>(16);
        void Add(double x, double y)
        {
            long X = (long)Math.Round(x, MidpointRounding.AwayFromZero), Y = (long)Math.Round(y, MidpointRounding.AwayFromZero);
            int n = xy.Count;
            if (n >= 2 && xy[n - 2] == X && xy[n - 1] == Y) return;     // a chamfer ending exactly on a pin corner
            xy.Add(X); xy.Add(Y);
        }

        Add(0.0 + oX1 * half, 0.0 + oY1 * half);                        // pin 1, outer side
        if (miter > 0.0)
        {
            Add(outerX - d1X * leg, outerY - d1Y * leg);
            Add(outerX + d2X * leg, outerY + d2Y * leg);
        }
        else
        {
            Add(outerX, outerY);
        }
        Add(pin2X + oX2 * half, pin2Y + oY2 * half);                    // pin 2, outer side
        Add(pin2X - oX2 * half, pin2Y - oY2 * half);                    // pin 2, inner side
        Add(innerX, innerY);
        Add(0.0 - oX1 * half, 0.0 - oY1 * half);                        // pin 1, inner side
        if (xy.Count >= 4 && xy[0] == xy[^2] && xy[1] == xy[^1]) xy.RemoveRange(xy.Count - 2, 2);

        var outline = new PolygonShape { Layer = signalLayer, Xy = [.. xy] };

        long p2X = (long)Math.Round(pin2X, MidpointRounding.AwayFromZero);
        long p2Y = (long)Math.Round(pin2Y, MidpointRounding.AwayFromZero);
        long cX = (long)Math.Round(pX, MidpointRounding.AwayFromZero);
        var pins = new[]
        {
            new PCellPin("1", 0, 0, signalLayer, w, 180.0),
            new PCellPin("2", p2X, p2Y, signalLayer, w, sign * turn),
        };

        // pcell-parameter-handles.md: W on arm 1's edge (the ordinary "widen this trace" gesture) and
        // Angle at pin 2, swung about the centre-line corner — its real path. M has no grip: it is a
        // fraction of a diagonal, and dragging a chamfer corner would move it along an edge, not along
        // the quantity; it belongs in the parameter list.
        var handles = new[]
        {
            new PCellHandle("W", cX / 2, 0, cX / 2, w / 2, AxisDeg: 90, Quantity: PCellHandleQuantity.Length),
            new PCellHandle("Angle", cX, 0, p2X, p2Y, AxisDeg: 0, Kind: PCellHandleKind.Angular,
                KeepAnchorFixed: true, Quantity: PCellHandleQuantity.Angle),
        };

        return new PCellResult([outline], pins, diagnostics, Handles: handles);
    }
}
