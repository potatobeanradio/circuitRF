
namespace CircuitRF.Design.Layout.PCells;

/// <summary>
/// MCurve artwork: a radiused microstrip bend of width <c>W</c>, CENTRE-LINE radius <c>Radius</c> and
/// turn <c>Angle</c> (degrees, counter-clockwise positive from arm 1's +X, MBend's convention).
/// R-pc-3: pin 1 at the origin, arriving along +X; the centre of curvature is at
/// <c>(0, s·Radius)</c> with <c>s = sign(Angle)</c>.
///
/// <para><b>The outline is ONE <see cref="CurveShape"/> whose two long edges are TRUE ARCS</b>, not
/// chords: the outer edge at <c>Radius + W/2</c> and the inner at <c>Radius − W/2</c>, joined by the two
/// radial end caps. Every consumer that needs straight edges — the planar EM extractor, the Gerber and
/// GDSII writers, DRC — already flattens arcs to the technology's own tolerance
/// (<see cref="LayoutFlattener"/>), so the artwork stores the exact geometry once and each consumer
/// decides how finely to see it. A chord polygon here would bake one tolerance into every one of them.</para>
///
/// <para>At <c>Radius = W/2</c> the inner edge is a single point — the centre of curvature — and the
/// outline is a sector; below that the inner edge would cross the centre, which the electrical model
/// refuses and this cell reports and clamps rather than drawing a self-intersecting ring.</para>
/// </summary>
public static class MCurvePCell
{
    public const string GeneratorId = "MCURVE";

    /// <summary>The smallest turn an angle grip drags to. At zero the curve has no length and no
    /// artwork, so a grip dragged there would have nothing left to hold on to.</summary>
    public const double MinDragAngleDeg = 1.0;

    public static PCellResult Generate(
        IReadOnlyDictionary<string, PCellValue> parameters,
        Technology? technology,
        PCellLayerSelection layerSelection)
    {
        double wMeters = parameters.Real("W", 0.0);
        double angleDeg = parameters.Real("Angle", 90.0);
        double radiusMeters = parameters.Real("Radius", 0.0);

        int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
        long w = PCellUnits.MetresToDbu(wMeters, dbuPerMicron);
        double r = PCellUnits.MetresToDbu(radiusMeters, dbuPerMicron);
        var signalLayer = SubstrateResolver.ResolveSignalLayerKey(technology, layerSelection, out _);
        List<string>? diagnostics = null;

        if (r < w / 2.0)
        {
            (diagnostics ??= []).Add(
                $"MCurve: Radius {radiusMeters * 1e6:G6} um is below W/2 ({wMeters * 0.5e6:G6} um). Radius is " +
                "the CENTRE-LINE radius, so the inner edge would cross the centre of curvature; drawn at " +
                "W/2 instead, and the simulation refuses this geometry.");
            r = w / 2.0;
        }
        if (Math.Abs(angleDeg) > 360.0)
        {
            (diagnostics ??= []).Add(
                $"MCurve: Angle {angleDeg:G6} deg turns past a full circle, where the strip overlaps itself; " +
                "drawn at 360 deg, and the simulation refuses this geometry.");
            angleDeg = Math.Sign(angleDeg) * 360.0;
        }

        double s = angleDeg < 0 ? -1.0 : 1.0;
        double theta = Math.Abs(angleDeg) * Math.PI / 180.0;
        double cy = s * r;                                   // centre of curvature (0, cy)
        double ro = r + w / 2.0, ri = r - w / 2.0;

        // A point at radius rho, swept theta' from pin 1's radial: P = C + rho*(sin, -s*cos).
        (long X, long Y) At(double rho, double t) =>
            ((long)Math.Round(rho * Math.Sin(t), MidpointRounding.AwayFromZero),
             (long)Math.Round(cy - s * rho * Math.Cos(t), MidpointRounding.AwayFromZero));

        // Pin 2's end cap is built round the ROUNDED pin, half a width either way along the radius, so the pin is the
        // exact midpoint of its own cap at any angle. Rounding the three points independently left an oblique curve's pin
        // a fraction of a DBU outside its copper, and LVS read what was joined to it as open (brief-sdl-1).
        var p2 = At(r, theta);
        var (capX, capY) = (w / 2.0 * Math.Sin(theta), -s * w / 2.0 * Math.Cos(theta));
        (long X, long Y) Cap(double sign) =>
            (p2.X + (long)Math.Round(sign * capX, MidpointRounding.AwayFromZero),
             p2.Y + (long)Math.Round(sign * capY, MidpointRounding.AwayFromZero));
        var a0 = At(ro, 0); var a1 = Cap(1.0);
        var b1 = Cap(-1.0); var b0 = At(ri, 0);
        // tan(sweep/4) of each arc, +ve = counter-clockwise. Rounded to 12 decimals because Math.Tan is the
        // platform's libm and is not correctly rounded: tan(pi/8) differs by one ulp between Windows
        // and macOS, which made the artwork (and PCellGeneratorVersioningTests' fingerprint) depend on
        // the machine. 1e-12 of bulge moves the arc by far less than a DBU; the vertices are already
        // rounded to DBU for the same reason.
        //
        // A turn past 180 deg is drawn as TWO arcs meeting at mid-turn, each under a half-turn. One
        // arc's bulge runs to infinity as its sweep approaches 360 deg and its chord to zero, and the
        // chord is integer DBU: by 359.99 deg a one-DBU rounding is a 0.5% radius error, and at
        // exactly 360 deg both ends coincide and the arc has no circle at all. A 90 deg curve keeps
        // its single arc, so the shipped artwork below 180 deg is unchanged.
        bool split = theta > Math.PI;
        double bulge = Math.Round(s * Math.Tan(theta / (split ? 8.0 : 4.0)), 12);

        var xy = new List<long>(); var edges = new List<LayoutEdge>();
        void Vertex((long X, long Y) p, LayoutEdge e) { xy.Add(p.X); xy.Add(p.Y); edges.Add(e); }
        var line = new Func<LayoutEdge>(() => new LayoutEdge { Kind = EdgeKind.Line });
        LayoutEdge Arc(double b) => new() { Kind = EdgeKind.Arc, Bulge = b };

        if (theta <= 1e-12)
        {
            (diagnostics ??= []).Add("MCurve: Angle 0 is a zero-length curve; nothing is drawn.");
            return new PCellResult([], [new PCellPin("1", 0, 0, signalLayer, w, 180.0),
                                        new PCellPin("2", 0, 0, signalLayer, w, 0.0)], diagnostics);
        }

        Vertex(a0, Arc(bulge));                              // outer edge, pin 1 -> pin 2
        if (split) Vertex(At(ro, theta / 2.0), Arc(bulge));
        Vertex(a1, line());                                  // pin 2's end cap
        if (ri >= 1.0)
        {
            Vertex(b1, Arc(-bulge));                         // inner edge, back to pin 1
            if (split) Vertex(At(ri, theta / 2.0), Arc(-bulge));
            Vertex(b0, line());                              // pin 1's end cap
        }
        else
        {
            Vertex(At(0, 0), line());                        // inner edge collapsed onto the centre
        }

        var outline = new CurveShape { Layer = signalLayer, Xy = [.. xy], Edges = edges };

        var pins = new[]
        {
            new PCellPin("1", 0, 0, signalLayer, w, 180.0),
            new PCellPin("2", p2.X, p2.Y, signalLayer, w, angleDeg),
        };

        // pcell-parameter-handles.md — four grips, each on the quantity it changes:
        //   W      — the outer edge at mid-turn, anchored on the centre line there, along the radius.
        //   Radius — the centre line at mid-turn, anchored on pin 1, which the radius does not move.
        //   Angle  — one at EACH pin, and they are MIRROR IMAGES: grab either end and swing it round
        //            the centre of curvature; the other end keeps its place AND its heading, so the
        //            trace attached there stays attached. Both drive the same `Angle`.
        //
        //   * PIN 2 swings about the centre of curvature (0, cy), which never moves with `Angle`, and
        //     pin 1 stays exactly where it is with no help from the host.
        //   * PIN 1 sits at the cell origin heading +X for every `Angle` (R4), so holding pin 2 still
        //     instead is a ROTATION of the instance, which no generator can express. The grip holds the
        //     centre of curvature in place (KeepAnchorFixed) and pin 2's outward direction with it
        //     (AnchorDirDeg); the host turns the instance by the angle's change and pin 1 travels round
        //     the same circle in the other direction. Its reference axis turns with pin 2's direction,
        //     so the bearing it reads is -Angle, a slope of one.
        //
        // Bounded to this curve's own turn direction: crossing zero flips the centre of curvature to
        // the other side, where neither grip is measured from anything meaningful, and at exactly zero
        // nothing is drawn. 360 is the generator's own limit.
        var mid = At(r, theta / 2.0); var midOuter = At(ro, theta / 2.0);
        double radialDeg = Math.Atan2(midOuter.Y - mid.Y, midOuter.X - mid.X) * 180.0 / Math.PI;
        double chordDeg = Math.Atan2(mid.Y, mid.X) * 180.0 / Math.PI;
        long cyDbu = (long)Math.Round(cy, MidpointRounding.AwayFromZero);
        double angleMin = s > 0 ? MinDragAngleDeg : -360.0, angleMax = s > 0 ? 360.0 : -MinDragAngleDeg;
        var handles = new[]
        {
            new PCellHandle("W", mid.X, mid.Y, midOuter.X, midOuter.Y, AxisDeg: radialDeg,
                Quantity: PCellHandleQuantity.Length),
            new PCellHandle("Radius", 0, 0, mid.X, mid.Y, AxisDeg: chordDeg,
                Quantity: PCellHandleQuantity.Length),
            new PCellHandle("Angle", 0, cyDbu, p2.X, p2.Y, AxisDeg: -s * 90.0,
                Kind: PCellHandleKind.Angular, Min: angleMin, Max: angleMax,
                Quantity: PCellHandleQuantity.Angle),
            new PCellHandle("Angle", 0, cyDbu, 0, 0, AxisDeg: angleDeg - s * 90.0,
                Kind: PCellHandleKind.Angular, Min: angleMin, Max: angleMax, KeepAnchorFixed: true,
                Quantity: PCellHandleQuantity.Angle, AnchorDirDeg: angleDeg),
        };

        return new PCellResult([outline], pins, diagnostics, Handles: handles);
    }
}
