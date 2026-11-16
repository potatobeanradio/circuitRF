using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;

namespace CircuitRF.Design.Layout.PCells;

/// <summary>
/// MRadial artwork: the radial stub <see cref="MicrostripRadialStub"/> simulates. R-pc-3: pin 1 at the origin, the
/// middle of the sector's narrow end, facing −X. The sector fans out along +X: its apex is r_i·cos(α/2) behind the pin
/// (r_i = W / (2·sin(α/2)), where the sector is as wide as its feed), its narrow end is the chord of width W there, and
/// its far edge is a TRUE ARC of radius <c>Ro</c> about the apex, as MCurve's edges are, which every consumer flattens to
/// its own tolerance. <c>Sides</c> = 2 adds the mirror image fanning along −X, the two joined across the narrow end in one
/// outline, and the pin, now between them, faces −Y.
/// </summary>
public static class MRadialPCell
{
    public const string GeneratorId = ComponentModelFactory.RadialStubTypeName;

    public static PCellResult Generate(
        IReadOnlyDictionary<string, PCellValue> parameters,
        Technology? technology,
        PCellLayerSelection layerSelection)
    {
        double wMeters = parameters.Real("W", 0.0), roMeters = parameters.Real("Ro", 0.0);
        double angleDeg = parameters.Real("Angle", 60.0);
        int sides = (int)Math.Round(parameters.Real("Sides", 1.0));

        int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
        long w = PCellUnits.MetresToDbu(wMeters, dbuPerMicron);
        var signalLayer = SubstrateResolver.ResolveSignalLayerKey(technology, layerSelection, out _);
        var pins = new[] { new PCellPin("1", 0, 0, signalLayer, w, sides == 2 ? 270.0 : 180.0) };
        if (MicrostripRadialStub.Refusal(wMeters, roMeters, angleDeg, sides) is { } why)
            return new PCellResult([], pins, [why.Render()]);

        double half = angleDeg * Math.PI / 360.0;
        double ro = roMeters * 1e6 * dbuPerMicron;
        double apex = -w / (2.0 * Math.Sin(half)) * Math.Cos(half);
        static long Round(double v) => (long)Math.Round(v, MidpointRounding.AwayFromZero);
        long hw = Round(w / 2.0), ax = Round(apex + ro * Math.Cos(half)), ay = Round(ro * Math.Sin(half));
        // tan(sweep/4), counter-clockwise, rounded to 12 decimals as MCurve's is (Math.Tan is not correctly rounded).
        double bulge = Math.Round(Math.Tan(half / 2.0), 12);

        var xy = new List<long>(); var edges = new List<LayoutEdge>();
        void Vertex(long x, long y, EdgeKind kind)
        {
            xy.Add(x); xy.Add(y);
            edges.Add(kind == EdgeKind.Arc ? new LayoutEdge { Kind = EdgeKind.Arc, Bulge = bulge } : new LayoutEdge { Kind = kind });
        }
        Vertex(0, -hw, EdgeKind.Line);         // the narrow end's lower corner, out to the arc
        Vertex(ax, -ay, EdgeKind.Arc);         // the arc about the apex, counter-clockwise
        Vertex(ax, ay, EdgeKind.Line);
        Vertex(0, hw, EdgeKind.Line);          // the narrow end's upper corner
        if (sides == 2)
        {
            Vertex(-ax, ay, EdgeKind.Arc);     // the mirror sector's arc, counter-clockwise about its own apex
            Vertex(-ax, -ay, EdgeKind.Line);
        }

        return new PCellResult([new CurveShape { Layer = signalLayer, Xy = [.. xy], Edges = edges }], pins);
    }
}
