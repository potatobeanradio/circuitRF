// PMS-3 R-pms3-4 — the stacked-regions setting in the .cem and in the provenance stamp. Off is the default and is never
// written (its hash term is appended only when on); on is written, round-trips, survives Clone and moves the hash.

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
    public void Off_IsOmitted_AndOn_RoundTrips()
    {
        Assert.False(EmSetup.DefaultPlanarMesh.StackedRegions);
        Assert.DoesNotContain("StackedRegions", EmSetupPersistence.Serialize(Setup(false)), StringComparison.Ordinal);

        string json = EmSetupPersistence.Serialize(Setup(true));
        Assert.Contains("\"StackedRegions\": true", json, StringComparison.Ordinal);
        Assert.True(EmSetupPersistence.Deserialize(json).PlanarMesh.StackedRegions);
        Assert.True(Setup(true).Clone().PlanarMesh.StackedRegions);
        Assert.Equal(json, EmSetupPersistence.Serialize(EmSetupPersistence.Deserialize(json)));
    }

    [Fact]
    public void On_MovesTheMeshHash()
    {
        var off = EmSetup.DefaultPlanarMesh;
        Assert.NotEqual(EmSnpProvenance.MeshHash(off), EmSnpProvenance.MeshHash(off with { StackedRegions = true }));
    }
}
