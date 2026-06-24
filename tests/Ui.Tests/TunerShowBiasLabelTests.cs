using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The Tuner family (Tuner/SourceTuner/LoadTuner) grows its glyph downward when ShowBias=true (a bias
/// branch is drawn below the box). The renderer's DrawLabels clears that extent via
/// glyphHalfH = GlyphBbMaxY - Y. The inline text editor and the label hit-test must use the SAME extent,
/// or they sit too high over the tuner. This gates that all three share the bias-aware baseline.
/// </summary>
public sealed class TunerShowBiasLabelTests
{
    [Theory]
    [InlineData(SymbolKind.Tuner)]
    [InlineData(SymbolKind.SourceTuner)]
    [InlineData(SymbolKind.LoadTuner)]
    public void ShowBias_ExtendsGlyph_AndPushesLabelBaselineDown(SymbolKind kind)
    {
        var noBias = new EditableComponent { Symbol = kind, X = 1000, Y = 1000 };
        var bias   = new EditableComponent { Symbol = kind, X = 1000, Y = 1000 };
        bias.Parameters.Add(new EditableParameter { Name = "ShowBias", Expression = "true" });

        double halfNoBias = noBias.ComputeGlyphBb().MaxY - noBias.Y;
        double halfBias   = bias.ComputeGlyphBb().MaxY - bias.Y;
        Assert.True(halfBias > halfNoBias,
            $"ShowBias must extend the glyph downward (noBias={halfNoBias}, bias={halfBias})");

        int pc = bias.PortCount;

        // The shared baseline: renderer/hit-test/inline-editor all derive it from LabelRowGeometry
        // with glyphHalfH = ComputeGlyphBb().MaxY - Y.
        double baselineWithBias = SchematicComponent
            .LabelRowGeometry(bias.X, bias.Y, 0, 0, 0, kind, pc, halfBias).BaselineY;

        // The old bug passed null for non-SnP symbols → LabelBaseYFor fell back to the 100-unit box
        // half-height → the default LabelBaseY, sitting the editor above the bias branch.
        double baselineNull = SchematicComponent
            .LabelRowGeometry(bias.X, bias.Y, 0, 0, 0, kind, pc, null).BaselineY;

        Assert.True(baselineWithBias > baselineNull,
            $"ShowBias label baseline {baselineWithBias} must sit below the old null-fallback {baselineNull}");
    }
}
