// ================================================================
//  AntennaCoSimExampleTests.cs — the Antenna Co-Simulation example, on a copy as File ▸ New ▸ Examples makes one: no
//  .generated-cells, so its footprints have to be rebuilt before its component ports can stand on their pads.
// ================================================================

using CircuitRF.Ui.Tests.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.Examples;

public sealed class AntennaCoSimExampleTests : IDisposable
{
    private const string View = "Meander Antenna/3d/Meander Antenna.c3d";
    private readonly string _ws = Directory.CreateTempSubdirectory("crf-antenna-example-").FullName;

    public AntennaCoSimExampleTests()
    {
        string from = Path.Combine(PalaceBackendTests.RepoRoot(), "examples", "Antenna Co-Simulation");
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string to = Path.Combine(_ws, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }
    }

    public void Dispose() { try { Directory.Delete(_ws, true); } catch (IOException) { } }

    /// <summary>All three parts get a verified port with no <c>.generated-cells</c>, and asking writes none.</summary>
    [Fact]
    public void AFreshCopy_BuildsAPortOnEveryPart()
    {
        var (code, stdout, stderr) = CliProcess.Run(PalaceBackendTests.RepoRoot(), [], "explain", Path.Combine(_ws, View),
                                                    "--setup", "Pattern", "--component-ports", "*");
        Assert.True(code == 0, stderr);
        foreach (string part in new[] { "C1", "C2", "L1" })
            Assert.Matches($@"component port {part}: port \d.*verified", stdout);
        Assert.False(Directory.Exists(Path.Combine(_ws, ".generated-cells")));
    }

    /// <summary>The board's layout matches its schematic: C2's unfitted footprint is a note, and L1 is the right way round.</summary>
    [Fact]
    public void TheLayoutMatchesTheSchematic_WithTheUnfittedPartAsANote()
    {
        var (code, stdout, stderr) = CliProcess.Run(PalaceBackendTests.RepoRoot(), [], "lvs", Path.Combine(_ws, "Meander Antenna"), "--severity", "warning");

        string report = stdout + stderr;
        Assert.True(code == 0, report);
        Assert.Contains("'C2' is marked Open", report);
    }

    /// <summary>A part marked Open is not fitted, so it asks for no model. A run's own copy of the view under results/ is no design.</summary>
    [Fact]
    public void CheckingTheWorkspace_FindsNoMissingModelAndWalksPastARunsCopyOfTheView()
    {
        string run = Path.Combine(_ws, "results", "Meander Antenna Pattern.emerge");
        Directory.CreateDirectory(run);
        File.Copy(Path.Combine(_ws, View), Path.Combine(run, "document.c3d"));

        var (_, stdout, stderr) = CliProcess.Run(PalaceBackendTests.RepoRoot(), [], "check", _ws);

        string report = stdout + stderr;
        Assert.DoesNotContain("document.c3d", report);
        Assert.DoesNotContain("has no model", report);
    }
}
