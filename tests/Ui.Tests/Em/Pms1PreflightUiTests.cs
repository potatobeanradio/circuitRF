// PMS-1 — the preflight a drawn MMIC chip takes (EmRunService.Preflight: what `circuitrf check`, `em --dry-run` and
// the EM panel run) reaches its verdict and does not price the standards when the structure alone is past the limit.

using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em;

public class Pms1PreflightUiTests(ITestOutputHelper output)
{
    /// <summary>Gate 1 — fixture S on the shipped MMIC technology, judged against a 7.6 GB machine: the mesh is past the
    /// limit, the run is asked rather than refused, and the standards are not priced (and the report says so).</summary>
    [Fact]
    public void FixtureS_OnTheShippedMmicTechnology_ReachesItsVerdict_WithoutPricingTheStandards()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pms1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var chip = SyntheticMmicChip.Build(SyntheticMmicChipSpec.S);
            var source = SyntheticMmicChipLayout.Source(chip, Path.Combine(dir, "chip.clay"));
            var setup = new EmSetup
            {
                Name      = "chip",
                LayoutRef = "chip.clay",
                Frequency = new Core.Design.FrequencySpec("10", "10", 1, Core.Design.SweepKind.Linear, "GHz", "GHz"),
            };

            var pre = EmRunService.Preflight(setup, source, null, memoryCeilingBytes: 7_600_000_000L);
            var report = pre.PlanarMesh;
            output.WriteLine($"N = {report?.UnknownCount:N0}; {pre.Refusal ?? "not refused"}");

            Assert.True(pre.Ok, pre.Refusal);
            Assert.NotNull(report);
            Assert.True(report!.PastMachineLimit);
            Assert.True(report.MatrixAlonePastLimit);
            Assert.Equal(0, report.StandardResidentBytes);
            Assert.Contains(pre.Findings, f => f.Text == PlanarRunMemory.NotPricedNote);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
