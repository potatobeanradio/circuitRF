using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Interchange;

namespace CircuitRF.Ui.Tests;

public class GdsiiLayerReconciliationTests
{
    private static readonly LayoutShape Shape1 = new RectShape { Layer = new LayerKey(1, 0) };
    private static readonly LayoutShape Shape2 = new RectShape { Layer = new LayerKey(7, 0) };

    [Fact]
    public void BuildSourceLayers_MatchesDestTechByNativeKey_SameKeySameName()
    {
        var destTech = new Technology
        {
            Layers = [new LayerDef { Key = new LayerKey(1, 0), Name = "Metal1" }],
        };
        var sourceLayers = GdsiiLayerReconciliation.BuildSourceLayers([Shape1], destTech);
        var rows = LayoutLayerMapping.Propose([Shape1], sourceLayers, destTech);

        Assert.Single(rows);
        Assert.Equal(LayerMatchKind.SameKeySameName, rows[0].Match);
    }

    [Fact]
    public void BuildSourceLayers_MatchesViaDeclaredGdsiiAlias_EvenWhenKeyDiffers()
    {
        var destTech = new Technology
        {
            Layers =
            [
                new LayerDef
                {
                    Key = new LayerKey(50, 0), // internal key differs from the GDSII-facing alias
                    Name = "Metal1",
                    Interchange = new InterchangeMapping(1, 0, null, null, null),
                },
            ],
        };
        var sourceLayers = GdsiiLayerReconciliation.BuildSourceLayers([Shape1], destTech);
        var rows = LayoutLayerMapping.Propose([Shape1], sourceLayers, destTech);

        Assert.Single(rows);
        // The synthetic source layer's key is the incoming GDSII key (1,0), which differs from the
        // destination's own internal key (50,0) — so this resolves as a confident NAME match
        // (ExactName) rather than SameKeySameName, exactly as L1g's own matching order intends.
        Assert.Equal(LayerMatchKind.ExactName, rows[0].Match);
        Assert.Equal(new LayerKey(50, 0), rows[0].Proposed);
    }

    [Fact]
    public void BuildSourceLayers_UnknownLayer_NoMatch_RequiresConfirmation()
    {
        var destTech = new Technology { Layers = [new LayerDef { Key = new LayerKey(1, 0), Name = "Metal1" }] };
        var sourceLayers = GdsiiLayerReconciliation.BuildSourceLayers([Shape2], destTech);
        var rows = LayoutLayerMapping.Propose([Shape2], sourceLayers, destTech);

        Assert.Single(rows);
        Assert.Equal(LayerMatchKind.NoMatch, rows[0].Match);
        Assert.True(LayoutLayerMapping.RequiresConfirmation(rows));
    }

    [Fact]
    public void BuildSourceLayers_NullDestTech_ProducesEmptyNames_NoThrow()
    {
        var sourceLayers = GdsiiLayerReconciliation.BuildSourceLayers([Shape1, Shape2], null);
        Assert.Equal(2, sourceLayers.Count);
        Assert.All(sourceLayers, l => Assert.Equal("", l.Name));
    }

    [Fact]
    public void BuildSourceLayers_DistinctKeysOnly_OneRowPerDistinctLayer()
    {
        var shapes = new List<LayoutShape>
        {
            new RectShape { Layer = new LayerKey(1, 0) },
            new RectShape { Layer = new LayerKey(1, 0) },
            new RectShape { Layer = new LayerKey(2, 0) },
        };
        var sourceLayers = GdsiiLayerReconciliation.BuildSourceLayers(shapes, null);
        Assert.Equal(2, sourceLayers.Count);
    }
}
