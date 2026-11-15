// ICF-5 R-icf5-3, gate 4 — an array of identical via cuts becomes one region, its DC resistance unchanged, and the
// setting turns it off. The airbridge fixture and layer keys are RegionViaExtractionTests' own (the L9 gate's MMIC starter).

using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests.Em;

public class ViaArrayMergeTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    private static readonly LayerKey Metal1 = new(1, 0);
    private static readonly LayerKey Metal2 = new(2, 0);
    private static readonly LayerKey Post   = new(3, 0);   // -> "Metal1-Metal2 Post"

    private static long Um(double v) => (long)Math.Round(v * Dbu);

    private static RectShape Rect(LayerKey layer, double x0, double y0, double x1, double y1) =>
        new() { Layer = layer, X1 = Um(x0), Y1 = Um(y0), X2 = Um(x1), Y2 = Um(y1) };

    /// <summary>The airbridge with a 4 × 4 array of 4 µm cuts at 8 µm pitch where Metal1 and Metal2 overlap.</summary>
    private static List<LayoutShape> AirbridgeWithArray()
    {
        var shapes = new List<LayoutShape>
        {
            Rect(Metal1, 0, 0, 120, 100), Rect(Metal1, 180, 0, 300, 100), Rect(Metal2, 20, 0, 280, 100),
            new LabelShape { Layer = Metal1, X = Um(0), Y = Um(50), Text = "P1", Height = Um(20), IsPort = true },
            new LabelShape { Layer = Metal1, X = Um(300), Y = Um(50), Text = "P2", Height = Um(20), IsPort = true },
        };
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                shapes.Add(Rect(Post, 40 + 8 * i, 30 + 8 * j, 44 + 8 * i, 34 + 8 * j));
        return shapes;
    }

    private static double SigmaA(PlanarVia v) => v.SigmaSm * v.Polygons.Sum(p => p.Area());

    [Fact]
    public void AViaArray_BecomesOneRegion_WithItsDcResistanceUnchanged_AndTheSettingTurnsItOff()
    {
        var tech = StarterTechnologies.MmicGaAs();

        var asDrawn = PlanarExtractor.Extract(AirbridgeWithArray(), tech, Dbu, 30e9,
                                              EmExtractionSettings.Default with { MergeViaArrays = false });
        Assert.True(asDrawn.Ok, asDrawn.Refusal);
        var cuts = Assert.Single(asDrawn.Problem!.ViaList);
        Assert.Equal(16, cuts.Polygons.Count);
        Assert.DoesNotContain(asDrawn.Notes, n => n.Contains("cuts merged", StringComparison.Ordinal));

        var merged = PlanarExtractor.Extract(AirbridgeWithArray(), tech, Dbu, 30e9);
        Assert.True(merged.Ok, merged.Refusal);
        var hull = Assert.Single(Assert.Single(merged.Problem!.ViaList).Polygons);
        Assert.Equal(28e-6 * 28e-6, hull.Area(), 15);                    // 40..68 × 30..58 µm

        // Same length, so equal σ·A is equal DC resistance.
        Assert.Equal(1.0, SigmaA(merged.Problem.ViaList[0]) / SigmaA(cuts), 9);
        Assert.Contains(merged.Notes, n => n.Contains("16 cuts merged into one region, fill 0.33", StringComparison.Ordinal));
    }
}
