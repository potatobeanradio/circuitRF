using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Engine.Tests.Devices;

/// <summary>
/// An SnP whose file starts above 0 Hz is clamped to its lowest row at DC, so a bias through it takes that row as its DC answer.
/// The DC operating point says so, naming the block and its lowest frequency; a file with a 0 Hz row says nothing.
/// </summary>
public sealed class SnpNoDcRowTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("snp-nodc-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private ElaboratedNetlist Bias(double firstHz)
    {
        string file = Path.Combine(_dir, $"block{firstHz:0}.s2p");
        File.WriteAllText(file, "# Hz S RI R 50\n" + string.Join("\n",
            new[] { firstHz, 2e9, 4e9 }.Select(f => $"{f} 0.1 0 0.8 0 0.8 0 0.1 0")) + "\n");
        var (lib, tb) = new CnlReader().Read($"""
            Vdc:V1 a 0 Vdc=1 V
            SnP:EM1 a b NumPorts=2 File="{file}"
            R:R1 b 0 R=50 Ohm
            analysis DC1 type=dc
            """);
        return new Elaborator(lib).Elaborate(tb);
    }

    [Fact]
    public void ABlockWithNoZeroHertzRow_IsWarnedAbout_AtTheDcOperatingPoint()
    {
        var nl = Bias(1e9);
        Assert.True(NonlinearDcEngine.Run(nl).Converged);
        var w = Assert.Single(nl.Warnings, x => x.Contains("no 0 Hz row", StringComparison.Ordinal));
        Assert.StartsWith("SnP 'EM1' has no 0 Hz row: its lowest frequency is 1 GHz", w, StringComparison.Ordinal);

        var dc = Bias(0);
        Assert.True(NonlinearDcEngine.Run(dc).Converged);
        Assert.DoesNotContain(dc.Warnings, x => x.Contains("no 0 Hz row", StringComparison.Ordinal));
    }
}
