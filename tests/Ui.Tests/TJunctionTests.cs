using System.Linq;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.Commands;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// T-junction connectivity (§5.1): a wire endpoint landing on another wire's
/// segment interior auto-connects and auto-shows a junction dot, while a 4-way
/// crossing still needs a user-placed dot.
/// </summary>
public class TJunctionTests
{
    private static EditableWire MakeWire(params (double X, double Y)[] pts)
    {
        var w = new EditableWire();
        w.Points.AddRange(pts);
        return w;
    }

    // ── Step 1 + 2: T-junction is connected and gets one auto dot ──────────────

    [Fact]
    public void EndpointOnMidSegment_ReadsConnected_AndShowsAutoDot()
    {
        var model = new SchematicEditModel();
        // Horizontal trunk from (0,0) to (200,0).
        var trunk = MakeWire((0, 0), (200, 0));
        // Branch ending at (100,0) — the middle of the trunk's only segment.
        var branch = MakeWire((100, 100), (100, 0));
        model.Wires.Add(trunk);
        model.Wires.Add(branch);

        var (render, _) = model.BuildRenderModel();

        // The branch's T-end (its last point) reads connected — no false red dot.
        var rBranch = render.Wires.First(w => w.Id == branch.Id);
        Assert.True(rBranch.EndConnected, "T-junction endpoint should read connected");

        // Exactly one auto junction dot at the T point (no user dot was placed).
        Assert.Single(render.ConnectionDots);
        var dot = render.ConnectionDots[0];
        Assert.Equal(100.0, dot.X, 3);
        Assert.Equal(0.0, dot.Y, 3);
    }

    [Fact]
    public void AutoDot_IsNotPersistedAsUserDot()
    {
        var model = new SchematicEditModel();
        model.Wires.Add(MakeWire((0, 0), (200, 0)));
        model.Wires.Add(MakeWire((100, 100), (100, 0)));

        model.BuildRenderModel();

        // The derived dot must not be added back as an EditableDot (would persist + fight recompute).
        Assert.Empty(model.Dots);
    }

    // ── Step 2: dedup — vertex-coincidence + T at same point shows one dot ─────

    [Fact]
    public void TJunctionCoincidingWithUserDot_DrawsOneDot()
    {
        var model = new SchematicEditModel();
        model.Wires.Add(MakeWire((0, 0), (200, 0)));
        model.Wires.Add(MakeWire((100, 100), (100, 0)));
        // A user dot already sits exactly at the T point.
        model.Dots.Add(new EditableDot { X = 100, Y = 0 });

        var (render, _) = model.BuildRenderModel();

        Assert.Single(render.ConnectionDots);
    }

    // ── Scope guard: 4-way crossing stays unconnected without a user dot ──────

    [Fact]
    public void FourWayCrossing_NoEndpointOnBody_NoAutoDot()
    {
        var model = new SchematicEditModel();
        // Two crossing wires, neither ending on the other (both pass through (100,0)).
        model.Wires.Add(MakeWire((0, 0), (200, 0)));     // horizontal, crosses at interior
        model.Wires.Add(MakeWire((100, -100), (100, 100))); // vertical, crosses at interior
        // Each wire's endpoints are at its own extremities, NOT on the other's body.

        var (render, _) = model.BuildRenderModel();

        // No endpoint lands on another wire's body → no auto dot (4-way needs a manual dot).
        Assert.Empty(render.ConnectionDots);
    }

    // ── Step 3: snapping makes the T land exactly on the segment ──────────────

    [Fact]
    public void NearestPointOnWireSegment_ProjectsOntoBody()
    {
        var model = new SchematicEditModel();
        model.Wires.Add(MakeWire((0, 0), (200, 0)));

        // Near the middle of the body but a few units off in Y.
        var (found, _, _, x, y) = SchematicHitTest.NearestPointOnWireSegment(model, 100, 4, 15);

        Assert.True(found);
        Assert.Equal(100.0, x, 3);
        Assert.Equal(0.0, y, 3);
    }

    [Fact]
    public void NearestPointOnWireSegment_TooFar_NotFound()
    {
        var model = new SchematicEditModel();
        model.Wires.Add(MakeWire((0, 0), (200, 0)));

        var (found, _, _, _, _) = SchematicHitTest.NearestPointOnWireSegment(model, 100, 100, 15);

        Assert.False(found);
    }
}
