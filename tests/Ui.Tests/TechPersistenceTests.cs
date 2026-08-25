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
        // brief-via-primitive-and-stackup.md R-via-4: gains one Via entry (Plated Through-Hole,
        // Top Copper -> Bottom Copper) alongside the original Top Copper / FR-4 / Bottom Copper trio.
        Assert.Equal(4, tech.Stackup.Layers.Count);
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
        // brief-via-primitive-and-stackup.md §3.1/R-via-4: Metal2 / Air / Metal1 / GaAs / Backside Metal
        // (5 physical layers, replacing the old single "Plated Gold" conductor that wrongly merged
        // Metal1+Metal2) plus two Via entries (Backside Via, Metal1-Metal2 Post) = 7.
        Assert.Equal(7, tech.Stackup.Layers.Count);
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
        // purely additive and does not require a FormatVersion bump. Built here rather than taken from
        // a starter technology: the shipped PCB starters DO declare one now (their PcbLayerName aliases
        // are what land an imported board's copper on their own copper), and this gate is about the
        // field's absence, not about which technology happens to leave it unset.
        var tech = new Technology
        {
            Name = "T",
            Layers = [new LayerDef { Key = new LayerKey(1, 0), Name = "Metal1" }],
        };
        var json = TechPersistence.Serialize(tech);
        Assert.DoesNotContain("\"Interchange\"", json); // no layer has one set — omitted by WhenWritingNull

        var restored = TechPersistence.Deserialize(json);
        Assert.All(restored.Layers, l => Assert.Null(l.Interchange));
    }

    [Fact]
    public void PcbStarter_DeclaresBoardLayerAliases_AndTheyRoundTrip()
    {
        // R-L4d-4: these aliases are the entire reason Import Board lands a board's F.Cu on this
        // technology's own Top Copper instead of minting a synthetic layer beside it. A missing or
        // misspelt one fails SILENTLY — the import still succeeds, it just quietly doubles the layer
        // table and puts the copper somewhere nothing else in the technology refers to.
        var restored = TechPersistence.Deserialize(TechPersistence.Serialize(StarterTechnologies.Pcb2Layer()));

        string? AliasOf(string layerName) =>
            restored.Layers.First(l => l.Name == layerName).Interchange?.PcbLayerName;

        Assert.Equal("F.Cu",      AliasOf("Top Copper"));
        Assert.Equal("B.Cu",      AliasOf("Bottom Copper"));
        Assert.Equal("F.Mask",    AliasOf("Soldermask Top"));
        Assert.Equal("B.Mask",    AliasOf("Soldermask Bottom"));
        Assert.Equal("F.SilkS",   AliasOf("Silk Top"));
        Assert.Equal("B.SilkS",   AliasOf("Silk Bottom"));
        Assert.Equal("Edge.Cuts", AliasOf("Outline"));

        // Drill deliberately has none — the reader's own synthetic drill layer is literally called
        // "Drill", so it already matches this one by name with nothing declared.
        Assert.Null(AliasOf("Drill"));

        // Declaring a board alias must not disturb the other interchange fields, which stay unstated.
        var topCopper = restored.Layers.First(l => l.Name == "Top Copper").Interchange!;
        Assert.Null(topCopper.GdsiiLayer);
        Assert.Null(topCopper.DxfLayerName);
        Assert.Null(topCopper.GerberSuffix);
    }

    [Fact]
    public void ShippedPcbTechnologyResources_DeclareTheSameBoardLayerAliasesAsTheCodeStarter()
    {
        // Two copies of one table: the .ctech files under resources/technologies are what a NEW
        // workspace actually copies in, while StarterTechnologies is the in-code fallback. They drifting
        // apart is invisible until someone imports a board into a workspace made the other way.
        var expected = StarterTechnologies.Pcb2Layer().Layers
            .Where(l => l.Interchange?.PcbLayerName is not null)
            .ToDictionary(l => l.Name, l => l.Interchange!.PcbLayerName!);

        var dir = Path.Combine(RepoRoot(), "src", "Ui", "resources", "technologies");
        var files = Directory.GetFiles(dir, "pcb-*.ctech");
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var tech = TechPersistence.Deserialize(File.ReadAllText(file));
            foreach (var (layerName, alias) in expected)
            {
                var layer = tech.Layers.FirstOrDefault(l => l.Name == layerName);
                Assert.True(layer is not null, $"{Path.GetFileName(file)} has no layer \"{layerName}\"");
                Assert.Equal(alias, layer!.Interchange?.PcbLayerName);
            }
        }
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitrf.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repo root not found");
    }

    // ── R-via-2/R-via-3 (docs/sonnet-briefs/brief-via-primitive-and-stackup.md): fill model, wall
    // thickness, and span — additive, nullable, no FormatVersion bump ────────────────────────────────

    [Fact]
    public void ViaStackupFields_RoundTrip_ByteIdentical()
    {
        var tech = new Technology
        {
            Name = "T",
            Stackup = new Stackup
            {
                Layers =
                [
                    new StackupLayer { Kind = StackupKind.Conductor, Name = "Top", ThicknessDbu = 1, SigmaSm = 1 },
                    new StackupLayer { Kind = StackupKind.Conductor, Name = "Bottom", ThicknessDbu = 1, SigmaSm = 1 },
                    new StackupLayer
                    {
                        Kind = StackupKind.Via, Name = "PTH",
                        Fill = ViaFillKind.Plated, WallThicknessDbu = 25_000,
                        SpanFromLayer = "Top", SpanToLayer = "Bottom",
                    },
                ],
            },
        };
        var json1 = TechPersistence.Serialize(tech);
        var restored = TechPersistence.Deserialize(json1);
        var json2 = TechPersistence.Serialize(restored);

        Assert.Equal(json1, json2);
        var via = restored.Stackup.Layers.Single(l => l.Kind == StackupKind.Via);
        Assert.Equal(ViaFillKind.Plated, via.Fill);
        Assert.Equal(25_000, via.WallThicknessDbu);
        Assert.Equal("Top", via.SpanFromLayer);
        Assert.Equal("Bottom", via.SpanToLayer);
    }

    [Fact]
    public void HandStrippedCtech_MissingViaFields_StillLoads()
    {
        // A pre-via-brief .ctech never had Fill/WallThicknessDbu/SpanFromLayer/SpanToLayer on a
        // StackupLayer at all — confirms these are purely additive, no FormatVersion bump.
        var tech = new Technology
        {
            Stackup = new Stackup { Layers = [new StackupLayer { Kind = StackupKind.Conductor, Name = "M", ThicknessDbu = 1, SigmaSm = 1 }] },
        };
        var json = TechPersistence.Serialize(tech);
        Assert.DoesNotContain("\"Fill\"", json);
        Assert.DoesNotContain("\"SpanFromLayer\"", json);

        var restored = TechPersistence.Deserialize(json);
        Assert.Null(restored.Stackup.Layers[0].Fill);
        Assert.Null(restored.Stackup.Layers[0].SpanFromLayer);
    }

    [Fact]
    public void Validate_ViaSpanNamesUnknownConductor_Reported()
    {
        var tech = new Technology
        {
            Stackup = new Stackup
            {
                Layers = [new StackupLayer { Kind = StackupKind.Via, Name = "V", SpanFromLayer = "DoesNotExist", SpanToLayer = "AlsoMissing" }],
            },
        };
        Assert.NotEmpty(TechValidation.Validate(tech));
    }

    [Fact]
    public void Validate_ViaPlatedWithNoWallThickness_Reported()
    {
        var tech = new Technology
        {
            Stackup = new Stackup
            {
                Layers =
                [
                    new StackupLayer { Kind = StackupKind.Conductor, Name = "A", ThicknessDbu = 1, SigmaSm = 1 },
                    new StackupLayer { Kind = StackupKind.Conductor, Name = "B", ThicknessDbu = 1, SigmaSm = 1 },
                    new StackupLayer { Kind = StackupKind.Via, Name = "V", Fill = ViaFillKind.Plated, SpanFromLayer = "A", SpanToLayer = "B" },
                ],
            },
        };
        Assert.Contains(TechValidation.Validate(tech), s => s.Contains("Plated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_ViaEntry_NoIndependentThicknessRequired()
    {
        // R-via-2/R-via-3: a Via entry is a vertical connector, not a horizontal layer — it must NOT
        // trip the same "non-positive thickness" rule Dielectric/Conductor entries get (ThicknessDbu
        // is left at its zero default here, deliberately).
        var tech = new Technology
        {
            Stackup = new Stackup
            {
                Layers =
                [
                    new StackupLayer { Kind = StackupKind.Conductor, Name = "A", ThicknessDbu = 1, SigmaSm = 1 },
                    new StackupLayer { Kind = StackupKind.Conductor, Name = "B", ThicknessDbu = 1, SigmaSm = 1, IsGroundReference = true },
                    new StackupLayer { Kind = StackupKind.Via, Name = "V", Fill = ViaFillKind.Solid, SpanFromLayer = "A", SpanToLayer = "B" },
                ],
            },
        };
        Assert.Empty(TechValidation.Validate(tech));
    }

    // ── Gate 3: starter stackups — PCB one via entry, MMIC two, over Metal2/Air/Metal1/GaAs/ground ──

    [Fact]
    public void Pcb2Layer_ExposesOneViaEntry_PlatedSpanningBothCopperLayers()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var via = Assert.Single(tech.Stackup.Layers, l => l.Kind == StackupKind.Via);
        Assert.Equal(ViaFillKind.Plated, via.Fill);
        Assert.True(via.WallThicknessDbu > 0);
        Assert.Equal("Top Copper (1 oz)", via.SpanFromLayer);
        Assert.Equal("Bottom Copper (1 oz)", via.SpanToLayer);
    }

    [Fact]
    public void MmicGaAs_ExposesTwoViaEntries_BacksideViaAndMetal1Metal2Post()
    {
        var tech = StarterTechnologies.MmicGaAs();
        var vias = tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Via).ToList();
        Assert.Equal(2, vias.Count);
        Assert.Contains(vias, v => v.SpanFromLayer == "Metal1" && v.SpanToLayer == "Backside Metal" && v.Fill == ViaFillKind.Plated);
        Assert.Contains(vias, v => v.SpanFromLayer == "Metal1" && v.SpanToLayer == "Metal2");
    }

    [Fact]
    public void MmicGaAs_AirLayer_HasEpsrOne()
    {
        var tech = StarterTechnologies.MmicGaAs();
        var air = Assert.Single(tech.Stackup.Layers, l => l.Kind == StackupKind.Dielectric && l.Name == "Air");
        Assert.Equal(1.0, air.Epsr);
    }

    [Fact]
    public void MmicGaAs_StackOrder_Metal2AirMetal1GaAsBacksideMetal()
    {
        var tech = StarterTechnologies.MmicGaAs();
        var physical = tech.Stackup.Layers.Where(l => l.Kind != StackupKind.Via).Select(l => l.Name).ToList();
        Assert.Equal(["Metal2", "Air", "Metal1", "GaAs", "Backside Metal"], physical);
    }
}
