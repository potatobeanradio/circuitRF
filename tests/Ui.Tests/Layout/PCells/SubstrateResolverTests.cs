using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout.PCells;

/// <summary>Gates 7-9: substrate resolution (R-pc-8/9/10).</summary>
public class SubstrateResolverTests
{
    [Fact]
    public void PcbStarter_ZeroConfig_ResolvesTopCopperOverFr4OverBottomCopper()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var (substrate, failure, _) = SubstrateResolver.ResolveElectrical(tech, PCellLayerSelection.Default);

        Assert.Null(failure);
        Assert.NotNull(substrate);
        Assert.Equal("Top Copper (1 oz)", substrate!.SignalConductorName);
        Assert.Equal("Bottom Copper (1 oz)", substrate.GroundConductorName);
        Assert.Equal(1.6e-3, substrate.HeightMeters, 6);
        Assert.Equal(4.4, substrate.RelativePermittivity, 6);
        Assert.Equal(0.02, substrate.LossTangent, 6);
        Assert.Equal(5.8e7, substrate.ConductivitySPerM, 1);
        Assert.False(substrate.IsStripline);
    }

    [Fact]
    public void SignalLayerKey_ZeroConfig_ResolvesToTopCopperDrawingLayer()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var key = SubstrateResolver.ResolveSignalLayerKey(tech, PCellLayerSelection.Default, out _);
        Assert.Equal(new LayerKey(1, 0), key); // Top Copper is (1,0) in the PCB starter
    }

    [Fact]
    public void ExplicitLayerOverride_IsHonoured()
    {
        // The MMIC starter's own genuinely-ambiguous 3-conductor case (Metal2/air/Metal1/GaAs/
        // Backside Metal, per StarterTechnologies.MmicGaAs's own doc comment): the zero-config
        // default picks Metal2 (topmost); overriding to Metal1 (the conventional MMIC RF routing
        // layer) is the documented escape hatch for exactly this ambiguity (R-pc-9).
        var tech = StarterTechnologies.MmicGaAs();
        var defaultResult = SubstrateResolver.ResolveElectrical(tech, PCellLayerSelection.Default);
        Assert.Equal("Metal2", defaultResult.Substrate!.SignalConductorName);

        var selection = new PCellLayerSelection("Metal1", "Backside Metal");
        var (substrate, failure, _) = SubstrateResolver.ResolveElectrical(tech, selection);

        Assert.Null(failure);
        Assert.Equal("Metal1", substrate!.SignalConductorName);
        Assert.Equal("Backside Metal", substrate.GroundConductorName);
    }

    [Fact]
    public void UnknownSignalLayerOverrideName_ReportsAndFallsBackToDefault_R_tec_9()
    {
        // brief-technology-editor-units-and-layers.md R-tec-9: a named override that no longer
        // exists (a rename, a technology swap) reports and falls back to the default — it must
        // NOT fail the component outright, which was this method's own OLD behavior.
        var tech = StarterTechnologies.Pcb2Layer();
        var selection = new PCellLayerSelection("NoSuchLayer", null);
        var (substrate, failure, warnings) = SubstrateResolver.ResolveElectrical(tech, selection);

        Assert.Null(failure);
        Assert.NotNull(substrate);
        Assert.Equal("Top Copper (1 oz)", substrate!.SignalConductorName); // falls back to topmost
        Assert.Contains(warnings, w => w.Contains("NoSuchLayer"));
    }

    [Fact]
    public void UnknownGroundLayerOverrideName_ReportsAndFallsBackToDefault_R_tec_9()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var selection = new PCellLayerSelection(null, "NoSuchGround");
        var (substrate, failure, warnings) = SubstrateResolver.ResolveElectrical(tech, selection);

        Assert.Null(failure);
        Assert.NotNull(substrate);
        Assert.Equal("Bottom Copper (1 oz)", substrate!.GroundConductorName); // falls back to inferred default
        Assert.Contains(warnings, w => w.Contains("NoSuchGround"));
    }

    [Fact]
    public void EmptyStringOverrides_AreTreatedAsNoOverride_NoWarning()
    {
        // R-tec-8: empty means "follow the technology" — this must be silent (no report), not
        // treated as a stale/missing named reference.
        var tech = StarterTechnologies.Pcb2Layer();
        var selection = new PCellLayerSelection("", "");
        var (substrate, failure, warnings) = SubstrateResolver.ResolveElectrical(tech, selection);

        Assert.Null(failure);
        Assert.NotNull(substrate);
        Assert.Equal("Top Copper (1 oz)", substrate!.SignalConductorName);
        Assert.Empty(warnings);
    }

    [Fact]
    public void NoTechnology_ElectricalResolutionFails_NamingWhatIsMissing()
    {
        var (substrate, failure, _) = SubstrateResolver.ResolveElectrical(null, PCellLayerSelection.Default);
        Assert.Null(substrate);
        Assert.NotNull(failure);
        Assert.Contains("no technology", failure!.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoTechnology_SignalLayerKey_StillResolves_ToFallback()
    {
        // §2 of the brief: geometry is still generatable even with no technology.
        var key = SubstrateResolver.ResolveSignalLayerKey(null, PCellLayerSelection.Default, out _);
        Assert.Equal(new LayerKey(1, 0), key);
    }

    [Fact]
    public void GroundBothAboveAndBelow_ReportsStriplineWarning()
    {
        // Build a 3-conductor technology: Ground / Dielectric / Signal / Dielectric / Ground.
        var tech = new Technology { Name = "Stripline Test" };
        tech.Stackup.Layers.Add(new StackupLayer { Kind = StackupKind.Conductor, Name = "TopGnd", IsGroundReference = true, SigmaSm = 5.8e7 });
        tech.Stackup.Layers.Add(new StackupLayer { Kind = StackupKind.Dielectric, Name = "D1", ThicknessDbu = 1_000_000, Epsr = 4.4 });
        tech.Stackup.Layers.Add(new StackupLayer { Kind = StackupKind.Conductor, Name = "Signal", SigmaSm = 5.8e7, ThicknessDbu = 35_000 });
        tech.Stackup.Layers.Add(new StackupLayer { Kind = StackupKind.Dielectric, Name = "D2", ThicknessDbu = 1_000_000, Epsr = 4.4 });
        tech.Stackup.Layers.Add(new StackupLayer { Kind = StackupKind.Conductor, Name = "BotGnd", IsGroundReference = true, SigmaSm = 5.8e7 });

        var selection = new PCellLayerSelection("Signal", "BotGnd");
        var (substrate, failure, warnings) = SubstrateResolver.ResolveElectrical(tech, selection);

        Assert.Null(failure);
        Assert.NotNull(substrate);
        Assert.True(substrate!.IsStripline);
        Assert.Contains(warnings, w => w.Contains("stripline", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NoGroundReferenceMarked_ThreeConductors_StaysAmbiguous_ElectricalResolutionFails()
    {
        // 3+ conductors, NOTHING marked: must stay ambiguous (mirrors the MMIC starter's own
        // Metal2/Metal1/Backside Metal shape) — the fallback below only fires for the unambiguous
        // 2-conductor case.
        var tech = new Technology { Name = "No Ground, 3 Conductors" };
        tech.Stackup.Layers.Add(new StackupLayer { Kind = StackupKind.Conductor, Name = "Top", SigmaSm = 5.8e7 });
        tech.Stackup.Layers.Add(new StackupLayer { Kind = StackupKind.Dielectric, Name = "D1", ThicknessDbu = 1_000_000, Epsr = 4.4 });
        tech.Stackup.Layers.Add(new StackupLayer { Kind = StackupKind.Conductor, Name = "Middle", SigmaSm = 5.8e7 });
        tech.Stackup.Layers.Add(new StackupLayer { Kind = StackupKind.Dielectric, Name = "D2", ThicknessDbu = 1_000_000, Epsr = 4.4 });
        tech.Stackup.Layers.Add(new StackupLayer { Kind = StackupKind.Conductor, Name = "Bottom", SigmaSm = 5.8e7 });

        var (substrate, failure, _) = SubstrateResolver.ResolveElectrical(tech, PCellLayerSelection.Default);
        Assert.Null(substrate);
        Assert.NotNull(failure);
        Assert.Contains("ground-designated", failure!.Reason);
    }

    [Fact]
    public void NoGroundReferenceMarked_UnambiguousTwoConductorBoard_FallsBackToBottomConductor()
    {
        // Reproduces the real owner-reported bug: a .ctech saved before StackupLayer.
        // IsGroundReference existed has NO conductor marked at all. On a plain 2-conductor board
        // (Stackup.Bottom == Ground), the bottom-most conductor is the only sensible reading and
        // resolution must succeed with zero configuration, exactly like a freshly-created workspace.
        var tech = new Technology { Name = "Legacy PCB (no marker)" };
        tech.Stackup.Bottom = BoundaryCondition.Ground;
        tech.Stackup.Layers.Add(new StackupLayer { Kind = StackupKind.Conductor, Name = "Top Copper", SigmaSm = 5.8e7, ThicknessDbu = 35_000 });
        tech.Stackup.Layers.Add(new StackupLayer { Kind = StackupKind.Dielectric, Name = "FR-4", ThicknessDbu = 1_600_000, Epsr = 4.4, TanD = 0.02 });
        tech.Stackup.Layers.Add(new StackupLayer { Kind = StackupKind.Conductor, Name = "Bottom Copper", SigmaSm = 5.8e7, ThicknessDbu = 35_000 });

        var (substrate, failure, _) = SubstrateResolver.ResolveElectrical(tech, PCellLayerSelection.Default);

        Assert.Null(failure);
        Assert.NotNull(substrate);
        Assert.Equal("Top Copper", substrate!.SignalConductorName);
        Assert.Equal("Bottom Copper", substrate.GroundConductorName);
        Assert.Equal(1.6e-3, substrate.HeightMeters, 6);
    }

    [Fact]
    public void NoGroundReferenceMarked_OpenBottomBoundary_StillFails_NoFallback()
    {
        // The fallback is keyed on Stackup.Bottom == Ground; an Open bottom (e.g. a bare
        // 2-conductor stripline-ish stack with no ground plane at all) must NOT silently guess.
        var tech = new Technology { Name = "No Ground, Open Bottom" };
        tech.Stackup.Bottom = BoundaryCondition.Open;
        tech.Stackup.Layers.Add(new StackupLayer { Kind = StackupKind.Conductor, Name = "Top", SigmaSm = 5.8e7 });
        tech.Stackup.Layers.Add(new StackupLayer { Kind = StackupKind.Dielectric, Name = "D1", ThicknessDbu = 1_000_000, Epsr = 4.4 });
        tech.Stackup.Layers.Add(new StackupLayer { Kind = StackupKind.Conductor, Name = "Bottom", SigmaSm = 5.8e7 });

        var (substrate, failure, _) = SubstrateResolver.ResolveElectrical(tech, PCellLayerSelection.Default);
        Assert.Null(substrate);
        Assert.NotNull(failure);
    }
}
