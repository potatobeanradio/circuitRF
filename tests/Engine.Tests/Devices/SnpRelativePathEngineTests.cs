using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;

namespace CircuitRF.Engine.Tests.Devices;

/// <summary>
/// Gate test for brief-snp-relative-path: confirms that BaseDirectory threads through
/// ParametricSweepEngine.Run → per-point Elaborator so SnP loads at every sweep point.
/// </summary>
public class SnpRelativePathEngineTests : IDisposable
{
    private const string MinimalS2p = """
        # GHz S MA R 50
        1.0   0.9 170   0.1 45   0.1 45   0.9 170
        """;

    private readonly string _root;

    public SnpRelativePathEngineTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"snp_rp_{Path.GetRandomFileName()}");
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "amp.s2p"), MinimalS2p);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // ── T6: SnP inside a parametric sweep loads at every point when baseDirectory is threaded ──

    [Fact]
    public void SweptSnP_Resolves()
    {
        // File="amp.s2p" is relative; CnlReader has no sourceDirectory so it stays relative.
        // Only ParametricSweepEngine's BaseDirectory should resolve it.
        const string cnl = """
            Gain = 1
            Port:T1  n1 0  Num=1 Z=50 Ohm
            Port:T2  n2 0  Num=2 Z=50 Ohm
            SnP:S1   n1 n2 0  NumPorts=2 File="amp.s2p"
            analysis SP1  type=sparam  start=1e9  stop=1e9  npts=1
            analysis SW1  type=parametric_sweep  Var=Gain  Values=1,2,3  Inner=SP1
            """;

        var (lib, tb) = new CnlReader().Read(cnl, "tb");
        var sw1 = tb.Analyses.OfType<ParametricSweepAnalysis>().First(a => a.Name == "SW1");

        // Should not throw — relative File is resolved at each re-elaboration.
        var ds = ParametricSweepEngine.Run(sw1, lib, tb, baseDirectory: _root);

        // S cube must have the sweep axis with 3 points.
        var sCube = ds["S"];
        Assert.Equal(4, sCube.Rank);   // Gain(3) × freq(1) × port_i(2) × port_j(2)
        Assert.Equal("Gain", sCube.Axes[0].Name);
        Assert.Equal(3, sCube.Axes[0].Length);
    }
}
