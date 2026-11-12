// PMS-3 R-pms3-4, PMS-7 — the stacked-regions setting in the .cem and in the provenance stamp. Since PMS-7 ON is the
// product default and is never written; off is written, round-trips and survives Clone; a file that omits the key reads
// on; and the two states hash apart (the term is present at the default, so an .snp stamped before PMS-7 — which
// carries none, the spelling of off — reads as stale).

using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout.Em;

namespace CircuitRF.Ui.Tests.Em;

public class StackedRegionsCemTests
{
    private static EmSetup Setup(bool stacked) => new()
    {
        Name = "planar", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar,
        PlanarMesh = EmSetup.DefaultPlanarMesh with { StackedRegions = stacked },
    };

    [Fact]
    public void On_IsTheDefault_AndOmitted_AndOff_RoundTrips()
    {
        Assert.True(EmSetup.DefaultPlanarMesh.StackedRegions);
        Assert.False(PlanarMeshSettings.Default.StackedRegions);   // the engine keeps it off, as it keeps merging off
        Assert.DoesNotContain("StackedRegions", EmSetupPersistence.Serialize(Setup(true)), StringComparison.Ordinal);

        string json = EmSetupPersistence.Serialize(Setup(false));
        Assert.Contains("\"StackedRegions\": false", json, StringComparison.Ordinal);
        Assert.False(EmSetupPersistence.Deserialize(json).PlanarMesh.StackedRegions);
        Assert.False(Setup(false).Clone().PlanarMesh.StackedRegions);
        Assert.Equal(json, EmSetupPersistence.Serialize(EmSetupPersistence.Deserialize(json)));

        // A file written before PMS-7 omitted the key while it meant off; it now reads on.
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        Assert.True(node["PlanarMesh"]!.AsObject().Remove("StackedRegions"));
        Assert.True(EmSetupPersistence.Deserialize(node.ToJsonString()).PlanarMesh.StackedRegions);
    }

    [Fact]
    public void OnAndOff_HashApart()
    {
        var on = EmSetup.DefaultPlanarMesh;
        Assert.NotEqual(EmSnpProvenance.MeshHash(on), EmSnpProvenance.MeshHash(on with { StackedRegions = false }));
    }
}
