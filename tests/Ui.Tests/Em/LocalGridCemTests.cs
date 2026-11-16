// PMS-4 R-pms4-1 / R-pms4-4, PMS-7 — the local-grid setting in the .cem and in the provenance stamp, and the preflight a
// drawn layout takes with it on: the mesh and its count are reported, and (PMS-6 R-pms6-6) nothing refuses it. Since
// PMS-7 ON is the product default and is never written; off is written; a file that omits the key reads on.

using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using CircuitRF.Ui.Layout.Em;

namespace CircuitRF.Ui.Tests.Em;

public class LocalGridCemTests
{
    private static EmSetup Setup(bool local) => new()
    {
        Name = "planar", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar,
        PlanarMesh = EmSetup.DefaultPlanarMesh with { LocalGrid = local },
    };

    [Fact]
    public void On_IsTheDefault_AndOmitted_AndOff_RoundTrips()
    {
        Assert.True(EmSetup.DefaultPlanarMesh.LocalGrid);
        Assert.False(PlanarMeshSettings.Default.LocalGrid);   // the engine keeps it off
        Assert.DoesNotContain("LocalGrid", EmSetupPersistence.Serialize(Setup(true)), StringComparison.Ordinal);

        string json = EmSetupPersistence.Serialize(Setup(false));
        Assert.Contains("\"LocalGrid\": false", json, StringComparison.Ordinal);
        Assert.False(EmSetupPersistence.Deserialize(json).PlanarMesh.LocalGrid);
        Assert.False(Setup(false).Clone().PlanarMesh.LocalGrid);
        Assert.Equal(json, EmSetupPersistence.Serialize(EmSetupPersistence.Deserialize(json)));

        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        Assert.True(node["PlanarMesh"]!.AsObject().Remove("LocalGrid"));
        Assert.True(EmSetupPersistence.Deserialize(node.ToJsonString()).PlanarMesh.LocalGrid);
    }

    /// <summary>ICF-4 R-icf4-1 — the local grid now starts small artwork from one block, so a result meshed before reads stale on
    /// such artwork only: a 100 µm pad on GaAs at 40 GHz gains the term, a 20 mm line and the grid switched off do not.</summary>
    [Fact]
    public void SmallArtwork_GainsTheBaseTerm_ABoardDoesNot()
    {
        static PlanarProblem Rect(double w, double l) =>
            new([new PlanarConductorLayer("Metal1", [new PlanarPolygon([new EmPoint(0, 0), new EmPoint(l, 0), new EmPoint(l, w), new EmPoint(0, w)])],
                                          4.1e7, 3e-6)], GroundedSlab.GaAsStarter, 40e9);
        var on = EmSetup.DefaultPlanarMesh;
        var pad = Rect(100e-6, 100e-6);
        var board = Rect(500e-6, 20e-3);
        Assert.NotEqual(EmSnpProvenance.MeshHash(on), EmSnpProvenance.MeshHash(on, pad));
        Assert.Equal(EmSnpProvenance.MeshHash(on), EmSnpProvenance.MeshHash(on, board));
        var off = on with { LocalGrid = false };
        Assert.Equal(EmSnpProvenance.MeshHash(off), EmSnpProvenance.MeshHash(off, pad));
    }

    [Fact]
    public void OnAndOff_HashApart()
    {
        var on = EmSetup.DefaultPlanarMesh;
        Assert.NotEqual(EmSnpProvenance.MeshHash(on), EmSnpProvenance.MeshHash(on with { LocalGrid = false }));
    }

    /// <summary>A2 on the shipped MMIC technology, local grid on: the preflight (what `check`, `em --dry-run` and the
    /// panel run) hands back the block mesh with its count and, since PMS-6, no refusal.</summary>
    [Fact]
    public void ThePreflight_ReportsTheMesh_AndDoesNotRefuseIt()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pms4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var source = SyntheticMmicChipLayout.Source(SyntheticMmicChip.AccuracyBlock(regionVia: false),
                                                        Path.Combine(dir, "a2.clay"));
            var setup = new EmSetup
            {
                Name = "a2", LayoutRef = "a2.clay",
                Frequency = new Core.Design.FrequencySpec("10", "10", 1, Core.Design.SweepKind.Linear, "GHz", "GHz"),
                PlanarMesh = EmSetup.DefaultPlanarMesh with { LocalGrid = true },
            };

            var pre = EmRunService.Preflight(setup, source, null, memoryCeilingBytes: 16_000_000_000L);

            Assert.NotNull(pre.PlanarMesh);
            Assert.True(pre.PlanarMesh!.LocalGrid, string.Join("\n", pre.Findings.Select(f => f.Text)));
            Assert.True(pre.PlanarMesh.Mesh.HasBlocks, string.Join("\n", pre.PlanarMesh.Notes));
            Assert.True(pre.PlanarMesh.UnknownCount > 0);
            Assert.Null(pre.Refusal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
