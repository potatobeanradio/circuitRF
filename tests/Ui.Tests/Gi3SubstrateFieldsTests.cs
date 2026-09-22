// Gate for docs/sonnet-briefs/brief-gi3-substrate-fields.md.
//
// Every other field on the Stackup tab's detail panel carried guidance; the two that a board import
// leaves blank — σ and a plated via's Wall — carried none, had no preset, and after a Gerber import
// had no value either. GI3 fills all three gaps, puts the conductivity constants in ONE place, and
// adds the three structural checks that ride along on the same tab: does the stack add up, are two
// entries sharing a name, and is a via row pretending to be in the z order.
//
// THE LOAD-BEARING ASSERTIONS HERE ARE THE ONES ABOUT ORDER AND ABOUT NOT CORRECTING.
// R-gi3-9's grouping is a PRESENTATION change: Stackup.Layers' own Conductor/Dielectric order must be
// byte-identical before and after, because that order IS z and a reversed stack simulates cleanly and
// answers a different question (R-L4d-5, R-L4g-10). And R-gi3-7's board thickness is a second opinion,
// never a correction: the two numbers disagreeing is information, and which one is wrong is not
// something the application knows.
//
// COUNTERS AND CONTENT ONLY. No wall-clock assertion anywhere in this file.

using System.Text.RegularExpressions;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Tests;

public class Gi3SubstrateFieldsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gi3-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────────────────────

    private static Technology TwoLayerBoard()
    {
        var tech = new Technology { Name = "T", DefaultDisplayUnit = LayoutUnit.Um };
        tech.Layers.Add(new LayerDef { Key = new LayerKey(1, 0), Name = "Top",    Color = new Rgba(1, 2, 3, 255) });
        tech.Layers.Add(new LayerDef { Key = new LayerKey(2, 0), Name = "Bottom", Color = new Rgba(1, 2, 3, 255) });
        tech.Layers.Add(new LayerDef { Key = new LayerKey(3, 0), Name = "Drill",  Color = new Rgba(1, 2, 3, 255) });
        tech.Stackup.Layers.AddRange(
        [
            new StackupLayer { Kind = StackupKind.Conductor, Name = "Top Copper",
                               ThicknessDbu = 35_000, SigmaSm = 5.8e7, DrawingLayers = [new LayerKey(1, 0)] },
            new StackupLayer { Kind = StackupKind.Dielectric, Name = "Core",
                               ThicknessDbu = 1_500_000, Epsr = 4.4, TanD = 0.02 },
            new StackupLayer { Kind = StackupKind.Conductor, Name = "Bottom Copper",
                               ThicknessDbu = 35_000, SigmaSm = 5.8e7, DrawingLayers = [new LayerKey(2, 0)],
                               IsGroundReference = true },
            new StackupLayer { Kind = StackupKind.Via, Name = "PTH", DrawingLayers = [new LayerKey(3, 0)],
                               Fill = ViaFillKind.Plated, WallThicknessDbu = 25_000,
                               SpanFromLayer = "Top Copper", SpanToLayer = "Bottom Copper" },
        ]);
        return tech;
    }

    private TechEditorViewModel Editor(Technology tech) =>
        new(Path.Combine(_root, "t.ctech"), tech);

    private static StackupLayerRowViewModel Row(TechEditorViewModel vm, string name) =>
        vm.StackupLayers.Single(r => r.Layer.Name == name);

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir;
    }

    /// <summary>Line and block comments removed, so a source scan cannot be satisfied by prose — the
    /// established pattern (AuthoringCliVerbTests). String literals are left alone; none of the
    /// constants scanned for below appears in one.</summary>
    private static string StripComments(string code)
        => Regex.Replace(Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 1 — the preset writes THROUGH the field, and the field stays the source of truth
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("Copper",    5.80e7)]
    [InlineData("Silver",    6.30e7)]
    [InlineData("Gold",      4.10e7)]
    [InlineData("Aluminium", 3.77e7)]
    [InlineData("Nickel",    1.43e7)]
    public void ChoosingAMetal_SetsTheConductivityTheTableStates(string metal, double expected)
    {
        var vm = Editor(TwoLayerBoard());
        Row(vm, "Top Copper").SelectedConductorMaterial = metal;

        // Read off the MODEL, not the row: the preset is only worth anything if it reaches the file.
        var layer = vm.Working.Stackup.Layers.Single(l => l.Name == "Top Copper");
        Assert.Equal(expected, layer.SigmaSm, 6);
        Assert.Equal(expected, ConductorMaterials.ByName(metal)!.SigmaSm, 6);

        // ...and the row that gets rebuilt after the edit reads it back as that same metal.
        Assert.Equal(metal, Row(vm, "Top Copper").SelectedConductorMaterial);
    }

    /// <summary>R-gi3-1: the preset is a shortcut, never a constraint. A typed value that matches no
    /// metal survives exactly as typed and displays as Custom.</summary>
    [Fact]
    public void ATypedCustomValue_Survives_AndDisplaysAsCustom()
    {
        var vm = Editor(TwoLayerBoard());
        var row = Row(vm, "Top Copper");

        row.StagedSigmaSm = "3.9e7";
        row.CommitSigmaSm();

        Assert.Equal(3.9e7, vm.Working.Stackup.Layers.Single(l => l.Name == "Top Copper").SigmaSm, 6);
        Assert.Equal(ConductorMaterials.Custom, Row(vm, "Top Copper").SelectedConductorMaterial);
        Assert.Contains(ConductorMaterials.Custom, StackupLayerRowViewModel.ConductorMaterialChoices);
    }

    /// <summary>Selecting the "Custom" row is a readout, not a value — it must not write anything.</summary>
    [Fact]
    public void SelectingCustom_ChangesNothing()
    {
        var vm = Editor(TwoLayerBoard());
        double before = vm.Working.Stackup.Layers.Single(l => l.Name == "Top Copper").SigmaSm;

        Row(vm, "Top Copper").SelectedConductorMaterial = ConductorMaterials.Custom;

        Assert.Equal(before, vm.Working.Stackup.Layers.Single(l => l.Name == "Top Copper").SigmaSm, 6);
    }

    /// <summary>The round trip through the file format is exact — the preset writes a number, and a
    /// number is all the format has ever carried. No material NAME is stored anywhere.</summary>
    [Fact]
    public void TheChosenConductivityRoundTripsThroughCtech_Exactly()
    {
        var vm = Editor(TwoLayerBoard());
        Row(vm, "Top Copper").SelectedConductorMaterial = "Gold";

        string json = TechPersistence.Serialize(vm.Working);
        var back = TechPersistence.Deserialize(json);

        Assert.Equal(ConductorMaterials.Gold.SigmaSm,
                     back.Stackup.Layers.Single(l => l.Name == "Top Copper").SigmaSm, 10);
        Assert.DoesNotContain("Gold", json, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 2 — ONE table
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// R-gi3-2. A sixth copy is the failure mode the table exists to prevent, so the scan is over the
    /// files that HELD the copies: StarterTechnologies (two of copper, four of gold) and the two
    /// interchange stackup mappings (the named copper constant). Comment-stripped, so a note that
    /// quotes a number cannot satisfy or break it.
    /// </summary>
    [Theory]
    [InlineData("src/Ui/Layout/StarterTechnologies.cs")]
    [InlineData("src/Design/Layout/Interchange/PcbStackupMapping.cs")]
    [InlineData("src/Design/Layout/Interchange/GerberStackupMapping.cs")]
    [InlineData("src/Design/Layout/Em/CrossSectionExtractor.cs")]
    public void NoSecondCopyOfTheConductivityConstantsRemains(string relativePath)
    {
        string code = StripComments(File.ReadAllText(Path.Combine(RepoRoot(), relativePath)));

        foreach (string literal in new[] { "5.8e7", "5.80e7", "6.3e7", "6.30e7",
                                           "4.1e7", "4.10e7", "3.77e7", "1.43e7" })
            Assert.DoesNotContain(literal, code, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The table is the ONE definition, and the long-standing named default now reads from
    /// it rather than repeating it.</summary>
    [Fact]
    public void TheInterchangeDefaultIsTheTablesOwnCopper()
    {
        Assert.Equal(ConductorMaterials.Copper.SigmaSm, PcbStackupMapping.DefaultCopperConductivitySm, 10);
    }

    /// <summary>R-gi3-2's other half: elements only. A laminate/dielectric table is refused
    /// permanently, and this is the tripwire on it.</summary>
    [Fact]
    public void ThePresetListIsElementsOnly()
    {
        Assert.Equal(["Silver", "Copper", "Gold", "Aluminium", "Nickel"],
                     ConductorMaterials.All.Select(m => m.Name));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 3 — a Gerber import's via carries the shipped default, and says it is one
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void AnImportedPlatedVia_Carries25um_ReadFromTheSameConstantTheShippedTechnologiesUse()
    {
        var result = ImportTwoLayerWithDrill("wall_default");
        var tech = TechPersistence.LoadFromFile(result.TechPath!);

        var via = Assert.Single(tech.Stackup.Layers, l => l.Kind == StackupKind.Via);
        Assert.Equal(ViaDefaults.PlatedWallThicknessDbu(1000), via.WallThicknessDbu);
        Assert.Equal(25_000, via.WallThicknessDbu);           // 25 µm at the default resolution

        // The value equals the shipped technologies' own, and every one of them agrees.
        foreach (var shipped in ShippedTechnologies.All.Select(ShippedTechnologies.Load))
            foreach (var l in shipped.Stackup.Layers.Where(l => l.Kind == StackupKind.Via &&
                                                                l.Fill == ViaFillKind.Plated &&
                                                                l.Name.Contains("Through-Hole", StringComparison.Ordinal)))
                Assert.Equal(via.WallThicknessDbu, l.WallThicknessDbu);
    }

    /// <summary>R-gi3-4/R-L4d-7: defaulted AND named as a default, once for the whole import.</summary>
    [Fact]
    public void TheImportNamesTheWallThicknessAsADefault_Once()
    {
        var result = ImportTwoLayerWithDrill("wall_message");

        var said = Assert.Single(result.Messages,
            m => m.Contains("Plated via wall thickness is defaulted", StringComparison.Ordinal));
        Assert.Contains("25 µm", said, StringComparison.Ordinal);
        Assert.Contains("named here as a default", said, StringComparison.Ordinal);
        Assert.Contains("PLATING thickness", said, StringComparison.Ordinal);
    }

    /// <summary>A NON-plated hole is not metal, so it gets no wall thickness — the same rule that
    /// leaves its Fill unstated (GI1 R-gi1-2).</summary>
    [Fact]
    public void ANonPlatedDrillLayer_GetsNoWallThickness_AndIsNotCounted()
    {
        var dir = Folder("nonplated");
        Write(dir, "board.gtl", Artwork("Copper,L1,Top,Signal"));
        Write(dir, "board.gbl", Artwork("Copper,L2,Bot,Signal", xMm: 2.0));
        Write(dir, "board-NPTH.drl", Drill());

        var result = Import(dir, "nonplated_import");
        var tech = TechPersistence.LoadFromFile(result.TechPath!);

        var via = Assert.Single(tech.Stackup.Layers, l => l.Kind == StackupKind.Via);
        Assert.False(via.Plated);
        Assert.Null(via.WallThicknessDbu);
        Assert.DoesNotContain(result.Messages,
            m => m.Contains("Plated via wall thickness is defaulted", StringComparison.Ordinal));
    }

    /// <summary>The whole point of R-gi3-4: the import's own technology used to fail the validator on
    /// a field with a known answer. It no longer does.</summary>
    [Fact]
    public void AnImportedTechnology_NoLongerReportsAViaWithNoWallThickness()
    {
        var result = ImportTwoLayerWithDrill("wall_validation");
        var tech = TechPersistence.LoadFromFile(result.TechPath!);

        Assert.DoesNotContain(TechValidation.Validate(tech),
            m => m.Contains("Plated with no wall thickness", StringComparison.Ordinal));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 4 — both fields have a tooltip
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Thin, and deliberately kept: this phase exists BECAUSE these two were bare, and the next XAML
    /// edit in that pane can silently drop one. The scan reads each field's own element and requires a
    /// non-empty literal tip on it.
    /// </summary>
    [Theory]
    [InlineData("Sigma",         "skin depth")]
    [InlineData("WallThickness", "skin depths")]
    public void TheFieldThisPhaseExistsFor_HasANonEmptyTooltip(string tag, string mustSay)
    {
        string xaml = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Ui", "Views", "Layout", "TechEditorView.axaml"));

        var element = Regex.Match(xaml, $@"<TextBox[^>]*Tag=""{tag}""[^>]*>", RegexOptions.Singleline);
        Assert.True(element.Success, $"No TextBox tagged {tag} in TechEditorView.axaml.");

        var tip = Regex.Match(element.Value, @"ToolTip\.Tip=""([^""]+)""");
        Assert.True(tip.Success, $"The {tag} field has no ToolTip.Tip.");
        Assert.Contains(mustSay, tip.Groups[1].Value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>R-gi3-3's specific requirement: say what a blank conductivity actually costs, because
    /// that is the error someone meets and the validator already reports it.</summary>
    [Fact]
    public void TheConductivityTooltip_SaysANonPositiveValueIsRefused()
    {
        string xaml = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Ui", "Views", "Layout", "TechEditorView.axaml"));
        var element = Regex.Match(xaml, @"<TextBox[^>]*Tag=""Sigma""[^>]*>", RegexOptions.Singleline);
        var tip = Regex.Match(element.Value, @"ToolTip\.Tip=""([^""]+)""").Groups[1].Value;

        Assert.Contains("REFUSED", tip, StringComparison.Ordinal);

        // ...and it is a real refusal, not a claim about one.
        var tech = TwoLayerBoard();
        tech.Stackup.Layers[0].SigmaSm = 0;
        Assert.Contains(TechValidation.Validate(tech),
            m => m.Contains("non-positive conductivity", StringComparison.Ordinal));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 5 — does the stackup add up?
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void TheTotalIsTheSumOfTheSubstrateEntries_AndAViaDoesNotChangeIt()
    {
        var tech = TwoLayerBoard();
        var vm = Editor(tech);

        Assert.Equal(35_000 + 1_500_000 + 35_000, tech.Stackup.TotalThicknessDbu);
        Assert.Equal("1570 µm", vm.StackTotalText);

        // R-gi3-6: a via has no z band of its own, so adding one — even one carrying a thickness a
        // hand-edited file put there — cannot move the total.
        long before = tech.Stackup.TotalThicknessDbu;
        tech.Stackup.Layers.Add(new StackupLayer
        {
            Kind = StackupKind.Via, Name = "Blind Via", ThicknessDbu = 999_999,
        });
        Assert.Equal(before, tech.Stackup.TotalThicknessDbu);
    }

    [Fact]
    public void EditingAThickness_UpdatesTheTotal()
    {
        var vm = Editor(TwoLayerBoard());
        Assert.Equal("1570 µm", vm.StackTotalText);

        var row = Row(vm, "Core");
        row.StagedThicknessText = "800";
        row.CommitThickness();

        Assert.Equal("870 µm", vm.StackTotalText);
    }

    /// <summary>The derived total must never reach the file — it is data already in it.</summary>
    [Fact]
    public void TheTotalIsNotSerialized()
    {
        Assert.DoesNotContain("TotalThicknessDbu", TechPersistence.Serialize(TwoLayerBoard()),
                              StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 6 — a stated board thickness, shown beside the total and never corrected
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void AJobFilesBoardThickness_ReachesTheStackup()
    {
        var dir = Folder("boardthk");
        Write(dir, "board.gtl", Artwork("Copper,L1,Top,Signal"));
        Write(dir, "board.gbl", Artwork("Copper,L2,Bot,Signal", xMm: 2.0));
        Write(dir, "board.gbrjob", JobFileNoStackup);

        var tech = TechPersistence.LoadFromFile(Import(dir, "boardthk_import").TechPath!);

        // 1.57 mm at 1000 DBU/µm.
        Assert.Equal(1_570_000, tech.Stackup.BoardThicknessDbu);
    }

    [Fact]
    public void TheEditorShowsBothNumbers_AndFlagsADisagreement_WithoutCorrectingEither()
    {
        var tech = TwoLayerBoard();
        tech.Stackup.BoardThicknessDbu = 1_570_000;      // the stack itself sums to 1,570,000
        var vm = Editor(tech);

        Assert.True(vm.HasBoardThickness);
        Assert.Equal("1570 µm", vm.BoardThicknessText);
        Assert.False(vm.HasStackHeightMismatch);

        // Now make the two disagree by a whole dielectric's worth.
        var row = Row(vm, "Core");
        row.StagedThicknessText = "800";
        row.CommitThickness();

        Assert.True(vm.HasStackHeightMismatch);
        Assert.Equal("870 µm", vm.StackTotalText);

        // NOT corrected — neither number moved to meet the other.
        Assert.Equal(1_570_000, vm.Working.Stackup.BoardThicknessDbu);
        Assert.Equal(870_000, vm.Working.Stackup.TotalThicknessDbu);
        Assert.Contains("Nothing has been corrected", vm.StackHeightMismatchText, StringComparison.Ordinal);
    }

    /// <summary>The tolerance is stated and is not zero: a stack within it is not a disagreement.</summary>
    [Fact]
    public void ASmallDifferenceIsNotADisagreement()
    {
        var tech = TwoLayerBoard();
        tech.Stackup.BoardThicknessDbu = tech.Stackup.TotalThicknessDbu + 5_000;   // 5 µm on 1.57 mm
        Assert.False(Editor(tech).HasStackHeightMismatch);

        tech.Stackup.BoardThicknessDbu = tech.Stackup.TotalThicknessDbu + 100_000; // 100 µm on 1.57 mm
        Assert.True(Editor(tech).HasStackHeightMismatch);
    }

    /// <summary>Nothing stated one is the ordinary case — every hand-authored technology — and the
    /// editor must not invent a comparison.</summary>
    [Fact]
    public void WithNoStatedBoardThickness_ThereIsNothingToDisagreeWith()
    {
        var vm = Editor(TwoLayerBoard());
        Assert.False(vm.HasBoardThickness);
        Assert.False(vm.HasStackHeightMismatch);
        Assert.Equal("", vm.BoardThicknessText);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 7 — duplicate names
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void TwoConductorsSharingAName_YieldExactlyOneProblem_NamingBoth()
    {
        var tech = TwoLayerBoard();
        tech.Stackup.Layers[2].Name = "Top Copper";        // both conductors now share one name
        tech.Stackup.Layers[3].SpanToLayer = "Top Copper"; // ...and the via spans the ambiguous name

        var problems = TechValidation.Analyze(tech)
            .Where(p => p.Message.Contains("share the name", StringComparison.Ordinal)).ToList();

        var only = Assert.Single(problems);
        Assert.Equal(TechProblemArea.Stackup, only.Area);
        Assert.Contains("\"Top Copper\"", only.Message, StringComparison.Ordinal);
        Assert.Contains("#1 (Conductor)", only.Message, StringComparison.Ordinal);
        Assert.Contains("#3 (Conductor)", only.Message, StringComparison.Ordinal);

        // R-gi3-8: ONE cause, ONE message. The via's span is not additionally reported.
        Assert.DoesNotContain(TechValidation.Validate(tech),
            m => m.Contains("spans an unknown conductor layer", StringComparison.Ordinal));
    }

    /// <summary>Latent today — nothing resolves a dielectric by name — and reported on the same terms,
    /// because the cost of allowing it is paid by whoever adds the next name-based reference.</summary>
    [Fact]
    public void TwoDielectricsSharingAName_AreReportedOnTheSameTerms()
    {
        var tech = TwoLayerBoard();
        tech.Stackup.Layers.Insert(2, new StackupLayer
        {
            Kind = StackupKind.Dielectric, Name = "Core", ThicknessDbu = 100_000, Epsr = 4.4,
        });

        var only = Assert.Single(TechValidation.Validate(tech),
            m => m.Contains("share the name", StringComparison.Ordinal));
        Assert.Contains("(Dielectric)", only, StringComparison.Ordinal);
    }

    [Fact]
    public void DistinctNames_ReportNothing()
    {
        Assert.DoesNotContain(TechValidation.Validate(TwoLayerBoard()),
            m => m.Contains("share the name", StringComparison.Ordinal));
    }

    /// <summary>The editor must not inflict the problem on itself: two clicks on "＋ Conductor" used to
    /// produce two entries called "New Conductor".</summary>
    [Fact]
    public void AddingTwoLayersOfOneKind_DoesNotCreateADuplicateName()
    {
        var vm = Editor(TwoLayerBoard());
        vm.AddConductorLayerCommand.Execute(null);
        vm.AddConductorLayerCommand.Execute(null);

        var names = vm.Working.Stackup.Layers.Select(l => l.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(TechValidation.Validate(vm.Working),
            m => m.Contains("share the name", StringComparison.Ordinal));
    }

    /// <summary>No shipped technology carries a duplicate — the check must not fire on the files the
    /// application itself hands people.</summary>
    [Fact]
    public void NoShippedTechnologyHasADuplicateStackupName()
    {
        foreach (var tech in ShippedTechnologies.All.Select(ShippedTechnologies.Load))
            Assert.DoesNotContain(TechValidation.Validate(tech),
                m => m.Contains("share the name", StringComparison.Ordinal));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 8 — via rows are outside the z order, and the model's order is untouched
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void TheEditorListsSubstrateEntriesFirst_ThenTheVias_WithoutTouchingTheModelsOrder()
    {
        var tech = TwoLayerBoard();

        // A via added BEFORE the conductors — the case that used to render above the top copper and
        // read as a layer sitting over the board.
        tech.Stackup.Layers.Insert(0, new StackupLayer
        {
            Kind = StackupKind.Via, Name = "Blind Via", DrawingLayers = [new LayerKey(3, 0)],
            Fill = ViaFillKind.Plated, WallThicknessDbu = 25_000,
            SpanFromLayer = "Top Copper", SpanToLayer = "Bottom Copper",
        });

        string modelOrderBefore = string.Join("|", tech.Stackup.Layers
            .Where(l => l.Kind != StackupKind.Via).Select(l => l.Name));

        var vm = Editor(tech);

        Assert.Equal(["Top Copper", "Core", "Bottom Copper", "Blind Via", "PTH"],
                     vm.FilteredStackupLayers.Select(r => r.Layer.Name));

        // R-gi3-9's hard constraint: the presentation moved, the z order did not.
        Assert.Equal(modelOrderBefore, string.Join("|", vm.Working.Stackup.Layers
            .Where(l => l.Kind != StackupKind.Via).Select(l => l.Name)));
        Assert.Equal(["Blind Via", "Top Copper", "Core", "Bottom Copper", "PTH"],
                     vm.Working.Stackup.Layers.Select(l => l.Name));
    }

    /// <summary>The group is labelled, once, on the first via row — and the label follows the filter
    /// rather than being pinned to a model position.</summary>
    [Fact]
    public void ExactlyOneViaRowCarriesTheGroupHeader_AndItFollowsTheFilter()
    {
        var tech = TwoLayerBoard();
        tech.Stackup.Layers.Add(new StackupLayer { Kind = StackupKind.Via, Name = "Blind Via" });
        var vm = Editor(tech);

        Assert.Equal("PTH", Assert.Single(vm.FilteredStackupLayers, r => r.ShowsViaGroupHeader).Layer.Name);

        vm.StackupFilter = "Blind";
        Assert.Equal("Blind Via", Assert.Single(vm.FilteredStackupLayers, r => r.ShowsViaGroupHeader).Layer.Name);

        // A filter that lists no via at all leaves no header behind on any row.
        vm.StackupFilter = "Core";
        Assert.DoesNotContain(vm.StackupLayers, r => r.ShowsViaGroupHeader);
    }

    [Fact]
    public void MovingIsRefusedOnAViaRow()
    {
        var vm = Editor(TwoLayerBoard());
        var via = Row(vm, "PTH");
        Assert.False(via.CanMove);

        string before = string.Join("|", vm.Working.Stackup.Layers.Select(l => l.Name));
        via.MoveUpCommand.Execute(null);
        via.MoveDownCommand.Execute(null);
        Assert.Equal(before, string.Join("|", vm.Working.Stackup.Layers.Select(l => l.Name)));
    }

    /// <summary>A substrate row still moves, and it moves past an intervening via rather than trading
    /// places with it — which is what the presented order means.</summary>
    [Fact]
    public void ASubstrateRowMovesPastAnInterveningVia()
    {
        var tech = TwoLayerBoard();
        tech.Stackup.Layers.Insert(1, new StackupLayer { Kind = StackupKind.Via, Name = "Blind Via" });
        var vm = Editor(tech);
        Assert.True(Row(vm, "Core").CanMove);

        Row(vm, "Core").MoveUpCommand.Execute(null);

        // The two substrate entries traded places; the via's own index did not move, which is correct
        // precisely because it carries no meaning.
        Assert.Equal(["Core", "Blind Via", "Top Copper", "Bottom Copper", "PTH"],
                     vm.Working.Stackup.Layers.Select(l => l.Name));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The board import — the same two answers, on the path GI3's own brief did not scope
    //
    // Both were noticed while writing this phase's RESOLVED.md note and fixed on the owner's ask
    // rather than left as follow-ups. Neither is a new idea: they are R-gi3-7 and R-gi3-4 applied to
    // the reader that had the same two gaps.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// R-gi3-7 on the board path. <c>PcbStackupMapping</c> took an <c>overallThicknessMm</c>, named it
    /// in one sentence and dropped it. It is now carried on the stackup it BUILDS — which is the
    /// difference from the Gerber path: a board import never replaces a stackup that is already there,
    /// so a thickness belonging to a different board must be refused with the rows it came with.
    /// </summary>
    [Fact]
    public void ABoardFilesOverallThickness_ReachesTheStackupItBuilds()
    {
        var stackup = PcbStackupMapping.Build(
            [
                new PcbStackupEntry("F.Cu",         "copper",  0.035, null, null),
                new PcbStackupEntry("dielectric 1", "core",    1.500, 4.4,  0.02),
                new PcbStackupEntry("B.Cu",         "copper",  0.035, null, null),
            ],
            overallThicknessMm: 1.6, dbuPerMicron: 1000, _ => null);

        Assert.Equal(1_600_000, stackup.Stackup!.BoardThicknessDbu);

        // ...and it is a second opinion, not a correction: the rows still sum to what the rows say.
        Assert.Equal(1_570_000, stackup.Stackup.TotalThicknessDbu);
    }

    /// <summary>A file stating no overall thickness carries none — the field must stay null rather
    /// than acquire a number nothing said.</summary>
    [Fact]
    public void ABoardFileStatingNoOverallThickness_CarriesNone()
    {
        var stackup = PcbStackupMapping.Build(
            [new PcbStackupEntry("F.Cu", "copper", 0.035, null, null)],
            overallThicknessMm: null, dbuPerMicron: 1000, _ => null);

        Assert.Null(stackup.Stackup!.BoardThicknessDbu);
    }

    /// <summary>
    /// R-gi3-4 on the board path. <c>PcbViaSpanMapping</c> minted its entries with no fill model at
    /// all, which is the only reason they never tripped the validator's wall-thickness rule — that
    /// rule fires on <c>Fill == Plated</c>. Quiet, not right: an entry is minted ONLY for a span
    /// joining two DIFFERENT conductors, which is to say only for interconnect, and interconnect is
    /// plated. <b>Both fields together</b> — Fill alone would engage the rule and hand back the very
    /// problem this phase removed.
    /// </summary>
    [Fact]
    public void AMintedBoardViaEntry_IsPlatedWithTheSameWallThicknessEverythingElseUses()
    {
        var stackup = new Stackup();
        stackup.Layers.AddRange(
        [
            new StackupLayer { Kind = StackupKind.Conductor, Name = "Top",
                               ThicknessDbu = 35_000, SigmaSm = 5.8e7, DrawingLayers = [new LayerKey(1, 0)] },
            new StackupLayer { Kind = StackupKind.Dielectric, Name = "Core",
                               ThicknessDbu = 1_500_000, Epsr = 4.4, TanD = 0.02 },
            new StackupLayer { Kind = StackupKind.Conductor, Name = "Bottom",
                               ThicknessDbu = 35_000, SigmaSm = 5.8e7, DrawingLayers = [new LayerKey(2, 0)] },
        ]);

        var built = PcbViaSpanMapping.Build(
            [new PcbViaSpanMapping.SourceSpan(new LayerKey(1, 0), new LayerKey(2, 0))],
            stackup,
            [new LayerKey(1, 0), new LayerKey(2, 0)],
            dbuPerMicron: 1000);

        var entry = Assert.Single(built.NewEntries);
        Assert.Equal(ViaFillKind.Plated, entry.Fill);
        Assert.Equal(ViaDefaults.PlatedWallThicknessDbu(1000), entry.WallThicknessDbu);
        Assert.Equal(25_000, entry.WallThicknessDbu);

        // Named as a default, in the sentence that says the entries were created (R-L4d-7's pattern).
        Assert.Contains(built.Messages,
            m => m.Contains("wall thickness defaulted to 25 µm", StringComparison.Ordinal) &&
                 m.Contains("PLATING thickness", StringComparison.Ordinal));

        // And the whole point: applying it leaves a technology the validator is silent about, rather
        // than one carrying a problem the import itself created.
        foreach (var e in built.NewEntries) stackup.Layers.Add(e);
        Assert.DoesNotContain(TechValidation.Validate(new Technology { Name = "T", Stackup = stackup }),
            m => m.Contains("Plated with no wall thickness", StringComparison.Ordinal));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gerber fixtures — hand-authored, following L4e/L4f/L4g/GI1/GI2's precedent
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private const string MmHeader = "%FSLAX46Y46*%\n%MOMM*%\n";

    private static string Artwork(string? fileFunction = null, double xMm = 1.0, double yMm = 1.0)
    {
        string attribute = fileFunction is { Length: > 0 } fn ? $"%TF.FileFunction,{fn}*%\n" : "";
        long x = (long)Math.Round(xMm * 1_000_000);
        long y = (long)Math.Round(yMm * 1_000_000);
        return MmHeader + attribute + "%ADD10C,0.400*%\nD10*\n" + $"X{x}Y{y}D03*\n" + "M02*\n";
    }

    private static string Drill(double xMm = 1.0, double yMm = 1.0) =>
        "M48\nMETRIC\nT1C0.300000\n%\nG90\nG05\nT1\n" + $"X{xMm:0.000000}Y{yMm:0.000000}\n" + "M30\n";

    private const string JobFileNoStackup =
        """
        {
          "Header": { "GenerationSoftware": { "Vendor": "n/a", "Application": "n/a" } },
          "GeneralSpecs": { "ProjectId": { "Name": "board" }, "Size": { "X": 10, "Y": 10 },
                            "LayerNumber": 2, "BoardThickness": 1.57 }
        }
        """;

    private string Folder(string name)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Write(string dir, string fileName, string content) =>
        File.WriteAllText(Path.Combine(dir, fileName), content);

    private GerberImport.ImportResult Import(string sourceDir, string name) =>
        GerberImport.Import(
            [.. Directory.EnumerateFiles(sourceDir).OrderBy(p => p, StringComparer.Ordinal)],
            _root, name, null, 1000, null, null);

    private GerberImport.ImportResult ImportTwoLayerWithDrill(string name)
    {
        var dir = Folder(name);
        Write(dir, "board.gtl", Artwork("Copper,L1,Top,Signal"));
        Write(dir, "board.gbl", Artwork("Copper,L2,Bot,Signal", xMm: 2.0));
        Write(dir, "board.drl", Drill());
        return Import(dir, name + "_import");
    }
}
