using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// R-pc-8: "an MLIN in a PCB workspace picks up FR-4 1.6mm with no user configuration." End-to-end
/// through a real temp workspace (.cws + .ctech on disk), proving the ancestor-.cws walk +
/// DefaultTechRef resolution actually works, not just SubstrateResolver's own pure logic
/// (covered separately in Layout/PCells/SubstrateResolverTests.cs).
/// </summary>
public class MicrostripSubstrateInjectionTests : IDisposable
{
    private readonly string _root;

    public MicrostripSubstrateInjectionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-mstrip-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string WriteWorkspaceWithTech(Technology tech)
    {
        Directory.CreateDirectory(Path.Combine(_root, "tech"));
        TechPersistence.SaveToFile(Path.Combine(_root, "tech", "pcb.ctech"), tech);

        var cws = new CwsFile { DefaultTechRef = "tech/pcb.ctech" };
        WorkspacePersistence.SaveToFile(Path.Combine(_root, ".cws"), cws);

        var schematicDir = Path.Combine(_root, "Amp", "schematic");
        Directory.CreateDirectory(schematicDir);
        return schematicDir;
    }

    [Fact]
    public void ResolveWorkspaceTechnology_WalksUpToAncestorCws_LoadsDefaultTech()
    {
        var schematicDir = WriteWorkspaceWithTech(StarterTechnologies.Pcb2Layer());

        var tech = MicrostripSubstrateInjection.ResolveWorkspaceTechnology(schematicDir);

        Assert.NotNull(tech);
        Assert.Equal("PCB 2-Layer", tech!.Name);
    }

    [Fact]
    public void BuildOverrides_ForRealPcbWorkspace_ProducesFr4SubstrateWithNoConfiguration()
    {
        var schematicDir = WriteWorkspaceWithTech(StarterTechnologies.Pcb2Layer());
        var tech = MicrostripSubstrateInjection.ResolveWorkspaceTechnology(schematicDir);

        var overrides = MicrostripSubstrateInjection.BuildOverrides(tech, out var warning);

        Assert.Null(warning);
        Assert.Equal(5, overrides.Count);
        var h = overrides.First(o => o.Name == "H");
        var er = overrides.First(o => o.Name == "Er");
        Assert.Equal(1.6e-3, double.Parse(h.Expression), 6);
        Assert.Equal(4.4, double.Parse(er.Expression), 6);
    }

    [Fact]
    public void ResolveWorkspaceTechnology_NoAncestorWorkspace_ReturnsNull()
    {
        var looseDir = Path.Combine(_root, "loose");
        Directory.CreateDirectory(looseDir);
        Assert.Null(MicrostripSubstrateInjection.ResolveWorkspaceTechnology(looseDir));
    }

    [Fact]
    public void BuildOverrides_NoTechnology_ReturnsEmpty_WithReason()
    {
        var overrides = MicrostripSubstrateInjection.BuildOverrides(null, out var warning);
        Assert.Empty(overrides);
        Assert.NotNull(warning);
    }

    // ── brief-technology-editor-units-and-layers.md gate 8: SignalLayer override changes H/Er ────

    [Fact]
    public void BuildOverrides_MmicSignalLayerOverride_ChangesResolvedH_InTheExpectedDirection()
    {
        var tech = StarterTechnologies.MmicGaAs();

        var defaultOverrides = MicrostripSubstrateInjection.BuildOverrides(tech, out var w1);
        Assert.Null(w1);
        double hDefault = double.Parse(defaultOverrides.First(o => o.Name == "H").Expression);

        // Metal1 is closer to the ground plane than Metal2 (the default, topmost conductor) —
        // spanning only the GaAs layer rather than Air+GaAs — so its resolved H must be smaller.
        var metal1Overrides = MicrostripSubstrateInjection.BuildOverrides(tech, out var w2, signalLayerNameOverride: "Metal1");
        Assert.Null(w2);
        double hMetal1 = double.Parse(metal1Overrides.First(o => o.Name == "H").Expression);

        Assert.True(hMetal1 < hDefault, $"expected Metal1's H ({hMetal1}) < Metal2's default H ({hDefault})");
    }

    [Fact]
    public void BuildOverrides_UnknownSignalLayerOverride_FallsBackToDefault_ReportsWarning()
    {
        // R-tec-9: a stale/renamed layer name reports and falls back rather than failing outright.
        var tech = StarterTechnologies.Pcb2Layer();
        var overrides = MicrostripSubstrateInjection.BuildOverrides(tech, out var warning, signalLayerNameOverride: "NoSuchLayer");

        Assert.NotEmpty(overrides);
        Assert.NotNull(warning);
        Assert.Contains("NoSuchLayer", warning);
    }
}
