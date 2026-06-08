using System.Linq;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// 4-way crossing connections (§5.1) with the hard invariant: a user junction dot exists IFF it
/// sits on a genuine crossing. The render layer (BuildRenderModel) only shows user dots on real
/// crossings; an inert user dot is never rendered. (Placement rejection and undoable auto-removal
/// of dots whose crossing dissolves are covered in DotInvariantTests.)
/// </summary>
public class CrossConnectionTests
{
    private static EditableWire MakeWire(params (double X, double Y)[] pts)
    {
        var w = new EditableWire();
        w.Points.AddRange(pts);
        return w;
    }

    private static SchematicEditModel ModelWith(params EditableWire[] wires)
    {
        var m = new SchematicEditModel();
        foreach (var w in wires) m.Wires.Add(w);
        return m;
    }

    // Horizontal (0,0)-(200,0) and vertical (100,-100)-(100,100) cross at (100,0); neither ends there.
    private static SchematicEditModel CrossingModel()
        => ModelWith(MakeWire((0, 0), (200, 0)), MakeWire((100, -100), (100, 100)));

    // ── No dot → no connection, no auto-dot ───────────────────────────────────

    [Fact]
    public void Crossing_NoDot_NoConnectionNoAutoDot()
    {
        var (render, _) = CrossingModel().BuildRenderModel();
        Assert.Empty(render.ConnectionDots);
    }

    // ── Dot at crossing → rendered as the junction ────────────────────────────

    [Fact]
    public void Crossing_WithDot_RendersOneDotAtCrossing()
    {
        var model = CrossingModel();
        model.Dots.Add(new EditableDot { X = 100, Y = 0 });

        var (render, _) = model.BuildRenderModel();

        var dot = render.ConnectionDots.Single();
        Assert.Equal((100.0, 0.0), (dot.X, dot.Y));
    }

    // ── Invariant at the render layer: inert user dots are NOT rendered ───────

    [Fact]
    public void StrayDot_EmptySpace_NotRendered()
    {
        var model = CrossingModel();
        model.Dots.Add(new EditableDot { X = 9000, Y = 9000 });   // nowhere near anything

        Assert.Empty(model.BuildRenderModel().Model.ConnectionDots);
    }

    [Fact]
    public void DotOnSingleWireBody_NotRendered()
    {
        var model = ModelWith(MakeWire((0, 0), (200, 0)));   // one wire only
        model.Dots.Add(new EditableDot { X = 100, Y = 0 });   // on its body, but nothing crosses

        Assert.Empty(model.BuildRenderModel().Model.ConnectionDots);
    }

    [Fact]
    public void UserDotAtVertexJunction_NotRendered()
    {
        // Two wires share an endpoint at (100,0): a vertex junction, not a crossing → user dot inert.
        var model = ModelWith(MakeWire((0, 0), (100, 0)), MakeWire((100, 0), (100, 100)));
        model.Dots.Add(new EditableDot { X = 100, Y = 0 });

        Assert.Empty(model.BuildRenderModel().Model.ConnectionDots);
    }

    // ── T-junction auto-dot is exempt from the user-dot invariant ─────────────

    [Fact]
    public void TJunction_AutoDot_AlwaysRendered_NoUserDotNeeded()
    {
        // Endpoint-on-body T: auto-derived dot, not an EditableDot. Always valid by construction.
        var model = ModelWith(MakeWire((0, 0), (200, 0)), MakeWire((100, 100), (100, 0)));

        var dot = model.BuildRenderModel().Model.ConnectionDots.Single();
        Assert.Equal((100.0, 0.0), (dot.X, dot.Y));
        Assert.Empty(model.Dots);   // no EditableDot was created
    }

    [Fact]
    public void RedundantUserDotAtTJunction_RendersExactlyOneDot()
    {
        // A user dot dropped atop a T is inert (filtered); the auto-T dot still renders → one dot.
        var model = ModelWith(MakeWire((0, 0), (200, 0)), MakeWire((100, 100), (100, 0)));
        model.Dots.Add(new EditableDot { X = 100, Y = 0 });

        var dot = model.BuildRenderModel().Model.ConnectionDots.Single();
        Assert.Equal((100.0, 0.0), (dot.X, dot.Y));
    }
}
