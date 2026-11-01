// brief-artsch-8-gui-command.md R-as8-6 — the unknown-ceiling refusal points at Create Schematic from Artwork, and no
// other refusal does. Both through EmRunService.Run, the function Simulate and `circuitrf em` call, on the committed
// port-calibration fixtures: one meshed past the ceiling on purpose (the refusal is taken before any fill, so it is
// cheap), one refused for its port clearance.

using System;
using System.IO;
using CircuitRF.Core.Design;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Design.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.Em;

public sealed class EmRunServiceTests : IDisposable
{
    private readonly string _results = Path.Combine(Path.GetTempPath(), $"crf-as8-em-{Guid.NewGuid():N}", "results");

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_results)!, true); } catch { /* best effort */ }
    }

    private static string Portcal()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return Path.Combine(dir, "testdata", "portcal");
    }

    private static (EmSetup Setup, EmLayoutSource Source) Fixture(string name)
    {
        string cem = Path.Combine(Portcal(), name, "em", name + ".cem");
        var setup = EmSetupPersistence.LoadFromFile(cem).Clone();
        setup.Frequency = new FrequencySpec("1", "1", 1, SweepKind.Linear, "GHz", "GHz");
        var r = EmSetupResolver.Resolve(cem, setup.LayoutRef, Path.Combine(Portcal(), ".cws"), new TechnologyCache());
        return (setup, r.Source!);
    }

    [Fact]
    public void ARunDeclinedPastTheMemoryLimit_PointsAtCreateSchematicFromArtwork_AndAPortRefusalDoesNot()
    {
        // No memory limit refuses at the mesh any more: past the machine's a run is asked about, and with no one
        // to say yes it is declined — the outcome a board too large for EM now meets, so the pointer rides on it.
        var (setup, source) = Fixture("separated-pair");
        setup.PlanarMesh = new PlanarMeshSettings(Auto: false, CellsPerWavelength: 400, EdgeMesh: true, EdgeCells: 3,
                                                  BoundaryCells: PlanarBoundaryCells.Staircase, MeshFrequencyHz: 60e9);
        var ceiling = EmRunService.Run(setup, source, _results,
                                       memoryCeilingBytes: PlanarSystem.ResidentBytes(SurfaceMesher.UnknownCeiling));

        Assert.Equal(EmRunStatus.Refused, ceiling.Status);
        Assert.Equal("em.refused.memory", ceiling.Diagnostic!.Id);
        Assert.EndsWith(EmRunService.WithoutEmPointer, ceiling.Error);
        Assert.Contains("Design ▸ Create Schematic from Artwork", ceiling.Error);

        var (other, otherSource) = Fixture("offset-pair");
        var clearance = EmRunService.Run(other, otherSource, _results);
        Assert.Equal(EmRunStatus.Refused, clearance.Status);
        Assert.Equal("em.refused.port-clearance", clearance.Diagnostic!.Id);
        Assert.DoesNotContain("Create Schematic from Artwork", clearance.Error);
    }

    /// <summary>
    /// CAP-1 and its follow-up — with no memory limit of its own, a setup's ceiling is sized on the machine,
    /// with a de-embedded run's calibration standards counted, and the run, the pre-solve report (`circuitrf
    /// check`) and the panel's mesh preview all ask the one function. Past the machine's memory nothing is
    /// refused: the report warns and the run ASKS (Simulate Anyway), and with no one to say yes it is declined
    /// naming `--force`. A tiny machine limit keeps it cheap: the run is asked before anything is filled.
    /// </summary>
    [Fact]
    public void PastAMachineLimit_TheRunAsks_AndThePreflightAndThePanelWarnAlike()
    {
        var (setup, source) = Fixture("separated-pair");
        setup.AcceleratedSolve = false;
        const long Machine = 100_000;

        Em3dMemoryVerdict? asked = null;
        var run = EmRunService.Run(setup, source, _results, confirmMemory: v => { asked = v; return false; },
                                   memoryCeilingBytes: Machine);
        Assert.Equal(EmRunStatus.Refused, run.Status);
        Assert.Equal("em.refused.memory", run.Diagnostic!.Id);
        Assert.Equal(Em3dMemoryLevel.Severe, asked!.Level);
        Assert.Contains("calibration standards", asked.Text, StringComparison.Ordinal);
        Assert.Contains("--force", run.Error, StringComparison.Ordinal);

        var pre = EmRunService.Preflight(setup, source, memoryCeilingBytes: Machine);
        var preMesh = Assert.IsType<PlanarMeshReport>(pre.PlanarMesh);
        Assert.True(preMesh.CanSolve);
        Assert.True(preMesh.PastMachineLimit);
        Assert.True(preMesh.StandardResidentBytes > 0);
        Assert.Null(pre.Refusal);

        var vm = new EmSetupEditorViewModel(Path.Combine(Portcal(), "separated-pair", "em", "separated-pair.cem"), setup)
        {
            ResolveLayout           = _ => source,
            MachineMemoryLimitBytes = () => Machine,
            PhysicalMemoryBytes     = () => 0,
        };
        vm.Refresh();
        vm.BuildPlanarMesh();
        var panel = Assert.IsType<PlanarMeshReport>(vm.PlanarMeshReport);
        Assert.True(panel.CanSolve);
        Assert.Equal(preMesh.CeilingUnknowns, panel.CeilingUnknowns);
        Assert.Equal(preMesh.RunResidentBytes, panel.RunResidentBytes);
        Assert.Equal(preMesh.AllFindings.Where(f => f.IsWarning).Select(f => f.Text),
                     panel.AllFindings.Where(f => f.IsWarning).Select(f => f.Text));

        // An EM setup has no memory limit of its own: bytes left on its mesh in code are replaced by the machine's.
        var own = setup.Clone();
        own.PlanarMesh = own.PlanarMesh with { MemoryLimitBytes = 50_000_000_000 };
        var ownPre = EmRunService.Preflight(own, source, memoryCeilingBytes: Machine);
        Assert.True(ownPre.PlanarMesh!.CanSolve);
        Assert.True(ownPre.PlanarMesh!.PastMachineLimit);
        Assert.Equal(preMesh.CeilingUnknowns, ownPre.PlanarMesh!.CeilingUnknowns);
    }
}
