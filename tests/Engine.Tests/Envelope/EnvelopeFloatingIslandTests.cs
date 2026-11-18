using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Envelope;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Envelope;

/// <summary>
/// The envelope twin of <c>HbFloatingIslandBackSolveTests</c>: a floating source feeding a diode bridge through series
/// bead networks. With the diodes removed the source and the bead internals reach ground only through gmin, so a linear
/// state rebuilt from the device currents amplified the Newton residual by ~1e11 Ω: 63.7 kV at the start point of the
/// one-fundamental engine and 63 kV at the last step of the lattice engine, on a bench whose sources have a 6.3 V
/// open-circuit peak. The diode nodes are read from that state too (a bead touches them), so they drifted with it.
/// </summary>
public sealed class EnvelopeFloatingIslandTests(ITestOutputHelper output)
{
    private const string Bead = """
        define BEAD (p1 p2)
          R:R1  p1 1  R=163.4
          L:L1  p1 1  L=4.425e-7
          C:C1  p1 2  C=2.828e-13
          R:R2  1 2   R=61.04
          L:L2  1 2   L=9.984e-7
          R:R3  2 3   R=62.71
          L:L3  2 3   L=4.598e-8
          R:R4  3 p2  R=0.035
        end BEAD
        """;

    private const string Bridge = """
        BEAD:X2  n1  Vacp
        BEAD:X3  n2  Vacmin
        Diode:D1  n1  out  Is=1e-14
        Diode:D3  0  n1  Is=1e-14
        Diode:D4  n2  out  Is=1e-14
        Diode:D2  0  n2  Is=1e-14
        C:C1  out  0  C=1 nF
        R:RL  out  0  R=10k
        """;

    // One fundamental: EnvelopeEngine.
    private static readonly string OneCarrier = Bead + "\n" + """
        MOD_Source:S1  Vacp  Vacmin  Fc=869 MHz  Pavg=20  Z=50  Record=20e-6  Carrier[1]="CW;Off=0;P=0"
        """ + "\n" + Bridge + "\n" + """
        analysis ENV1 type=env Stop=2e-6 MaxHarm=3 Segments=1 Warmup=0 Tol=1e-6 SaveWaveforms=yes
        labelednets Vacp Vacmin n1 n2 out
        """;

    // Two fundamentals in series in the floating loop: EnvelopeEngineMt.
    private static readonly string TwoCarriers = Bead + "\n" + """
        MOD_Source:S1  Vacp  mid  Fc=869 MHz  Pavg=17  Z=25  Record=20e-6  Carrier[1]="CW;Off=0;P=0"
        MOD_Source:S2  mid  Vacmin  Fc=2.4 GHz  Pavg=17  Z=25  Record=20e-6  Carrier[1]="CW;Off=0;P=0"
        """ + "\n" + Bridge + "\n" + """
        analysis ENV1 type=env Stop=2e-6 MaxHarm=3 MaxMixOrder=3 Segments=1 Warmup=0 Tol=1e-6 SaveWaveforms=yes
        labelednets Vacp Vacmin n1 n2 out
        """;

    private static DataSet Run(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        using var first = new Elaborator(lib).Elaborate(tb);
        var plan = EnvelopeRunner.Resolve(tb.Analyses.OfType<EnvelopeAnalysis>().Single(), first.ResolvedGlobals);
        bool used = false;
        var gate = new object();
        ElaboratedNetlist Elab() { lock (gate) { if (used) return new Elaborator(lib).Elaborate(tb); used = true; return first; } }
        return EnvelopeRunner.Run(plan, Elab);
    }

    /// <summary>Worst per-harmonic |V| over the island's nodes at one time sample.</summary>
    private static double Worst(DataCube v, int step)
    {
        string[] nodes = v.Axes[1].Labels!;
        double worst = 0;
        foreach (var n in new[] { "Vacp", "Vacmin", "n1", "n2" })
        {
            int i = Array.IndexOf(nodes, n);
            Assert.True(i >= 0, $"{n} not recorded: {string.Join(", ", nodes)}");
            for (int h = 0; h < v.Axes[2].Length; h++) worst = Math.Max(worst, ((Complex)v[step, i, h]).Magnitude);
        }
        return worst;
    }

    // The sources' open-circuit peak is 6.3 V in both benches, and the diodes clamp the bridge nodes to within a diode drop
    // of ground and of the output, so no island node can reach 10 V at any harmonic.
    private const double IslandBoundV = 10.0;

    private void AssertBounded(string cnl, string engine)
    {
        var v = Run(cnl)["V"];
        int last = v.Axes[0].Length - 1;
        double start = Worst(v, 0), end = Worst(v, last);
        output.WriteLine($"{engine}: worst island |V| {start:E3} V at the start point, {end:E3} V at the last step");
        Assert.True(start < IslandBoundV, $"{engine}: an island node reads {start:E3} V at the start point");
        Assert.True(end < IslandBoundV, $"{engine}: an island node reads {end:E3} V at the last step");
    }

    [Fact]
    public void OneFundamental_IslandBehindTheBridge_StaysWithinTheDrive() => AssertBounded(OneCarrier, "EnvelopeEngine");

    [Fact]
    public void TwoFundamentals_IslandBehindTheBridge_StaysWithinTheDrive() => AssertBounded(TwoCarriers, "EnvelopeEngineMt");
}
