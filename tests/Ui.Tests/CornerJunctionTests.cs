using System.Linq;
using Avalonia.Input;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Auto-junction dots follow the standard EDA rule: a dot wherever 3+ wire segments meet with a
/// vertex present — covering a wire ending on another wire's BODY (classic T), on another wire's
/// CORNER (bend vertex), and 3+ wire-ends meeting. Pure crossings still have no auto-dot.
/// </summary>
public class CornerJunctionTests
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

    private static bool HasDotAt(SchematicModel render, double x, double y)
        => render.ConnectionDots.Any(d => d.X == x && d.Y == y);

    // ── The reported bug: a wire ending on another wire's CORNER ──────────────

    [Fact]
    public void WireEndingOnCorner_ShowsJunctionDot()
    {
        // L-wire bending at the corner (100,0); a second wire ends exactly at that corner.
        var bent = MakeWire((0, 0), (100, 0), (100, 100));
        var stem = MakeWire((100, 0), (200, 0));   // ends at the corner (100,0)
        var (render, _) = ModelWith(bent, stem).BuildRenderModel();

        Assert.True(HasDotAt(render, 100, 0), "a wire ending on another wire's corner is a junction");
        Assert.Single(render.ConnectionDots);
    }

    [Fact]
    public void WireEndingOnCorner_EndpointReadsConnected()
    {
        var bent = MakeWire((0, 0), (100, 0), (100, 100));
        var stem = MakeWire((100, 0), (200, 0));
        var (render, _) = ModelWith(bent, stem).BuildRenderModel();

        var rStem = render.Wires.First(w => w.Points[0] == (100.0, 0.0));
        Assert.True(rStem.StartConnected);   // no false "unconnected" indicator at the corner
    }

    // ── A lone corner (no third wire) gets NO dot ─────────────────────────────

    [Fact]
    public void LoneCorner_NoDot()
    {
        var bent = MakeWire((0, 0), (100, 0), (100, 100));   // just one bending wire
        var (render, _) = ModelWith(bent).BuildRenderModel();

        Assert.Empty(render.ConnectionDots);
    }

    // ── 3-way endpoint meeting gets a dot ─────────────────────────────────────

    [Fact]
    public void ThreeWireEndsMeeting_ShowsDot()
    {
        var a = MakeWire((0, 0), (100, 0));
        var b = MakeWire((100, 0), (200, 0));
        var c = MakeWire((100, 0), (100, 100));
        var (render, _) = ModelWith(a, b, c).BuildRenderModel();

        Assert.True(HasDotAt(render, 100, 0));
        Assert.Single(render.ConnectionDots);
    }

    [Fact]
    public void TwoWireEndsMeeting_NoDot()
    {
        // A simple corner formed by two separate wires (not merged) — 2 segments, no dot.
        var a = MakeWire((0, 0), (100, 0));
        var b = MakeWire((100, 0), (100, 100));
        var (render, _) = ModelWith(a, b).BuildRenderModel();

        Assert.Empty(render.ConnectionDots);
    }

    // ── Pure crossing still excluded (regression guard) ───────────────────────

    [Fact]
    public void PureCrossing_StillNoAutoDot()
    {
        var h = MakeWire((0, 0), (200, 0));
        var v = MakeWire((100, -100), (100, 100));
        var (render, _) = ModelWith(h, v).BuildRenderModel();

        Assert.Empty(render.ConnectionDots);
    }

    // ── Drawing a new wire from a corner forms the junction (end-to-end) ──────

    [Fact]
    public void DrawingWireFromCorner_FormsJunctionDot()
    {
        var bent = MakeWire((0, 0), (100, 0), (100, 100));
        var model = ModelWith(bent);
        var vm = new SchematicViewModel(model);
        vm.SetWireTool();

        vm.OnPointerPressed(100, 0, default);     // start on the corner
        vm.OnPointerPressed(200, 0, default);     // empty space → keeps drawing
        vm.FinishCurrentWire();

        Assert.True(HasDotAt(vm.RenderModel!, 100, 0), "drawing from a corner must form a junction");
    }
}
