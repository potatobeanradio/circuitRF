// The planar solver's memory limit: the run service hands the machine's memory (Settings ▸ Solvers, or physical
// memory) down as how much one frequency point may hold, in place of the shipped unknown ceilings, and a run
// asks its caller about its size before it fills. Every such limit is ADVISORY — past it the mesh warns and the
// run asks; only the fixed ceilings, with no limit at all, refuse. An EM setup carries no limit of its own.

using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using Xunit;

namespace CircuitRF.Engine.Tests.Mom;

public sealed class PlanarMemoryLimitTests
{
    [Fact]
    public void CeilingFor_WithNoLimit_IsTheShippedCeiling_AndWithOne_IsTheLargestCountThatFits()
    {
        Assert.Equal(SurfaceMesher.UnknownCeiling, SurfaceMesher.CeilingFor(false, null));
        Assert.Equal(SurfaceMesher.AcceleratedUnknownCeiling, SurfaceMesher.CeilingFor(true, null));

        const long Limit = 6_000_000_000;
        int n = SurfaceMesher.CeilingFor(false, Limit, cellCount: 9_000);
        Assert.True(PlanarSystem.ResidentBytes(n, 9_000) <= Limit);
        Assert.True(PlanarSystem.ResidentBytes(n + 1, 9_000) > Limit);

        // The accelerator holds far less than the dense matrix the count is sized on, so a small limit
        // never takes its ceiling below the shipped one.
        Assert.Equal(SurfaceMesher.AcceleratedUnknownCeiling, SurfaceMesher.CeilingFor(true, 1_000_000_000));
    }

    [Fact]
    public void TheMeshVerdict_IsJudgedAgainstTheLimit_AndTheWarningSaysWhatTheMeshNeeds()
    {
        var problem = PlanarLineFixtures.Fr4Line(50e-3, 10e9);
        var free    = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Shipping);
        Assert.True(free.CanSolve);

        // Past a limit: a warning naming what the mesh needs and whose limit it is past — never a refusal.
        long tight = PlanarSystem.ResidentBytes(free.Mesh) / 2;
        var past = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Shipping with { MemoryLimitBytes = tight });
        Assert.True(past.CanSolve);
        Assert.True(past.PastMachineLimit);
        Assert.Null(past.Refusal);
        var warning = Assert.Single(past.AllFindings, f => f.IsWarning && f.Text.Contains("Simulate Anyway"));
        Assert.Contains("memory limit set for this machine (Settings ▸ Solvers)", warning.Text, StringComparison.Ordinal);
        Assert.Contains(SurfaceMesher.FormatBytes(PlanarSystem.ResidentBytes(free.Mesh)), warning.Text, StringComparison.Ordinal);

        // Under a limit large enough for it, nothing is said.
        Assert.Equal(free.UnknownCount, past.UnknownCount);
        var roomy = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Shipping with { MemoryLimitBytes = 50_000_000_000 });
        Assert.True(roomy.CanSolve);
        Assert.Contains("memory limit", roomy.Budget, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGuardsBelowTheMesher_LetAnyLimitThrough_AndStillHoldTheFixedCeilingWithout()
    {
        int past = SurfaceMesher.UnknownCeiling + 1;
        Assert.Null(Record.Exception(() => PlanarSystem.GuardCeiling(past, limit: 50_000_000_000)));
        Assert.Null(Record.Exception(() => SurfaceMesher.GuardCeiling(past, false, limit: 50_000_000_000)));
        // A run that carries a limit was asked about its size: even one far past it is let through.
        Assert.Null(Record.Exception(() => SurfaceMesher.GuardCeiling(2_000, false, limit: 10_000_000)));
        Assert.Null(Record.Exception(() => PlanarSystem.GuardCeiling(2_000, limit: 10_000_000)));

        // With no limit at all — an engine-direct caller — the fixed ceiling still refuses.
        Assert.Throws<InvalidOperationException>(() => PlanarSystem.GuardCeiling(past));
    }

    [Fact]
    public void TheLimit_SurvivesAuto()
        => Assert.Equal(7_000_000_000,
                        (PlanarMeshSettings.Default with { MemoryLimitBytes = 7_000_000_000 }).Resolved.MemoryLimitBytes);

    [Fact]
    public void ADeclinedRun_ThrowsBeforeAnythingIsFilled_AndTheHookSeesTheDenseEstimate()
    {
        var problem = PlanarLineFixtures.Fr4Line(20e-3, 5e9);
        PlanarRunSize? seen = null;
        var st = new PlanarSolveSettings(Deembed: false) { Admit = size => { seen = size; return "declined here"; } };

        var ex = Assert.Throws<PlanarRunDeclinedException>(() => new PlanarKernel().Solve(
            problem, PlanarLineFixtures.Shipping, PlanarLineFixtures.EndPorts(problem), [5e9], st));

        Assert.Equal("declined here", ex.Message);
        Assert.NotNull(seen);
        Assert.False(seen!.Accelerated);
        // CAP-3 — the estimate is the mesh's own: its counted cores, not the a-priori figure for its size.
        Assert.Equal(PlanarSystem.ResidentBytes(SurfaceMesher.Mesh(problem, PlanarLineFixtures.Shipping).Mesh),
                     seen.DenseResidentBytes);
    }

    // ── CAP-1 — the ceiling comes from the machine's memory, handed down by the run service ──────

    [Fact]
    public void AMachineDerivedLimit_SizesBothCeilings()
    {
        const long Machine = 17_179_869_184;
        // Both ceilings are sized on the memory: the dense one well past the shipped fallback, and
        // on a machine this size the accelerated ceiling is the memory count, past the shipped 12,000.
        Assert.True(SurfaceMesher.CeilingFor(false, Machine) > SurfaceMesher.UnknownCeiling);
        Assert.True(SurfaceMesher.CeilingFor(true, Machine, 0)
                    > SurfaceMesher.AcceleratedUnknownCeiling);
    }

    [Fact]
    public void PastAMachineDerivedLimit_TheMeshWarns_TheGuardsLetItThrough_AndTheRunProceedsWhenAdmitted()
    {
        var problem = PlanarLineFixtures.Fr4Line(20e-3, 5e9);
        var free    = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Coarse);
        long tight  = PlanarSystem.ResidentBytes(free.Mesh) / 2;
        var machine = PlanarLineFixtures.Coarse with
        {
            MemoryLimitBytes = tight, MemoryLimitSource = PlanarMemoryLimitSource.PhysicalMemory,
        };

        var report = SurfaceMesher.Mesh(problem, machine);
        Assert.True(report.CanSolve);
        Assert.True(report.PastMachineLimit);
        var warning = Assert.Single(report.AllFindings, f => f.IsWarning && f.Text.Contains("Simulate Anyway"));
        Assert.Contains($"this machine's {SurfaceMesher.FormatBytes(tight)} of physical memory", warning.Text,
                        StringComparison.Ordinal);

        Assert.Null(Record.Exception(() => PlanarSystem.GuardCeiling(
            free.UnknownCount, free.CellCount, tight)));
        Assert.Null(Record.Exception(() => SurfaceMesher.GuardCeiling(
            free.UnknownCount, false, free.CellCount, tight)));

        // Simulate Anyway: the run is asked, says yes, and completes de-embedded on the handed-down limit.
        PlanarRunSize? asked = null;
        var st = new PlanarSolveSettings { Admit = size => { asked = size; return null; } };
        var solved = new PlanarKernel().Solve(problem, machine, PlanarLineFixtures.EndPorts(problem), [5e9], st);
        Assert.NotNull(solved.Data);
        Assert.True(asked!.PastMachineCeiling);
    }

    [Fact]
    public void ADeembeddedRun_IsJudgedWithItsCalibrationStandards()
    {
        var problem = PlanarLineFixtures.Fr4Line(20e-3, 5e9);
        var ports   = PlanarLineFixtures.EndPorts(problem);
        var free    = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Coarse);
        var resolved = PlanarPorts.ResolveAll(free.Mesh, ports);
        long std = PlanarRunMemory.StandardBytes(problem, resolved, [5e9], new PlanarSolveSettings());
        long dut = PlanarSystem.ResidentBytes(free.Mesh);
        Assert.True(std > 0);
        Assert.Equal(0, PlanarRunMemory.StandardBytes(problem, resolved, [5e9], new PlanarSolveSettings(Deembed: false)));

        // A limit the structure alone fits but the standards beside it do not: a warning saying why, and the
        // size check sees the structure and the standards.
        var machine = PlanarLineFixtures.Coarse with
        {
            MemoryLimitBytes = dut + std / 2, MemoryLimitSource = PlanarMemoryLimitSource.PhysicalMemory,
        };
        var alone = SurfaceMesher.Mesh(problem, machine);
        Assert.False(alone.PastMachineLimit);
        var priced  = PlanarRunMemory.WithStandards(alone, std, machine, false);
        Assert.True(priced.CanSolve);
        Assert.True(priced.PastMachineLimit);
        Assert.Equal(dut + std, priced.RunResidentBytes);
        Assert.Contains(priced.AllFindings,
            f => f.IsWarning && f.Text.Contains("with its calibration standards solved beside it", StringComparison.Ordinal));

        PlanarRunSize? asked = null;
        var st = new PlanarSolveSettings { Admit = size => { asked = size; return "declined"; } };
        Assert.Throws<PlanarRunDeclinedException>(() => new PlanarKernel().Solve(problem, machine, ports, [5e9], st));
        Assert.True(asked!.StandardBytes > 0);
        Assert.Equal(PlanarSystem.ResidentBytes(free.Mesh) + asked.StandardBytes, asked.DenseResidentBytes);
        Assert.True(asked.PastMachineCeiling);
    }

    [Fact]
    public void PastTheOldFixedCeiling_TheBudgetStatesTheEstimatedTimePerPoint_AndBelowItDoesNot()
    {
        var problem = PlanarLineFixtures.Fr4Line(40e-3, 10e9);
        var roomy   = new PlanarMeshSettings(Auto: false, CellsPerWavelength: 110)
        {
            MemoryLimitBytes = 50_000_000_000, MemoryLimitSource = PlanarMemoryLimitSource.PhysicalMemory,
        };
        var big = SurfaceMesher.Mesh(problem, roomy);
        Assert.True(big.CanSolve);
        Assert.InRange(big.UnknownCount, SurfaceMesher.UnknownCeiling + 1, int.MaxValue);
        Assert.Contains($"about {SurfaceMesher.EstimatedFactorTime(big.UnknownCount)} per frequency point, estimated",
                        big.Budget, StringComparison.Ordinal);

        var small = SurfaceMesher.Mesh(problem, roomy with { CellsPerWavelength = 20 });
        Assert.True(small.UnknownCount <= SurfaceMesher.UnknownCeiling);
        Assert.DoesNotContain("estimated", small.Budget, StringComparison.Ordinal);

        // c·N³ at the recorded measurement: 24.5 s at N = 11,201 (CAP-2's triangle; 37 s on the
        // square at N = 11,210 before it).
        Assert.Equal("25 s", SurfaceMesher.EstimatedFactorTime(11_210));
    }
}
