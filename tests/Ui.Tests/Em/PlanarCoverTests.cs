// ================================================================
//  PlanarCoverTests.cs — PSV-1: a dielectric above the topmost analysis level is in the planar medium
//
//  The medium runs from the return plane up through every dielectric the technology declares, and is
//  terminated in open air above the highest one. A cover over the metal (passivation, solder mask, a
//  coating, a radome) is a layer like any other. Free space at the top of the stack merges into the
//  air termination, so an explicit εᵣ = 1 entry above the top metal changes nothing at all.
//
//  A stack closed above — a lid (Stackup.Top = Ground) or a ground plane above the levels — keeps the
//  stack ending at the topmost level and warns, because the planar medium has one return plane,
//  beneath its levels, and represents neither.
//
//  The two solve gates (a covered line against the cross-section kernel, and a 1 µm nitride over an
//  MMIC line) are Category=Benchmark: each is a de-embedded solve of 15-30 s.
// ================================================================

using CircuitRF.Design.Layout;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em;

public sealed class PlanarCoverTests(ITestOutputHelper output)
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey Top    = new(1, 0);
    private static readonly LayerKey Bottom = new(2, 0);

    private static long Um(double v) => (long)Math.Round(v * Dbu);
    private static long M(double metres) => (long)Math.Round(metres * 1e6 * Dbu);

    private static StackupLayer Dielectric(string name, double um, double epsr, double tanD = 0.0) => new()
    {
        Kind = StackupKind.Dielectric, Name = name, ThicknessDbu = Um(um), Epsr = epsr, TanD = tanD, Mur = 1,
    };

    private static StackupLayer Copper(string name, LayerKey? drawn, bool ground) => new()
    {
        Kind = StackupKind.Conductor, Name = name, ThicknessDbu = Um(35), SigmaSm = 5.8e7,
        DrawingLayers = drawn is { } k ? [k] : [], IsGroundReference = ground,
    };

    /// <summary>Ground / 762 µm εᵣ 3.66 / top copper, with whatever <paramref name="above"/> says on top.</summary>
    private static Technology PatchTech(BoundaryCondition top, params StackupLayer[] above)
    {
        var layers = new List<StackupLayer>(above)
        {
            Copper("Top Copper", Top, ground: false),
            Dielectric("Substrate", 762, 3.66, 0.0037),
            Copper("Bottom Copper", null, ground: true),
        };
        return new Technology
        {
            Name = "cover-probe", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = Um(1), DefaultFlattenTolDbu = Um(1),
            Layers = [new LayerDef { Key = Top, Name = "Top Copper", ZOrder = 1, Purpose = "drawing" }],
            Stackup = new Stackup { Top = top, Bottom = BoundaryCondition.Ground, Layers = layers },
        };
    }

    private static List<LayoutShape> Line(LayerKey layer, double wM, double lenM) =>
    [
        new RectShape { Layer = layer, X1 = 0, Y1 = 0, X2 = M(lenM), Y2 = M(wM) },
        new LabelShape { Layer = layer, X = 0, Y = M(wM / 2), Text = "P1", Height = M(wM * 0.3), IsPort = true },
        new LabelShape { Layer = layer, X = M(lenM), Y = M(wM / 2), Text = "P2", Height = M(wM * 0.3), IsPort = true },
    ];

    /// <summary>
    /// <b>Gate 1 — an explicit εᵣ = 1 entry above a starter's top metal is the stack with none, bit for bit.</b>
    /// The free-space slab merges into the air termination rather than adding an interface, so the medium,
    /// every level height, the kernel choice and every sentence the run says are unchanged.
    /// </summary>
    [Fact]
    public void AnExplicitAirEntryAboveAStartersTopMetal_IsTheSameMediumBitForBit()
    {
        var bare = ShippedTechnologies.Load("pcb-2layer_RO4350B_20mil_1oz");
        var aired = ShippedTechnologies.Load("pcb-2layer_RO4350B_20mil_1oz");
        aired.Stackup.Layers.Insert(0, Dielectric("Air", 250, 1.0));

        var a = PlanarExtractor.Extract(Line(Top, 1.1e-3, 10e-3), bare,  Dbu, 2e9);
        var b = PlanarExtractor.Extract(Line(Top, 1.1e-3, 10e-3), aired, Dbu, 2e9);
        Assert.True(a.Ok, a.Refusal);
        Assert.True(b.Ok, b.Refusal);

        var sa = a.Problem!.EffectiveStack;
        var sb = b.Problem!.EffectiveStack;
        Assert.Equal(a.Problem.RequiresGeneralKernel, b.Problem.RequiresGeneralKernel);
        Assert.Equal(sa.LayerCount, sb.LayerCount);
        Assert.Equal(sa.Top, sb.Top);
        Assert.Equal(sa.Bottom, sb.Bottom);
        for (int i = 0; i < sa.LayerCount; i++)
            Assert.Equal(sa.Layers[i], sb.Layers[i]);
        Assert.Equal(a.Problem.LevelZ(0), b.Problem.LevelZ(0));
        Assert.Equal(a.Problem.Slab, b.Problem.Slab);
        Assert.Equal(a.Notes, b.Notes);
    }

    /// <summary>
    /// <b>A cover is in the medium, and the run says so as a note, not a warning.</b> The conductor band is
    /// absorbed into the cover (R-em-4a), so the region above the sheet is the copper's 35 µm plus the
    /// cover's 500 µm; the level stays on the substrate's top surface; and an open-microstrip closed form
    /// is not offered for a line under it.
    /// </summary>
    [Fact]
    public void ACoverAboveTheTopLevel_EntersTheMedium_AndIsReportedAsANote()
    {
        var tech = PatchTech(BoundaryCondition.Open, Dielectric("Radome", 500, 3.0));
        var r = PlanarExtractor.Extract(Line(Top, 1.7e-3, 10e-3), tech, Dbu, 6e9, null, ["MLIN"]);
        Assert.True(r.Ok, r.Refusal);

        var stack = r.Problem!.EffectiveStack;
        Assert.True(r.Problem.RequiresGeneralKernel);
        Assert.Equal(2, stack.LayerCount);
        Assert.Equal(762e-6, stack.Layers[0].ThicknessM, 12);
        Assert.Equal(535e-6, stack.Layers[1].ThicknessM, 12);
        Assert.Equal(3.0, stack.Layers[1].Material.EpsR);
        Assert.Equal(TerminationKind.HalfSpace, stack.Top.Kind);
        Assert.Equal(1.0, stack.Top.Material.EpsR);
        Assert.Equal(762e-6, r.Problem.LevelZ(0), 12);     // on the interface, not on the stack's top

        string note = Assert.Single(r.Notes, n => n.Contains("'Radome'", StringComparison.Ordinal));
        Assert.DoesNotContain(note, r.Warnings);
        Assert.Contains("535 µm", note, StringComparison.Ordinal);
        Assert.Empty(r.Problem.Alternatives);

        var bare = PlanarExtractor.Extract(Line(Top, 1.7e-3, 10e-3), PatchTech(BoundaryCondition.Open),
                                           Dbu, 6e9, null, ["MLIN"]);
        Assert.Single(bare.Problem!.Alternatives);
    }

    /// <summary>
    /// <b>The flipped stack is the same case mirrored.</b> A trace on the bottom copper returning through the
    /// top copper is solved upside down (RP-3), and a mask under the bottom copper is then above the level.
    /// </summary>
    [Fact]
    public void AMaskUnderTheBottomCopper_EntersTheFlippedMedium()
    {
        var tech = new Technology
        {
            Name = "flipped-probe", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = Um(1), DefaultFlattenTolDbu = Um(1),
            Layers =
            [
                new LayerDef { Key = Top, Name = "Top Copper", ZOrder = 1, Purpose = "drawing" },
                new LayerDef { Key = Bottom, Name = "Bottom Copper", ZOrder = 2, Purpose = "drawing" },
            ],
            Stackup = new Stackup
            {
                Top = BoundaryCondition.Open, Bottom = BoundaryCondition.Ground,
                Layers =
                [
                    Copper("Top Copper", Top, ground: true),
                    Dielectric("Substrate", 508, 3.66, 0.0037),
                    Copper("Bottom Copper", Bottom, ground: false),
                    Dielectric("Bottom Mask", 25, 3.5),
                ],
            },
        };

        var r = PlanarExtractor.Extract(Line(Bottom, 1.1e-3, 10e-3), tech, Dbu, 2e9);
        Assert.True(r.Ok, r.Refusal);
        Assert.True(r.ReturnPlane!.Flipped);

        var stack = r.Problem!.EffectiveStack;
        Assert.Equal(2, stack.LayerCount);
        Assert.Equal(3.5, stack.Layers[1].Material.EpsR);
        Assert.Contains(r.Notes, n => n.Contains("'Bottom Mask'", StringComparison.Ordinal));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("Bottom Mask", StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>A stack closed above keeps ending at the topmost level, and warns by name.</b> A lid and a ground
    /// plane above the levels are both a second return the planar medium cannot represent; a dielectric run
    /// up to (or through) one would describe neither structure.
    /// </summary>
    [Theory]
    [InlineData("lid")]
    [InlineData("plane")]
    public void AStackClosedAbove_KeepsTheStackAtTheTopLevel_AndWarns(string closure)
    {
        var tech = closure == "lid"
            ? PatchTech(BoundaryCondition.Ground, Dielectric("Radome", 500, 3.0))
            : PatchTech(BoundaryCondition.Open, Copper("Shield", null, ground: true), Dielectric("Radome", 500, 3.0));

        var r = PlanarExtractor.Extract(Line(Top, 1.7e-3, 10e-3), tech, Dbu, 6e9);
        Assert.True(r.Ok, r.Refusal);
        Assert.Equal(1, r.Problem!.EffectiveStack.LayerCount);

        string warning = Assert.Single(r.Warnings, w => w.Contains("'Radome'", StringComparison.Ordinal));
        Assert.Contains("not in this solve", warning, StringComparison.Ordinal);
        Assert.Contains(closure == "lid" ? "Stackup.Top = Ground" : "'Shield'", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Gate 2 — a covered line agrees with the cross-section kernel, and the cover is what moved it.</b>
    /// 50 Ω microstrip on the RO4350B 20 mil starter under a 25 µm, εᵣ 3.5 mask, at 2 GHz on the shipping
    /// mesh. The tolerance is L8PhaseGateTests' Gate 3 A-vs-B figure, 4 %, set there from L8d's measured
    /// metal-thickness sensitivity — applied here to ε_eff and Z₀ alike. Measured: uncovered +1.26 % / +2.45 %,
    /// covered +0.94 % / +2.64 %; the cover moves ε_eff by +5.4 % (A) and +5.0 % (B), past the tolerance, so
    /// this cannot pass on an uncovered answer.
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void Gate2_ACoveredLine_AgreesWithTheCrossSectionKernel()
    {
        const double Tolerance = 0.04;   // L8PhaseGateTests.Gate3_AAndB_AgreeOnAUniformLine_ThroughTheSameProductPath

        var bare = Solve(ShippedTechnologies.Load("pcb-2layer_RO4350B_20mil_1oz"), Top, 1.1e-3, 10e-3, 2e9, null);
        var tech = ShippedTechnologies.Load("pcb-2layer_RO4350B_20mil_1oz");
        tech.Stackup.Layers.Insert(0, Dielectric("Mask", 25, 3.5));
        var covered = Solve(tech, Top, 1.1e-3, 10e-3, 2e9, null);

        output.WriteLine($"uncovered: A ε_eff {bare.A.Eeff:F4} Z₀ {bare.A.Zc:F3} | B ε_eff {bare.B.Eeff:F4} Z₀ {bare.B.Zc:F3}");
        output.WriteLine($"covered:   A ε_eff {covered.A.Eeff:F4} Z₀ {covered.A.Zc:F3} | B ε_eff {covered.B.Eeff:F4} Z₀ {covered.B.Zc:F3}");

        Assert.InRange((covered.B.Eeff - covered.A.Eeff) / covered.A.Eeff, -Tolerance, Tolerance);
        Assert.InRange((covered.B.Zc   - covered.A.Zc)   / covered.A.Zc,   -Tolerance, Tolerance);
        Assert.True((covered.B.Eeff - bare.B.Eeff) / bare.B.Eeff > Tolerance,
                    $"the cover moved the planar ε_eff by only {(covered.B.Eeff / bare.B.Eeff - 1) * 100:F2} %");
        Assert.True((covered.A.Eeff - bare.A.Eeff) / bare.A.Eeff > Tolerance);

        // Z_c's C_pul is solved in the whole medium, cover included, and the run says that rather than the
        // one-slab image series' "neglects".
        Assert.Contains(covered.BNotes, n => n.Contains("solved in the whole layered medium", StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>Gate 4 — an MMIC line under passivation.</b> The GaAs starter with a 1 µm, εᵣ 7 nitride over Metal2,
    /// a 20 µm Metal2 line at 10 GHz: the run solves, the note names the layer, and Z₀ falls. Measured
    /// 138.3 → 118.3 Ω (−14.5 %). The note reports 4 µm: Metal2's own 3 µm band is absorbed into the cover
    /// above it (R-em-4a), as the cross-section kernel absorbs it too.
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void Gate4_AnMmicLineUnderNitride_Solves_AndItsImpedanceFalls()
    {
        var bare = Solve(ShippedTechnologies.Load("mmic-GaAs_2LM_100um"), Bottom, 20e-6, 400e-6, 10e9, ["Metal2"], planarOnly: true);
        var tech = ShippedTechnologies.Load("mmic-GaAs_2LM_100um");
        tech.Stackup.Layers.Insert(0, Dielectric("Nitride Passivation", 1, 7.0));
        var covered = Solve(tech, Bottom, 20e-6, 400e-6, 10e9, ["Metal2"], planarOnly: true);

        output.WriteLine($"Metal2 20 µm at 10 GHz: Z₀ {bare.B.Zc:F2} → {covered.B.Zc:F2} Ω, ε_eff {bare.B.Eeff:F4} → {covered.B.Eeff:F4}");
        Assert.Contains(covered.BNotes, n => n.Contains("'Nitride Passivation'", StringComparison.Ordinal));
        Assert.InRange(covered.B.Zc / bare.B.Zc, 0.70, 0.95);
    }

    private sealed record Line2(double Eeff, double Zc);
    private sealed record Pair(Line2 A, Line2 B, IReadOnlyList<string> BNotes);

    private static Pair Solve(Technology tech, LayerKey layer, double wM, double lenM, double fHz,
                              string[]? levels, bool planarOnly = false)
    {
        Line2 Read(EmRunResult r, string group)
        {
            var cubes = r.Data!.CubesIn(group);
            var zc = cubes["Zc"];
            return new Line2(cubes["Eeff"].RealValues[0],
                             zc.DataKind == RfCore.Data.DataKind.Complex ? zc.ComplexValues[0].Real : zc.RealValues[0]);
        }

        EmRunResult Run(EmAnalysisKind kind)
        {
            var view = new LayoutView { DbuPerMicron = Dbu };
            foreach (var s in kind == EmAnalysisKind.Planar ? Line(layer, wM, lenM) : Line(layer, wM, lenM).Take(1))
                view.Shapes.Add(s);
            string f = (fHz / 1e9).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            var setup = new EmSetup
            {
                Name = "psv1", LayoutRef = "layout.clay", AnalysisKind = kind,
                Frequency = new Core.Design.FrequencySpec(f, f, 1, Core.Design.SweepKind.Linear, "GHz", "GHz"),
                PlanarMesh = PlanarMeshSettings.Default,
                AnalysisLevelNames = levels is null ? [] : [.. levels],
            };
            string dir = Directory.CreateTempSubdirectory("crf-psv1-").FullName;
            try
            {
                var r = EmRunService.Run(setup, new EmLayoutSource("layout.clay", view, tech, Dbu), dir);
                Assert.True(r.Status == EmRunStatus.Ok, r.Error);
                return r;
            }
            finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
        }

        var b = Run(EmAnalysisKind.Planar);
        var a = planarOnly ? null : Run(EmAnalysisKind.CrossSection);
        return new Pair(a is null ? new Line2(double.NaN, double.NaN) : Read(a, "tline"),
                        Read(b, PlanarKernel.DiagnosticsGroup), [.. b.Notes ?? [], .. b.Warnings]);
    }
}
