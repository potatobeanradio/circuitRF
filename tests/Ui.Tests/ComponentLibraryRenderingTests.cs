using System.Linq;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate tests for brief-component-library-rendering (Parts 1, 3, 4, 5).
/// </summary>
public class ComponentLibraryRenderingTests
{
    // ── Part 1: FullBb contains GlyphBb for tall symbols (SDD N=6) ──────────

    [Fact]
    public void SddN6_FullBb_ContainsGlyphBb()
    {
        var model = new SchematicEditModel();
        var sdd   = new EditableComponent { InstanceName = "X1", Symbol = SymbolKind.Sdd };
        sdd.Parameters.Add(new EditableParameter { Name = "NumPorts", Expression = "6" });
        model.Components.Add(sdd);

        var (render, _) = model.BuildRenderModel();
        var comp = render.Components.Single();

        Assert.True(comp.FullBbMinY <= comp.GlyphBbMinY,
            $"FullBbMinY ({comp.FullBbMinY}) must be <= GlyphBbMinY ({comp.GlyphBbMinY})");
        Assert.True(comp.FullBbMaxY >= comp.GlyphBbMaxY,
            $"FullBbMaxY ({comp.FullBbMaxY}) must be >= GlyphBbMaxY ({comp.GlyphBbMaxY})");
    }

    [Fact]
    public void SddN6_SpatialIndex_ReturnsComponent_WhenOnlyTopVisible()
    {
        // Place SDD N=6 at origin. Its glyph extends well below y=0.
        // Query a viewport that contains only the TOP of the symbol (center is below viewport).
        var model = new SchematicEditModel();
        var sdd   = new EditableComponent { InstanceName = "X1", Symbol = SymbolKind.Sdd };
        sdd.Parameters.Add(new EditableParameter { Name = "NumPorts", Expression = "6" });
        model.Components.Add(sdd);

        var (render, _) = model.BuildRenderModel();
        var comp = render.Components.Single();

        // Viewport contains the top of FullBb but not the center (0,0).
        // The glyph top is GlyphBbMinY; go just above it.
        double vpMaxY = comp.FullBbMinY + 10; // just below the top edge
        double vpMinY = comp.FullBbMinY - 100;
        double vpMinX = -500;
        double vpMaxX =  500;

        var index = new SchematicSpatialIndex(render);
        var foundComps = new System.Collections.Generic.HashSet<int>();
        var foundWires = new System.Collections.Generic.HashSet<int>();
        index.QueryViewport(vpMinX, vpMinY, vpMaxX, vpMaxY, foundComps, foundWires);

        Assert.Contains(0, foundComps);
    }

    // ── Part 3: the library FET (FetSdd) was hard-removed, brief-housekeeping-tearoff-palette-
    // repo.md §7A — its dedicated glyph/port-def tests are removed along with the component
    // itself (there is nothing analogous to assert; see UnknownComponentTypeTests.cs for the
    // graceful-load coverage of a `.csch` that still names the removed "FET" type).

    // ── Part 4: P1Tone has Term-sized box, circle, and sine ─────────────────

    [Fact]
    public void P1Tone_HasRoundedRect_110x240()
    {
        var sym = BuiltInSymbols.Primitives(SymbolKind.P1Tone);
        var rects = sym.Primitives.OfType<RoundedRectPrimitive>().ToList();

        Assert.Single(rects);
        Assert.Equal(110.0, rects[0].W, 0.1);
        Assert.Equal(240.0, rects[0].H, 0.1);
    }

    [Fact]
    public void P1Tone_HasCircleAndSineWithOneCycle()
    {
        var sym = BuiltInSymbols.Primitives(SymbolKind.P1Tone);

        var circles = sym.Primitives.OfType<CirclePrimitive>().ToList();
        Assert.Single(circles);

        var sines = sym.Primitives.OfType<SinePrimitive>().ToList();
        Assert.Single(sines);
        Assert.Equal(1.0, sines[0].Cycles, 0.01);
    }

    [Fact]
    public void P1Tone_PortDefs_TopAndBottom()
    {
        var ports = SymbolPortDefs.For(SymbolKind.P1Tone);
        Assert.Equal(2, ports.Length);
        // Top port at (0, -200), bottom at (0, +200)
        Assert.True(ports.Any(p => System.Math.Abs(p.LocalX) < 0.1 && System.Math.Abs(p.LocalY - (-200)) < 0.1),
            "P1Tone must have a port at (0,−200)");
        Assert.True(ports.Any(p => System.Math.Abs(p.LocalX) < 0.1 && System.Math.Abs(p.LocalY - 200) < 0.1),
            "P1Tone must have a port at (0,+200)");
    }

    // ── Part 5: Pin port at (100,0), hex x-extent [-100,50], stem (50,0)→(100,0) ──

    [Fact]
    public void Pin_PortDef_AtOneHundredZero()
    {
        var ports = SymbolPortDefs.For(SymbolKind.Pin);
        Assert.Single(ports);
        Assert.Equal(100f, ports[0].LocalX);
        Assert.Equal(0f,   ports[0].LocalY);
    }

    [Fact]
    public void Pin_HexagonExtent_And_StemCoords()
    {
        var sym  = BuiltInSymbols.Primitives(SymbolKind.Pin);

        // Hexagon primitive
        var hex = sym.Primitives.OfType<PolygonPrimitive>().FirstOrDefault();
        Assert.NotNull(hex);
        var xs = hex.Points.Select(p => p[0]).ToList();
        Assert.True(xs.Min() >= -100 - 0.1 && xs.Min() <= -100 + 0.1,
            $"Hex MinX should be -100, got {xs.Min()}");
        Assert.True(xs.Max() >= 50 - 0.1 && xs.Max() <= 50 + 0.1,
            $"Hex MaxX should be 50, got {xs.Max()}");

        // Stem line: from (50,0) to (100,0)
        var stem = sym.Primitives.OfType<LinePrimitive>().FirstOrDefault(l =>
            ((System.Math.Abs(l.X1 - 50) < 0.1 && System.Math.Abs(l.X2 - 100) < 0.1) ||
             (System.Math.Abs(l.X1 - 100) < 0.1 && System.Math.Abs(l.X2 - 50) < 0.1)) &&
            System.Math.Abs(l.Y1) < 0.1 && System.Math.Abs(l.Y2) < 0.1);
        Assert.NotNull(stem);
    }

    [Fact]
    public void Pin_TotalXSpan_Is200()
    {
        var sym   = BuiltInSymbols.Primitives(SymbolKind.Pin);
        var (minX, _, maxX, _) = SymbolGeometry.ComputeBb(sym.Primitives);
        Assert.Equal(200.0, maxX - minX, 1.0);
    }
}
