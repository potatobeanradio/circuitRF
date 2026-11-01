// The planar solver's memory limit, Ui and run-service side: Settings ▸ Solvers is the one memory limit (an EM setup
// carries none, and a .cem that still names one opens with it ignored), the panel sizes on the machine, and the check
// Simulate makes before a run fills anything. The engine half (what count a limit allows, the mesh verdict, the
// guards) is tests/Engine.Tests' PlanarMemoryLimitTests.

using CircuitRF.Design.Em3d;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;

namespace CircuitRF.Ui.Tests.Em;

public class PlanarMemoryLimitUiTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    [Fact]
    public void ACemThatStillNamesAMemoryLimit_OpensWithItIgnored_AndSavesWithoutIt()
    {
        var setup = new EmSetup { Name = "hero", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar };
        setup.PlanarMesh = setup.PlanarMesh with { Auto = false, CellsPerWavelength = 30 };
        string current = EmSetupPersistence.Serialize(setup);
        Assert.DoesNotContain("MemoryLimitGb", current, StringComparison.Ordinal);

        // A file written while a setup could carry its own limit: the key inside the planar mesh block.
        string legacy = current.Replace("\"CellsPerWavelength\": 30", "\"CellsPerWavelength\": 30,\n    \"MemoryLimitGb\": 12.5",
                                        StringComparison.Ordinal);
        Assert.Contains("\"MemoryLimitGb\": 12.5", legacy, StringComparison.Ordinal);

        var read = EmSetupPersistence.Deserialize(legacy);
        Assert.Null(read.PlanarMesh.MemoryLimitBytes);
        Assert.Equal(30, read.PlanarMesh.CellsPerWavelength);
        Assert.Equal(current, EmSetupPersistence.Serialize(read));

        // Even a setup built in code with bytes on its mesh writes no limit: there is no key to write it to.
        setup.PlanarMesh = setup.PlanarMesh with { MemoryLimitBytes = 12_500_000_000 };
        Assert.DoesNotContain("MemoryLimitGb", EmSetupPersistence.Serialize(setup), StringComparison.Ordinal);
    }

    [Fact]
    public void ThePanel_HasNoMemoryLimitOfItsOwn_AndSizesOnTheMachine()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-memlim-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "panel.cem");
        var setup = new EmSetup { Name = "panel", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar };
        EmSetupPersistence.SaveToFile(path, setup);
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new RectShape { Layer = new(1, 0), X1 = 0, Y1 = 0, X2 = 20_000_000, Y2 = 2_900_000 });
        const long Machine = 6_000_000_000;
        var vm = new EmSetupEditorViewModel(path, setup)
        {
            ResolveLayout           = _ => new EmLayoutSource(Path.Combine(dir, "a.clay"), view, StarterTechnologies.Pcb2Layer(), Dbu),
            MachineMemoryLimitBytes = () => Machine,
            PhysicalMemoryBytes     = () => 16_000_000_000,
        };
        vm.Refresh();

        var names = typeof(EmSetupEditorViewModel).GetProperties().Select(p => p.Name).ToHashSet();
        Assert.DoesNotContain("PlanarMemoryLimitText", names);
        Assert.DoesNotContain("MemoryLimitError", names);
        Assert.DoesNotContain("MemoryLimitPlaceholder", names);
        Assert.Equal(SurfaceMesher.CeilingFor(false, Machine, 0), vm.DenseUnknownCeiling);

        // Bytes left on a setup's mesh from code are not a limit: the machine's replaces them.
        vm.Working.PlanarMesh = vm.Working.PlanarMesh with { MemoryLimitBytes = 1_000_000_000 };
        Assert.Equal(SurfaceMesher.CeilingFor(false, Machine, 0), vm.DenseUnknownCeiling);
    }

    [Fact]
    public void TheCheck_WarnsPast75Percent_AndAsksPastTheMachine()
    {
        const long Machine = 16_000_000_000;
        static PlanarRunSize Dense(long bytes) => new(20_000, 10_000, false, bytes);

        Assert.Null(PlanarMemoryCheck.Evaluate(Dense(10_000_000_000), Machine).Text);
        Assert.Equal(Em3dMemoryLevel.Warning, PlanarMemoryCheck.Evaluate(Dense(13_000_000_000), Machine).Level);
        Assert.Equal(Em3dMemoryLevel.Severe,  PlanarMemoryCheck.Evaluate(Dense(17_000_000_000), Machine).Level);
        // The accelerated path has no closed-form estimate, so it is never asked about.
        Assert.Null(PlanarMemoryCheck.Evaluate(new PlanarRunSize(20_000, 10_000, true, null), Machine).Text);
    }

    [Fact]
    public void PastTheMachine_TheRunIsAsked_AndStartsOnlyOnYes()
    {
        var size = new PlanarRunSize(30_000, 15_000, false, 20_000_000_000);
        const long Machine = 16_000_000_000;

        var warnings = new List<string>();
        string? declined = PlanarMemoryCheck.Admitter(_ => false, warnings, Machine)(size);
        Assert.Contains("It was not started", declined, StringComparison.Ordinal);

        declined = PlanarMemoryCheck.Admitter(null, warnings, Machine)(size);   // headless, no --force
        Assert.Contains("--force", declined, StringComparison.Ordinal);

        Assert.Null(PlanarMemoryCheck.Admitter(_ => true, warnings, Machine)(size));
        Assert.Contains(warnings, w => w.Contains("started anyway, as confirmed", StringComparison.Ordinal));
    }

    [Fact]
    public void TheMachineCeiling_IsTheMachineLimit_ThenPhysicalMemory_AndNeverTheSetups()
    {
        const long Physical = 16_000_000_000, MachineLimit = 12_000_000_000, Own = 7_000_000_000;
        var blank = PlanarMeshSettings.Default;

        var own = PlanarMemoryCheck.WithMachineCeiling(blank with { MemoryLimitBytes = Own }, MachineLimit, Physical);
        Assert.Equal(MachineLimit, own.MemoryLimitBytes);
        Assert.Equal(PlanarMemoryLimitSource.MachineLimit, own.MemoryLimitSource);

        var machine = PlanarMemoryCheck.WithMachineCeiling(blank, MachineLimit, Physical);
        Assert.Equal(MachineLimit, machine.MemoryLimitBytes);
        Assert.Equal(PlanarMemoryLimitSource.MachineLimit, machine.MemoryLimitSource);

        var physical = PlanarMemoryCheck.WithMachineCeiling(blank, null, Physical);
        Assert.Equal(Physical, physical.MemoryLimitBytes);
        Assert.Equal(PlanarMemoryLimitSource.PhysicalMemory, physical.MemoryLimitSource);

        // Memory that could not be read leaves the shipped ceiling, and nothing here enters a hash.
        Assert.Null(PlanarMemoryCheck.WithMachineCeiling(blank, null, 0).MemoryLimitBytes);
        Assert.Equal(EmSnpProvenance.MeshHash(blank), EmSnpProvenance.MeshHash(physical));
    }

    [Fact]
    public void TheCheck_CountsTheCalibrationStandards_AndAsksAnAcceleratedRunPastTheMachinesCeiling()
    {
        const long Machine = 16_000_000_000;
        var deembedded = new PlanarRunSize(20_000, 10_000, false, 17_000_000_000) { StandardBytes = 5_000_000_000 };
        var verdict = PlanarMemoryCheck.Evaluate(deembedded, Machine);
        Assert.Equal(Em3dMemoryLevel.Severe, verdict.Level);
        Assert.Contains("calibration standards solved beside the structure", verdict.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("before de-embedding", verdict.Text, StringComparison.Ordinal);

        // An accelerated run has no dense figure; past the machine's unknown ceiling it is still asked.
        var accelerated = new PlanarRunSize(40_000, 20_000, true, null) { PastMachineCeiling = true, CeilingUnknowns = 34_000 };
        Assert.Equal(Em3dMemoryLevel.Severe, PlanarMemoryCheck.Evaluate(accelerated, Machine).Level);
        Assert.Null(PlanarMemoryCheck.Evaluate(accelerated with { PastMachineCeiling = false }, Machine).Text);
    }
}
