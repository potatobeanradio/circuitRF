using CircuitRF.Core.Design;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Em3d;
using CircuitRF.Ui.Layout.Em;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-dfr34-2 — the air box a 3D view starts with. A radiator pads for its pattern's frequency (R-1) and is offered a
//  sweep round it (R-2); the floor's H is measured from the bottom metal (R-3); a 95 % bottom pour is the floor whether or
//  not the technology marks it ground (R-4). R-5 (the panel) is EditorRound1TreeTests.AFacesPadding_ReadsAsALengthWithItsBasis.
// ══════════════════════════════════════════════════════════════════════════════════════════════

public sealed class AirBoxDefaultsDfr34Tests
{
    private const double C0 = 299_792_458;

    /// <summary>The field case's antenna: a 44 × 95 × 1.5 mm board.</summary>
    private static readonly (double, double, double, double, double, double) Antenna = (0, 0, 44e-3, 95e-3, 0, 1.5e-3);

    private static EmSetup Radiator(FrequencySpec sweep, double? patternGHz) => new()
    {
        Name = "ant", LayoutRef = "ant.clay", Solver3D = Em3dSolverId.Palace, RadiationPattern = true,
        Frequency = sweep, PatternFrequencyGHz = patternGHz,
    };

    private static double[] Pads(Em3dAirBox b)
    {
        var (x0, y0, x1, y1, z0, z1) = Antenna;
        return [x0 - b.Min.X, b.Max.X - x1, y0 - b.Min.Y, b.Max.Y - y1, z0 - b.Min.Z, b.Max.Z - z1];
    }

    /// <summary>R-dfr34-2-1 — a pattern at 2.45 GHz, on the sweep R-2 offers round it: every face λ/4 at 2.45 GHz (30.6 mm), not
    /// 150 mm. With no pattern frequency on the 0.5 GHz default, λ/4 there (150 mm) is capped at the board's 95 mm, and the
    /// note says so.</summary>
    [Fact]
    public void ARadiator_PadsForItsPatternFrequency_AndTheSweepsLowestIsCappedByTheStructure()
    {
        var setup = Radiator(EmSetup.SweepAround(2.45), 2.45);
        double fMin = setup.ExpandFrequencies().Where(f => f > 0).Min();
        List<string> notes = [];
        var box = Em3dGenerator.PaddedAirBox(setup, Antenna, fMin, null, [], notes, out var refusal)!;
        Assert.Null(refusal);
        Assert.All(Pads(box), p => Assert.Equal(C0 / 4 / 2.45e9, p, 1e-9));

        var unstated = Radiator(EmSetup.NewSetupFrequency(), null);
        notes.Clear();
        box = Em3dGenerator.PaddedAirBox(unstated, Antenna, 0.5e9, null, [], notes, out refusal)!;
        Assert.All(Pads(box), p => Assert.Equal(95e-3, p, 1e-9));
        Assert.Contains(notes, n => n.Contains("caps λ/4 at 500 MHz", StringComparison.Ordinal));
    }

    /// <summary>R-dfr34-2-2 — typing the pattern frequency moves an untouched 0.5-8 GHz sweep round it (2.45 GHz on the grid),
    /// in one undo entry; a sweep someone chose stays.</summary>
    [Fact]
    public void TypingThePatternFrequency_RecentresOnlyTheUntouchedDefaultSweep()
    {
        var around = EmSetup.SweepAround(2.45);
        Assert.Equal(("1.23", "3.67", "0.02"), (around.StartExpr, around.StopExpr, around.StepExpr));

        string dir = Path.Combine(Path.GetTempPath(), "crf-dfr34-2-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            EmSetupEditorViewModel Editor(FrequencySpec sweep)
            {
                var setup = Radiator(sweep, null);
                string path = Path.Combine(dir, Guid.NewGuid().ToString("N")[..6] + ".cem");
                EmSetupPersistence.SaveToFile(path, setup);
                return new EmSetupEditorViewModel(path, setup);
            }
            var vm = Editor(EmSetup.NewSetupFrequency());
            vm.PatternFrequencyText = "2.45";
            vm.CommitPatternFrequency();
            Assert.Equal(2.45, vm.Working.PatternFrequencyGHz);
            Assert.Equal("1.23", vm.Working.Frequency.StartExpr);
            Assert.Equal("1.23", vm.Frequency.Build().StartExpr);
            vm.UndoRedo.Undo();
            Assert.Null(vm.Working.PatternFrequencyGHz);
            Assert.True(EmSetup.IsNewSetupFrequency(vm.Working.Frequency));

            var chosen = Editor(new FrequencySpec("2", "3", "0.1", SweepKind.Linear, "GHz", "GHz", "GHz"));
            chosen.PatternFrequencyText = "2.45";
            chosen.CommitPatternFrequency();
            Assert.Equal("2", chosen.Working.Frequency.StartExpr);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    /// <summary>R-dfr34-2-3 — a 10-metal stack whose bottom dielectric is half the top's: the floor pads 10 × the BOTTOM H, the
    /// sides 20 × the top's.</summary>
    [Fact]
    public void TheFloorsH_IsMeasuredFromTheBottomMetalUpward()
    {
        const double t = 35e-6, hTop = 200e-6, hMid = 150e-6, hBottom = 100e-6;
        List<(double, double)> metal = [], diel = [];
        double z = 0;
        for (int k = 0; k < 10; k++)
        {
            metal.Add((z, z + t));
            z += t;
            if (k == 9) break;
            double h = k == 0 ? hBottom : k == 8 ? hTop : hMid;
            diel.Add((z, z + h));
            z += h;
        }
        double? top = Em3dGenerator.PaddingHeight(metal, diel, null), below = Em3dGenerator.PaddingHeightBelow(metal, diel);
        Assert.Equal(hTop, top!.Value, 1e-12);
        Assert.Equal(hBottom, below!.Value, 1e-12);

        var setup = new EmSetup { Name = "s", LayoutRef = "s.clay", Solver3D = Em3dSolverId.Palace };
        var d = Em3dGenerator.DefaultFacePadding(setup, 1e9, 0.1, top, below);
        Assert.Equal(10 * hBottom, d.For("zmin", 0), 1e-12);
        Assert.Equal(20 * hTop, d.For("xmin", 0), 1e-12);
        Assert.Contains("over the bottom metal", d.Basis("zmin", 0), StringComparison.Ordinal);
    }

    /// <summary>R-dfr34-2-4 — a bottom pour the technology does not mark ground-reference is still the floor when it covers the
    /// footprint, and the note says it was treated as one.</summary>
    [Fact]
    public void AnUnmarkedBottomPour_IsTheFloor_AndTheNoteSaysSo()
    {
        var (setup, source) = Em3dGeneratorTests.Microstrip();
        var bottom = source.Technology!.Stackup.Layers.Last(l => l.Kind == StackupKind.Conductor);
        bottom.IsGroundReference = false;
        source.View.Shapes.Add(new RectShape { Layer = bottom.DrawingLayers[0], X1 = 0, Y1 = -3_000_000, X2 = 10_000_000, Y2 = 3_000_000 });
        var r = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(r.Ok, r.Refusal);
        Assert.Equal(Em3dBoundaryKind.Pec, r.Problem!.Boundary.Faces.ZMin);
        Assert.Contains(r.Notes, n => n.Contains("treated as a floor", StringComparison.Ordinal));
    }
}
