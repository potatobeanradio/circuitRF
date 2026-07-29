using System;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// brief-technology-editor-units-and-layers.md R-tec-6/7/8/9 — the SignalLayer/GroundReference
/// override actually reaches SubstrateResolver end-to-end through NetExtractor.Extract, using a
/// REAL temp workspace (.cws + .ctech on disk) so the ancestor-.cws walk is genuinely exercised,
/// mirroring MicrostripSubstrateInjectionTests's own established pattern.
/// </summary>
public class NetExtractorMicrostripLayerOverrideTests : IDisposable
{
    private readonly string _root;

    public NetExtractorMicrostripLayerOverrideTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-mstrip-layerchoice-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string WriteWorkspaceWithTech(Technology tech)
    {
        Directory.CreateDirectory(Path.Combine(_root, "tech"));
        TechPersistence.SaveToFile(Path.Combine(_root, "tech", "t.ctech"), tech);

        var cws = new CwsFile { DefaultTechRef = "tech/t.ctech" };
        WorkspacePersistence.SaveToFile(Path.Combine(_root, ".cws"), cws);

        var schematicDir = Path.Combine(_root, "Amp", "schematic");
        Directory.CreateDirectory(schematicDir);
        return schematicDir;
    }

    private static EditableComponent MakeMlin(string? signalLayer = null, string? groundReference = null)
    {
        var comp = new EditableComponent { InstanceName = "ML1", Symbol = SymbolKind.Mlin, X = 0, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Mlin, 0))
        {
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name,
                Expression = dp.Name == "SignalLayer" ? (signalLayer ?? "")
                           : dp.Name == "GroundReference" ? (groundReference ?? "")
                           : dp.Expression,
                Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic,
                Dimension = dp.Dimension,
            });
        }
        return comp;
    }

    private static double OverrideValue(CircuitRF.Core.Design.Instance inst, string name)
        => double.Parse(inst.Overrides.First(o => o.Name == name).Expression);

    [Fact]
    public void DefaultParameters_Mlin_IncludesSignalLayerAndGroundReference_EmptyByDefault()
    {
        var dps = ComponentTypeRegistry.DefaultParameters(SymbolKind.Mlin, 0);
        var signal = dps.First(p => p.Name == "SignalLayer");
        var ground = dps.First(p => p.Name == "GroundReference");

        Assert.Equal("", signal.Expression);
        Assert.False(signal.ShowOnSchematic);
        Assert.Equal("", ground.Expression);
        Assert.False(ground.ShowOnSchematic);
    }

    [Fact]
    public void SignalLayerGroundReference_NeverReachTheEngineAsRawOverrides()
    {
        var schematicDir = WriteWorkspaceWithTech(StarterTechnologies.MmicGaAs());
        var model = new SchematicEditModel { SchematicDirectory = schematicDir };
        model.Components.Add(MakeMlin(signalLayer: "Metal1"));

        var result = NetExtractor.Extract(model);
        var inst = result.TestBench.Instances.First(i => i.InstanceName == "ML1");

        Assert.DoesNotContain(inst.Overrides, o => o.Name is "SignalLayer" or "GroundReference");
    }

    [Fact]
    public void EmptySignalLayer_ResolvesToDefault_TopmostConductor()
    {
        var schematicDir = WriteWorkspaceWithTech(StarterTechnologies.MmicGaAs());
        var model = new SchematicEditModel { SchematicDirectory = schematicDir };
        model.Components.Add(MakeMlin()); // both unset

        var result = NetExtractor.Extract(model);
        var inst = result.TestBench.Instances.First(i => i.InstanceName == "ML1");

        double hDefault = OverrideValue(inst, "H");

        // Cross-check directly against SubstrateResolver's own default resolution.
        var tech = StarterTechnologies.MmicGaAs();
        var (substrate, failure, _) = CircuitRF.Ui.Layout.PCells.SubstrateResolver.ResolveElectrical(
            tech, CircuitRF.Ui.Layout.PCells.PCellLayerSelection.Default);
        Assert.Null(failure);
        Assert.Equal(substrate!.HeightMeters, hDefault, 9);
    }

    [Fact]
    public void OverriddenSignalLayer_ChangesResolvedH_MatchingDirectSubstrateResolverCall()
    {
        var schematicDir = WriteWorkspaceWithTech(StarterTechnologies.MmicGaAs());
        var model = new SchematicEditModel { SchematicDirectory = schematicDir };
        model.Components.Add(MakeMlin(signalLayer: "Metal1"));

        var result = NetExtractor.Extract(model);
        var inst = result.TestBench.Instances.First(i => i.InstanceName == "ML1");
        double hOverridden = OverrideValue(inst, "H");

        var tech = StarterTechnologies.MmicGaAs();
        var selection = new CircuitRF.Ui.Layout.PCells.PCellLayerSelection("Metal1", null);
        var (substrate, failure, _) = CircuitRF.Ui.Layout.PCells.SubstrateResolver.ResolveElectrical(tech, selection);
        Assert.Null(failure);
        Assert.Equal(substrate!.HeightMeters, hOverridden, 9);

        // And it genuinely differs from the default (topmost = Metal2) resolution — gate 8's
        // "changes the resolved h ... in the expected direction."
        var defaultResult = CircuitRF.Ui.Layout.PCells.SubstrateResolver.ResolveElectrical(
            tech, CircuitRF.Ui.Layout.PCells.PCellLayerSelection.Default);
        Assert.NotEqual(defaultResult.Substrate!.HeightMeters, hOverridden);
    }

    [Fact]
    public void MissingRenamedSignalLayer_FallsBackAndReportsConflict_ComponentStillStamps()
    {
        var schematicDir = WriteWorkspaceWithTech(StarterTechnologies.Pcb2Layer());
        var model = new SchematicEditModel { SchematicDirectory = schematicDir };
        model.Components.Add(MakeMlin(signalLayer: "NoLongerExists"));

        var result = NetExtractor.Extract(model);
        var inst = result.TestBench.Instances.First(i => i.InstanceName == "ML1");

        // The component still gets a full set of resolved substrate overrides (falls back, does
        // not fail) — R-tec-9.
        Assert.Contains(inst.Overrides, o => o.Name == "H");
        Assert.Contains(result.Conflicts, c => c.Contains("NoLongerExists"));
    }
}
