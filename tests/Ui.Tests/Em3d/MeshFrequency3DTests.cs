using CircuitRF.Core.Design;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

// A driven 3D setup's mesh, sized at a frequency it states rather than the sweep's top: a power amplifier's fundamental dominates
// and its fifth harmonic only has to be reasonable. Palace's script and EMerge's both size from GmshGeoWriter.SizingFrequencyHz.
public sealed class MeshFrequency3DTests
{
    private const int Dbu = 1000;

    private static long Um(double v) => (long)Math.Round(v * Dbu);

    private static (EmSetup Setup, EmLayoutSource Source) Board(double? meshGHz)
    {
        var tech = ShippedTechnologies.Load("pcb-4layer_FR-4_62mil_1oz");
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new RectShape { Layer = new LayerKey(11, 0), X1 = 0, Y1 = 0, X2 = Um(10000), Y2 = Um(5000) });
        view.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = Um(2200), X2 = Um(10000), Y2 = Um(2800) });
        view.Shapes.Add(new RectShape { Layer = new LayerKey(2, 0), X1 = 0, Y1 = 0, X2 = Um(10000), Y2 = Um(5000) });
        foreach (var (text, x, direction) in new[] { ("P1", 0.0, LayoutRotation.R0), ("P2", 10000.0, LayoutRotation.R180) })
            view.Shapes.Add(new LabelShape
            {
                Layer = new LayerKey(1, 0), PortLayer = new LayerKey(1, 0), X = Um(x), Y = Um(2500), Text = text, Height = Um(100), IsPort = true,
                PortKind = CircuitRF.Engine.Mom.PlanarPortKind.Edge, PortDirection = direction,
            });
        var setup = new EmSetup
        {
            Name = "pa", LayoutRef = "pa/layout/pa.clay", Solver3D = Em3dSolverId.Palace, MeshFrequencyGHz = meshGHz,
            Frequency = new FrequencySpec("1", "12.5", 24, SweepKind.Linear, "GHz", "GHz"),
        };
        return (setup, new EmLayoutSource("/nowhere/pa.clay", view, tech, Dbu));
    }

    private static Em3dGenerationResult Generate(double? meshGHz)
    {
        var (setup, source) = Board(meshGHz);
        var g = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(g.Ok, g.Refusal);
        return g;
    }

    /// <summary>The largest element and the estimate follow the stated frequency, the sweep does not, and the run says so.</summary>
    [Fact]
    public void TheMeshIsSizedAtTheStatedFrequency_AndTheSweepStays()
    {
        var top = Generate(null);
        var low = Generate(2.5);

        Assert.Equal(12.5e9, GmshGeoWriter.SizingFrequencyHz(top.Problem!));
        Assert.Equal(2.5e9, GmshGeoWriter.SizingFrequencyHz(low.Problem!));
        Assert.Equal(top.Problem!.Frequency.StopHz, low.Problem!.Frequency.StopHz);
        var settings = PalaceSettings.Resolve(null);
        Assert.Equal(5, GmshGeoWriter.SmallestRequestedSizeM(low.Problem, settings) / GmshGeoWriter.SmallestRequestedSizeM(top.Problem, settings), 6);
        Assert.True(Em3dRunService.EstimatePalace(low.Problem, settings)!.Tetrahedra < Em3dRunService.EstimatePalace(top.Problem, settings)!.Tetrahedra);
        Assert.DoesNotContain(top.Notes, n => n.StartsWith("The mesh is sized at", StringComparison.Ordinal));
        Assert.Contains(low.Notes, n => n.StartsWith("The mesh is sized at 2.5 GHz", StringComparison.Ordinal) && n.Contains("5 × as long", StringComparison.Ordinal));
    }

    /// <summary>A static or eigenmode problem has its own rule, whatever the field holds.</summary>
    [Fact]
    public void OnlyADrivenProblemReadsIt()
    {
        var (setup, source) = Board(2.5);
        setup.Problem3D = Em3dProblemType.Eigenmode;
        setup.Eigenmode = new EmEigenmode3D { Count = 1, TargetGHz = 4 };
        var g = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(g.Ok, g.Refusal);
        Assert.Null(g.Problem!.MeshFrequencyHz);
        Assert.Equal(8e9, GmshGeoWriter.SizingFrequencyHz(g.Problem));
    }

    /// <summary>It persists in a .cem and in a .c3d's embedded setup, and a setup without it writes the same bytes as before.</summary>
    [Fact]
    public void ItPersistsInACemAndAC3d_AndIsOmittedWhenBlank()
    {
        var (setup, _) = Board(2.5);
        string cem = EmSetupPersistence.Serialize(setup);
        Assert.Contains("\"MeshFrequencyGHz\": 2.5", cem, StringComparison.Ordinal);
        Assert.Equal(2.5, EmSetupPersistence.Deserialize(cem).MeshFrequencyGHz);
        Assert.Equal(2.5, setup.Clone().MeshFrequencyGHz);

        var (blank, _) = Board(null);
        Assert.DoesNotContain("MeshFrequencyGHz", EmSetupPersistence.Serialize(blank), StringComparison.Ordinal);

        var doc = new C3dDocument();
        doc.Setups.Add(EmSetupPersistence.ToEmbedded(setup));
        var back = C3dPersistence.Deserialize(C3dPersistence.Serialize(doc));
        var (read, why) = C3dSetups.Select(back, setup.Name);
        Assert.True(read is not null, why);
        Assert.Equal(2.5, read!.MeshFrequencyGHz);
    }

    /// <summary>The grey text is the sweep's top in the box's unit, and follows the sweep: its value, and its unit, as they are edited. A
    /// stated value keeps its frequency when the unit changes.</summary>
    [Fact]
    public void TheGreyTextIsTheSweepsTop_AndFollowsTheSweep()
    {
        var (setup, source) = Board(null);
        var vm = new CircuitRF.Ui.Layout.Em.EmSetupEditorViewModel("/nowhere/pa.cem", setup) { ResolveLayout = _ => source };
        vm.Refresh();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.Frequency.StopCoeff = "10";
        Assert.Equal("10", vm.MeshFrequency3DPlaceholder);
        Assert.Contains(nameof(vm.MeshFrequency3DPlaceholder), raised);

        vm.MeshFrequency3DText = "2.5";
        vm.CommitMeshFrequency3D();
        vm.Frequency.StopUnit = "MHz";
        Assert.Equal("MHz", vm.MeshFrequency3DUnit);
        Assert.Equal("2500", vm.MeshFrequency3DText);
        Assert.Equal(2.5, vm.Working.MeshFrequencyGHz);

        vm.MeshFrequency3DText = "2450";
        vm.CommitMeshFrequency3D();
        Assert.Equal(2.45, vm.Working.MeshFrequencyGHz);
    }

    /// <summary>Segments in different units: the box takes the unit of the segment holding the top. A segment that does not parse yet,
    /// a blank one included, throws nothing and leaves a stated value alone, and the box comes back when the sweep does.</summary>
    [Fact]
    public void ASweepOfSegmentsInDifferentUnits_ReadsAtTheTopSegmentsUnit_AndSurvivesATypo()
    {
        var (setup, source) = Board(null);
        setup.Frequency = new FrequencySpec("1", "2", 3, SweepKind.Linear, "GHz", "GHz");
        setup.AdditionalFrequencies = [new FrequencySpec("3000", "9000", 3, SweepKind.Linear, "MHz", "MHz")];
        var vm = new CircuitRF.Ui.Layout.Em.EmSetupEditorViewModel("/nowhere/pa.cem", setup) { ResolveLayout = _ => source };
        vm.Refresh();

        Assert.Equal("MHz", vm.MeshFrequency3DUnit);
        Assert.Equal("9000", vm.MeshFrequency3DPlaceholder);

        vm.MeshFrequency3DText = "2450";
        vm.CommitMeshFrequency3D();
        Assert.Equal(2.45, vm.Working.MeshFrequencyGHz);

        // The first segment becomes the top: same frequency, now read in GHz.
        vm.Frequency.StopCoeff = "20";
        Assert.Equal("GHz", vm.MeshFrequency3DUnit);
        Assert.Equal("20", vm.MeshFrequency3DPlaceholder);
        Assert.Equal("2.45", vm.MeshFrequency3DText);

        // Mid-typing, in either segment.
        foreach (string typed in new[] { "", "abc", "1e", "-", "1/0" })
        {
            vm.FrequencySegments[1].StopCoeff = typed;
            _ = vm.MeshFrequency3DPlaceholder;
            Assert.Equal(2.45, vm.Working.MeshFrequencyGHz);
            Assert.Equal("2.45", vm.MeshFrequency3DText);
            vm.Frequency.StopCoeff = typed;
            _ = vm.MeshFrequency3DPlaceholder;
            Assert.Equal(2.45, vm.Working.MeshFrequencyGHz);
            vm.Frequency.StopCoeff = "20";
        }
        vm.FrequencySegments[1].StopCoeff = "9000";
        Assert.Equal("20", vm.MeshFrequency3DPlaceholder);

        // Taking the top segment away hands the box to the one that is left.
        vm.Frequency.RemoveCommand.Execute(null);
        Assert.Equal("MHz", vm.MeshFrequency3DUnit);
        Assert.Equal("9000", vm.MeshFrequency3DPlaceholder);
        Assert.Equal("2450", vm.MeshFrequency3DText);
    }

    /// <summary>The panel takes it in one undo entry, clears it when blank, refuses nonsense, and shows it only where it is read.</summary>
    [Fact]
    public void ThePanelCommitsIt_RefusesNonsense_AndHidesItWhereItIsNotRead()
    {
        var (setup, source) = Board(null);
        var vm = new CircuitRF.Ui.Layout.Em.EmSetupEditorViewModel("/nowhere/pa.cem", setup) { ResolveLayout = _ => source };
        vm.Refresh();
        Assert.True(vm.ShowMeshFrequency3D);
        Assert.Equal("12.5", vm.MeshFrequency3DPlaceholder);

        vm.MeshFrequency3DText = "-1";
        vm.CommitMeshFrequency3D();
        Assert.NotNull(vm.MeshFrequency3DError);
        Assert.Null(vm.Working.MeshFrequencyGHz);

        vm.MeshFrequency3DText = "2.5";
        vm.CommitMeshFrequency3D();
        Assert.Null(vm.MeshFrequency3DError);
        Assert.Equal(2.5, vm.Working.MeshFrequencyGHz);
        vm.UndoRedo.Undo();
        Assert.Null(vm.Working.MeshFrequencyGHz);
        Assert.Equal("", vm.MeshFrequency3DText);

        vm.Solver3DChoice = vm.Solver3DChoiceList.First(c => c.Value == CircuitRF.Design.Em3d.OpenEmsSolver.Id);
        Assert.False(vm.ShowMeshFrequency3D);
    }
}
