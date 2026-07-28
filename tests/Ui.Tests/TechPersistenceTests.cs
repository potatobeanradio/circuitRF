using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

public class TechPersistenceTests
{
    // ── Gate 5: .ctech round-trips byte-identically for both starter techs, Validate empty ───

    [Fact]
    public void Pcb2Layer_RoundTrip_ByteIdentical()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var json1 = TechPersistence.Serialize(tech);
        var restored = TechPersistence.Deserialize(json1);
        var json2 = TechPersistence.Serialize(restored);

        Assert.Equal(json1, json2);
    }

    [Fact]
    public void Pcb2Layer_Validates_NoProblems()
        => Assert.Empty(TechValidation.Validate(StarterTechnologies.Pcb2Layer()));

    [Fact]
    public void MmicGaAs_RoundTrip_ByteIdentical()
    {
        var tech = StarterTechnologies.MmicGaAs();
        var json1 = TechPersistence.Serialize(tech);
        var restored = TechPersistence.Deserialize(json1);
        var json2 = TechPersistence.Serialize(restored);

        Assert.Equal(json1, json2);
    }

    [Fact]
    public void MmicGaAs_Validates_NoProblems()
        => Assert.Empty(TechValidation.Validate(StarterTechnologies.MmicGaAs()));

    // ── §2.4 table spot checks ─────────────────────────────────────────────────

    [Fact]
    public void Pcb2Layer_MatchesTableDefaults()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        Assert.Equal(LayoutUnit.Mil, tech.DefaultDisplayUnit);
        Assert.Equal(LayoutUnits.ToDbu(1m, LayoutUnit.Mil, LayoutUnits.DefaultDbuPerMicron), tech.DefaultSnapDbu);
        Assert.Equal(8, tech.Layers.Count);
        Assert.Equal(3, tech.Stackup.Layers.Count);
        Assert.Equal(BoundaryCondition.Ground, tech.Stackup.Bottom);
        Assert.Equal(4, tech.DrcRules.Count);
    }

    [Fact]
    public void MmicGaAs_MatchesTableDefaults()
    {
        var tech = StarterTechnologies.MmicGaAs();
        Assert.Equal(LayoutUnit.Um, tech.DefaultDisplayUnit);
        Assert.Equal(LayoutUnits.ToDbu(5m, LayoutUnit.Nm, LayoutUnits.DefaultDbuPerMicron), tech.DefaultSnapDbu);
        Assert.Equal(8, tech.Layers.Count);
        Assert.Equal(2, tech.Stackup.Layers.Count);
        Assert.Equal(BoundaryCondition.Ground, tech.Stackup.Bottom);
        Assert.Equal(4, tech.DrcRules.Count);
    }

    // ── Gate 6: format_version reject-on-mismatch ─────────────────────────────

    [Fact]
    public void Ctech_NewerFormatVersion_ThrowsInvalidDataException()
    {
        var json = TechPersistence.Serialize(StarterTechnologies.Pcb2Layer());
        var broken = json.Replace("\"FormatVersion\": 1", "\"FormatVersion\": 999");

        Assert.Throws<InvalidDataException>(() => TechPersistence.Deserialize(broken));
    }

    // ── TechValidation catches real problems ──────────────────────────────────

    [Fact]
    public void Validate_DuplicateLayerKey_Reported()
    {
        var tech = new Technology
        {
            Layers =
            [
                new LayerDef { Key = new LayerKey(1, 0), Name = "A" },
                new LayerDef { Key = new LayerKey(1, 0), Name = "B" },
            ],
        };
        Assert.Contains(TechValidation.Validate(tech), s => s.Contains("Duplicate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_UnknownDrawingLayerRef_Reported()
    {
        var tech = new Technology
        {
            Layers = [new LayerDef { Key = new LayerKey(1, 0), Name = "A" }],
            Stackup = new Stackup
            {
                Layers = [new StackupLayer { Kind = StackupKind.Conductor, Name = "M", SigmaSm = 1, ThicknessDbu = 1, DrawingLayers = [new LayerKey(9, 0)] }],
            },
        };
        Assert.NotEmpty(TechValidation.Validate(tech));
    }

    [Fact]
    public void Validate_ConductorWithZeroSigma_Reported()
    {
        var tech = new Technology
        {
            Stackup = new Stackup { Layers = [new StackupLayer { Kind = StackupKind.Conductor, Name = "M", SigmaSm = 0, ThicknessDbu = 1 }] },
        };
        Assert.NotEmpty(TechValidation.Validate(tech));
    }

    [Fact]
    public void Validate_DielectricWithSubUnityEpsr_Reported()
    {
        var tech = new Technology
        {
            Stackup = new Stackup { Layers = [new StackupLayer { Kind = StackupKind.Dielectric, Name = "D", Epsr = 0.5, ThicknessDbu = 1 }] },
        };
        Assert.NotEmpty(TechValidation.Validate(tech));
    }

    [Fact]
    public void Validate_NonPositiveThickness_Reported()
    {
        var tech = new Technology
        {
            Stackup = new Stackup { Layers = [new StackupLayer { Kind = StackupKind.Conductor, Name = "M", SigmaSm = 1, ThicknessDbu = 0 }] },
        };
        Assert.NotEmpty(TechValidation.Validate(tech));
    }

    [Fact]
    public void Validate_DrcRuleOnUnknownLayer_Reported()
    {
        var tech = new Technology
        {
            DrcRules = [new DrcRule { Name = "X", Layer = new LayerKey(9, 0), ValueDbu = 1 }],
        };
        Assert.NotEmpty(TechValidation.Validate(tech));
    }

    [Fact]
    public void Validate_NeverThrows_OnEmptyTechnology()
    {
        var ex = Record.Exception(() => TechValidation.Validate(new Technology()));
        Assert.Null(ex);
    }

    // ── R-L4a-1: interchange mappings — additive, nullable, no FormatVersion bump ──────────────

    [Fact]
    public void InterchangeMapping_RoundTrips_ByteIdentical()
    {
        var tech = new Technology
        {
            Name = "T",
            Layers =
            [
                new LayerDef
                {
                    Key = new LayerKey(1, 0),
                    Name = "Metal1",
                    Interchange = new InterchangeMapping(11, 2, "METAL1", "GTL", "Copper,L1,Top"),
                },
            ],
        };
        var json1 = TechPersistence.Serialize(tech);
        var restored = TechPersistence.Deserialize(json1);
        var json2 = TechPersistence.Serialize(restored);

        Assert.Equal(json1, json2);
        Assert.Equal(11, restored.Layers[0].Interchange!.GdsiiLayer);
        Assert.Equal(2, restored.Layers[0].Interchange!.GdsiiDatatype);
        Assert.Equal("METAL1", restored.Layers[0].Interchange!.DxfLayerName);
        Assert.Equal("GTL", restored.Layers[0].Interchange!.GerberSuffix);
        Assert.Equal("Copper,L1,Top", restored.Layers[0].Interchange!.GerberFileFunction);
    }

    [Fact]
    public void HandStrippedCtech_MissingInterchangeField_StillLoads()
    {
        // A pre-L4a .ctech never had "Interchange" on a LayerDef at all — confirms the field is
        // purely additive and does not require a FormatVersion bump.
        var tech = StarterTechnologies.Pcb2Layer();
        var json = TechPersistence.Serialize(tech);
        Assert.DoesNotContain("\"Interchange\"", json); // no layer has one set — omitted by WhenWritingNull

        var restored = TechPersistence.Deserialize(json);
        Assert.All(restored.Layers, l => Assert.Null(l.Interchange));
    }
}
