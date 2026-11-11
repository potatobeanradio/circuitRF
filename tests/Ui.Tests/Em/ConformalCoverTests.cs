// ================================================================
//  ConformalCoverTests.cs — PSV-1b: a dielectric marked Conformal starts on top of the metal below it
//
//  R-em-4a fills a conductor band's empty space with the dielectric above it, so a cover over the top
//  metal was modelled as the metal's thickness plus its own: right for a solder mask, about 3x too much
//  for a conformal coat. StackupLayer.Conformal leaves that one band as air, in every solver and in the
//  stackup drawing, through the one rule Stackup.LeavesBandAsAir.
//
//  The solve gates (Palace electrostatics, the planar and cross-section kernels on a 20 x 3 µm line
//  under 1 µm of nitride) were measured once and are recorded in src/Design/RESOLVED.md: each is a
//  solve of seconds to a minute, or needs an external solver, and the series adds no Benchmark test.
// ================================================================

using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Em3d;
using CircuitRF.Diagnostics;
using CircuitRF.Render;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Tests.Em3d;

namespace CircuitRF.Ui.Tests.Em;

public sealed class ConformalCoverTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey Metal = new(1, 0);

    private static long Um(double v) => (long)Math.Round(v * Dbu);

    private static StackupLayer Dielectric(string name, double um, double epsr, bool? conformal = null) => new()
    {
        Kind = StackupKind.Dielectric, Name = name, ThicknessDbu = Um(um), Epsr = epsr, Mur = 1, Conformal = conformal,
    };

    private static StackupLayer Gold(string name, double um, LayerKey? drawn, bool ground,
                                     ConductorSheetSurface? sheet = null) => new()
    {
        Kind = StackupKind.Conductor, Name = name, ThicknessDbu = Um(um), SigmaSm = 4.1e7,
        DrawingLayers = drawn is { } k ? [k] : [], IsGroundReference = ground, SheetAt = sheet,
    };

    /// <summary>1 µm εᵣ 7 nitride / 3 µm gold / 100 µm GaAs / ground — the measured fixture.</summary>
    private static Technology Mmic(bool? conformal, ConductorSheetSurface? sheet = null) => new()
    {
        Name = "psv1b", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = Um(1), DefaultFlattenTolDbu = Um(1),
        Layers = [new LayerDef { Key = Metal, Name = "Metal", ZOrder = 1, Purpose = "drawing" }],
        Stackup = new Stackup
        {
            Top = BoundaryCondition.Open, Bottom = BoundaryCondition.Ground,
            Layers =
            [
                Dielectric("Nitride", 1, 7.0, conformal),
                Gold("Metal", 3, Metal, ground: false, sheet),
                Dielectric("GaAs", 100, 12.9),
                Gold("Backside", 3, null, ground: true),
            ],
        },
    };

    private static List<LayoutShape> Line() =>
    [
        new RectShape { Layer = Metal, X1 = 0, Y1 = -Um(10), X2 = Um(400), Y2 = Um(10) },
        new LabelShape { Layer = Metal, X = 0, Y = 0, Text = "1", Height = Um(5), IsPort = true },
        new LabelShape { Layer = Metal, X = Um(400), Y = 0, Text = "2", Height = Um(5), IsPort = true },
    ];

    /// <summary><b>Gate 1, the file.</b> Unset, the field is not written, so every existing <c>.ctech</c>
    /// round-trips byte for byte; set, it is written and read back.</summary>
    [Fact]
    public void TheField_IsWrittenOnlyWhenSet_AndRoundTrips()
    {
        string bare = TechPersistence.Serialize(Mmic(conformal: null));
        Assert.DoesNotContain("Conformal", bare, StringComparison.Ordinal);
        Assert.Equal(bare, TechPersistence.Serialize(TechPersistence.Deserialize(bare)));

        string set = TechPersistence.Serialize(Mmic(conformal: true));
        Assert.Contains("\"Conformal\": true", set, StringComparison.Ordinal);
        Assert.True(TechPersistence.Deserialize(set).Stackup.Layers[0].Conformal);
    }

    /// <summary>Validation warns, and only warns, wherever the setting acts on nothing: a non-dielectric,
    /// a dielectric with no conductor directly below, and one over a conductor whose sheet is at its top.</summary>
    [Fact]
    public void Validation_WarnsWhereTheSettingHasNoEffect()
    {
        IEnumerable<TechProblem> Conformal(Technology t) =>
            TechValidation.Analyze(t).Where(p => p.Id == TechValidation.Ids.ConformalNoEffect);

        Assert.Empty(Conformal(Mmic(conformal: true)));

        var onTop = Mmic(conformal: true, sheet: ConductorSheetSurface.Top);
        var w = Assert.Single(Conformal(onTop));
        Assert.Equal(DiagnosticSeverity.Warning, w.Severity);
        Assert.Contains("\"Metal\"", w.Message, StringComparison.Ordinal);

        var overDielectric = Mmic(conformal: null);
        overDielectric.Stackup.Layers[2].Conformal = true;          // GaAs: Backside is below it, so it acts
        overDielectric.Stackup.Layers.Insert(2, Dielectric("Spacer", 5, 4.0, conformal: true));
        Assert.Contains("not a conductor", Assert.Single(Conformal(overDielectric)).Message, StringComparison.Ordinal);

        var onMetal = Mmic(conformal: null);
        onMetal.Stackup.Layers[1].Conformal = true;
        Assert.Contains("Only a dielectric", Assert.Single(Conformal(onMetal)).Message, StringComparison.Ordinal);
    }

    /// <summary><b>Planar.</b> The metal's band is a free-space region under the cover, the level stays on
    /// the substrate, nothing says "taken as free space", and the cover note says where the cover starts.
    /// Unset, the band is the cover's material, as before.</summary>
    [Fact]
    public void Planar_TheBandUnderAConformalCover_IsFreeSpace_AndTheNoteSaysWhereTheCoverStarts()
    {
        var absorbed = PlanarExtractor.Extract(Line(), Mmic(conformal: null), Dbu, 1e9);
        Assert.True(absorbed.Ok, absorbed.Refusal);
        var a = absorbed.Problem!.EffectiveStack;
        Assert.Equal(2, a.LayerCount);
        Assert.Equal(4e-6, a.Layers[1].ThicknessM, 12);
        Assert.Equal(7.0, a.Layers[1].Material.EpsR);

        var conformal = PlanarExtractor.Extract(Line(), Mmic(conformal: true), Dbu, 1e9);
        Assert.True(conformal.Ok, conformal.Refusal);
        var c = conformal.Problem!.EffectiveStack;
        Assert.Equal(3, c.LayerCount);
        Assert.Equal(100e-6, c.Layers[0].ThicknessM, 12);
        Assert.Equal((3e-6, 1.0), (Math.Round(c.Layers[1].ThicknessM, 12), c.Layers[1].Material.EpsR));
        Assert.Equal((1e-6, 7.0), (Math.Round(c.Layers[2].ThicknessM, 12), c.Layers[2].Material.EpsR));
        Assert.Equal(100e-6, conformal.Problem.LevelZ(0), 12);

        Assert.DoesNotContain(conformal.Notes, n => n.Contains("taken as free space", StringComparison.Ordinal));
        string note = Assert.Single(conformal.Notes, n => n.Contains("'Nitride' is conformal", StringComparison.Ordinal));
        Assert.Contains("3 µm above 'Metal'", note, StringComparison.Ordinal);
    }

    /// <summary><b>Cross-section.</b> The cover's region starts at the metal's top and an air region fills
    /// the metal's band; unset, the cover's region takes the band.</summary>
    [Fact]
    public void CrossSection_TheCoverRegionStartsAtTheMetalsTop()
    {
        static IReadOnlyList<EmRegionReadback> Regions(Technology t)
        {
            var r = CrossSectionExtractor.Extract(Line().Take(1).ToList(), t, Dbu);
            Assert.True(r.Ok, r.Refusal);
            return r.Readback!.Regions;
        }

        var absorbed = Regions(Mmic(conformal: null));
        var nitride = Assert.Single(absorbed, r => r.Name == "Nitride");
        Assert.Equal((100e-6, 104e-6), (Math.Round(nitride.YBottomMeters, 12), Math.Round(nitride.YTopMeters, 12)));

        var conformal = Regions(Mmic(conformal: true));
        Assert.Equal(["GaAs", "Air", "Nitride", "Air"], conformal.Select(r => r.Name));
        Assert.Equal((100e-6, 103e-6, 1.0), (Math.Round(conformal[1].YBottomMeters, 12), Math.Round(conformal[1].YTopMeters, 12), conformal[1].EpsR));
        Assert.Equal((103e-6, 104e-6), (Math.Round(conformal[2].YBottomMeters, 12), Math.Round(conformal[2].YTopMeters, 12)));
    }

    /// <summary><b>3D.</b> On the RO4350B starter's microstrip under a 25 µm mask: the mask solid starts on
    /// the substrate (absorbing the copper's band) when unset, and on the copper's top when conformal.</summary>
    [Fact]
    public void ThreeD_AConformalMaskSolid_StartsOnTheCoppersTop()
    {
        double MaskBottom(bool? conformal)
        {
            var (setup, source) = Em3dGeneratorTests.Microstrip();
            source.Technology!.Stackup.Layers.Insert(0, Dielectric("Mask", 25, 3.5, conformal));
            var r = Em3dGenerator.Generate(setup, source, source.Technology!);
            Assert.True(r.Ok, r.Refusal);
            return Assert.IsType<Em3dBox>(r.Problem!.Solids.Single(s => s.Name == "Mask").Primitive).Min.Z;
        }

        Assert.Equal(543e-6, MaskBottom(null), 12);    // the substrate's top
        Assert.Equal(578e-6, MaskBottom(true), 12);    // the copper's top
    }

    /// <summary>The stackup drawing tags a conformal cover, and only where the setting acts.</summary>
    [Fact]
    public void TheStackupDrawing_TagsAConformalCover_OnlyWhereItActs()
    {
        static bool Tagged(Technology t) => StackupScene.Build(t, 900f).Labels
            .Any(l => l.LayerName == "Nitride" && l.Text == StackupScene.ConformalText);

        Assert.True(Tagged(Mmic(conformal: true)));
        Assert.False(Tagged(Mmic(conformal: null)));
        Assert.False(Tagged(Mmic(conformal: true, sheet: ConductorSheetSurface.Top)));
    }

    /// <summary>The editor's checkbox: enabled over a conductor whose metal thickness goes to the layer
    /// above, disabled with its reason otherwise, and one undoable edit that writes null when cleared.</summary>
    [Fact]
    public void TheEditorCheckbox_IsEnabledWhereItActs_AndCommitsUndoably()
    {
        var tech = Mmic(conformal: null);
        var vm = new TechEditorViewModel(Path.Combine(Path.GetTempPath(), $"psv1b-{Guid.NewGuid():N}.ctech"), tech);
        var nitride = vm.StackupLayers.Single(r => r.Layer.Name == "Nitride");
        var gaas    = vm.StackupLayers.Single(r => r.Layer.Name == "GaAs");

        Assert.True(nitride.CanToggleConformal);
        Assert.True(gaas.CanToggleConformal);                 // Backside is a conductor below it
        Assert.False(nitride.IsConformal);

        nitride.IsConformal = true;
        Assert.True(vm.Working.Stackup.Layers.Single(l => l.Name == "Nitride").Conformal);
        vm.UndoRedo.Undo();
        Assert.Null(vm.Working.Stackup.Layers.Single(l => l.Name == "Nitride").Conformal);

        var lowered = Mmic(conformal: null, sheet: ConductorSheetSurface.Top);
        var vm2 = new TechEditorViewModel(Path.Combine(Path.GetTempPath(), $"psv1b-{Guid.NewGuid():N}.ctech"), lowered);
        var row = vm2.StackupLayers.Single(r => r.Layer.Name == "Nitride");
        Assert.False(row.CanToggleConformal);
        Assert.Contains("'Metal'", row.ConformalTip, StringComparison.Ordinal);
    }
}
