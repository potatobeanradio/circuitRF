using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

/// <summary>
/// A via is a pad and a barrel, and the EM extractors built only the barrel. An imported board's pour has each via's pad
/// carved out of it (the copper moves to the via), so a stitching via stood in a ring of air: a designer's pour floated
/// about 4 pF off its plane. Both extractors must put the pad back as copper on the via's landing layer.
/// </summary>
public sealed class ViaPadCopperTests
{
    private const double Um = 1e-6;

    /// <summary>A 4 mm top-copper pour with a 600 µm hole at its centre, filled by a through via's 600 µm pad (300 µm drill).</summary>
    private static (EmLayoutSource Source, Technology Tech) Board()
    {
        const string clay = """
            {
              "FormatVersion": 1, "DbuPerMicron": 1000, "DisplayUnit": "Mm", "SnapDbu": 0,
              "Shapes": [
                { "$type": "Poly", "Layer": { "Layer": 1, "Datatype": 0 },
                  "Xy": [0, 0, 4000000, 0, 4000000, 4000000, 0, 4000000],
                  "Holes": [[1700000, 1700000, 2300000, 1700000, 2300000, 2300000, 1700000, 2300000]] },
                { "$type": "Poly", "Layer": { "Layer": 2, "Datatype": 0 },
                  "Xy": [0, 0, 4000000, 0, 4000000, 4000000, 0, 4000000] },
                { "$type": "Via", "Layer": { "Layer": 9, "Datatype": 0 }, "X": 2000000, "Y": 2000000,
                  "PadSize": 600000, "DrillSize": 300000, "LandingLayer": { "Layer": 1, "Datatype": 0 } }
              ],
              "Instances": []
            }
            """;
        var view = LayoutPersistence.Deserialize(clay);
        var tech = ShippedTechnologies.Load("pcb-4layer_FR-4_62mil_1oz");
        return (new EmLayoutSource("/nowhere/pour.clay", view, tech, view.DbuPerMicron), tech);
    }

    /// <summary>A point between the drill (r 150 µm) and the pad's edge (r 300 µm).</summary>
    private const double RingX = 2.22e-3, RingY = 2.0e-3;

    [Fact]
    public void TheViaPad_IsCopper_InThe3DProblem()
    {
        var (source, tech) = Board();
        var r = Em3dLayoutSolids.From(source, tech, null, new Em3dLayoutSolidsOptions(6e9, EmSetup.DefaultOperatingTempC, Instance: false));
        Assert.True(r.Ok, r.Refusal);
        double zTop = r.Solids.Where(s => s.Role == Em3dRole.Conductor).Select(s => s.Primitive)
                       .OfType<Em3dExtrudedPolygon>().Max(e => e.ZTop);
        var top = r.Solids.Where(s => s.Role == Em3dRole.Conductor).Select(s => s.Primitive).OfType<Em3dExtrudedPolygon>()
                   .Where(e => Math.Abs(e.ZTop - zTop) < 1 * Um).ToList();
        Assert.Contains(top, e => Inside(e.Outline.Select(p => (p.X, p.Y)), RingX, RingY)
                                  && !e.Holes.Any(h => Inside(h.Select(p => (p.X, p.Y)), RingX, RingY)));
    }

    [Fact]
    public void TheViaPad_IsCopper_InThePlanarProblem()
    {
        var (source, tech) = Board();
        var r = PlanarExtractor.Extract(source.View.Shapes, tech, source.DbuPerMicron, 6e9);
        Assert.True(r.Ok, r.Refusal);
        var top = r.Problem!.Layers.Single(l => l.Polygons.Count > 0);
        Assert.Contains(top.Polygons, q => Inside(q.Outer.Select(p => (p.X, p.Y)), RingX, RingY)
                                           && !q.HoleRings.Any(h => Inside(h.Select(p => (p.X, p.Y)), RingX, RingY)));
    }

    private static bool Inside(IEnumerable<(double X, double Y)> ring, double x, double y)
    {
        var p = ring.ToList();
        bool c = false;
        for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
            if ((p[i].Y > y) != (p[j].Y > y) && x < (p[j].X - p[i].X) * (y - p[i].Y) / (p[j].Y - p[i].Y) + p[i].X) c = !c;
        return c;
    }
}
