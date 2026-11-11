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

    /// <summary><b>3D.</b> On the RO4350B starter's microstrip under a 25 µm mask: unset, the mask is one slab from the
    /// substrate's top (absorbing the copper's band). Conformal, it FOLLOWS the copper (PSV-4): 25 µm on the copper's top,
    /// a 25 µm wall round its sides up to that coat's top, and 25 µm on the substrate beside it.</summary>
    [Fact]
    public void ThreeD_AConformalMask_FollowsTheCopper_TopWallsAndFloor()
    {
        IReadOnlyList<Em3dSolid> Mask(bool? conformal)
        {
            var (setup, source) = Em3dGeneratorTests.Microstrip();
            source.Technology!.Stackup.Layers.Insert(0, Dielectric("Mask", 25, 3.5, conformal));
            var r = Em3dGenerator.Generate(setup, source, source.Technology!);
            Assert.True(r.Ok, r.Refusal);
            return [.. r.Problem!.Solids.Where(s => s.Name == "Mask" || s.Name.StartsWith("Mask/", StringComparison.Ordinal))];
        }

        var flat = Assert.Single(Mask(null));
        Assert.Equal(543e-6, Assert.IsType<Em3dBox>(flat.Primitive).Min.Z, 12);    // the substrate's top

        var coat = Mask(true).ToDictionary(s => s.Name, s => Em3dProblem.Bounds(s.Primitive));
        Assert.Equal(["Mask", "Mask/top", "Mask/wall"], coat.Keys.Order(StringComparer.Ordinal));
        // On the substrate beside the copper, 25 µm thick.
        Assert.Equal((543e-6, 568e-6), (coat["Mask"].Z0, coat["Mask"].Z1), new Tol());
        // On the copper's top, 25 µm thick, exactly over the 1.1 mm strip.
        Assert.Equal((578e-6, 603e-6), (coat["Mask/top"].Z0, coat["Mask/top"].Z1), new Tol());
        Assert.Equal((-550e-6, 550e-6), (coat["Mask/top"].Y0, coat["Mask/top"].Y1), new Tol());
        // The wall: from the substrate to the coat's top, 25 µm outside the strip's edges.
        Assert.Equal((543e-6, 603e-6), (coat["Mask/wall"].Z0, coat["Mask/wall"].Z1), new Tol());
        Assert.Equal((-575e-6, 575e-6), (coat["Mask/wall"].Y0, coat["Mask/wall"].Y1), new Tol());
    }

    /// <summary><b>3D, the fill above a coat thicker than the metal.</b> A 50 µm conformal mask over the starter's 35 µm
    /// copper puts its floor piece's top 15 µm ABOVE the copper's top. The dielectric on the coat fills beside the metal
    /// from that height, never from the copper's top, so no two solids of different materials share a volume.</summary>
    [Fact]
    public void ThreeD_TheFillOnACoatThickerThanTheMetal_StartsOnTheCoat()
    {
        var (setup, source) = Em3dGeneratorTests.Microstrip();
        source.Technology!.Stackup.Layers.InsertRange(0, [Dielectric("Coat", 100, 3.0), Dielectric("Mask", 50, 3.5, conformal: true)]);
        var r = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(r.Ok, r.Refusal);
        var b = r.Problem!.Solids.Where(s => s.Name.StartsWith("Mask", StringComparison.Ordinal) ||
                                             s.Name.StartsWith("Coat", StringComparison.Ordinal))
                 .ToDictionary(s => s.Name, s => Em3dProblem.Bounds(s.Primitive));

        Assert.Equal(["Coat", "Coat/fill", "Mask", "Mask/top", "Mask/wall"], b.Keys.Order(StringComparer.Ordinal));
        Assert.Equal((543e-6, 593e-6), (b["Mask"].Z0, b["Mask"].Z1), new Tol());        // on the substrate, 50 µm
        Assert.Equal((593e-6, 628e-6), (b["Coat/fill"].Z0, b["Coat/fill"].Z1), new Tol()); // on that, up to the coat's top
        Assert.Equal((628e-6, 728e-6), (b["Coat"].Z0, b["Coat"].Z1), new Tol());
    }

    private sealed class Tol : IEqualityComparer<(double, double)>
    {
        public bool Equals((double, double) a, (double, double) b) => Math.Abs(a.Item1 - b.Item1) < 1e-9 && Math.Abs(a.Item2 - b.Item2) < 1e-9;
        public int GetHashCode((double, double) v) => 0;
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
