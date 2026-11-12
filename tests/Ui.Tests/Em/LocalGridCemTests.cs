// PMS-4 R-pms4-1 / R-pms4-4 — the local-grid setting in the .cem and in the provenance stamp, and the preflight a drawn
// layout takes with it on: the mesh and its count are reported, and (PMS-6 R-pms6-6) nothing refuses it.

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
    public void Off_IsOmitted_AndOn_RoundTrips()
    {
        Assert.False(EmSetup.DefaultPlanarMesh.LocalGrid);
        Assert.DoesNotContain("LocalGrid", EmSetupPersistence.Serialize(Setup(false)), StringComparison.Ordinal);

        string json = EmSetupPersistence.Serialize(Setup(true));
        Assert.Contains("\"LocalGrid\": true", json, StringComparison.Ordinal);
        Assert.True(EmSetupPersistence.Deserialize(json).PlanarMesh.LocalGrid);
        Assert.True(Setup(true).Clone().PlanarMesh.LocalGrid);
        Assert.Equal(json, EmSetupPersistence.Serialize(EmSetupPersistence.Deserialize(json)));
    }

    [Fact]
    public void On_MovesTheMeshHash_AndOffDoesNot()
    {
        var off = EmSetup.DefaultPlanarMesh;
        Assert.NotEqual(EmSnpProvenance.MeshHash(off), EmSnpProvenance.MeshHash(off with { LocalGrid = true }));
        Assert.DoesNotContain("local", "|" + EmSnpProvenance.MeshHash(off), StringComparison.Ordinal);
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
