using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// VAR and MEAS are all text — the glyph is a small box and the rows beneath it are the content.
/// The shared label anchor was sized for a two-terminal part whose leads run to ±200, so on these
/// it hung the block down and to the LEFT of a box with no lead to clear. Their labels now
/// left-justify to the glyph's own left edge and sit just below its bottom.
///
/// <para>Second subject: a parameter added AFTER the labels were moved. It has no saved offset of
/// its own, and reading (0,0) for it dropped that one row back at the un-moved default position.
/// It must render directly under the row above it.</para>
/// </summary>
public sealed class VarMeasLabelPlacementTests
{
    private static (double X, double Y) Row(SymbolKind kind, int row, double oDx = 0, double oDy = 0)
    {
        var (bx, by, _, _) = SchematicComponent.LabelRowGeometry(
            0, 0, row, oDx, oDy, kind, portCount: 0, glyphHalfH: SchematicComponent.AnnotationBodyHalfH);
        return (bx, by);
    }

    // ── Placement ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SymbolKind.Var)]
    [InlineData(SymbolKind.Meas)]
    public void AnnotationLabels_LeftJustifyToGlyphLeftEdge(SymbolKind kind)
    {
        Assert.Equal(-SchematicComponent.AnnotationBodyHalfW,
                     SchematicComponent.LabelBaseXFor(kind));

        // Flush with the box's left edge, and strictly right of the shared lead-clearing anchor.
        Assert.Equal(-SchematicComponent.AnnotationBodyHalfW, Row(kind, 0).X, 6);
        Assert.True(Row(kind, 0).X > SchematicComponent.LabelBaseOffsetX,
            "the block must move IN toward the glyph, not stay out at the two-terminal anchor");
    }

    [Theory]
    [InlineData(SymbolKind.Var)]
    [InlineData(SymbolKind.Meas)]
    public void AnnotationLabels_SitJustBelowTheGlyph_WithPadding(SymbolKind kind)
    {
        double capTop     = Row(kind, 0).Y - SchematicComponent.LabelWorldHeight;
        double glyphBottom = SchematicComponent.AnnotationBodyHalfH;

        Assert.Equal(SchematicComponent.AnnotationLabelPadY, capTop - glyphBottom, 6);
        Assert.True(capTop > glyphBottom, "the first row must not overlap the box");
        Assert.True(Row(kind, 0).Y < SchematicComponent.LabelBaseY,
            "the block must move UP from the two-terminal default");
    }

    [Fact]
    public void RowPitchIsUnchanged_ForAnnotationSymbols()
        => Assert.Equal(SchematicComponent.LabelWorldStep, Row(SymbolKind.Var, 1).Y - Row(SymbolKind.Var, 0).Y, 6);

    [Theory]
    [InlineData(SymbolKind.Resistor)]
    [InlineData(SymbolKind.Capacitor)]
    [InlineData(SymbolKind.Sdd)]
    public void OtherSymbols_KeepTheSharedAnchor(SymbolKind kind)
        => Assert.Equal(SchematicComponent.LabelBaseOffsetX, SchematicComponent.LabelBaseXFor(kind));

    /// <summary>The placed component's own cull box must follow the anchor it is drawn at.</summary>
    [Fact]
    public void PlacedVar_FullBb_TracksTheNewAnchor()
    {
        var model = new SchematicEditModel();
        var comp  = new EditableComponent { InstanceName = "VAR1", Symbol = SymbolKind.Var, X = 1000, Y = 1000 };
        comp.Parameters.Add(new EditableParameter
            { Name = "RFfreq", Expression = "2", Unit = "GHz", ShowOnSchematic = true });
        model.Components.Add(comp);

        var rc = model.BuildRenderModel().Model.Components.First();
        var (bx, by, _, _) = SchematicComponent.LabelRowGeometry(
            rc.X, rc.Y, 0, 0, 0, rc.Symbol, 0, rc.GlyphBbMaxY - rc.Y);

        Assert.Equal(rc.X - SchematicComponent.AnnotationBodyHalfW, bx, 6);
        Assert.True(rc.FullBbMinX <= bx + 1e-9, $"FullBbMinX {rc.FullBbMinX} must cover the anchor {bx}");
        Assert.True(rc.FullBbMinY <= by - SchematicComponent.LabelWorldHeight + 1e-9);
    }

    // ── A parameter added after the labels were moved ─────────────────────────

    [Fact]
    public void LabelOffsetAt_FallsBackToTheLastStoredOffset()
    {
        var offsets = new List<(double DX, double DY)> { (10, 20), (10, 20), (10, 20) };
        Assert.Equal((10.0, 20.0), SchematicComponent.LabelOffsetAt(offsets, 0));
        Assert.Equal((10.0, 20.0), SchematicComponent.LabelOffsetAt(offsets, 7));   // row added since the move
        Assert.Equal((0.0, 0.0),   SchematicComponent.LabelOffsetAt([], 3));        // never moved
    }

    [Fact]
    public void ParameterAddedAfterAMove_RendersDirectlyUnderTheRowAbove()
    {
        var model = new SchematicEditModel();
        var comp  = new EditableComponent { InstanceName = "VAR1", Symbol = SymbolKind.Var, X = 0, Y = 0 };
        comp.Parameters.Add(new EditableParameter
            { Name = "RFfreq", Expression = "2", Unit = "GHz", ShowOnSchematic = true });
        model.Components.Add(comp);

        // The user drags the block: three rows (type, name, one param), all offset by the same delta.
        comp.LabelOffsets.AddRange([(300.0, -400.0), (300.0, -400.0), (300.0, -400.0)]);

        // …then adds a second variable, which has no offset of its own.
        comp.Parameters.Add(new EditableParameter
            { Name = "Pin", Expression = "10", Unit = "dBm", ShowOnSchematic = true });

        var rc = model.BuildRenderModel().Model.Components.First();
        Assert.Equal(4, rc.Labels.Count);

        (double X, double Y) At(int i)
        {
            var (oDx, oDy) = SchematicComponent.LabelOffsetAt(rc.LabelOffsets, i);
            var (bx, by, _, _) = SchematicComponent.LabelRowGeometry(
                rc.X, rc.Y, i, oDx, oDy, rc.Symbol, 0, rc.GlyphBbMaxY - rc.Y);
            return (bx, by);
        }

        var prev = At(2);
        var added = At(3);
        Assert.Equal(prev.X, added.X, 6);
        Assert.Equal(prev.Y + SchematicComponent.LabelWorldStep, added.Y, 6);

        // The cull box must cover it too, or the moved block loses its last row at the viewport edge.
        Assert.True(rc.FullBbMaxY >= added.Y - 1e-9);
    }

    /// <summary>The added row is clickable where it is drawn — hit-test and renderer read one offset.</summary>
    [Fact]
    public void ParameterAddedAfterAMove_IsHitTestableAtItsDrawnPosition()
    {
        var model = new SchematicEditModel();
        var comp  = new EditableComponent { InstanceName = "VAR1", Symbol = SymbolKind.Var, X = 0, Y = 0 };
        comp.Parameters.Add(new EditableParameter
            { Name = "RFfreq", Expression = "2", Unit = "GHz", ShowOnSchematic = true });
        model.Components.Add(comp);
        comp.LabelOffsets.AddRange([(300.0, -400.0), (300.0, -400.0), (300.0, -400.0)]);
        comp.Parameters.Add(new EditableParameter
            { Name = "Pin", Expression = "10", Unit = "dBm", ShowOnSchematic = true });

        var built = model.BuildRenderModel();
        var rc    = built.Model.Components.First();
        var (oDx, oDy) = SchematicComponent.LabelOffsetAt(rc.LabelOffsets, 3);
        var (bx, by, _, _) = SchematicComponent.LabelRowGeometry(
            rc.X, rc.Y, 3, oDx, oDy, rc.Symbol, 0, rc.GlyphBbMaxY - rc.Y);

        var hit = SchematicHitTest.Test(model, built.Model, built.Index,
                                        bx + 40, by - 20, zoom: 1.0);
        Assert.Equal(SchematicHitTest.HitKind.ComponentParam, hit.Kind);
        Assert.Equal(1, hit.SubIndex);
    }
}
