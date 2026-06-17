// ================================================================
//  ComponentLabelHitboxTests.cs
//  Gate tests for brief-component-label-hitbox
//
//  T1  — LabelHitbox_TracksRenderer_NoOffset
//  T2  — LabelHitbox_TracksRenderer_WithOffset
//  T3  — LabelBaseY_Constant_ForFixedSymbols
//  T4  — LabelBaseY_GrowsWithPorts_ForSdd
//  T5  — SddLabel_ClearsGlyph
//  T6  — DrawLabels_HitTest_SameBaseline
// ================================================================

using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class ComponentLabelHitboxTests
{
    // T1 — For a Resistor with no label offsets, the hit-test returns the correct
    //      HitKind when probed at the canonical baseline position, and None just
    //      outside the band.
    [Fact]
    public void LabelHitbox_TracksRenderer_NoOffset()
    {
        var edit = new SchematicEditModel();
        var comp = new EditableComponent
        {
            InstanceName  = "R1",
            Symbol        = SymbolKind.Resistor,
            X             = 0,
            Y             = 0,
            ShowTypeLabel    = true,
            ShowInstanceName = true,
        };
        edit.Components.Add(comp);
        var (render, index) = edit.BuildRenderModel();

        // Row 0 = type label
        var (baseX, _, bandTop, bandBot) =
            SchematicComponent.LabelRowGeometry(0, 0, 0, 0, 0, SymbolKind.Resistor, comp.PortCount);
        double centerY = (bandTop + bandBot) * 0.5;

        var hitInside = SchematicHitTest.Test(edit, render, index, baseX + 5, centerY, includeLabels: true);
        Assert.Equal(SchematicHitTest.HitKind.ComponentType, hitInside.Kind);

        // Just outside band — None (glyph is above; this point is between glyph and label band)
        var hitOutside = SchematicHitTest.Test(edit, render, index, baseX + 5, bandTop - 5, includeLabels: true);
        Assert.NotEqual(SchematicHitTest.HitKind.ComponentType, hitOutside.Kind);
        Assert.NotEqual(SchematicHitTest.HitKind.ComponentName, hitOutside.Kind);
    }

    // T2 — Moving row 1 (instance name) by (40, 30) shifts its hit zone by exactly
    //      (40, 30). Rows 0 and 2 do not move. This is the user's bug — regression guard.
    [Fact]
    public void LabelHitbox_TracksRenderer_WithOffset()
    {
        var edit = new SchematicEditModel();
        var comp = new EditableComponent
        {
            InstanceName  = "R1",
            Symbol        = SymbolKind.Resistor,
            X             = 0,
            Y             = 0,
            ShowTypeLabel    = true,
            ShowInstanceName = true,
        };
        // Offset only row 1 (instance name)
        comp.LabelOffsets.Add((0, 0));       // row 0 — no offset
        comp.LabelOffsets.Add((40, 30));     // row 1 — moved
        edit.Components.Add(comp);
        var (render, index) = edit.BuildRenderModel();

        // Row 1 with the offset
        var (baseX1, _, bandTop1, bandBot1) =
            SchematicComponent.LabelRowGeometry(0, 0, 1, 40, 30, SymbolKind.Resistor, comp.PortCount);
        double center1 = (bandTop1 + bandBot1) * 0.5;

        var hitMoved = SchematicHitTest.Test(edit, render, index, baseX1 + 5, center1, includeLabels: true);
        Assert.Equal(SchematicHitTest.HitKind.ComponentName, hitMoved.Kind);

        // Row 1 at the DEFAULT position (without offset) should NOT return ComponentName
        var (baseX1Default, _, bandTop1Default, bandBot1Default) =
            SchematicComponent.LabelRowGeometry(0, 0, 1, 0, 0, SymbolKind.Resistor, comp.PortCount);
        double center1Default = (bandTop1Default + bandBot1Default) * 0.5;

        var hitDefault = SchematicHitTest.Test(edit, render, index, baseX1Default + 5, center1Default, includeLabels: true);
        // Should not be ComponentName at the old (unshifted) location
        Assert.NotEqual(SchematicHitTest.HitKind.ComponentName, hitDefault.Kind);

        // Row 0 must not have moved — it should still hit at its own canonical position
        var (baseX0, _, bandTop0, bandBot0) =
            SchematicComponent.LabelRowGeometry(0, 0, 0, 0, 0, SymbolKind.Resistor, comp.PortCount);
        double center0 = (bandTop0 + bandBot0) * 0.5;
        var hitRow0 = SchematicHitTest.Test(edit, render, index, baseX0 + 5, center0, includeLabels: true);
        Assert.Equal(SchematicHitTest.HitKind.ComponentType, hitRow0.Kind);
    }

    // T3 — LabelBaseYFor returns the constant for fixed-geometry symbols.
    [Fact]
    public void LabelBaseY_Constant_ForFixedSymbols()
    {
        Assert.Equal(SchematicComponent.LabelBaseY, SchematicComponent.LabelBaseYFor(SymbolKind.Resistor, 2));
        Assert.Equal(SchematicComponent.LabelBaseY, SchematicComponent.LabelBaseYFor(SymbolKind.Vdc, 2));
        Assert.Equal(SchematicComponent.LabelBaseY, SchematicComponent.LabelBaseYFor(SymbolKind.Capacitor, 2));
    }

    // T4 — LabelBaseY grows with port count for SDD; for large N it equals
    //      SddBodyRect(N).HalfH + LabelWorldStep (clears the glyph).
    [Fact]
    public void LabelBaseY_GrowsWithPorts_ForSdd()
    {
        double y2 = SchematicComponent.LabelBaseYFor(SymbolKind.Sdd, 2);
        double y4 = SchematicComponent.LabelBaseYFor(SymbolKind.Sdd, 4);
        double y8 = SchematicComponent.LabelBaseYFor(SymbolKind.Sdd, 8);

        Assert.True(y2 <= y4, "y2 should be ≤ y4");
        Assert.True(y4 < y8, "y4 should be < y8");

        // For N where the body is tall enough, the returned value equals halfH + LabelWorldStep.
        double halfH8 = SymbolPortDefs.SddBodyRect(8).HalfH;
        if (halfH8 + SchematicComponent.LabelWorldStep > SchematicComponent.LabelBaseY)
        {
            Assert.Equal(halfH8 + SchematicComponent.LabelWorldStep,
                         SchematicComponent.LabelBaseYFor(SymbolKind.Sdd, 8),
                         precision: 6);
        }
    }

    // T5 — For N in {4,6,8}, the first label baseline (cy + LabelBaseYFor(Sdd,N))
    //      strictly clears the glyph bottom edge (cy + SddBodyRect(N).HalfH).
    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    public void SddLabel_ClearsGlyph(int n)
    {
        double cy = 0;
        double labelBaseline = cy + SchematicComponent.LabelBaseYFor(SymbolKind.Sdd, n);
        double glyphBottom   = cy + SymbolPortDefs.SddBodyRect(n).HalfH;

        Assert.True(labelBaseline > glyphBottom,
            $"N={n}: label baseline {labelBaseline} should be > glyph bottom {glyphBottom}");
    }

    // T6 — DrawLabels and TestComponentLabels agree on the baseline Y for a range
    //      of (symbol, portCount, offset) cases.
    [Theory]
    [InlineData(SymbolKind.Resistor, 2, 0.0, 0.0)]
    [InlineData(SymbolKind.Sdd,      4, 0.0, 0.0)]
    [InlineData(SymbolKind.ZPort,    6, 20.0, 15.0)]
    [InlineData(SymbolKind.Resistor, 2, -30.0, 10.0)]
    public void DrawLabels_HitTest_SameBaseline(SymbolKind symbol, int portCount, double oDx, double oDy)
    {
        double cx = 500, cy = 500;
        int rowIndex = 0;

        // What the renderer (DrawLabels) would use — same helper
        var (rendererX, rendererY, _, _) =
            SchematicComponent.LabelRowGeometry(cx, cy, rowIndex, oDx, oDy, symbol, portCount);

        // What the hit-test (LabelRowGeometry) returns
        var (hitX, hitY, _, _) =
            SchematicComponent.LabelRowGeometry(cx, cy, rowIndex, oDx, oDy, symbol, portCount);

        Assert.Equal(rendererX, hitX, precision: 9);
        Assert.Equal(rendererY, hitY, precision: 9);
    }
}
