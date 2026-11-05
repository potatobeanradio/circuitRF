// SI-SIG — a dielectric's conductivity in the file, the extractors, the 3D route, the editor and the line models.

using CircuitRF.Core.Design;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Schematic;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using Xunit;

namespace CircuitRF.Ui.Tests.Em;

public sealed class ConductingDielectricTests
{
    private const int Dbu = 1000;

    private static TechMaterial Silicon(double? sigma) =>
        new() { Name = "Si", Epsr = 11.9, TanD = 0, Mur = 1, Sigma20 = sigma };

    /// <summary>Metal over 2 µm of oxide on 100 µm of silicon over a backside plane, with a line drawn on layer 1.</summary>
    private static Technology Tech(double sigma, string? material = null, IEnumerable<TechMaterial>? materials = null)
    {
        var tech = new Technology { Name = "si", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000 };
        tech.Layers.Add(new LayerDef { Key = new LayerKey(1, 0), Name = "M", Color = new CircuitRF.Design.Theming.Rgba(1, 2, 3), ZOrder = 1 });
        tech.Stackup.Layers.Add(new StackupLayer { Kind = StackupKind.Conductor, Name = "M", ThicknessDbu = 3000, SigmaSm = 5.8e7, DrawingLayers = [new LayerKey(1, 0)] });
        tech.Stackup.Layers.Add(new StackupLayer { Kind = StackupKind.Dielectric, Name = "Oxide", ThicknessDbu = 2000, Epsr = 3.9 });
        tech.Stackup.Layers.Add(new StackupLayer { Kind = StackupKind.Dielectric, Name = "Silicon", ThicknessDbu = 100000, Epsr = 11.9, SigmaSm = sigma, Material = material });
        tech.Stackup.Layers.Add(new StackupLayer { Kind = StackupKind.Conductor, Name = "Back", ThicknessDbu = 3000, SigmaSm = 5.8e7, IsGroundReference = true });
        if (materials is not null) tech.Materials.AddRange(materials);
        return tech;
    }

    private static List<LayoutShape> Line() =>
    [
        new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = -45 * Dbu, X2 = 1000 * Dbu, Y2 = 45 * Dbu },
        new LabelShape { Layer = new LayerKey(1, 0), X = 0, Y = 0, Text = "1", Height = 5000, IsPort = true },
        new LabelShape { Layer = new LayerKey(1, 0), X = 1000 * Dbu, Y = 0, Text = "2", Height = 5000, IsPort = true },
    ];

    // ── R-sisig-1 — the file ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void AMaterialWithBothPutsItsConductivityOnADielectricEntry()
    {
        var tech = Tech(0, "Si", [Silicon(10)]);
        TechPersistence.ResolveMaterials(tech);
        Assert.Equal(10, tech.Stackup.Layers[2].SigmaSm);
    }

    [Fact]
    public void AMaterialWithConductivityAndNoPermittivityPutsNothingOnADielectricEntry()
    {
        var tech = Tech(0, "Metal", [new TechMaterial { Name = "Metal", Sigma20 = 5.8e7 }]);
        TechPersistence.ResolveMaterials(tech);
        Assert.Equal(0, tech.Stackup.Layers[2].SigmaSm);
    }

    [Fact]
    public void ANegativeConductivityIsAnErrorAndOneBesideATanDeltaIsNot()
    {
        Assert.Contains(TechValidation.Analyze(Tech(-1)), p => p.Message.Contains("non-negative number"));
        var both = Tech(10);
        both.Stackup.Layers[2].TanD = 0.001;
        Assert.DoesNotContain(TechValidation.Analyze(both), p => p.Message.Contains("conductivity of"));
    }

    [Theory]
    [InlineData("Silicon, 10 kΩ·cm", 0.01)]
    [InlineData("Silicon, 800 Ω·cm", 0.125)]
    [InlineData("Silicon, 10 Ω·cm", 10.0)]
    [InlineData("Silicon, 0.01 Ω·cm", 1e4)]
    public void TheLibrarysSiliconsStateTheirConductivity(string name, double sigma)
    {
        var m = MaterialLibraries.LoadGeneric().Single(x => x.Name == name);
        Assert.Equal(sigma, m.Sigma20);
        Assert.Equal(0, m.TanD);
        Assert.Equal(11.9, m.Epsr);
    }

    [Fact]
    public void HighResistivitySiliconKeepsItsConstantLossTangent()
    {
        var m = MaterialLibraries.LoadGeneric().Single(x => x.Name == "High-resistivity silicon");
        Assert.Equal(0.0015, m.TanD);
        Assert.Null(m.Sigma20);
    }

    // ── R-sisig-2 / gate 1 — the extractor and the provenance ────────────────────────────────────

    private static PlanarProblem Extract(Technology tech)
    {
        var r = PlanarExtractor.Extract(Line(), tech, Dbu, 40e9);
        Assert.True(r.Problem is not null, r.Refusal);
        return r.Problem!;
    }

    [Fact]
    public void AStatedConductivityReachesThePlanarMedium() =>
        Assert.Equal(10, Extract(Tech(10)).EffectiveStack.Layers[0].Material.SigmaSm);

    /// <summary>Gate 1's half about provenance: only a stated σ moves the hash. The string with none is the
    /// one every existing .snp was stamped with, since the only change is a clause written when σ is not 0.</summary>
    [Fact]
    public void OnlyAStatedConductivityMovesTheGeometryHash()
    {
        string none = EmSnpProvenance.GeometryHash(Extract(Tech(0)));
        Assert.Equal(none, EmSnpProvenance.GeometryHash(Extract(Tech(0))));
        Assert.NotEqual(none, EmSnpProvenance.GeometryHash(Extract(Tech(10))));
    }

    [Fact]
    public void TheTwoDimensionalKernelRefusesAConductingDielectricAndAutomaticGoesOn()
    {
        var cross = CrossSectionExtractor.Extract(Line(), Tech(10), Dbu);
        Assert.Contains("states a conductivity", cross.Refusal);
        Assert.Null(CrossSectionExtractor.Extract(Line(), Tech(0), Dbu).Refusal);
    }

    // ── R-sisig-3 / gate 5 — 3D ──────────────────────────────────────────────────────────────────

    /// <summary>Gate 5: the stackup's σ arrives as a body's does, and Palace's config writes it the same way.</summary>
    [Fact]
    public void AStackupConductivityReachesPalaceAsABodysDoes()
    {
        var view = new LayoutView();
        view.Shapes.AddRange(Line());
        var tech = Tech(10);
        var face = new EmAirBoxFace(500, null);
        var setup = new EmSetup
        {
            Name = "line", LayoutRef = "line/layout/line.clay",
            Frequency = new FrequencySpec("1", "40", 5, SweepKind.Linear, "GHz", "GHz"),
            Solver3D = Em3dSolverId.Palace,
            AirBox = new EmAirBox(face, face, face, face, face, face),
        };
        var g = Em3dGenerator.Generate(setup, new EmLayoutSource("/nowhere/line.clay", view, tech, Dbu), tech);
        Assert.True(g.Ok, g.Refusal);

        var silicon = g.Problem!.Materials.Single(m => m.Epsr == 11.9);
        Assert.Equal(10, silicon.SigmaSm);
        Assert.Contains(g.Problem.Solids, s => s.Material == silicon.Name && s.Role == Em3dRole.Dielectric);
    }

    // ── R-sisig-4 / gate 6 — the editor ──────────────────────────────────────────────────────────

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"crf-sisig-{Guid.NewGuid():N}.ctech");

    private static (TechEditorViewModel Vm, StackupLayerRowViewModel Row) Editor(double sigma)
    {
        var vm = new TechEditorViewModel(TempPath(), Tech(sigma));
        return (vm, vm.StackupLayers.Single(r => r.Layer.Name == "Silicon"));
    }

    [Theory]
    [InlineData("S/m", "10", 10.0)]
    [InlineData("Ω·cm", "10", 10.0)]
    [InlineData("Ω·cm", "800", 0.125)]
    [InlineData("Ω·cm", "0.01", 1e4)]
    public void BothUnitsStoreSiemensPerMetre(string unit, string typed, double stored)
    {
        var (_, row) = Editor(0);
        row.SelectedDielectricSigmaUnit = unit;
        row.DielectricSigmaInUnit = typed;
        row.CommitDielectricSigma();
        Assert.Equal(stored, row.Layer.SigmaSm);
    }

    [Theory]
    [InlineData("—")]
    [InlineData("")]
    public void TheDashMeansNone(string typed)
    {
        var (_, row) = Editor(10);
        row.DielectricSigmaInUnit = typed;
        row.CommitDielectricSigma();
        Assert.Equal(0, row.Layer.SigmaSm);
        Assert.Equal("—", row.DielectricSigmaInUnit);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("abc")]
    public void AValueThatIsNotAConductivityReverts(string typed)
    {
        var (_, row) = Editor(10);
        row.DielectricSigmaInUnit = typed;
        row.CommitDielectricSigma();
        Assert.Equal(10, row.Layer.SigmaSm);
        Assert.Equal("10", row.DielectricSigmaInUnit);
    }

    /// <summary>Gate 6: visiting the card and switching units writes nothing, so the file comes back byte for byte.</summary>
    [Fact]
    public void AConductivityRoundTripsThroughTheEditorByteForByte()
    {
        string file = TechPersistence.Serialize(Tech(0.123456789));
        var vm = new TechEditorViewModel(TempPath(), TechPersistence.Deserialize(file));
        var row = vm.StackupLayers.Single(r => r.Layer.Name == "Silicon");

        row.CommitDielectricSigma();
        row.SelectedDielectricSigmaUnit = "Ω·cm";
        row.DielectricSigmaInUnit = row.DielectricSigmaInUnit;
        row.CommitDielectricSigma();

        Assert.Equal(file, TechPersistence.Serialize(vm.Working));
    }

    [Fact]
    public void ChoosingAMaterialFillsTheConductivityAndOneWithNoneClearsIt()
    {
        var (_, row) = Editor(0);
        row.SelectedMaterial = "Silicon, 800 Ω·cm";
        Assert.Equal(0.125, row.Layer.SigmaSm);
        Assert.Equal("0.125", row.DielectricSigmaInUnit);

        row.SelectedMaterial = "High-resistivity silicon";
        Assert.Equal(0, row.Layer.SigmaSm);
        Assert.Equal("—", row.DielectricSigmaInUnit);
    }

    [Fact]
    public void TheMaterialComboboxListsTheNewSiliconsForADielectricOnly()
    {
        var (vm, row) = Editor(0);
        Assert.Contains("Silicon, 10 Ω·cm", row.MaterialChoices);
        Assert.Contains("Silicon, 0.01 Ω·cm", row.MaterialChoices);
        Assert.DoesNotContain("Silicon, 10 Ω·cm", vm.StackupLayers.Single(r => r.Layer.Name == "M").MaterialChoices);
    }

    // ── R-sisig-5 / gate 7 — the line models ─────────────────────────────────────────────────────

    [Fact]
    public void MlinOnAConductingSubstrateSaysTheClosedFormDoesNotApply()
    {
        MicrostripSubstrateInjection.BuildOverrides(Tech(10), out string? warning);
        Assert.Contains("does not apply", warning);
        Assert.Contains("EM run", warning);

        MicrostripSubstrateInjection.BuildOverrides(Tech(0), out string? none);
        Assert.Null(none);
    }

    [Fact]
    public void TheLineCalculatorSaysSoAndCarriesTheConductivity()
    {
        var r = LineCalculator.Calculate(Tech(10), new LineCalcRequest("M", [90e-6], []));
        Assert.Contains(r.Warnings, w => w.Contains("does not apply"));
        Assert.Equal(10, r.Substrate!.DielectricSigmaSPerM);
    }

    [Fact]
    public void TheImpedanceReportSaysSo()
    {
        var report = TraceImpedanceAnalysis.Analyze(Line(), Tech(10), Dbu, new TraceImpedanceOptions());
        Assert.Contains(report.Notes, n => n.Contains("do not apply here"));
    }
}
