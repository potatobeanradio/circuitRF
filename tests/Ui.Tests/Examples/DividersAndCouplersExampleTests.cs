using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Tests.Em3d;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Examples;

/// <summary>The shipped <i>Dividers and Couplers</i> example: the Wilkinson's isolation resistor, whose pads the ring joins the long
/// way round, is a component port on every EM setup, and check notes that the port closes a loop. Read-only: check writes nothing.</summary>
public sealed class DividersAndCouplersExampleTests(ITestOutputHelper output)
{
    [Fact]
    public void TheWilkinsonsResistor_IsAComponentPort_OnPlanarAndIn3D()
    {
        string root = ExampleWorkspaces.ResolveRoot() ?? throw new InvalidOperationException("no examples/ tree");
        string cell = Path.Combine(root, "Dividers and Couplers", "Wilkinson");
        string repo = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(repo, "circuitRF.slnx"))) repo = Path.GetDirectoryName(repo)!;
        // A note, not a warning: the setups sweep 1.2-4.8 GHz, with no 0 Hz row for the loop to decide.
        string loop = EmDiagnostics.ComponentPortAcrossLoop("R1", "1", "2", sweepHasDc: false).Render();

        foreach (var (doc, setups) in new[] { (Path.Combine(cell, "em", "Wilkinson Planar.cem"), 1), (Path.Combine(cell, "3d", "Wilkinson.c3d"), 2) })
        {
            var (exit, stdout, stderr) = CliProcess.Run(repo, [], "check", doc);
            string all = stdout + stderr;
            output.WriteLine(all);
            Assert.True(exit == 0, all);
            Assert.Equal(setups, all.Split(loop).Length - 1);
            Assert.DoesNotContain("R1 is left out", all, StringComparison.Ordinal);
        }
    }
}
